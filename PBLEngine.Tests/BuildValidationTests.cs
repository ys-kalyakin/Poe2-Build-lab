using PBLEngine;
using Xunit;

namespace PBLEngine.Tests;

/// <summary>/build/validate: свод предупреждений из уже посчитанного
/// mainEnv/mainOutput. На свежем билде — пусто и валидно; гем с недостижимыми
/// требованиями атрибутов даёт attrs-нарушение.</summary>
[Collection("LuaHost")]
public class BuildValidationTests : IClassFixture<LuaHostFixture>
{
    private readonly LuaHost _host;
    public BuildValidationTests(LuaHostFixture fixture) => _host = fixture.Host;

    [Fact]
    public void FreshBuild_ValidShape_NoIssues()
    {
        var v = _host.GetBuildValidation();
        Assert.NotNull(v.Points);
        Assert.NotNull(v.Issues);
        Assert.NotNull(v.DisabledSkills);
        Assert.DoesNotContain(v.Issues, i => i.Code == "attrs");
    }

    [Fact]
    public void UnmetGemRequirements_ProduceAttrsIssue()
    {
        _host.AddSkillGroupWithGem("Fireball");   // level 19, reqInt ~98 >> атрибуты новичка
        var v = _host.GetBuildValidation();
        var attrs = v.Issues.Where(i => i.Code == "attrs").ToList();
        Assert.NotEmpty(attrs);
        Assert.All(attrs, i => Assert.False(string.IsNullOrWhiteSpace(i.Message)));
    }

    [Fact]
    public void ItemWarnings_NestedTables_SurfaceTheirStringsNotKeys()
    {
        // В PoB itemWarnings — вложенные таблицы (jewelLimitWarning/socketLimitWarning
        // — массивы строк, CalcSetup.lua:958/2127). Валидация должна отдавать строки
        // предупреждений, а не имена ключей вроде "socketLimitWarning".
        _host.RecalcStats();
        _host.State.DoString(@"
            if build and build.calcsTab and build.calcsTab.mainEnv then
                build.calcsTab.mainEnv.itemWarnings = {
                    socketLimitWarning = { 'Jewel radius exceeded for Test Jewel' },
                    plain = 'plain warning',
                }
            end
        ");
        var v = _host.GetBuildValidation();
        Assert.Contains(v.Issues, i => i.Code == "item" && i.Message.Contains("Jewel radius exceeded"));
        Assert.Contains(v.Issues, i => i.Code == "item" && i.Message == "plain warning");
        Assert.DoesNotContain(v.Issues, i => i.Code == "item" && i.Message == "socketLimitWarning");
        _host.State.DoString("if build and build.calcsTab and build.calcsTab.mainEnv then build.calcsTab.mainEnv.itemWarnings = {} end");
    }
}
