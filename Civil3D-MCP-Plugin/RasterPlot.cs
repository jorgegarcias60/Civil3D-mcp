using System.Globalization;
using System.Text.RegularExpressions;

namespace Civil3DMcpPlugin;

// AutoCAD-free helpers for civil3d_plot render_layouts_to_png: the choice of a
// pixel medium on the raster PC3, and a check of the PNG that comes back.
//
// "PublishToWeb PNG.pc3" has no paper sizes, only canonical pixel media such as
// "Sun_Hi-Res_(1600.00_x_1280.00_Pixels)". A medium of any other size needs a
// custom paper size written into the PC3's PMP file, which this server never
// edits, so the render picks the best of the media the device already offers
// (or the one the caller names) and fits the sheet to it.
internal static class RasterPlot
{
  internal const string DefaultDevice = "PublishToWeb PNG.pc3";
  internal const int DefaultPixelWidth = 3300;
  internal const int MinPixelWidth = 320;
  internal const int MaxPixelWidth = 16000;

  // "(1600.00_x_1280.00_Pixels)" in canonical names, "(1600.00 x 1280.00 Pixels)" in locale names.
  private static readonly Regex PixelSize = new(
    @"\(\s*(\d+(?:\.\d+)?)[\s_]*x[\s_]*(\d+(?:\.\d+)?)[\s_]*Pixels\s*\)",
    RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

  internal sealed record PixelMedium(string Canonical, string Locale, double LongPx, double ShortPx);

  // The medium chosen for one layout. WidthPx/HeightPx are the size the page is
  // expected to have for the layout's orientation; the PNG header is the truth.
  internal sealed record MediaChoice(PixelMedium Medium, int WidthPx, int HeightPx, double SheetLongPx);

  /// <summary>Pixel size written in a medium name, long side first; null when the name has none.</summary>
  internal static (double Long, double Short)? ParsePixelSize(string? name)
  {
    if (string.IsNullOrWhiteSpace(name)) return null;
    var match = PixelSize.Match(name);
    if (!match.Success) return null;
    var width = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    var height = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
    if (width <= 0 || height <= 0) return null;
    return (Math.Max(width, height), Math.Min(width, height));
  }

  /// <summary>The media whose name carries a pixel size; paper-size media of the device are ignored.</summary>
  internal static List<PixelMedium> PixelMedia(IEnumerable<(string Canonical, string Locale)> media)
  {
    var result = new List<PixelMedium>();
    foreach (var (canonical, locale) in media)
    {
      var size = ParsePixelSize(canonical) ?? ParsePixelSize(locale);
      if (size != null) result.Add(new PixelMedium(canonical, locale, size.Value.Long, size.Value.Short));
    }
    return result;
  }

  // Sheet widths this close to the best are equally near the request: a few
  // percent of pixels is not worth a letterbox, so the aspect decides among them.
  private const double WidthTolerance = 0.05;

  /// <summary>
  /// Picks the pixel medium for a sheet. The sheet is plotted fit-to-medium, so
  /// its long side ends up min(medium long side, medium short side x sheet
  /// aspect) pixels wide. The media whose sheet comes closest to
  /// <paramref name="requestedWidth"/> (a ratio, so 1600 for 3300 beats 800) are
  /// the candidates, all within 5% of the closest one; among them the medium
  /// whose aspect is nearest the sheet's wins, then the larger one. So a device
  /// that offers a medium shaped like the sheet at about the requested width
  /// gets exactly that one, and a device that only offers 4:3 media gets the
  /// largest of them (the sheet is letterboxed, never cropped).
  /// </summary>
  internal static MediaChoice? Choose(IReadOnlyList<PixelMedium> media, double paperWidth, double paperHeight,
    int requestedWidth, bool landscape)
  {
    if (media.Count == 0 || paperWidth <= 0 || paperHeight <= 0 || requestedWidth <= 0) return null;
    var sheetAspect = Math.Max(paperWidth, paperHeight) / Math.Min(paperWidth, paperHeight);
    var scored = media
      .Select(medium =>
      {
        var sheetLong = SheetLongSide(medium, sheetAspect);
        return (Medium: medium,
          Width: Math.Abs(Math.Log(sheetLong / requestedWidth)),
          Aspect: Math.Abs(Math.Log(medium.LongPx / medium.ShortPx / sheetAspect)));
      })
      .ToList();
    var nearest = scored.Min(item => item.Width);
    var best = scored
      .Where(item => item.Width <= nearest + WidthTolerance)
      .OrderBy(item => Math.Round(item.Aspect, 6))
      .ThenByDescending(item => item.Medium.LongPx)
      .ThenBy(item => item.Medium.Canonical, StringComparer.OrdinalIgnoreCase)
      .First();
    return ToChoice(best.Medium, sheetAspect, landscape);
  }

  /// <summary>A medium the caller named, with the page size it is expected to give.</summary>
  internal static MediaChoice ForMedium(PixelMedium medium, double paperWidth, double paperHeight, bool landscape)
  {
    var sheetAspect = Math.Max(paperWidth, paperHeight) / Math.Min(paperWidth, paperHeight);
    return ToChoice(medium, sheetAspect, landscape);
  }

  private static double SheetLongSide(PixelMedium medium, double sheetAspect) =>
    Math.Min(medium.LongPx, medium.ShortPx * sheetAspect);

  private static MediaChoice ToChoice(PixelMedium medium, double sheetAspect, bool landscape) =>
    new(medium,
      (int)Math.Round(landscape ? medium.LongPx : medium.ShortPx),
      (int)Math.Round(landscape ? medium.ShortPx : medium.LongPx),
      Math.Round(SheetLongSide(medium, sheetAspect)));

  /// <summary>Width and height from the IHDR chunk of a PNG stream; null when it is not a PNG.</summary>
  internal static (int Width, int Height)? ReadPngSize(Stream stream)
  {
    stream.Position = 0;
    var header = new byte[24];
    var read = 0;
    while (read < header.Length)
    {
      var count = stream.Read(header, read, header.Length - read);
      if (count == 0) return null;
      read += count;
    }
    byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    for (var index = 0; index < signature.Length; index++)
    {
      if (header[index] != signature[index]) return null;
    }
    // Bytes 8-15 are the first chunk's length and type: it must be IHDR.
    if (header[12] != (byte)'I' || header[13] != (byte)'H' || header[14] != (byte)'D' || header[15] != (byte)'R') return null;
    var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
    var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
    return width > 0 && height > 0 ? (width, height) : null;
  }
}
