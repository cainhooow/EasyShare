using EasyShare.Models;
using EasyShare.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EasyShare.Tests;

public sealed class ContentIndexServiceTests
{
    [Fact]
    public async Task InitializeCreatesOwnedSchemaIdempotently()
    {
        using var environment = new TestDirectory();
        var (database, service, _) = CreateService(environment);

        await service.InitializeAsync();
        await service.InitializeAsync();
        await new ContentIndexService(database).InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={database.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE name IN (
                'IndexedContent',
                'ContentAccessStats',
                'IX_IndexedContent_Scope_Name',
                'IX_IndexedContent_Scope_Path',
                'IX_IndexedContent_Scope_Route',
                'IX_ContentAccessStats_Scope_Rank');
            """;
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Assert.True(names.Add(reader.GetString(0)));
        }

        Assert.Equal(6, names.Count);
    }

    [Fact]
    public async Task FullResetRehydratesSettingsAndIndexInTheSameProcessWithoutResidualStores()
    {
        using var environment = new TestDirectory();
        var paths = new AppDataPaths(
            Path.Combine(environment.Root, "data"),
            Path.Combine(environment.Root, "machine-policy.json"));
        var database = new LocalDatabase(paths);
        var index = new ContentIndexService(database);
        var reset = new LocalDataResetService(paths);
        var runtime = new LocalDataRuntimeRehydrator(reset, index);
        var oldRouteId = Guid.NewGuid();

        await database.InitializeAsync();
        await index.InitializeAsync();
        await database.SaveSettingsAsync(new AppSettings
        {
            ClientId = "OLD-PRIVATE-CLIENT"
        });
        await index.UpsertItemsAsync(
            "old-scope",
            [new ContentIndexItem(
                oldRouteId,
                "Private",
                "old-secret.docx",
                "old-secret.docx",
                IsDirectory: false)]);
        WriteFile(paths.TokenCachePath, "old-token"u8.ToArray());
        WriteFile(paths.UploadPayloadKeyPath, "old-upload-key"u8.ToArray());
        WriteFile(
            Path.Combine(paths.UploadQueueDirectory, "old-payload.bin"),
            "old-payload"u8.ToArray());
        WriteFile(paths.OfflineCacheKeyPath, "old-offline-key"u8.ToArray());
        WriteFile(
            Path.Combine(paths.OfflineCacheDirectory, "old-cache.bin"),
            "old-cache"u8.ToArray());
        WriteFile(
            Path.Combine(paths.BrowserProfilePath, "old-cookie.db"),
            "old-cookie"u8.ToArray());

        database.ReleasePooledConnectionsForLocalDataReset();
        var resetResult = await reset.ResetAsync();

        Assert.True(
            resetResult.Succeeded,
            string.Join(
                Environment.NewLine,
                resetResult.Failures.Select(failure => $"{failure.Path}: {failure.Reason}")));
        Assert.False(Directory.Exists(paths.DataDirectory));
        Assert.False(File.Exists(reset.PendingMarkerPath));

        await runtime.RehydrateAsync();

        Assert.True(File.Exists(paths.DatabasePath));
        Assert.False(File.Exists(paths.TokenCachePath));
        Assert.False(File.Exists(paths.UploadPayloadKeyPath));
        Assert.False(File.Exists(paths.OfflineCacheKeyPath));
        Assert.False(Directory.Exists(paths.UploadQueueDirectory));
        Assert.False(Directory.Exists(paths.OfflineCacheDirectory));
        Assert.False(Directory.Exists(paths.BrowserProfilePath));
        Assert.Empty(Directory.EnumerateDirectories(paths.DataDirectory));
        Assert.Equal(
            [Path.GetFileName(paths.DatabasePath)],
            Directory.EnumerateFiles(paths.DataDirectory)
                .Select(Path.GetFileName)
                .Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(await index.SearchAsync("old-scope", "old-secret"));
        Assert.True(string.IsNullOrWhiteSpace((await database.GetSettingsAsync()).ClientId));
        Assert.False(File.Exists(reset.PendingMarkerPath));

        const string newClientId = "11111111-1111-1111-1111-111111111111";
        var newRouteId = Guid.NewGuid();
        await database.SaveSettingsAsync(new AppSettings
        {
            ClientId = newClientId
        });
        await index.UpsertItemsAsync(
            "new-scope",
            [new ContentIndexItem(
                newRouteId,
                "New route",
                "new-file.docx",
                "new-file.docx",
                IsDirectory: false)]);

        Assert.Equal(newClientId, (await database.GetSettingsAsync()).ClientId);
        Assert.Equal(
            "new-file.docx",
            Assert.Single(await index.SearchAsync("new-scope", "new-file")).Name);
    }

    [Fact]
    public async Task FailedRuntimeRehydrationRestoresPendingResetMarker()
    {
        using var environment = new TestDirectory();
        var paths = new AppDataPaths(
            Path.Combine(environment.Root, "data"),
            Path.Combine(environment.Root, "machine-policy.json"));
        var database = new LocalDatabase(paths);
        var index = new ContentIndexService(database);
        var reset = new LocalDataResetService(paths);
        var runtime = new LocalDataRuntimeRehydrator(reset, index);

        await database.InitializeAsync();
        await index.InitializeAsync();
        database.ReleasePooledConnectionsForLocalDataReset();
        var resetResult = await reset.ResetAsync();
        Assert.True(
            resetResult.Succeeded,
            string.Join(
                Environment.NewLine,
                resetResult.Failures.Select(failure => $"{failure.Path}: {failure.Reason}")));
        Assert.False(File.Exists(reset.PendingMarkerPath));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.RehydrateAsync(_ =>
                Task.FromException(new InvalidOperationException("offline rehydration failed"))));

        Assert.Equal("offline rehydration failed", exception.Message);
        Assert.True(File.Exists(paths.DatabasePath));
        Assert.True(File.Exists(reset.PendingMarkerPath));

        database.ReleasePooledConnectionsForLocalDataReset();
        Assert.True(reset.CompletePendingResetOrThrow().Succeeded);
        Assert.False(Directory.Exists(paths.DataDirectory));
        Assert.False(File.Exists(reset.PendingMarkerPath));
    }

    [Fact]
    public async Task SearchIsCaseAndAccentInsensitiveAndEscapesLikeWildcards()
    {
        using var environment = new TestDirectory();
        var (_, service, _) = CreateService(environment);
        var routeId = Guid.NewGuid();
        var item = new ContentIndexItem(
            routeId,
            "Projetos",
            "Financeiro/Relatórios/Orçamento_100%.xlsx",
            "Orçamento_100%.xlsx",
            IsDirectory: false,
            Length: 42,
            ModifiedAt: DateTimeOffset.Parse("2026-07-01T10:00:00Z"),
            RemoteLocator: "/sites/equipe/Orçamento_100%.xlsx");

        await service.UpsertItemsAsync("scope-a", [item]);
        await service.UpsertItemsAsync("scope-b", [item with { RouteDisplayName = "Outro usuário" }]);

        var byPath = Assert.Single(await service.SearchAsync("scope-a", "RELATORIOS"));
        Assert.Equal(item.Name, byPath.Name);
        Assert.Equal(item.RelativePath, byPath.RelativePath);

        var literalWildcard = Assert.Single(await service.SearchAsync("scope-a", "ORÇAMENTO_100%"));
        Assert.Equal(item.RemoteLocator, literalWildcard.RemoteLocator);

        Assert.Empty(await service.SearchAsync("scope-a", "orcamentoX1000"));
        Assert.Empty(await service.SearchAsync("scope-c", "relatorios"));
    }

    [Fact]
    public async Task UpsertUpdatesMetadataWithoutDuplicatingOrLosingAccessHistory()
    {
        using var environment = new TestDirectory();
        var (_, service, _) = CreateService(environment);
        var routeId = Guid.NewGuid();
        const string path = "Documentos/arquivo.docx";

        await service.UpsertItemsAsync(
            "scope",
            [new ContentIndexItem(routeId, "Documentos", path, "Rascunho.docx", false, 10)]);
        await service.RecordAccessAsync("scope", routeId, path, ContentAccessKind.FileOpened);

        await service.UpsertItemsAsync(
            "scope",
            [new ContentIndexItem(
                routeId,
                "Documentos renomeados",
                path,
                "Final.docx",
                false,
                99,
                RemoteLocator: "graph://drive/item")]);

        Assert.Empty(await service.SearchAsync("scope", "rascunho"));
        var updated = Assert.Single(await service.SearchAsync("scope", "final"));
        Assert.Equal("Documentos renomeados", updated.RouteDisplayName);
        Assert.Equal(99, updated.Length);
        Assert.Equal("graph://drive/item", updated.RemoteLocator);
        Assert.Equal(1, updated.AccessCount);
        Assert.Equal(ContentAccessKind.FileOpened, updated.LastAccessKind);
    }

    [Fact]
    public async Task RankingCombinesTextAccessCountAndRecencyDeterministically()
    {
        using var environment = new TestDirectory();
        var (_, service, clock) = CreateService(environment);
        var routeId = Guid.NewGuid();
        var exact = new ContentIndexItem(routeId, "Planos", "Plano", "Plano", true);
        var oldFrequent = new ContentIndexItem(routeId, "Planos", "Plano A.docx", "Plano A.docx", false);
        var recentFrequent = new ContentIndexItem(routeId, "Planos", "Plano B.docx", "Plano B.docx", false);
        var recentSingle = new ContentIndexItem(routeId, "Planos", "Plano C.docx", "Plano C.docx", false);
        await service.UpsertItemsAsync("scope", [exact, oldFrequent, recentFrequent, recentSingle]);

        await service.RecordAccessAsync("scope", routeId, oldFrequent.RelativePath);
        await service.RecordAccessAsync("scope", routeId, oldFrequent.RelativePath);
        await service.RecordAccessAsync("scope", routeId, recentFrequent.RelativePath);
        clock.Advance(TimeSpan.FromDays(20));
        await service.RecordAccessAsync("scope", routeId, recentFrequent.RelativePath);
        await service.RecordAccessAsync("scope", routeId, recentSingle.RelativePath);

        var search = await service.SearchAsync("scope", "plano");
        Assert.Equal(exact.RelativePath, search[0].RelativePath);
        Assert.Equal(recentFrequent.RelativePath, search[1].RelativePath);
        Assert.Equal(recentSingle.RelativePath, search[2].RelativePath);
        Assert.Equal(oldFrequent.RelativePath, search[3].RelativePath);

        var mostAccessed = await service.GetMostAccessedAsync("scope");
        Assert.Equal(recentFrequent.RelativePath, mostAccessed[0].RelativePath);
        Assert.Equal(oldFrequent.RelativePath, mostAccessed[1].RelativePath);
        Assert.Equal(recentSingle.RelativePath, mostAccessed[2].RelativePath);
    }

    [Fact]
    public async Task RecordAccessUsesAtomicUpsertUnderConcurrency()
    {
        using var environment = new TestDirectory();
        var (_, service, _) = CreateService(environment);
        var routeId = Guid.NewGuid();
        const string path = "Equipe/Atas.docx";
        await service.UpsertItemsAsync(
            "scope",
            [new ContentIndexItem(routeId, "Equipe", path, "Atas.docx", false)]);

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            service.RecordAccessAsync(
                "scope",
                routeId,
                path,
                ContentAccessKind.VirtualDrive)));

        var result = Assert.Single(await service.GetMostAccessedAsync("scope"));
        Assert.Equal(32, result.AccessCount);
        Assert.Equal(ContentAccessKind.VirtualDrive, result.LastAccessKind);
    }

    [Fact]
    public async Task RemoveRouteAndClearScopeDoNotAffectOtherData()
    {
        using var environment = new TestDirectory();
        var (_, service, _) = CreateService(environment);
        var firstRoute = Guid.NewGuid();
        var secondRoute = Guid.NewGuid();
        var first = new ContentIndexItem(firstRoute, "Primeira", "alpha.txt", "alpha.txt", false);
        var second = new ContentIndexItem(secondRoute, "Segunda", "beta.txt", "beta.txt", false);

        await service.UpsertItemsAsync("scope-a", [first, second]);
        await service.UpsertItemsAsync("scope-b", [first]);
        await service.RecordAccessAsync("scope-a", firstRoute, first.RelativePath);
        await service.RecordAccessAsync("scope-b", firstRoute, first.RelativePath);

        await service.RemoveRouteAsync("scope-a", firstRoute);

        Assert.Empty(await service.SearchAsync("scope-a", "alpha"));
        Assert.Single(await service.SearchAsync("scope-a", "beta"));
        Assert.Single(await service.SearchAsync("scope-b", "alpha"));
        Assert.Empty(await service.GetMostAccessedAsync("scope-a"));

        await service.ClearScopeAsync("scope-a");

        Assert.Empty(await service.SearchAsync("scope-a", "beta"));
        Assert.Single(await service.SearchAsync("scope-b", "alpha"));
        Assert.Single(await service.GetMostAccessedAsync("scope-b"));
    }

    [Fact]
    public async Task ReplacingCompleteRouteSnapshotPrunesStaleItemsAndKeepsRetainedHistory()
    {
        using var environment = new TestDirectory();
        var (_, service, _) = CreateService(environment);
        var routeId = Guid.NewGuid();
        var retained = new ContentIndexItem(routeId, "Equipe", "retido.docx", "retido.docx", false);
        var stale = new ContentIndexItem(routeId, "Equipe", "removido.docx", "removido.docx", false);
        await service.UpsertItemsAsync("scope", [retained, stale]);
        await service.RecordAccessAsync("scope", routeId, retained.RelativePath);
        await service.RecordAccessAsync("scope", routeId, stale.RelativePath);

        await service.ReplaceRouteItemsAsync(
            "scope",
            routeId,
            [
                retained with { Length = 99 },
                new ContentIndexItem(routeId, "Equipe", "novo.docx", "novo.docx", false)
            ]);

        Assert.Empty(await service.SearchAsync("scope", "removido"));
        Assert.Single(await service.SearchAsync("scope", "novo"));
        var kept = Assert.Single(await service.SearchAsync("scope", "retido"));
        Assert.Equal(99, kept.Length);
        Assert.Equal(1, kept.AccessCount);
        Assert.Equal("retido.docx", Assert.Single(await service.GetMostAccessedAsync("scope")).Name);
    }

    [Fact]
    public async Task RemovingRouteFromAllScopesRemovesEveryIdentityPartition()
    {
        using var environment = new TestDirectory();
        var (_, service, _) = CreateService(environment);
        var routeId = Guid.NewGuid();
        var item = new ContentIndexItem(routeId, "Equipe", "segredo.docx", "segredo.docx", false);
        await service.UpsertItemsAsync("scope-a", [item]);
        await service.UpsertItemsAsync("scope-b", [item]);
        await service.RecordAccessAsync("scope-a", routeId, item.RelativePath);
        await service.RecordAccessAsync("scope-b", routeId, item.RelativePath);

        await service.RemoveRouteFromAllScopesAsync(routeId);

        Assert.Empty(await service.SearchAsync("scope-a", "segredo"));
        Assert.Empty(await service.SearchAsync("scope-b", "segredo"));
        Assert.Empty(await service.GetMostAccessedAsync("scope-a"));
        Assert.Empty(await service.GetMostAccessedAsync("scope-b"));
    }

    [Fact]
    public async Task RemovingDirectoryItemPurgesDescendantsAndAccessHistoryAcrossScopes()
    {
        using var environment = new TestDirectory();
        var (_, service, _) = CreateService(environment);
        var routeId = Guid.NewGuid();
        var directory = new ContentIndexItem(
            routeId,
            "Equipe",
            "Projects/Old",
            "Old",
            IsDirectory: true);
        var descendant = new ContentIndexItem(
            routeId,
            "Equipe",
            "Projects/Old/report.docx",
            "report.docx",
            IsDirectory: false);
        var sibling = new ContentIndexItem(
            routeId,
            "Equipe",
            "Projects/keep.docx",
            "keep.docx",
            IsDirectory: false);
        await service.UpsertItemsAsync("scope-a", [directory, descendant, sibling]);
        await service.UpsertItemsAsync("scope-b", [directory, descendant, sibling]);
        await service.RecordAccessAsync("scope-a", routeId, descendant.RelativePath);
        await service.RecordAccessAsync("scope-b", routeId, descendant.RelativePath);

        var removed = await service.RemoveItemFromAllScopesAsync(
            routeId,
            directory.RelativePath,
            isDirectory: true);

        Assert.Equal(4, removed);
        Assert.Empty(await service.SearchAsync("scope-a", "report"));
        Assert.Empty(await service.SearchAsync("scope-b", "report"));
        Assert.Empty(await service.GetMostAccessedAsync("scope-a"));
        Assert.Empty(await service.GetMostAccessedAsync("scope-b"));
        Assert.Single(await service.SearchAsync("scope-a", "keep"));
        Assert.Single(await service.SearchAsync("scope-b", "keep"));
    }

    private static (LocalDatabase Database, ContentIndexService Service, ManualTimeProvider Clock)
        CreateService(TestDirectory environment)
    {
        var paths = new AppDataPaths(
            Path.Combine(environment.Root, "data"),
            Path.Combine(environment.Root, "machine-policy.json"));
        var database = new LocalDatabase(paths);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-01T12:00:00Z"));
        return (database, new ContentIndexService(database, clock), clock);
    }

    private static void WriteFile(string path, byte[] contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
