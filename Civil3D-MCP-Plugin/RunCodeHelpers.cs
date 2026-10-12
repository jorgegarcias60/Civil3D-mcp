using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Civil3DMcpPlugin;

/// <summary>Small, AutoCAD-free helpers shared with the offline harness.</summary>
internal static class RunCodeHelpers
{
  internal static (int Index, string? Error) MatchDocument(string key, IReadOnlyList<string> openNames)
  {
    key = key.Trim().Replace('/', Path.DirectorySeparatorChar);
    var rooted = Path.IsPathRooted(key);
    string normalized;
    try { normalized = rooted ? Path.GetFullPath(key) : key; }
    catch (ArgumentException) { return (-1, $"Invalid document path '{key}'. Open drawings: {string.Join(", ", openNames)}."); }
    var matches = new List<int>();
    for (var index = 0; index < openNames.Count; index++)
    {
      var name = openNames[index].Replace('/', Path.DirectorySeparatorChar);
      // An unsaved drawing's name (Drawing1.dwg) is not a path: a full-path
      // request must not resolve it against the process folder.
      if (rooted && !Path.IsPathFullyQualified(name))
      {
        continue;
      }

      // Document.Name is intentional: Database.Filename can be the template
      // path for an unsaved drawing such as Drawing1.dwg.
      var candidate = rooted ? Path.GetFullPath(name) : Path.GetFileName(name);
      if (string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase)
          || (!rooted && !normalized.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate, normalized + ".dwg", StringComparison.OrdinalIgnoreCase)))
        matches.Add(index);
    }
    return matches.Count switch
    {
      1 => (matches[0], null),
      0 => (-1, $"Document '{key}' is not open. Open drawings: {string.Join(", ", openNames)}."),
      _ => (-1, $"Document '{key}' is ambiguous. Matching open drawings: {string.Join(", ", matches.Select(index => openNames[index]))}."),
    };
  }

  internal static string Sha256Hex(string text) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

  internal static long ParseHandle(string hex)
  {
    if (hex == null) throw new ArgumentNullException(nameof(hex));
    var value = hex.Trim();
    if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
    if (!long.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var handle) || handle <= 0)
      throw new ArgumentException("A handle must be a positive hexadecimal number.", nameof(hex));
    return handle;
  }

  internal static bool IsDrawingContentError(string? status) => status is
    "DwgNeedsRecovery" or "DwgNotRecoverable" or "InvalidDwgVersion" or "DwgObjectImproperlyRead";
}
