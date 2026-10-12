import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const source = readFileSync(
  fileURLToPath(new URL("../Civil3D-MCP-Plugin/RunCodeScript.cs", import.meta.url)),
  "utf8",
).replaceAll("\r\n", "\n");
const executionSource = readFileSync(
  fileURLToPath(new URL("../Civil3D-MCP-Plugin/CivilExecution.cs", import.meta.url)),
  "utf8",
).replaceAll("\r\n", "\n");
const commandsSource = readFileSync(
  fileURLToPath(new URL("../Civil3D-MCP-Plugin/RunCodeCommands.cs", import.meta.url)),
  "utf8",
).replaceAll("\r\n", "\n");
const dispatchSource = readFileSync(
  fileURLToPath(new URL("../Civil3D-MCP-Plugin/HostDispatchState.cs", import.meta.url)),
  "utf8",
).replaceAll("\r\n", "\n");
const jsonSource = readFileSync(
  fileURLToPath(new URL("../Civil3D-MCP-Plugin/RunCodeJson.cs", import.meta.url)),
  "utf8",
).replaceAll("\r\n", "\n");
const compareSource = readFileSync(
  fileURLToPath(new URL("../Civil3D-MCP-Plugin/CompareCommands.cs", import.meta.url)),
  "utf8",
).replaceAll("\r\n", "\n").replace(/^[\t ]*\/\/.*$/gm, "");

describe("OpenSide retry ownership", () => {
  it("opens each attempt in a new Database and disposes a failed attempt", () => {
    expect(source).toContain("ReadSideOnce(fullPath, FileOpenMode.OpenForReadAndAllShare)");
    expect(source).toContain("ReadSideOnce(fullPath, FileOpenMode.OpenTryForReadShare)");
    const helper = source.slice(source.indexOf("private static Database ReadSideOnce("), source.indexOf("/// <summary>The run's transaction"));
    expect(helper).toContain("var side = new Database(false, true);");
    expect(helper).toContain("side.ReadDwgFile(path, mode, true, string.Empty);");
    expect(helper).toContain("side.CloseInput(true);");
    expect(helper).toMatch(/catch\s*\{\s*side\.Dispose\(\);\s*throw;/);
  });

  it("does not retry drawing-content failures and preserves the first error when both attempts fail", () => {
    expect(source).toContain("catch (Exception first) when (first is not RunCodeTimeoutException and not OperationCanceledException\n      && !RunCodeHelpers.IsDrawingContentError((first as AcRuntimeException)?.ErrorStatus.ToString()))");
    expect(source).toContain("ExceptionDispatchInfo.Capture(first).Throw()");
  });
});

describe("drawing comparison side-database retry ownership (#24)", () => {
  const helperStart = compareSource.indexOf("private static Database ReadSideOnce(");
  const captureEnd = compareSource.indexOf("internal static DrawingFingerprint Capture(");
  const capture = compareSource.slice(
    compareSource.indexOf("private static DrawingFingerprint CaptureSideDatabase("),
    helperStart < 0 ? captureEnd : helperStart,
  );
  const helper = helperStart < 0 ? "" : compareSource.slice(helperStart, captureEnd);

  it("opens each attempt in a fresh database and disposes read or CloseInput failures", () => {
    expect(capture.match(/ReadSideOnce\(path, FileOpenMode\./g) ?? []).toHaveLength(2);
    expect(capture).toContain("ReadSideOnce(path, FileOpenMode.OpenForReadAndAllShare)");
    expect(capture).toContain("ReadSideOnce(path, FileOpenMode.OpenTryForReadShare)");
    expect(capture).not.toContain(".ReadDwgFile(");
    expect(helper).toMatch(/var side = new Database\(false, true\);\s*try\s*\{/);
    expect(helper).toMatch(/try\s*\{\s*side\.ReadDwgFile\(path, mode, true, string\.Empty\);\s*side\.CloseInput\(true\);\s*return side;\s*\}/);
    expect(helper).toMatch(/catch\s*\{\s*side\.Dispose\(\);\s*throw;/);
  });

  it("retries only non-content errors once and reports the first failure if both attempts fail", () => {
    expect(capture).toContain("catch (Exception first) when (!RunCodeHelpers.IsDrawingContentError((first as AcRuntimeException)?.ErrorStatus.ToString()))");
    expect(capture).toMatch(/try\s*\{\s*side = ReadSideOnce\(path, FileOpenMode\.OpenTryForReadShare\);\s*\}\s*catch\s*\{\s*ExceptionDispatchInfo\.Capture\(first\)\.Throw\(\);\s*throw;/);
    expect(capture).toMatch(/catch \(Exception exception\)\s*\{\s*throw new JsonRpcDispatchException\("CIVIL3D\.FILE_IO_ERROR",[^;]*exception\.Message/);
  });

  it("disposes the successful database after fingerprint capture", () => {
    expect(capture).toMatch(/using \(side\)\s*\{[\s\S]*using var transaction = side\.TransactionManager\.StartOpenCloseTransaction\(\);[\s\S]*var fingerprint = Capture\(side,[\s\S]*return fingerprint;\s*\}/);
  });
});

describe("run-code retention scheduling (#25)", () => {
  it("keeps seven-day output pruning and passes the current folder to whole-folder cleanup", () => {
    expect(commandsSource).toContain("OutputRetention = TimeSpan.FromDays(7)");
    expect(commandsSource).toMatch(/if \(Interlocked\.Exchange\(ref _pruned, 1\) == 0\)\s*\{\s*_ = Task\.Run\(\(\) =>\s*\{\s*RunCodeLog\.PruneOldOutputs\(OutputRetention\);\s*RunCodeLog\.PruneOldRunFolders\(runFolder\);/);
  });
});

describe("run-code streamed result integration (#12)", () => {
  it("serializes into the confined result stream while host objects are still open", () => {
    const execution = commandsSource.slice(
      commandsSource.indexOf("var value = script.Run();"),
      commandsSource.indexOf("private static bool RestoreRunTransaction("),
    );
    expect(jsonSource).not.toContain("SerializeToUtf8Bytes");
    expect(jsonSource).toContain("new StreamBufferWriter(stream)");
    expect(jsonSource).toContain("JsonSerializer.Serialize(writer,");
    expect(execution).toContain("RunCodeResult.Save(runFolder, stream =>");
    expect(execution.indexOf("RunCodeResult.Save(")).toBeLessThan(execution.indexOf("if (script.Tripped != null)"));
    expect(execution.indexOf("RunCodeResult.Save(")).toBeLessThan(execution.indexOf("transaction!.Commit();"));
    expect(execution.indexOf("RunCodeResult.Save(")).toBeLessThan(execution.indexOf("script.ReleaseOwned();"));
  });

  it("retains file metadata and excludes an incomplete result from output copies", () => {
    expect(commandsSource).not.toContain("byte[]? Result");
    expect(commandsSource).toContain("RunCodeResult? Result");
    expect(commandsSource).toContain("includeResult: outcome.Result?.FilePath != null");
    expect(commandsSource).toContain("outcome.Result?.AddTo(response, maxInlineBytes, PreviewChars)");
  });
});

describe("PR4 targeted write host route", () => {
  const runCodeRoute = executionSource.slice(
    executionSource.indexOf("public static async Task<T> ExecuteRunCodeAsync<T>"),
    executionSource.indexOf("private static async Task<bool> SwitchBackAsync"),
  );

  it("keeps an untargeted write on the existing command route", () => {
    expect(runCodeRoute).toContain("document == null && App.DocumentManager.MdiActiveDocument != null");
    expect(runCodeRoute).toContain("write: false, inCommand: writeMode");
  });

  it("activates the target before the command and restores the previous drawing inside the gate", () => {
    expect(runCodeRoute).toContain("return await ExecuteSerializedAsync(async cancellationToken =>");
    expect(runCodeRoute).toContain("App.DocumentManager.MdiActiveDocument = target;");
    expect(runCodeRoute).toContain("targetDocument: target");
    expect(runCodeRoute).toMatch(/finally\s*\{[\s\S]*activation\.SwitchedBack = await SwitchBackAsync\(previous\)/);
    expect(runCodeRoute.indexOf("App.DocumentManager.MdiActiveDocument = target;")).toBeLessThan(runCodeRoute.indexOf("targetDocument: target"));
    expect(runCodeRoute.indexOf("targetDocument: target")).toBeLessThan(runCodeRoute.indexOf("finally\n      {"));
  });

  it("sends the plugin command to the selected document and checks its identity", () => {
    expect(executionSource).toContain("StartPluginCommandAsync(commandDocument,");
    expect(executionSource).toContain("private static async Task StartPluginCommandAsync(Document doc,");
    expect(executionSource).toContain("Document? targetDocument = null");
    expect(executionSource).toContain("App.DocumentManager.MdiActiveDocument != targetDocument");
    const hostAction = executionSource.slice(executionSource.indexOf("private static async Task<T> ExecuteOnHostAsync<T>"));
    expect(hostAction.indexOf("App.DocumentManager.MdiActiveDocument != targetDocument")).toBeLessThan(hostAction.indexOf("doc.LockDocument()"));
    expect(hostAction).toContain('"the active drawing changed before the write; nothing changed"');
  });

  it("keeps restoration independent of request cancellation and waits up to 30 seconds for a quiet host", () => {
    const switchBack = executionSource.slice(
      executionSource.indexOf("private static async Task<bool> SwitchBackAsync"),
      executionSource.indexOf("private static async Task<T> ExecuteOnHostAsync<T>"),
    );
    expect(switchBack).toContain("new CancellationTokenSource(TimeSpan.FromSeconds(30))");
    expect(switchBack).toContain("App.DocumentManager.Cast<Document>().Contains(previous)");
    expect(switchBack).toContain("App.DocumentManager.MdiActiveDocument = previous;");
    expect(switchBack).toContain("needsCommand: false, needsQuietEditor: true, cleanup.Token");
    expect(switchBack).toContain("busyTimeout: TimeSpan.FromSeconds(30), drainOnCancellation: true");
    expect(switchBack).toMatch(/catch \(Exception error\)[\s\S]*PluginLog.Warn/);
    expect(runCodeRoute).toContain('activation.AddWarning($"could not switch back to {previousName}")');
  });

  it("atomically abandons unstarted work and drains both the operation and COM dispatch before restoration", () => {
    expect(executionSource).toContain("targetDocument: targetDocument, drainOnCancellation: targetDocument != null");
    expect(executionSource).toContain("if (targetDocument != null) await hostTask;");
    expect(executionSource).toMatch(/catch \(OperationCanceledException\) when \(drainOnCancellation\)[\s\S]*await dispatch\.DrainAsync\(TimeSpan\.FromSeconds\(30\)/);
    expect(executionSource).toMatch(/if \(!dispatch\.TryStart\(\)\)\s*\{\s*return;\s*\}\s*PendingCommandLine.Clear\(\)/);
    expect(executionSource).toContain("dispatch.TryDispatch(() => StartPluginCommandAsync(commandDocument,");
    expect(executionSource).toContain("PluginLog.Warn(\"Host\", warning);");
    expect(executionSource).toContain("dispatchWarning: activation == null ? null : activation.AddWarning");
    expect(executionSource.match(/HostExecutionGate\.Release\(\)/g)).toHaveLength(1);
    expect(dispatchSource).toContain("Interlocked.CompareExchange(ref state, Started, NotStarted)");
    expect(dispatchSource).toContain("Interlocked.CompareExchange(ref state, Abandoned, NotStarted)");
    expect(dispatchSource).toContain("if (!abandoned) await operationDone.Task;");
    expect(dispatchSource).toContain("await pendingDispatch.WaitAsync(dispatchTimeout);");
    expect(dispatchSource).toContain("late callbacks will not run.");
  });

  it("reports activation and restoration in both the response and run.json, with restoration failures as warnings", () => {
    expect(commandsSource).toContain("internal const int ApiVersion = 5;");
    expect(commandsSource).toMatch(/finally\s*\{\s*if \(outcome.Activation.Activated\)/);
    for (const container of ["response", "summary"]) {
      expect(commandsSource).toContain(`${container}["activated"] = true;`);
      expect(commandsSource).toContain(`${container}["switchedBack"] = outcome.Activation.SwitchedBack;`);
    }
    expect(commandsSource).toContain("if (outcome.Activation.Warning != null) warnings.Add(outcome.Activation.Warning);");
    expect(commandsSource).toContain("}, outcome.Activation);");
  });
});
