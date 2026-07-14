using EasyShare.Models;
using EasyShare.Resources;
using Microsoft.Data.Sqlite;
using System.Text.Json;

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
                LastCheckedAt TEXT NULL,
                SiteId TEXT NULL,
                DriveId TEXT NULL,
                RootItemId TEXT NULL,
                FolderWebUrl TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS SyncJobs (
                Id TEXT NOT NULL PRIMARY KEY,
                RouteId TEXT NULL,
                FileName TEXT NOT NULL,
                RouteDisplayName TEXT NOT NULL,
                RelativePath TEXT NULL,
                PayloadPath TEXT NULL,
                ExpectedModifiedAt TEXT NULL,
                State INTEGER NOT NULL,
                Progress INTEGER NOT NULL,
                UpdatedAt TEXT NOT NULL,
                Attempts INTEGER NOT NULL DEFAULT 0,
                LastError TEXT NULL,
                NextAttemptAt TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS DirectoryCache (
                RouteId TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                CachedAt TEXT NOT NULL,
                ItemsJson TEXT NOT NULL,
                PRIMARY KEY (RouteId, RelativePath)
            );

            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT NOT NULL PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """);

        await EnsureDriveRouteColumnsAsync(connection);
        await EnsureSyncJobColumnsAsync(connection);

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
            ThemeMode = GetEnum(values, nameof(AppSettings.ThemeMode), AppThemeMode.System),
            AccentColor = Get(values, nameof(AppSettings.AccentColor), "#F97316"),
            HighContrastEnabled = GetBool(values, nameof(AppSettings.HighContrastEnabled), false),
            LanguageCode = Get(values, nameof(AppSettings.LanguageCode), AppText.PortugueseLanguageCode),
            SetupWizardCompleted = GetBool(values, nameof(AppSettings.SetupWizardCompleted), false),
            SetupWizardCompletedVersion = GetInt(values, nameof(AppSettings.SetupWizardCompletedVersion), 0),
            NotificationsEnabled = GetBool(values, nameof(AppSettings.NotificationsEnabled), true),
            NotifyUploadCompleted = GetBool(values, nameof(AppSettings.NotifyUploadCompleted), true),
            NotifyUploadFailed = GetBool(values, nameof(AppSettings.NotifyUploadFailed), true),
            NotifyConflict = GetBool(values, nameof(AppSettings.NotifyConflict), true),
            NotifySessionExpired = GetBool(values, nameof(AppSettings.NotifySessionExpired), true),
            NotifyDriveDisconnected = GetBool(values, nameof(AppSettings.NotifyDriveDisconnected), true),
            NotifyUpdateReady = GetBool(values, nameof(AppSettings.NotifyUpdateReady), true),
            QuietModeEnabled = GetBool(values, nameof(AppSettings.QuietModeEnabled), false),
            OfflineCacheLimitMb = GetInt(values, nameof(AppSettings.OfflineCacheLimitMb), 2048),
            OfflinePauseOnMeteredNetwork = GetBool(values, nameof(AppSettings.OfflinePauseOnMeteredNetwork), true),
            OfflinePauseOnBattery = GetBool(values, nameof(AppSettings.OfflinePauseOnBattery), true)
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
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.ThemeMode), settings.ThemeMode.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.AccentColor), NormalizeAccentColor(settings.AccentColor));
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.HighContrastEnabled), settings.HighContrastEnabled.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.LanguageCode), AppText.NormalizeLanguageCode(settings.LanguageCode));
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.SetupWizardCompleted), settings.SetupWizardCompleted.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.SetupWizardCompletedVersion), Math.Max(0, settings.SetupWizardCompletedVersion).ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.NotificationsEnabled), settings.NotificationsEnabled.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.NotifyUploadCompleted), settings.NotifyUploadCompleted.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.NotifyUploadFailed), settings.NotifyUploadFailed.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.NotifyConflict), settings.NotifyConflict.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.NotifySessionExpired), settings.NotifySessionExpired.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.NotifyDriveDisconnected), settings.NotifyDriveDisconnected.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.NotifyUpdateReady), settings.NotifyUpdateReady.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.QuietModeEnabled), settings.QuietModeEnabled.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.OfflineCacheLimitMb), Math.Clamp(settings.OfflineCacheLimitMb, 128, 102400).ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.OfflinePauseOnMeteredNetwork), settings.OfflinePauseOnMeteredNetwork.ToString());
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.OfflinePauseOnBattery), settings.OfflinePauseOnBattery.ToString());
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
        await ExecuteAsync(connection, transaction, "DELETE FROM DirectoryCache;");
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
            SELECT Id, DisplayName, SharePointUrl, RemotePath, IsConnected, StatusText, LastCheckedAt,
                   SiteId, DriveId, RootItemId, FolderWebUrl
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
                LastCheckedAt = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
                SiteId = GetNullableString(reader, 7),
                DriveId = GetNullableString(reader, 8),
                RootItemId = GetNullableString(reader, 9),
                FolderWebUrl = GetNullableString(reader, 10)
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
            INSERT INTO DriveRoutes
                (Id, DisplayName, SharePointUrl, RemotePath, IsConnected, StatusText, LastCheckedAt,
                 SiteId, DriveId, RootItemId, FolderWebUrl)
            VALUES
                ($id, $displayName, $sharePointUrl, $remotePath, $isConnected, $statusText, $lastCheckedAt,
                 $siteId, $driveId, $rootItemId, $folderWebUrl);
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
                LastCheckedAt = $lastCheckedAt,
                SiteId = $siteId,
                DriveId = $driveId,
                RootItemId = $rootItemId,
                FolderWebUrl = $folderWebUrl
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
            SELECT Id, RouteId, FileName, RouteDisplayName, RelativePath, PayloadPath,
                   ExpectedModifiedAt, State, Progress, UpdatedAt, Attempts, LastError, NextAttemptAt
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
                RouteId = ParseGuid(reader, 1),
                FileName = reader.GetString(2),
                RouteDisplayName = reader.GetString(3),
                RelativePath = GetNullableString(reader, 4),
                PayloadPath = GetNullableString(reader, 5),
                ExpectedModifiedAt = ParseDateTimeOffset(reader, 6),
                State = (SyncJobState)reader.GetInt32(7),
                Progress = reader.GetInt32(8),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(9)),
                Attempts = reader.GetInt32(10),
                LastError = GetNullableString(reader, 11),
                NextAttemptAt = ParseDateTimeOffset(reader, 12)
            });
        }

        return jobs;
    }

    public async Task AddSyncJobAsync(SyncJob job)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO SyncJobs
                (Id, RouteId, FileName, RouteDisplayName, RelativePath, PayloadPath,
                 ExpectedModifiedAt, State, Progress, UpdatedAt, Attempts, LastError, NextAttemptAt)
            VALUES
                ($id, $routeId, $fileName, $routeDisplayName, $relativePath, $payloadPath,
                 $expectedModifiedAt, $state, $progress, $updatedAt, $attempts, $lastError, $nextAttemptAt);
            """;
        BindSyncJobParameters(command, job);
        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateSyncJobAsync(SyncJob job)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE SyncJobs
            SET RouteId = $routeId,
                FileName = $fileName,
                RouteDisplayName = $routeDisplayName,
                RelativePath = $relativePath,
                PayloadPath = $payloadPath,
                ExpectedModifiedAt = $expectedModifiedAt,
                State = $state,
                Progress = $progress,
                UpdatedAt = $updatedAt,
                Attempts = $attempts,
                LastError = $lastError,
                NextAttemptAt = $nextAttemptAt
            WHERE Id = $id;
            """;
        BindSyncJobParameters(command, job);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<SyncJob?> FindPendingSyncJobAsync(Guid routeId, string relativePath)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, RouteId, FileName, RouteDisplayName, RelativePath, PayloadPath,
                   ExpectedModifiedAt, State, Progress, UpdatedAt, Attempts, LastError, NextAttemptAt
            FROM SyncJobs
            WHERE RouteId = $routeId
              AND RelativePath = $relativePath
              AND State IN ($waiting, $uploading, $failed, $conflict)
            ORDER BY UpdatedAt DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$routeId", routeId.ToString());
        command.Parameters.AddWithValue("$relativePath", relativePath);
        command.Parameters.AddWithValue("$waiting", (int)SyncJobState.Waiting);
        command.Parameters.AddWithValue("$uploading", (int)SyncJobState.Uploading);
        command.Parameters.AddWithValue("$failed", (int)SyncJobState.Failed);
        command.Parameters.AddWithValue("$conflict", (int)SyncJobState.Conflict);

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadSyncJob(reader) : null;
    }

    public async Task<IReadOnlyList<SyncJob>> GetPendingSyncJobsAsync()
    {
        var jobs = await GetSyncJobsAsync();
        return jobs
            .Where(job => job.State is SyncJobState.Waiting or SyncJobState.Uploading)
            .ToArray();
    }

    public DirectoryCacheSnapshot? TryGetDirectoryCache(Guid routeId, string relativePath, TimeSpan maxAge)
    {
        try
        {
            using var connection = CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT CachedAt, ItemsJson
                FROM DirectoryCache
                WHERE RouteId = $routeId AND RelativePath = $relativePath
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$routeId", routeId.ToString());
            command.Parameters.AddWithValue("$relativePath", relativePath);

            using var reader = command.ExecuteReader();
            if (!reader.Read() || !DateTimeOffset.TryParse(reader.GetString(0), out var cachedAt))
            {
                return null;
            }

            var items = JsonSerializer.Deserialize<SharePointDriveItem[]>(reader.GetString(1));
            return items is null || DateTimeOffset.UtcNow - cachedAt > maxAge
                ? null
                : new DirectoryCacheSnapshot(cachedAt, items);
        }
        catch
        {
            return null;
        }
    }

    public void SaveDirectoryCache(Guid routeId, string relativePath, IReadOnlyList<SharePointDriveItem> items)
    {
        try
        {
            using var connection = CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO DirectoryCache (RouteId, RelativePath, CachedAt, ItemsJson)
                VALUES ($routeId, $relativePath, $cachedAt, $itemsJson)
                ON CONFLICT(RouteId, RelativePath) DO UPDATE SET
                    CachedAt = excluded.CachedAt,
                    ItemsJson = excluded.ItemsJson;
                """;
            command.Parameters.AddWithValue("$routeId", routeId.ToString());
            command.Parameters.AddWithValue("$relativePath", relativePath);
            command.Parameters.AddWithValue("$cachedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$itemsJson", JsonSerializer.Serialize(items));
            command.ExecuteNonQuery();
        }
        catch
        {
            // A cache write must never make Explorer operations fail.
        }
    }

    public void ClearDirectoryCache()
    {
        try
        {
            using var connection = CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM DirectoryCache;";
            command.ExecuteNonQuery();
        }
        catch
        {
            // Cache cleanup is best effort; session cleanup still clears in-memory data.
        }
    }

    public void InvalidateDirectoryCache(Guid routeId, string relativePath)
    {
        try
        {
            using var connection = CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM DirectoryCache WHERE RouteId = $routeId AND RelativePath = $relativePath;";
            command.Parameters.AddWithValue("$routeId", routeId.ToString());
            command.Parameters.AddWithValue("$relativePath", relativePath);
            command.ExecuteNonQuery();
        }
        catch
        {
            // Cache invalidation is best effort.
        }
    }

    public void InvalidateRouteDirectoryCache(Guid routeId)
    {
        try
        {
            using var connection = CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM DirectoryCache WHERE RouteId = $routeId;";
            command.Parameters.AddWithValue("$routeId", routeId.ToString());
            command.ExecuteNonQuery();
        }
        catch
        {
            // Cache invalidation is best effort.
        }
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static SyncJob ReadSyncJob(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        RouteId = ParseGuid(reader, 1),
        FileName = reader.GetString(2),
        RouteDisplayName = reader.GetString(3),
        RelativePath = GetNullableString(reader, 4),
        PayloadPath = GetNullableString(reader, 5),
        ExpectedModifiedAt = ParseDateTimeOffset(reader, 6),
        State = (SyncJobState)reader.GetInt32(7),
        Progress = reader.GetInt32(8),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(9)),
        Attempts = reader.GetInt32(10),
        LastError = GetNullableString(reader, 11),
        NextAttemptAt = ParseDateTimeOffset(reader, 12)
    };

    private static void BindSyncJobParameters(SqliteCommand command, SyncJob job)
    {
        command.Parameters.AddWithValue("$id", job.Id.ToString());
        command.Parameters.AddWithValue("$routeId", job.RouteId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$fileName", job.FileName);
        command.Parameters.AddWithValue("$routeDisplayName", job.RouteDisplayName);
        command.Parameters.AddWithValue("$relativePath", job.RelativePath ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$payloadPath", job.PayloadPath ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$expectedModifiedAt", job.ExpectedModifiedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$state", (int)job.State);
        command.Parameters.AddWithValue("$progress", Math.Clamp(job.Progress, 0, 100));
        command.Parameters.AddWithValue("$updatedAt", job.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$attempts", Math.Max(0, job.Attempts));
        command.Parameters.AddWithValue("$lastError", string.IsNullOrWhiteSpace(job.LastError) ? (object)DBNull.Value : job.LastError);
        command.Parameters.AddWithValue("$nextAttemptAt", job.NextAttemptAt?.ToString("O") ?? (object)DBNull.Value);
    }

    private static async Task EnsureDriveRouteColumnsAsync(SqliteConnection connection)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SiteId"] = "TEXT NULL",
            ["DriveId"] = "TEXT NULL",
            ["RootItemId"] = "TEXT NULL",
            ["FolderWebUrl"] = "TEXT NULL"
        };

        foreach (var column in columns)
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT 1 FROM pragma_table_info('DriveRoutes') WHERE name = $name LIMIT 1;";
            check.Parameters.AddWithValue("$name", column.Key);
            if (await check.ExecuteScalarAsync() is not null)
            {
                continue;
            }

            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE DriveRoutes ADD COLUMN {column.Key} {column.Value};";
            await alter.ExecuteNonQueryAsync();
        }
    }

    private static async Task EnsureSyncJobColumnsAsync(SqliteConnection connection)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RouteId"] = "TEXT NULL",
            ["RelativePath"] = "TEXT NULL",
            ["PayloadPath"] = "TEXT NULL",
            ["ExpectedModifiedAt"] = "TEXT NULL",
            ["Attempts"] = "INTEGER NOT NULL DEFAULT 0",
            ["LastError"] = "TEXT NULL",
            ["NextAttemptAt"] = "TEXT NULL"
        };

        foreach (var column in columns)
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT 1 FROM pragma_table_info('SyncJobs') WHERE name = $name LIMIT 1;";
            check.Parameters.AddWithValue("$name", column.Key);
            if (await check.ExecuteScalarAsync() is not null)
            {
                continue;
            }

            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE SyncJobs ADD COLUMN {column.Key} {column.Value};";
            await alter.ExecuteNonQueryAsync();
        }
    }

    private static Guid? ParseGuid(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) || !Guid.TryParse(reader.GetString(ordinal), out var value)
            ? null
            : value;

    private static DateTimeOffset? ParseDateTimeOffset(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) || !DateTimeOffset.TryParse(reader.GetString(ordinal), out var value)
            ? null
            : value;

    private static string GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);

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
        command.Parameters.AddWithValue("$siteId", ToNullableDatabaseValue(route.SiteId));
        command.Parameters.AddWithValue("$driveId", ToNullableDatabaseValue(route.DriveId));
        command.Parameters.AddWithValue("$rootItemId", ToNullableDatabaseValue(route.RootItemId));
        command.Parameters.AddWithValue("$folderWebUrl", ToNullableDatabaseValue(route.FolderWebUrl));
    }

    private static object ToNullableDatabaseValue(string value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

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

    private static string NormalizeAccentColor(string accentColor)
    {
        var normalized = accentColor?.Trim() ?? string.Empty;
        return normalized.Length == 7 &&
               normalized[0] == '#' &&
               normalized[1..].All(Uri.IsHexDigit)
            ? normalized.ToUpperInvariant()
            : "#F97316";
    }

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

public sealed record DirectoryCacheSnapshot(
    DateTimeOffset CachedAt,
    IReadOnlyList<SharePointDriveItem> Items);
