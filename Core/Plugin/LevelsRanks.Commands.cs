namespace LevelsRanks;

public partial class LevelsRanks
{
    public void GrantExperience(string steamIdOrSteamId64, int experience)
    {
        var steamId = steamIdOrSteamId64;

        if (ulong.TryParse(steamIdOrSteamId64, out var steamId64))
            steamId = SteamIdConverter.ConvertToSteamId(steamId64);
        else
            steamId = NormalizeSteamID(steamIdOrSteamId64);


        if (OnlineUsers.TryGetValue(steamId, out var user))
        {
            var player = Utilities.GetPlayers().FirstOrDefault(p =>
                p.AuthorizedSteamID != null &&
                SteamIdConverter.ConvertToSteamId(p.AuthorizedSteamID.SteamId64) == steamId);

            if (player != null)
            {
                Logger.LogInformation($"{experience} experience to {steamId}.");
                if (experience >= 0)
                    ApplyExperienceUpdateSyncWithoutLimits(user, player, experience, Localizer["admin_grant_points"],
                        ChatColors.Green);
                else
                    ApplyExperienceUpdateSyncWithoutLimits(user, player, experience, Localizer["admin_revoke_points"],
                        ChatColors.DarkRed);
                return;
            }
        }


        Task.Run(async () =>
        {
            var userFromDb = await Database.GetUserFromDb(steamId);
            if (userFromDb != null)
            {
                userFromDb.Value += experience;
                if (userFromDb.Value < 0) userFromDb.Value = 0;

                CheckAndUpdateRank(userFromDb);

                await Database.UpdateUsersInDb(new List<User> { userFromDb });

                Logger.LogInformation($"{experience} experience to {steamIdOrSteamId64} (offline).");
            }
            else
            {
                Logger.LogWarning($"Player offline with SteamID {steamId} not found in database.");
            }
        });
    }

    [ConsoleCommand("css_lvl_giveexp_no_check", "Grants experience to a player by SteamID or SteamID64 without checking if they are online")]
    public void GrantExperienceNoCheckCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null)
        {
            if (commandInfo.ArgCount < 3)
            {
                Logger.LogInformation("Usage: css_lvl_giveexp_no_check <SteamID or SteamID64> <experience>");
                return;
            }

            var steamId = commandInfo.GetArg(1);
            if (!int.TryParse(commandInfo.GetArg(2), out var experience))
            {
                Logger.LogInformation("Invalid experience value.");
                return;
            }

            GrantExperienceNoCheck(steamId, experience);
        }
        else
        {
            player.PrintToChat(ReplaceColorPlaceholders(Localizer["command_console_only"]));
        }
    }

    public void GrantExperienceNoCheck(string steamIdOrSteamId64, int experience)
    {
        var steamId = steamIdOrSteamId64;

        if (ulong.TryParse(steamIdOrSteamId64, out var steamId64))
            steamId = SteamIdConverter.ConvertToSteamId(steamId64);
        else
            steamId = NormalizeSteamID(steamIdOrSteamId64);

        Task.Run(async () =>
        {
            var userFromDb = await Database.GetUserFromDb(steamId);
            if (userFromDb != null)
            {
                userFromDb.Value += experience;
                if (userFromDb.Value < 0) userFromDb.Value = 0;

                CheckAndUpdateRank(userFromDb);

                await Database.UpdateUsersInDb(new List<User> { userFromDb });

                Logger.LogInformation($"{experience} experience to {steamIdOrSteamId64} (offline).");
            }
            else
            {
                Logger.LogWarning($"Player offline with SteamID {steamId} not found in database.");
            }
        });
    }

    public string NormalizeSteamID(string steamId)
    {
        if (steamId.StartsWith("STEAM_0:")) steamId = "STEAM_1:" + steamId.Substring(8);
        return steamId;
    }

    [ConsoleCommand("css_lvl_reload", "Reloads the configuration files")]
    public void ReloadConfigsCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null)
            try
            {
                LoadConfig();
                Logger.LogInformation("Configuration successfully reloaded.");
            }
            catch (Exception ex)
            {
                Logger.LogInformation($"Error reloading configuration: {ex.Message}");
            }
        else
            player.PrintToChat(ReplaceColorPlaceholders(Localizer["command_console_only"]));
    }

    [ConsoleCommand("css_lvl_giveexp", "Grants experience to a player by SteamID or SteamID64")]
    public void GrantExperienceCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null)
        {
            if (commandInfo.ArgCount < 3)
            {
                Logger.LogInformation("Usage: grant_experience <SteamID or SteamID64> <experience>");
                return;
            }

            var steamId = commandInfo.GetArg(1);
            if (!int.TryParse(commandInfo.GetArg(2), out var experience))
            {
                Logger.LogInformation("Invalid experience value.");
                return;
            }

            GrantExperience(steamId, experience);
        }
        else
        {
            player.PrintToChat(ReplaceColorPlaceholders(Localizer["command_console_only"]));
        }
    }

    [ConsoleCommand("css_lvl_reset", "Resets statistics for all players")]
    public void ResetStatisticsCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null)
        {
            if (commandInfo.ArgCount < 2)
            {
                Logger.LogInformation("Usage: css_lvl_reset <type>");
                Logger.LogInformation("Types: all, exp, stats");
                return;
            }

            var resetType = commandInfo.GetArg(1).ToLower();

            Task.Run(async () =>
            {
                var users = await Database.GetAllUsers();
                foreach (var user in users)
                {
                    ResetUserStatistics(user, resetType);

                    if (OnlineUsers.ContainsKey(user.SteamId!))
                        await Server.NextFrameAsync(() =>
                        {
                            var player = Utilities.GetPlayers().FirstOrDefault(p =>
                                p.AuthorizedSteamID != null &&
                                SteamIdConverter.ConvertToSteamId(p.AuthorizedSteamID.SteamId64) == user.SteamId);
                            if (player != null)
                                ApplyExperienceUpdateSyncWithoutLimits(user, player, 0,
                                    Localizer["admin_revoke_points"], ChatColors.DarkRed);
                        });
                }

                await Database.UpdateUsersInDb(users);

                Logger.LogInformation($"Statistics reset for all players with type: {resetType}");
            });
        }
        else
        {
            player.PrintToChat(ReplaceColorPlaceholders(Localizer["command_console_only"]));
        }
    }

    [ConsoleCommand("css_lvl_del", "Resets statistics for a specific player")]
    public void ResetPlayerStatisticsCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null)
        {
            if (commandInfo.ArgCount < 2)
            {
                Logger.LogInformation("Usage: css_lvl_del <SteamID or SteamID64>");
                return;
            }

            var steamId = commandInfo.GetArg(1);

            if (ulong.TryParse(steamId, out var steamId64))
                steamId = SteamIdConverter.ConvertToSteamId(steamId64);
            else
                steamId = NormalizeSteamID(steamId);

            Task.Run(async () =>
            {
                var user = await Database.GetUserFromDb(steamId);
                if (user != null)
                {
                    ResetUserStatistics(user, "all");
                    await Database.UpdateUsersInDb(new List<User> { user });

                    if (OnlineUsers.ContainsKey(user.SteamId!))
                        await Server.NextFrameAsync(() =>
                        {
                            var player = Utilities.GetPlayers().FirstOrDefault(p =>
                                p.AuthorizedSteamID != null &&
                                SteamIdConverter.ConvertToSteamId(p.AuthorizedSteamID.SteamId64) == user.SteamId);
                            if (player != null)
                                ApplyExperienceUpdateSyncWithoutLimits(user, player, 0,
                                    Localizer["admin_revoke_points"], ChatColors.DarkRed);
                        });

                    Logger.LogInformation($"Statistics reset for player: {steamId}");


                    CheckAndUpdateRank(user);
                }
                else
                {
                    Logger.LogWarning($"Player with SteamID {steamId} not found in database.");
                }
            });
        }
        else
        {
            player.PrintToChat(ReplaceColorPlaceholders(Localizer["command_console_only"]));
        }
    }

    private void ResetUserStatistics(User user, string resetType)
    {
        switch (resetType)
        {
            case "all":
                user.Value = 0;
                user.Rank = 1;
                user.Kills = 0;
                user.Deaths = 0;
                user.Shoots = 0;
                user.Hits = 0;
                user.Headshots = 0;
                user.Assists = 0;
                user.RoundWin = 0;
                user.RoundLose = 0;
                break;

            case "exp":
                user.Value = 0;
                user.Rank = 1;
                break;

            case "stats":
                user.Kills = 0;
                user.Deaths = 0;
                user.Shoots = 0;
                user.Hits = 0;
                user.Headshots = 0;
                user.Assists = 0;
                user.RoundWin = 0;
                user.RoundLose = 0;
                user.Playtime = 0;
                break;

            default:
                Logger.LogWarning($"Unknown reset type: {resetType}");
                break;
        }
    }

    public string ReplaceColorPlaceholders(string message)
    {
        if (message.Contains('{'))
        {
            var modifiedValue = message;
            foreach (var field in typeof(ChatColors).GetFields())
            {
                var pattern = $"{{{field.Name}}}";
                if (message.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    modifiedValue = modifiedValue.Replace(pattern, field.GetValue(null)?.ToString(),
                        StringComparison.OrdinalIgnoreCase);
            }

            return modifiedValue;
        }

        return message;
    }
}
