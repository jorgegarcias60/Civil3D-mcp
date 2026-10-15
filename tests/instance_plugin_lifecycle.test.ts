import { readFileSync } from "node:fs";
import { expect, it } from "vitest";

const runtime = readFileSync(new URL("../Civil3D-MCP-Plugin/PluginRuntime.cs", import.meta.url), "utf8");
const quit = readFileSync(new URL("../Civil3D-MCP-Plugin/InstanceCommands.cs", import.meta.url), "utf8");

it("wires database saves to a guarded deferred refresh and removes subscriptions", () => {
  expect(runtime).toContain("foreach (var document in openDocuments) AttachSaveHandler(document.Database)");
  expect(runtime).toMatch(/DatabaseIOEventHandler handler =[\s\S]*?try \{ ScheduleDiscoveryRefresh\(\); \}[\s\S]*?catch \(Exception error\)/);
  expect(runtime).toContain("database.SaveComplete += handler");
  expect(runtime).toContain("foreach (var (database, handler) in SaveHandlers) database.SaveComplete -= handler");
  expect(runtime).toContain("DetachSaveHandler(args.Document.Database)");
  expect(runtime).toContain("database.SaveComplete -= handler");
  expect(runtime).toContain("ReferenceEquals(_discovery, scheduledFor)) return");
  expect(runtime).toContain("if (!scheduledFor.OwnershipFinalized &&");
  expect(runtime).toContain("ScheduleDiscoveryRefresh();");
  expect(runtime).toContain("PendingDiscoveryRefreshes.Count != 0");
  expect(runtime).toContain(".Select(doc => doc.Database.Filename)");
});

it("uses the tested port binder and restores process ownership when restarting", () => {
  expect(runtime).toMatch(/Port = InstancePortBinder.Bind\(port =>[\s\S]*?new RpcTcpServer\(port,[\s\S]*?candidate.Start\(\)/);
  expect(runtime).toContain("_ownership = _discovery.Ownership");
  expect(runtime).toContain("new InstanceDiscovery(Environment.ProcessId, Port, ownership: _ownership)");
});

it("checks supplied instance identity before dispatching any command", () => {
  const guard = runtime.indexOf('_discovery?.AllowsRequestIdentity(request["instanceIdentity"])');
  const dispatch = runtime.indexOf("await CommandDispatcher.DispatchAsync");
  expect(guard).toBeGreaterThan(0);
  expect(guard).toBeLessThan(dispatch);
  expect(runtime.slice(guard, dispatch)).toContain("!= true");
  expect(runtime.slice(guard, dispatch)).toContain('"CIVIL3D.STALE_INSTANCE"');
});

it("rejects automatic legacy fallback before dispatch even without identity", () => {
  const fallback = runtime.indexOf('request["legacyFallback"] is JsonValue fallbackValue');
  const identity = runtime.indexOf('_discovery?.AllowsRequestIdentity(request["instanceIdentity"])');
  const dispatch = runtime.indexOf("await CommandDispatcher.DispatchAsync");
  expect(fallback).toBeGreaterThan(0);
  expect(fallback).toBeLessThan(identity);
  expect(identity).toBeLessThan(dispatch);
  expect(runtime.slice(fallback, identity)).toContain("legacyFallback)");
  expect(runtime.slice(fallback, identity)).toContain('"CIVIL3D.STALE_INSTANCE"');
});

it("revokes credentials and pending quit before releasing the listener", () => {
  const stop = runtime.slice(runtime.indexOf("public static void StopServer()"), runtime.indexOf("public static string Owner"));
  expect(stop.indexOf("InstanceCommands.CancelPendingQuit()")).toBeLessThan(stop.indexOf("_server?.Stop()"));
  expect(stop.indexOf("RpcAuth.Revoke()")).toBeLessThan(stop.indexOf("_server?.Stop()"));
});

it("wakes and bounds both Idle stages of quit", () => {
  expect(quit).toContain("deadline.CancelAfter(InspectionTimeout)");
  expect(quit).toContain("CoreApp.Idle -= idle");
  expect(quit).toContain("var wake = StartIdleWake()");
  expect(quit).toContain("DeferredQuitTimeout");
  expect(quit).toContain("_pendingQuitWake = StartIdleWake(() =>");
  expect(quit).toContain("PostMessage(mainWindow, 0 /* WM_NULL */");
  expect(quit).toContain("if (_state != QuitState.Scheduled || _requestId != responseId) return;");
  expect(quit).toContain("if (_pendingQuitDeadline?.IsExpired() != false)");
  expect(quit).toContain("ResetPendingQuitLocked()");
  expect(quit).toContain("_state = QuitState.Executing");
});
