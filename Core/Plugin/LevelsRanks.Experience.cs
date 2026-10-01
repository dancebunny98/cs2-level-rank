namespace LevelsRanks;

public partial class LevelsRanks
{
    private void TryFlushMessages()
    {
        if (DateTime.UtcNow - lastFlushTime > batchInterval)
        {
            FlushMessagesAsync();
            lastFlushTime = DateTime.UtcNow;
        }
    }

    private async void FlushMessagesAsync()
    {
        await Task.Delay(100);
        while (_messageQueue.Count > 0)
        {
            var (player, color, newExp, expChange, eventDescription) = _messageQueue.Dequeue();
            if (player.IsValid)
            {
                SendChatMessage(player, color, newExp, expChange, eventDescription);
                await Task.Delay(50);
            }
        }
    }

    private async Task UpdateOnlineUserPlaytime()
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var onlineUser in OnlineUsers)
        {
            var user = onlineUser.Value;
            user.Playtime += (int)(currentTime - user.LastConnect);
            user.LastConnect = (int)currentTime;

            _userUpdateQueue.Enqueue(user);
        }

        // В режиме "только при выходе" (lr_db_savedataplayer_mode = "0") не форсируем запись в БД
        // каждую минуту - изменения (playtime и т.д.) остаются в том же объекте User в OnlineUsers
        // и будут сохранены при отключении игрока/смене карты.
        if (SaveDataPlayerModeLive)
            await ProcessUserUpdateQueue();
    }


    public void SetExperienceMultiplier(string steamId, double multiplier)
    {
        if (multiplier < 0) return;

        _experienceMultipliers[steamId] = multiplier;
    }

    public double GetExperienceMultiplier(string steamId)
    {
        return _experienceMultipliers.TryGetValue(steamId, out var multiplier) ? multiplier : 1.0;
    }

    private int GetActivePlayersCount()
    {
        return Utilities.GetPlayers().Count(p =>
            (p.Team == CsTeam.Terrorist || p.Team == CsTeam.CounterTerrorist) && p.AuthorizedSteamID != null);
    }

    public string GetColorFromLocalizer(string colorKey)
    {
        return ReplaceColorPlaceholders(Localizer[colorKey]);
    }

    public void ApplyExperienceUpdateSync(User user, CCSPlayerController player, int expChange, string eventDescription,
        string colorKey)
    {
        if (expChange == 0) return;
        
        if ((BlockExpDuringWarmup && IsWarmupPeriod()) || (!GiveExpOnRoundEnd && IsRoundEnded)) return;


        var playersCount = GetActivePlayersCount();
        if (playersCount < MinPlayersCount) return;


        var steamId = player.SteamID.ToString();


        var multiplier = GetExperienceMultiplier(steamId);


        if (expChange > 0) expChange = (int)(expChange * multiplier);

        var newExp = user.Value + expChange;
        user.Value = newExp < 0 ? 0 : newExp;


        CheckAndUpdateRank(user);


        _userUpdateQueue.Enqueue(user);


        var color = GetColorFromLocalizer(colorKey);


        switch (ShowUsualMessage)
        {
            case 1:
                SendChatMessage(player, color, user.Value, expChange, eventDescription);
                break;
            case 2:
                AddRoundExpChange(player.AuthorizedSteamID!.SteamId64.ToString(), expChange);
                break;
        }
    }


    public void ApplyExperienceUpdateSyncWithoutLimits(User user, CCSPlayerController player, int expChange,
        string eventDescription, char color)
    {
        if (expChange == 0) return;
        
        var newExp = user.Value += expChange;
        if (newExp < 0) user.Value = newExp = 0;

        _userUpdateQueue.Enqueue(user);

        CheckAndUpdateRank(user);

        if (ShowUsualMessage == 1)
            SendChatMessage2(player, color, user.Value, expChange, eventDescription);
        else if (ShowUsualMessage == 2) AddRoundExpChange(player.AuthorizedSteamID!.SteamId64.ToString(), expChange);
    }

    public void SendChatMessage2(CCSPlayerController player, char color, int newExp, int expChange,
        string eventDescription)
    {
        var expChangeStr = expChange > 0 ? $"+{expChange}" : expChange.ToString();
        var message = Localizer["experience_message", color, newExp, expChangeStr, eventDescription];
        player.PrintToChat(ReplaceColorPlaceholders(message));
    }

    public void SendChatMessage(CCSPlayerController player, string color, int newExp, int expChange,
        string eventDescription)
    {
        var expChangeStr = expChange > 0 ? $"+{expChange}" : expChange.ToString();
        var message = Localizer["experience_message", color, newExp, expChangeStr, eventDescription];
        player.PrintToChat(ReplaceColorPlaceholders(message));
    }


    private bool IsWarmupPeriod()
    {
        var gameRulesProxy =
            Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        return gameRulesProxy != null && gameRulesProxy.GameRules!.WarmupPeriod;
    }

    private async Task ProcessUserUpdateQueue()
    {
        // Одновременно работает только один сброс в БД (иначе при недоступной БД
        // каждые 5 секунд плодились бы новые бесконечные ретраи).
        if (!await _queueGate.WaitAsync(0)) return;

        try
        {
            const int batchSize = 50;
            var usersToUpdate = _userUpdateQueue.Drain();

            for (var i = 0; i < usersToUpdate.Count; i += batchSize)
            {
                var chunk = usersToUpdate.Skip(i).Take(batchSize).ToList();
                await Database.UpdateUsersInDbWithRetry(chunk);
            }
        }
        catch (Exception e)
        {
            Logger.LogError($"Error updating users in database: {e}");
        }
        finally
        {
            _queueGate.Release();
        }
    }

    /// <summary>
    /// Serializes urgent saves (map end and disconnect) with the periodic batch writer.
    /// Without this, an older full-row snapshot could finish after a newer one and roll
    /// back the player's value and rank in the database.
    /// </summary>
    private async Task SaveUsersImmediatelyAsync(IEnumerable<User> users, string reason)
    {
        var snapshot = users.Where(user => !string.IsNullOrWhiteSpace(user.SteamId)).Distinct().ToList();
        if (snapshot.Count == 0) return;

        await _queueGate.WaitAsync();
        try
        {
            await Database.UpdateUsersInDbWithRetry(snapshot);
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to save players ({reason}): {ex}");
        }
        finally
        {
            _queueGate.Release();
        }
    }
}
