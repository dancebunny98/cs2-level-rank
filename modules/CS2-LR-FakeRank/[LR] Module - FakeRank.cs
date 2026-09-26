using System;
using System.Collections.Concurrent;
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
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using LevelsRanksApi;
using Microsoft.Extensions.Logging;

namespace LevelsRanksModuleFakeRank
{
    [MinimumApiVersion(80)]
    public class LevelsRanksModuleFakeRank : BasePlugin
    {
        public override string ModuleName => "[LR] Module - FakeRank";
        public override string ModuleVersion => "1.0.3";
        public override string ModuleAuthor => "ABKAM designed by RoadSide Romeo & Wend4r";

        private Dictionary<int, (int competitiveRanking, int competitiveRankType)>? _ranksConfig;
        private readonly Dictionary<string, (int competitiveRanking, int competitiveRankType)> _playerRanks = new();

        // Type "3" из оригинального плагина (Pisex): вместо таблицы FakeRank ранг
        // показывается как сырое значение опыта игрока (аналог Premier rating).
        // Type "4": ранг берётся из таблицы FakeRank (как в 0/1/2), но отображается
        // с competitiveRankType = 11, как и Type "3".
        private bool _useRawExperienceAsRanking;
        private int _rankTypeForConfig;
        private ILevelsRanksApi? _api;
        private readonly PluginCapability<ILevelsRanksApi> _apiCapability = new("levels_ranks");
        private IPlayerRankApi? _playerRankApi;
        private readonly PluginCapability<IPlayerRankApi> _playerRankApiCapability = new("PLAYER_RANK_API");
        private readonly Dictionary<string, (int competitiveRanking, int competitiveRankType)> _lastKnownRankInfo = new();
        private ConcurrentDictionary<string, (int competitiveRanking, int competitiveRankType)> _rankCache = new();
        private ConcurrentDictionary<string, DateTime> _cacheTimestamps = new();

        private readonly ConcurrentDictionary<string, bool>
            _isCustomRankActive = new();

        // Отслеживание игроков, которые ещё не появились в OnlineUsers (обычно это
        // нормальная короткая гонка сразу после коннекта, пока Core асинхронно
        // подгружает пользователя из БД). Раньше это логировалось каждую секунду для
        // каждого такого игрока, что засоряло логи. Теперь: тихо ждём grace-период,
        // и если игрок так и не появился - предупреждаем один раз, а не каждый тик.
        private readonly ConcurrentDictionary<string, DateTime> _missingFirstSeen = new();
        private readonly ConcurrentDictionary<string, DateTime> _missingLastWarned = new();
        private static readonly TimeSpan MissingGracePeriod = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MissingWarnCooldown = TimeSpan.FromSeconds(60);

        private const float UpdateInterval = 1.0f;

        public override void Load(bool hotReload)
        {
            _playerRankApi = new PlayerRankApi(this);
            Capabilities.RegisterPluginCapability(_playerRankApiCapability, () => _playerRankApi);
        }

        public override void OnAllPluginsLoaded(bool hotReload)
        {
            base.OnAllPluginsLoaded(hotReload);

            _api = _apiCapability.Get();
            if (_api == null)
            {
                Server.PrintToConsole("Levels Ranks API is currently unavailable.");
                return;
            }

            PlayerRankApi.ServerId = _api.ServerId;

            CreateRanksConfig();
            _ranksConfig = LoadRanksConfig();

            RegisterListener<Listeners.OnTick>(OnTick);
            AddTimer(UpdateInterval, async () => { await FetchPlayerRanks(); }, TimerFlags.REPEAT);

            RegisterEventHandler<EventPlayerDisconnect>((@event, info) =>
            {
                var player = @event.Userid;
                if (player?.AuthorizedSteamID != null)
                {
                    var steamId = _api!.ConvertToSteamId(player.AuthorizedSteamID.SteamId64);
                    _missingFirstSeen.TryRemove(steamId, out _);
                    _missingLastWarned.TryRemove(steamId, out _);
                }

                return HookResult.Continue;
            });
        }

        private async Task FetchPlayerRanks()
        {
            var players = Utilities.GetPlayers()
                .Where(player => !player.IsBot && player.TeamNum != (int)CsTeam.Spectator);
            var steamIds = players.Select(player => _api!.ConvertToSteamId(player.SteamID)).ToList();

            var playersToFetch = steamIds.Where(steamId =>
                    !_rankCache.TryGetValue(steamId, out var cachedRank) ||
                    !_cacheTimestamps.TryGetValue(steamId, out var cacheTime) ||
                    (DateTime.UtcNow - cacheTime).TotalSeconds >= UpdateInterval)
                .ToList();

            if (playersToFetch.Count == 0)
            {
                return;
            }

            foreach (var steamId in playersToFetch)
            {
                if (_api!.OnlineUsers.TryGetValue(steamId, out var onlineUser))
                {
                    (int competitiveRanking, int competitiveRankType) rankInfo;
                    var haveRankInfo = true;

                    if (_useRawExperienceAsRanking)
                    {
                        // Type "3": ранг = сырое значение опыта игрока (без таблицы FakeRank).
                        // Игра не отображает числа > 99999, поэтому зажимаем.
                        var displayValue = Math.Min(onlineUser.Value, 99999);
                        rankInfo = (displayValue, _rankTypeForConfig);
                    }
                    else if (_ranksConfig == null || !_ranksConfig.TryGetValue(onlineUser.Rank, out rankInfo))
                    {
                        haveRankInfo = false;
                        rankInfo = default;
                    }

                    if (haveRankInfo &&
                        (!_isCustomRankActive.TryGetValue(steamId, out var isCustomActive) || !isCustomActive))
                    {
                        if (!_lastKnownRankInfo.TryGetValue(steamId, out var lastRankInfo) ||
                            rankInfo != lastRankInfo)
                        {
                            _playerRanks[steamId] = rankInfo;
                            _lastKnownRankInfo[steamId] = rankInfo;
                            _rankCache[steamId] = rankInfo;
                            _cacheTimestamps[steamId] = DateTime.UtcNow;
                        }
                    }
                }
                else
                {
                    HandleMissingOnlineUser(steamId);
                    continue;
                }

                // Игрок найден в OnlineUsers - сбрасываем накопленное состояние "отсутствия".
                _missingFirstSeen.TryRemove(steamId, out _);
                _missingLastWarned.TryRemove(steamId, out _);
            }
        }

        // Игрок из Utilities.GetPlayers() ещё не появился в ILevelsRanksApi.OnlineUsers.
        // В подавляющем большинстве случаев это нормальная гонка на подключении
        // (Core ещё выполняет асинхронный запрос к БД) и разрешается за 1-2 тика.
        // Логируем не каждую секунду, а один раз после grace-периода, и дальше не
        // чаще, чем раз в MissingWarnCooldown, пока проблема не решится.
        private void HandleMissingOnlineUser(string steamId)
        {
            var now = DateTime.UtcNow;
            var firstSeen = _missingFirstSeen.GetOrAdd(steamId, now);

            if (now - firstSeen < MissingGracePeriod)
                return;

            if (_missingLastWarned.TryGetValue(steamId, out var lastWarned) &&
                now - lastWarned < MissingWarnCooldown)
                return;

            _missingLastWarned[steamId] = now;
            Logger.LogWarning(
                $"Player {steamId} has been missing from LevelsRanks.OnlineUsers for over {MissingGracePeriod.TotalSeconds:0}s. " +
                "This usually means Core failed to load/authorize this user (check Core logs for DB errors); FakeRank will keep retrying.");
        }

        // Как часто принудительно "довещаем" всем клиентам актуальные ранги (usermsg 350),
        // независимо от того, менялось ли что-то в эту секунду. Нужно потому, что usermsg 350 -
        // это триггер "покажи мне ранги всех" ДЛЯ ТОГО КЛИЕНТА, КОМУ ОН ОТПРАВЛЕН, а не заявление
        // "у этого игрока такой-то ранг" для всех остальных. Раньше сообщение уходило только
        // тому игроку, у кого только что изменилось значение (RecipientFilter из одного человека) -
        // остальные клиенты вообще не получали triggеr на обновление своего таба для этого игрока.
        // Плюс сам клиент CS2, пока открыт таб, время от времени сам запрашивает у сервера
        // "настоящий" (заниженный/анврайтенный) ранг - без периодической повторной рассылки
        // нашего фейкового значения ВСЕМ клиентам это и давало то самое мерцание
        // "то текущий, то как будто другой ранг".
        private const float RankRevealInterval = 2.0f;
        private float _timeSinceLastReveal;

        private void OnTick()
        {
            var players = Utilities.GetPlayers()
                .Where(player => !player.IsBot && player.TeamNum != (int)CsTeam.Spectator).ToList();

            var anyChanged = false;

            foreach (var player in players)
            {
                var steamId64 = player.SteamID;
                var steamId = _api!.ConvertToSteamId(steamId64);

                var customRank = PlayerRankApi.LoadPlayerRankFromFile(steamId64);

                if (customRank != null)
                {
                    if (player.CompetitiveRankType != (sbyte)customRank.RankType ||
                        player.CompetitiveRanking != customRank.Rank)
                    {
                        player.CompetitiveRankType = (sbyte)customRank.RankType;
                        player.CompetitiveRanking = customRank.Rank;
                        player.CompetitiveWins = 777;
                        anyChanged = true;
                    }
                }
                else
                {
                    if (_playerRanks.TryGetValue(steamId, out var rankInfo))
                    {
                        if (player.CompetitiveRankType != (sbyte)rankInfo.competitiveRankType ||
                            player.CompetitiveRanking != rankInfo.competitiveRanking)
                        {
                            player.CompetitiveRankType = (sbyte)rankInfo.competitiveRankType;
                            player.CompetitiveRanking = rankInfo.competitiveRanking;
                            player.CompetitiveWins = 777;
                            anyChanged = true;
                        }
                    }
                }
            }

            // Реальное изменение ранга - рассылаем ревил всем сразу, не дожидаясь таймера ниже,
            // чтобы левел-ап/даун появился в табе у всех без задержки в RankRevealInterval секунд.
            if (anyChanged)
            {
                BroadcastRankReveal();
                _timeSinceLastReveal = 0f;
                return;
            }

            _timeSinceLastReveal += Server.TickInterval;
            if (_timeSinceLastReveal < RankRevealInterval) return;

            _timeSinceLastReveal = 0f;
            BroadcastRankReveal();
        }

        // Отправляет usermsg 350 ВСЕМ подключённым (не боты) клиентам - каждому из них "скажи
        // покажи актуальные ранги всех игроков", а не только тому, чей ранг только что поменялся.
        private void BroadcastRankReveal()
        {
            var recipients = new RecipientFilter();
            foreach (var p in Utilities.GetPlayers().Where(p => !p.IsBot && p.IsValid))
                recipients.Add(p);

            if (recipients.Count > 0)
            {
                var msg = UserMessage.FromId(350);
                msg.Send(recipients);
            }
        }

        private void CreateRanksConfig()
        {
            var configDirectory = Path.Combine(Application.RootDirectory, "configs/plugins/LevelsRanks");
            var filePath = Path.Combine(configDirectory, "settings_fakerank.json");
            var options = new JsonSerializerOptions { WriteIndented = true };

            // Дефолтный Type = "0" -> Premier (competitiveRankType 12), где ранг - это НЕ бейдж
            // 0..18, а непрерывный CS Rating (примерно 0..35000+, цветовые полосы по ~5000).
            // Поэтому таблица ниже растянута под реальную шкалу Premier, а не 1..18.
            // Если переключите Type на "1" (Wingman), "2" (Danger Zone) или "4" (Competitive 2.0
            // через таблицу) - там ранг снова просто бейдж 0..18, и таблицу лучше вернуть к 1..18.
            var defaultFakeRank = new Dictionary<string, string>
            {
                { "1", "0" }, { "2", "500" }, { "3", "1000" }, { "4", "2000" }, { "5", "3500" },
                { "6", "5000" }, { "7", "7000" }, { "8", "9000" }, { "9", "11000" }, { "10", "13000" },
                { "11", "15000" }, { "12", "17500" }, { "13", "20000" }, { "14", "22500" }, { "15", "25000" },
                { "16", "27500" }, { "17", "30000" }, { "18", "33000" }
            };

            if (!File.Exists(filePath))
            {
                var defaultConfig = new
                {
                    LR_FakeRank = new
                    {
                        // 0 - Premier, 1 - Wingman, 2 - Danger Zone,
                        // 3 - Competitive 2.0 (ранг = опыт игрока), 4 - Competitive 2.0 (таблица ниже)
                        Type = "0",
                        FakeRank = defaultFakeRank
                    }
                };

                Directory.CreateDirectory(configDirectory);
                File.WriteAllText(filePath, JsonSerializer.Serialize(defaultConfig, options));
                return;
            }

            // Файл уже существует (обновление плагина) - дописываем только то, чего в нём
            // не хватает (например, отсутствующий "Type" или новые уровни в "FakeRank"),
            // не трогая то, что уже настроено.
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject ?? new JsonObject();
                var changed = false;

                if (root["LR_FakeRank"] is not JsonObject section)
                {
                    section = new JsonObject();
                    root["LR_FakeRank"] = section;
                    changed = true;
                }

                if (!section.ContainsKey("Type"))
                {
                    section["Type"] = "0";
                    changed = true;
                }

                if (section["FakeRank"] is not JsonObject fakeRankSection)
                {
                    fakeRankSection = new JsonObject();
                    section["FakeRank"] = fakeRankSection;
                    changed = true;
                }

                foreach (var (level, value) in defaultFakeRank)
                    if (!fakeRankSection.ContainsKey(level))
                    {
                        fakeRankSection[level] = value;
                        changed = true;
                    }

                if (changed)
                    File.WriteAllText(filePath, root.ToJsonString(options));
            }
            catch (JsonException)
            {
                // Повреждённый JSON - не трогаем файл, чтобы не потерять то, что там есть.
            }
        }

        private Dictionary<int, (int competitiveRanking, int competitiveRankType)> LoadRanksConfig()
        {
            var configDirectory = Path.Combine(Application.RootDirectory, "configs/plugins/LevelsRanks");
            var filePath = Path.Combine(configDirectory, "settings_fakerank.json");

            var json = File.ReadAllText(filePath);
            var config = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(json);

            var ranks = new Dictionary<int, (int competitiveRanking, int competitiveRankType)>();

            if (config != null && config.TryGetValue("LR_FakeRank", out var fakeRankSection) &&
                fakeRankSection.TryGetValue("FakeRank", out var fakeRanksObject))
            {
                if (fakeRanksObject is JsonElement fakeRanksElement)
                {
                    var type = 0;
                    if (fakeRankSection.TryGetValue("Type", out var typeValue) &&
                        typeValue is JsonElement typeElement &&
                        typeElement.GetString() is string typeString && int.TryParse(typeString, out var parsedType))
                        type = parsedType;

                    // Соответствует Type в оригинальном lr_fakerank.cpp (Pisex):
                    // 0 - Premier (12), таблица FakeRank
                    // 1 - Wingman (7), таблица FakeRank
                    // 2 - Danger Zone (10), таблица FakeRank
                    // 3 - Competitive 2.0 (11), ранг = сырой опыт игрока (без таблицы)
                    // 4 - Competitive 2.0 (11), таблица FakeRank
                    int rankType;
                    switch (type)
                    {
                        case 1:
                            rankType = 7;
                            break;
                        case 2:
                            rankType = 10;
                            break;
                        case 3:
                        case 4:
                            rankType = 11;
                            break;
                        default:
                            rankType = 12;
                            break;
                    }

                    _useRawExperienceAsRanking = type == 3;
                    _rankTypeForConfig = rankType;

                    foreach (var rank in fakeRanksElement.EnumerateObject())
                    {
                        if (int.TryParse(rank.Name, out var level) &&
                            rank.Value.GetString() is string competitiveRankingString &&
                            int.TryParse(competitiveRankingString, out var competitiveRanking))
                        {
                            ranks[level] = (competitiveRanking, rankType);
                        }
                    }
                }
            }

            return ranks;
        }
    }
}
