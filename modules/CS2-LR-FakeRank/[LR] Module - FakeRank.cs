using System.Collections.Concurrent;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
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
        public override string ModuleVersion => "1.0.11";
        public override string ModuleAuthor => "ABKAM designed by RoadSide Romeo & Wend4r";

        private ILevelsRanksApi? _api;
        private IPlayerRankApi? _playerRankApi;

        // Имена capability ДОЛЖНЫ совпадать с теми, что ждут Core и VIP-модуль:
        //   Core регистрирует "levels_ranks" (ILevelsRanksApi)
        //   VIP_CustomFakeRank ищет "PLAYER_RANK_API" (IPlayerRankApi)
        private readonly PluginCapability<ILevelsRanksApi> _apiCapability = new("levels_ranks");
        private readonly PluginCapability<IPlayerRankApi> _playerRankApiCapability = new("PLAYER_RANK_API");

        private Dictionary<int, (int competitiveRanking, int competitiveRankType)>? _ranksConfig;

        // Последний вычисленный ранг игрока. Нужен как запасной вариант, пока игрока
        // временно нет в OnlineUsers (загрузка из БД при заходе) - ранг НЕ должен
        // в этот момент откатываться на настоящий.
        private readonly ConcurrentDictionary<ulong, (int competitiveRanking, int competitiveRankType)> _playerRanks = new();

        // SteamID64 -> строка формата LR (кэш, чтобы не аллоцировать строку каждый тик)
        private readonly ConcurrentDictionary<ulong, string> _steamIdStrings = new();

        // Type "3": ранг = сырое значение опыта игрока (без таблицы FakeRank).
        private bool _useRawExperienceAsRanking;
        private int _rankTypeForConfig = 12;
        private const int MaxDisplayRanking = 99999;

        // Диагностика (см. css_fakerank_debug): кто и когда откатывает ранг.
        private readonly ConcurrentDictionary<ulong, int> _rewrites = new();
        private readonly ConcurrentDictionary<ulong, (int rank, int type, int wins)> _lastForeign = new();
        private readonly ConcurrentDictionary<string, int> _rewriteBuckets = new();
        private string _lastEventName = "none";
        private long _lastEventMs;
        private long _statsSinceMs = Environment.TickCount64;

        public override void Load(bool hotReload)
        {
            _playerRankApi = new PlayerRankApi(this);
            Capabilities.RegisterPluginCapability(_playerRankApiCapability, () => _playerRankApi);

            RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);

            // Только для диагностики: запоминаем, после какого события движок трогает ранг.
            RegisterEventHandler<EventPlayerSpawn>((_, _) => MarkEvent("player_spawn"));
            RegisterEventHandler<EventRoundStart>((_, _) => MarkEvent("round_start"));
            RegisterEventHandler<EventRoundEnd>((_, _) => MarkEvent("round_end"));
            RegisterEventHandler<EventPlayerTeam>((_, _) => MarkEvent("player_team"));

            // Если в консоли эта строка появляется ДВАЖДЫ - на сервере две копии FakeRank.
            Logger.LogInformation($"[FakeRank] v{ModuleVersion} loaded from {ModuleDirectory}");
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

        }

        private void OnClientDisconnect(int slot)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null)
                return;

            var steamId = player.SteamID;
            _playerRanks.TryRemove(steamId, out _);
            _steamIdStrings.TryRemove(steamId, out _);
            _lastForeign.TryRemove(steamId, out _);
            _rewrites.TryRemove(steamId, out _);
            PlayerRankApi.Forget(steamId);
        }

        // Ранг игрока по его ТЕКУЩЕМУ опыту/уровню. Считается на каждом тике напрямую
        // из OnlineUsers, поэтому всегда соответствует опыту (без задержки таймера).
        private bool TryGetDesiredRank(ulong steamId64, out int rank, out int rankType)
        {
            rank = 0;
            rankType = 0;

            // 1. Кастомный ранг (из VIP-меню) приоритетнее. Читается из кэша в памяти.
            if (PlayerRankApi.TryGetCustomRank(steamId64, out var custom))
            {
                rank = custom.Rank;
                rankType = custom.RankType;
                return true;
            }

            // 2. Ранг по опыту / уровню
            var api = _api;
            if (api != null)
            {
                var steamId = _steamIdStrings.GetOrAdd(steamId64, id => api.ConvertToSteamId(id));

                if (api.OnlineUsers.TryGetValue(steamId, out var onlineUser))
                {
                    if (_useRawExperienceAsRanking)
                    {
                        // Type "3": ранг = сырое значение опыта игрока (без таблицы FakeRank).
                        // Игра не отображает числа > 99999, поэтому зажимаем.
                        var displayValue = Math.Clamp(onlineUser.Value, 0, MaxDisplayRanking);
                        _playerRanks[steamId64] = (displayValue, _rankTypeForConfig);
                    }
                    else if (_ranksConfig != null &&
                             _ranksConfig.TryGetValue(onlineUser.Rank, out var levelRank))
                    {
                        _playerRanks[steamId64] = levelRank;
                    }
                }
            }

            // 3. Свежее значение (или последнее известное, если игрока пока нет в OnlineUsers)
            if (_playerRanks.TryGetValue(steamId64, out var known))
            {
                rank = known.competitiveRanking;
                rankType = known.competitiveRankType;
                return true;
            }

            return false;
        }

        private void OnTick()
        {
            RecipientFilter? filter = null;
            var now = Environment.TickCount64;

            foreach (var player in Utilities.GetPlayers())
            {
                // Спектаторов НЕ пропускаем: ранг должен быть виден всегда.
                if (player.IsBot)
                    continue;

                var steamId64 = player.SteamID;
                if (steamId64 == 0)
                    continue;

                if (!TryGetDesiredRank(steamId64, out var rank, out var rankType))
                    continue;

                // Пишем в контроллер только если значение реально отличается.
                // Wins тоже проверяем: если движок сбросит его, Premier на табло пропадает.
                if (player.CompetitiveRankType != (sbyte)rankType ||
                    player.CompetitiveRanking != rank ||
                    player.CompetitiveWins != 777)
                {
                    // Что было ДО нашей перезаписи - показывает, кто менял ранг.
                    RecordRewrite(steamId64, player.CompetitiveRanking, player.CompetitiveRankType, player.CompetitiveWins, now);

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

        private HookResult MarkEvent(string name)
        {
            _lastEventName = name;
            _lastEventMs = Environment.TickCount64;
            return HookResult.Continue;
        }

        private void RecordRewrite(ulong steamId64, int oldRank, int oldType, int oldWins, long now)
        {
            _rewrites.AddOrUpdate(steamId64, 1, (_, v) => v + 1);
            _lastForeign[steamId64] = (oldRank, oldType, oldWins);

            var bucket = now - _lastEventMs < 500 ? "after_" + _lastEventName : "steady(no event)";
            _rewriteBuckets.AddOrUpdate(bucket, 1, (_, v) => v + 1);
        }

        // Консоль сервера: css_fakerank_debug. Запускать 2-3 раза во время раундов.
        [ConsoleCommand("css_fakerank_debug", "FakeRank rewrite statistics")]
        public void OnDebugCommand(CCSPlayerController? caller, CommandInfo info)
        {
            if (caller != null)
                return;

            var seconds = Math.Max(1, (Environment.TickCount64 - _statsSinceMs) / 1000.0);
            info.ReplyToCommand($"[FakeRank] v{ModuleVersion}, period {seconds:F0}s, RawXP mode={_useRawExperienceAsRanking}, type={_rankTypeForConfig}");

            foreach (var kv in _rewriteBuckets)
                info.ReplyToCommand($"  rewrites {kv.Key}: {kv.Value}");

            foreach (var player in Utilities.GetPlayers())
            {
                if (player.IsBot) continue;
                _rewrites.TryGetValue(player.SteamID, out var count);
                var foreign = _lastForeign.TryGetValue(player.SteamID, out var f)
                    ? $"last value we overwrote: rank={f.rank} type={f.type} wins={f.wins}"
                    : "never overwritten";
                info.ReplyToCommand(
                    $"  {player.PlayerName}: rewrites={count} ({count / seconds:F2}/s), " +
                    $"now rank={player.CompetitiveRanking} type={player.CompetitiveRankType} wins={player.CompetitiveWins}; {foreign}");
            }

            _rewrites.Clear();
            _rewriteBuckets.Clear();
            _statsSinceMs = Environment.TickCount64;
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

            _useRawExperienceAsRanking = false;
            _rankTypeForConfig = 12;

            try
            {
                var json = File.ReadAllText(ConfigFilePath);
                var config = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(json);

                if (config == null || !config.TryGetValue("LR_FakeRank", out var section))
                    return ranks;

                // Type: 1 -> 12, 2 -> 7, 3 -> 11 (по умолчанию 12) - как в оригинале.
                if (section.TryGetValue("Type", out var typeValue) &&
                    typeValue is JsonElement typeElement &&
                    typeElement.ValueKind == JsonValueKind.String &&
                    int.TryParse(typeElement.GetString(), out var type))
                {
                    _rankTypeForConfig = type switch
                    {
                        1 => 12,
                        2 => 7,
                        3 => 11,
                        _ => 12
                    };

                    // Type "3": ранг = сырой опыт игрока, таблица FakeRank не нужна
                    // (секции FakeRank в конфиге может вообще не быть).
                    _useRawExperienceAsRanking = type == 3;
                }

                if (section.TryGetValue("FakeRank", out var fakeRanksObject) &&
                    fakeRanksObject is JsonElement fakeRanksElement &&
                    fakeRanksElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var rank in fakeRanksElement.EnumerateObject())
                    {
                        if (int.TryParse(rank.Name, out var level) &&
                            rank.Value.ValueKind == JsonValueKind.String &&
                            int.TryParse(rank.Value.GetString(), out var competitiveRanking))
                        {
                            ranks[level] = (competitiveRanking, _rankTypeForConfig);
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
