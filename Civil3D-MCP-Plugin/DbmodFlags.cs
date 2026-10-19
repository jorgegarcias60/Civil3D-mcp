namespace Civil3DMcpPlugin;

/// <summary>
/// Names for the bits of AutoCAD's DBMOD system variable, which says why a
/// drawing counts as modified: 1 objects (entities, tables, dictionaries,
/// xdata), 4 drawing variables saved in the DWG, 8 window, 16 view, 32 fields.
/// Bit 2 is not documented in current releases; it and any other bit are
/// reported as "bit N", N being the bit's value. Only bits 1 and 4 are drawing
/// content a discard would lose. AutoCAD-free, so RunCodeHarness checks it.
/// </summary>
internal static class DbmodFlags
{
  internal const int Objects = 1;
  internal const int Variables = 4;
  internal const int Window = 8;
  internal const int View = 16;
  internal const int Fields = 32;
  private const int DisplayOnly = Window | View | Fields;

  /// <summary>The set bits' names, lowest bit first.</summary>
  internal static IReadOnlyList<string> Describe(int value)
  {
    var names = new List<string>();
    for (var shift = 0; shift < 32; shift++)
    {
      var bit = 1u << shift;
      if (((uint)value & bit) == 0) continue;
      names.Add(bit switch
      {
        Objects => "objects",
        Variables => "variables",
        Window => "window",
        View => "view",
        Fields => "fields",
        _ => $"bit {bit}",
      });
    }
    return names;
  }

  /// <summary>"0", or the value and its bit names, such as "48 (view, fields)".</summary>
  internal static string Format(int value) =>
    value == 0 ? "0" : $"{value} ({string.Join(", ", Describe(value))})";

  internal static bool HasContentChanges(int value) => (value & (Objects | Variables)) != 0;

  internal static Dictionary<string, object?> ToJson(int value) => new()
  {
    ["value"] = value,
    ["flags"] = Describe(value).ToArray(),
  };

  /// <summary>"A.dwg (DBMOD 1 (objects)), B.dwg (DBMOD 16 (view))".</summary>
  internal static string FormatDrawings(IEnumerable<(string Name, int Value)> drawings) =>
    string.Join(", ", drawings.Select(drawing => $"{drawing.Name} (DBMOD {Format(drawing.Value)})"));

  /// <summary>
  /// civil3d_instance stop's refusal. The words around the list stay as they
  /// were, since callers match them. When no listed drawing has content bits,
  /// one sentence says so: the refusal then protects no objects or variables.
  /// </summary>
  internal static string UnsavedDrawingsMessage(IReadOnlyCollection<(string Name, int Value)> drawings)
  {
    var message = $"Instance has unsaved drawings: {FormatDrawings(drawings)}.";
    if (drawings.Count > 0 && drawings.All(drawing => !HasContentChanges(drawing.Value)))
      message += drawings.All(drawing => (drawing.Value & ~DisplayOnly) == 0)
        ? " Only view, window or field flags are set: no object or drawing-variable changes."
        : " No object or drawing-variable changes are flagged.";
    return message + " Pass discardChanges=true to close them without saving.";
  }
}
