import { randomUUID } from "node:crypto";
import { AsyncLocalStorage } from "node:async_hooks";
import { join, win32 } from "node:path";
import { readInstanceRegistry, pluginDataRoot, describeInstances, processStartTimes, type CivilInstance, type RegistrySnapshot } from "./instanceRegistry.js";
import { createLogger } from "./logger.js";

const log = createLogger("InstanceSession");

export interface InstanceEndpoint { port: number; tokenPath: string; pid?: number; startedAtUtc?: string; owner?: "user" | "agent"; legacyFallback?: boolean }
export const sessionId = randomUUID();
export let sessionLabel: string | null = process.env.CIVIL3D_SESSION_LABEL?.trim() || null;
let explicitJob: string | undefined;
let identity: Promise<{ serverPid: number; serverStartedAtUtc: string }> | undefined;

export function deriveSessionLabel(job?: string, drawing?: string, environmentLabel = process.env.CIVIL3D_SESSION_LABEL): string | null {
  if (job?.trim()) return job.trim();
  if (environmentLabel?.trim()) return environmentLabel.trim();
  if (!drawing) return null;
  const parts = win32.normalize(drawing).split(/[\\/]/);
  const jobs = parts.findIndex(part => part.toLowerCase() === "jobs");
  return jobs >= 0 && jobs < parts.length - 2 ? parts[jobs + 1] : win32.basename(drawing);
}

export function configureSessionLabel(job?: string, drawing?: string): string | null {
  if (job?.trim()) explicitJob = job.trim();
  return sessionLabel = deriveSessionLabel(explicitJob, drawing) ?? sessionLabel;
}

/** Tests only: forget the job label, so a test never inherits the label an earlier test set (issue #34). */
export function resetSessionLabelForTests(): void {
  explicitJob = undefined;
  sessionLabel = process.env.CIVIL3D_SESSION_LABEL?.trim() || null;
}

export function serverIdentity(): Promise<{ serverPid: number; serverStartedAtUtc: string }> {
  return identity ??= processStartTimes([process.pid]).then(starts => {
    if (!Number.isFinite(Date.parse(starts[process.pid]))) throw new Error("Cannot establish MCP server process creation time.");
    return { serverPid: process.pid, serverStartedAtUtc: starts[process.pid] };
  }).catch(error => { identity = undefined; throw error; });
}

export async function adoptInstanceOwnership(instance: CivilInstance, label: string): Promise<CivilInstance> {
  if (instance.owner !== "agent" || instance.orphan !== true) throw new Error("Only a verified orphan agent instance can be adopted.");
  const { withApplicationConnection, effectivePluginHost } = await import("./ConnectionManager.js");
  const { readPluginToken } = await import("./pluginAuth.js");
  if (effectivePluginHost !== "127.0.0.1") throw new Error("Instance adoption requires the local plugin.");
  const server = await serverIdentity();
  await withPinnedEndpoint(instance, () => withApplicationConnection(client => client.sendCommand("adoptInstance", {
    authToken: readPluginToken(), ownerSession: sessionId, label, ...server,
    expectedOwnerSession: instance.ownerSession, expectedOwnerServerPid: instance.ownerServerPid,
    expectedOwnerServerStartedAtUtc: instance.ownerServerStartedAtUtc,
  })));
  return { ...instance, ownerSession: sessionId, ownerLabel: label, label,
    ownerServerPid: server.serverPid, ownerServerStartedAtUtc: server.serverStartedAtUtc, orphan: false };
}
const explicitPort = process.env.CIVIL3D_PORT?.trim() || undefined;
export const routingMode = explicitPort === undefined ? "discovery" : "fixed";
const pinnedEndpoint = new AsyncLocalStorage<InstanceEndpoint>();
const requestEndpoint = new AsyncLocalStorage<{ attachment?: CivilInstance; endpoint?: Promise<InstanceEndpoint> }>();
let attached: CivilInstance | undefined;

export function attachedInstance(): CivilInstance | undefined { return attached; }
export function setAttachedInstance(instance?: CivilInstance): void { attached = instance; }
export function activeEndpoint(): InstanceEndpoint | undefined { return pinnedEndpoint.getStore() ?? attached; }
export function withPinnedEndpoint<T>(endpoint: InstanceEndpoint, action: () => T): T { return pinnedEndpoint.run(endpoint, action); }
// Capture before any await, but resolve lazily so local and lifecycle tools need no attachment.
export function withInstanceRequest<T>(action: () => T): T {
  return requestEndpoint.getStore() ? action() : requestEndpoint.run({ attachment: attached }, action);
}

export function selectionError(instances: CivilInstance[]): Error {
  return Object.assign(new Error(`Civil 3D session is not attached. Instances: ${describeInstances(instances)}. `
    + "Call civil3d_instance attach with pid or drawing. A user-owned instance needs userInstance: true, only when Jorge says so."),
  { code: "CIVIL3D.NOT_ATTACHED" });
}

export function selectDefault(snapshot: RegistrySnapshot, ownSession = sessionId, label = sessionLabel): CivilInstance | undefined {
  if (snapshot.instances.length === 0) return undefined;
  const eligible = snapshot.instances.filter(i => i.owner === "agent" && i.responsive
    && (i.ownerSession === ownSession || (i.orphan === true && !!label && (i.ownerLabel ?? i.label) === label)));
  if (snapshot.instances.length === 1 && eligible.length === 1) return eligible[0];
  throw selectionError(snapshot.instances);
}

export async function resolveInstanceEndpoint(): Promise<InstanceEndpoint> {
  const pinned = pinnedEndpoint.getStore();
  if (pinned) return pinned;
  const request = requestEndpoint.getStore();
  if (request) return request.endpoint ??= resolveUnpinnedEndpoint(request.attachment);
  return resolveUnpinnedEndpoint(attached);
}

async function resolveUnpinnedEndpoint(attachment: CivilInstance | undefined): Promise<InstanceEndpoint> {
  if (explicitPort !== undefined) {
    const port = Number(explicitPort);
    if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error("Invalid CIVIL3D_PORT.");
    const { instances } = await readInstanceRegistry({ port });
    const instance = instances.find(i => i.port === port);
    if (instance) {
      if ((instance.owner === "user" || instance.ownerSession !== sessionId)
        && process.env.CIVIL3D_ALLOW_FIXED_INSTANCE !== "true") {
        throw Object.assign(new Error("Fixed routing refused a user-owned or another session's instance. Unset CIVIL3D_PORT and attach explicitly, or set CIVIL3D_ALLOW_FIXED_INSTANCE=true only with authorization."),
          { code: "CIVIL3D.NOT_ATTACHED" });
      }
      if (!instance.responsive) throw new Error(`Fixed Civil 3D instance ${instance.pid} is no longer responsive.`);
      return instance;
    }
    return { port, tokenPath: process.env.CIVIL3D_RPC_TOKEN_FILE ?? join(pluginDataRoot(), port === 8757 ? "rpc-token" : `rpc-token-${port}`),
      legacyFallback: process.env.CIVIL3D_ALLOW_FIXED_INSTANCE !== "true" };
  }
  const snapshot = await readInstanceRegistry(attachment ? { pid: attachment.pid } : {});
  if (attachment) {
    const live = snapshot.instances.find(i => i.pid === attachment.pid && i.startedAtUtc === attachment.startedAtUtc
      && i.port === attachment.port && i.owner === attachment.owner && i.ownerSession === attachment.ownerSession);
    if (!live) throw new Error(`Attached Civil 3D instance ${attachment.pid} is no longer available: it was closed or restarted, or its owner changed. Call civil3d_instance list and attach again.`);
    // The registry has matched the process and its start time, and the request
    // carries that identity, which the plugin checks. A missed 1.2 s health probe
    // then means busy (a publish keeps Civil 3D busy for seconds), not gone: send
    // the request and let its own timeout decide (issue #58).
    if (!live.responsive) log.warn("Attached instance missed the health probe; sending the request anyway (busy)", { pid: live.pid, port: live.port });
    if (attached === attachment) attached = live;
    return live;
  }
  const selected = selectDefault(snapshot);
  if (selected) {
    const live = selected.orphan ? await adoptInstanceOwnership(selected, sessionLabel!) : selected;
    if (attached === attachment) attached = live;
    return live;
  }
  return { port: 8757, tokenPath: process.env.CIVIL3D_RPC_TOKEN_FILE ?? join(pluginDataRoot(), "rpc-token"), legacyFallback: true };
}
