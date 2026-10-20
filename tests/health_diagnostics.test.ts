import { beforeEach, describe, expect, it, vi } from "vitest";
import { z } from "zod";

// civil3d_health probeTypedInput / hopStart: the issue #53 diagnostics.
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

import { HealthArgsSchema, PLUGIN_DOMAIN_DEFINITION } from "../src/tools/domains/pluginDomain.js";
import { Civil3DRpcError } from "../src/utils/SocketClient.js";
import { isApprovalRequired } from "../src/tools/approvalPolicy.js";

const typedInput = {
  state: "alive", reason: "probe", lastChangeUtc: "2026-10-19T12:00:00.0000000Z",
  lastProbeUtc: "2026-10-19T12:00:00.0000000Z", lastLatencyMs: 18.5, lastStartLatencyMs: null,
  lostStarts: 0, lateRuns: 0, inputPendingAtLastLoss: null,
};

const pluginHealth = (hopStart = "com") => ({
  connected: true, pid: 4242, civil3dVersion: "25.1s", pluginVersion: "1.2.1.0", drawingLoaded: true,
  operationInProgress: false, currentOperation: null, queueDepth: 0, queueCapacity: 64,
  currentOperationStartedAtUnixMs: null, currentRequestId: null, currentOperationDurationMs: null,
  memoryUsageMb: 512, logFilePath: "C:\\logs\\plugin.log", fileLoggingHealthy: true, fileLoggingError: null,
  jobs: { total: 0, running: 0, completed: 0, failed: 0, cancelled: 0, capacity: 256, terminalRetentionMinutes: 1440 },
  hostRoute: { hopStart, typedInput },
});

const probeResult = {
  ran: true, latencyMs: 18.5, timeoutMs: 3000, inputPendingBefore: false, inputPendingAfter: false, cmdnames: "",
  session: { remoteSession: false, connectState: "active", inputDesktop: true, foreground: false, minimized: false },
  typedInput,
};

const health = PLUGIN_DOMAIN_DEFINITION.actions.health;
const exposure = PLUGIN_DOMAIN_DEFINITION.exposures.find((entry) => entry.toolName === "civil3d_health")!;

describe("civil3d_health issue #53 diagnostics", () => {
  beforeEach(() => {
    sendCommandMock.mockReset();
  });

  it("accepts probeTypedInput, probeTimeoutMs and hopStart and nothing else for them", () => {
    expect(HealthArgsSchema.parse({ action: "health" })).toEqual({ action: "health" });
    expect(HealthArgsSchema.parse({ action: "health", probeTypedInput: true, probeTimeoutMs: 8000, hopStart: "auto" }))
      .toEqual({ action: "health", probeTypedInput: true, probeTimeoutMs: 8000, hopStart: "auto" });
    for (const hopStart of ["com", "typed", "auto"]) {
      expect(HealthArgsSchema.safeParse({ action: "health", hopStart }).success).toBe(true);
    }
    expect(HealthArgsSchema.safeParse({ action: "health", hopStart: "postcommand" }).success).toBe(false);
    expect(HealthArgsSchema.safeParse({ action: "health", hopStart: "COM" }).success).toBe(false);
    expect(HealthArgsSchema.safeParse({ action: "health", probeTypedInput: "yes" }).success).toBe(false);
    for (const probeTimeoutMs of [250, 3000, 10_000]) {
      expect(HealthArgsSchema.safeParse({ action: "health", probeTimeoutMs }).success).toBe(true);
    }
    for (const probeTimeoutMs of [249, 10_001, 1500.5, "3000"]) {
      expect(HealthArgsSchema.safeParse({ action: "health", probeTimeoutMs }).success, String(probeTimeoutMs)).toBe(false);
    }

    const shape = z.object(exposure.inputShape);
    expect(Object.keys(exposure.inputShape).sort()).toEqual(["hopStart", "probeTimeoutMs", "probeTypedInput"]);
    expect(shape.safeParse({}).success).toBe(true);
    expect(shape.safeParse({ hopStart: "typed", probeTypedInput: false, probeTimeoutMs: 250 }).success).toBe(true);
    expect(shape.safeParse({ hopStart: "bogus" }).success).toBe(false);
    expect(shape.safeParse({ probeTimeoutMs: 20_000 }).success).toBe(false);
    expect(exposure.resolveAction({ hopStart: "typed", probeTypedInput: true }))
      .toEqual({ action: "health", args: { hopStart: "typed", probeTypedInput: true, action: "health" } });
    expect(exposure.description).toContain("issue #53");
  });

  // hopStart changes how the instance starts every later write: the tool says so,
  // and that it is for scratch instances, while staying an approval-free,
  // retry-safe status call.
  it("is retry-safe and approval-free, and says hopStart changes later writes on the instance", () => {
    expect(health.safeForRetry).toBe(true);
    expect(health.capabilities).toEqual(["query", "inspect"]);
    expect(health.pluginMethods).toEqual(["getCivil3DHealth", "setHostRoute", "probeTypedInput"]);
    expect(isApprovalRequired({ toolName: "civil3d_health", action: "health", capabilities: health.capabilities,
      safeForRetry: health.safeForRetry })).toBe(false);
    expect(exposure.description).toMatch(/every later write/);
    expect(exposure.description).toMatch(/not the drawing/);
    expect(exposure.description).toMatch(/scratch instances/);
    const hopStart = exposure.inputShape.hopStart.description ?? "";
    expect(hopStart).toMatch(/every later write/);
    expect(hopStart).toMatch(/not the drawing/);
    expect(hopStart).toMatch(/scratch instances/);
    // The probe is not called harmless: a late probe string can answer a prompt.
    const probe = exposure.inputShape.probeTypedInput.description ?? "";
    expect(`${exposure.description} ${probe} ${hopStart}`).not.toMatch(/harmless/i);
    expect(probe).toMatch(/may still run later and answer a prompt/);
  });

  it("without the new parameters sends only getCivil3DHealth, and reports hostRoute", async () => {
    sendCommandMock.mockResolvedValue(pluginHealth());
    const result = await health.execute({ action: "health" }) as Record<string, unknown>;
    expect(sendCommandMock.mock.calls).toEqual([["getCivil3DHealth", {}]]);
    expect(result).not.toHaveProperty("typedInputProbe");
    const parsed = health.responseSchema!.parse(result) as Record<string, any>;
    expect(parsed.hostRoute).toEqual({ hopStart: "com", typedInput });
    // An older plugin reports no hostRoute; the report still validates.
    const { hostRoute: _omitted, ...older } = pluginHealth();
    expect(health.responseSchema!.safeParse({ ...older, routing: "fixed" }).success).toBe(true);
  });

  it("sets the route first, then probes, then reports, on one attached instance", async () => {
    sendCommandMock.mockImplementation(async (method: string, params: Record<string, unknown>) => {
      if (method === "setHostRoute") return { hopStart: params.hopStart, previous: "com", typedInput };
      if (method === "probeTypedInput") return probeResult;
      return pluginHealth("typed");
    });
    const result = await health.execute({ action: "health", hopStart: "typed", probeTypedInput: true });
    expect(sendCommandMock.mock.calls).toEqual([
      ["setHostRoute", { hopStart: "typed" }],
      ["probeTypedInput", {}],
      ["getCivil3DHealth", {}],
    ]);
    // The response schema keeps both diagnostics (it strips unknown keys).
    const parsed = health.responseSchema!.parse(result) as Record<string, any>;
    expect(parsed.hostRoute.hopStart).toBe("typed");
    expect(parsed.typedInputProbe).toEqual(probeResult);
    expect(parsed.routing).toBeDefined();
  });

  it("forwards probeTimeoutMs as the probe's timeoutMs, and only with probeTypedInput", async () => {
    sendCommandMock.mockImplementation(async (method: string) =>
      method === "probeTypedInput" ? { ...probeResult, timeoutMs: 8000 } : pluginHealth());
    await health.execute({ action: "health", probeTypedInput: true, probeTimeoutMs: 8000 });
    expect(sendCommandMock.mock.calls).toEqual([["probeTypedInput", { timeoutMs: 8000 }], ["getCivil3DHealth", {}]]);

    sendCommandMock.mockClear();
    await health.execute({ action: "health", probeTimeoutMs: 8000 });
    expect(sendCommandMock.mock.calls).toEqual([["getCivil3DHealth", {}]]);
  });

  // A newer plugin may report a route or state this server does not know; the health
  // report must still validate (it is the tool for a server/plugin build mismatch).
  it("accepts a hostRoute with a route or state this server does not know", () => {
    const newer = {
      ...pluginHealth(), routing: "fixed",
      hostRoute: { hopStart: "postcommand", typedInput: { ...typedInput, state: "stalled", lostInputs: 1 } },
      typedInputProbe: { ...probeResult, ran: false, skipped: "busy", cmdactive: 1, cmdnames: "LINE",
        typedInput: { ...typedInput, state: "stalled" } },
    };
    const parsed = health.responseSchema!.safeParse(newer);
    expect(parsed.success).toBe(true);
    const result = (parsed as { data: Record<string, any> }).data;
    expect(result.hostRoute).toMatchObject({ hopStart: "postcommand", typedInput: { state: "stalled", lostInputs: 1 } });
    expect(result.typedInputProbe).toMatchObject({ skipped: "busy", cmdactive: 1, cmdnames: "LINE" });
  });

  it("probes without changing the route when only probeTypedInput is set", async () => {
    sendCommandMock.mockImplementation(async (method: string) =>
      method === "probeTypedInput" ? { ...probeResult, ran: false, skipped: "busy", latencyMs: null } : pluginHealth());
    const result = await health.execute({ action: "health", probeTypedInput: true }) as Record<string, any>;
    expect(sendCommandMock.mock.calls.map(([method]) => method)).toEqual(["probeTypedInput", "getCivil3DHealth"]);
    expect(result.typedInputProbe).toMatchObject({ ran: false, skipped: "busy" });
    expect(health.responseSchema!.safeParse(result).success).toBe(true);
  });

  it("names the missing diagnostics when the plugin build is older", async () => {
    sendCommandMock.mockImplementation(async (method: string) => {
      if (method === "setHostRoute") {
        throw new Civil3DRpcError("Plugin method 'setHostRoute' is not implemented yet.", "CIVIL3D.METHOD_NOT_FOUND", -32601);
      }
      return pluginHealth();
    });
    const failure = await health.execute({ action: "health", hopStart: "auto" }).catch((error: unknown) => error);
    expect(failure).toBeInstanceOf(Civil3DRpcError);
    expect((failure as Civil3DRpcError).code).toBe("CIVIL3D.METHOD_NOT_FOUND");
    expect((failure as Error).message).toContain("issue #53");
    expect(sendCommandMock.mock.calls.map(([method]) => method)).toEqual(["setHostRoute"]);
  });
});
