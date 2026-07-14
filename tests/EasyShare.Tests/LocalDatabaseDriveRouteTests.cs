using EasyShare.Models;
using EasyShare.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EasyShare.Tests
{
    public sealed class LocalDatabaseDriveRouteTests
    {
        private static readonly string[] GraphColumnNames =
            ["SiteId", "DriveId", "RootItemId", "FolderWebUrl"];

        [Fact]
        public async Task NewDatabaseCreatesNullableGraphIdentityColumnsIdempotently()
        {
            using var environment = new TestDirectory();
            var paths = CreatePaths(environment);
            var database = new LocalDatabase(paths);

            await database.InitializeAsync();
            await database.InitializeAsync();

            var columns = await ReadDriveRouteColumnsAsync(paths.DatabasePath);
            foreach (var columnName in GraphColumnNames)
            {
                Assert.True(columns.TryGetValue(columnName, out var isNotNull));
                Assert.False(isNotNull);
            }

            Assert.Equal(GraphColumnNames.Length, columns.Keys.Count(GraphColumnNames.Contains));
        }

        [Fact]
        public async Task InitializeMigratesLegacySchemaWithoutChangingExistingRoute()
        {
            using var environment = new TestDirectory();
            var paths = CreatePaths(environment);
            paths.EnsureCreated();
            var routeId = Guid.NewGuid();
            var checkedAt = DateTimeOffset.Parse("2025-04-03T12:30:00+00:00");
            await CreateLegacyDatabaseAsync(paths.DatabasePath, routeId, checkedAt);
            var database = new LocalDatabase(paths);

            await database.InitializeAsync();
            await database.InitializeAsync();

            var route = Assert.Single(await database.GetRoutesAsync());
            Assert.Equal(routeId, route.Id);
            Assert.Equal("Legado", route.DisplayName);
            Assert.Equal("https://contoso.sharepoint.com/sites/legacy", route.SharePointUrl);
            Assert.Equal("/Documentos/Arquivo", route.RemotePath);
            Assert.True(route.IsConnected);
            Assert.Equal("Disponível", route.StatusText);
            Assert.Equal(checkedAt, route.LastCheckedAt);
            Assert.Equal(string.Empty, route.SiteId);
            Assert.Equal(string.Empty, route.DriveId);
            Assert.Equal(string.Empty, route.RootItemId);
            Assert.Equal(string.Empty, route.FolderWebUrl);
            Assert.False(route.HasGraphIdentity);

            var columns = await ReadDriveRouteColumnsAsync(paths.DatabasePath);
            Assert.All(GraphColumnNames, columnName => Assert.Contains(columnName, columns.Keys));
        }

        [Fact]
        public async Task RouteWithGraphIdentityRoundTripsAcrossInsertAndUpdate()
        {
            using var environment = new TestDirectory();
            var database = new LocalDatabase(CreatePaths(environment));
            await database.InitializeAsync();
            var route = CreateRoute();

            await database.AddRouteAsync(route);
            var inserted = Assert.Single(await database.GetRoutesAsync());

            AssertRouteIdentity(inserted, "site-1", "drive-1", "root-1", "https://contoso.sharepoint.com/sites/team/Documents/Forms/AllItems.aspx");
            Assert.True(inserted.HasGraphIdentity);
            Assert.Equal("https://contoso.sharepoint.com/sites/team", inserted.SharePointUrl);
            Assert.Equal("/Documents/Projetos", inserted.RemotePath);

            route.SiteId = "site-2";
            route.DriveId = "drive-2";
            route.RootItemId = "folder-2";
            route.FolderWebUrl = "https://contoso.sharepoint.com/sites/team/Documents/Projetos";
            await database.UpdateRouteAsync(route);

            var updated = Assert.Single(await database.GetRoutesAsync());
            AssertRouteIdentity(updated, "site-2", "drive-2", "folder-2", route.FolderWebUrl);
            Assert.True(updated.HasGraphIdentity);
        }

        [Fact]
        public async Task RouteWithoutGraphIdentityRoundTripsAsEmptyValues()
        {
            using var environment = new TestDirectory();
            var database = new LocalDatabase(CreatePaths(environment));
            await database.InitializeAsync();
            var route = CreateRoute();
            route.SiteId = string.Empty;
            route.DriveId = string.Empty;
            route.RootItemId = string.Empty;
            route.FolderWebUrl = string.Empty;

            await database.AddRouteAsync(route);

            var stored = Assert.Single(await database.GetRoutesAsync());
            AssertRouteIdentity(stored, string.Empty, string.Empty, string.Empty, string.Empty);
            Assert.False(stored.HasGraphIdentity);
        }

        [Fact]
        public async Task SetupWizardCompletionVersionRoundTripsWithSettings()
        {
            using var environment = new TestDirectory();
            var database = new LocalDatabase(CreatePaths(environment));
            await database.InitializeAsync();
            var settings = new AppSettings
            {
                SetupWizardCompleted = true,
                SetupWizardCompletedVersion = SetupWizardAdvisor.CurrentVersion
            };

            await database.SaveSettingsAsync(settings);

            var stored = await database.GetSettingsAsync();
            Assert.True(stored.SetupWizardCompleted);
            Assert.Equal(SetupWizardAdvisor.CurrentVersion, stored.SetupWizardCompletedVersion);
        }

        [Fact]
        public async Task NegativeSetupWizardCompletionVersionIsPersistedAsZero()
        {
            using var environment = new TestDirectory();
            var database = new LocalDatabase(CreatePaths(environment));
            await database.InitializeAsync();

            await database.SaveSettingsAsync(new AppSettings
            {
                SetupWizardCompletedVersion = -10
            });

            var stored = await database.GetSettingsAsync();
            Assert.Equal(0, stored.SetupWizardCompletedVersion);
        }

        private static AppDataPaths CreatePaths(TestDirectory environment) =>
            new(
                Path.Combine(environment.Root, "data"),
                Path.Combine(environment.Root, "machine-policy.json"));

        private static DriveRoute CreateRoute() => new()
        {
            DisplayName = "Projetos",
            SharePointUrl = "https://contoso.sharepoint.com/sites/team",
            RemotePath = "/Documents/Projetos",
            SiteId = "site-1",
            DriveId = "drive-1",
            RootItemId = "root-1",
            FolderWebUrl = "https://contoso.sharepoint.com/sites/team/Documents/Forms/AllItems.aspx",
            IsConnected = true,
            StatusText = "Disponível",
            LastCheckedAt = DateTimeOffset.Parse("2026-07-13T15:45:00+00:00")
        };

        private static void AssertRouteIdentity(
            DriveRoute route,
            string siteId,
            string driveId,
            string rootItemId,
            string folderWebUrl)
        {
            Assert.Equal(siteId, route.SiteId);
            Assert.Equal(driveId, route.DriveId);
            Assert.Equal(rootItemId, route.RootItemId);
            Assert.Equal(folderWebUrl, route.FolderWebUrl);
        }

        private static async Task<Dictionary<string, bool>> ReadDriveRouteColumnsAsync(string databasePath)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info('DriveRoutes');";
            await using var reader = await command.ExecuteReaderAsync();
            var columns = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync())
            {
                columns[reader.GetString(1)] = reader.GetInt32(3) == 1;
            }

            return columns;
        }

        private static async Task CreateLegacyDatabaseAsync(
            string databasePath,
            Guid routeId,
            DateTimeOffset checkedAt)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE DriveRoutes (
                    Id TEXT NOT NULL PRIMARY KEY,
                    DisplayName TEXT NOT NULL,
                    SharePointUrl TEXT NOT NULL,
                    RemotePath TEXT NOT NULL,
                    IsConnected INTEGER NOT NULL,
                    StatusText TEXT NOT NULL,
                    LastCheckedAt TEXT NULL
                );

                INSERT INTO DriveRoutes
                    (Id, DisplayName, SharePointUrl, RemotePath, IsConnected, StatusText, LastCheckedAt)
                VALUES
                    ($id, 'Legado', 'https://contoso.sharepoint.com/sites/legacy',
                     '/Documentos/Arquivo', 1, 'Disponível', $lastCheckedAt);
                """;
            command.Parameters.AddWithValue("$id", routeId.ToString());
            command.Parameters.AddWithValue("$lastCheckedAt", checkedAt.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
    }
}

namespace EasyShare.Services
{
    public sealed record SharePointDriveItem(
        string Name,
        string ServerRelativeUrl,
        bool IsDirectory,
        long Length,
        DateTimeOffset ModifiedAt);
}
