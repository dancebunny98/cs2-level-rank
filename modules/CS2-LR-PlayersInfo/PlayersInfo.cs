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
    /// false = поведение оригинала: игроки без CCSPlayerPawn (спектаторы, подключающиеся) пропускаются
    /// в mm_getinfo (но не в mm_getinfo_slot - тот всегда отвечает, если слот занят реальным игроком).
    /// </summary>
    [JsonPropertyName("IncludePlayersWithoutPawn")]
    public bool IncludePlayersWithoutPawn { get; set; } = false;

    /// <summary>
    /// true = дополнительно выводить поле "exp" (опыт игрока из LevelsRanks, User.Value).
    /// </summary>
    [JsonPropertyName("IncludeExperience")]
    public bool IncludeExperience { get; set; } = false;
}

[MinimumApiVersion(80)]
public class PlayersInfoModule : BasePlugin, IPluginConfig<PlayersInfoConfig>
{
    public override string ModuleName => "[LR] Module - PlayersInfo";
    public override string ModuleVersion => "1.3.2";
    public override string ModuleAuthor => "CounterStrikeSharp port of Pisex's PlayersInfo / FluteCS2PlayersList";
    public override string ModuleDescription =>
        "Prints server and players info (incl. LevelsRanks rank) as JSON via mm_getinfo / mm_getinfo_slot / mm_getinfo_file";

    private const int MaxSlots = 64;
    private const byte TeamT = 2;
    private const byte TeamCT = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        // Как nlohmann::json::dump(): не-ASCII символы пишутся как есть (UTF-8), а не \uXXXX.
        // .NET-строки уже валидный Unicode, поэтому санитайзер невалидных байт из форка не нужен.
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

        AddCommand("mm_getinfo", "Dump full server info as JSON to console", OnGetInfoCommand);
        AddCommand("mm_getinfo_slot", "Dump single player JSON for slot <N> (or {} if empty)", OnGetInfoSlotCommand);
        AddCommand("mm_getinfo_file", "Dump full server info as JSON to <path> atomically", OnGetInfoFileCommand);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnClientConnected>(OnClientConnected);
        RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServer);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);

        // Модуль может загрузиться до того, как движок проинициализировал глобальные
        // переменные (например, при старте сервера ещё до загрузки первой карты) - в этот
        // момент Utilities.GetPlayers()/Server.MaxPlayers кидает NativeException
        // "Global Variables not initialized yet". Откладываем скан уже подключённых игроков
        // (актуален для hot reload посреди карты) на следующий кадр и на всякий случай
        // страхуемся try/catch, чтобы холодная загрузка без карты не роняла плагин: в этом
        // случае _connectedAt заполнится штатно через OnClientConnected/OnClientPutInServer.
        Server.NextFrame(() =>
        {
            try
            {
                var now = Now();
                foreach (var player in Utilities.GetPlayers())
                {
                    if (IsValidSlot(player.Slot))
                        _connectedAt[player.Slot] = now;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "PlayersInfo: skipped initial player scan, globals not ready yet.");
            }
        });
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
        // Как в форке: кэш Prime не переживает смену карты (сервер мог заново залогиниться в Steam).
        SteamLicense.ClearCache();

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

    // ---- команды ----

    private void OnGetInfoCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller is not null) // только консоль сервера / RCON
            return;

        command.ReplyToCommand(JsonSerializer.Serialize(BuildServerInfo(), JsonOptions));
    }

    private void OnGetInfoSlotCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller is not null)
            return;

        if (command.ArgCount < 2 || !int.TryParse(command.GetArg(1), out var slot))
        {
            command.ReplyToCommand("usage: mm_getinfo_slot <slot>");
            return;
        }

        var player = TryBuildPlayer(slot);
        command.ReplyToCommand(player is null
            ? "{}"
            : JsonSerializer.Serialize(player, JsonOptions));
    }

    private void OnGetInfoFileCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller is not null)
            return;

        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("usage: mm_getinfo_file <path>");
            return;
        }

        var path = command.GetArg(1);
        var json = JsonSerializer.Serialize(BuildServerInfo(), JsonOptions);

        command.ReplyToCommand(WriteFileAtomic(path, json)
            ? "mm_getinfo_file: ok"
            : "mm_getinfo_file: failed");
    }

    // ---- сбор данных (GetServerInfo в оригинале) ----

    private SortedDictionary<string, object?> BuildServerInfo()
    {
        var (scoreCt, scoreT) = GetTeamScores();
        var now = Now();

        var players = new List<SortedDictionary<string, object?>>();

        for (var slot = 0; slot < MaxSlots; slot++)
        {
            var player = TryBuildPlayer(slot, now);
            if (player is not null)
                players.Add(player);
        }

        var info = NewObject();
        info["time"] = now;
        info["current_map"] = Server.MapName;
        info["score_ct"] = scoreCt;
        info["score_t"] = scoreT;
        info["player_count"] = players.Count;
        // Оригинал оставляет "players" как JSON null, если список пуст; форк - как [].
        // Оставляем null для совместимости с уже подключёнными клиентами.
        info["players"] = players.Count == 0 ? null : players;
        return info;
    }

    /// <summary>Собирает JSON одного игрока, либо null, если слот пуст/бот/не прошёл фильтры.</summary>
    private SortedDictionary<string, object?>? TryBuildPlayer(int slot, long? nowOverride = null)
    {
        if (!IsValidSlot(slot) || _connectedAt[slot] == 0)
            return null;

        var controller = Utilities.GetPlayerFromSlot(slot);
        if (controller is null || !controller.IsValid)
            return null;

        // Как в FluteCS2PlayersList: боты и HLTV в отчёт не попадают.
        if (controller.IsBot)
            return null;

        var hasPawn = controller.PlayerPawn.Value is not null;
        if (!Config.IncludePlayersWithoutPawn && !hasPawn)
            return null;

        var now = nowOverride ?? Now();
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
        player["alive"] = hasPawn;

        // LevelsRanks: ранг (ST_RANK в оригинале). Поле опускается, если ядро недоступно
        // или игрок ещё не загружен ядром (бот, идёт загрузка из БД) - как у обоих оригиналов.
        var api = _apiCapability.Get();
        if (api is not null && steamId != 0 && api.OnlineUsers.TryGetValue(api.ConvertToSteamId(steamId), out var lrUser))
        {
            player["rank"] = lrUser.Rank;
            if (Config.IncludeExperience)
                player["exp"] = lrUser.Value;
        }

        return player;
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

    /// <summary>
    /// Атомарная запись файла: пишем во временный файл рядом и переименовываем поверх целевого.
    /// File.Move(..., overwrite: true) на Linux выполняется через rename(2), как и в оригинале -
    /// читатель никогда не увидит частично записанный JSON.
    /// </summary>
    private bool WriteFileAtomic(string path, string content)
    {
        var tmpPath = path + ".tmp";
        try
        {
            File.WriteAllText(tmpPath, content);
            File.Move(tmpPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "mm_getinfo_file: failed to write {Path}", path);
            try { File.Delete(tmpPath); } catch { /* ignore */ }
            return false;
        }
    }

    // ---- вспомогательное ----

    // Объекты nlohmann::json основаны на std::map, поэтому dump() выводит ключи по порядку; делаем так же.
    private static SortedDictionary<string, object?> NewObject() => new(StringComparer.Ordinal);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static bool IsValidSlot(int slot) => slot >= 0 && slot < MaxSlots;
}
