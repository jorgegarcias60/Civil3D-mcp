using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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

  // Kept when old run outputs are pruned.
  private static readonly string[] KeptFiles = { ScriptFile, SummaryFile };
  // Written by the plugin; a script's WriteFile may not use these names.
  private static readonly string[] ReservedFiles = { ScriptFile, SummaryFile, ResultFile, OutputFile };
  private static readonly UTF8Encoding Utf8NoBom = new(false);
  private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

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

  internal static string WriteBytes(string runFolder, string fileName, byte[] bytes)
  {
    var path = FilePath(runFolder, fileName);
    File.WriteAllBytes(path, bytes);
    return path;
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
  internal static CopyResult CopyOutputs(string runFolder, string destination, bool removeCopied)
  {
    var result = new CopyResult(new(), new(), new());
    try
    {
      var targetFolder = ValidateCopyDestination(destination);
      var sources = Directory.EnumerateFiles(Inside(runFolder))
        .Where(file => !KeptFiles.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
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

  // Results and files written by scripts older than the retention are
  // deleted; every script.cs and run.json is kept.
  internal static void PruneOldOutputs(TimeSpan retention)
  {
    try
    {
      if (!Directory.Exists(Root))
      {
        return;
      }

      var cutoff = DateTime.UtcNow - retention;
      foreach (var directory in Directory.EnumerateDirectories(Root))
      {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
          if (!KeptFiles.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(file) < cutoff)
          {
            File.Delete(Inside(file));
          }
        }
      }
    }
    catch (Exception exception)
    {
      PluginLog.Swallow("RunCode", "pruning old run outputs", exception);
    }
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
