import { format } from "node:util";
import type { AddressInfo } from "node:net";
import type { Server } from "node:http";
import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { sendCommandMock } = vi.hoisted(() => ({ sendCommandMock: vi.fn() }));
vi.mock("../src/utils/ConnectionManager.js", () => ({
  effectivePluginHost: "127.0.0.1",
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({ sendCommand: sendCommandMock }),
}));
vi.mock("../src/utils/pluginAuth.js", () => ({ readPluginToken: () => undefined }));

type Handler = NonNullable<ReturnType<typeof import("../src/tools/toolHandlerRegistry.js").getToolHandler>>;
type Result = Awaited<ReturnType<Handler>> & { _meta?: { requestId: string } };

let policy: typeof import("../src/tools/approvalPolicy.js").approvalPolicy;
let target: import("../src/tools/approvalPolicy.js").ApprovalTarget;
let directHandler: Handler;
let registryHandler: Handler;
let directHandlers: Map<string, Handler>;
let httpServer: Server | undefined;
let drawing: string;
const parameters = { code: "return 1;" };

beforeEach(async () => {
  vi.resetModules();
  vi.stubEnv("CIVIL3D_LOG_LEVEL", "info");
  vi.stubEnv("CIVIL3D_RUN_CODE_APPROVAL", "token");
  vi.spyOn(console, "error").mockImplementation(() => {});
  drawing = "drawing-a";
  sendCommandMock.mockReset();
  sendCommandMock.mockImplementation(async (method: string) => {
    if (method === "getCivil3DHealth") return { drawingLoaded: true };
    if (method === "getDrawingInfo") return { name: drawing };
    if (method === "getProjectContext") return {
      drawingInfo: { name: drawing }, objectTypes: [], selectedObjects: [],
    };
    if (method === "runCode") return {
      runId: "r", mode: "read", status: "ok", ok: true, logFolder: "x",
      timing: { compileMs: 0, runMs: 0, totalMs: 0 },
    };
    throw new Error(`Unexpected command: ${method}`);
  });

  const { CODE_DOMAIN_DEFINITION: definition } = await import("../src/tools/domains/codeDomain.js");
  const { DOCS_DOMAIN_DEFINITION: docsDefinition } = await import("../src/tools/domains/docsDomain.js");
  const { captureDomainToolHandlers, registerSelectedDomainTools } = await import("../src/tools/domainRuntime.js");
  const { getToolHandler } = await import("../src/tools/toolHandlerRegistry.js");
  ({ approvalPolicy: policy } = await import("../src/tools/approvalPolicy.js"));
  target = {
    toolName: "civil3d_run_code", action: "run",
    capabilities: definition.actions.run.capabilities,
    safeForRetry: false, requiresActiveDrawing: false,
  };
  directHandlers = new Map();
  const server = {
    registerTool: (name: string, _config: unknown, handler: Handler) => { directHandlers.set(name, handler); },
  } as unknown as McpServer;
  registerSelectedDomainTools(server, definition, definition.exposures);
  captureDomainToolHandlers(definition);
  registerSelectedDomainTools(server, docsDefinition, docsDefinition.exposures);
  captureDomainToolHandlers(docsDefinition);
  directHandler = directHandlers.get(target.toolName)!;
  registryHandler = getToolHandler(target.toolName)!;
});

afterEach(async () => {
  const closing = httpServer;
  httpServer = undefined;
  if (closing) await new Promise<void>((resolve, reject) => closing.close((error) => error ? reject(error) : resolve()));
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
});

const approvalRequiredMessage = "Approval required for 'civil3d_run_code' action 'run'. " +
  "Call civil3d_request_approval with the same toolName, action, and parameters, then retry with approvalToken.";
const invalidTokenMessage = "Approval token is missing, expired, or has already been used.";
const runCodeFailure = (message: string) => `civil3d_run_code action 'run' failed: ${message}`;
const orchestrate = (toolParameters: Record<string, unknown>) => ({
  toolName: "civil3d_run_code", execute: true, toolParameters,
});
const wrapperCases = [
  {
    name: "orchestrate -> run_code",
    tool: "civil3d_orchestrate",
    args: orchestrate,
    failure: (message: string) => `civil3d_orchestrate action 'orchestrate' failed: ${runCodeFailure(message)}`,
  },
  {
    name: "docs -> run_code",
    tool: "civil3d_docs",
    args: (toolParameters: Record<string, unknown>) => ({ action: "orchestrate", ...orchestrate(toolParameters) }),
    failure: (message: string) => `civil3d_docs action 'orchestrate' failed: ${runCodeFailure(message)}`,
  },
  {
    name: "orchestrate -> docs -> run_code",
    tool: "civil3d_orchestrate",
    args: (toolParameters: Record<string, unknown>) => ({
      toolName: "civil3d_docs", toolAction: "orchestrate", execute: true,
      toolParameters: { action: "orchestrate", ...orchestrate(toolParameters) },
    }),
    failure: (message: string) => "civil3d_orchestrate action 'orchestrate' failed: " +
      `civil3d_docs action 'orchestrate' failed: ${runCodeFailure(message)}`,
  },
];

function expectSingleRefusalLog(message: string, requestId?: string) {
  const lines = vi.mocked(console.error).mock.calls.map((args) => format(...args));
  expect(lines.every((line) => !line.includes("[ERROR]") && !line.includes("    at ") && !/[\r\n]/.test(line))).toBe(true);
  const warnings = lines.filter((line) => line.includes("[WARN]"));
  expect(warnings).toHaveLength(1);
  expect(JSON.parse(warnings[0].slice(warnings[0].indexOf("{")))).toMatchObject({
    tool: target.toolName, action: target.action, requestId: requestId ?? expect.any(String), error: message,
  });
  expect(sendCommandMock.mock.calls.some(([method]) => method === "runCode")).toBe(false);
}

function expectRefusal(result: Result, message: string, errorCode = "CIVIL3D.INTERNAL_ERROR", rpcCode = -32000) {
  expect(result).toEqual({
    content: [{ type: "text", text: `civil3d_run_code action 'run' failed: ${message}` }],
    isError: true, errorCode, rpcCode, _meta: { requestId: expect.any(String) },
  });
  const calls = vi.mocked(console.error).mock.calls;
  expect(calls).toHaveLength(1);
  const line = format(...calls[0]);
  expect(line).toContain("[WARN]");
  expect(line).not.toContain("[ERROR]");
  expect(line).not.toContain("    at ");
  expect(line).not.toMatch(/[\r\n]/);
  expect(JSON.parse(line.slice(line.indexOf("{")))).toMatchObject({
    tool: target.toolName, action: target.action, requestId: result._meta!.requestId, error: message,
  });
  expect(sendCommandMock.mock.calls.some(([method]) => method === "runCode")).toBe(false);
}

describe.each(["direct MCP", "registry"])("approval logging through %s", (route) => {
  const call = (args: Record<string, unknown>) =>
    (route === "direct MCP" ? directHandler : registryHandler)(args) as Promise<Result>;

  it("logs approval required once without a stack and preserves the client error", async () => {
    const result = await call(parameters);
    expectRefusal(result,
      "Approval required for 'civil3d_run_code' action 'run'. " +
      "Call civil3d_request_approval with the same toolName, action, and parameters, then retry with approvalToken.",
      "CIVIL3D.CONFLICT", -32009,
    );
  });

  it("logs an unknown token once without a stack", async () => {
    const result = await call({ ...parameters, approvalToken: "unknown-token" });
    expectRefusal(result, "Approval token is missing, expired, or has already been used.");
  });

  it("logs an expired token once without a stack", async () => {
    const receipt = await policy.requestApproval(target, parameters, -1);
    const result = await call({ ...parameters, approvalToken: receipt.approvalToken });
    expectRefusal(result, "Approval token has expired. Request a new approval.");
  });

  it("logs a used token once without a stack", async () => {
    const receipt = await policy.requestApproval(target, parameters);
    const approved = { ...parameters, approvalToken: receipt.approvalToken };
    await policy.authorize(target, approved);
    const result = await call(approved);
    expectRefusal(result, "Approval token is missing, expired, or has already been used.");
  });

  it("logs mismatched parameters once without a stack", async () => {
    const receipt = await policy.requestApproval(target, parameters);
    const result = await call({ code: "return 2;", approvalToken: receipt.approvalToken });
    expectRefusal(result, "Approval token does not match this tool action and its parameters.");
  });

  it("logs a changed drawing once without a stack", async () => {
    const receipt = await policy.requestApproval(target, parameters);
    drawing = "drawing-b";
    const result = await call({ ...parameters, approvalToken: receipt.approvalToken });
    expectRefusal(result, "The active drawing changed after approval. Request approval for the current drawing.");
  });

  it("keeps the full stack for an unexpected error even when its message mentions approval", async () => {
    const receipt = await policy.requestApproval(target, parameters);
    const error = new Error("Approval required: unexpected plugin failure");
    sendCommandMock.mockImplementation(async () => { throw error; });
    const result = await call({ ...parameters, approvalToken: receipt.approvalToken });
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toBe(`civil3d_run_code action 'run' failed: ${error.message}`);
    const output = vi.mocked(console.error).mock.calls.map((args) => format(...args)).join("\n");
    expect(output).toContain("[ERROR]");
    expect(output).toContain("    at ");
    // JSON loggers escape newlines; both structured and raw stacks must retain every frame.
    expect(output.replaceAll("\\n", "\n")).toContain(error.stack);
  });
});

describe.each(["token", "client"])("nested approval logging in %s mode", (mode) => {
  describe.each([
    { name: "missing approval", token: undefined, message: approvalRequiredMessage, errorCode: "CIVIL3D.CONFLICT", rpcCode: -32009, status: 409 },
    { name: "invalid approval", token: "unknown-token", message: invalidTokenMessage, errorCode: "CIVIL3D.INTERNAL_ERROR", rpcCode: -32000, status: 500 },
  ])("$name", (refusal) => {
    const message = refusal.message + (mode === "client" && !refusal.token
      ? " In client mode, calls through civil3d_orchestrate, civil3d_docs or the HTTP bridge still need a token."
      : "");
    it.each(wrapperCases)("logs only the inner refusal through MCP $name", async (route) => {
      vi.stubEnv("CIVIL3D_RUN_CODE_APPROVAL", mode);
      const args = route.args({ ...parameters, ...(refusal.token ? { approvalToken: refusal.token } : {}) });
      const result = await directHandlers.get(route.tool)!(args) as Result;
      // Compare the serialized envelope so internal propagation metadata cannot leak to clients.
      expect(JSON.parse(JSON.stringify(result))).toEqual({
        content: [{ type: "text", text: route.failure(message) }],
        isError: true, errorCode: refusal.errorCode, rpcCode: refusal.rpcCode,
        _meta: { requestId: expect.any(String) },
      });
      expectSingleRefusalLog(message, result._meta!.requestId);
    });

    it.each([
      { name: "run_code", tool: "civil3d_run_code", args: (args: Record<string, unknown>) => args, failure: runCodeFailure },
      ...wrapperCases,
    ])("logs only the inner refusal through authenticated HTTP $name", async (route) => {
      vi.stubEnv("CIVIL3D_RUN_CODE_APPROVAL", mode);
      const { startHttpBridge } = await import("../src/httpBridge.js");
      httpServer = startHttpBridge({ port: 0, host: "127.0.0.1", authToken: "test-bridge-token" });
      await new Promise<void>((resolve, reject) => {
        httpServer!.once("listening", resolve);
        httpServer!.once("error", reject);
      });
      const { port } = httpServer.address() as AddressInfo;
      const response = await fetch(`http://127.0.0.1:${port}/execute`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: "Bearer test-bridge-token", "X-Request-Id": "approval-refusal-http" },
        body: JSON.stringify({
          tool: route.tool,
          parameters: route.args({ ...parameters, ...(refusal.token ? { approvalToken: refusal.token } : {}) }),
        }),
      });
      expect(response.status).toBe(refusal.status);
      expect(await response.json()).toEqual({ error: { code: refusal.errorCode, message: route.failure(message) } });
      expect(response.headers.get("x-request-id")).toBe("approval-refusal-http");
      expectSingleRefusalLog(message, "approval-refusal-http");
    });
  });
});

it.each(wrapperCases)("keeps unexpected approval-like error stacks through MCP $name", async (route) => {
  const error = new Error("Approval required: unexpected policy failure");
  const authorize = policy.authorize.bind(policy);
  vi.spyOn(policy, "authorize").mockImplementation((approvalTarget, args, options) =>
    approvalTarget.toolName === target.toolName ? Promise.reject(error) : authorize(approvalTarget, args, options),
  );
  const result = await directHandlers.get(route.tool)!(route.args(parameters)) as Result;
  expect(result.isError).toBe(true);
  expect(result.content[0].text).toBe(route.failure(error.message));
  const output = vi.mocked(console.error).mock.calls.map((args) => format(...args)).join("\n");
  expect(output).toContain("[ERROR]");
  expect(output).not.toContain("[WARN]");
  expect(output.replaceAll("\\n", "\n")).toContain(error.stack);
});
