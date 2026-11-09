using System.Globalization;

namespace PBLApp.Core;

/// <summary>
/// Numeric validation for Config-tab values. The engine's Lua
/// <c>tonumber()</c> is locale-independent and expects a dot decimal
/// separator, so the C# gate must be invariant too — under ru-RU a
/// culture-sensitive double.TryParse would let "1,5" through and poison
/// downstream Lua arithmetic.
/// </summary>
public static class ConfigValues
{
    public static bool IsNumericInvariant(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}
