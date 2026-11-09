using PBLEngine;
using System.Linq;
using Xunit;

namespace PBLEngine.Tests;

/// <summary>Каталог гемов (data.gems) для MCP-агента: какие гемы существуют,
/// их теги/требования/мана — чтобы агент строил скил-сетапы по данным приложения,
/// а не по памяти о PoE2.</summary>
[Collection("LuaHost")]
public class GemCatalogTests : IClassFixture<LuaHostFixture>
{
    private readonly LuaHost _host;
    public GemCatalogTests(LuaHostFixture fixture) => _host = fixture.Host;

    [Fact]
    public void Catalog_NonEmpty_AllNamesKnown()
    {
        var gems = _host.GetGemCatalog();
        Assert.NotEmpty(gems);
        Assert.All(gems, g => Assert.False(string.IsNullOrWhiteSpace(g.Name)));
        Assert.Contains(gems, g => g.Support);           // саппорты есть
        Assert.Contains(gems, g => !g.Support);          // активы есть
    }

    [Fact]
    public void Fireball_PresentWithMetadata()
    {
        var fb = Assert.Single(_host.GetGemCatalog(), g => g.Name == "Fireball");
        Assert.False(fb.Support);
        Assert.True(fb.ReqInt > 0);
        Assert.True(fb.ManaCost > 0);
        Assert.False(string.IsNullOrEmpty(fb.TagString));
    }
}
