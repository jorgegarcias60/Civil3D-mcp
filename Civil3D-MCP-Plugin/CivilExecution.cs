using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Civil3DMcpPlugin;

public static class CivilExecution
{
  private static readonly SemaphoreSlim HostExecutionGate = new(1, 1);

  public static async Task<T> ExecuteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action, bool write)
  {
    return await ExecuteSerializedAsync(async cancellationToken =>
    {
      // With zero documents open there is no command context, so the
      // ExecuteInCommandContextAsync callback below never runs and the
      // NO_DRAWING check inside it is unreachable. Hanging here would hold
      // HostExecutionGate forever and wedge every later host operation, so
      // fail fast before the hop. The inner check stays because the document
      // can still close while this request waits in the queue.
      if (App.DocumentManager.MdiActiveDocument == null)
      {
        throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
      }

      T? result = default;
      Exception? capturedException = null;

      var hostTask = RunOnHostAsync(async _ =>
      {
        try
        {
          cancellationToken.ThrowIfCancellationRequested();
          var doc = App.DocumentManager.MdiActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
          var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();
          var activeDrawingIdentity = PluginRuntime.GetDrawingIdentity(doc);
          if (!string.IsNullOrWhiteSpace(expectedDrawingIdentity) &&
              !string.Equals(expectedDrawingIdentity, activeDrawingIdentity, StringComparison.OrdinalIgnoreCase))
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.CONFLICT",
              $"The active drawing changed from '{expectedDrawingIdentity}' to '{activeDrawingIdentity}' while the operation was queued. No drawing changes were made.");
          }
          var civilDoc = CivilApplication.ActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");
          var database = doc.Database;

          using var documentLock = doc.LockDocument();
          using var transaction = database.TransactionManager.StartTransaction();

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

        await Task.CompletedTask;
      }, cancellationToken);

      await hostTask;

      if (capturedException != null)
      {
        throw capturedException;
      }

      return result!;
    });
  }

  // How long a command-context hop may take to start while Civil 3D is idle
  // before it is treated as wedged, and how long a running command or LISP
  // prompt may hold the host before the request fails with HOST_BUSY.
  private static readonly TimeSpan CommandContextStartGrace = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan HostBusyTimeout = TimeSpan.FromSeconds(15);

  // Set once a command-context hop failed to start although no command was
  // active. AutoCAD does not recover from that state (seen 2026-09-25: a LISP
  // expression sent over COM waited for input, a request queued behind it,
  // and after the prompt was cancelled every later ExecuteInCommandContextAsync
  // stayed queued until Civil 3D restarted). From then on requests use the
  // Application.Idle hop only.
  private static volatile bool _commandContextWedged;

  // Runs the operation on the host's main thread exactly once. The normal
  // route is the command-context hop; an Application.Idle watcher runs next to
  // it and
  //  - fails fast with CIVIL3D.HOST_BUSY while a command or prompt stays active
  //    (CMDACTIVE != 0), instead of letting the request time out after 120 s;
  //  - runs the operation itself from the Idle tick (application context; the
  //    operation takes its own document lock) when the command-context hop has
  //    not started within the grace period although Civil 3D is idle.
  // A claim flag makes sure whichever hop comes second does nothing, including
  // a command-context callback that AutoCAD runs long after the request ended.
  private static async Task RunOnHostAsync(Func<object, Task> operation, CancellationToken cancellationToken)
  {
    var claimed = 0;
    var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    async Task RunOnce()
    {
      if (Interlocked.Exchange(ref claimed, 1) != 0)
      {
        return;
      }

      try
      {
        await operation(null!);
        done.TrySetResult(true);
      }
      catch (Exception ex)
      {
        done.TrySetException(ex);
      }
    }

    var useCommandContext = !_commandContextWedged;
    if (useCommandContext)
    {
      try
      {
        var commandTask = RunInCommandContextAsync(_ => RunOnce());
        _ = commandTask.ContinueWith(
          t => PluginLog.Warn("Host", "Command-context hop failed; the Idle watcher takes over.", t.Exception),
          CancellationToken.None,
          TaskContinuationOptions.OnlyOnFaulted,
          TaskScheduler.Default);
      }
      catch (Exception ex)
      {
        PluginLog.Warn("Host", "Command-context hop could not be queued; the Idle watcher takes over.", ex);
      }
    }

    // Busy and idle time are measured separately: only continuous idle time
    // counts toward the grace period, so a command that runs for a few seconds
    // does not make a healthy command-context hop look wedged.
    DateTime? busySince = null;
    DateTime? idleSince = null;
    EventHandler? idle = null;
    idle = async (_, _) =>
    {
      // async void: an exception escaping this handler would take down the
      // host, so every failure ends the request instead.
      try
      {
        if (Volatile.Read(ref claimed) != 0)
        {
          CoreApp.Idle -= idle;
          return;
        }

        var now = DateTime.UtcNow;
        var commandActive = Convert.ToInt32(CoreApp.GetSystemVariable("CMDACTIVE")) != 0;
        if (commandActive)
        {
          idleSince = null;
          busySince ??= now;
          if (now - busySince.Value >= HostBusyTimeout && Interlocked.Exchange(ref claimed, 1) == 0)
          {
            CoreApp.Idle -= idle;
            done.TrySetException(new JsonRpcDispatchException(
              "CIVIL3D.HOST_BUSY",
              "Civil 3D has a command or prompt in progress (CMDACTIVE is not 0). Finish it or press Esc in Civil 3D, then retry. No drawing changes were made."));
          }

          return;
        }

        busySince = null;
        idleSince ??= now;
        if (useCommandContext && now - idleSince.Value < CommandContextStartGrace)
        {
          return;
        }

        CoreApp.Idle -= idle;
        if (useCommandContext && !_commandContextWedged)
        {
          _commandContextWedged = true;
          PluginLog.Warn("Host", $"Command-context hop did not start within {CommandContextStartGrace.TotalSeconds:0} s while Civil 3D was idle; switching to the Application.Idle hop until Civil 3D restarts.");
        }

        await RunOnce();
      }
      catch (Exception ex)
      {
        CoreApp.Idle -= idle;
        if (Interlocked.Exchange(ref claimed, 1) == 0)
        {
          done.TrySetException(ex);
        }
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
      if (Volatile.Read(ref claimed) == 0 && mainWindow != IntPtr.Zero)
      {
        PostMessage(mainWindow, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
      }
    }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

    try
    {
      await AwaitHostContextAsync(done.Task, cancellationToken);
    }
    finally
    {
      // Request finished, failed or was cancelled: nothing may run it later.
      Interlocked.Exchange(ref claimed, 1);
      CoreApp.Idle -= idle;
    }
  }

  [System.Runtime.InteropServices.DllImport("user32.dll")]
  private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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
        throw capturedException;
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

  // ExecuteInCommandContextAsync returns an awaitable ExecutionResult rather
  // than a Task; surface it as a Task so it can be raced against cancellation.
  private static async Task RunInCommandContextAsync(Func<object, Task> callback)
  {
    await App.DocumentManager.ExecuteInCommandContextAsync(callback, null);
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
      try
      {
        // One-shot: detach before running so a slow callback cannot be re-entered
        // by the next idle tick.
        CoreApp.Idle -= handler;
        cancellation.Dispose();
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
