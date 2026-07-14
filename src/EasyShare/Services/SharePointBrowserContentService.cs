using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EasyShare.Models;

namespace EasyShare.Services;

public enum UploadAttemptState
{
    Succeeded,
    RetryableFailure,
    Conflict
}

public sealed record UploadAttemptResult(UploadAttemptState State, string? Error = null);

public interface ISharePointContentTransfer
{
    Task<bool> DownloadFileAsync(
        DriveRoute route,
        string relativePath,
        Stream destination,
        CancellationToken cancellationToken = default);

    Task<UploadAttemptResult> TryUploadFileAsync(
        DriveRoute route,
        string relativePath,
        Stream content,
        DateTimeOffset? expectedModifiedAt,
        CancellationToken cancellationToken = default);
}

public sealed class SharePointBrowserContentService : ISharePointContentTransfer
{
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(20);
    private static readonly IRemoteHttpTransport SharedHttpTransport = new RemoteHttpTransport();
    private const int MaximumRemoteFileCacheBytes = 16 * 1024 * 1024;
    private readonly LocalDatabase _database;
    private readonly IRemoteHttpTransport _httpTransport;
    private readonly ConcurrentDictionary<string, CacheEntry> _directoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _fileCache = new(StringComparer.OrdinalIgnoreCase);
    private TimeSpan _cacheTtl = DefaultCacheTtl;
    private OfflineCacheService? _offlineCache;
    private GraphSharePointContentService? _graphContent;
    private EnterprisePolicy _enterprisePolicy = new();

    public SharePointBrowserContentService(LocalDatabase database)
        : this(database, SharedHttpTransport)
    {
    }

    public SharePointBrowserContentService(LocalDatabase database, IRemoteHttpTransport httpTransport)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _httpTransport = httpTransport ?? throw new ArgumentNullException(nameof(httpTransport));
    }

    public void ConfigureCache(TimeSpan ttl) => _cacheTtl = ttl <= TimeSpan.Zero ? DefaultCacheTtl : ttl;

    public void ConfigureOfflineCache(OfflineCacheService offlineCache) =>
        _offlineCache = offlineCache ?? throw new ArgumentNullException(nameof(offlineCache));

    internal void ConfigureGraphContent(GraphSharePointContentService graphContent) =>
        _graphContent = graphContent ?? throw new ArgumentNullException(nameof(graphContent));

    public void ConfigureEnterprisePolicy(EnterprisePolicy policy) =>
        _enterprisePolicy = policy ?? throw new ArgumentNullException(nameof(policy));

    public IReadOnlyList<SharePointDriveItem> ListDirectory(DriveRoute route, string relativePath) =>
        ListDirectoryAsync(route, relativePath).GetAwaiter().GetResult();

    public async Task<IReadOnlyList<SharePointDriveItem>> ListDirectoryAsync(
        DriveRoute route,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var routeInfo = SharePointRouteInfo.FromRoute(route);
        if (routeInfo is null || !IsHostAllowed(routeInfo.SiteUri))
        {
            return [];
        }

        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        var cacheKey = $"{route.Id:N}:{normalizedRelativePath}";
        if (_directoryCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.CreatedAt < _cacheTtl)
        {
            return cached.Items;
        }

        var persisted = _database.TryGetDirectoryCache(route.Id, normalizedRelativePath, _cacheTtl);
        if (persisted is not null)
        {
            _directoryCache[cacheKey] = new CacheEntry(persisted.CachedAt, persisted.Items);
            return persisted.Items;
        }

        if (route.HasGraphIdentity && _graphContent is not null)
        {
            try
            {
                var items = await _graphContent
                    .ListDirectoryAsync(route, normalizedRelativePath, cancellationToken)
                    .ConfigureAwait(false);
                _directoryCache[cacheKey] = new CacheEntry(DateTimeOffset.UtcNow, items);
                _database.SaveDirectoryCache(route.Id, normalizedRelativePath, items);
                return MergeOfflineItems(route, normalizedRelativePath, items);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return MergeOfflineItems(route, normalizedRelativePath, cached?.Items ?? []);
            }
        }

        if (!SharePointCookieStore.TryGetCookieHeader(routeInfo.SiteUri, out var cookieHeader))
        {
            return MergeOfflineItems(route, normalizedRelativePath, cached?.Items ?? []);
        }

        try
        {
            var folderPath = routeInfo.BuildServerRelativePath(relativePath);
            var foldersTask = GetItemsAsync(
                routeInfo,
                folderPath,
                cookieHeader,
                isFolderRequest: true,
                cancellationToken);
            var filesTask = GetItemsAsync(
                routeInfo,
                folderPath,
                cookieHeader,
                isFolderRequest: false,
                cancellationToken);
            await Task.WhenAll(foldersTask, filesTask).ConfigureAwait(false);

            var folders = await foldersTask.ConfigureAwait(false);
            var files = await filesTask.ConfigureAwait(false);
            var items = folders
                .Concat(files)
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _directoryCache[cacheKey] = new CacheEntry(DateTimeOffset.UtcNow, items);
            _database.SaveDirectoryCache(route.Id, normalizedRelativePath, items);
            return items;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return MergeOfflineItems(route, normalizedRelativePath, cached?.Items ?? []);
        }
    }

    public SharePointDriveItem? GetItem(DriveRoute route, string relativePath) =>
        GetItemAsync(route, relativePath).GetAwaiter().GetResult();

    public async Task<SharePointDriveItem?> GetItemAsync(
        DriveRoute route,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var parentPath = GetParentPath(normalized);
        var name = GetFileName(normalized);
        var items = await ListDirectoryAsync(route, parentPath, cancellationToken).ConfigureAwait(false);
        return items
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public byte[] ReadFile(DriveRoute route, string relativePath) =>
        ReadFileAsync(route, relativePath).GetAwaiter().GetResult();

    public async Task<byte[]> ReadFileAsync(
        DriveRoute route,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var cacheKey = $"{route.Id:N}:{normalized}";
        if (_fileCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            using var destination = new MemoryStream();
            if (!await DownloadFileAsync(route, normalized, destination, cancellationToken).ConfigureAwait(false))
            {
                return await ReadOfflineAsync(route, normalized, cancellationToken).ConfigureAwait(false);
            }

            var bytes = destination.ToArray();
            if (bytes.Length <= MaximumRemoteFileCacheBytes)
            {
                _fileCache[cacheKey] = bytes;
            }

            return bytes;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await ReadOfflineAsync(route, normalized, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<byte[]> ReadOfflineAsync(
        DriveRoute route,
        string relativePath,
        CancellationToken cancellationToken) =>
        _offlineCache is null
            ? []
            : await _offlineCache.TryReadAllBytesAsync(route.Id, relativePath, cancellationToken)
                .ConfigureAwait(false) ?? [];

    private IReadOnlyList<SharePointDriveItem> MergeOfflineItems(
        DriveRoute route,
        string relativePath,
        IReadOnlyList<SharePointDriveItem> onlineOrPersisted)
    {
        if (_offlineCache is null)
        {
            return onlineOrPersisted;
        }

        var merged = onlineOrPersisted.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _offlineCache.GetDirectoryItems(route, relativePath))
        {
            merged.TryAdd(item.Name, item);
        }

        return merged.Values
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<bool> DownloadFileAsync(
        DriveRoute route,
        string relativePath,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var routeInfo = SharePointRouteInfo.FromRoute(route);
        if (routeInfo is null || !IsHostAllowed(routeInfo.SiteUri))
        {
            return false;
        }

        var normalized = NormalizeRelativePath(relativePath);
        var cacheKey = $"{route.Id:N}:{normalized}";
        if (_fileCache.TryGetValue(cacheKey, out var cached))
        {
            await destination.WriteAsync(cached, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (route.HasGraphIdentity && _graphContent is not null)
        {
            return await _graphContent
                .DownloadFileAsync(route, normalized, destination, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!SharePointCookieStore.TryGetCookieHeader(routeInfo.SiteUri, out var cookieHeader))
        {
            return false;
        }

        try
        {
            var serverRelativePath = routeInfo.BuildServerRelativePath(normalized);
            var candidates = new[]
            {
                BuildFileValueUrl(routeInfo, serverRelativePath),
                BuildLegacyFileValueUrl(routeInfo, serverRelativePath),
                BuildDirectFileUrl(routeInfo, serverRelativePath)
            };

            foreach (var candidate in candidates)
            {
                var result = await TryDownloadFileAsync(candidate, cookieHeader, destination, cancellationToken)
                    .ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    return true;
                }

                if (!ShouldTryLegacyFallback(result.StatusCode))
                {
                    return false;
                }
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<HttpOperationResult> TryDownloadFileAsync(
        Uri uri,
        string cookieHeader,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var canReplay = TryPrepareDestinationForReplay(destination, out var destinationStart);
        var requestCount = 0;
        try
        {
            return await _httpTransport.SendAsync(
                uri,
                requestUri =>
                {
                    if (Interlocked.Increment(ref requestCount) > 1 && canReplay)
                    {
                        destination.Position = destinationStart;
                        destination.SetLength(destinationStart);
                    }

                    var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    ApplyHeaders(request, cookieHeader);
                    return request;
                },
                HttpCompletionOption.ResponseHeadersRead,
                retryable: canReplay,
                async (response, operationToken) =>
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return new HttpOperationResult(false, response.StatusCode);
                    }

                    await using var source = await response.Content
                        .ReadAsStreamAsync(operationToken)
                        .ConfigureAwait(false);
                    await source
                        .CopyToAsync(destination, 81_920, operationToken)
                        .ConfigureAwait(false);
                    return new HttpOperationResult(true, response.StatusCode);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (canReplay)
            {
                destination.Position = destinationStart;
                destination.SetLength(destinationStart);
            }

            throw;
        }
    }

    private static bool TryPrepareDestinationForReplay(Stream destination, out long initialPosition)
    {
        initialPosition = 0;
        if (!destination.CanSeek)
        {
            return false;
        }

        try
        {
            initialPosition = destination.Position;
            destination.SetLength(initialPosition);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public bool CreateFolder(DriveRoute route, string relativePath) =>
        CreateFolderAsync(route, relativePath).GetAwaiter().GetResult();

    public async Task<bool> CreateFolderAsync(
        DriveRoute route,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var routeInfo = SharePointRouteInfo.FromRoute(route);
        if (routeInfo is null || !IsHostAllowed(routeInfo.SiteUri))
        {
            return false;
        }

        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (route.HasGraphIdentity && _graphContent is not null)
        {
            var created = await _graphContent
                .CreateFolderAsync(route, normalized, cancellationToken)
                .ConfigureAwait(false);
            if (created)
            {
                InvalidateDirectory(route, GetParentPath(normalized));
            }

            return created;
        }

        if (!SharePointCookieStore.TryGetCookieHeader(routeInfo.SiteUri, out var cookieHeader))
        {
            return false;
        }

        try
        {
            var digest = await GetRequestDigestAsync(routeInfo, cookieHeader, cancellationToken).ConfigureAwait(false);
            var folderPath = routeInfo.BuildServerRelativePath(normalized);
            var result = await SendForStatusAsync(
                BuildCreateFolderUrl(routeInfo, folderPath),
                requestUri =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
                    ApplyWriteHeaders(request, cookieHeader, digest);
                    request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
                    return request;
                },
                retryable: false,
                cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess && ShouldTryLegacyFallback(result.StatusCode))
            {
                result = await SendForStatusAsync(
                    BuildLegacyCreateFolderUrl(routeInfo),
                    requestUri =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
                        ApplyWriteHeaders(request, cookieHeader, digest);
                        request.Content = new StringContent(
                            JsonSerializer.Serialize(new
                            {
                                __metadata = new { type = "SP.Folder" },
                                ServerRelativeUrl = folderPath
                            }),
                            Encoding.UTF8,
                            "application/json");
                        return request;
                    },
                    retryable: false,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!result.IsSuccess)
            {
                return false;
            }

            InvalidateDirectory(route, GetParentPath(normalized));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public bool UploadFile(DriveRoute route, string relativePath, byte[] bytes) =>
        TryUploadFile(route, relativePath, bytes, expectedModifiedAt: null).State == UploadAttemptState.Succeeded;

    public UploadAttemptResult TryUploadFile(
        DriveRoute route,
        string relativePath,
        byte[] bytes,
        DateTimeOffset? expectedModifiedAt)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var content = new MemoryStream(bytes, writable: false);
        var result = TryUploadFileAsync(route, relativePath, content, expectedModifiedAt)
            .GetAwaiter()
            .GetResult();
        if (result.State == UploadAttemptState.Succeeded)
        {
            CacheLocalFile(route, relativePath, bytes);
        }

        return result;
    }

    public async Task<UploadAttemptResult> TryUploadFileAsync(
        DriveRoute route,
        string relativePath,
        Stream content,
        DateTimeOffset? expectedModifiedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The upload stream must be readable.", nameof(content));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var routeInfo = SharePointRouteInfo.FromRoute(route);
        if (routeInfo is null || !IsHostAllowed(routeInfo.SiteUri))
        {
            return new UploadAttemptResult(UploadAttemptState.RetryableFailure, "Sessão do SharePoint indisponível.");
        }

        var normalized = NormalizeRelativePath(relativePath);
        var parentPath = GetParentPath(normalized);
        var fileName = GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new UploadAttemptResult(UploadAttemptState.RetryableFailure, "Caminho de arquivo inválido.");
        }

        if (route.HasGraphIdentity && _graphContent is not null)
        {
            var graphResult = await _graphContent
                .TryUploadFileAsync(route, normalized, content, expectedModifiedAt, cancellationToken)
                .ConfigureAwait(false);
            if (graphResult.State == UploadAttemptState.Succeeded)
            {
                InvalidateDirectory(route, parentPath);
            }

            return graphResult;
        }

        if (!SharePointCookieStore.TryGetCookieHeader(routeInfo.SiteUri, out var cookieHeader))
        {
            return new UploadAttemptResult(UploadAttemptState.RetryableFailure, "Sessão do SharePoint indisponível.");
        }

        try
        {
            if (expectedModifiedAt is not null)
            {
                InvalidateDirectory(route, parentPath);
                var current = await GetItemAsync(route, normalized, cancellationToken).ConfigureAwait(false);
                if (current is null || Math.Abs((current.ModifiedAt - expectedModifiedAt.Value).TotalSeconds) > 2)
                {
                    return new UploadAttemptResult(
                        UploadAttemptState.Conflict,
                        "O arquivo remoto mudou enquanto o arquivo local estava sendo editado.");
                }
            }

            var digest = await GetRequestDigestAsync(routeInfo, cookieHeader, cancellationToken).ConfigureAwait(false);
            var folderPath = routeInfo.BuildServerRelativePath(parentPath);
            var contentLength = content.CanSeek ? content.Length - content.Position : (long?)null;
            var contentStart = content.CanSeek ? content.Position : 0;
            var result = await UploadToEndpointAsync(
                BuildUploadFileUrl(routeInfo, folderPath, fileName),
                cookieHeader,
                digest,
                content,
                contentStart,
                contentLength,
                cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess && content.CanSeek && ShouldTryLegacyFallback(result.StatusCode))
            {
                result = await UploadToEndpointAsync(
                    BuildLegacyUploadFileUrl(routeInfo, folderPath, fileName),
                    cookieHeader,
                    digest,
                    content,
                    contentStart,
                    contentLength,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!result.IsSuccess)
            {
                return new UploadAttemptResult(
                    UploadAttemptState.RetryableFailure,
                    $"SharePoint retornou {(int)result.StatusCode}.");
            }

            InvalidateDirectory(route, parentPath);
            return new UploadAttemptResult(UploadAttemptState.Succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new UploadAttemptResult(UploadAttemptState.RetryableFailure, "Não foi possível enviar o arquivo agora.");
        }
    }

    private Task<HttpOperationResult> UploadToEndpointAsync(
        Uri uri,
        string cookieHeader,
        string digest,
        Stream content,
        long contentStart,
        long? contentLength,
        CancellationToken cancellationToken)
    {
        var usedNonSeekableStream = 0;
        return _httpTransport.SendAsync(
            uri,
            requestUri =>
            {
                if (content.CanSeek)
                {
                    content.Position = contentStart;
                }
                else if (Interlocked.Exchange(ref usedNonSeekableStream, 1) != 0)
                {
                    throw new InvalidOperationException("A non-seekable upload stream cannot be replayed.");
                }

                var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
                ApplyWriteHeaders(request, cookieHeader, digest);
                request.Content = new StreamContent(new NonDisposingReadStream(content), 81_920);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Content.Headers.ContentLength = contentLength;
                return request;
            },
            HttpCompletionOption.ResponseHeadersRead,
            retryable: content.CanSeek,
            (response, _) => Task.FromResult(new HttpOperationResult(response.IsSuccessStatusCode, response.StatusCode)),
            cancellationToken);
    }

    public void CacheLocalFile(DriveRoute route, string relativePath, byte[] bytes) =>
        _fileCache[$"{route.Id:N}:{NormalizeRelativePath(relativePath)}"] = bytes.ToArray();

    public bool DeleteItem(DriveRoute route, string relativePath, bool isDirectory) =>
        DeleteItemAsync(route, relativePath, isDirectory).GetAwaiter().GetResult();

    public async Task<bool> DeleteItemAsync(
        DriveRoute route,
        string relativePath,
        bool isDirectory,
        CancellationToken cancellationToken = default)
    {
        var routeInfo = SharePointRouteInfo.FromRoute(route);
        if (routeInfo is null || !IsHostAllowed(routeInfo.SiteUri))
        {
            return false;
        }

        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (route.HasGraphIdentity && _graphContent is not null)
        {
            var deleted = await _graphContent
                .DeleteItemAsync(route, normalized, isDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (deleted)
            {
                _fileCache.TryRemove($"{route.Id:N}:{normalized}", out _);
                InvalidateDirectory(route, GetParentPath(normalized));
            }

            return deleted;
        }

        if (!SharePointCookieStore.TryGetCookieHeader(routeInfo.SiteUri, out var cookieHeader))
        {
            return false;
        }

        try
        {
            var digest = await GetRequestDigestAsync(routeInfo, cookieHeader, cancellationToken).ConfigureAwait(false);
            var result = await SendForStatusAsync(
                BuildDeleteItemUrl(routeInfo, routeInfo.BuildServerRelativePath(normalized), isDirectory),
                requestUri =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
                    ApplyWriteHeaders(request, cookieHeader, digest);
                    request.Headers.TryAddWithoutValidation("IF-MATCH", "*");
                    request.Headers.TryAddWithoutValidation("X-HTTP-Method", "DELETE");
                    request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
                    return request;
                },
                retryable: false,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return false;
            }

            _fileCache.TryRemove($"{route.Id:N}:{normalized}", out _);
            InvalidateDirectory(route, GetParentPath(normalized));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public bool RenameItem(
        DriveRoute route,
        string oldRelativePath,
        string newRelativePath,
        bool isDirectory,
        bool replaceIfExists)
        => RenameItemAsync(
                route,
                oldRelativePath,
                newRelativePath,
                isDirectory,
                replaceIfExists)
            .GetAwaiter()
            .GetResult();

    public async Task<bool> RenameItemAsync(
        DriveRoute route,
        string oldRelativePath,
        string newRelativePath,
        bool isDirectory,
        bool replaceIfExists,
        CancellationToken cancellationToken = default)
    {
        var routeInfo = SharePointRouteInfo.FromRoute(route);
        if (routeInfo is null || !IsHostAllowed(routeInfo.SiteUri))
        {
            return false;
        }

        var oldNormalized = NormalizeRelativePath(oldRelativePath);
        var newNormalized = NormalizeRelativePath(newRelativePath);
        if (string.IsNullOrWhiteSpace(oldNormalized) || string.IsNullOrWhiteSpace(newNormalized))
        {
            return false;
        }

        if (route.HasGraphIdentity && _graphContent is not null)
        {
            var renamed = await _graphContent
                .RenameItemAsync(
                    route,
                    oldNormalized,
                    newNormalized,
                    isDirectory,
                    replaceIfExists,
                    cancellationToken)
                .ConfigureAwait(false);
            if (renamed)
            {
                _fileCache.TryRemove($"{route.Id:N}:{oldNormalized}", out _);
                _fileCache.TryRemove($"{route.Id:N}:{newNormalized}", out _);
                InvalidateDirectory(route, GetParentPath(oldNormalized));
                InvalidateDirectory(route, GetParentPath(newNormalized));
            }

            return renamed;
        }

        if (!SharePointCookieStore.TryGetCookieHeader(routeInfo.SiteUri, out var cookieHeader))
        {
            return false;
        }

        try
        {
            var digest = await GetRequestDigestAsync(routeInfo, cookieHeader, cancellationToken).ConfigureAwait(false);
            var oldServerRelativePath = routeInfo.BuildServerRelativePath(oldNormalized);
            var newServerRelativePath = routeInfo.BuildServerRelativePath(newNormalized);

            var result = await SendForStatusAsync(
                BuildMoveItemUrl(routeInfo, oldServerRelativePath, newServerRelativePath, isDirectory, replaceIfExists),
                requestUri =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
                    ApplyWriteHeaders(request, cookieHeader, digest);
                    request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
                    return request;
                },
                retryable: false,
                cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess && ShouldTryLegacyFallback(result.StatusCode))
            {
                result = await SendForStatusAsync(
                    BuildLegacyMoveItemUrl(routeInfo, oldServerRelativePath, newServerRelativePath, isDirectory, replaceIfExists),
                    requestUri =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
                        ApplyWriteHeaders(request, cookieHeader, digest);
                        request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
                        return request;
                    },
                    retryable: false,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!result.IsSuccess)
            {
                return false;
            }

            _fileCache.TryRemove($"{route.Id:N}:{oldNormalized}", out _);
            _fileCache.TryRemove($"{route.Id:N}:{newNormalized}", out _);
            InvalidateDirectory(route, GetParentPath(oldNormalized));
            InvalidateDirectory(route, GetParentPath(newNormalized));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public void InvalidateRoute(DriveRoute route)
    {
        var prefix = $"{route.Id:N}:";
        foreach (var key in _directoryCache.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            _directoryCache.TryRemove(key, out _);
        }

        foreach (var key in _fileCache.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            _fileCache.TryRemove(key, out _);
        }

        _database.InvalidateRouteDirectoryCache(route.Id);
    }

    public void ClearCache()
    {
        _directoryCache.Clear();
        _fileCache.Clear();
        _database.ClearDirectoryCache();
    }

    private Task<IReadOnlyList<SharePointDriveItem>> GetItemsAsync(
        SharePointRouteInfo routeInfo,
        string serverRelativePath,
        string cookieHeader,
        bool isFolderRequest,
        CancellationToken cancellationToken)
    {
        var uri = isFolderRequest
            ? BuildFoldersUrl(routeInfo, serverRelativePath)
            : BuildFilesUrl(routeInfo, serverRelativePath);
        return _httpTransport.SendAsync<IReadOnlyList<SharePointDriveItem>>(
            uri,
            requestUri =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                ApplyHeaders(request, cookieHeader);
                return request;
            },
            HttpCompletionOption.ResponseHeadersRead,
            retryable: true,
            async (response, operationToken) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    return [];
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(operationToken)
                    .ConfigureAwait(false);
                using var document = await JsonDocument
                    .ParseAsync(stream, cancellationToken: operationToken)
                    .ConfigureAwait(false);
                return ParseItems(document.RootElement, isFolderRequest);
            },
            cancellationToken);
    }

    private static IReadOnlyList<SharePointDriveItem> ParseItems(JsonElement root, bool isDirectory)
    {
        var values = EnumerateODataValues(root);
        var items = new List<SharePointDriveItem>();

        foreach (var item in values)
        {
            var name = GetString(item, "Name");
            if (string.IsNullOrWhiteSpace(name) ||
                string.Equals(name, "Forms", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var serverRelativeUrl = GetString(item, "ServerRelativeUrl");
            var modified = ParseDate(GetString(item, "TimeLastModified"));
            var length = isDirectory ? 0 : GetLong(item, "Length");
            items.Add(new SharePointDriveItem(name, serverRelativeUrl, isDirectory, length, modified));
        }

        return items;
    }

    private static IEnumerable<JsonElement> EnumerateODataValues(JsonElement root)
    {
        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray();
        }

        if (root.TryGetProperty("d", out var d))
        {
            if (d.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                return results.EnumerateArray();
            }

            if (d.TryGetProperty("Folders", out var folders) &&
                folders.TryGetProperty("results", out var folderResults) &&
                folderResults.ValueKind == JsonValueKind.Array)
            {
                return folderResults.EnumerateArray();
            }

            if (d.TryGetProperty("Files", out var files) &&
                files.TryGetProperty("results", out var fileResults) &&
                fileResults.ValueKind == JsonValueKind.Array)
            {
                return fileResults.EnumerateArray();
            }
        }

        return [];
    }

    private Task<HttpOperationResult> SendForStatusAsync(
        Uri uri,
        Func<Uri, HttpRequestMessage> requestFactory,
        bool retryable,
        CancellationToken cancellationToken) =>
        _httpTransport.SendAsync(
            uri,
            requestFactory,
            HttpCompletionOption.ResponseHeadersRead,
            retryable,
            (response, _) => Task.FromResult(new HttpOperationResult(response.IsSuccessStatusCode, response.StatusCode)),
            cancellationToken);

    private static bool ShouldTryLegacyFallback(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest or
            HttpStatusCode.NotFound or
            HttpStatusCode.MethodNotAllowed or
            HttpStatusCode.UnsupportedMediaType or
            HttpStatusCode.NotImplemented;

    private static void ApplyHeaders(HttpRequestMessage request, string cookieHeader)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", "EasyShare");
    }

    private static void ApplyWriteHeaders(HttpRequestMessage request, string cookieHeader, string digest)
    {
        ApplyHeaders(request, cookieHeader);
        request.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
    }

    private Task<string> GetRequestDigestAsync(
        SharePointRouteInfo routeInfo,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"{routeInfo.SiteRoot}/_api/contextinfo");
        return _httpTransport.SendAsync(
            uri,
            requestUri =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
                ApplyHeaders(request, cookieHeader);
                request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
                return request;
            },
            HttpCompletionOption.ResponseHeadersRead,
            retryable: true,
            async (response, operationToken) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    return string.Empty;
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(operationToken)
                    .ConfigureAwait(false);
                using var document = await JsonDocument
                    .ParseAsync(stream, cancellationToken: operationToken)
                    .ConfigureAwait(false);
                return ExtractFormDigest(document.RootElement);
            },
            cancellationToken);
    }

    private static string ExtractFormDigest(JsonElement root)
    {
        if (root.TryGetProperty("FormDigestValue", out var direct) && direct.ValueKind == JsonValueKind.String)
        {
            return direct.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("d", out var d))
        {
            if (d.TryGetProperty("GetContextWebInformation", out var info) &&
                info.TryGetProperty("FormDigestValue", out var digest) &&
                digest.ValueKind == JsonValueKind.String)
            {
                return digest.GetString() ?? string.Empty;
            }

            if (d.TryGetProperty("FormDigestValue", out var digestValue) &&
                digestValue.ValueKind == JsonValueKind.String)
            {
                return digestValue.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static Uri BuildFoldersUrl(SharePointRouteInfo routeInfo, string serverRelativePath) =>
        BuildFolderCollectionUrl(routeInfo, serverRelativePath, "Folders", "$select=Name,ServerRelativeUrl,TimeLastModified,ItemCount");

    private static Uri BuildFilesUrl(SharePointRouteInfo routeInfo, string serverRelativePath) =>
        BuildFolderCollectionUrl(routeInfo, serverRelativePath, "Files", "$select=Name,ServerRelativeUrl,TimeLastModified,Length");

    private static Uri BuildFolderCollectionUrl(SharePointRouteInfo routeInfo, string serverRelativePath, string collection, string query)
    {
        var alias = Uri.EscapeDataString(ToODataStringLiteral(serverRelativePath));
        return new Uri($"{routeInfo.SiteRoot}/_api/web/GetFolderByServerRelativePath(decodedurl=@p)/{collection}?{query}&@p={alias}");
    }

    private static Uri BuildFileValueUrl(SharePointRouteInfo routeInfo, string serverRelativePath)
    {
        var alias = Uri.EscapeDataString(ToODataStringLiteral(serverRelativePath));
        return new Uri($"{routeInfo.SiteRoot}/_api/web/GetFileByServerRelativePath(decodedurl=@p)/$value?@p={alias}");
    }

    private static Uri BuildLegacyFileValueUrl(SharePointRouteInfo routeInfo, string serverRelativePath) =>
        new($"{routeInfo.SiteRoot}/_api/web/GetFileByServerRelativeUrl({ToODataStringLiteral(serverRelativePath)})/$value");

    private static Uri BuildDirectFileUrl(SharePointRouteInfo routeInfo, string serverRelativePath) =>
        new($"{routeInfo.SiteUri.Scheme}://{routeInfo.SiteUri.Host}{EscapePath(serverRelativePath)}");

    private static Uri BuildCreateFolderUrl(SharePointRouteInfo routeInfo, string serverRelativePath)
    {
        var alias = Uri.EscapeDataString(ToODataStringLiteral(serverRelativePath));
        return new Uri($"{routeInfo.SiteRoot}/_api/web/Folders/AddUsingPath(decodedurl=@p)?@p={alias}");
    }

    private static Uri BuildLegacyCreateFolderUrl(SharePointRouteInfo routeInfo) =>
        new($"{routeInfo.SiteRoot}/_api/web/folders");

    private static Uri BuildUploadFileUrl(SharePointRouteInfo routeInfo, string folderPath, string fileName)
    {
        var folderAlias = Uri.EscapeDataString(ToODataStringLiteral(folderPath));
        var fileAlias = Uri.EscapeDataString(ToODataStringLiteral(fileName));
        return new Uri($"{routeInfo.SiteRoot}/_api/web/GetFolderByServerRelativePath(decodedurl=@p)/Files/AddUsingPath(decodedurl=@n,overwrite=true)?@p={folderAlias}&@n={fileAlias}");
    }

    private static Uri BuildLegacyUploadFileUrl(SharePointRouteInfo routeInfo, string folderPath, string fileName) =>
        new($"{routeInfo.SiteRoot}/_api/web/GetFolderByServerRelativeUrl({ToODataStringLiteral(folderPath)})/Files/add(overwrite=true,url={ToODataStringLiteral(fileName)})");

    private static Uri BuildDeleteItemUrl(SharePointRouteInfo routeInfo, string serverRelativePath, bool isDirectory)
    {
        var alias = Uri.EscapeDataString(ToODataStringLiteral(serverRelativePath));
        var target = isDirectory ? "GetFolderByServerRelativePath" : "GetFileByServerRelativePath";
        return new Uri($"{routeInfo.SiteRoot}/_api/web/{target}(decodedurl=@p)?@p={alias}");
    }

    private static Uri BuildMoveItemUrl(
        SharePointRouteInfo routeInfo,
        string oldServerRelativePath,
        string newServerRelativePath,
        bool isDirectory,
        bool replaceIfExists)
    {
        var oldAlias = Uri.EscapeDataString(ToODataStringLiteral(oldServerRelativePath));
        var newAlias = Uri.EscapeDataString(ToODataStringLiteral(newServerRelativePath));
        var target = isDirectory ? "GetFolderByServerRelativePath" : "GetFileByServerRelativePath";
        var flags = replaceIfExists ? 1 : 0;
        var moveCall = isDirectory ? "MoveTo(newurl=@d)" : $"MoveTo(newurl=@d,flags={flags})";
        return new Uri($"{routeInfo.SiteRoot}/_api/web/{target}(decodedurl=@s)/{moveCall}?@s={oldAlias}&@d={newAlias}");
    }

    private static Uri BuildLegacyMoveItemUrl(
        SharePointRouteInfo routeInfo,
        string oldServerRelativePath,
        string newServerRelativePath,
        bool isDirectory,
        bool replaceIfExists)
    {
        var target = isDirectory ? "GetFolderByServerRelativeUrl" : "GetFileByServerRelativeUrl";
        var flags = replaceIfExists ? 1 : 0;
        var moveCall = isDirectory
            ? $"MoveTo(newurl={ToODataStringLiteral(newServerRelativePath)})"
            : $"MoveTo(newurl={ToODataStringLiteral(newServerRelativePath)},flags={flags})";
        return new Uri($"{routeInfo.SiteRoot}/_api/web/{target}({ToODataStringLiteral(oldServerRelativePath)})/{moveCall}");
    }

    private void InvalidateDirectory(DriveRoute route, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        _directoryCache.TryRemove($"{route.Id:N}:{normalized}", out _);
        var parent = GetParentPath(normalized);
        _directoryCache.TryRemove($"{route.Id:N}:{parent}", out _);
        _database.InvalidateDirectoryCache(route.Id, normalized);
        _database.InvalidateDirectoryCache(route.Id, parent);
    }

    private static string ToODataStringLiteral(string value) => $"'{value.Replace("'", "''")}'";

    private static string EscapePath(string value) =>
        "/" + string.Join(
            "/",
            value.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private bool IsHostAllowed(Uri siteUri)
    {
        var allowedHosts = _enterprisePolicy.AllowedSharePointHosts;
        if (allowedHosts.Count == 0)
        {
            return true;
        }

        return allowedHosts.Any(pattern =>
            pattern.StartsWith("*.", StringComparison.Ordinal)
                ? siteUri.DnsSafeHost.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) &&
                  !string.Equals(siteUri.DnsSafeHost, pattern[2..], StringComparison.OrdinalIgnoreCase)
                : string.Equals(siteUri.DnsSafeHost, pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var segments = relativePath.Replace('\\', '/')
            .Trim()
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (string.Equals(decoded, ".", StringComparison.Ordinal) ||
                string.Equals(decoded, "..", StringComparison.Ordinal))
            {
                throw new ArgumentException("Relative SharePoint paths cannot contain dot segments.", nameof(relativePath));
            }
        }

        return string.Join('/', segments);
    }

    private static string GetParentPath(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var index = normalized.LastIndexOf('/');
        return index < 0 ? string.Empty : normalized[..index];
    }

    private static string GetFileName(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static long GetLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), out var value) => value,
            _ => 0
        };
    }

    private static DateTimeOffset ParseDate(string value)
    {
        if (DateTimeOffset.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.UtcNow;
    }

    private sealed record CacheEntry(DateTimeOffset CreatedAt, IReadOnlyList<SharePointDriveItem> Items);

    private sealed record HttpOperationResult(bool IsSuccess, HttpStatusCode StatusCode);

    private sealed class NonDisposingReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // StreamContent owns this wrapper, not the caller's stream.
        }
    }

    private sealed record SharePointRouteInfo(Uri SiteUri, string SiteRoot, string SiteServerRelativePath, string RouteServerRelativePath)
    {
        public static SharePointRouteInfo? FromRoute(DriveRoute route)
        {
            if (!Uri.TryCreate(route.SharePointUrl, UriKind.Absolute, out var siteUri) ||
                !SharePointRouteParser.IsAllowedSharePointUri(siteUri))
            {
                return null;
            }

            var sitePath = siteUri.AbsolutePath.TrimEnd('/');
            if (sitePath.EndsWith("/Forms/AllItems.aspx", StringComparison.OrdinalIgnoreCase))
            {
                sitePath = sitePath[..^"/Forms/AllItems.aspx".Length];
            }

            var siteServerRelativePath = ExtractSiteServerRelativePath(sitePath);
            var routeServerRelativePath = BuildRouteServerRelativePath(siteServerRelativePath, route.RemotePath);
            var siteRoot = $"{siteUri.Scheme}://{siteUri.Host}{siteServerRelativePath}";
            return new SharePointRouteInfo(siteUri, siteRoot, siteServerRelativePath, routeServerRelativePath);
        }

        public string BuildServerRelativePath(string relativePath)
        {
            var normalized = NormalizeRelativePath(relativePath);
            return string.IsNullOrWhiteSpace(normalized)
                ? RouteServerRelativePath
                : $"{RouteServerRelativePath.TrimEnd('/')}/{normalized}";
        }

        private static string ExtractSiteServerRelativePath(string absolutePath)
        {
            var segments = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 &&
                (string.Equals(segments[0], "sites", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(segments[0], "teams", StringComparison.OrdinalIgnoreCase)))
            {
                return $"/{segments[0]}/{segments[1]}";
            }

            return string.IsNullOrWhiteSpace(absolutePath) ? string.Empty : absolutePath;
        }

        private static string BuildRouteServerRelativePath(string siteServerRelativePath, string remotePath)
        {
            if (string.IsNullOrWhiteSpace(remotePath) || remotePath.Trim() == "/")
            {
                return siteServerRelativePath;
            }

            var normalized = remotePath.Replace('\\', '/').Trim();
            if (normalized.StartsWith("/sites/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("/teams/", StringComparison.OrdinalIgnoreCase))
            {
                return normalized.TrimEnd('/');
            }

            return $"{siteServerRelativePath.TrimEnd('/')}/{normalized.Trim('/')}";
        }
    }
}

public sealed record SharePointDriveItem(
    string Name,
    string ServerRelativeUrl,
    bool IsDirectory,
    long Length,
    DateTimeOffset ModifiedAt);
