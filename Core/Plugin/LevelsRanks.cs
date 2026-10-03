using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using MenuManager;
using LevelsRanksApi;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Timer = System.Threading.Timer;

namespace LevelsRanks;

[MinimumApiVersion(80)]
public partial class LevelsRanks : BasePlugin
{
    public override string ModuleName => "[LevelsRanks] Core";
	public override string ModuleAuthor => "ABKAM designed by RoadSide Romeo & Wend4r";
    public override string ModuleVersion => $"v{typeof(LevelsRanks).Assembly.GetName().Version?.ToString(3) ?? "1.2.0"}";
    public DatabaseConnection DatabaseConnection { get; set; } = null!;
    public Database Database { get; set; } = null!;
    public string? DbConnectionString = string.Empty;
    private bool IsRoundEnded;
    public bool ExperienceFromBots { get; set; }
    private string StatisticType { get; set; } = "0";
    public string? TableName { get; set; } = "lvl_base";
    public string ServerId { get; set; } = "default";
    public int MinPlayersCount { get; set; } = 4;
    private bool ShowSpawnMessage { get; set; } = true;
    public int ShowUsualMessage { get; set; } = 1;
    public bool BlockExpDuringWarmup { get; set; }
    public bool GiveExpOnRoundEnd { get; set; }
    public bool AllAgainstAll { get; set; }
    public bool ShowLevelUpMessage { get; set; }
    public bool ShowLevelDownMessage { get; set; }
    public bool PlaySound { get; set; }
    public string SoundLvlUp { get; set; } = null!;
    public string SoundLvlDown { get; set; } = null!;
    public bool ShowRankMessage { get; set; }
    public bool ShowRankList { get; set; }
    public string AdminMenuFlag { get; private set; } = "@lr/admin";
    public string PluginTitle { get; set; } = "Levels Ranks v1.1.0";
    public int TopCount { get; set; } = 10;
    public int StartPoints { get; set; }
    public int CleanDbDays { get; set; } = 30;
    public bool SaveDataPlayerModeLive { get; set; } = true;
    public ConcurrentDictionary<string, User> OnlineUsers { get; set; } = new();
    private readonly ConcurrentDictionary<string, int> _killStreaks = new();

    public readonly List<(string Name, Action<CCSPlayerController> Action)> CustomMenuOptions = new();

    private readonly ConcurrentDictionary<string, int>
        _roundExpChanges = new();

    private ResetStatsCooldown _resetStatsCooldown = new();
    private string _resetStatsCooldownFilePath = null!;
    private bool _showResetMyStats;
    private long _resetMyStatsCooldown;
    private readonly ConcurrentDictionary<string, double> _experienceMultipliers = new();
    private readonly DirtyUserSet _userUpdateQueue = new();
    private readonly SemaphoreSlim _queueGate = new(1, 1);

    // Игроки, которые уже вышли (или сменили карту), но чьё сохранение в БД ещё не завершилось.
    // Пока запись не дошла до БД, именно этот объект - самая свежая версия данных игрока.
    private readonly ConcurrentDictionary<string, User> _pendingSaves = new();
    private readonly
        ConcurrentQueue<(User user, CCSPlayerController player, int expChange, string eventDescription, char color)>
        _expChangeQueue = new();

    private IMenuApi? _api;
    private readonly PluginCapability<IMenuApi?> _pluginCapability = new("menu:nfcore");
    public double _experienceMultiplier = 1.0;
    private Dictionary<string, double> _playerExperienceMultipliers = new();
    private ILevelsRanksApi? _rankapi;
    private readonly PluginCapability<ILevelsRanksApi> _rankpluginCapability = new("levels_ranks");

    private readonly ILoggerFactory LoggerFactory;

    private readonly
        Queue<(CCSPlayerController Player, string Color, int NewExp, int ExpChange, string EventDescription)>
        _messageQueue = new();

    private readonly TimeSpan batchInterval = TimeSpan.FromSeconds(5);
    private DateTime lastFlushTime = DateTime.UtcNow;

    public LevelsRanks(ILogger<LevelsRanks> logger, ILoggerFactory loggerFactory)
    {
        Logger = logger;
        LoggerFactory = loggerFactory;
    }

    public override void Load(bool hotReload)
    {
        LoadConfig();

        DbConnectionString = Database.BuildConnectionString(DatabaseConnection);

        var databaseLogger = LoggerFactory.CreateLogger<Database>();

        Database = new Database(this, DbConnectionString, TableName, ServerId, databaseLogger);

        var levelsRanksApiLogger = LoggerFactory.CreateLogger<LevelsRanksApi>();
        _rankapi = new LevelsRanksApi(this, levelsRanksApiLogger);
        Capabilities.RegisterPluginCapability(_rankpluginCapability, () => _rankapi);

        // Инициализация БД не блокирует загрузку игровых обработчиков и сама
        // продолжает попытки до восстановления MySQL.
        _ = Database.StartHealthCheckAsync();


        // lr_db_savedataplayer_mode = "1" (по умолчанию): батчим сохранение каждые 5 секунд,
        // плюс сохраняем при смене ранга/выходе/конце карты - актуальные данные.
        // lr_db_savedataplayer_mode = "0": не флашим очередь по таймеру, копим изменения в
        // памяти и сохраняем только при выходе игрока/смене карты - меньше нагрузка на БД.
        if (SaveDataPlayerModeLive)
            AddTimer(5.0f, async () => await ProcessUserUpdateQueue(), TimerFlags.REPEAT);
        AddTimer(60.0f, async () => await UpdateOnlineUserPlaytime(), TimerFlags.REPEAT);

        if (CleanDbDays > 0)
        {
            AddTimer(10.0f, async () => await Database.CleanupInactiveUsersAsync(CleanDbDays));
            AddTimer(24 * 60 * 60f, async () => await Database.CleanupInactiveUsersAsync(CleanDbDays),
                TimerFlags.REPEAT);
        }

        Task.Run(ReauthorizeOnlinePlayers);

        RegisterEventHandlers();
        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            // На конце карты сохраняем все актуальные объекты. Запись проходит через тот же
            // шлюз, что и сохранения очереди/выхода игрока, чтобы старый снимок не затёр новый.
            var snapshot = OnlineUsers.Values
                .Concat(_pendingSaves.Values)
                .Concat(_userUpdateQueue.Drain())
                .Distinct()
                .ToList();
            if (snapshot.Count == 0) return;

            _ = SaveUsersImmediatelyAsync(snapshot, "map end");
        });
    }

    public override void Unload(bool hotReload)
    {
        // Остановка сервера / перезагрузка плагина: сохраняем всё, что ещё не дошло до БД.
        try
        {
            var snapshot = OnlineUsers.Values.Concat(_pendingSaves.Values).Distinct().ToList();
            snapshot.AddRange(_userUpdateQueue.Drain().Where(u => !snapshot.Contains(u)).ToList());
            if (snapshot.Count > 0)
                Task.Run(() => SaveUsersImmediatelyAsync(snapshot, "plugin unload"))
                    .Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to save players on unload: {ex}");
        }
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        ResolveMenuApi();
        AddTimer(2.0f, ResolveMenuApi);
    }

    private void ResolveMenuApi()
    {
        try
        {
            _api = _pluginCapability.Get();
            if (_api == null)
                Logger.LogWarning("MenuManager Core is not available; LevelsRanks menu will retry shortly.");
        }
        catch (Exception ex)
        {
            _api = null;
            Logger.LogWarning(ex, "Unable to resolve MenuManager capability; LevelsRanks menu will use retry path.");
        }
    }

    private void LoadConfig()
    {
        Logger.LogInformation("Loading configuration...");
        var configDirectory = Path.Combine(Application.RootDirectory, "configs/plugins/LevelsRanks");
        var configFilePath = Path.Combine(configDirectory, "database.json");
        var settingsFilePath = Path.Combine(configDirectory, "settings_stats.json");
        var ranksFilePath = Path.Combine(configDirectory, "settings_ranks.json");
        var mainSettingsFilePath = Path.Combine(configDirectory, "settings.json");
        _resetStatsCooldownFilePath = Path.Combine(ModuleDirectory, "reset_stats_cooldown.json");
        _resetStatsCooldown = ResetStatsCooldown.Load(_resetStatsCooldownFilePath);

        DatabaseConnection = ConfigLoader<DatabaseConnection>.Load(configFilePath);

        ExperienceSettings.Initialize(Logger);
        ExperienceSettings.Load(settingsFilePath);

        RanksSettings.Load(ranksFilePath);

        // ConfigLoader сам создаст settings.json с дефолтами, если файла ещё нет,
        // и сам допишет в уже существующий файл недостающие ключи (например, после
        // обновления плагина), не трогая то, что уже настроено на сервере.
        var mainSettings = ConfigLoader<MainSettings>.Load(mainSettingsFilePath);
        TableName = mainSettings.lr_table;
        ServerId = string.IsNullOrWhiteSpace(mainSettings.lr_server_id) ? "default" : mainSettings.lr_server_id;
        StatisticType = mainSettings.lr_type_statistics;
        ExperienceFromBots = mainSettings.lr_experience_from_bots == "1";
        MinPlayersCount = int.TryParse(mainSettings.lr_minplayers_count, out var minPlayers) ? minPlayers : 4;
        ShowSpawnMessage = mainSettings.lr_show_spawnmessage == "1";
        ShowUsualMessage = int.TryParse(mainSettings.lr_show_usualmessage, out var showUsualMessage)
            ? showUsualMessage
            : 1;
        BlockExpDuringWarmup = mainSettings.lr_block_warmup == "1";
        GiveExpOnRoundEnd = mainSettings.lr_giveexp_roundend == "1";
        AllAgainstAll = mainSettings.lr_allagainst_all == "1";
        ShowLevelUpMessage = mainSettings.lr_show_levelup_message == "1";
        ShowLevelDownMessage = mainSettings.lr_show_leveldown_message == "1";
        PlaySound = mainSettings.lr_sound == "1";
        SoundLvlUp = mainSettings.lr_sound_lvlup;
        SoundLvlDown = mainSettings.lr_sound_lvldown;
        _showResetMyStats = mainSettings.lr_show_resetmystats == "1";
        _resetMyStatsCooldown = long.TryParse(mainSettings.lr_resetmystats_cooldown, out var cooldown)
            ? cooldown
            : 86400;
        ShowRankMessage = mainSettings.lr_show_rankmessage == "1";
        ShowRankList = mainSettings.lr_show_ranklist == "1";
        PluginTitle = mainSettings.lr_plugin_title;
        AdminMenuFlag = mainSettings.lr_flag_adminmenu;
        TopCount = int.TryParse(mainSettings.lr_top_count, out var topCount) ? topCount : 10;
        StartPoints = int.TryParse(mainSettings.lr_start_points, out var startPoints) ? startPoints : 0;
        CleanDbDays = int.TryParse(mainSettings.lr_cleandb_days, out var cleanDbDays) ? cleanDbDays : 30;
        SaveDataPlayerModeLive = mainSettings.lr_db_savedataplayer_mode != "0";

        if (DatabaseConnection == null) throw new NullReferenceException("Database connection configuration is null.");
    }

}
