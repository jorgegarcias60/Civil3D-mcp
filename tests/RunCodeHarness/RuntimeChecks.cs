// Offline checks of runCode's AutoCAD-free runtime helpers, called from
// Program.cs: the Log cap, runCode token and host dispatch gate.
// RunTransaction (RunCodeScript.cs) needs AutoCAD at runtime, so it is only
// compiled here (with the plugin build) and checked live in Civil 3D.
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Civil3DMcpPlugin;

internal static class RuntimeChecks
{
  internal static void Run(Action<bool, string> expect)
  {
    CheckOutput(expect);
    CheckAuth(expect);
    CheckHelpers(expect);
    CheckDrawingHelpers(expect);
    CheckViewportLayouts(expect);
    CheckPreview(expect);
    CheckDocuments(expect);
    CheckCopies(expect);
    RetentionChecks.Run(expect);
    ResultChecks.Run(expect);
    CheckDispatchGateAsync(expect).GetAwaiter().GetResult();
  }

  private static void CheckViewportLayouts(Action<bool, string> expect)
  {
    foreach (var (original, empty, shouldActivate) in new[] {
      ("Model", true, true), ("Other sheet", false, true),
      ("sHeEt", false, false), ("Sheet", true, true) })
    {
      var current = original;
      var events = new List<string>();
      var activated = false;
      var result = RunCodeHelpers.WithCurrentLayout("Sheet", empty, () => current,
        name => { current = name; events.Add("layout:" + name); },
        () => { activated = true; events.Add("activated"); },
        () =>
        {
          expect(string.Equals(current, "Sheet", StringComparison.OrdinalIgnoreCase),
            "viewport guard and edit run with their layout current");
          events.Add("edit");
          return "2A";
        });
      var expected = shouldActivate
        ? new[] { "layout:Sheet", "activated", "edit", "layout:" + original }
        : new[] { "edit" };
      expect(result == "2A" && current == original && activated == shouldActivate && events.SequenceEqual(expected),
        $"viewport activation and restoration: previous={original}, empty={empty}");
    }

    foreach (var failure in new Exception[] {
      new ArgumentException("The overall viewport shows paper space; edits to it revert."),
      new InvalidOperationException("Viewport setter failed"),
      new TimeoutException("Run budget expired"), new OperationCanceledException() })
    {
      var current = "Model";
      var activated = false;
      try
      {
        RunCodeHelpers.WithCurrentLayout<int>("Sheet", true, () => current, name => current = name,
          () => activated = true, () => throw failure);
        expect(false, "viewport edit failure must propagate");
      }
      catch (Exception caught)
      {
        expect(ReferenceEquals(caught, failure) && current == "Model" && activated,
          $"viewport restoration preserves {failure.GetType().Name} and activation report");
      }
    }

    // Issue #43: SetViewport refuses to switch layouts in a run that created or erased viewports,
    // before anything is switched (nothing to restore) and before the edit.
    foreach (var (original, empty, changed, shouldRefuse) in new[] {
      ("Model", false, true, true), ("sHeEt", true, true, true), ("Model", false, false, false),
      ("sHeEt", false, true, false), ("sHeEt", false, false, false) })
    {
      var current = original;
      var events = new List<string>();
      try
      {
        RunCodeHelpers.WithCurrentLayout("Sheet", empty, () => current,
          name => { current = name; events.Add("layout:" + name); }, () => events.Add("activated"),
          () => { events.Add("edit"); return 0; }, () => changed);
        expect(!shouldRefuse && events.Contains("edit") && current == original,
          $"viewport changes in the run + layout: edit runs only when no switch is needed (previous={original}, empty={empty}, changed={changed})");
      }
      catch (InvalidOperationException refused)
      {
        expect(shouldRefuse && events.Count == 0 && current == original
            && refused.Message.Contains("issue #43", StringComparison.Ordinal)
            && refused.Message.Contains("'Sheet'", StringComparison.Ordinal),
          $"viewport changes in the run + layout switch is refused before switching or editing (previous={original}, empty={empty})");
      }
    }

    var partiallyChanged = "Model";
    var activationFailure = new InvalidOperationException("Activation failed after changing layout");
    var editRan = false;
    try
    {
      RunCodeHelpers.WithCurrentLayout("Sheet", true, () => partiallyChanged,
        name => { partiallyChanged = name; if (name == "Sheet") throw activationFailure; },
        () => { }, () => { editRan = true; return 0; });
      expect(false, "partial viewport activation must fail");
    }
    catch (Exception caught)
    {
      expect(ReferenceEquals(caught, activationFailure) && partiallyChanged == "Model" && !editRan,
        "partially failed layout activation restores the original and never edits");
    }
  }

  private static void CheckPreview(Action<bool, string> expect)
  {
    const long startHandseed = 0x30;
    // Live objects can have IsNewObject == false before the run commits.
    // Handles at or above the starting seed still identify additions.
    var objects = new[] {
      new RunCodeChange("TEXT", "0", 0x30, false, true, "30"),
      new RunCodeChange("TEXT", "0", 0x31, false, true, "31"),
      new RunCodeChange("TEXT", "0", 0x32, false, false, "32"),
      new RunCodeChange("TEXT", "0", 0x33, true, true, "33"),
      new RunCodeChange("TEXT", "0", 0x2a, true, true, "2a"),
      new RunCodeChange("TEXT", "0", 0x2b, false, true, "2b"),
      new RunCodeChange("TEXT", "0", 0x2f, false, false, "2f"),
      new RunCodeChange("LAYER", null, 1, false, true, "1"),
      new RunCodeChange("TEXT", "A", 3, false, true, "3"),
    };
    var summary = RunCodeChanges.Reduce(objects, startHandseed);
    var text = summary["byType"]!.AsArray().Single(row => (string?)row!["type"] == "TEXT" && (string?)row["layer"] == "0")!;
    expect((long)text["added"]! == 3 && (long)text["erased"]! == 1 && (long)text["modified"]! == 1,
      "preview starting handseed counts additions, erasures and modifications exactly once");
    expect((long)summary["totals"]!["modified"]! == 3 && summary["byType"]![0]!["layer"] == null,
      "preview groups separate layers and include non-entity objects");
    expect(summary["erasedHandles"]!.ToJsonString() == "[\"2A\"]" && summary["modifiedHandles"]!.ToJsonString() == "[\"1\",\"2B\",\"3\"]",
      "preview handles are existing objects only, uppercase and ordered");
    expect(summary.ToJsonString() == RunCodeChanges.Reduce(objects.Reverse(), startHandseed).ToJsonString(),
      "preview groups and handles are independent of transaction iteration order");
    var boundary = RunCodeChanges.Reduce(new[] {
      new RunCodeChange("TEXT", "0", startHandseed - 1, false, true, "2f"),
      new RunCodeChange("TEXT", "0", startHandseed, false, true, "30"),
      new RunCodeChange("TEXT", "0", startHandseed + 1, true, true, "31"),
    }, startHandseed);
    expect((long)boundary["totals"]!["added"]! == 1 && (long)boundary["totals"]!["erased"]! == 0 &&
        (long)boundary["totals"]!["modified"]! == 1 && boundary["modifiedHandles"]!.ToJsonString() == "[\"2F\"]" &&
        boundary["erasedHandles"]!.AsArray().Count == 0,
      "preview includes the seed boundary as new and omits new-then-erased objects and new handles");
    const long manyHandseed = 0x1000;
    var many = Enumerable.Range(1, 250).SelectMany(i => new[] {
      new RunCodeChange("TEXT", "0", i, true, true, i.ToString("X")),
      new RunCodeChange("TEXT", "0", i + 500, false, true, (i + 500).ToString("X")),
      new RunCodeChange("TEXT", "0", manyHandseed + i, false, true, (manyHandseed + i).ToString("X")),
      new RunCodeChange("TEXT", "0", manyHandseed + i + 500, true, true, (manyHandseed + i + 500).ToString("X")),
    }).ToArray();
    var bounded = RunCodeChanges.Reduce(many, manyHandseed);
    expect(bounded["erasedHandles"]!.AsArray().Count == 200 && bounded["modifiedHandles"]!.AsArray().Count == 200 && (bool)bounded["truncated"]!,
      "preview independently caps both existing handle lists at 200");
    expect((long)bounded["totals"]!["added"]! == 250 && (long)bounded["totals"]!["erased"]! == 250 &&
        (long)bounded["totals"]!["modified"]! == 250 &&
        bounded.ToJsonString() == RunCodeChanges.Reduce(many.Reverse(), manyHandseed).ToJsonString(),
      "preview truncates only handles, preserves complete totals and deterministic selection");
    var exact = RunCodeChanges.Reduce(many.Where(obj => obj.HandleValue <= 200 || obj.HandleValue >= manyHandseed), manyHandseed);
    expect(!(bool)exact["truncated"]! && exact["erasedHandles"]!.AsArray().Count == 200,
      "preview does not mark exactly 200 handles truncated");
    var empty = RunCodeChanges.Reduce(Array.Empty<RunCodeChange>(), startHandseed);
    expect(empty["byType"]!.AsArray().Count == 0 && (long)empty["totals"]!["added"]! == 0 && !(bool)empty["truncated"]!,
      "preview with no changes returns empty groups and zero totals");

    var args = JsonNode.Parse("{\"z\":null,\"a\":{\"b\":1,\"a\":\"Δ\"},\"list\":[2,1]}")!.AsObject();
    const string canonical = "{\"a\":{\"a\":\"Δ\",\"b\":1},\"list\":[2,1],\"z\":null}";
    expect(RunCodeArguments.CanonicalJson(args, null) == canonical,
      "preview args hash sorts nested keys, preserves arrays, nulls and Unicode");
    expect(RunCodeArguments.CanonicalJson(args, canonical) == canonical && RunCodeHelpers.Sha256Hex(canonical).Length == 64,
      "preview preserves the server's canonical bytes for cross-runtime hashes");
    try { RunCodeArguments.CanonicalJson(args, "{}"); expect(false, "argsJson mismatch is refused"); }
    catch (ArgumentException) { expect(true, "argsJson mismatch is refused"); }
  }

  private static void CheckDrawingHelpers(Action<bool, string> expect)
  {
    expect(RunCodeHelpers.XrefScale(null, "Feet", "USSurveyFeet", () => throw new Exception()) == 1,
      "feet and US survey feet use scale one");
    expect(RunCodeHelpers.XrefScale(null, "USSurveyFeet", "Feet", () => throw new Exception()) == 1 &&
      RunCodeHelpers.XrefScale(null, "Feet", "Feet", () => throw new Exception()) == 1,
      "survey feet to feet and feet to feet use scale one");
    expect(RunCodeHelpers.XrefScale(0.25, "Unitless", "Feet", () => throw new Exception()) == 0.25,
      "explicit xref scale overrides undefined source units");
    expect(RunCodeHelpers.XrefScale(null, "Inches", "Feet", () => 1.0 / 12) == 1.0 / 12,
      "xref unit converter factor is used without double scaling");
    try { RunCodeHelpers.XrefScale(null, "Unitless", "Feet", () => 1); expect(false, "unitless xref requires explicit scale"); }
    catch (ArgumentException) { expect(true, "unitless xref requires explicit scale"); }
    try { RunCodeHelpers.XrefScale(null, "Feet", "Undefined", () => 1); expect(false, "undefined host units require explicit scale"); }
    catch (ArgumentException) { expect(true, "undefined host units require explicit scale"); }
    try { RunCodeHelpers.XrefScale(null, "Undefined", "Feet", () => 1); expect(false, "undefined source units require explicit scale"); }
    catch (ArgumentException) { expect(true, "undefined source units require explicit scale"); }
    try { RunCodeHelpers.XrefScale(0, "Feet", "Feet", () => 1); expect(false, "zero xref scale is refused"); }
    catch (ArgumentOutOfRangeException) { expect(true, "zero xref scale is refused"); }
    try { RunCodeHelpers.XrefScale(null, "Inches", "Feet", () => double.PositiveInfinity); expect(false, "invalid unit conversion is refused"); }
    catch (ArgumentException) { expect(true, "invalid unit conversion is refused"); }
    var sourcePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "xref-source.dwg"));
    var hostPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "host", "host.dwg"));
    expect(RunCodeHelpers.XrefPathName(sourcePath, hostPath, true) == Path.GetRelativePath(Path.GetDirectoryName(hostPath)!, sourcePath),
      "saved same-drive host uses relative xref path");
    var siblingSource = Path.Combine(Path.GetDirectoryName(hostPath)!, "source.dwg");
    expect(RunCodeHelpers.XrefPathName(siblingSource, hostPath, true) == "." + Path.DirectorySeparatorChar + "source.dwg",
      "same-folder xref path is explicitly relative, not AutoCAD No path");
    expect(RunCodeHelpers.XrefPathName(sourcePath, string.Empty, true) == sourcePath,
      "unsaved host keeps full xref path");
    expect(RunCodeHelpers.XrefPathName(sourcePath, hostPath, false) == sourcePath,
      "relativePath false keeps full xref path");
    if (OperatingSystem.IsWindows())
      expect(RunCodeHelpers.XrefPathName(@"D:\source.dwg", @"C:\host\host.dwg", true) == @"D:\source.dwg",
        "another-drive xref keeps full path");
    var square = RunCodeHelpers.ClipPolygon(new[] { (0d, 0d), (0d, 2d), (2d, 2d), (2d, 0d), (0d, 0d) });
    expect(square.Length == 4 && square[0] == (2d, 0d), "clip polygon drops repeated closure and normalizes winding");
    var distantSquare = RunCodeHelpers.ClipPolygon(new[] {
      (1e7, 1e7), (1e7, 1e7 + 0.1), (1e7 + 0.1, 1e7 + 0.1), (1e7 + 0.1, 1e7) });
    expect(distantSquare.Length == 4 && distantSquare[0] == (1e7 + 0.1, 1e7),
      "clip polygon keeps a 0.1 square at coordinates near ten million and normalizes winding");
    var smallerSquare = RunCodeHelpers.ClipPolygon(new[] {
      (5e6, 5e6), (5e6 + 0.05, 5e6), (5e6 + 0.05, 5e6 + 0.05), (5e6, 5e6 + 0.05) });
    expect(smallerSquare.Length == 4 && smallerSquare[0] == (5e6, 5e6),
      "clip polygon keeps a 0.05 square at coordinates near five million");
    try { RunCodeHelpers.ClipPolygon(new[] { (1e7, 1e7), (1e7 + 0.1, 1e7), (1e7 + 0.2, 1e7) }); expect(false, "distant zero-area clip polygon is rejected"); }
    catch (ArgumentException) { expect(true, "distant zero-area clip polygon is rejected"); }
    try { RunCodeHelpers.ClipPolygon(new[] { (0d, 0d), (2d, 2d), (0d, 2d), (2d, 0d) }); expect(false, "clip polygon rejects crossings"); }
    catch (ArgumentException) { expect(true, "clip polygon rejects crossings"); }
  }

  private static async Task CheckDispatchGateAsync(Action<bool, string> expect)
  {
    await CheckCancelBeforeDispatchAsync(expect);
    await CheckStartBeforeCancelAsync(expect);
    await CheckDispatchRegistrationRaceAsync(expect);
    await CheckDispatchTimeoutAsync(expect);
    await CheckStartCancelRaceAsync(expect);
  }

  private static async Task CheckCancelBeforeDispatchAsync(Action<bool, string> expect)
  {
    var gate = new RunCodeDispatchGate();
    var dispatch = NewCompletion();
    var warnings = new List<string>();
    using var semaphore = new SemaphoreSlim(0, 1);
    var releases = 0;
    var restored = false;
    var factoryCalls = 0;
    gate.TryDispatch(() => { factoryCalls++; return dispatch.Task; });
    gate.CancelUnstartedOperation();
    gate.CancelUnstartedOperation();
    expect(gate.OperationDone.IsCanceled && !gate.TryStart() && gate.TryAbandon(),
      "cancellation before the host starts abandons the operation once and refuses a late start");
    gate.TryDispatch(() => { factoryCalls++; return Task.CompletedTask; });
    expect(factoryCalls == 1, "abandoned dispatch refuses a later factory call");

    var cleanup = CleanupAsync(gate, semaphore, () => restored = true, () => releases++, warnings);
    await Task.Yield();
    expect(!cleanup.IsCompleted && !restored && releases == 0 && semaphore.CurrentCount == 0,
      "outer cleanup waits for the pending dispatch before restoring and releasing the gate");
    dispatch.SetResult();
    await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
    expect(restored && releases == 1 && semaphore.CurrentCount == 1 && warnings.Count == 0,
      "canceled dispatch drains before restoration and exactly one gate release");
  }

  private static async Task CheckStartBeforeCancelAsync(Action<bool, string> expect)
  {
    await CheckStartedDrainOrderAsync(expect, completeDispatchFirst: true);
    await CheckStartedDrainOrderAsync(expect, completeDispatchFirst: false);
  }

  private static async Task CheckStartedDrainOrderAsync(Action<bool, string> expect, bool completeDispatchFirst)
  {
    var gate = new RunCodeDispatchGate();
    var dispatch = NewCompletion();
    var warnings = new List<string>();
    using var semaphore = new SemaphoreSlim(0, 1);
    var releases = 0;
    var restored = false;
    gate.TryDispatch(() => dispatch.Task);
    expect(gate.TryStart(), "host starts the runCode operation once");
    gate.CancelUnstartedOperation();
    gate.CancelUnstartedOperation();
    expect(!gate.TryAbandon() && !gate.OperationDone.IsCompleted,
      "cancellation after the host starts keeps its operation running");

    var cleanup = CleanupAsync(gate, semaphore, () => restored = true, () => releases++, warnings);
    await Task.Yield();
    if (completeDispatchFirst) dispatch.SetResult();
    else gate.CompleteOperation();
    await Task.Yield();
    expect(!cleanup.IsCompleted && releases == 0 && !restored && semaphore.CurrentCount == 0
      && (completeDispatchFirst ? !gate.OperationDone.IsCompleted : !gate.DispatchDone.IsCompleted),
      completeDispatchFirst
        ? "completed dispatch cannot release the gate while the host operation runs"
        : "completed operation cannot release the gate while dispatch is pending");
    if (completeDispatchFirst) gate.CompleteOperation();
    else dispatch.SetResult();
    await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
    expect(restored && releases == 1 && semaphore.CurrentCount == 1 && warnings.Count == 0,
      "running operation and dispatch both finish before one restoration and gate release");
    expect(!gate.TryStart(), "a completed operation cannot start a second time");
  }

  private static async Task CheckDispatchRegistrationRaceAsync(Action<bool, string> expect)
  {
    var gate = new RunCodeDispatchGate();
    var dispatch = NewCompletion();
    var factoryEntered = NewCompletion();
    var factoryRelease = NewCompletion();
    var cancelEntered = NewCompletion();
    var cleanupEntered = NewCompletion();
    var warnings = new List<string>();
    using var semaphore = new SemaphoreSlim(0, 1);
    var releases = 0;
    var restored = false;
    var registration = Task.Factory.StartNew(() => gate.TryDispatch(() =>
    {
      factoryEntered.SetResult();
      factoryRelease.Task.GetAwaiter().GetResult();
      return dispatch.Task;
    }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    try
    {
      await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
      var cancellation = Task.Factory.StartNew(() =>
      {
        cancelEntered.SetResult();
        gate.CancelUnstartedOperation();
      }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
      var cleanup = Task.Factory.StartNew(async () =>
      {
        cleanupEntered.SetResult();
        await CleanupAsync(gate, semaphore, () => restored = true, () => releases++, warnings);
      }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
      await Task.WhenAll(cancelEntered.Task, cleanupEntered.Task).WaitAsync(TimeSpan.FromSeconds(2));
      await Task.Yield();
      expect(!registration.IsCompleted && !cancellation.IsCompleted && !cleanup.IsCompleted
        && !restored && releases == 0, "cancellation and drain cannot pass a factory holding dispatch registration");

      factoryRelease.SetResult();
      expect(await registration.WaitAsync(TimeSpan.FromSeconds(2)), "dispatch factory registers while cancellation is waiting");
      await cancellation.WaitAsync(TimeSpan.FromSeconds(2));
      expect(ReferenceEquals(gate.DispatchDone, dispatch.Task) && !cleanup.IsCompleted
        && !restored && releases == 0 && semaphore.CurrentCount == 0,
        "drain sees the registered dispatch and holds the gate until it finishes");
      dispatch.SetResult();
      await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
      expect(restored && releases == 1 && semaphore.CurrentCount == 1 && warnings.Count == 0
        && !gate.TryStart(), "registered dispatch drains before one restoration and gate release");
    }
    finally
    {
      factoryRelease.TrySetResult();
      dispatch.TrySetResult();
    }
  }

  private static async Task CheckDispatchTimeoutAsync(Action<bool, string> expect)
  {
    var gate = new RunCodeDispatchGate();
    var neverDispatched = NewCompletion();
    var warnings = new List<string>();
    using var semaphore = new SemaphoreSlim(0, 1);
    var releases = 0;
    var restored = false;
    gate.TryDispatch(() => neverDispatched.Task);
    gate.CancelUnstartedOperation();
    await CleanupAsync(gate, semaphore, () => restored = true, () => releases++, warnings,
      TimeSpan.FromMilliseconds(30)).WaitAsync(TimeSpan.FromSeconds(2));
    expect(warnings.Count == 1 && restored && releases == 1 && semaphore.CurrentCount == 1,
      "stuck dispatch times out with a warning, then outer cleanup restores and releases once");
    expect(!gate.TryStart() && gate.TryAbandon(), "timed-out, unstarted dispatch remains abandoned");
    neverDispatched.SetResult();
  }

  private static async Task CheckStartCancelRaceAsync(Action<bool, string> expect)
  {
    for (var attempt = 0; attempt < 32; attempt++)
    {
      var gate = new RunCodeDispatchGate();
      using var start = new ManualResetEventSlim();
      var starter = Task.Run(() => { start.Wait(); return gate.TryStart(); });
      var canceler = Task.Run(() => { start.Wait(); gate.CancelUnstartedOperation(); });
      start.Set();
      await Task.WhenAll(starter, canceler);
      var running = starter.Result;
      expect(running != gate.OperationDone.IsCanceled && !gate.TryStart(),
        "simultaneous host start and cancellation produce one terminal dispatch decision");
      if (running)
      {
        expect(!gate.TryAbandon(), "a started operation cannot be abandoned by a later cancellation");
        gate.CompleteOperation();
      }
      else
      {
        expect(gate.TryAbandon(), "an abandoned operation stays abandoned after repeated cancellation");
      }
    }
  }

  private static TaskCompletionSource NewCompletion() =>
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  private static async Task CleanupAsync(RunCodeDispatchGate gate, SemaphoreSlim semaphore,
    Action restore, Action released, List<string> warnings, TimeSpan? timeout = null)
  {
    try
    {
      await gate.DrainAsync(timeout ?? TimeSpan.FromSeconds(2), warnings.Add);
    }
    finally
    {
      restore();
      semaphore.Release();
      released();
    }
  }

  private static void CheckDocuments(Action<bool, string> expect)
  {
    var names = new[] { @"C:\one\Alpha.dwg", @"C:\two\Shared.dwg", @"D:\three\Shared.dwg", "Drawing1.dwg", @"C:\one\Site.rev2.dwg" };
    var full = RunCodeHelpers.MatchDocument("c:/ONE/ALPHA.dwg", names);
    expect(full == (0, null), "MatchDocument normalizes full paths, case and forward slashes");
    expect(RunCodeHelpers.MatchDocument(@"C:\one\..\one\Alpha.dwg", names) == (0, null), "MatchDocument resolves full-path dot segments");
    expect(RunCodeHelpers.MatchDocument("ALPHA.dwg", names) == (0, null), "MatchDocument matches a unique bare filename");
    expect(RunCodeHelpers.MatchDocument("alpha", names) == (0, null), "MatchDocument accepts a bare name without .dwg");
    expect(RunCodeHelpers.MatchDocument("Drawing1.dwg", names) == (3, null)
      && RunCodeHelpers.MatchDocument("Drawing1", names) == (3, null), "MatchDocument uses unsaved Document.Name values");
    var ambiguous = RunCodeHelpers.MatchDocument("Shared", names);
    expect(ambiguous.Index == -1 && ambiguous.Error!.Contains("ambiguous")
      && ambiguous.Error.Contains(names[1]) && ambiguous.Error.Contains(names[2]), "ambiguous names list both open drawings");
    var cwdUnsaved = RunCodeHelpers.MatchDocument(Path.Combine(Environment.CurrentDirectory, "Drawing1.dwg"), names);
    expect(cwdUnsaved.Index == -1 && cwdUnsaved.Error!.Contains("not open"),
      "a full path never matches an unsaved drawing through the process folder");
    var missing = RunCodeHelpers.MatchDocument(@"C:\missing\Alpha.dwg", names);
    expect(missing.Index == -1 && missing.Error!.Contains("not open") && names.All(missing.Error.Contains),
      "a not-open full path does not fall back to its filename and lists all open drawings");
    var empty = RunCodeHelpers.MatchDocument("Alpha", Array.Empty<string>());
    expect(empty.Index == -1 && empty.Error!.Contains("Open drawings:"), "no open names produces a useful not-open error");
    expect(RunCodeHelpers.MatchDocument("Site.rev2", names) == (4, null), "a dotted bare drawing name accepts omitted .dwg");
  }

  private static void CheckHelpers(Action<bool, string> expect)
  {
    expect(RunCodeHelpers.Sha256Hex("abc") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
      "Sha256Hex uses UTF-8 and returns lowercase SHA-256");
    expect(RunCodeHelpers.ParseHandle(" 0xAbC ") == 0xabc && RunCodeHelpers.ParseHandle("10") == 16,
      "ParseHandle trims and accepts plain hex or a 0x prefix");
    foreach (var text in new[] { "", " ", "0x", "0", "-1", "xyz", "1 2", "0x0", "10000000000000000" })
    {
      var refused = false;
      try { RunCodeHelpers.ParseHandle(text); }
      catch (ArgumentException) { refused = true; }
      expect(refused, $"ParseHandle refuses '{text}'");
    }
    foreach (var name in new[] { "DwgNeedsRecovery", "DwgNotRecoverable", "InvalidDwgVersion", "DwgObjectImproperlyRead" })
      expect(RunCodeHelpers.IsDrawingContentError(name), $"{name} is a drawing-content error");
    expect(!RunCodeHelpers.IsDrawingContentError("FileSharingViolation") && !RunCodeHelpers.IsDrawingContentError(""),
      "sharing and unknown errors are not drawing-content errors");
  }

  private static void CheckCopies(Action<bool, string> expect)
  {
    var folder = Path.Combine(AppContext.BaseDirectory, "copy-check-" + Guid.NewGuid().ToString("N"));
    var oldLogPath = PluginLog.LogFilePath;
    try
    {
      PluginLog.LogFilePath = Path.Combine(folder, "plugin.log");
      var runFolder = RunCodeLog.CreateRunFolder("run");
      RunCodeLog.WriteText(runFolder, RunCodeLog.ScriptFile, "return 1;");
      RunCodeLog.WriteSummary(runFolder, new JsonObject { ["status"] = "ok" });
      var destination = Path.Combine(folder, "copied");
      var none = RunCodeLog.CopyOutputs(runFolder, destination, removeCopied: true);
      expect(none.Copied.Count == 0 && none.Warnings.Count == 0 && !Directory.Exists(destination),
        "no outputs means no destination folder is created");

      var source = RunCodeLog.WriteText(runFolder, "data.txt", "first");
      RunCodeLog.WriteText(runFolder, RunCodeLog.OutputFile, "output");
      RunCodeLog.WriteText(runFolder, RunCodeLog.ResultFile, "{}");
      var copied = RunCodeLog.CopyOutputs(runFolder, destination, removeCopied: false);
      expect(copied.Copied.Count == 3 && copied.Removed.Count == 0 && copied.Warnings.Count == 0
        && File.ReadAllText(Path.Combine(destination, "data.txt")) == "first" && File.Exists(source),
        "CopyOutputs creates the destination and copies script output, output.txt and result.json");
      expect(!File.Exists(Path.Combine(destination, RunCodeLog.ScriptFile))
        && !File.Exists(Path.Combine(destination, RunCodeLog.SummaryFile)), "CopyOutputs never copies script.cs or run.json");
      RunCodeLog.WriteText(runFolder, "data.txt", "replacement");
      var replaced = RunCodeLog.CopyOutputs(runFolder, destination, removeCopied: false);
      expect(replaced.Warnings.Count == 0 && File.ReadAllText(Path.Combine(destination, "data.txt")) == "replacement",
        "CopyOutputs atomically replaces existing outputs");

      foreach (var prefix in new[] { @"\\?\", @"\\.\" })
      {
        var device = RunCodeLog.CopyOutputs(runFolder, prefix + destination, removeCopied: true);
        expect(device.Warnings.Count == 1 && device.Copied.Count == 0 && device.Removed.Count == 0
          && File.ReadAllText(source) == "replacement" && Directory.GetFiles(runFolder).Length == 5,
          $"CopyOutputs refuses a {prefix} destination and keeps all source files");
      }
      CheckCopyLink(runFolder, source, Path.Combine(folder, "linked"), expect);

      using (var locked = new FileStream(Path.Combine(destination, "data.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
      {
        var partial = RunCodeLog.CopyOutputs(runFolder, destination, removeCopied: true);
        expect(partial.Warnings.Count > 0 && File.Exists(source)
          && !partial.Copied.Contains(Path.Combine(destination, "data.txt"))
          && !partial.Removed.Contains(source), "a locked destination warns and keeps its source");
        expect(partial.Copied.Count == 2 && partial.Removed.Count == 2,
          "other outputs are copied and removed after their own successful copy");
      }
      var removed = RunCodeLog.CopyOutputs(runFolder, destination, removeCopied: true);
      expect(removed.Copied.Count == 1 && removed.Removed.SequenceEqual(new[] { source }) && !File.Exists(source),
        "removeCopied removes an output only after it was copied");
      expect(File.Exists(Path.Combine(runFolder, RunCodeLog.ScriptFile))
        && File.Exists(Path.Combine(runFolder, RunCodeLog.SummaryFile)), "audit files are kept when outputs are removed");
      expect(!Directory.GetFiles(destination).Any(path => path.EndsWith(".tmp")), "CopyOutputs cleans up temporary files");

      var inside = RunCodeLog.CopyOutputs(runFolder, Path.Combine(RunCodeLog.Root, "copy"), false);
      expect(inside.Warnings.Count == 1, "CopyOutputs refuses a destination inside the log root");
      var relative = RunCodeLog.CopyOutputs(runFolder, "relative", false);
      expect(relative.Warnings.Count == 1, "CopyOutputs refuses a relative destination");
      var outside = RunCodeLog.CopyOutputs(folder, destination, false);
      expect(outside.Warnings.Count == 1, "CopyOutputs refuses sources outside the log root");
    }
    finally
    {
      PluginLog.LogFilePath = oldLogPath;
      if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
  }

  private static void CheckCopyLink(string runFolder, string source, string link, Action<bool, string> expect)
  {
    try
    {
      Directory.CreateSymbolicLink(link, RunCodeLog.Root);
    }
    catch (Exception)
    {
      try
      {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
          ArgumentList = { "/c", "mklink", "/J", link, RunCodeLog.Root },
          UseShellExecute = false,
          CreateNoWindow = true,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
        })!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("mklink /J failed.");
      }
      catch (Exception exception)
      {
        Console.WriteLine($"SKIPPED: cannot create a directory link for CopyOutputs: {exception.Message}");
        return;
      }
    }

    try
    {
      foreach (var destination in new[] { link, Path.Combine(link, "run"), Path.Combine(link, "new", "nested") })
      {
        var refused = RunCodeLog.CopyOutputs(runFolder, destination, removeCopied: true);
        expect(refused.Warnings.Count == 1 && refused.Copied.Count == 0 && refused.Removed.Count == 0
          && File.ReadAllText(source) == "replacement" && Directory.GetFiles(runFolder).Length == 5
          && !Directory.Exists(Path.Combine(RunCodeLog.Root, "new")),
          "CopyOutputs refuses a junction or symbolic link into the log root and keeps all source files");
      }
    }
    finally
    {
      Directory.Delete(link, recursive: false);
    }
  }

  private static void CheckOutput(Action<bool, string> expect)
  {
    var full = new StringBuilder();
    expect(RunCodeOutput.AppendLine(full, new string('x', 50), 10) && full.Length == 10 && full[^1] == '\n',
      "Log cuts one oversized line to the room left (cap 10) and reports the cut");
    expect(RunCodeOutput.AppendLine(full, "more", 10) && full.Length == 10, "Log refuses further lines at the cap");

    var fits = new StringBuilder();
    expect(!RunCodeOutput.AppendLine(fits, "abc", 10) && fits.ToString() == "abc\n", "a line that fits is appended with its newline");
    expect(!RunCodeOutput.AppendLine(fits, null, 10) && fits.ToString() == "abc\n\n", "Log(null) appends an empty line");

    var exact = new StringBuilder();
    expect(!RunCodeOutput.AppendLine(exact, "abc", 4) && exact.Length == 4, "a line that fills the cap exactly is not cut");
    expect(RunCodeOutput.AppendLine(exact, string.Empty, 4) && exact.Length == 4, "at the cap even an empty line is refused");

    // Room 3 for "a" + U+1F600 (a surrogate pair): keep "a", drop the whole pair.
    var pair = new StringBuilder("1234567");
    var pairCut = RunCodeOutput.AppendLine(pair, "a\U0001F600", 10);
    var text = pair.ToString();
    expect(pairCut && text == "1234567a\n" && !char.IsHighSurrogate(text[^2]), "a cut never leaves half of a surrogate pair");
  }

  private static void CheckAuth(Action<bool, string> expect)
  {
    var folder = Path.Combine(Path.GetTempPath(), "RunCodeHarness-" + Guid.NewGuid().ToString("N"));
    var defaultPath = RpcAuth.TokenPath;
    try
    {
      RpcAuth.TokenPath = Path.Combine(folder, "rpc-token");
      expect(RpcAuth.Issue() && File.Exists(RpcAuth.TokenPath), "Issue writes the token file, creating its folder");
      var token = File.ReadAllText(RpcAuth.TokenPath);
      expect(token.Length == 64 && token.All(Uri.IsHexDigit), "the token is 64 hex characters");
      expect(Directory.GetFiles(folder).Length == 1, "Issue leaves no temp file behind");
      if (OperatingSystem.IsWindows())
      {
        expect(IsPrivate(RpcAuth.TokenPath), "the token file has a protected DACL with one allow rule, for this user (also its owner)");
      }

      expect(Accepts(token), "Require accepts this session's token");
      expect(Refused(null) && Refused(new JsonObject()), "Require refuses a call without authToken");
      expect(Refused(Token(string.Empty)), "Require refuses an empty authToken");
      expect(Refused(new JsonObject { [RpcAuth.ParameterName] = 42 }), "Require refuses an authToken that is not a string");
      var wrong = token[..^1] + (token[^1] == '0' ? '1' : '0');
      expect(Refused(Token(wrong)), "Require refuses a wrong authToken");
      expect(PluginLog.Warnings.Count >= 4 && PluginLog.Warnings.All(line => !line.Contains(token) && !line.Contains(wrong)),
        "each refusal is logged, without the token");

      RpcAuth.Issue();
      var second = File.ReadAllText(RpcAuth.TokenPath);
      expect(second != token && Refused(Token(token)) && Accepts(second), "a restart issues a new token and refuses the old one");

      File.WriteAllText(RpcAuth.TokenPath, "another instance");
      RpcAuth.Revoke();
      expect(File.ReadAllText(RpcAuth.TokenPath) == "another instance", "Revoke keeps a token file another instance wrote");
      expect(Refused(Token(second)), "after Revoke runCode is refused");

      // Replaces the file written above with the folder's inherited permissions.
      RpcAuth.Issue();
      if (OperatingSystem.IsWindows())
      {
        expect(IsPrivate(RpcAuth.TokenPath), "a token file that replaces one with inherited permissions is private too");
      }

      RpcAuth.Revoke();
      expect(!File.Exists(RpcAuth.TokenPath), "Revoke deletes its own token file");

      RpcAuth.TokenPath = "relative-rpc-token";
      expect(!RpcAuth.Issue() && Refused(Token(second), "could not write its token file"),
        "a token file that cannot be written refuses runCode and says why");
    }
    finally
    {
      RpcAuth.Revoke();
      RpcAuth.TokenPath = defaultPath;
      try
      {
        Directory.Delete(folder, true);
      }
      catch (IOException)
      {
      }
    }
  }

  [SupportedOSPlatform("windows")]
  private static bool IsPrivate(string path)
  {
    using var identity = WindowsIdentity.GetCurrent();
    var security = new FileInfo(path).GetAccessControl();
    var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
    return security.AreAccessRulesProtected
      && Equals(security.GetOwner(typeof(SecurityIdentifier)), identity.User)
      && rules.Count == 1
      && rules[0].AccessControlType == AccessControlType.Allow
      && Equals(rules[0].IdentityReference, identity.User);
  }

  private static JsonObject Token(string token) => new() { [RpcAuth.ParameterName] = token };

  private static bool Accepts(string token)
  {
    try
    {
      RpcAuth.Require(Token(token));
      return true;
    }
    catch (JsonRpcDispatchException)
    {
      return false;
    }
  }

  private static bool Refused(JsonObject? parameters, string because = "Other tools are not affected")
  {
    try
    {
      RpcAuth.Require(parameters);
      return false;
    }
    catch (JsonRpcDispatchException exception)
    {
      return exception.Code == "CIVIL3D.AUTH_REQUIRED" && exception.Message.Contains(because);
    }
  }
}

namespace Civil3DMcpPlugin
{
  // Stand-ins for the plugin types RpcAuth.cs uses: the real ones live in a
  // file that needs AutoCAD (PluginRuntime.cs) or write the real plugin.log.
  internal sealed class JsonRpcDispatchException : Exception
  {
    public JsonRpcDispatchException(string code, string message) : base(message)
    {
      Code = code;
    }

    public string Code { get; }
  }

  internal static class PluginLog
  {
    public static string LogFilePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "plugin.log");
    internal static readonly List<string> Warnings = new();

    public static void Info(string component, string message, Exception? exception = null)
    {
    }

    public static void Warn(string component, string message, Exception? exception = null) => Warnings.Add(message);

    public static void Swallow(string component, string operation, Exception exception)
    {
    }
  }
}
