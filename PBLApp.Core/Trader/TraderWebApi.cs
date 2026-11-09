using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PBLApp.Core.Trader;

/// <summary>
/// Прямые (без Lua) веб-запросы вкладки «Трейдер»: список лиг PoE2 и курсы
/// валют poe.ninja (в дивинах, кэш на час на лигу). Handler инжектируется в тестах.
/// </summary>
public sealed class TraderWebApi
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, (DateTime FetchedAt, IReadOnlyDictionary<string, double> Rates)> _ratesCache = [];
    private static readonly TimeSpan RatesTtl = TimeSpan.FromHours(1);

    /// <summary>Exchange-типы poe.ninja для poe2 (doka /docs/api). Важно: во
    /// множественном числе — «Fragment»/«Rune» ninja молча возвращает пустыми.</summary>
    public static readonly IReadOnlyList<string> ExchangeTypes =
    [
        "Currency", "Fragments", "Abyss", "UncutGems", "LineageSupportGems", "Essences",
        "SoulCores", "Idols", "Runes", "RitualOmens", "Expedition", "Delirium", "Breach", "Verisium",
    ];

    public TraderWebApi(HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler(), disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PathOfBuilding-PBLApp/1.0");
    }

    /// <summary>Лиги trade2-сайта — ровно те 4 варианта, что в его дропдауне
    /// (api/leagues отдаёт общий список с PoE1-лигами, которых на трейде нет).</summary>
    public async Task<IReadOnlyList<string>> GetLeaguesAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(
            "https://www.pathofexile.com/api/trade2/data/leagues", ct);
        using var doc = JsonDocument.Parse(json);
        var leagues = new List<string>();
        if (doc.RootElement.TryGetProperty("result", out var result) &&
            result.ValueKind == JsonValueKind.Array)
        {
            foreach (var league in result.EnumerateArray())
            {
                var id = league.TryGetProperty("id", out var i) ? i.GetString() : null;
                var realm = league.TryGetProperty("realm", out var r) ? r.GetString() : "poe2";
                if (!string.IsNullOrEmpty(id) && realm == "poe2")
                    leagues.Add(id);
            }
        }
        return leagues;
    }

    /// <summary>Курсы валют лиги в дивинах: id → primaryValue
    /// (форма ответа — как в TradeQuery:PriceBuilderProcessPoENinjaResponse).</summary>
    public Task<IReadOnlyDictionary<string, double>> GetCurrencyRatesAsync(
        string league, CancellationToken ct = default)
        => GetExchangeRatesAsync(league, "Currency", ct);

    /// <summary>Курсы exchange-типа ninja (ExchangeTypes) лиги в дивинах:
    /// id → primaryValue. Кэш на час на пару (league, type).</summary>
    public async Task<IReadOnlyDictionary<string, double>> GetExchangeRatesAsync(
        string league, string type, CancellationToken ct = default)
    {
        var key = $"{league}|{type}";
        if (_ratesCache.TryGetValue(key, out var cached) &&
            DateTime.UtcNow - cached.FetchedAt < RatesTtl)
            return cached.Rates;

        var json = await _http.GetStringAsync(
            "https://poe.ninja/poe2/api/economy/exchange/current/overview?type="
                + Uri.EscapeDataString(type)
                + "&league=" + Uri.EscapeDataString(league), ct);
        using var doc = JsonDocument.Parse(json);
        var rates = new Dictionary<string, double>();
        if (doc.RootElement.TryGetProperty("lines", out var lines) &&
            lines.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in lines.EnumerateArray())
            {
                if (line.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } cid &&
                    line.TryGetProperty("primaryValue", out var v) &&
                    v.ValueKind == JsonValueKind.Number)
                {
                    rates[cid] = v.GetDouble();
                }
            }
        }
        _ratesCache[key] = (DateTime.UtcNow, rates);
        return rates;
    }

    private readonly Dictionary<string, (DateTime FetchedAt, IReadOnlyList<UniquePriceEntry> Items)> _itemCache = [];

    /// <summary>Stash-типы poe.ninja для poe2 (доки /docs/api): уники. Для poe2
    /// формы со «s» — UniqueWeapons, не UniqueWeapon (как в poe1).</summary>
    public static readonly IReadOnlyList<string> StashItemTypes =
    [
        "UniqueWeapons", "UniqueArmours", "UniqueAccessories", "UniqueFlasks",
        "UniqueCharms", "UniqueJewels", "UniqueSanctumRelics", "UniqueTablets",
        "PrecursorTablets",
    ];

    /// <summary>Цены уников stash-типа ninja лиги (DivineValue — в дивинах,
    /// ListingCount — живых лотов). Кэш на час на (league, type), полный список;
    /// query — регистронезависимый substring по name/baseType поверх кэша.</summary>
    public async Task<IReadOnlyList<UniquePriceEntry>> GetUniquePricesAsync(
        string league, string type, string? query = null, CancellationToken ct = default)
    {
        var key = $"{league}|{type}";
        if (!_itemCache.TryGetValue(key, out var cached) ||
            DateTime.UtcNow - cached.FetchedAt >= RatesTtl)
        {
            var json = await _http.GetStringAsync(
                "https://poe.ninja/poe2/api/economy/stash/current/item/overview?type="
                    + Uri.EscapeDataString(type)
                    + "&league=" + Uri.EscapeDataString(league), ct);
            cached = (DateTime.UtcNow, ParseUniquePrices(json));
            _itemCache[key] = cached;
        }

        if (string.IsNullOrEmpty(query))
            return cached.Items;
        return cached.Items
            .Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        i.BaseName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static IReadOnlyList<UniquePriceEntry> ParseUniquePrices(string json)
    {
        var items = new List<UniquePriceEntry>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("lines", out var lines) &&
            lines.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in lines.EnumerateArray())
            {
                var name = line.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;
                var baseName = line.TryGetProperty("baseType", out var b) ? b.GetString() : "";
                double divine = line.TryGetProperty("primaryValue", out var pv) &&
                                pv.ValueKind == JsonValueKind.Number ? pv.GetDouble() : 0;
                int listings = line.TryGetProperty("listingCount", out var lc) &&
                               lc.ValueKind == JsonValueKind.Number ? lc.GetInt32() : 0;
                bool corrupted = line.TryGetProperty("corrupted", out var c) &&
                                 c.ValueKind == JsonValueKind.True;
                string? detailsId = line.TryGetProperty("detailsId", out var d) ? d.GetString() : null;

                var mods = new List<string>();
                if (line.TryGetProperty("explicitModifiers", out var ems) &&
                    ems.ValueKind == JsonValueKind.Array)
                {
                    foreach (var em in ems.EnumerateArray())
                    {
                        if (em.TryGetProperty("text", out var t) &&
                            t.GetString() is { Length: > 0 } text)
                            mods.Add(text);
                    }
                }

                items.Add(new UniquePriceEntry(name!, baseName ?? "", divine, listings,
                    corrupted, mods, detailsId));
            }
        }
        return items;
    }
}

/// <summary>Строка цены уника из poe.ninja (stash item overview): цена в дивинах,
/// число живых лотов и текст модов.</summary>
public sealed record UniquePriceEntry(
    string Name,
    string BaseName,
    double DivineValue,
    int ListingCount,
    bool Corrupted,
    IReadOnlyList<string> Mods,
    string? DetailsId);
