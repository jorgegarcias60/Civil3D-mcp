import { z } from "zod";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { isAbsolute, win32 } from "node:path";
import { effectivePluginHost, withApplicationConnection } from "../../utils/ConnectionManager.js";
import { readPluginToken } from "../../utils/pluginAuth.js";
import { Civil3DRpcError, COMMAND_TIMEOUT_MS } from "../../utils/SocketClient.js";
import type { DomainToolDefinition } from "../domainRuntime.js";
import { runCodeApprovalMode } from "../approvalPolicy.js";

// civil3d_run_code: a C# snippet compiled with Roslyn inside the plugin and run
// on Civil 3D's main thread. It exists for work the fixed tools and COM/LISP
// make too slow (2026-10-11: 96,913 objects sampled into 547,441 points in
// 1.2 s in-process against about 20 minutes in LISP). The code runs with the
// user's full rights. A direct MCP client call may use its tool permission in
// client mode; nested calls still need a token. The plugin saves
// every script with a summary in its run-code log folder.

export const RUN_CODE_MAX_CODE_CHARS = 200_000;
export const RUN_CODE_MAX_READ_TIMEOUT_SECONDS = 600;
export const RUN_CODE_MAX_WRITE_TIMEOUT_SECONDS = 90;
// code + args as JSON; leaves room for the envelope under the plugin's 1 MiB
// request limit (SocketClient MAX_REQUEST_BYTES). Checked here so that
// civil3d_request_approval refuses an oversized call before issuing a token.
export const RUN_CODE_MAX_REQUEST_BYTES = 1_000_000;
// The plugin refuses (HOST_BUSY, nothing changed) a run that has not started
// within this window, so a run either gets its full budget inside the client's
// timeout or does not start at all.
export const RUN_CODE_START_WINDOW_SECONDS = 30;
// Compile, the host hop and the reply, on top of the start window and budget.
const RUN_CODE_REPLY_MARGIN_SECONDS = 30;

const absolutePath = z.string().min(1).refine(
  (path) => isAbsolute(path) && (process.platform !== "win32" || win32.parse(path).root.length > 1),
  "Path must be absolute.",
);

function checkPayloadSize(code: string, args: Record<string, unknown>): number {
  return Buffer.byteLength(JSON.stringify({ code, args }), "utf8");
}

export const RunCodeArgsSchema = z.object({
  action: z.literal("run"),
  code: z.string().min(1).max(RUN_CODE_MAX_CODE_CHARS).optional(),
  codePath: absolutePath.optional(),
  document: z.string().min(1).max(1024).optional(),
  mode: z.enum(["read", "write"]).optional(),
  timeoutSeconds: z.number().int().min(1).optional(),
  maxInlineBytes: z.number().int().min(1024).max(1024 * 1024).optional(),
  args: z.record(z.unknown()).optional(),
  argsPath: absolutePath.optional(),
  copyOutputsTo: absolutePath.optional(),
  keepOutputs: z.boolean().optional(),
}).superRefine((value, ctx) => {
  if (Boolean(value.code) === Boolean(value.codePath)) {
    ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["code"], message: "Supply exactly one of code or codePath." });
  }
  if (value.args !== undefined && value.argsPath !== undefined) {
    ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["args"], message: "Supply either args or argsPath, not both." });
  }
  if (value.keepOutputs === false && !value.copyOutputsTo) {
    ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["keepOutputs"], message: "keepOutputs:false requires copyOutputsTo." });
  }
  const limit = (value.mode ?? "read") === "write"
    ? RUN_CODE_MAX_WRITE_TIMEOUT_SECONDS : RUN_CODE_MAX_READ_TIMEOUT_SECONDS;
  if ((value.timeoutSeconds ?? 30) > limit) {
    ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["timeoutSeconds"], message: `${value.mode === "write" ? "Write" : "Read"} runs are limited to ${limit} seconds.` });
  }
  if (value.codePath || value.argsPath) return;
  const bytes = checkPayloadSize(value.code ?? "", value.args ?? {});
  if (bytes > RUN_CODE_MAX_REQUEST_BYTES) {
    ctx.addIssue({
      code: z.ZodIssueCode.custom,
      path: ["args"],
      message: `code and args are ${bytes} bytes as JSON; the limit is ${RUN_CODE_MAX_REQUEST_BYTES}. ` +
        "Put large data in a file and pass its path in args (read it with File.ReadAllText in the script), or split the run.",
    });
  }
});

type RunCodeArgs = z.infer<typeof RunCodeArgsSchema>;

async function readUtf8File(path: string): Promise<{ text: string; sha256: string }> {
  const bytes = await readFile(path);
  if (bytes.length > RUN_CODE_MAX_REQUEST_BYTES) {
    throw new Error(`File contents are ${bytes.length} bytes as JSON; the limit is ${RUN_CODE_MAX_REQUEST_BYTES}.`);
  }
  const text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes).replace(/^\uFEFF/, "");
  return { text, sha256: createHash("sha256").update(bytes).digest("hex") };
}

export async function prepareRunCode(args: RunCodeArgs, rawArgs: Record<string, unknown>): Promise<{ args: RunCodeArgs; approvalParameters: Record<string, unknown> }> {
  const binding: Record<string, unknown> = {};
  let code = args.code;
  let values = args.args;
  if (args.codePath) {
    const file = await readUtf8File(args.codePath);
    code = file.text;
    binding.codeSha256 = file.sha256;
  }
  if (args.argsPath) {
    const file = await readUtf8File(args.argsPath);
    let parsed: unknown;
    try { parsed = JSON.parse(file.text); }
    catch { throw new Error("argsPath must contain a JSON object."); }
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      throw new Error("argsPath must contain a JSON object.");
    }
    values = parsed as Record<string, unknown>;
    binding.argsSha256 = file.sha256;
  }
  if (!code || code.length > RUN_CODE_MAX_CODE_CHARS) {
    throw new Error(`code must contain 1-${RUN_CODE_MAX_CODE_CHARS} characters.`);
  }
  const bytes = checkPayloadSize(code, values ?? {});
  if (bytes > RUN_CODE_MAX_REQUEST_BYTES) {
    throw new Error(`code and args are ${bytes} bytes as JSON; the limit is ${RUN_CODE_MAX_REQUEST_BYTES}.`);
  }
  return { args: { ...args, code, args: values }, approvalParameters: { ...rawArgs, ...binding } };
}

const DiagnosticSchema = z.object({
  line: z.number().int(),
  column: z.number().int(),
  code: z.string(),
  message: z.string(),
});

export const RunCodeResponseSchema = z.object({
  runId: z.string(),
  mode: z.enum(["read", "write"]),
  status: z.enum(["ok", "compile_error", "rejected", "runtime_error", "timeout", "cancelled", "failed"]),
  ok: z.boolean(),
  approvedBy: z.enum(["token", "client"]).optional(),
  drawing: z.string().optional(),
  activated: z.boolean().optional(),
  switchedBack: z.boolean().optional(),
  output: z.string().optional(),
  outputFile: z.string().optional(),
  result: z.unknown().optional(),
  resultShape: z.string().optional(),
  resultBytes: z.number().int().optional(),
  resultFile: z.string().optional(),
  resultPreview: z.string().optional(),
  resultError: z.string().optional(),
  files: z.array(z.string()).optional(),
  copiedFiles: z.array(z.string()).optional(),
  scriptSha256: z.string().regex(/^[0-9a-f]{64}$/).optional(),
  sideErrors: z.array(z.object({ path: z.string(), errorStatus: z.string().nullable().optional(), message: z.string() })).optional(),
  tryErrors: z.array(z.object({ label: z.string(), type: z.string(), message: z.string(), line: z.number().int().nullable().optional() })).optional(),
  errors: z.array(DiagnosticSchema).optional(),
  error: z.object({
    type: z.string(),
    message: z.string(),
    line: z.number().int().optional(),
    errorStatus: z.string().optional(),
    inner: z.string().optional(),
    stack: z.string().optional(),
  }).optional(),
  warnings: z.array(z.union([z.string(), DiagnosticSchema])).optional(),
  timing: z.object({
    compileMs: z.number(),
    runMs: z.number(),
    totalMs: z.number(),
  }),
  logFolder: z.string(),
  // "unknown" when the script ended the run's transaction itself.
  committed: z.union([z.boolean(), z.literal("unknown")]).optional(),
}).passthrough();

export const CODE_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "code",
  actions: {
    run: {
      action: "run",
      inputSchema: RunCodeArgsSchema,
      prepare: async (args, rawArgs) => await prepareRunCode(args as RunCodeArgs, rawArgs),
      progressHeartbeat: true,
      responseSchema: RunCodeResponseSchema,
      // "manage" + not safe for retry: approval is required (and forced for
      // this tool even when approvals are disabled).
      capabilities: ["query", "analyze", "manage"],
      requiresActiveDrawing: false,
      safeForRetry: false,
      pluginMethods: ["runCode"],
      execute: async (parsedArgs, context) => {
        if (effectivePluginHost !== "127.0.0.1") {
          throw new Civil3DRpcError(
            `civil3d_run_code requires CIVIL3D_HOST to dial exactly 127.0.0.1 (effective host: ${effectivePluginHost}). Nothing ran.`,
            "CIVIL3D.INVALID_INPUT", -32602,
          );
        }
        const args = parsedArgs as z.infer<typeof RunCodeArgsSchema>;
        const timeoutSeconds = args.timeoutSeconds ?? 30;
        // Added here, after approval and idempotency hashed the caller's
        // arguments, so the token never enters a hash or a log.
        const authToken = readPluginToken();
        const timeoutMs = Math.max(
          COMMAND_TIMEOUT_MS,
          (timeoutSeconds + RUN_CODE_START_WINDOW_SECONDS + RUN_CODE_REPLY_MARGIN_SECONDS) * 1000,
        );
        try {
          const response = await withApplicationConnection(
            async (appClient) => {
              if (args.document !== undefined || args.copyOutputsTo !== undefined || args.keepOutputs !== undefined || timeoutSeconds > 90) {
                const health = await appClient.sendCommand("getCivil3DHealth", {}) as Record<string, unknown>;
                const api = typeof health?.runCodeApi === "number" ? health.runCodeApi : 1;
                const requiredApi = args.document !== undefined && (args.mode ?? "read") === "write" ? 5
                  : args.document !== undefined ? 4 : 3;
                if (api < requiredApi) {
                  const parameter = args.document !== undefined ? "document"
                    : args.copyOutputsTo !== undefined ? "copyOutputsTo"
                    : args.keepOutputs !== undefined ? "keepOutputs" : "timeoutSeconds";
                  throw new Civil3DRpcError(
                    `The Civil 3D plugin is older than this server: ${parameter} needs runCode API ${requiredApi}, the loaded plugin has ${api}. Install the plugin from this build (Civil 3D closed). Nothing ran.`,
                    "CIVIL3D.INVALID_INPUT", -32602,
                  );
                }
              }
              return await appClient.sendCommand("runCode", {
                code: args.code,
                ...(args.document !== undefined ? { document: args.document } : {}),
                ...(args.codePath ? { codePath: args.codePath } : {}),
                ...(args.argsPath ? { argsPath: args.argsPath } : {}),
                ...(args.copyOutputsTo !== undefined ? { copyOutputsTo: args.copyOutputsTo } : {}),
                ...(args.keepOutputs !== undefined ? { keepOutputs: args.keepOutputs } : {}),
                ...(context?.approval === "token" || context?.approval === "client" ? { approval: context.approval } : {}),
                mode: args.mode ?? "read",
                timeoutSeconds,
                startWithinSeconds: RUN_CODE_START_WINDOW_SECONDS,
                maxInlineBytes: args.maxInlineBytes ?? 65536,
                args: args.args ?? {},
                ...(authToken ? { authToken } : {}),
              }, { timeoutMs });
            },
          );
          const withHash = {
            ...response as Record<string, unknown>,
            scriptSha256: (response as Record<string, unknown>).scriptSha256
              ?? createHash("sha256").update(args.code ?? "", "utf8").digest("hex"),
          };
          return context?.approval === "token" || context?.approval === "client"
            ? { ...withHash, approvedBy: context.approval }
            : withHash;
        } catch (error) {
          // No answer, so no runId: the agent must not retry blindly.
          if (error instanceof Civil3DRpcError && error.code === "CIVIL3D.TIMEOUT") {
            throw new Civil3DRpcError(
              `${error.message}. The script may have run: before retrying, open the newest folder in ` +
              "%LOCALAPPDATA%\\Civil3DMcpPlugin\\run-code\\ (next to plugin.log) and read its run.json (status, committed).",
              error.code,
              error.rpcCode,
            );
          }
          throw error;
        }
      },
    },
  },
  exposures: [
    {
      toolName: "civil3d_run_code",
      displayName: "Civil 3D Run Code",
      get description() {
        return "Runs a C# snippet inside Civil 3D (compiled with Roslyn, run on the main thread with the drawing locked), for work that is too slow or impossible with the other tools or COM/LISP: bulk geometry over thousands of objects, reading other DWGs as side databases, anything the AutoCAD/Civil 3D .NET API reaches. " +
        (runCodeApprovalMode() === "client"
          ? "Call this tool directly: your MCP client's tool permission is the approval; calls through civil3d_orchestrate, civil3d_docs or the HTTP bridge still need a civil3d_request_approval token. "
          : "Always needs approval: call civil3d_request_approval with toolName civil3d_run_code, action 'run' and the same parameters, then retry with approvalToken. ") +
        "The code runs with the user's full rights. " +
        "For a read, document selects another open drawing by full path or unique file name without changing the active drawing; with no drawing open, OpenSide(path) can still read a DWG. Db, Doc, Ed, Civil and Tr need an open drawing. For a write, document can select another open drawing: Civil 3D briefly activates it, runs the command there, then switches back. The response reports activated and switchedBack. " +
        "The snippet is the body of `object Run()`: statements, local functions and leading `using` lines; `return` a value (JSON: points and vectors as [x,y,z], ObjectIds as handles, tuples as arrays, anonymous objects by property). " +
        "In scope: Doc, Db, Ed, Civil (CivilDocument), Tr (the run's transaction), HasDrawing, Args (the 'args' object), Log(x), Check(), Open<T>(id, forWrite), ModelSpace<T>(db), PaperSpace<T>(layout, db), ById(hex, db), OpenSide(path), OpenSideOrNull(path), SideErrors, Try(label, action), TryErrors, EnsureLayer(name, colorIndex?, linetype?, db) and Tag(obj, app, value?) in write mode, TagValue and Tagged<T>(app, value?, db), TransactionFor(db), WriteFile(name, text or lines) (into the run folder), Session (a dictionary kept across runs until Civil 3D closes; plain data only). OpenSide and OpenSideOrNull return managed read-only side databases; do not dispose them. " +
        "Namespaces: System, Linq, IO, Text, Text.Json, Autodesk.AutoCAD ApplicationServices/DatabaseServices/EditorInput/Geometry/Colors, Autodesk.Civil, Civil ApplicationServices/DatabaseServices/Styles; names both AutoCAD and Civil 3D define mean AutoCAD's (DBObject, Entity, Table, Shape...; Civil 3D's are CivilDBObject, CivilEntity...), except Surface and Section, which are Civil 3D's (AcSurface, AcSection); AcException is AutoCAD's exception. " +
        "Civil 3D's user interface freezes during a long read and it does not answer other tool calls until the run finishes. Call Check() in long loops, which ends the run when timeoutSeconds (read 1-600, write 1-90, default 30) is used up or the request is cancelled; a timeout the script catches still ends the run as a timeout. The server sends MCP progress every 15 seconds when the client provides a progress token. " +
        "mode 'read' (default): an open-close transaction, never committed; the compiler refuses OpenMode.ForWrite, UpgradeOpen, Erase, AppendEntity, Civil 3D Create/Add/Update..., regular transactions and drawing-setting setters, including ++/-- and deconstruction (a guard against mistakes, not a sandbox). " +
        "mode 'write': runs inside a plugin command; committed as one undo step when the snippet returns, rolled back when it throws or runs out of time (only changes made through Tr are rolled back: write mode refuses open-close transactions; database or system variables, files and Session are not rolled back). Both modes refuse Editor prompts, modal windows and message loops (any ShowDialog/ShowModal, MessageBox, InputBox, Application.Run), event subscriptions, TransactionManager.TopTransaction and Tr.Commit()/Abort() (the run owns Tr: just return; `using` on Tr does nothing); a nested transaction only as `using var t = ...StartTransaction();` (write) or `...StartOpenCloseTransaction()` (read). " +
        "status is ok, compile_error or rejected (errors with snippet line numbers), runtime_error, timeout or cancelled (error with line); committed is true, false or 'unknown' (the transaction was ended some other way: check the drawing). Output or results larger than maxInlineBytes (default 64 KB) go to outputFile/resultFile with a preview. Every script and its summary are saved in logFolder. " +
        "Supply code or an absolute UTF-8 codePath (at most 200k characters after BOM removal), and args or an absolute argsPath containing a JSON object. File contents count toward the 1 MB JSON limit and are bound by hash to approval. copyOutputsTo copies output files atomically to an absolute folder; keepOutputs:false requires it and removes successfully copied outputs after an ok run. script.cs and run.json stay in the log. The response includes scriptSha256, copiedFiles, sideErrors and tryErrors when available. A run that cannot start within 30 s because Civil 3D is busy with other work fails with HOST_BUSY and changes nothing.";
      },
      inputShape: {
        code: z.string().min(1).max(RUN_CODE_MAX_CODE_CHARS).optional(),
        codePath: absolutePath.optional(),
        document: z.string().min(1).max(1024).optional(),
        mode: z.enum(["read", "write"]).optional(),
        timeoutSeconds: z.number().int().min(1).max(RUN_CODE_MAX_READ_TIMEOUT_SECONDS).optional(),
        maxInlineBytes: z.number().int().min(1024).max(1024 * 1024).optional(),
        args: z.record(z.unknown()).optional(),
        argsPath: absolutePath.optional(),
        copyOutputsTo: absolutePath.optional(),
        keepOutputs: z.boolean().optional(),
      },
      supportedActions: ["run"],
      resolveAction: (rawArgs) => ({ action: "run", args: { ...rawArgs, action: "run" } }),
    },
  ],
};
