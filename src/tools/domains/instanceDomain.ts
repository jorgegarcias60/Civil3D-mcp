import { z } from "zod";
import type { DomainToolDefinition } from "../domainRuntime.js";
import { instanceManager, stopTargetIdentity } from "../../utils/instanceManager.js";

const fields = {
  pid: z.number().int().positive().optional(), drawing: z.string().min(1).optional(), job: z.string().trim().min(1).optional(),
  userInstance: z.boolean().optional(), force: z.boolean().optional(), adoptOrphan: z.boolean().optional(),
  year: z.number().int().min(2024).max(2027).optional(), units: z.enum(["Imperial", "Metric"]).optional(),
  discardChanges: z.boolean().optional(),
};

export const INSTANCE_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "instance",
  actions: {
    list: {
      action: "list", inputSchema: z.object({ action: z.literal("list") }),
      capabilities: ["query", "inspect"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async () => instanceManager.list(),
    },
    attach: {
      action: "attach", inputSchema: z.object({ action: z.literal("attach"), pid: fields.pid,
        drawing: fields.drawing, job: fields.job, userInstance: fields.userInstance, force: fields.force, adoptOrphan: fields.adoptOrphan }),
      capabilities: ["query"], requiresActiveDrawing: false, safeForRetry: false,
      execute: async args => instanceManager.attach(args),
    },
    detach: {
      action: "detach", inputSchema: z.object({ action: z.literal("detach") }),
      capabilities: ["query"], requiresActiveDrawing: false, safeForRetry: false,
      execute: async () => instanceManager.detach(),
    },
    start: {
      action: "start", inputSchema: z.object({ action: z.literal("start"), drawing: z.string().min(1),
        job: fields.job, year: fields.year, units: fields.units }),
      capabilities: ["create", "manage"], requiresActiveDrawing: false, safeForRetry: false,
      progressHeartbeat: true,
      execute: async args => instanceManager.start(args as { drawing: string; job?: string; year?: number; units?: string }),
    },
    stop: {
      action: "stop", inputSchema: z.object({ action: z.literal("stop"), pid: fields.pid, discardChanges: fields.discardChanges }),
      capabilities: ["delete", "manage"], requiresActiveDrawing: false, safeForRetry: false,
      progressHeartbeat: true, pluginMethods: ["quitInstance"],
      prepare: async (args, rawArgs) => {
        const instance = await instanceManager.resolveStopTarget(args);
        const target = { pid: instance.pid, targetIdentity: stopTargetIdentity(instance) };
        return { args: { ...args, ...target }, approvalParameters: { ...rawArgs, ...target } };
      },
      execute: async args => instanceManager.stop(args),
    },
  },
  exposures: [{
    toolName: "civil3d_instance", displayName: "Civil 3D Instances",
    description: "List, start, attach, detach or stop Civil 3D instances for this MCP session. Set job to label a session; a same-label orphan is adopted automatically, while another label needs adoptOrphan: true. Attach to a user-owned instance only when Jorge says so, with userInstance: true. Start and stop require approval. Defaults: newest installed 2024–2027 Imperial shortcut, 300-second start wait, three agent instances per machine.",
    inputShape: { action: z.enum(["list", "start", "attach", "detach", "stop"]), ...fields,
      approvalToken: z.string().optional() },
    supportedActions: ["list", "start", "attach", "detach", "stop"],
    resolveAction: args => ({ action: String(args.action), args }),
  }],
};
