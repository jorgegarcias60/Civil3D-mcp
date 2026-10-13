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

  internal static double XrefScale(double? scale, string sourceUnits, string hostUnits, Func<double> conversion)
  {
    if (scale.HasValue)
    {
      if (!double.IsFinite(scale.Value) || scale.Value <= 0) throw new ArgumentOutOfRangeException(nameof(scale), "Xref scale must be finite and positive.");
      return scale.Value;
    }
    if (sourceUnits is "Undefined" or "Unitless" || hostUnits is "Undefined" or "Unitless")
      throw new ArgumentException($"Cannot infer xref scale from source units '{sourceUnits}' and host units '{hostUnits}'; provide an explicit scale.");
    if ((sourceUnits is "Feet" or "USSurveyFeet") && (hostUnits is "Feet" or "USSurveyFeet")) return 1;
    var result = conversion();
    if (!double.IsFinite(result) || result <= 0)
      throw new ArgumentException($"Invalid conversion from '{sourceUnits}' to '{hostUnits}'; provide an explicit scale.");
    return result;
  }

  internal static string XrefPathName(string source, string host, bool relative)
  {
    var full = Path.GetFullPath(source);
    if (!relative || !Path.IsPathFullyQualified(host)) return full;
    var hostDirectory = Path.GetDirectoryName(Path.GetFullPath(host));
    if (hostDirectory == null || !string.Equals(Path.GetPathRoot(full), Path.GetPathRoot(hostDirectory), StringComparison.OrdinalIgnoreCase)) return full;
    var path = Path.GetRelativePath(hostDirectory, full);
    return path.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
      ? path : "." + Path.DirectorySeparatorChar + path;
  }

  internal static (double X, double Y)[] ClipPolygon(IReadOnlyList<(double X, double Y)> points, Action? check = null)
  {
    if (points == null) throw new ArgumentNullException(nameof(points));
    var vertices = points.ToList();
    if (vertices.Count > 1 && vertices[0] == vertices[^1]) vertices.RemoveAt(vertices.Count - 1);
    if (vertices.Count < 3 || vertices.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)) || vertices.Distinct().Count() < 3)
      throw new ArgumentException("XClip requires at least three distinct, finite points.", nameof(points));
    double cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
      (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    var area = 0.0;
    var origin = vertices[0];
    for (var i = 0; i < vertices.Count; i++)
    {
      check?.Invoke();
      var a = vertices[i]; var b = vertices[(i + 1) % vertices.Count];
      if (a == b) throw new ArgumentException("XClip has a zero-length edge.", nameof(points));
      // Translate before multiplying: absolute-coordinate products can cancel
      // the area of a small clip boundary far from the drawing origin.
      area += (a.X - origin.X) * (b.Y - origin.Y) - (b.X - origin.X) * (a.Y - origin.Y);
      for (var j = i + 1; j < vertices.Count; j++)
      {
        check?.Invoke();
        if (j == i + 1 || i == 0 && j == vertices.Count - 1) continue;
        var c = vertices[j]; var d = vertices[(j + 1) % vertices.Count];
        var c1 = cross(a, b, c); var c2 = cross(a, b, d);
        var c3 = cross(c, d, a); var c4 = cross(c, d, b);
        bool between(double x, double a, double b) => x >= Math.Min(a, b) && x <= Math.Max(a, b);
        bool on((double X, double Y) x, (double X, double Y) y, (double X, double Y) z) =>
          cross(x, y, z) == 0 && between(z.X, x.X, y.X) && between(z.Y, x.Y, y.Y);
        if ((c1 * c2 < 0 && c3 * c4 < 0) || on(a, b, c) || on(a, b, d) || on(c, d, a) || on(c, d, b))
          throw new ArgumentException("XClip polygon self-intersects or has touching edges.", nameof(points));
      }
    }
    if (!double.IsFinite(area) || Math.Abs(area) < 1e-12) throw new ArgumentException("XClip polygon has zero area.", nameof(points));
    if (area < 0) vertices.Reverse();
    return vertices.ToArray();
  }
}
