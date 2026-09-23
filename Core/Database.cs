using LevelsRanksApi;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace LevelsRanks;

public class Database
{
    private static string? _connectionString;
    private static string? _tableName;

    // Идентификатор сервера. Позволяет использовать ОДНУ базу/таблицу для нескольких
    // серверов CS2 без создания отдельного пользователя под каждый сервер:
    // строки различаются парой (steam, server_id), а не только steam.
    private static string _serverId = "default";

    private readonly ILogger<Database> _logger;
    private readonly LevelsRanks _plugin;

    public Database(LevelsRanks plugin, string? connectionString, string? tableName, string? serverId,
        ILogger<Database> logger)
    {
        _plugin = plugin;
        _connectionString = connectionString;
        _tableName = tableName;
        _serverId = string.IsNullOrWhiteSpace(serverId) ? "default" : serverId;
        _logger = logger;
    }

    public async Task<Dictionary<string, int>> GetCurrentRanksAsync()
    {
        var ranks = new Dictionary<string, int>();

        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            var commandText = $"SELECT `steam`, `rank` FROM `{_tableName}` WHERE `server_id` = @serverId";
            await using var command = new MySqlCommand(commandText, connection);
            command.Parameters.AddWithValue("@serverId", _serverId);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var steamId = reader.GetString(0); // Используем индекс вместо GetOrdinal для оптимизации
                var rank = reader.GetInt32(1);
                ranks[steamId] = rank;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in GetCurrentRanksAsync: {ex}");
        }

        return ranks;
    }



    public async Task UpdateUsersInDbWithRetry(IEnumerable<User> users)
    {
        const int maxRetries = 3;
        var retryCount = 0;

        while (retryCount < maxRetries)
            try
            {
                await UpdateUsersInDb(users);
                return;
            }
            catch (MySqlException ex) when (ex.Number == 1213)
            {
                retryCount++;
                if (retryCount == maxRetries) throw;
                await Task.Delay(1000);
            }
    }

    public async Task<List<User>> GetAllUsers()
    {
        var users = new List<User>();

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        var commandText = $"SELECT * FROM `{_tableName}` WHERE `server_id` = @serverId";
        await using var command = new MySqlCommand(commandText, connection);
        command.Parameters.AddWithValue("@serverId", _serverId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            users.Add(ReadUser(reader));

        return users;
    }

    public async Task CreateTable()
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        // server_id входит в первичный ключ: одна и та же учётная запись (steam) может
        // иметь независимую строку статистики на каждом сервере, при этом сам игрок
        // остаётся "одним и тем же" пользователем — отдельная регистрация под каждый
        // сервер не требуется.
        var commandText = $@"
                CREATE TABLE IF NOT EXISTS `{_tableName}` (
                    `steam` VARCHAR(22) NOT NULL,
                    `server_id` VARCHAR(64) NOT NULL DEFAULT 'default',
                    `name` VARCHAR(32),
                    `value` INT NOT NULL DEFAULT 0,
                    `rank` INT NOT NULL DEFAULT 0,
                    `kills` INT NOT NULL DEFAULT 0,
                    `deaths` INT NOT NULL DEFAULT 0,
                    `shoots` INT NOT NULL DEFAULT 0,
                    `hits` INT NOT NULL DEFAULT 0,
                    `headshots` INT NOT NULL DEFAULT 0,
                    `assists` INT NOT NULL DEFAULT 0,
                    `round_win` INT NOT NULL DEFAULT 0,
                    `round_lose` INT NOT NULL DEFAULT 0,
                    `playtime` INT NOT NULL DEFAULT 0,
                    `lastconnect` INT NOT NULL DEFAULT 0,
                    PRIMARY KEY (`steam`, `server_id`),
                    KEY `idx_server_value` (`server_id`, `value`)
                );";

        await using var command = new MySqlCommand(commandText, connection);
        await command.ExecuteNonQueryAsync();

        await EnsureServerIdColumn(connection);
    }

    // Мягкая миграция для баз, созданных до появления server_id: если таблица уже
    // существует в старом виде (steam как единственный PRIMARY KEY, без server_id),
    // добавляем колонку и переопределяем первичный ключ, не теряя данные.
    private async Task EnsureServerIdColumn(MySqlConnection connection)
    {
        try
        {
            var checkColumnText = @"
                SELECT COUNT(*) FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tableName AND COLUMN_NAME = 'server_id';";
            await using (var checkCommand = new MySqlCommand(checkColumnText, connection))
            {
                checkCommand.Parameters.AddWithValue("@tableName", _tableName);
                var exists = Convert.ToInt32(await checkCommand.ExecuteScalarAsync()) > 0;
                if (exists) return;
            }

            _logger.LogWarning(
                $"Column `server_id` is missing in `{_tableName}`. Running one-time migration to add it (existing rows will be assigned to server_id='default').");

            var alterText = $@"
                ALTER TABLE `{_tableName}`
                ADD COLUMN `server_id` VARCHAR(64) NOT NULL DEFAULT 'default' AFTER `steam`,
                DROP PRIMARY KEY,
                ADD PRIMARY KEY (`steam`, `server_id`),
                ADD KEY `idx_server_value` (`server_id`, `value`);";
            await using var alterCommand = new MySqlCommand(alterText, connection);
            await alterCommand.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                $"Automatic server_id migration for `{_tableName}` failed. Please run the migration SQL manually (see MIGRATION.md): {ex}");
        }
    }

    public async Task<User?> GetUserFromDb(string steamId)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        var commandText = $"SELECT * FROM `{_tableName}` WHERE `steam` = @steamId AND `server_id` = @serverId";
        await using var command = new MySqlCommand(commandText, connection);
        command.Parameters.AddWithValue("@steamId", steamId);
        command.Parameters.AddWithValue("@serverId", _serverId);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return ReadUser(reader);

        return null;
    }

    public async Task AddUserToDb(User user)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        var commandText = $@"
                INSERT INTO `{_tableName}` (`steam`, `server_id`, `name`, `value`, `rank`, `kills`, `deaths`, `shoots`, `hits`, `headshots`, `assists`, `round_win`, `round_lose`, `playtime`, `lastconnect`)
                VALUES (@steam, @serverId, @name, @value, @rank, @kills, @deaths, @shoots, @hits, @headshots, @assists, @round_win, @round_lose, @playtime, @lastconnect);";

        await using var command = new MySqlCommand(commandText, connection);
        command.Parameters.AddWithValue("@steam", user.SteamId);
        command.Parameters.AddWithValue("@serverId", _serverId);
        command.Parameters.AddWithValue("@name", user.Name);
        command.Parameters.AddWithValue("@value", user.Value);
        command.Parameters.AddWithValue("@rank", user.Rank);
        command.Parameters.AddWithValue("@kills", user.Kills);
        command.Parameters.AddWithValue("@deaths", user.Deaths);
        command.Parameters.AddWithValue("@shoots", user.Shoots);
        command.Parameters.AddWithValue("@hits", user.Hits);
        command.Parameters.AddWithValue("@headshots", user.Headshots);
        command.Parameters.AddWithValue("@assists", user.Assists);
        command.Parameters.AddWithValue("@round_win", user.RoundWin);
        command.Parameters.AddWithValue("@round_lose", user.RoundLose);
        command.Parameters.AddWithValue("@playtime", user.Playtime);
        command.Parameters.AddWithValue("@lastconnect", user.LastConnect);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<int?> GetPlayerRankAsync(string steamId)
    {
        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            var commandText = $"SELECT `rank` FROM `{_tableName}` WHERE `steam` = @steamId AND `server_id` = @serverId";
            await using var command = new MySqlCommand(commandText, connection);
            command.Parameters.AddWithValue("@steamId", steamId);
            command.Parameters.AddWithValue("@serverId", _serverId);

            var rank = await command.ExecuteScalarAsync();
            return rank != null ? (int?)Convert.ToInt32(rank) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in GetPlayerRankAsync: {ex}");
            return null;
        }
    }

    public async Task UpdatePlayerRankAsync(string steamId, int newRank)
    {
        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            var commandText = $"UPDATE `{_tableName}` SET `rank` = @newRank WHERE `steam` = @steamId AND `server_id` = @serverId";
            await using var command = new MySqlCommand(commandText, connection);
            command.Parameters.AddWithValue("@newRank", newRank);
            command.Parameters.AddWithValue("@steamId", steamId);
            command.Parameters.AddWithValue("@serverId", _serverId);

            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in UpdatePlayerRankAsync: {ex}");
        }
    }

    public static async Task UpdateUsersInDb(IEnumerable<User> users)
    {
        if (!users.Any()) return;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            foreach (var user in users)
            {
                var commandText = $@"
                        UPDATE `{_tableName}` SET 
                        `name` = @name, 
                        `value` = @value, 
                        `rank` = @rank, 
                        `kills` = @kills, 
                        `deaths` = @deaths, 
                        `shoots` = @shoots, 
                        `hits` = @hits, 
                        `headshots` = @headshots, 
                        `assists` = @assists, 
                        `round_win` = @round_win, 
                        `round_lose` = @round_lose, 
                        `playtime` = @playtime, 
                        `lastconnect` = @lastconnect 
                        WHERE `steam` = @steam AND `server_id` = @serverId;";

                await using var command = new MySqlCommand(commandText, connection, transaction);
                command.Parameters.AddWithValue("@steam", user.SteamId);
                command.Parameters.AddWithValue("@serverId", _serverId);
                command.Parameters.AddWithValue("@name", user.Name);
                command.Parameters.AddWithValue("@value", user.Value);
                command.Parameters.AddWithValue("@rank", user.Rank);
                command.Parameters.AddWithValue("@kills", user.Kills);
                command.Parameters.AddWithValue("@deaths", user.Deaths);
                command.Parameters.AddWithValue("@shoots", user.Shoots);
                command.Parameters.AddWithValue("@hits", user.Hits);
                command.Parameters.AddWithValue("@headshots", user.Headshots);
                command.Parameters.AddWithValue("@assists", user.Assists);
                command.Parameters.AddWithValue("@round_win", user.RoundWin);
                command.Parameters.AddWithValue("@round_lose", user.RoundLose);
                command.Parameters.AddWithValue("@playtime", user.Playtime);
                command.Parameters.AddWithValue("@lastconnect", user.LastConnect);

                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch (Exception)
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<User?> GetUserByNameAsync(string name)
    {
        User? user = null;

        try
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = $"SELECT * FROM `{_tableName}` WHERE `name` LIKE @Name AND `server_id` = @serverId LIMIT 1";
            using var command = new MySqlCommand(query, connection);
            command.Parameters.AddWithValue("@Name", "%" + name + "%");
            command.Parameters.AddWithValue("@serverId", _serverId);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                user = ReadUser(reader);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in GetUserByName: {ex}");
        }

        return user;
    }


    public async Task<(int totalPlayers, int playerRank)> GetPlayerRankAndTotalPlayersAsync(string steamId)
    {
        var totalPlayers = 0;
        var playerRank = 0;

        try
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            var countQuery = $"SELECT COUNT(*) FROM `{_tableName}` WHERE `server_id` = @serverId";
            using var countCommand = new MySqlCommand(countQuery, connection);
            countCommand.Parameters.AddWithValue("@serverId", _serverId);
            totalPlayers = Convert.ToInt32(await countCommand.ExecuteScalarAsync());

            var rankQuery = $@"
                SELECT COUNT(*) + 1
                FROM `{_tableName}`
                WHERE `server_id` = @serverId
                  AND value > (SELECT value FROM `{_tableName}` WHERE steam = @SteamId AND server_id = @serverId)";
            using var rankCommand = new MySqlCommand(rankQuery, connection);
            rankCommand.Parameters.AddWithValue("@SteamId", steamId);
            rankCommand.Parameters.AddWithValue("@serverId", _serverId);

            playerRank = Convert.ToInt32(await rankCommand.ExecuteScalarAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in GetPlayerRankAndTotalPlayers: {ex}");
        }

        return (totalPlayers, playerRank);
    }

    public async Task<List<User>> GetTopPlayersByExperience(int topN)
    {
        var users = new List<User>();

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        var commandText = $"SELECT * FROM `{_tableName}` WHERE `server_id` = @serverId AND `lastconnect` > 0 ORDER BY `value` DESC LIMIT @topN";
        await using var command = new MySqlCommand(commandText, connection);
        command.Parameters.AddWithValue("@serverId", _serverId);
        command.Parameters.AddWithValue("@topN", topN);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            users.Add(ReadUser(reader));

        return users;
    }

    public async Task<List<User>> GetTopPlayersByPlaytime(int topN)
    {
        var users = new List<User>();

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        var commandText = $"SELECT * FROM `{_tableName}` WHERE `server_id` = @serverId AND `lastconnect` > 0 ORDER BY `playtime` DESC LIMIT @topN";
        await using var command = new MySqlCommand(commandText, connection);
        command.Parameters.AddWithValue("@serverId", _serverId);
        command.Parameters.AddWithValue("@topN", topN);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            users.Add(ReadUser(reader));

        return users;
    }

    // Аналог lr_cleandb_days из оригинального плагина: игроков, которые не заходили
    // дольше `days` дней, не удаляем, а обнуляем lastconnect - тогда они перестают
    // попадать в топы (см. фильтр `lastconnect` > 0 выше), но статистика сохраняется
    // и восстанавливается автоматически при следующем заходе игрока.
    public async Task<int> CleanupInactiveUsersAsync(int days)
    {
        if (days <= 0) return 0;

        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)days * 86400;

            var commandText =
                $"UPDATE `{_tableName}` SET `lastconnect` = 0 WHERE `server_id` = @serverId AND `lastconnect` > 0 AND `lastconnect` < @cutoff";
            await using var command = new MySqlCommand(commandText, connection);
            command.Parameters.AddWithValue("@serverId", _serverId);
            command.Parameters.AddWithValue("@cutoff", cutoff);

            var affected = await command.ExecuteNonQueryAsync();
            if (affected > 0)
                _logger.LogInformation(
                    $"CleanDB: hidden {affected} inactive player(s) from top/stats (inactive > {days} days).");

            return affected;
        }
        catch (Exception ex)
        {
            _logger.LogError($"CleanDB failed: {ex}");
            return 0;
        }
    }

    private static User ReadUser(MySqlDataReader reader)
    {
        return new User
        {
            SteamId = reader.GetString("steam"),
            ServerId = reader.GetString("server_id"),
            Name = reader.GetString("name"),
            Value = reader.GetInt32("value"),
            Rank = reader.GetInt32("rank"),
            Kills = reader.GetInt32("kills"),
            Deaths = reader.GetInt32("deaths"),
            Shoots = reader.GetInt32("shoots"),
            Hits = reader.GetInt32("hits"),
            Headshots = reader.GetInt32("headshots"),
            Assists = reader.GetInt32("assists"),
            RoundWin = reader.GetInt32("round_win"),
            RoundLose = reader.GetInt32("round_lose"),
            Playtime = reader.GetInt32("playtime"),
            LastConnect = reader.GetInt32("lastconnect")
        };
    }

    public static string? BuildConnectionString(DatabaseConnection connection)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Database = connection.Database,
            UserID = connection.User,
            Password = connection.Password,
            Server = connection.Host,
            Port = (uint)connection.Port,
            Pooling = true,
            MinimumPoolSize = 0,
            MaximumPoolSize = 640,
            ConnectionIdleTimeout = 30
        };

        return builder.ConnectionString;
    }

    public async Task ConnectAsync()
    {
        if (_connectionString == null)
            throw new InvalidOperationException("ConnectionString is not set.");

        using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
    }
}
