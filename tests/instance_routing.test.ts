import { afterEach, describe, expect, it, vi } from "vitest";
import net from "node:net";
import { mkdtemp, writeFile, rm } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

const registry = vi.hoisted(() => ({ read: vi.fn() }));
vi.mock("../src/utils/instanceRegistry.js", async importOriginal => ({
  ...await importOriginal<typeof import("../src/utils/instanceRegistry.js")>(),
  readInstanceRegistry: registry.read,
}));
afterEach(() => { vi.restoreAllMocks(); registry.read.mockReset(); vi.unstubAllEnvs(); vi.resetModules(); });

describe("instance routing over the real TCP transport", () => {
  it("refuses automatic attachment after detach with an own agent alongside a user instance", async () => {
    vi.stubEnv("CIVIL3D_PORT", undefined);
    const { sessionId, setAttachedInstance, attachedInstance, resolveInstanceEndpoint } = await import("../src/utils/instanceSession.js");
    const instance = { instanceApi: 1 as const, pid: 101, port: 8758, tokenPath: "unused",
      startedAtUtc: "2026-10-15T12:00:00Z", updatedAtUtc: "2026-10-15T12:00:00Z", acadVersion: "25.1",
      owner: "agent" as const, ownerSession: sessionId, claimId: "claim", documents: ["C:\\scratch\\A.dwg"], responsive: true };
    setAttachedInstance(instance);
    registry.read.mockResolvedValue({ hasDiscoveryFiles: true, instances: [instance] });
    expect(await resolveInstanceEndpoint()).toMatchObject({ pid: 101 });
    setAttachedInstance();
    registry.read.mockResolvedValue({ hasDiscoveryFiles: true, instances: [instance,
      { ...instance, pid: 102, port: 8759, owner: "user", ownerSession: null, claimId: null }] });
    await expect(resolveInstanceEndpoint()).rejects.toMatchObject({ code: "CIVIL3D.NOT_ATTACHED" });
    expect(attachedInstance()).toBeUndefined();
  });

  it("treats an optional empty Desktop port as discovery mode", async () => {
    vi.stubEnv("CIVIL3D_PORT", "");
    registry.read.mockResolvedValue({ hasDiscoveryFiles: false, instances: [] });
    const { resolveInstanceEndpoint, routingMode } = await import("../src/utils/instanceSession.js");
    expect(routingMode).toBe("discovery");
    expect(await resolveInstanceEndpoint()).toMatchObject({ port: 8757, legacyFallback: true });
  });
  it.each(["user", "agent"] as const)("refuses fixed routing to %s ownership unless explicitly opted in", async owner => {
    vi.stubEnv("CIVIL3D_PORT", "8757");
    vi.stubEnv("CIVIL3D_ALLOW_FIXED_INSTANCE", undefined);
    const instance = { pid: 901, port: 8757, owner, ownerSession: owner === "user" ? null : "another-session",
      startedAtUtc: "2026-10-15T12:00:00Z", tokenPath: "selected-token", responsive: true };
    registry.read.mockResolvedValue({ hasDiscoveryFiles: true, instances: [instance] });
    const { resolveInstanceEndpoint } = await import("../src/utils/instanceSession.js");
    await expect(resolveInstanceEndpoint()).rejects.toMatchObject({ code: "CIVIL3D.NOT_ATTACHED" });
    expect(registry.read).toHaveBeenCalledWith({ port: 8757 });
    const { PLUGIN_DOMAIN_DEFINITION } = await import("../src/tools/domains/pluginDomain.js");
    expect(await PLUGIN_DOMAIN_DEFINITION.actions.health.execute({})).toMatchObject({ routing: "fixed", status: "not attached" });
    vi.stubEnv("CIVIL3D_ALLOW_FIXED_INSTANCE", "true");
    expect(await resolveInstanceEndpoint()).toMatchObject(instance);
  });

  it("scopes drawing-independent retry results to the selected instance", async () => {
    vi.stubEnv("CIVIL3D_PORT", undefined);
    const requests: number[] = [];
    const servers = [0, 1].map((i) => net.createServer(socket => socket.once("data", data => {
      const request = JSON.parse(data.toString()); requests.push(i);
      socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { fileName: `drawing-${i}.dwg` } }));
    })));
    await Promise.all(servers.map(server => new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve))));
    try {
      const { sessionId, setAttachedInstance } = await import("../src/utils/instanceSession.js");
      const instances = servers.map((server, i) => ({ instanceApi: 1 as const, pid: 101 + i,
        port: (server.address() as net.AddressInfo).port, tokenPath: "unused",
        startedAtUtc: `2026-10-15T12:00:0${i}Z`, updatedAtUtc: new Date().toISOString(), acadVersion: "25.1",
        owner: "agent" as const, ownerSession: sessionId, claimId: `claim-${i}`, documents: [], responsive: true }));
      registry.read.mockResolvedValue({ hasDiscoveryFiles: true, instances });
      const { captureDomainToolHandlers } = await import("../src/tools/domainRuntime.js");
      const { DRAWING_RUNTIME_DOMAIN_DEFINITION } = await import("../src/tools/domains/drawingRuntimeDomain.js");
      const { getToolHandler } = await import("../src/tools/toolHandlerRegistry.js");
      captureDomainToolHandlers(DRAWING_RUNTIME_DOMAIN_DEFINITION);
      const read = () => getToolHandler("civil3d_drawing")!({ action: "info", idempotencyKey: "same-drawing-read" });
      setAttachedInstance(instances[0]);
      expect(JSON.stringify(await read())).toContain("drawing-0.dwg");
      expect(JSON.stringify(await read())).toContain("drawing-0.dwg");
      setAttachedInstance(instances[1]);
      expect(JSON.stringify(await read())).toContain("drawing-1.dwg");
      expect(requests).toEqual([0, 1]);
      expect(registry.read).toHaveBeenCalledWith({ pid: 101 });
      expect(registry.read).toHaveBeenCalledWith({ pid: 102 });
    } finally { await Promise.all(servers.map(server => new Promise<void>(resolve => server.close(() => resolve())))); }
  });

  it.each(["attach", "detach"])("rejects cached retries for local %s state changes", async action => {
    const { INSTANCE_DOMAIN_DEFINITION } = await import("../src/tools/domains/instanceDomain.js");
    const { captureDomainToolHandlers } = await import("../src/tools/domainRuntime.js");
    const { getToolHandler } = await import("../src/tools/toolHandlerRegistry.js");
    captureDomainToolHandlers(INSTANCE_DOMAIN_DEFINITION);
    expect(INSTANCE_DOMAIN_DEFINITION.actions[action].safeForRetry).toBe(false);
    const result = await getToolHandler("civil3d_instance")!({ action, pid: 101, idempotencyKey: "attachment" });
    expect(result).toMatchObject({ isError: true, errorCode: "CIVIL3D.INVALID_INPUT" });
    expect(registry.read).not.toHaveBeenCalled();
  });

  it.each(["different pid", "reused pid"])("rejects drawing creation approved on another empty instance: %s", async kind => {
    vi.stubEnv("CIVIL3D_PORT", undefined);
    const requests: string[] = [];
    const servers = [0, 1].map(() => net.createServer(socket => socket.once("data", data => {
      const request = JSON.parse(data.toString());
      requests.push(request.method);
      socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { drawingLoaded: false } }));
    })));
    await Promise.all(servers.map(server => new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve))));
    try {
      const { sessionId, setAttachedInstance } = await import("../src/utils/instanceSession.js");
      const instances = servers.map((server, i) => ({ instanceApi: 1 as const, pid: kind === "reused pid" ? 101 : 101 + i,
        port: (server.address() as net.AddressInfo).port, tokenPath: "unused",
        startedAtUtc: `2026-10-15T12:00:0${i}Z`, updatedAtUtc: new Date().toISOString(), acadVersion: "25.1",
        owner: "agent" as const, ownerSession: sessionId, claimId: `claim-${i}`, documents: [], responsive: true }));
      registry.read.mockResolvedValue({ hasDiscoveryFiles: true, instances: [instances[0]] });
      setAttachedInstance(instances[0]);
      const { approvalPolicy } = await import("../src/tools/approvalPolicy.js");
      const { captureDomainToolHandlers } = await import("../src/tools/domainRuntime.js");
      const { DRAWING_RUNTIME_DOMAIN_DEFINITION: domain } = await import("../src/tools/domains/drawingRuntimeDomain.js");
      const { getToolHandler } = await import("../src/tools/toolHandlerRegistry.js");
      captureDomainToolHandlers(domain);
      const parameters = { action: "new" };
      const grant = await approvalPolicy.requestApproval({ toolName: "civil3d_drawing", action: "new", ...domain.actions.new }, parameters);
      expect(grant.drawingFingerprint).toBe("no-active-drawing");
      registry.read.mockResolvedValue({ hasDiscoveryFiles: true, instances: [instances[1]] });
      setAttachedInstance(instances[1]);
      requests.length = 0;
      const reply = await getToolHandler("civil3d_drawing")!({ ...parameters, approvalToken: grant.approvalToken });
      expect(reply.isError).toBe(true);
      expect(JSON.stringify(reply)).toContain("instance changed after approval");
      expect(requests).toEqual([]);
    } finally { await Promise.all(servers.map(server => new Promise<void>(resolve => server.close(() => resolve())))); }
  });

  it("marks undiscovered routing legacy-only unless fixed-instance access is authorized", async () => {
    const requests: any[] = [];
    const server = net.createServer(socket => socket.once("data", data => {
      const request = JSON.parse(data.toString()); requests.push(request);
      socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: "old plugin accepts unknown fields" }));
    }));
    await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
    try {
      vi.stubEnv("CIVIL3D_PORT", undefined);
      registry.read.mockResolvedValue({ hasDiscoveryFiles: false, instances: [] });
      const { resolveInstanceEndpoint, withPinnedEndpoint } = await import("../src/utils/instanceSession.js");
      const { withApplicationConnection } = await import("../src/utils/ConnectionManager.js");
      const endpoint = await resolveInstanceEndpoint();
      expect(endpoint.legacyFallback).toBe(true);
      // Redirect only the destination port to the fake old/new listener; keep the snapshot marker.
      const port = (server.address() as net.AddressInfo).port;
      await withPinnedEndpoint({ ...endpoint, port }, () => withApplicationConnection(c => c.sendCommand("createText", {})));
      expect(requests[0].legacyFallback).toBe(true);
      expect(requests[0].instanceIdentity).toBeUndefined();
      vi.stubEnv("CIVIL3D_PORT", String(port)); vi.resetModules();
      vi.stubEnv("CIVIL3D_ALLOW_FIXED_INSTANCE", undefined);
      const fixed = await import("../src/utils/ConnectionManager.js");
      await fixed.withApplicationConnection(c => c.sendCommand("createText", {}));
      expect(requests[1].legacyFallback).toBe(true);
      expect(requests[1].instanceIdentity).toBeUndefined();
      vi.stubEnv("CIVIL3D_ALLOW_FIXED_INSTANCE", "true");
      await fixed.withApplicationConnection(c => c.sendCommand("createText", {}));
      expect(requests[2].legacyFallback).toBeUndefined();
    } finally { await new Promise<void>(resolve => server.close(() => resolve())); }
  });

  it.each([0, 50])("pins direct client-approved run_code before progress %s can change attachment", async progress => {
    vi.stubEnv("CIVIL3D_PORT", undefined);
    vi.stubEnv("CIVIL3D_RUN_CODE_APPROVAL", "client");
    const temp = await mkdtemp(join(tmpdir(), "instance-client-routing-"));
    const requests: { instance: number; method: string; token: string }[] = [];
    const servers = [0, 1].map(instance => net.createServer(socket => socket.once("data", data => {
      const request = JSON.parse(data.toString());
      requests.push({ instance, method: request.method, token: request.params.authToken });
      socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: {
        runId: "routing-check", mode: "write", status: "ok", ok: true,
        timing: { compileMs: 0, runMs: 1, totalMs: 1 }, logFolder: temp,
      } }));
    })));
    await Promise.all(servers.map(server => new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve))));
    try {
      const { sessionId, setAttachedInstance, attachedInstance } = await import("../src/utils/instanceSession.js");
      const instances = servers.map((server, i) => ({ instanceApi: 1 as const, pid: 101 + i,
        port: (server.address() as net.AddressInfo).port, tokenPath: join(temp, `token-${i}`),
        startedAtUtc: `2026-10-15T12:00:0${i}Z`, updatedAtUtc: new Date().toISOString(), acadVersion: "25.1",
        owner: "agent" as const, ownerSession: sessionId, claimId: `claim-${i}`, documents: [], responsive: true }));
      await Promise.all(instances.map((instance, i) => writeFile(instance.tokenPath, `token-${i}`)));
      registry.read.mockResolvedValue({ hasDiscoveryFiles: true, instances });
      const { registerDomainTools } = await import("../src/tools/domainRuntime.js");
      const { CODE_DOMAIN_DEFINITION } = await import("../src/tools/domains/codeDomain.js");
      let handler: any;
      registerDomainTools({ registerTool: (name: string, _config: unknown, callback: unknown) => {
        if (name === "civil3d_run_code") handler = callback;
      } } as any, CODE_DOMAIN_DEFINITION);
      setAttachedInstance(instances[0]);
      const reply = await handler({ code: "return 1;", mode: "write" }, {
        _meta: { progressToken: "client-write" }, sendNotification: async (notification: any) => {
          if (notification.params.progress === progress) setAttachedInstance(instances[1]);
        },
      });
      expect(reply.isError).not.toBe(true);
      expect(requests).toEqual([{ instance: 0, method: "runCode", token: "token-0" }]);
      expect(attachedInstance()?.pid).toBe(instances[1].pid);
      expect(registry.read).toHaveBeenCalledWith({ pid: 101 });
    } finally {
      await Promise.all(servers.map(server => new Promise<void>(resolve => server.close(() => resolve()))));
      await rm(temp, { recursive: true, force: true });
    }
  });

  it("re-adopts a sole same-label orphan through the production authenticated transport", async () => {
    vi.stubEnv("CIVIL3D_PORT", undefined);
    vi.stubEnv("CIVIL3D_SESSION_LABEL", "Job A");
    const temp = await mkdtemp(join(tmpdir(), "instance-adoption-routing-"));
    const requests: any[] = [];
    const server = net.createServer(socket => socket.once("data", data => {
      const request = JSON.parse(data.toString()); requests.push(request);
      socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { adopted: true } }));
    }));
    await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
    try {
      const { sessionId, resolveInstanceEndpoint, attachedInstance } = await import("../src/utils/instanceSession.js");
      const tokenPath = join(temp, "token");
      await writeFile(tokenPath, "adoption-token");
      const orphan = { instanceApi: 1 as const, pid: 101, port: (server.address() as net.AddressInfo).port, tokenPath,
        startedAtUtc: "2026-10-15T12:00:00Z", updatedAtUtc: new Date().toISOString(), acadVersion: "25.1",
        owner: "agent" as const, ownerSession: "dead-session", ownerLabel: "Job A", ownerServerPid: 999,
        ownerServerStartedAtUtc: "2026-10-14T12:00:00Z", orphan: true, claimId: "claim", documents: [], responsive: true };
      registry.read.mockResolvedValue({ hasDiscoveryFiles: true, instances: [orphan] });
      expect(await resolveInstanceEndpoint()).toMatchObject({ pid: 101, ownerSession: sessionId, orphan: false });
      expect(attachedInstance()?.ownerSession).toBe(sessionId);
      expect(requests).toHaveLength(1);
      expect(requests[0]).toMatchObject({ method: "adoptInstance",
        instanceIdentity: { pid: orphan.pid, startedAtUtc: orphan.startedAtUtc },
        params: { authToken: "adoption-token", ownerSession: sessionId, label: "Job A", serverPid: process.pid,
          serverStartedAtUtc: expect.any(String), expectedOwnerSession: "dead-session",
          expectedOwnerServerPid: 999, expectedOwnerServerStartedAtUtc: orphan.ownerServerStartedAtUtc } });
    } finally {
      await new Promise<void>(resolve => server.close(() => resolve()));
      await rm(temp, { recursive: true, force: true });
    }
  }, 20_000);
  it.each(["before validation", "during progress", "replacement during progress"])("binds stop approval to its resolved identity: %s", async when => {
    vi.stubEnv("CIVIL3D_PORT", undefined);
    const { sessionId, setAttachedInstance } = await import("../src/utils/instanceSession.js");
    const instances = [101, 102].map(pid => ({ instanceApi: 1 as const, pid, port: 8757 + pid - 101,
      tokenPath: "unused", startedAtUtc: new Date().toISOString(), updatedAtUtc: new Date().toISOString(),
      acadVersion: "25.1", owner: "agent" as const, ownerSession: sessionId, claimId: `claim-${pid}`, documents: [], responsive: true }));
    registry.read.mockResolvedValue({ hasDiscoveryFiles: true, instances });
    setAttachedInstance(instances[0]);
    const { defaultInstanceDependencies } = await import("../src/utils/instanceManager.js");
    const quit = vi.spyOn(defaultInstanceDependencies, "quit").mockResolvedValue({});
    vi.spyOn(defaultInstanceDependencies, "alive").mockReturnValue(false);
    const { approvalPolicy } = await import("../src/tools/approvalPolicy.js");
    const { INSTANCE_DOMAIN_DEFINITION: domain } = await import("../src/tools/domains/instanceDomain.js");
    const { captureDomainToolHandlers } = await import("../src/tools/domainRuntime.js");
    const { getToolHandler } = await import("../src/tools/toolHandlerRegistry.js");
    captureDomainToolHandlers(domain);
    const parameters = { action: "stop", discardChanges: true };
    const prepared = await domain.actions.stop.prepare!(parameters, parameters);
    const grant = await approvalPolicy.requestApproval({ toolName: "civil3d_instance", action: "stop", ...domain.actions.stop }, prepared.approvalParameters);
    if (when === "before validation") setAttachedInstance(instances[1]);
    const reply = await getToolHandler("civil3d_instance")!({ ...parameters, approvalToken: grant.approvalToken }, {
      _meta: { progressToken: "stop" }, sendNotification: async (notification: any) => {
        if (notification.params.progress === 50) {
          if (when === "replacement during progress") instances[0] = { ...instances[0], startedAtUtc: "2099-01-01T00:00:00Z" };
          else setAttachedInstance(instances[1]);
        }
      },
    });
    if (when === "during progress") {
      expect(reply.isError).not.toBe(true);
      expect(quit).toHaveBeenCalledWith(instances[0], true);
    } else {
      expect(reply.isError).toBe(true);
      expect(quit).not.toHaveBeenCalled();
    }
  });
  it("routes a composite call to its pinned port and token even if attachment changes", async () => {
    const temp = await mkdtemp(join(tmpdir(), "instance-routing-"));
    const requests: any[] = [];
    const server = net.createServer(socket => socket.once("data", data => {
      const request = JSON.parse(data.toString()); requests.push(request);
      socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: request.params }));
    }));
    await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
    try {
      vi.stubEnv("CIVIL3D_PORT", undefined);
      const { sessionId, setAttachedInstance } = await import("../src/utils/instanceSession.js");
      const { withApplicationConnection } = await import("../src/utils/ConnectionManager.js");
      const { readPluginToken } = await import("../src/utils/pluginAuth.js");
      const instance = { instanceApi: 1 as const, pid: 101, port: (server.address() as net.AddressInfo).port,
        tokenPath: join(temp, "rpc-token"), startedAtUtc: new Date().toISOString(), updatedAtUtc: new Date().toISOString(),
        acadVersion: "25.1", owner: "agent" as const, ownerSession: sessionId, claimId: "claim", documents: [], responsive: true };
      await writeFile(instance.tokenPath, "selected-token");
      registry.read.mockResolvedValue({ hasDiscoveryFiles: true, instances: [instance] });
      setAttachedInstance(instance);
      await withApplicationConnection(async client => {
        setAttachedInstance({ ...instance, pid: 102, port: 1, tokenPath: "does-not-exist" });
        await client.sendCommand("runCode", { authToken: readPluginToken() });
        await client.sendCommand("next", { authToken: readPluginToken() });
      });
      expect(requests.map(r => r.params.authToken)).toEqual(["selected-token", "selected-token"]);
      expect(requests.map(r => r.instanceIdentity)).toEqual([
        { pid: instance.pid, startedAtUtc: instance.startedAtUtc },
        { pid: instance.pid, startedAtUtc: instance.startedAtUtc },
      ]);
    } finally {
      await new Promise<void>(resolve => server.close(() => resolve()));
      await rm(temp, { recursive: true, force: true });
    }
  });

  it("pins approval validation and execution through a concurrent attach during progress", async () => {
    vi.stubEnv("CIVIL3D_PORT", undefined);
    const requests: { port: number; method: string }[] = [];
    const servers = [0, 1].map(() => net.createServer(socket => socket.once("data", data => {
      const request = JSON.parse(data.toString());
      const port = (socket.address() as net.AddressInfo).port;
      requests.push({ port, method: request.method });
      socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id,
        result: request.method === "getCivil3DHealth" ? { drawingLoaded: true }
          : request.method === "saveDrawing" ? { saved: true, filePath: `drawing-${port}.dwg` }
          : { fileName: `drawing-${port}.dwg` } }));
    })));
    await Promise.all(servers.map(server => new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve))));
    try {
      const { sessionId, setAttachedInstance, attachedInstance } = await import("../src/utils/instanceSession.js");
      const instances = servers.map((server, i) => ({ instanceApi: 1 as const, pid: 101 + i,
        port: (server.address() as net.AddressInfo).port, tokenPath: "unused",
        startedAtUtc: new Date().toISOString(), updatedAtUtc: new Date().toISOString(), acadVersion: "25.1",
        owner: "agent" as const, ownerSession: sessionId, claimId: `claim-${i}`, documents: [], responsive: true }));
      registry.read.mockResolvedValue({ hasDiscoveryFiles: true, instances });
      setAttachedInstance(instances[0]);
      const { approvalPolicy } = await import("../src/tools/approvalPolicy.js");
      const { captureDomainToolHandlers } = await import("../src/tools/domainRuntime.js");
      const { DRAWING_RUNTIME_DOMAIN_DEFINITION: drawing } = await import("../src/tools/domains/drawingRuntimeDomain.js");
      const { INSTANCE_DOMAIN_DEFINITION: instance } = await import("../src/tools/domains/instanceDomain.js");
      const { getToolHandler } = await import("../src/tools/toolHandlerRegistry.js");
      captureDomainToolHandlers(drawing); captureDomainToolHandlers(instance);
      const parameters = { action: "save" };
      const grant = await approvalPolicy.requestApproval({ toolName: "civil3d_drawing", action: "save", ...drawing.actions.save }, parameters);
      requests.length = 0;
      const reply = await getToolHandler("civil3d_drawing")!({ ...parameters, approvalToken: grant.approvalToken }, {
        _meta: { progressToken: "test" }, sendNotification: async (notification: any) => {
          if (notification.params.progress === 50) {
            const attached = await getToolHandler("civil3d_instance")!({ action: "attach", pid: instances[1].pid });
            expect(attached.isError).not.toBe(true);
          }
        },
      });
      expect(reply.isError).not.toBe(true);
      expect(attachedInstance()?.pid).toBe(instances[1].pid);
      expect(requests).toEqual(["getCivil3DHealth", "getDrawingInfo", "saveDrawing"].map(method => ({ port: instances[0].port, method })));
    } finally { await Promise.all(servers.map(server => new Promise<void>(resolve => server.close(() => resolve())))); }
  });

  it("refuses calls to a user instance before any TCP command or token read", async () => {
    vi.stubEnv("CIVIL3D_PORT", undefined);
    registry.read.mockResolvedValue({ hasDiscoveryFiles: true, instances: [{ pid: 9, owner: "user", documents: ["C:\\job.dwg"] }] });
    const { withApplicationConnection } = await import("../src/utils/ConnectionManager.js");
    const operation = vi.fn();
    await expect(withApplicationConnection(operation)).rejects.toThrow("userInstance: true");
    expect(operation).not.toHaveBeenCalled();
    const { PLUGIN_DOMAIN_DEFINITION } = await import("../src/tools/domains/pluginDomain.js");
    const health = await PLUGIN_DOMAIN_DEFINITION.actions.health.execute({});
    expect(health).toMatchObject({ connected: false, status: "not attached",
      instances: [expect.objectContaining({ pid: 9, owner: "user" })] });
    expect(PLUGIN_DOMAIN_DEFINITION.actions.health.responseSchema!.safeParse(health).success).toBe(true);
  });

  it("does not fall back to legacy routing after the attached process disappears", async () => {
    vi.stubEnv("CIVIL3D_PORT", undefined);
    const { setAttachedInstance } = await import("../src/utils/instanceSession.js");
    setAttachedInstance({ pid: 123, startedAtUtc: "2026-10-15T12:00:00Z" } as any);
    registry.read.mockResolvedValue({ hasDiscoveryFiles: false, instances: [] });
    const { withApplicationConnection } = await import("../src/utils/ConnectionManager.js");
    const operation = vi.fn();
    await expect(withApplicationConnection(operation)).rejects.toThrow("no longer responsive");
    expect(operation).not.toHaveBeenCalled();
  });

  it("keeps explicitly configured fixed routing and the legacy default without discovery", async () => {
    vi.stubEnv("CIVIL3D_PORT", "8759");
    registry.read.mockResolvedValue({ hasDiscoveryFiles: true, instances: [] });
    const fixed = await import("../src/utils/instanceSession.js");
    expect(await fixed.resolveInstanceEndpoint()).toMatchObject({ port: 8759, tokenPath: expect.stringContaining("rpc-token-8759") });
    vi.stubEnv("CIVIL3D_PORT", undefined); vi.resetModules();
    registry.read.mockResolvedValue({ hasDiscoveryFiles: false, instances: [] });
    const legacy = await import("../src/utils/instanceSession.js");
    expect(await legacy.resolveInstanceEndpoint()).toMatchObject({ port: 8757, tokenPath: expect.stringMatching(/rpc-token$/) });
  });
});
