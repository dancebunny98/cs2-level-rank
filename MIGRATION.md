# Миграция базы данных на server_id

Начиная с этой версии Core и модули ExStats-Hits/ExStats-Weapons хранят статистику
с привязкой к `server_id` (настройка `lr_server_id` в `settings.json`), чтобы
несколько серверов CS2 могли делить одну базу без смешивания статистики и без
создания отдельного пользователя под каждый сервер.

При старте плагин **пытается сделать миграцию автоматически** (добавить колонку
`server_id` и пересобрать PRIMARY KEY). Если у пользователя БД недостаточно прав
(нет `ALTER`), в логе появится ошибка вида
`Automatic server_id migration for ... failed` — в этом случае выполните SQL ниже
вручную.

Все существующие строки получат `server_id = 'default'`. Если в `settings.json`
на этом сервере вы оставите `lr_server_id` по умолчанию (`"default"`), вся текущая
статистика продолжит работать без изменений. Если хотите задать конкретному
серверу отдельный id — либо сразу назначьте его равным `'default'` для "основного"
сервера, либо перенесите нужные строки на новый `server_id` вручную.

```sql
-- Таблица Core (имя из lr_table, по умолчанию lvl_base)
ALTER TABLE `lvl_base`
    ADD COLUMN `server_id` VARCHAR(64) NOT NULL DEFAULT 'default' AFTER `steam`,
    DROP PRIMARY KEY,
    ADD PRIMARY KEY (`steam`, `server_id`),
    ADD KEY `idx_server_value` (`server_id`, `value`);

-- Модуль ExStats-Hits (таблица <lr_table>_hits, например lvl_base_hits)
ALTER TABLE `lvl_base_hits`
    ADD COLUMN `ServerID` VARCHAR(64) NOT NULL DEFAULT 'default' AFTER `SteamID`,
    DROP PRIMARY KEY,
    ADD PRIMARY KEY (`SteamID`, `ServerID`);

-- Модуль ExStats-Weapons (таблица <lr_table>_weapons)
ALTER TABLE `lvl_base_weapons`
    ADD COLUMN `server_id` VARCHAR(64) NOT NULL DEFAULT 'default' AFTER `steam`,
    DROP PRIMARY KEY,
    ADD PRIMARY KEY (`steam`, `server_id`, `classname`);
```

Замените `lvl_base` / `lvl_base_hits` / `lvl_base_weapons` на реальные имена ваших
таблиц, если меняли `lr_table` в конфиге.

## Как назначить server_id

В `settings.json` каждого сервера (Core) добавьте/поменяйте:

```json
"lr_server_id": "server1"
```

Каждому серверу, который делит одну БД с остальными — свой уникальный id.
Если сервер один и тот же, но БД раздельные (разные схемы/базы под каждый
сервер) — можно оставить `"default"` везде, это ни на что не повлияет.
