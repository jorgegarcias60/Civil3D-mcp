using System.Globalization;

namespace Civil3DMcpPlugin;

// AutoCAD-free comparison of system variable values, so a value that did not
// change is not set again (finding cad-tools#18: on Civil 3D 2027, restoring a
// variable after a large raster plot crashed acad.exe; setting CTAB to the
// layout that is already current is a layout activation all the same).
internal static class SystemVariableValues
{
  // Strings compare case-insensitively (CTAB "Model" equals "MODEL"); numbers
  // compare by value whatever their boxed type (short 0 equals int 0). A null on
  // either side is never "the same", so the caller does not skip it.
  internal static bool Same(object? current, object? original)
  {
    if (current == null || original == null) return false;
    if (current is string currentText && original is string originalText)
      return string.Equals(currentText, originalText, StringComparison.OrdinalIgnoreCase);
    if (IsNumber(current) && IsNumber(original))
      return Convert.ToDouble(current, CultureInfo.InvariantCulture) == Convert.ToDouble(original, CultureInfo.InvariantCulture);
    return Equals(current, original);
  }

  internal static string Describe(object? value) => value switch
  {
    null => "null",
    string text => $"'{text}'",
    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
    _ => value.ToString() ?? "?",
  };

  private static bool IsNumber(object value) => value is short or int or long or double or float or decimal or byte or ushort or uint;
}
