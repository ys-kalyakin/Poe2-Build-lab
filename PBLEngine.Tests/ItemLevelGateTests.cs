using PBLApp.ViewModels;
using PBLEngine;
using Xunit;

namespace PBLEngine.Tests;

/// <summary>ilvl-гейт крафта: тиры афиксов с Level > ItemLevel нельзя добавить —
/// иначе агент конструирует фантазийные предметы (T1 на ilvl-1 базе) и примерка
/// даёт ложные цифры. Гейт живёт в ItemEditorViewModel.CanAddAffix.</summary>
[Collection("LuaHost")]
public class ItemLevelGateTests : IClassFixture<LuaHostFixture>
{
    private readonly LuaHost _host;
    public ItemLevelGateTests(LuaHostFixture fixture) => _host = fixture.Host;

    private static AffixEntry Entry(int level) => new(
        Id: "test", AffixName: "of Testing", StatText: "+1 to something",
        AffixType: "Prefix", Level: level, Group: "TestGroup", Tier: 1, TierCount: 8);

    private ItemEditorViewModel Editor()
    {
        var vm = new ItemEditorViewModel(_host, (_, _) => { });
        vm.ItemLevel = 80;
        return vm;
    }

    [Fact]
    public void TierAboveItemLevel_Rejected()
    {
        var ed = Editor();
        Assert.False(ed.CanAddAffix(Entry(90)));
    }

    [Fact]
    public void TierAtOrBelowItemLevel_Allowed()
    {
        var ed = Editor();
        Assert.True(ed.CanAddAffix(Entry(80)));   // граница
        Assert.True(ed.CanAddAffix(Entry(5)));
    }

    [Fact]
    public void RaisingItemLevel_UnlocksTier()
    {
        var ed = Editor();
        Assert.False(ed.CanAddAffix(Entry(95)));
        ed.ItemLevel = 95;
        Assert.True(ed.CanAddAffix(Entry(95)));
    }
}
