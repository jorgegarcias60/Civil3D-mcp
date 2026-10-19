using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Civil3DMcpPlugin;

public readonly record struct RunCodeHost(Document? Doc, CivilDocument? Civil, Database? Database, Transaction? Transaction);

public sealed class RunCodeActivation
{
  public bool Activated { get; internal set; }
  public bool SwitchedBack { get; internal set; }
  public string? Warning { get; internal set; }

  internal void AddWarning(string warning) => Warning = Warning == null ? warning : $"{Warning}; {warning}";
}

public static partial class CivilExecution
{
  private static readonly SemaphoreSlim HostExecutionGate = new(1, 1);

  // Where an operation runs. Nothing uses DocumentManager
  // .ExecuteInCommandContextAsync any more: on Civil 3D 2027, in a
  // disconnected Windows session, it stopped running callbacks for the rest of
  // the session as soon as any COM client (another process, or this plugin)
  // had run a command with SendCommand (see RunOnHostAsync). Reads run from an
  // Application.Idle tick; everything that changes the drawing, drives
  // commands, or creates scratch objects runs inside the plugin's own
  // C3DMCPHOP command, started over COM (or typed, opt-in for issue #53: see
  // CivilExecution.TypedInput.cs).
  private enum HostRoute
  {
    Idle,
    PluginCommand,
  }

  public static async Task<T> ExecuteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action, bool write, bool inCommand = false, bool commandLine = false)
  {
    return await ExecuteSerializedAsync(cancellationToken => ExecuteOnHostAsync(action, write, inCommand, commandLine, cancellationToken));
  }

  public static async Task<T> ExecuteRunCodeAsync<T>(string? document, bool writeMode, Func<RunCodeHost, T> action,
    RunCodeActivation? activation = null)
  {
    if (document == null && App.DocumentManager.MdiActiveDocument != null)
      return await ExecuteAsync((doc, civil, db, tr) => action(new(doc, civil, db, tr)), write: false, inCommand: writeMode);

    activation ??= new RunCodeActivation();
    return await ExecuteSerializedAsync(async cancellationToken =>
    {
      T? result = default;
      Document? target = null;
      Document? previous = null;
      string? previousName = null;
      var restorePrevious = false;
      Exception? failure = null;
      try
      {
        await RunOnHostAsync(_ =>
        {
          try
          {
            cancellationToken.ThrowIfCancellationRequested();
            var active = App.DocumentManager.MdiActiveDocument;
            if (active == null)
            {
              if (writeMode || document != null)
                throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
              result = action(default);
              return Task.CompletedTask;
            }

            var open = App.DocumentManager.Cast<Document>().ToArray();
            var match = document == null ? (Index: Array.IndexOf(open, active), Error: (string?)null)
              : RunCodeHelpers.MatchDocument(document, open.Select(doc => doc.Name).ToArray());
            if (match.Error != null)
              throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", match.Error);
            target = open[match.Index];
            if (writeMode)
            {
              if (target != active)
              {
                previous = active;
                previousName = active.Name;
                // The setter is an application-context API. Restore even if it
                // throws after partially changing the active document.
                restorePrevious = true;
                try
                {
                  App.DocumentManager.MdiActiveDocument = target;
                  activation.Activated = App.DocumentManager.MdiActiveDocument == target;
                }
                catch (Exception error)
                {
                  PluginLog.Warn("RunCode", "Could not activate the target drawing.", error);
                  throw new JsonRpcDispatchException("CIVIL3D.HOST_BUSY", $"could not activate {target.Name}; nothing changed");
                }
                if (!activation.Activated)
                  throw new JsonRpcDispatchException("CIVIL3D.HOST_BUSY", $"could not activate {target.Name}; nothing changed");
              }
              return Task.CompletedTask;
            }

            var civil = CivilDocument.GetCivilDocument(target.Database);
            DocumentLock documentLock;
            try { documentLock = target.LockDocument(); }
            catch (Autodesk.AutoCAD.Runtime.Exception error) when (error.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.LockViolation)
            {
              throw new JsonRpcDispatchException("CIVIL3D.HOST_BUSY", $"Drawing '{target.Name}' is locked by other work; nothing ran.");
            }
            using (documentLock)
            using (var transaction = target.Database.TransactionManager.StartOpenCloseTransaction())
              result = action(new(target, civil, target.Database, transaction));
          }
          catch (Exception error) { failure = error; }
          return Task.CompletedTask;
        }, needsCommand: false, needsQuietEditor: writeMode, cancellationToken, drainOnCancellation: writeMode);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (writeMode)
        {
          // Send C3DMCPHOP to the resolved document, and check its identity
          // inside the command before locking or opening a transaction.
          return await ExecuteOnHostAsync((doc, civil, db, tr) => action(new(doc, civil, db, tr)),
            write: false, inCommand: true, commandLine: false, cancellationToken, targetDocument: target, activation: activation);
        }
        return result!;
      }
      finally
      {
        // Still inside ExecuteSerializedAsync: nobody else may use the host
        // until the restoration hop finishes, including after cancellation.
        if (restorePrevious && previous != null)
        {
          activation.SwitchedBack = await SwitchBackAsync(previous);
          if (!activation.SwitchedBack)
            activation.AddWarning($"could not switch back to {previousName}");
        }
      }
    });
  }

  private static async Task<bool> SwitchBackAsync(Document previous)
  {
    var switchedBack = false;
    // Cleanup has its own budget: a disconnected client must not strand the
    // user in the target drawing, or release the gate before this hop ends.
    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
      await RunOnHostAsync(_ =>
      {
        cleanup.Token.ThrowIfCancellationRequested();
        if (App.DocumentManager.Cast<Document>().Contains(previous))
        {
          App.DocumentManager.MdiActiveDocument = previous;
          switchedBack = App.DocumentManager.MdiActiveDocument == previous;
        }
        return Task.CompletedTask;
      }, needsCommand: false, needsQuietEditor: true, cleanup.Token,
        busyTimeout: TimeSpan.FromSeconds(30), drainOnCancellation: true);
    }
    catch (Exception error)
    {
      // Restoration must never turn a committed write into an RPC error.
      PluginLog.Warn("RunCode", "Could not switch back to the previous drawing.", error);
    }
    return switchedBack;
  }

  private static async Task<T> ExecuteOnHostAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action,
    bool write, bool inCommand, bool commandLine, CancellationToken cancellationToken, Document? targetDocument = null,
    RunCodeActivation? activation = null)
  {
    // These operations require a drawing. Idle itself also works with zero
    // documents, as used by ExecuteRunCodeAsync for side-file-only reads.
    // Check again in the callback because a drawing can close while queued.
    if (App.DocumentManager.MdiActiveDocument == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
    }

    T? result = default;
    Exception? capturedException = null;
    var inputWarnings = new List<string>();

    // Read on the request's thread: the expected identity is an AsyncLocal,
    // and the Idle and plugin-command routes run on the UI thread's
    // execution context, where it would read empty and skip the check.
    var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();

    var hostTask = RunOnHostAsync(route =>
    {
      try
      {
        cancellationToken.ThrowIfCancellationRequested();
        if (targetDocument != null && App.DocumentManager.MdiActiveDocument != targetDocument)
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", "the active drawing changed before the write; nothing changed");
        var doc = targetDocument ?? RequireExpectedDocument(expectedDrawingIdentity, "No drawing changes were made.");
        var civilDoc = CivilApplication.ActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");
        var database = doc.Database;

        using var documentLock = doc.LockDocument();
        // Outside a command (the Idle route), a document lock plus a regular
        // Transaction wipes the drawing's undo history even when it is only
        // read and aborted; an OpenCloseTransaction does not. Only plain reads
        // take the Idle route. Writes run in a command (PluginCommand) so they
        // are one undoable step, and so do reads that create scratch objects:
        // aborting a regular Transaction removes them again, aborting an
        // OpenCloseTransaction does not.
        using var transaction = route == HostRoute.Idle
          ? database.TransactionManager.StartOpenCloseTransaction()
          : database.TransactionManager.StartTransaction();

        result = action(doc, civilDoc, database, transaction);

        if (write)
        {
          transaction.Commit();
        }
      }
      catch (Exception ex)
      {
        capturedException = ex;
      }

      return Task.CompletedTask;
    }, needsCommand: write || inCommand, needsQuietEditor: commandLine, cancellationToken,
      targetDocument: targetDocument, drainOnCancellation: targetDocument != null,
      dispatchWarning: activation == null ? null : activation.AddWarning, inputWarning: inputWarnings.Add);

    // A targeted write must finish its host work before its caller restores
    // the previous drawing. RunOnHostAsync drains started work on cancellation.
    if (targetDocument != null) await hostTask;
    else await AwaitHostContextAsync(hostTask, cancellationToken);

    if (capturedException != null)
    {
      System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(capturedException).Throw();
    }

    return WithInputWarnings(result!, inputWarnings);
  }

  // Warnings from typed command-line input that has no evidence of running
  // (issue #53) go into the operation's result, so a caller never gets plain
  // success for an UNDO that did not run. Every operation that queues
  // command-line input returns a Dictionary; anything else keeps only the log.
  private static T WithInputWarnings<T>(T result, List<string> warnings)
  {
    if (warnings.Count == 0 || result is not IDictionary<string, object?> response) return result;
    var combined = response.TryGetValue("warnings", out var existing) switch
    {
      true when existing is string single => new List<string> { single },
      true when existing is IEnumerable<string> earlier => earlier.ToList(),
      _ => new List<string>(),
    };
    combined.AddRange(warnings);
    response["warnings"] = combined;
    return result;
  }

  // Same host gate, command context, drawing-identity check, and document
  // lock as ExecuteAsync, but WITHOUT an enclosing transaction. Some database
  // operations (xref reload/unload/bind/detach) manage their own internal
  // transactions and must not run nested inside an open top transaction; the
  // callback opens short transactions of its own where it needs to read.
  public static async Task<T> ExecuteLockedWithoutTransactionAsync<T>(Func<Document, CivilDocument, Database, T> action)
  {
    return await ExecuteSerializedAsync(async cancellationToken =>
    {
      if (App.DocumentManager.MdiActiveDocument == null)
      {
        throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
      }

      T? result = default;
      Exception? capturedException = null;
      var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();

      var hostTask = RunOnHostAsync(_ =>
      {
        try
        {
          cancellationToken.ThrowIfCancellationRequested();
          var doc = RequireExpectedDocument(expectedDrawingIdentity, "No drawing changes were made.");
          var civilDoc = CivilApplication.ActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");

          using var documentLock = doc.LockDocument();
          result = action(doc, civilDoc, doc.Database);
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }

        return Task.CompletedTask;
      }, needsCommand: true, needsQuietEditor: false, cancellationToken);

      await AwaitHostContextAsync(hostTask, cancellationToken);

      if (capturedException != null)
      {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(capturedException).Throw();
      }

      return result!;
    });
  }

  public static async Task<T> ExecuteInCommandContextAsync<T>(Func<Task<T>> action)
  {
    return await ExecuteSerializedAsync(async cancellationToken =>
    {
      T? result = default;
      Exception? capturedException = null;

      Func<object, Task> callback = async _ =>
      {
        try
        {
          cancellationToken.ThrowIfCancellationRequested();
          result = await action();
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }
      };

      // Always run application-context work from the Idle hop, never from a
      // command context. The only caller is newDrawing, whose DocumentManager
      // .Add is an application-context API that needs no command context, and
      // choosing the hop by MdiActiveDocument was racy: if the last document
      // closed between the check and the hop, the command-context callback
      // would never run and the request would hang holding the gate.
      var hostTask = ExecuteInApplicationContextAsync(callback, cancellationToken);

      await AwaitHostContextAsync(hostTask, cancellationToken);

      if (capturedException != null)
      {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(capturedException).Throw();
      }

      return result!;
    });
  }

  // Runs an async action that drives AutoCAD commands (Editor.CommandAsync)
  // for the active document, serialized behind the same host gate as every
  // other drawing operation. Unlike ExecuteAsync it deliberately opens no
  // transaction and takes no explicit document lock around the action: a
  // command such as -PLOT or -PUBLISH must own the document itself, and
  // holding an open transaction across it is what makes the .NET-only paths
  // unsafe (see PlotCommands.cs). Callers open short, disposed read
  // transactions of their own before issuing commands.
  public static async Task<T> ExecuteCommandSequenceAsync<T>(Func<Document, CancellationToken, Task<T>> action,
    bool drainOnCancellation = false)
  {
    return await ExecuteSerializedAsync(async cancellationToken =>
    {
      // Same zero-document fast fail as ExecuteAsync: the command-context hop
      // below never fires without a document and would wedge the gate.
      if (App.DocumentManager.MdiActiveDocument == null)
      {
        throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
      }

      T? result = default;
      Exception? capturedException = null;
      var actionCompleted = false;
      var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();

      var hostTask = RunOnHostAsync(async _ =>
      {
        try
        {
          cancellationToken.ThrowIfCancellationRequested();
          var doc = RequireExpectedDocument(expectedDrawingIdentity, "No commands were run.");
          result = await action(doc, cancellationToken);
          actionCompleted = true;
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }
      }, needsCommand: true, needsQuietEditor: false, cancellationToken,
        drainOnCancellation: drainOnCancellation);

      // A started foreground publish owns the host until the native writer
      // has returned, even if its client requests cancellation meanwhile.
      if (drainOnCancellation)
      {
        try { await hostTask; }
        catch (OperationCanceledException) when (actionCompleted)
        {
          // Cancellation arrived after the native writer and verification
          // completed. The accepted output remains a completed job with a warning.
        }
        catch (OperationCanceledException) when (capturedException != null)
        {
          // Preserve the host failure after draining, including PDF verification
          // failures. The original exception is rethrown below.
        }
      }
      else await AwaitHostContextAsync(hostTask, cancellationToken);

      if (capturedException != null)
      {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(capturedException).Throw();
      }

      return result!;
    });
  }

  public static Task<T> ReadAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, false);
  }

  public static Task<T> WriteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, true);
  }

  // A read that needs a command around it: it creates scratch objects that
  // must disappear when the transaction is aborted (TinVolumeSurface.Create
  // for volumes), or it uses the pickfirst selection (Editor.SelectImplied
  // and SetImpliedSelection only work inside a command flagged UsePickSet).
  // The transaction is a regular one and is never committed. It may also
  // queue command-line input with QueueCommandLine.
  public static Task<T> ReadInCommandAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, false, inCommand: true);
  }

  // A plain read that then runs AutoCAD commands by queueing command-line
  // input with QueueCommandLine (undo, redo, and commands that open their own
  // dialogs). Waits for a quiescent editor so the input is not typed into a
  // prompt.
  public static Task<T> ReadThenCommandLineAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, false, commandLine: true);
  }

  // Runs AutoCAD command-line input and nothing else: no document lock and no
  // transaction, because on the Idle route even a read under a lock empties
  // the redo stack (seen live: UNDO, one plugin read, then MREDO said "Nothing
  // to redo"). Used by undo and redo. Waits for a quiescent editor.
  public static async Task<T> RunCommandLineAsync<T>(string input, T result)
  {
    return await ExecuteSerializedAsync(async cancellationToken =>
    {
      if (App.DocumentManager.MdiActiveDocument == null)
      {
        throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
      }

      var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();
      var inputWarnings = new List<string>();
      var hostTask = RunOnHostAsync(_ =>
      {
        cancellationToken.ThrowIfCancellationRequested();
        RequireExpectedDocument(expectedDrawingIdentity, "No commands were run.");
        QueueCommandLine(input);
        return Task.CompletedTask;
      }, needsCommand: false, needsQuietEditor: true, cancellationToken, inputWarning: inputWarnings.Add);

      await AwaitHostContextAsync(hostTask, cancellationToken);
      return WithInputWarnings(result, inputWarnings);
    });
  }

  // Command-line input requested by the running host operation; sent once the
  // operation (and C3DMCPHOP) has finished, through the channel the operation
  // took (COM SendCommand by default). Only touched on the main thread, one
  // host operation at a time.
  private static readonly List<string> PendingCommandLine = new();

  // Main thread, inside a host operation: run this command-line input (for
  // example "_.UNDO 2 ") right after the operation. It takes the channel the
  // operation's C3DMCPHOP was started with, never the other one: input queued
  // with SendStringToExecute inside a COM-started C3DMCPHOP mixed with its COM
  // input (seen live: UNDO received "C3DMCPHOP" as its answer instead of the
  // count). With the default route that channel is COM.
  public static void QueueCommandLine(string input)
  {
    // COM SendCommand needs a trailing space (or Enter) to submit the input;
    // typed input reads the space as Enter too.
    PendingCommandLine.Add(input.TrimEnd('\r', '\n', ' ') + " ");
  }

  // Set by a host operation that left a command waiting at a prompt. After the
  // operation ends, successfully or not, ESC ESC goes to the document through
  // the operation's channel: COM SendCommand by default. Typed input
  // (SendStringToExecute) was lost after COM-started commands (issue #46:
  // another plugin's commands were lost right after civil3d_plot; issue #53:
  // not every time, and it came back), so it is used only on the opt-in typed
  // route. Main thread only.
  private static bool PendingCancel;

  public static void QueueCancelAfterOperation() => PendingCancel = true;

  private static Document RequireExpectedDocument(string? expectedDrawingIdentity, string noChanges)
  {
    var doc = App.DocumentManager.MdiActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
    var activeDrawingIdentity = PluginRuntime.GetDrawingIdentity(doc);
    if (!string.IsNullOrWhiteSpace(expectedDrawingIdentity) &&
        !string.Equals(expectedDrawingIdentity, activeDrawingIdentity, StringComparison.OrdinalIgnoreCase))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.CONFLICT",
        $"The active drawing changed from '{expectedDrawingIdentity}' to '{activeDrawingIdentity}' while the operation was queued. {noChanges}");
    }

    return doc;
  }

  // How long a running command, a prompt (for work that needs a command) or a
  // modal dialog may hold the host before the request fails with HOST_BUSY.
  private static readonly TimeSpan HostBusyTimeout = TimeSpan.FromSeconds(15);

  // Runs the operation on the host's main thread exactly once, from an
  // Application.Idle tick:
  //  - reads (needsCommand false) run in the Idle tick itself (application
  //    context, with their own document lock);
  //  - everything else is queued for the plugin's C3DMCPHOP command, which the
  //    Idle tick starts with COM SendCommand from a background thread (the
  //    default route; setHostRoute "typed" or "auto" starts it with
  //    SendStringToExecute instead, for issue #53: CivilExecution.TypedInput.cs).
  //
  // Why not DocumentManager.ExecuteInCommandContextAsync, the usual route:
  // verified on Civil 3D 2027 (2026-10-11, diagnostic build, Windows session
  // disconnected, only this plugin loaded), the first COM SendCommand from any
  // client stops it for the rest of the session. That includes PowerShell,
  // other plugins' automation, and this plugin's own C3DMCPHOP start. Queued
  // callbacks never start their EXECUTEFUNCTION command, Application.Idle keeps
  // firing, CMDACTIVE is 0, the editor is quiescent, and nothing in or outside
  // the process revives it (SendStringToExecute, PostCommandPrompt, more COM
  // commands, WM_NULL, focus, injected mouse and key input). The earlier
  // design detected that state after 5 s and switched to these routes; using
  // them from the start removes the stall and the dependency. It also stops
  // plugin calls from cancelling a command or prompt the user has open, which
  // the command-context hop did.
  //
  // The watcher fails fast with CIVIL3D.HOST_BUSY instead of letting the
  // request time out after 120 s:
  //  - while a command is active (CMDACTIVE != 0) for HostBusyTimeout, or, for
  //    work that needs C3DMCPHOP, while the editor is not quiescent (a LISP
  //    prompt waits with CMDACTIVE 0; the command name must not be typed into
  //    it);
  //  - while no Idle tick arrives at all for HostBusyTimeout (a modal dialog).
  // The busy checks, the no-Idle watchdog and the claim flag come from
  // jorgegarcias60/Civil3D-mcp#1 and #2.
  // inputWarning receives the warnings of typed command-line input that has no
  // evidence of running (SendTypedInputAsync); the COM channel has none.
  private static async Task RunOnHostAsync(Func<HostRoute, Task> operation, bool needsCommand, bool needsQuietEditor,
    CancellationToken cancellationToken, Document? targetDocument = null, TimeSpan? busyTimeout = null,
    bool drainOnCancellation = false, Action<string>? dispatchWarning = null, Action<string>? inputWarning = null)
  {
    var timeout = busyTimeout ?? HostBusyTimeout;
    var dispatch = new RunCodeDispatchGate();
    var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    void ReportInputWarning(string? warning)
    {
      if (warning != null) inputWarning?.Invoke(warning);
    }

    async Task RunOnce(HostRoute route)
    {
      if (!dispatch.TryStart())
      {
        return;
      }

      PendingCommandLine.Clear();
      PendingCancel = false;
      try
      {
        await operation(route);
        dispatch.CompleteOperation();
      }
      catch (Exception ex)
      {
        dispatch.CompleteOperation(ex);
      }
    }

    DateTime? busySince = null;
    // Last Idle tick (UTC ticks), read by the timer thread: no tick at all for
    // HostBusyTimeout means the host is inside a modal dialog or a long
    // operation.
    long lastIdleTicks = DateTime.UtcNow.Ticks;
    var started = 0;
    EventHandler? idle = null;
    idle = async (_, _) =>
    {
      // async void: an exception escaping this handler would take down the
      // host, so every failure ends the request instead.
      try
      {
        Interlocked.Exchange(ref lastIdleTicks, DateTime.UtcNow.Ticks);
        if (!dispatch.IsNotStarted || Volatile.Read(ref started) != 0)
        {
          CoreApp.Idle -= idle;
          return;
        }

        var now = DateTime.UtcNow;
        var activeDocument = App.DocumentManager.MdiActiveDocument;
        var busy = activeDocument != null && (Convert.ToInt32(CoreApp.GetSystemVariable("CMDACTIVE")) != 0
          || ((needsCommand || needsQuietEditor) && activeDocument.Editor.IsQuiescent == false));
        if (busy)
        {
          busySince ??= now;
          if (now - busySince.Value >= timeout && dispatch.TryAbandon())
          {
            CoreApp.Idle -= idle;
            done.TrySetException(new JsonRpcDispatchException(
              "CIVIL3D.HOST_BUSY",
              "Civil 3D has a command or prompt in progress. Finish it or press Esc in Civil 3D, then retry. No drawing changes were made."));
          }

          return;
        }

        CoreApp.Idle -= idle;
        Interlocked.Exchange(ref started, 1);
        // The COM document must be fetched on the main thread.
        var inputDocument = targetDocument ?? activeDocument;
        var acadDocument = inputDocument?.GetAcadDocument();
        // One channel per operation, chosen here once (issue #53): its
        // C3DMCPHOP start, then its command-line input and cancel. The route
        // is read once, so setHostRoute changes the next operation only. The
        // default route "com" always gives Com: today's code path below. A
        // lost typed start under "auto" switches the channel to Com before
        // the work starts.
        var hopStart = Route.HopStart;
        var channel = new StrongBox<InputChannel>(needsCommand
          ? HostRouteDecision.HopStart(hopStart, Route.TypedInput.State)
          : HostRouteDecision.IdleCommandLine(hopStart, Route.TypedInput.State));
        if (needsCommand)
        {
          var commandDocument = targetDocument ?? activeDocument
            ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
          // Register the whole COM dispatch before cancellation can start its
          // drain. If cancellation won first, do not fetch/queue the command.
          if (channel.Value == InputChannel.Typed)
          {
            // The dispatch completes when the work has started (or the start
            // was lost and, under "auto", SendCommand has returned).
            if (!dispatch.TryDispatch(() => StartPluginCommandTypedAsync(commandDocument,
                  () => RunOnce(HostRoute.PluginCommand), () => !dispatch.IsNotStarted, hopStart, channel, timeout))) return;
          }
          else if (!dispatch.TryDispatch(() => StartPluginCommandAsync(commandDocument,
                () => RunOnce(HostRoute.PluginCommand), () => !dispatch.IsNotStarted))) return;
          await dispatch.DispatchDone;
        }
        else
        {
          await RunOnce(HostRoute.Idle);
        }

        // Work in C3DMCPHOP may still be awaiting commands (-PLOT) after
        // SendCommand returned; wait for the operation, then send any
        // command-line input it queued.
        try
        {
          await dispatch.OperationDone;
        }
        finally
        {
          if (PendingCancel)
          {
            PendingCancel = false;
            if (channel.Value == InputChannel.Typed)
            {
              if (inputDocument != null) ReportInputWarning(await SendTypedInputAsync(inputDocument, new[] { "\x03\x03" }));
            }
            else if (acadDocument != null) await SendCancelAsync(acadDocument);
          }
        }
        if (cancellationToken.IsCancellationRequested) return;
        if (PendingCommandLine.Count > 0 && channel.Value == InputChannel.Typed && inputDocument != null)
        {
          var input = PendingCommandLine.ToList();
          PendingCommandLine.Clear();
          ReportInputWarning(await SendTypedInputAsync(inputDocument, input));
        }
        else if (PendingCommandLine.Count > 0 && acadDocument != null)
        {
          var input = PendingCommandLine.ToList();
          PendingCommandLine.Clear();
          await SendCommandLineAsync(acadDocument, input);
        }

        done.TrySetResult(true);
      }
      catch (Exception ex)
      {
        CoreApp.Idle -= idle;
        dispatch.TryAbandon();
        done.TrySetException(ex);
      }
    };
    CoreApp.Idle += idle;

    // Idle is raised when the host's message queue empties; a background
    // Civil 3D with nothing to process may not raise it again. Post a no-op
    // message to the main window while the request waits so the watcher keeps
    // getting ticks. PostMessage is safe from any thread.
    IntPtr mainWindow;
    using (var host = System.Diagnostics.Process.GetCurrentProcess())
    {
      mainWindow = host.MainWindowHandle;
    }
    using var nudge = new Timer(_ =>
    {
      if (!dispatch.IsNotStarted || Volatile.Read(ref started) != 0)
      {
        return;
      }

      var sinceIdle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref lastIdleTicks));
      if (sinceIdle >= timeout && dispatch.TryAbandon())
      {
        done.TrySetException(new JsonRpcDispatchException(
          "CIVIL3D.HOST_BUSY",
          "Civil 3D is not processing requests: a dialog is open or a long operation is running. Close the dialog or wait, then retry. No drawing changes were made."));
        return;
      }

      if (mainWindow != IntPtr.Zero)
      {
        PostMessage(mainWindow, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
      }
    }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

    try
    {
      await AwaitHostContextAsync(done.Task, cancellationToken);
    }
    catch (OperationCanceledException) when (drainOnCancellation)
    {
      // Start and abandonment compete atomically. Drain the COM dispatch even
      // if RunOnce never started, before the caller restores the drawing and
      // the single ExecuteSerializedAsync finally releases the host gate.
      await dispatch.DrainAsync(TimeSpan.FromSeconds(30), warning =>
      {
        PluginLog.Warn("Host", warning);
        dispatchWarning?.Invoke(warning);
      });
      throw;
    }
    finally
    {
      // Request finished, failed or was cancelled: nothing may run it later,
      // and an Idle handler still waiting for queued C3DMCPHOP work that will
      // now never run must not wait forever.
      CoreApp.Idle -= idle;
      dispatch.CancelUnstartedOperation();
    }
  }

  [DllImport("user32.dll")]
  private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

  // ---------------------------------------------------------------------------
  // Plugin-command route
  // ---------------------------------------------------------------------------

  // Work waiting for the next C3DMCPHOP run. Each item runs once, inside the
  // command, where the document is locked by the command and a regular
  // Transaction is recorded as one undo step.
  private static readonly ConcurrentQueue<Func<Task>> PluginCommandWork = new();

  internal const string PluginCommandName = "C3DMCPHOP";

  // Awaited by PluginEntry's C3DMCPHOP command method (async void, on the main
  // thread in command context), so the command stays active while the work
  // awaits Editor.CommandAsync.
  internal static async Task RunPluginCommandWorkAsync()
  {
    while (PluginCommandWork.TryDequeue(out var work))
    {
      try
      {
        await work();
      }
      catch (Exception ex)
      {
        PluginLog.Error("Host", "C3DMCPHOP work failed outside its own error handling.", ex);
      }
    }
  }

  // Main thread (an Idle tick). Queues the work and starts C3DMCPHOP with COM
  // SendCommand from a background thread: AutoCAD marshals that call to the
  // main thread like an external client's and runs it at once, also in a
  // disconnected Windows session with nobody at the keyboard. Completes when SendCommand
  // has returned; the work itself reports through the request's own task.
  // acadDocument is passed only by the typed route's COM fallback, which
  // fetched it on the main thread before its typed attempt.
  private static async Task StartPluginCommandAsync(Document doc, Func<Task> work, Func<bool> finished,
    object? acadDocument = null)
  {
    // The COM document must be fetched on the main thread.
    acadDocument ??= doc.GetAcadDocument();
    PluginCommandWork.Enqueue(work);

    await Task.Run(() => SendCommandWithRetry(acadDocument, "_" + PluginCommandName + " "));

    if (!finished())
    {
      // SendCommand returned without the command running the work (it is
      // still queued, so drop it): report instead of waiting for the request
      // timeout.
      DropQueuedWork(work);
      throw new JsonRpcDispatchException(
        "CIVIL3D.HOST_BUSY",
        $"Civil 3D did not run the {PluginCommandName} command (another command or a dialog may be active). No drawing changes were made.");
    }
  }

  // Sends queued command-line input, one entry at a time, with COM
  // SendCommand from a background thread. SendCommand returns once AutoCAD
  // has consumed the input. A command that opens a dialog or stops at a
  // prompt keeps it from returning; after a few seconds the request completes
  // anyway, so the host gate is not held while the user works in the dialog.
  private static async Task SendCommandLineAsync(object acadDocument, IReadOnlyList<string> input)
  {
    foreach (var text in input)
    {
      var call = Task.Run(() => SendCommandWithRetry(acadDocument, text));
      await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(3)));
      if (!call.IsCompleted)
      {
        _ = call.ContinueWith(
          t => PluginLog.Warn("Host", $"Command-line input '{text.Trim()}' failed.", t.Exception),
          CancellationToken.None,
          TaskContinuationOptions.OnlyOnFaulted,
          TaskScheduler.Default);
        PluginLog.Info("Host", $"Command-line input '{text.Trim()}' is still running (a dialog or prompt); not waiting for it.");
        return;
      }

      await call;
    }
  }

  // Best effort: a failure is logged and never replaces the operation's result.
  private static async Task SendCancelAsync(object acadDocument)
  {
    try
    {
      await SendCommandLineAsync(acadDocument, new[] { "\x03\x03" });
    }
    catch (Exception ex)
    {
      PluginLog.Warn("Host", "Could not cancel the command left at a prompt.", ex);
    }
  }

  private static void SendCommandWithRetry(object acadDocument, string text)
  {
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    while (true)
    {
      try
      {
        Civil3DCompatibility.InvokeComMethod(acadDocument, "SendCommand", text);
        return;
      }
      catch (COMException ex) when (IsCallRejected(ex) && DateTime.UtcNow < deadline)
      {
        Thread.Sleep(200);
      }
    }
  }

  private static bool IsCallRejected(COMException ex) =>
    ex.HResult is unchecked((int)0x80010001) /* RPC_E_CALL_REJECTED */ or unchecked((int)0x8001010A) /* RPC_E_SERVERCALL_RETRYLATER */;

  private static void DropQueuedWork(Func<Task> work)
  {
    var keep = new List<Func<Task>>();
    while (PluginCommandWork.TryDequeue(out var queued))
    {
      if (!ReferenceEquals(queued, work)) keep.Add(queued);
    }

    foreach (var queued in keep)
    {
      PluginCommandWork.Enqueue(queued);
    }
  }

  // Runs the callback on the host's main thread via a one-shot Application.Idle
  // handler and completes when the async callback has finished, propagating any
  // exception it throws.
  //
  // Verified live on Civil 3D 2027 with zero documents open:
  // DocumentManager.ExecuteInApplicationContext never invoked its callback in
  // that state (the request timed out after 120 s with no document created),
  // whereas Application.Idle keeps firing while only the Start tab is showing.
  // Idle runs on the main thread, which is the context DocumentManager.Add needs.
  private static Task ExecuteInApplicationContextAsync(Func<object, Task> callback, CancellationToken cancellationToken)
  {
    var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    CancellationTokenRegistration cancellation = default;

    EventHandler? handler = null;
    handler = async (_, _) =>
    {
      // One-shot: detach before running so a slow callback cannot be re-entered
      // by the next idle tick.
      CoreApp.Idle -= handler;
      cancellation.Dispose();
      try
      {
        await callback(null!);
        completion.TrySetResult(true);
      }
      catch (Exception ex)
      {
        completion.TrySetException(ex);
      }
    };
    CoreApp.Idle += handler;

    // A request cancelled before the next idle tick (client disconnect while
    // Civil 3D is busy) must not leave its handler subscribed; otherwise
    // abandoned handlers accumulate until Idle next fires. Unsubscribe and
    // complete as cancelled so the awaiting request observes it promptly.
    cancellation = cancellationToken.Register(() =>
    {
      CoreApp.Idle -= handler;
      completion.TrySetCanceled(cancellationToken);
    });

    return completion.Task;
  }

  // Waits for a host-context hop without ignoring request cancellation. A
  // client disconnect cancels the request token (RpcTcpServer monitors the
  // socket); without this, a hop whose callback never fires would keep
  // HostExecutionGate held after the client has already gone away. If the
  // host callback does run later, it observes the cancelled token and does
  // no work; its result is discarded.
  private static async Task AwaitHostContextAsync(Task hostTask, CancellationToken cancellationToken)
  {
    if (hostTask.IsCompleted || !cancellationToken.CanBeCanceled)
    {
      await hostTask;
      return;
    }

    using var abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var abandonTask = Task.Delay(Timeout.Infinite, abandon.Token);
    var completed = await Task.WhenAny(hostTask, abandonTask);
    if (completed != hostTask)
    {
      cancellationToken.ThrowIfCancellationRequested();
    }

    // Release the abandon registration so it does not outlive the request.
    abandon.Cancel();
    await hostTask;
  }

  private static async Task<T> ExecuteSerializedAsync<T>(Func<CancellationToken, Task<T>> action)
  {
    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    PluginRuntime.QueueHostOperation();
    var started = false;

    try
    {
      await HostExecutionGate.WaitAsync(cancellationToken);
      started = true;
      PluginRuntime.StartHostOperation();
      cancellationToken.ThrowIfCancellationRequested();
      return await action(cancellationToken);
    }
    finally
    {
      if (started)
      {
        PluginRuntime.CompleteHostOperation();
        HostExecutionGate.Release();
      }
      else
      {
        PluginRuntime.CancelQueuedHostOperation();
      }
    }
  }
}
