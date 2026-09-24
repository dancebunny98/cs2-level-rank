# [LR] Module - PlayersInfo

Модуль LevelsRanks: порт плагина **PlayersInfo** (Metamod, C++, автор Pisex) на CounterStrikeSharp.
Консольная команда `mm_getinfo` печатает одну строку JSON с информацией о сервере и игроках,
включая **ранг из LevelsRanks**. Команда работает только из консоли сервера / RCON.

## Формат вывода

```json
{"current_map":"de_dust2","players":[{"death":2,"headshots":1,"kills":5,"name":"Nick","ping":23,"playtime":123,"prime":true,"rank":7,"steamid":"76561198000000000","team":3,"userid":3}],"score_ct":4,"score_t":7,"time":1758800000}
```

| Поле | Значение |
|---|---|
| `time` | unix time |
| `current_map` | текущая карта |
| `score_ct`, `score_t` | счёт команд |
| `players` | список игроков, `null` если список пуст (как в оригинале) |
| `userid` | номер слота |
| `team` | 2 = T, 3 = CT |
| `kills`, `death`, `headshots` | статистика матча |
| `ping` | пинг |
| `playtime` | секунды с момента подключения (см. настройки) |
| `prime` | Prime-статус (лицензия на app 624820 или 54029) |
| `rank` | ранг игрока в LevelsRanks (`User.Rank`); `0`, если ядро ещё не загрузило игрока (боты, идёт загрузка из БД) |
| `exp` | опыт игрока (`User.Value`), только если включён `IncludeExperience` |

Если ядро LevelsRanks недоступно, поля `rank`/`exp` не выводятся. Ключи идут в алфавитном порядке, как у оригинала.

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
| `IncludePlayersWithoutPawn` | `false` | как в оригинале: игроки без pawn (спектаторы, подключающиеся) пропускаются |
| `IncludeExperience` | `false` | добавить в JSON поле `exp` |

## Ограничения

- Prime проверяется вызовом Steam API напрямую (P/Invoke в `libsteam_api`, `SteamLicense.cs`).
  Если привязка не удалась, в лог пишется предупреждение, а `prime` всегда `false`.

## Лицензия

Оригинальный плагин распространяется под GPL; этот модуль — производная работа.
Оригинал: PlayersInfo (cs2-PlayersListCommand), автор Pisex.
