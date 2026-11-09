# MCP Item Economy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Агент через MCP получает существование вещей (базы, уники), пулы афиксов с тирами и цены (валюта/уники — poe.ninja; точный предмет — живой trade2 через существующий трейд-флоу).

**Architecture:** Слой фактов — `TraderWebApi` (ninja exchange+stash, кэш 1ч на (league,type)); данные вещей — существующие LuaHost-методы (`GetItemBaseCategories/GetItemBasesForCategory/GetItemAffixes/GetItemCorruptedAffixes/GetAllRunes/GetUniqueItems`); наружу — 5 IPC-эндпоинтов в `IpcServer` и 5 тонких MCP-обёрток в `VisualTools`. Оценку дешевизны rares делает сам агент из тиров.

**Tech Stack:** C# / .NET (PBLApp.Core, PBLEngine, PBLApp, PBLMcp), xUnit, System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-11-09-mcp-item-economy-design.md`

## Global Constraints

- dotnet в `~/.dotnet` (Linux); код кроссплатформенный (Windows+Linux).
- Ninja-типы ровно: exchange — `Currency, Fragments, Abyss, UncutGems, LineageSupportGems, Essences, SoulCores, Idols, Runes, RitualOmens, Expedition, Delirium, Breach, Verisium`; stash — `UniqueWeapons, UniqueArmours, UniqueAccessories, UniqueFlasks, UniqueCharms, UniqueJewels, UniqueSanctumRelics, UniqueTablets, PrecursorTablets` (множественное число; ninja на неверный тип отвечает 200 с пустыми данными — потому whitelist).
- Кэш ninja — 1 час на пару (league, type).
- Сигнатура `GetCurrencyRatesAsync(league, ct)` не меняется (её едят TraderSession и Lua-PriceBuilder).
- IPC-обработчики — на UI-потоке через `OnUi`/`OnUiAsync`, ответы — анонимные объекты (паттерн существующих хендлеров `IpcServer.cs`).
- MCP-инструменты — тонкие обёртки `IpcClient.CallAsync` в try/catch с `Error(ex)`.

## Review Focus

1. Неверный ninja-тип («Fragment») → ninja возвращает 200 с пустыми `lines` — пустой ответ без ошибки выглядел бы как «рынок пуст». Ожидается: whitelist в `TraderWebApi` отсекает до запроса (тест в Task 1/2).
2. Кэш-ключ без типа: тот же league, другой type — должен рефетчить, иначе руны получат курсы валют. Ожидается: ключ `league|type` (тесты Task 1/2).
3. Пустой `SelectedLeague` у сессии — запрос в ninja с `league=` мусорит и вернёт чужие данные. Ожидается: сессия возвращает пустой результат без сети (тест Task 3).
4. `explicitModifiers` — массив объектов с полем `text` (не строки); элемент без `text` пропускается, не падает (тест Task 2).
5. `query`-фильтр уника регистронезависимый по Name/BaseName и применяется к кэшированному списку — повторный вызов с другим query не бьёт по сети (тест Task 2).

---

### Task 1: TraderWebApi — exchange-типы (`GetExchangeRatesAsync`)

**Files:**
- Modify: `PBLApp.Core/Trader/TraderWebApi.cs`
- Test: `PBLEngine.Tests/TraderWebApiTests.cs`

**Interfaces:**
- Consumes: существующий `_ratesCache`, паттерн `FakeHttpHandler` (`PBLEngine.Tests/TraderHttpTests.cs`).
- Produces: `public Task<IReadOnlyDictionary<string, double>> GetExchangeRatesAsync(string league, string type, CancellationToken ct = default)`; `public static readonly IReadOnlyList<string> ExchangeTypes` (14 значений из Global Constraints). `GetCurrencyRatesAsync(league, ct)` делегирует в новый метод с `"Currency"`, кэш общий.

- [ ] **Step 1: Write the failing tests** в `TraderWebApiTests.cs`

```csharp
[Fact]
public async Task GetExchangeRates_FetchesPerTypeAndCachesPerLeagueTypePair()
{
    // fake.Responder: URL содержит "type=Currency" → NinjaJson; "type=Runes" → RunesJson
    // ({"lines":[{"id":"iron","primaryValue":0.03}]});
    // другой league → тоже отдельный ответ-счётчик
    var api = new TraderWebApi(fake);
    var rates = await api.GetExchangeRatesAsync("Standard", "Currency");
    Assert.Equal(0.005, rates["exalted"], 6);
    var runes = await api.GetExchangeRatesAsync("Standard", "Runes");
    Assert.Equal(0.03, runes["iron"], 6);
    var before = fake.Requests.Count;
    await api.GetExchangeRatesAsync("Standard", "Currency");   // кэш — без запроса
    await api.GetExchangeRatesAsync("Standard", "Runes");
    await api.GetExchangeRatesAsync("HC Standard", "Currency"); // другая лига — запрос
    Assert.Equal(before + 1, fake.Requests.Count);
}

[Fact]
public void ExchangeTypes_ContainPluralNinjaTypes()
{
    Assert.Contains("Fragments", TraderWebApi.ExchangeTypes);
    Assert.DoesNotContain("Fragment", TraderWebApi.ExchangeTypes);
    Assert.Equal(14, TraderWebApi.ExchangeTypes.Count);
}
```

- [ ] **Step 2: Run tests, verify FAIL**

Run: `dotnet test PBLEngine.Tests --filter "FullyQualifiedName~TraderWebApiTests"`
Expected: FAIL — `GetExchangeRatesAsync`/`ExchangeTypes` не существуют.

- [ ] **Step 3: Implement** в `TraderWebApi.cs`

Переименовать логику `GetCurrencyRatesAsync` в приватный fetch с ключом кэша `$"{league}|{type}"` (URL: `.../exchange/current/overview?type={Uri.EscapeDataString(type)}&league=...`); `GetCurrencyRatesAsync` → `GetExchangeRatesAsync(league, "Currency", ct)`. Список `ExchangeTypes` — статический массив 14 значений.

- [ ] **Step 4: Run tests, verify PASS** (та же команда; все существующие тесты TraderWebApi зелёные)

- [ ] **Step 5: Commit**

```bash
git add PBLApp.Core/Trader/TraderWebApi.cs PBLEngine.Tests/TraderWebApiTests.cs
git commit -m "feat(trader): TraderWebApi.GetExchangeRatesAsync — все 14 exchange-типов ninja, кэш (league|type)"
```

### Task 2: TraderWebApi — цены уников (`GetUniquePricesAsync`)

**Files:**
- Modify: `PBLApp.Core/Trader/TraderWebApi.cs`
- Test: `PBLEngine.Tests/TraderWebApiTests.cs`

**Interfaces:**
- Consumes: тот же `_http`, паттерн кэша Task 1.
- Produces:
  - `public sealed record UniquePriceEntry(string Name, string BaseName, double DivineValue, int ListingCount, bool Corrupted, IReadOnlyList<string> Mods, string? DetailsId)` — в этом же файле, namespace `PBLApp.Core.Trader`;
  - `public Task<IReadOnlyList<UniquePriceEntry>> GetUniquePricesAsync(string league, string type, string? query = null, CancellationToken ct = default)`;
  - `public static readonly IReadOnlyList<string> StashItemTypes` (9 значений из Global Constraints).

- [ ] **Step 1: Write the failing tests**

```csharp
private const string StashJson =
    """{"lines":[{"name":"Winter's Bite","baseType":"Glass Shank","primaryValue":1.25,
    "listingCount":7,"corrupted":false,"detailsId":"winters-bite-glass-shank",
    "explicitModifiers":[{"text":"Adds (8-10) to (15-18) Cold Damage"},{"text":"12% increased Attack Speed"}]},
    {"name":"Doom Fletch","baseType":"Blood Bow","primaryValue":0.4,"listingCount":22,"corrupted":true,
    "explicitModifiers":[{"optional":true}]}]}""";

[Fact]
public async Task GetUniquePrices_ParsesFieldsAndModTexts()
{
    // Responder: URL содержит "/stash/" → StashJson
    var api = new TraderWebApi(fake);
    var items = await api.GetUniquePricesAsync("Standard", "UniqueWeapons");
    Assert.Equal(2, items.Count);
    var first = items[0];
    Assert.Equal("Winter's Bite", first.Name);
    Assert.Equal(1.25, first.DivineValue, 6);
    Assert.Equal(7, first.ListingCount);
    Assert.False(first.Corrupted);
    Assert.Equal(["Adds (8-10) to (15-18) Cold Damage", "12% increased Attack Speed"], first.Mods);
    Assert.Null(items[1].Mods.LastOrDefault()); // элемент без text пропущен
    Assert.True(items[1].Corrupted);
}

[Fact]
public async Task GetUniquePrices_QueryFiltersCasedAndPostCache()
{
    var api = new TraderWebApi(fake);
    await api.GetUniquePricesAsync("Standard", "UniqueWeapons");   // прогрев кэша
    var before = fake.Requests.Count;
    var hit = await api.GetUniquePricesAsync("Standard", "UniqueWeapons", "glass shank");
    Assert.Single(hit);
    Assert.Equal("Winter's Bite", hit[0].Name);
    var byBase = await api.GetUniquePricesAsync("Standard", "UniqueWeapons", "DOOM");
    Assert.Single(byBase);
    Assert.Equal(before, fake.Requests.Count); // фильтр — по кэшу, без сети
}

[Fact]
public void StashItemTypes_AreTheNineNinjaUniqueCategories()
{
    Assert.Contains("UniqueWeapons", TraderWebApi.StashItemTypes);
    Assert.Equal(9, TraderWebApi.StashItemTypes.Count);
}
```

- [ ] **Step 2: Run tests, verify FAIL** (та же команда фильтра)

- [ ] **Step 3: Implement**

URL `.../stash/current/item/overview?type={type}&league={league}`; парс `lines[]`: `name`, `baseType`, `primaryValue` (TryGet double, 0 если нет), `listingCount` (int, 0), `corrupted` (bool, false), `explicitModifiers[]` → `text` (не-строки/без text пропускаются), `detailsId`. Кэш: словарь `_itemCache` ключ `league|type` — полный список; `query` фильтрует in-memory (OrdinalIgnoreCase по Name/BaseName).

- [ ] **Step 4: Run tests, verify PASS**

- [ ] **Step 5: Commit**

```bash
git add PBLApp.Core/Trader/TraderWebApi.cs PBLEngine.Tests/TraderWebApiTests.cs
git commit -m "feat(trader): TraderWebApi.GetUniquePricesAsync — цены уников ninja (stash overview), кэш+фильтр"
```

### Task 3: TraderSession — публичные обёртки цен

**Files:**
- Modify: `PBLApp.Core/TraderSession.cs` (после `RateFor`, ~строка 305)
- Test: `PBLEngine.Tests/TraderTabViewModelTests.cs` (сессия уже конструируется там с инжекцией)

**Interfaces:**
- Consumes: `_webApi` (поле, `TraderSession.cs:29`), `SelectedLeague`, `UniquePriceEntry` из Task 2.
- Produces:
  - `public Task<IReadOnlyDictionary<string, double>> GetExchangeRatesAsync(string type, CancellationToken ct = default)` — лига сессии;
  - `public Task<IReadOnlyList<UniquePriceEntry>> GetUniquePricesAsync(string type, string? query = null, CancellationToken ct = default)`;
  - обе: `string.IsNullOrEmpty(SelectedLeague)` → пустой результат **без сети**.

- [ ] **Step 1: Write the failing test** — сессия с `webApi: new TraderWebApi(fake)` (`fake` отвечает StashJson/NinjaJson как в Task 1/2); `session.SelectedLeague = "Standard"`; `await session.GetUniquePricesAsync("UniqueWeapons")` → 2 записи; `fake.Requests.Count` растёт ровно на 1 (LoadRates/лиги могут добавить свои — считать дельту вокруг вызова); сессия с `SelectedLeague = ""` → пусто и `Requests` не растёт.

- [ ] **Step 2: Run, verify FAIL**

Run: `dotnet test PBLEngine.Tests --filter "FullyQualifiedName~TraderTabViewModelTests"`

- [ ] **Step 3: Implement** — два one-liner-метода-делегата с guard'ом пустой лиги (см. Interfaces).

- [ ] **Step 4: Run, verify PASS** (оба фильтра Task 1–3)

- [ ] **Step 5: Commit**

```bash
git add PBLApp.Core/TraderSession.cs PBLEngine.Tests/TraderTabViewModelTests.cs
git commit -m "feat(trader): TraderSession.GetExchangeRatesAsync/GetUniquePricesAsync — цены через сессию (лига сессии)"
```

### Task 4: IPC `/items/bases`, `/items/affixes`, `/items/uniques`

**Files:**
- Modify: `PBLApp.Core/BuildPageViewModel.cs` (публичный доступ к хосту)
- Modify: `PBLApp/Ipc/IpcServer.cs` (роуты ~строка 160-215 + хендлеры рядом с items-блоком)

**Interfaces:**
- Consumes: `LuaHost.GetItemBaseCategories()`, `GetItemBasesForCategory(category)`, `GetItemAffixes(baseName)`, `GetItemCorruptedAffixes(baseName)`, `GetAllRunes()`, `GetUniqueItems()`; `BaseItemEntry(Name, Category, Implicit, LevelReq, Type, SocketCount)`, `AffixEntry(Id, AffixName, StatText, AffixType, Level, Group, Tier, TierCount)`, `RuneEntry(Name, SlotTypes, ModsByType, AugType)`, `UniqueItemEntry(Name, BaseName, Category, LookupKey)`.
- Produces: `public LuaHost? Host => _host;` на `BuildPageViewModel`; IPC-роуты (POST, тела — JSON):
  - `/items/bases` `{category?}` → без category: `{categories:[…]}`; с category: `{category, bases:[{name,category,implicit,levelReq,type,socketCount}]}`; неизвестная категория → `{error, categories}`.
  - `/items/affixes` `{base}` → `{base, affixes:[{id,name,statText,type,level,group,tier,tierCount}], corruption:[…], runes:[{name,slotTypes,modsByType,augType}]}` (руны глобальные, не базовые — полный `GetAllRunes()` каждый раз); неизвестная база (пустой пул и коррапшна) → `{error, bases?}` (substring-подсказка по категориям).
  - `/items/uniques` `{query?}` → `{uniques:[{name,base,category,lookupKey}]}` — substring OrdinalIgnoreCase по Name/BaseName.

- [ ] **Step 1:** Добавить `public LuaHost? Host => _host;` в `BuildPageViewModel.cs` (рядом с `TraderSession`).
- [ ] **Step 2:** Зарегистрировать 3 роута в switch `IpcServer` рядом с `/items/*`; хендлеры — `OnUi` (синхронные Lua-вызовы), `GetTraderSession()`-стиль получения страницы: `GetMainVm()?.CurrentPage as BuildPageViewModel`, null → `{error:"No build page open."}`, `Host` null → `{error:"Engine not ready."}`.
- [ ] **Step 3: Build**

Run: `dotnet build Poe2BuildLab.sln`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add PBLApp.Core/BuildPageViewModel.cs PBLApp/Ipc/IpcServer.cs
git commit -m "feat(ipc): /items/bases|affixes|uniques — базы, пулы афиксов с тирами, поиск уник"
```

### Task 5: IPC `/prices/currency`, `/prices/uniques`

**Files:**
- Modify: `PBLApp/Ipc/IpcServer.cs`

**Interfaces:**
- Consumes: Task 3 (`TraderSession.GetExchangeRatesAsync/GetUniquePricesAsync`), Task 1/2 (`TraderWebApi.ExchangeTypes/StashItemTypes`), `GetTraderSession()` (`IpcServer.cs:1860`).
- Produces (POST, тела — JSON):
  - `/prices/currency` `{type?="Currency", query?}` → `{league, type, rates:[{name,value}]}` (query — substring по name); type не из whitelist → `{error:"type must be one of: …"}`; сессии нет → `{error:"Trader session not created."}`.
  - `/prices/uniques` `{type?="UniqueArmours", query?}` → `{league, type, items:[{name,base,price,listingCount,corrupted,mods}]}`; та же валидация.

- [ ] **Step 1:** Зарегистрировать 2 роута (хендлеры асинхронные — `OnUiAsync`); валидация type по статическим спискам до вызова сессии; query-фильтрация по месту.
- [ ] **Step 2: Build** — `dotnet build Poe2BuildLab.sln`, Build succeeded.
- [ ] **Step 3: Commit**

```bash
git add PBLApp/Ipc/IpcServer.cs
git commit -m "feat(ipc): /prices/currency|uniques — курсы ninja и цены уников с whitelist-валидацией типов"
```

### Task 6: MCP-инструменты + документация

**Files:**
- Modify: `PBLMcp/VisualTools.cs`
- Modify: `docs/mcp.md`

**Interfaces:**
- Consumes: IPC-роуты Task 4/5; паттерн обёрток `VisualTools` (`IpcClient.CallAsync` + `Error(ex)`, например `VisualItemsCompatibleSlots`).
- Produces: `visual_item_bases(category?)`, `visual_item_affixes(base)`, `visual_item_uniques(query?)`, `visual_prices_currency(type?, query?)`, `visual_prices_uniques(type?, query?)`.

- [ ] **Step 1:** 5 инструментов с самодостаточными `Description` (что возвращает, когда какой): уник по имени → `visual_item_uniques` затем `visual_prices_uniques`; сравнение вариантов rares → `visual_item_affixes` + рассуждение по тирам; точная цена конкретного предмета → трейд-флоу `/trader/search`+`/trader/state` (нужен логин, `loggedIn` в стейте); `visual_prices_currency` — бюджет в дивинах.
- [ ] **Step 2:** `docs/mcp.md` — каталог + 3 рецепта: «оцени бюджет билда» (уники из ninja + валюта на крафт), «найди дешёвый уник в слот» (`visual_item_uniques` → `visual_prices_uniques` → примерка через трейд), «сколько стоит такой редкий предмет» (трейд-флоу с весами; ограничение: OAuth-логин пользователя, ошибка при `loggedIn:false`).
- [ ] **Step 3: Build + smoke**

Run: `dotnet build Poe2BuildLab.sln`
Expected: Build succeeded. (Каталог MCP проверить e2e-проходом агента после запуска клиента.)

- [ ] **Step 4: Commit**

```bash
git add PBLMcp/VisualTools.cs docs/mcp.md
git commit -m "feat(mcp): visual_item_* и visual_prices_* — существование вещей, пулы афиксов, цены"
```
