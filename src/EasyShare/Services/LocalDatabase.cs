using EasyShare.Models;
using EasyShare.Resources;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace EasyShare.Services;

public sealed class LocalDatabase
{
    private const string StartMinimizedDefaultMigrationKey = "Migration.StartMinimizedDefaultFalse";
    private const string SyncJobLegacyErrorsSanitizedMigrationKey =
        "Migration.SyncJobLegacyErrorsSanitized";
    private const string SyncJobSelectColumns =
        """
        Id, RouteId, OperationKey, FileName, RouteDisplayName, RelativePath, PayloadPath,
        PayloadLength, PayloadSha256, ExpectedModifiedAt, State, Progress, BytesTransferred,
        UploadMayHaveCommitted, IsProgressIndeterminate, BytesPerSecond, EstimatedCompletionAt,
        CreatedAt, StoredAt, UploadStartedAt, CompletedAt, UpdatedAt, Attempts, LastError,
        FailureKind, WaitReason, UserMessage, TechnicalDetails, NextAttemptAt, RemoteConfirmedAt,
        RemoteItemId, RemoteETag, RemoteLocator, RemoteLength, RemoteModifiedAt,
        OperationKind, IsDirectory, DeleteBarrierObservedAt, DeleteArmed
        """;
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
                OperationKey TEXT NULL,
                FileName TEXT NOT NULL,
                RouteDisplayName TEXT NOT NULL,
                RelativePath TEXT NULL,
                PayloadPath TEXT NULL,
                PayloadLength INTEGER NULL,
                PayloadSha256 TEXT NULL,
                ExpectedModifiedAt TEXT NULL,
                State INTEGER NOT NULL,
                Progress INTEGER NOT NULL,
                BytesTransferred INTEGER NOT NULL DEFAULT 0,
                UploadMayHaveCommitted INTEGER NOT NULL DEFAULT 0,
                IsProgressIndeterminate INTEGER NOT NULL DEFAULT 1,
                BytesPerSecond REAL NULL,
                EstimatedCompletionAt TEXT NULL,
                CreatedAt TEXT NULL,
                StoredAt TEXT NULL,
                UploadStartedAt TEXT NULL,
                CompletedAt TEXT NULL,
                UpdatedAt TEXT NOT NULL,
                Attempts INTEGER NOT NULL DEFAULT 0,
                LastError TEXT NULL,
                FailureKind INTEGER NOT NULL DEFAULT 0,
                WaitReason INTEGER NOT NULL DEFAULT 0,
                UserMessage TEXT NULL,
                TechnicalDetails TEXT NULL,
                NextAttemptAt TEXT NULL,
                RemoteConfirmedAt TEXT NULL,
                RemoteItemId TEXT NULL,
                RemoteETag TEXT NULL,
                RemoteLocator TEXT NULL,
                RemoteLength INTEGER NULL,
                RemoteModifiedAt TEXT NULL,
                OperationKind INTEGER NOT NULL DEFAULT 0,
                IsDirectory INTEGER NOT NULL DEFAULT 0,
                DeleteBarrierObservedAt TEXT NULL,
                DeleteArmed INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS DirectoryCache (
                AccountScope TEXT NOT NULL,
                RouteId TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                CachedAt TEXT NOT NULL,
                ItemsJson TEXT NOT NULL,
                PRIMARY KEY (AccountScope, RouteId, RelativePath)
            );

            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT NOT NULL PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """);

        await EnsureDriveRouteColumnsAsync(connection);
        await EnsureSyncJobColumnsAsync(connection);
        await EnsureDirectoryCacheScopeAsync(connection);

        await MigrateStartMinimizedDefaultAsync(connection);
        await PurgeExpiredTerminalSyncJobsAsync(connection, DateTimeOffset.UtcNow.AddDays(-7));
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
            CloseBehavior = GetEnum(values, nameof(AppSettings.CloseBehavior), AppCloseBehavior.Ask),
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
        await SaveSettingAsync(connection, transaction, nameof(AppSettings.CloseBehavior), settings.CloseBehavior.ToString());
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
        await ExecuteAsync(connection, "PRAGMA secure_delete = ON;");
        await using var transaction = await connection.BeginTransactionAsync();

        await ExecuteAsync(connection, transaction, "DELETE FROM DriveRoutes;");
        await ExecuteAsync(connection, transaction, "DELETE FROM SyncJobs;");
        await ExecuteAsync(connection, transaction, "DELETE FROM DirectoryCache;");
        await ExecuteAsync(connection, transaction, "DELETE FROM Settings;");

        await transaction.CommitAsync();
        await ExecuteAsync(connection, "VACUUM;");
    }

    public void ReleasePooledConnectionsForLocalDataReset() =>
        SqliteConnection.ClearAllPools();

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
            $"SELECT {SyncJobSelectColumns} FROM SyncJobs ORDER BY UpdatedAt DESC;";

        var jobs = new List<SyncJob>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            jobs.Add(ReadSyncJob(reader));
        }

        return jobs;
    }

    public async Task<SyncJob?> GetSyncJobAsync(Guid jobId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {SyncJobSelectColumns} FROM SyncJobs WHERE Id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", jobId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadSyncJob(reader) : null;
    }

    public async Task AddSyncJobAsync(SyncJob job)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO SyncJobs
                (Id, RouteId, OperationKey, FileName, RouteDisplayName, RelativePath, PayloadPath,
                 PayloadLength, PayloadSha256, ExpectedModifiedAt, State, Progress, BytesTransferred,
                 UploadMayHaveCommitted, IsProgressIndeterminate, BytesPerSecond, EstimatedCompletionAt,
                 CreatedAt, StoredAt, UploadStartedAt, CompletedAt, UpdatedAt, Attempts, LastError,
                 FailureKind, WaitReason, UserMessage, TechnicalDetails, NextAttemptAt, RemoteConfirmedAt,
                 RemoteItemId, RemoteETag, RemoteLocator, RemoteLength, RemoteModifiedAt,
                 OperationKind, IsDirectory, DeleteBarrierObservedAt, DeleteArmed)
            VALUES
                ($id, $routeId, $operationKey, $fileName, $routeDisplayName, $relativePath, $payloadPath,
                 $payloadLength, $payloadSha256, $expectedModifiedAt, $state, $progress, $bytesTransferred,
                 $uploadMayHaveCommitted, $isProgressIndeterminate, $bytesPerSecond, $estimatedCompletionAt,
                 $createdAt, $storedAt, $uploadStartedAt, $completedAt, $updatedAt, $attempts, $lastError,
                 $failureKind, $waitReason, $userMessage, $technicalDetails, $nextAttemptAt,
                  $remoteConfirmedAt, $remoteItemId, $remoteETag, $remoteLocator, $remoteLength,
                  $remoteModifiedAt, $operationKind, $isDirectory, $deleteBarrierObservedAt,
                  $deleteArmed);
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
                OperationKey = $operationKey,
                FileName = $fileName,
                RouteDisplayName = $routeDisplayName,
                RelativePath = $relativePath,
                PayloadPath = $payloadPath,
                PayloadLength = $payloadLength,
                PayloadSha256 = $payloadSha256,
                ExpectedModifiedAt = $expectedModifiedAt,
                State = $state,
                Progress = $progress,
                BytesTransferred = $bytesTransferred,
                UploadMayHaveCommitted = $uploadMayHaveCommitted,
                IsProgressIndeterminate = $isProgressIndeterminate,
                BytesPerSecond = $bytesPerSecond,
                EstimatedCompletionAt = $estimatedCompletionAt,
                CreatedAt = $createdAt,
                StoredAt = $storedAt,
                UploadStartedAt = $uploadStartedAt,
                CompletedAt = $completedAt,
                UpdatedAt = $updatedAt,
                Attempts = $attempts,
                LastError = $lastError,
                FailureKind = $failureKind,
                WaitReason = $waitReason,
                UserMessage = $userMessage,
                TechnicalDetails = $technicalDetails,
                NextAttemptAt = $nextAttemptAt,
                RemoteConfirmedAt = $remoteConfirmedAt,
                RemoteItemId = $remoteItemId,
                RemoteETag = $remoteETag,
                RemoteLocator = $remoteLocator,
                RemoteLength = $remoteLength,
                RemoteModifiedAt = $remoteModifiedAt,
                OperationKind = $operationKind,
                IsDirectory = $isDirectory,
                DeleteBarrierObservedAt = $deleteBarrierObservedAt,
                DeleteArmed = $deleteArmed
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
            $"""
            SELECT {SyncJobSelectColumns}
            FROM SyncJobs
            WHERE RouteId = $routeId
              AND RelativePath = $relativePath
              AND State NOT IN ($completed, $discarded)
            ORDER BY UpdatedAt DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$routeId", routeId.ToString());
        command.Parameters.AddWithValue("$relativePath", relativePath);
        command.Parameters.AddWithValue("$completed", (int)SyncJobState.Completed);
        command.Parameters.AddWithValue("$discarded", (int)SyncJobState.Discarded);

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadSyncJob(reader) : null;
    }

    public async Task<SyncJob?> FindActiveSyncJobByOperationKeyAsync(string operationKey)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {SyncJobSelectColumns}
            FROM SyncJobs
            WHERE OperationKey = $operationKey
              AND State NOT IN ($completed, $discarded)
            ORDER BY UpdatedAt DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$operationKey", operationKey);
        command.Parameters.AddWithValue("$completed", (int)SyncJobState.Completed);
        command.Parameters.AddWithValue("$discarded", (int)SyncJobState.Discarded);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadSyncJob(reader) : null;
    }

    public async Task<IReadOnlyList<SyncJob>> GetPendingSyncJobsAsync()
    {
        var jobs = await GetSyncJobsAsync();
        return jobs
            .Where(job => job.State is
                SyncJobState.PersistingLocal or
                SyncJobState.StoredLocally or
                SyncJobState.Waiting or
                SyncJobState.Uploading or
                SyncJobState.VerifyingRemote)
            .ToArray();
    }

    public async Task<IReadOnlyList<SyncJob>> GetActiveSyncJobsAsync(Guid? routeId = null)
    {
        var jobs = await GetSyncJobsAsync();
        return jobs
            .Where(job => job.IsActive && (routeId is null || job.RouteId == routeId))
            .ToArray();
    }

    public async Task<bool> DeleteSyncJobAsync(Guid jobId, SyncJobState requiredState)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SyncJobs WHERE Id = $id AND State = $state;";
        command.Parameters.AddWithValue("$id", jobId.ToString());
        command.Parameters.AddWithValue("$state", (int)requiredState);
        return await command.ExecuteNonQueryAsync() == 1;
    }

    public async Task<int> DeleteTerminalSyncJobsOlderThanAsync(DateTimeOffset cutoff)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM SyncJobs
            WHERE State IN ($completed, $discarded)
              AND COALESCE(CompletedAt, UpdatedAt) <= $cutoff;
            """;
        command.Parameters.AddWithValue("$completed", (int)SyncJobState.Completed);
        command.Parameters.AddWithValue("$discarded", (int)SyncJobState.Discarded);
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        return await command.ExecuteNonQueryAsync();
    }

    public DirectoryCacheSnapshot? TryGetDirectoryCache(
        string accountScope,
        Guid routeId,
        string relativePath,
        TimeSpan maxAge)
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
                WHERE AccountScope = $accountScope
                  AND RouteId = $routeId
                  AND RelativePath = $relativePath
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$accountScope", NormalizeCacheScope(accountScope));
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

    public void SaveDirectoryCache(
        string accountScope,
        Guid routeId,
        string relativePath,
        IReadOnlyList<SharePointDriveItem> items)
    {
        try
        {
            using var connection = CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO DirectoryCache (AccountScope, RouteId, RelativePath, CachedAt, ItemsJson)
                VALUES ($accountScope, $routeId, $relativePath, $cachedAt, $itemsJson)
                ON CONFLICT(AccountScope, RouteId, RelativePath) DO UPDATE SET
                    CachedAt = excluded.CachedAt,
                    ItemsJson = excluded.ItemsJson;
                """;
            command.Parameters.AddWithValue("$accountScope", NormalizeCacheScope(accountScope));
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

    public void ClearDirectoryCache(string? accountScope = null)
    {
        try
        {
            using var connection = CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = string.IsNullOrWhiteSpace(accountScope)
                ? "DELETE FROM DirectoryCache;"
                : "DELETE FROM DirectoryCache WHERE AccountScope = $accountScope;";
            if (!string.IsNullOrWhiteSpace(accountScope))
            {
                command.Parameters.AddWithValue("$accountScope", NormalizeCacheScope(accountScope));
            }
            command.ExecuteNonQuery();
        }
        catch
        {
            // Cache cleanup is best effort; session cleanup still clears in-memory data.
        }
    }

    public void InvalidateDirectoryCache(string accountScope, Guid routeId, string relativePath)
    {
        try
        {
            using var connection = CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM DirectoryCache
                WHERE AccountScope = $accountScope AND RouteId = $routeId AND RelativePath = $relativePath;
                """;
            command.Parameters.AddWithValue("$accountScope", NormalizeCacheScope(accountScope));
            command.Parameters.AddWithValue("$routeId", routeId.ToString());
            command.Parameters.AddWithValue("$relativePath", relativePath);
            command.ExecuteNonQuery();
        }
        catch
        {
            // Cache invalidation is best effort.
        }
    }

    public void InvalidateDeletePathDirectoryCache(
        Guid routeId,
        string relativePath,
        bool isDirectory)
    {
        if (routeId == Guid.Empty || string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        try
        {
            var normalized = relativePath
                .Replace('\\', '/')
                .Trim('/');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            var separator = normalized.LastIndexOf('/');
            var parent = separator >= 0 ? normalized[..separator] : string.Empty;
            var descendantPrefix = normalized + "/";

            using var connection = CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM DirectoryCache
                WHERE RouteId = $routeId
                  AND (
                      RelativePath COLLATE NOCASE = $relativePath
                      OR RelativePath COLLATE NOCASE = $parentPath
                      OR (
                          $isDirectory = 1
                          AND substr(RelativePath, 1, length($descendantPrefix)) COLLATE NOCASE =
                              $descendantPrefix
                      )
                  );
                """;
            command.Parameters.AddWithValue("$routeId", routeId.ToString());
            command.Parameters.AddWithValue("$relativePath", normalized);
            command.Parameters.AddWithValue("$parentPath", parent);
            command.Parameters.AddWithValue("$descendantPrefix", descendantPrefix);
            command.Parameters.AddWithValue("$isDirectory", isDirectory ? 1 : 0);
            command.ExecuteNonQuery();
        }
        catch
        {
            // Cache invalidation is best effort.
        }
    }

    public void InvalidateRouteDirectoryCache(string accountScope, Guid routeId)
    {
        try
        {
            using var connection = CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM DirectoryCache WHERE AccountScope = $accountScope AND RouteId = $routeId;";
            command.Parameters.AddWithValue("$accountScope", NormalizeCacheScope(accountScope));
            command.Parameters.AddWithValue("$routeId", routeId.ToString());
            command.ExecuteNonQuery();
        }
        catch
        {
            // Cache invalidation is best effort.
        }
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static SyncJob ReadSyncJob(SqliteDataReader reader)
    {
        var routeId = ParseGuid(reader, 1);
        var relativePath = GetNullableString(reader, 5);
        var updatedAt = DateTimeOffset.Parse(reader.GetString(21));
        var persistedLastError = GetNullableString(reader, 23);
        var failureKind = (SyncFailureKind)reader.GetInt32(24);
        var persistedUserMessage = GetNullableString(reader, 26);
        var userMessage = !string.IsNullOrWhiteSpace(persistedUserMessage)
            ? persistedUserMessage
            : !string.IsNullOrWhiteSpace(persistedLastError)
                ? FriendlyLegacyFailure(
                    failureKind == SyncFailureKind.None
                        ? SyncFailureKind.Unknown
                        : failureKind)
                : string.Empty;
        var operationKey = GetNullableString(reader, 2);
        if (string.IsNullOrWhiteSpace(operationKey) &&
            routeId is { } resolvedRouteId &&
            !string.IsNullOrWhiteSpace(relativePath))
        {
            operationKey = SyncJob.CreateOperationKey(resolvedRouteId, relativePath);
        }

        return new SyncJob
        {
            Id = Guid.Parse(reader.GetString(0)),
            RouteId = routeId,
            OperationKey = operationKey,
            FileName = reader.GetString(3),
            RouteDisplayName = reader.GetString(4),
            RelativePath = relativePath,
            PayloadPath = GetNullableString(reader, 6),
            PayloadLength = GetNullableInt64(reader, 7),
            PayloadSha256 = GetNullableString(reader, 8),
            ExpectedModifiedAt = ParseDateTimeOffset(reader, 9),
            State = (SyncJobState)reader.GetInt32(10),
            Progress = reader.GetInt32(11),
            BytesTransferred = reader.GetInt64(12),
            UploadMayHaveCommitted = reader.GetInt32(13) != 0,
            IsProgressIndeterminate = reader.GetInt32(14) != 0,
            BytesPerSecond = GetNullableDouble(reader, 15),
            EstimatedCompletionAt = ParseDateTimeOffset(reader, 16),
            CreatedAt = ParseDateTimeOffset(reader, 17) ?? updatedAt,
            StoredAt = ParseDateTimeOffset(reader, 18),
            UploadStartedAt = ParseDateTimeOffset(reader, 19),
            CompletedAt = ParseDateTimeOffset(reader, 20),
            UpdatedAt = updatedAt,
            Attempts = reader.GetInt32(22),
            LastError = userMessage,
            FailureKind = failureKind,
            WaitReason = (SyncWaitReason)reader.GetInt32(25),
            UserMessage = userMessage,
            TechnicalDetails = GetNullableString(reader, 27),
            NextAttemptAt = ParseDateTimeOffset(reader, 28),
            RemoteConfirmedAt = ParseDateTimeOffset(reader, 29),
            RemoteItemId = GetNullableString(reader, 30),
            RemoteETag = GetNullableString(reader, 31),
            RemoteLocator = GetNullableString(reader, 32),
            RemoteLength = GetNullableInt64(reader, 33),
            RemoteModifiedAt = ParseDateTimeOffset(reader, 34),
            OperationKind = (SyncOperationKind)reader.GetInt32(35),
            IsDirectory = reader.GetInt32(36) != 0,
            DeleteBarrierObservedAt = ParseDateTimeOffset(reader, 37),
            DeleteArmed = reader.GetInt32(38) != 0
        };
    }

    private static void BindSyncJobParameters(SqliteCommand command, SyncJob job)
    {
        if (string.IsNullOrWhiteSpace(job.OperationKey) &&
            job.RouteId is { } routeId &&
            !string.IsNullOrWhiteSpace(job.RelativePath))
        {
            job.OperationKey = SyncJob.CreateOperationKey(routeId, job.RelativePath);
        }

        command.Parameters.AddWithValue("$id", job.Id.ToString());
        command.Parameters.AddWithValue("$routeId", job.RouteId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$operationKey", EmptyToDbNull(job.OperationKey));
        command.Parameters.AddWithValue("$fileName", job.FileName);
        command.Parameters.AddWithValue("$routeDisplayName", job.RouteDisplayName);
        command.Parameters.AddWithValue("$relativePath", job.RelativePath ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$payloadPath", job.PayloadPath ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$payloadLength", job.PayloadLength ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$payloadSha256", EmptyToDbNull(job.PayloadSha256));
        command.Parameters.AddWithValue("$expectedModifiedAt", job.ExpectedModifiedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$state", (int)job.State);
        command.Parameters.AddWithValue("$progress", Math.Clamp(job.Progress, 0, 100));
        command.Parameters.AddWithValue("$bytesTransferred", Math.Max(0, job.BytesTransferred));
        command.Parameters.AddWithValue("$uploadMayHaveCommitted", job.UploadMayHaveCommitted ? 1 : 0);
        command.Parameters.AddWithValue("$isProgressIndeterminate", job.IsProgressIndeterminate ? 1 : 0);
        command.Parameters.AddWithValue("$bytesPerSecond", job.BytesPerSecond is > 0 ? job.BytesPerSecond.Value : (object)DBNull.Value);
        command.Parameters.AddWithValue("$estimatedCompletionAt", job.EstimatedCompletionAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$storedAt", job.StoredAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$uploadStartedAt", job.UploadStartedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", job.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", job.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$attempts", Math.Max(0, job.Attempts));
        command.Parameters.AddWithValue("$lastError", EmptyToDbNull(job.LastError));
        command.Parameters.AddWithValue("$failureKind", (int)job.FailureKind);
        command.Parameters.AddWithValue("$waitReason", (int)job.WaitReason);
        command.Parameters.AddWithValue("$userMessage", EmptyToDbNull(job.UserMessage));
        command.Parameters.AddWithValue("$technicalDetails", EmptyToDbNull(job.TechnicalDetails));
        command.Parameters.AddWithValue("$nextAttemptAt", job.NextAttemptAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$remoteConfirmedAt", job.RemoteConfirmedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$remoteItemId", EmptyToDbNull(job.RemoteItemId));
        command.Parameters.AddWithValue("$remoteETag", EmptyToDbNull(job.RemoteETag));
        command.Parameters.AddWithValue("$remoteLocator", EmptyToDbNull(job.RemoteLocator));
        command.Parameters.AddWithValue("$remoteLength", job.RemoteLength ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$remoteModifiedAt", job.RemoteModifiedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$operationKind", (int)job.OperationKind);
        command.Parameters.AddWithValue("$isDirectory", job.IsDirectory ? 1 : 0);
        command.Parameters.AddWithValue(
            "$deleteBarrierObservedAt",
            job.DeleteBarrierObservedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$deleteArmed", job.DeleteArmed ? 1 : 0);
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
            ["OperationKey"] = "TEXT NULL",
            ["RelativePath"] = "TEXT NULL",
            ["PayloadPath"] = "TEXT NULL",
            ["PayloadLength"] = "INTEGER NULL",
            ["PayloadSha256"] = "TEXT NULL",
            ["ExpectedModifiedAt"] = "TEXT NULL",
            ["BytesTransferred"] = "INTEGER NOT NULL DEFAULT 0",
            ["UploadMayHaveCommitted"] = "INTEGER NOT NULL DEFAULT 0",
            ["IsProgressIndeterminate"] = "INTEGER NOT NULL DEFAULT 1",
            ["BytesPerSecond"] = "REAL NULL",
            ["EstimatedCompletionAt"] = "TEXT NULL",
            ["CreatedAt"] = "TEXT NULL",
            ["StoredAt"] = "TEXT NULL",
            ["UploadStartedAt"] = "TEXT NULL",
            ["CompletedAt"] = "TEXT NULL",
            ["Attempts"] = "INTEGER NOT NULL DEFAULT 0",
            ["LastError"] = "TEXT NULL",
            ["FailureKind"] = "INTEGER NOT NULL DEFAULT 0",
            ["WaitReason"] = "INTEGER NOT NULL DEFAULT 0",
            ["UserMessage"] = "TEXT NULL",
            ["TechnicalDetails"] = "TEXT NULL",
            ["NextAttemptAt"] = "TEXT NULL",
            ["RemoteConfirmedAt"] = "TEXT NULL",
            ["RemoteItemId"] = "TEXT NULL",
            ["RemoteETag"] = "TEXT NULL",
            ["RemoteLocator"] = "TEXT NULL",
            ["RemoteLength"] = "INTEGER NULL",
            ["RemoteModifiedAt"] = "TEXT NULL",
            ["OperationKind"] = "INTEGER NOT NULL DEFAULT 0",
            ["IsDirectory"] = "INTEGER NOT NULL DEFAULT 0",
            ["DeleteBarrierObservedAt"] = "TEXT NULL",
            ["DeleteArmed"] = "INTEGER NOT NULL DEFAULT 1"
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

        await ExecuteAsync(
            connection,
            $"""
            UPDATE SyncJobs
            SET OperationKey =
                    lower(replace(RouteId, '-', '')) || ':' ||
                    upper(trim(replace(RelativePath, '\', '/'), '/'))
            WHERE (OperationKey IS NULL OR trim(OperationKey) = '')
              AND RouteId IS NOT NULL
              AND RelativePath IS NOT NULL;

            UPDATE SyncJobs
            SET CreatedAt = UpdatedAt
            WHERE CreatedAt IS NULL OR trim(CreatedAt) = '';

            UPDATE SyncJobs
            SET FailureKind = {(int)SyncFailureKind.Unknown}
            WHERE FailureKind = {(int)SyncFailureKind.None}
              AND LastError IS NOT NULL
              AND trim(LastError) <> '';

            UPDATE SyncJobs
            SET CompletedAt = UpdatedAt,
                IsProgressIndeterminate = 0
            WHERE State IN ({(int)SyncJobState.Completed}, {(int)SyncJobState.Discarded})
              AND (CompletedAt IS NULL OR trim(CompletedAt) = '');
            """);

        await FailUnsafeLegacyUploadingJobsAsync(connection);
        await SanitizeLegacySyncJobErrorsAsync(connection);
    }

    private static async Task FailUnsafeLegacyUploadingJobsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE SyncJobs
            SET State = $failed,
                Progress = 0,
                UploadMayHaveCommitted = 1,
                FailureKind = $unknown,
                WaitReason = $remoteVerification,
                UserMessage = $message,
                NextAttemptAt = NULL
            WHERE State = $uploading
              AND OperationKind = $upload
              AND (UploadStartedAt IS NULL OR trim(UploadStartedAt) = '');
            """;
        command.Parameters.AddWithValue("$failed", (int)SyncJobState.Failed);
        command.Parameters.AddWithValue("$unknown", (int)SyncFailureKind.Unknown);
        command.Parameters.AddWithValue(
            "$remoteVerification",
            (int)SyncWaitReason.RemoteVerification);
        command.Parameters.AddWithValue("$uploading", (int)SyncJobState.Uploading);
        command.Parameters.AddWithValue("$upload", (int)SyncOperationKind.Upload);
        command.Parameters.AddWithValue(
            "$message",
            AppText.Get("SyncFailureRemoteVerification"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SanitizeLegacySyncJobErrorsAsync(SqliteConnection connection)
    {
        if (await GetSettingValueAsync(connection, SyncJobLegacyErrorsSanitizedMigrationKey)
            .ConfigureAwait(false) is not null)
        {
            return;
        }

        var rows = new List<LegacySyncJobError>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText =
                """
                SELECT Id, LastError, FailureKind, UserMessage, TechnicalDetails
                FROM SyncJobs
                WHERE (LastError IS NOT NULL AND trim(LastError) <> '')
                   OR (UserMessage IS NOT NULL AND trim(UserMessage) <> '')
                   OR (TechnicalDetails IS NOT NULL AND trim(TechnicalDetails) <> '');
                """;
            await using var reader = await select.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new LegacySyncJobError(
                    reader.GetString(0),
                    GetNullableString(reader, 1),
                    (SyncFailureKind)reader.GetInt32(2),
                    GetNullableString(reader, 3),
                    GetNullableString(reader, 4)));
            }
        }

        var redactor = new SensitiveDataRedactor();
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
        foreach (var row in rows)
        {
            var failureKind = row.FailureKind == SyncFailureKind.None
                ? SyncFailureKind.Unknown
                : row.FailureKind;
            var userMessage = FriendlyLegacyFailure(failureKind);
            var diagnosticSource = !string.IsNullOrWhiteSpace(row.TechnicalDetails)
                ? row.TechnicalDetails
                : !string.IsNullOrWhiteSpace(row.LastError)
                    ? row.LastError
                    : row.UserMessage;
            var technicalDetails = redactor.Redact(diagnosticSource).Trim();
            if (technicalDetails.Length > 2048)
            {
                technicalDetails = technicalDetails[..2048];
            }

            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText =
                """
                UPDATE SyncJobs
                SET LastError = $userMessage,
                    UserMessage = $userMessage,
                    FailureKind = $failureKind,
                    TechnicalDetails = $technicalDetails
                WHERE Id = $id;
                """;
            update.Parameters.AddWithValue("$id", row.Id);
            update.Parameters.AddWithValue("$userMessage", userMessage);
            update.Parameters.AddWithValue("$failureKind", (int)failureKind);
            update.Parameters.AddWithValue(
                "$technicalDetails",
                string.IsNullOrWhiteSpace(technicalDetails) ? DBNull.Value : technicalDetails);
            await update.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await SaveSettingAsync(
                connection,
                transaction,
                SyncJobLegacyErrorsSanitizedMigrationKey,
                bool.TrueString)
            .ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static string FriendlyLegacyFailure(SyncFailureKind kind) => kind switch
    {
        SyncFailureKind.Network => AppText.Get("SyncFailureNetwork"),
        SyncFailureKind.Session => AppText.Get("SyncFailureSession"),
        SyncFailureKind.Permission => AppText.Get("SyncFailurePermission"),
        SyncFailureKind.Quota => AppText.Get("SyncFailureQuota"),
        SyncFailureKind.Conflict => AppText.Get("SyncFailureConflict"),
        SyncFailureKind.Integrity => AppText.Get("SyncFailureIntegrity"),
        SyncFailureKind.RouteUnavailable => AppText.Get("SyncFailureRouteUnavailable"),
        SyncFailureKind.PayloadUnavailable => AppText.Get("SyncFailurePayloadUnavailable"),
        SyncFailureKind.ServiceBusy => AppText.Get("SyncFailureServiceBusy"),
        _ => AppText.Get("SyncFailureUnknown")
    };

    private sealed record LegacySyncJobError(
        string Id,
        string LastError,
        SyncFailureKind FailureKind,
        string UserMessage,
        string TechnicalDetails);

    private static async Task EnsureDirectoryCacheScopeAsync(SqliteConnection connection)
    {
        await using var check = connection.CreateCommand();
        check.CommandText =
            "SELECT 1 FROM pragma_table_info('DirectoryCache') WHERE name = 'AccountScope' LIMIT 1;";
        if (await check.ExecuteScalarAsync() is not null)
        {
            return;
        }

        // Legacy cache entries cannot be attributed to an authenticated account.
        // Discarding this best-effort cache is safer than exposing names from a
        // previous WebView or Graph identity.
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "ALTER TABLE DirectoryCache RENAME TO DirectoryCacheLegacy;");
        await ExecuteAsync(
            connection,
            transaction,
            """
            CREATE TABLE DirectoryCache (
                AccountScope TEXT NOT NULL,
                RouteId TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                CachedAt TEXT NOT NULL,
                ItemsJson TEXT NOT NULL,
                PRIMARY KEY (AccountScope, RouteId, RelativePath)
            );
            """);
        await ExecuteAsync(connection, transaction, "DROP TABLE DirectoryCacheLegacy;");
        await transaction.CommitAsync();
    }

    private static async Task PurgeExpiredTerminalSyncJobsAsync(
        SqliteConnection connection,
        DateTimeOffset cutoff)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM SyncJobs
            WHERE State IN ($completed, $discarded)
              AND COALESCE(CompletedAt, UpdatedAt) <= $cutoff;
            """;
        command.Parameters.AddWithValue("$completed", (int)SyncJobState.Completed);
        command.Parameters.AddWithValue("$discarded", (int)SyncJobState.Discarded);
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        await command.ExecuteNonQueryAsync();
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

    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static double? GetNullableDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static object EmptyToDbNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

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

    private static string NormalizeCacheScope(string accountScope)
    {
        var normalized = accountScope?.Trim() ?? string.Empty;
        if (normalized.Length is < 8 or > 256 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("The cache account scope is invalid.", nameof(accountScope));
        }

        return normalized;
    }
}

public sealed record DirectoryCacheSnapshot(
    DateTimeOffset CachedAt,
    IReadOnlyList<SharePointDriveItem> Items);
