using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using Dapper;
using LevelsRanksApi;
using MySqlConnector;

namespace LevelsRanksModuleUnusualKills;

// Порт LR_UnusualKills (Pisex, SourceMod/C++) на CounterStrikeSharp/C#.
// Даёт бонус опыта за "необычные" убийства: первое убийство раунда, сквозь стену,
// ноускоп, на бегу, в прыжке, вслепую, сквозь дым, последним патроном в магазине,
// с большой дистанции. Статистика по типам копится в отдельной таблице `<table>_unusualkills`.
[MinimumApiVersion(80)]
public class LevelsRanksUnusualKills : BasePlugin
{
    public override string ModuleName => "[LR] Module - UnusualKills";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "ABKAM designed by RoadSide Romeo & Wend4r";
    public override string ModuleDescription => "Бонус опыта за необычные убийства.";

    private readonly PluginCapability<ILevelsRanksApi> _levelsRanksApiCapability = new("levels_ranks");
    private ILevelsRanksApi? _levelsRanksApi;

    private static readonly string[] UnusualKillTypes =
    {
        "OpenFrag", "Penetrated", "NoScope", "Run", "Jump", "Flash", "Smoke", "LastClip", "Distance"
    };

    private UnusualKillsConfig _config = new();

    // Первое убийство раунда - общий флаг, сбрасывается на round_start.
    private bool _openFragTaken;

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        base.OnAllPluginsLoaded(hotReload);

        _levelsRanksApi = _levelsRanksApiCapability.Get();
        if (_levelsRanksApi == null)
        {
            Console.WriteLine("[LR-UK] LevelsRanksApi is not initialized. Exiting Load method.");
            return;
        }

        _config = LoadConfig();
        CreateDbTableIfNotExists();

        RegisterEventHandler<EventRoundStart>((@event, info) =>
        {
            _openFragTaken = false;
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        try
        {
            if (_levelsRanksApi == null) return HookResult.Continue;

            var attacker = @event.Attacker;
            var victim = @event.Userid;
            if (attacker == null || victim == null) return HookResult.Continue;

            var isBot = false;
            try
            {
                isBot = attacker.IsBot;
            }
            catch (ArgumentNullException)
            {
            }

            if (isBot && !_levelsRanksApi.GetExperienceFromBots()) return HookResult.Continue;

            var weapon = @event.Weapon ?? string.Empty;
            if (_config.ProhibitedWeapons.Any(w => string.Equals(w, weapon, StringComparison.OrdinalIgnoreCase)))
                return HookResult.Continue;

            var attackerSteamId = _levelsRanksApi.ConvertToSteamId(attacker.SteamID);
            if (string.IsNullOrEmpty(attackerSteamId)) return HookResult.Continue;
            if (victim.SteamID > 0 && attacker.SteamID == victim.SteamID) return HookResult.Continue; // суицид не считаем
            if (!_levelsRanksApi.OnlineUsers.TryGetValue(attackerSteamId, out var attackerUser))
                return HookResult.Continue;

            var triggered = new List<string>();

            // Первое убийство раунда.
            if (!_openFragTaken)
            {
                _openFragTaken = true;
                triggered.Add("OpenFrag");
            }

            // Сквозь стену/несколько игроков.
            if (@event.Penetrated > 0)
                triggered.Add("Penetrated");

            // Без прицела.
            if (@event.Noscope)
                triggered.Add("NoScope");

            // В прыжке.
            if (@event.Attackerinair)
                triggered.Add("Jump");

            // Вслепую (после флешки).
            if (@event.Attackerblind)
                triggered.Add("Flash");

            // Сквозь дым.
            if (@event.Thrusmoke)
                triggered.Add("Smoke");

            // Дальний выстрел (юниты движка, "distance" появилось в событии player_death в CS2).
            if (@event.Distance >= _config.MinDistance)
                triggered.Add("Distance");

            // Скорость атакующего в момент убийства.
            // Если сборка ругается на AbsVelocity - в вашей версии CounterStrikeSharp.API
            // поле может называться иначе (например Velocity); поправьте один раз тут.
            try
            {
                var pawn = attacker.PlayerPawn?.Value;
                if (pawn != null && pawn.AbsVelocity.Length() >= _config.MinSpeed)
                    triggered.Add("Run");
            }
            catch
            {
                // см. комментарий выше
            }

            // Последний патрон в магазине.
            // Аналогично - если сборка ругается на ActiveWeapon/Clip1, поправьте под свою версию API.
            try
            {
                var activeWeapon = attacker.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
                if (activeWeapon != null && activeWeapon.Clip1 == 1)
                    triggered.Add("LastClip");
            }
            catch
            {
                // см. комментарий выше
            }

            if (triggered.Count == 0) return HookResult.Continue;

            ApplyBonuses(attackerUser, attacker, triggered);

            _ = Task.Run(() => UpdatePlayerStatsAsync(attackerSteamId, triggered));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LR-UK] OnPlayerDeath error: {ex}");
        }

        return HookResult.Continue;
    }

    private void ApplyBonuses(User attackerUser, CCSPlayerController attacker, List<string> triggeredTypes)
    {
        if (_levelsRanksApi == null) return;

        // Exp_Mode = "1" (по умолчанию, как в оригинале): каждый тип - отдельное сообщение и начисление.
        // Exp_Mode = "0": все бонусы суммируются в одно начисление с одним сообщением (меньше спама в чат).
        if (_config.ExpMode == "1")
        {
            foreach (var type in triggeredTypes)
            {
                var exp = _config.GetExp(type);
                if (exp == 0) continue;

                var sign = exp > 0 ? "+" : "";
                var message = Localizer["message.single", Localizer[$"type.{type}"], sign, exp];
                _levelsRanksApi.ApplyExperienceUpdateSync(attackerUser, attacker, exp, message, "green");
            }
        }
        else
        {
            var total = triggeredTypes.Sum(_config.GetExp);
            if (total == 0) return;

            var namesJoined = string.Join(" + ", triggeredTypes
                .Where(t => _config.GetExp(t) != 0)
                .Select(t => Localizer[$"type.{t}"]));
            var sign = total > 0 ? "+" : "";
            var message = Localizer["message.combined", namesJoined, sign, total];
            _levelsRanksApi.ApplyExperienceUpdateSync(attackerUser, attacker, total, message, "green");
        }
    }

    private async Task UpdatePlayerStatsAsync(string steamId, List<string> triggeredTypes)
    {
        if (_levelsRanksApi == null || triggeredTypes.Count == 0) return;

        var connectionString = _levelsRanksApi.DbConnectionString;
        var tableName = $"{_levelsRanksApi.TableName}_unusualkills";

        var setClauses = string.Join(", ", triggeredTypes.Distinct().Select(t => $"`{t}` = `{t}` + 1"));

        var query = $@"
INSERT INTO `{tableName}` (`SteamID`, `ServerID`)
VALUES (@SteamID, @ServerID)
ON DUPLICATE KEY UPDATE {setClauses};";

        try
        {
            using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(query, new { SteamID = steamId, ServerID = _levelsRanksApi.ServerId });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LR-UK] Failed to update stats for {steamId}: {ex}");
        }
    }

    private void CreateDbTableIfNotExists()
    {
        if (_levelsRanksApi == null) return;

        var connectionString = _levelsRanksApi.DbConnectionString;
        var tableName = $"{_levelsRanksApi.TableName}_unusualkills";
        var columns = string.Join(", ", UnusualKillTypes.Select(t => $"`{t}` int NOT NULL DEFAULT 0"));

        var createTableQuery = $@"
            CREATE TABLE IF NOT EXISTS `{tableName}`
            (
                `SteamID` varchar(32) NOT NULL DEFAULT '',
                `ServerID` varchar(64) NOT NULL DEFAULT 'default',
                {columns},
                PRIMARY KEY (`SteamID`, `ServerID`)
            ) CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

        using var connection = new MySqlConnection(connectionString);
        connection.Open();
        connection.Execute(createTableQuery);
    }

    // Локальный загрузчик конфига (модули не ссылаются на сборку Core, поэтому у каждого
    // модуля своя копия этой логики): файла нет -> создаём с дефолтами; файл есть, но не
    // хватает каких-то ключей -> дописываем недостающее, не трогая то, что уже настроено.
    private static UnusualKillsConfig LoadConfig()
    {
        var configDirectory = Path.Combine(Application.RootDirectory, "configs/plugins/LevelsRanks");
        var filePath = Path.Combine(configDirectory, "settings_unusualkills.json");
        var options = new JsonSerializerOptions { WriteIndented = true };

        if (!File.Exists(filePath))
        {
            var defaultConfig = new UnusualKillsConfig();
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(filePath, JsonSerializer.Serialize(defaultConfig, options));
            return defaultConfig;
        }

        try
        {
            var existingNode = JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject ?? new JsonObject();
            var defaultNode = JsonSerializer.SerializeToNode(new UnusualKillsConfig(), options) as JsonObject
                               ?? new JsonObject();

            if (MergeMissingKeys(existingNode, defaultNode))
                File.WriteAllText(filePath, existingNode.ToJsonString(options));

            return existingNode.Deserialize<UnusualKillsConfig>(options) ?? new UnusualKillsConfig();
        }
        catch (JsonException)
        {
            return new UnusualKillsConfig();
        }
    }

    private static bool MergeMissingKeys(JsonObject target, JsonObject source)
    {
        var changed = false;
        foreach (var kvp in source)
        {
            if (!target.ContainsKey(kvp.Key))
            {
                target[kvp.Key] = kvp.Value?.DeepClone();
                changed = true;
            }
            else if (target[kvp.Key] is JsonObject targetChild && kvp.Value is JsonObject sourceChild)
            {
                if (MergeMissingKeys(targetChild, sourceChild))
                    changed = true;
            }
        }

        return changed;
    }
}

public class UnusualKillsConfig
{
    /// <summary>
    /// "1" - каждый тип необычного убийства даёт своё сообщение и своё начисление опыта (как в оригинале).
    /// "0" - все сработавшие типы за одно убийство суммируются в одно начисление/сообщение.
    /// </summary>
    public string ExpMode { get; set; } = "1";

    /// <summary>Минимальная дистанция убийства (юниты движка), чтобы засчитать тип "Distance".</summary>
    public double MinDistance { get; set; } = 1200;

    /// <summary>Минимальная скорость атакующего (юниты/сек) в момент убийства для типа "Run".</summary>
    public double MinSpeed { get; set; } = 150;

    /// <summary>
    /// Оружие/классы килла, которые не считаются "необычным убийством" (например, нож).
    /// Значения сравниваются с полем "weapon" события player_death (без префикса weapon_).
    /// </summary>
    public List<string> ProhibitedWeapons { get; set; } = new() { "knife" };

    public Dictionary<string, int> Exp { get; set; } = new()
    {
        { "OpenFrag", 2 },
        { "Penetrated", 2 },
        { "NoScope", 3 },
        { "Run", 1 },
        { "Jump", 1 },
        { "Flash", 2 },
        { "Smoke", 2 },
        { "LastClip", 2 },
        { "Distance", 2 }
    };

    public int GetExp(string type) => Exp.TryGetValue(type, out var value) ? value : 0;
}
