using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Civil3DMcpPlugin;

// Issue #53 diagnostics: the typed-input probe (C3DMCPPROBE) and the opt-in
// typed start of C3DMCPHOP (setHostRoute "typed" or "auto"). The default route
// is "com", which never reaches this file's typed paths. The state machine and
// the decisions are AutoCAD-free, in HostInputRoute.cs.
//
// Document.SendStringToExecute is called in one place, SendTyped, from three
// callers only: the typed C3DMCPHOP start, typed command-line input after an
// operation, and the probe (tests/host_routes.test.ts holds this).
public static partial class CivilExecution
{
  // Not persisted: a restart starts with "com" again.
  internal static readonly HostRouteState Route = new();

  internal const string ProbeCommandName = "C3DMCPPROBE";

  // Quiet idle time a typed C3DMCPHOP start, or the command of typed
  // command-line input, may take before it counts as lost. Healthy typed
  // starts and command-context callbacks took 12-51 ms live.
  private static readonly TimeSpan TypedStartQuietTimeout = TimeSpan.FromMilliseconds(2000);

  // How long typed command-line input may take to run before the request
  // completes anyway (as SendCommandLineAsync waits on the COM channel).
  private static readonly TimeSpan TypedInputWait = TimeSpan.FromSeconds(3);

  private static readonly TypedProbeLedger ProbeLedger = new();

  public static object? SetHostRoute(JsonObject? parameters)
  {
    var text = PluginRuntime.GetRequiredString(parameters, "hopStart");
    if (!HostRouteDecision.TryParse(text, out var route))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "hopStart must be \"com\", \"typed\" or \"auto\".");

    // Read once per operation in RunOnHostAsync: this changes the next
    // operation, never the channels of one that is running.
    var previous = Route.HopStart;
    Route.HopStart = route;
    if (previous != route)
      PluginLog.Info("Host", $"Hop start route set to {HostRouteDecision.Name(route)} (was {HostRouteDecision.Name(previous)}); not persisted.");
    var report = Route.Report();
    report["previous"] = HostRouteDecision.Name(previous);
    return report;
  }

  // The C3DMCPPROBE command (main thread). It touches no database and only
  // records that it ran and when; it is flagged NoUndoMarker so it should
  // leave no undo step (not yet verified live). A run after the probe's waiter
  // gave up still counts. Such a late string can also answer a prompt that is
  // open by then, which is why the probe is meant for an idle Command: prompt.
  internal static void ProbeCommandRan()
  {
    var run = ProbeLedger.CommandRan(Stopwatch.GetTimestamp());
    if (run is { Late: true } late)
    {
      Route.TypedInput.RecordLateProbe(late.LatencyMs, DateTime.UtcNow);
      PluginLog.Info("Host", $"{ProbeCommandName} ran {late.LatencyMs:0} ms after it was typed, after its probe had timed out: typed input was delayed, not lost.");
    }
  }

  // probeTypedInput: types C3DMCPPROBE with SendStringToExecute and waits for
  // it to run. Serialized with every other host operation, and sent only from
  // an Idle tick with CMDACTIVE 0 and a quiescent editor, so the probe never
  // answers a prompt.
  public static Task<object?> ProbeTypedInputAsync(JsonObject? parameters)
  {
    var timeoutMs = Math.Clamp(PluginRuntime.GetOptionalInt(parameters, "timeoutMs") ?? 3000, 250, 10000);
    var timeout = TimeSpan.FromMilliseconds(timeoutMs);
    return ExecuteSerializedAsync<object?>(async _ =>
    {
      var session = HostSessionInfo.Read(HostMainWindow());
      Dictionary<string, object?> Result(bool ran, string? skipped, double? latencyMs = null, bool? before = null,
        bool? after = null, string? cmdnames = null, int? cmdactive = null)
      {
        var result = new Dictionary<string, object?>
        {
          ["ran"] = ran,
          ["latencyMs"] = latencyMs,
          ["timeoutMs"] = timeoutMs,
          ["inputPendingBefore"] = before,
          ["inputPendingAfter"] = after,
          ["cmdnames"] = cmdnames,
          ["cmdactive"] = cmdactive,
          ["session"] = session,
          ["typedInput"] = Route.TypedInput.Report(),
        };
        if (skipped != null) result["skipped"] = skipped;
        return result;
      }

      (long Id, Task<double> Ran, bool? Before)? sent;
      string? skipped = null;
      // Read before deciding to skip: when the host is busy, which command
      // holds it is what the experiment needs to see.
      string? busyCmdnames = null;
      int? busyCmdactive = null;
      try
      {
        sent = await RunOnIdleAsync<(long Id, Task<double> Ran, bool? Before)?>(() =>
        {
          var doc = App.DocumentManager.MdiActiveDocument;
          if (doc == null)
          {
            skipped = "no-drawing";
            return null;
          }

          var cmdactive = Convert.ToInt32(CoreApp.GetSystemVariable("CMDACTIVE"));
          var cmdnames = Convert.ToString(CoreApp.GetSystemVariable("CMDNAMES"));
          if (cmdactive != 0 || !doc.Editor.IsQuiescent)
          {
            skipped = "busy";
            busyCmdnames = cmdnames;
            busyCmdactive = cmdactive;
            return null;
          }

          var before = AcadInputPending.Read();
          var (id, ran) = ProbeLedger.Begin(Stopwatch.GetTimestamp());
          SendTyped(doc, "_" + ProbeCommandName + " ");
          return (id, ran, before);
        }, HostBusyTimeout);
      }
      catch (TimeoutException)
      {
        return Result(false, "no-idle");
      }

      if (sent is not { } probe) return Result(false, skipped, cmdnames: busyCmdnames, cmdactive: busyCmdactive);

      await PollOnIdleAsync(() => probe.Ran.IsCompleted, timeout);
      double? latency = null;
      // TimeOut is false when the command ran just before the deadline.
      var ran = probe.Ran.IsCompleted || !ProbeLedger.TimeOut(probe.Id);
      if (ran) latency = await probe.Ran;
      Route.TypedInput.RecordProbe(ran, latency, DateTime.UtcNow);

      bool? after = null;
      string? cmdnamesAfter = null;
      int? cmdactiveAfter = null;
      try
      {
        (after, cmdnamesAfter, cmdactiveAfter) = await RunOnIdleAsync(
          () => (AcadInputPending.Read(), Convert.ToString(CoreApp.GetSystemVariable("CMDNAMES")),
            (int?)Convert.ToInt32(CoreApp.GetSystemVariable("CMDACTIVE"))),
          TimeSpan.FromSeconds(2));
      }
      catch (TimeoutException)
      {
        // No Idle tick after the wait: report nulls.
      }

      if (!ran)
        PluginLog.Warn("Host", $"Typed-input probe: {ProbeCommandName} did not run within {timeoutMs} ms (input pending before {Describe(probe.Before)}, after {Describe(after)}); typed input is marked dead (issue #53).");
      return Result(ran, null, latency, probe.Before, after, cmdnamesAfter, cmdactiveAfter);
    });
  }

  // Issue #53, opt-in ("typed", or "auto" while typed input is not dead). Main
  // thread (an Idle tick). Queues the work and starts C3DMCPHOP with
  // SendStringToExecute. Completes when the work has started inside the
  // command. A start that does not come within TypedStartQuietTimeout of quiet
  // idle time is lost: typed input is marked dead and the work is dropped
  // (as on the COM not-started path); then "typed" fails the request and
  // "auto" starts the same work over COM (channel becomes Com, so the
  // operation's command-line input and cancel go over COM too).
  //
  // Why a late typed C3DMCPHOP cannot run work out of order or twice:
  //  - the queued item is TypedStartClaim.Wrap(work): "started" (at the top of
  //    the hop's work) and "lost/abandoned" (this watcher) compete atomically,
  //    so a hop arriving after the verdict finds the item inert even if
  //    DropQueuedWork missed it;
  //  - work is RunOnce, which starts only through the request's
  //    RunCodeDispatchGate.TryStart: at most once, never after abandonment;
  //  - HostExecutionGate serializes requests, so the only live item in
  //    PluginCommandWork belongs to the current request; a stale item of an
  //    earlier request is inert by the two points above. A late typed hop that
  //    dequeues the "auto" fallback's COM item runs that same request's work,
  //    once, inside a C3DMCPHOP command (the context it needs); the COM-started
  //    hop then finds nothing, and the command-line input still waits for both
  //    SendCommand and the operation to end.
  // Not prevented: a stalled "_C3DMCPHOP " delivered later while a command is
  // at a prompt answers that prompt. That is the risk this route exists to
  // measure (inputPendingAtLastLoss in civil3d_health shows whether the string
  // was still queued), and why the route is opt-in.
  private static async Task StartPluginCommandTypedAsync(Document doc, Func<Task> work, Func<bool> finished,
    HopStartRoute hopStart, StrongBox<InputChannel> channel, TimeSpan busyTimeout)
  {
    // Fetched now, on the main thread, for a possible COM fallback.
    var acadDocument = HostRouteDecision.FallsBackToCom(hopStart) ? doc.GetAcadDocument() : null;
    var claim = new TypedStartClaim();
    var verdict = new TaskCompletionSource<TypedStartOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
    // Taken at the top of the hop's work: the verdict's continuation runs on
    // the main thread only after the work's synchronous part has returned.
    long startedAt = 0;
    var typedWork = claim.Wrap(work,
      started: () =>
      {
        Volatile.Write(ref startedAt, Stopwatch.GetTimestamp());
        verdict.TrySetResult(TypedStartOutcome.Started);
      },
      lateRun: Route.TypedInput.RecordLateTypedRun);
    var sentAt = Stopwatch.GetTimestamp();
    PluginCommandWork.Enqueue(typedWork);
    try
    {
      SendTyped(doc, "_" + PluginCommandName + " ");
    }
    catch
    {
      claim.TryDeclareLost();
      DropQueuedWork(typedWork);
      throw;
    }

    TypedStartOutcome outcome;
    bool? inputPending;
    try
    {
      (outcome, inputPending) = await WaitForTypedStartAsync(claim, verdict,
        new QuietStartWatch(TypedStartQuietTimeout, busyTimeout, sentAt), finished);
    }
    catch
    {
      // The watcher failed and declared the claim lost: the item is inert.
      DropQueuedWork(typedWork);
      throw;
    }

    switch (outcome)
    {
      case TypedStartOutcome.Started:
        Route.TypedInput.RecordTypedStart(ElapsedMs(sentAt, Volatile.Read(ref startedAt)), DateTime.UtcNow);
        return;
      case TypedStartOutcome.Abandoned:
        // The request was cancelled or timed out first; it reports that itself.
        DropQueuedWork(typedWork);
        return;
      case TypedStartOutcome.Busy:
        DropQueuedWork(typedWork);
        throw new JsonRpcDispatchException(
          "CIVIL3D.HOST_BUSY",
          $"Civil 3D did not start the typed {PluginCommandName} command while a command, prompt or dialog was active. Finish it or press Esc in Civil 3D, then retry. No drawing changes were made.");
    }

    DropQueuedWork(typedWork);
    if (Route.TypedInput.RecordLostStart(inputPending, DateTime.UtcNow))
    {
      PluginLog.Warn("Host",
        $"Typed {PluginCommandName} start was lost after {TypedStartQuietTimeout.TotalMilliseconds:0} ms of quiet idle time (input pending: {Describe(inputPending)}); typed input is marked dead (issue #53)." +
        (HostRouteDecision.FallsBackToCom(hopStart) ? " Route auto starts with COM until a probe runs." : string.Empty));
    }

    if (!HostRouteDecision.FallsBackToCom(hopStart))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.HOST_BUSY",
        "typed hop start was lost; nothing ran. Typed input (SendStringToExecute) looks dead in this Civil 3D session (issue #53); set civil3d_health hopStart to \"com\" or \"auto\", or retry. No drawing changes were made.");
    }

    // Same operation, COM start; never both channels for its input.
    channel.Value = InputChannel.Com;
    await StartPluginCommandAsync(doc, work, finished, acadDocument);
  }

  // Waits for the verdict on a typed start. Idle ticks (main thread) measure
  // quiet idle time; the timer keeps Idle coming (WM_NULL every 250 ms, as
  // RunOnHostAsync does), notices abandonment, and ends the wait at the
  // overall limit, also when no Idle tick arrives (a modal dialog).
  private static async Task<(TypedStartOutcome Outcome, bool? InputPending)> WaitForTypedStartAsync(
    TypedStartClaim claim, TaskCompletionSource<TypedStartOutcome> verdict, QuietStartWatch watch, Func<bool> finished)
  {
    bool? inputPending = null;
    EventHandler? idle = null;
    idle = (_, _) =>
    {
      // An exception escaping an Idle handler would reach the host.
      try
      {
        if (verdict.Task.IsCompleted)
        {
          CoreApp.Idle -= idle;
          return;
        }

        // finished: the request's gate left NotStarted. Started work won the
        // claim first, so a pending claim here means the request was abandoned.
        if (finished())
        {
          if (claim.TryDeclareLost()) verdict.TrySetResult(TypedStartOutcome.Abandoned);
          return;
        }

        if (!watch.QuietExpired(Stopwatch.GetTimestamp(), HostIsIdle())) return;
        var pending = AcadInputPending.Read();
        if (claim.TryDeclareLost())
        {
          inputPending = pending;
          verdict.TrySetResult(TypedStartOutcome.Lost);
        }
      }
      catch (Exception ex)
      {
        if (claim.TryDeclareLost()) verdict.TrySetException(ex);
      }
    };
    CoreApp.Idle += idle;

    var mainWindow = HostMainWindow();
    using var nudge = new Timer(_ =>
    {
      if (verdict.Task.IsCompleted) return;
      if (finished())
      {
        if (claim.TryDeclareLost()) verdict.TrySetResult(TypedStartOutcome.Abandoned);
        return;
      }

      if (watch.OverallExpired(Stopwatch.GetTimestamp()))
      {
        if (claim.TryDeclareLost()) verdict.TrySetResult(TypedStartOutcome.Busy);
        return;
      }

      if (mainWindow != IntPtr.Zero)
      {
        PostMessage(mainWindow, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
      }
    }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

    try
    {
      var outcome = await verdict.Task;
      return (outcome, inputPending);
    }
    finally
    {
      CoreApp.Idle -= idle;
    }
  }

  // Typed counterpart of SendCommandLineAsync for an operation whose channel is
  // Typed. Queued from an Idle tick (application context), so AutoCAD runs it
  // after C3DMCPHOP has ended, in order, and never inside a COM-started
  // command. Then waits up to TypedInputWait for evidence that it ran
  // (TypedInputWatch): the command each entry names must start
  // (Document.CommandWillStart), then the host must be idle again, so the next
  // request does not start before UNDO has run. ESC ESC names no command; the
  // host returning to idle is its evidence. A dialog or prompt keeps a started
  // command running; then the request completes anyway, as on the COM path.
  // Returns a warning for the operation's result when a command did not
  // start; when no command started at all in quiet idle time, typed input is
  // also marked dead. Never resent over COM: the typed string may still run.
  // Best effort: a failure is logged and never replaces the operation's result.
  private static async Task<string?> SendTypedInputAsync(Document doc, IReadOnlyList<string> input)
  {
    var description = string.Join(", ", input.Select(text => text == "\x03\x03" ? "ESC ESC" : text.Trim()));
    CommandEventHandler? commandStarted = null;
    try
    {
      var watch = await RunOnIdleAsync(() =>
      {
        var sentAt = Stopwatch.GetTimestamp();
        var created = new TypedInputWatch(input,
          new QuietStartWatch(TypedStartQuietTimeout, TypedInputWait, sentAt, quietAtStart: HostIsIdle()));
        // Subscribed before the input is queued, so its command cannot start unseen.
        commandStarted = (_, e) => created.CommandStarted(e.GlobalCommandName);
        doc.CommandWillStart += commandStarted;
        foreach (var text in input) SendTyped(doc, text);
        return created;
      }, TimeSpan.FromSeconds(3));

      bool? pending = null;
      await PollOnIdleAsync(() =>
      {
        pending = AcadInputPending.Read();
        return watch.Tick(Stopwatch.GetTimestamp(), HostIsIdle(), pending) != null;
      }, TypedInputWait);

      var missing = string.Join(", ", watch.Missing);
      switch (watch.Final())
      {
        case TypedInputVerdict.Consumed:
          return null;
        case TypedInputVerdict.Running:
          PluginLog.Info("Host", $"Typed command-line input '{description}' is still running (a dialog or prompt); not waiting for it.");
          return null;
        case TypedInputVerdict.Lost:
          var markedDead = Route.TypedInput.RecordLostInput(pending, DateTime.UtcNow);
          PluginLog.Warn("Host",
            $"Typed command-line input '{description}' was not consumed: {missing} did not start in {TypedStartQuietTimeout.TotalMilliseconds:0} ms of quiet idle time and no other command started (input pending: {Describe(pending)}); " +
            (markedDead ? "typed input is marked dead" : "typed input was already dead") +
            " (issue #53). It may still run later; it is not resent over COM.");
          return $"Typed input was not consumed: {missing} did not start, so this request may have had no effect yet, and the input may still run later. Typed input is marked dead in this Civil 3D session (issue #53). Check the drawing before the next change.";
        default:
          var others = watch.Others;
          var instead = others.Count > 0 ? $"; {string.Join(", ", others)} started instead" : "; Civil 3D was busy";
          PluginLog.Warn("Host", $"Typed command-line input '{description}' has not started {missing} within {TypedInputWait.TotalSeconds:0} s{instead}; it may still run later (issue #53).");
          return $"Typed input has not run yet: {missing} did not start within {TypedInputWait.TotalSeconds:0} s{instead}. It may still run later. Check the drawing before the next change.";
      }
    }
    catch (Exception ex)
    {
      PluginLog.Warn("Host", $"Typed command-line input '{description}' could not be sent.", ex);
      return $"Typed input could not be sent ({ex.Message}), so this request may have had no effect. Check the drawing before the next change.";
    }
    finally
    {
      if (commandStarted != null) doc.CommandWillStart -= commandStarted;
    }
  }

  // The one SendStringToExecute call. Main thread, application context (an
  // Idle tick), so the string is queued and runs when AutoCAD next reads
  // input. activate false: never switches documents.
  private static void SendTyped(Document doc, string text) =>
    doc.SendStringToExecute(text, false, false, false);

  // Runs action once on the next Application.Idle tick (main thread,
  // application context), posting WM_NULL every 250 ms so a background Civil
  // 3D raises Idle. Without a tick within noIdleTimeout the action never runs
  // and the task fails with TimeoutException. The tick and the timeout claim
  // the run atomically, so an action that started always completes and reports.
  private static async Task<T> RunOnIdleAsync<T>(Func<T> action, TimeSpan noIdleTimeout)
  {
    var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    var claim = 0;
    EventHandler? idle = null;
    idle = (_, _) =>
    {
      CoreApp.Idle -= idle;
      if (Interlocked.CompareExchange(ref claim, 1, 0) != 0) return;
      try
      {
        result.TrySetResult(action());
      }
      catch (Exception ex)
      {
        result.TrySetException(ex);
      }
    };
    CoreApp.Idle += idle;

    var mainWindow = HostMainWindow();
    var deadline = new IdleDeadline(noIdleTimeout);
    using var nudge = new Timer(_ =>
    {
      if (Volatile.Read(ref claim) != 0) return;
      if (deadline.IsExpired())
      {
        if (Interlocked.CompareExchange(ref claim, 2, 0) == 0)
          result.TrySetException(new TimeoutException($"No Application.Idle tick within {noIdleTimeout.TotalSeconds:g} seconds."));
        return;
      }

      if (mainWindow != IntPtr.Zero)
      {
        PostMessage(mainWindow, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
      }
    }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

    try
    {
      return await result.Task;
    }
    finally
    {
      CoreApp.Idle -= idle;
    }
  }

  // Calls poll on Application.Idle ticks (main thread) until it returns true or
  // timeout passes, keeping Idle coming with WM_NULL. False on timeout. poll
  // must have no effect that matters when the timeout wins the race.
  private static async Task<bool> PollOnIdleAsync(Func<bool> poll, TimeSpan timeout)
  {
    var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    EventHandler? idle = null;
    idle = (_, _) =>
    {
      if (result.Task.IsCompleted)
      {
        CoreApp.Idle -= idle;
        return;
      }

      try
      {
        if (!poll()) return;
        CoreApp.Idle -= idle;
        result.TrySetResult(true);
      }
      catch (Exception ex)
      {
        CoreApp.Idle -= idle;
        result.TrySetException(ex);
      }
    };
    CoreApp.Idle += idle;

    var mainWindow = HostMainWindow();
    var deadline = new IdleDeadline(timeout);
    using var nudge = new Timer(_ =>
    {
      if (result.Task.IsCompleted) return;
      if (deadline.IsExpired())
      {
        result.TrySetResult(false);
        return;
      }

      if (mainWindow != IntPtr.Zero)
      {
        PostMessage(mainWindow, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
      }
    }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

    try
    {
      return await result.Task;
    }
    finally
    {
      CoreApp.Idle -= idle;
    }
  }

  private static IntPtr HostMainWindow()
  {
    using var host = Process.GetCurrentProcess();
    return host.MainWindowHandle;
  }

  private static double ElapsedMs(long since, long until) =>
    Math.Round((until - since) * 1000.0 / Stopwatch.Frequency, 1);

  // CMDACTIVE 0 and a quiescent editor (main thread).
  private static bool HostIsIdle() =>
    Convert.ToInt32(CoreApp.GetSystemVariable("CMDACTIVE")) == 0
    && App.DocumentManager.MdiActiveDocument?.Editor.IsQuiescent != false;

  private static string Describe(bool? value) => value switch { true => "yes", false => "no", null => "unknown" };
}
