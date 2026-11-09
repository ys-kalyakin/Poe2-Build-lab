# MCP: закрытие гэпов «построить и улучшить билд» — дизайн

Дата: 2026-11-09. Продолжение `2026-11-09-mcp-full-build-control-design.md` и
item-economy. Гэп-анализ (чат 2026-11-09) выявил 8 дыр в цикле
«прочитать → поменять → оценить → сохранить»; пользователь утвердил порядок
реализации: **гемы-каталог → diff-статов → DPS breakdown → validate →
import → weights/ilvl → weapon set'ы**.

## Intent и почему

Агент с текущим surface может манипулировать UI, но не может: узнать, какие
гемы существуют в данных приложения (полагается на свою память о PoE2 —
рассинхрон с версией данных), дёшево сравнить статы до/после примерки
(сравнение ~600 чисел в голове), увидеть разбивку DPS по скилам, узнать о
сломанных требованиях, взять чужой билд за основу, отличить «мод может
выпасть» от «не может», сконструировать реалистичный предмет, работать с
weapon set очками.

## Goals / Non-goals / Anti-goals

**Goals**
- Все новые знания — из данных самого приложения (никакой памяти модели).
- Мутации остаются лёгкими; diff/dps — отдельные read-инструменты (решено
  в чате).
- Каждый пункт: IPC-эндпоинт → MCP-инструмент → запись в docs/mcp.md → тест.

**Non-goals**
- Перебор DPS всех групп через переключение main (тяжёлый мутационный цикл) —
  recipe «включи includeInFullDps → visual_skills_dps» покрывает это
  существующими инструментами.
- Фейковые «веса распределения» тиров: в PoE2-датасете их нет.
- Чат в приложении (отложен ранее), рефейс общего слоя инструментов (отложен).

**Anti-goals**
- Ответы, скрывающие источник данных (агент должен понимать, что число из
  mainOutput, а цена — из ninja и т.п.).
- Неломающее поведение существующих инструментов.

## Найденное в коде (проверено)

- Гемы: `data.gems` (src/Data/Gems.lua, 967 записей) — теги, требования,
  weaponRequirements, naturalMaxLevel; мана/резерв в grantedEffect.levels;
  `GetGemTooltipByName` (LuaHost.cs:740) уже рендерит тултип по имени;
  `SkillsAddGroup` (IpcServer.cs:828) НЕ валидирует имя — неизвестный гем
  молча создаёт битую группу («Unrecognised gem name» не всплывает).
- Статы: `/stats` → `build.AllStats` (LuaHost.GetAllStats, LuaHost.cs:1041),
  плоский словарь примитивов.
- DPS: `LuaHost.GetFullDpsBreakdown()` (LuaHost.cs:406) → per-skill
  (Name, Dps, Count) для групп с includeInFullDPS; итоги TotalDPS/FullDPS
  в AllStats.
- Validate: всё уже посчитано в mainEnv/mainOutput — ReqStr/Dex/Int vs
  Str/Dex/Int, Unreserved Life/Mana/Spirit, LifeCostWarning-семейство,
  env.itemWarnings, skillFlags.disable + disableReason, PointUsage.
- Import: `BuildModel.LoadBuildFromXml(xml, name)` (BuildModel.cs:177) —
  программная загрузка, уже используется импортом; `BuildCodec.Decode`
  (BuildCodec.cs:26) — PoB share code → XML; после загрузки нужны Refresh
  вкладок (паттерн BuildPageViewModel.cs:145).
- Афиксы: `weightVal` в ModItem.lua бинарный (0/1) = spawn-маска «может ли
  выпасть на базе»; вес уже вычисляется в GetItemAffixes (LuaHost.cs:2013).
- ilvl: `ItemEditorViewModel.ItemLevel` (по умолчанию 80) уже есть, поле в
  UI и в /items/get-editor есть; гейтинга тиров по ilvl нет нигде;
  `AffixEntry.Level` (уровень тира) уже отдаётся.
- Weapon set: Lua полностью поддерживает (`spec.allocMode` 0/1/2,
  node.allocMode, CountAllocNodes → ws1/ws2/max); из C# не экспонировано
  ничего кроме чтения бюджетов; переключение активного сета (items) умеют VM
  и движок (`SetWeaponSwapActive`), IPC-маршрута нет; привязка скил-группы к
  сету = `group.slot` ("Weapon 1"/"Weapon 1 Swap").

## Подходы и решения

Ключевая развилка (решена в чате, выбрал я): **расширять ответы мутаций vs
отдельные read-инструменты** — выбраны отдельные read'ы; исключение —
spawnWeight, это просто поле существующего ответа афиксов.

Остальные развилки — мелкие, решил я:

- *Каталог гемов одним DoString* (итерация `data.gems` в LuaHost) vs
  N×GetGemTooltipByName — первый, один проход, метаданные из тех же
  структур, что уже парсит тултип.
- *Снапшоты в памяти IpcServer* (именованные, `last` по умолчанию) vs файлы
  — память; сбрасываются при открытии/импорте билда (diff между билдами —
  шум).
- *import: save по умолчанию false* — визуальный build-файл пользователя
  не перезаписывается молча; `save:true` — явно.
- *ilvl-гейт в CanAddAffix с внятной ошибкой* («tier X требует ilvl Y,
  текущий Z»), без автосинхронизации ItemLevel при смене базы — менее
  сюрпризно для UI-пользователя; агент ставит ilvl сам.
- *validate — единый Lua-сниппет, читающий посчитанное*; ничего не
  пересчитываем.

## Спецификация инструментов

### P1. Каталог гемов
- `LuaHost.GetGemCatalog()` — один DoString: `{name, baseTypeName, support,
  tags[], tagString, gemType, reqStr/reqDex/reqInt, naturalMaxLevel, Tier,
  weaponRequirements, manaCost(на naturalMaxLevel), description}`.
- IPC `GET /skills/gem-catalog?query=&type=active|support&tag=&weapon=`;
  без фильтров — все, кап 400 + `truncated:true`.
- MCP `visual_gems_list {query?, type?, tag?, weapon?}`.
- MCP `visual_gem_tooltip {gemName}` → существующий GetGemTooltipByName
  (детали по уровням, полный текст).
- Фикс: `SkillsAddGroup` валидирует имя по каталогу; ошибка + fuzzy-
  подсказки (по образцу guesses в ItemAffixes).

### P2. Снапшот/diff статов
- IPC `POST /stats/snapshot {name?}` → `{ok, name, keyCount}`; дефолт `last`.
- IPC `GET /stats/diff?from=&to=` — to по умолчанию текущие статы, from —
  `last`. Ответ `{changed:{key:{from,to}}, added:{key:val}, removed:[key]}`.
- Хранилище в IpcServer (одна сессия = один клиент с токеном); очистка при
  /open-build и /build/import.
- MCP `visual_stats_snapshot {name?}` · `visual_stats_diff {from?, to?}`.

### P3. DPS по скилам
- IPC `GET /skills/dps` → `{fullDps, totalDps, skills:[{name, dps, count}],
  groups:[{index, name, isMain, includeInFullDps}]}` — GetFullDpsBreakdown +
  GetSkillGroups; чистый read.
- MCP `visual_skills_dps`. Recipe в docs/mcp.md: не видишь группу — включи
  ей includeInFullDps (`visual_skills_set_group`) и перечитай.

### P4. Валидация билда
- `LuaHost.GetBuildValidation()` — один сниппет: атрибуты (Req* > *),
  отрицательные Unreserved Life/Mana/Spirit, LifeCost/ManaCost/...Warning,
  env.itemWarnings, disabled active skills (skillFlags.disable +
  disableReason), PointUsage (очки/асцензия/weapon set).
- Ответ: `{ok, points:{...}, warnings:[{code, message, source?}],
  disabledSkills:[{group, gem, reason}]}`. Коды стабильные (attrs, pool,
  cost, points, item, skill-disabled).
- IPC `GET /build/validate`; MCP `visual_build_validate`.

### P5. Импорт билда
- IPC `POST /build/import {xml|code, name?, save?}`: code →
  BuildCodec.Decode; валидация `<PathOfBuilding`; LoadBuildFromXml(xml,
  name ?? текущее имя); Refresh вкладок; save → bp.SaveNow().
- MCP `visual_build_import {xml|code, name?, save?}`; снапшоты сбрасываются.

### P6. spawnWeight в афиксах
- AffixEntry += `SpawnWeight` (int; в PoE2-данных 0/1 — spawn-маска);
  заполнение в GetItemAffixes (wv уже вычислен), поле в ItemAffixEntry.
- docs/mcp.md: честно — распределения тиров в данных нет, для реализма:
  spawnWeight>0 + tier level ≤ ilvl.

### P7. Item level
- IPC `POST /items/editor-set-item-level {level}` (clamp 1..100).
- Гейт в `CanAddAffix`: `AffixEntry.Level > ItemLevel` → отказ с текстом
  «tier requires item level Y, current Z» (уровень тира уже в записи).
- MCP `visual_editor_set_item_level {level}`; /items/get-editor уже отдаёт
  itemLevel.

### P8. Weapon set'ы (последний, при живомLua-контракте)
- `LuaHost.SetAllocMode(mode)` + параметр `weaponSet` в AllocatePathTo/
  DeallocateNode/AllocNode/DeallocNode (ставить allocMode до, вернуть после);
  guard деаллокации — как в UI (PassiveTreeView.lua:2036).
- `/tree/allocated`: per-node `allocMode`; `/items/select-weapon-set {set}`;
  `/skills/set-group` += `weaponSet` (→ group.slot "Weapon 1"/"Weapon 1
  Swap"/снять); SkillsListing += slot/slotEnabled.
- MCP: `weaponSet` в visual_tree_allocate_path/deallocate_node,
  `visual_items_select_weapon_set`, `weaponSet` в visual_skills_set_group.

## Что осталось билдеру

- Точная форма Lua-сниппетов (по образцам существующих Get* методов).
- Кэш каталога гемов в IpcServer (не дёргать движок на каждый фильтр) — по
  вкусу, каталог статичен в рамках версии данных.
- Тесты: PBLEngine.Tests по образцу ItemDataTests (LuaHost-методы: каталог,
  валидация, spawnWeight, ilvl-гейт VM, BuildCodec-круг); IPC/MCP-обёртки —
  как существующие тесты покрывают аналогичные.
- Порядок коммитов: по приоритетам P1..P8, docs/mcp.md — в конце или с
  каждым (по образцу ветки).
