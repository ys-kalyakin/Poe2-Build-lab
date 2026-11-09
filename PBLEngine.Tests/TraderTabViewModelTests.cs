using PBLApp.ViewModels;
using PBLEngine;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PBLEngine.Tests;

[Collection("LuaHost")]
public class TraderTabViewModelTests : IClassFixture<LuaHostFixture>
{
    private readonly LuaHost _host;

    public TraderTabViewModelTests(LuaHostFixture fixture)
    {
        _host = fixture.Host;
        _host.NewBuild();
    }

    [Fact(Timeout = 60_000)]
    public async Task TryOnListing_EquipsItemAndChangesLife()
    {
        var before = Convert.ToDouble(_host.GetStat("Life"));

        var ok = await _host.TryOnListingAsync("Helmet",
            "Rarity: RARE\nDoom Crown\nWarrior Greathelm\nItem Level: 81\nImplicits: 0\n+120 to maximum Life",
            CancellationToken.None);

        Assert.True(ok);
        var after = Convert.ToDouble(_host.GetStat("Life"));
        Assert.True(after > before, $"Life before={before}, after={after}");
    }

    // Кнопка «Примерить» больше не гаснет после первого нажатия, поэтому повторная
    // примерка того же лота обязана надеть предмет из пула, а не плодить копии
    [Fact(Timeout = 60_000)]
    public async Task TryOnListing_Twice_DoesNotDuplicatePoolItem()
    {
        const string itemText =
            "Rarity: RARE\nDoom Crown\nWarrior Greathelm\nItem Level: 81\nImplicits: 0\n+120 to maximum Life";

        Assert.True(await _host.TryOnListingAsync("Helmet", itemText, CancellationToken.None));
        var afterFirst = PoolItemCount();
        var life = Convert.ToDouble(_host.GetStat("Life"));

        Assert.True(await _host.TryOnListingAsync("Helmet", itemText, CancellationToken.None));

        Assert.True(afterFirst == PoolItemCount(), DumpPool());
        Assert.Equal(life, Convert.ToDouble(_host.GetStat("Life")), 6);
    }

    private int PoolItemCount() => Convert.ToInt32(_host.State.DoString(
        "local n = 0 for _ in pairs(build.itemsTab.items) do n = n + 1 end return n")[0]);

    private string DumpPool() => (string)_host.State.DoString(
        "local out = {} for id, it in pairs(build.itemsTab.items) do " +
        "out[#out+1] = '--- ' .. tostring(id) .. ' ---\\n' .. tostring(it:BuildRaw()) end " +
        "return table.concat(out, '\\n')")[0];

    [Fact(Timeout = 60_000)]
    public async Task TryOnListing_GarbageText_ReturnsFalse()
    {
        var ok = await _host.TryOnListingAsync("Helmet", "garbage", CancellationToken.None);
        Assert.False(ok);
    }

    [Fact]
    public void Session_PopulatesWeights_AndPresetsChangeWeights()
    {
        var session = new TraderSession(_host, new BuildModel(_host),
            webApi: new PBLApp.Core.Trader.TraderWebApi(new FakeHttpHandler()));

        Assert.NotEmpty(session.WeightStats);
        Assert.Contains(session.KnownSlots, s => s == "Helmet");
        Assert.Contains(session.KnownSlots, s => s == "Body Armour");

        session.ApplyPresetCommand.Execute("ehp");
        var fullDps = session.WeightStats.First(w => w.Stat == "FullDPS");
        var totalEhp = session.WeightStats.First(w => w.Stat == "TotalEHP");
        Assert.Equal(0.1, fullDps.WeightMult, 6);
        Assert.Equal(1.0, totalEhp.WeightMult, 6);
        Assert.True(session.HasActiveWeights);
        Assert.Contains("FullDPS", session.StatWeightsJson);
    }

    [Fact]
    public void Window_BuildsOptionsJson_FromSessionWeightsAndMaxPrice()
    {
        var session = new TraderSession(_host, new BuildModel(_host),
            webApi: new PBLApp.Core.Trader.TraderWebApi(new FakeHttpHandler()));
        session.ApplyPresetCommand.Execute("dps");
        var win = new TraderWindowViewModel(session, "Helmet");

        Assert.Contains("\"statWeights\"", win.OptionsJson);
        Assert.Contains("FullDPS", win.OptionsJson);
    }

    [Fact]
    public void Window_RequiredFilters_LoadAddAndBuildJson_PersistPerSlotInSession()
    {
        var session = new TraderSession(_host, new BuildModel(_host),
            webApi: new PBLApp.Core.Trader.TraderWebApi(new FakeHttpHandler()));
        var win = new TraderWindowViewModel(session, "Helmet");
        Assert.True(win.HasStatCategory);

        win.LoadAvailableStatsCommand.Execute(null);
        Assert.NotEmpty(win.AvailableStats);

        var stat = win.AvailableStats[0];
        win.AddRequiredCommand.Execute(stat);
        win.RequiredFilters[0].Min = "75";

        Assert.Contains(stat.Id, win.RequiredJson);
        Assert.Contains("\"min\":75", win.RequiredJson);

        // персист в сессии: перенацелить на другой слот и обратно — фильтр на месте
        win.Retarget("Gloves");
        Assert.Empty(win.RequiredFilters);
        win.Retarget("Helmet");
        Assert.Single(win.RequiredFilters);
        Assert.Equal(stat.Id, win.RequiredFilters[0].Id);
    }

    // Форма stash item overview ninja (как в TraderWebApiTests)
    private const string StashJson =
        """{"lines":[{"name":"Winter's Bite","baseType":"Glass Shank","primaryValue":1.25,"listingCount":7,"corrupted":false,"detailsId":"winters-bite-glass-shank","explicitModifiers":[{"text":"Adds (8-10) to (15-18) Cold Damage"}]},{"name":"Doom Fletch","baseType":"Blood Bow","primaryValue":0.4,"listingCount":22,"corrupted":true}]}""";

    // Форма exchange overview ninja (как в TraderWebApiTests)
    private const string NinjaJson =
        """{"lines":[{"id":"exalted","primaryValue":0.005},{"id":"chaos","primaryValue":0.01},{"id":"mirror","primaryValue":400.0}]}""";

    private static FakeHttpHandler MakePriceHandler() =>
        new()
        {
            Responder = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    req.RequestUri!.ToString().Contains("/stash/") ? StashJson : NinjaJson,
                    Encoding.UTF8, "application/json"),
            },
        };

    [Fact(Timeout = 60_000)]
    public async Task Session_PriceWrappers_UseSessionLeagueAndSkipNetworkWithoutLeague()
    {
        // выбранная лига персистится в настройки — сохраняем и возвращаем;
        // стартово чистим: noLeague-сценарий не должен подхватить чужую лигу
        var savedLeague = PBLApp.Core.AppPreferences.Get("TraderLeague");
        PBLApp.Core.AppPreferences.Set("TraderLeague", "");
        try
        {
            var fake = MakePriceHandler();
            var session = new TraderSession(_host, new BuildModel(_host),
                webApi: new PBLApp.Core.Trader.TraderWebApi(fake));
            session.SelectedLeague = "Standard";
            await Task.Delay(300); // фон конструктора (лиги/курсы) оседает — ответы fake мгновенны

            var items = await session.GetUniquePricesAsync("UniqueWeapons");
            Assert.Equal(2, items.Count);
            Assert.Equal(1.25, items[0].DivineValue, 6);

            var rates = await session.GetExchangeRatesAsync("Currency");
            Assert.Equal(0.005, rates["exalted"], 6);

            var stashBefore = fake.Requests.Count(r =>
                r.RequestUri!.ToString().Contains("/stash/"));
            var filtered = await session.GetUniquePricesAsync("UniqueWeapons", "glass");
            Assert.Single(filtered);
            Assert.Equal(stashBefore, fake.Requests.Count(r =>
                r.RequestUri!.ToString().Contains("/stash/"))); // фильтр поверх кэша

            // сессия без лиги: в ninja не ходим вообще. Pref чистим повторно:
            // session1 выше персистил «Standard», а InitLeaguesAsync noLeague
            // читает преф асинхронно и назначил бы её фоном
            PBLApp.Core.AppPreferences.Set("TraderLeague", "");
            var emptyFake = MakePriceHandler();
            var noLeague = new TraderSession(_host, new BuildModel(_host),
                webApi: new PBLApp.Core.Trader.TraderWebApi(emptyFake));
            var none = await noLeague.GetUniquePricesAsync("UniqueWeapons");
            Assert.Empty(none);
            Assert.Empty(await noLeague.GetExchangeRatesAsync("Currency"));
            Assert.DoesNotContain(emptyFake.Requests,
                r => r.RequestUri!.ToString().Contains("poe.ninja"));
        }
        finally
        {
            if (savedLeague is null) PBLApp.Core.AppPreferences.Set("TraderLeague", "");
            else PBLApp.Core.AppPreferences.Set("TraderLeague", savedLeague);
        }
    }
}
