import { z } from "zod";
import { withApplicationConnection, type ApplicationCommandClient } from "../../utils/ConnectionManager.js";
import { Civil3DRpcError } from "../../utils/SocketClient.js";
import type { DomainToolDefinition } from "../domainRuntime.js";
import { attachedInstance, routingMode } from "../../utils/instanceSession.js";
import { instanceManager } from "../../utils/instanceManager.js";

const HOP_START_ROUTES = ["com", "typed", "auto"] as const;

// Issue #53 diagnostics, reported by plugin builds that have them. The route
// and state are plain strings here (enums only for input): health is the tool
// that diagnoses a server/plugin build mismatch, so a plugin reporting a route
// or state this server does not know must not make it fail.
const TypedInputSchema = z.object({
  state: z.string(),
  reason: z.string().nullable().optional(),
  lastChangeUtc: z.string().nullable().optional(),
  lastProbeUtc: z.string().nullable().optional(),
  lastLatencyMs: z.number().nullable().optional(),
  lastStartLatencyMs: z.number().nullable().optional(),
  lostStarts: z.number().optional(),
  lostInputs: z.number().optional(),
  lateRuns: z.number().optional(),
  inputPendingAtLastLoss: z.boolean().nullable().optional(),
}).passthrough();

const HostRouteSchema = z.object({
  hopStart: z.string(),
  typedInput: TypedInputSchema,
}).passthrough();

const TypedInputProbeSchema = z.object({
  ran: z.boolean(),
  skipped: z.string().optional(),
  latencyMs: z.number().nullable().optional(),
  timeoutMs: z.number().optional(),
  inputPendingBefore: z.boolean().nullable().optional(),
  inputPendingAfter: z.boolean().nullable().optional(),
  cmdnames: z.string().nullable().optional(),
  cmdactive: z.number().nullable().optional(),
  session: z.record(z.string(), z.unknown()).optional(),
  typedInput: TypedInputSchema.optional(),
}).passthrough();

const HealthResponseSchema = z.object({
  routing: z.enum(["fixed", "discovery"]),
  instanceApi: z.number().optional(),
  pid: z.number().optional(),
  port: z.number().optional(),
  owner: z.enum(["user", "agent"]).optional(),
  ownerSession: z.string().nullable().optional(),
  attachedInstance: z.unknown().optional(),
  connected: z.boolean(),
  civil3dVersion: z.string().optional(),
  pluginVersion: z.string().optional(),
  runCodeApi: z.number().optional(),
  plotApi: z.number().optional(),
  drawingLoaded: z.boolean(),
  operationInProgress: z.boolean(),
  currentOperation: z.string().nullable(),
  queueDepth: z.number(),
  queueCapacity: z.number(),
  currentOperationStartedAtUnixMs: z.number().nullable(),
  currentRequestId: z.string().nullable(),
  currentOperationDurationMs: z.number().nullable(),
  memoryUsageMb: z.number(),
  logFilePath: z.string(),
  fileLoggingHealthy: z.boolean(),
  fileLoggingError: z.string().nullable(),
  jobs: z.object({
    total: z.number(),
    running: z.number(),
    completed: z.number(),
    failed: z.number(),
    cancelled: z.number(),
    capacity: z.number(),
    terminalRetentionMinutes: z.number(),
  }),
  hostRoute: HostRouteSchema.optional(),
  typedInputProbe: TypedInputProbeSchema.optional(),
});

const PROBE_TIMEOUT_MS = { min: 250, max: 10_000 } as const;

export const HealthArgsSchema = z.object({
  action: z.literal("health"),
  probeTypedInput: z.boolean().optional(),
  probeTimeoutMs: z.number().int().min(PROBE_TIMEOUT_MS.min).max(PROBE_TIMEOUT_MS.max).optional(),
  hopStart: z.enum(HOP_START_ROUTES).optional(),
});

const healthInputShape = {
  probeTypedInput: z.boolean().optional().describe(
    "Issue #53 diagnostic: type the plugin's C3DMCPPROBE command with SendStringToExecute and wait for it to run (3 s, or probeTimeoutMs), before reporting. The result (ran, latency, input pending, active commands, Windows session state) is returned as typedInputProbe and updates hostRoute.typedInput. Skipped while a command or prompt is active. The probe changes no drawing data; a probe string that did not run may still run later and answer a prompt open by then, so probe at an idle Command: prompt, preferably on a scratch instance."),
  probeTimeoutMs: z.number().int().min(PROBE_TIMEOUT_MS.min).max(PROBE_TIMEOUT_MS.max).optional().describe(
    "Issue #53 diagnostic, with probeTypedInput: how long the probe waits for C3DMCPPROBE to run, 250-10000 ms (default 3000)."),
  hopStart: z.enum(HOP_START_ROUTES).optional().describe(
    "Issue #53 diagnostic: how this Civil 3D instance starts the plugin's C3DMCPHOP command, and so every later write and command, from now on (it changes the instance, not the drawing). com (the default) is COM SendCommand; typed is SendStringToExecute, failing with HOST_BUSY when the start is lost; auto is typed until a start is lost, then COM. A typed start that arrives late can answer a prompt open by then, so typed and auto are meant for scratch instances. Set on the attached instance before the report; not persisted (a restart is com again)."),
};

// A plugin build without the issue #53 diagnostics answers METHOD_NOT_FOUND.
async function sendDiagnostic(appClient: ApplicationCommandClient, method: string, params: Record<string, unknown>) {
  try {
    return await appClient.sendCommand(method, params);
  } catch (error) {
    if ((error as { code?: string }).code !== "CIVIL3D.METHOD_NOT_FOUND") throw error;
    throw new Civil3DRpcError(
      `This Civil3D-mcp plugin build has no '${method}' (the issue #53 diagnostics behind civil3d_health probeTypedInput and hopStart). Install a plugin from the same build as this server.`,
      "CIVIL3D.METHOD_NOT_FOUND", -32601);
  }
}

export const PLUGIN_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "plugin",
  actions: {
    health: {
      action: "health",
      inputSchema: HealthArgsSchema,
      responseSchema: z.union([HealthResponseSchema, z.object({ connected: z.literal(false),
        status: z.literal("not attached"), routing: z.enum(["fixed", "discovery"]), sessionId: z.string(), instances: z.array(z.unknown()), instruction: z.string() })]),
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: false,
      // Setting the same route twice, or probing again, leaves the same state.
      safeForRetry: true,
      pluginMethods: ["getCivil3DHealth", "setHostRoute", "probeTypedInput"],
      execute: async (rawArgs) => {
        const args = rawArgs as Partial<z.infer<typeof HealthArgsSchema>>;
        try {
          return await withApplicationConnection(async appClient => {
            // Route first, so the probe and the report show the new route.
            if (args.hopStart) await sendDiagnostic(appClient, "setHostRoute", { hopStart: args.hopStart });
            const probe = args.probeTypedInput === true
              ? await sendDiagnostic(appClient, "probeTypedInput",
                args.probeTimeoutMs === undefined ? {} : { timeoutMs: args.probeTimeoutMs })
              : undefined;
            return {
              ...await appClient.sendCommand("getCivil3DHealth", {}), routing: routingMode, attachedInstance: attachedInstance() ?? null,
              ...(probe === undefined ? {} : { typedInputProbe: probe }),
            };
          });
        } catch (error) {
          if ((error as { code?: string }).code !== "CIVIL3D.NOT_ATTACHED") throw error;
          const listing = await instanceManager.list();
          return { connected: false, status: "not attached", routing: routingMode, sessionId: listing.sessionId,
            instances: listing.instances, instruction: (error as Error).message };
        }
      },
    },
  },
  exposures: [
    {
      toolName: "civil3d_health",
      displayName: "Civil 3D Health",
      description: "Reports the status of the Civil 3D connection and plugin, including hostRoute: how the plugin starts its C3DMCPHOP command and whether typed input works in this session. Optional issue #53 diagnostics: probeTypedInput runs a typed-input probe first; hopStart changes how the attached instance starts every later write and command (com, the default; typed; auto), not the drawing, and typed or auto is meant for scratch instances.",
      inputShape: healthInputShape,
      supportedActions: ["health"],
      resolveAction: (rawArgs) => ({ action: "health", args: { ...rawArgs, action: "health" } }),
    },
  ],
};
