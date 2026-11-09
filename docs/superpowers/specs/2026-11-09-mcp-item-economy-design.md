# MCP: знания о вещах и ценах для агента — дизайн

Дата: 2026-11-09
Статус: утверждён («начинай», гибрид); реализация на ветке `feat/mcp-full-build-control`
Предшественник: `2026-11-09-mcp-full-build-control-design.md` (управление билдом — в имплементации)

## Замысел и почему

Агент через MCP уже строит билды (дерево, скилы, экипировка, конфиг, статы).
Но «эффективный и **дешёвый**» билд требует трёх знаний, которых агенту не
достаётся:

1. **Существование**: какие базы бывают (по слоту), какие уники существуют.
2. **Афиксы**: какой пул афиксов у каждой базы — какие префиксы/суффиксы,
   тиры, диапазоны значений; руны; коррапшн.
3. **Цены**: сколько стоит валюта, уники, и «сколько стоит вот такой предмет».

Сейчас: базы/уники видны агенту только через открытый UI-редактор предмета
(`/items/editor-state`), пул афиксов недоступен без редактора, из цен есть
только курсы валют (`TraderWebApi.GetCurrencyRatesAsync`, ninja exchange) и
живой трейд-флоу через окно трейдера (`/trader/search` + `/trader/state`).

## Цели / не-цели / анти-цели

**Цели**
1. Существование: агент перечисляет базы по категории, ищет уники по имени.
2. Афиксы: агент получает полный пул базы (префикс/суффикс, тиры, диапазоны)
   плюс руны и коррапшн — без открытия редактора.
3. Цены: валюта по категориям (ninja exchange, уже подключён — расширить),
   цены уников (ninja stash overview, новый источник), точечная цена предмета
   (живой trade2 через существующий трейд-флоу).
4. «Дешёвость» rares оценивает **сам агент** из данных тиров + курсов —
   локальной формулы цены в приложении нет.

**Не-цели**
- Автопокупка, снайпинг, история цен, собственный дамп-кэш.
- Цены BaseType для poe2 — ninja их не отдаёт (проверено; для poe1 отдаёт, для
  poe2 только уники и exchange).
- Оценщик цены rares формулой — см. цель 4.

**Анти-цели** (что считаем провалом)
- Агент выдумывает цену там, где источник вернул пусто: инструменты обязаны
  возвращать явные пустые результаты/ошибки, описания — называть покрытие.

## Подходы (рассмотренные)

**A. Всё через живой трейд.** Точная цена чего угодно, но: лимиты trade2,
нужен OAuth-логин пользователя, каждый поиск — секунды. Перебор 10 вариантов
билда = 10+ поисков — тяжело и по лимитам, и по UX. Отклонено как единственный
слой.

**B. Локальная эвристика цен.** Мгновенно, без сети, но: мета-спрос эвристикой
не ловится (T1×4 может стоить 2 див или 40 — зависит от мета), а уники и
валюту ninja отдаёт дёшево и точно. Отклонено.

**C. Гибрид (выбран).** Ninja — дешёвый слой фактов (валюта, уники; ответы из
кэша 1ч). Trade2 — точечные запросы «сколько стоит такой предмет» через уже
работающий трейд-флоу. Быстрые сравнения вариантов rares агент делает сам из
пулов афиксов/тиров без всякой сети.

## Решения

### 1. poe.ninja poe2 API — проверено запросами 2026-11-09

- **Обмен/валюта**: `GET https://poe.ninja/poe2/api/economy/exchange/current/overview?league={l}&type={t}`
  `t` ∈ `Currency, Fragments, Abyss, UncutGems, LineageSupportGems, Essences,
  SoulCores, Idols, Runes, RitualOmens, Expedition, Delirium, Breach, Verisium`.
  ⚠ Типы во множественном числе; `Fragment`/`Rune` в единственном числе
  возвращают пусто (проверено).
- **Уники**: `GET https://poe.ninja/poe2/api/economy/stash/current/item/overview?league={l}&type={t}`
  `t` ∈ `UniqueWeapons, UniqueArmours, UniqueAccessories, UniqueFlasks,
  UniqueCharms, UniqueJewels, UniqueSanctumRelics, UniqueTablets,
  PrecursorTablets`. (Для poe2 типы с «s» — `UniqueWeapons`, не `UniqueWeapon`.)
- Поля уника: `name`, `baseType`, `primaryValue` (цена в дивинах),
  `listingCount` (сколько лотов — прокси ликвидности), `explicitModifiers`
  (текст модов!), `corrupted`, `icon`, `detailsId`. Проверено: 148 уник в
  `UniqueWeapons` лиги Forbidden Rites.
- Кэш — как у текущих курсов: 1 час на пару (league, type). Данные ninja
  обновляются ~раз в час; HTTP-кэш/ETag (~5 мин) не используем.
- Лиги — не отдельный источник: используем trade2-лиги, которые уже грузит
  `TraderWebApi.GetLeaguesAsync` (та же лига «Forbidden Rites» — проверено).
- Официальные доки: https://poe.ninja/docs/api (без SLA; «за засорение
  блокируют» — потому кэш обязателен, запросы только по кнопке агента).

### 2. TraderWebApi (PBLApp.Core/Trader/TraderWebApi.cs) — расширение

- `GetExchangeRatesAsync(league, type)` — обобщение существующего
  `GetCurrencyRatesAsync` (тот же парсер, кэш 1ч на (league,type)); старый
  метод остаётся как вызов с `type=Currency`.
- `GetUniquePricesAsync(league, type, query?)` — новый: stash item overview;
  кэш 1ч на (league,type); `query` — регистронезависимый substring по
  `name`/`baseType`, фильтрация уже на нашей стороне (данные с ninja всё равно
  кэшируются целиком).

### 3. LuaHost — новое/существующее

- **Афиксы**: `GetItemAffixes(baseName)` уже есть (`LuaHost.cs:1986`) — пул
  базы из `data.itemMods`; руны (`:2191`) и коррапшн (`:2061`) тоже. Строителю:
  обогатить ответ тирами/диапазонами из модуля мод-тиров (`LuaHostModTiers.cs`,
  `EnsureModTiers`), насколько данные позволяют; если тир в пуле не хранится —
  вернуть пул как есть, это не блокер.
- **Уники**: список уник (name, base, категория) из движка — источник тот же,
  что питает `FilteredUniques` редактора; строителю найти точный API
  (вероятно `data.uniques`). Это существование, не цены — цены уников из ninja.

### 4. Новые IPC-эндпоинты (PBLApp/Ipc/IpcServer.cs)

| Эндпоинт | Тело | Ответ | Источник |
|---|---|---|---|
| `/items/bases` | `{category?}` | `{categories:[…], bases:[{name,category,subType,…}]}` | `GetItemBasesForCategory` |
| `/items/affixes` | `{base}` | `{base, affixes:[…], runes:[…], corruption:[…]}` или `{error}` | `GetItemAffixes` + руны/коррапшн |
| `/items/uniques` | `{query?}` | `{uniques:[{name,base,category}]}` | список уник движка |
| `/prices/currency` | `{type?=Currency, query?}` | `{league,type,rates:[{name,value}]}` | `GetExchangeRatesAsync` |
| `/prices/uniques` | `{query?, type?=UniqueArmours}` | `{league,type,items:[{name,base,price,listingCount,corrupted,mods?}]}` | `GetUniquePricesAsync` |

- `/prices/item` **не делаем**: живой трейд уже достижим агентом
  (`/trader/search` → `/trader/state` — в результатах `price`, `dps`/`ehp`
  дельты, `valuePerDiv`; в стейте `loggedIn`, `busy`). Рецепт — в docs/mcp.md.
  Если позже выяснится, что весами не выразить нужный шейп — отдельная фича.
- Ошибки явные: неизвестная база/категория/тип — `{error}` с перечнем
  допустимых значений (анти-цель: агент не фантазирует).

### 5. MCP-инструменты (PBLMcp/VisualTools.cs)

`visual_item_bases`, `visual_item_affixes`, `visual_item_uniques`,
`visual_prices_currency`, `visual_prices_uniques` — тонкие обёртки, описания
самодостаточные: какой инструмент для чего (уник по имени → `visual_item_uniques`
сначала, цену — `visual_prices_uniques`; сравнение вариантов rares →
`visual_item_affixes` + рассуждение; точная цена конкретного предмета →
трейд-рецепт `/trader/*`).

### 6. Тесты

- `TraderWebApi`: fixture-JSON exchange/stash (структура ninja), кэш на
  (league,type) — повторный вызов не бьёт по сети, парсинг
  `primaryValue`/`listingCount`.
- `LuaHost`: `/items/affixes`-эквивалент на реальной базе (существующие
  паттерны `LuaHost`-тестов), поиск уник.
- IPC/MCP-обёртки тонкие — e2e агентом.

### 7. Документация

`docs/mcp.md`: новые инструменты каталогом + рецепты: «оцени бюджет билда»
(суммирует уники из ninja + валюту на крафт), «найди дешёвый уник в слот»,
«сколько стоит такой редкий предмет» (трейд-флоу с весами; ограничение —
нужен логин пользователя, `loggedIn` из `/trader/state`).

## Что остаётся строителю

- Точный Lua-источник списка уник (что питает `FilteredUniques`).
- Формат тиров в `/items/affixes` — сверить с `ModTierInfo`/`PBLModTiers`.
- Маппинг категорий `/items/bases` на категории редактора.
- Кэш в `TraderWebApi` — в существующем стиле (не переусложнять).
- Порядок коммитов: TraderWebApi → LuaHost → IPC → MCP → тесты/доки.
