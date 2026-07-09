using EasyShare.Models;
using Microsoft.Data.Sqlite;

namespace EasyShare.Services;

public sealed class LocalDatabase
{
    private const string StartMinimizedDefaultMigrationKey = "Migration.StartMinimizedDefaultFalse";
    private readonly AppDataPaths _paths;
    private readonly string _connectionString;

    public string DatabasePath => _paths.DatabasePath;

    public LocalDatabase(AppDataPaths paths)
    {
        _paths = paths;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task InitializeAsync()
    {
        _paths.EnsureCreated();

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS DriveRoutes (
                Id TEXT NOT NULL PRIMARY KEY,
                DisplayName TEXT NOT NULL,
                SharePointUrl TEXT NOT NULL,
                RemotePath TEXT NOT NULL,
                IsConnected INTEGER NOT NULL,
                StatusText TEXT NOT NULL,
                LastCheckedAt TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS SyncJobs (
                Id TEXT NOT NULL PRIMARY KEY,
                FileName TEXT NOT NULL,
                RouteDisplayName TEXT NOT NULL,
                State INTEGER NOT NULL,
                Progress INTEGER NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT NOT NULL PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """);

        await MigrateStartMinimizedDefaultAsync(connection);
    }

    public async Task<AppSettings> GetSettingsAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM Settings;";

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return new AppSettings
        {
            AuthenticationMode = GetEnum(values, nameof(AppSettings.AuthenticationMode), AuthenticationMode.BrowserSession),
            ClientId = Get(values, nameof(AppSettings.ClientId), Environment.GetEnvironmentVariable("EASYSHARE_CLIENT_ID") ?? string.Empty),
            TenantId = Get(values, nameof(AppSettings.TenantId), Environment.GetEnvironmentVariable("EASYSHARE_TENANT_ID") ?? "organizations"),
            StartWithWindows = GetBool(values, nameof(AppSettings.StartWithWindows), false),
            StartMinimized = GetBool(values, nameof(AppSettings.StartMinimized), false),
            AutoStartVirtualDrive = GetBool(values, nameof(AppSettings.AutoStartVirtualDrive), true),
            MountPoint = Get(values, nameof(AppSettings.MountPoint), "S:"),
            CacheMinutes = GetInt(values, nameof(AppSettings.CacheMinutes), 15),
            BrowserSessionStartUrl = Get(values, nameof(AppSettings.BrowserSessionStartUrl), "https://www.office.com/?auth=2"),
            BrowserKeepSessionAlive = GetBool(values, nameof(AppSettings.BrowserKeepSessionAlive), true),
            BrowserKeepAliveMinutes = GetInt(values, nameof(AppSettings.BrowserKeepAliveMinutes), 20),
            SetupWizardCompleted = GetBool(values, nameof(AppSettings.SetupWizardCompleted), false)
        };
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.AuthenticationMode), settings.AuthenticationMode.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.ClientId), settings.ClientId.Trim());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.TenantId), NormalizeTenant(settings.TenantId));
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.StartWithWindows), settings.StartWithWindows.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.StartMinimized), settings.StartMinimized.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.AutoStartVirtualDrive), settings.AutoStartVirtualDrive.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.MountPoint), NormalizeMountPoint(settings.MountPoint));
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.CacheMinutes), Math.Clamp(settings.CacheMinutes, 1, 1440).ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.BrowserSessionStartUrl), NormalizeBrowserSessionStartUrl(settings.BrowserSessionStartUrl));
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.BrowserKeepSessionAlive), settings.BrowserKeepSessionAlive.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.BrowserKeepAliveMinutes), Math.Clamp(settings.BrowserKeepAliveMinutes, 5, 240).ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.SetupWizardCompleted), settings.SetupWizardCompleted.ToString());
        await transaction.CommitAsync();
    }

    public async Task ResetAsync()
    {
        await InitializeAsync();

        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await ExecuteAsync(connection, transaction, "DELETE FROM DriveRoutes;");
        await ExecuteAsync(connection, transaction, "DELETE FROM SyncJobs;");
        await ExecuteAsync(connection, transaction, "DELETE FROM Settings;");

        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<DriveRoute>> GetRoutesAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, DisplayName, SharePointUrl, RemotePath, IsConnected, StatusText, LastCheckedAt
            FROM DriveRoutes
            ORDER BY DisplayName COLLATE NOCASE;
            """;

        var routes = new List<DriveRoute>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            routes.Add(new DriveRoute
            {
                Id = Guid.Parse(reader.GetString(0)),
                DisplayName = reader.GetString(1),
                SharePointUrl = reader.GetString(2),
                RemotePath = reader.GetString(3),
                IsConnected = reader.GetInt32(4) == 1,
                StatusText = reader.GetString(5),
                LastCheckedAt = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6))
            });
        }

        return routes;
    }

    public async Task AddRouteAsync(DriveRoute route)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO DriveRoutes (Id, DisplayName, SharePointUrl, RemotePath, IsConnected, StatusText, LastCheckedAt)
            VALUES ($id, $displayName, $sharePointUrl, $remotePath, $isConnected, $statusText, $lastCheckedAt);
            """;
        BindRouteParameters(command, route);

        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateRouteAsync(DriveRoute route)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE DriveRoutes
            SET DisplayName = $displayName,
                SharePointUrl = $sharePointUrl,
                RemotePath = $remotePath,
                IsConnected = $isConnected,
                StatusText = $statusText,
                LastCheckedAt = $lastCheckedAt
            WHERE Id = $id;
            """;
        BindRouteParameters(command, route);

        await command.ExecuteNonQueryAsync();
    }

    public async Task RemoveRouteAsync(Guid routeId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DriveRoutes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", routeId.ToString());

        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<SyncJob>> GetSyncJobsAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, FileName, RouteDisplayName, State, Progress, UpdatedAt
            FROM SyncJobs
            ORDER BY UpdatedAt DESC;
            """;

        var jobs = new List<SyncJob>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            jobs.Add(new SyncJob
            {
                Id = Guid.Parse(reader.GetString(0)),
                FileName = reader.GetString(1),
                RouteDisplayName = reader.GetString(2),
                State = (SyncJobState)reader.GetInt32(3),
                Progress = reader.GetInt32(4),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(5))
            });
        }

        return jobs;
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static async Task SaveSettingAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string key, string value)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO Settings (Key, Value)
            VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task MigrateStartMinimizedDefaultAsync(SqliteConnection connection)
    {
        if (await GetSettingValueAsync(connection, StartMinimizedDefaultMigrationKey) is not null)
        {
            return;
        }

        var currentValue = await GetSettingValueAsync(connection, nameof(AppSettings.StartMinimized));
        await using var transaction = await connection.BeginTransactionAsync();
        if (!bool.TryParse(currentValue, out var startMinimized) || startMinimized)
        {
            await SaveSettingAsync(connection, transaction, nameof(AppSettings.StartMinimized), bool.FalseString);
        }

        await SaveSettingAsync(connection, transaction, StartMinimizedDefaultMigrationKey, bool.TrueString);
        await transaction.CommitAsync();
    }

    private static async Task<string?> GetSettingValueAsync(SqliteConnection connection, string key)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync();
        return value as string;
    }

    private static void BindRouteParameters(SqliteCommand command, DriveRoute route)
    {
        command.Parameters.AddWithValue("$id", route.Id.ToString());
        command.Parameters.AddWithValue("$displayName", route.DisplayName);
        command.Parameters.AddWithValue("$sharePointUrl", route.SharePointUrl);
        command.Parameters.AddWithValue("$remotePath", route.RemotePath);
        command.Parameters.AddWithValue("$isConnected", route.IsConnected ? 1 : 0);
        command.Parameters.AddWithValue("$statusText", route.StatusText);
        command.Parameters.AddWithValue("$lastCheckedAt", route.LastCheckedAt?.ToString("O") ?? (object)DBNull.Value);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) ? value : fallback;

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    private static TEnum GetEnum<TEnum>(IReadOnlyDictionary<string, string> values, string key, TEnum fallback)
        where TEnum : struct, Enum =>
        values.TryGetValue(key, out var value) && Enum.TryParse(value, ignoreCase: true, out TEnum parsed)
            ? parsed
            : fallback;

    private static string NormalizeTenant(string tenantId) =>
        string.IsNullOrWhiteSpace(tenantId) ? "organizations" : tenantId.Trim();

    private static string NormalizeBrowserSessionStartUrl(string value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            ? uri.ToString()
            : "https://www.office.com/?auth=2";

    private static string NormalizeMountPoint(string mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return "S:";
        }

        var normalized = mountPoint.Trim().ToUpperInvariant();
        return normalized.Length == 1 ? $"{normalized}:" : normalized;
    }
}
