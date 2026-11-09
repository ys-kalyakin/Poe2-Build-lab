using PBLApp.Core;
using System.Globalization;
using System.Threading;
using Xunit;

namespace PBLEngine.Tests;

/// <summary>
/// Numeric config values are parsed with the invariant culture: the engine's
/// Lua tonumber() expects a dot decimal separator regardless of the OS locale
/// (the app runs under ru-RU where "1,5" is the natural typing).
/// </summary>
public class ConfigValuesTests
{
    [Fact]
    public void IsNumericInvariant_DotDecimal_ParsesUnderRuCulture()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            Assert.True(ConfigValues.IsNumericInvariant("1.5"));
            Assert.True(ConfigValues.IsNumericInvariant("80"));
            Assert.True(ConfigValues.IsNumericInvariant("-10"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = prev;
        }
    }

    [Fact]
    public void IsNumericInvariant_CommaDecimal_Rejected()
    {
        // "1,5" must NOT pass: it would poison downstream Lua arithmetic
        // (tonumber("1,5") is nil) even though ru-RU's double.TryParse accepts it.
        var prev = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            Assert.False(ConfigValues.IsNumericInvariant("1,5"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = prev;
        }
    }

    [Fact]
    public void IsNumericInvariant_EmptyAndGarbage_Rejected()
    {
        Assert.False(ConfigValues.IsNumericInvariant(""));
        Assert.False(ConfigValues.IsNumericInvariant("  "));
        Assert.False(ConfigValues.IsNumericInvariant("abc"));
    }
}
