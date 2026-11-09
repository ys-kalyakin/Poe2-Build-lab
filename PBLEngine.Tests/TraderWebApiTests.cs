using PBLApp.Core.Trader;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace PBLEngine.Tests;

public class TraderWebApiTests
{
    // Форма ответа api/trade2/data/leagues — ровно 4 лиги trade-сайта
    private const string LeaguesJson =
        """{"result":[{"id":"Runes of Aldur","realm":"poe2","text":"Runes of Aldur"},{"id":"HC Runes of Aldur","realm":"poe2","text":"HC Runes of Aldur"},{"id":"Standard","realm":"poe2","text":"Standard"},{"id":"Hardcore","realm":"poe2","text":"Hardcore"}]}""";

    // Форма как в TradeQuery:PriceBuilderProcessPoENinjaResponse: lines[].id/primaryValue (в дивинах)
    private const string NinjaJson =
        """{"lines":[{"id":"exalted","primaryValue":0.005},{"id":"chaos","primaryValue":0.01},{"id":"mirror","primaryValue":400.0}]}""";

    private static FakeHttpHandler MakeHandler()
    {
        var fake = new FakeHttpHandler();
        fake.Responder = req =>
        {
            var url = req.RequestUri!.ToString();
            var json = url.Contains("trade2/data/leagues") ? LeaguesJson
                     : url.Contains("poe.ninja") ? NinjaJson
                     : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        };
        return fake;
    }

    [Fact]
    public async Task GetLeagues_ReturnsTradeSiteLeagues()
    {
        var api = new TraderWebApi(MakeHandler());
        var leagues = await api.GetLeaguesAsync();

        Assert.Equal(["Runes of Aldur", "HC Runes of Aldur", "Standard", "Hardcore"], leagues);
    }

    [Fact]
    public async Task GetCurrencyRates_ParsesAndCaches()
    {
        var fake = MakeHandler();
        var api = new TraderWebApi(fake);

        var rates = await api.GetCurrencyRatesAsync("Standard");
        Assert.Equal(0.005, rates["exalted"], 6);
        Assert.Equal(400.0, rates["mirror"], 6);

        var before = fake.Requests.Count;
        var again = await api.GetCurrencyRatesAsync("Standard"); // кэш — без второго запроса
        Assert.Same(rates, again);
        Assert.Equal(before, fake.Requests.Count);
    }

    // Форма как у Currency, но для другого exchange-типа ninja
    private const string NinjaRunesJson =
        """{"lines":[{"id":"iron","primaryValue":0.03}]}""";

    private static FakeHttpHandler MakeTypedHandler()
    {
        var fake = new FakeHttpHandler();
        fake.Responder = req =>
        {
            var url = req.RequestUri!.ToString();
            var json = url.Contains("type=Currency") ? NinjaJson
                     : url.Contains("type=Runes") ? NinjaRunesJson
                     : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        };
        return fake;
    }

    [Fact]
    public async Task GetExchangeRates_FetchesPerTypeAndCachesPerLeagueTypePair()
    {
        var fake = MakeTypedHandler();
        var api = new TraderWebApi(fake);

        var rates = await api.GetExchangeRatesAsync("Standard", "Currency");
        Assert.Equal(0.005, rates["exalted"], 6);

        var runes = await api.GetExchangeRatesAsync("Standard", "Runes");
        Assert.Equal(0.03, runes["iron"], 6);

        var before = fake.Requests.Count;
        await api.GetExchangeRatesAsync("Standard", "Currency");    // кэш — без запроса
        await api.GetExchangeRatesAsync("Standard", "Runes");       // кэш — без запроса
        await api.GetExchangeRatesAsync("HC Standard", "Currency"); // другая лига — запрос
        Assert.Equal(before + 1, fake.Requests.Count);
    }

    [Fact]
    public async Task GetCurrencyRates_SharesCacheWithExchangeRates()
    {
        var fake = MakeTypedHandler();
        var api = new TraderWebApi(fake);

        var viaOld = await api.GetCurrencyRatesAsync("Standard");
        var before = fake.Requests.Count;
        var viaNew = await api.GetExchangeRatesAsync("Standard", "Currency");
        Assert.Same(viaOld, viaNew); // общий кэш: старый метод — тот же вызов с type=Currency
        Assert.Equal(before, fake.Requests.Count);
    }

    [Fact]
    public void ExchangeTypes_ContainPluralNinjaTypes()
    {
        Assert.Contains("Fragments", TraderWebApi.ExchangeTypes);
        Assert.DoesNotContain("Fragment", TraderWebApi.ExchangeTypes);
        Assert.Contains("Runes", TraderWebApi.ExchangeTypes);
        Assert.DoesNotContain("Rune", TraderWebApi.ExchangeTypes);
        Assert.Equal(14, TraderWebApi.ExchangeTypes.Count);
    }
}
