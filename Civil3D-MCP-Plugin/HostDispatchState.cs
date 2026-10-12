namespace Civil3DMcpPlugin;

// No AutoCAD dependencies: coordinates a host callback with cancellation and
// the COM call that dispatches it. The host semaphore remains owned by the
// outer serialized operation until this drain and document restoration finish.
internal sealed class RunCodeDispatchGate
{
  private const int NotStarted = 0;
  private const int Started = 1;
  private const int Abandoned = 2;
  private int state = NotStarted;
  private readonly object dispatchSync = new();
  private Task dispatch = Task.CompletedTask;
  private readonly TaskCompletionSource<bool> operationDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

  internal bool IsNotStarted => Volatile.Read(ref state) == NotStarted;
  internal Task OperationDone => operationDone.Task;
  internal Task DispatchDone { get { lock (dispatchSync) return dispatch; } }

  internal bool TryStart() => Interlocked.CompareExchange(ref state, Started, NotStarted) == NotStarted;

  // Serializing abandonment with dispatch registration prevents cleanup from
  // observing "no dispatch" just before an Idle handler queues SendCommand.
  // RunOnce still competes with cancellation through the same atomic state.
  internal bool TryAbandon()
  {
    lock (dispatchSync)
      return Interlocked.CompareExchange(ref state, Abandoned, NotStarted) != Started;
  }

  internal bool TryDispatch(Func<Task> startDispatch)
  {
    lock (dispatchSync)
    {
      if (Volatile.Read(ref state) == Abandoned) return false;
      dispatch = startDispatch();
      return true;
    }
  }

  internal void CompleteOperation(Exception? error = null)
  {
    if (error == null) operationDone.TrySetResult(true);
    else operationDone.TrySetException(error);
  }

  internal void CancelUnstartedOperation()
  {
    if (TryAbandon()) operationDone.TrySetCanceled();
  }

  internal async Task DrainAsync(TimeSpan dispatchTimeout, Action<string> warning)
  {
    var abandoned = TryAbandon();
    try
    {
      // Never restore a drawing while its action/transaction is still running.
      if (!abandoned) await operationDone.Task;
    }
    finally
    {
      Task pendingDispatch;
      lock (dispatchSync) pendingDispatch = dispatch;
      try
      {
        await pendingDispatch.WaitAsync(dispatchTimeout);
      }
      catch (TimeoutException) when (!pendingDispatch.IsFaulted)
      {
        // Do not reset the state: a callback arriving after this timeout must
        // still refuse to start, before it accesses any documents or queues.
        warning($"C3DMCPHOP dispatch did not finish within {dispatchTimeout.TotalSeconds:g} seconds; late callbacks will not run.");
        _ = pendingDispatch.ContinueWith(task => { _ = task.Exception; },
          CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
      }
      catch (Exception error)
      {
        // Observe a failed dispatch without replacing the request cancellation.
        warning($"C3DMCPHOP dispatch failed while draining: {error.Message}");
      }
    }
  }
}
