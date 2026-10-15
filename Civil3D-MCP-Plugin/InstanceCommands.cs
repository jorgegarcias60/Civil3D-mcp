using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Civil3DMcpPlugin;

internal static class InstanceCommands
{
  private static readonly object Sync = new();
  private enum QuitState { Idle, Armed, Scheduled, Executing }
  private static QuitState _state;
  private static string? _requestId;
  private static bool _discard;
  private static EventHandler? _pendingQuitIdle;
  private static Timer? _pendingQuitWake;
  private static IdleDeadline? _pendingQuitDeadline;
  private static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(45);
  private static readonly TimeSpan DeferredQuitTimeout = TimeSpan.FromSeconds(10);

  internal static Task<object?> AdoptInstanceAsync(JsonObject? parameters)
  {
    RpcAuth.Require(parameters);
    var ownerSession = PluginRuntime.GetRequiredString(parameters, "ownerSession");
    var expectedOwnerSession = PluginRuntime.GetRequiredString(parameters, "expectedOwnerSession");
    var ownerLabel = parameters?["label"]?.GetValue<string>();
    int serverPid, expectedOwnerServerPid;
    DateTimeOffset serverStartedAtUtc, expectedOwnerServerStartedAtUtc;
    try
    {
      serverPid = parameters!["serverPid"]!.GetValue<int>();
      expectedOwnerServerPid = parameters["expectedOwnerServerPid"]!.GetValue<int>();
      serverStartedAtUtc = DateTimeOffset.Parse(parameters["serverStartedAtUtc"]!.GetValue<string>());
      expectedOwnerServerStartedAtUtc = DateTimeOffset.Parse(parameters["expectedOwnerServerStartedAtUtc"]!.GetValue<string>());
    }
    catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException or NullReferenceException)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Adoption requires exact server and expected owner process identities.");
    }
    if (!PluginRuntime.TryAdoptInstance(expectedOwnerSession, expectedOwnerServerPid,
      expectedOwnerServerStartedAtUtc, ownerSession, ownerLabel, serverPid, serverStartedAtUtc))
      throw new JsonRpcDispatchException("CIVIL3D.FORBIDDEN",
        "Only a verifiably orphaned agent instance can be adopted by a live server.");
    return Task.FromResult<object?>(new { adopted = true, ownerSession, ownerLabel,
      ownerServerPid = serverPid, ownerServerStartedAtUtc = serverStartedAtUtc.ToUniversalTime() });
  }

  internal static Task<object?> QuitInstanceAsync(JsonObject? parameters)
  {
    RpcAuth.Require(parameters);
    var session = PluginRuntime.GetRequiredString(parameters, "ownerSession");
    var claim = PluginRuntime.GetRequiredString(parameters, "claimId");
    var discard = PluginRuntime.GetOptionalBool(parameters, "discardChanges") ?? false;
    if (PluginRuntime.Owner != "agent" ||
        !string.Equals(PluginRuntime.OwnerSession, session, StringComparison.Ordinal) ||
        !string.Equals(PluginRuntime.ClaimId, claim, StringComparison.Ordinal))
      throw new JsonRpcDispatchException("CIVIL3D.FORBIDDEN", "Only the owning agent session may quit its claimed Civil 3D instance.");

    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(InspectionTimeout);
    var requestId = PluginRuntime.GetCurrentRequestId();
    var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    EventHandler? idle = null;
    idle = (_, _) =>
    {
      CoreApp.Idle -= idle;
      try
      {
        deadline.Token.ThrowIfCancellationRequested();
        var documents = App.DocumentManager.Cast<Document>().ToArray();
        var unsaved = documents.Where(IsModified).Select(doc => doc.Name).ToArray();
        if (unsaved.Length > 0 && !discard)
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
            $"Instance has unsaved drawings: {string.Join(", ", unsaved)}. Pass discardChanges=true to close them without saving.");
        lock (Sync)
        {
          if (_state != QuitState.Idle) throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", "This instance is already quitting.");
          _discard = discard;
          _requestId = requestId;
          _state = QuitState.Armed;
        }
        if (!completion.TrySetResult(new { quitting = true, pid = Environment.ProcessId }))
          lock (Sync)
          {
            if (_state == QuitState.Armed && _requestId == requestId)
            {
              _state = QuitState.Idle;
              _requestId = null;
            }
          }
      }
      catch (Exception error) { completion.TrySetException(error); }
    };
    CoreApp.Idle += idle;
    var cancellation = deadline.Token.Register(() =>
    {
      CoreApp.Idle -= idle;
      completion.TrySetException(new JsonRpcDispatchException("CIVIL3D.HOST_BUSY",
        "Civil 3D did not become idle in time to inspect drawings. Retry when the host is responsive."));
      lock (Sync)
      {
        if (_state == QuitState.Armed && _requestId == requestId)
        {
          _state = QuitState.Idle;
          _requestId = null;
        }
      }
    });
    var wake = StartIdleWake();
    _ = completion.Task.ContinueWith(_ =>
    {
      CoreApp.Idle -= idle;
      wake.Dispose();
      cancellation.Dispose();
      deadline.Dispose();
    }, TaskScheduler.Default);
    return completion.Task;
  }

  private static bool IsModified(Document document)
  {
    var previous = App.DocumentManager.MdiActiveDocument;
    try
    {
      App.DocumentManager.MdiActiveDocument = document;
      if (!ReferenceEquals(App.DocumentManager.MdiActiveDocument, document))
        throw new JsonRpcDispatchException("CIVIL3D.HOST_BUSY", "Could not inspect every open drawing before quitting.");
      return Convert.ToInt32(App.GetSystemVariable("DBMOD")) != 0;
    }
    finally { App.DocumentManager.MdiActiveDocument = previous; }
  }

  // Called only after the quitInstance reply has been flushed to the socket.
  internal static void AfterResponse(string request, string response, bool delivered)
  {
    string? responseId;
    bool accepted;
    try
    {
      var parsedRequest = JsonNode.Parse(request);
      if (parsedRequest?["method"]?.GetValue<string>() != "quitInstance") return;
      responseId = parsedRequest["id"]?.ToJsonString();
      accepted = delivered && JsonNode.Parse(response)?["result"]?["quitting"]?.GetValue<bool>() == true;
    }
    catch (Exception) { return; }
    bool discard;
    lock (Sync)
    {
      if (_state != QuitState.Armed || _requestId != responseId) return;
      if (!accepted)
      {
        _state = QuitState.Idle;
        _requestId = null;
        return;
      }
      discard = _discard;
      _state = QuitState.Scheduled;
    }
    EventHandler? idle = null;
    idle = (_, _) =>
    {
      CoreApp.Idle -= idle;
      lock (Sync)
      {
        if (_state != QuitState.Scheduled || _requestId != responseId) return;
        if (_pendingQuitDeadline?.IsExpired() != false)
        {
          ResetPendingQuitLocked();
          PluginLog.Warn("Instance", "Deferred quit expired before Civil 3D became idle.");
          return;
        }
        _state = QuitState.Executing;
        _pendingQuitIdle = null;
        _pendingQuitWake?.Dispose();
        _pendingQuitWake = null;
        _pendingQuitDeadline = null;
      }
      try
      {
        var documents = App.DocumentManager.Cast<Document>().ToArray();
        if (!discard && documents.Any(IsModified))
        {
          PluginLog.Warn("Instance", "Quit cancelled because a drawing changed after the reply.");
          return;
        }
        foreach (var doc in documents) doc.CloseAndDiscard();
        lock (Sync) { if (_state != QuitState.Executing || _requestId != responseId) return; }
        App.Quit();
      }
      catch (Exception error) { PluginLog.Error("Instance", "Could not quit owned instance.", error); }
      finally
      {
        lock (Sync)
        {
          if (_state == QuitState.Executing && _requestId == responseId)
          {
            _state = QuitState.Idle;
            _requestId = null;
          }
        }
      }
    };
    lock (Sync)
    {
      if (_state != QuitState.Scheduled || _requestId != responseId) return;
      _pendingQuitIdle = idle;
      var deadline = new IdleDeadline(DeferredQuitTimeout);
      _pendingQuitDeadline = deadline;
      CoreApp.Idle += idle;
      _pendingQuitWake = StartIdleWake(() =>
      {
        lock (Sync)
        {
          if (_state != QuitState.Scheduled || _requestId != responseId) return;
          ResetPendingQuitLocked();
          PluginLog.Warn("Instance", "Deferred quit expired before Civil 3D became idle.");
        }
      }, deadline);
    }
  }

  internal static void CancelPendingQuit()
  {
    lock (Sync)
    {
      ResetPendingQuitLocked();
    }
  }

  private static void ResetPendingQuitLocked()
  {
    if (_pendingQuitIdle != null) CoreApp.Idle -= _pendingQuitIdle;
    _pendingQuitIdle = null;
    _pendingQuitWake?.Dispose();
    _pendingQuitWake = null;
    _pendingQuitDeadline = null;
    _state = QuitState.Idle;
    _requestId = null;
  }

  private static Timer StartIdleWake(Action? onTimeout = null, IdleDeadline? deadline = null)
  {
    IntPtr mainWindow;
    using (var host = System.Diagnostics.Process.GetCurrentProcess()) mainWindow = host.MainWindowHandle;
    var expiresAt = deadline ?? new IdleDeadline(InspectionTimeout);
    return new Timer(_ =>
    {
      if (expiresAt.IsExpired()) { onTimeout?.Invoke(); return; }
      if (mainWindow != IntPtr.Zero) PostMessage(mainWindow, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
    }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
  }

  [DllImport("user32.dll")]
  private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
