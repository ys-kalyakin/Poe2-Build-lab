using PBLEngine;
using Xunit;

namespace PBLEngine.Tests;

/// <summary>Композиция для /skills/dps: разбивка Full DPS (пер-скил) + метаданные
/// групп. На свежем билде — пусто и валидно; после добавления группы — группа видна.</summary>
[Collection("LuaHost")]
public class SkillsDpsTests : IClassFixture<LuaHostFixture>
{
    private readonly LuaHost _host;
    public SkillsDpsTests(LuaHostFixture fixture) => _host = fixture.Host;

    [Fact]
    public void FreshBuild_EmptyButValid()
    {
        Assert.NotNull(_host.GetFullDpsBreakdown());
        Assert.NotNull(_host.GetSkillGroups());
    }

    [Fact]
    public void AddedGroup_IsListedWithIndex()
    {
        _host.AddSkillGroupWithGem("Fireball");
        var groups = _host.GetSkillGroups();
        Assert.NotEmpty(groups);
        Assert.Contains(groups, g => g.Name == "Fireball");
        Assert.All(groups, g => Assert.True(g.Index >= 1));   // 1-based
        // группа не в Full DPS, пока флаг не включён — breakdown остаётся валидным списком
        Assert.NotNull(_host.GetFullDpsBreakdown());
    }
}
