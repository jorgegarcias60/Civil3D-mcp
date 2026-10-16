using System.Text.Json;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Civil3DMcpPlugin;

internal static class InstanceDiscoveryChecks
{
  // Issue #39: records of exited instances and reused pids go; live, uninspectable and foreign files stay.
  private static void CheckPruneStale(Action<bool, string> expect)
  {
    var root = Path.Combine(Path.GetTempPath(), "c3d-prune-harness-" + Guid.NewGuid().ToString("N"));
    var folder = Path.Combine(root, "instances");
    Directory.CreateDirectory(folder);
    try
    {
      using var self = Process.GetCurrentProcess();
      var selfStart = new DateTimeOffset(self.StartTime.ToUniversalTime());
      string Record(DateTimeOffset started) => JsonSerializer.Serialize(new { startedAtUtc = started.ToString("o") });
      string Write(string name, string content)
      {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
      }
      // Real process checks: int.MaxValue is never a running pid; this harness is a live one.
      var dead = Write($"{int.MaxValue}.json", Record(selfStart));
      var deadUnreadable = Write($"{int.MaxValue - 1}.json", "{ not json");
      var live = Write($"{self.Id}.json", Record(selfStart));
      var removed = InstanceDiscovery.PruneStaleRecords(ownPid: 0, root: root);
      expect(removed == 2 && !File.Exists(dead) && !File.Exists(deadUnreadable) && File.Exists(live),
        "pruning removes records of exited pids, readable or not, and keeps a live instance");

      File.WriteAllText(live, Record(selfStart.AddMinutes(-5)));
      expect(InstanceDiscovery.PruneStaleRecords(ownPid: 0, root: root) == 1 && !File.Exists(live),
        "pruning removes a record whose pid now belongs to a process started at another time");

      var unreadableLive = Write($"{self.Id}.json", "{ not json");
      var own = Write("4242.json", Record(selfStart));
      var foreign = Write("notes.json", "{}");
      var freshTemp = Write($"{self.Id}.json.{Guid.NewGuid():N}.tmp", "{}");
      var oldTemp = Write($"{self.Id}.json.{Guid.NewGuid():N}.tmp", "{}");
      File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow.AddHours(-2));
      var asked = new List<int>();
      removed = InstanceDiscovery.PruneStaleRecords(ownPid: 4242, root: root, isGone: (pid, _) => { asked.Add(pid); return false; });
      expect(removed == 1 && !File.Exists(oldTemp) && File.Exists(freshTemp) && File.Exists(unreadableLive) &&
        File.Exists(own) && File.Exists(foreign) && asked.SequenceEqual(new[] { self.Id }),
        "pruning keeps uninspectable records, its own pid and other files, and removes only old temp files");

      var started = new List<DateTimeOffset?>();
      File.WriteAllText(unreadableLive, Record(selfStart));
      InstanceDiscovery.PruneStaleRecords(ownPid: 4242, root: root, isGone: (_, s) => { started.Add(s); return false; });
      expect(started.Count == 1 && started[0] == selfStart, "pruning passes the record's start time to the identity check");
      expect(InstanceDiscovery.PruneStaleRecords(ownPid: 0, root: Path.Combine(root, "missing")) == 0,
        "pruning a missing folder removes nothing");
    }
    finally { Directory.Delete(root, true); }
  }

  internal static void Run(Action<bool, string> expect)
  {
    CheckGraceClaims(expect);
    CheckPruneStale(expect);
    var now = Stopwatch.GetTimestamp();
    var deadline = new IdleDeadline(TimeSpan.FromSeconds(10), now);
    expect(!deadline.IsExpired(now) &&
      !deadline.IsExpired(now + 9 * Stopwatch.Frequency) &&
      deadline.IsExpired(now + 10 * Stopwatch.Frequency),
      "deferred host action expires at its monotonic deadline");
    try
    {
      InstancePortBinder.Bind(_ => false);
      expect(false, "instance port selection reports a full range");
    }
    catch (IOException) { expect(true, "instance port selection reports a full range"); }
    var attempted = new List<int>();
    expect(InstancePortBinder.Bind(port => { attempted.Add(port); return port == 8759; }) == 8759 &&
      attempted.SequenceEqual(new[] { 8757, 8758, 8759 }),
      "production port binder retries occupied ports in order");
    TcpListener? listener = null;
    try
    {
      var bound = InstancePortBinder.Bind(port =>
      {
        var candidate = new TcpListener(IPAddress.Loopback, port);
        try { candidate.Start(); listener = candidate; return true; }
        catch (SocketException) { candidate.Stop(); return false; }
      });
      expect(listener != null && bound >= 8757 && bound <= 8766,
        "production binder acquires a real loopback listener");
    }
    finally { listener?.Stop(); }
    expect(InstanceDiscovery.TokenPathForPort(8757, "C:\\scratch").EndsWith("rpc-token-8757", StringComparison.Ordinal) &&
      InstanceDiscovery.TokenPathForPort(8758, "C:\\scratch").EndsWith("rpc-token-8758", StringComparison.Ordinal),
      "every instance has a port-specific token path");

    var tokenRoot = Path.Combine(Path.GetTempPath(), "c3d-token-harness-" + Guid.NewGuid().ToString("N"));
    var previousTokenPath = RpcAuth.TokenPath;
    try
    {
      RpcAuth.TokenPath = InstanceDiscovery.TokenPathForPort(8757, tokenRoot);
      var issued = RpcAuth.Issue();
      expect(issued && File.Exists(Path.Combine(tokenRoot, "rpc-token")) &&
        File.ReadAllText(RpcAuth.TokenPath) == File.ReadAllText(Path.Combine(tokenRoot, "rpc-token")),
        "port 8757 mirrors its token at the legacy rpc-token path");
      RpcAuth.Revoke();
      expect(!File.Exists(RpcAuth.TokenPath) && !File.Exists(Path.Combine(tokenRoot, "rpc-token")),
        "revoke removes both owned token files");
    }
    finally
    {
      RpcAuth.TokenPath = previousTokenPath;
      if (Directory.Exists(tokenRoot)) Directory.Delete(tokenRoot, true);
    }

    var root = Path.Combine(Path.GetTempPath(), "c3d-instance-harness-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "claims"));
    try
    {
      var drawing = Path.GetFullPath(Path.Combine(root, "Término-日本語.dwg"));
      var claimId = Guid.NewGuid().ToString("D");
      var launchToken = Guid.NewGuid().ToString("D");
      var startedAt = DateTimeOffset.UtcNow;
      var ownerServerPid = Environment.ProcessId;
      var ownerServerStartedAtUtc = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime());
      File.WriteAllText(Path.Combine(root, "claims", claimId + ".json"), JsonSerializer.Serialize(new
      {
        claimId, drawing = drawing.ToUpperInvariant(), ownerSession = "harness-session",
        ownerLabel = "harness-job", ownerServerPid, ownerServerStartedAtUtc,
        createdAtUtc = startedAt.AddSeconds(-1), launchToken, launchPid = 12001,
        launchStartedAtUtc = startedAt,
        expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2),
      }));
      var first = new InstanceDiscovery(12001, 8757, root, privateFiles: false, startedAtUtc: startedAt, launchToken: launchToken);
      var second = new InstanceDiscovery(12002, 8758, root, privateFiles: false, startedAtUtc: startedAt, launchToken: launchToken);
      expect(first.MatchesIdentity(new JsonObject
      {
        ["pid"] = 12001, ["startedAtUtc"] = first.StartedAtUtc.ToString("O"),
      }) && !first.MatchesIdentity(new JsonObject
      {
        ["pid"] = 12001, ["startedAtUtc"] = first.StartedAtUtc.AddMilliseconds(1).ToString("O"),
      }) && !first.MatchesIdentity(new JsonObject
      {
        ["pid"] = 12002, ["startedAtUtc"] = first.StartedAtUtc.ToString("O"),
      }), "routed RPC identity requires exact process id and start timestamp");
      second.UpdateDocuments(new[] { drawing }, "25.0");
      first.UpdateDocuments(new[] { drawing }, "25.0");
      expect(first.Owner == "agent" && second.Owner == "user",
        $"only process named in launch proof takes claim ({first.Owner}, {second.Owner})");
      expect(!first.AllowsRequestIdentity(null) && second.AllowsRequestIdentity(null),
        "identity-less old servers cannot drive an agent instance but retain user-instance compatibility");
      expect(first.AllowsRequestIdentity(new JsonObject
      {
        ["pid"] = 12001, ["startedAtUtc"] = first.StartedAtUtc.ToString("O"),
      }), "discovery-routed identity can drive the selected agent instance");
      expect(File.Exists(Path.Combine(root, "claims", claimId + ".taken-12001.json")),
        "won claim uses the specified taken-pid filename");
      var winner = first;
      using (var openRecord = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "instances", "12001.json"))))
        expect(openRecord.RootElement.GetProperty("documents")[0].GetString() == drawing,
          "Unicode drawing path round-trips through discovery JSON");
      winner.UpdateDocuments(Array.Empty<string>(), "25.0");
      expect(winner.Owner == "agent" && winner.ClaimId == claimId && winner.OwnerSession == "harness-session",
        "agent ownership stays assigned after document closes");
      using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "instances", "12001.json")));
      var data = record.RootElement;
      expect(data.GetProperty("instanceApi").GetInt32() == 1 &&
        data.GetProperty("port").GetInt32() == winner.Port &&
        data.GetProperty("tokenPath").GetString() == InstanceDiscovery.TokenPathForPort(winner.Port, root) &&
        data.GetProperty("documents").GetArrayLength() == 0 &&
        data.GetProperty("startedAtUtc").GetDateTimeOffset() <= data.GetProperty("updatedAtUtc").GetDateTimeOffset(),
        "discovery JSON includes version, port, token, documents and timestamps");
      expect(data.GetProperty("claimId").GetString() == claimId &&
        data.GetProperty("ownerSession").GetString() == "harness-session" &&
        data.GetProperty("ownerLabel").GetString() == "harness-job" &&
        data.GetProperty("ownerServerPid").GetInt32() == ownerServerPid &&
        data.GetProperty("ownerServerStartedAtUtc").GetDateTimeOffset() == ownerServerStartedAtUtc,
        "claim owner session, label and process identity survive discovery");
      var savedPath = Path.Combine(root, "saved-as.dwg");
      winner.UpdateDocuments(new[] { savedPath }, "25.0");
      using (var savedRecord = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "instances", "12001.json"))))
        expect(savedRecord.RootElement.GetProperty("documents")[0].GetString() == savedPath,
          "discovery refresh replaces old drawing paths after Save As");
      var restarted = new InstanceDiscovery(12001, 8759, root, privateFiles: false,
        ownership: winner.Ownership);
      restarted.UpdateDocuments(Array.Empty<string>(), "25.0");
      expect(restarted.Owner == "agent" && restarted.ClaimId == claimId &&
        restarted.OwnerSession == "harness-session",
        "listener restart retains process ownership");
      first.Delete(); second.Delete(); restarted.Delete();
      expect(!Directory.EnumerateFiles(Path.Combine(root, "instances"), "*.json").Any(),
        "shutdown removes each instance discovery file");
    }
    finally { Directory.Delete(root, true); }
    CheckPreexistingProcess(expect);
    CheckClaimReaderExclusion(expect);
    CheckLaunchProof(expect);
    CheckAdoption(expect);
    CheckIsolatedProcessContention(expect);
  }

  private static void CheckGraceClaims(Action<bool, string> expect)
  {
    var root = Path.Combine(Path.GetTempPath(), "c3d-grace-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "claims"));
    try
    {
      foreach (var expired in new[] { false, true })
      {
        var claimId = Guid.NewGuid().ToString("D");
        var token = Guid.NewGuid().ToString("D");
        var started = DateTimeOffset.UtcNow.AddMinutes(expired ? -16 : -7);
        var drawing = Path.Combine(root, expired ? "expired.dwg" : "late.dwg");
        File.WriteAllText(Path.Combine(root, "claims", claimId + ".json"), JsonSerializer.Serialize(new
        {
          claimId, drawing, ownerSession = "grace-session", launchToken = token,
          launchPid = 28001, launchStartedAtUtc = started,
          createdAtUtc = started.AddSeconds(-1), expiresAtUtc = started.AddMinutes(15),
        }));
        var discovery = new InstanceDiscovery(28001, 8757, root, privateFiles: false,
          startedAtUtc: started, launchToken: token);
        discovery.UpdateDocuments(new[] { drawing }, "25.1");
        expect(discovery.Owner == (expired ? "user" : "agent"), expired
          ? "plugin refuses claim after the fifteen-minute grace period"
          : "plugin claims a seven-minute-old launch within the fifteen-minute grace period");
      }
    }
    finally { Directory.Delete(root, true); }
  }

  private static void CheckAdoption(Action<bool, string> expect)
  {
    var root = Path.Combine(Path.GetTempPath(), "c3d-adoption-" + Guid.NewGuid().ToString("N"));
    try
    {
      var currentPid = Environment.ProcessId;
      var currentStart = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime());
      var formerPid = int.MaxValue;
      var formerStart = currentStart.AddMinutes(-1);
      var instance = new InstanceDiscovery(24001, 8757, root, privateFiles: false,
        ownership: new InstanceOwnership("old-session", "claim", "Old", true, formerPid, formerStart));
      instance.UpdateDocuments(Array.Empty<string>(), "25.0");
      expect(!instance.TryAdopt("wrong-session", formerPid, formerStart, "new-session", "New", currentPid, currentStart) &&
        !instance.TryAdopt("old-session", formerPid, formerStart.AddSeconds(1), "new-session", "New", currentPid, currentStart),
        "adoption rejects stale owner session and process identity");
      expect(!instance.TryAdopt("old-session", formerPid, formerStart, "new-session", "New", formerPid, formerStart),
        "adoption rejects a dead incoming server");
      expect(instance.TryAdopt("old-session", formerPid, formerStart, "new-session", "New", currentPid, currentStart),
        "dead owner process permits adoption by a live server");
      using (var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "instances", "24001.json"))))
      {
        var data = record.RootElement;
        expect(data.GetProperty("ownerSession").GetString() == "new-session" &&
          data.GetProperty("ownerLabel").GetString() == "New" &&
          data.GetProperty("ownerServerPid").GetInt32() == currentPid &&
          data.GetProperty("ownerServerStartedAtUtc").GetDateTimeOffset() == currentStart &&
          data.GetProperty("claimId").GetString() == "claim",
          "adoption atomically publishes new ownership in discovery");
      }
      var restarted = new InstanceDiscovery(24001, 8758, root, privateFiles: false, ownership: instance.Ownership);
      expect(restarted.OwnerSession == "new-session" && restarted.Ownership.OwnerServerPid == currentPid,
        "listener restart retains adopted ownership");
      var liveOwner = new InstanceDiscovery(24002, 8759, root, privateFiles: false,
        ownership: new InstanceOwnership("live-session", "claim-live", "Live", true, currentPid, currentStart));
      liveOwner.UpdateDocuments(Array.Empty<string>(), "25.0");
      expect(!liveOwner.TryAdopt("live-session", currentPid, currentStart, "new-session", "New", currentPid, currentStart),
        "live owner cannot be adopted");
      var reusedPid = new InstanceDiscovery(24003, 8760, root, privateFiles: false,
        ownership: new InstanceOwnership("reused-session", "claim-reused", "Reused", true,
          currentPid, currentStart.AddMilliseconds(-1)));
      reusedPid.UpdateDocuments(Array.Empty<string>(), "25.0");
      expect(reusedPid.TryAdopt("reused-session", currentPid, currentStart.AddMilliseconds(-1),
        "new-session", "New", currentPid, currentStart),
        "PID reused with a different start time permits adoption");
      var user = new InstanceDiscovery(24004, 8761, root, privateFiles: false,
        ownership: new InstanceOwnership(null, null, null, true));
      user.UpdateDocuments(Array.Empty<string>(), "25.0");
      expect(!user.TryAdopt("old-session", formerPid, formerStart, "new-session", "New", currentPid, currentStart),
        "user-owned instance cannot be adopted");
      var unproven = new InstanceDiscovery(24005, 8762, root, privateFiles: false,
        ownership: new InstanceOwnership("legacy-session", "legacy-claim", "Legacy", true));
      unproven.UpdateDocuments(Array.Empty<string>(), "25.0");
      expect(!unproven.TryAdopt("legacy-session", formerPid, formerStart, "new-session", "New", currentPid, currentStart),
        "agent instance without recorded server identity cannot be adopted");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
  }

  private static void CheckClaimReaderExclusion(Action<bool, string> expect)
  {
    var root = Path.Combine(Path.GetTempPath(), "c3d-claim-reader-" + Guid.NewGuid().ToString("N"));
    var claims = Path.Combine(root, "claims");
    Directory.CreateDirectory(claims);
    try
    {
      var drawing = Path.GetFullPath(Path.Combine(root, "reader.dwg"));
      var claimId = Guid.NewGuid().ToString("D");
      var path = Path.Combine(claims, claimId + ".json");
      var startedAt = DateTimeOffset.UtcNow;
      var launchToken = Guid.NewGuid().ToString("D");
      File.WriteAllText(path, JsonSerializer.Serialize(new
      {
        claimId, drawing, ownerSession = "reader-session",
        createdAtUtc = startedAt.AddSeconds(-1), expiresAtUtc = startedAt.AddMinutes(2),
        launchToken, launchPid = 19001, launchStartedAtUtc = startedAt,
      }));
      var blocked = new InstanceDiscovery(19001, 8757, root, privateFiles: false,
        startedAtUtc: startedAt, launchToken: launchToken);
      using (var heldReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete))
      {
        blocked.UpdateDocuments(new[] { drawing }, "25.0");
        expect(blocked.Owner == "user" && !blocked.Ownership.Finalized && File.Exists(path),
          "held claim reader defers ownership without taking the claim");
      }
      blocked.UpdateDocuments(new[] { drawing }, "25.0");
      expect(blocked.Owner == "agent" && blocked.Ownership.Finalized && !File.Exists(path) &&
        File.Exists(Path.Combine(claims, claimId + ".taken-19001.json")),
        "same discovery instance retries after reader releases claim");
      var restarted = new InstanceDiscovery(19001, 8758, root, privateFiles: false,
        ownership: blocked.Ownership);
      restarted.UpdateDocuments(Array.Empty<string>(), "25.0");
      expect(restarted.Owner == "agent" && restarted.ClaimId == claimId,
        "ownership recovered after a locked claim survives listener restart");
    }
    finally { Directory.Delete(root, true); }
  }

  private static void CheckPreexistingProcess(Action<bool, string> expect)
  {
    var root = Path.Combine(Path.GetTempPath(), "c3d-claim-age-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "claims"));
    try
    {
      var drawing = Path.GetFullPath(Path.Combine(root, "existing.dwg"));
      var fixedUser = new InstanceDiscovery(18002, 8759, root, privateFiles: false);
      fixedUser.UpdateDocuments(Array.Empty<string>(), "25.0");
      expect(!fixedUser.Ownership.Finalized, "startup ownership waits for an available document");
      fixedUser.UpdateDocuments(new[] { "" }, "25.0");
      expect(fixedUser.Ownership.Finalized, "an unsaved startup document finalizes user ownership");
      var eligibleId = Guid.NewGuid().ToString("D");
      File.WriteAllText(Path.Combine(root, "claims", eligibleId + ".json"), JsonSerializer.Serialize(new
      {
        claimId = eligibleId, drawing, ownerSession = "eligible-session",
        createdAtUtc = fixedUser.StartedAtUtc.AddSeconds(-1), expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2),
        launchToken = "eligible-token", launchPid = 18002, launchStartedAtUtc = fixedUser.StartedAtUtc,
      }));
      fixedUser.UpdateDocuments(new[] { drawing }, "25.0");
      expect(fixedUser.Owner == "user" && File.Exists(Path.Combine(root, "claims", eligibleId + ".json")),
        "finalized user ownership never takes a later matching claim");
      File.Delete(Path.Combine(root, "claims", eligibleId + ".json"));
      var claimId = Guid.NewGuid().ToString("D");
      File.WriteAllText(Path.Combine(root, "claims", claimId + ".json"), JsonSerializer.Serialize(new
      {
        claimId, drawing, ownerSession = "late-session",
        createdAtUtc = DateTimeOffset.UtcNow, expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2),
        launchToken = "late-token", launchPid = Environment.ProcessId,
        launchStartedAtUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
      }));
      var existing = new InstanceDiscovery(Environment.ProcessId, 8757, root, privateFiles: false,
        launchToken: "late-token");
      existing.UpdateDocuments(new[] { drawing }, "25.0");
      expect(existing.Owner == "user" && existing.Ownership.Finalized &&
        File.Exists(Path.Combine(root, "claims", claimId + ".json")),
        "pre-existing process rejects late claim and finalizes user ownership");
      existing.UpdateDocuments(new[] { drawing }, "25.0");
      expect(existing.Owner == "user", "final user ownership does not retry claims");

      var missingTimeId = Guid.NewGuid().ToString("D");
      File.WriteAllText(Path.Combine(root, "claims", missingTimeId + ".json"), JsonSerializer.Serialize(new
      {
        claimId = missingTimeId, drawing, ownerSession = "missing-time",
        expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2),
      }));
      var unbound = new InstanceDiscovery(18001, 8758, root, privateFiles: false);
      unbound.UpdateDocuments(new[] { drawing }, "25.0");
      expect(unbound.Owner == "user" && File.Exists(Path.Combine(root, "claims", missingTimeId + ".json")),
        "claim without creation timestamp cannot bind to a process");
    }
    finally { Directory.Delete(root, true); }
  }

  private static void CheckLaunchProof(Action<bool, string> expect)
  {
    var root = Path.Combine(Path.GetTempPath(), "c3d-launch-proof-" + Guid.NewGuid().ToString("N"));
    var claims = Path.Combine(root, "claims");
    Directory.CreateDirectory(claims);
    try
    {
      var drawing = Path.GetFullPath(Path.Combine(root, "launch.dwg"));
      var claimId = Guid.NewGuid().ToString("D");
      var path = Path.Combine(claims, claimId + ".json");
      var token = Guid.NewGuid().ToString("D");
      var startedAt = DateTimeOffset.UtcNow;
      void WriteClaim(int? pid, DateTimeOffset? start) => File.WriteAllText(path, JsonSerializer.Serialize(new
      {
        claimId, drawing, ownerSession = "launched-session", launchToken = token,
        launchPid = pid, launchStartedAtUtc = start,
        createdAtUtc = startedAt.AddSeconds(-1), expiresAtUtc = startedAt.AddMinutes(2),
      }));

      WriteClaim(22001, startedAt);
      var noToken = new InstanceDiscovery(22001, 8757, root, false, startedAt, launchToken: "");
      var wrongToken = new InstanceDiscovery(22001, 8758, root, false, startedAt, launchToken: "wrong-token");
      var wrongPid = new InstanceDiscovery(22002, 8759, root, false, startedAt, launchToken: token);
      var wrongStart = new InstanceDiscovery(22001, 8760, root, false, startedAt.AddMilliseconds(1), launchToken: token);
      foreach (var instance in new[] { noToken, wrongToken, wrongPid, wrongStart })
      {
        instance.UpdateDocuments(new[] { drawing }, "25.0");
        expect(instance.Owner == "user" && instance.Ownership.Finalized && File.Exists(path),
          "same drawing cannot take claim without exact launch token, PID and start proof");
      }
      File.Delete(path);
      WriteClaim(null, null);
      var launched = new InstanceDiscovery(22001, 8761, root, false, startedAt, launchToken: token);
      launched.UpdateDocuments(new[] { "" }, "25.0");
      expect(launched.Owner == "user" && !launched.Ownership.Finalized && File.Exists(path),
        "matching launch token waits for pending proof before drawing opens");
      var temp = path + ".tmp";
      File.WriteAllText(temp, JsonSerializer.Serialize(new
      {
        claimId, drawing, ownerSession = "launched-session", launchToken = token,
        launchPid = 22001, launchStartedAtUtc = startedAt,
        createdAtUtc = startedAt.AddSeconds(-1), expiresAtUtc = startedAt.AddMinutes(2),
      }));
      File.Move(temp, path, true);
      launched.UpdateDocuments(new[] { "" }, "25.0");
      expect(launched.Owner == "user" && !launched.Ownership.Finalized,
        "bound launch waits for its target drawing to open");
      launched.UpdateDocuments(new[] { drawing }, "25.0");
      expect(launched.Owner == "agent" && launched.Ownership.Finalized && launched.ClaimId == claimId,
        "matching launched instance takes claim after atomic proof publication");
    }
    finally { Directory.Delete(root, true); }
  }

  private static void CheckIsolatedProcessContention(Action<bool, string> expect)
  {
    var root = Path.Combine(Path.GetTempPath(), "c3d-process-claim-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "claims"));
    try
    {
      var drawing = Path.GetFullPath(Path.Combine(root, "shared.dwg"));
      var claimId = Guid.NewGuid().ToString("D");
      var token = Guid.NewGuid().ToString("D");
      var startedAt = DateTimeOffset.UtcNow;
      File.WriteAllText(Path.Combine(root, "claims", claimId + ".json"), JsonSerializer.Serialize(new
      {
        claimId, drawing, ownerSession = "process-session",
        createdAtUtc = startedAt.AddSeconds(-1), expiresAtUtc = startedAt.AddMinutes(2),
        launchToken = token, launchPid = 23001, launchStartedAtUtc = startedAt,
      }));
      var gate = Path.Combine(root, "go");
      using var first = StartContender(root, drawing, gate, token, startedAt);
      using var second = StartContender(root, drawing, gate, token, startedAt);
      var ready = SpinWait.SpinUntil(() => Directory.GetFiles(root, "ready-*").Length == 2, 10000);
      File.WriteAllText(gate, "go");
      var exited = first.WaitForExit(10000) && second.WaitForExit(10000);
      expect(ready && exited && first.ExitCode == 0 && second.ExitCode == 0,
        "two isolated contenders synchronize before claiming");
      if (exited)
      {
        var owners = Directory.GetFiles(root, "result-*").Select(File.ReadAllText).ToArray();
        expect(owners.Length == 2 && owners.Count(x => x == "agent") == 1,
          $"atomic claim has exactly one winner across processes ({string.Join(",", owners)})");
      }
    }
    finally { Directory.Delete(root, true); }
  }

  private static Process StartContender(string root, string drawing, string gate, string token, DateTimeOffset startedAt)
  {
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    start.ArgumentList.Add("--instance-contender");
    start.ArgumentList.Add(root);
    start.ArgumentList.Add(drawing);
    start.ArgumentList.Add(gate);
    start.ArgumentList.Add(token);
    start.ArgumentList.Add(startedAt.ToString("O"));
    return Process.Start(start)!;
  }

  internal static int RunContender(string root, string drawing, string gate, string token, DateTimeOffset startedAt)
  {
    File.WriteAllText(Path.Combine(root, "ready-" + Environment.ProcessId), "ready");
    if (!SpinWait.SpinUntil(() => File.Exists(gate), 10000)) return 1;
    var instance = new InstanceDiscovery(23001, 8757, root, privateFiles: false,
      startedAtUtc: startedAt, launchToken: token);
    // Both test processes impersonate one bound PID, so their instance records
    // collide. The ownership decision happens before the record write.
    try { instance.UpdateDocuments(new[] { drawing }, "25.0"); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
    File.WriteAllText(Path.Combine(root, "result-" + Environment.ProcessId), instance.Owner);
    return 0;
  }
}
