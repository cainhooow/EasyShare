using EasyShare.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EasyShare.Tests;

public sealed class DirectoryCacheScopeTests
{
    private const string AccountScopeA = "account-scope-a";
    private const string AccountScopeB = "account-scope-b";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task NewDatabaseIncludesAccountScopeInDirectoryCachePrimaryKey()
    {
        using var environment = new TestDirectory();
        var paths = CreatePaths(environment);
        var database = new LocalDatabase(paths);

        await database.InitializeAsync();
        await database.InitializeAsync();

        var primaryKey = await ReadDirectoryCachePrimaryKeyAsync(paths.DatabasePath);
        Assert.Equal(
            ["AccountScope", "RouteId", "RelativePath"],
            primaryKey.OrderBy(column => column.Position).Select(column => column.Name));
        Assert.True(primaryKey.Single(column => column.Name == "AccountScope").IsNotNull);
    }

    [Fact]
    public async Task LegacyUnscopedDirectoryCacheIsMigratedAndDiscarded()
    {
        using var environment = new TestDirectory();
        var paths = CreatePaths(environment);
        paths.EnsureCreated();
        await CreateLegacyDirectoryCacheAsync(paths.DatabasePath);
        var database = new LocalDatabase(paths);

        await database.InitializeAsync();
        await database.InitializeAsync();

        var primaryKey = await ReadDirectoryCachePrimaryKeyAsync(paths.DatabasePath);
        Assert.Equal(
            ["AccountScope", "RouteId", "RelativePath"],
            primaryKey.OrderBy(column => column.Position).Select(column => column.Name));
        Assert.Equal(0L, await CountRowsAsync(paths.DatabasePath, "DirectoryCache"));
        Assert.False(await TableExistsAsync(paths.DatabasePath, "DirectoryCacheLegacy"));
    }

    [Fact]
    public async Task SameRouteAndPathRemainPartitionedByAccountScope()
    {
        using var environment = new TestDirectory();
        var database = new LocalDatabase(CreatePaths(environment));
        await database.InitializeAsync();
        var routeId = Guid.NewGuid();

        database.SaveDirectoryCache(AccountScopeA, routeId, "Reports", [CreateItem("Alpha.docx")]);
        database.SaveDirectoryCache(AccountScopeB, routeId, "Reports", [CreateItem("Beta.docx")]);

        var accountA = database.TryGetDirectoryCache(AccountScopeA, routeId, "Reports", CacheLifetime);
        var accountB = database.TryGetDirectoryCache(AccountScopeB, routeId, "Reports", CacheLifetime);

        Assert.Equal("Alpha.docx", Assert.Single(Assert.IsType<DirectoryCacheSnapshot>(accountA).Items).Name);
        Assert.Equal("Beta.docx", Assert.Single(Assert.IsType<DirectoryCacheSnapshot>(accountB).Items).Name);
    }

    [Fact]
    public async Task ClearingOneAccountScopePreservesTheOtherScope()
    {
        using var environment = new TestDirectory();
        var database = new LocalDatabase(CreatePaths(environment));
        await database.InitializeAsync();
        var routeId = Guid.NewGuid();

        database.SaveDirectoryCache(AccountScopeA, routeId, "Reports", [CreateItem("Alpha.docx")]);
        database.SaveDirectoryCache(AccountScopeB, routeId, "Reports", [CreateItem("Beta.docx")]);

        database.ClearDirectoryCache(AccountScopeA);

        Assert.Null(database.TryGetDirectoryCache(AccountScopeA, routeId, "Reports", CacheLifetime));
        Assert.Equal(
            "Beta.docx",
            Assert.Single(database.TryGetDirectoryCache(AccountScopeB, routeId, "Reports", CacheLifetime)!.Items).Name);
    }

    [Fact]
    public async Task InvalidatingOneAccountScopePreservesTheOtherScope()
    {
        using var environment = new TestDirectory();
        var database = new LocalDatabase(CreatePaths(environment));
        await database.InitializeAsync();
        var routeId = Guid.NewGuid();

        SaveTwoScopedPaths(database, routeId);

        database.InvalidateDirectoryCache(AccountScopeA, routeId, "Reports");

        Assert.Null(database.TryGetDirectoryCache(AccountScopeA, routeId, "Reports", CacheLifetime));
        Assert.NotNull(database.TryGetDirectoryCache(AccountScopeA, routeId, "Plans", CacheLifetime));
        Assert.NotNull(database.TryGetDirectoryCache(AccountScopeB, routeId, "Reports", CacheLifetime));

        database.InvalidateRouteDirectoryCache(AccountScopeA, routeId);

        Assert.Null(database.TryGetDirectoryCache(AccountScopeA, routeId, "Plans", CacheLifetime));
        Assert.NotNull(database.TryGetDirectoryCache(AccountScopeB, routeId, "Reports", CacheLifetime));
        Assert.NotNull(database.TryGetDirectoryCache(AccountScopeB, routeId, "Plans", CacheLifetime));
    }

    [Fact]
    public async Task DirectoryDeleteInvalidatesPathParentAndDescendantsAcrossEveryScope()
    {
        using var environment = new TestDirectory();
        var database = new LocalDatabase(CreatePaths(environment));
        await database.InitializeAsync();
        var routeId = Guid.NewGuid();
        var otherRouteId = Guid.NewGuid();

        foreach (var scope in new[] { AccountScopeA, AccountScopeB })
        {
            database.SaveDirectoryCache(scope, routeId, string.Empty, [CreateItem("Root.docx")]);
            database.SaveDirectoryCache(scope, routeId, "Reports", [CreateItem("2026")]);
            database.SaveDirectoryCache(scope, routeId, "Reports/2026", [CreateItem("July")]);
            database.SaveDirectoryCache(scope, routeId, "Reports/2026/July", [CreateItem("Close.docx")]);
            database.SaveDirectoryCache(scope, routeId, "Reports/2025", [CreateItem("Keep.docx")]);
            database.SaveDirectoryCache(scope, otherRouteId, "Reports/2026", [CreateItem("Other.docx")]);
        }

        database.InvalidateDeletePathDirectoryCache(routeId, "Reports/2026", isDirectory: true);

        foreach (var scope in new[] { AccountScopeA, AccountScopeB })
        {
            Assert.Null(database.TryGetDirectoryCache(scope, routeId, "Reports", CacheLifetime));
            Assert.Null(database.TryGetDirectoryCache(scope, routeId, "Reports/2026", CacheLifetime));
            Assert.Null(database.TryGetDirectoryCache(scope, routeId, "Reports/2026/July", CacheLifetime));
            Assert.NotNull(database.TryGetDirectoryCache(scope, routeId, string.Empty, CacheLifetime));
            Assert.NotNull(database.TryGetDirectoryCache(scope, routeId, "Reports/2025", CacheLifetime));
            Assert.NotNull(database.TryGetDirectoryCache(scope, otherRouteId, "Reports/2026", CacheLifetime));
        }
    }

    [Fact]
    public async Task FileDeleteInvalidatesItemAndParentAcrossEveryScopeWithoutRemovingSiblingDirectories()
    {
        using var environment = new TestDirectory();
        var database = new LocalDatabase(CreatePaths(environment));
        await database.InitializeAsync();
        var routeId = Guid.NewGuid();

        foreach (var scope in new[] { AccountScopeA, AccountScopeB })
        {
            database.SaveDirectoryCache(scope, routeId, "Reports", [CreateItem("Budget.docx")]);
            database.SaveDirectoryCache(scope, routeId, "Reports/Budget.docx", [CreateItem("Stale.docx")]);
            database.SaveDirectoryCache(scope, routeId, "Reports/Budget.docx/Unexpected", [CreateItem("Keep.docx")]);
            database.SaveDirectoryCache(scope, routeId, "Reports/Plans", [CreateItem("Keep.docx")]);
        }

        database.InvalidateDeletePathDirectoryCache(
            routeId,
            @"\Reports\Budget.docx\",
            isDirectory: false);

        foreach (var scope in new[] { AccountScopeA, AccountScopeB })
        {
            Assert.Null(database.TryGetDirectoryCache(scope, routeId, "Reports", CacheLifetime));
            Assert.Null(database.TryGetDirectoryCache(scope, routeId, "Reports/Budget.docx", CacheLifetime));
            Assert.NotNull(database.TryGetDirectoryCache(
                scope,
                routeId,
                "Reports/Budget.docx/Unexpected",
                CacheLifetime));
            Assert.NotNull(database.TryGetDirectoryCache(scope, routeId, "Reports/Plans", CacheLifetime));
        }
    }

    private static void SaveTwoScopedPaths(LocalDatabase database, Guid routeId)
    {
        database.SaveDirectoryCache(AccountScopeA, routeId, "Reports", [CreateItem("Alpha.docx")]);
        database.SaveDirectoryCache(AccountScopeA, routeId, "Plans", [CreateItem("AlphaPlan.docx")]);
        database.SaveDirectoryCache(AccountScopeB, routeId, "Reports", [CreateItem("Beta.docx")]);
        database.SaveDirectoryCache(AccountScopeB, routeId, "Plans", [CreateItem("BetaPlan.docx")]);
    }

    private static SharePointDriveItem CreateItem(string name) =>
        new(
            name,
            $"/sites/team/Documents/{name}",
            IsDirectory: false,
            Length: 42,
            ModifiedAt: DateTimeOffset.Parse("2026-07-14T12:00:00+00:00"));

    private static AppDataPaths CreatePaths(TestDirectory environment) =>
        new(
            Path.Combine(environment.Root, "data"),
            Path.Combine(environment.Root, "machine-policy.json"));

    private static async Task CreateLegacyDirectoryCacheAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE DirectoryCache (
                RouteId TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                CachedAt TEXT NOT NULL,
                ItemsJson TEXT NOT NULL,
                PRIMARY KEY (RouteId, RelativePath)
            );

            INSERT INTO DirectoryCache (RouteId, RelativePath, CachedAt, ItemsJson)
            VALUES ($routeId, 'Reports', $cachedAt, '[]');
            """;
        command.Parameters.AddWithValue("$routeId", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$cachedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<PrimaryKeyColumn>> ReadDirectoryCachePrimaryKeyAsync(
        string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('DirectoryCache');";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<PrimaryKeyColumn>();
        while (await reader.ReadAsync())
        {
            var position = reader.GetInt32(5);
            if (position > 0)
            {
                columns.Add(new PrimaryKeyColumn(reader.GetString(1), position, reader.GetInt32(3) == 1));
            }
        }

        return columns;
    }

    private static async Task<long> CountRowsAsync(string databasePath, string tableName)
    {
        Assert.Equal("DirectoryCache", tableName);
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM DirectoryCache;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> TableExistsAsync(string databasePath, string tableName)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName LIMIT 1;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return await command.ExecuteScalarAsync() is not null;
    }

    private sealed record PrimaryKeyColumn(string Name, int Position, bool IsNotNull);
}
