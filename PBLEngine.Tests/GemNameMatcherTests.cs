using PBLApp.ViewModels;
using System.Linq;
using Xunit;

namespace PBLEngine.Tests;

/// <summary>Резолв имён гемов для /skills/add-group: опечатка агента должна давать
/// ошибку с подсказками, а не молча битую группу ("Unrecognised gem name" в Lua
/// не всплывает в IPC).</summary>
public class GemNameMatcherTests
{
    private static readonly string[] Gems = ["Fireball", "Fire Trap", "Cold Snap", "Frost Bomb", "Momentum Support"];

    [Fact]
    public void FindExact_IsCaseInsensitive()
        => Assert.Equal("Fireball", GemNameMatcher.FindExact("fireball", Gems));

    [Fact]
    public void FindExact_Unknown_ReturnsNull()
        => Assert.Null(GemNameMatcher.FindExact("нет такого", Gems));

    [Fact]
    public void Suggest_TypoWithSpace_FindsBySubstring()
    {
        var s = GemNameMatcher.Suggest("fire bal", Gems);
        Assert.Contains("Fireball", s);
    }

    [Fact]
    public void Suggest_Substring_OnlyMatching()
    {
        var s = GemNameMatcher.Suggest("cold", Gems);
        Assert.Equal(["Cold Snap"], s);
    }

    [Fact]
    public void Suggest_CappedAndDeterministic()
    {
        var a = GemNameMatcher.Suggest("f", Gems, max: 2);
        var b = GemNameMatcher.Suggest("f", Gems, max: 2);
        Assert.Equal(a, b);
        Assert.True(a.Length <= 2);
        Assert.Contains("Fireball", a);   // "f" → Fireball, Frost Bomb, Fire Trap — кап 2, Fireball короче
    }
}
