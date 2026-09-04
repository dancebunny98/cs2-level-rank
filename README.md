# LevelsRanks (LVL Core)

## Структура

- `Core/` — плагин **LVL Core**, реализация. Собирается в `LVLCore.dll`.
  Использует контракт из `Api/`, но не содержит его исходников в себе.
- `Api/` — отдельный проект `LevelsRanksApi.csproj`, контракт (`ILevelsRanksApi`,
  `User`). Собирается в `LevelsRanksApi.dll` — это разделяемая (`shared/`) сборка,
  через которую Core и модули общаются друг с другом.
- `modules/` — модули (FakeRank, ExStats-Hits, ExStats-Weapons, Tag, VIP-*).
  Каждый — отдельный плагин, ссылается только на `Api/LevelsRanksApi.csproj`
  (реализация Core им для компиляции не нужна — они видят только интерфейс).
- `libs/` — сюда нужно вручную положить сторонние DLL (`VipCoreApi.dll`), от
  которых зависят VIP-модули. Без этого файла VIP-модули просто не будут
  собраны CI (остальное соберётся штатно).
- `.github/workflows/build.yml` — сборка на .NET 8 через GitHub Actions.
- `LevelsRanks.sln` — решение с Api, Core и всеми не-VIP модулями (используется CI).

## Сборка

```bash
dotnet build LevelsRanks.sln -c Release
# VIP-модули (нужен libs/VipCoreApi.dll):
dotnet build "modules/CS2-VIP-LR-ExperienceMultiplier/[VIP] [LR] ExperienceMultiplier.csproj" -c Release
dotnet build "modules/CS2_VIP_LR_CustomFakeRank/VIP_CustomFakeRank.csproj" -c Release
```

Через GitHub Actions — просто push/PR, готовый артефакт (структура
`addons/counterstrikesharp/...`) появится во вкладке Actions → Artifacts,
а на теге `vX.Y.Z` — ещё и в Releases.

## ⚠️ Установка на сервер — важно про shared/

`LevelsRanksApi.dll` (контракт `ILevelsRanksApi`) и `FakeRanksApi.dll` (контракт
`IPlayerRankApi`) — это **общие контракты**, через которые Core и модули общаются
друг с другом (`PluginCapability<T>`). CounterStrikeSharp требует,
чтобы такие сборки грузились **один раз, из одного места**, а не отдельной
копией в папке каждого плагина — иначе привязка API между плагинами
падает с ошибкой приведения типов, потому что технически это будут разные
сборки, просто с одинаковым содержимым. Это касается и самого Core — он тоже
не хранит свою копию `LevelsRanksApi.dll`, а резолвит её из `shared/`.

Поэтому итоговая раскладка на сервере должна выглядеть так:

```
addons/counterstrikesharp/
├── shared/
│   ├── LevelsRanksApi/LevelsRanksApi.dll
│   └── FakeRanksApi/FakeRanksApi.dll
└── plugins/
    ├── LVL Core/                (LVLCore.dll + прочие зависимости Core)
    ├── LR Module - FakeRank/
    ├── LR Module - ExStats Hits/
    ├── LR Module - ExStats Weapons/
    ├── LR Module - Tag/
    ├── VIP LR ExperienceMultiplier/   (если собирали)
    └── VIP CustomFakeRank/            (если собирали)
```

Именно такую структуру и собирает `build.yml` — просто скопируйте содержимое
артефакта `addons/counterstrikesharp/` в соответствующую папку игрового сервера.
Если раскладываете вручную — не копируйте `LevelsRanksApi.dll`/`FakeRanksApi.dll`
в `plugins/*/` (включая саму `LVL Core/`), там их быть не должно (сборка это уже
гарантирует через `Private=false` в csproj, локальных копий там не появится).

## Работа с несколькими серверами (server_id)

В `settings.json` (Core) есть `lr_server_id` (по умолчанию `"default"`).
Если несколько серверов CS2 используют одну и ту же базу данных — задайте
каждому серверу свой уникальный `lr_server_id`. Статистика (Core, ExStats-Hits,
ExStats-Weapons, кастомные ранги FakeRank) будет вестись раздельно по каждому
серверу, при этом steam-аккаунт игрока остаётся одним и тем же — заводить
отдельного "пользователя" под каждый сервер не нужно.

Если у вас и так отдельная БД/таблица под каждый сервер — можно ничего не
трогать, оставить везде `"default"`, поведение не изменится.

Подробности миграции существующих таблиц — в [MIGRATION.md](MIGRATION.md).
