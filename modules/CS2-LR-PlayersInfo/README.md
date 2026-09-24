# [LR] Module - PlayersInfo

Модуль LevelsRanks: порт плагина **PlayersInfo** (Metamod, C++, автор Pisex) на CounterStrikeSharp,
дополненный возможностями из форка **FluteCS2PlayersList** (player_count, alive, `mm_getinfo_slot`,
`mm_getinfo_file`, фильтр ботов, кэш Prime).

Три консольные команды, все только из консоли сервера / RCON (игрокам недоступны):

| Команда | Что делает |
|---|---|
| `mm_getinfo` | Полный отчёт: сервер + все реальные подключённые игроки, одной строкой JSON |
| `mm_getinfo_slot <N>` | JSON одного игрока по номеру слота, либо `{}` если слот пуст/бот/вне диапазона |
| `mm_getinfo_file <path>` | То же, что `mm_getinfo`, но атомарно пишет в файл (для раздачи статикой через nginx) |

## Формат вывода

```json
{"current_map":"de_dust2","player_count":1,"players":[{"alive":true,"death":0,"headshots":1,"kills":2,"name":"Flames \uD83D\uDC7E","ping":1,"playtime":6,"prime":true,"rank":42,"steamid":"76561198295345385","team":3,"userid":2}],"score_ct":2,"score_t":0,"time":1758800000}
```

| Поле | Значение |
|---|---|
| `time` | unix time |
| `current_map` | текущая карта |
| `score_ct`, `score_t` | счёт команд |
| `player_count` | длина `players` |
| `players` | список игроков; `null`, если список пуст (как у оригинала Pisex; форк отдаёт `[]`) |
| `userid` | номер слота |
| `team` | 2 = T, 3 = CT |
| `kills`, `death`, `headshots` | статистика **текущего матча** (обнуляется движком на новой карте/раунде, это не накопительная статистика LevelsRanks) |
| `ping` | пинг |
| `playtime` | секунды с момента подключения (см. настройки) |
| `prime` | Prime-статус (лицензия на app 624820 или 54029), результат кэшируется по SteamID |
| `alive` | есть ли у игрока живой pawn |
| `rank` | ранг игрока в LevelsRanks (`User.Rank`); поле отсутствует, если ядро недоступно или игрок ещё не загружен ядром (боты и т.п. сюда всё равно не попадают - см. ниже) |
| `exp` | опыт игрока (`User.Value`), только если включён `IncludeExperience` |

**Боты и HLTV в список не попадают** (`controller.IsBot`), как в форке. Ключи выводятся в алфавитном порядке.

### Отличие от оригинала на C++

`System.Text.Json` всегда экранирует символы вне BMP (большинство эмодзи) как `\uXXXX\uYYYY`,
в отличие от `nlohmann::json`, который пишет их сырыми UTF-8 байтами. Это валидный JSON, любой
парсер (`JSON.parse`, `json.loads` и т.д.) декодирует его в тот же символ - разница чисто в
побайтовом представлении, не в данных.

## Установка

Собирается вместе со всем репозиторием (`.github/workflows/build.yml`) и попадает в релиз как
`addons/counterstrikesharp/plugins/LR_Module_PlayersInfo/`. Нужен установленный LevelsRanks Core
(`plugins/LevelsRanksCore` + `shared/LevelsRanksApi`).

Локальная сборка:

```bash
dotnet build "modules/CS2-LR-PlayersInfo/[LR] Module - PlayersInfo.csproj" -c Release
```

## Настройки

Файл создаётся при первой загрузке: `addons/counterstrikesharp/configs/plugins/LR_Module_PlayersInfo/LR_Module_PlayersInfo.json`

| Параметр | По умолчанию | Описание |
|---|---|---|
| `ResetPlaytimeOnMapStart` | `true` | как в оригинале: `playtime` обнуляется при каждой смене карты |
| `IncludePlayersWithoutPawn` | `false` | как в оригинале: игроки без pawn (спектаторы, подключающиеся) пропускаются в `mm_getinfo` |
| `IncludeExperience` | `false` | добавить в JSON поле `exp` |

## Веб-интеграция

Как и у форка, три варианта:

- **RCON pull** — веб-приложение шлёт `mm_getinfo` по RCON и парсит ответ как JSON.
- **Файл + статика** — по крону/расписанию дергать `mm_getinfo_file /path/to/stats.json` по RCON,
  раздавать файл через nginx/Apache. Подходит, если сайт и игровой сервер на разных хостах.
- **`mm_getinfo_slot <N>`** — точечный опрос одного игрока (дешевле полного дампа), для виджетов
  вида «карточка игрока».

## Ограничения

- Prime проверяется вызовом Steam API напрямую (P/Invoke в `libsteam_api`, `SteamLicense.cs`).
  Результат кэшируется по SteamID и сбрасывается на старте карты. Если привязка не удалась,
  в лог пишется предупреждение, а `prime` всегда `false`.
- В отличие от нативной C++ версии, вывод `mm_getinfo` в консоль не разбивается на чанки:
  интероп CounterStrikeSharp передаёт строки целиком, а не через printf-подобный буфер с фиксированным
  размером, так что риска обрезки в духе `META_CONPRINTF` тут ниже. Если всё же увидишь обрезанный
  вывод на сервере с большим числом игроков - используй `mm_getinfo_file` вместо `mm_getinfo`,
  у файловой записи такого ограничения нет в принципе.

## Лицензия

Оригинальный плагин распространяется под GPL; этот модуль — производная работа.
Оригиналы: PlayersInfo (cs2-PlayersListCommand) и FluteCS2PlayersList, автор Pisex.
