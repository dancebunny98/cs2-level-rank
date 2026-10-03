namespace LevelsRanks;

public partial class LevelsRanks
{
    private void RegisterEventHandlers()
    {
        RegisterListener<Listeners.OnClientAuthorized>((slot, id) =>
        {
            TryAuthorizePlayer(slot, id, 0);
        });

        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventBombPickup>(OnBombPickup);
        RegisterEventHandler<EventBombDropped>(OnBombDropped);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventHostageRescued>(OnHostageRescued);
        RegisterEventHandler<EventHostageKilled>(OnHostageKilled);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundMvp>(OnRoundMvp);
    }

    private void TryAuthorizePlayer(int slot, SteamID steamId, int attempt)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not null && player.IsValid)
        {
            Task.Run(() => OnClientAuthorized(player, steamId));
            return;
        }

        if (attempt < 20)
        {
            AddTimer(0.25f, () => TryAuthorizePlayer(slot, steamId, attempt + 1));
        }
        else
        {
            Logger.LogWarning("Could not resolve player controller for authorized slot {Slot}", slot);
        }
    }

    private void OnClientAuthorized(CCSPlayerController player, SteamID steamId)
    {
        // Используем SteamID, переданный листенером напрямую, а не player.AuthorizedSteamID:
        // на момент вызова это поле у игрока иногда ещё не проставлено движком.
        var steamIdStr = SteamIdConverter.ConvertToSteamId(steamId.SteamId64);
        var playerName = player.PlayerName;

        Task.Run(async () =>
        {
            try
            {
                var userFromDb = await LoadOrCreateUserAsync(steamIdStr, playerName);

                Server.NextFrame(() => RegisterOnlineUser(steamIdStr, userFromDb, playerName));
            }
            catch (Exception e)
            {
                Logger.LogError(e.ToString());
            }
        });
    }

    /// <summary>
    /// Читает игрока из БД, а если строки нет - создаёт. Если строка уже существует (гонка
    /// двух авторизаций или дубликат ключа), берёт актуальные данные из БД, а НЕ новый
    /// пустой объект с нулевым опытом - иначе следующее сохранение затёрло бы накопленное.
    /// </summary>
    private async Task<User> LoadOrCreateUserAsync(string steamIdStr, string playerName)
    {
        var user = await Database.GetUserFromDb(steamIdStr);
        if (user != null) return user;

        var fresh = new User
        {
            SteamId = steamIdStr,
            ServerId = ServerId,
            Name = playerName,
            LastConnect = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Value = StatisticType == "1" || StatisticType == "2" ? 1000 : StartPoints,
            Rank = 1
        };

        if (await Database.AddUserToDb(fresh)) return fresh;

        return await Database.GetUserFromDb(steamIdStr) ?? fresh;
    }

    /// <summary>
    /// Выполняется в игровом потоке. Решает, какой объект User становится "живым".
    /// Правило: данные в памяти всегда новее данных в БД (БД пишется батчами с задержкой),
    /// поэтому объект из БД используется только если у нас нет ничего свежее.
    /// </summary>
    private void RegisterOnlineUser(string steamIdStr, User userFromDb, string playerName)
    {
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        User user;

        if (OnlineUsers.TryGetValue(steamIdStr, out var live))
        {
            // Повторная авторизация (смена карты и т.п.) без отключения - память новее БД.
            user = live;
            user.Name = playerName;
        }
        else if (_pendingSaves.TryGetValue(steamIdStr, out var pending))
        {
            // Игрок только что вышел, и его сохранение ещё не завершилось - БД ещё старая.
            user = pending;
            user.Name = playerName;
            user.LastConnect = now;
        }
        else
        {
            user = userFromDb;
            user.Name = playerName;
            user.LastConnect = now;
        }

        OnlineUsers[steamIdStr] = user;
        // Recalculate even when the database contained an old/invalid rank.
        CheckAndUpdateRank(user);
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull eventPlayerConnectFull, GameEventInfo gameEventInfo)
    {
        var player = eventPlayerConnectFull.Userid;
        if (player == null || player.AuthorizedSteamID == null) return HookResult.Continue;

        var steamIdStr = SteamIdConverter.ConvertToSteamId(player.AuthorizedSteamID.SteamId64);

        if (OnlineUsers.TryGetValue(steamIdStr, out var user))
            CheckAndUpdateRank(user);
        else
            Logger.LogWarning($"Player Online with SteamID {steamIdStr} not found in OnlineUsers.");

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventPlayerDisconnect, GameEventInfo gameEventInfo)
    {
        var player = eventPlayerDisconnect.Userid;
        if (player == null || player.IsBot) return HookResult.Continue;

        // При отключении AuthorizedSteamID часто уже null. Раньше из-за этого мы выходили
        // из обработчика, игрок оставался в OnlineUsers и НЕ сохранялся при выходе.
        var steamId64 = player.AuthorizedSteamID?.SteamId64 ?? player.SteamID;
        if (steamId64 == 0) return HookResult.Continue;

        var steamIdStr = SteamIdConverter.ConvertToSteamId(steamId64);
        if (OnlineUsers.TryRemove(steamIdStr, out var user))
        {
            var disconnectTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            user.Playtime += (int)(disconnectTime - user.LastConnect);
            user.LastConnect = (int)disconnectTime;

            // Пока запись не дошла до БД, держим объект здесь: если игрок тут же вернётся
            // (реконнект, смена карты), он получит эти данные, а не устаревшую строку из БД.
            _pendingSaves[steamIdStr] = user;

            _ = SaveDisconnectedUserAsync(steamIdStr, user);
        }

        return HookResult.Continue;
    }

    private async Task SaveDisconnectedUserAsync(string steamIdStr, User user)
    {
        await SaveUsersImmediatelyAsync(new[] { user }, "player disconnect");
        _pendingSaves.TryRemove(new KeyValuePair<string, User>(steamIdStr, user));
    }

    private void CheckAndUpdateRank(User user)
    {
        var newRank = RanksSettings.GetRankForExperience(user.Value, StatisticType);
        var oldRank = user.Rank;

        // Update the shared in-memory value before scheduling notifications.  TAB and
        // other modules read OnlineUsers directly, so deferring this assignment caused
        // stale ranks and allowed a later database write to appear as a rollback.
        if (newRank == oldRank) return;
        user.Rank = newRank;
        _userUpdateQueue.Enqueue(user);

        Server.NextFrame(() =>
        {
            var steamIdStr = user.SteamId;
            var player = Utilities.GetPlayers().FirstOrDefault(p =>
                p.AuthorizedSteamID != null &&
                SteamIdConverter.ConvertToSteamId(p.AuthorizedSteamID.SteamId64) == steamIdStr);

            if (player != null)
            {
                var rankName = Localizer[$"rank_{newRank}"];

                if (newRank > oldRank)
                {
                    player.PrintToChat(
                        ReplaceColorPlaceholders(Localizer["rank_up_message", rankName, user.Name!]));

                    if (PlaySound) player.ExecuteClientCommand($"play {SoundLvlUp}");

                    if (ShowLevelUpMessage)
                        foreach (var otherPlayer in Utilities.GetPlayers().Where(p =>
                                     p.AuthorizedSteamID != null && p.AuthorizedSteamID.SteamId64 !=
                                     player.AuthorizedSteamID!.SteamId64))
                            otherPlayer.PrintToChat(
                                ReplaceColorPlaceholders(Localizer["rank_up_broadcast", user.Name!, rankName]));
                }
                else
                {
                    player.PrintToChat(
                        ReplaceColorPlaceholders(Localizer["rank_down_message", rankName, user.Name!]));

                    if (PlaySound) player.ExecuteClientCommand($"play {SoundLvlDown}");

                    if (ShowLevelDownMessage)
                        foreach (var otherPlayer in Utilities.GetPlayers().Where(p =>
                                     p.AuthorizedSteamID != null && p.AuthorizedSteamID.SteamId64 !=
                                     player.AuthorizedSteamID!.SteamId64))
                            otherPlayer.PrintToChat(
                                ReplaceColorPlaceholders(Localizer["rank_down_broadcast", user.Name!, rankName]));
                }
            }
        });
    }


    private async Task ReauthorizeOnlinePlayers()
    {
        List<CCSPlayerController>? players = null;

        await Server.NextFrameAsync(() => { players = Utilities.GetPlayers().ToList(); });

        foreach (var player in players!)
        {
            if (player == null || player.IsBot || player.AuthorizedSteamID == null) continue;

            var steamIdStr = SteamIdConverter.ConvertToSteamId(player.AuthorizedSteamID.SteamId64);
            var playerName = player.PlayerName;

            try
            {
                var userFromDb = await LoadOrCreateUserAsync(steamIdStr, playerName);

                await Server.NextFrameAsync(() => RegisterOnlineUser(steamIdStr, userFromDb, playerName));
            }
            catch (Exception e)
            {
                Logger.LogError(e.ToString());
            }
        }
    }

    private HookResult OnPlayerDeath(EventPlayerDeath eventPlayerDeath, GameEventInfo gameEventInfo)
    {
        var attacker = eventPlayerDeath.Attacker;
        var victim = eventPlayerDeath.Userid;
        var assister = eventPlayerDeath.Assister;
        var headshot = eventPlayerDeath.Headshot;

        if (attacker == null || victim == null) return HookResult.Continue;

        var victimId = victim.AuthorizedSteamID?.SteamId64;
        var attackerId = attacker.AuthorizedSteamID?.SteamId64;

        var victimSteamIdStr = victimId != null ? SteamIdConverter.ConvertToSteamId(victimId.Value) : "BOT";
        var attackerSteamIdStr = attackerId != null ? SteamIdConverter.ConvertToSteamId(attackerId.Value) : "BOT";

        if (!OnlineUsers.TryGetValue(victimSteamIdStr, out var victimUser) && victimId != null)
            return HookResult.Continue;

        if (!OnlineUsers.TryGetValue(attackerSteamIdStr, out var attackerUser) && attackerId != null)
            return HookResult.Continue;
        
        bool isSuicide = attackerSteamIdStr == victimSteamIdStr;

        if (isSuicide)
        {
            ProcessPlayerSuicide(victimUser!, victim);
        }
        else
        {
            if (attacker.IsBot && !ExperienceFromBots)
            {
                if (victimUser != null)
                    ProcessPlayerDeath(victimUser, victim, null, attacker, attackerSteamIdStr, headshot, false);
            }
            else
            {
                ProcessPlayerDeath(victimUser!, victim, attackerUser, attacker, attackerSteamIdStr, headshot, true);
            }

            if (assister != null && assister.AuthorizedSteamID != null)
            {
                var assisterId = assister.AuthorizedSteamID.SteamId64;
                var assisterSteamIdStr = SteamIdConverter.ConvertToSteamId(assisterId);

                if (OnlineUsers.TryGetValue(assisterSteamIdStr, out var assisterUser))
                {
                    assisterUser.Assists++;
                    var assistExp = ExperienceSettings.GetExperience(StatisticType, "lr_assist");
                    ApplyExperienceUpdateSync(assisterUser, assister, (int)assistExp, Localizer["assist"],
                        Localizer["assist_color"]);
                }
                else
                {
                    Logger.LogError($"Assister user not found in OnlineUsers: {assisterSteamIdStr}");
                }
            }
        }

        return HookResult.Continue;
    }
    private void ProcessPlayerSuicide(User victimUser, CCSPlayerController victim)
    {
        if (victim == null)
        {
            Logger.LogError("Victim is null in ProcessPlayerSuicide");
            return;
        }
        
        if (!ExperienceFromBots && victim.IsBot)
        {
            return;
        }

        if (victimUser != null)
        {
            victimUser.Deaths++;
            var expVictim = ExperienceSettings.GetExperience(StatisticType, "lr_suicide");
            expVictim = -Math.Abs(expVictim);
            ApplyExperienceUpdateSync(victimUser, victim, (int)expVictim, Localizer["suicide"], Localizer["suicide_color"]);
        }
    }

    private void ProcessPlayerDeath(User victimUser, CCSPlayerController victim, User? attackerUser,
        CCSPlayerController attacker, string attackerSteamIdStr, bool headshot, bool isKill)
    {
        if (victim == null || attacker == null)
        {
            Logger.LogError("Victim or attacker is null in ProcessPlayerDeath");
            return;
        }
        
        if (!ExperienceFromBots && (attacker.IsBot || victim.IsBot))
        {
            return;
        }

        if (victimUser != null)
        {
            victimUser.Deaths++;
            var expChangeDeath = CalculateExperience(attackerUser, victimUser, headshot, out var expVictim);
            expVictim = -Math.Abs(expVictim);
            ApplyExperienceUpdateSync(victimUser, victim, expVictim, Localizer["death"], Localizer["death_color"]);
        }

        if (isKill && attackerUser != null)
        {
            attackerUser.Kills++;
            var expAttacker = CalculateExperience(attackerUser, victimUser, headshot, out _);
            ApplyExperienceUpdateSync(attackerUser, attacker, expAttacker, Localizer["kill"], Localizer["kill_color"]);

            if (headshot)
            {
                var headshotExp = (int)ExperienceSettings.GetExperience(StatisticType, "lr_headshot");
                ApplyExperienceUpdateSync(attackerUser, attacker, headshotExp, Localizer["headshot"],
                    Localizer["headshot_color"]);
                attackerUser.Headshots++;
            }

            if (_killStreaks.TryGetValue(attackerSteamIdStr, out var streak))
            {
                streak++;
                _killStreaks[attackerSteamIdStr] = streak;
                if (streak >= 2)
                {
                    var streakName = Localizer[$"killstreak_{streak}"];
                    var streakExp =
                        (int)(ExperienceSettings.Experience.Special_Bonuses.TryGetValue($"lr_bonus_{streak}",
                            out var exp)
                            ? exp
                            : 0);
                    ApplyExperienceUpdateSync(attackerUser, attacker, streakExp,
                        Localizer["killstreak_bonus", streakName], Localizer["killstreak_bonus_color"]);
                }
            }
            else
            {
                _killStreaks[attackerSteamIdStr] = 1;
            }
        }
    }

    private int CalculateExperience(User? attacker, User? victim, bool headshot, out int expVictim)
    {
        var expAttacker = 0;
        expVictim = 0;

        if (attacker == null || string.IsNullOrEmpty(attacker.SteamId) || attacker.SteamId == "BOT")
        {
            if (!ExperienceFromBots || StatisticType != "0") return expAttacker;
            attacker = new User { SteamId = "BOT", Value = 0 };
        }

        if (victim == null || string.IsNullOrEmpty(victim.SteamId) || victim.SteamId == "BOT")
        {
            if (!ExperienceFromBots || StatisticType != "0") return expAttacker;
            victim = new User { SteamId = "BOT", Value = 0 };
        }

        switch (StatisticType)
        {
            case "0":
                expAttacker = (int)ExperienceSettings.GetExperience("0", "lr_kill");
                expVictim = (int)-ExperienceSettings.GetExperience("0", "lr_death");
                break;
            case "1":
            {
                var killCoefficient =
                    Math.Max(0.5, Math.Min(2.0, ExperienceSettings.GetExperience("1", "lr_killcoeff")));
                expAttacker = Math.Max(1, (int)Math.Round((float)victim.Value / attacker.Value * 5.0));
                expVictim = -Math.Max(1, (int)Math.Round(expAttacker * killCoefficient));
            }
                break;
            case "2":
            {
                expAttacker = Math.Max(2, (int)Math.Round(victim.Value / 10.0 + 2));
                expVictim = -expAttacker;
            }
                break;
        }

        return expAttacker;
    }

    private HookResult OnHostageKilled(EventHostageKilled eventHostageKilled, GameEventInfo gameEventInfo)
    {
        var player = eventHostageKilled.Userid;

        if (player != null && player.AuthorizedSteamID != null)
        {
            var steamIdStr = SteamIdConverter.ConvertToSteamId(player.AuthorizedSteamID.SteamId64);
            if (OnlineUsers.TryGetValue(steamIdStr, out var user))
            {
                var expChange = (int)ExperienceSettings.GetExperience(StatisticType, "lr_hostagekilled");
                ApplyExperienceUpdateSync(user, player, expChange, Localizer["hostage_killed"],
                    Localizer["hostage_killed_color"]);
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd eventRoundEnd, GameEventInfo gameEventInfo)
    {
        try
        {
            var winner = eventRoundEnd.Winner;

            if (ShowUsualMessage == 2)
            {
                foreach (var player in Utilities.GetPlayers())
                    if (player.AuthorizedSteamID != null)
                        SendRoundSummaryMessage(player,
                            SteamIdConverter.ConvertToSteamId(player.AuthorizedSteamID.SteamId64));
                _roundExpChanges.Clear();
            }

            foreach (var player in Utilities.GetPlayers())
            {
                if (player.AuthorizedSteamID == null || !player.IsValid || player.Team == CsTeam.Spectator) continue;

                var steamIdStr = SteamIdConverter.ConvertToSteamId(player.AuthorizedSteamID.SteamId64);
                if (OnlineUsers.TryGetValue(steamIdStr, out var user))
                {
                    var expChange = 0;

                    if (player.Team == (CsTeam)winner)
                    {
                        user.RoundWin++;
                        expChange = (int)ExperienceSettings.GetExperience("0", "lr_winround");
                    }
                    else
                    {
                        user.RoundLose++;
                        expChange = (int)ExperienceSettings.GetExperience("0", "lr_loseround");
                    }

                    ApplyExperienceUpdateSync(user, player, expChange,
                        player.Team == (CsTeam)winner ? Localizer["win_round"] : Localizer["lose_round"],
                        player.Team == (CsTeam)winner ? Localizer["win_round_color"] : Localizer["lose_round_color"]);
                }
            }
        }
        catch (Exception e)
        {
            Logger.LogError(e.ToString());
        }

        IsRoundEnded = true;
        return HookResult.Continue;
    }


    private void AddRoundExpChange(string steamIdStr, int expChange)
    {
        _roundExpChanges.AddOrUpdate(steamIdStr, expChange, (key, oldValue) => oldValue + expChange);
    }

    private void SendRoundSummaryMessage(CCSPlayerController player, string convertToSteamId)
    {
        var steamId64 = player.AuthorizedSteamID!.SteamId64;
        var steamId64Str = steamId64.ToString();

        if (_roundExpChanges.TryGetValue(steamId64Str, out var totalExpChange))
        {
            Logger.LogInformation($"Sending round summary to {player.PlayerName}: {totalExpChange} points");

            if (totalExpChange > 0)
                player.PrintToChat(
                    ReplaceColorPlaceholders(Localizer["round_summary_positive", totalExpChange]));
            else if (totalExpChange < 0)
                player.PrintToChat(
                    ReplaceColorPlaceholders(Localizer["round_summary_negative", totalExpChange]));
            else
                player.PrintToChat(
                    ReplaceColorPlaceholders(Localizer["round_summary_neutral"]));
        }
        else
        {
            Logger.LogInformation($"No experience change recorded for player {steamId64Str}");
        }
    }


    private HookResult OnWeaponFire(EventWeaponFire eventWeaponFire, GameEventInfo gameEventInfo)
    {
        try
        {
            var player = eventWeaponFire.Userid;
            if (player == null || player.AuthorizedSteamID == null || !player.IsValid) return HookResult.Continue;

            var steamId64 = player.AuthorizedSteamID.SteamId64;
            var steamIdStr = SteamIdConverter.ConvertToSteamId(steamId64);

            if (OnlineUsers.TryGetValue(steamIdStr, out var user))
            {
                user.Shoots++;
                _userUpdateQueue.Enqueue(user);
            }
            else
            {
                Logger.LogWarning($"OnWeaponFire: Player {steamIdStr} not found in OnlineUsers");
            }
        }
        catch (Exception e)
        {
            Logger.LogError(e.ToString());
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt eventPlayerHurt, GameEventInfo gameEventInfo)
    {
        var attacker = eventPlayerHurt.Attacker;
        var victim = eventPlayerHurt.Userid;

        if (attacker != null && attacker != victim && attacker.AuthorizedSteamID != null)
        {
            var steamIdStr = SteamIdConverter.ConvertToSteamId(attacker.AuthorizedSteamID.SteamId64);
            if (OnlineUsers.TryGetValue(steamIdStr, out var user))
            {
                user.Hits++;
                _userUpdateQueue.Enqueue(user);
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnBombPickup(EventBombPickup eventBombPickup, GameEventInfo gameEventInfo)
    {
        var player = eventBombPickup.Userid;

        if (player != null && player.AuthorizedSteamID != null && !player.IsBot)
        {
            var steamIdStr = SteamIdConverter.ConvertToSteamId(player.AuthorizedSteamID.SteamId64);
            if (OnlineUsers.TryGetValue(steamIdStr, out var user))
            {
                var expChange = (int)ExperienceSettings.GetExperience(StatisticType, "lr_bombpickup");
                ApplyExperienceUpdateSync(user, player, expChange, Localizer["bomb_pickup"],
                    Localizer["bomb_pickup_color"]);
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnBombDropped(EventBombDropped eventBombDropped, GameEventInfo gameEventInfo)
    {
        var player = eventBombDropped.Userid;

        if (player != null && player.AuthorizedSteamID != null)
        {
            var steamIdStr = SteamIdConverter.ConvertToSteamId(player.AuthorizedSteamID.SteamId64);
            if (OnlineUsers.TryGetValue(steamIdStr, out var user))
            {
                var expChange = (int)ExperienceSettings.GetExperience(StatisticType, "lr_bombdropped");
                Server.NextFrame(() =>
                {
                    ApplyExperienceUpdateSync(user, player, expChange, Localizer["bomb_dropped"],
                        Localizer["bomb_dropped_color"]);
                });
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused eventBombDefused, GameEventInfo gameEventInfo)
    {
        var player = eventBombDefused.Userid;

        if (player != null && player.AuthorizedSteamID != null)
        {
            var steamIdStr = SteamIdConverter.ConvertToSteamId(player.AuthorizedSteamID.SteamId64);
            if (OnlineUsers.TryGetValue(steamIdStr, out var user))
            {
                var expChange = (int)ExperienceSettings.GetExperience("0", "lr_bombdefused");
                ApplyExperienceUpdateSync(user, player, expChange, Localizer["bomb_defused"],
                    Localizer["bomb_defused_color"]);
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnBombPlanted(EventBombPlanted eventBombPlanted, GameEventInfo gameEventInfo)
    {
        var player = eventBombPlanted.Userid;

        if (player != null && player.AuthorizedSteamID != null)
        {
            var steamIdStr = SteamIdConverter.ConvertToSteamId(player.AuthorizedSteamID.SteamId64);
            if (OnlineUsers.TryGetValue(steamIdStr, out var user))
            {
                var expChange = (int)ExperienceSettings.GetExperience("0", "lr_bombplanted");
                ApplyExperienceUpdateSync(user, player, expChange, Localizer["bomb_planted"],
                    Localizer["bomb_planted"]);
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnHostageRescued(EventHostageRescued eventHostageRescued, GameEventInfo gameEventInfo)
    {
        var player = eventHostageRescued.Userid;

        if (player != null && player.AuthorizedSteamID != null)
        {
            var steamIdStr = SteamIdConverter.ConvertToSteamId(player.AuthorizedSteamID.SteamId64);
            if (OnlineUsers.TryGetValue(steamIdStr, out var user))
            {
                var expChange = (int)ExperienceSettings.GetExperience("0", "lr_hostagerescued");
                ApplyExperienceUpdateSync(user, player, expChange, Localizer["hostage_rescued"],
                    Localizer["hostage_rescued_color"]);
            }
        }

        return HookResult.Continue;
    }

    public HookResult OnRoundStart(EventRoundStart eventRoundStart, GameEventInfo gameEventInfo)
    {
        var players = Utilities.GetPlayers().Count(p =>
            (p.Team == CsTeam.Terrorist || p.Team == CsTeam.CounterTerrorist) && p.AuthorizedSteamID != null);

        if (players < MinPlayersCount)
            foreach (var player in Utilities.GetPlayers().Where(p => p.AuthorizedSteamID != null))
            {
                player.PrintToChat(
                    ReplaceColorPlaceholders(Localizer["round_start_message"]));
                player.PrintToChat(
                    ReplaceColorPlaceholders(Localizer["round_start_few_players", players, MinPlayersCount]));
            }
        else if (ShowSpawnMessage)
            foreach (var player in Utilities.GetPlayers().Where(p => p.AuthorizedSteamID != null))
                player.PrintToChat(
                    ReplaceColorPlaceholders(Localizer["round_start_message"]));

        _killStreaks.Clear();
        IsRoundEnded = false;
        return HookResult.Continue;
    }

    private HookResult OnRoundMvp(EventRoundMvp eventRoundMvp, GameEventInfo gameEventInfo)
    {
        var player = eventRoundMvp.Userid;

        if (player == null || player.AuthorizedSteamID == null || !player.IsValid) return HookResult.Continue;

        var steamIdStr = SteamIdConverter.ConvertToSteamId(player.AuthorizedSteamID.SteamId64);

        if (OnlineUsers.TryGetValue(steamIdStr, out var user))
        {
            var mvpExp = ExperienceSettings.GetExperience(StatisticType, "lr_mvpround");
            ApplyExperienceUpdateSync(user, player, (int)mvpExp, Localizer["mvp_round"], Localizer["mvp_round_color"]);
        }
        else
        {
            Logger.LogError($"MVP user not found in OnlineUsers: {steamIdStr}");
        }

        return HookResult.Continue;
    }

}
