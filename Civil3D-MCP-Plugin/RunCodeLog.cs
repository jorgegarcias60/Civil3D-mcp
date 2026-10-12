using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Civil3DMcpPlugin;

/// <summary>
/// Every file runCode writes, all inside its own folder next to plugin.log
/// (run-code\&lt;run id&gt;\): the script and its summary, large output and
/// results, and the files scripts write with WriteFile. Like PluginLog it
/// stays outside FileBoundary's import/export roots, because these are the
/// plugin's own logs; every path is checked to be inside run-code\.
/// </summary>
internal static class RunCodeLog
{
  internal const string ScriptFile = "script.cs";
  internal const string SummaryFile = "run.json";
  internal const string ResultFile = "result.json";
  internal const string OutputFile = "output.txt";
  internal const int DefaultFolderRetentionDays = 180;
  internal const string FolderRetentionEnvironmentVariable = "CIVIL3D_RUN_CODE_RETENTION_DAYS";

  // Kept when old run outputs are pruned.
  private static readonly string[] KeptFiles = { ScriptFile, SummaryFile };
  // Written by the plugin; a script's WriteFile may not use these names.
  private static readonly string[] ReservedFiles = { ScriptFile, SummaryFile, ResultFile, OutputFile };
  private static readonly UTF8Encoding Utf8NoBom = new(false);
  private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
  private static readonly Regex RunIdPattern = new(@"\A[0-9]{8}-[0-9]{6}-[0-9a-fA-F]{8}\z", RegexOptions.CultureInvariant);
  private static int _warnedInvalidRetention;

  internal static string Root => Path.Combine(Path.GetDirectoryName(PluginLog.LogFilePath)!, "run-code");

  internal static string CreateRunFolder(string runId)
  {
    var folder = Inside(Path.Combine(Root, runId));
    Directory.CreateDirectory(folder);
    return folder;
  }

  internal static string WriteText(string runFolder, string fileName, string text)
  {
    var path = FilePath(runFolder, fileName);
    File.WriteAllText(path, text, Utf8NoBom);
    return path;
  }

  // CreateNew refuses an existing result (including a filesystem link). The
  // caller publishes the path only after its stream has been fully closed.
  internal static string ResultPath(string runFolder) => FilePath(runFolder, ResultFile);

  internal static FileStream OpenResultStream(string runFolder) =>
    new(ResultPath(runFolder), FileMode.CreateNew, FileAccess.Write, FileShare.None);

  internal static FileStream OpenResultReadStream(string path) =>
    new(ConfinedResultPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);

  internal static void DeletePartialResult(string path) => File.Delete(ConfinedResultPath(path));

  private static string ConfinedResultPath(string path)
  {
    var full = Inside(path);
    if (!Path.GetFileName(full).Equals(ResultFile, StringComparison.OrdinalIgnoreCase))
      throw new ArgumentException("Expected the run-code result file.");
    return full;
  }

  // A file a script writes with WriteFile(name, ...).
  internal static string WriteScriptFile(string runFolder, string fileName, string? text, IEnumerable<string>? lines)
  {
    if (ReservedFiles.Contains(Path.GetFileName(fileName), StringComparer.OrdinalIgnoreCase))
    {
      throw new ArgumentException($"WriteFile cannot use '{fileName}': the plugin writes that file.");
    }

    var path = FilePath(runFolder, fileName);
    if (lines != null)
    {
      File.WriteAllLines(path, lines, Utf8NoBom);
    }
    else
    {
      File.WriteAllText(path, text ?? string.Empty, Utf8NoBom);
    }

    return path;
  }

  internal static void WriteSummary(string runFolder, JsonObject summary)
  {
    try
    {
      WriteText(runFolder, SummaryFile, summary.ToJsonString(Indented));
    }
    catch (Exception exception)
    {
      PluginLog.Warn("RunCode", $"Could not write the run summary in {runFolder}.", exception);
    }
  }

  // Files the script wrote with WriteFile (not the plugin's own).
  internal static IEnumerable<string> ScriptFiles(string runFolder)
  {
    return Directory.EnumerateFiles(runFolder)
      .Where(file => !ReservedFiles.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase));
  }

  internal sealed record CopyResult(List<string> Copied, List<string> Removed, List<string> Warnings);

  // Output destinations are caller-selected. Only sources are confined to Root.
  internal static CopyResult CopyOutputs(string runFolder, string destination, bool removeCopied, bool includeResult = true)
  {
    var result = new CopyResult(new(), new(), new());
    try
    {
      var targetFolder = ValidateCopyDestination(destination);
      var sources = Directory.EnumerateFiles(Inside(runFolder))
        .Where(file => !KeptFiles.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
        .Where(file => includeResult || !Path.GetFileName(file).Equals(ResultFile, StringComparison.OrdinalIgnoreCase))
        .Select(Inside).ToArray();
      if (sources.Length == 0) return result;

      Directory.CreateDirectory(targetFolder);
      foreach (var source in sources)
      {
        var target = Path.Combine(targetFolder, Path.GetFileName(source));
        var temporary = Path.Combine(targetFolder, ".run-code-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
          File.Copy(source, temporary);
          File.Move(temporary, target, overwrite: true);
          result.Copied.Add(target);
          if (removeCopied)
          {
            var sourcePath = ResolveFullPath(source);
            var targetPath = ResolveFullPath(target);
            if (sourcePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
              throw new InvalidOperationException("The copied output resolves to its source; the source was kept.");
            // Re-check the resolved destination before removing the only source.
            ValidateCopyDestination(Path.GetDirectoryName(targetPath)!);
            File.Delete(source);
            result.Removed.Add(source);
          }
        }
        catch (Exception exception)
        {
          result.Warnings.Add($"Could not copy or remove output '{source}' to '{target}': {exception.Message}");
        }
        finally
        {
          try
          {
            if (File.Exists(temporary)) File.Delete(temporary);
          }
          catch (Exception exception)
          {
            result.Warnings.Add($"Could not remove temporary output '{temporary}': {exception.Message}");
          }
        }
      }
    }
    catch (Exception exception)
    {
      result.Warnings.Add($"Could not copy outputs to '{destination}': {exception.Message}");
    }

    return result;
  }

  internal static string ValidateCopyDestination(string destination)
  {
    if (destination.StartsWith(@"\\?\", StringComparison.Ordinal)
      || destination.StartsWith(@"\\.\", StringComparison.Ordinal))
      throw new ArgumentException("'copyOutputsTo' must not use a device path.");
    if (!Path.IsPathFullyQualified(destination))
      throw new ArgumentException("'copyOutputsTo' must be an absolute folder.");
    var full = Path.GetFullPath(destination);
    var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var target = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (target.Equals(root, StringComparison.OrdinalIgnoreCase)
      || target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
      throw new ArgumentException("'copyOutputsTo' must not be inside the run-code log root.");
    for (var directory = new DirectoryInfo(full); directory != null; directory = directory.Parent)
    {
      if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new ArgumentException("'copyOutputsTo' must not traverse filesystem links or junctions.");
    }
    return full;
  }

  private static string ResolveFullPath(string path)
  {
    var full = Path.GetFullPath(new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path);
    for (var directory = new FileInfo(full).Directory; directory != null; directory = directory.Parent)
    {
      if ((directory.Attributes & FileAttributes.ReparsePoint) == 0) continue;
      var resolved = directory.ResolveLinkTarget(true);
      if (resolved != null)
        return Path.GetFullPath(Path.Combine(resolved.FullName, Path.GetRelativePath(directory.FullName, full)));
    }
    return full;
  }

  // Output pruning keeps audit files. Whole-folder pruning expires them separately.
  internal static void PruneOldOutputs(TimeSpan retention)
  {
    try
    {
      var root = PlainPrunePath(Root, allowRoot: true);
      var cutoff = DateTime.UtcNow - retention;
      foreach (var directory in Directory.EnumerateDirectories(root))
      {
        try
        {
          foreach (var file in Directory.EnumerateFiles(PlainPrunePath(directory)))
          {
            try
            {
              var path = PlainPrunePath(file);
              if (!KeptFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(path) < cutoff)
                File.Delete(PlainPrunePath(path));
            }
            catch (Exception exception)
            {
              PluginLog.Swallow("RunCode", "pruning an old run output", exception);
            }
          }
        }
        catch (Exception exception)
        {
          PluginLog.Swallow("RunCode", "pruning outputs in a run folder", exception);
        }
      }
    }
    catch (Exception exception)
    {
      PluginLog.Swallow("RunCode", "pruning old run outputs", exception);
    }
  }

  internal static int ReadFolderRetentionDays()
  {
    var value = Environment.GetEnvironmentVariable(FolderRetentionEnvironmentVariable);
    if (value == null) return DefaultFolderRetentionDays;
    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)) return days;
    if (Interlocked.Exchange(ref _warnedInvalidRetention, 1) == 0)
      PluginLog.Warn("RunCode", $"Invalid {FolderRetentionEnvironmentVariable}; using {DefaultFolderRetentionDays} days for run-folder retention.");
    return DefaultFolderRetentionDays;
  }

  internal static void PruneOldRunFolders(string currentRunFolder) =>
    PruneOldRunFolders(currentRunFolder, ReadFolderRetentionDays(), DateTime.Now);

  // Run IDs use DateTime.Now, so age uses local time too. File timestamps do
  // not extend a run's lifetime. A nonpositive retention keeps whole folders.
  internal static void PruneOldRunFolders(string currentRunFolder, int retentionDays, DateTime now)
  {
    if (retentionDays <= 0) return;
    try
    {
      if (!Path.IsPathFullyQualified(currentRunFolder)) return;
      var root = PlainPrunePath(Root, allowRoot: true);
      var current = PlainPrunePath(currentRunFolder);
      if (!string.Equals(Path.GetDirectoryName(current), root, StringComparison.OrdinalIgnoreCase)
        || !TryRunDate(Path.GetFileName(current), out _)
        || (File.GetAttributes(current) & FileAttributes.Directory) == 0) return;
      foreach (var directory in Directory.EnumerateDirectories(root))
      {
        try
        {
          var folder = Inside(directory);
          if (folder.Equals(current, StringComparison.OrdinalIgnoreCase)) continue;
          var name = Path.GetFileName(folder);
          if (!TryRunDate(name, out var runDate)
            || (now - runDate).TotalDays <= retentionDays) continue;
          DeleteRunTree(folder);
        }
        catch (Exception exception)
        {
          // A locked file, link or inaccessible folder must not fail the run
          // or prevent pruning the other candidates.
          PluginLog.Swallow("RunCode", "pruning an old run folder", exception);
        }
      }
    }
    catch (Exception exception)
    {
      PluginLog.Swallow("RunCode", "pruning old run folders", exception);
    }
  }

  private static bool TryRunDate(string name, out DateTime runDate)
  {
    runDate = default;
    return RunIdPattern.IsMatch(name)
      && DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out runDate);
  }

  private static void DeleteRunTree(string folder)
  {
    var directories = new List<string> { PlainPrunePath(folder) };
    var files = new List<string>();
    // Inspect the whole tree before removing anything. Enumerate one level
    // at a time and reject links instead of resolving or traversing them.
    for (var index = 0; index < directories.Count; index++)
    {
      foreach (var entry in Directory.EnumerateFileSystemEntries(PlainPrunePath(directories[index])))
      {
        var path = PlainPrunePath(entry);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) directories.Add(path);
        else files.Add(path);
      }
    }
    foreach (var file in files) File.Delete(PlainPrunePath(file));
    // Recheck each path and delete directories nonrecursively.
    for (var index = directories.Count - 1; index >= 0; index--)
      Directory.Delete(PlainPrunePath(directories[index]), recursive: false);
  }

  private static string PlainPrunePath(string path, bool allowRoot = false)
  {
    var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (!(allowRoot && full.Equals(root, StringComparison.OrdinalIgnoreCase))) full = Inside(full);
    // Check Root before its children. Links above Root (such as redirected
    // AppData) do not disable pruning. File.GetAttributes inspects the link
    // itself; never call ResolveLinkTarget during pruning.
    var ancestors = new Stack<string>();
    for (var entry = full; entry != null; entry = Path.GetDirectoryName(entry))
    {
      ancestors.Push(entry);
      if (entry.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
    }
    foreach (var entry in ancestors)
    {
      if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
        throw new IOException("Run-code pruning must not traverse filesystem links or junctions.");
    }
    return full;
  }

  private static string FilePath(string runFolder, string fileName)
  {
    var name = Path.GetFileName(fileName);
    if (string.IsNullOrWhiteSpace(name) || name != fileName)
    {
      throw new ArgumentException($"Expected a plain file name (no folders), got '{fileName}'.");
    }

    return Inside(Path.Combine(runFolder, name));
  }

  private static string Inside(string path)
  {
    var full = Path.GetFullPath(path);
    var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidOperationException($"runCode writes only inside {root}; refused {full}.");
    }

    return full;
  }
}
