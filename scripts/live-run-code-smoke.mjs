// Live smoke test of civil3d_run_code through the real MCP server (build/index.js)
// and the plugin in a running Civil 3D. Run it against a SCRATCH drawing: the
// write cases add and remove text objects in scratch drawings. Active-drawing
// cases send one undo; a targeted write cleans up in its target drawing.
//
//   npm run build
//   node scripts/live-run-code-smoke.mjs --expect-drawing <name> [--side <drawing.dwg>] [--approval token|client]
//     [--long <seconds>] [--damaged <directory>] [--other <open drawing path>] [--no-drawing]
//
// --expect-drawing: the write cases run only when the active drawing's name contains
//   this text (guards against testing on a real job by accident); without it, or
//   --allow-writes, only the read cases run. --other must name a disposable open
//   drawing too when writes are enabled: the PR 4 case commits and erases text there.
// --side: a DWG read as a side database (never opened, never changed) for the
//   OpenSide and bulk-sampling cases; skipped when not given.
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { createHash } from "node:crypto";
import net from "node:net";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { parseArgs } from "node:util";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { pluginTokenPath, readPluginToken } from "../build/utils/pluginAuth.js";

// Strict: an unknown option or a missing value is an error, never a silently disabled guard.
const { values: options } = parseArgs({
  options: {
    side: { type: "string" },
    "expect-drawing": { type: "string" },
    "allow-writes": { type: "boolean", default: false },
    approval: { type: "string", default: "token" },
    long: { type: "string" },
    damaged: { type: "string" },
    other: { type: "string" },
    "no-drawing": { type: "boolean", default: false },
  },
  strict: true,
});
const sidePath = options.side;
const expectDrawing = options["expect-drawing"];
const approvalMode = options.approval;
const longSeconds = options.long === undefined ? undefined : Number(options.long);
if (longSeconds !== undefined && (!Number.isInteger(longSeconds) || longSeconds < 91 || longSeconds > 600)) {
  console.error("--long must be an integer from 91 to 600 seconds");
  process.exit(2);
}
const damagedDir = options.damaged;
if (options["no-drawing"] && (options.other || expectDrawing || options["allow-writes"])) {
  console.error("--no-drawing cannot be combined with --other, --expect-drawing, or --allow-writes");
  process.exit(2);
}
if (approvalMode !== "token" && approvalMode !== "client") {
  console.error("--approval must be token or client");
  process.exit(2);
}

const transport = new StdioClientTransport({
  command: process.execPath,
  args: ["./build/index.js"],
  cwd: process.cwd(),
  env: { ...process.env, MCP_HTTP_HOST: "127.0.0.1", MCP_HTTP_PORT: "0", CIVIL3D_RUN_CODE_APPROVAL: approvalMode },
  stderr: "pipe",
});
const client = new Client({ name: "civil3d-run-code-smoke", version: "1.0.0" });
const results = [];
let failures = 0;

function record(name, passed, detail) {
  results.push({ name, passed, ...(detail === undefined ? {} : { detail }) });
  if (!passed) failures++;
  console.error(`${passed ? "ok  " : "FAIL"} ${name}${detail === undefined ? "" : ` :: ${JSON.stringify(detail).slice(0, 400)}`}`);
}

async function call(name, args, requestOptions = {}) {
  return await client.callTool({ name, arguments: args }, undefined,
    { timeout: 180_000, maxTotalTimeout: 180_000, ...requestOptions });
}

function text(result) {
  return (result.content ?? []).map((item) => item.text ?? "").join("\n");
}

async function approve(toolName, action, parameters) {
  const approval = await call("civil3d_request_approval", { toolName, action, parameters });
  if (approval.isError) throw new Error(`approval failed: ${text(approval)}`);
  return approval.structuredContent.approvalToken;
}

// One approved run; returns the plugin's response object (structuredContent.result).
async function run(parameters, requestOptions) {
  let response = await call("civil3d_run_code", parameters, requestOptions);
  if (response.isError && /approval required/i.test(text(response))) {
    const approvalToken = await approve("civil3d_run_code", "run", parameters);
    response = await call("civil3d_run_code", { ...parameters, approvalToken }, requestOptions);
  }
  if (response.isError) throw new Error(`civil3d_run_code failed: ${text(response)}`);
  return response.structuredContent.result;
}

async function callGatedRun(parameters) {
  let response = await call("civil3d_run_code", parameters);
  if (response.isError && /approval required/i.test(text(response))) {
    const approvalToken = await approve("civil3d_run_code", "run", parameters);
    response = await call("civil3d_run_code", { ...parameters, approvalToken });
  }
  return response;
}

// One JSON-RPC request straight to the plugin's port, bypassing the MCP server
// (and its approval): for the token and start-window cases. The socket is not
// half-closed before the answer, because the plugin cancels a request whose
// client closes.
function rawRpc(method, params, timeoutMs = 120_000) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: "127.0.0.1", port: Number(process.env.CIVIL3D_PORT ?? 8757) });
    let data = "";
    let settled = false;
    const finish = (fn, value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.destroy();
      fn(value);
    };
    const timer = setTimeout(() => finish(reject, new Error(`raw ${method} timed out`)), timeoutMs);
    socket.setEncoding("utf8");
    socket.on("connect", () => socket.write(JSON.stringify({ jsonrpc: "2.0", id: `smoke-${process.pid}-${method}`, method, params })));
    socket.on("data", (chunk) => {
      data += chunk;
      try { finish(resolve, JSON.parse(data)); } catch { /* not complete yet */ }
    });
    socket.on("error", (error) => finish(reject, error));
    socket.on("close", () => finish(reject, new Error(`raw ${method}: connection closed without a complete answer (${data.length} chars)`)));
  });
}

async function check(name, fn) {
  try {
    await fn();
  } catch (error) {
    record(name, false, error instanceof Error ? error.message : String(error));
  }
}

try {
  await client.connect(transport);
  const health = await call("civil3d_health", {});
  if (health.isError) throw new Error(`civil3d_health failed: ${text(health)}`);
  const runCodeApi = health.structuredContent?.result?.runCodeApi ?? 1;
  const tools = await client.listTools();
  const tool = tools.tools.find((item) => item.name === "civil3d_run_code");
  record("the server lists civil3d_run_code as destructive", Boolean(tool && tool.annotations?.destructiveHint === true));

  let drawingName = "";
  if (options["no-drawing"]) {
    if (health.structuredContent?.result?.drawingLoaded !== false) {
      throw new Error("--no-drawing requires every drawing to be closed before testing");
    }
    if (runCodeApi < 4) record("PR 3 no-drawing checks: skipped (plugin runCodeApi below 4)", true, { runCodeApi });
    else {
    await check("PR 3: OpenSide read with no drawing", async () => {
      if (!sidePath) throw new Error("--no-drawing requires --side <scratch drawing path>");
      const response = await run({ code: "return new { hasDrawing = HasDrawing, side = OpenSide((string)Args[\"path\"]!).Filename };", args: { path: sidePath } });
      record("PR 3: OpenSide read with no drawing", response.status === "ok" && response.result?.hasDrawing === false
        && typeof response.result?.side === "string" && !Object.hasOwn(response, "drawing"), response);
    });
    await check("PR 3: Db reports a clear runtime error with no drawing", async () => {
      const response = await run({ code: "return Db.Filename;" });
      record("PR 3: Db reports a clear runtime error with no drawing", response.status === "runtime_error"
        && response.error?.message === "No drawing is open in Civil 3D, so Db is not available: read files with OpenSide(path) and TransactionFor(side).", response);
    });
    await check("PR 3: write with no drawing is refused", async () => {
      const response = await callGatedRun({ code: "return 1;", mode: "write" });
      record("PR 3: write with no drawing is refused", response.isError === true && response.errorCode === "CIVIL3D.NO_DRAWING", response);
    });
    }
  } else {
  const info = await run({ code: "return new { name = Doc.Name, dbmod = Convert.ToInt32(Application.GetSystemVariable(\"DBMOD\")) };" });
  drawingName = info.result?.name ?? "";
  record("read: basic run returns the active drawing", info.status === "ok" && drawingName.length > 0, { drawing: drawingName, timing: info.timing });
  if (expectDrawing && !drawingName.toLowerCase().includes(expectDrawing.toLowerCase())) {
    throw new Error(`Active drawing '${drawingName}' does not contain '${expectDrawing}'; stopping before any write.`);
  }

  if (options.other) {
    if (runCodeApi < 4) {
      record("PR 3 other-drawing checks: skipped (plugin runCodeApi below 4)", true, { runCodeApi });
    }
    else {
    const otherPath = resolve(options.other);
    const otherName = otherPath.split(/[\\/]/).at(-1);
    if (drawingName.toLowerCase() === otherPath.toLowerCase()) throw new Error("--other must name a different, already open drawing");
    const probe = "return new { name = Doc.Name, hasDrawing = HasDrawing };";
    const before = await run({ code: "return new { name = Doc.Name, dbmod = Convert.ToInt32(Application.GetSystemVariable(\"DBMOD\")) };" });
    for (const key of [otherPath, otherName]) {
      await check(`PR 3: read other drawing by ${key === otherPath ? "path" : "name"}`, async () => {
        const selected = await run({ code: probe, document: key });
        const after = await run({ code: "return new { name = Doc.Name, dbmod = Convert.ToInt32(Application.GetSystemVariable(\"DBMOD\")) };" });
        record(`PR 3: read other drawing by ${key === otherPath ? "path" : "name"}`,
          selected.status === "ok" && selected.result?.name?.toLowerCase() === otherPath.toLowerCase()
          && selected.result?.hasDrawing === true && selected.drawing?.toLowerCase() === otherPath.toLowerCase()
          && JSON.stringify(after.result) === JSON.stringify(before.result),
          { selected: selected.result, responseDrawing: selected.drawing, activeBefore: before.result, activeAfter: after.result });
      });
    }
    if (runCodeApi < 5) {
      await check("PR 3: write to other open drawing is refused", async () => {
        const response = await callGatedRun({ code: "return 1;", mode: "write", document: otherPath });
        record("PR 3: write to other open drawing is refused", response.isError === true && response.errorCode === "CIVIL3D.INVALID_INPUT"
          && /active drawing|active document/i.test(text(response)), text(response));
      });
    }
    await check("PR 3: missing drawing lists open drawings", async () => {
      const response = await callGatedRun({ code: "return 1;", document: join(resolve(options.other, ".."), `missing-pr3-${process.pid}.dwg`) });
      record("PR 3: missing drawing lists open drawings", response.isError === true
        && /not open/i.test(text(response)) && /Open drawings:/i.test(text(response))
        && text(response).toLowerCase().includes(otherName.toLowerCase())
        && text(response).toLowerCase().includes(drawingName.toLowerCase()), text(response));
    });
    }
  }

  if (runCodeApi < 3) {
    await check("skew guard: copyOutputsTo refused by an older plugin", async () => {
      const parameters = { code: "return 1;", copyOutputsTo: join(tmpdir(), `run-code-skew-${process.pid}`) };
      const approvalToken = approvalMode === "token" ? await approve("civil3d_run_code", "run", parameters) : undefined;
      const response = await call("civil3d_run_code", { ...parameters, ...(approvalToken ? { approvalToken } : {}) });
      record("skew guard: copyOutputsTo refused by an older plugin",
        response.isError === true && /older than this server/i.test(text(response)) && /nothing ran/i.test(text(response)),
        { runCodeApi, response: text(response).slice(0, 240) });
    });
    record("PR 2 checks: skipped (plugin runCodeApi below 3)", true, { runCodeApi });
  } else {
    await check("PR 2: codePath/argsPath strips BOM and hashes script", async () => {
      const folder = mkdtempSync(join(tmpdir(), "run-code-files-"));
      try {
        const code = "return (string)Args[\"echo\"]!;";
        const codePath = join(folder, "script.cs");
        const argsPath = join(folder, "args.json");
        writeFileSync(codePath, `\uFEFF${code}`, "utf8");
        writeFileSync(argsPath, JSON.stringify({ echo: "pr2-file-arg" }), "utf8");
        const response = await run({ codePath, argsPath });
        const hash = createHash("sha256").update(code, "utf8").digest("hex");
        record("PR 2: codePath/argsPath strips BOM and hashes script",
          response.status === "ok" && response.result === "pr2-file-arg" && response.scriptSha256 === hash,
          { status: response.status, result: response.result, scriptSha256: response.scriptSha256, expected: hash });
      } finally {
        rmSync(folder, { recursive: true, force: true });
      }
    });

    await check("PR 2: copyOutputsTo copies WriteFile output", async () => {
      const folder = mkdtempSync(join(tmpdir(), "run-code-copy-"));
      try {
        const target = join(folder, "outputs");
        const output = join(target, "pr2-out.txt");
        const response = await run({ code: "return WriteFile(\"pr2-out.txt\", \"x\");", copyOutputsTo: target });
        record("PR 2: copyOutputsTo copies WriteFile output",
          response.status === "ok" && existsSync(output) && readFileSync(output, "utf8") === "x"
            && (response.copiedFiles ?? []).some((file) => resolve(file) === resolve(output)),
          { status: response.status, copiedFiles: response.copiedFiles, logFolder: response.logFolder });
      } finally {
        rmSync(folder, { recursive: true, force: true });
      }
    });

    await check("PR 2: keepOutputs:false leaves only script.cs and run.json", async () => {
      const folder = mkdtempSync(join(tmpdir(), "run-code-move-"));
      try {
        const target = join(folder, "outputs");
        const output = join(target, "pr2-out.txt");
        const response = await run({ code: "return WriteFile(\"pr2-out.txt\", \"x\");", copyOutputsTo: target, keepOutputs: false });
        const remaining = response.logFolder && existsSync(response.logFolder) ? readdirSync(response.logFolder).sort() : [];
        record("PR 2: keepOutputs:false leaves only script.cs and run.json",
          response.status === "ok" && existsSync(output) && readFileSync(output, "utf8") === "x"
            && (response.copiedFiles ?? []).some((file) => resolve(file) === resolve(output))
            && JSON.stringify(remaining) === JSON.stringify(["run.json", "script.cs"]),
          { status: response.status, copiedFiles: response.copiedFiles, remaining });
      } finally {
        rmSync(folder, { recursive: true, force: true });
      }
    });

    await check("PR 2: write timeout of 91 seconds is refused before running", async () => {
      const response = await call("civil3d_run_code", { code: "return 1;", mode: "write", timeoutSeconds: 91 });
      record("PR 2: write timeout of 91 seconds is refused before running",
        response.isError === true && /Write runs are limited to 90 seconds/i.test(text(response)), text(response).slice(0, 240));
    });

    if (longSeconds === undefined) {
      record("PR 2: long read skipped (no --long)", true);
    } else {
      await check("PR 2: long read and MCP progress", async () => {
        const progress = [];
        const response = await run({
          mode: "read", timeoutSeconds: longSeconds,
          code: `while (Elapsed.TotalSeconds < ${longSeconds} - 5) { Check(); System.Threading.Thread.Sleep(250); } return Elapsed.TotalSeconds;`,
        }, {
          onprogress: (notification) => progress.push(notification),
          resetTimeoutOnProgress: true,
          timeout: 60_000,
          maxTotalTimeout: (longSeconds + 120) * 1000,
        });
        record("PR 2: long read and MCP progress",
          response.status === "ok" && typeof response.result === "number"
            && response.result >= longSeconds - 6 && progress.length >= Math.floor(longSeconds / 15) - 2,
          { status: response.status, elapsed: response.result, notifications: progress.length, timeoutSeconds: longSeconds });
      });
    }

    if (damagedDir === undefined) {
      record("PR 2: damaged drawings skipped (no --damaged)", true);
    } else {
      await check("PR 2: OpenSideOrNull reports every failed drawing", async () => {
        const paths = readdirSync(damagedDir, { withFileTypes: true })
          .filter((entry) => entry.isFile() && /\.dwg$/i.test(entry.name))
          .map((entry) => resolve(damagedDir, entry.name));
        if (paths.length === 0) throw new Error(`No .dwg files in ${damagedDir}`);
        paths.push(join(resolve(damagedDir), `missing-pr2-${process.pid}.dwg`), drawingName);
        const response = await run({
          args: { paths },
          code: [
            "var opened = new List<object>();",
            "foreach (var node in Args[\"paths\"]!.AsArray())",
            "{",
            "  var path = (string)node!;",
            "  var side = OpenSideOrNull(path);",
            "  opened.Add(new { path, opened = side != null });",
            "}",
            "return opened;",
          ].join("\n"),
        });
        const outcomes = response.result ?? [];
        const failed = Array.isArray(outcomes) ? outcomes.filter((item) => !item.opened) : [];
        record("PR 2: OpenSideOrNull reports every failed drawing",
          response.status === "ok" && Array.isArray(outcomes) && outcomes.length === paths.length
            && outcomes.some((item) => item.opened) && failed.length > 0
            && failed.every((item) => (response.sideErrors ?? []).some((error) => error.path === item.path && "errorStatus" in error)),
          { status: response.status, outcomes, sideErrors: response.sideErrors });
      });
    }

    await check("PR 2: Try records one error and continues", async () => {
      const response = await run({ code: [
        "var done = new List<int>();",
        "for (var i = 0; i < 3; i++)",
        "  Try($\"item-{i}\", () => { if (i == 1) throw new InvalidOperationException(\"middle\"); done.Add(i); });",
        "return done;",
      ].join("\n") });
      record("PR 2: Try records one error and continues",
        response.status === "ok" && JSON.stringify(response.result) === "[0,2]"
          && response.tryErrors?.length === 1 && response.tryErrors[0].label === "item-1",
        { status: response.status, result: response.result, tryErrors: response.tryErrors });
    });

    await check("PR 2: ById resolves a handle and rejects an unknown one", async () => {
      const known = await run({ code: "return ById(Db.BlockTableId.Handle.ToString()) == Db.BlockTableId;" });
      const missing = await run({ code: "return ById(\"FFFFFFFF\") == ObjectId.Null;" });
      record("PR 2: ById resolves a handle and rejects an unknown one",
        known.status === "ok" && known.result === true && missing.status === "runtime_error"
          && /Object handle FFFFFFFF was not found or is erased/.test(missing.error?.message ?? ""),
        { known: known.result, missing: missing.status, error: missing.error?.message });
    });

    await check("PR 2: PaperSpace lists layouts for an unknown name", async () => {
      // The unknown name lists the drawing's layouts; the first listed one must then work.
      const missing = await run({ code: "return PaperSpace(\"RUNCODE-SMOKE-NO-LAYOUT\").Count();" });
      const first = (/Available layouts:s*([^,.]+)/i.exec(missing.error?.message ?? "") ?? [])[1]?.trim();
      const known = first ? await run({ code: "return PaperSpace((string)Args[\"layout\"]).Count();", args: { layout: first } }) : { status: "skipped" };
      record("PR 2: PaperSpace lists layouts for an unknown name",
        known.status === "ok" && typeof known.result === "number" && missing.status === "runtime_error" && Boolean(first),
        { known: known.status, count: known.result, missing: missing.error?.message });
    });

    await check("PR 2: plugin-internal CivilExecution call is rejected", async () => {
      const response = await run({ code: "Civil3DMcpPlugin.CivilExecution.QueueCommandLine(\"x\"); return 1;" });
      record("PR 2: plugin-internal CivilExecution call is rejected", response.status === "rejected",
        { status: response.status, errors: response.errors });
    });

    await check("PR 2: EnsureLayer is rejected in read mode", async () => {
      const response = await run({ code: "return EnsureLayer(\"RUNCODE-SMOKE-LAYER\", 1).Handle.ToString();" });
      record("PR 2: EnsureLayer is rejected in read mode", response.status === "rejected",
        { status: response.status, errors: response.errors });
    });
  }

  const noTokenName = approvalMode === "client"
    ? "approval: direct run without a token succeeds with approvedBy client"
    : "approval: refused without a token";
  await check(noTokenName, async () => {
    const response = await call("civil3d_run_code", { code: "return 1;" });
    const passed = approvalMode === "client"
      ? response.isError !== true && response.structuredContent?.result?.approvedBy === "client"
        && response.structuredContent?.result?.status === "ok" && response.structuredContent?.result?.result === 1
      : response.isError === true && /approval required/i.test(text(response));
    record(noTokenName, passed, text(response).slice(0, 160));
  });

  const wrongTokenName = approvalMode === "client"
    ? "approval: a token for other code is still refused"
    : "approval: a token for other code is refused";
  await check(wrongTokenName, async () => {
    const token = await approve("civil3d_run_code", "run", { code: "return 1;" });
    const response = await call("civil3d_run_code", { code: "return 2;", approvalToken: token });
    record(wrongTokenName, response.isError === true && /does not match/i.test(text(response)), text(response).slice(0, 160));
  });

  await check("read: counts model space and logs", async () => {
    const response = await run({ code: "var n = ModelSpace<Entity>().Count();\nLog($\"{n} entities\");\nreturn n;" });
    record("read: counts model space and logs", response.status === "ok" && typeof response.result === "number" && /entities/.test(response.output ?? ""), { result: response.result, timing: response.timing });
  });

  await check("read: adds no change to the drawing (DBMOD unchanged)", async () => {
    const before = await run({ code: "return Convert.ToInt32(Application.GetSystemVariable(\"DBMOD\"));" });
    await run({ code: "foreach (var e in ModelSpace<Entity>()) { var x = e.Layer; }\nreturn 0;" });
    const after = await run({ code: "return Convert.ToInt32(Application.GetSystemVariable(\"DBMOD\"));" });
    record("read: adds no change to the drawing (DBMOD unchanged)", before.result === after.result, { before: before.result, after: after.result });
  });

  await check("compile error with its line", async () => {
    const response = await run({ code: "var a = 1;\nvar b = a + ;\nreturn b;" });
    record("compile error with its line", response.status === "compile_error" && response.errors?.[0]?.line === 2, response.errors);
  });

  await check("read mode refuses OpenMode.ForWrite", async () => {
    const response = await run({ code: "foreach (var e in ModelSpace<Entity>()) Tr.GetObject(e.ObjectId, OpenMode.ForWrite);\nreturn 0;" });
    record("read mode refuses OpenMode.ForWrite", response.status === "rejected" && response.errors?.[0]?.code === "C3DMCP001", response.errors);
  });

  await check("read mode refuses Db.Ltscale++", async () => {
    const response = await run({ code: "Db.Ltscale++;\nreturn 0;" });
    record("read mode refuses Db.Ltscale++", response.status === "rejected" && response.errors?.[0]?.code === "C3DMCP001", response.errors);
  });

  await check("using on Tr does nothing: Tr still works after it", async () => {
    const response = await run({ code: "{ using var tx = Tr; }\nreturn Open<DBObject>(Db.BlockTableId).Handle.ToString();" });
    record("using on Tr does nothing: Tr still works after it", response.status === "ok" && typeof response.result === "string", { status: response.status, result: response.result, error: response.error });
  });

  await check("a Tr kept in Session throws in a later run (no crash)", async () => {
    await run({ code: "Session[\"smoke-tr\"] = Tr;\nreturn 1;" });
    const later = await run({ code: "return ((Transaction)Session[\"smoke-tr\"]).NumberOfOpenedObjects;" });
    const after = await run({ code: "Session.TryRemove(\"smoke-tr\", out _);\nreturn 7;" });
    record("a Tr kept in Session throws in a later run (no crash)", later.status === "runtime_error" && after.status === "ok" && after.result === 7,
      { later: later.status, error: later.error?.message, after: after.status });
  });

  await check("read: a nested open-close transaction in a using works", async () => {
    const response = await run({ code: "using var t = Db.TransactionManager.StartOpenCloseTransaction();\nvar bt = (BlockTable)t.GetObject(Db.BlockTableId, OpenMode.ForRead);\nreturn bt.Has(BlockTableRecord.ModelSpace);" });
    record("read: a nested open-close transaction in a using works", response.status === "ok" && response.result === true, { status: response.status, error: response.error });
  });

  await check("the plugin wrote this session's rpc-token", async () => {
    const path = pluginTokenPath();
    const token = existsSync(path) ? readFileSync(path, "utf8").trim() : "";
    record("the plugin wrote this session's rpc-token", /^[0-9A-F]{64}$/i.test(token), { path, length: token.length });
  });

  await check("runCode without the token is refused (raw TCP)", async () => {
    const missing = await rawRpc("runCode", { code: "return 1;" });
    const wrong = await rawRpc("runCode", { code: "return 1;", authToken: "0".repeat(64) });
    record("runCode without the token is refused (raw TCP)",
      missing.error?.data?.code === "CIVIL3D.AUTH_REQUIRED" && wrong.error?.data?.code === "CIVIL3D.AUTH_REQUIRED",
      { missing: missing.error?.data?.code ?? missing.result, wrong: wrong.error?.data?.code ?? wrong.result });
  });

  await check("start window: a run that cannot start in time fails HOST_BUSY", async () => {
    const long = run({ code: "while (Elapsed.TotalSeconds < 12) Check();\nreturn 1;", timeoutSeconds: 30 });
    await new Promise((resolve) => setTimeout(resolve, 3000));
    const busy = await rawRpc("runCode", { code: "return 1;", mode: "read", timeoutSeconds: 10, startWithinSeconds: 3, authToken: readPluginToken() });
    const first = await long;
    record("start window: a run that cannot start in time fails HOST_BUSY",
      busy.error?.data?.code === "CIVIL3D.HOST_BUSY" && first.status === "ok",
      { busy: busy.error ? `${busy.error.data?.code}: ${busy.error.message}` : busy.result, first: first.status });
  });

  await check("runtime error with its line", async () => {
    const response = await run({ code: "var a = 1;\nthrow new InvalidOperationException(\"boom\");" });
    record("runtime error with its line", response.status === "runtime_error" && response.error?.line === 2 && response.error?.message === "boom", response.error);
  });

  await check("timeout through Check()", async () => {
    const response = await run({ code: "while (true) Check();", timeoutSeconds: 3 });
    const runMs = response.timing?.runMs ?? 0;
    record("timeout through Check()", response.status === "timeout" && runMs >= 2900 && runMs < 10_000, { status: response.status, timing: response.timing });
  });

  await check("Civil 3D still answers after the timeout", async () => {
    const response = await run({ code: "return 7;" });
    record("Civil 3D still answers after the timeout", response.status === "ok" && response.result === 7);
  });

  await check("large result goes to a file", async () => {
    const response = await run({ code: "return Enumerable.Range(0, 200000).Select(i => (i * 0.5, i * 0.25)).ToList();" });
    record("large result goes to a file", response.status === "ok" && Boolean(response.resultFile) && response.resultBytes > 65_536 && response.result === null,
      { resultFile: response.resultFile, resultBytes: response.resultBytes, resultShape: response.resultShape });
  });

  await check("WriteFile output is listed", async () => {
    const response = await run({ code: "return WriteFile(\"points.txt\", new[] { \"1 2\", \"3 4\" });" });
    record("WriteFile output is listed", response.status === "ok" && (response.files ?? []).some((file) => file.endsWith("points.txt")), response.files);
  });

  await check("AutoCAD values become compact JSON", async () => {
    const response = await run({ code: "return new { p = new Point3d(1, 2, 3), id = Db.BlockTableId, ext = new Extents3d(new Point3d(0,0,0), new Point3d(1,1,0)), t = (1, \"a\") };" });
    const r = response.result ?? {};
    record("AutoCAD values become compact JSON", response.status === "ok" && Array.isArray(r.p) && r.p[2] === 3 && typeof r.id === "string" && Array.isArray(r.ext?.min) && Array.isArray(r.t), r);
  });

  await check("dictionaries keyed by ObjectId use handles", async () => {
    const response = await run({ code: "return ModelSpace<Entity>().Take(2).ToDictionary(e => e.ObjectId, e => e.Layer);" });
    const keys = Object.keys(response.result ?? {});
    record("dictionaries keyed by ObjectId use handles", response.status === "ok" && !response.resultError && keys.length > 0 && keys.every((key) => /^[0-9A-F]+$/i.test(key)), { result: response.result, resultError: response.resultError });
  });

  await check("Session keeps data across runs", async () => {
    await run({ code: "Session[\"smoke\"] = 42;\nreturn null;" });
    const response = await run({ code: "return Session[\"smoke\"];" });
    record("Session keeps data across runs", response.status === "ok" && response.result === 42, response.result);
  });

  if (sidePath) {
    await check("side database: bulk curve sampling", async () => {
      const code = [
        "var side = OpenSide((string)Args[\"dwg\"]);",
        "long objects = 0, curves = 0, points = 0;",
        "foreach (var e in ModelSpace<Entity>(side))",
        "{",
        "  objects++;",
        "  if (e is not Curve c) continue;",
        "  double length; try { length = c.GetDistanceAtParameter(c.EndParam); } catch (Exception) { continue; }",
        "  curves++;",
        "  for (double s = 0; s <= length; s += 0.8) { try { c.GetPointAtDist(s); points++; } catch (Exception) { } }",
        "}",
        "return new { objects, curves, points, seconds = Elapsed.TotalSeconds };",
      ].join("\n");
      const response = await run({ code, args: { dwg: sidePath }, timeoutSeconds: 90 });
      record("side database: bulk curve sampling", response.status === "ok" && response.result?.objects > 0, { result: response.result, timing: response.timing, error: response.error });
    });
    await check("the working database is restored after a failing run", async () => {
      const failing = await run({
        code: "var side = OpenSide((string)Args[\"dwg\"]);\nHostApplicationServices.WorkingDatabase = side;\nthrow new InvalidOperationException(\"fail with the side database as working database\");",
        args: { dwg: sidePath },
      });
      const after = await run({ code: "return HostApplicationServices.WorkingDatabase == Db;" });
      record("the working database is restored after a failing run", failing.status === "runtime_error" && after.status === "ok" && after.result === true, { failing: failing.status, restored: after.result });
    });
    await check("a side database kept in Session throws in a later run (no crash)", async () => {
      await run({ code: "var side = OpenSide((string)Args[\"dwg\"]);\nSession[\"smoke-side\"] = side;\nreturn ModelSpace<Entity>(side).Take(10).Count();", args: { dwg: sidePath } });
      const later = await run({ code: "return TransactionFor((Database)Session[\"smoke-side\"]).NumberOfOpenedObjects;" });
      const after = await run({ code: "Session.TryRemove(\"smoke-side\", out _);\nreturn 7;" });
      record("a side database kept in Session throws in a later run (no crash)", later.status === "runtime_error" && after.status === "ok" && after.result === 7,
        { later: later.status, error: later.error?.message, after: after.status });
    });
  } else {
    record("side database: skipped (no --side)", true);
  }

  // Writes: add a tagged text, check it, undo once, check it is gone.
  const tag = `RUNCODE-SMOKE-${process.pid}`;
  const countTagged = `return ModelSpace<DBText>().Count(t => t.TextString == (string)Args["tag"]);`;
  const writesAllowed = Boolean(expectDrawing) || options["allow-writes"];
  if (!writesAllowed) {
    record("write cases: skipped (pass --expect-drawing <scratch drawing name>, or --allow-writes)", true);
  }

  if (writesAllowed) {
  if (options.other && runCodeApi >= 5) {
    const otherPath = resolve(options.other);
    const otherTag = `${tag}-OTHER`;
    const countOther = async (value) => await run({ document: otherPath,
      code: "return Tagged<DBText>(\"RUNCODE-SMOKE-PR4\").Count(t => t.TextString == (string)Args[\"tag\"]);",
      args: { tag: value } });
    const activeName = async () => (await run({ code: "return Doc.Name;" })).result;
    const addOther = [
      "var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), forWrite: true);",
      "var text = new DBText { TextString = (string)Args[\"tag\"], Position = new Point3d(-1e6, -1e6, 0), Height = 1 };",
      "ms.AppendEntity(text);",
      "Tr.AddNewlyCreatedDBObject(text, true);",
      "Tag(text, \"RUNCODE-SMOKE-PR4\");",
      "return text.Handle.ToString();",
    ].join("\n");
    try {
      await check("PR 4: targeted write commits in B and switches back to A", async () => {
        const added = await run({ document: otherPath, mode: "write", code: addOther, args: { tag: otherTag } });
        const count = await countOther(otherTag);
        const active = await activeName();
        record("PR 4: targeted write commits in B and switches back to A",
          added.status === "ok" && added.committed === true && added.activated === true && added.switchedBack === true
            && added.drawing?.toLowerCase() === otherPath.toLowerCase()
            && count.status === "ok" && count.result === 1 && active?.toLowerCase() === drawingName.toLowerCase(),
          { status: added.status, committed: added.committed, activated: added.activated,
            switchedBack: added.switchedBack, drawing: added.drawing, count: count.result, active });
      });
      await check("PR 4: failing targeted write rolls back and switches back", async () => {
        const before = await countOther(`${otherTag}-ROLLBACK`);
        const failed = await run({ document: otherPath, mode: "write", args: { tag: `${otherTag}-ROLLBACK` },
          code: addOther.replace("return text.Handle.ToString();", "throw new InvalidOperationException(\"roll back\");") });
        const after = await countOther(`${otherTag}-ROLLBACK`);
        const committedCount = await countOther(otherTag);
        const active = await activeName();
        record("PR 4: failing targeted write rolls back and switches back",
          failed.status === "runtime_error" && failed.committed === false && failed.activated === true
            && failed.switchedBack === true && failed.drawing?.toLowerCase() === otherPath.toLowerCase()
            && before.status === "ok" && before.result === 0 && after.status === "ok" && after.result === before.result
            && committedCount.status === "ok" && committedCount.result === 1
            && active?.toLowerCase() === drawingName.toLowerCase(),
          { status: failed.status, committed: failed.committed, activated: failed.activated,
            switchedBack: failed.switchedBack, drawing: failed.drawing, before: before.result,
            after: after.result, committedCount: committedCount.result, active });
      });
    } finally {
      await check("PR 4: targeted cleanup erases smoke text in B", async () => {
        const cleanup = await run({ document: otherPath, mode: "write", args: { tag: otherTag }, code: [
          "foreach (var item in Tagged<DBText>(\"RUNCODE-SMOKE-PR4\").Where(t => t.TextString == (string)Args[\"tag\"] || t.TextString == (string)Args[\"tag\"] + \"-ROLLBACK\").ToList())",
          "  Open<DBText>(item.ObjectId, forWrite: true).Erase();",
          "return 1;",
        ].join("\n") });
        const count = await countOther(otherTag);
        const rollbackCount = await countOther(`${otherTag}-ROLLBACK`);
        const active = await activeName();
        record("PR 4: targeted cleanup erases smoke text in B",
          cleanup.status === "ok" && cleanup.switchedBack === true && count.status === "ok" && count.result === 0
            && rollbackCount.status === "ok" && rollbackCount.result === 0
            && active?.toLowerCase() === drawingName.toLowerCase(),
          { status: cleanup.status, count: count.result, rollbackCount: rollbackCount.result, active });
      });
    }
  } else if (options.other) {
    record("PR 4 targeted write checks: skipped (plugin runCodeApi below 5)", true, { runCodeApi });
  }
  if (runCodeApi >= 3) {
    await check("PR 2 write: EnsureLayer twice returns the same handle", async () => {
      const response = await run({ mode: "write", code: [
        "var first = EnsureLayer(\"RUNCODE-SMOKE-LAYER\", 1);",
        "var second = EnsureLayer(\"RUNCODE-SMOKE-LAYER\", 1);",
        "return new { first = first.Handle.ToString(), second = second.Handle.ToString() };",
      ].join("\n") });
      record("PR 2 write: EnsureLayer twice returns the same handle",
        response.status === "ok" && response.result?.first === response.result?.second
          && /^[0-9A-F]+$/i.test(response.result?.first ?? ""),
        { status: response.status, result: response.result, error: response.error });
    });

    const cleanupTagged = [
      "foreach (var entity in Tagged<Entity>(\"RUNCODE-SMOKE-PR2\").ToList())",
      "  Open<Entity>(entity.ObjectId, forWrite: true).Erase();",
      "return 1;",
    ].join("\n");
    try {
      await check("PR 2 write: Tag/Tagged rerun leaves three texts", async () => {
        const script = [
          "foreach (var entity in Tagged<Entity>(\"RUNCODE-SMOKE-PR2\").ToList())",
          "  Open<Entity>(entity.ObjectId, forWrite: true).Erase();",
          "var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), forWrite: true);",
          "for (var i = 0; i < 3; i++)",
          "{",
          "  var item = new DBText { TextString = $\"RUNCODE-SMOKE-PR2-{i}\", Position = new Point3d(-1e6 - i * 10, -1e6, 0), Height = 1 };",
          "  ms.AppendEntity(item);",
          "  Tr.AddNewlyCreatedDBObject(item, true);",
          "  Tag(item, \"RUNCODE-SMOKE-PR2\");",
          "}",
          "return Tagged<Entity>(\"RUNCODE-SMOKE-PR2\").Count();",
        ].join("\n");
        const first = await run({ mode: "write", code: script });
        const second = await run({ mode: "write", code: script });
        const after = await run({ code: "return Tagged<Entity>(\"RUNCODE-SMOKE-PR2\").Count();" });
        record("PR 2 write: Tag/Tagged rerun leaves three texts",
          first.status === "ok" && first.result === 3 && second.status === "ok" && second.result === 3
            && after.status === "ok" && after.result === 3,
          { first: first.status, firstCount: first.result, second: second.status, secondCount: second.result, after: after.result });
      });
    } finally {
      await check("PR 2 write: final tagged-text cleanup", async () => {
        const cleanup = await run({ mode: "write", code: cleanupTagged });
        const after = await run({ code: "return Tagged<Entity>(\"RUNCODE-SMOKE-PR2\").Count();" });
        record("PR 2 write: final tagged-text cleanup",
          cleanup.status === "ok" && after.status === "ok" && after.result === 0,
          { cleanup: cleanup.status, after: after.result, error: cleanup.error });
      });
    }
  }
  await check("write: adds an object, one undo removes it", async () => {
    const add = await run({
      mode: "write",
      args: { tag },
      code: [
        "var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), forWrite: true);",
        "var text = new DBText { TextString = (string)Args[\"tag\"], Position = new Point3d(-1e6, -1e6, 0), Height = 1 };",
        "ms.AppendEntity(text);",
        "Tr.AddNewlyCreatedDBObject(text, true);",
        "return text.Handle.ToString();",
      ].join("\n"),
    });
    const afterAdd = await run({ code: countTagged, args: { tag } });
    // Undo only what this case committed: an undo after a rolled-back add would revert the
    // drawing's previous undo group instead.
    let undo = { isError: true, content: [{ text: "not sent: the add did not commit" }] };
    let afterUndo = { result: afterAdd.result };
    if (add.status === "ok" && add.committed === true && afterAdd.result === 1) {
      const undoToken = await approve("civil3d_drawing", "undo", { action: "undo", steps: 1 });
      undo = await call("civil3d_drawing", { action: "undo", steps: 1, approvalToken: undoToken });
      await new Promise((resolve) => setTimeout(resolve, 1500));
      afterUndo = await run({ code: countTagged, args: { tag } });
    }
    record("write: adds an object, one undo removes it",
      add.status === "ok" && add.committed === true && afterAdd.result === 1 && !undo.isError && afterUndo.result === 0,
      { add: add.status, committed: add.committed, afterAdd: afterAdd.result, undo: undo.isError ? text(undo) : "ok", afterUndo: afterUndo.result });
  });

  await check("write: an exception rolls the change back", async () => {
    const failed = await run({
      mode: "write",
      args: { tag: `${tag}-ROLLBACK` },
      code: [
        "var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), forWrite: true);",
        "var text = new DBText { TextString = (string)Args[\"tag\"], Position = new Point3d(-1e6, -1e6, 0), Height = 1 };",
        "ms.AppendEntity(text);",
        "Tr.AddNewlyCreatedDBObject(text, true);",
        "throw new InvalidOperationException(\"roll back\");",
      ].join("\n"),
    });
    const after = await run({ code: countTagged, args: { tag: `${tag}-ROLLBACK` } });
    record("write: an exception rolls the change back", failed.status === "runtime_error" && after.result === 0, { failed: failed.status, after: after.result });
  });

  await check("write: a timeout the script catches is still rolled back", async () => {
    const swallowed = await run({
      mode: "write",
      timeoutSeconds: 2,
      args: { tag: `${tag}-SWALLOWED` },
      code: [
        "var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), forWrite: true);",
        "var text = new DBText { TextString = (string)Args[\"tag\"], Position = new Point3d(-1e6, -1e6, 0), Height = 1 };",
        "ms.AppendEntity(text);",
        "Tr.AddNewlyCreatedDBObject(text, true);",
        "try { while (true) Check(); } catch (Exception) { }",
        "return \"caught it\";",
      ].join("\n"),
    });
    const after = await run({ code: countTagged, args: { tag: `${tag}-SWALLOWED` } });
    record("write: a timeout the script catches is still rolled back", swallowed.status === "timeout" && after.result === 0, { status: swallowed.status, after: after.result, warnings: swallowed.warnings });
  });

  await check("write: Commit through an alias of Tr is refused and rolled back", async () => {
    const aliased = await run({
      mode: "write",
      args: { tag: `${tag}-ALIAS` },
      code: [
        "var tx = Tr;",
        "var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), forWrite: true);",
        "var text = new DBText { TextString = (string)Args[\"tag\"], Position = new Point3d(-1e6, -1e6, 0), Height = 1 };",
        "ms.AppendEntity(text);",
        "tx.AddNewlyCreatedDBObject(text, true);",
        "tx.Commit();",
        "return 1;",
      ].join("\n"),
    });
    const after = await run({ code: countTagged, args: { tag: `${tag}-ALIAS` } });
    record("write: Commit through an alias of Tr is refused and rolled back",
      aliased.status === "runtime_error" && aliased.committed === false && /owns Tr/.test(aliased.error?.message ?? "") && after.result === 0,
      { status: aliased.status, committed: aliased.committed, error: aliased.error?.message, after: after.result });
  });

  await check("write: a nested transaction in a using commits with the run", async () => {
    const nested = await run({
      mode: "write",
      args: { tag: `${tag}-NESTED` },
      code: [
        "using (var t = Db.TransactionManager.StartTransaction())",
        "{",
        "  var ms = (BlockTableRecord)t.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(Db), OpenMode.ForWrite);",
        "  var text = new DBText { TextString = (string)Args[\"tag\"], Position = new Point3d(-1e6, -1e6, 0), Height = 1 };",
        "  ms.AppendEntity(text);",
        "  t.AddNewlyCreatedDBObject(text, true);",
        "  t.Commit();",
        "}",
        "throw new InvalidOperationException(\"roll back the outer run\");",
      ].join("\n"),
    });
    const after = await run({ code: countTagged, args: { tag: `${tag}-NESTED` } });
    // The nested commit lands in the run's transaction, so the run's rollback removes it too.
    // Only the script's own last line may fail: an earlier error would also leave nothing behind.
    record("write: a nested transaction in a using commits with the run",
      nested.status === "runtime_error" && nested.error?.message === "roll back the outer run" && nested.committed === false && after.result === 0,
      { status: nested.status, error: nested.error?.message, committed: nested.committed, after: after.result, warnings: nested.warnings });
  });

  await check("write: using var tx = Tr commits as usual, one undo removes it", async () => {
    const add = await run({
      mode: "write",
      args: { tag: `${tag}-USING` },
      code: [
        "using var tx = Tr;",
        "var ms = (BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(Db), OpenMode.ForWrite);",
        "var text = new DBText { TextString = (string)Args[\"tag\"], Position = new Point3d(-1e6, -1e6, 0), Height = 1 };",
        "ms.AppendEntity(text);",
        "tx.AddNewlyCreatedDBObject(text, true);",
        "return text.Handle.ToString();",
      ].join("\n"),
    });
    const afterAdd = await run({ code: countTagged, args: { tag: `${tag}-USING` } });
    let undo = { isError: true, content: [{ text: "not sent: the add did not commit" }] };
    let afterUndo = { result: afterAdd.result };
    if (add.status === "ok" && add.committed === true && afterAdd.result === 1) {
      const undoToken = await approve("civil3d_drawing", "undo", { action: "undo", steps: 1 });
      undo = await call("civil3d_drawing", { action: "undo", steps: 1, approvalToken: undoToken });
      await new Promise((resolve) => setTimeout(resolve, 1500));
      afterUndo = await run({ code: countTagged, args: { tag: `${tag}-USING` } });
    }
    record("write: using var tx = Tr commits as usual, one undo removes it",
      add.status === "ok" && add.committed === true && afterAdd.result === 1 && !undo.isError && afterUndo.result === 0,
      { add: add.status, committed: add.committed, error: add.error?.message, afterAdd: afterAdd.result, undo: undo.isError ? text(undo) : "ok", afterUndo: afterUndo.result });
  });

  }

  }
  console.log(JSON.stringify({ status: failures === 0 ? "passed" : "failed", failures, drawing: drawingName, results }, null, 2));
  process.exitCode = failures === 0 ? 0 : 1;
} catch (error) {
  console.error(`Live run-code smoke stopped: ${error instanceof Error ? error.message : String(error)}`);
  console.log(JSON.stringify({ status: "stopped", failures, results }, null, 2));
  process.exitCode = 1;
} finally {
  // Closing the stdio transport can leave a promise that never settles once
  // the server exits; Node would then end with code 13 (unsettled top-level
  // await) over the real result, so bound it and exit explicitly.
  await Promise.race([
    client.close().catch(() => undefined),
    new Promise((resolve) => setTimeout(resolve, 5000)),
  ]);
}
process.exit(process.exitCode ?? 0);
