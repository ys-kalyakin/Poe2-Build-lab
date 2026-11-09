# MCP Build/Improve Gaps Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Закрыть 8 гэпов MCP-агента для полного цикла «построить/улучшить билд»: каталог гемов, diff статов, DPS по скилам, валидация билда, импорт XML, ilvl-гейт крафта, weapon set'ы.

**Architecture:** Три слоя на каждый гэп: метод/record в `PBLEngine` (LuaHost поверх существующих Lua-данных) → HTTP-эндпоинт в `PBLApp/Ipc/IpcServer.cs` → MCP-инструмент в `PBLMcp/VisualTools.cs`. Чистые алгоритмы (дифф, матчинг имён) — в `PBLApp.Core`, чтобы тестировались без Avalonia. Мутации остаются лёгкими; diff/dps — отдельные read'ы.

**Tech Stack:** .NET 9, C# records, Avalonia (UI-поток для IPC), xUnit + LuaHostFixture, Lua-данные из `src/Data`.

**Spec:** `docs/superpowers/specs/2026-11-09-mcp-build-improve-gaps-design.md`

## Global Constraints

- Тесты: `dotnet test PBLEngine.Tests/PBLEngine.Tests.csproj`; LuaHost-тесты только через `LuaHostFixture` (`[Collection("LuaHost")]`) — инициализация ~40 с, свой `new LuaHost()` в тестах запрещён.
- DTO-records — в `PBLEngine/ConfigOptionDto.cs`; IPC-ответы — анонимные объекты camelCase, успех `{ok=true,...}`, ошибка `{error="..."}`; доступ к движку из IpcServer — паттерн `ItemDataHost()` (IpcServer.cs:1866).
- MCP-инструменты — обёртки в `PBLMcp/VisualTools.cs` по образцу существующих `visual_*` (IpcClient POST json / GET query).
- Только аддитивные изменения; существующая семантика маршрутов/инструментов не меняется.
- Скилл-индексы 1-based (существующее правило адресации).
- Коммиты: префиксы `feat(mcp)/feat(ipc)/feat(engine)/test` как в истории ветки; в каждый коммит — только файлы задачи.
- docs/mcp.md правится в задаче своего инструмента (русский, стиль существующих разделов).

## Review Focus

1. **Опечатка имени гема** — агент передаёт «Fire Ball» вместо «Fireball»: должен получить ошибку с подсказками, а не молча битую группу. Пин: `GemNameMatcherTests` (Task 2).
2. **Diff между разными билдами** — снапшот от билда A, открыли билд B: сравнение бессмысленно. Ожидание: снапшоты стираются при `/open-build` и `/build/import`. Пин: шаги очистки в Task 3/6 + code-review (IPC-хендлеры headless-тестом не покрываются).
3. **Смешанные типы статов** (`double` vs `string` vs `bool`, одно и то же число «12» vs 12) — дифф не должен падать и не должен рапортовать изменение там, где значение равно. Пин: `StatsDiffTests` (Task 3).
4. **Валидация на пустом билде** — у свежего `NewBuild()` часть таблиц `mainEnv` может отсутствовать; `/build/validate` не должен кидать и должен вернуть форму. Пин: `BuildValidationTests` (Task 5).
5. **ilvl-гейт меняет поведение UI-редактора** — дефолт ItemLevel=80 теперь режет тиры с Level>80 для существующих пользователей. Ожидание: отказ с внятной причиной (как поднять), не молчаливое исчезновение из списка. Пин: VM-тест (Task 7) + примечание в docs/mcp.md.

---

### Task 0: WIP-снапшот ветки

**Files:** существующие грязные: `PBLApp.Core/Trader/TraderWebApi.cs`, `PBLApp/Ipc/IpcServer.cs`, `PBLEngine.Tests/TraderTabViewModelTests.cs`, `PBLEngine.Tests/TraderWebApiTests.cs`, `PBLMcp/VisualTools.cs`, `docs/mcp.md`, `PBLEngine.Tests/ItemDataTests.cs` (untracked).

- [ ] **Step 1: Проверить, что грязные файлы — только ожидаемые**

Run: `git status --porcelain`
Expected: перечисленные выше файлы, ничего больше.

- [ ] **Step 2: Закоммитить как есть (решение пользователя — отдельный WIP-коммит)**

```bash
git add PBLApp.Core/Trader/TraderWebApi.cs PBLApp/Ipc/IpcServer.cs PBLEngine.Tests/TraderTabViewModelTests.cs PBLEngine.Tests/TraderWebApiTests.cs PBLMcp/VisualTools.cs docs/mcp.md PBLEngine.Tests/ItemDataTests.cs
git commit -m "chore(item-economy): follow-up правки после ревью (wip-снапшот)"
```

- [ ] **Step 3: Убедиться, что тесты на старте зелёные**

Run: `dotnet test PBLEngine.Tests/PBLEngine.Tests.csproj`
Expected: PASS (это база для всех последующих задач).

---

### Task 1 (P1a): `GemCatalogEntry` + `LuaHost.GetGemCatalog()`

**Files:**
- Modify: `PBLEngine/ConfigOptionDto.cs` (новый record рядом с `FullDpsSkillEntry`, :24)
- Modify: `PBLEngine/LuaHost.cs` (новый метод рядом с `GetGemTooltipByName`, :740-865)
- Create: `PBLEngine.Tests/GemCatalogTests.cs`

**Interfaces:**
- Produces: `public sealed record GemCatalogEntry(string Name, string BaseTypeName, bool Support, IReadOnlyList<string> Tags, string TagString, string GemType, int ReqStr, int ReqDex, int ReqInt, int NaturalMaxLevel, int Tier, string? WeaponRequirements, double ManaCost, string? Description)`; `public List<GemCatalogEntry> GetGemCatalog()` — потребляется Task 2.

- [ ] **Step 1: Тест**

```csharp
[Collection("LuaHost")]
public class GemCatalogTests : IClassFixture<LuaHostFixture>
{
    private readonly LuaHost _host;
    public GemCatalogTests(LuaHostFixture fixture) => _host = fixture.Host;

    [Fact] public void Catalog_NonEmpty_AllNamesKnown()
    {
        var gems = _host.GetGemCatalog();
        Assert.NotEmpty(gems);
        Assert.All(gems, g => Assert.False(string.IsNullOrWhiteSpace(g.Name)));
        Assert.Contains(gems, g => g.Support);           // саппорты есть
        Assert.Contains(gems, g => !g.Support);          // активы есть
    }

    [Fact] public void Fireball_PresentWithMetadata()
    {
        var fb = Assert.Single(_host.GetGemCatalog(), g => g.Name == "Fireball");
        Assert.False(fb.Support);
        Assert.True(fb.ReqInt > 0);
        Assert.True(fb.ManaCost > 0);
        Assert.False(string.IsNullOrEmpty(fb.TagString));
    }
}
```

- [ ] **Step 2: Запустить, убедиться в падении** (`GetGemCatalog` не существует).

Run: `dotnet test PBLEngine.Tests/PBLEngine.Tests.csproj --filter GemCatalogTests`
Expected: FAIL (compile error / method missing).

- [ ] **Step 3: Реализация**

Record — в `ConfigOptionDto.cs`. `GetGemCatalog()` — один `State.DoString`, образцы чтения полей — внутри `GetGemTooltipByName` (LuaHost.cs:756-841): итерация `data.gems`; `tags` = ключи `gem.tags`; мана — `gem.grantedEffect.levels[gem.naturalMaxLevel].cost.Mana`; `support` из `gem.grantedEffect.support`; сортировка по Name. Отсутствующие поля — `0/""/null`, не ошибки.

- [ ] **Step 4: Тест зелёный**

Run: `dotnet test PBLEngine.Tests/PBLEngine.Tests.csproj --filter GemCatalogTests`
Expected: PASS.

- [ ] **Step 5: Commit** — `git add` двух файлов движка + тест; `feat(engine): GetGemCatalog — каталог гемов data.gems с тегами/требованиями/маной`

---

### Task 2 (P1b): матчинг имён, IPC `/skills/gem-catalog` + `/skills/gem-tooltip`, валидация `add-group`, MCP

**Files:**
- Create: `PBLApp.Core/GemNameMatcher.cs`, `PBLEngine.Tests/GemNameMatcherTests.cs`
- Modify: `PBLApp/Ipc/IpcServer.cs` (роуты :188-196; хендлеры рядом с `SkillsList` :822; валидация в `SkillsAddGroup` :828-835), `PBLMcp/VisualTools.cs`, `docs/mcp.md`

**Interfaces:**
- Consumes: `GetGemCatalog()` (Task 1).
- Produces: `public static class GemNameMatcher { public static string? FindExact(string name, IEnumerable<string> all); public static string[] Suggest(string name, IEnumerable<string> all, int max = 5); }`; IPC `GET /skills/gem-catalog?query=&type=active|support&tag=&weapon=` (кап 400, поле `truncated`), `POST /skills/gem-tooltip {gemName, level?=20}`; MCP `visual_gems_list {query?, type?, tag?, weapon?}`, `visual_gem_tooltip {gemName, level?}`.

- [ ] **Step 1: Тест матчера**

Кейсы: `FindExact("fireball", ["Fireball"]) == "Fireball"`; `FindExact("нет такого", ...)` → null; `Suggest("fire bal", ["Fireball","Fire Trap"])` содержит "Fireball"; `Suggest` по подстроке ("cold" → только содержащие "cold"); результат ≤ max и детерминирован (повторный вызов — тот же порядок).

- [ ] **Step 2: Падение** — `dotnet test --filter GemNameMatcherTests` → FAIL.

- [ ] **Step 3: Реализация матчера** — точное совпадение OrdinalIgnoreCase; Suggest: подстрока-совпадения первыми (по возрастанию длины), затем совпадения по словам; cap `max`.

- [ ] **Step 4: Тест матчера зелёный.**

- [ ] **Step 5: IPC-хендлеры и валидация add-group**

`/skills/gem-catalog`: `ItemDataHost().GetGemCatalog()`, фильтры query (подстрока Name/TagString), type (`active`→Support=false, `support`→true), tag (Tags содержит, ignore case), weapon (WeaponRequirements содержит); кап 400 записей + `truncated=true`. Статический кэш каталога в поле хендлера (каталог статичен в рамках версии данных).
`/skills/gem-tooltip`: `ItemDataHost().GetGemTooltipByName(gemName, level)`.
`SkillsAddGroup`: перед `AddGroupFromPicker` — имя должно точечно резолвиться (`GemNameMatcher.FindExact` по объединённым Active+Support из `GetAvailableGemNames()`), иначе `{error, suggestions}` — и группа не создаётся.

- [ ] **Step 6: MCP-инструменты** — `visual_gems_list`, `visual_gem_tooltip` в `VisualTools.cs` по образцу `visual_item_bases`; описание инструмента: «какие гемы существуют в данных приложения; перед add_group/add_gem проверяй имя здесь».

- [ ] **Step 7: docs/mcp.md** — раздел «Каталог гемов» в блоке «Скилы и гемы»: фильтры, пример, фикс про валидацию add-group.

- [ ] **Step 8: Полный тест-прогон зелёный; Commit** — `feat(mcp): visual_gems_list/visual_gem_tooltip + валидация имён гемов в add-group`

---

### Task 3 (P2): `StatsDiff`, `/stats/snapshot`, `/stats/diff`, MCP

**Files:**
- Create: `PBLApp.Core/StatsDiff.cs`, `PBLEngine.Tests/StatsDiffTests.cs`
- Modify: `PBLApp/Ipc/IpcServer.cs` (хранилище снапшотов — поле; роуты рядом с `/stats` :207; очистка в `OpenBuild` :377-398), `PBLMcp/VisualTools.cs`, `docs/mcp.md`

**Interfaces:**
- Produces: `public sealed record StatChange(string Key, object? From, object? To); public sealed record StatsDiffResult(List<StatChange> Changed, List<StatChange> Added, List<string> Removed); public static class StatsDiff { public static StatsDiffResult Compute(IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after); }` — потребляется Task 6 (очистка хранилища).
- IPC: `POST /stats/snapshot {name?}` → `{ok, name, keyCount}` (имя по умолчанию `last`); `GET /stats/diff?from=&to=` (`from` по умолчанию `last`, `to` по умолчанию текущие статы). MCP: `visual_stats_snapshot {name?}`, `visual_stats_diff {from?, to?}`.

- [ ] **Step 1: Тест диффа**

Кейсы: число изменилось → `Changed`; `12` (string) vs `12` (double) → пусто (равно); `"12"` vs `13` (double) → `Changed` (сравнение строковой формой InvariantCulture, не исключение); ключ появился → `Added`; исчез → `Removed`; одинаковые словари → все списки пусты.

- [ ] **Step 2: Падение** → **Step 3: Реализация** (`Convert.ToString(..., CultureInfo.InvariantCulture)` обе стороны при несовпадении типов) → **Step 4: зелёный**.

- [ ] **Step 5: IPC**: хранилище `Dictionary<string, Dictionary<string, object?>>` в статическом поле IpcServer (одна сессия = один токен); snapshot — копия `bp.Build.AllStats` (значения-примитивы, копия словаря достаточна); diff — `StatsDiff.Compute`; отсутствующий снапшот-имя → `{error}` с доступными именами; **очистка хранилища в `OpenBuild`** (и в `/build/import` — Task 6).

- [ ] **Step 6: MCP-инструменты** + **Step 7: docs/mcp.md** — обновить рецепт «Сравнить два предмета»: snapshot → equip → diff вместо ручного сравнения.

- [ ] **Step 8: Полный прогон; Commit** — `feat(mcp): visual_stats_snapshot/diff — сравнение статов до/после примерки`

---

### Task 4 (P3): `/skills/dps` + MCP

**Files:**
- Modify: `PBLApp/Ipc/IpcServer.cs` (роут рядом с `/skills/list` :189; хендлер рядом с `SkillsList`), `PBLMcp/VisualTools.cs`, `docs/mcp.md`
- Create: `PBLEngine.Tests/SkillsDpsTests.cs`

**Interfaces:**
- Consumes (уже есть в движке, ничего не менять): `LuaHost.GetFullDpsBreakdown()` → `List<FullDpsSkillEntry(Name, Dps, Count)>` (LuaHost.cs:410); `LuaHost.GetSkillGroups()` → `List<SkillGroupEntry>` (LuaHost.cs:200-255); `LuaHost.GetMainSkillGroupIndex()` (LuaHost.cs:514); `BuildModel.AllStats` (`FullDPS`, `TotalDPS`).
- Produces: IPC `GET /skills/dps` → `{fullDps, totalDps, skills:[{name, dps, count}], groups:[{index, name, isMain, includeInFullDps}]}`; MCP `visual_skills_dps`.

- [ ] **Step 1: Тест** (LuaHostFixture): свежий билд → `GetFullDpsBreakdown()` пуст, `GetSkillGroups()` пуст; `AddSkillGroupWithGem("Fireball")` → `GetSkillGroups().Count == 1`; `GetFullDpsBreakdown()` по-прежнему валидный список (группа не в Full DPS до включения флага).

- [ ] **Step 2: Падение** → **Step 3: хендлер** (композиция перечисленного, `isMain = index == mainIdx`) → **Step 4: зелёный**.

- [ ] **Step 5: MCP `visual_skills_dps`** — описание: «DPS по скилам; группа не видна — включи includeInFullDps через visual_skills_set_group и перечитай».

- [ ] **Step 6: docs/mcp.md** — раздел «DPS по скилам» + recipe. **Commit** — `feat(mcp): visual_skills_dps — разбивка Full DPS по скилам`

---

### Task 5 (P4): `GetBuildValidation`, `/build/validate`, MCP

**Files:**
- Modify: `PBLEngine/ConfigOptionDto.cs`, `PBLEngine/LuaHost.cs` (метод рядом с `GetPointUsage` :1009)
- Modify: `PBLApp/Ipc/IpcServer.cs` (роут рядом с `/build/save` :208), `PBLMcp/VisualTools.cs`, `docs/mcp.md`
- Create: `PBLEngine.Tests/BuildValidationTests.cs`

**Interfaces:**
- Produces: `public sealed record ValidationIssue(string Code, string Message, string? Source); public sealed record DisabledSkillEntry(int GroupIndex, string GemName, string Reason); public sealed record BuildValidation(PointUsage Points, List<ValidationIssue> Issues, List<DisabledSkillEntry> DisabledSkills);` + `public BuildValidation GetBuildValidation()`.
- Коды Issues (стабильный контракт): `attrs` (Req\* > \*; Source — `ReqStrItem.source` если есть), `pool` (Unreserved Life/Mana/Spirit < 0), `cost` (LifeCost/ManaCost/RageCost/ESCostWarning), `item` (`mainEnv.itemWarnings` — строки), `points` (overBudget пассивок/асцензии/weapon set по CountAllocNodes). Disabled skills — из `env.player.activeSkillList`: `skillFlags.disable` + `disableReason`.

- [ ] **Step 1: Тест** (LuaHostFixture): свежий билд → `GetBuildValidation()` не null; `Points` не null; `Issues`/`DisabledSkills` не null, все `Code`/`Reason` непустые; после `AddSkillGroupWithGem("Fireball")` — форма та же, исключений нет.

- [ ] **Step 2: Падение** → **Step 3: реализация** — один Lua-сниппет, читает уже посчитанное в `mainOutput`/`mainEnv` (ничего не пересчитывать; образцы доступа — `GetPointUsage` LuaHost.cs:1009-1038; чтение таблиц с проверкой nil); → **Step 4: зелёный**.

- [ ] **Step 5: IPC `GET /build/validate`** → `{ok, points, warnings, disabledSkills}`; **MCP `visual_build_validate`** — описание: «запускай после мутаций шмота/дерева: несходящиеся атрибуты, отрицательные пулы, отключённые скилы».

- [ ] **Step 6: docs/mcp.md** + recipe «После изменений — проверь билд». **Commit** — `feat(mcp): visual_build_validate — предупреждения билда из mainEnv/mainOutput`

---

### Task 6 (P5): `/build/import`, MCP `visual_build_import`

**Files:**
- Modify: `PBLApp/Ipc/IpcServer.cs` (роут рядом с `/build/xml` :209; хендлер по образцу `NotesSet` :2163), `PBLMcp/VisualTools.cs`, `docs/mcp.md`
- Create: `PBLEngine.Tests/BuildImportTests.cs`

**Interfaces:**
- Consumes: `PBLEngine.BuildCodec.Decode/Encode` (BuildCodec.cs:26-45, public static); `BuildModel.LoadBuildFromXml(xml, name)` (BuildModel.cs:177); `BuildPageViewModel.SaveNow()`.
- Produces: IPC `POST /build/import {xml|code, name?, save?}` → `{ok, name, saved}`; очистка `_statSnapshots` (Task 3). MCP `visual_build_import {xml|code, name?, save?}` (save по умолчанию false).

- [ ] **Step 1: Тесты** (LuaHostFixture): (a) BuildCodec roundtrip — `Decode(Encode(xml)) == xml` на строке с `<PathOfBuilding`; (b) движок: `xml = host.SaveBuildToXml()`; `host.LoadBuildFromXml(xml, "RoundTrip")`; `host.GetAllStats().Count > 0`; `xml.Contains("PathOfBuilding")`.

- [ ] **Step 2: Падение** → **Step 3: хендлер**: `code` → `BuildCodec.Decode`; вход без `<PathOfBuilding` → `{error}`; `LoadBuildFromXml(xml, name ?? "Imported Build")`; рефреш вкладок по образцу `BuildPageViewModel.cs:145` (`CalcsTab?.Refresh(); SkillsTab?.Refresh(); ItemsTab?.Refresh()` — имена полей проверить по файлу); `save` → `bp.SaveNow()`; очистить снапшоты; → **Step 4: зелёный**.

- [ ] **Step 5: MCP `visual_build_import`** — описание: «загрузить чужой билд (XML или PoB share code с poe.ninja/форума) как текущий; save:false не перезаписывает файл пользователя».

- [ ] **Step 6: docs/mcp.md** + recipe «Взять мета-билд за основу». **Commit** — `feat(mcp): visual_build_import — импорт XML/share code в живое приложение`

---

### Task 7 (P7): ilvl-гейт крафта

**Files:**
- Modify: `PBLApp.Core/ItemEditorViewModel.cs` (`CanAddAffix` :1272-1281), `PBLApp/Ipc/IpcServer.cs` (`EditorAddAffix` :1342-1357; новый роут `/items/editor-set-item-level` рядом с :153), `PBLMcp/VisualTools.cs`, `docs/mcp.md`
- Create: `PBLEngine.Tests/ItemLevelGateTests.cs`

**Interfaces:**
- Consumes: `AffixEntry` (ConfigOptionDto.cs:100, поле `Level` уже есть); `ItemEditorViewModel.ItemLevel` (:428, дефолт 80).
- Produces: IPC `POST /items/editor-set-item-level {level}` (clamp 1..100) → `{ok, itemLevel}`; MCP `visual_editor_set_item_level {level}`; отказ `EditorAddAffix` с причиной тира.

- [ ] **Step 1: Тест** (конструирует `new ItemEditorViewModel(host, (_,_) => { })` — ссылка на PBLApp.Core в тестах есть): `ItemLevel = 80`; `CanAddAffix(new AffixEntry(Id:"t", AffixName:"T", StatText:"S", AffixType:"Prefix", Level:90, Group:"G", Tier:1, TierCount:8))` → false; `ItemLevel = 95` → true (свежий VM: слоты префиксов/суффиксов свободны, ExplicitMods пуст); граница: `Level == ItemLevel` → true.

- [ ] **Step 2: Падение** → **Step 3: реализация**: гейт `if (e.Level > ItemLevel) return false;` в `CanAddAffix` (после проверок слотов); в `EditorAddAffix` при отказе из-за уровня — `{error = "tier level 90 requires item level ≥ 90 (current 80) — call visual_editor_set_item_level"}`; новый хендлер set-item-level по образцу `EditorSetQuality` :1247-1254 (clamp 1..100); → **Step 4: зелёный**.

- [ ] **Step 5: MCP `visual_editor_set_item_level`; docs/mcp.md** — в «Предметы»: ilvl влияет на доступные тиры (`visual_item_affixes` отдаёт `level` тира), пул афиксов уже отфильтрован по spawn-маске базы (все перечисленные могут выпасть); примечание про изменение поведения UI-редактора (Review Focus 5). **Commit** — `feat(mcp): item level в редакторе + гейт тиров афиксов по ilvl`

---

### Task 8 (P8): weapon set'ы

**Files:**
- Modify: `PBLEngine/LuaHost.cs` (`SetAllocMode`, `GetAllocatedNodeModes`, `SetGroupSlot`), `PBLEngine/ConfigOptionDto.cs` (`SkillGroupEntry` += `Slot`), `PBLApp.Core/TreeTabViewModel.cs` (`AllocatePathTo` :584-623, `DeallocateNode` :627-646 — опц. параметр), `PBLApp/Ipc/IpcServer.cs` (`TreeAllocated` :1566 += allocMode per node; роут `/items/select-weapon-set`; `SkillsSetGroup` :911 += weaponSet; `SkillsListing` :783 += slot), `PBLMcp/VisualTools.cs`, `docs/mcp.md`
- Create: `PBLEngine.Tests/WeaponSetTests.cs`

**Interfaces:**
- Produces: `public void SetAllocMode(int mode)` (0/1/2, Lua `build.spec.allocMode`); `public Dictionary<int,int> GetAllocatedNodeModes()` (spec.allocNodes → node.allocMode); `public void SetGroupSlot(int groupIndex, string? slot)` (nil/`"Weapon 1"`/`"Weapon 1 Swap"` + ProcessSocketGroup + recalc); `TreeTabViewModel.AllocatePathTo(int nodeId, string? attribute = null, int weaponSet = 0)` и `DeallocateNode(int nodeId, int weaponSet = 0)` — ставят allocMode до операции и возвращают 0 после; guard: деаллокация узла с allocMode==0 из режима set → отказ (семантика UI, PassiveTreeView.lua:2036).
- MCP: `weaponSet` параметр в `visual_tree_allocate_path` / `visual_tree_deallocate_node`, новый `visual_items_select_weapon_set {set}`, `weaponSet` в `visual_skills_set_group` (1 → slot "Weapon 1", 2 → "Weapon 1 Swap", 0 → снять).

- [ ] **Step 1: Тест** (LuaHostFixture): из `GetTreeData` взять id первого не-keystone/не-socket узла; `SetAllocMode(1)`; `AllocNode(id)`; `GetPointUsage().WeaponSet1Used >= 1`; `DeallocNode(id)`; `WeaponSet1Used == 0`; `SetAllocMode(0)`. Негатив: `SetAllocMode(3)` → исключение/отказ (валидация 0..2).

- [ ] **Step 2: Падение** → **Step 3: движок** (SetAllocMode/GetAllocatedNodeModes/SetGroupSlot — DoString по образцам `AllocNode` :2598 и `GetSkillGroups` :200) → **Step 4: зелёный**.

- [ ] **Step 5: VM + IPC**: параметр weaponSet в AllocatePathTo/DeallocateNode (+guard), allocMode в `TreeAllocated`, `/items/select-weapon-set` (команды `SelectWeaponSet1Command/2Command` — ItemsTabViewModel.cs:456-460), weaponSet в `/skills/set-group` через `SetGroupSlot`, `slot`/`slotEnabled` в `SkillsListing` (slot из `GetSkillGroups`, сопоставление по индексу).

- [ ] **Step 6: MCP-обёртки + docs/mcp.md** — раздел «Weapon set'ы»: бюджет ws1/ws2 уже в `/tree/allocated` points, per-node allocMode, привязка групп к сетам. **Step 7: полный прогон; Commit** — `feat(mcp): weapon set'ы — аллокация узлов и привязка скил-групп к set 1/2`

---

### Task 9: дизайн-док — правка P6 + финальный прогон

**Files:**
- Modify: `docs/superpowers/specs/2026-11-09-mcp-build-improve-gaps-design.md`

- [ ] **Step 1: Правка P6 в дизайн-доке**: разведка показала, что `GetItemAffixes` уже фильтрует пул по `weightVal > 0` (LuaHost.cs:2013-2015) — поле spawnWeight было бы константой 1. P6 снимается как код: реалистичность крафта обеспечивают (а) pre-filtered пул (все перечисленные афиксы могут выпасть) и (б) гейт `tier Level ≤ ilvl` из Task 7. Зафиксировать в доке как «решено при планировании».

- [ ] **Step 2: Полный прогон** — `dotnet test PBLEngine.Tests/PBLEngine.Tests.csproj` + `dotnet build PBLMcp/PBLMcp.csproj -c Release`. Expected: зелёные.

- [ ] **Step 3: Commit** — `docs(spec): P6 spawnWeight снят — пул афиксов уже отфильтрован по spawn-маске`

---

## Self-Review

- **Spec coverage:** P1 → Tasks 1-2; P2 → 3; P3 → 4; P4 → 5; P5 → 6; P6 → Task 9 (снят, правка дока) + Task 7 (docs); P7 → 7; P8 → 8; docs/mcp.md — в каждой задаче. Гэп не найден.
- **Step scan:** каждый шаг — одно действие; тела даны только там, где сигнатура+тест не определяют (Lua-сниппеты — по образцам с точными ссылками на строки).
- **Type consistency:** `GemCatalogEntry`/`GetGemCatalog` (T1→T2), `StatsDiffResult`/`_statSnapshots` (T3→T6), `BuildValidation` (T5), `AffixEntry.Level`/`ItemLevel` (T7), `SetAllocMode`/`GetAllocatedNodeModes`/`SetGroupSlot` (T8) — совпадают.
- **Review Focus:** 1→T2, 2→T3+T6, 3→T3, 4→T5, 5→T7. Все запинены тестами либо (IPC-only п.2) явным review-гейтом.
- **Proportion:** план ~2 экрана на задачу; тела без алгоритмической необходимости опущены.
