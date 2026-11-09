using PBLEngine;
using Xunit;

namespace PBLEngine.Tests;

/// <summary>Путь импорта билда для /build/import: PoB share code (base64url(zlib(xml)))
/// и программная загрузка XML в живой движок. Кодек и LoadBuildFromXml существуют —
/// тесты пинят контракт, который ест IPC-хендлер.</summary>
[Collection("LuaHost")]
public class BuildImportTests : IClassFixture<LuaHostFixture>
{
    private readonly LuaHost _host;
    public BuildImportTests(LuaHostFixture fixture) => _host = fixture.Host;

    [Fact]
    public void BuildCodec_Roundtrip_PreservesXml()
    {
        var xml = "<PathOfBuilding><Build level=\"1\" className=\"Witch\"/></PathOfBuilding>";
        Assert.Equal(xml, BuildCodec.Decode(BuildCodec.Encode(xml)));
        Assert.True(BuildCodec.LooksLikeCode(BuildCodec.Encode(xml)));
        Assert.Null(BuildCodec.Decode("not a code"));
    }

    [Fact]
    public void LoadBuildFromXml_Roundtrip_KeepsStatsAlive()
    {
        var xml = _host.SaveBuildToXml();
        Assert.False(string.IsNullOrEmpty(xml));
        Assert.Contains("PathOfBuilding", xml);

        _host.LoadBuildFromXml(xml, "RoundTrip");
        Assert.True(_host.GetAllStats().Count > 0);
    }
}
