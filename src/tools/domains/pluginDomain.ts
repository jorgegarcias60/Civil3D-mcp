import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";
import { attachedInstance, routingMode } from "../../utils/instanceSession.js";
import { instanceManager } from "../../utils/instanceManager.js";

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
});

export const PLUGIN_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "plugin",
  actions: {
    health: {
      action: "health",
      inputSchema: z.object({ action: z.literal("health") }),
      responseSchema: z.union([HealthResponseSchema, z.object({ connected: z.literal(false),
        status: z.literal("not attached"), routing: z.enum(["fixed", "discovery"]), sessionId: z.string(), instances: z.array(z.unknown()), instruction: z.string() })]),
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: false,
      safeForRetry: true,
      pluginMethods: ["getCivil3DHealth"],
      execute: async () => {
        try {
          return await withApplicationConnection(async appClient => ({
            ...await appClient.sendCommand("getCivil3DHealth", {}), routing: routingMode, attachedInstance: attachedInstance() ?? null,
          }));
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
      description: "Reports the status of the Civil 3D connection and plugin.",
      inputShape: {},
      supportedActions: ["health"],
      resolveAction: () => ({ action: "health", args: { action: "health" } }),
    },
  ],
};
