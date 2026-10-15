import { readdir, readFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join, win32 } from "node:path";
import net from "node:net";
import { randomUUID } from "node:crypto";
import { windowsScript } from "./instanceLauncher.js";

export interface CivilInstance {
  instanceApi: 1;
  pid: number;
  port: number;
  tokenPath: string;
  startedAtUtc: string;
  acadVersion: string;
  owner: "user" | "agent";
  ownerSession: string | null;
  ownerLabel?: string | null;
  ownerServerPid?: number | null;
  ownerServerStartedAtUtc?: string | null;
  orphan?: boolean;
  claimId: string | null;
  label?: string | null;
  documents: string[];
  updatedAtUtc: string;
  responsive?: boolean;
}

export function pluginDataRoot(): string {
  return join(process.env.LOCALAPPDATA ?? join(homedir(), "AppData", "Local"), "Civil3DMcpPlugin");
}

export function processAlive(pid: number): boolean {
  try { process.kill(pid, 0); return true; }
  catch (error) { return (error as NodeJS.ErrnoException).code === "EPERM"; }
}

// Missing identity in old discovery is unknown, not proof that its owner died.
export function isOrphan(instance: CivilInstance, alive: (pid: number) => boolean, starts: Record<number, string>): boolean {
  if (instance.owner !== "agent" || !instance.ownerServerPid || !instance.ownerServerStartedAtUtc) return false;
  if (!alive(instance.ownerServerPid)) return true;
  const start = starts[instance.ownerServerPid];
  return Number.isFinite(Date.parse(start)) && Date.parse(start) !== Date.parse(instance.ownerServerStartedAtUtc);
}

/** Read creation times together, so PID reuse cannot keep a crashed instance alive. */
export async function processStartTimes(pids: number[]): Promise<Record<number, string>> {
  if (!pids.length) return {};
  const result = await windowsScript(`
$data = [Console]::In.ReadToEnd() | ConvertFrom-Json
$times = @{}
foreach ($instancePid in $data.pids) {
  try {
    $instanceProcess = Get-Process -Id $instancePid -ErrorAction Stop
    $times[[string]$instancePid] = $instanceProcess.StartTime.ToUniversalTime().ToString('o')
  } catch {
    # Exited or inaccessible processes cannot establish discovery identity.
  }
}
$times | ConvertTo-Json -Compress`, { pids });
  return JSON.parse(result);
}

export function sameDrawing(a: string, b: string): boolean {
  return win32.resolve(a).toLowerCase() === win32.resolve(b).toLowerCase();
}

export function parseInstance(value: unknown, filename: string): CivilInstance | undefined {
  if (!value || typeof value !== "object") return undefined;
  const v = value as CivilInstance;
  if (v.instanceApi !== 1 || !Number.isInteger(v.pid) || v.pid <= 0 || filename !== `${v.pid}.json`
    || !Number.isInteger(v.port) || v.port < 8757 || v.port > 8766
    || !["user", "agent"].includes(v.owner) || typeof v.tokenPath !== "string"
    || !win32.isAbsolute(v.tokenPath) || typeof v.acadVersion !== "string"
    || !Number.isFinite(Date.parse(v.startedAtUtc)) || !Number.isFinite(Date.parse(v.updatedAtUtc))
    || !Array.isArray(v.documents) || !v.documents.every(d => typeof d === "string" && win32.isAbsolute(d))
    || (v.ownerSession !== null && typeof v.ownerSession !== "string")
    || (v.claimId !== null && typeof v.claimId !== "string")
    || (v.label !== undefined && v.label !== null && typeof v.label !== "string")
    || (v.ownerLabel != null && typeof v.ownerLabel !== "string")
    || (v.ownerServerPid != null && (!Number.isInteger(v.ownerServerPid) || v.ownerServerPid <= 0))
    || (v.ownerServerStartedAtUtc != null && !Number.isFinite(Date.parse(v.ownerServerStartedAtUtc)))
    || (v.owner === "agent" && (!v.ownerSession || !v.claimId))) return undefined;
  // Never trust discovery to redirect a token read to an arbitrary file.
  if (!sameDrawing(v.tokenPath, join(pluginDataRoot(), `rpc-token-${v.port}`))) return undefined;
  return v;
}

/** A short, unauthenticated health probe. It never reads or sends a token. */
export async function probeInstance(instance: CivilInstance): Promise<boolean> {
  return await new Promise<boolean>(resolve => {
    const socket = new net.Socket();
    const id = randomUUID();
    let data = "";
    let settled = false;
    const finish = (responsive: boolean) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.destroy();
      resolve(responsive);
    };
    const timer = setTimeout(() => finish(false), 1200);
    socket.on("error", () => finish(false));
    socket.on("close", () => finish(false));
    socket.on("data", chunk => {
      data += chunk.toString();
      if (data.length > 1024 * 1024) return finish(false);
      try {
        const reply = JSON.parse(data);
        finish(reply.id === id && reply.result?.instanceApi === 1
          && reply.result.pid === instance.pid && reply.result.port === instance.port
          && reply.result.owner === instance.owner && reply.result.ownerSession === instance.ownerSession);
      } catch { /* More TCP data may follow. */ }
    });
    socket.connect(instance.port, "127.0.0.1", () => socket.write(JSON.stringify({
      jsonrpc: "2.0", id, method: "getCivil3DHealth", params: {},
      instanceIdentity: { pid: instance.pid, startedAtUtc: instance.startedAtUtc },
    })));
  });
}

export interface RegistrySnapshot { hasDiscoveryFiles: boolean; instances: CivilInstance[] }

export async function readInstanceRegistry(options: {
  root?: string;
  pid?: number;
  port?: number;
  alive?: (pid: number) => boolean;
  startTimes?: (pids: number[]) => Promise<Record<number, string>>;
  probe?: (instance: CivilInstance) => Promise<boolean>;
} = {}): Promise<RegistrySnapshot> {
  const folder = join(options.root ?? pluginDataRoot(), "instances");
  let files: string[];
  try { files = (await readdir(folder)).filter(f => /^\d+\.json$/i.test(f)); }
  catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return { hasDiscoveryFiles: false, instances: [] };
    throw error;
  }
  const selectedFiles = options.pid === undefined ? files : files.filter(f => f === `${options.pid}.json`);
  const candidates = (await Promise.all(selectedFiles.map(async filename => {
    let instance: CivilInstance | undefined;
    try { instance = parseInstance(JSON.parse(await readFile(join(folder, filename), "utf8")), filename); }
    catch { return undefined; }
    if (!instance || (options.port !== undefined && instance.port !== options.port)
      || !(options.alive ?? processAlive)(instance.pid)) return undefined;
    return instance;
  }))).filter((i): i is CivilInstance => i !== undefined);
  const starts = await (options.startTimes ?? processStartTimes)([...new Set(candidates.flatMap(i =>
    i.owner === "agent" && i.ownerServerPid ? [i.pid, i.ownerServerPid] : [i.pid]))]);
  const matching = candidates.filter(i => Number.isFinite(Date.parse(starts[i.pid]))
    && Date.parse(starts[i.pid]) === Date.parse(i.startedAtUtc));
  const instances = await Promise.all(matching.map(async instance => ({
    ...instance, ownerLabel: instance.ownerLabel ?? instance.label ?? null,
    orphan: isOrphan(instance, options.alive ?? processAlive, starts),
    responsive: await (options.probe ?? probeInstance)(instance),
  })));
  return { hasDiscoveryFiles: files.length > 0, instances: instances.sort((a, b) => a.pid - b.pid) };
}

export function describeInstances(instances: CivilInstance[]): string {
  return instances.length ? instances.map(i => `pid=${i.pid}, port=${i.port}, owner=${i.owner}, label=${JSON.stringify(i.ownerLabel ?? i.label ?? null)}, orphan=${i.orphan === true}, drawings=${JSON.stringify(i.documents)}`).join("; ") : "none";
}
