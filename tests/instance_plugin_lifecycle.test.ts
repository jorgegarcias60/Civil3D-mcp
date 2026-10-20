import { existsSync, readFileSync } from "node:fs";
import { expect, it } from "vitest";

const runtime = readFileSync(new URL("../Civil3D-MCP-Plugin/PluginRuntime.cs", import.meta.url), "utf8");
const quit = readFileSync(new URL("../Civil3D-MCP-Plugin/InstanceCommands.cs", import.meta.url), "utf8");
const drawing = readFileSync(new URL("../Civil3D-MCP-Plugin/DrawingCommands.cs", import.meta.url), "utf8");
const workflow = readFileSync(new URL("../Civil3D-MCP-Plugin/WorkflowCommands.cs", import.meta.url), "utf8");
const execution = readFileSync(new URL("../Civil3D-MCP-Plugin/CivilExecution.cs", import.meta.url), "utf8");
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
  // DBMOD is a 16-bit short: every read path masks it unsigned.
  expect(normal).toContain('return DbmodFlags.FromSystemVariable(App.GetSystemVariable("DBMOD"));');
  expect(normal).toContain('return DbmodFlags.FromSystemVariable(Civil3DCompatibility.InvokeComMethod(document.GetAcadDocument(), "GetVariable", "DBMOD"));');
  expect(read).not.toContain("Convert.ToInt32(");
  expect(normal).not.toMatch(/MdiActiveDocument\s*=[^=]/);
  expect(read.slice(fallback)).toMatch(/PluginLog\.Warn\([\s\S]*?return ReadDbmodBySwitching\(document\);/);
  // Both checks go through ReadDbmod; nothing else switches drawings to read DBMOD.
  expect(quit).not.toContain("IsModified");
  expect(member(quit, "private static List<(string Name, int Value)> ModifiedDrawings()")).toContain("var dbmod = ReadDbmod(document);");
  expect(quit.match(/MdiActiveDocument = document;/g)).toHaveLength(1);
  const switching = member(quit, "private static int ReadDbmodBySwitching(Document document)");
  expect(switching).toContain("App.DocumentManager.MdiActiveDocument = document;");
  expect(switching).toContain('return DbmodFlags.FromSystemVariable(App.GetSystemVariable("DBMOD"));');
  expect(switching).not.toContain("Convert.ToInt32(");
  expect(flags).toContain("internal static int FromSystemVariable(object? value) => Convert.ToInt32(value) & 0xFFFF;");
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

it("reports DBMOD right after SaveAs and once the save command has ended (#54)", () => {
  const save = member(drawing, "public static async Task<object?> SaveDrawingAsync(JsonObject? parameters)");
  const write = save.indexOf("await CivilExecution.LockedThenAfterCommandAsync((doc, civilDoc, database) =>");
  const saveAs = save.indexOf("database.SaveAs(targetPath, true, DwgVersion.Current, database.SecurityParameters);");
  const inCommand = save.indexOf("try { afterSave = InstanceCommands.ReadDbmod(doc); }");
  const afterCommand = save.indexOf("}, InstanceCommands.ReadDbmod);");
  expect(write).toBeGreaterThan(0);
  expect(save).not.toContain("CivilExecution.WriteAsync");
  expect(saveAs).toBeGreaterThan(write);
  expect(inCommand).toBeGreaterThan(saveAs);
  expect(save).toContain("catch (Exception error) { afterSaveError = error.Message; }");
  expect(afterCommand).toBeGreaterThan(inCommand);
  const report = save.slice(afterCommand);
  expect(report).toContain('result["dbmodAfterSave"] = afterSave is int saved ? DbmodFlags.ToJson(saved) : null;');
  expect(report).toContain('result["dbmodAfterCommand"] = afterCommandError == null ? DbmodFlags.ToJson(afterCommand) : null;');
  expect(report).toMatch(/var warnings = DbmodFlags\.SaveWarnings\(afterSave, afterSaveError,\s*afterCommandError == null \? afterCommand : null, afterCommandError\?\.Message\);\s*if \(warnings\.Count > 0\) result\["warnings"\] = warnings;\s*return result;$/);

  // One hold of the host gate: the save in C3DMCPHOP under a document lock with NO transaction (committing one after
  // SaveAs set DBMOD 1 again, seen live 2026-10-19), then an Idle read once the editor is quiescent, without the
  // drawing-identity check (a Save As renames the drawing). Its failure is returned, never thrown over the written file.
  const helper = member(execution, "public static Task<(T Result, TAfter? After, Exception? AfterError)> LockedThenAfterCommandAsync<T, TAfter>(");
  expect(helper.match(/ExecuteSerializedAsync/g)).toHaveLength(1);
  expect(helper).not.toMatch(/StartTransaction|ExecuteOnHostAsync|write: true/);
  expect(helper).toContain("using var documentLock = doc.LockDocument();");
  const hostWrite = helper.indexOf("}, needsCommand: true, needsQuietEditor: false, cancellationToken);");
  const idleRead = helper.indexOf("}, needsCommand: false, needsQuietEditor: true, cancellationToken);");
  expect(hostWrite).toBeGreaterThan(0);
  expect(idleRead).toBeGreaterThan(hostWrite);
  expect(helper).toContain("if (written == null || !App.DocumentManager.Cast<Document>().Contains(written))");
  // The save itself checks the drawing identity, like every write; the read after it must not.
  expect(helper.slice(0, hostWrite)).toContain("RequireExpectedDocument(expectedDrawingIdentity");
  expect(helper.slice(hostWrite)).not.toContain("RequireExpectedDocument");
  expect(helper.slice(idleRead)).toMatch(/await AwaitHostContextAsync\(hostTask, cancellationToken\);\s*\}\s*catch \(Exception error\) \{ failure = error; \}\s*return \(result!, failure == null \? value : default, failure\);/);

  expect(workflow.match(/AddSaveWarnings\(saveResult, warnings\);/g)).toHaveLength(2);
  expect(member(workflow, "private static void AddSaveWarnings(")).toContain('saveResult.TryGetValue("warnings", out var value) && value is IEnumerable<string> saveWarnings');
});
