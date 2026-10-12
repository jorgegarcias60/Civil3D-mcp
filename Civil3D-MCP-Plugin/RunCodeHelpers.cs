using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Civil3DMcpPlugin;

/// <summary>Small, AutoCAD-free helpers shared with the offline harness.</summary>
internal static class RunCodeHelpers
{
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
