using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Civil3DMcpPlugin;

// No AutoCAD dependencies (also compiled into tests/RunCodeHarness): the state
// and decisions behind issue #53's diagnostics and the opt-in typed start of
// C3DMCPHOP. CivilExecution.TypedInput.cs drives them from Idle ticks.
//
// Issue #53: after a command started with COM SendCommand (C3DMCPHOP, or a COM
// NETLOAD), input queued with Document.SendStringToExecute and COM PostCommand
// was sometimes lost, now and then for the rest of the session. It is not
// deterministic, it came back after a COM "(princ) " and after Escape of a
// COM-started command, and the 2026-10-11 trials only saw the related stall of
// ExecuteInCommandContextAsync in a disconnected Windows session. The probe and
// the "typed"/"auto" routes exist to measure that live; "com" stays the default.

// How C3DMCPHOP is started. Com is today's route and the default.
internal enum HopStartRoute
{
  Com,
  Typed,
  Auto,
}

// What this session knows about typed input (SendStringToExecute).
internal enum TypedInputState
{
  Unknown,
  Alive,
  Dead,
}

// The channel one operation's C3DMCPHOP start, command-line input and cancel use.
internal enum InputChannel
{
  Com,
  Typed,
}

// Outcome of waiting for a typed C3DMCPHOP start.
internal enum TypedStartOutcome
{
  // The work started inside the command.
  Started,
  // No start within the quiet idle time: typed input looks dead.
  Lost,
  // The host stayed busy (a command, prompt or dialog) until the request's
  // busy timeout: says nothing about typed input.
  Busy,
  // The request was cancelled or timed out first.
  Abandoned,
}

internal static class HostRouteDecision
{
  internal static string Name(HopStartRoute route) => route switch
  {
    HopStartRoute.Typed => "typed",
    HopStartRoute.Auto => "auto",
    _ => "com",
  };

  internal static string Name(TypedInputState state) => state switch
  {
    TypedInputState.Alive => "alive",
    TypedInputState.Dead => "dead",
    _ => "unknown",
  };

  internal static bool TryParse(string? text, out HopStartRoute route)
  {
    switch (text?.Trim().ToLowerInvariant())
    {
      case "com": route = HopStartRoute.Com; return true;
      case "typed": route = HopStartRoute.Typed; return true;
      case "auto": route = HopStartRoute.Auto; return true;
      default: route = HopStartRoute.Com; return false;
    }
  }

  // The channel C3DMCPHOP is started with. "auto" uses typed input until it is
  // known to be dead, then COM directly (no more start timeouts).
  internal static InputChannel HopStart(HopStartRoute route, TypedInputState state) => route switch
  {
    HopStartRoute.Typed => InputChannel.Typed,
    HopStartRoute.Auto => state == TypedInputState.Dead ? InputChannel.Com : InputChannel.Typed,
    _ => InputChannel.Com,
  };

  // After a lost typed start: "auto" starts the same work over COM, "typed"
  // fails the request (nothing ran).
  internal static bool FallsBackToCom(HopStartRoute route) => route == HopStartRoute.Auto;

  // Command-line input queued by an operation on the Idle route (no
  // C3DMCPHOP): typed when the route allows it and typed input is not dead.
  internal static InputChannel IdleCommandLine(HopStartRoute route, TypedInputState state) =>
    route != HopStartRoute.Com && state != TypedInputState.Dead ? InputChannel.Typed : InputChannel.Com;
}

// Session-wide: the hop start route (set per instance with setHostRoute, never
// persisted) and the typed-input state.
internal sealed class HostRouteState
{
  private int hopStart = (int)HopStartRoute.Com;

  internal HopStartRoute HopStart
  {
    get => (HopStartRoute)Volatile.Read(ref hopStart);
    set => Volatile.Write(ref hopStart, (int)value);
  }

  internal TypedInputTracker TypedInput { get; } = new();

  internal Dictionary<string, object?> Report() => new()
  {
    ["hopStart"] = HostRouteDecision.Name(HopStart),
    ["typedInput"] = TypedInput.Report(),
  };
}

// unknown | alive | dead, updated by the probe (C3DMCPPROBE), by typed
// C3DMCPHOP starts and by typed command-line input that was lost. Dead is
// sticky for "auto": once dead, operations start over COM and only a probe
// that runs (or a typed start on the "typed" route) makes it alive again.
// Thread-safe.
internal sealed class TypedInputTracker
{
  private readonly object sync = new();
  private TypedInputState state = TypedInputState.Unknown;
  private string? reason;
  private DateTime? lastChangeUtc;
  private DateTime? lastProbeUtc;
  private double? lastLatencyMs;
  private double? lastStartLatencyMs;
  private bool? inputPendingAtLastLoss;
  private int lostStarts;
  private int lostInputs;
  private int lateRuns;

  internal TypedInputState State { get { lock (sync) return state; } }

  // A probe finished waiting: it ran (alive) or timed out (dead).
  internal void RecordProbe(bool ran, double? latencyMs, DateTime utc)
  {
    lock (sync)
    {
      lastProbeUtc = utc;
      lastLatencyMs = ran ? latencyMs : null;
      Set(ran ? TypedInputState.Alive : TypedInputState.Dead, ran ? "probe" : "probe-timeout", utc);
    }
  }

  // A probe ran after its waiter had given up: input was delayed, not lost.
  internal void RecordLateProbe(double latencyMs, DateTime utc)
  {
    lock (sync)
    {
      lateRuns++;
      lastLatencyMs = latencyMs;
      Set(TypedInputState.Alive, "late-probe", utc);
    }
  }

  internal void RecordTypedStart(double latencyMs, DateTime utc)
  {
    lock (sync)
    {
      lastStartLatencyMs = latencyMs;
      Set(TypedInputState.Alive, "typed-start", utc);
    }
  }

  // A typed C3DMCPHOP start was lost. True when typed input was not dead
  // before, so the caller logs the change once.
  internal bool RecordLostStart(bool? inputPending, DateTime utc)
  {
    lock (sync)
    {
      var wasDead = state == TypedInputState.Dead;
      lostStarts++;
      inputPendingAtLastLoss = inputPending;
      Set(TypedInputState.Dead, "typed-start-lost", utc);
      return !wasDead;
    }
  }

  // Typed command-line input (UNDO, MREDO, a data shortcut or Storm and
  // Sanitary command) did not start its command in quiet idle time and no
  // other command started. True when typed input was not dead before.
  internal bool RecordLostInput(bool? inputPending, DateTime utc)
  {
    lock (sync)
    {
      var wasDead = state == TypedInputState.Dead;
      lostInputs++;
      inputPendingAtLastLoss = inputPending;
      Set(TypedInputState.Dead, "typed-input-lost", utc);
      return !wasDead;
    }
  }

  // A typed C3DMCPHOP ran after its start was declared lost (or its request
  // ended). Counted only: it ran nothing, and dead stays dead for "auto".
  internal void RecordLateTypedRun()
  {
    lock (sync) lateRuns++;
  }

  internal Dictionary<string, object?> Report()
  {
    lock (sync)
    {
      return new Dictionary<string, object?>
      {
        ["state"] = HostRouteDecision.Name(state),
        ["reason"] = reason,
        ["lastChangeUtc"] = lastChangeUtc?.ToString("o"),
        ["lastProbeUtc"] = lastProbeUtc?.ToString("o"),
        ["lastLatencyMs"] = lastLatencyMs,
        ["lastStartLatencyMs"] = lastStartLatencyMs,
        ["lostStarts"] = lostStarts,
        ["lostInputs"] = lostInputs,
        ["lateRuns"] = lateRuns,
        ["inputPendingAtLastLoss"] = inputPendingAtLastLoss,
      };
    }
  }

  private void Set(TypedInputState value, string why, DateTime utc)
  {
    state = value;
    reason = why;
    lastChangeUtc = utc;
  }
}

// Decides atomically between "the typed C3DMCPHOP started this work" (inside
// the command) and "the start was declared lost or abandoned" (the watcher,
// on an Idle tick or the timer thread). Whichever comes first wins.
internal sealed class TypedStartClaim
{
  private const int Pending = 0;
  private const int Started = 1;
  private const int Lost = 2;
  private int state = Pending;

  internal bool IsStarted => Volatile.Read(ref state) == Started;
  internal bool TryStart() => Interlocked.CompareExchange(ref state, Started, Pending) == Pending;
  internal bool TryDeclareLost() => Interlocked.CompareExchange(ref state, Lost, Pending) == Pending;

  // The item queued for a typed C3DMCPHOP start. It runs work only when it
  // wins the claim. After a lost or abandoned verdict it is inert, so a late
  // hop that still finds it in the queue runs nothing. The work itself is
  // RunOnce, which also starts only through the request's
  // RunCodeDispatchGate.TryStart.
  internal Func<Task> Wrap(Func<Task> work, Action started, Action lateRun) => () =>
  {
    if (!TryStart())
    {
      lateRun();
      return Task.CompletedTask;
    }

    started();
    return work();
  };
}

// The start timeout of a typed C3DMCPHOP (and the loss check of typed
// command-line input), in quiet idle time: Idle ticks with CMDACTIVE 0 and a
// quiescent editor. A busy tick restarts the quiet window (a command or prompt
// holds typed input back without losing it), and so does a gap of more than
// MaxGap between ticks: the waiters post WM_NULL every 250 ms, so a longer gap
// means the main thread was busy without a command (a regen, an autosave,
// another Idle handler, Civil 3D rebuilding after the previous write) and had
// no chance to read the input. The overall limit (the request's busy timeout)
// also ends the wait when the host stays busy or raises no Idle at all.
// Stopwatch timestamps.
internal sealed class QuietStartWatch
{
  // Three WM_NULL nudges.
  internal static readonly TimeSpan MaxGap = TimeSpan.FromMilliseconds(750);

  private readonly long quietTicks;
  private readonly long overallTicks;
  private readonly long maxGapTicks;
  private readonly long startedAt;
  private long lastTick;
  private long? quietSince;

  // quietAtStart: the input was sent from a quiet tick (a typed C3DMCPHOP
  // start always is), so quiet time counts from the send.
  internal QuietStartWatch(TimeSpan quiet, TimeSpan overall, long startedAt, long? frequency = null,
    bool quietAtStart = true)
  {
    var ticksPerSecond = frequency ?? Stopwatch.Frequency;
    quietTicks = (long)(quiet.TotalSeconds * ticksPerSecond);
    overallTicks = (long)(overall.TotalSeconds * ticksPerSecond);
    maxGapTicks = (long)(MaxGap.TotalSeconds * ticksPerSecond);
    this.startedAt = startedAt;
    lastTick = startedAt;
    quietSince = quietAtStart ? startedAt : null;
  }

  // Main thread, every Idle tick while waiting.
  internal bool QuietExpired(long now, bool quiet)
  {
    var gap = now - lastTick;
    lastTick = now;
    if (!quiet)
    {
      quietSince = null;
      return false;
    }

    if (quietSince == null || gap > maxGapTicks) quietSince = now;
    return now - quietSince.Value >= quietTicks;
  }

  // Any thread.
  internal bool OverallExpired(long now) => now - startedAt >= overallTicks;
}

// What became of typed command-line input.
internal enum TypedInputVerdict
{
  // Every command it names started, and the host is idle again.
  Consumed,
  // Every command it names started; one is still running (a dialog or prompt).
  Running,
  // A command it names has not started, while the host was busy or another
  // command started: it may still run later.
  NotStarted,
  // A command it names did not start in quiet idle time and no command started
  // at all: typed input looks dead. The input may still run later.
  Lost,
}

// Positive evidence that typed command-line input ran (issue #53). Each entry
// names the command it starts by its first token ("_.UNDO 2 " starts UNDO), and
// counts only when Document.CommandWillStart reports that command. Input that
// names no command (ESC ESC) is checked by CMDACTIVE returning to 0 instead.
// "Input no longer pending" proves nothing: acedIsInputPending is also false
// when the strings were discarded. Thread-safe.
internal sealed class TypedInputWatch
{
  private readonly object sync = new();
  private readonly string?[] expected;
  private readonly bool[] started;
  private readonly List<string> others = new();
  private readonly QuietStartWatch quiet;
  private int ticks;
  private TypedInputVerdict? verdict;

  internal TypedInputWatch(IReadOnlyList<string> input, QuietStartWatch quiet)
  {
    expected = input.Select(CommandName).ToArray();
    started = new bool[expected.Length];
    this.quiet = quiet;
  }

  // The global name of the command an entry starts: its first token without
  // the "_" (global name) and "." (built-in) prefixes, upper case. Null for
  // input that names no command (control characters, empty, LISP).
  internal static string? CommandName(string input)
  {
    var token = input.TrimStart().Split(new[] { ' ', '\t', '\r', '\n' }, 2)[0].TrimStart('_', '.');
    return token.Length > 0 && token.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '$')
      ? token.ToUpperInvariant()
      : null;
  }

  // Document.CommandWillStart (main thread): marks the first entry still
  // waiting for that command, in order.
  internal void CommandStarted(string? globalName)
  {
    var name = globalName?.Trim().ToUpperInvariant() ?? string.Empty;
    lock (sync)
    {
      for (var i = 0; i < expected.Length; i++)
      {
        if (started[i] || expected[i] == null || expected[i] != name) continue;
        started[i] = true;
        return;
      }

      others.Add(name);
    }
  }

  internal IReadOnlyList<string> Missing
  {
    get
    {
      lock (sync)
        return expected.Where((name, i) => name != null && !started[i]).Select(name => name!).ToList();
    }
  }

  internal IReadOnlyList<string> Others { get { lock (sync) return others.ToList(); } }

  // Idle tick (main thread), with CMDACTIVE 0 and a quiescent editor as quiet.
  // Returns the verdict once the wait can end, null to keep waiting.
  internal TypedInputVerdict? Tick(long now, bool idle, bool? inputPending)
  {
    lock (sync)
    {
      if (verdict != null) return verdict;
      ticks++;
      // Every tick, so a busy tick or a gap restarts the quiet window.
      var quietExpired = quiet.QuietExpired(now, idle);
      var allStarted = AllStarted();
      if (allStarted && ticks >= 2 && idle && inputPending != true) verdict = TypedInputVerdict.Consumed;
      else if (!allStarted && quietExpired && others.Count == 0) verdict = TypedInputVerdict.Lost;
      return verdict;
    }
  }

  // The wait timed out (any thread).
  internal TypedInputVerdict Final()
  {
    lock (sync)
    {
      verdict ??= AllStarted() ? TypedInputVerdict.Running : TypedInputVerdict.NotStarted;
      return verdict.Value;
    }
  }

  private bool AllStarted()
  {
    for (var i = 0; i < expected.Length; i++)
    {
      if (expected[i] != null && !started[i]) return false;
    }

    return true;
  }
}

// One typed-input probe at a time (probes run under the host gate). Matches a
// C3DMCPPROBE run to the probe that typed it. A run after the waiter gave up is
// late: input was delayed, not lost. A run with no probe outstanding (typed by
// hand) is ignored. Two strings typed by consecutive probes cannot be told
// apart, so a stalled one that arrives during a later probe counts for that
// probe. Thread-safe.
internal sealed class TypedProbeLedger
{
  internal readonly record struct ProbeRun(long Id, double LatencyMs, bool Late);

  private readonly object sync = new();
  private long sequence;
  private long pendingId;
  private long pendingStart;
  private bool pendingTimedOut;
  private TaskCompletionSource<double>? pendingRan;

  // Just before typing the probe. Ran completes with the latency in ms.
  internal (long Id, Task<double> Ran) Begin(long now)
  {
    lock (sync)
    {
      pendingId = ++sequence;
      pendingStart = now;
      pendingTimedOut = false;
      pendingRan = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
      return (pendingId, pendingRan.Task);
    }
  }

  // C3DMCPPROBE ran (main thread). Null when no probe is outstanding.
  internal ProbeRun? CommandRan(long now, long? frequency = null)
  {
    lock (sync)
    {
      if (pendingRan == null) return null;
      var latency = (now - pendingStart) * 1000.0 / (frequency ?? Stopwatch.Frequency);
      var run = new ProbeRun(pendingId, Math.Round(latency, 1), pendingTimedOut);
      var ran = pendingRan;
      pendingRan = null;
      if (!run.Late) ran.TrySetResult(run.LatencyMs);
      return run;
    }
  }

  // The waiter gave up. False when the probe ran first: Ran then has the latency.
  internal bool TimeOut(long id)
  {
    lock (sync)
    {
      if (pendingRan == null || pendingId != id) return false;
      pendingTimedOut = true;
      return true;
    }
  }
}

// acedIsInputPending (accore.dll): whether characters wait in the active
// document's input buffer. Characters piling up mean the consumer stalled;
// none mean typed strings are discarded. Null when the library or the export
// is missing (another release, or the offline harness).
internal static class AcadInputPending
{
  internal const string Library = "accore.dll";
  internal const string ExportName = "?acedIsInputPending@@YA_NXZ";

  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  internal delegate byte IsInputPendingFunction();

  private static readonly Lazy<IsInputPendingFunction?> Function = new(() => Resolve(Library, ExportName));

  internal static IsInputPendingFunction? Resolve(string library, string export)
  {
    try
    {
      if (!NativeLibrary.TryLoad(library, out var handle)) return null;
      if (!NativeLibrary.TryGetExport(handle, export, out var address)) return null;
      return Marshal.GetDelegateForFunctionPointer<IsInputPendingFunction>(address);
    }
    catch (Exception)
    {
      return null;
    }
  }

  // Main thread only.
  internal static bool? Read()
  {
    var function = Function.Value;
    if (function == null) return null;
    try
    {
      return function() != 0;
    }
    catch (Exception)
    {
      return null;
    }
  }
}

// The Windows session Civil 3D runs in, for the probe report: the 2026-10-11
// trials tied the stalled queue to a disconnected session, and typed input
// waits for focus in some cases. Any thread; each value is null when its call
// fails.
internal static class HostSessionInfo
{
  private const int SmRemoteSession = 0x1000;
  private const int WtsConnectState = 8;
  private const uint DesktopReadObjects = 0x0001;
  private static readonly IntPtr WtsCurrentServer = IntPtr.Zero;
  private const int WtsCurrentSession = -1;

  internal static Dictionary<string, object?> Read(IntPtr mainWindow) => new()
  {
    ["remoteSession"] = Try(() => GetSystemMetrics(SmRemoteSession) != 0),
    ["connectState"] = ConnectState(),
    ["inputDesktop"] = Try(HasInputDesktop),
    ["foreground"] = Try(() => IsOwnForegroundWindow()),
    ["minimized"] = Try(() => mainWindow != IntPtr.Zero && IsIconic(mainWindow)),
  };

  internal static string ConnectStateName(int value) => value switch
  {
    0 => "active",
    1 => "connected",
    2 => "connectQuery",
    3 => "shadow",
    4 => "disconnected",
    5 => "idle",
    6 => "listen",
    7 => "reset",
    8 => "down",
    9 => "init",
    _ => $"unknown({value})",
  };

  private static string? ConnectState()
  {
    try
    {
      if (!WTSQuerySessionInformationW(WtsCurrentServer, WtsCurrentSession, WtsConnectState, out var buffer, out var bytes))
        return null;
      try
      {
        return bytes >= sizeof(int) ? ConnectStateName(Marshal.ReadInt32(buffer)) : null;
      }
      finally
      {
        WTSFreeMemory(buffer);
      }
    }
    catch (Exception)
    {
      return null;
    }
  }

  // Fails in a disconnected session and while the secure desktop (lock
  // screen, UAC) has the input.
  private static bool HasInputDesktop()
  {
    var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
    if (desktop == IntPtr.Zero) return false;
    CloseDesktop(desktop);
    return true;
  }

  private static bool IsOwnForegroundWindow()
  {
    var window = GetForegroundWindow();
    if (window == IntPtr.Zero) return false;
    GetWindowThreadProcessId(window, out var processId);
    return processId == (uint)Environment.ProcessId;
  }

  private static bool? Try(Func<bool> read)
  {
    try
    {
      return read();
    }
    catch (Exception)
    {
      return null;
    }
  }

  [DllImport("user32.dll")]
  private static extern int GetSystemMetrics(int index);

  [DllImport("user32.dll", SetLastError = true)]
  private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

  [DllImport("user32.dll", SetLastError = true)]
  private static extern bool CloseDesktop(IntPtr desktop);

  [DllImport("user32.dll")]
  private static extern IntPtr GetForegroundWindow();

  [DllImport("user32.dll")]
  private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

  [DllImport("user32.dll")]
  private static extern bool IsIconic(IntPtr window);

  [DllImport("wtsapi32.dll", SetLastError = true)]
  private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

  [DllImport("wtsapi32.dll")]
  private static extern void WTSFreeMemory(IntPtr memory);
}
