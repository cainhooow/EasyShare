using System.Globalization;
using System.Text;
using EasyShare.Models;
using Microsoft.Data.Sqlite;

namespace EasyShare.Services;

/// <summary>
/// Owns the local, identity-scoped index of SharePoint files and folders.
/// This service intentionally has no crawling responsibility: callers feed it
/// directory snapshots and decide when remote content should be refreshed.
/// </summary>
public sealed class ContentIndexService
{
    private const int MaximumSearchResults = 200;
    private const long MaximumAccessCount = long.MaxValue;
    private readonly LocalDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    public ContentIndexService(LocalDatabase database, TimeProvider? timeProvider = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        EnsureInitializedAsync(cancellationToken);

    /// <summary>
    /// Recreates the base SQLite schema and this service's owned tables after a
    /// successful full local-data reset. The service instance survives the
    /// reset, so its in-memory initialization flag must not be trusted.
    /// </summary>
    public async Task RehydrateAfterLocalDataResetAsync(
        CancellationToken cancellationToken = default)
    {
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _initialized = false;
            cancellationToken.ThrowIfCancellationRequested();
            await _database.InitializeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await InitializeOwnedSchemaAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task UpsertItemsAsync(
        string scopeKey,
        IEnumerable<ContentIndexItem> items,
        CancellationToken cancellationToken = default)
    {
        scopeKey = ValidateScopeKey(scopeKey);
        ArgumentNullException.ThrowIfNull(items);

        var normalizedItems = new Dictionary<(Guid RouteId, string PathKey), NormalizedIndexItem>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeItem(item);
            normalizedItems[(normalized.Item.RouteId, normalized.PathKey)] = normalized;
        }

        if (normalizedItems.Count == 0)
        {
            return;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO IndexedContent
                (ScopeKey, RouteId, RouteDisplayName, RelativePath, PathKey,
                 NormalizedPath, Name, NormalizedName, IsDirectory, Length,
                 ModifiedAt, RemoteLocator, IndexedAt)
            VALUES
                ($scopeKey, $routeId, $routeDisplayName, $relativePath, $pathKey,
                 $normalizedPath, $name, $normalizedName, $isDirectory, $length,
                 $modifiedAt, $remoteLocator, $indexedAt)
            ON CONFLICT(ScopeKey, RouteId, PathKey) DO UPDATE SET
                RouteDisplayName = excluded.RouteDisplayName,
                RelativePath = excluded.RelativePath,
                NormalizedPath = excluded.NormalizedPath,
                Name = excluded.Name,
                NormalizedName = excluded.NormalizedName,
                IsDirectory = excluded.IsDirectory,
                Length = excluded.Length,
                ModifiedAt = excluded.ModifiedAt,
                RemoteLocator = excluded.RemoteLocator,
                IndexedAt = excluded.IndexedAt;
            """;

        var scopeParameter = command.Parameters.Add("$scopeKey", SqliteType.Text);
        var routeIdParameter = command.Parameters.Add("$routeId", SqliteType.Text);
        var routeDisplayNameParameter = command.Parameters.Add("$routeDisplayName", SqliteType.Text);
        var relativePathParameter = command.Parameters.Add("$relativePath", SqliteType.Text);
        var pathKeyParameter = command.Parameters.Add("$pathKey", SqliteType.Text);
        var normalizedPathParameter = command.Parameters.Add("$normalizedPath", SqliteType.Text);
        var nameParameter = command.Parameters.Add("$name", SqliteType.Text);
        var normalizedNameParameter = command.Parameters.Add("$normalizedName", SqliteType.Text);
        var isDirectoryParameter = command.Parameters.Add("$isDirectory", SqliteType.Integer);
        var lengthParameter = command.Parameters.Add("$length", SqliteType.Integer);
        var modifiedAtParameter = command.Parameters.Add("$modifiedAt", SqliteType.Text);
        var remoteLocatorParameter = command.Parameters.Add("$remoteLocator", SqliteType.Text);
        var indexedAtParameter = command.Parameters.Add("$indexedAt", SqliteType.Text);
        var indexedAt = ToDatabaseDate(_timeProvider.GetUtcNow());

        foreach (var normalized in normalizedItems.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = normalized.Item;
            scopeParameter.Value = scopeKey;
            routeIdParameter.Value = item.RouteId.ToString();
            routeDisplayNameParameter.Value = item.RouteDisplayName;
            relativePathParameter.Value = normalized.RelativePath;
            pathKeyParameter.Value = normalized.PathKey;
            normalizedPathParameter.Value = normalized.NormalizedPath;
            nameParameter.Value = item.Name;
            normalizedNameParameter.Value = normalized.NormalizedName;
            isDirectoryParameter.Value = item.IsDirectory ? 1 : 0;
            lengthParameter.Value = item.Length;
            modifiedAtParameter.Value = item.ModifiedAt is null
                ? DBNull.Value
                : ToDatabaseDate(item.ModifiedAt.Value);
            remoteLocatorParameter.Value = string.IsNullOrWhiteSpace(item.RemoteLocator)
                ? DBNull.Value
                : item.RemoteLocator;
            indexedAtParameter.Value = indexedAt;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceRouteItemsAsync(
        string scopeKey,
        Guid routeId,
        IEnumerable<ContentIndexItem> items,
        CancellationToken cancellationToken = default)
    {
        scopeKey = ValidateScopeKey(scopeKey);
        ValidateRouteId(routeId);
        ArgumentNullException.ThrowIfNull(items);

        var normalizedItems = new Dictionary<string, NormalizedIndexItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.RouteId != routeId)
            {
                throw new ArgumentException(
                    "Every replacement item must belong to the requested route.",
                    nameof(items));
            }

            var normalized = NormalizeItem(item);
            normalizedItems[normalized.PathKey] = normalized;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText =
                "DELETE FROM IndexedContent WHERE ScopeKey = $scopeKey AND RouteId = $routeId;";
            delete.Parameters.AddWithValue("$scopeKey", scopeKey);
            delete.Parameters.AddWithValue("$routeId", routeId.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText =
                """
                INSERT INTO IndexedContent
                    (ScopeKey, RouteId, RouteDisplayName, RelativePath, PathKey,
                     NormalizedPath, Name, NormalizedName, IsDirectory, Length,
                     ModifiedAt, RemoteLocator, IndexedAt)
                VALUES
                    ($scopeKey, $routeId, $routeDisplayName, $relativePath, $pathKey,
                     $normalizedPath, $name, $normalizedName, $isDirectory, $length,
                     $modifiedAt, $remoteLocator, $indexedAt);
                """;
            var scopeParameter = insert.Parameters.Add("$scopeKey", SqliteType.Text);
            var routeIdParameter = insert.Parameters.Add("$routeId", SqliteType.Text);
            var routeDisplayNameParameter = insert.Parameters.Add("$routeDisplayName", SqliteType.Text);
            var relativePathParameter = insert.Parameters.Add("$relativePath", SqliteType.Text);
            var pathKeyParameter = insert.Parameters.Add("$pathKey", SqliteType.Text);
            var normalizedPathParameter = insert.Parameters.Add("$normalizedPath", SqliteType.Text);
            var nameParameter = insert.Parameters.Add("$name", SqliteType.Text);
            var normalizedNameParameter = insert.Parameters.Add("$normalizedName", SqliteType.Text);
            var isDirectoryParameter = insert.Parameters.Add("$isDirectory", SqliteType.Integer);
            var lengthParameter = insert.Parameters.Add("$length", SqliteType.Integer);
            var modifiedAtParameter = insert.Parameters.Add("$modifiedAt", SqliteType.Text);
            var remoteLocatorParameter = insert.Parameters.Add("$remoteLocator", SqliteType.Text);
            var indexedAtParameter = insert.Parameters.Add("$indexedAt", SqliteType.Text);
            var indexedAt = ToDatabaseDate(_timeProvider.GetUtcNow());

            foreach (var normalized in normalizedItems.Values)
            {
                var item = normalized.Item;
                scopeParameter.Value = scopeKey;
                routeIdParameter.Value = routeId.ToString();
                routeDisplayNameParameter.Value = item.RouteDisplayName;
                relativePathParameter.Value = normalized.RelativePath;
                pathKeyParameter.Value = normalized.PathKey;
                normalizedPathParameter.Value = normalized.NormalizedPath;
                nameParameter.Value = item.Name;
                normalizedNameParameter.Value = normalized.NormalizedName;
                isDirectoryParameter.Value = item.IsDirectory ? 1 : 0;
                lengthParameter.Value = item.Length;
                modifiedAtParameter.Value = item.ModifiedAt is null
                    ? DBNull.Value
                    : ToDatabaseDate(item.ModifiedAt.Value);
                remoteLocatorParameter.Value = string.IsNullOrWhiteSpace(item.RemoteLocator)
                    ? DBNull.Value
                    : item.RemoteLocator;
                indexedAtParameter.Value = indexedAt;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var pruneAccess = connection.CreateCommand())
        {
            pruneAccess.Transaction = (SqliteTransaction)transaction;
            pruneAccess.CommandText =
                """
                DELETE FROM ContentAccessStats
                WHERE ScopeKey = $scopeKey
                  AND RouteId = $routeId
                  AND NOT EXISTS (
                      SELECT 1
                      FROM IndexedContent AS i
                      WHERE i.ScopeKey = ContentAccessStats.ScopeKey
                        AND i.RouteId = ContentAccessStats.RouteId
                        AND i.PathKey = ContentAccessStats.PathKey
                  );
                """;
            pruneAccess.Parameters.AddWithValue("$scopeKey", scopeKey);
            pruneAccess.Parameters.AddWithValue("$routeId", routeId.ToString());
            await pruneAccess.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContentSearchResult>> SearchAsync(
        string scopeKey,
        string query,
        int limit = 50,
        bool? isDirectory = null,
        Guid? routeId = null,
        CancellationToken cancellationToken = default)
    {
        scopeKey = ValidateScopeKey(scopeKey);
        query ??= string.Empty;
        var effectiveLimit = NormalizeLimit(limit);
        var normalizedQuery = NormalizeSearchText(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return await GetMostAccessedAsync(
                    scopeKey,
                    effectiveLimit,
                    isDirectory,
                    routeId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var terms = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT i.RouteId, i.RouteDisplayName, i.RelativePath, i.Name,
                   i.IsDirectory, i.Length, i.ModifiedAt, i.RemoteLocator,
                   COALESCE(a.AccessCount, 0) AS AccessCount,
                   a.LastAccessedAt, a.LastAccessKind,
                   i.NormalizedName, i.NormalizedPath
            FROM IndexedContent AS i
            LEFT JOIN ContentAccessStats AS a
              ON a.ScopeKey = i.ScopeKey
             AND a.RouteId = i.RouteId
             AND a.PathKey = i.PathKey
            WHERE i.ScopeKey = $scopeKey
            """);

        command.Parameters.AddWithValue("$scopeKey", scopeKey);
        AddOptionalFilters(sql, command, isDirectory, routeId);
        for (var index = 0; index < terms.Length; index++)
        {
            var parameterName = $"$term{index}";
            sql.Append($" AND (i.NormalizedName LIKE {parameterName} ESCAPE '\\' OR i.NormalizedPath LIKE {parameterName} ESCAPE '\\')");
            command.Parameters.AddWithValue(parameterName, $"%{EscapeLikePattern(terms[index])}%");
        }

        sql.Append(
            """
             ORDER BY
                CASE
                    WHEN i.NormalizedName = $normalizedQuery THEN 0
                    WHEN i.NormalizedName LIKE $prefixQuery ESCAPE '\' THEN 1
                    WHEN i.NormalizedName LIKE $containsQuery ESCAPE '\' THEN 2
                    WHEN i.NormalizedPath LIKE $containsQuery ESCAPE '\' THEN 3
                    ELSE 4
                END,
                AccessCount DESC,
                a.LastAccessedAt DESC,
                i.NormalizedName,
                i.RouteId,
                i.PathKey
             LIMIT $candidateLimit;
            """);
        command.Parameters.AddWithValue("$normalizedQuery", normalizedQuery);
        command.Parameters.AddWithValue("$prefixQuery", $"{EscapeLikePattern(normalizedQuery)}%");
        command.Parameters.AddWithValue("$containsQuery", $"%{EscapeLikePattern(normalizedQuery)}%");
        command.Parameters.AddWithValue(
            "$candidateLimit",
            Math.Min(2000, Math.Max(200, effectiveLimit * 20)));
        command.CommandText = sql.ToString();

        var candidates = new List<SearchCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(ReadCandidate(reader));
        }

        var now = _timeProvider.GetUtcNow();
        return candidates
            .Select(candidate => candidate.Result with
            {
                Score = CalculateSearchScore(candidate, normalizedQuery, terms, now)
            })
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.IsDirectory)
            .ThenBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Name, StringComparer.Ordinal)
            .ThenBy(result => result.RouteDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.RelativePath, StringComparer.Ordinal)
            .Take(effectiveLimit)
            .ToArray();
    }

    public async Task<IReadOnlyList<ContentSearchResult>> GetMostAccessedAsync(
        string scopeKey,
        int limit = 20,
        bool? isDirectory = null,
        Guid? routeId = null,
        CancellationToken cancellationToken = default)
    {
        scopeKey = ValidateScopeKey(scopeKey);
        var effectiveLimit = NormalizeLimit(limit);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT i.RouteId, i.RouteDisplayName, i.RelativePath, i.Name,
                   i.IsDirectory, i.Length, i.ModifiedAt, i.RemoteLocator,
                   a.AccessCount, a.LastAccessedAt, a.LastAccessKind,
                   i.NormalizedName, i.NormalizedPath
            FROM ContentAccessStats AS a
            INNER JOIN IndexedContent AS i
              ON i.ScopeKey = a.ScopeKey
             AND i.RouteId = a.RouteId
             AND i.PathKey = a.PathKey
            WHERE a.ScopeKey = $scopeKey
              AND a.AccessCount > 0
            """);
        command.Parameters.AddWithValue("$scopeKey", scopeKey);
        AddOptionalFilters(sql, command, isDirectory, routeId);
        sql.Append(
            """
             ORDER BY a.AccessCount DESC,
                      a.LastAccessedAt DESC,
                      i.NormalizedName,
                      i.RouteId,
                      i.PathKey
             LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$limit", effectiveLimit);
        command.CommandText = sql.ToString();

        var now = _timeProvider.GetUtcNow();
        var results = new List<ContentSearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var candidate = ReadCandidate(reader);
            var score = (candidate.Result.AccessCount * 100d) +
                        CalculateRecencyBonus(candidate.Result.LastAccessedAt, now);
            results.Add(candidate.Result with { Score = score });
        }

        return results;
    }

    public async Task RecordAccessAsync(
        string scopeKey,
        Guid routeId,
        string relativePath,
        ContentAccessKind accessKind = ContentAccessKind.Unknown,
        CancellationToken cancellationToken = default)
    {
        scopeKey = ValidateScopeKey(scopeKey);
        ValidateRouteId(routeId);
        var normalizedPath = NormalizeRelativePath(relativePath);
        var pathKey = CreatePathKey(normalizedPath);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ContentAccessStats
                (ScopeKey, RouteId, PathKey, AccessCount, LastAccessedAt, LastAccessKind)
            VALUES
                ($scopeKey, $routeId, $pathKey, 1, $lastAccessedAt, $lastAccessKind)
            ON CONFLICT(ScopeKey, RouteId, PathKey) DO UPDATE SET
                AccessCount = CASE
                    WHEN ContentAccessStats.AccessCount = $maximumAccessCount
                        THEN ContentAccessStats.AccessCount
                    ELSE ContentAccessStats.AccessCount + 1
                END,
                LastAccessedAt = excluded.LastAccessedAt,
                LastAccessKind = excluded.LastAccessKind;
            """;
        command.Parameters.AddWithValue("$scopeKey", scopeKey);
        command.Parameters.AddWithValue("$routeId", routeId.ToString());
        command.Parameters.AddWithValue("$pathKey", pathKey);
        command.Parameters.AddWithValue("$lastAccessedAt", ToDatabaseDate(_timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue("$lastAccessKind", (int)accessKind);
        command.Parameters.AddWithValue("$maximumAccessCount", MaximumAccessCount);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveRouteAsync(
        string scopeKey,
        Guid routeId,
        CancellationToken cancellationToken = default)
    {
        scopeKey = ValidateScopeKey(scopeKey);
        ValidateRouteId(routeId);
        return DeleteAsync(scopeKey, routeId, cancellationToken);
    }

    public async Task RemoveRouteFromAllScopesAsync(
        Guid routeId,
        CancellationToken cancellationToken = default)
    {
        ValidateRouteId(routeId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            DELETE FROM ContentAccessStats WHERE RouteId = $routeId;
            DELETE FROM IndexedContent WHERE RouteId = $routeId;
            """;
        command.Parameters.AddWithValue("$routeId", routeId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ClearScopeAsync(string scopeKey, CancellationToken cancellationToken = default)
    {
        scopeKey = ValidateScopeKey(scopeKey);
        return DeleteAsync(scopeKey, routeId: null, cancellationToken);
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            DELETE FROM ContentAccessStats;
            DELETE FROM IndexedContent;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteAsync(
        string scopeKey,
        Guid? routeId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = routeId is null
            ?
            """
            DELETE FROM ContentAccessStats WHERE ScopeKey = $scopeKey;
            DELETE FROM IndexedContent WHERE ScopeKey = $scopeKey;
            """
            :
            """
            DELETE FROM ContentAccessStats WHERE ScopeKey = $scopeKey AND RouteId = $routeId;
            DELETE FROM IndexedContent WHERE ScopeKey = $scopeKey AND RouteId = $routeId;
            """;
        command.Parameters.AddWithValue("$scopeKey", scopeKey);
        if (routeId is not null)
        {
            command.Parameters.AddWithValue("$routeId", routeId.Value.ToString());
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await InitializeOwnedSchemaAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task InitializeOwnedSchemaAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_database.DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS IndexedContent (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ScopeKey TEXT NOT NULL,
                RouteId TEXT NOT NULL,
                RouteDisplayName TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                PathKey TEXT NOT NULL,
                NormalizedPath TEXT NOT NULL,
                Name TEXT NOT NULL,
                NormalizedName TEXT NOT NULL,
                IsDirectory INTEGER NOT NULL,
                Length INTEGER NOT NULL,
                ModifiedAt TEXT NULL,
                RemoteLocator TEXT NULL,
                IndexedAt TEXT NOT NULL,
                UNIQUE (ScopeKey, RouteId, PathKey)
            );

            CREATE INDEX IF NOT EXISTS IX_IndexedContent_Scope_Name
                ON IndexedContent (ScopeKey, NormalizedName);

            CREATE INDEX IF NOT EXISTS IX_IndexedContent_Scope_Path
                ON IndexedContent (ScopeKey, NormalizedPath);

            CREATE INDEX IF NOT EXISTS IX_IndexedContent_Scope_Route
                ON IndexedContent (ScopeKey, RouteId);

            CREATE TABLE IF NOT EXISTS ContentAccessStats (
                ScopeKey TEXT NOT NULL,
                RouteId TEXT NOT NULL,
                PathKey TEXT NOT NULL,
                AccessCount INTEGER NOT NULL,
                LastAccessedAt TEXT NOT NULL,
                LastAccessKind INTEGER NOT NULL,
                PRIMARY KEY (ScopeKey, RouteId, PathKey)
            );

            CREATE INDEX IF NOT EXISTS IX_ContentAccessStats_Scope_Rank
                ON ContentAccessStats (ScopeKey, AccessCount DESC, LastAccessedAt DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void AddOptionalFilters(
        StringBuilder sql,
        SqliteCommand command,
        bool? isDirectory,
        Guid? routeId)
    {
        if (isDirectory is not null)
        {
            sql.Append(" AND i.IsDirectory = $isDirectory");
            command.Parameters.AddWithValue("$isDirectory", isDirectory.Value ? 1 : 0);
        }

        if (routeId is not null)
        {
            ValidateRouteId(routeId.Value);
            sql.Append(" AND i.RouteId = $routeId");
            command.Parameters.AddWithValue("$routeId", routeId.Value.ToString());
        }
    }

    private static SearchCandidate ReadCandidate(SqliteDataReader reader)
    {
        var lastAccessKind = reader.IsDBNull(10)
            ? (ContentAccessKind?)null
            : (ContentAccessKind)reader.GetInt32(10);
        var result = new ContentSearchResult(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4) == 1,
            reader.GetInt64(5),
            ReadDatabaseDate(reader, 6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt64(8),
            ReadDatabaseDate(reader, 9),
            lastAccessKind,
            Score: 0);
        return new SearchCandidate(result, reader.GetString(11), reader.GetString(12));
    }

    private static double CalculateSearchScore(
        SearchCandidate candidate,
        string normalizedQuery,
        IReadOnlyList<string> terms,
        DateTimeOffset now)
    {
        var name = candidate.NormalizedName;
        var path = candidate.NormalizedPath;
        double textScore;
        if (string.Equals(name, normalizedQuery, StringComparison.Ordinal))
        {
            textScore = 1000;
        }
        else if (name.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            textScore = 800;
        }
        else if (name.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            textScore = 650;
        }
        else if (path.EndsWith($"/{normalizedQuery}", StringComparison.Ordinal))
        {
            textScore = 550;
        }
        else
        {
            textScore = 450;
        }

        foreach (var term in terms)
        {
            if (string.Equals(name, term, StringComparison.Ordinal))
            {
                textScore += 30;
            }
            else if (name.StartsWith(term, StringComparison.Ordinal))
            {
                textScore += 20;
            }
            else if (name.Contains(term, StringComparison.Ordinal))
            {
                textScore += 12;
            }
            else if (path.Contains(term, StringComparison.Ordinal))
            {
                textScore += 6;
            }
        }

        var accessBonus = Math.Min(
            150,
            Math.Log2(Math.Max(0, candidate.Result.AccessCount) + 1d) * 25d);
        return textScore + accessBonus +
               CalculateRecencyBonus(candidate.Result.LastAccessedAt, now);
    }

    private static double CalculateRecencyBonus(DateTimeOffset? lastAccessedAt, DateTimeOffset now)
    {
        if (lastAccessedAt is null)
        {
            return 0;
        }

        var age = now - lastAccessedAt.Value;
        var ageDays = Math.Max(0, age.TotalDays);
        return Math.Max(0, 30 - ageDays);
    }

    private static NormalizedIndexItem NormalizeItem(ContentIndexItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateRouteId(item.RouteId);
        if (string.IsNullOrWhiteSpace(item.RouteDisplayName))
        {
            throw new ArgumentException("Route display name is required.", nameof(item));
        }

        if (string.IsNullOrWhiteSpace(item.Name))
        {
            throw new ArgumentException("Item name is required.", nameof(item));
        }

        if (item.Length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(item), "Item length cannot be negative.");
        }

        var relativePath = NormalizeRelativePath(item.RelativePath);
        var normalizedName = NormalizeSearchText(item.Name);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("Item name must contain searchable characters.", nameof(item));
        }

        return new NormalizedIndexItem(
            item,
            relativePath,
            CreatePathKey(relativePath),
            NormalizeSearchText(relativePath),
            normalizedName);
    }

    private static string ValidateScopeKey(string scopeKey)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
        {
            throw new ArgumentException("Scope key is required.", nameof(scopeKey));
        }

        var normalized = scopeKey.Trim();
        if (normalized.Length > 1024 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Scope key is invalid.", nameof(scopeKey));
        }

        return normalized;
    }

    private static void ValidateRouteId(Guid routeId)
    {
        if (routeId == Guid.Empty)
        {
            throw new ArgumentException("Route id is required.", nameof(routeId));
        }
    }

    private static int NormalizeLimit(int limit)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");
        }

        return Math.Min(limit, MaximumSearchResults);
    }

    private static string NormalizeRelativePath(string? relativePath)
    {
        var segments = (relativePath ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.Any(char.IsControl))
            {
                throw new ArgumentException("Relative path contains an invalid segment.", nameof(relativePath));
            }
        }

        return string.Join('/', segments).Normalize(NormalizationForm.FormC);
    }

    private static string CreatePathKey(string relativePath) =>
        relativePath.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    private static string NormalizeSearchText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            builder.Append(character);
        }

        var normalized = builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        return string.Join(
            ' ',
            normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string ToDatabaseDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadDatabaseDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    private sealed record NormalizedIndexItem(
        ContentIndexItem Item,
        string RelativePath,
        string PathKey,
        string NormalizedPath,
        string NormalizedName);

    private sealed record SearchCandidate(
        ContentSearchResult Result,
        string NormalizedName,
        string NormalizedPath);
}
