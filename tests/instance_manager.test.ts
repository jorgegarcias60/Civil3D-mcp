import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mkdir, mkdtemp, readdir, readFile, rename, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { InstanceManager, defaultInstanceDependencies } from "../src/utils/instanceManager.js";
import { sessionId, sessionLabel, setAttachedInstance, attachedInstance, selectDefault, withPinnedEndpoint } from "../src/utils/instanceSession.js";
import { isOrphan, parseInstance, pluginDataRoot, processStartTimes, readInstanceRegistry, type CivilInstance } from "../src/utils/instanceRegistry.js";
import { findCivilShortcut, shortcutArguments, windowsScript, launchProcessScript, acquireStartLock, releaseStartLock, type CivilLaunchProof } from "../src/utils/instanceLauncher.js";
import { readPluginToken } from "../src/utils/pluginAuth.js";
import { ApprovalPolicyService, isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { INSTANCE_DOMAIN_DEFINITION } from "../src/tools/domains/instanceDomain.js";

vi.mock("node:fs/promises", async importOriginal => {
  const actual = await importOriginal<typeof import("node:fs/promises")>();
  return { ...actual, readFile: vi.fn(actual.readFile) };
});

let temp: string;
let root: string;
let drawing: string;
let instances: CivilInstance[];
const own = (overrides: Partial<CivilInstance> = {}): CivilInstance => ({
  instanceApi: 1, pid: 101, port: 8758, tokenPath: join(root, "rpc-token-8758"),
  startedAtUtc: "2026-10-15T12:00:00Z", updatedAtUtc: "2026-10-15T12:00:00Z", acadVersion: "25.1",
  owner: "agent", ownerSession: sessionId, claimId: "test-claim", documents: [drawing], responsive: true, ...overrides,
});

beforeEach(async () => {
  temp = await mkdtemp(join(tmpdir(), "c3d-instances-test-"));
  vi.stubEnv("LOCALAPPDATA", temp);
  vi.stubEnv("CIVIL3D_PORT", undefined);
  vi.stubEnv("CIVIL3D_MAX_AGENT_INSTANCES", undefined);
  root = pluginDataRoot();
  await mkdir(join(root, "instances"), { recursive: true });
  await mkdir(join(root, "claims"), { recursive: true });
  drawing = join(temp, "scratch.dwg");
  await writeFile(drawing, "fake drawing; never launched");
  instances = [];
  setAttachedInstance();
});
afterEach(async () => { setAttachedInstance(); vi.unstubAllEnvs(); await rm(temp, { recursive: true, force: true }); });

function manager() {
  let now = Date.now();
  const deps = {
    ...defaultInstanceDependencies,
    registry: vi.fn(async () => ({ hasDiscoveryFiles: instances.length > 0, instances })),
    protect: vi.fn(async () => {}),
    shortcut: vi.fn(async () => ({ path: "Civil 3D 2027 Imperial.lnk", year: 2027, units: "Imperial" })),
    launch: vi.fn(async (_source: string, _destination: string, _drawing: string, _launchToken: string, _claimPath: string): Promise<CivilLaunchProof> =>
      ({ pid: 101, startedAtUtc: new Date(now + 1).toISOString() })),
    serverIdentity: vi.fn(async () => ({ serverPid: 777, serverStartedAtUtc: "2026-10-15T11:00:00Z" })),
    adopt: vi.fn(async (instance: CivilInstance, label: string) => ({ ...instance, ownerSession: sessionId, ownerLabel: label,
      label, ownerServerPid: 777, ownerServerStartedAtUtc: "2026-10-15T11:00:00Z", orphan: false })),
    acadPids: vi.fn(async () => [123]),
    alive: vi.fn(() => false), quit: vi.fn(async () => ({})),
    acquireLock: vi.fn(async (path: string) => {
      try { await writeFile(path, JSON.stringify({ pid: process.pid }), { flag: "wx" }); }
      catch (error) {
        if ((error as NodeJS.ErrnoException).code === "EEXIST") throw new Error("Another Civil 3D start is in progress.");
        throw error;
      }
    }),
    releaseLock: vi.fn(async (path: string) => { await rm(path, { force: true }); }),
    now: () => now, wait: vi.fn(async (ms: number) => { now += ms; }),
  };
  return { deps, service: new InstanceManager(deps, root) };
}

describe("instance registry and selection", () => {
  it("validates the discovery version, pid, port, token path and ownership", () => {
    expect(parseInstance(own(), "101.json")).toBeDefined();
    for (const change of [{ pid: -1 }, { instanceApi: 2 }, { port: 8080 }, { tokenPath: drawing },
      { owner: "unknown" }, { documents: ["relative.dwg"] }, { startedAtUtc: "bad" }, { ownerSession: null }]) {
      expect(parseInstance({ ...own(), ...change }, "101.json")).toBeUndefined();
    }
    expect(parseInstance(own(), "102.json")).toBeUndefined();
  });

  it("drops stale and malformed records, retains unresponsive live instances, and probes only live pids", async () => {
    for (const [name, value] of [["101.json", own()], ["102.json", own({ pid: 102 })], ["103.json", {}]] as const) {
      await writeFile(join(root, "instances", name), JSON.stringify(value));
    }
    const probe = vi.fn(async () => false);
    const snapshot = await readInstanceRegistry({ root, alive: pid => pid === 101,
      startTimes: async () => ({ 101: own().startedAtUtc }), probe });
    expect(snapshot.hasDiscoveryFiles).toBe(true);
    expect(snapshot.instances).toEqual([expect.objectContaining({ pid: 101, responsive: false })]);
    expect(probe).toHaveBeenCalledTimes(1);
  });

  it("drops reused PIDs before drawing-open and cap checks, retaining matching unresponsive processes", async () => {
    await writeFile(join(root, "instances", "101.json"), JSON.stringify(own()));
    await writeFile(join(root, "instances", "102.json"), JSON.stringify(own({ pid: 102, documents: [] })));
    const probe = vi.fn(async () => false);
    const snapshot = await readInstanceRegistry({ root, alive: () => true, probe,
      startTimes: async () => ({ 101: "2026-10-15T13:00:00Z", 102: own().startedAtUtc }) });
    expect(snapshot.instances).toEqual([expect.objectContaining({ pid: 102, responsive: false })]);
    expect(probe).toHaveBeenCalledTimes(1);
    const { service, deps } = manager();
    deps.registry.mockResolvedValue(snapshot);
    vi.stubEnv("CIVIL3D_MAX_AGENT_INSTANCES", "2");
    deps.launch.mockRejectedValue(new Error("reached launcher"));
    await expect(service.start({ drawing })).rejects.toThrow("reached launcher");
    expect(deps.launch).toHaveBeenCalledOnce();
  });

  it("checks only the selected pid before probing the registry", async () => {
    await writeFile(join(root, "instances", "101.json"), JSON.stringify(own()));
    await writeFile(join(root, "instances", "102.json"), JSON.stringify(own({ pid: 102 })));
    const alive = vi.fn(() => true);
    const startTimes = vi.fn(async () => ({ 101: own().startedAtUtc }));
    const probe = vi.fn(async () => true);
    const snapshot = await readInstanceRegistry({ root, pid: 101, alive, startTimes, probe });
    expect(snapshot.instances.map(i => i.pid)).toEqual([101]);
    expect(alive).toHaveBeenCalledExactlyOnceWith(101);
    expect(startTimes).toHaveBeenCalledExactlyOnceWith([101]);
    expect(probe).toHaveBeenCalledOnce();
  });

  it.skipIf(process.platform !== "win32")("reads the real process creation time and rejects an old record with that live PID", async () => {
    const starts = await processStartTimes([process.pid]);
    expect(Number.isFinite(Date.parse(starts[process.pid]))).toBe(true);
    const instance = own({ pid: process.pid, startedAtUtc: starts[process.pid] });
    const file = join(root, "instances", `${process.pid}.json`);
    const probe = vi.fn(async () => false);
    await writeFile(file, JSON.stringify(instance));
    expect((await readInstanceRegistry({ root, probe })).instances).toHaveLength(1);
    await writeFile(file, JSON.stringify({ ...instance, startedAtUtc: "2000-01-01T00:00:00Z" }));
    expect((await readInstanceRegistry({ root, probe })).instances).toEqual([]);
    expect(probe).toHaveBeenCalledTimes(1);
  }, 20_000);

  it("uses legacy only when discovery files are absent", async () => {
    expect(selectDefault(await readInstanceRegistry({ root }))).toBeUndefined();
    expect(selectDefault({ hasDiscoveryFiles: true, instances: [] })).toBeUndefined();
  });

  it("automatically selects exactly one responsive instance owned by this session", () => {
    expect(selectDefault({ hasDiscoveryFiles: true, instances: [own()] })?.pid).toBe(101);
  });

  it.each([
    ["user", () => [own({ owner: "user", ownerSession: null, claimId: null })]],
    ["other agent", () => [own({ ownerSession: "other" })]],
    ["multiple", () => [own(), own({ pid: 102 })]],
    ["own agent plus user", () => [own(), own({ pid: 102, owner: "user", ownerSession: null, claimId: null })]],
    ["own agent plus other agent", () => [own(), own({ pid: 102, ownerSession: "other" })]],
    ["unresponsive", () => [own({ responsive: false })]],
  ])("refuses %s and lists pid, owner and drawings", (_name, make) => {
    const call = () => selectDefault({ hasDiscoveryFiles: true, instances: make() });
    expect(call).toThrow("pid=101");
    expect(call).toThrow(JSON.stringify(drawing));
    expect(call).toThrow("userInstance: true");
  });
});

describe("attachment", () => {
  it("requires explicit user permission for a user instance", async () => {
    const { service } = manager();
    instances = [own({ owner: "user", ownerSession: null, claimId: null })];
    await expect(service.attach({ pid: 101 })).rejects.toThrow("userInstance: true");
    expect(attachedInstance()).toBeUndefined();
    await service.attach({ pid: 101, userInstance: true });
    expect(attachedInstance()?.pid).toBe(101);
  });

  it("requires force for another session and returns a warning without changing ownership", async () => {
    const { service } = manager();
    instances = [own({ ownerSession: "another" })];
    await expect(service.attach({ pid: 101 })).rejects.toThrow("another agent session");
    const result = await service.attach({ pid: 101, force: true });
    expect(result.warning).toMatch(/Forced attachment/);
    expect(result.instance.ownerSession).toBe("another");
    await expect(service.stop({})).rejects.toThrow("THIS session");
  });

  it("attaches by case-insensitive full drawing path and detaches locally", async () => {
    const { service } = manager(); instances = [own()];
    await service.attach({ drawing: drawing.toUpperCase() });
    expect((await service.list()).instances[0].attachedToThisSession).toBe(true);
    expect(service.detach().attached).toBe(false);
    expect(instances).toHaveLength(1);
    await expect(service.attach({ pid: 101, drawing })).rejects.toThrow("exactly one");
    await expect(service.attach({ drawing: "scratch.dwg" })).rejects.toThrow("absolute");
  });

  it("reads tokens from the pinned instance even when another attachment exists", async () => {
    const a = own(); const b = own({ pid: 102, port: 8759, tokenPath: join(root, "rpc-token-8759") });
    await writeFile(a.tokenPath, "first"); await writeFile(b.tokenPath, "second");
    setAttachedInstance(a);
    expect(withPinnedEndpoint(b, readPluginToken)).toBe("second");
    expect(readPluginToken()).toBe("first");
  });
});

describe("start lifecycle", () => {
  it("refuses launch when a claim is renamed between enumeration and reading", async () => {
    const { service, deps } = manager();
    const claimId = "moving-claim";
    const from = join(root, "claims", `${claimId}.json`);
    const to = join(root, "claims", `${claimId}.taken-201.json`);
    await writeFile(from, JSON.stringify({ claimId, drawing, ownerSession: "other",
      expiresAtUtc: new Date(deps.now() + 300_000).toISOString() }));
    vi.mocked(readFile).mockImplementationOnce(async () => {
      await rename(from, to);
      throw Object.assign(new Error("claim was taken"), { code: "ENOENT" });
    });
    await expect(service.start({ drawing })).rejects.toThrow("Launch reservations changed");
    expect(deps.launch).not.toHaveBeenCalled();
    expect(await readdir(join(root, "claims"))).toEqual([`${claimId}.taken-201.json`]);
    await expect(service.start({ drawing })).rejects.toThrow("pending Civil 3D launch");
  });

  it("reserves capacity when a helper fails before returning process proof", async () => {
    const { service, deps } = manager();
    vi.stubEnv("CIVIL3D_MAX_AGENT_INSTANCES", "1");
    deps.launch.mockRejectedValue(new Error("No process launch proof received"));
    await expect(service.start({ drawing })).rejects.toThrow("No process launch proof received");
    const [file] = await readdir(join(root, "claims"));
    expect(JSON.parse(await readFile(join(root, "claims", file), "utf8")).launchPid).toBeUndefined();
    const otherDrawing = join(temp, "other.dwg");
    await writeFile(otherDrawing, "fake drawing");
    await expect(service.start({ drawing: otherDrawing })).rejects.toThrow("cap (1) reached");
    expect(deps.launch).toHaveBeenCalledOnce();
  });

  it.each(["same drawing", "different drawing"])("reserves a cancelled launch before discovery (%s)", async kind => {
    const { service, deps } = manager();
    vi.stubEnv("CIVIL3D_MAX_AGENT_INSTANCES", "1");
    deps.alive.mockReturnValue(true);
    deps.wait.mockRejectedValue(new Error("mock cancellation"));
    await expect(service.start({ drawing })).rejects.toThrow("mock cancellation");
    const [file] = await readdir(join(root, "claims"));
    const claimPath = join(root, "claims", file);
    const claim = JSON.parse(await readFile(claimPath, "utf8"));
    // A different server owns the retained reservation when this start retries.
    await writeFile(claimPath, JSON.stringify({ ...claim, ownerSession: "another-session" }));
    const nextDrawing = kind === "same drawing" ? drawing.toUpperCase() : join(temp, "other.dwg");
    if (kind === "different drawing") await writeFile(nextDrawing, "fake drawing");
    await expect(service.start({ drawing: nextDrawing })).rejects.toThrow(
      kind === "same drawing" ? "pending Civil 3D launch" : "cap (1) reached");
    expect(deps.launch).toHaveBeenCalledOnce();
    expect(await readdir(join(root, "claims"))).toEqual([file]);
    expect(deps.releaseLock).toHaveBeenCalledTimes(2);
  });

  it("counts a taken claim before publication but deduplicates it once discovered", async () => {
    const { service, deps } = manager();
    vi.stubEnv("CIVIL3D_MAX_AGENT_INSTANCES", "2");
    const claim = { claimId: "pending-claim", drawing: join(temp, "pending.dwg"), ownerSession: "other",
      expiresAtUtc: new Date(deps.now() + 300_000).toISOString() };
    await writeFile(join(root, "claims", "pending-claim.taken-201.json"), JSON.stringify(claim));
    await writeFile(join(root, "claims", "pending-claim.json"), JSON.stringify(claim));
    instances = [own({ pid: 202, documents: [] })];
    await expect(service.start({ drawing })).rejects.toThrow("cap (2) reached");
    expect(deps.launch).not.toHaveBeenCalled();
    instances = [own({ pid: 201, claimId: claim.claimId, documents: [claim.drawing] })];
    deps.launch.mockRejectedValue(new Error("reached launcher"));
    await expect(service.start({ drawing })).rejects.toThrow("reached launcher");
    expect(deps.launch).toHaveBeenCalledOnce();
  });

  it("ignores expired, malformed and temporary claims", async () => {
    const { service, deps } = manager();
    vi.stubEnv("CIVIL3D_MAX_AGENT_INSTANCES", "1");
    const claim = { claimId: "expired", drawing, ownerSession: "other", expiresAtUtc: new Date(deps.now()).toISOString() };
    await writeFile(join(root, "claims", "expired.json"), JSON.stringify(claim));
    await writeFile(join(root, "claims", "broken.json"), "{");
    await writeFile(join(root, "claims", "null.json"), "null");
    await writeFile(join(root, "claims", "expired.json.tmp"), JSON.stringify({ ...claim,
      expiresAtUtc: new Date(deps.now() + 300_000).toISOString() }));
    deps.launch.mockRejectedValue(new Error("reached launcher"));
    await expect(service.start({ drawing })).rejects.toThrow("reached launcher");
    expect(deps.launch).toHaveBeenCalledOnce();
  });

  it("refuses the global agent cap before launch, including orphans regardless of session", async () => {
    const { service, deps } = manager(); vi.stubEnv("CIVIL3D_MAX_AGENT_INSTANCES", "2");
    instances = [own({ documents: [], ownerSession: "other", orphan: true }), own({ pid: 102, documents: [] })];
    await expect(service.start({ drawing })).rejects.toThrow("cap (2) reached");
    expect(deps.launch).not.toHaveBeenCalled();
    expect(await readdir(join(root, "claims"))).toEqual([]);
  });

  it("refuses a drawing open in a user or another agent's instance", async () => {
    const { service, deps } = manager(); instances = [own({ ownerSession: "other" })];
    await expect(service.start({ drawing })).rejects.toThrow("already open");
    instances = [own({ owner: "user", ownerSession: null, claimId: null })];
    await expect(service.start({ drawing })).rejects.toThrow("cannot be open twice");
    expect(deps.launch).not.toHaveBeenCalled();
  });

  it("reattaches an already-open own drawing even at the cap", async () => {
    const { service, deps } = manager(); instances = [own()]; vi.stubEnv("CIVIL3D_MAX_AGENT_INSTANCES", "1");
    expect((await service.start({ drawing })).alreadyOpen).toBe(true);
    expect(deps.launch).not.toHaveBeenCalled();
  });

  it("writes an expiring claim, waits for its responsive discovery, attaches and cleans temporary files", async () => {
    const { service, deps } = manager(); let shortcutPath = "";
    deps.launch.mockImplementation(async (_source, destination, path, launchToken) => {
      shortcutPath = destination;
      const names = await readdir(join(root, "claims"));
      expect(names).toHaveLength(1);
      const claim = JSON.parse(await readFile(join(root, "claims", names[0]), "utf8"));
      expect(claim).toMatchObject({ drawing, ownerSession: sessionId, label: "scratch.dwg", ownerLabel: "scratch.dwg",
        ownerServerPid: 777, ownerServerStartedAtUtc: "2026-10-15T11:00:00Z" });
      expect(claim.launchToken).toBe(launchToken);
      expect(launchToken).toMatch(/^[0-9a-f-]{36}$/);
      expect(claim.launchPid).toBeUndefined();
      expect(Date.parse(claim.expiresAtUtc) - Date.parse(claim.createdAtUtc)).toBe(300_000);
      expect(path).toBe(drawing);
      instances = [own({ claimId: claim.claimId, startedAtUtc: new Date(Date.parse(claim.createdAtUtc) + 1).toISOString() })];
      return { pid: instances[0].pid, startedAtUtc: instances[0].startedAtUtc };
    });
    deps.registry.mockImplementation(async () => {
      if (instances.length) {
        const [file] = await readdir(join(root, "claims"));
        expect(JSON.parse(await readFile(join(root, "claims", file), "utf8"))).toMatchObject({
          launchPid: instances[0].pid, launchStartedAtUtc: instances[0].startedAtUtc,
        });
      }
      return { hasDiscoveryFiles: instances.length > 0, instances };
    });
    expect((await service.start({ drawing })).instance.pid).toBe(101);
    expect(deps.shortcut).toHaveBeenCalledWith(undefined, "Imperial");
    expect(attachedInstance()?.pid).toBe(101);
    expect(await readdir(join(root, "claims"))).toEqual([]);
    await expect(readFile(shortcutPath)).rejects.toMatchObject({ code: "ENOENT" });
    await expect(readFile(join(root, "instance-start.lock"))).rejects.toMatchObject({ code: "ENOENT" });
  });

  it("removes a claim renamed by the plugin after successful attachment", async () => {
    const { service, deps } = manager();
    deps.launch.mockImplementation(async () => {
      const [file] = await readdir(join(root, "claims"));
      const claim = JSON.parse(await readFile(join(root, "claims", file), "utf8"));
      const startedAtUtc = new Date(Date.parse(claim.createdAtUtc) + 1).toISOString();
      instances = [own({ claimId: claim.claimId, startedAtUtc })];
      await rename(join(root, "claims", file), join(root, "claims", `${claim.claimId}.taken-101.json`));
      return { pid: 101, startedAtUtc };
    });
    expect((await service.start({ drawing })).instance.pid).toBe(101);
    expect(await readdir(join(root, "claims"))).toEqual([]);
  });

  it("retains a launched process claim after cancellation so discovery can still attach it", async () => {
    const { service, deps } = manager();
    deps.alive.mockReturnValue(true);
    deps.wait.mockRejectedValue(new Error("mock cancellation"));
    await expect(service.start({ drawing })).rejects.toThrow("mock cancellation");
    const [file] = await readdir(join(root, "claims"));
    const claim = JSON.parse(await readFile(join(root, "claims", file), "utf8"));
    expect(claim).toMatchObject({ launchPid: 101, ownerSession: sessionId,
      ownerLabel: sessionLabel, ownerServerPid: 777, ownerServerStartedAtUtc: "2026-10-15T11:00:00Z" });
    expect(deps.serverIdentity).toHaveBeenCalledOnce();
    expect(Date.parse(claim.expiresAtUtc)).toBeGreaterThan(Date.parse(claim.createdAtUtc));
  });

  it.skipIf(process.platform !== "win32")("allows a late harmless helper to publish proof after server launch wait fails", async () => {
    const { service, deps } = manager();
    let retained: { claimPath: string; token: string; proofPath: string } | undefined;
    deps.launch.mockImplementation(async (_source, destination, _drawing, token, claimPath) => {
      retained = { claimPath, token, proofPath: `${destination}.proof.json` };
      throw new Error("No process launch proof received");
    });
    await expect(service.start({ drawing })).rejects.toThrow("No process launch proof received");
    expect(retained).toBeDefined();
    const data = retained!;
    expect(JSON.parse(await readFile(data.claimPath, "utf8"))).toMatchObject({ launchToken: data.token });
    const childCode = "Start-Sleep -Milliseconds 200";
    await windowsScript('$data = [Console]::In.ReadToEnd() | ConvertFrom-Json\n' + launchProcessScript, {
      target: join(process.env.SystemRoot!, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
      arguments: `-NoProfile -NonInteractive -EncodedCommand ${Buffer.from(childCode, "utf16le").toString("base64")}`,
      workingDirectory: temp, launchToken: data.token, proofPath: data.proofPath, claimPath: data.claimPath,
    });
    const proof = JSON.parse(await readFile(data.proofPath, "utf8"));
    expect(JSON.parse(await readFile(data.claimPath, "utf8"))).toMatchObject({
      launchPid: proof.pid, launchStartedAtUtc: proof.startedAtUtc,
    });
  }, 20_000);

  it("allows a third agent at the default cap alongside user instances", async () => {
    const { service, deps } = manager();
    instances = [own({ pid: 11, documents: [], ownerSession: "other1" }),
      own({ pid: 12, documents: [], ownerSession: "other2" }),
      own({ pid: 13, documents: [], owner: "user", ownerSession: null, claimId: null })];
    deps.launch.mockImplementation(async () => {
      const [file] = await readdir(join(root, "claims"));
      const claim = JSON.parse(await readFile(join(root, "claims", file), "utf8"));
      instances.push(own({ claimId: claim.claimId, startedAtUtc: new Date(Date.parse(claim.createdAtUtc) + 1).toISOString() }));
      return { pid: 101, startedAtUtc: instances.at(-1)!.startedAtUtc };
    });
    expect((await service.start({ drawing })).instance.pid).toBe(101);
    expect(deps.launch).toHaveBeenCalledOnce();
  });

  it("cleans the claim and shortcut on timeout and reports new acad pids", async () => {
    const { service, deps } = manager(); let link = "";
    deps.acadPids.mockResolvedValueOnce([]).mockResolvedValueOnce([123]);
    deps.launch.mockImplementation(async (_source, destination) => {
      link = destination; await writeFile(destination, "fake lnk");
      return { pid: 101, startedAtUtc: new Date(deps.now() + 1).toISOString() };
    });
    await expect(service.start({ drawing }, 2000)).rejects.toThrow("New acad pids: [123]");
    expect(await readdir(join(root, "claims"))).toEqual([]);
    await expect(readFile(link)).rejects.toMatchObject({ code: "ENOENT" });
  });

  it.each(["old pid", "old start time"])("refuses a claim adopted by a pre-existing process (%s)", async kind => {
    const { service, deps } = manager();
    deps.launch.mockImplementation(async () => {
      const [file] = await readdir(join(root, "claims"));
      const claim = JSON.parse(await readFile(join(root, "claims", file), "utf8"));
      instances = [own({ pid: kind === "old pid" ? 123 : 101, claimId: claim.claimId,
        startedAtUtc: kind === "old pid" ? new Date(Date.parse(claim.createdAtUtc) + 1).toISOString() : claim.createdAtUtc })];
      return { pid: instances[0].pid, startedAtUtc: instances[0].startedAtUtc };
    });
    await expect(service.start({ drawing })).rejects.toThrow("pre-existing process");
    expect(attachedInstance()).toBeUndefined();
    expect(await readdir(join(root, "claims"))).toEqual([]);
  });

  it("retains the claim after an uncertain launcher failure and refuses concurrent starts", async () => {
    const { service, deps } = manager();
    deps.launch.mockRejectedValue(new Error("mock launch failed"));
    await expect(service.start({ drawing })).rejects.toThrow("mock launch failed");
    expect(await readdir(join(root, "claims"))).toHaveLength(1);
    await writeFile(join(root, "instance-start.lock"), "123");
    await expect(service.start({ drawing })).rejects.toThrow("start is in progress");
  });

  it.skipIf(process.platform !== "win32")("serializes start locks and reaps only dead or reused owners", async () => {
    const lockPath = join(root, "instance-start.lock");
    await acquireStartLock(lockPath);
    const held = JSON.parse(await readFile(lockPath, "utf8"));
    expect(held.pid).toBe(process.pid);
    await expect(acquireStartLock(lockPath)).rejects.toThrow("start is in progress");
    await releaseStartLock(lockPath);
    await expect(readFile(lockPath)).rejects.toMatchObject({ code: "ENOENT" });
    await writeFile(lockPath, JSON.stringify({ pid: 999999999, startedAtUtc: "2026-10-15T10:00:00Z" }));
    await acquireStartLock(lockPath);
    await releaseStartLock(lockPath);
    await writeFile(lockPath, JSON.stringify({ pid: process.pid, startedAtUtc: "2000-01-01T00:00:00Z" }));
    await acquireStartLock(lockPath);
    await releaseStartLock(lockPath);
    await writeFile(lockPath, "123");
    await expect(acquireStartLock(lockPath)).rejects.toThrow("incomplete");
  }, 30_000);

  it("rejects invalid drawings and fixed-port mode before writing a claim", async () => {
    const { service, deps } = manager();
    await expect(service.start({ drawing: "relative.dwg" })).rejects.toThrow("absolute");
    vi.stubEnv("CIVIL3D_PORT", "8757");
    await expect(service.start({ drawing })).rejects.toThrow("Unset CIVIL3D_PORT");
    expect(deps.protect).not.toHaveBeenCalled();
  });

  it.each(["\\scratch\\A.dwg", "/scratch/A.dwg", "C:scratch\\A.dwg", "\\\\server\\A.dwg"])(
    "rejects paths without a drive root or complete UNC share before side effects: %s", async path => {
      const { service, deps } = manager();
      await expect(service.start({ drawing: path })).rejects.toThrow("fully qualified");
      expect(deps.protect).not.toHaveBeenCalled();
      expect(deps.launch).not.toHaveBeenCalled();
    });

  it("normalizes drawing paths before file checks, existing-instance selection, claims and launch", async () => {
    const { service, deps } = manager();
    const alias = `${temp.replaceAll("\\", "/")}/unused/../scratch.dwg`;
    instances = [own()];
    expect((await service.start({ drawing: alias })).alreadyOpen).toBe(true);
    instances = [];
    deps.launch.mockImplementation(async (_source, _destination, path) => {
      const [file] = await readdir(join(root, "claims"));
      const claim = JSON.parse(await readFile(join(root, "claims", file), "utf8"));
      expect(path).toBe(drawing);
      expect(claim.drawing).toBe(drawing);
      instances = [own({ claimId: claim.claimId, startedAtUtc: new Date(deps.now() + 1).toISOString() })];
      return { pid: 101, startedAtUtc: instances[0].startedAtUtc };
    });
    expect((await service.start({ drawing: alias })).instance.pid).toBe(101);
  });

  it.each(["different pid", "different start"])("refuses a concurrent user process carrying the claim (%s)", async kind => {
    const { service, deps } = manager();
    deps.launch.mockImplementation(async () => {
      const [file] = await readdir(join(root, "claims"));
      const claim = JSON.parse(await readFile(join(root, "claims", file), "utf8"));
      const startedAtUtc = new Date(deps.now() + 1).toISOString();
      instances = [own({ pid: kind === "different pid" ? 102 : 101, claimId: claim.claimId,
        startedAtUtc: kind === "different start" ? new Date(deps.now() + 2).toISOString() : startedAtUtc })];
      return { pid: 101, startedAtUtc };
    });
    await expect(service.start({ drawing })).rejects.toThrow("does not match the launched process proof");
    expect(attachedInstance()).toBeUndefined();
    expect(deps.quit).not.toHaveBeenCalled();
  });

  it("fails closed and retains the claim when a dispatched launcher provides no proof", async () => {
    const { service, deps } = manager();
    deps.launch.mockResolvedValue(undefined as unknown as CivilLaunchProof);
    await expect(service.start({ drawing })).rejects.toThrow("Missing or invalid process launch proof");
    expect(attachedInstance()).toBeUndefined();
    expect(await readdir(join(root, "claims"))).toHaveLength(1);
  });
});

describe("stop lifecycle", () => {
  it.each(["user", "other"])("never stops a %s instance", async kind => {
    const { service, deps } = manager();
    instances = [own(kind === "user" ? { owner: "user", ownerSession: null, claimId: null } : { ownerSession: "other" })];
    await expect(service.stop({ pid: 101, discardChanges: true })).rejects.toThrow("THIS session");
    expect(deps.quit).not.toHaveBeenCalled();
  });

  it("propagates the plugin's unsaved guard and keeps attachment", async () => {
    const { service, deps } = manager(); instances = [own()]; setAttachedInstance(instances[0]);
    deps.quit.mockRejectedValue(new Error("Open documents have unsaved changes"));
    await expect(service.stop({})).rejects.toThrow("unsaved changes");
    expect(deps.quit).toHaveBeenCalledWith(instances[0], false);
    expect(attachedInstance()).toBeDefined();
  });

  it("stops an own instance with explicit discard and clears discovery only after exit", async () => {
    const { service, deps } = manager(); instances = [own()]; setAttachedInstance(instances[0]);
    await writeFile(join(root, "instances", "101.json"), JSON.stringify(instances[0]));
    expect(await service.stop({ discardChanges: true })).toEqual({ stopped: true, pid: 101 });
    expect(deps.quit).toHaveBeenCalledWith(instances[0], true);
    expect(attachedInstance()).toBeUndefined();
    expect(await readdir(join(root, "instances"))).toEqual([]);
  });

  it("does not remove discovery or kill the process on quit timeout", async () => {
    const { service, deps } = manager(); instances = [own()]; deps.alive.mockReturnValue(true);
    await writeFile(join(root, "instances", "101.json"), "kept");
    await expect(service.stop({ pid: 101 }, 1000)).rejects.toThrow("was not killed");
    expect(await readFile(join(root, "instances", "101.json"), "utf8")).toBe("kept");
  });
});

describe("shortcuts and approvals", () => {
  it.skipIf(process.platform !== "win32")("launches a harmless child with a private token and returns its exact identity", async () => {
    const output = join(temp, "child.json");
    const proofPath = join(temp, "proof.json");
    const claimPath = join(temp, "claim.json");
    await writeFile(claimPath, JSON.stringify({ launchToken: "unique-test-launch-token" }));
    const scriptPath = join(temp, "launch-check.ps1");
    await writeFile(scriptPath, '$ErrorActionPreference = "Stop"\ntry {\n' + launchProcessScript
      + '\n} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }');
    const childCode = `[IO.File]::WriteAllText('${output.replaceAll("'", "''")}', (@{ pid = $PID; startedAtUtc = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o'); token = $env:CIVIL3D_MCP_LAUNCH_TOKEN } | ConvertTo-Json -Compress))`;
    const parentToken = process.env.CIVIL3D_MCP_LAUNCH_TOKEN;
    await windowsScript('$data = [Console]::In.ReadToEnd() | ConvertFrom-Json\n& $data.scriptPath', {
      scriptPath,
      target: join(process.env.SystemRoot!, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
      arguments: `-NoProfile -NonInteractive -EncodedCommand ${Buffer.from(childCode, "utf16le").toString("base64")}`,
      workingDirectory: temp, launchToken: "unique-test-launch-token", proofPath, claimPath,
    });
    const proof = JSON.parse(await readFile(proofPath, "utf8"));
    expect(JSON.parse(await readFile(claimPath, "utf8"))).toMatchObject({
      launchPid: proof.pid, launchStartedAtUtc: proof.startedAtUtc,
    });
    await vi.waitFor(async () => {
      const child = JSON.parse(await readFile(output, "utf8"));
      expect(child).toEqual({ ...proof, token: "unique-test-launch-token" });
    }, { timeout: 5000 });
    expect(process.env.CIVIL3D_MCP_LAUNCH_TOKEN).toBe(parentToken);
  }, 20_000);
  it.skipIf(process.platform !== "win32")("passes Unicode drawing paths as data to the Windows helper", async () => {
    const drawing = "C:\\Scratch folder\\Término-日本語.dwg";
    const value = await windowsScript('$data = [Console]::In.ReadToEnd() | ConvertFrom-Json\n[Console]::Write($data.drawing)', { drawing });
    expect(value).toBe(drawing);
  });
  it.skipIf(process.platform !== "win32")("reads a plain JSON launch payload with Unicode without starting a process", async () => {
    const payloadPath = join(temp, "launch-payload.json");
    const payload = { drawing: "C:\\Scratch folder\\Término-日本語.dwg", launchToken: "dry-run-token" };
    await writeFile(payloadPath, JSON.stringify(payload));
    const output = await windowsScript('$inputData = [Console]::In.ReadToEnd() | ConvertFrom-Json\n'
      + '$data = [IO.File]::ReadAllText($inputData.payloadPath) | ConvertFrom-Json\n'
      + '[Console]::Write(($data | ConvertTo-Json -Compress))', { payloadPath });
    expect(JSON.parse(output)).toEqual(payload);
  });
  it("preserves installed shortcut switches and quotes drawings with spaces", () => {
    expect(shortcutArguments('/p "Civil 3D" /product C3D', 'C:\\Scratch folder\\A.dwg'))
      .toBe('/p "Civil 3D" /product C3D "C:\\Scratch folder\\A.dwg"');
    expect(() => shortcutArguments("", 'C:\\bad".dwg')).toThrow("Invalid drawing");
  });

  it("selects the newest matching shortcut, or an explicit year and units", async () => {
    const menu = join(temp, "Start Menu"); await mkdir(menu);
    for (const name of ["Civil 3D 2026 Imperial.lnk", "Civil 3D 2027 Imperial.lnk", "Civil 3D 2024 Metric.lnk"]) {
      await writeFile(join(menu, name), "fake");
    }
    expect((await findCivilShortcut(undefined, "Imperial", [menu])).year).toBe(2027);
    expect((await findCivilShortcut(2024, "Metric", [menu])).year).toBe(2024);
    await expect(findCivilShortcut(2025, "Imperial", [menu])).rejects.toThrow("not found");
  });

  it("gates start and stop, and obtains lifecycle approval without touching any plugin", async () => {
    const fingerprint = vi.fn(async () => { throw new Error("must not connect"); });
    const policy = new ApprovalPolicyService(fingerprint);
    for (const [action, definition] of Object.entries(INSTANCE_DOMAIN_DEFINITION.actions)) {
      const target = { toolName: "civil3d_instance", action, ...definition };
      expect(isApprovalRequired(target)).toBe(["start", "stop"].includes(action));
      if (["start", "stop"].includes(action)) {
        const parameters = { action, drawing };
        const grant = await policy.requestApproval(target, parameters);
        await policy.enforce(target, { ...parameters, approvalToken: grant.approvalToken });
      }
    }
    expect(fingerprint).not.toHaveBeenCalled();
  });
});

describe("orphan attachment policy", () => {
  it("classifies only proven dead or reused owners as orphans and selects a sole matching label", () => {
    const former = own({ ownerSession: "former-session", ownerLabel: "current-job", label: "current-job",
      ownerServerPid: 800, ownerServerStartedAtUtc: "2026-10-15T10:00:00Z" });
    expect(isOrphan(former, () => false, {})).toBe(true);
    expect(isOrphan(former, () => true, { 800: "2026-10-15T10:00:01Z" })).toBe(true);
    expect(isOrphan(former, () => true, { 800: "2026-10-15T10:00:00Z" })).toBe(false);
    expect(isOrphan(former, () => true, {})).toBe(false);
    expect(isOrphan({ ...former, ownerServerStartedAtUtc: null }, () => false, {})).toBe(false);
    expect(selectDefault({ hasDiscoveryFiles: true, instances: [{ ...former, orphan: true }] }, sessionId, "current-job")?.pid).toBe(101);
    expect(() => selectDefault({ hasDiscoveryFiles: true, instances: [{ ...former, orphan: true }] },
      sessionId, "other-job")).toThrow("not attached");
  });

  it("adopts a matching label, requires adoptOrphan for another label, and never lets force bypass that requirement", async () => {
    const { service, deps } = manager();
    const orphan = (label: string) => own({ ownerSession: "former-session", ownerLabel: label, label,
      ownerServerPid: 800, ownerServerStartedAtUtc: "2026-10-15T10:00:00Z", orphan: true });
    instances = [orphan("current-job")];
    expect(await service.attach({ pid: 101, job: "current-job" })).toMatchObject({ adopted: true,
      instance: { ownerSession: sessionId, ownerLabel: "current-job" } });
    expect(deps.adopt).toHaveBeenCalledWith(instances[0], "current-job");

    service.detach();
    instances = [orphan("other-job")];
    await expect(service.attach({ pid: 101, force: true })).rejects.toThrow("adoptOrphan: true");
    expect(attachedInstance()).toBeUndefined();
    expect(deps.adopt).toHaveBeenCalledTimes(1);
    expect(await service.attach({ pid: 101, adoptOrphan: true })).toMatchObject({ adopted: true,
      instance: { ownerSession: sessionId, ownerLabel: "current-job" } });
    expect(deps.adopt).toHaveBeenCalledTimes(2);
  });
});
