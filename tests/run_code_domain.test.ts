import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";

const { sendCommandMock } = vi.hoisted(() => ({
  sendCommandMock: vi.fn(),
}));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({
    sendCommand: sendCommandMock,
  }),
}));

import {
  approvalPolicy,
  ApprovalPolicyService,
  ApprovalRequiredError,
  isApprovalRequired,
} from "../src/tools/approvalPolicy.js";
import { registerApprovalTool } from "../src/tools/approvalTool.js";
import { buildExposureAnnotations, captureDomainToolHandlers, registerSelectedDomainTools } from "../src/tools/domainRuntime.js";
import type { DomainToolDefinition } from "../src/tools/domainRuntime.js";
import {
  CODE_DOMAIN_DEFINITION,
  RUN_CODE_START_WINDOW_SECONDS,
  RunCodeArgsSchema,
  RunCodeResponseSchema,
  prepareRunCode,
} from "../src/tools/domains/codeDomain.js";
import { getToolHandler } from "../src/tools/toolHandlerRegistry.js";
import { GENERATED_TOOL_CATALOG_ENTRIES, findManifestAction } from "../src/tools/toolManifest.js";
import { Civil3DRpcError, COMMAND_TIMEOUT_MS } from "../src/utils/SocketClient.js";

const runTarget = {
  toolName: "civil3d_run_code",
  action: "run",
  capabilities: CODE_DOMAIN_DEFINITION.actions.run.capabilities,
  safeForRetry: CODE_DOMAIN_DEFINITION.actions.run.safeForRetry,
  requiresActiveDrawing: CODE_DOMAIN_DEFINITION.actions.run.requiresActiveDrawing,
};

describe("civil3d_run_code domain", () => {
  const savedApprovalMode = process.env.CIVIL3D_APPROVAL_MODE;
  afterEach(() => {
    if (savedApprovalMode === undefined) delete process.env.CIVIL3D_APPROVAL_MODE;
    else process.env.CIVIL3D_APPROVAL_MODE = savedApprovalMode;
  });

  it("is registered as one tool that calls the plugin's runCode", () => {
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((item) => item.toolName === "civil3d_run_code");
    expect(entry).toBeDefined();
    expect(entry!.domain).toBe("code");
    expect(entry!.pluginMethods).toEqual(["runCode"]);
    expect(entry!.safeForRetry).toBe(false);
    expect(entry!.requiresActiveDrawing).toBe(false);
    expect(CODE_DOMAIN_DEFINITION.actions.run.requiresActiveDrawing).toBe(false);
    expect(findManifestAction("civil3d_run_code", "run")).toBeDefined();
  });

  it("always requires approval, also when approvals are disabled", () => {
    expect(isApprovalRequired(runTarget)).toBe(true);
    process.env.CIVIL3D_APPROVAL_MODE = "disabled";
    expect(isApprovalRequired(runTarget)).toBe(true);
    // Other tools still honour the switch.
    expect(isApprovalRequired({ ...runTarget, toolName: "civil3d_xref", action: "detach" })).toBe(false);
  });

  it("refuses to run without a token, also when approvals are disabled", async () => {
    process.env.CIVIL3D_APPROVAL_MODE = "disabled";
    const policy = new ApprovalPolicyService(async () => "drawing-a");
    const parameters = { code: "return 1;" };
    await expect(policy.enforce(runTarget, parameters)).rejects.toBeInstanceOf(ApprovalRequiredError);

    const receipt = await policy.requestApproval(runTarget, parameters);
    await expect(policy.enforce(runTarget, { ...parameters, approvalToken: receipt.approvalToken })).resolves.toBeUndefined();
  });

  it("binds the approval to the exact code", async () => {
    const policy = new ApprovalPolicyService(async () => "drawing-a");
    const receipt = await policy.requestApproval(runTarget, { code: "return 1;" });
    await expect(policy.enforce(runTarget, { code: "return 2;", approvalToken: receipt.approvalToken }))
      .rejects.toThrow(/does not match/);
  });

  it("is marked destructive and not read-only for MCP clients", () => {
    const annotations = buildExposureAnnotations(CODE_DOMAIN_DEFINITION, CODE_DOMAIN_DEFINITION.exposures[0]);
    expect(annotations.readOnlyHint).toBe(false);
    expect(annotations.destructiveHint).toBe(true);
    expect(annotations.idempotentHint).toBe(false);
  });

  it("validates the arguments", () => {
    const valid = { action: "run", code: "return Db.Filename;" };
    expect(RunCodeArgsSchema.safeParse(valid).success).toBe(true);
    expect(RunCodeArgsSchema.safeParse({ ...valid, mode: "write", timeoutSeconds: 90, args: { dwg: "C:/bg.dwg" } }).success).toBe(true);
    expect(RunCodeArgsSchema.safeParse({ ...valid, document: "B.dwg" }).success).toBe(true);
    expect(RunCodeArgsSchema.safeParse({ ...valid, document: "C:/Drawings/B.dwg" }).success).toBe(true);
    expect(RunCodeArgsSchema.safeParse({ ...valid, document: "" }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...valid, document: "x".repeat(1025) }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...valid, code: "" }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...valid, mode: "admin" }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...valid, timeoutSeconds: 0 }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...valid, timeoutSeconds: 600 }).success).toBe(true);
    expect(RunCodeArgsSchema.safeParse({ ...valid, timeoutSeconds: 601 }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...valid, mode: "write", timeoutSeconds: 91 }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...valid, maxInlineBytes: 100 }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...valid, args: ["not", "an", "object"] }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...valid, code: "x".repeat(200_001) }).success).toBe(false);
  });

  it("refuses code and args over about 1 MB of JSON, counted in UTF-8 bytes", () => {
    const valid = { action: "run", code: "return Args.Count;" };
    expect(RunCodeArgsSchema.safeParse({ ...valid, code: "x".repeat(199_000) }).success).toBe(true);
    // Under the 200k character limit, but escaped as \u0001 (6 bytes each) in JSON.
    expect(RunCodeArgsSchema.safeParse({ ...valid, code: "\u0001".repeat(180_000) }).success).toBe(false);

    const points = Array.from({ length: 30_000 }, (_, i) => [512345.123456 + i, 941234.987654 + i, 12.3456]);
    const result = RunCodeArgsSchema.safeParse({ ...valid, args: { pts: points } });
    expect(result.success).toBe(false);
    const issue = result.success ? undefined : result.error.issues[0];
    expect(issue?.path).toEqual(["args"]);
    expect(issue?.message).toMatch(/bytes as JSON; the limit is 1000000/);
    expect(issue?.message).toMatch(/file/);
  });

  it("issues no approval token for an oversized call", async () => {
    sendCommandMock.mockReset();
    registerApprovalTool({ registerTool: () => undefined } as unknown as McpServer);
    const requestApproval = getToolHandler("civil3d_request_approval")!;
    const result = await requestApproval({
      toolName: "civil3d_run_code",
      action: "run",
      parameters: { code: "return 1;", args: { blob: "x".repeat(1_000_001) } },
    });
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toMatch(/bytes as JSON/);
    expect(result.structuredContent).toBeUndefined();
    expect(sendCommandMock).not.toHaveBeenCalled();
  });

  it("resolves calls without an action to 'run'", () => {
    const resolved = CODE_DOMAIN_DEFINITION.exposures[0].resolveAction({ code: "return 1;", mode: "read" });
    expect(resolved.action).toBe("run");
    expect(RunCodeArgsSchema.parse(resolved.args).code).toBe("return 1;");
  });

  it("accepts the plugin's response shapes", () => {
    const base = { runId: "20261011-120000-abcd1234", mode: "read", logFolder: "C:/log/run-code/20261011-120000-abcd1234", timing: { compileMs: 180, runMs: 1200, totalMs: 1450 } };
    expect(RunCodeResponseSchema.safeParse({
      ...base, status: "ok", ok: true, drawing: "Job.dwg", output: "96913 objects\n",
      result: { objects: 96913, points: 547441 }, resultShape: "<>f__AnonymousType0`2", resultBytes: 37,
    }).success).toBe(true);
    expect(RunCodeResponseSchema.safeParse({
      ...base, status: "ok", ok: true, result: null, resultFile: "C:/log/run-code/x/result.json",
      resultBytes: 8_400_000, resultPreview: "[[1.00,2.00],...", resultShape: "array of 547441", files: ["C:/log/run-code/x/points.txt"],
    }).success).toBe(true);
    expect(RunCodeResponseSchema.safeParse({
      ...base, status: "rejected", ok: false,
      errors: [{ line: 3, column: 40, code: "C3DMCP001", message: "Read mode opens objects for read only" }],
      warnings: [{ line: 1, column: 1, code: "CS0105", message: "duplicate using" }, "ran past its budget"],
    }).success).toBe(true);
    expect(RunCodeResponseSchema.safeParse({
      ...base, status: "timeout", ok: false,
      error: { type: "Civil3DMcpPlugin.RunCodeTimeoutException", message: "budget used up", line: 12, stack: "at Run() in script:line 12" },
    }).success).toBe(true);
    expect(RunCodeResponseSchema.safeParse({ ...base, status: "exploded", ok: false }).success).toBe(false);
    expect(RunCodeResponseSchema.safeParse({ ...base, status: "ok", ok: true, scriptSha256: "xyz" }).success).toBe(false);
  });

  it("accepts committed true, false and 'unknown'", () => {
    const base = { runId: "r", mode: "write", status: "runtime_error", ok: false, logFolder: "C:/log/run-code/r", timing: { compileMs: 1, runMs: 1, totalMs: 2 } };
    expect(RunCodeResponseSchema.safeParse({ ...base, committed: true }).success).toBe(true);
    expect(RunCodeResponseSchema.safeParse({ ...base, committed: false }).success).toBe(true);
    expect(RunCodeResponseSchema.safeParse({ ...base, committed: "unknown" }).success).toBe(true);
    expect(RunCodeResponseSchema.safeParse({ ...base, committed: "maybe" }).success).toBe(false);
  });
});

describe("civil3d_run_code execution", () => {
  const okResponse = {
    runId: "20261011-120000-abcd1234",
    mode: "read",
    status: "ok",
    ok: true,
    result: 1,
    logFolder: "C:/log/run-code/20261011-120000-abcd1234",
    timing: { compileMs: 1, runMs: 1, totalMs: 2 },
  };
  const execute = CODE_DOMAIN_DEFINITION.actions.run.execute;
  let tokenDir: string;

  beforeEach(() => {
    tokenDir = mkdtempSync(join(tmpdir(), "c3d-rpc-token-"));
    vi.stubEnv("CIVIL3D_RPC_TOKEN_FILE", join(tokenDir, "rpc-token"));
    sendCommandMock.mockReset();
  });

  afterEach(() => {
    vi.unstubAllEnvs();
    rmSync(tokenDir, { recursive: true, force: true });
  });

  it("sends the session token, the start window and a timeout that covers the run", async () => {
    writeFileSync(join(tokenDir, "rpc-token"), "a1b2c3\r\n");
    sendCommandMock.mockResolvedValue(okResponse);

    await execute(RunCodeArgsSchema.parse({ action: "run", code: "return 1;", timeoutSeconds: 90 }));

    expect(RUN_CODE_START_WINDOW_SECONDS).toBe(30);
    expect(sendCommandMock).toHaveBeenCalledWith("runCode", {
      code: "return 1;",
      mode: "read",
      timeoutSeconds: 90,
      startWithinSeconds: 30,
      maxInlineBytes: 65536,
      args: {},
      authToken: "a1b2c3",
    }, { timeoutMs: Math.max(COMMAND_TIMEOUT_MS, 150_000) });
    expect(sendCommandMock.mock.calls[0][2].timeoutMs).toBeGreaterThanOrEqual(150_000);
  });

  it("keeps the command timeout for short runs and omits a missing token", async () => {
    sendCommandMock.mockResolvedValue(okResponse);

    await execute(RunCodeArgsSchema.parse({ action: "run", code: "return 1;" }));

    const [, params, options] = sendCommandMock.mock.calls[0];
    expect(params.timeoutSeconds).toBe(30);
    expect(params.startWithinSeconds).toBe(30);
    expect(params).not.toHaveProperty("authToken");
    expect(options).toEqual({ timeoutMs: Math.max(COMMAND_TIMEOUT_MS, 90_000) });
  });

  it("sends document only when set and checks API 4 before runCode", async () => {
    sendCommandMock.mockImplementation(async (method: string) =>
      method === "getCivil3DHealth" ? { runCodeApi: 4 } : okResponse);
    await execute(RunCodeArgsSchema.parse({ action: "run", code: "return Doc.Name;", document: "B.dwg" }));
    expect(sendCommandMock.mock.calls.map(([method]) => method)).toEqual(["getCivil3DHealth", "runCode"]);
    expect(sendCommandMock.mock.calls[1][1].document).toBe("B.dwg");
    sendCommandMock.mockReset();
    sendCommandMock.mockResolvedValue(okResponse);
    await execute(RunCodeArgsSchema.parse({ action: "run", code: "return 1;" }));
    expect(sendCommandMock.mock.calls[0][1]).not.toHaveProperty("document");
  });

  it.each([undefined, 3])("refuses document with runCode API %s before running", async (api) => {
    sendCommandMock.mockResolvedValue({ runCodeApi: api });
    const error = await execute(RunCodeArgsSchema.parse({ action: "run", code: "return 1;", document: "B.dwg" })).catch((e) => e);
    expect(error).toBeInstanceOf(Civil3DRpcError);
    expect(error.message).toMatch(/document needs runCode API 4/);
    expect(error.message).toMatch(/Nothing ran/);
    expect(sendCommandMock.mock.calls.map(([method]) => method)).toEqual(["getCivil3DHealth"]);
  });

  it("approves the caller's arguments, not the token added at execution", async () => {
    writeFileSync(join(tokenDir, "rpc-token"), "session-token");
    sendCommandMock.mockImplementation(async (method: string) => {
      if (method === "getCivil3DHealth") return { drawingLoaded: true };
      if (method === "getDrawingInfo") return { name: "Job.dwg" };
      if (method === "runCode") return okResponse;
      throw new Error(`unexpected ${method}`);
    });
    captureDomainToolHandlers(CODE_DOMAIN_DEFINITION);
    const rawArgs = { code: "return 1;", mode: "read", args: { n: 1 } };
    const receipt = await approvalPolicy.requestApproval(runTarget, rawArgs);

    const result = await getToolHandler("civil3d_run_code")!({ ...rawArgs, approvalToken: receipt.approvalToken });

    expect(result.isError).toBeUndefined();
    const runCall = sendCommandMock.mock.calls.find(([method]) => method === "runCode")!;
    expect(runCall[1].authToken).toBe("session-token");
    expect(result.content[0].text).not.toContain("session-token");
  });

  it.each([
    ["CIVIL3D.AUTH_REQUIRED", -32043, "runCode needs this Civil 3D session's token (%LOCALAPPDATA%\\Civil3DMcpPlugin\\rpc-token); update the civil3d-mcp server to match the plugin."],
    ["CIVIL3D.HOST_BUSY", -32029, "The script waited 31 s for other Civil 3D work and was not started; nothing changed. Retry when Civil 3D is idle."],
  ])("passes the plugin's %s error through unchanged", async (code, rpcCode, message) => {
    sendCommandMock.mockRejectedValue(new Civil3DRpcError(message, code, rpcCode));

    const error = await execute(RunCodeArgsSchema.parse({ action: "run", code: "return 1;" })).catch((e) => e);

    expect(error).toBeInstanceOf(Civil3DRpcError);
    expect(error.code).toBe(code);
    expect(error.rpcCode).toBe(rpcCode);
    expect(error.message).toBe(message);
  });

  it("tells the agent where the run's record is when the call times out", async () => {
    sendCommandMock.mockRejectedValue(new Civil3DRpcError("Command timed out after 150000ms: runCode", "CIVIL3D.TIMEOUT", -32008));

    const error = await execute(RunCodeArgsSchema.parse({ action: "run", code: "return 1;", timeoutSeconds: 90 })).catch((e) => e);

    expect(error.code).toBe("CIVIL3D.TIMEOUT");
    expect(error.message).toContain("Command timed out after 150000ms: runCode");
    expect(error.message).toContain("%LOCALAPPDATA%\\Civil3DMcpPlugin\\run-code\\");
    expect(error.message).toContain("run.json");
  });
});

describe("civil3d_run_code PR2 plumbing", () => {
  let dir: string;
  function directHandler(definition: DomainToolDefinition = CODE_DOMAIN_DEFINITION) {
    let handler: ((args: Record<string, unknown>, extra?: unknown) => Promise<{
      isError?: boolean; errorCode?: string; rpcCode?: number;
      content: Array<{ text?: string }>; structuredContent?: Record<string, unknown>;
    }>) | undefined;
    registerSelectedDomainTools({ registerTool: (_name: string, _definition: unknown, fn: typeof handler) => { handler = fn; } } as unknown as McpServer,
      definition, definition.exposures);
    return handler!;
  }
  beforeEach(() => {
    dir = mkdtempSync(join(tmpdir(), "c3d-code-files-"));
    vi.stubEnv("CIVIL3D_RUN_CODE_APPROVAL", "client");
    sendCommandMock.mockReset();
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllEnvs();
    rmSync(dir, { recursive: true, force: true });
  });

  it("validates exclusive sources, absolute paths, copy dependency and budgets", () => {
    const file = join(dir, "script.cs");
    const base = { action: "run", codePath: file };
    expect(RunCodeArgsSchema.safeParse(base).success).toBe(true);
    expect(RunCodeArgsSchema.safeParse({ action: "run" }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...base, code: "return 1;" }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...base, codePath: "relative.cs" }).success).toBe(false);
    if (process.platform === "win32") {
      expect(RunCodeArgsSchema.safeParse({ ...base, codePath: "\\drive-relative.cs" }).success).toBe(false);
    }
    expect(RunCodeArgsSchema.safeParse({ ...base, args: {}, argsPath: file }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...base, keepOutputs: false }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...base, copyOutputsTo: dir, keepOutputs: false }).success).toBe(true);
  });

  it("reads UTF-8 files, strips BOM, checks prepared size, and binds hashes", async () => {
    const codePath = join(dir, "script.cs");
    const argsPath = join(dir, "args.json");
    writeFileSync(codePath, "\uFEFFreturn Args.n;");
    writeFileSync(argsPath, "\uFEFF{\"n\":2}");
    const raw = { action: "run", codePath, argsPath };
    const prepared = await prepareRunCode(RunCodeArgsSchema.parse(raw), raw);
    expect(prepared.args.code).toBe("return Args.n;");
    expect(prepared.args.args).toEqual({ n: 2 });
    expect(prepared.approvalParameters).toEqual({ ...raw,
      codeSha256: createHash("sha256").update("\uFEFFreturn Args.n;").digest("hex"),
      argsSha256: createHash("sha256").update("\uFEFF{\"n\":2}").digest("hex"),
    });
    writeFileSync(codePath, "x".repeat(200_001));
    await expect(prepareRunCode(RunCodeArgsSchema.parse({ action: "run", codePath }), { codePath })).rejects.toThrow(/200000/);
    writeFileSync(codePath, "return 1;");
    writeFileSync(argsPath, JSON.stringify({ blob: "x".repeat(1_000_000) }));
    await expect(prepareRunCode(RunCodeArgsSchema.parse(raw), raw)).rejects.toThrow(/bytes as JSON/);
    writeFileSync(argsPath, " ".repeat(1_000_000) + "{}");
    await expect(prepareRunCode(RunCodeArgsSchema.parse(raw), raw)).rejects.toThrow(/bytes as JSON/);
  });

  it.each(["[]", "null", "42", "\"text\"", "{bad JSON}"])("refuses args file content %s", async (content) => {
    const argsPath = join(dir, "args.json");
    writeFileSync(argsPath, content);
    const raw = { action: "run", code: "return 1;", argsPath };
    await expect(prepareRunCode(RunCodeArgsSchema.parse(raw), raw)).rejects.toThrow(/JSON object/);
  });

  it("refuses an empty code file in preview and approval before any host call", async () => {
    const codePath = join(dir, "empty.cs");
    writeFileSync(codePath, "\uFEFF");
    registerApprovalTool({ registerTool: () => undefined } as unknown as McpServer);
    for (const name of ["civil3d_preview_action", "civil3d_request_approval"]) {
      const result = await getToolHandler(name)!({ toolName: "civil3d_run_code", action: "run", parameters: { codePath } });
      expect(result.isError).toBe(true);
      expect(result.content[0].text).toContain("1-200000");
    }
    expect(sendCommandMock).not.toHaveBeenCalled();
  });

  it("executes the prepared content when a file changes during the health check", async () => {
    const codePath = join(dir, "script.cs");
    writeFileSync(codePath, "return 1;");
    sendCommandMock.mockImplementation(async (method: string) => {
      if (method === "getCivil3DHealth") {
        writeFileSync(codePath, "return 2;");
        return { runCodeApi: 3 };
      }
      return { runId: "r", mode: "read", status: "ok", ok: true, logFolder: dir, timing: { compileMs: 1, runMs: 1, totalMs: 2 } };
    });
    const result = await directHandler()({ codePath, keepOutputs: true });
    expect(result.isError).toBeUndefined();
    expect(sendCommandMock.mock.calls.find(([method]) => method === "runCode")![1].code).toBe("return 1;");
    expect((result.structuredContent?.result as Record<string, unknown>).scriptSha256)
      .toBe(createHash("sha256").update("return 1;").digest("hex"));
  });

  it.each(["codePath", "argsPath"])("rejects a changed %s file after approval before runCode", async (source) => {
    const codePath = join(dir, "script.cs");
    const argsPath = join(dir, "args.json");
    writeFileSync(codePath, "return 1;");
    writeFileSync(argsPath, "{\"n\":1}");
    sendCommandMock.mockImplementation(async (method: string) => {
      if (method === "getCivil3DHealth") return { drawingLoaded: true };
      if (method === "getDrawingInfo") return { name: "Job.dwg" };
      throw new Error(`unexpected ${method}`);
    });
    registerApprovalTool({ registerTool: () => undefined } as unknown as McpServer);
    captureDomainToolHandlers(CODE_DOMAIN_DEFINITION);
    const parameters = source === "codePath" ? { codePath } : { code: "return Args.n;", argsPath };
    const receipt = await getToolHandler("civil3d_request_approval")!({
      toolName: "civil3d_run_code", action: "run", parameters,
    });
    const approvalToken = receipt.structuredContent?.approvalToken;
    expect(approvalToken).toBeTypeOf("string");
    writeFileSync(source === "codePath" ? codePath : argsPath,
      source === "codePath" ? "return 2;" : "{\"n\":2}");
    const result = await getToolHandler("civil3d_run_code")!({ ...parameters, approvalToken });
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toMatch(/does not match/);
    expect(sendCommandMock.mock.calls.some(([method]) => method === "runCode")).toBe(false);
  });

  it("guards old plugins, sends audit paths, and fills a missing script hash", async () => {
    const codePath = join(dir, "script.cs");
    writeFileSync(codePath, "return 1;");
    const handler = directHandler();
    sendCommandMock.mockImplementation(async (method: string) => {
      if (method === "getCivil3DHealth") return { runCodeApi: 2 };
      throw new Error(`unexpected ${method}`);
    });
    const rejected = await handler({ codePath, copyOutputsTo: dir });
    expect(rejected.isError).toBe(true);
    expect(rejected.content[0].text).toContain("The Civil 3D plugin is older than this server: copyOutputsTo needs runCode API 3, the loaded plugin has 2. Install the plugin from this build (Civil 3D closed). Nothing ran.");
    expect(rejected.errorCode).toBe("CIVIL3D.INVALID_INPUT");
    expect(rejected.rpcCode).toBe(-32602);
    expect(sendCommandMock.mock.calls.some(([method]) => method === "runCode")).toBe(false);
    sendCommandMock.mockReset();
    sendCommandMock.mockImplementation(async (method: string) => method === "getCivil3DHealth"
      ? { runCodeApi: 3 }
      : { runId: "r", mode: "read", status: "ok", ok: true, logFolder: dir, timing: { compileMs: 1, runMs: 1, totalMs: 2 } });
    const result = await handler({ codePath, copyOutputsTo: dir, keepOutputs: false, timeoutSeconds: 600 });
    expect(result.isError).toBeUndefined();
    const run = sendCommandMock.mock.calls.find(([method]) => method === "runCode")!;
    expect(run[1]).toMatchObject({ code: "return 1;", codePath, copyOutputsTo: dir, keepOutputs: false, timeoutSeconds: 600, approval: "client" });
    expect(run[2].timeoutMs).toBe(660_000);
    expect((result.structuredContent?.result as Record<string, unknown>).scriptSha256)
      .toBe(createHash("sha256").update("return 1;").digest("hex"));
    sendCommandMock.mockReset();
    sendCommandMock.mockResolvedValue({ drawingLoaded: true });
    const missingApi = await handler({ code: "return 1;", copyOutputsTo: dir });
    expect(missingApi.content[0].text).toContain("copyOutputsTo needs runCode API 3, the loaded plugin has 1.");
    expect(sendCommandMock.mock.calls.some(([method]) => method === "runCode")).toBe(false);
  });

  it("emits opt-in heartbeat progress below 100 and stops on completion", async () => {
    vi.useFakeTimers();
    let finish!: (value: unknown) => void;
    sendCommandMock.mockImplementation(async (method: string) => {
      if (method !== "runCode") throw new Error(`unexpected ${method}`);
      return await new Promise((resolve) => { finish = resolve; });
    });
    const handler = directHandler();
    const progress: number[] = [];
    const pending = handler(
      { code: "return 1;" },
      { _meta: { progressToken: "p" }, sendNotification: async (n: { params: { progress: number } }) => { progress.push(n.params.progress); } },
    );
    await vi.waitFor(() => expect(finish).toBeTypeOf("function"));
    await vi.advanceTimersByTimeAsync(45_000);
    expect(progress).toEqual([0, 50, 60, 50 + 50 * 30 / 90, 50 + 50 * 45 / 105]);
    expect(progress.every((value, i) => i === 0 || value > progress[i - 1])).toBe(true);
    expect(progress.every((value) => value < 100)).toBe(true);
    finish({ runId: "r", mode: "read", status: "ok", ok: true, logFolder: dir, timing: { compileMs: 1, runMs: 1, totalMs: 2 } });
    await pending;
    expect(progress.at(-1)).toBe(100);
    await vi.advanceTimersByTimeAsync(30_000);
    expect(progress.filter((n) => n > 50 && n < 100)).toHaveLength(3);
  });

  it.each(["no progress token", "another action"])("has no heartbeat for %s", async (caseName) => {
    vi.useFakeTimers();
    let finish!: (value: unknown) => void;
    sendCommandMock.mockImplementation(async () => await new Promise((resolve) => { finish = resolve; }));
    const progress: number[] = [];
    const otherDefinition: DomainToolDefinition = {
      ...CODE_DOMAIN_DEFINITION,
      actions: { run: { ...CODE_DOMAIN_DEFINITION.actions.run, progressHeartbeat: undefined,
        capabilities: ["query"], requiresActiveDrawing: false, safeForRetry: true } },
      exposures: [{ ...CODE_DOMAIN_DEFINITION.exposures[0], toolName: "civil3d_test_action" }],
    };
    const pending = directHandler(caseName === "another action" ? otherDefinition : CODE_DOMAIN_DEFINITION)(
      { code: "return 1;" },
      { ...(caseName === "another action" ? { _meta: { progressToken: 0 } } : {}),
        sendNotification: async (n: { params: { progress: number } }) => { progress.push(n.params.progress); } },
    );
    await vi.waitFor(() => expect(finish).toBeTypeOf("function"));
    await vi.advanceTimersByTimeAsync(45_000);
    expect(progress).toEqual(caseName === "another action" ? [0, 50] : []);
    finish({ runId: "r", mode: "read", status: "ok", ok: true, logFolder: dir, timing: { compileMs: 1, runMs: 1, totalMs: 2 } });
    await pending;
    await vi.advanceTimersByTimeAsync(30_000);
    expect(progress).toEqual(caseName === "another action" ? [0, 50, 100] : []);
  });
});
