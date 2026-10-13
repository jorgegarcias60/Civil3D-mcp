using System.Text;
using Civil3DMcpPlugin;

internal static class PdfChecks
{
  internal static void Run(Action<bool, string> expect)
  {
    const string sample = """
      %PDF-1.7
      1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj
      2 0 obj << /Type /Pages /Kids [4 0 R 3 0 R] /Count 2 /MediaBox [0 0 612 792] >> endobj
      3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 792 612] /Rotate 90 >> endobj
      4 0 obj << /Type /Page /Parent 2 0 R /UserUnit 2 >> endobj
      %%EOF
      """;
    using var stream = new MemoryStream(Encoding.Latin1.GetBytes(sample));
    var parsed = PdfOutputInspector.Inspect(stream);
    expect(!parsed.UnverifiedObjectStreams && parsed.Pages.Count == 2,
      "PDF page tree order and exact count are resolved");
    expect(Math.Abs(parsed.Pages[0].WidthMm - 431.8) < 0.01
      && Math.Abs(parsed.Pages[0].HeightMm - 558.8) < 0.01
      && parsed.Pages[0].UserUnit == 2,
      "inherited MediaBox and UserUnit determine physical size");
    expect(Math.Abs(parsed.Pages[1].WidthMm - 279.4) < 0.01
      && Math.Abs(parsed.Pages[1].HeightMm - 215.9) < 0.01
      && parsed.Pages[1].Rotation == 90,
      "mixed sheet sizes and rotation are retained");
    expect(PdfOutputInspector.Validate(parsed, [(431.8 + 0.15, 558.8), (215.9, 279.4)]),
      "page verification accepts one-point rounding and rotated media");
    try
    {
      PdfOutputInspector.Validate(parsed, [(431.8, 558.8)]);
      expect(false, "wrong PDF page count is rejected");
    }
    catch (InvalidDataException) { expect(true, "wrong PDF page count is rejected"); }
    try
    {
      PdfOutputInspector.Validate(parsed, [(100, 100), (215.9, 279.4)]);
      expect(false, "wrong PDF paper size is rejected");
    }
    catch (InvalidDataException) { expect(true, "wrong PDF paper size is rejected"); }
    try
    {
      PdfOutputInspector.Validate(parsed, [(431.8 + 1, 558.8), (215.9, 279.4)]);
      expect(false, "paper error greater than one point is rejected");
    }
    catch (InvalidDataException) { expect(true, "paper error greater than one point is rejected"); }

    PdfOutputInspector.Inspection InspectWithObject(string body)
    {
      using var pdf = new MemoryStream(Encoding.Latin1.GetBytes(sample.Replace("%%EOF", $"5 0 obj {body} endobj\n%%EOF")));
      return PdfOutputInspector.Inspect(pdf);
    }

    var hyperlink = InspectWithObject("<< /Type /Annot /A << /S /URI /URI (https://example.org/help/ObjStm) >> >>");
    expect(!hyperlink.UnverifiedObjectStreams && hyperlink.Pages.Count == 2,
      "ObjStm in a hyperlink still permits page inspection");
    try
    {
      PdfOutputInspector.Validate(hyperlink, [(431.8, 558.8)]);
      expect(false, "a hyperlink containing ObjStm cannot hide a wrong page count");
    }
    catch (InvalidDataException) { expect(true, "a hyperlink containing ObjStm cannot hide a wrong page count"); }

    foreach (var (body, label) in new[]
    {
      ("<< /Note (nested (/Type /ObjStm) and escaped \\) text) >>", "literal string"),
      ("<< /Note (endobj 99 0 obj << /Type /ObjStm >> endobj) >>", "literal string containing object syntax"),
      ("<< /Type /Annot % /Type /ObjStm\n /Subtype /Link >>", "comment"),
      ("<< /Length 14 >>\nstream\n/Type /ObjStm\nendstream", "stream payload"),
      ("<< /Length 48 >>\nstream\nendobj 99 0 obj << /Type /ObjStm >> endobj\nendstream", "stream payload containing object syntax")
    })
    {
      var ordinary = InspectWithObject(body);
      expect(!ordinary.UnverifiedObjectStreams && ordinary.Pages.Count == 2
        && PdfOutputInspector.Validate(ordinary, [(431.8, 558.8), (215.9, 279.4)]),
        $"/Type /ObjStm in a {label} does not disable page validation");
    }

    var compressed = InspectWithObject("<< /Type /ObjStm /N 1 /First 0 /Length 0 >>\nstream\nendstream");
    expect(compressed.UnverifiedObjectStreams && !PdfOutputInspector.Validate(compressed, []),
      "a genuine object stream produces an explicit unverified result");
    try
    {
      using var junk = new MemoryStream(Encoding.Latin1.GetBytes("fresh nonempty junk"));
      PdfOutputInspector.Inspect(junk);
      expect(false, "non-PDF output is rejected");
    }
    catch (InvalidDataException) { expect(true, "non-PDF output is rejected"); }
  }
}
