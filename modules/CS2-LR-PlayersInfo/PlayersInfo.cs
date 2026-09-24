using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using LevelsRanksApi;
using Microsoft.Extensions.Logging;

namespace LevelsRanksModulePlayersInfo;

public class PlayersInfoConfig : BasePluginConfig
{
    /// <summary>
    /// true  = поведение оригинала: при каждом старте карты "playtime" всех игроков сбрасывается.
    /// false = playtime считается с момента подключения игрока.
    /// </summary>
    [JsonPropertyName("ResetPlaytimeOnMapStart")]
    public bool ResetPlaytimeOnMapStart { get; set; } = true;

    /// <summary>
    /// false = поведение оригинала: игроки без CCSPlayerPawn (спектаторы, подключающиеся) пропускаются.
    /// </summary>
    [JsonPropertyName("IncludePlayersWithoutPawn")]
    public bool IncludePlayersWithoutPawn { get; set; } = false;

    /// <summary>
    /// true = дополнительно выводить поле "exp" (опыт игрока из LevelsRanks, User.Value).
    /// false = формат ровно как у оригинала.
    /// </summary>
    [JsonPropertyName("IncludeExperience")]
    public bool IncludeExperience { get; set; } = false;
}

[MinimumApiVersion(80)]
public class PlayersInfoModule : BasePlugin, IPluginConfig<PlayersInfoConfig>
{
    public override string ModuleName => "[LR] Module - PlayersInfo";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleAuthor => "CounterStrikeSharp port of Pisex's PlayersInfo";
    public override string ModuleDescription =>
        "Prints server and players info (incl. LevelsRanks rank) as JSON via the mm_getinfo console command";

    private const int MaxSlots = 64;
    private const byte TeamT = 2;
    private const byte TeamCT = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        // Как nlohmann::json::dump(): не-ASCII символы пишутся как есть (UTF-8), а не \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly PluginCapability<ILevelsRanksApi> _apiCapability = new("levels_ranks");

    // Unix time подключения по слотам; 0 = слот не подключён.
    private readonly long[] _connectedAt = new long[MaxSlots];

    public PlayersInfoConfig Config { get; set; } = new();

    public void OnConfigParsed(PlayersInfoConfig config) => Config = config;

    public override void Load(bool hotReload)
    {
        SteamLicense.Init(Logger);

        AddCommand("mm_getinfo", "Prints server and players info as JSON", OnGetInfoCommand);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnClientConnected>(OnClientConnected);
        RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServer);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);

        // Модуль загружен, когда на сервере уже есть игроки.
        var now = Now();
        foreach (var player in Utilities.GetPlayers())
        {
            if (IsValidSlot(player.Slot))
                _connectedAt[player.Slot] = now;
        }
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        base.OnAllPluginsLoaded(hotReload);

        if (_apiCapability.Get() is null)
            Logger.LogWarning("Levels Ranks API is currently unavailable: 'rank' will be omitted until the core is loaded.");
    }

    public override void Unload(bool hotReload)
    {
        SteamLicense.Shutdown();
    }

    // ---- учёт подключений (player_connect / player_disconnect / StartupServer в оригинале) ----

    private void OnMapStart(string mapName)
    {
        if (!Config.ResetPlaytimeOnMapStart)
            return;

        var now = Now();
        for (var i = 0; i < MaxSlots; i++)
            _connectedAt[i] = now;
    }

    private void OnClientConnected(int slot)
    {
        if (IsValidSlot(slot))
            _connectedAt[slot] = Now();
    }

    // Покрывает ботов и клиентов, для которых OnClientConnected не сработал.
    private void OnClientPutInServer(int slot)
    {
        if (IsValidSlot(slot) && _connectedAt[slot] == 0)
            _connectedAt[slot] = Now();
    }

    private void OnClientDisconnect(int slot)
    {
        if (IsValidSlot(slot))
            _connectedAt[slot] = 0;
    }

    // ---- команда ----

    private void OnGetInfoCommand(CCSPlayerController? caller, CommandInfo command)
    {
        // Только консоль сервера / RCON.
        if (caller is not null)
            return;

        var json = JsonSerializer.Serialize(BuildServerInfo(), JsonOptions);
        command.ReplyToCommand(json);
    }

    // ---- сбор данных (GetServerInfo в оригинале) ----

    private SortedDictionary<string, object?> BuildServerInfo()
    {
        var (scoreCt, scoreT) = GetTeamScores();
        var now = Now();

        // Ядро LevelsRanks может быть перезагружено, поэтому API берём заново при каждом вызове.
        var api = _apiCapability.Get();

        var players = new List<SortedDictionary<string, object?>>();

        for (var slot = 0; slot < MaxSlots; slot++)
        {
            if (_connectedAt[slot] == 0)
                continue;

            var controller = Utilities.GetPlayerFromSlot(slot);
            if (controller is null || !controller.IsValid)
                continue;

            if (!Config.IncludePlayersWithoutPawn && controller.PlayerPawn.Value is null)
                continue;

            var name = controller.PlayerName;
            var steamId = controller.SteamID;

            var player = NewObject();
            player["userid"] = slot;
            player["name"] = string.IsNullOrEmpty(name) ? "Unknown" : name;
            player["team"] = (int)controller.TeamNum;
            player["steamid"] = steamId.ToString();

            var tracking = controller.ActionTrackingServices;
            if (tracking is not null)
            {
                var stats = tracking.MatchStats;
                player["kills"] = stats.Kills;
                player["death"] = stats.Deaths;
                player["headshots"] = stats.HeadShotKills;
            }

            player["ping"] = controller.Ping;
            player["playtime"] = now - _connectedAt[slot];
            player["prime"] = SteamLicense.HasPrime(steamId);

            // LevelsRanks: ранг (ST_RANK в оригинале) берётся из OnlineUsers ядра.
            // Игрок, ещё не загруженный ядром (бот, идёт загрузка из БД), получает rank = 0.
            if (api is not null)
            {
                User? lrUser = null;
                if (steamId != 0)
                    api.OnlineUsers.TryGetValue(api.ConvertToSteamId(steamId), out lrUser);

                player["rank"] = lrUser?.Rank ?? 0;

                if (Config.IncludeExperience)
                    player["exp"] = lrUser?.Value ?? 0;
            }

            players.Add(player);
        }

        var info = NewObject();
        info["time"] = now;
        info["current_map"] = Server.MapName;
        info["score_ct"] = scoreCt;
        info["score_t"] = scoreT;
        // Оригинал оставляет "players" как JSON null, если список пуст.
        info["players"] = players.Count == 0 ? null : players;
        return info;
    }

    private static (int ct, int t) GetTeamScores()
    {
        int ct = 0, t = 0;

        foreach (var team in Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager"))
        {
            if (team.TeamNum == TeamCT)
                ct = team.Score;
            else if (team.TeamNum == TeamT)
                t = team.Score;
        }

        return (ct, t);
    }

    // ---- вспомогательное ----

    // Объекты nlohmann::json основаны на std::map, поэтому dump() выводит ключи по порядку; делаем так же.
    private static SortedDictionary<string, object?> NewObject() => new(StringComparer.Ordinal);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static bool IsValidSlot(int slot) => slot >= 0 && slot < MaxSlots;
}
