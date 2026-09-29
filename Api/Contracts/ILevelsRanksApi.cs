using System.Collections.Concurrent;
using CounterStrikeSharp.API.Core;

namespace LevelsRanksApi
{
    public interface ILevelsRanksApi
    {
        Task ConnectAsync();
        string? TableName { get; set; }
        string? DbConnectionString { get; }
        /// <summary>
        /// Идентификатор этого сервера (настройка lr_server_id в settings.json).
        /// Используется Core и модулями для того, чтобы несколько серверов CS2 могли
        /// делить одну базу данных, но хранить свою статистику раздельно, не создавая
        /// отдельного пользователя под каждый сервер.
        /// </summary>
        string ServerId { get; }
        void ApplyExperienceUpdateSync(User user, CCSPlayerController player, int expChange, string eventDescription, string color);
        void ApplyExperienceUpdateSyncWithoutLimits(User user, CCSPlayerController player, int expChange, string eventDescription, char color);
        Task<Dictionary<string, int>> GetCurrentRanksAsync();
        ulong ConvertToSteamId64(string steamId);
        string ConvertToSteamId(ulong steamId64);
        void RegisterMenuOption(string menuOptionName, Action<CCSPlayerController> action);
        void UnregisterMenuOption(string menuOptionName);
        void SetExperienceMultiplier(string steamId, double multiplier);
        double GetExperienceMultiplier(string steamId);
        bool GetExperienceFromBots();
		ConcurrentDictionary<string, User> OnlineUsers { get; }
    }
}