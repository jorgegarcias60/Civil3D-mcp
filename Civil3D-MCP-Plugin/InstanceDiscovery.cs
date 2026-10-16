using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

// File protocol shared with the MCP server. This class deliberately has no AutoCAD dependency.
internal sealed class InstanceDiscovery
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
  };
  private static readonly object ClaimSync = new();
  internal const int Api = 1;
  internal const int FirstPort = 8757;
  internal const int LastPort = 8766;
  internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Civil3DMcpPlugin");
  internal static string TokenPathForPort(int port, string? root = null) => Path.Combine(root ?? Root, $"rpc-token-{port}");

  private readonly object _sync = new();
  private readonly string _root;
  private readonly int _pid;
  private readonly bool _privateFiles;
  private readonly DateTimeOffset _started;
  private readonly string? _launchToken;
  private bool _ownershipFinalized;
  private string? _ownerSession;
  private string? _claimId;
  private string? _label;
  private int? _ownerServerPid;
  private DateTimeOffset? _ownerServerStartedAtUtc;
  private string? _acadVersion;
  private readonly HashSet<string> _documents = new(StringComparer.OrdinalIgnoreCase);
  internal int Port { get; }
  internal string Owner => _ownerSession == null ? "user" : "agent";
  internal string? OwnerSession { get { lock (_sync) return _ownerSession; } }
  internal string? ClaimId { get { lock (_sync) return _claimId; } }
  internal string? Label { get { lock (_sync) return _label; } }
  internal DateTimeOffset StartedAtUtc => _started;
  internal bool OwnershipFinalized { get { lock (_sync) return _ownershipFinalized; } }
  internal bool AllowsRequestIdentity(JsonNode? identity) => identity == null ? Owner != "agent" : MatchesIdentity(identity);
  internal bool MatchesIdentity(JsonNode? node)
  {
    if (node is not JsonObject identity) return false;
    try
    {
      return identity["pid"]?.GetValue<int>() == _pid &&
        DateTimeOffset.TryParse(identity["startedAtUtc"]?.GetValue<string>(), out var started) &&
        started == _started;
    }
    catch (Exception) { return false; }
  }
  internal InstanceOwnership Ownership { get { lock (_sync) return new(_ownerSession, _claimId, _label, _ownershipFinalized,
    _ownerServerPid, _ownerServerStartedAtUtc); } }

  internal InstanceDiscovery(int pid, int port, string? root = null, bool privateFiles = true,
    DateTimeOffset? startedAtUtc = null, InstanceOwnership? ownership = null, string? launchToken = null)
  {
    _pid = pid;
    Port = port;
    _root = root ?? Root;
    _privateFiles = privateFiles;
    _started = startedAtUtc ?? Process.GetCurrentProcess().StartTime.ToUniversalTime();
    _launchToken = launchToken ?? Environment.GetEnvironmentVariable("CIVIL3D_MCP_LAUNCH_TOKEN");
    if (ownership != null)
    {
      _ownerSession = ownership.OwnerSession;
      _claimId = ownership.ClaimId;
      _label = ownership.Label;
      _ownershipFinalized = ownership.Finalized;
      _ownerServerPid = ownership.OwnerServerPid;
      _ownerServerStartedAtUtc = ownership.OwnerServerStartedAtUtc;
    }
  }

  internal void UpdateDocuments(IEnumerable<string> paths, string? acadVersion)
  {
    lock (_sync)
    {
      _acadVersion = acadVersion;
      _documents.Clear();
      var documentsAvailable = false;
      foreach (var path in paths)
      {
        documentsAvailable = true;
        if (Path.IsPathFullyQualified(path)) _documents.Add(Path.GetFullPath(path));
      }
      if (!_ownershipFinalized && documentsAvailable)
      {
        lock (ClaimSync) _ownershipFinalized = TryClaim();
      }
      Write(acadVersion);
    }
  }

  // False means a relevant claim may become readable or receive its launch proof.
  private bool TryClaim()
  {
    var directory = Path.Combine(_root, "claims");
    if (!Directory.Exists(directory)) return true;
    var retry = false;
    foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
    {
      if (Path.GetFileName(path).Contains(".taken-", StringComparison.OrdinalIgnoreCase)) continue;
      if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out _)) continue;
      try
      {
        // Keep the source open until the rename has elected its owner. Other
        // contenders need read access and cannot inspect the same claim while
        // this handle is open; FileShare.Delete still permits our rename.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
        Claim? claim;
        try { claim = JsonSerializer.Deserialize<Claim>(source, JsonOptions); }
        catch (JsonException) { continue; }
        string normalizedDrawing;
        try
        {
          if (claim == null || string.IsNullOrWhiteSpace(claim.Drawing) || !Path.IsPathFullyQualified(claim.Drawing)) continue;
          normalizedDrawing = Path.GetFullPath(claim.Drawing);
        }
        catch (Exception) { continue; }
        if (claim == null || !string.Equals(Path.GetFileNameWithoutExtension(path), claim.ClaimId, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(claim.OwnerSession) || string.IsNullOrWhiteSpace(claim.Drawing) ||
            claim.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            claim.CreatedAtUtc == default || _started <= claim.CreatedAtUtc) continue;
        if (string.IsNullOrWhiteSpace(_launchToken) ||
            !string.Equals(claim.LaunchToken, _launchToken, StringComparison.Ordinal)) continue;
        if (claim.LaunchPid == null || claim.LaunchStartedAtUtc == null)
        {
          retry = true;
          continue;
        }
        if (claim.LaunchPid != _pid || claim.LaunchStartedAtUtc != _started) continue;
        if (!_documents.Contains(normalizedDrawing))
        {
          retry = true;
          continue;
        }
        var taken = Path.Combine(directory, $"{claim.ClaimId}.taken-{_pid}.json");
        File.Move(path, taken);
        _claimId = claim.ClaimId;
        _ownerSession = claim.OwnerSession;
        _label = claim.OwnerLabel ?? claim.Label;
        _ownerServerPid = claim.OwnerServerPid;
        _ownerServerStartedAtUtc = claim.OwnerServerStartedAtUtc;
        return true;
      }
      catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { retry = true; }
      catch (IOException) { /* A removed or already-taken claim is a completed scan. */ }
      catch (UnauthorizedAccessException) { /* An inaccessible claim cannot be taken. */ }
    }
    return !retry;
  }

  private void Write(string? acadVersion)
  {
    var directory = Path.Combine(_root, "instances");
    if (_privateFiles) { RpcAuth.EnsurePrivateDirectory(_root); RpcAuth.EnsurePrivateDirectory(directory); }
    else Directory.CreateDirectory(directory);
    var target = Path.Combine(directory, $"{_pid}.json");
    var temp = target + $".{Guid.NewGuid():N}.tmp";
    var record = new InstanceRecord(Api, _pid, Port, TokenPathForPort(Port, _root), _started,
      acadVersion, Owner, _ownerSession, _claimId, _label, _label, _ownerServerPid, _ownerServerStartedAtUtc,
      _documents.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(), DateTimeOffset.UtcNow);
    try
    {
      if (_privateFiles) RpcAuth.WritePrivateFile(temp, JsonSerializer.Serialize(record, JsonOptions));
      else File.WriteAllText(temp, JsonSerializer.Serialize(record, JsonOptions));
      File.Move(temp, target, true);
    }
    finally { if (File.Exists(temp)) File.Delete(temp); }
  }

  // The plugin checks both process identities itself. Missing old proof cannot
  // establish an orphan, so legacy agent records are deliberately not adoptable.
  internal bool TryAdopt(string expectedOwnerSession, int expectedOwnerServerPid,
    DateTimeOffset expectedOwnerServerStartedAtUtc, string ownerSession, string? ownerLabel,
    int ownerServerPid, DateTimeOffset ownerServerStartedAtUtc)
  {
    lock (_sync)
    {
      if (_ownerSession == null || !_ownershipFinalized ||
          !string.Equals(_ownerSession, expectedOwnerSession, StringComparison.Ordinal) ||
          _ownerServerPid != expectedOwnerServerPid ||
          _ownerServerStartedAtUtc != expectedOwnerServerStartedAtUtc ||
          string.IsNullOrWhiteSpace(ownerSession) || ownerServerPid <= 0 || ownerServerStartedAtUtc == default ||
          !IsLiveProcess(ownerServerPid, ownerServerStartedAtUtc) ||
          IsLiveProcess(expectedOwnerServerPid, expectedOwnerServerStartedAtUtc)) return false;
      var priorSession = _ownerSession;
      var priorLabel = _label;
      var priorPid = _ownerServerPid;
      var priorStarted = _ownerServerStartedAtUtc;
      _ownerSession = ownerSession;
      _label = ownerLabel;
      _ownerServerPid = ownerServerPid;
      _ownerServerStartedAtUtc = ownerServerStartedAtUtc;
      try { Write(_acadVersion); }
      catch
      {
        _ownerSession = priorSession;
        _label = priorLabel;
        _ownerServerPid = priorPid;
        _ownerServerStartedAtUtc = priorStarted;
        throw;
      }
      return true;
    }
  }

  private static bool IsLiveProcess(int pid, DateTimeOffset startedAtUtc)
  {
    if (pid <= 0 || startedAtUtc == default) return true; // Invalid old proof is not evidence of death.
    try
    {
      using var process = Process.GetProcessById(pid);
      return !process.HasExited && process.StartTime.ToUniversalTime() == startedAtUtc;
    }
    catch (ArgumentException) { return false; }
    catch (InvalidOperationException) { return false; }
    catch (System.ComponentModel.Win32Exception) { return true; } // Inaccessible identity fails closed.
  }

  internal void Delete()
  {
    lock (_sync)
    {
      var path = Path.Combine(_root, "instances", $"{_pid}.json");
      if (File.Exists(path)) File.Delete(path);
      if (_claimId != null)
      {
        var taken = Path.Combine(_root, "claims", $"{_claimId}.taken-{_pid}.json");
        if (File.Exists(taken)) File.Delete(taken);
      }
    }
  }

  // Removes the discovery records that crashed or killed instances left behind (issue #39): the
  // process has exited, or its pid now belongs to a process started at another time. A record
  // whose process cannot be inspected is kept, and so is an unreadable record of a live pid.
  // Temp files older than an hour go too. The MCP server already removes expired claims and
  // claims of dead launches when it starts an instance. Returns the number of files removed.
  internal static int PruneStaleRecords(int ownPid, string? root = null, Func<int, DateTimeOffset?, bool>? isGone = null)
  {
    var directory = Path.Combine(root ?? Root, "instances");
    if (!Directory.Exists(directory)) return 0;
    isGone ??= IsGoneProcess;
    var removed = 0;
    foreach (var path in Directory.EnumerateFiles(directory))
    {
      try
      {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
          if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= TimeSpan.FromHours(1)) continue;
        }
        else
        {
          if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
              !int.TryParse(Path.GetFileNameWithoutExtension(name), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var pid) || pid <= 0 || pid == ownPid) continue;
          DateTimeOffset? started = null;
          try
          {
            if (JsonNode.Parse(File.ReadAllText(path))?["startedAtUtc"]?.GetValue<string>() is string text &&
                DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                  System.Globalization.DateTimeStyles.None, out var parsed)) started = parsed;
          }
          catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { }
          if (!isGone(pid, started)) continue;
        }
        File.Delete(path);
        removed++;
      }
      catch (IOException) { /* Locked or already removed: the next start tries again. */ }
      catch (UnauthorizedAccessException) { /* Not ours to remove. */ }
    }
    return removed;
  }

  // True when the pid runs no process, or a process that started at another time than the record
  // says (pid reuse; a second of tolerance for clock rounding). Unknown start: only exit counts.
  private static bool IsGoneProcess(int pid, DateTimeOffset? startedAtUtc)
  {
    try
    {
      using var process = Process.GetProcessById(pid);
      if (process.HasExited) return true;
      return startedAtUtc != null &&
        Math.Abs((process.StartTime.ToUniversalTime() - startedAtUtc.Value.UtcDateTime).TotalSeconds) > 1;
    }
    catch (ArgumentException) { return true; }
    catch (InvalidOperationException) { return true; }
    catch (System.ComponentModel.Win32Exception) { return false; } // Cannot inspect: keep the record.
  }

  private sealed record Claim(string ClaimId, string Drawing, string OwnerSession, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc,
    string? Label, string? LaunchToken, int? LaunchPid, DateTimeOffset? LaunchStartedAtUtc,
    string? OwnerLabel, int? OwnerServerPid, DateTimeOffset? OwnerServerStartedAtUtc);
  private sealed record InstanceRecord(int InstanceApi, int Pid, int Port, string TokenPath,
    DateTimeOffset StartedAtUtc, string? AcadVersion, string Owner, string? OwnerSession,
    string? ClaimId, string? Label, string? OwnerLabel, int? OwnerServerPid,
    DateTimeOffset? OwnerServerStartedAtUtc, string[] Documents, DateTimeOffset UpdatedAtUtc);
}

internal sealed record InstanceOwnership(string? OwnerSession, string? ClaimId, string? Label, bool Finalized,
  int? OwnerServerPid = null, DateTimeOffset? OwnerServerStartedAtUtc = null);
