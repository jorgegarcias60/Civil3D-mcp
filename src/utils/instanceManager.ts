import { mkdir, readdir, readFile, rename, rm, stat, writeFile, mkdtemp } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { isAbsolute, join, win32 } from "node:path";
import { tmpdir } from "node:os";
import { setTimeout as delay } from "node:timers/promises";
import { readInstanceRegistry, processAlive, pluginDataRoot, describeInstances, sameDrawing, type CivilInstance } from "./instanceRegistry.js";
import { attachedInstance, setAttachedInstance, sessionId, sessionLabel, configureSessionLabel, serverIdentity, adoptInstanceOwnership, withPinnedEndpoint } from "./instanceSession.js";
import { acadPids, findCivilShortcut, launchCivilShortcut, protectInstanceFolders, acquireStartLock, releaseStartLock, launchedProcessAlive, stopLaunchedProcess } from "./instanceLauncher.js";
import { scanInstanceDialogs, titleIs, XREF_PROMPT, SHX_PROMPT, RECOVERY_PROMPT, type DialogScanResult, type InstanceDialog } from "./instanceDialogs.js";
import { effectivePluginHost, withApplicationConnection } from "./ConnectionManager.js";
import { readPluginToken } from "./pluginAuth.js";
import { currentAbortSignal } from "./requestContext.js";

export const defaultInstanceDependencies = {
  registry: readInstanceRegistry, alive: processAlive, protect: protectInstanceFolders,
  shortcut: findCivilShortcut, launch: launchCivilShortcut, acadPids,
  serverIdentity, adopt: adoptInstanceOwnership,
  acquireLock: acquireStartLock, releaseLock: releaseStartLock,
  identityAlive: launchedProcessAlive, killPending: stopLaunchedProcess, scanDialogs: scanInstanceDialogs,
  now: () => Date.now(), wait: async (ms: number) => { await delay(ms, undefined, { signal: currentAbortSignal() }); },
  quit: async (instance: CivilInstance, discardChanges: boolean) => await withPinnedEndpoint(instance, () =>
    withApplicationConnection(client => client.sendCommand("quitInstance", {
      authToken: readPluginToken(), ownerSession: sessionId, claimId: instance.claimId, discardChanges,
    }))),
};

export const LAUNCH_GRACE_MS = 15 * 60_000;
const SCAN_FAILED = "Dialog scan failed";
// After an answer the instance can need a moment to respond, and a second prompt may follow the first.
const RECOVERY_ROUNDS = 8;
const RECOVERY_WAIT_MS = 2000;
const XREF_WARNING = "The drawing has missing external references (Civil 3D's prompt was answered with Ignore). Review missingXrefs and the dialog text before using its contents.";
const RECOVERY_WARNING = "Civil 3D's Drawing Recovery notice (the previous session ended unexpectedly) was closed; its backups stay in the Drawing Recovery Manager.";
const SHX_WARNING = "Civil 3D's Missing SHX Files prompt was answered with Ignore; text that uses those fonts shows a substitute font.";

// Every dialog seen during the start stays in the result: one answered with Ignore is gone by the next scan
// (seen live 2026-10-15). A later sighting of the same title and class replaces the earlier entry.
export function mergeDialogs(seen: InstanceDialog[], latest: InstanceDialog[]): InstanceDialog[] {
  const merged = new Map(seen.map(d => [`${d.title}\u0000${d.className}`, d]));
  for (const dialog of latest) merged.set(`${dialog.title}\u0000${dialog.className}`, dialog);
  return [...merged.values()];
}
interface PendingLaunch {
  instance: CivilInstance; expiresAt: number; claimPath: string;
  diagnostics: DialogScanResult;
}

export class InstanceManager {
  private readonly pending = new Map<number, PendingLaunch>();
  private lastLaunch?: CivilInstance;
  constructor(private readonly deps = defaultInstanceDependencies, private readonly root = pluginDataRoot()) {}

  async list() {
    const snapshot = await this.deps.registry({ root: this.root });
    await this.reconcilePending(snapshot.instances);
    return { sessionId, label: sessionLabel, instances: [...snapshot.instances, ...[...this.pending.values()].map(p => ({
      ...p.instance, ...this.diagnostics(p.diagnostics),
    }))].map(i => ({
      ...i, label: i.label ?? null,
      attachedToThisSession: attachedInstance()?.pid === i.pid && attachedInstance()?.startedAtUtc === i.startedAtUtc,
    })) };
  }

  async attach(args: { pid?: number; drawing?: string; job?: string; userInstance?: boolean; force?: boolean; adoptOrphan?: boolean }) {
    this.requireAutomaticMode();
    if (args.pid === undefined && args.drawing === undefined) {
      args = { ...args, pid: attachedInstance()?.pid ?? this.lastLaunch?.pid };
    }
    if ((args.pid === undefined) === (args.drawing === undefined)) throw new Error("Specify exactly one of pid or drawing.");
    if (args.drawing && !isAbsolute(args.drawing)) throw new Error("drawing must be an absolute path.");
    const { instances } = await this.deps.registry({ root: this.root });
    await this.reconcilePending(instances);
    const matches = instances.filter(i => args.pid !== undefined ? i.pid === args.pid : i.documents.some(d => sameDrawing(d, args.drawing!)));
    if (matches.length !== 1) {
      const pending = [...this.pending.values()].find(p => args.pid === p.instance.pid
        || (args.drawing && p.instance.documents.some(d => sameDrawing(d, args.drawing!))));
      if (pending) return { status: "starting" as const, pid: pending.instance.pid,
        message: "Civil 3D started; the plugin has not loaded yet", ...this.diagnostics(pending.diagnostics) };
      throw new Error(`Expected one live instance. Instances: ${describeInstances(instances)}`);
    }
    let instance = matches[0];
    let recovered: DialogScanResult | undefined;
    if (!instance.responsive) ({ instance, diagnostics: recovered } = await this.recoverUnresponsive(instance));
    const extra = recovered ? this.diagnostics(recovered) : {};
    if (instance.owner === "user" && args.userInstance !== true) throw new Error("User-owned instance requires userInstance: true, only when Jorge says so.");
    configureSessionLabel(args.job, args.drawing ?? instance.documents[0]);
    if (instance.owner === "agent" && instance.orphan === true) {
      const sameLabel = !!sessionLabel && (instance.ownerLabel ?? instance.label) === sessionLabel;
      if (!sameLabel && args.adoptOrphan !== true) {
        throw new Error("Orphaned agent instance has a different or unknown label. Pass adoptOrphan: true to take ownership; force does not adopt it.");
      }
      const adopted = await this.deps.adopt(instance, sessionLabel ?? `session-${sessionId}`);
      setAttachedInstance(adopted);
      return { instance: adopted, adopted: true, ...extra };
    }
    const other = instance.owner === "agent" && instance.ownerSession !== sessionId;
    if (other && args.force !== true) throw new Error("Instance belongs to another agent session. Refused; force: true is required to attach.");
    setAttachedInstance(instance);
    return { instance, ...extra, ...(other ? { warning: [(extra as { warning?: string }).warning, "Forced attachment to another agent session's instance. Ownership is unchanged; this session cannot stop it."].filter(Boolean).join(" ") } : {}) };
  }

  detach() { setAttachedInstance(); return { attached: false, sessionId }; }

  private requireAutomaticMode() {
    this.requireLocalHost();
    if (process.env.CIVIL3D_PORT?.trim()) throw new Error("Unset CIVIL3D_PORT and restart this MCP server to use instance attachment or start. Explicit CIVIL3D_PORT uses fixed routing.");
  }

  private requireLocalHost() {
    if (effectivePluginHost !== "127.0.0.1") throw new Error("Instance lifecycle requires CIVIL3D_HOST to dial exactly 127.0.0.1. Nothing ran.");
  }

  async start(args: { drawing: string; job?: string; year?: number; units?: string }, timeoutMs = 300_000) {
    this.requireAutomaticMode();
    // win32.isAbsolute also accepts current-drive-rooted paths, unlike .NET's
    // IsPathFullyQualified. Require a drive root or a complete UNC share.
    if (!/^(?:[a-z]:[\\/]|[\\/]{2}[^\\/]+[\\/][^\\/]+[\\/])/i.test(args.drawing)
      || !/\.dwg$/i.test(args.drawing) || /["\r\n\0]/.test(args.drawing)) {
      throw new Error("drawing must be an existing absolute, fully qualified drive or UNC .dwg path.");
    }
    const drawing = win32.normalize(args.drawing);
    if (!(await stat(drawing)).isFile()) throw new Error("drawing must be an existing absolute .dwg path.");
    configureSessionLabel(args.job, drawing);
    const cap = Number(process.env.CIVIL3D_MAX_AGENT_INSTANCES ?? "3");
    if (!Number.isInteger(cap) || cap < 1) throw new Error("CIVIL3D_MAX_AGENT_INSTANCES must be a positive integer.");
    await this.deps.protect(this.root);
    // Serialize starts across MCP server processes, including the discovery wait.
    const lockPath = join(this.root, "instance-start.lock");
    await this.deps.acquireLock(lockPath);
    let claimPath: string | undefined;
    let takenClaimPath: string | undefined;
    let temp: string | undefined;
    let dispatched = false;
    let attached = false;
    let launchPid: number | undefined;
    let claimExpiresAt: number | undefined;
    try {
      const { instances } = await this.deps.registry({ root: this.root });
      const already = instances.find(i => i.documents.some(d => sameDrawing(d, drawing)));
      if (already) {
        if (already.owner === "agent" && already.ownerSession === sessionId && already.responsive) {
          setAttachedInstance(already); return { instance: already, alreadyOpen: true };
        }
        throw new Error(`Drawing is already open; a DWG cannot be open twice. Instances: ${describeInstances([already])}`);
      }
      const agents = instances.filter(i => i.owner === "agent");
      // Cancelled or uncertain starts release the lock but keep their claims.
      // Reserve their drawing and capacity until discovery represents them or
      // their claims expire, including claims already taken before publication.
      const pendingLaunches = await this.pendingLaunches(instances);
      if (pendingLaunches.some(claim => sameDrawing(claim.drawing, drawing))) {
        throw new Error("Drawing has a pending Civil 3D launch; wait for discovery or claim expiry before retrying.");
      }
      if (agents.length + pendingLaunches.length >= cap) throw new Error(`Agent instance cap (${cap}) reached. Instances: ${describeInstances(agents)}. Pending launches: ${JSON.stringify(pendingLaunches)}`);
      const shortcut = await this.deps.shortcut(args.year, args.units ?? "Imperial");
      const claimId = randomUUID();
      const beforePids = await this.deps.acadPids();
      const beforeInstancePids = new Set(instances.map(i => i.pid));
      const created = this.deps.now();
      claimExpiresAt = created + LAUNCH_GRACE_MS;
      const launchToken = randomUUID();
      const server = await this.deps.serverIdentity();
      const claim = { claimId, drawing, ownerSession: sessionId, label: sessionLabel, ownerLabel: sessionLabel,
        ownerServerPid: server.serverPid, ownerServerStartedAtUtc: server.serverStartedAtUtc, launchToken,
        createdAtUtc: new Date(created).toISOString(), expiresAtUtc: new Date(claimExpiresAt).toISOString() };
      const claimsFolder = join(this.root, "claims");
      await mkdir(claimsFolder, { recursive: true });
      claimPath = join(claimsFolder, `${claimId}.json`);
      const pending = `${claimPath}.tmp`;
      try { await writeFile(pending, JSON.stringify(claim), { flag: "wx" }); await rename(pending, claimPath); }
      finally { await rm(pending, { force: true }); }
      temp = await mkdtemp(join(tmpdir(), "civil3d-instance-"));
      dispatched = true;
      const proof = await this.deps.launch(shortcut.path, join(temp, "start.lnk"), drawing, launchToken, claimPath);
      if (!proof || !Number.isInteger(proof.pid) || proof.pid <= 0 || !Number.isFinite(Date.parse(proof.startedAtUtc))) {
        throw new Error("Missing or invalid process launch proof. Refused attachment; inspect civil3d_instance list.");
      }
      launchPid = proof.pid;
      if (beforePids.includes(proof.pid) || beforeInstancePids.has(proof.pid) || Date.parse(proof.startedAtUtc) <= created) {
        throw new Error("Launch proof identifies a pre-existing process. Refused attachment; inspect civil3d_instance list.");
      }
      // The detached helper publishes proof before replying. The fallback also
      // permits injected launchers in tests to return proof directly.
      let onDisk: { launchPid?: number; launchStartedAtUtc?: string } | undefined;
      try { onDisk = JSON.parse(await readFile(claimPath, "utf8")); }
      catch (error) { if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error; }
      if (onDisk && onDisk.launchPid === undefined) {
        try {
          await writeFile(pending, JSON.stringify({ ...claim, launchPid: proof.pid, launchStartedAtUtc: proof.startedAtUtc }), { flag: "wx" });
          await rename(pending, claimPath);
        } finally { await rm(pending, { force: true }); }
      } else if (onDisk && (onDisk.launchPid !== proof.pid
        || Date.parse(onDisk.launchStartedAtUtc ?? "") !== Date.parse(proof.startedAtUtc))) {
        throw new Error("Launch claim proof differs from process proof. Refused attachment; inspect civil3d_instance list.");
      }
      const pendingLaunch: PendingLaunch = {
        instance: { instanceApi: 1, pid: proof.pid, startedAtUtc: proof.startedAtUtc, port: 0, tokenPath: "",
          acadVersion: "", owner: "agent", ownerSession: sessionId, claimId, label: sessionLabel,
          documents: [drawing], updatedAtUtc: new Date(created).toISOString(), status: "starting", responsive: false },
        claimPath, expiresAt: claimExpiresAt, diagnostics: { dialogs: [], missingXrefs: [] },
      };
      this.pending.set(proof.pid, pendingLaunch);
      this.lastLaunch = pendingLaunch.instance;
      let seen: CivilInstance[] = [];
      let nextScan = created;
      let postRegistrationScan = false;
      while (this.deps.now() - created < timeoutMs) {
        if (currentAbortSignal()?.aborted) throw new Error("Civil 3D instance start cancelled. Check civil3d_instance list before retrying.");
        seen = (await this.deps.registry({ root: this.root })).instances;
        const instance = seen.find(i => i.claimId === claimId && i.owner === "agent" && i.ownerSession === sessionId && i.responsive);
        if (instance) {
          if (instance.pid !== proof.pid || Date.parse(instance.startedAtUtc) !== Date.parse(proof.startedAtUtc)) {
            throw new Error("Start claim does not match the launched process proof. Refused attachment; inspect civil3d_instance list.");
          }
          if (beforePids.includes(instance.pid) || beforeInstancePids.has(instance.pid)
            || Date.parse(instance.startedAtUtc) <= created) {
            throw new Error("Start claim was taken by a pre-existing process. Refused attachment; inspect civil3d_instance list.");
          }
          // A known prompt can appear after the plugin registers (issue #48). Answer it before returning; if it
          // blocks the instance the next pass finds it unresponsive and the scheduled scan below keeps answering.
          if (!postRegistrationScan) {
            postRegistrationScan = true;
            await this.scanInto(pendingLaunch.diagnostics, proof.pid, proof.startedAtUtc, false);
            nextScan = this.deps.now() + 2000;
            continue;
          }
          takenClaimPath = join(claimsFolder, `${claimId}.taken-${instance.pid}.json`);
          attached = true;
          this.pending.delete(instance.pid);
          setAttachedInstance(instance); return { instance, ...this.diagnostics(pendingLaunch.diagnostics) };
        }
        if (this.deps.now() >= nextScan) {
          if (!(await this.deps.identityAlive(proof.pid, proof.startedAtUtc))) {
            await this.withdrawPending(pendingLaunch);
            throw new Error(`Civil 3D launched process ${proof.pid} exited or its identity changed before the plugin loaded.`);
          }
          await this.scanInto(pendingLaunch.diagnostics, proof.pid, proof.startedAtUtc, false);
          nextScan = this.deps.now() + 2000;
        }
        await this.deps.wait(1000);
      }
      if (this.deps.now() < claimExpiresAt && await this.deps.identityAlive(proof.pid, proof.startedAtUtc)) {
        return { status: "starting" as const, pid: proof.pid,
          message: "Civil 3D started; the plugin has not loaded yet", ...this.diagnostics(pendingLaunch.diagnostics) };
      }
      await this.withdrawPending(pendingLaunch);
      const newPids = (await this.deps.acadPids()).filter(pid => !beforePids.includes(pid));
      throw new Error(`Civil 3D start timed out. Instances seen: ${describeInstances(seen)}. New acad pids: ${JSON.stringify(newPids)}. `
        + "A new acad pid without discovery means the plugin is not loaded. Check the Civil 3D window and plugin installation.");
    } finally {
      try {
        const removeClaim = attached || !dispatched || (launchPid !== undefined && !this.pending.has(launchPid)
          && !this.deps.alive(launchPid));
        if (claimPath && removeClaim) {
          await rm(claimPath, { force: true });
        }
        if (takenClaimPath) await rm(takenClaimPath, { force: true });
        if (temp) {
          if (removeClaim) await rm(temp, { recursive: true, force: true });
          else {
            const folder = temp;
            const remaining = Math.max(0, (claimExpiresAt ?? this.deps.now()) - this.deps.now());
            const expiredClaim = claimPath;
            setTimeout(() => {
              void (async () => {
                if (expiredClaim) await rm(expiredClaim, { force: true });
                if (launchPid !== undefined) {
                  const pendingLaunch = this.pending.get(launchPid);
                  if (pendingLaunch && pendingLaunch.claimPath === expiredClaim) await this.withdrawPending(pendingLaunch);
                }
                await rm(folder, { recursive: true, force: true });
              })().catch(error => console.warn("Could not clean expired Civil 3D launch files:", (error as Error).message));
            }, remaining).unref();
          }
        }
      } finally {
        await this.deps.releaseLock(lockPath);
      }
    }
  }

  private diagnostics(scan: DialogScanResult) {
    const answered = (title: string) => scan.dialogs.some(d => d.action === "ignore_invoked" && titleIs(d, title));
    const warnings = [
      ...(scan.missingXrefs.length || answered(XREF_PROMPT) ? [XREF_WARNING] : []),
      ...(answered(SHX_PROMPT) ? [SHX_WARNING] : []),
      ...(answered(RECOVERY_PROMPT) ? [RECOVERY_WARNING] : []),
    ];
    return { ...scan, ...(warnings.length ? { warning: warnings.join(" ") } : {}) };
  }

  // One scan pass of the process identity (pid + start time) into diagnostics; a failed scan is recorded, never thrown.
  // reportOnly: nothing is clicked. Returns the dialogs of this pass.
  private async scanInto(diagnostics: DialogScanResult, pid: number, startedAtUtc: string, reportOnly: boolean): Promise<InstanceDialog[]> {
    try {
      const scan = await this.deps.scanDialogs(pid, startedAtUtc, { reportOnly });
      diagnostics.dialogs = mergeDialogs(diagnostics.dialogs.filter(d => d.title !== SCAN_FAILED), scan.dialogs);
      diagnostics.missingXrefs = [...new Set([...diagnostics.missingXrefs, ...scan.missingXrefs])];
      return scan.dialogs;
    } catch (error) {
      diagnostics.dialogs = mergeDialogs(diagnostics.dialogs,
        [{ title: SCAN_FAILED, className: "", buttons: [], error: (error as Error).message }]);
      return [];
    }
  }

  // attach on an unresponsive instance: a prompt is usually what blocks it. Only an agent instance owned by THIS
  // session gets answers (pid and start time from discovery); any other instance is only reported on.
  private async recoverUnresponsive(instance: CivilInstance): Promise<{ instance: CivilInstance; diagnostics: DialogScanResult }> {
    const mine = instance.owner === "agent" && instance.ownerSession === sessionId;
    const diagnostics: DialogScanResult = { dialogs: [], missingXrefs: [] };
    let latest = await this.scanInto(diagnostics, instance.pid, instance.startedAtUtc, !mine);
    const wasAnswered = (d: InstanceDialog) => d.action === "ignore_invoked";
    const answered = mine && latest.some(wasAnswered);
    for (let round = 0; answered && round < RECOVERY_ROUNDS; round++) {
      await this.deps.wait(RECOVERY_WAIT_MS);
      const fresh = (await this.deps.registry({ root: this.root, pid: instance.pid })).instances
        .find(i => i.pid === instance.pid && Date.parse(i.startedAtUtc) === Date.parse(instance.startedAtUtc));
      if (!fresh) break;
      if (fresh.responsive) return { instance: fresh, diagnostics };
      latest = await this.scanInto(diagnostics, instance.pid, instance.startedAtUtc, false);
    }
    const parts = [
      ...latest.filter(d => !wasAnswered(d)).map(d => `dialog '${d.title}' is open (buttons: ${d.buttons.join(", ") || "none"})`),
      ...diagnostics.dialogs.filter(wasAnswered).map(d => `dialog '${d.title}' was answered with Ignore`),
    ];
    const failed = diagnostics.dialogs.find(d => d.title === SCAN_FAILED);
    if (!parts.length && failed) parts.push(`dialog scan failed (${failed.error})`);
    const notAnswered = !mine && latest.length > 0;
    throw new Error(`Civil 3D instance ${instance.pid} is not responsive${parts.length ? `: ${parts.join("; ")}` : ""}.`
      + (notAnswered ? " Not answered: this session does not own the instance." : ""));
  }

  private async withdrawPending(pending: PendingLaunch) {
    await rm(pending.claimPath, { force: true });
    await rm(pending.claimPath.replace(/\.json$/, `.taken-${pending.instance.pid}.json`), { force: true });
    this.pending.delete(pending.instance.pid);
  }

  private async reconcilePending(instances: CivilInstance[]) {
    for (const pending of this.pending.values()) {
      const registered = instances.find(i => i.pid === pending.instance.pid
        && Date.parse(i.startedAtUtc) === Date.parse(pending.instance.startedAtUtc)
        && i.claimId === pending.instance.claimId && i.ownerSession === sessionId);
      if (registered || this.deps.now() >= pending.expiresAt
        || !(await this.deps.identityAlive(pending.instance.pid, pending.instance.startedAtUtc))) {
        await this.withdrawPending(pending);
      }
    }
  }

  private async pendingLaunches(instances: CivilInstance[]): Promise<{ claimId: string; drawing: string }[]> {
    const folder = join(this.root, "claims");
    let names: string[];
    try { names = await readdir(folder); }
    catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") return [];
      throw error;
    }
    const represented = new Set(instances.map(instance => instance.claimId));
    const pending = new Map<string, { claimId: string; drawing: string }>();
    for (const name of names) {
      if (!name.endsWith(".json")) continue;
      let claim;
      try { claim = JSON.parse(await readFile(join(folder, name), "utf8")); }
      catch (error) {
        if (error instanceof SyntaxError) continue;
        // The plugin can take a claim between enumeration and reading. Do not
        // mistake that rename (before discovery publication) for free capacity.
        if ((error as NodeJS.ErrnoException).code === "ENOENT") {
          throw new Error("Launch reservations changed during start checks. Retry start after discovery settles.");
        }
        throw error;
      }
      if (!claim || typeof claim.claimId !== "string" || typeof claim.drawing !== "string"
        || typeof claim.ownerSession !== "string" || !claim.ownerSession
        || !isAbsolute(claim.drawing) || !/\.dwg$/i.test(claim.drawing)
        || !Number.isFinite(Date.parse(claim.expiresAtUtc))
        || (name !== `${claim.claimId}.json` && !name.startsWith(`${claim.claimId}.taken-`))
        || represented.has(claim.claimId)) continue;
      if (Date.parse(claim.expiresAtUtc) <= this.deps.now()
        || (Number.isInteger(claim.launchPid) && typeof claim.launchStartedAtUtc === "string"
          && !(await this.deps.identityAlive(claim.launchPid, claim.launchStartedAtUtc)))) {
        await rm(join(folder, name), { force: true });
        continue;
      }
      pending.set(claim.claimId, { claimId: claim.claimId, drawing: claim.drawing });
    }
    return [...pending.values()];
  }

  async resolveStopTarget(args: { pid?: number }) {
    this.requireLocalHost();
    const pid = args.pid ?? attachedInstance()?.pid ?? this.lastLaunch?.pid;
    if (!pid) throw new Error("No attached instance; supply pid or attach first.");
    const { instances } = await this.deps.registry({ root: this.root });
    await this.reconcilePending(instances);
    const pending = this.pending.get(pid);
    if (pending && !instances.some(i => i.pid === pid)) return pending.instance;
    const instance = instances.find(i => i.pid === pid);
    if (!instance || instance.owner !== "agent" || instance.ownerSession !== sessionId) throw new Error("Only an agent-owned instance owned by THIS session can be stopped.");
    if (!instance.responsive) throw new Error(`Civil 3D instance ${pid} is not responsive.`);
    if (args.pid === undefined && (attachedInstance() ?? this.lastLaunch)?.startedAtUtc !== instance.startedAtUtc) {
      throw new Error("Attached instance identity changed. Attach again before stopping.");
    }
    return instance;
  }

  async stop(args: { pid?: number; discardChanges?: boolean; targetIdentity?: string }, timeoutMs = 60_000) {
    const instance = await this.resolveStopTarget(args);
    const pid = instance.pid;
    if (args.targetIdentity !== undefined && args.targetIdentity !== stopTargetIdentity(instance)) {
      throw new Error("Stop target instance changed after approval. Request a new approval.");
    }
    if (instance.status === "starting") {
      if (args.discardChanges !== true) throw new Error("Pending launch requires discardChanges: true; unsaved state cannot be checked before the plugin loads.");
      await this.deps.killPending(pid, instance.startedAtUtc);
    } else await this.deps.quit(instance, args.discardChanges === true);
    const started = this.deps.now();
    while (this.deps.alive(pid) && this.deps.now() - started < timeoutMs) await this.deps.wait(500);
    if (this.deps.alive(pid)) {
      // Without discardChanges the plugin checks DBMOD again just before it
      // quits, and cancels when a drawing changed after its reply. Only
      // plugin.log records that.
      const hint = args.discardChanges === true ? ""
        : " If a drawing changed after the unsaved check, the plugin cancelled the quit: plugin.log names the drawing and its DBMOD flags.";
      throw new Error(`Civil 3D instance ${pid} did not exit within 60 seconds. It was not killed.${hint}`);
    }
    await rm(join(this.root, "instances", `${pid}.json`), { force: true });
    const pending = this.pending.get(pid);
    if (pending) await this.withdrawPending(pending);
    if (attachedInstance()?.pid === pid) setAttachedInstance();
    return { stopped: true, pid };
  }
}

export function stopTargetIdentity(instance: CivilInstance): string {
  return JSON.stringify([instance.pid, instance.port, instance.startedAtUtc, instance.ownerSession, instance.claimId]);
}

export const instanceManager = new InstanceManager();
