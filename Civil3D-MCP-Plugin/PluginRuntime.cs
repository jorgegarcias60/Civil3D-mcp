using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using App = Autodesk.AutoCAD.ApplicationServices.Application;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Civil3DMcpPlugin;

public sealed record PluginStatus(
  bool IsRunning,
  bool OperationInProgress,
  string? CurrentOperation,
  int QueueDepth,
  int QueueCapacity,
  long? CurrentOperationStartedAtUnixMs,
  string? CurrentRequestId);

public sealed class JsonRpcDispatchException : Exception
{
  public JsonRpcDispatchException(string code, string message) : base(message)
  {
    Code = code;
  }

  public string Code { get; }
}

public static class PluginRuntime
{
  public static int Port { get; private set; } = InstanceDiscovery.FirstPort;

  private static readonly object Sync = new();
  private static RpcTcpServer? _server;
  private static InstanceDiscovery? _discovery;
  private static InstanceOwnership? _ownership;
  private static readonly Dictionary<Autodesk.AutoCAD.DatabaseServices.Database,
    Autodesk.AutoCAD.DatabaseServices.DatabaseIOEventHandler> SaveHandlers = new();
  private static readonly HashSet<EventHandler> PendingDiscoveryRefreshes = new();
  private static readonly AsyncLocal<string?> CurrentRequestOperation = new();
  private static readonly AsyncLocal<string?> CurrentRequestId = new();
  private static readonly AsyncLocal<CancellationToken> CurrentRequestCancellation = new();
  private static readonly AsyncLocal<string?> CurrentExpectedDrawingIdentity = new();
  private const int MaxQueuedHostOperations = 64;
  private static int _queueDepth;
  private static int _activeOperations;
  private static string? _currentOperation;
  private static string? _currentRequestId;
  private static long? _currentOperationStartedAtUnixMs;

  public static void StartServer()
  {
    lock (Sync)
    {
      if (_server != null)
      {
        return;
      }

      Port = InstancePortBinder.Bind(port =>
      {
        var candidate = new RpcTcpServer(port, HandleRawRequestAsync, InstanceCommands.AfterResponse);
        try { candidate.Start(); }
        catch (System.Net.Sockets.SocketException) { candidate.Stop(); return false; }
        _server = candidate;
        return true;
      });
      try
      {
        RpcAuth.TokenPath = InstanceDiscovery.TokenPathForPort(Port);
        RpcAuth.Issue();
        PruneStaleDiscovery();
        _discovery = new InstanceDiscovery(Environment.ProcessId, Port, ownership: _ownership);
        RegisterForCom();
        App.DocumentManager.DocumentCreated += OnDocumentCreated;
        App.DocumentManager.DocumentToBeDestroyed += OnDocumentClosing;
        RefreshDiscovery();
        ScheduleDiscoveryRefresh();
      }
      catch
      {
        StopServer();
        throw;
      }
    }
  }

  public static void StopServer()
  {
    lock (Sync)
    {
      if (_server != null)
      {
        App.DocumentManager.DocumentCreated -= OnDocumentCreated;
        App.DocumentManager.DocumentToBeDestroyed -= OnDocumentClosing;
      }
      foreach (var (database, handler) in SaveHandlers) database.SaveComplete -= handler;
      SaveHandlers.Clear();
      foreach (var handler in PendingDiscoveryRefreshes) CoreApp.Idle -= handler;
      PendingDiscoveryRefreshes.Clear();
      InstanceCommands.CancelPendingQuit();
      try { ComRegistration.Revoke(); }
      catch (Exception error) { PluginLog.Warn("ComRegistration", "Could not revoke the per-process COM registration.", error); }
      if (_discovery != null) _ownership = _discovery.Ownership;
      // Invalidate the credential before the listener releases its port to another instance.
      RpcAuth.Revoke();
      _server?.Stop();
      _server = null;
      _discovery?.Delete();
      _discovery = null;
      _currentOperation = null;
      _activeOperations = 0;
      _queueDepth = 0;
      _currentRequestId = null;
      _currentOperationStartedAtUnixMs = null;
    }
  }

  // Lets COM clients (CAD-Tools PowerShell) reach this instance by process id; see ComRegistration. A failure
  // only costs that reachability, so the plugin keeps running.
  private static void RegisterForCom()
  {
    try
    {
      ComRegistration.Register(App.AcadApplication, Environment.ProcessId);
      PluginLog.Info("ComRegistration", $"Registered {ComRegistration.DisplayName(Environment.ProcessId)} in the Running Object Table.");
    }
    catch (Exception error) { PluginLog.Warn("ComRegistration", "Could not register the per-process COM name.", error); }
  }

  private static void PruneStaleDiscovery()
  {
    try
    {
      var removed = InstanceDiscovery.PruneStaleRecords(Environment.ProcessId);
      if (removed > 0) PluginLog.Info("Discovery", $"Removed {removed} stale discovery file(s) of exited instances.");
    }
    catch (Exception error) { PluginLog.Warn("Discovery", "Could not prune stale discovery files.", error); }
  }

  public static string Owner => _discovery?.Owner ?? "user";
  public static string? OwnerSession => _discovery?.OwnerSession;
  public static string? ClaimId => _discovery?.ClaimId;
  public static string? Label => _discovery?.Label;

  internal static bool TryAdoptInstance(string expectedOwnerSession, int expectedOwnerServerPid,
    DateTimeOffset expectedOwnerServerStartedAtUtc, string ownerSession, string? ownerLabel,
    int ownerServerPid, DateTimeOffset ownerServerStartedAtUtc) =>
    _discovery?.TryAdopt(expectedOwnerSession, expectedOwnerServerPid, expectedOwnerServerStartedAtUtc,
      ownerSession, ownerLabel, ownerServerPid, ownerServerStartedAtUtc) == true;

  private static void OnDocumentCreated(object sender, Autodesk.AutoCAD.ApplicationServices.DocumentCollectionEventArgs args)
  {
    try { RefreshDiscovery(); ScheduleDiscoveryRefresh(); }
    catch (Exception error) { PluginLog.Warn("Discovery", "Could not refresh instance discovery.", error); }
  }

  private static void OnDocumentClosing(object sender, Autodesk.AutoCAD.ApplicationServices.DocumentCollectionEventArgs args)
  {
    try
    {
      DetachSaveHandler(args.Document.Database);
      var documents = App.DocumentManager.Cast<Autodesk.AutoCAD.ApplicationServices.Document>()
        .Where(doc => !ReferenceEquals(doc, args.Document))
        .Select(doc => doc.Database.Filename);
      _discovery?.UpdateDocuments(documents, App.GetSystemVariable("ACADVER")?.ToString());
      ScheduleDiscoveryRefresh();
    }
    catch (Exception error) { PluginLog.Warn("Discovery", "Could not refresh closing document.", error); }
  }

  private static void ScheduleDiscoveryRefresh()
  {
    lock (Sync)
    {
      if (_discovery == null || PendingDiscoveryRefreshes.Count != 0) return;
      var scheduledFor = _discovery;
      EventHandler? idle = null;
      idle = (_, _) =>
      {
        CoreApp.Idle -= idle;
        lock (Sync) PendingDiscoveryRefreshes.Remove(idle!);
        try
        {
          if (_server == null || !ReferenceEquals(_discovery, scheduledFor)) return;
          RefreshDiscovery();
          if (!scheduledFor.OwnershipFinalized &&
              App.DocumentManager.Cast<Autodesk.AutoCAD.ApplicationServices.Document>().Any())
            ScheduleDiscoveryRefresh();
        }
        catch (Exception error) { PluginLog.Warn("Discovery", "Could not refresh instance discovery on Idle.", error); }
      };
      PendingDiscoveryRefreshes.Add(idle);
      CoreApp.Idle += idle;
    }
  }

  internal static void RefreshDiscovery()
  {
    var openDocuments = App.DocumentManager.Cast<Autodesk.AutoCAD.ApplicationServices.Document>().ToArray();
    foreach (var document in openDocuments) AttachSaveHandler(document.Database);
    var documents = openDocuments
      .Select(doc => doc.Database.Filename);
    _discovery?.UpdateDocuments(documents, App.GetSystemVariable("ACADVER")?.ToString());
  }

  private static void AttachSaveHandler(Autodesk.AutoCAD.DatabaseServices.Database database)
  {
    if (SaveHandlers.ContainsKey(database)) return;
    Autodesk.AutoCAD.DatabaseServices.DatabaseIOEventHandler handler = (_, _) =>
    {
      try { ScheduleDiscoveryRefresh(); }
      catch (Exception error) { PluginLog.Warn("Discovery", "Could not schedule discovery after save.", error); }
    };
    database.SaveComplete += handler;
    SaveHandlers.Add(database, handler);
  }

  private static void DetachSaveHandler(Autodesk.AutoCAD.DatabaseServices.Database database)
  {
    if (!SaveHandlers.Remove(database, out var handler)) return;
    database.SaveComplete -= handler;
  }

  public static PluginStatus GetStatus()
  {
    lock (Sync)
    {
      return new PluginStatus(
        _server != null,
        _activeOperations > 0,
        _currentOperation,
        _queueDepth,
        MaxQueuedHostOperations,
        _currentOperationStartedAtUnixMs,
        _currentRequestId);
    }
  }

  public static async Task<string> HandleRawRequestAsync(string rawRequest, CancellationToken cancellationToken)
  {
    JsonNode? parsed;
    try
    {
      parsed = JsonNode.Parse(rawRequest);
    }
    catch (Exception ex)
    {
      return JsonRpcProtocol.SerializeError(null, -32700, "CIVIL3D.INVALID_JSON", $"Invalid JSON request: {ex.Message}");
    }

    if (parsed is not JsonObject request)
    {
      return JsonRpcProtocol.SerializeError(null, -32600, "CIVIL3D.INVALID_REQUEST", "JSON-RPC request must be an object.");
    }

    var id = request["id"]?.DeepClone();
    if (request["jsonrpc"] is not JsonValue versionValue
      || !versionValue.TryGetValue<string>(out var version)
      || version != "2.0")
    {
      return JsonRpcProtocol.SerializeError(id, -32600, "CIVIL3D.INVALID_REQUEST", "JSON-RPC request must specify jsonrpc='2.0'.");
    }

    var method = request["method"] is JsonValue methodValue
      && methodValue.TryGetValue<string>(out var methodText)
      ? methodText
      : null;

    if (string.IsNullOrWhiteSpace(method))
    {
      return JsonRpcProtocol.SerializeError(id, -32600, "CIVIL3D.INVALID_REQUEST", "JSON-RPC request is missing a string method.");
    }

    if (request["params"] != null && request["params"] is not JsonObject)
    {
      return JsonRpcProtocol.SerializeError(id, -32602, "CIVIL3D.INVALID_INPUT", "JSON-RPC params must be an object when provided.");
    }
    var parameters = request["params"] as JsonObject;

    // A discovery-capable listener must never silently receive traffic from the
    // client's automatic legacy fallback, even if that request has no identity.
    if (request["legacyFallback"] is JsonValue fallbackValue &&
        fallbackValue.TryGetValue<bool>(out var legacyFallback) && legacyFallback)
      return JsonRpcProtocol.SerializeError(id, -32011, "CIVIL3D.STALE_INSTANCE",
        "Automatic legacy fallback cannot target a discovery-capable Civil 3D instance. Refresh instance discovery and retry.");

    // The listener is bound before token/discovery initialization finishes.
    // Fail closed here so a stale request cannot wait for startup and then run.
    if (_discovery?.AllowsRequestIdentity(request["instanceIdentity"]) != true)
      return JsonRpcProtocol.SerializeError(id, -32011, "CIVIL3D.STALE_INSTANCE",
        "The selected Civil 3D instance changed. Refresh instance discovery and retry.");

    var previousOperation = CurrentRequestOperation.Value;
    var previousRequestId = CurrentRequestId.Value;
    var previousCancellation = CurrentRequestCancellation.Value;
    CurrentRequestOperation.Value = method;
    CurrentRequestId.Value = id?.ToJsonString();
    CurrentRequestCancellation.Value = cancellationToken;

    var timer = System.Diagnostics.Stopwatch.StartNew();
    try
    {
      PluginLog.Debug("Dispatch", $"-> {method} [{CurrentRequestId.Value ?? "no-id"}]");
      var result = await CommandDispatcher.DispatchAsync(method, parameters, cancellationToken);
      PluginLog.Debug("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] ok durationMs={timer.ElapsedMilliseconds}");
      return JsonRpcProtocol.SerializeResult(id, result);
    }
    catch (JsonRpcDispatchException ex)
    {
      // Domain-level errors are part of the contract; record at info so they
      // show up in diagnostics without looking like runtime faults.
      PluginLog.Info("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] dispatch error {ex.Code} durationMs={timer.ElapsedMilliseconds}: {ex.Message}");
      return JsonRpcProtocol.SerializeError(id, JsonRpcProtocol.NumericErrorCode(ex.Code), ex.Code, ex.Message);
    }
    catch (OperationCanceledException)
    {
      PluginLog.Info("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] cancelled durationMs={timer.ElapsedMilliseconds}");
      return JsonRpcProtocol.SerializeError(id, -32010, "CIVIL3D.CANCELLED", $"Operation '{method}' was cancelled.");
    }
    catch (Exception ex)
    {
      PluginLog.Error("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] unhandled failure durationMs={timer.ElapsedMilliseconds} category={ex.GetType().Name}", ex);
      return JsonRpcProtocol.SerializeError(id, -32603, "CIVIL3D.INTERNAL_ERROR", "The Civil 3D plugin encountered an unexpected error.");
    }
    finally
    {
      CurrentRequestOperation.Value = previousOperation;
      CurrentRequestId.Value = previousRequestId;
      CurrentRequestCancellation.Value = previousCancellation;
    }
  }

  internal static CancellationToken GetCurrentRequestCancellationToken() => CurrentRequestCancellation.Value;

  internal static string GetCurrentRequestOperation() => CurrentRequestOperation.Value ?? "Civil 3D operation";

  internal static string? GetCurrentRequestId() => CurrentRequestId.Value;

  internal static string? GetActiveDrawingIdentity()
  {
    var document = App.DocumentManager.MdiActiveDocument;
    return GetDrawingIdentity(document);
  }

  internal static string? GetDrawingIdentity(Autodesk.AutoCAD.ApplicationServices.Document? document)
  {
    if (document == null) return null;
    var fileName = document.Database.Filename;
    return string.IsNullOrWhiteSpace(fileName) ? document.Name : fileName;
  }

  internal static string? GetExpectedDrawingIdentity() => CurrentExpectedDrawingIdentity.Value;

  internal static async Task<T> RunWithRequestContextAsync<T>(
    string operation,
    string requestId,
    CancellationToken cancellationToken,
    string? expectedDrawingIdentity,
    Func<Task<T>> action)
  {
    var previousOperation = CurrentRequestOperation.Value;
    var previousRequestId = CurrentRequestId.Value;
    var previousCancellation = CurrentRequestCancellation.Value;
    var previousExpectedDrawingIdentity = CurrentExpectedDrawingIdentity.Value;
    CurrentRequestOperation.Value = operation;
    CurrentRequestId.Value = requestId;
    CurrentRequestCancellation.Value = cancellationToken;
    CurrentExpectedDrawingIdentity.Value = expectedDrawingIdentity;
    try
    {
      return await action();
    }
    finally
    {
      CurrentRequestOperation.Value = previousOperation;
      CurrentRequestId.Value = previousRequestId;
      CurrentRequestCancellation.Value = previousCancellation;
      CurrentExpectedDrawingIdentity.Value = previousExpectedDrawingIdentity;
    }
  }

  internal static void QueueHostOperation()
  {
    lock (Sync)
    {
      if (_queueDepth >= MaxQueuedHostOperations)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.HOST_BUSY",
          $"Civil 3D host queue is full ({MaxQueuedHostOperations} operations). Retry after current work completes.");
      }

      _queueDepth++;
    }
  }

  internal static void StartHostOperation()
  {
    lock (Sync)
    {
      _queueDepth = Math.Max(0, _queueDepth - 1);
      _activeOperations++;
      _currentOperation = GetCurrentRequestOperation();
      _currentRequestId = GetCurrentRequestId();
      _currentOperationStartedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
  }

  internal static void CancelQueuedHostOperation()
  {
    lock (Sync)
    {
      _queueDepth = Math.Max(0, _queueDepth - 1);
    }
  }

  internal static void CompleteHostOperation()
  {
    lock (Sync)
    {
      _activeOperations = Math.Max(0, _activeOperations - 1);
      if (_activeOperations == 0)
      {
        _currentOperation = null;
        _currentRequestId = null;
        _currentOperationStartedAtUnixMs = null;
      }
    }
  }

  public static object? GetParameter(JsonObject? parameters, string name)
  {
    if (parameters == null)
    {
      return null;
    }

    return parameters.TryGetPropertyValue(name, out var value) ? value : null;
  }

  public static string GetRequiredString(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    }

    var stringValue = value.GetValue<string>();
    if (string.IsNullOrWhiteSpace(stringValue))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Parameter '{name}' must be a non-empty string.");
    }

    return stringValue;
  }

  public static double GetRequiredDouble(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    }

    return value.GetValue<double>();
  }

  public static int GetRequiredInt(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    }

    return value.GetValue<int>();
  }

  public static string? GetOptionalString(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : value.GetValue<string?>();
  }

  public static double? GetOptionalDouble(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : value.GetValue<double>();
  }

  public static int? GetOptionalInt(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : value.GetValue<int>();
  }

  public static bool? GetOptionalBool(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : value.GetValue<bool>();
  }

}
