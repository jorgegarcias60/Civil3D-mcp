using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Civil3DMcpPlugin;

// Pure managed PDF checks for the ordinary uncompressed page trees written by
// AutoCAD's PDF PC3s. Compressed object streams remain explicitly unverified.
internal static class PdfOutputInspector
{
  internal sealed record Page(double WidthMm, double HeightMm, double[] MediaBoxPt, int Rotation, double UserUnit);
  internal sealed record Inspection(bool UnverifiedObjectStreams, IReadOnlyList<Page> Pages);

  internal static bool Validate(Inspection inspection, IReadOnlyList<(double WidthMm, double HeightMm)> expected)
  {
    if (inspection.UnverifiedObjectStreams) return false;
    if (inspection.Pages.Count != expected.Count)
      throw new InvalidDataException($"PDF has {inspection.Pages.Count} pages; expected {expected.Count}.");
    const double toleranceMm = 25.4 / 72;
    for (var index = 0; index < expected.Count; index++)
    {
      var page = inspection.Pages[index];
      var size = expected[index];
      var direct = Math.Abs(page.WidthMm - size.WidthMm) <= toleranceMm
        && Math.Abs(page.HeightMm - size.HeightMm) <= toleranceMm;
      var rotated = Math.Abs(page.WidthMm - size.HeightMm) <= toleranceMm
        && Math.Abs(page.HeightMm - size.WidthMm) <= toleranceMm;
      if (!direct && !rotated)
        throw new InvalidDataException($"PDF page {index + 1} is {page.WidthMm:F2} x {page.HeightMm:F2} mm; expected {size.WidthMm:F2} x {size.HeightMm:F2} mm.");
    }
    return true;
  }

  private static readonly Regex Objects = new(@"(?s)(\d+)\s+\d+\s+obj\b(.*?)endobj", RegexOptions.Compiled);
  private static readonly Regex Reference = new(@"(\d+)\s+\d+\s+R", RegexOptions.Compiled);
  private static readonly Regex Box = new(@"/MediaBox\s*\[\s*([-+\d.]+)\s+([-+\d.]+)\s+([-+\d.]+)\s+([-+\d.]+)\s*\]", RegexOptions.Compiled);

  internal static Inspection Inspect(Stream stream)
  {
    stream.Position = 0;
    var bytes = new byte[stream.Length];
    stream.ReadExactly(bytes);
    var content = Encoding.Latin1.GetString(bytes);
    if (!content.StartsWith("%PDF-", StringComparison.Ordinal) || !content.Contains("%%EOF", StringComparison.Ordinal))
      throw new InvalidDataException("PDF header or end marker is missing.");
    if (IsObjectStream(content))
      return new Inspection(true, []);

    var objects = Objects.Matches(content).Cast<Match>()
      .GroupBy(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
      .ToDictionary(group => group.Key, group => group.Last().Groups[2].Value);
    var catalog = objects.Values.FirstOrDefault(body => Regex.IsMatch(body, @"/Type\s*/Catalog\b"))
      ?? throw new InvalidDataException("PDF catalog is missing.");
    var root = GetReference(catalog, "Pages")
      ?? throw new InvalidDataException("PDF page tree is missing.");
    var pages = new List<Page>();
    var visited = new HashSet<int>();
    Visit(root, null, 0, objects, visited, pages);
    if (pages.Count == 0) throw new InvalidDataException("PDF has no pages.");
    return new Inspection(false, pages);
  }

  // Only a /Type name whose value is /ObjStm in a dictionary signals compressed
  // objects. Link targets, comments, literal/hex strings and stream bytes can
  // contain the same text without changing how the page tree is inspected.
  private static bool IsObjectStream(string content)
  {
    var containers = new Stack<char>();
    var awaitingType = false;
    var inObject = false;
    for (var index = 0; index < content.Length;)
    {
      var current = content[index];
      if (char.IsWhiteSpace(current)) { index++; continue; }
      if (current == '%')
      {
        while (index < content.Length && content[index] != '\r' && content[index] != '\n') index++;
        continue;
      }
      if (current == '(')
      {
        var depth = 1;
        index++;
        while (index < content.Length && depth > 0)
        {
          if (content[index] == '\\') { index = Math.Min(index + 2, content.Length); continue; }
          if (content[index] == '(') depth++;
          else if (content[index] == ')') depth--;
          index++;
        }
        awaitingType = false;
        continue;
      }
      if (current == '<')
      {
        if (index + 1 < content.Length && content[index + 1] == '<')
        {
          if (inObject) containers.Push('<');
          index += 2;
        }
        else
        {
          var end = content.IndexOf('>', index + 1);
          index = end >= 0 ? end + 1 : content.Length;
        }
        awaitingType = false;
        continue;
      }
      if (current == '>' && index + 1 < content.Length && content[index + 1] == '>')
      {
        if (containers.Count > 0) containers.Pop();
        index += 2;
        awaitingType = false;
        continue;
      }
      if (current == '[') { if (inObject) containers.Push('['); index++; awaitingType = false; continue; }
      if (current == ']')
      {
        if (containers.Count > 0) containers.Pop();
        index++;
        awaitingType = false;
        continue;
      }
      if (current == '/')
      {
        var start = index++;
        while (index < content.Length && !IsDelimiter(content[index])) index++;
        var name = content.AsSpan(start, index - start);
        if (inObject && awaitingType && name.SequenceEqual("/ObjStm")) return true;
        awaitingType = inObject && containers.Count > 0 && containers.Peek() == '<' && name.SequenceEqual("/Type");
        continue;
      }
      var tokenStart = index++;
      while (index < content.Length && !IsDelimiter(content[index])) index++;
      var token = content.AsSpan(tokenStart, index - tokenStart);
      if (token.SequenceEqual("obj")) { inObject = true; containers.Clear(); }
      else if (token.SequenceEqual("endobj")) { inObject = false; containers.Clear(); }
      else if (inObject && token.SequenceEqual("stream"))
      {
        var end = content.IndexOf("endstream", index, StringComparison.Ordinal);
        index = end >= 0 ? end + "endstream".Length : content.Length;
      }
      awaitingType = false;
    }
    return false;
  }

  private static bool IsDelimiter(char value) => char.IsWhiteSpace(value) || value is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

  private static void Visit(int id, double[]? inheritedBox, int inheritedRotation,
    IReadOnlyDictionary<int, string> objects, HashSet<int> visited, List<Page> pages)
  {
    if (!visited.Add(id) || !objects.TryGetValue(id, out var body))
      throw new InvalidDataException("PDF page tree has a missing or repeated node.");
    var box = ReadBox(body) ?? inheritedBox;
    var rotation = ReadNumber(body, "Rotate") is double degrees ? (int)degrees : inheritedRotation;
    if (Regex.IsMatch(body, @"/Type\s*/Page\b"))
    {
      if (box == null) throw new InvalidDataException("PDF page has no MediaBox, including inherited boxes.");
      var userUnit = ReadNumber(body, "UserUnit") ?? 1;
      if (userUnit <= 0) throw new InvalidDataException("PDF page has an invalid UserUnit.");
      var width = Math.Abs(box[2] - box[0]) * userUnit * 25.4 / 72;
      var height = Math.Abs(box[3] - box[1]) * userUnit * 25.4 / 72;
      if (width <= 0 || height <= 0) throw new InvalidDataException("PDF page has an invalid MediaBox.");
      pages.Add(new Page(width, height, box, rotation, userUnit));
      return;
    }
    if (!Regex.IsMatch(body, @"/Type\s*/Pages\b"))
      throw new InvalidDataException("PDF page tree contains a non-page node.");
    var kids = Regex.Match(body, @"/Kids\s*\[(.*?)\]", RegexOptions.Singleline);
    if (!kids.Success) throw new InvalidDataException("PDF Pages node has no Kids array.");
    foreach (Match child in Reference.Matches(kids.Groups[1].Value))
      Visit(int.Parse(child.Groups[1].Value, CultureInfo.InvariantCulture), box, rotation, objects, visited, pages);
  }

  private static int? GetReference(string body, string name)
  {
    var match = Regex.Match(body, @"/" + name + @"\s+(\d+)\s+\d+\s+R");
    return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
  }

  private static double[]? ReadBox(string body)
  {
    var match = Box.Match(body);
    return match.Success
      ? Enumerable.Range(1, 4).Select(index => double.Parse(match.Groups[index].Value, CultureInfo.InvariantCulture)).ToArray()
      : null;
  }

  private static double? ReadNumber(string body, string name)
  {
    var match = Regex.Match(body, @"/" + name + @"\s+([-+\d.]+)");
    return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
  }
}
