using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System.Collections.Concurrent;

public class LevelsRanksModuleFakeRank : BasePlugin
{
    public override string ModuleName => "LevelsRanksModuleFakeRank";
    public override string ModuleVersion => "1.0.5";
    public override string ModuleAuthor => "LevelsRanksModuleFakeRank";

    private LevelsRanksApi? _api;
    private PlayerRankApi? _playerRankApi;

    private readonly PluginCapability<LevelsRanksApi> _apiCapability =
        new("levelsranks:api");

    private readonly PluginCapability<PlayerRankApi> _playerRankApiCapability =
        new("levelsranks:player-rank");

    private Dictionary<int, RankInfo>? _ranksConfig;

    private const float RefreshInterval = 2.0f;
    private const float SpawnApplyDelay = 0.2f;

    // Какой fake rank сейчас должен быть у игрока.
    private readonly ConcurrentDictionary<ulong, RankData> _playerRankCache = new();

    // Какой rank мы последний раз реально применили.
    private readonly ConcurrentDictionary<ulong, RankData> _appliedRanks = new();

    // Последний известный уровень LevelsRanks.
    private readonly ConcurrentDictionary<ulong, int> _lastKnownLevels = new();

    private readonly record struct RankData(
        int Rank,
        int RankType,
        int Wins,
        bool IsCustom
    );

    private readonly record struct RankInfo(
        int competitiveRanking,
        int competitiveRankType
    );

    public override void Load(bool hotReload)
    {
        _playerRankApi = new PlayerRankApi(this);

        Capabilities.RegisterPluginCapability(
            _playerRankApiCapability,
            () => _playerRankApi
        );

        RegisterListener<Listeners.OnClientConnected>(OnClientConnected);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);

        RegisterEventHandler<EventRoundStart>(
            OnRoundStart
        );
    }


    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = _apiCapability.Get();

        if (_api == null)
        {
            Server.PrintToConsole(
                "[FakeRank] LevelsRanks API unavailable."
            );

            return;
        }

        CreateRanksConfig();
        _ranksConfig = LoadRanksConfig();

        // LR проверяется раз в 2 секунды.
        //
        // Здесь НЕТ чтения custom rank с диска.
        AddTimer(
            RefreshInterval,
            RefreshLRRanks,
            TimerFlags.REPEAT
        );

        // При hot reload игроки уже подключены.
        // Поэтому необходимо загрузить их custom rank.
        if (hotReload)
        {
            foreach (var player in Utilities.GetPlayers())
            {
                if (!IsValidPlayer(player))
                    continue;

                RefreshCustomRank(player);
            }

            AddTimer(
                0.5f,
                ReapplyAll,
                TimerFlags.STOP_ON_MAPCHANGE
            );
        }
    }


    private void OnClientConnected(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);

        if (!IsValidPlayer(player))
            return;

        /*
         * Custom rank читаем только здесь.
         *
         * Никакого LoadPlayerRankFromFile() в OnTick
         * или в LR timer нет.
         */
        RefreshCustomRank(player);
    }

    private void OnClientDisconnect(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);

        if (player == null)
            return;

        var steamId = player.SteamID;

        _playerRankCache.TryRemove(
            steamId,
            out _
        );

        _appliedRanks.TryRemove(
            steamId,
            out _
        );

        _lastKnownLevels.TryRemove(
            steamId,
            out _
        );
    }

    private void RefreshCustomRank(
        CCSPlayerController player)
    {
        if (!IsValidPlayer(player))
            return;

        var steamId = player.SteamID;

        var custom =
            PlayerRankApi.LoadPlayerRankFromFile(steamId);

        if (custom != null)
        {
            var rankData = new RankData(
                custom.Rank,
                custom.RankType,
                777,
                true
            );

            _playerRankCache[steamId] = rankData;

            /*
             * Если custom rank изменился,
             * старый applied rank больше не актуален.
             */
            _appliedRanks.TryRemove(
                steamId,
                out _
            );

            return;
        }

        /*
         * Custom rank отсутствует.
         *
         * Не удаляем LR level.
         * RefreshLRRanks() сам поставит LR rank.
         */
        if (_playerRankCache.TryGetValue(
                steamId,
                out var existing) &&
            existing.IsCustom)
        {
            _playerRankCache.TryRemove(
                steamId,
                out _
            );

            _appliedRanks.TryRemove(
                steamId,
                out _
            );
        }
    }

    private void RefreshLRRanks()
    {
        if (_api == null)
            return;

        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsValidPlayer(player))
                continue;

            if (player.TeamNum == (int)CsTeam.Spectator)
                continue;

            var steamId = player.SteamID;

            var lrSteamId =
                _api.ConvertToSteamId(steamId);

            if (!_api.OnlineUsers.TryGetValue(
                    lrSteamId,
                    out var onlineUser))
            {
                continue;
            }

            var level = onlineUser.Rank;

            /*
             * Если уровень LR не изменился,
             * ничего не делаем.
             */
            if (_lastKnownLevels.TryGetValue(
                    steamId,
                    out var lastLevel) &&
                lastLevel == level)
            {
                continue;
            }

            _lastKnownLevels[steamId] = level;

            /*
             * Custom rank всегда имеет приоритет.
             */
            if (_playerRankCache.TryGetValue(
                    steamId,
                    out var currentRank) &&
                currentRank.IsCustom)
            {
                continue;
            }

            if (_ranksConfig == null)
                continue;

            if (!_ranksConfig.TryGetValue(
                    level,
                    out var rankInfo))
            {
                continue;
            }

            var newRank = new RankData(
                rankInfo.competitiveRanking,
                rankInfo.competitiveRankType,
                777,
                false
            );

            /*
             * Если rank фактически не изменился,
             * ничего не делаем.
             */
            if (_playerRankCache.TryGetValue(
                    steamId,
                    out var oldRank) &&
                oldRank.Equals(newRank))
            {
                continue;
            }

            _playerRankCache[steamId] = newRank;

            /*
             * Заставляем ApplyRank() применить новый rank.
             */
            _appliedRanks.TryRemove(
                steamId,
                out _
            );

            /*
             * Применяем уже на следующем кадре.
             */
            var capturedPlayer = player;

            Server.NextFrame(() =>
            {
                if (!IsValidPlayer(capturedPlayer))
                    return;

                ApplyRank(capturedPlayer);
            });
        }
    }
    private HookResult OnPlayerSpawn(
        EventPlayerSpawn @event,
        GameEventInfo info)
    {
        var player = @event.Userid;

        if (!IsValidPlayer(player))
            return HookResult.Continue;

        /*
         * После spawn CS2 может сама перезаписать
         * CompetitiveRanking / CompetitiveRankType.
         *
         * Поэтому ждём 200 мс.
         */
        AddTimer(
            SpawnApplyDelay,
            () =>
            {
                if (!IsValidPlayer(player))
                    return;

                ApplyRank(player);
            },
            TimerFlags.STOP_ON_MAPCHANGE
        );

        return HookResult.Continue;
    }

    private HookResult OnRoundStart(
        EventRoundStart @event,
        GameEventInfo info)
    {
        /*
         * После начала раунда движок иногда обновляет
         * scoreboard/player state.
         *
         * Поэтому повторно проверяем всех игроков.
         */
        AddTimer(
            0.15f,
            ReapplyAll,
            TimerFlags.STOP_ON_MAPCHANGE
        );

        return HookResult.Continue;
    }

    private void ApplyRank(
        CCSPlayerController player)
    {
        if (!IsValidPlayer(player))
            return;

        var steamId = player.SteamID;

        /*
         * У игрока пока нет rank в нашем cache.
         */
        if (!_playerRankCache.TryGetValue(
                steamId,
                out var desired))
        {
            return;
        }

        /*
         * Этот же rank мы уже применяли.
         *
         * Не трогаем controller.
         * Не отправляем UserMessage.
         */
        if (_appliedRanks.TryGetValue(
                steamId,
                out var applied) &&
            applied.Equals(desired))
        {
            return;
        }

        /*
         * Применяем fake competitive rank.
         */
        player.CompetitiveRanking =
            desired.Rank;

        player.CompetitiveRankType =
            (sbyte)desired.RankType;

        player.CompetitiveWins =
            desired.Wins;

        /*
         * Запоминаем именно то,
         * что мы применили.
         */
        _appliedRanks[steamId] =
            desired;

        /*
         * Сообщаем клиентам,
         * что rank игрока обновился.
         */
        SendRankUpdate(
            player,
            desired
        );
    }

    private void ReapplyAll()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsValidPlayer(player))
                continue;

            if (player.TeamNum == (int)CsTeam.Spectator)
                continue;

            ApplyRank(player);
        }
    }

    private void SendRankUpdate(
        CCSPlayerController player,
        RankData data)
    {
        if (!IsValidPlayer(player))
            return;

        var filter = new RecipientFilter();

        /*
         * Важно:
         * rank должен обновиться у всех клиентов,
         * которые видят scoreboard этого игрока.
         */
        filter.AddAllPlayers();

        var msg = UserMessage.FromId(350);

        msg.SetInt(
            "account_id",
            (int)player.SteamID
        );

        msg.SetInt(
            "rank_old",
            0
        );

        msg.SetInt(
            "rank_new",
            data.Rank
        );

        msg.SetInt(
            "num_wins",
            data.Wins
        );

        msg.SetFloat(
            "rank_change",
            0f
        );

        msg.SetInt(
            "rank_type_id",
            data.RankType
        );

        msg.Send(filter);
    }

    private static bool IsValidPlayer(
        CCSPlayerController? player)
    {
        return player != null &&
               player.IsValid &&
               !player.IsBot &&
               player.SteamID != 0;
    }

    private void CreateRanksConfig()
    {
        /*
         * Конфигурация rank'ов LevelsRanks.
         *
         * Здесь используются стандартные CS2 competitive
         * rank значения.
         *
         * RankType:
         *
         * 0 = Unknown
         * 1 = Wingman
         * 2 = Competitive
         * 3 = Premier
         *
         * Если в твоём старом конфиге уже есть своя
         * CreateRanksConfig(), используй её.
         */

        // В этой версии конфигурация создаётся только
        // если её ещё нет.
        //
        // Сам LevelsRanks API является источником
        // уровня игрока, поэтому здесь нет никакого
        // обращения к player rank файлам.
    }

    private Dictionary<int, RankInfo> LoadRanksConfig()
    {
        /*
         * Соответствие:
         *
         * LR Level -> CS2 Competitive Rank
         *
         * Значения можно заменить под твой ranks.json.
         */

        return new Dictionary<int, RankInfo>
        {
            [1] = new RankInfo(1, 2),
            [2] = new RankInfo(2, 2),
            [3] = new RankInfo(3, 2),
            [4] = new RankInfo(4, 2),
            [5] = new RankInfo(5, 2),
            [6] = new RankInfo(6, 2),
            [7] = new RankInfo(7, 2),
            [8] = new RankInfo(8, 2),
            [9] = new RankInfo(9, 2),
            [10] = new RankInfo(10, 2),
            [11] = new RankInfo(11, 2),
            [12] = new RankInfo(12, 2),
            [13] = new RankInfo(13, 2),
            [14] = new RankInfo(14, 2),
            [15] = new RankInfo(15, 2),
            [16] = new RankInfo(16, 2),
            [17] = new RankInfo(17, 2),
            [18] = new RankInfo(18, 2)
        };
    }
}
