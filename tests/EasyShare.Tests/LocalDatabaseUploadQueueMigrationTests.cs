using EasyShare.Models;
using EasyShare.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EasyShare.Tests;

public sealed class LocalDatabaseUploadQueueMigrationTests
{
    [Fact]
    public async Task UpgradesLegacySyncJobsAndBackfillsDurableQueueFields()
    {
        using var directory = new MigrationDirectory();
        var paths = new AppDataPaths(directory.DataPath);
        Directory.CreateDirectory(paths.DataDirectory);
        var routeId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var payloadPath = Path.Combine(paths.UploadQueueDirectory, "legacy.payload");
        Directory.CreateDirectory(paths.UploadQueueDirectory);
        await File.WriteAllBytesAsync(payloadPath, "legacy protected copy"u8.ToArray());
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE SyncJobs (
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

                INSERT INTO SyncJobs
                    (Id, RouteId, FileName, RouteDisplayName, RelativePath, PayloadPath,
                     ExpectedModifiedAt, State, Progress, UpdatedAt, Attempts, LastError, NextAttemptAt)
                VALUES
                    ($id, $routeId, 'legacy.txt', 'Legado', 'Folder\legacy.txt', $payloadPath,
                     NULL, $state, 0, $updatedAt, 2, $legacyError, NULL);
                """;
            command.Parameters.AddWithValue("$id", jobId.ToString());
            command.Parameters.AddWithValue("$routeId", routeId.ToString());
            command.Parameters.AddWithValue("$payloadPath", payloadPath);
            command.Parameters.AddWithValue("$state", (int)SyncJobState.Uploading);
            command.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
            command.Parameters.AddWithValue(
                "$legacyError",
                "HTTP failed access_token=super-secret user=augustus@example.com&email=owner@contoso.com");
            await command.ExecuteNonQueryAsync();
        }

        var database = new LocalDatabase(paths);
        await database.InitializeAsync();
        var migrated = Assert.Single(await database.GetSyncJobsAsync());

        Assert.Equal(jobId, migrated.Id);
        Assert.Equal(SyncJob.CreateOperationKey(routeId, "Folder/legacy.txt"), migrated.OperationKey);
        Assert.Equal(updatedAt, migrated.CreatedAt);
        Assert.Equal("SyncFailureUnknown", migrated.UserMessage);
        Assert.Equal("SyncFailureUnknown", migrated.LastError);
        Assert.Equal(SyncFailureKind.Unknown, migrated.FailureKind);
        Assert.Equal(SyncJobState.Failed, migrated.State);
        Assert.True(migrated.UploadMayHaveCommitted);
        Assert.Equal(SyncWaitReason.RemoteVerification, migrated.WaitReason);
        Assert.False(migrated.CanRetry);
        Assert.True(migrated.CanExport);
        Assert.Contains("[REDACTED]", migrated.TechnicalDetails);
        Assert.DoesNotContain("super-secret", migrated.TechnicalDetails);
        Assert.DoesNotContain("augustus@example.com", migrated.TechnicalDetails);
        Assert.DoesNotContain("owner@contoso.com", migrated.TechnicalDetails);
        Assert.Equal(2, migrated.Attempts);

        await using var verify = new SqliteConnection($"Data Source={paths.DatabasePath}");
        await verify.OpenAsync();
        await using var columns = verify.CreateCommand();
        columns.CommandText = "SELECT name FROM pragma_table_info('SyncJobs');";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await columns.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        Assert.Contains("OperationKey", names);
        Assert.Contains("PayloadSha256", names);
        Assert.Contains("UploadMayHaveCommitted", names);
        Assert.Contains("RemoteConfirmedAt", names);
        Assert.Contains("TechnicalDetails", names);
        Assert.Contains("OperationKind", names);
        Assert.Contains("IsDirectory", names);
        Assert.Contains("DeleteBarrierObservedAt", names);
        Assert.Contains("DeleteArmed", names);
        Assert.Equal(SyncOperationKind.Upload, migrated.OperationKind);
        Assert.True(migrated.DeleteArmed);
    }

    [Fact]
    public void LegacyEnumValuesRemainStable()
    {
        Assert.Equal(0, (int)SyncJobState.Waiting);
        Assert.Equal(1, (int)SyncJobState.Uploading);
        Assert.Equal(2, (int)SyncJobState.Completed);
        Assert.Equal(3, (int)SyncJobState.Failed);
        Assert.Equal(4, (int)SyncJobState.Conflict);
        Assert.Equal(5, (int)SyncJobState.PersistingLocal);
        Assert.Equal(6, (int)SyncJobState.StoredLocally);
        Assert.Equal(7, (int)SyncJobState.VerifyingRemote);
        Assert.Equal(8, (int)SyncJobState.Discarded);
    }

    [Fact]
    public async Task InitializationDoesNotMisclassifyInterruptedDeleteAsLegacyUpload()
    {
        using var directory = new MigrationDirectory();
        var paths = new AppDataPaths(directory.DataPath);
        var database = new LocalDatabase(paths);
        await database.InitializeAsync();
        var routeId = Guid.NewGuid();
        var interruptedDelete = new SyncJob
        {
            RouteId = routeId,
            OperationKey = SyncJob.CreateOperationKey(routeId, "Folder/delete-me.txt"),
            OperationKind = SyncOperationKind.Delete,
            FileName = "delete-me.txt",
            RouteDisplayName = "Test route",
            RelativePath = "Folder/delete-me.txt",
            State = SyncJobState.Uploading,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await database.AddSyncJobAsync(interruptedDelete);

        await new LocalDatabase(paths).InitializeAsync();
        var recovered = Assert.Single(await database.GetSyncJobsAsync());

        Assert.Equal(SyncOperationKind.Delete, recovered.OperationKind);
        Assert.Equal(SyncJobState.Uploading, recovered.State);
        Assert.False(recovered.UploadMayHaveCommitted);
        Assert.True(recovered.DeleteArmed);
        Assert.Equal(SyncFailureKind.None, recovered.FailureKind);
    }

    [Fact]
    public async Task PersistsCloseBehaviorSetting()
    {
        using var directory = new MigrationDirectory();
        var database = new LocalDatabase(new AppDataPaths(directory.DataPath));
        await database.InitializeAsync();
        var settings = await database.GetSettingsAsync();
        settings.CloseBehavior = AppCloseBehavior.Exit;

        await database.SaveSettingsAsync(settings);
        var reloaded = await database.GetSettingsAsync();

        Assert.Equal(AppCloseBehavior.Exit, reloaded.CloseBehavior);
    }

    [Fact]
    public async Task InitializationPurgesOnlyExpiredTerminalHistoryBeforeInitialLoad()
    {
        using var directory = new MigrationDirectory();
        var paths = new AppDataPaths(directory.DataPath);
        var database = new LocalDatabase(paths);
        await database.InitializeAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-8);
        var routeId = Guid.NewGuid();
        var completed = new SyncJob
        {
            RouteId = routeId,
            OperationKey = SyncJob.CreateOperationKey(routeId, "completed.txt"),
            FileName = "completed.txt",
            RelativePath = "completed.txt",
            State = SyncJobState.Completed,
            Progress = 100,
            CreatedAt = old,
            CompletedAt = old,
            UpdatedAt = old
        };
        var active = new SyncJob
        {
            RouteId = routeId,
            OperationKey = SyncJob.CreateOperationKey(routeId, "active.txt"),
            FileName = "active.txt",
            RelativePath = "active.txt",
            State = SyncJobState.Waiting,
            CreatedAt = old,
            UpdatedAt = old
        };
        await database.AddSyncJobAsync(completed);
        await database.AddSyncJobAsync(active);

        await new LocalDatabase(paths).InitializeAsync();
        var remaining = await database.GetSyncJobsAsync();

        Assert.DoesNotContain(remaining, job => job.Id == completed.Id);
        Assert.Contains(remaining, job => job.Id == active.Id);
    }

    private sealed class MigrationDirectory : IDisposable
    {
        public MigrationDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"EasyShareQueueMigration-{Guid.NewGuid():N}");
            DataPath = Path.Combine(Root, "Data");
        }

        public string Root { get; }

        public string DataPath { get; }

        public void Dispose()
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
