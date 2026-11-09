# MCP: управление PoE2 Build Lab через ИИ-агента

PoE2 Build Lab поставляется с MCP-сервером (`PBLMcp`, stdio): ИИ-агент
(Claude Desktop, ZCode и т. п.) подключается к нему и получает полный набор
инструментов — от чтения статов до постройки билда: дерево пассивок, скилы и
гемы, экипировка, конфиг, сохранение.

## Два режима инструментов

| Группа | Как работает | Когда использовать |
|---|---|---|
| `visual_*` | управляет **живым окном** PBLApp через локальный HTTP IPC (127.0.0.1 + токен) | основной сценарий: агент меняет билд, пользователь видит всё в реальном времени |
| `pbl_*`, `app_*` | headless-движок (LuaHost) и MVVM-драйвер **внутри процесса MCP** | быстрые расчёты, CI, тесты; пользователь ничего не видит |

Инструменты `visual_*` требуют запущенный клиент — запускайте
`visual_launch_client` первым (он сам найдёт бинарник: Windows —
`PBLApp.exe`, Linux — apphost `PBLApp` или `dotnet PBLApp.dll`; клиент
стартует с `--enable-ipc`). Первый вызов любого инструмента подождёт
инициализации движка (~30–45 с).

## Подключение

### ZCode

В настройках MCP (`mcpServers`):

```json
{
  "mcpServers": {
    "poe2-build-lab": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/Poe2-Build-lab/PBLMcp/PBLMcp.csproj"]
    }
  }
}
```

Вариант с собранной dll (быстрее старта):

```bash
dotnet build PBLMcp/PBLMcp.csproj -c Release
# command: dotnet, args: ["/path/to/PBLMcp/bin/Release/net9.0/PBLMcp.dll"]
```

### Claude Desktop

`claude_desktop_config.json` (`Settings → Developer → Edit Config`):

```json
{
  "mcpServers": {
    "poe2-build-lab": {
      "command": "dotnet",
      "args": ["/path/to/Poe2-Build-lab/PBLMcp/bin/Release/net9.0/PBLMcp.dll"]
    }
  }
}
```

## Каталог инструментов `visual_*`

### Клиент и навигация
`visual_launch_client` · `visual_is_client_running` · `visual_close_client` ·
`visual_get_state` · `visual_select_tab` (Items/Tree/Skills/Calcs/Config/…) ·
`visual_list_builds` · `visual_open_build` · `visual_go_back` ·
`visual_screenshot` · `visual_set_language` (en/ru) · `visual_ui_text` (дамп
видимого текста UI)

### Дерево — построение
`visual_tree_allocated` — занятые узлы + бюджет очков и `overBudget` ·
`visual_tree_nodes` — поиск узлов по имени/статам (возвращает id для аллокации) ·
`visual_tree_allocate_path` — проложить кратчайший путь до узла (атрибутивные
узлы автоматически или `attribute: str/dex/int`) · `visual_tree_deallocate_node`
— снять узел (откажет со списком зависимых) · `visual_tree_toggle_node` ·
`visual_tree_node_text` — текст тултипов узлов · `visual_tree_set_search`

### Дерево — просмотр и анализ
`visual_tree_state` · `visual_tree_select_class` ·
`visual_tree_select_ascendancy` · `visual_tree_get_view` / `set_view` /
`focus_node` / `zoom` / `pan` · `visual_tree_open_jewel_picker` /
`pick_jewel` · `visual_tree_power_build` / `visual_tree_power_report`
(heatmap мощности узлов)

### Скилы и гемы
`visual_skills_list` — все группы и гемы · `visual_skills_add_group` ·
`visual_skills_remove_group` (granted-группы защищены) ·
`visual_skills_add_gem` · `visual_skills_remove_gem` · `visual_skills_set_gem`
(имя/уровень/качество/вкл одним вызовом) · `visual_skills_set_group`
(вкл/FullDPS/метка) · `visual_skills_set_main` — активная группа для Full DPS ·
`visual_skills_tooltips` · `visual_skills_support_picker` /
`support_set_tab` / `support_set_mode` / `support_select` ·
`visual_skills_dps` — разбивка Full DPS по скилам + итоги + флаги групп
(группа не видна? включи ей `includeInFullDps` через `visual_skills_set_group`
и перечитай)

### Каталог гемов (какие гемы существуют)
`visual_gems_list {query?, type?, tag?, weapon?}` — каталог гемов из данных
приложения (967 гемов): имя, теги, требования атрибутов, naturalMaxLevel,
мана-кост, требования оружия, описание. Фильтры комбинируются; без фильтров —
первые 400 + `truncated`. `visual_gem_tooltip {gemName, level?}` — тултип по
точному имени (стоимость/резерв/кулдаун на уровне).

**Правило:** перед `visual_skills_add_group` / `add_gem` резолви имя гема
через `visual_gems_list` — приложение принимает только точные имена каталога
(неверное имя вернёт ошибку с подсказками, группу не создаст).

> ⚠️ Адресация: индексы движка, 1-based. После любого удаления движок
> переиндексирует группы и гемы **плотно** — каждый мутационный ответ
> возвращает свежий листинг, адресуйтесь только по нему.

### Предметы
`visual_items_state` · `select_slot` / `select_pool` · `equip` / `unequip` ·
`compatible_slots` · `get_tooltip` / `get_all_tooltips` · редактор:
`editor_create_new`, `editor_set_rarity/base/unique/quality/corrupted/base_stat/mod_slider`,
`editor_add_affix` / `remove_mod`, `editor_add_rune_socket` / `set_rune` /
`remove_rune_socket`, `tattoo_state` / `set_tattoo`, `phylactery_state`,
`editor_save` · `items_edit_current` / `cancel_edit`

### Статы, конфиг, сохранение
`visual_get_stats` — все ~600 статов или фильтр `keys=TotalDPS,Life,…` ·
`visual_stats_snapshot` / `visual_stats_diff` — снапшот статов и diff до/после
(снапшоты стираются при открытии/импорте другого билда) ·
`visual_build_validate` — свод предупреждений: несходящиеся атрибуты,
отрицательные пулы, нехватка пула на скилы, itemWarnings движка, отключённые
скилы (например «weapon is not usable with this skill»), перерасход очков
дерева/асцензии/weapon set. Запускай после мутаций шмота/дерева/гемов — UI
ничего не блокирует, предупреждения видны только здесь ·
`visual_config_state` / `visual_config_set` — настройки боя (враг, заряды,
баффы — влияют на цифры) · `visual_build_save` — сохранить XML билда ·
`visual_build_xml` — выгрузить XML наружу ·
`visual_build_import {xml|code, name?, save?}` — загрузить чужой билд
(XML или PoB share code) как текущий; по умолчанию только в памяти,
save:true перезаписывает файл пользователя · `visual_notes` — читать/писать
заметки билда (рекомендации агента видит пользователь)

### Прочее
`visual_trader_open` / `search` / `state` / `set_league` — подбор апгрейдов
через официальный трейд · `visual_character_import` — импорт/обновление
персонажа из игры · `visual_export_open` — окно импорт/экспорт ·
`visual_update_from_game` — обновить билд из аккаунта

### Знания о вещах и цены (item-economy)
`visual_item_bases {category?}` — какие базы существуют (по категории: имя,
имплисит, требования, руны) · `visual_item_affixes {base}` — полный пул
афиксов базы (префиксы/суффиксы, тиры, диапазоны) + коррапшн + руны ·
`visual_item_uniques {query?}` — какие уники существуют ·
`visual_prices_currency {type?, query?}` — курсы валют/осколков/рун/эссенций
в дивинах (poe.ninja, кэш 1ч) · `visual_prices_uniques {type?, query?}` —
цены уников в дивинах + число живых лотов + моды (poe.ninja, кэш 1ч).
Цены rares по весам — только живой трейд (`visual_trader_*`, нужен логин
пользователя: `loggedIn` в `visual_trader_state`).

### Headless (без окна)
`new_build`, `load_build`, `get_build_xml`, `get_stats`, `set_custom_mods`,
`add_skill`, `equip_item`, `list_builds`, `run_lua` — прямой движок;
`app_wait_for_ready`, `app_list_builds`, `app_select_build`, `app_open_build`,
`app_get_stats`, `app_list_skills`, `app_select_skill`, `app_get_gems`, … —
MVVM-драйвер для тестов.

## Рецепты

**Проанализируй билд**
1. `visual_launch_client` → `visual_open_build {name}` (подождать
   `visual_get_state` → `BuildPageReady`)
2. `visual_get_stats` (ключевые статы) · `visual_tree_allocated` ·
   `visual_skills_list`
3. Выводы — в `visual_notes {text}` (пользователь прочитает в окне заметок) и
   в ответе агенту.

**Сравнить два предмета**
1. `visual_stats_snapshot` — зафиксировать текущие статы
2. `visual_items_equip` (второй предмет) → `visual_stats_diff` — changed/added/removed
3. Откат: equip первого предмета, `visual_stats_diff` подтвердит возврат.

**Построить путь на дереве**
1. `visual_tree_nodes {query: "название узла"}` → взять id
2. `visual_tree_allocate_path {nodeId}` → в ответе `path`, `pointsSpent`,
   `overBudget`
3. `visual_get_stats` — эффект; откат — `visual_tree_deallocate_node`
   (с конца пути).

**После мутаций — проверь билд**
1. После смены шмота/дерева/гемов: `visual_build_validate`.
2. `warnings` с кодами `attrs`/`pool`/`cost`/`item`/`points` — почини
   (атрибуты — атрибутивными узлами дерева или шмотом, очки — деаллокация).
3. `disabledSkills` — скил не работает (чаще всего оружие не подходит) —
   смена базы оружия или гема.

**Собрать скил-сетап**
1. `visual_skills_add_group {gemName}` → индекс из листинга
2. `visual_skills_add_gem {group, gemName}` для каждого саппорта
3. `visual_skills_set_main {group}` → `visual_get_stats`.

**Взять мета-билд за основу и улучшить**
1. Достань share code билда (poe.ninja, форум) →
   `visual_build_import {code}` — билд в памяти, файл пользователя не тронут.
2. `visual_build_validate` → `visual_skills_dps` → `visual_tree_allocated` —
   что внутри.
3. Улучшай: примерки через `visual_stats_snapshot`/`diff`, апгрейды через
   `visual_trader_*`/`visual_prices_*`.
4. Понравилось — `visual_build_save` (или `visual_build_import {..., save: true}`
   сразу).

**Сохранить билд** — `visual_build_save` (XML по пути из ответа).

**Оценить бюджет билда**
0. Один раз: `visual_trader_open {slot}` — создаёт трейд-сессию, без неё
   `visual_prices_*` вернут ошибку.
1. Уники из билда: `visual_item_uniques` (или имена из тултипов) →
   `visual_prices_uniques {query: имя}` по каждому (type по слоту).
2. Крафт/валюта на апгрейды: `visual_prices_currency` (типы Essences,
   SoulCores, Runes — что нужно по `visual_item_affixes`).
3. Итог — сумма в дивинах; дешёвые замены уников ищи тем же
   `visual_prices_uniques` по базе.

**Найти дешёвый уник в слот**
1. `visual_item_uniques {query: база или категория}` — кандидаты.
2. `visual_prices_uniques {type: слот, query: имя}` — цена + `listingCount`
   (много лотов = легко купить).
3. Эффект — примеркой: трейд-флоу ниже или `visual_items_editor_create_new`
   с уником → `visual_get_stats`.

**Сколько стоит такой редкий предмет** (точная цена, нужен логин пользователя)
1. `visual_trader_state` → `loggedIn: false` — попроси пользователя войти
   в трейд-таб.
2. `visual_trader_open {slot}` → веса статов через трейд-таб (или дефолт) →
   `visual_trader_search {slot}`.
3. `visual_trader_state` — результаты с ценой, dps/ehp-дельтой и
   `valuePerDiv`; не трогай примерку без нужды пользователя.
Ограничение: трейд лимитирован — точечные запросы, не перебор десятков
вариантов; быстрые сравнения делай по `visual_item_affixes` (тиры) и
`visual_prices_uniques` без сети трейда.

## Безопасность

- IPC-сервер слушает только `127.0.0.1`, токен лежит в
  `%LOCALAPPDATA%/PathOfBuilding/mcp-ipc.json`
  (Linux: `~/.local/share/PathOfBuilding/mcp-ipc.json`) — файл читают только
  процессы текущего пользователя.
- Агент управляет **локальным приложением и его билдами**; сетевые вызовы —
  только официальный трейд/OAuth GGG, как и в ручном режиме.
