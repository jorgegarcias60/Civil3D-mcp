// Offline checks of HostInputRoute.cs (issue #53): the hop start route
// decisions, the typed-input state machine, the typed start claim against the
// dispatch gate, the quiet-idle start timeout, the probe ledger and the native
// guards. Called from RuntimeChecks.Run.
using System.Collections.Concurrent;
using Civil3DMcpPlugin;

internal static class HostRouteChecks
{
  internal static void Run(Action<bool, string> expect)
  {
    CheckDecisions(expect);
    CheckTracker(expect);
    CheckQuietWatch(expect);
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
  }

  private static void CheckQuietWatch(Action<bool, string> expect)
  {
    const long ms = 1000;
    var quiet = new QuietStartWatch(TimeSpan.FromMilliseconds(2000), TimeSpan.FromSeconds(15), 0, ms);
    expect(!quiet.QuietExpired(1999, true) && quiet.QuietExpired(2000, true),
      "quiet idle time counts from the send, which happened in a quiet tick");

    var busy = new QuietStartWatch(TimeSpan.FromMilliseconds(2000), TimeSpan.FromSeconds(15), 0, ms);
    expect(!busy.QuietExpired(1500, false) && !busy.QuietExpired(1600, true) && !busy.QuietExpired(3599, true)
        && busy.QuietExpired(3600, true),
      "a busy tick restarts the quiet window (a command or prompt holds typed input back)");
    expect(!busy.QuietExpired(20000, false), "a busy tick never declares a start lost");
    expect(!busy.OverallExpired(14999) && busy.OverallExpired(15000),
      "the overall limit ends the wait independently of quiet time");
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
