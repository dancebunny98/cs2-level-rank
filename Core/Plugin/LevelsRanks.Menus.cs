namespace LevelsRanks;

public partial class LevelsRanks
{
    [ConsoleCommand("css_rank", "Показать статистику игрока")]
    public void HandleRankCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null)
        {
            Server.NextFrame(() =>
            {
                commandInfo.ReplyToCommand(ReplaceColorPlaceholders(Localizer["command_player_only"]));
            });
            return;
        }

        string searchTerm;
        bool isSteamId;
        
        if (commandInfo.ArgCount < 2)
        {
            searchTerm = SteamIdConverter.ConvertToSteamId(player.SteamID);  
            isSteamId = true;
        }
        else
        {
            searchTerm = commandInfo.GetArg(1);
            isSteamId = searchTerm.StartsWith("STEAM_1:");
        }

        Task.Run(async () =>
        {
            User? user = null;
            var totalPlayers = 0;
            var playerRank = 0;
            double kdr = 0;

            try
            {
                if (isSteamId)
                {
                    user = await Database.GetUserFromDb(searchTerm);
                }
                else
                {
                    user = await Database.GetUserByNameAsync(searchTerm);
                }

                if (user != null)
                {
                    var rankAndTotalPlayers = await Database.GetPlayerRankAndTotalPlayersAsync(user.SteamId!);
                    totalPlayers = rankAndTotalPlayers.totalPlayers;
                    playerRank = rankAndTotalPlayers.playerRank;
                    kdr = user.Deaths > 0 ? (double)user.Kills / user.Deaths : user.Kills;
                }

                var message = user == null
                    ? ReplaceColorPlaceholders(Localizer["player_not_found", searchTerm])
                    : ReplaceColorPlaceholders(Localizer["player_stats", user.Name!, playerRank, totalPlayers,
                        user.Value, user.Kills, user.Deaths, kdr]);

                Server.NextFrame(() =>
                {
                    player.PrintToChat(message);

                    if (ShowRankMessage && user != null)
                        foreach (var p in Utilities.GetPlayers().Where(p => p.AuthorizedSteamID != null && p != player))
                            p.PrintToChat(message);
                });
            }
            catch (Exception ex)
            {
                Server.NextFrame(() =>
                {
                    player.PrintToChat(
                        ReplaceColorPlaceholders(Localizer["command_error"]));
                    Logger.LogError($"Error in HandleRankCommand: {ex}");
                });
            }
        });
    }

    [ConsoleCommand("css_lvl", "Открыть меню Levels Ranks")]
    public void OpenLevelsRanksMenu(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null)
        {
            if (_api == null)
            {
                player.PrintToChat(ReplaceColorPlaceholders(Localizer["menu_api_not_found"]));
                return;
            }

            if (player.AuthorizedSteamID == null)
            {
                player.PrintToChat(ReplaceColorPlaceholders(Localizer["user_data_not_found"]));
                return;
            }

            var steamIdStr = SteamIdConverter.ConvertToSteamId(player.AuthorizedSteamID.SteamId64);
            if (!OnlineUsers.TryGetValue(steamIdStr, out var user))
            {
                player.PrintToChat(ReplaceColorPlaceholders(Localizer["user_data_not_found"]));
                return;
            }

            var menu = _api.GetMenu(PluginTitle, null, null);

            if (AdminManager.PlayerHasPermissions(player, AdminMenuFlag))
                menu.AddMenuOption(ReplaceColorPlaceholders(Localizer["admin_panel"]),
                    (p, option) => OpenAdminPanel(p));

            menu.AddMenuOption(ReplaceColorPlaceholders(Localizer["my_stats"]),
                (p, option) => OpenUserStatsMenu(p, user));
            menu.AddMenuOption(ReplaceColorPlaceholders(Localizer["top_players"]),
                (p, option) => OpenTopPlayersMenu(p));
            if (ShowRankList)
                menu.AddMenuOption(ReplaceColorPlaceholders(Localizer["all_ranks"]),
                    (p, option) => OpenAllRanksMenu(p));


            foreach (var menuOption in CustomMenuOptions)
                menu.AddMenuOption(menuOption.Name, (p, option) => menuOption.Action(p));

            menu.Open(player);
        }
    }

    private void OpenAllRanksMenu(CCSPlayerController player)
    {
        var menu = _api?.GetMenu(ReplaceColorPlaceholders(Localizer["all_ranks"]), null, null);

        foreach (var rank in RanksSettings.Ranks.OrderBy(r => r.Key))
        {
            var experienceNeeded = rank.Value.Value0;
            var rankName = Localizer[$"rank_{rank.Key}"];
            menu?.AddMenuOption(ReplaceColorPlaceholders(Localizer["rank_item", experienceNeeded, rankName]),
                (p, option) => { });
        }

        menu?.Open(player);
    }

    private void OpenTopPlayersMenu(CCSPlayerController player)
    {
        var menu = _api?.GetMenu(ReplaceColorPlaceholders(Localizer["top_players"]), null, null);
        menu?.AddMenuOption(ReplaceColorPlaceholders(Localizer["top_10_experience"]),
            (p, option) => ShowTopPlayersByExperience(p));
        menu?.AddMenuOption(ReplaceColorPlaceholders(Localizer["top_10_activity"]),
            (p, option) => ShowTopPlayersByPlaytime(p));
        menu?.Open(player);
    }

    private async void ShowTopPlayersByExperience(CCSPlayerController player)
    {
        var topPlayers = await Database.GetTopPlayersByExperience(TopCount);
        var menu = _api?.GetMenu(ReplaceColorPlaceholders(Localizer["top_10_experience"]), null, null);

        for (var i = 0; i < topPlayers.Count; i++)
        {
            var user = topPlayers[i];
            menu?.AddMenuOption(ReplaceColorPlaceholders(Localizer["top_player_item", i + 1, user.Value, user.Name!]),
                (p, option) => { });
        }
        
        Server.NextFrame(() => menu?.Open(player));
    }

    [ConsoleCommand("css_top", "Показать топ игроков по очкам опыта")]
    public void HandleTopCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null)
        {
            Server.NextFrame(() =>
            {
                commandInfo.ReplyToCommand(ReplaceColorPlaceholders(Localizer["command_player_only"]));
            });
            return;
        }

        Server.NextFrame(() => { ShowTopPlayersByExperience(player); });
    }

    private async void ShowTopPlayersByPlaytime(CCSPlayerController player)
    {
        var topPlayers = await Database.GetTopPlayersByPlaytime(TopCount);
        var menu = _api?.GetMenu(ReplaceColorPlaceholders(Localizer["top_10_activity"]), null, null);

        for (var i = 0; i < topPlayers.Count; i++)
        {
            var user = topPlayers[i];
            var playtime = TimeSpan.FromSeconds(user.Playtime);
            var formattedPlaytime = $"{playtime.Days}д {playtime.Hours}ч {playtime.Minutes}м";
            menu?.AddMenuOption(
                ReplaceColorPlaceholders(Localizer["top_player_activity_item", i + 1, formattedPlaytime, user.Name!]),
                (p, option) => { });
        }

        Server.NextFrame(() =>
        {
            menu?.Open(player);
        });
    }

    private void OpenUserStatsMenu(CCSPlayerController player, User user)
    {
        var menu = _api?.GetMenu(ReplaceColorPlaceholders(Localizer["my_stats"]), null, null);
        menu?.AddMenuOption(ReplaceColorPlaceholders(Localizer["show_stats_in_chat"]),
            (p, option) => ShowUserStats(p, user));
        if (_showResetMyStats)
            menu?.AddMenuOption(ReplaceColorPlaceholders(Localizer["reset_stats"]),
                (p, option) => ResetUserStats(p, user));

        menu?.Open(player);
    }

    private void ShowUserStats(CCSPlayerController player, User user)
    {
        var playtime = TimeSpan.FromSeconds(user.Playtime);
        var formattedPlaytime = $"{playtime.Days}д {playtime.Hours}ч {playtime.Minutes}м";

        player.PrintToChat(ReplaceColorPlaceholders(Localizer["user_stats_title"]));
        player.PrintToChat(ReplaceColorPlaceholders(Localizer["user_stat_experience", user.Value]));
        player.PrintToChat(ReplaceColorPlaceholders(Localizer["user_stat_kills", user.Kills]));
        player.PrintToChat(ReplaceColorPlaceholders(Localizer["user_stat_deaths", user.Deaths]));
        player.PrintToChat(ReplaceColorPlaceholders(Localizer["user_stat_headshots", user.Headshots]));
        player.PrintToChat(ReplaceColorPlaceholders(Localizer["user_stat_assists", user.Assists]));
        player.PrintToChat(ReplaceColorPlaceholders(Localizer["user_stat_shoots", user.Shoots]));
        player.PrintToChat(ReplaceColorPlaceholders(Localizer["user_stat_hits", user.Hits]));
        player.PrintToChat(ReplaceColorPlaceholders(Localizer["user_stat_round_win", user.RoundWin]));
        player.PrintToChat(ReplaceColorPlaceholders(Localizer["user_stat_round_lose", user.RoundLose]));
        player.PrintToChat(ReplaceColorPlaceholders(Localizer["user_stat_playtime", formattedPlaytime]));

        _api?.CloseMenu(player);
    }

    private void ResetUserStats(CCSPlayerController player, User user)
    {
        var steamIdStr = SteamIdConverter.ConvertToSteamId(player.AuthorizedSteamID!.SteamId64);
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (_resetStatsCooldown.LastResetTimestamps.TryGetValue(steamIdStr, out var lastResetTime))
            if (currentTime - lastResetTime < _resetMyStatsCooldown)
            {
                var timeLeft = TimeSpan.FromSeconds(_resetMyStatsCooldown - (currentTime - lastResetTime));
                Server.NextFrame(() =>
                {
                    player.PrintToChat(
                        ReplaceColorPlaceholders(Localizer["reset_stats_cooldown", timeLeft.Days, timeLeft.Hours,
                            timeLeft.Minutes, timeLeft.Seconds]));
                    _api?.CloseMenu(player);
                });
                return;
            }

        user.Kills = 0;
        user.Deaths = 0;
        user.Headshots = 0;
        user.Assists = 0;
        user.Shoots = 0;
        user.Hits = 0;
        user.RoundWin = 0;
        user.RoundLose = 0;
        user.Playtime = 0;


        if (StatisticType == "1" || StatisticType == "2")
            user.Value = 1000;
        else
            user.Value = 0;


        Task.Run(async () =>
        {
            try
            {
                await Database.UpdateUsersInDb(new List<User> { user });


                Server.NextFrame(() =>
                {
                    CheckAndUpdateRank(user);
                    player.PrintToChat(ReplaceColorPlaceholders(Localizer["stats_reset_success"]));
                });
            }
            catch (Exception ex)
            {
                Server.NextFrame(() =>
                {
                    player.PrintToChat(ReplaceColorPlaceholders(Localizer["stats_reset_failed"]));
                });
                Logger.LogError($"Error updating user stats in database: {ex}");
            }


            Server.NextFrame(() =>
            {
                _resetStatsCooldown.LastResetTimestamps[steamIdStr] = currentTime;
                _resetStatsCooldown.Save(_resetStatsCooldownFilePath);
                _api?.CloseMenu(player);
            });
        });
    }


    private void OpenAdminPanel(CCSPlayerController player)
    {
        var menu = _api?.GetMenu(ReplaceColorPlaceholders(Localizer["admin_panel"]), null, null);
        menu?.AddMenuOption(ReplaceColorPlaceholders(Localizer["grant_revoke_points"]),
            (p, option) => OpenGrantRevokeMenu(p));
        menu?.AddMenuOption(ReplaceColorPlaceholders(Localizer["reload_plugin_settings"]),
            (p, option) => ReloadPluginSettings(p));
        menu?.Open(player);
    }

    private void ReloadPluginSettings(CCSPlayerController player)
    {
        _api?.CloseMenu(player);

        LoadConfig();

        player.PrintToChat(
            ReplaceColorPlaceholders(Localizer["plugin_settings_reloaded"]));
    }

    private void OpenGrantRevokeMenu(CCSPlayerController player)
    {
        var menu = _api?.GetMenu(ReplaceColorPlaceholders(Localizer["grant_revoke_points"]), null, null);
        menu?.AddMenuOption(ReplaceColorPlaceholders(Localizer["grant_points"]),
            (p, option) => OpenPlayerSelectionMenu(p, true));
        menu?.AddMenuOption(ReplaceColorPlaceholders(Localizer["revoke_points"]),
            (p, option) => OpenPlayerSelectionMenu(p, false));
        menu?.Open(player);
    }

    private void OpenPlayerSelectionMenu(CCSPlayerController player, bool isGrant)
    {
        var menu = _api?.GetMenu(ReplaceColorPlaceholders(Localizer["select_player"]), null, null);
        var players = Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot).ToList();

        foreach (var targetPlayer in players)
            menu?.AddMenuOption(targetPlayer.PlayerName,
                (p, option) => OpenAmountSelectionMenu(p, targetPlayer, isGrant));

        menu?.Open(player);
    }

    private void OpenAmountSelectionMenu(CCSPlayerController player, CCSPlayerController targetPlayer, bool isGrant)
    {
        var menu = _api?.GetMenu(isGrant
            ? ReplaceColorPlaceholders(Localizer["grant_points"])
            : ReplaceColorPlaceholders(Localizer["revoke_points"]), null, null);
        var amounts = new[] { 10, 50, 100, 500 };

        foreach (var amount in amounts)
            menu?.AddMenuOption($"{amount} {ReplaceColorPlaceholders(Localizer["points"])}",
                (p, option) => AdjustPlayerPoints(p, targetPlayer, amount, isGrant));

        menu?.Open(player);
    }

    private void AdjustPlayerPoints(CCSPlayerController player, CCSPlayerController targetPlayer, int amount,
        bool isGrant)
    {
        if (OnlineUsers.TryGetValue(SteamIdConverter.ConvertToSteamId(targetPlayer.AuthorizedSteamID!.SteamId64),
                out var user))
        {
            var expChange = isGrant ? amount : -amount;
            ApplyExperienceUpdateSyncWithoutLimits(user, targetPlayer, expChange,
                isGrant ? Localizer["admin_grant_points"] : Localizer["admin_revoke_points"],
                isGrant ? ChatColors.Green : ChatColors.DarkRed);
            player.PrintToChat(
                ReplaceColorPlaceholders(Localizer[isGrant ? "points_granted" : "points_revoked", amount,
                    targetPlayer.PlayerName]));
        }
        else
        {
            player.PrintToChat(
                ReplaceColorPlaceholders(Localizer["user_data_not_found_for_player", targetPlayer.PlayerName]));
        }
    }

}
