using Civil3DMcpPlugin;

// AutoCAD-free checks of the review render's media choice and PNG header read
// (RasterPlot.cs). Nothing here talks to Civil 3D.
internal static class RasterPlotChecks
{
  private static readonly (string Canonical, string Locale)[] StandardMedia =
  [
    ("VGA_(640.00_x_480.00_Pixels)", "VGA (640.00 x 480.00 Pixels)"),
    ("SVGA_(800.00_x_600.00_Pixels)", "SVGA (800.00 x 600.00 Pixels)"),
    ("XVGA_(1024.00_x_768.00_Pixels)", "XVGA (1024.00 x 768.00 Pixels)"),
    ("Sun_Standard_(1152.00_x_900.00_Pixels)", "Sun Standard (1152.00 x 900.00 Pixels)"),
    ("Sun_Hi-Res_(1600.00_x_1280.00_Pixels)", "Sun Hi-Res (1600.00 x 1280.00 Pixels)"),
    ("ANSI_B_(17.00_x_11.00_Inches)", "ANSI B (17.00 x 11.00 Inches)"),
    ("User_Defined_Size", "User Defined Size"),
  ];

  internal static void Run(Action<bool, string> expect)
  {
    var size = RasterPlot.ParsePixelSize("Sun_Hi-Res_(1600.00_x_1280.00_Pixels)");
    expect(size is { Long: 1600, Short: 1280 }, "pixel size is read from a canonical medium name");
    size = RasterPlot.ParsePixelSize("Poster (1280.5 x 3300 pixels)");
    expect(size is { Long: 3300, Short: 1280.5 }, "pixel size is read from a locale name, long side first, case-insensitive");
    expect(RasterPlot.ParsePixelSize("ANSI_B_(17.00_x_11.00_Inches)") == null
      && RasterPlot.ParsePixelSize("User_Defined_Size") == null
      && RasterPlot.ParsePixelSize(null) == null
      && RasterPlot.ParsePixelSize("Odd (0 x 0 Pixels)") == null,
      "names without a pixel size are not pixel media");

    var media = RasterPlot.PixelMedia(StandardMedia);
    expect(media.Count == 5 && media.All(item => item.Canonical.Contains("Pixels")),
      "only the pixel media of a device are candidates");

    // 11 x 17 in, the sheet the default of 3300 px was chosen for.
    var choice = RasterPlot.Choose(media, 17, 11, 3300, true);
    expect(choice?.Medium.Canonical == "Sun_Hi-Res_(1600.00_x_1280.00_Pixels)",
      "the largest standard medium is the closest to a width no standard medium reaches");
    expect(choice is { WidthPx: 1600, HeightPx: 1280, SheetLongPx: 1600 },
      "a landscape sheet on a landscape medium is expected at the medium's own size");
    choice = RasterPlot.Choose(media, 11, 17, 3300, false);
    expect(choice is { WidthPx: 1280, HeightPx: 1600 }, "a portrait sheet swaps the expected pixel size");
    choice = RasterPlot.Choose(media, 17, 11, 800, true);
    expect(choice?.Medium.Canonical == "SVGA_(800.00_x_600.00_Pixels)",
      "a smaller requested width picks the medium whose sheet is closest to it");
    // A 4:3 medium letterboxes a 17 x 11 sheet: the sheet is 1600 px wide, not 1600 x 1280.
    choice = RasterPlot.Choose(media, 17, 11, 3300, true);
    expect(choice != null && Math.Abs(choice.SheetLongPx - 1600) < 0.5, "the fitted sheet width is the medium's long side");
    // A tall medium is only as wide as its short side lets a wide sheet be.
    var tall = new[] { new RasterPlot.PixelMedium("Tall", "Tall", 3300, 600) };
    choice = RasterPlot.Choose(tall, 17, 11, 3300, true);
    expect(choice != null && Math.Abs(choice.SheetLongPx - 600 * 17.0 / 11) < 1, "a medium short side limits the fitted sheet");

    // A device with media shaped like the sheet: the one at the requested width wins on aspect.
    var custom = RasterPlot.PixelMedia(StandardMedia.Concat(
    [
      ("Sheet_(3300.00_x_2550.00_Pixels)", "Sheet (3300.00 x 2550.00 Pixels)"),
      ("Sheet_17x11_(3300.00_x_2131.00_Pixels)", "Sheet 17x11 (3300.00 x 2131.00 Pixels)"),
    ]));
    choice = RasterPlot.Choose(custom, 17, 11, 3300, true);
    expect(choice?.Medium.Canonical == "Sheet_17x11_(3300.00_x_2131.00_Pixels)" && choice.WidthPx == 3300 && choice.HeightPx == 2131,
      "equal sheet widths go to the medium whose aspect is closest to the sheet's");
    choice = RasterPlot.Choose(custom, 36, 24, 3300, true);
    expect(choice?.Medium.Canonical == "Sheet_17x11_(3300.00_x_2131.00_Pixels)",
      "a 3:2 sheet takes the 17x11-shaped medium: its width is within 5% of the 4:3 one and its aspect is closer");
    var nearWidth = RasterPlot.PixelMedia(
    [
      ("Wide_(3300.00_x_2550.00_Pixels)", "Wide (3300.00 x 2550.00 Pixels)"),
      ("Shaped_(3200.00_x_2070.00_Pixels)", "Shaped (3200.00 x 2070.00 Pixels)"),
      ("Far_(2000.00_x_1294.00_Pixels)", "Far (2000.00 x 1294.00 Pixels)"),
    ]);
    choice = RasterPlot.Choose(nearWidth, 17, 11, 3300, true);
    expect(choice?.Medium.Canonical == "Shaped_(3200.00_x_2070.00_Pixels)",
      "a medium a few percent narrower but shaped like the sheet beats a letterboxed one");
    choice = RasterPlot.Choose(nearWidth, 17, 11, 2000, true);
    expect(choice?.Medium.Canonical == "Far_(2000.00_x_1294.00_Pixels)",
      "a medium shaped like the sheet at the requested width is taken as is");

    expect(RasterPlot.Choose([], 17, 11, 3300, true) == null
      && RasterPlot.Choose(media, 0, 11, 3300, true) == null
      && RasterPlot.Choose(media, 17, 11, 0, true) == null,
      "no media or an unusable sheet gives no choice");

    var named = RasterPlot.ForMedium(new RasterPlot.PixelMedium("VGA", "VGA", 640, 480), 17, 11, true);
    expect(named is { WidthPx: 640, HeightPx: 480, SheetLongPx: 640 }, "a named medium is used as given");
    var unknown = RasterPlot.ForMedium(new RasterPlot.PixelMedium("Poster", "Poster", 0, 0), 17, 11, true);
    expect(unknown is { WidthPx: 0, HeightPx: 0 }, "a named medium without a pixel size reports no expected size");

    expect(RasterPlot.DefaultPixelWidth == 3300 && RasterPlot.DefaultDevice == "PublishToWeb PNG.pc3",
      "the render defaults are 3300 px on PublishToWeb PNG.pc3");

    using var png = new MemoryStream(PngHeader(1600, 1280));
    expect(RasterPlot.ReadPngSize(png) is { Width: 1600, Height: 1280 }, "PNG width and height come from the IHDR chunk");
    using var wide = new MemoryStream(PngHeader(70000, 3));
    expect(RasterPlot.ReadPngSize(wide) is { Width: 70000, Height: 3 }, "PNG sizes above 16 bits are read");
    using var notPng = new MemoryStream(new byte[64]);
    expect(RasterPlot.ReadPngSize(notPng) == null, "a file without the PNG signature is not a PNG");
    var bad = PngHeader(10, 10);
    bad[12] = (byte)'X';
    using var noHeader = new MemoryStream(bad);
    expect(RasterPlot.ReadPngSize(noHeader) == null, "a PNG whose first chunk is not IHDR is refused");
    using var truncated = new MemoryStream(PngHeader(10, 10)[..20]);
    expect(RasterPlot.ReadPngSize(truncated) == null, "a truncated PNG header is refused");
    using var zero = new MemoryStream(PngHeader(0, 10));
    expect(RasterPlot.ReadPngSize(zero) == null, "a zero-sized PNG is refused");
  }

  private static byte[] PngHeader(int width, int height)
  {
    var header = new byte[33];
    byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
    signature.CopyTo(header, 0);
    for (var index = 0; index < 4; index++)
    {
      header[16 + index] = (byte)(width >> (24 - 8 * index));
      header[20 + index] = (byte)(height >> (24 - 8 * index));
    }
    header[24] = 8;
    header[25] = 6;
    return header;
  }
}
