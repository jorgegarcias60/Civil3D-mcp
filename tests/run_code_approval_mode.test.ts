import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";

const { sendCommandMock } = vi.hoisted(() => ({ sendCommandMock: vi.fn() }));
vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({ sendCommand: sendCommandMock }),
}));

import {
  approvalPolicy, ApprovalPolicyService, ApprovalRequiredError, ApprovalValidationError,
  isApprovalRequired, runCodeApprovalMode,
} from "../src/tools/approvalPolicy.js";
import { registerApprovalTool } from "../src/tools/approvalTool.js";
import { captureDomainToolHandlers, registerSelectedDomainTools } from "../src/tools/domainRuntime.js";
import { CODE_DOMAIN_DEFINITION } from "../src/tools/domains/codeDomain.js";
import { getToolHandler } from "../src/tools/toolHandlerRegistry.js";
import { registerTools } from "../src/tools/register.js";

const runTarget = {
  toolName: "civil3d_run_code", action: "run",
  capabilities: CODE_DOMAIN_DEFINITION.actions.run.capabilities,
  safeForRetry: false, requiresActiveDrawing: true,
};
const saveTarget = {
  toolName: "civil3d_drawing", action: "save",
  capabilities: ["edit", "manage"] as const, safeForRetry: false,
};

beforeEach(() => {
  vi.stubEnv("CIVIL3D_RUN_CODE_APPROVAL", "client");
  sendCommandMock.mockReset();
  sendCommandMock.mockImplementation(async (method: string) => {
    if (method === "getCivil3DHealth") return { drawingLoaded: true };
    if (method === "getDrawingInfo") return { name: "Job.dwg" };
    if (method === "getProjectContext") return { drawingInfo: { name: "Job.dwg" }, objectTypes: [], selectedObjects: [] };
    if (method === "runCode") return {
      runId: "r", mode: "read", status: "ok", ok: true, logFolder: "x",
      timing: { compileMs: 0, runMs: 0, totalMs: 0 },
    };
    throw new Error(`unexpected ${method}`);
  });
});
afterEach(() => vi.unstubAllEnvs());

describe("run_code client approval policy", () => {
  it("authorizes a direct call without touching the drawing fingerprint", async () => {
    const fingerprint = vi.fn(async () => { throw new Error("fingerprint called"); });
    const policy = new ApprovalPolicyService(fingerprint);
    expect(isApprovalRequired(runTarget)).toBe(true);
    await expect(policy.authorize(runTarget, { code: "return 1;" }, { directClientCall: true }))
      .resolves.toBe("client");
    expect(fingerprint).not.toHaveBeenCalled();
    await expect(policy.enforce(runTarget, { code: "return 1;" }, { directClientCall: true }))
      .resolves.toBeUndefined();
  });

  it("keeps registry, token mode, and other gated tools protected", async () => {
    const policy = new ApprovalPolicyService(async () => "drawing-a");
    await expect(policy.authorize(runTarget, { code: "return 1;" }, { directClientCall: false }))
      .rejects.toBeInstanceOf(ApprovalRequiredError);
    await expect(policy.authorize(saveTarget, { action: "save" }, { directClientCall: true }))
      .rejects.toBeInstanceOf(ApprovalRequiredError);
    vi.stubEnv("CIVIL3D_RUN_CODE_APPROVAL", "token");
    await expect(policy.authorize(runTarget, { code: "return 1;" }, { directClientCall: true }))
      .rejects.toBeInstanceOf(ApprovalRequiredError);
    vi.stubEnv("CIVIL3D_RUN_CODE_APPROVAL", "yes");
    expect(runCodeApprovalMode()).toBe("token");
    await expect(policy.authorize(runTarget, { code: "return 1;" }, { directClientCall: true }))
      .rejects.toBeInstanceOf(ApprovalRequiredError);
    vi.stubEnv("CIVIL3D_RUN_CODE_APPROVAL", " CLIENT ");
    await expect(policy.authorize(runTarget, { code: "return 1;" }, { directClientCall: true }))
      .resolves.toBe("client");
  });

  it("still validates supplied tokens against the exact code and drawing", async () => {
    let drawing = "a";
    const policy = new ApprovalPolicyService(async () => drawing);
    await expect(policy.authorize(runTarget, { code: "return 1;", approvalToken: "wrong" }, { directClientCall: true }))
      .rejects.toBeInstanceOf(ApprovalValidationError);
    const other = await policy.requestApproval(runTarget, { code: "return 1;" });
    await expect(policy.authorize(runTarget, { code: "return 2;", approvalToken: other.approvalToken }, { directClientCall: true }))
      .rejects.toBeInstanceOf(ApprovalValidationError);
    const changed = await policy.requestApproval(runTarget, { code: "return 1;" });
    drawing = "b";
    await expect(policy.authorize(runTarget, { code: "return 1;", approvalToken: changed.approvalToken }, { directClientCall: true }))
      .rejects.toBeInstanceOf(ApprovalValidationError);
    const valid = await policy.requestApproval(runTarget, { code: "return 1;" });
    await expect(policy.authorize(runTarget, { code: "return 1;", approvalToken: valid.approvalToken }, { directClientCall: true }))
      .resolves.toBe("token");
  });

  it("rejects expired tokens in client mode", async () => {
    let now = 10_000;
    const policy = new ApprovalPolicyService(async () => "drawing-a", () => now);
    const receipt = await policy.requestApproval(runTarget, { code: "return 1;" }, 30_000);
    now += 31_000;
    await expect(policy.authorize(runTarget, { code: "return 1;", approvalToken: receipt.approvalToken }, { directClientCall: true }))
      .rejects.toBeInstanceOf(ApprovalValidationError);
  });
});

describe("run_code handler routing", () => {
  it("lets only the direct MCP handler run without a token", async () => {
    const handlers = new Map<string, (args: Record<string, unknown>) => Promise<any>>();
    const fakeServer = { registerTool: (name: string, _config: unknown, handler: (args: Record<string, unknown>) => Promise<any>) => {
      handlers.set(name, handler);
    } } as unknown as McpServer;
    captureDomainToolHandlers(CODE_DOMAIN_DEFINITION);
    registerSelectedDomainTools(fakeServer, CODE_DOMAIN_DEFINITION, CODE_DOMAIN_DEFINITION.exposures);
    const direct = await handlers.get("civil3d_run_code")!({ code: "return 1;" });
    expect(direct.isError).toBeUndefined();
    expect(direct.structuredContent.result.approvedBy).toBe("client");
    expect(sendCommandMock.mock.calls.filter(([method]) => method === "runCode")).toHaveLength(1);
    expect(sendCommandMock.mock.calls.some(([method]) => method === "getDrawingInfo")).toBe(false);

    const receipt = await approvalPolicy.requestApproval(runTarget, { code: "return 2;" });
    const withToken = await handlers.get("civil3d_run_code")!({ code: "return 2;", approvalToken: receipt.approvalToken });
    expect(withToken.structuredContent.result.approvedBy).toBe("token");

    const registry = await getToolHandler("civil3d_run_code")!({ code: "return 1;" });
    expect(registry.isError).toBe(true);
    expect(registry.content[0].text).toMatch(/Approval required/);
    expect(sendCommandMock.mock.calls.filter(([method]) => method === "runCode")).toHaveLength(2);
  });

  it("keeps orchestrate routing through the protected registry handler", async () => {
    await registerTools(new McpServer({ name: "approval-route", version: "test" }));
    const result = await getToolHandler("civil3d_orchestrate")!({
      toolName: "civil3d_docs", toolAction: "orchestrate", execute: true,
      toolParameters: {
        action: "orchestrate", toolName: "civil3d_run_code", execute: true,
        toolParameters: { code: "return 1;" },
      },
    });
    expect(JSON.stringify(result)).toMatch(/Approval required/);
    expect(sendCommandMock.mock.calls.some(([method]) => method === "runCode")).toBe(false);
  });

  it("keeps preview approval_required while explaining direct calls", async () => {
    registerApprovalTool({ registerTool: () => undefined } as unknown as McpServer);
    const result = await getToolHandler("civil3d_preview_action")!({
      toolName: "civil3d_run_code", action: "run", parameters: { code: "return 1;" },
    });
    expect(result.structuredContent?.status).toBe("approval_required");
    expect(result.structuredContent?.instruction).toMatch(/direct civil3d_run_code call needs no token/);
  });
});
