using System.Diagnostics;
using System.Globalization;
using Civil3DMcpPlugin;

// Offline, isolated checks of run-code folder retention. Never point these at
// the user's plugin log or an AutoCAD installation.
internal static class RetentionChecks
{
  private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Local);
  private const string CurrentId = "20200101-000000-1234abcd";

  internal static void Run(Action<bool, string> expect)
  {
    CheckConfiguration(expect);
    CheckCutoffAndNames(expect);
    CheckConfinement(expect);
    CheckLinks(expect);
    CheckLinkedAncestors(expect);
    CheckOutputLinks(expect);
    CheckLockedCandidate(expect);
    CheckOutputRetentionIndependence(expect);
  }

  private static void CheckConfiguration(Action<bool, string> expect)
  {
    var oldValue = Environment.GetEnvironmentVariable(RunCodeLog.FolderRetentionEnvironmentVariable);
    var warningCount = PluginLog.Warnings.Count;
    try
    {
      SetRetention(null);
      expect(RunCodeLog.ReadFolderRetentionDays() == RunCodeLog.DefaultFolderRetentionDays
        && RunCodeLog.DefaultFolderRetentionDays == 180, "folder retention defaults to 180 days");
      SetRetention("42");
      expect(RunCodeLog.ReadFolderRetentionDays() == 42, "folder retention accepts an integer override");
      SetRetention("0");
      expect(RunCodeLog.ReadFolderRetentionDays() == 0, "zero disables folder retention");
      SetRetention("-3");
      expect(RunCodeLog.ReadFolderRetentionDays() == -3, "negative retention disables folder retention");
      SetRetention("2147483647");
      expect(RunCodeLog.ReadFolderRetentionDays() == int.MaxValue, "the largest integer override is safe");
      foreach (var invalid in new[] { "garbage", "2147483648", "1.5" })
      {
        SetRetention(invalid);
        expect(RunCodeLog.ReadFolderRetentionDays() == 180,
          $"invalid folder retention '{invalid}' uses the default");
      }
      expect(PluginLog.Warnings.Count - warningCount == 1,
        "invalid retention warns only once process-wide, including overflow");

      InTempRoot(scope =>
      {
        var current = NewRun(CurrentId);
        var old = NewRun("20010101-000000-abcdef01");
        SetRetention("0");
        RunCodeLog.PruneOldRunFolders(current);
        expect(Directory.Exists(old), "the environment-based pruning entry point honors zero disablement");
        SetRetention("1");
        RunCodeLog.PruneOldRunFolders(current);
        expect(!Directory.Exists(old) && Directory.Exists(current),
          "the environment-based pruning entry point honors the override and protects the current run");
      });
      InTempRoot(scope =>
      {
        var current = NewRun(CurrentId);
        var old = NewRun("20010101-000000-abcdef02");
        var recentId = DateTime.Now.AddDays(-1).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-abcdef03";
        var recent = NewRun(recentId);
        SetRetention(null);
        RunCodeLog.PruneOldRunFolders(current);
        expect(!Directory.Exists(old) && Directory.Exists(recent),
          "default retention removes ancient runs but keeps recent ones");
      });
    }
    finally
    {
      SetRetention(oldValue);
    }
  }

  private static void CheckCutoffAndNames(Action<bool, string> expect)
  {
    InTempRoot(scope =>
    {
      var root = RunCodeLog.Root;
      var current = NewRun(CurrentId);
      var old = NewRun("20260415-115959-abcdef01");
      var boundary = NewRun("20260415-120000-abcdef02");
      var newer = NewRun("20260415-120001-abcdef03");
      var uppercase = NewRun("20260414-235959-ABCDEF09");
      var nested = Path.Combine(old, "nested");
      Directory.CreateDirectory(nested);
      var fresh = Path.Combine(nested, "fresh.txt");
      File.WriteAllText(fresh, "fresh content inside old run");
      File.SetLastWriteTime(fresh, Now);
      File.WriteAllText(Path.Combine(old, RunCodeLog.ScriptFile), "audit");
      File.WriteAllText(Path.Combine(old, RunCodeLog.SummaryFile), "{}");
      var badNames = new[]
      {
        "20260414-235959-abcdef0", "20260414-235959-abcdef012",
        "20260414-235959-abcdef0g", "20260414-235959-abcdef01-extra",
        "20261301-000000-abcdef01", "20260230-000000-abcdef01",
        "20260414-246000-abcdef01", "20260414-235960-abcdef01",
      };
      foreach (var name in badNames) NewRun(name);
      var nestedOld = Path.Combine(current, "20200101-000000-abcdef01");
      Directory.CreateDirectory(nestedOld);

      RunCodeLog.PruneOldRunFolders(current, 180, Now);
      expect(!Directory.Exists(old) && !Directory.Exists(uppercase),
        "runs older than the cutoff are removed recursively, including fresh nested files and uppercase suffixes");
      expect(Directory.Exists(boundary) && Directory.Exists(newer),
        "a run at the cutoff second or one second newer is kept");
      expect(Directory.Exists(current) && Directory.Exists(nestedOld)
        && badNames.All(name => Directory.Exists(Path.Combine(root, name))),
        "current, nested and malformed run names are kept");

      var thaiCulture = CultureInfo.GetCultureInfo("th-TH");
      var previousCulture = CultureInfo.CurrentCulture;
      try
      {
        CultureInfo.CurrentCulture = thaiCulture;
        var invariantOld = NewRun("20200107-000000-abcdef07");
        RunCodeLog.PruneOldRunFolders(current, 180, Now);
        expect(!Directory.Exists(invariantOld), "run identifiers parse with the invariant Gregorian calendar");
      }
      finally { CultureInfo.CurrentCulture = previousCulture; }

      var ancient = NewRun("20000101-000000-abcdef01");
      RunCodeLog.PruneOldRunFolders(current, int.MaxValue, Now);
      expect(Directory.Exists(ancient), "int.MaxValue retention cannot underflow the cutoff");
      RunCodeLog.PruneOldRunFolders(current, 0, Now);
      RunCodeLog.PruneOldRunFolders(current, -1, Now);
      expect(Directory.Exists(ancient), "zero and negative retention leave old folders intact");

      RunCodeLog.PruneOldRunFolders(Path.Combine(root, ".", CurrentId, "..", CurrentId), 1, Now);
      expect(Directory.Exists(current) && !Directory.Exists(ancient),
        "the current run is protected when its path contains normalized dot segments");
    });
  }

  private static void CheckConfinement(Action<bool, string> expect)
  {
    InTempRoot(scope =>
    {
      var current = NewRun(CurrentId);
      var old = NewRun("20200101-000000-abcdef01");
      var sibling = Path.Combine(scope.Folder, "run-code-sibling");
      Directory.CreateDirectory(sibling);
      var outside = Path.Combine(sibling, "20200101-000000-abcdef02");
      Directory.CreateDirectory(outside);
      RunCodeLog.PruneOldRunFolders(current, 1, Now);
      expect(!Directory.Exists(old) && Directory.Exists(outside),
        "pruning is confined to immediate children of the exact run-code root");

      old = NewRun("20200101-000000-abcdef03");
      RunCodeLog.PruneOldRunFolders(outside, 1, Now);
      expect(Directory.Exists(old) && Directory.Exists(outside),
        "a current path in a sibling root fails closed");
      RunCodeLog.PruneOldRunFolders("relative-run-folder", 1, Now);
      expect(Directory.Exists(old), "a relative current run path fails closed");
      var invalidCurrent = NewRun("not-a-run");
      RunCodeLog.PruneOldRunFolders(invalidCurrent, 1, Now);
      expect(Directory.Exists(old), "an invalid current run identifier fails closed");
    });
  }

  private static void CheckLinks(Action<bool, string> expect)
  {
    InTempRoot(scope =>
    {
      var current = NewRun(CurrentId);
      var outside = Path.Combine(scope.Folder, "outside");
      Directory.CreateDirectory(outside);
      var marker = Path.Combine(outside, "marker.txt");
      File.WriteAllText(marker, "keep");

      var candidateLink = Path.Combine(RunCodeLog.Root, "20200101-000000-abcdef01");
      if (TryDirectoryLink(candidateLink, outside, out var why))
      {
        scope.TrackLink(candidateLink);
        var ordinary = NewRun("20200102-000000-abcdef02");
        RunCodeLog.PruneOldRunFolders(current, 1, Now);
        expect(Directory.Exists(candidateLink) && File.Exists(marker) && !Directory.Exists(ordinary),
          "a linked candidate is skipped while another old run is pruned");
      }
      else Console.WriteLine($"SKIPPED: candidate directory link retention check: {why}");

      var nestedTarget = Path.Combine(scope.Folder, "nested-target");
      Directory.CreateDirectory(nestedTarget);
      var nestedMarker = Path.Combine(nestedTarget, "marker.txt");
      File.WriteAllText(nestedMarker, "keep");
      var nestedCandidate = NewRun("20200103-000000-abcdef03");
      var nestedLink = Path.Combine(nestedCandidate, "nested-link");
      if (TryDirectoryLink(nestedLink, nestedTarget, out why))
      {
        scope.TrackLink(nestedLink);
        var ordinary = NewRun("20200104-000000-abcdef04");
        RunCodeLog.PruneOldRunFolders(current, 1, Now);
        expect(Directory.Exists(nestedCandidate) && File.Exists(nestedMarker) && !Directory.Exists(ordinary),
          "a link inside a candidate preserves that whole candidate and does not stop later pruning");
      }
      else Console.WriteLine($"SKIPPED: nested directory link retention check: {why}");

      var fileTarget = Path.Combine(scope.Folder, "file-target.txt");
      File.WriteAllText(fileTarget, "keep");
      var fileCandidate = NewRun("20200105-000000-abcdef05");
      var fileLink = Path.Combine(fileCandidate, "linked-file.txt");
      var fileLinkCreated = false;
      try
      {
        File.CreateSymbolicLink(fileLink, fileTarget);
        scope.TrackFileLink(fileLink);
        fileLinkCreated = true;
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
      {
        Console.WriteLine($"SKIPPED: file symlink retention check: {exception.Message}");
      }
      if (fileLinkCreated)
      {
        var ordinary = NewRun("20200106-000000-abcdef06");
        RunCodeLog.PruneOldRunFolders(current, 1, Now);
        expect(Directory.Exists(fileCandidate) && File.ReadAllText(fileTarget) == "keep"
          && !Directory.Exists(ordinary),
          "a file symlink preserves its candidate while later candidates are pruned");
      }
    });

    InTempRoot(scope =>
    {
      var root = RunCodeLog.Root;
      var current = NewRun(CurrentId);
      var old = NewRun("20200101-000000-abcdef01");
      var real = Path.Combine(scope.Folder, "real-root");
      Directory.Move(root, real);
      if (!TryDirectoryLink(root, real, out var why))
      {
        Directory.Move(real, root);
        Console.WriteLine($"SKIPPED: linked run-code root check: {why}");
        return;
      }
      scope.TrackLink(root);
      RunCodeLog.PruneOldRunFolders(current, 1, Now);
      expect(Directory.Exists(old) && Directory.Exists(current), "a linked run-code root blocks pruning");
    });
  }

  private static void CheckLinkedAncestors(Action<bool, string> expect)
  {
    foreach (var logSubfolder in new[] { string.Empty, Path.Combine("profile", "AppData", "Local", "Civil3DMcpPlugin") })
    {
      InTempRoot(scope =>
      {
        var real = Path.Combine(scope.Folder, "real-parent");
        Directory.CreateDirectory(real);
        var linkedParent = Path.Combine(scope.Folder, "linked-parent");
        if (!TryDirectoryLink(linkedParent, real, out var why))
        {
          Console.WriteLine($"SKIPPED: linked run-code ancestor check: {why}");
          return;
        }
        scope.TrackLink(linkedParent);
        var previous = PluginLog.LogFilePath;
        try
        {
          PluginLog.LogFilePath = Path.Combine(real, logSubfolder, "plugin.log");
          var current = NewRun(CurrentId);
          var old = NewRun("20200101-000000-abcdef01");
          var output = Path.Combine(current, RunCodeLog.OutputFile);
          var script = Path.Combine(current, RunCodeLog.ScriptFile);
          var summary = Path.Combine(current, RunCodeLog.SummaryFile);
          foreach (var file in new[] { output, script, summary })
          {
            File.WriteAllText(file, "keep audit files");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-8));
          }
          PluginLog.LogFilePath = Path.Combine(linkedParent, logSubfolder, "plugin.log");
          var ancestor = logSubfolder.Length == 0 ? "immediate parent" : "higher ancestor";
          RunCodeLog.PruneOldRunFolders(Path.Combine(RunCodeLog.Root, CurrentId), 1, Now);
          expect(!Directory.Exists(old) && Directory.Exists(current),
            $"folder pruning through a linked {ancestor} removes old runs and keeps the current run");
          RunCodeLog.PruneOldOutputs(TimeSpan.FromDays(7));
          expect(!File.Exists(output) && File.Exists(script) && File.Exists(summary),
            $"seven-day output pruning through a linked {ancestor} removes outputs and keeps audit files");

          var outside = Path.Combine(linkedParent, "outside-run-code");
          Directory.CreateDirectory(outside);
          var marker = Path.Combine(outside, "output.txt");
          File.WriteAllText(marker, "keep");
          File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddDays(-8));
          var candidateLink = Path.Combine(RunCodeLog.Root, "20200102-000000-abcdef02");
          if (TryDirectoryLink(candidateLink, outside, out why))
          {
            scope.TrackLink(candidateLink);
            old = NewRun("20200103-000000-abcdef03");
            RunCodeLog.PruneOldRunFolders(Path.Combine(RunCodeLog.Root, CurrentId), 1, Now);
            RunCodeLog.PruneOldOutputs(TimeSpan.FromDays(7));
            expect(Directory.Exists(candidateLink) && File.ReadAllText(marker) == "keep" && !Directory.Exists(old),
              $"pruning through a linked {ancestor} still refuses links below Root and continues");
          }
          else Console.WriteLine($"SKIPPED: candidate link below a linked ancestor check: {why}");
        }
        finally { PluginLog.LogFilePath = previous; }
      });
    }
  }

  private static void CheckOutputLinks(Action<bool, string> expect)
  {
    InTempRoot(scope =>
    {
      var ordinary = NewRun(CurrentId);
      var ordinaryOutput = Path.Combine(ordinary, RunCodeLog.OutputFile);
      File.WriteAllText(ordinaryOutput, "old");
      File.SetLastWriteTimeUtc(ordinaryOutput, DateTime.UtcNow.AddDays(-8));
      var outside = Path.Combine(scope.Folder, "outside-output");
      Directory.CreateDirectory(outside);
      var outsideFile = Path.Combine(outside, "output.txt");
      File.WriteAllText(outsideFile, "keep");
      File.SetLastWriteTimeUtc(outsideFile, DateTime.UtcNow.AddDays(-8));
      var linkedCandidate = Path.Combine(RunCodeLog.Root, "20200101-000000-abcdef01");
      if (TryDirectoryLink(linkedCandidate, outside, out var why))
      {
        scope.TrackLink(linkedCandidate);
        RunCodeLog.PruneOldOutputs(TimeSpan.FromDays(7));
        expect(Directory.Exists(linkedCandidate) && File.Exists(outsideFile) && !File.Exists(ordinaryOutput),
          "seven-day output pruning skips a linked candidate and continues to an ordinary one");
      }
      else Console.WriteLine($"SKIPPED: linked candidate output pruning check: {why}");

      var nested = NewRun("20200102-000000-abcdef02");
      var linkFile = Path.Combine(nested, "linked-output.txt");
      var linkFileCreated = false;
      try
      {
        File.CreateSymbolicLink(linkFile, outsideFile);
        scope.TrackFileLink(linkFile);
        linkFileCreated = true;
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
      {
        Console.WriteLine($"SKIPPED: linked file output pruning check: {exception.Message}");
      }
      if (linkFileCreated)
      {
        RunCodeLog.PruneOldOutputs(TimeSpan.FromDays(7));
        expect(File.Exists(linkFile) && File.ReadAllText(outsideFile) == "keep",
          "seven-day output pruning skips a linked file");
      }
    });

    InTempRoot(scope =>
    {
      var originalRoot = RunCodeLog.Root;
      var run = NewRun(CurrentId);
      var output = Path.Combine(run, RunCodeLog.OutputFile);
      File.WriteAllText(output, "keep");
      File.SetLastWriteTimeUtc(output, DateTime.UtcNow.AddDays(-8));
      var actualRoot = Path.Combine(scope.Folder, "actual-run-code");
      Directory.Move(originalRoot, actualRoot);
      if (!TryDirectoryLink(originalRoot, actualRoot, out var why))
      {
        Directory.Move(actualRoot, originalRoot);
        Console.WriteLine($"SKIPPED: linked root output pruning check: {why}");
        return;
      }
      scope.TrackLink(originalRoot);
      RunCodeLog.PruneOldOutputs(TimeSpan.FromDays(7));
      expect(File.Exists(output), "seven-day output pruning refuses a linked root");
    });
  }

  private static void CheckLockedCandidate(Action<bool, string> expect)
  {
    if (!OperatingSystem.IsWindows())
    {
      Console.WriteLine("SKIPPED: locked file deletion semantics are Windows-specific");
      return;
    }
    InTempRoot(scope =>
    {
      var current = NewRun(CurrentId);
      var locked = NewRun("20200101-000000-abcdef01");
      var file = Path.Combine(locked, "locked.txt");
      File.WriteAllText(file, "keep");
      var ordinary = NewRun("20200102-000000-abcdef02");
      using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
      {
        RunCodeLog.PruneOldRunFolders(current, 1, Now);
        expect(Directory.Exists(locked) && File.Exists(file) && !Directory.Exists(ordinary),
          "a locked file preserves its candidate and later candidates are still removed");
      }
    });
  }

  private static void CheckOutputRetentionIndependence(Action<bool, string> expect)
  {
    InTempRoot(scope =>
    {
      var current = NewRun(CurrentId);
      var old = NewRun("20200101-000000-abcdef01");
      var output = Path.Combine(old, RunCodeLog.OutputFile);
      var script = Path.Combine(old, RunCodeLog.ScriptFile);
      var summary = Path.Combine(old, RunCodeLog.SummaryFile);
      File.WriteAllText(output, "old output");
      File.WriteAllText(script, "audit");
      File.WriteAllText(summary, "{}");
      foreach (var path in new[] { output, script, summary })
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
      SetRetention("0");
      RunCodeLog.PruneOldRunFolders(current);
      RunCodeLog.PruneOldOutputs(TimeSpan.FromDays(7));
      expect(Directory.Exists(old) && !File.Exists(output) && File.Exists(script) && File.Exists(summary),
        "disabled folder pruning still permits seven-day output pruning and keeps audit files");
    });
  }

  private static string NewRun(string id)
  {
    var path = Path.Combine(RunCodeLog.Root, id);
    Directory.CreateDirectory(path);
    return path;
  }

  private static void SetRetention(string? value) =>
    Environment.SetEnvironmentVariable(RunCodeLog.FolderRetentionEnvironmentVariable, value);

  private static void InTempRoot(Action<TempScope> check)
  {
    using var scope = new TempScope();
    check(scope);
  }

  private sealed class TempScope : IDisposable
  {
    private readonly string _previousLog = PluginLog.LogFilePath;
    private readonly string? _previousRetention = Environment.GetEnvironmentVariable(RunCodeLog.FolderRetentionEnvironmentVariable);
    private readonly List<string> _directoryLinks = new();
    private readonly List<string> _fileLinks = new();
    internal readonly string Folder = Path.Combine(Path.GetTempPath(), "RunCodeRetention-" + Guid.NewGuid().ToString("N"));

    internal TempScope()
    {
      Directory.CreateDirectory(Folder);
      PluginLog.LogFilePath = Path.Combine(Folder, "plugin.log");
    }

    internal void TrackLink(string path) => _directoryLinks.Add(path);
    internal void TrackFileLink(string path) => _fileLinks.Add(path);

    public void Dispose()
    {
      PluginLog.LogFilePath = _previousLog;
      SetRetention(_previousRetention);
      foreach (var link in _fileLinks.AsEnumerable().Reverse())
        if (File.Exists(link)) File.Delete(link);
      foreach (var link in _directoryLinks.AsEnumerable().Reverse())
        if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
      var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
      var full = Path.GetFullPath(Folder);
      if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(full).StartsWith("RunCodeRetention-", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing cleanup outside the harness temp folder.");
      if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
  }

  private static bool TryDirectoryLink(string link, string target, out string reason)
  {
    try
    {
      Directory.CreateSymbolicLink(link, target);
      reason = string.Empty;
      return true;
    }
    catch (Exception first)
    {
      if (!OperatingSystem.IsWindows())
      {
        reason = first.Message;
        return false;
      }
      try
      {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
          ArgumentList = { "/c", "mklink", "/J", link, target },
          UseShellExecute = false,
          CreateNoWindow = true,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
        })!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("mklink /J failed.");
        reason = string.Empty;
        return true;
      }
      catch (Exception second)
      {
        reason = $"symlink: {first.Message}; junction: {second.Message}";
        return false;
      }
    }
  }
}
