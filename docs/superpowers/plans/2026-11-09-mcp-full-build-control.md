# MCP Full Build Control — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Внешний ИИ-агент по MCP может полностью строить и управлять билдом в живом PBLApp: дерево, скилы, статы, конфиг, сохранение.

**Architecture:** Новые IPC-эндпоинты в `PBLApp/Ipc/IpcServer.cs` (все через `OnUi`, мутации — на уровне существующих ViewModel) + тонкие MCP-обёртки в `PBLMcp/VisualTools.cs`. Логика пути по дереву — чистый `TreePathFinder` в PBLApp.Core; «синхронная правда движка» — публичный дрен-хук в `TreeTabViewModel`.

**Tech Stack:** .NET 9, Avalonia MVVM (CommunityToolkit), NLua engine (PBLEngine), ModelContextProtocol SDK, xUnit + `LuaHostFixture`.

**Spec:** `docs/superpowers/specs/2026-11-09-mcp-full-build-control-design.md`

## Global Constraints

- Linux-first: ничего не завязывать на `PBLApp.exe`; запускается и Windows (exe), и Linux (`dotnet PBLApp.dll`).
- IPC-обработчики — на UI-потоке: `await OnUi(...)` / `OnUiAsync(...)`, паттерн как у существующих.
- Мутации — только через существующие ViewModel-методы; новые VM-методы публичные.
- Индексы движка 1-based везде в `/skills/*`; каждый мутационный ответ возвращает освежённый срез листинга.
- Никаких молчаливых частичных аллокаций: перебор бюджета разрешён (как UI), но ответ всегда содержит `overBudget`/`pointsLeft`.
- Описание MCP-инструмента ≤ 2 строк, самодостаточное.
- Стиль кода репозитория: file-scoped namespaces, 4 пробела, primary constructors где принято (см. `VisualTools.cs`).
- Тесты с LuaHost — `[Collection("LuaHost")]` + `IClassFixture<LuaHostFixture>`; LuaHost init ~40 с — новые тесты класть в существующие коллекции по возможности.

## Review Focus

1. **Путь через атрибутивный узел при живом окне** — диалог выбора атрибута должен НЕ открываться из IPC; авто-выбор дефолта движка. Пин: Task 2, тест `AllocatePathTo_ThroughAttributeNode_AutoPicks_NoDialog`.
2. **Сдвиг индексов после удаления группы/гема** — агент обязан переадресовываться; листинг в каждом ответе. Пин: Task 4, тест `RemoveGroup_Middle_IndiciesShift`.
3. **Стат после мутации не врёт** — эндпоинт отвечает только после дренa очереди Lua и reconcile. Пин: Task 2, тест `WaitForEngineSettled_ReconcilesFromLua`.
4. **Перебор очков громкий** — аллокация разрешена, но `overBudget`/`pointsLeft` в ответе. Пин: Task 2, тест `AllocatePathTo_OverBudget_ReportsPointsLeft`; Task 3 `/tree/allocate-path` прокидывает в JSON.
5. **Linux-запуск клиента** — нет `PBLApp.exe`, запуск через `dotnet PBLApp.dll`. Пин: Task 7 (код-ревью), Task 9 (smoke `/ping`).
6. **Granted-группы (от узлов дерева) нельзя удалять** — `/skills/remove-group` внятная ошибка. Пин: Task 4, тест `RemoveGroup_Granted_ReturnsError`.

---

### Task 1: TreePathFinder — чистый BFS пути по дереву

**Files:**
- Create: `PBLApp.Core/TreePathFinder.cs`
- Modify: `PBLApp.Core/TreeTabViewModel.cs` (метод `PredictAllocPath`, ~строки 461-496 — тело заменить делегированием)
- Test: `PBLEngine.Tests/TreePathFinderTests.cs`

**Interfaces:**
- Produces: `public static class TreePathFinder` с методом
  `public static List<int> FindPath(IReadOnlyDictionary<int, TreeNodeDto> nodesById, IReadOnlyCollection<int> allocatedIds, int targetId)`
  — путь ВКЛЮЧАЯ target, ИСКЛЮЧАЯ уже занятые; пустой список если unreachable / target уже занят / target не найден. Границу main-tree ↔ восхождение не пересекать (семантика текущего `PredictAllocPath`: сосед по `LinkedIds` с другим «наличием AscendancyName» отсекается).

- [ ] **Step 1: Write the failing tests**

В `TreePathFinderTests.cs` (без LuaHostFixture — чистые record'ы):

```csharp
private static TreeNodeDto N(int id, int[] links, string asc = "") =>
    new(id, 0, 0, "Normal", $"Node{id}", [], links, asc, "", "", "", "", 0, false);

[Fact] FindPath_StraightLine_ReturnsUnallocatedTailOnly:
    nodes 1-2-3, allocated {1}, target 3 → [2,3]
[Fact] FindPath_AlreadyAllocated_ReturnsEmpty: allocated {1,2}, target 2 → []
[Fact] FindPath_UnreachableAcrossAscendancy_ReturnsEmpty:
    main 1-2, asc 100-101 (linked друг с другом), allocated {1}, target 101 → []
[Fact] FindPath_WithinSameAscendancy_FindsPath:
    asc 100-101, allocated {100}, target 101 → [101]
[Fact] FindPath_MissingTarget_ReturnsEmpty: target 999 → []
[Fact] FindPath_PrefersShortest: ромб 1-(2,3)-4, allocated {1}, target 4 → путь длины 2
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test PBLEngine.Tests --filter "FullyQualifiedName~TreePathFinder"`
Expected: FAIL — тип `TreePathFinder` не существует.

- [ ] **Step 3: Implement TreePathFinder.cs**

Тело — перенос логики BFS из `PredictAllocPath` (TreeTabViewModel.cs:465-496) в статический метод. В `PredictAllocPath` оставить только построение `byId` (`_nodeIndex`) и вызов `TreePathFinder.FindPath`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test PBLEngine.Tests --filter "FullyQualifiedName~TreePathFinder"`
Expected: PASS (6 тестов).

- [ ] **Step 5: Commit**

```bash
git add PBLApp.Core/TreePathFinder.cs PBLApp.Core/TreeTabViewModel.cs PBLEngine.Tests/TreePathFinderTests.cs
git commit -m "feat(tree): TreePathFinder — чистый BFS пути аллокации"
```

---

### Task 2: TreeTabViewModel — AllocatePathTo / DeallocateNode / WaitForEngineSettledAsync

**Files:**
- Modify: `PBLApp.Core/TreeTabViewModel.cs`
- Test: `PBLEngine.Tests/TreeIpcApiTests.cs` (новый, `[Collection("LuaHost")]`, `IClassFixture<LuaHostFixture>`)

**Interfaces:**
- Consumes: `TreePathFinder.FindPath` (Task 1); приватные `RunBackgroundToggle`, `RefreshAllocated`, `RefreshPointUsage`, `_luaQueue`, `_pendingToggles` — всё уже есть.
- Produces (используют Task 3):
  - `public record TreeAllocateOutcome(bool Ok, string? Error, List<int> Path, List<int> AttributeNodes, int PointsSpent, PointUsage? Usage);`
  - `public async Task<TreeAllocateOutcome> AllocatePathTo(int nodeId, string? attribute = null)` — `attribute` ∈ null|"str"|"dex"|"int" (1=Str, 2=Dex, 3=Int); null → дефолт движка (текущий `spec.attributeIndex`).
  - `public (bool Ok, string? Error, int[] Dependents) DeallocateNode(int nodeId)`
  - `public async Task WaitForEngineSettledAsync()` — ждать `_pendingToggles == 0`, затем `_luaQueue.WaitAsync()` → `RefreshAllocated(); RefreshPointUsage();` → release.
  - `public PointUsage? PointUsage => _pointUsage;` (публичное чтение для IPC).
- Существующее, на что опираться: `AllocatedIds` (коллекция int, `Contains`/`Count`), `Nodes` (`IReadOnlyList<TreeNodeDto>`), `Nodes.FirstOrDefault(n => n.Id == ...)`, `_host.AllocNode(id, deferRecalc: true)`, `_host.DeallocNode(id, deferRecalc: true)`, `_host.AllocAttributeNode(id, attributeIndex)` (LuaHost.cs:2598, 2702; AllocAttributeNode ставит `spec.attributeIndex` перед `AllocNode`), `Type` узлов "ClassStart"/"AscendClassStart".

- [ ] **Step 1: Write the failing tests**

В `TreeIpcApiTests.cs` — фикстура, в конструкторе `_host.NewBuild(); _vm = new TreeTabViewModel(_host);`.
Найти рабочие id узлов из `_vm.Nodes` (main-tree, `AscendancyName == ""`, не attribute): стартовый занятый = `_vm.AllocatedIds.First()`; сосед = первый linked; узел на расстоянии 2 = linked соседа, не равный старту. Атрибутивный узел — первый с `IsAttribute == true`, имеющий linked соседство с занятым деревом (если в 2 шагах нет — строить путь от ближайшего занятого через `FindPath`).

```csharp
[Fact] AllocatePathTo_TwoStepNode_AllocatesWholePath: Ok, Path.Count==2, AllocatedIds содержит оба, PointsSpent==2
[Fact] AllocatePathTo_ThroughAttributeNode_AutoPicks_NoDialog: путь через IsAttribute (attribute=null) → Ok, AttributeNodes содержит id узла, узел в AllocatedIds; SelectAttribute у VM оставить null (диалог не нужен — код не должен его звать)
[Fact] AllocatePathTo_ExplicitAttribute_UsesGivenIndex: тот же узел, attribute:"dex" → Ok (index 2 ушёл в AllocAttributeNode)
[Fact] AllocatePathTo_Unreachable_ReturnsError: target из чужого восхождения → Ok==false, Error содержит "unreachable"
[Fact] AllocatePathTo_OverBudget_ReportsPointsLeft: после аллокации Usage не null; если PassivesUsed>PassivesMax → Outсome всё равно Ok (перебор разрешён)
[Fact] DeallocateNode_Leaf_Removes: снимает дальний узел пути → Ok, узел исчез из AllocatedIds
[Fact] DeallocateNode_WithDependents_ListsThem: снимать ближний узел пути → Ok==false, Dependents содержит дальний
[Fact] WaitForEngineSettled_ReconcilesFromLua: после AllocatePathTo + WaitForEngineSettledAsync → _host.GetAllocatedNodeIds() (Lua-правда) совпадает с AllocatedIds
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PBLEngine.Tests --filter "FullyQualifiedName~TreeIpcApi"`
Expected: FAIL — методы не существуют.

- [ ] **Step 3: Implement in TreeTabViewModel.cs**

- `AllocatePathTo`: path = `TreePathFinder.FindPath(byId, AllocatedIds, nodeId)`; пусто → `new(false, $"unreachable: node {nodeId} not linked to allocated tree", [], [], 0, _pointUsage)`. Далее по path от дерева наружу: если узел `IsAttribute` → index = attribute switch {"str"=>1,"dex"=>2,"int"=>3,_=>default}; оптимистичная покраска (как в `AllocNodeAsync`), `Interlocked.Increment(ref _pendingToggles)`, `await RunBackgroundToggle(id, allocate: true, attributeIndex: idx)`; иначе то же без idx. `RunBackgroundToggle` получить опциональный параметр `int? attributeIndex = null` и в `Task.Run` звать `AllocAttributeNode(id, idx)` когда задан. В конце `Usage = _pointUsage` (после последнего тоггла reconcile уже случился), `PointsSpent = path.Count`, `AttributeNodes` = path-узлы с IsAttribute.
- `DeallocateNode`: компонентный анализ зависимых — подграф из `AllocatedIds \ {nodeId}` по `LinkedIds` между занятыми; компоненты, не содержащие узел типа "ClassStart"/"AscendClassStart", = зависимые (отсортировать, вернуть). Есть зависимые → `(false, "node has dependents", deps)`. Иначе — оптимистично убрать + `await RunBackgroundToggle(id, allocate: false)` → `(true, null, [])`.
- `WaitForEngineSettledAsync` — как в Interfaces.
- `public PointUsage? PointUsage => _pointUsage;`

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PBLEngine.Tests --filter "FullyQualifiedName~TreeIpcApi"`
Expected: PASS (8 тестов). Затем вся солюшн-сборка зелёная.

- [ ] **Step 5: Commit**

```bash
git add PBLApp.Core/TreeTabViewModel.cs PBLEngine.Tests/TreeIpcApiTests.cs
git commit -m "feat(tree): AllocatePathTo/DeallocateNode/WaitForEngineSettledAsync + PointUsage"
```

---

### Task 3: IPC-эндпоинты дерева + MCP-обёртки

**Files:**
- Modify: `PBLApp/Ipc/IpcServer.cs` (роут-таблица ~строки 164-179 + новые хендлеры рядом с Tree*)
- Modify: `PBLMcp/VisualTools.cs` (новые инструменты в секции Tree tab)

**Interfaces:**
- Consumes: `TreeTabViewModel`: `AllocatePathTo(int, string?)` → `TreeAllocateOutcome`, `DeallocateNode(int)`, `WaitForEngineSettledAsync()`, `PointUsage`, `AllocatedIds`, `Nodes` (`TreeNodeDto`: Id, X, Y, Name, Stats, LinkedIds, AscendancyName, Type), `SearchText`, `ToggleNodeTimed(int?)`, `GetNodeHoverInfo(int)`.
- Produces (роут-таблица, JSON-контракты):
  - `GET /tree/allocated` → `{ids:int[], nodes:[{id,name,type,ascendancy}], points:{passivesUsed,passivesMax,ascendancyUsed,ascendancyMax,weaponSet1Used,weaponSet2Used,weaponSetMax}, overBudget:bool}`; overBudget = passivesUsed>passivesMax || ascendancyUsed>ascendancyMax; points из `t.PointUsage` (null → points:null).
  - `POST /tree/allocate-path` `{nodeId:int, attribute?:string}` → `await t.AllocatePathTo(...)` на UI-потоке (метод async — через `OnUiAsync`), ответ `{ok, error?, path, attributeNodes, pointsSpent, points:{...}, overBudget}`.
  - `POST /tree/deallocate-node` `{nodeId:int}` → `{ok, error?, dependents:int[]}`.
  - `POST /tree/nodes` `{query?, nearId?, limit?=50}` → `{nodes:[{id,x,y,name,type,ascendancy,allocated,stats:string[],linkedIds:int[]}]}`; поиск — OrdinalIgnoreCase substring по Name и по каждой строке Stats; nearId → сортировка по евклидовой дистанции (X,Y) от узла nearId, иначе порядок Nodes; limit clamp 1..200.
  - Обёртки существующих: `/tree/toggle-node` {nodeId?}, `/tree/set-search` {text}, `/tree/node-text` {nodeId?}.
- MCP-инструменты (VisualTools.cs, имена и Description ≤2 строк): `visual_tree_allocated`, `visual_tree_allocate_path` (в описании: «перебор очков разрешён; в ответе overBudget/points»), `visual_tree_deallocate_node`, `visual_tree_nodes`, `visual_tree_toggle_node`, `visual_tree_set_search`, `visual_tree_node_text`. Плюс misc-обёртки из спеки (в конец VisualTools.cs отдельной секцией): `visual_ui_text` (wrap `/ui/text`), `visual_character_import` (wrap `/character-import/open`), `visual_export_open` (wrap `/export/open`), `visual_update_from_game` (wrap `/build/update-from-game`). Каждый — `await IpcClient.CallAsync(...)` как соседние.

- [ ] **Step 1: Добавить 3 новых хендлера + 3 обёртки существующих в IpcServer.cs**

Новые хендлеры по паттерну `TreeToggleNode` (IpcServer.cs:1242): `GetTreeVm()` → null → `{error}`; body-парсинг `Dictionary<string, JsonElement>` (числа) / `Dictionary<string,string>` (строки) как у соседей. Хендлер allocate-path — `async Task<object>`, внутри `await t.AllocatePathTo(nodeId, attribute)` + `await t.WaitForEngineSettledAsync()` перед сборкой ответа. Роуты добавить в switch рядом с существующими `/tree/*`.

- [ ] **Step 2: Добавить 11 MCP-инструментов в VisualTools.cs** (7 tree + 4 misc-обёртки)

Следовать образцу `VisualTreeState`/`VisualTreeFocusNode` (params с nullable, body-объекты). `visual_tree_allocate_path` принимает `int nodeId, string? attribute = null`; `visual_tree_nodes` — `string? query = null, int? nearId = null, int limit = 50`.

- [ ] **Step 3: Build + smoke**

Run: `dotnet build Poe2BuildLab.sln`
Expected: BUILD SUCCEEDED.
Ручная e2e-проверка эндпоинтов — в Task 9 (клиент + curl). Здесь достаточно сборки: логика уже покрыта тестами Task 2, хендлеры тонкие.

- [ ] **Step 4: Commit**

```bash
git add PBLApp/Ipc/IpcServer.cs PBLMcp/VisualTools.cs
git commit -m "feat(mcp): дерево — allocate-path/deallocate/nodes/allocated через IPC"
```

---

### Task 4: IPC-эндпоинты скилов + MCP-обёртки

**Files:**
- Modify: `PBLApp/Ipc/IpcServer.cs`
- Modify: `PBLMcp/VisualTools.cs`
- Test: `PBLEngine.Tests/SkillsIpcApiTests.cs` (VM-уровень, `[Collection("LuaHost")]`)

**Interfaces:**
- Consumes: `SkillsTabViewModel`: `Groups` (SkillGroupViewModel: Index, IsEnabled, IsMain, IncludeInFullDps, IsGranted, ActiveGem, SupportSlots), `AddGroupFromPicker(string)`, `RemoveGroup(int)`, `AddGem(int)`, `RemoveGem(int,int)`, `SyncGemLevel(int,int,int)`, `SyncGemQuality(int,int,int)`, `SyncGemEnabled(int,int,bool)`, `CommitGemName(int,int,string)`, `SyncGroupEnabled(int,bool)`, `SyncGroupIncludeInFullDps(int,bool)`; GemViewModel: GemIndex, CommittedName, SearchText, Level, Quality, IsEnabled, IsSupport; `LuaHost.SetActiveSkillGroup(int, int activeSkillIndex = 1)`, `LuaHost.SetGroupLabel(int, string)`, `LuaHost.GetMainSkillGroupIndex()`.
- Produces: приватный хелпер `SkillsListing(SkillsTabViewModel v)` → объект `{groups:[{index,name(ActiveGemName),isMain,enabled,includeInFullDps,isGranted,sourceLabel,gems:[{gemIndex,name(CommittedName),level,quality,enabled,isSupport}]}]}` (гемы с GemIndex>0; ActiveGem первый, потом SupportSlots).
  Роуты (все ответы мутаций = `{ok, listing}` через SkillsListing):
  - `GET /skills/list` → `{ok, listing}`
  - `POST /skills/add-group` `{gemName?}` → AddGroupFromPicker(gemName ?? ""); index = последней группы.
  - `POST /skills/remove-group` `{group:int}` → если группа IsGranted → `{error:"group is granted by <sourceLabel>"}`; иначе RemoveGroup.
  - `POST /skills/add-gem` `{group:int, gemName:string}` → AddGem(group), затем CommitGemName(group, индекс нового гема = число гемов группы, gemName).
  - `POST /skills/remove-gem` `{group:int, gem:int}` → RemoveGem (gem — 1-based engine-индекс).
  - `POST /skills/set-gem` `{group:int, gem:int, name?, level?, quality?, enabled?}` — применить только переданные поля в порядке name→level→quality→enabled.
  - `POST /skills/set-group` `{group:int, enabled?, includeInFullDps?, label?}`.
  - `POST /skills/set-main` `{group:int}` → `_host.SetActiveSkillGroup(group)`, затем Build-обновление как в `SkillsTabViewModel.SetAsMain` (SkillsTabViewModel.cs:672-682: `_build.Refresh()` + `RefreshMainFlag()`; BuildModel брать у `GetMainVm()?.CurrentPage as BuildPageViewModel` → `bp.Build` — существующий паттерн доступа, отдельного хелпера GetBuildPageVm нет); ответ `{ok, listing}`.
  - Адресация: `group` — 1-based engine Index (не позиция в списке VM!); искать `v.Groups.FirstOrDefault(g => g.Index == group)`; не найдено → `{error:"group N not found"}`.
- MCP-инструменты: `visual_skills_list`, `visual_skills_add_group`, `visual_skills_remove_group`, `visual_skills_add_gem`, `visual_skills_remove_gem`, `visual_skills_set_gem`, `visual_skills_set_group`, `visual_skills_set_main`, `visual_skills_tooltips` (wrap `/skills/tooltips`), `visual_skills_support_picker` + `visual_skills_support_set_tab` + `visual_skills_support_set_mode` + `visual_skills_support_select` (wrap четырёх `/skills/support-picker*`).

- [ ] **Step 1: Write the failing VM-тесты**

`SkillsIpcApiTests.cs` — фикстура + `new SkillsTabViewModel(_host, build)`; build = `new BuildModel(_host)`; после каждого мутационного теста логика адресации повторяется через `Groups` (это и есть контракт листинга).

```csharp
AddSkill-хелпер как в SkillsTabTests (PasteSocketGroup).
[Fact] AddGroupFromPicker_NewGroup_LastIndex: → группа в Groups, Index == max
[Fact] RemoveGroup_Middle_IndiciesShift: 3 группы (PasteSocketGroup трижды), RemoveGroup(средний engine-Index) → листинг содержит 2, их Index — оставшиеся engine-индексы (адресация не по позиции)
[Fact] RemoveGroup_Granted_ReturnsError: группу с IsGranted (granted-группы есть в дефолт-билде после NewBuild при некоторых классах; если нет — скип-факт с пометкой Skip="needs granted group") → контракт IsGranted/CanRemove проверяется ассертом CanRemove==false
[Fact] SetGem_ChangesLevelQualityEnabled: add-group "Fireball", set level 10 / quality 10 / enabled false → значения в листинге
[Fact] AddGem_SupportGem_Appears: add-gem "Added Lightning Damage Support" → гем в листинге группы, isSupport
[Fact] RemoveGem_ThenRelist: 2 гема → remove-gem первого → второй остаётся с тем же engine-индексом 1 (переадресация по листингу)
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test PBLEngine.Tests --filter "FullyQualifiedName~SkillsIpcApi"`
Expected: FAIL/ошибка компиляции там, где нужны новые API (остальное — контрактные ассерты могут пройти сразу — это допустимо, они фиксируют контракт).

- [ ] **Step 3: Реализовать хендлеры + SkillsListing в IpcServer.cs, 13 инструментов в VisualTools.cs**

- [ ] **Step 4: Run to verify pass + build**

Run: `dotnet test PBLEngine.Tests --filter "FullyQualifiedName~SkillsIpcApi" && dotnet build Poe2BuildLab.sln`
Expected: PASS + BUILD SUCCEEDED.

- [ ] **Step 5: Commit**

```bash
git add PBLApp/Ipc/IpcServer.cs PBLMcp/VisualTools.cs PBLEngine.Tests/SkillsIpcApiTests.cs
git commit -m "feat(mcp): скилы — полный CRUD групп и гемов через IPC"
```

---

### Task 5: IPC /stats, /build/save, /build/xml, /notes + MCP-обёртки

**Files:**
- Modify: `PBLApp.Core/BuildPageViewModel.cs` (публичный `SaveNow`)
- Modify: `PBLApp.Core/NotesTabViewModel.cs` (публичный `ApplyToBuild`)
- Modify: `PBLApp/Ipc/IpcServer.cs`, `PBLMcp/VisualTools.cs`

**Interfaces:**
- Consumes: `BuildPageViewModel`: `Build` (BuildModel: `AllStats`, `SaveBuildToXml()`), приватный `SaveBuildAsync` (строки 345-352), `_xmlPath`; `NotesTabViewModel`: `Notes`, `_build`.
- Produces:
  - `BuildPageViewModel`: `public Task SaveNow() => SaveBuildAsync();`
  - `NotesTabViewModel`: `public void ApplyToBuild() => _build.Notes = Notes;`
  - `GET /stats` `?keys=a,b` (из query string; путь без body) → объект статов из `bp.Build.AllStats`, где bp = `(GetMainVm()?.CurrentPage as BuildPageViewModel)` (пустые keys → все, сортировка по ключу); bp/Build null → `{error}`.
  - `POST /build/save` → `await bp.SaveNow()` → `{ok, path: bp._xmlPath}` (_xmlPath недоступен — вернуть путь из `GetBuildPageVm()` через новое публичное свойство `public string XmlPath => _xmlPath;` в BuildPageViewModel).
  - `GET /build/xml` → `{xml}` из `Build.SaveBuildToXml()`.
  - `GET /notes` → `{text: bp.NotesTab?.Notes ?? ""}`; `POST /notes` `{text}` → `t.Notes = text; t.ApplyToBuild();` → `{ok}`.
- MCP-инструменты: `visual_get_stats` (param `string keys = ""`), `visual_build_save`, `visual_build_xml`, `visual_notes` (param `string text = ""` — пусто = прочитать).

- [ ] **Step 1: Добавить SaveNow/XmlPath в BuildPageViewModel, ApplyToBuild в NotesTabViewModel**

- [ ] **Step 2: Добавить 4 хендлера + роуты в IpcServer.cs** (query string парсить из `ctx.Request.Url!.Query` — передать в хендлер как параметр; см. как HandleAsync передаёт body)

- [ ] **Step 3: Добавить 4 MCP-инструмента в VisualTools.cs**

- [ ] **Step 4: Build + существующие тесты**

Run: `dotnet build Poe2BuildLab.sln && dotnet test PBLEngine.Tests`
Expected: BUILD SUCCEEDED, все тесты зелёные.

- [ ] **Step 5: Commit**

```bash
git add PBLApp.Core/BuildPageViewModel.cs PBLApp.Core/NotesTabViewModel.cs PBLApp/Ipc/IpcServer.cs PBLMcp/VisualTools.cs
git commit -m "feat(mcp): статы, сохранение билда, XML и заметки через IPC"
```

---

### Task 6: IPC /config/state, /config/set + MCP-обёртки

**Files:**
- Modify: `PBLApp/Ipc/IpcServer.cs`, `PBLMcp/VisualTools.cs`

**Interfaces:**
- Consumes: `BuildPageViewModel.ConfigTab` (ConfigTabViewModel: `Sections` → ConfigSectionViewModel: NameKey, Name, Options), `ConfigOptionViewModel`: Var, Label, Type ("check"|"list"|"text"|число), ListOptions (ConfigListItem: Label, Val), IsChecked, SelectedVal, SelectedItem, TextValue, ApplyText().
- Produces:
  - `GET /config/state` → `{sections:[{name, options:[{var,label,type,value,listOptions:[{label,val}]}]}]}`; value = IsChecked/SelectedVal/TextValue по Type; ConfigTab null → `{error:"ConfigTab not ready"}`.
  - `POST /config/set` `{var, value}` → найти опцию по Var во всех секциях; нет → `{error}`; применить по Type: "check" → `IsChecked = value=="true"`; "list" → `SelectedItem = ListOptions.FirstOrDefault(o => o.Val==value) ?? ListOptions.FirstOrDefault(o => o.Label==value)`; не найден → `{error:"unknown option value"}`; иначе → `TextValue = value` (для текстовых это триггерит SetConfigValue через OnTextValueChanged; после — `ApplyText()` только для Type=="text" с пустым значением? Нет: OnTextValueChanged для "text" применяет сам; ApplyText вызывать не нужно).
  - Ответ `{ok, var, applied:value}`.
- MCP-инструменты: `visual_config_state`, `visual_config_set` (Description: value для check — "true"/"false", для list — val из config_state, для остальных — строка/число).

- [ ] **Step 1: Добавить 2 хендлера + роуты в IpcServer.cs**

- [ ] **Step 2: Добавить 2 MCP-инструмента в VisualTools.cs**

- [ ] **Step 3: Build + тесты**

Run: `dotnet build Poe2BuildLab.sln && dotnet test PBLEngine.Tests`
Expected: зелёные.

- [ ] **Step 4: Commit**

```bash
git add PBLApp/Ipc/IpcServer.cs PBLMcp/VisualTools.cs
git commit -m "feat(mcp): config-таб — чтение и установка опций через IPC"
```

---

### Task 7: Кроссплатформенный VisualLaunchClient

**Files:**
- Modify: `PBLMcp/VisualTools.cs` (метод `VisualLaunchClient`, строки ~27-60)

**Interfaces:**
- Produces: поведение запуска: если `PBLApp.exe` существует (Windows) → запуск exe с `--enable-ipc` (как сейчас); иначе если `PBLApp.dll` существует → `ProcessStartInfo("dotnet", $"\"{dllPath}\" --enable-ipc")`; иначе ошибка с путём и подсказкой `dotnet build PBLApp/PBLApp.csproj`. Остальная логика (IsAlive-чек, WaitForReadyAsync, discovery) без изменений.

- [ ] **Step 1: Реализовать поиск и запуск**

Замена блока «Find the PBLApp executable» (VisualTools.cs:38-50). Прежде чем писать — проверить фактическое наличие артефактов: `ls PBLApp/bin/Debug/net9.0/ | head` (ожидаем PBLApp.dll на Linux).

- [ ] **Step 2: Build**

Run: `dotnet build PBLMcp/PBLMcp.csproj`
Expected: BUILD SUCCEEDED.

- [ ] **Step 3: Commit**

```bash
git add PBLMcp/VisualTools.cs
git commit -m "fix(mcp): запуск клиента на Linux через dotnet PBLApp.dll"
```

---

### Task 8: Документация — docs/mcp.md, README, CHANGELOG

**Files:**
- Create: `docs/mcp.md`
- Modify: `README.md` (короткая секция со ссылкой), `CHANGELOG.md` (запись в верхний непушнутый раздел по существующему формату файла)

**Interfaces:**_consumes всё из Task 3-7 (итоговый каталог инструментов фиксируется здесь)._

- [ ] **Step 1: Написать docs/mcp.md**

Структура: (1) что такое PBLMcp — MCP stdio-сервер, три группы инструментов (headless `pbl_*`/`app_*` для движка и тестов; `visual_*` для живого клиента); (2) подключение: ZCode — `mcpServers`-конфиг с `command: dotnet, args: [run, --project, PBLMcp]` (и вариант с собранной dll), Claude Desktop — `claude_desktop_config.json` c абсолютным путём; (3) запуск клиента: `visual_launch_client` сам соберёт/запустит (Windows exe / Linux dotnet), `--enable-ipc`; (4) каталог инструментов таблицей — все существующие + добавленные (сгруппировать: build lifecycle, stats, tree, skills, items, config, notes, trader, view/screenshots); (5) рецепты: «проанализируй билд» (open build → get stats → tree allocated → skills list → рекомендации → notes), «сравни два предмета» (editor + get_stats diff), «проложи путь на дереве» (tree_nodes → allocate_path → get_stats → deallocate откат), «собери скил-сетап» (add_group → add_gem → set_main → get_stats), «сохрани билд» (build_save).

- [ ] **Step 2: README + CHANGELOG**

README: секция «AI agent control (MCP)» — 3-4 строки + ссылка на docs/mcp.md. CHANGELOG: запись о новом покрытии MCP по формату файла (посмотреть верх существующего CHANGELOG.md).

- [ ] **Step 3: Commit**

```bash
git add docs/mcp.md README.md CHANGELOG.md
git commit -m "docs: MCP-подключение агентов и каталог инструментов"
```

---

### Task 9: Финальная верификация — сборка, тесты, Linux IPC smoke

**Files:** без изменений кода (фиксы по результатам — в соответствующие файлы).

- [ ] **Step 1: Полная сборка и тесты**

Run: `dotnet build Poe2BuildLab.sln && dotnet test PBLEngine.Tests`
Expected: BUILD SUCCEEDED, тесты зелёные (зафиксировать число).

- [ ] **Step 2: Linux IPC smoke — клиент запускается, /ping отвечает**

```bash
cd PBLApp/bin/Debug/net9.0 && dotnet PBLApp.dll --enable-ipc &   # дождаться старта ~40 c
```
Затем: прочитать `~/.local/share/PathOfBuilding/mcp-ipc.json` (порт+токен), `curl -H "X-PoB-Token: <token>" http://127.0.0.1:<port>/ping` → `{"ok":true,...}`; `curl .../state` → JSON страницы; закрыть клиент (kill по pid из discovery). Если `~/.local/share` пуст — проверить фактический SpecialFolder.LocalApplicationData на Linux (`echo $XDG_DATA_HOME`).

- [ ] **Step 3: Сквозной прогон инструментов по горячему клиенту (ручной, по docs/mcp.md)**

Подключить PBLMcp как MCP-сервер в ZCode-сессии и пройти рецепт «проанализируй билд»: launch_client → open build → get_stats → tree_allocated → skills_list → notes. Любой сбой — фикс в соответствующем файле + повтор. (Шаг выполняет пользователь/агент с работающим окружением; в плане остаётся чеклистом.)

- [ ] **Step 4: Финальный коммит, если были фиксы**

```bash
git add -A && git commit -m "fix(mcp): правки по результатам e2e-прогона"
```
