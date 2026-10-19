import { existsSync, readFileSync } from "node:fs";
import { expect, it } from "vitest";

const runtime = readFileSync(new URL("../Civil3D-MCP-Plugin/PluginRuntime.cs", import.meta.url), "utf8");
const quit = readFileSync(new URL("../Civil3D-MCP-Plugin/InstanceCommands.cs", import.meta.url), "utf8");
const drawing = readFileSync(new URL("../Civil3D-MCP-Plugin/DrawingCommands.cs", import.meta.url), "utf8");
const workflow = readFileSync(new URL("../Civil3D-MCP-Plugin/WorkflowCommands.cs", import.meta.url), "utf8");
const flagsPath = new URL("../Civil3D-MCP-Plugin/DbmodFlags.cs", import.meta.url);
const flags = existsSync(flagsPath) ? readFileSync(flagsPath, "utf8") : "";

// A member's source, from its signature to its closing brace at class level.
function member(source: string, signature: string): string {
  const start = source.indexOf(signature);
  if (start < 0) return "";
  const end = source.slice(start).search(/\r?\n  \}\r?\n/);
  return end < 0 ? "" : source.slice(start, start + end);
}

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

it("reads each drawing's DBMOD without switching the active drawing (#54)", () => {
  const read = member(quit, "internal static int ReadDbmod(Document document)");
  const fallback = read.indexOf("catch (Exception error)");
  expect(fallback).toBeGreaterThan(0);
  const normal = read.slice(0, fallback);
  expect(normal).toContain("if (ReferenceEquals(App.DocumentManager.MdiActiveDocument, document))");
  expect(normal).toContain('return Convert.ToInt32(App.GetSystemVariable("DBMOD"));');
  expect(normal).toContain('Civil3DCompatibility.InvokeComMethod(document.GetAcadDocument(), "GetVariable", "DBMOD")');
  expect(normal).not.toMatch(/MdiActiveDocument\s*=[^=]/);
  expect(read.slice(fallback)).toMatch(/PluginLog\.Warn\([\s\S]*?return ReadDbmodBySwitching\(document\);/);
  // Both checks go through ReadDbmod; nothing else switches drawings to read DBMOD.
  expect(quit).not.toContain("IsModified");
  expect(member(quit, "private static List<(string Name, int Value)> ModifiedDrawings()")).toContain("var dbmod = ReadDbmod(document);");
  expect(quit.match(/MdiActiveDocument = document;/g)).toHaveLength(1);
  expect(member(quit, "private static int ReadDbmodBySwitching(Document document)")).toContain("App.DocumentManager.MdiActiveDocument = document;");
});

it("skips the unsaved check when discarding and lists each drawing's DBMOD (#54)", () => {
  const quitInstance = member(quit, "internal static Task<object?> QuitInstanceAsync(");
  const inspect = quitInstance.slice(quitInstance.indexOf("deadline.Token.ThrowIfCancellationRequested();"), quitInstance.indexOf("lock (Sync)"));
  expect(inspect).toMatch(/^deadline\.Token\.ThrowIfCancellationRequested\(\);\s*if \(!discard\)\s*\{\s*var unsaved = ModifiedDrawings\(\);/);
  expect(inspect).toContain('throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", DbmodFlags.UnsavedDrawingsMessage(unsaved));');
  expect(inspect).not.toContain("App.DocumentManager.Cast<Document>()");

  const deferred = member(quit, "internal static void AfterResponse(");
  expect(deferred).toMatch(/if \(!discard\)\s*\{\s*var changed = ModifiedDrawings\(\);/);
  expect(deferred).toContain('PluginLog.Warn("Instance", $"Quit cancelled because a drawing changed after the reply: {DbmodFlags.FormatDrawings(changed)}.");');
  expect(deferred.indexOf("ModifiedDrawings()")).toBeLessThan(deferred.indexOf("doc.CloseAndDiscard()"));

  // The refusal keeps the words callers match (/unsaved/i, the discard hint).
  expect(flags).toContain('var message = $"Instance has unsaved drawings: {FormatDrawings(drawings)}.";');
  expect(flags).toContain('return message + " Pass discardChanges=true to close them without saving.";');
  expect(flags).toContain('$"{drawing.Name} (DBMOD {Format(drawing.Value)})"');
  expect(flags).toContain('" Only view, window or field flags are set: no object or drawing-variable changes."');
});

it("reports DBMOD in the save step, right after SaveAs (#54)", () => {
  const save = member(drawing, "public static Task<object?> SaveDrawingAsync(JsonObject? parameters)");
  const saveAs = save.indexOf("database.SaveAs(targetPath, true, DwgVersion.Current, database.SecurityParameters);");
  const read = save.indexOf("var dbmod = InstanceCommands.ReadDbmod(doc);");
  expect(saveAs).toBeGreaterThan(0);
  expect(read).toBeGreaterThan(saveAs);
  expect(save.indexOf('result["dbmodAfterSave"] = DbmodFlags.ToJson(dbmod);')).toBeGreaterThan(read);
  expect(save).toMatch(/if \(dbmod != 0\)\s*result\["warnings"\] = new List<string> \{ \$"The file was written, but Civil 3D still counts the drawing as modified \(DBMOD \{DbmodFlags\.Format\(dbmod\)\}\): civil3d_instance stop refuses it without discardChanges\." \};/);
  expect(save).toMatch(/catch \(Exception error\)\s*\{\s*result\["dbmodAfterSave"\] = null;\s*result\["warnings"\] = new List<string> \{ \$"The file was written, but its DBMOD could not be read afterwards: \{error\.Message\}" \};\s*\}\s*return result;\s*\}\);$/);

  expect(workflow.match(/AddSaveWarnings\(saveResult, warnings\);/g)).toHaveLength(2);
  expect(member(workflow, "private static void AddSaveWarnings(")).toContain('saveResult.TryGetValue("warnings", out var value) && value is IEnumerable<string> saveWarnings');
});
