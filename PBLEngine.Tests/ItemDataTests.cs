using PBLEngine;
using System.Linq;
using Xunit;

namespace PBLEngine.Tests;

/// <summary>Слой данных вещей, который едят IPC /items/*: существование баз,
/// пулы афиксов с тирами, коррапшн, руны, уники — на реальных Lua-данных.</summary>
[Collection("LuaHost")]
public class ItemDataTests : IClassFixture<LuaHostFixture>
{
    private readonly LuaHost _host;
    public ItemDataTests(LuaHostFixture fixture) => _host = fixture.Host;

    [Fact]
    public void GetItemBaseCategories_NonEmptyAndContainKnownBases()
    {
        var categories = _host.GetItemBaseCategories();
        Assert.NotEmpty(categories);

        // существование конкретной базы: она лежит в какой-то из категорий
        var beltCategory = categories.FirstOrDefault(c =>
            _host.GetItemBasesForCategory(c).Any(b => b.Name == "Heavy Belt"));
        Assert.NotNull(beltCategory);

        var belts = _host.GetItemBasesForCategory(beltCategory!);
        var heavyBelt = belts.First(b => b.Name == "Heavy Belt");
        Assert.False(string.IsNullOrEmpty(heavyBelt.Type));
    }

    [Fact]
    public void GetItemAffixes_HeavyBelt_HasPrefixSuffixAndTiers()
    {
        var affixes = _host.GetItemAffixes("Heavy Belt");
        Assert.NotEmpty(affixes);
        Assert.Contains(affixes, a => a.AffixType == "Prefix");
        Assert.Contains(affixes, a => a.AffixType == "Suffix");
        // мод-тиры разрешаются для этой базы: есть афикс с известным тиром
        Assert.Contains(affixes, a => a.Tier > 0 && a.TierCount >= a.Tier);
        Assert.All(affixes, a => Assert.False(string.IsNullOrEmpty(a.StatText)));
    }

    [Fact]
    public void GetItemCorruptedAffixes_HeavyBelt_NonEmpty()
    {
        var corruption = _host.GetItemCorruptedAffixes("Heavy Belt");
        Assert.NotEmpty(corruption);
        Assert.All(corruption, a => Assert.False(string.IsNullOrEmpty(a.StatText)));
    }

    [Fact]
    public void GetAllRunes_NonEmptyWithPerSlotMods()
    {
        var runes = _host.GetAllRunes();
        Assert.NotEmpty(runes);
        Assert.Contains(runes, r => r.ModsByType.Count > 0 &&
                                    r.ModsByType.Values.Any(v => v.Count > 0));
    }

    [Fact]
    public void GetUniqueItems_NonEmptyWithNamesAndBases()
    {
        var uniques = _host.GetUniqueItems();
        Assert.NotEmpty(uniques);
        Assert.All(uniques, u => Assert.False(string.IsNullOrEmpty(u.Name)));
        Assert.Contains(uniques, u => !string.IsNullOrEmpty(u.BaseName));
    }
}
