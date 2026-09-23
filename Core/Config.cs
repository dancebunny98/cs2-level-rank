using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace LevelsRanks;

public class ConfigLoader<T> where T : new()
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T Load(string path) => Load(path, () => new T());

    /// <summary>
    /// Загружает конфиг из файла. Если файла нет - создаёт его с дефолтами (createDefault).
    /// Если файл уже есть, но в нём не хватает каких-то ключей (плагин обновился и появились
    /// новые настройки) - дописывает в файл только отсутствующие ключи с их дефолтными
    /// значениями, не трогая то, что уже настроено пользователем.
    /// </summary>
    public static T Load(string path, Func<T> createDefault)
    {
        if (!File.Exists(path))
        {
            var config = createDefault();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
            return config;
        }

        JsonObject existingNode;
        try
        {
            existingNode = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            // Повреждённый JSON - не перетираем файл автоматически, просто отдаём дефолты в память.
            return createDefault();
        }

        var defaultNode = JsonSerializer.SerializeToNode(createDefault(), Options) as JsonObject ?? new JsonObject();

        if (MergeMissingKeys(existingNode, defaultNode))
            File.WriteAllText(path, existingNode.ToJsonString(Options));

        return existingNode.Deserialize<T>(Options) ?? createDefault();
    }

    // Рекурсивно добавляет в target ключи, которых там нет, но которые есть в source (дефолтах).
    // Существующие значения никогда не перезаписываются.
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

public static class ExperienceSettings
{
    private static Dictionary<string, double> _experienceCache = new();
    public static ExperienceConfig Experience { get; private set; } = new();
    private static ILogger? _logger;

    public static void Initialize(ILogger? logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static void Load(string settingsFilePath)
    {
        if (_logger == null)
            throw new InvalidOperationException("Logger not initialized. Call Initialize() method first.");

        Experience = ConfigLoader<ExperienceConfig>.Load(settingsFilePath);
    }

    public static double GetExperience(string statisticType, string key)
    {
        var cacheKey = $"{statisticType}_{key}";
        if (_experienceCache.TryGetValue(cacheKey, out var cachedValue)) return cachedValue;

        var value = statisticType switch
        {
            "0" => Experience.Funded_System.TryGetValue(key, out var val) ? val : 0,
            "1" => Experience.Rating_Extended.TryGetValue(key, out var val) ? val : 0.0,
            "2" => Experience.Rating_Simple.TryGetValue(key, out var val) ? val : 0,
            _ => 0
        };

        _experienceCache[cacheKey] = value;
        return value;
    }
}

public class ExperienceConfig
{
    public Dictionary<string, double> Funded_System { get; set; } = new()
    {
        { "lr_kill", 5 },
        { "lr_death", -5 },
        { "lr_headshot", 1 },
        { "lr_assist", 1 },
        { "lr_suicide", -6 },
        { "lr_teamkill", -6 },
        { "lr_winround", 2 },
        { "lr_loseround", -2 },
        { "lr_mvpround", 3 },
        { "lr_bombplanted", 2 },
        { "lr_bombdefused", 2 },
        { "lr_bombdropped", -1 },
        { "lr_bombpickup", 1 },
        { "lr_hostagekilled", -4 },
        { "lr_hostagerescued", 3 }
    };

    public Dictionary<string, double> Rating_Extended { get; set; } = new()
    {
        { "lr_killcoeff", 1.0 },
        { "lr_headshot", 1 },
        { "lr_assist", 1 },
        { "lr_suicide", -10 },
        { "lr_teamkill", -5 },
        { "lr_winround", 2 },
        { "lr_loseround", -2 },
        { "lr_mvpround", 1 },
        { "lr_bombplanted", 3 },
        { "lr_bombdefused", 3 },
        { "lr_bombdropped", -2 },
        { "lr_bombpickup", 2 },
        { "lr_hostagekilled", -20 },
        { "lr_hostagerescued", 5 }
    };

    public Dictionary<string, double> Rating_Simple { get; set; } = new()
    {
        { "lr_headshot", 1 },
        { "lr_assist", 1 },
        { "lr_suicide", 0 },
        { "lr_teamkill", 0 },
        { "lr_winround", 2 },
        { "lr_loseround", -2 },
        { "lr_mvpround", 1 },
        { "lr_bombplanted", 2 },
        { "lr_bombdefused", 2 },
        { "lr_bombdropped", -1 },
        { "lr_bombpickup", 1 },
        { "lr_hostagekilled", 0 },
        { "lr_hostagerescued", 2 }
    };

    public Dictionary<string, double> Special_Bonuses { get; set; } = new()
    {
        { "lr_bonus_1", 2 },
        { "lr_bonus_2", 3 },
        { "lr_bonus_3", 4 },
        { "lr_bonus_4", 5 },
        { "lr_bonus_5", 6 },
        { "lr_bonus_6", 7 },
        { "lr_bonus_7", 8 },
        { "lr_bonus_8", 9 },
        { "lr_bonus_9", 10 },
        { "lr_bonus_10", 11 },
        { "lr_bonus_11", 12 }
    };
}

public static class RanksSettings
{
    public static Dictionary<int, RankConfig> Ranks { get; private set; } = new();

    public static void Load(string settingsFilePath)
    public static void Load(string settingsFilePath)
    {
        Ranks = ConfigLoader<Dictionary<int, RankConfig>>.Load(settingsFilePath, CreateDefaultRanks);
    }

    private static Dictionary<int, RankConfig> CreateDefaultRanks()
    {
        return new Dictionary<int, RankConfig>
        {
            { 1, new RankConfig() },
            { 2, new RankConfig { Value0 = 10, Value1 = 700, Value2 = 850 } },
            { 3, new RankConfig { Value0 = 25, Value1 = 800, Value2 = 900 } },
            { 4, new RankConfig { Value0 = 50, Value1 = 850, Value2 = 935 } },
            { 5, new RankConfig { Value0 = 75, Value1 = 900, Value2 = 950 } },
            { 6, new RankConfig { Value0 = 100, Value1 = 925, Value2 = 965 } },
            { 7, new RankConfig { Value0 = 150, Value1 = 950, Value2 = 980 } },
            { 8, new RankConfig { Value0 = 200, Value1 = 975, Value2 = 990 } },
            { 9, new RankConfig { Value0 = 300, Value1 = 1000, Value2 = 1000 } },
            { 10, new RankConfig { Value0 = 500, Value1 = 1100, Value2 = 1050 } },
            { 11, new RankConfig { Value0 = 750, Value1 = 1250, Value2 = 1100 } },
            { 12, new RankConfig { Value0 = 1000, Value1 = 1400, Value2 = 1200 } },
            { 13, new RankConfig { Value0 = 1500, Value1 = 1600, Value2 = 1300 } },
            { 14, new RankConfig { Value0 = 2000, Value1 = 1800, Value2 = 1400 } },
            { 15, new RankConfig { Value0 = 3000, Value1 = 2100, Value2 = 1550 } },
            { 16, new RankConfig { Value0 = 5000, Value1 = 2400, Value2 = 1750 } },
            { 17, new RankConfig { Value0 = 7500, Value1 = 3000, Value2 = 2000 } },
            { 18, new RankConfig { Value0 = 10000, Value1 = 4000, Value2 = 2500 } }
        };
    }

    public static int GetRankForExperience(int experience, string statisticType)
    {
        foreach (var rank in Ranks.OrderByDescending(r => r.Key))
        {
            var value = statisticType switch
            {
                "0" => rank.Value.Value0,
                "1" => rank.Value.Value1,
                "2" => rank.Value.Value2,
                _ => int.MaxValue
            };

            if (experience >= value) return rank.Key;
        }

        return 1;
    }
}

public class RankConfig
{
    public int Value0 { get; set; } = 0;
    public int Value1 { get; set; } = 0;
    public int Value2 { get; set; } = 0;
}

public class MainSettings
{
    public string? lr_table { get; set; } = "lvl_base";

    /// <summary>
    /// Идентификатор сервера. Если несколько серверов CS2 используют одну и ту же
    /// базу/таблицу, задайте каждому серверу своё уникальное значение (например
    /// "server1", "server2"), чтобы статистика (Core и модули) не смешивалась и
    /// не требовалось заводить отдельного пользователя под каждый сервер.
    /// </summary>
    public string lr_server_id { get; set; } = "default";
    public string lr_type_statistics { get; set; } = "0";
    public string lr_flag_adminmenu { get; set; } = "@lr/admin";
    public string lr_plugin_title { get; set; } = "Levels Ranks v1.1.0";
    public string lr_sound { get; set; } = "1";
    public string lr_sound_lvlup { get; set; } = "sounds/levels_ranks/levelup.vsnd_c";
    public string lr_sound_lvldown { get; set; } = "sounds/levels_ranks/leveldown.vsnd_c";
    public string lr_show_resetmystats { get; set; } = "1";
    public string lr_resetmystats_cooldown { get; set; } = "86400";
    public string lr_minplayers_count { get; set; } = "4";
    public string lr_show_usualmessage { get; set; } = "1";
    public string lr_show_spawnmessage { get; set; } = "1";
    public string lr_show_levelup_message { get; set; } = "1";
    public string lr_show_leveldown_message { get; set; } = "1";
    public string lr_show_rankmessage { get; set; } = "1";
    public string lr_show_ranklist { get; set; } = "1";
    public string lr_giveexp_roundend { get; set; } = "1";
    public string lr_block_warmup { get; set; } = "1";
    public string lr_allagainst_all { get; set; } = "1";
    public string lr_experience_from_bots { get; set; } = "0";

    /// <summary>Количество игроков, выводимых по командам !top и !toptime.</summary>
    public string lr_top_count { get; set; } = "10";

    /// <summary>
    /// Стартовые очки опыта при первом заходе игрока на сервер. Применяется только
    /// для накопительной системы (lr_type_statistics = "0"); рейтинговые системы
    /// (1 и 2) всегда стартуют с 1000, как и в оригинальном плагине.
    /// </summary>
    public string lr_start_points { get; set; } = "0";

    /// <summary>
    /// Через сколько дней отсутствия игрока скрывать его из топов/статистики
    /// (не удаляя саму запись из БД). 0 - отключить автоочистку.
    /// </summary>
    public string lr_cleandb_days { get; set; } = "30";

    /// <summary>
    /// Как часто сохранять данные игрока в БД:
    /// "0" - только при выходе игрока с сервера (меньше нагрузка на БД);
    /// "1" - также каждые 5 секунд батчем, при смене ранга и в конце раунда (актуальные данные).
    /// </summary>
    public string lr_db_savedataplayer_mode { get; set; } = "1";
}

public class DatabaseConnection
{
    public string Database { get; set; } = "levelsranks";
    public string User { get; set; } = "root";
    public string Password { get; set; } = "";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
}