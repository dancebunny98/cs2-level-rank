using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CounterStrikeSharp.API.Core;

public class PlayerRankApi : IPlayerRankApi
{
    private static LevelsRanksModuleFakeRank.LevelsRanksModuleFakeRank _core = null!;
    private readonly Dictionary<ulong, (int originalRank, int originalRankType)> _originalRanks = new();

    // Кэш кастомных рангов в памяти. null-значение = "файла нет" (тоже кэшируем,
    // чтобы OnTick не ходил на диск каждый тик). Диск читается один раз на игрока.
    private static readonly ConcurrentDictionary<ulong, PlayerRankData?> _customCache = new();

    // Идентификатор сервера (LevelsRanks Core -> settings.json -> lr_server_id).
    // Кастомные ранги хранятся в файлах на диске (не в БД), поэтому если несколько
    // серверов делят один и тот же каталог плагина, их нужно различать по подпапке.
    public static string ServerId { get; set; } = "default";

    public PlayerRankApi(LevelsRanksModuleFakeRank.LevelsRanksModuleFakeRank core)
    {
        _core = core;
    }

    public void DisableRank(CCSPlayerController player)
    {
        var steamId = player.SteamID;
        if (!_originalRanks.ContainsKey(steamId))
        {
            _originalRanks[steamId] = (player.CompetitiveRanking, player.CompetitiveRankType);
        }

        player.CompetitiveRanking = 0;
        player.CompetitiveRankType = 0;
    }

    public void SetCustomRank(CCSPlayerController player, int rank, int rankType)
    {
        var steamId = player.SteamID;
        if (!_originalRanks.ContainsKey(steamId))
        {
            _originalRanks[steamId] = (player.CompetitiveRanking, player.CompetitiveRankType);
        }

        var data = new PlayerRankData { Rank = rank, RankType = rankType };

        // Сначала кэш - OnTick подхватит новый ранг уже на следующем тике.
        _customCache[steamId] = data;

        player.CompetitiveRanking = rank;
        player.CompetitiveRankType = (sbyte)rankType;

        SavePlayerRankToFile(steamId, data);
    }

    public void ResetRank(CCSPlayerController player)
    {
        var steamId = player.SteamID;

        if (_originalRanks.TryGetValue(steamId, out var originalRank))
        {
            player.CompetitiveRanking = originalRank.originalRank;
            player.CompetitiveRankType = (sbyte)originalRank.originalRankType;
            _originalRanks.Remove(steamId);
        }

        // Кастомный ранг снимаем всегда (даже после перезагрузки плагина, когда
        // _originalRanks пуст) - иначе файл остаётся и ранг "залипает".
        _customCache[steamId] = null;
        DeletePlayerRankFile(steamId);
    }

    /// <summary>Кэшированный кастомный ранг; при первом обращении читает файл один раз.</summary>
    public static bool TryGetCustomRank(ulong steamId, [NotNullWhen(true)] out PlayerRankData? data)
    {
        if (!_customCache.TryGetValue(steamId, out data))
        {
            data = LoadPlayerRankFromFile(steamId);
            _customCache[steamId] = data;
        }

        return data != null;
    }

    /// <summary>Сбросить кэш игрока (при выходе), файл не трогаем.</summary>
    public static void Forget(ulong steamId) => _customCache.TryRemove(steamId, out _);

    private static void SavePlayerRankToFile(ulong steamId, PlayerRankData data)
    {
        try
        {
            File.WriteAllText(GetPlayerRankFilePath(steamId), JsonSerializer.Serialize(data));
        }
        catch (Exception)
        {
            // ранг остаётся в кэше на время сессии, даже если диск недоступен
        }
    }

    public static PlayerRankData? LoadPlayerRankFromFile(ulong steamId)
    {
        try
        {
            var filePath = GetPlayerRankFilePath(steamId);
            if (!File.Exists(filePath))
                return null;

            return JsonSerializer.Deserialize<PlayerRankData>(File.ReadAllText(filePath));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void DeletePlayerRankFile(ulong steamId)
    {
        try
        {
            var filePath = GetPlayerRankFilePath(steamId);
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (Exception)
        {
        }
    }

    private static string GetPlayerRankFilePath(ulong steamId)
    {
        var safeServerId = string.Concat(ServerId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (string.IsNullOrWhiteSpace(safeServerId))
            safeServerId = "default";

        var dataDirectory = Path.Combine(_core.ModuleDirectory, "PlayerData", safeServerId);
        Directory.CreateDirectory(dataDirectory);

        return Path.Combine(dataDirectory, $"{steamId}_rank.json");
    }
}

public class PlayerRankData
{
    public int Rank { get; set; }
    public int RankType { get; set; }
}
