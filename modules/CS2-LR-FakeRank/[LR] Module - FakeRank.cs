using System.Collections.Concurrent;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using LevelsRanksApi;
using Microsoft.Extensions.Logging;

// ВАЖНО: namespace обязателен - FakeRankApi.cs ссылается на
// LevelsRanksModuleFakeRank.LevelsRanksModuleFakeRank.
namespace LevelsRanksModuleFakeRank
{
    [MinimumApiVersion(80)]
    public class LevelsRanksModuleFakeRank : BasePlugin
    {
        public override string ModuleName => "[LR] Module - FakeRank";
        public override string ModuleVersion => "1.0.6";
        public override string ModuleAuthor => "ABKAM designed by RoadSide Romeo & Wend4r";

        private ILevelsRanksApi? _api;
        private IPlayerRankApi? _playerRankApi;

        // Имена capability ДОЛЖНЫ совпадать с теми, что ждут Core и VIP-модуль:
        //   Core регистрирует "levels_ranks" (ILevelsRanksApi)
        //   VIP_CustomFakeRank ищет "PLAYER_RANK_API" (IPlayerRankApi)
        private readonly PluginCapability<ILevelsRanksApi> _apiCapability = new("levels_ranks");
        private readonly PluginCapability<IPlayerRankApi> _playerRankApiCapability = new("PLAYER_RANK_API");

        private Dictionary<int, (int competitiveRanking, int competitiveRankType)>? _ranksConfig;

        // Желаемый LR-ранг игрока (по его уровню). Обновляется таймером раз в секунду.
        private readonly ConcurrentDictionary<ulong, (int competitiveRanking, int competitiveRankType)> _playerRanks = new();
        private readonly ConcurrentDictionary<ulong, int> _lastKnownLevels = new();

        private const float UpdateInterval = 1.0f;

        public override void Load(bool hotReload)
        {
            _playerRankApi = new PlayerRankApi(this);
            Capabilities.RegisterPluginCapability(_playerRankApiCapability, () => _playerRankApi);

            RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        }

        public override void OnAllPluginsLoaded(bool hotReload)
        {
            base.OnAllPluginsLoaded(hotReload);

            _api = _apiCapability.Get();
            if (_api == null)
            {
                Server.PrintToConsole("[FakeRank] Levels Ranks API is currently unavailable.");
                return;
            }

            // Кастомные ранги раздельно по серверам (см. PlayerRankApi.ServerId)
            PlayerRankApi.ServerId = _api.ServerId;

            CreateRanksConfig();
            _ranksConfig = LoadRanksConfig();

            // Как в оригинале: ранг ПОСТОЯННО удерживается в OnTick.
            // Движок CS2 сам перезаписывает CompetitiveRanking/Type (спавн, раунд,
            // смена команды...), поэтому "применить один раз" даёт моргание.
            RegisterListener<Listeners.OnTick>(OnTick);

            AddTimer(UpdateInterval, FetchPlayerRanks, TimerFlags.REPEAT);
        }

        private void OnClientDisconnect(int slot)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null)
                return;

            var steamId = player.SteamID;
            _playerRanks.TryRemove(steamId, out _);
            _lastKnownLevels.TryRemove(steamId, out _);
            PlayerRankApi.Forget(steamId);
        }

        // Раз в секунду: уровень LR -> желаемый fake rank. Только память, без диска.
        private void FetchPlayerRanks()
        {
            if (_api == null || _ranksConfig == null)
                return;

            foreach (var player in Utilities.GetPlayers())
            {
                if (player.IsBot || player.TeamNum == (int)CsTeam.Spectator)
                    continue;

                var steamId64 = player.SteamID;
                var steamId = _api.ConvertToSteamId(steamId64);

                if (!_api.OnlineUsers.TryGetValue(steamId, out var onlineUser))
                    continue;

                var level = onlineUser.Rank;

                if (_lastKnownLevels.TryGetValue(steamId64, out var lastLevel) && lastLevel == level)
                    continue;

                if (_ranksConfig.TryGetValue(level, out var rankInfo))
                {
                    _playerRanks[steamId64] = rankInfo;
                    _lastKnownLevels[steamId64] = level;
                }
            }
        }

        private void OnTick()
        {
            RecipientFilter? filter = null;

            foreach (var player in Utilities.GetPlayers())
            {
                if (player.IsBot || player.TeamNum == (int)CsTeam.Spectator)
                    continue;

                var steamId64 = player.SteamID;
                if (steamId64 == 0)
                    continue;

                int rank;
                int rankType;

                // Кастомный ранг приоритетнее. Читается из кэша в памяти, НЕ с диска.
                if (PlayerRankApi.TryGetCustomRank(steamId64, out var custom))
                {
                    rank = custom.Rank;
                    rankType = custom.RankType;
                }
                else if (_playerRanks.TryGetValue(steamId64, out var info))
                {
                    rank = info.competitiveRanking;
                    rankType = info.competitiveRankType;
                }
                else
                {
                    continue;
                }

                // Пишем в контроллер только если значение реально отличается -
                // так же, как в оригинале.
                if (player.CompetitiveRankType != (sbyte)rankType ||
                    player.CompetitiveRanking != rank)
                {
                    player.CompetitiveRankType = (sbyte)rankType;
                    player.CompetitiveRanking = rank;
                    player.CompetitiveWins = 777;

                    filter ??= new RecipientFilter();
                    filter.Add(player);
                }
            }

            if (filter != null && filter.Count > 0)
            {
                var msg = UserMessage.FromId(350);
                msg.Send(filter);
            }
        }

        private static string ConfigDirectory =>
            Path.Combine(Application.RootDirectory, "configs/plugins/LevelsRanks");

        private static string ConfigFilePath =>
            Path.Combine(ConfigDirectory, "settings_fakerank.json");

        private void CreateRanksConfig()
        {
            if (File.Exists(ConfigFilePath))
                return;

            var ranks = new Dictionary<string, string>();
            for (var i = 1; i <= 18; i++)
                ranks[i.ToString()] = i.ToString();

            var defaultConfig = new
            {
                LR_FakeRank = new
                {
                    Type = "1",
                    FakeRank = ranks
                }
            };

            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFilePath, json);
        }

        private Dictionary<int, (int competitiveRanking, int competitiveRankType)> LoadRanksConfig()
        {
            var ranks = new Dictionary<int, (int competitiveRanking, int competitiveRankType)>();

            try
            {
                var json = File.ReadAllText(ConfigFilePath);
                var config = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(json);

                if (config != null &&
                    config.TryGetValue("LR_FakeRank", out var section) &&
                    section.TryGetValue("FakeRank", out var fakeRanksObject) &&
                    fakeRanksObject is JsonElement fakeRanksElement)
                {
                    // Type: 1 -> 12, 2 -> 7, 3 -> 11 (по умолчанию 12) - как в оригинале.
                    var rankType = 12;
                    if (section.TryGetValue("Type", out var typeValue) &&
                        typeValue is JsonElement typeElement &&
                        typeElement.ValueKind == JsonValueKind.String &&
                        int.TryParse(typeElement.GetString(), out var type))
                    {
                        rankType = type switch
                        {
                            1 => 12,
                            2 => 7,
                            3 => 11,
                            _ => 12
                        };
                    }

                    foreach (var rank in fakeRanksElement.EnumerateObject())
                    {
                        if (int.TryParse(rank.Name, out var level) &&
                            rank.Value.ValueKind == JsonValueKind.String &&
                            int.TryParse(rank.Value.GetString(), out var competitiveRanking))
                        {
                            ranks[level] = (competitiveRanking, rankType);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[FakeRank] Failed to load settings_fakerank.json");
            }

            return ranks;
        }
    }
}
