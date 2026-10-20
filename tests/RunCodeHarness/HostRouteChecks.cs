// Offline checks of HostInputRoute.cs (issue #53): the hop start route
// decisions, the typed-input state machine, the typed start claim against the
// dispatch gate, the quiet-idle start timeout, the evidence that typed
// command-line input ran, the probe ledger and the native guards. Called from
// RuntimeChecks.Run.
using System.Collections.Concurrent;
using Civil3DMcpPlugin;

internal static class HostRouteChecks
{
  internal static void Run(Action<bool, string> expect)
  {
    CheckDecisions(expect);
    CheckTracker(expect);
    CheckQuietWatch(expect);
    CheckTypedInputWatch(expect);
    CheckProbeLedger(expect);
    CheckClaimRace(expect);
    CheckLateHops(expect);
    CheckNativeGuards(expect);
  }

  private static void CheckDecisions(Action<bool, string> expect)
  {
    var route = new HostRouteState();
    expect(route.HopStart == HopStartRoute.Com && route.TypedInput.State == TypedInputState.Unknown
        && (string?)route.Report()["hopStart"] == "com",
      "the hop start route defaults to com and typed input to unknown");

    foreach (var state in Enum.GetValues<TypedInputState>())
    {
      expect(HostRouteDecision.HopStart(HopStartRoute.Com, state) == InputChannel.Com
          && HostRouteDecision.IdleCommandLine(HopStartRoute.Com, state) == InputChannel.Com,
        $"com starts and sends command-line input over COM whatever typed input is ({state})");
      expect(HostRouteDecision.HopStart(HopStartRoute.Typed, state) == InputChannel.Typed,
        $"typed always starts typed ({state})");
    }

    expect(HostRouteDecision.HopStart(HopStartRoute.Auto, TypedInputState.Unknown) == InputChannel.Typed
        && HostRouteDecision.HopStart(HopStartRoute.Auto, TypedInputState.Alive) == InputChannel.Typed
        && HostRouteDecision.HopStart(HopStartRoute.Auto, TypedInputState.Dead) == InputChannel.Com,
      "auto starts typed until typed input is dead, then over COM");
    expect(HostRouteDecision.IdleCommandLine(HopStartRoute.Typed, TypedInputState.Alive) == InputChannel.Typed
        && HostRouteDecision.IdleCommandLine(HopStartRoute.Auto, TypedInputState.Unknown) == InputChannel.Typed
        && HostRouteDecision.IdleCommandLine(HopStartRoute.Typed, TypedInputState.Dead) == InputChannel.Com
        && HostRouteDecision.IdleCommandLine(HopStartRoute.Auto, TypedInputState.Dead) == InputChannel.Com,
      "Idle-route command-line input is typed for typed/auto unless typed input is dead");
    expect(HostRouteDecision.FallsBackToCom(HopStartRoute.Auto)
        && !HostRouteDecision.FallsBackToCom(HopStartRoute.Typed) && !HostRouteDecision.FallsBackToCom(HopStartRoute.Com),
      "only auto falls back to COM after a lost typed start");

    foreach (var value in Enum.GetValues<HopStartRoute>())
    {
      expect(HostRouteDecision.TryParse(HostRouteDecision.Name(value), out var parsed) && parsed == value,
        $"route name {HostRouteDecision.Name(value)} round-trips");
    }

    expect(HostRouteDecision.TryParse(" AUTO ", out var auto) && auto == HopStartRoute.Auto,
      "route names are trimmed and case-insensitive");
    expect(!HostRouteDecision.TryParse("postcommand", out _) && !HostRouteDecision.TryParse(null, out _)
        && !HostRouteDecision.TryParse("", out _),
      "unknown, empty and missing route names are refused");
    route.HopStart = HopStartRoute.Auto;
    expect((string?)route.Report()["hopStart"] == "auto" && route.HopStart == HopStartRoute.Auto,
      "setting the route changes what is reported");
  }

  private static void CheckTracker(Action<bool, string> expect)
  {
    var tracker = new TypedInputTracker();
    var t = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    var report = tracker.Report();
    expect((string?)report["state"] == "unknown" && report["lastProbeUtc"] == null && (int)report["lostStarts"]! == 0,
      "a new tracker reports unknown with no probe");

    tracker.RecordProbe(true, 12.5, t);
    report = tracker.Report();
    expect(tracker.State == TypedInputState.Alive && (double?)report["lastLatencyMs"] == 12.5
        && (string?)report["lastProbeUtc"] == t.ToString("o") && (string?)report["reason"] == "probe",
      "a probe that ran makes typed input alive and records its latency and time");

    expect(tracker.RecordLostStart(true, t.AddSeconds(1)), "the first lost typed start reports a change (logged once)");
    expect(tracker.State == TypedInputState.Dead && (bool?)tracker.Report()["inputPendingAtLastLoss"] == true,
      "a lost typed start makes typed input dead and records whether the string was still pending");
    expect(!tracker.RecordLostStart(null, t.AddSeconds(2)) && (int)tracker.Report()["lostStarts"]! == 2
        && tracker.Report()["inputPendingAtLastLoss"] == null,
      "a second lost start while dead counts but reports no change");

    tracker.RecordLateTypedRun();
    expect(tracker.State == TypedInputState.Dead && (int)tracker.Report()["lateRuns"]! == 1,
      "a late typed hop is counted and dead stays dead (sticky for auto)");
    expect(HostRouteDecision.HopStart(HopStartRoute.Auto, tracker.State) == InputChannel.Com
        && HostRouteDecision.IdleCommandLine(HopStartRoute.Auto, tracker.State) == InputChannel.Com,
      "once dead, auto uses COM for starts and command-line input");

    tracker.RecordProbe(false, null, t.AddSeconds(3));
    report = tracker.Report();
    expect(tracker.State == TypedInputState.Dead && report["lastLatencyMs"] == null && (string?)report["reason"] == "probe-timeout",
      "a probe that timed out keeps typed input dead with no latency");

    tracker.RecordLateProbe(4000, t.AddSeconds(4));
    report = tracker.Report();
    expect(tracker.State == TypedInputState.Alive && (double?)report["lastLatencyMs"] == 4000
        && (string?)report["reason"] == "late-probe" && (int)report["lateRuns"]! == 2,
      "a probe that ran after its timeout makes typed input alive again (delayed, not lost)");

    tracker.RecordLostStart(false, t.AddSeconds(5));
    tracker.RecordTypedStart(21, t.AddSeconds(6));
    report = tracker.Report();
    expect(tracker.State == TypedInputState.Alive && (double?)report["lastStartLatencyMs"] == 21
        && (string?)report["reason"] == "typed-start" && (string?)report["lastChangeUtc"] == t.AddSeconds(6).ToString("o"),
      "a typed start that ran (typed route) makes typed input alive");

    expect((int)tracker.Report()["lostInputs"]! == 0 && tracker.RecordLostInput(true, t.AddSeconds(7)),
      "the first lost typed command-line input reports a change");
    report = tracker.Report();
    expect(tracker.State == TypedInputState.Dead && (string?)report["reason"] == "typed-input-lost"
        && (int)report["lostInputs"]! == 1 && (bool?)report["inputPendingAtLastLoss"] == true
        && (int)report["lostStarts"]! == 3,
      "lost typed command-line input makes typed input dead, counted apart from lost starts");
    expect(!tracker.RecordLostInput(false, t.AddSeconds(8)) && (int)tracker.Report()["lostInputs"]! == 2
        && HostRouteDecision.IdleCommandLine(HopStartRoute.Auto, tracker.State) == InputChannel.Com
        && HostRouteDecision.IdleCommandLine(HopStartRoute.Typed, tracker.State) == InputChannel.Com,
      "after lost typed input, later Idle-route input goes over COM on typed and auto");
  }

  // Ticks every 250 ms (the WM_NULL nudge) from `from` to `to` inclusive: the
  // first tick that reports the quiet window expired, or -1.
  private static long QuietTicks(QuietStartWatch watch, long from, long to, bool quiet = true)
  {
    for (var now = from; now <= to; now += 250)
    {
      if (watch.QuietExpired(now, quiet)) return now;
    }

    return -1;
  }

  private static void CheckQuietWatch(Action<bool, string> expect)
  {
    const long ms = 1000;
    var quiet = new QuietStartWatch(TimeSpan.FromMilliseconds(2000), TimeSpan.FromSeconds(15), 0, ms);
    expect(QuietTicks(quiet, 250, 2000) == 2000,
      "quiet idle time counts from the send, which happened in a quiet tick");

    var busy = new QuietStartWatch(TimeSpan.FromMilliseconds(2000), TimeSpan.FromSeconds(15), 0, ms);
    expect(QuietTicks(busy, 250, 1500) == -1 && !busy.QuietExpired(1750, false) && QuietTicks(busy, 2000, 4500) == 4000,
      "a busy tick restarts the quiet window (a command or prompt holds typed input back)");
    expect(!busy.QuietExpired(20000, false), "a busy tick never declares a start lost");
    expect(!busy.OverallExpired(14999) && busy.OverallExpired(15000),
      "the overall limit ends the wait independently of quiet time");

    // No Idle tick for 3 s after the send: the host was busy without a command
    // (a regen, an autosave, another Idle handler) and could not read the input.
    var gap = new QuietStartWatch(TimeSpan.FromMilliseconds(2000), TimeSpan.FromSeconds(15), 0, ms);
    expect(!gap.QuietExpired(3000, true), "the first quiet tick after a 3 s gap with no Idle tick does not expire");
    expect(QuietTicks(gap, 3250, 5000) == 5000, "after a gap, expiry needs 2 s of ticks spaced 250 ms apart");

    var midGap = new QuietStartWatch(TimeSpan.FromMilliseconds(2000), TimeSpan.FromSeconds(15), 0, ms);
    expect(QuietTicks(midGap, 250, 1500) == -1 && !midGap.QuietExpired(2400, true) && QuietTicks(midGap, 2650, 4650) == 4400,
      "a gap of more than 750 ms between ticks restarts the quiet window");
    var shortGap = new QuietStartWatch(TimeSpan.FromMilliseconds(2000), TimeSpan.FromSeconds(15), 0, ms);
    expect(QuietTicks(shortGap, 250, 1000) == -1 && !shortGap.QuietExpired(1750, true) && shortGap.QuietExpired(2000, true),
      "a gap of up to 750 ms keeps the quiet window");

    var notQuietAtStart = new QuietStartWatch(TimeSpan.FromMilliseconds(2000), TimeSpan.FromSeconds(15), 0, ms,
      quietAtStart: false);
    expect(QuietTicks(notQuietAtStart, 250, 2500) == 2250,
      "input sent from a busy tick counts quiet time from the first quiet tick");
  }

  private static void CheckTypedInputWatch(Action<bool, string> expect)
  {
    const long ms = 1000;
    QuietStartWatch Quiet(bool quietAtStart = true) =>
      new(TimeSpan.FromMilliseconds(2000), TimeSpan.FromSeconds(3), 0, ms, quietAtStart);
    TypedInputVerdict? TickUntil(TypedInputWatch watch, long from, long to, bool idle, bool? pending)
    {
      for (var now = from; now <= to; now += 250)
      {
        if (watch.Tick(now, idle, pending) is { } verdict) return verdict;
      }

      return null;
    }

    expect(TypedInputWatch.CommandName("_.UNDO 2 ") == "UNDO" && TypedInputWatch.CommandName("_.MREDO 1 ") == "MREDO"
        && TypedInputWatch.CommandName("CreateDataShortcuts ") == "CREATEDATASHORTCUTS"
        && TypedInputWatch.CommandName("_AeccSynchronizeReferences ") == "AECCSYNCHRONIZEREFERENCES"
        && TypedInputWatch.CommandName("ExportSSA ") == "EXPORTSSA"
        && TypedInputWatch.CommandName("._LINE ") == "LINE" && TypedInputWatch.CommandName("-PLOT ") == "-PLOT",
      "typed input names its command by its first token without the _ and . prefixes");
    expect(TypedInputWatch.CommandName("\x03\x03") == null && TypedInputWatch.CommandName("(princ) ") == null
        && TypedInputWatch.CommandName("   ") == null,
      "ESC ESC, LISP and empty input name no command");

    // UNDO started and the host is idle again: consumed.
    var undo = new TypedInputWatch(new[] { "_.UNDO 2 " }, Quiet());
    expect(undo.Tick(50, false, false) == null, "a busy tick while UNDO has not started keeps waiting");
    undo.CommandStarted("UNDO");
    expect(undo.Tick(100, true, false) == TypedInputVerdict.Consumed && undo.Missing.Count == 0
        && undo.Final() == TypedInputVerdict.Consumed,
      "typed input is consumed only once its command started and the host is idle");

    // The check before this fix: two quiet ticks with nothing pending passed
    // as consumed. Without a CommandWillStart that is no evidence.
    var discarded = new TypedInputWatch(new[] { "_.UNDO 2 " }, Quiet());
    expect(discarded.Tick(250, true, false) == null && discarded.Tick(500, true, false) == null,
      "quiet ticks with nothing pending are not evidence that UNDO ran (discarded input looks the same)");
    expect(TickUntil(discarded, 750, 3000, true, false) == TypedInputVerdict.Lost && discarded.Final() == TypedInputVerdict.Lost
        && discarded.Missing.SequenceEqual(new[] { "UNDO" }),
      "UNDO that did not start in 2 s of quiet idle time, with no command started, is lost");

    // The wedge signature: input piling up while CMDACTIVE is 0 and the editor
    // is quiescent. Lost too, never "still running".
    var wedged = new TypedInputWatch(new[] { "_.UNDO 1 " }, Quiet());
    expect(TickUntil(wedged, 250, 3000, true, true) == TypedInputVerdict.Lost,
      "input pending while the host is quiet (the wedge) is lost");

    // Busy all the time (a dialog of another command): not started, may run later.
    var busy = new TypedInputWatch(new[] { "_.MREDO 1 " }, Quiet(quietAtStart: false));
    expect(TickUntil(busy, 250, 3000, false, true) == null && busy.Final() == TypedInputVerdict.NotStarted,
      "input that did not start while the host was busy is not started, not lost");

    // A gap with no Idle tick is busy time, not quiet time.
    var gap = new TypedInputWatch(new[] { "_.UNDO 1 " }, Quiet());
    expect(TickUntil(gap, 2500, 3000, true, false) == null && gap.Final() == TypedInputVerdict.NotStarted,
      "no Idle tick for 2.5 s after the send is not quiet time: not lost");

    // Another command started: typed input was read, so not lost.
    var other = new TypedInputWatch(new[] { "_.UNDO 1 " }, Quiet());
    other.CommandStarted("C3DMCPHOP");
    expect(TickUntil(other, 250, 3000, true, false) == null && other.Final() == TypedInputVerdict.NotStarted
        && other.Others.SequenceEqual(new[] { "C3DMCPHOP" }),
      "another command starting instead is reported, not counted as UNDO and not lost");

    // Started, then a dialog keeps it running past the wait.
    var dialog = new TypedInputWatch(new[] { "CreateDataShortcuts " }, Quiet());
    dialog.CommandStarted("createdatashortcuts");
    expect(TickUntil(dialog, 250, 3000, false, false) == null && dialog.Final() == TypedInputVerdict.Running,
      "a started command still in a dialog is running");

    // Several entries: each must start, in order.
    var two = new TypedInputWatch(new[] { "_.UNDO 1 ", "_.MREDO 1 " }, Quiet());
    two.CommandStarted("UNDO");
    expect(two.Tick(250, true, false) == null && two.Missing.SequenceEqual(new[] { "MREDO" }),
      "with two entries, one started command is not enough");
    two.CommandStarted("MREDO");
    expect(two.Tick(500, true, false) == TypedInputVerdict.Consumed, "both commands started: consumed");

    // ESC ESC names no command: the host returning to idle is its evidence.
    var cancel = new TypedInputWatch(new[] { "\x03\x03" }, Quiet(quietAtStart: false));
    expect(cancel.Tick(250, false, false) == null && cancel.Tick(500, true, false) == TypedInputVerdict.Consumed,
      "ESC ESC is consumed once CMDACTIVE is 0 again, after at least two ticks");
    var stuck = new TypedInputWatch(new[] { "\x03\x03" }, Quiet(quietAtStart: false));
    expect(TickUntil(stuck, 250, 3000, false, false) == null && stuck.Final() == TypedInputVerdict.Running,
      "ESC ESC that leaves a command active is running, never lost");

    expect(undo.Tick(5000, false, true) == TypedInputVerdict.Consumed, "a verdict, once reached, does not change");
  }

  private static void CheckProbeLedger(Action<bool, string> expect)
  {
    const long ms = 1000;
    var ledger = new TypedProbeLedger();
    expect(ledger.CommandRan(50, ms) == null, "C3DMCPPROBE typed by hand, with no probe outstanding, is ignored");

    var (first, firstRan) = ledger.Begin(100);
    var run = ledger.CommandRan(118, ms);
    expect(run is { Late: false } && run.Value.Id == first && run.Value.LatencyMs == 18
        && firstRan.IsCompletedSuccessfully && firstRan.Result == 18,
      "a probe that runs in time completes its waiter with the latency");
    expect(!ledger.TimeOut(first) && ledger.CommandRan(130, ms) == null,
      "after a run the probe cannot time out and a second run is ignored");

    var (second, secondRan) = ledger.Begin(200);
    expect(ledger.TimeOut(second), "the waiter gives up on a probe that has not run");
    var late = ledger.CommandRan(5200, ms);
    expect(late is { Late: true } && late.Value.LatencyMs == 5000 && !secondRan.IsCompleted,
      "a run after the timeout is reported late with its latency and leaves the waiter alone");

    var (third, _) = ledger.Begin(6000);
    var (fourth, _) = ledger.Begin(6001);
    expect(fourth > third && !ledger.TimeOut(third) && ledger.TimeOut(fourth),
      "a new probe replaces the outstanding one");
  }

  private static void CheckClaimRace(Action<bool, string> expect)
  {
    for (var attempt = 0; attempt < 32; attempt++)
    {
      var claim = new TypedStartClaim();
      var ran = 0;
      var late = 0;
      var started = 0;
      var item = claim.Wrap(() => { Interlocked.Increment(ref ran); return Task.CompletedTask; },
        () => Interlocked.Increment(ref started), () => Interlocked.Increment(ref late));
      using var go = new ManualResetEventSlim();
      var hop = Task.Run(() => { go.Wait(); return item(); });
      var watcher = Task.Run(() => { go.Wait(); return claim.TryDeclareLost(); });
      go.Set();
      Task.WaitAll(hop, watcher);
      var lost = watcher.Result;
      expect(lost ? ran == 0 && started == 0 && late == 1 && !claim.IsStarted
          : ran == 1 && started == 1 && late == 0 && claim.IsStarted,
        "a typed start and a lost verdict racing produce one outcome: the work runs once or not at all");
    }

    var won = new TypedStartClaim();
    var wonRuns = 0;
    won.Wrap(() => { wonRuns++; return Task.CompletedTask; }, () => { }, () => { })();
    expect(wonRuns == 1 && !won.TryDeclareLost() && !won.TryStart(),
      "a start that already ran cannot be declared lost afterwards, nor start again");
  }

  // The queue below mirrors CivilExecution.PluginCommandWork: each C3DMCPHOP
  // run dequeues and runs every item. RunOnce starts only through the
  // request's RunCodeDispatchGate, as in CivilExecution.RunOnHostAsync.
  private static void CheckLateHops(Action<bool, string> expect)
  {
    var queue = new ConcurrentQueue<Func<Task>>();
    void Hop()
    {
      while (queue.TryDequeue(out var work)) work().GetAwaiter().GetResult();
    }

    void Drop(Func<Task> item)
    {
      var keep = queue.Where(queued => !ReferenceEquals(queued, item)).ToList();
      queue.Clear();
      foreach (var queued in keep) queue.Enqueue(queued);
    }

    (RunCodeDispatchGate Gate, Func<Task> RunOnce, Func<int> Runs) Request()
    {
      var gate = new RunCodeDispatchGate();
      var runs = 0;
      return (gate, () =>
      {
        if (!gate.TryStart()) return Task.CompletedTask;
        runs++;
        gate.CompleteOperation();
        return Task.CompletedTask;
      }, () => runs);
    }

    // auto: typed start lost, item dropped, same work started over COM; the late
    // typed hop runs the COM item, the COM-started hop then finds nothing.
    var (gate, runOnce, runs) = Request();
    var claim = new TypedStartClaim();
    var late = 0;
    var typed = claim.Wrap(runOnce, () => { }, () => late++);
    queue.Enqueue(typed);
    expect(claim.TryDeclareLost(), "the watcher declares the typed start lost");
    Drop(typed);
    queue.Enqueue(runOnce);
    Hop();
    Hop();
    expect(runs() == 1 && gate.OperationDone.IsCompletedSuccessfully && late == 0 && queue.IsEmpty,
      "auto fallback: a late typed hop runs the COM item once, the COM hop runs nothing");

    // The same when the drop missed the typed item: it is inert.
    (gate, runOnce, runs) = Request();
    claim = new TypedStartClaim();
    late = 0;
    typed = claim.Wrap(runOnce, () => { }, () => late++);
    queue.Enqueue(typed);
    claim.TryDeclareLost();
    queue.Enqueue(runOnce);
    Hop();
    Hop();
    expect(runs() == 1 && late == 1 && queue.IsEmpty,
      "a lost typed item left in the queue runs nothing; the work still runs exactly once");

    // typed: the request failed (abandoned) after a lost start; a later request's
    // work runs once, in order, and the stale items stay inert.
    var stale = Request();
    claim = new TypedStartClaim();
    late = 0;
    typed = claim.Wrap(stale.RunOnce, () => { }, () => late++);
    queue.Enqueue(typed);
    claim.TryDeclareLost();
    stale.Gate.CancelUnstartedOperation();
    queue.Enqueue(stale.RunOnce);
    var next = Request();
    queue.Enqueue(next.RunOnce);
    Hop();
    expect(stale.Runs() == 0 && next.Runs() == 1 && late == 1 && stale.Gate.OperationDone.IsCanceled
        && next.Gate.OperationDone.IsCompletedSuccessfully,
      "a late hop after an abandoned request runs only the next request's work, once");

    // A start that won the claim keeps its work: the watcher cannot drop or fail it.
    (gate, runOnce, runs) = Request();
    claim = new TypedStartClaim();
    var startedSignal = false;
    queue.Enqueue(claim.Wrap(runOnce, () => startedSignal = true, () => late++));
    Hop();
    expect(startedSignal && runs() == 1 && !claim.TryDeclareLost(),
      "a typed hop that started the work wins over a later lost verdict");
  }

  private static void CheckNativeGuards(Action<bool, string> expect)
  {
    expect(AcadInputPending.Resolve("kernel32.dll", AcadInputPending.ExportName) == null,
      "acedIsInputPending: a library without the export resolves to null");
    expect(AcadInputPending.Resolve("civil3d-mcp-no-such-library.dll", AcadInputPending.ExportName) == null,
      "acedIsInputPending: a missing library resolves to null");

    var session = HostSessionInfo.Read(IntPtr.Zero);
    expect(new[] { "remoteSession", "connectState", "inputDesktop", "foreground", "minimized" }.All(session.ContainsKey)
        && session.Count == 5,
      "the session report has remoteSession, connectState, inputDesktop, foreground and minimized");
    expect(session["remoteSession"] is bool && session["inputDesktop"] is bool && (bool?)session["minimized"] == false
        && (session["connectState"] is string || session["connectState"] == null),
      "the session report reads in this process (no window: not minimized)");
    expect(HostSessionInfo.ConnectStateName(0) == "active" && HostSessionInfo.ConnectStateName(4) == "disconnected"
        && HostSessionInfo.ConnectStateName(42) == "unknown(42)",
      "WTS connect states are reported by name");
  }
}
