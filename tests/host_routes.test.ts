import { readdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const pluginDirectory = fileURLToPath(new URL("../Civil3D-MCP-Plugin/", import.meta.url));
const source = (name: string) => readFileSync(`${pluginDirectory}/${name}`, "utf8");
const withoutComments = (code: string) => code.replace(/\/\/[^\n]*/g, "");
const pluginFiles = readdirSync(pluginDirectory).filter((name) => name.endsWith(".cs"));

// Body of the lambda passed to the call that starts at `start`.
function lambdaBody(code: string, start: number): string {
  const arrow = code.indexOf("=>", start);
  let k = arrow + 2;
  while (/\s/.test(code[k])) k++;
  if (code[k] !== "{") return code.slice(k, code.indexOf(";", k));
  let depth = 0;
  for (let j = k; j < code.length; j++) {
    if (code[j] === "{") depth++;
    if (code[j] === "}" && --depth === 0) return code.slice(k, j);
  }
  return code.slice(k);
}

// Body of the method whose signature starts with `signature` (block or
// expression body).
function methodBody(code: string, signature: string): string {
  const start = code.indexOf(signature);
  if (start < 0) throw new Error(`missing: ${signature}`);
  let depth = signature.endsWith("(") ? 1 : 0;
  for (let j = start + signature.length; j < code.length; j++) {
    if (code[j] === "(") depth++;
    if (code[j] === ")") depth--;
    if (depth > 0) continue;
    if (code.startsWith("=>", j)) return code.slice(j, code.indexOf(";", j) + 1);
    if (code[j] === "{") {
      let braces = 0;
      for (let k = j; k < code.length; k++) {
        if (code[k] === "{") braces++;
        if (code[k] === "}" && --braces === 0) return code.slice(j, k + 1);
      }
    }
  }
  throw new Error(`no body: ${signature}`);
}

const count = (code: string, text: string) => code.split(text).length - 1;

// On Civil 3D 2027, in a disconnected Windows session, ExecuteInCommandContextAsync
// stopped running callbacks for the rest of the session once any COM client had run
// SendCommand (verified live; see CivilExecution.RunOnHostAsync). Host work therefore
// runs from an Application.Idle tick (reads) or inside the plugin's C3DMCPHOP command.
describe("host routes", () => {
  const execution = withoutComments(source("CivilExecution.cs"));
  const typedInput = withoutComments(source("CivilExecution.TypedInput.cs"));
  const decisions = withoutComments(source("HostInputRoute.cs"));

  it("never uses the command-context hop", () => {
    for (const name of pluginFiles) {
      expect(withoutComments(source(name)), name).not.toMatch(/DocumentManager\s*\.\s*ExecuteInCommandContextAsync/);
    }
  });

  it("reads from Application.Idle with an OpenCloseTransaction, which keeps the undo history", () => {
    expect(execution).toMatch(/route == HostRoute\.Idle\s*\?\s*database\.TransactionManager\.StartOpenCloseTransaction\(\)/);
    expect(execution).toContain("needsCommand: write || inCommand");
    expect(execution).toMatch(/if \(needsCommand\)\s*\{\s*var commandDocument = targetDocument \?\? activeDocument[\s\S]*?else if \(!dispatch\.TryDispatch\(\(\) => StartPluginCommandAsync\(commandDocument,/);
    expect(execution).toContain("await dispatch.DispatchDone;");
  });

  it("runs writes, command sequences and lock-only work inside the plugin's own command", () => {
    expect(execution.match(/needsCommand: true, needsQuietEditor: false/g)?.length).toBe(2);
    expect(execution).toContain('Civil3DCompatibility.InvokeComMethod(acadDocument, "SendCommand"');
    const entry = source("PluginEntry.cs");
    expect(entry).toContain("[CommandMethod(CivilExecution.PluginCommandName");
    // async void and awaited, so Editor.CommandAsync can run inside the command.
    expect(entry).toMatch(/public static async void HostHopCommand\(\)[\s\S]*await CivilExecution\.RunPluginCommandWorkAsync\(\)/);
  });

  it("fails fast while Civil 3D is busy instead of waiting for the request timeout", () => {
    expect(execution).toContain("HostBusyTimeout = TimeSpan.FromSeconds(15)");
    expect(execution).toContain('"CIVIL3D.HOST_BUSY"');
    expect(execution).toContain("Editor.IsQuiescent == false");
    const dispatch = withoutComments(source("HostDispatchState.cs"));
    expect(execution).toContain("dispatch.TryStart()");
    expect(execution).toContain("dispatch.TryAbandon()");
    expect(dispatch).toContain("Interlocked.CompareExchange(ref state, Started, NotStarted)");
    expect(dispatch).toContain("Interlocked.CompareExchange(ref state, Abandoned, NotStarted)");
  });

  // An OpenCloseTransaction is not rolled back on abort, and Idle ticks run
  // outside any command, so a plain read must not create scratch objects, use
  // the pickfirst selection or run commands. Those use ReadInCommandAsync or
  // ReadThenCommandLineAsync, and command-line input goes through
  // QueueCommandLine (sent after the operation through the operation's
  // channel), never SendStringToExecute from a command file: typed input
  // queued inside a COM-started C3DMCPHOP mixed with its input.
  it("keeps scratch objects, pickfirst and command-line input out of plain reads", () => {
    const scratch = ["GetVolumeProperties(", "ComputeVolumeBetweenSurfaces(", "GetEarthworkVolumes(", "TinVolumeSurface.Create("];
    const pickfirst = ["SetImpliedSelection(", "SelectImplied(", "BuildSelectedCivilObjects("];
    const commandLine = ["QueueCommandLine(", "SendCommand(doc"];
    const writes = ["UpgradeOpen(", "OpenMode.ForWrite", ".Commit("];
    const offenders: string[] = [];
    const counts: Record<string, number> = {};
    for (const name of pluginFiles.filter((file) => file !== "CivilExecution.cs" && file !== "CivilExecution.TypedInput.cs")) {
      const code = withoutComments(source(name));
      for (const match of code.matchAll(/CivilExecution\.(ReadAsync|ReadInCommandAsync|ReadThenCommandLineAsync)</g)) {
        const kind = match[1];
        counts[kind] = (counts[kind] ?? 0) + 1;
        const body = lambdaBody(code, match.index!);
        const has = (markers: string[]) => markers.filter((marker) => body.includes(marker));
        const bad =
          kind === "ReadAsync" ? [...has(scratch), ...has(pickfirst), ...has(commandLine), ...has(writes)]
          : kind === "ReadThenCommandLineAsync" ? [...has(scratch), ...has(pickfirst), ...has(writes)]
          : has(writes);
        if (bad.length > 0) offenders.push(`${name} ${kind}: ${bad.join(", ")}`);
      }
      if (code.includes("SendStringToExecute(")) offenders.push(`${name}: SendStringToExecute`);
    }
    expect(offenders).toEqual([]);
    expect(counts.ReadInCommandAsync).toBe(11);
    expect(counts.ReadThenCommandLineAsync).toBe(6);
  });

  // Typed input (SendStringToExecute) was lost after COM-started commands (issue #46; not
  // every time, issue #53), so with the default route a stuck -PLOT is cancelled over COM
  // after the operation, also when the operation failed.
  it("cancels a command left at a prompt through COM SendCommand after the operation", () => {
    const plot = withoutComments(source("PlotCommands.cs"));
    expect(plot.match(/CivilExecution\.QueueCancelAfterOperation\(\)/g)?.length).toBe(2);
    expect(execution).toMatch(/try\s*\{\s*await dispatch\.OperationDone;\s*\}\s*finally\s*\{\s*if \(PendingCancel\)[\s\S]*?await SendCancelAsync\(acadDocument\)/);
    expect(execution).toContain('SendCommandLineAsync(acadDocument, new[] { "\\x03\\x03" })');
    expect(execution).toMatch(/PendingCommandLine\.Clear\(\);\s*PendingCancel = false;/);
  });

  // Issue #53: SendStringToExecute only on the opt-in typed route and in the probe.
  it("calls SendStringToExecute once, for the typed hop start, typed command-line input and the probe only", () => {
    for (const name of pluginFiles.filter((file) => file !== "CivilExecution.TypedInput.cs")) {
      const code = withoutComments(source(name));
      expect(code, name).not.toContain("SendStringToExecute(");
      if (name !== "CivilExecution.cs") {
        expect(code, name).not.toMatch(/\bSendTyped(Input)?(Async)?\(/);
        expect(code, name).not.toContain("StartPluginCommandTypedAsync(");
      }
    }
    expect(count(typedInput, "SendStringToExecute(")).toBe(1);
    expect(methodBody(typedInput, "private static void SendTyped(Document doc, string text)"))
      .toContain("doc.SendStringToExecute(text, false, false, false)");
    // Three callers plus the definition.
    expect(count(typedInput, "SendTyped(")).toBe(4);
    for (const caller of [
      "private static async Task StartPluginCommandTypedAsync(",
      "private static async Task<string?> SendTypedInputAsync(",
      "public static Task<object?> ProbeTypedInputAsync(",
    ]) {
      expect(count(methodBody(typedInput, caller), "SendTyped("), caller).toBe(1);
    }
    expect(methodBody(typedInput, "private static async Task StartPluginCommandTypedAsync("))
      .toContain('SendTyped(doc, "_" + PluginCommandName + " ")');
    expect(methodBody(typedInput, "public static Task<object?> ProbeTypedInputAsync("))
      .toContain('SendTyped(doc, "_" + ProbeCommandName + " ")');
    // RunOnHostAsync is the only caller of the typed start and of the typed input sender.
    expect(count(execution, "StartPluginCommandTypedAsync(")).toBe(1);
    expect(count(execution, "SendTypedInputAsync(")).toBe(2);
  });

  it("keeps COM the default hop start and today's COM code path for it", () => {
    expect(decisions).toContain("private int hopStart = (int)HopStartRoute.Com;");
    expect(decisions).toMatch(/HopStart\(HopStartRoute route, TypedInputState state\) => route switch\s*\{\s*HopStartRoute\.Typed => InputChannel\.Typed,\s*HopStartRoute\.Auto => state == TypedInputState\.Dead \? InputChannel\.Com : InputChannel\.Typed,\s*_ => InputChannel\.Com,/);
    expect(decisions).toContain("route != HopStartRoute.Com && state != TypedInputState.Dead ? InputChannel.Typed : InputChannel.Com");
    // The typed start only when the operation's channel is Typed; otherwise the old COM dispatch.
    expect(execution).toMatch(/if \(channel\.Value == InputChannel\.Typed\)\s*\{[^}]*dispatch\.TryDispatch\(\(\) => StartPluginCommandTypedAsync\(commandDocument,[^}]*\}\s*else if \(!dispatch\.TryDispatch\(\(\) => StartPluginCommandAsync\(commandDocument,\s*\(\) => RunOnce\(HostRoute\.PluginCommand\), \(\) => !dispatch\.IsNotStarted\)\)\) return;/);
    const com = methodBody(execution, "private static async Task StartPluginCommandAsync(");
    expect(com).toContain("acadDocument ??= doc.GetAcadDocument();");
    expect(com).toMatch(/PluginCommandWork\.Enqueue\(work\);\s*await Task\.Run\(\(\) => SendCommandWithRetry\(acadDocument, "_" \+ PluginCommandName \+ " "\)\);\s*if \(!finished\(\)\)\s*\{\s*DropQueuedWork\(work\);/);
    // setHostRoute and the probe are plugin methods; health reports the route.
    const dispatcher = withoutComments(source("CommandDispatcher.cs"));
    expect(dispatcher).toContain('"setHostRoute" => Task.FromResult(CivilExecution.SetHostRoute(parameters))');
    expect(dispatcher).toContain('"probeTypedInput" => CivilExecution.ProbeTypedInputAsync(parameters)');
    expect(withoutComments(source("DrawingCommands.cs"))).toContain('["hostRoute"] = CivilExecution.Route.Report()');
  });

  // Never COM input into a typed-started C3DMCPHOP or typed input into a COM-started
  // one: UNDO once received "C3DMCPHOP" as its count.
  it("sends an operation's command-line input and cancel through the channel its hop was started with", () => {
    // One channel per operation, chosen once from a route read once.
    expect(count(execution, "var hopStart = Route.HopStart;")).toBe(1);
    expect(count(execution + typedInput, "Route.HopStart")).toBe(3);
    expect(count(execution, "new StrongBox<InputChannel>(")).toBe(1);
    expect(execution).toMatch(/new StrongBox<InputChannel>\(needsCommand\s*\? HostRouteDecision\.HopStart\(hopStart, Route\.TypedInput\.State\)\s*: HostRouteDecision\.IdleCommandLine\(hopStart, Route\.TypedInput\.State\)\);/);
    // The only change during an operation: a lost typed start under auto switches to COM
    // before the COM start of the same work. Never to Typed.
    expect(count(execution + typedInput, "channel.Value = ")).toBe(1);
    expect(typedInput).toMatch(/if \(!HostRouteDecision\.FallsBackToCom\(hopStart\)\)\s*\{[^}]*"typed hop start was lost; nothing ran\.[^}]*\}\s*channel\.Value = InputChannel\.Com;\s*await StartPluginCommandAsync\(doc, work, finished, acadDocument\);/);
    // Cancel and command-line input follow channel.Value; COM stays the else branch.
    expect(execution).toMatch(/if \(PendingCancel\)\s*\{\s*PendingCancel = false;\s*if \(channel\.Value == InputChannel\.Typed\)\s*\{\s*if \(inputDocument != null\) ReportInputWarning\(await SendTypedInputAsync\(inputDocument, new\[\] \{ "\\x03\\x03" \}\)\);\s*\}\s*else if \(acadDocument != null\) await SendCancelAsync\(acadDocument\);/);
    expect(execution).toMatch(/if \(PendingCommandLine\.Count > 0 && channel\.Value == InputChannel\.Typed && inputDocument != null\)\s*\{[^}]*ReportInputWarning\(await SendTypedInputAsync\(inputDocument, input\)\);\s*\}\s*else if \(PendingCommandLine\.Count > 0 && acadDocument != null\)\s*\{[^}]*await SendCommandLineAsync\(acadDocument, input\);/);
  });

  // "Input no longer pending" is no evidence: acedIsInputPending is also false when typed
  // strings were discarded. Each entry's command must start (CommandWillStart); without
  // that the caller's result carries a warning (never plain success for an UNDO that did
  // not run) and, after quiet idle time with no command started, typed input is marked
  // dead. Never resent over COM: the typed string may still run.
  it("requires evidence that typed command-line input ran, and reports it in the result", () => {
    const send = methodBody(typedInput, "private static async Task<string?> SendTypedInputAsync(");
    expect(send).toMatch(/commandStarted = \(_, e\) => created\.CommandStarted\(e\.GlobalCommandName\);\s*doc\.CommandWillStart \+= commandStarted;\s*foreach \(var text in input\) SendTyped\(doc, text\);/);
    expect(send).toMatch(/finally\s*\{\s*if \(commandStarted != null\) doc\.CommandWillStart -= commandStarted;\s*\}/);
    expect(send).toMatch(/watch\.Tick\(Stopwatch\.GetTimestamp\(\), HostIsIdle\(\), pending\)/);
    expect(send).toMatch(/case TypedInputVerdict\.Lost:[\s\S]*Route\.TypedInput\.RecordLostInput\(pending, DateTime\.UtcNow\)[\s\S]*return \$"Typed input was not consumed/);
    expect(send).toMatch(/default:[\s\S]*return \$"Typed input has not run yet/);
    expect(send).not.toMatch(/SendCommand|SendCancelAsync|SendCommandLineAsync|AcadInputPending\.Read\(\) != true/);
    // The warnings reach the results of the operations that queue command-line input.
    expect(count(execution, "ReportInputWarning(await SendTypedInputAsync(")).toBe(2);
    expect(count(execution, "inputWarning: inputWarnings.Add")).toBe(2);
    expect(methodBody(execution, "private static async Task<T> ExecuteOnHostAsync<T>("))
      .toContain("return WithInputWarnings(result!, inputWarnings);");
    expect(methodBody(execution, "public static async Task<T> RunCommandLineAsync<T>("))
      .toContain("return WithInputWarnings(result, inputWarnings);");
    expect(methodBody(execution, "private static T WithInputWarnings<T>(")).toContain('response["warnings"] = combined;');
    expect(decisions).toContain('Set(TypedInputState.Dead, "typed-input-lost", utc)');
  });

  it("drops a lost typed start's work and fails or falls back without running anything twice", () => {
    const start = methodBody(typedInput, "private static async Task StartPluginCommandTypedAsync(");
    expect(typedInput).toContain("TypedStartQuietTimeout = TimeSpan.FromMilliseconds(2000)");
    expect(start).toMatch(/var typedWork = claim\.Wrap\(work,[\s\S]*PluginCommandWork\.Enqueue\(typedWork\);\s*try\s*\{\s*SendTyped\([^;]*;\s*\}\s*catch\s*\{\s*claim\.TryDeclareLost\(\);\s*DropQueuedWork\(typedWork\);\s*throw;/);
    // A failed send, a failed watcher, abandoned, busy and lost all drop the queued item,
    // as the COM not-started path does.
    expect(count(start, "DropQueuedWork(typedWork);")).toBe(5);
    expect(start).toContain("Route.TypedInput.RecordLostStart(inputPending, DateTime.UtcNow)");
    // The start latency is taken at the top of the hop's work, not when the verdict's
    // continuation runs after the work's synchronous part.
    expect(start).toMatch(/started: \(\) =>\s*\{\s*Volatile\.Write\(ref startedAt, Stopwatch\.GetTimestamp\(\)\);\s*verdict\.TrySetResult\(TypedStartOutcome\.Started\);\s*\}/);
    expect(start).toContain("Route.TypedInput.RecordTypedStart(ElapsedMs(sentAt, Volatile.Read(ref startedAt)), DateTime.UtcNow)");
    expect(count(start, '"CIVIL3D.HOST_BUSY"')).toBe(2);
    // The wrapper runs work only after winning the claim; the watcher declares a start
    // lost only through the same claim.
    expect(decisions).toMatch(/internal Func<Task> Wrap\(Func<Task> work, Action started, Action lateRun\) => \(\) =>\s*\{\s*if \(!TryStart\(\)\)\s*\{\s*lateRun\(\);\s*return Task\.CompletedTask;\s*\}\s*started\(\);\s*return work\(\);/);
    const wait = methodBody(typedInput, "private static async Task<(TypedStartOutcome Outcome, bool? InputPending)> WaitForTypedStartAsync(");
    expect(count(wait, "claim.TryDeclareLost()")).toBe(5);
    expect(wait).not.toContain("TryStart(");
    expect(wait).toContain("PostMessage(mainWindow, 0 /* WM_NULL */");
    // A typed C3DMCPHOP with no work still runs nothing.
    expect(execution).toMatch(/while \(PluginCommandWork\.TryDequeue\(out var work\)\)/);
  });

  it("probes typed input only from a quiet Idle tick, inside the host gate", () => {
    const probe = methodBody(typedInput, "public static Task<object?> ProbeTypedInputAsync(");
    expect(probe).toContain('Math.Clamp(PluginRuntime.GetOptionalInt(parameters, "timeoutMs") ?? 3000, 250, 10000)');
    expect(probe).toContain("return ExecuteSerializedAsync<object?>(");
    // CMDACTIVE and CMDNAMES are read before deciding to skip, and returned when busy.
    expect(probe).toMatch(/var cmdactive = Convert\.ToInt32\(CoreApp\.GetSystemVariable\("CMDACTIVE"\)\);\s*var cmdnames = Convert\.ToString\(CoreApp\.GetSystemVariable\("CMDNAMES"\)\);\s*if \(cmdactive != 0 \|\| !doc\.Editor\.IsQuiescent\)\s*\{\s*skipped = "busy";\s*busyCmdnames = cmdnames;\s*busyCmdactive = cmdactive;\s*return null;\s*\}[\s\S]*AcadInputPending\.Read\(\);[\s\S]*SendTyped\(/);
    expect(probe).toContain("return Result(false, skipped, cmdnames: busyCmdnames, cmdactive: busyCmdactive);");
    expect(probe).toContain('["cmdactive"] = cmdactive,');
    expect(probe).toContain("Route.TypedInput.RecordProbe(ran, latency, DateTime.UtcNow)");
    const entry = withoutComments(source("PluginEntry.cs"));
    // NoUndoMarker: the probe touches no database, so it must not leave an undo step
    // that shifts UNDO counts or clears redo.
    expect(entry).toMatch(/\[CommandMethod\(CivilExecution\.ProbeCommandName, CommandFlags\.Modal \| CommandFlags\.NoHistory \| CommandFlags\.NoMultiple \| CommandFlags\.NoUndoMarker\)\]\s*public static void ProbeCommand\(\)/);
    expect(typedInput).toContain('internal const string ProbeCommandName = "C3DMCPPROBE";');
    expect(decisions).toContain('internal const string ExportName = "?acedIsInputPending@@YA_NXZ";');
  });
});
