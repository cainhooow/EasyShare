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

public sealed class SharePointBrowserContentService
{
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(20);
    private readonly LocalDatabase _database;
    private readonly ConcurrentDictionary<string, CacheEntry> _directoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _fileCache = new(StringComparer.OrdinalIgnoreCase);
    private TimeSpan _cacheTtl = DefaultCacheTtl;

    public SharePointBrowserContentService(LocalDatabase database)
    {
        _database = database;
    }

    public void ConfigureCache(TimeSpan ttl) => _cacheTtl = ttl <= TimeSpan.Zero ? DefaultCacheTtl : ttl;

    public IReadOnlyList<SharePointDriveItem> ListDirectory(DriveRoute route, string relativePath)
    {
        var routeInfo = SharePointRouteInfo.FromRoute(route);
        if (routeInfo is null)
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

        if (!SharePointCookieStore.TryGetCookieHeader(routeInfo.SiteUri, out var cookieHeader))
        {
            return cached?.Items ?? [];
        }

        try
        {
            var folderPath = routeInfo.BuildServerRelativePath(relativePath);
            var folders = GetItems(routeInfo, folderPath, cookieHeader, isFolderRequest: true);
            var files = GetItems(routeInfo, folderPath, cookieHeader, isFolderRequest: false);
            var items = folders
                .Concat(files)
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _directoryCache[cacheKey] = new CacheEntry(DateTimeOffset.UtcNow, items);
            _database.SaveDirectoryCache(route.Id, normalizedRelativePath, items);
            return items;
        }
        catch
        {
            return cached?.Items ?? [];
        }
    }

    public SharePointDriveItem? GetItem(DriveRoute route, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var parentPath = GetParentPath(normalized);
        var name = GetFileName(normalized);
        return ListDirectory(route, parentPath)
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public byte[] ReadFile(DriveRoute route, string relativePath)
    {
        var routeInfo = SharePointRouteInfo.FromRoute(route);
        if (routeInfo is null)
        {
            return [];
        }

        if (!SharePointCookieStore.TryGetCookieHeader(routeInfo.SiteUri, out var cookieHeader))
        {
            return [];
        }

        var normalized = NormalizeRelativePath(relativePath);
        var cacheKey = $"{route.Id:N}:{normalized}";
        if (_fileCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            using var httpClient = CreateHttpClient();
            var serverRelativePath = routeInfo.BuildServerRelativePath(normalized);
            var bytes = TryReadFile(httpClient, BuildFileValueUrl(routeInfo, serverRelativePath), cookieHeader);
            if (bytes.Length == 0)
            {
                bytes = TryReadFile(httpClient, BuildLegacyFileValueUrl(routeInfo, serverRelativePath), cookieHeader);
            }

            if (bytes.Length == 0)
            {
                bytes = TryReadFile(httpClient, BuildDirectFileUrl(routeInfo, serverRelativePath), cookieHeader);
            }

            _fileCache[cacheKey] = bytes;
            return bytes;
        }
        catch
        {
            return [];
        }
    }

    private static byte[] TryReadFile(HttpClient httpClient, Uri uri, string cookieHeader)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyHeaders(request, cookieHeader);

        using var response = httpClient.Send(request);
        var bytes = response.IsSuccessStatusCode
            ? response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            : [];
        return bytes;
    }

    public bool CreateFolder(DriveRoute route, string relativePath)
    {
        var routeInfo = SharePointRouteInfo.FromRoute(route);
        if (routeInfo is null || !SharePointCookieStore.TryGetCookieHeader(routeInfo.SiteUri, out var cookieHeader))
        {
            return false;
        }

        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        try
        {
            var digest = GetRequestDigest(routeInfo, cookieHeader);
            var folderPath = routeInfo.BuildServerRelativePath(normalized);
            using var httpClient = CreateHttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildCreateFolderUrl(routeInfo, folderPath));
            ApplyWriteHeaders(request, cookieHeader, digest);
            request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

            using var response = httpClient.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                using var fallback = new HttpRequestMessage(HttpMethod.Post, BuildLegacyCreateFolderUrl(routeInfo));
                ApplyWriteHeaders(fallback, cookieHeader, digest);
                fallback.Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        __metadata = new { type = "SP.Folder" },
                        ServerRelativeUrl = folderPath
                    }),
                    Encoding.UTF8,
                    "application/json");

                using var fallbackResponse = httpClient.Send(fallback);
                if (!fallbackResponse.IsSuccessStatusCode)
                {
                    return false;
                }
            }

            InvalidateDirectory(route, GetParentPath(normalized));
            return true;
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
        var routeInfo = SharePointRouteInfo.FromRoute(route);
        if (routeInfo is null || !SharePointCookieStore.TryGetCookieHeader(routeInfo.SiteUri, out var cookieHeader))
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

        try
        {
            if (expectedModifiedAt is not null)
            {
                InvalidateDirectory(route, parentPath);
                var current = GetItem(route, normalized);
                if (current is null || Math.Abs((current.ModifiedAt - expectedModifiedAt.Value).TotalSeconds) > 2)
                {
                    return new UploadAttemptResult(
                        UploadAttemptState.Conflict,
                        "O arquivo remoto mudou enquanto o arquivo local estava sendo editado.");
                }
            }

            var digest = GetRequestDigest(routeInfo, cookieHeader);
            var folderPath = routeInfo.BuildServerRelativePath(parentPath);
            using var httpClient = CreateHttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUploadFileUrl(routeInfo, folderPath, fileName));
            ApplyWriteHeaders(request, cookieHeader, digest);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var response = httpClient.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                using var fallback = new HttpRequestMessage(HttpMethod.Post, BuildLegacyUploadFileUrl(routeInfo, folderPath, fileName));
                ApplyWriteHeaders(fallback, cookieHeader, digest);
                fallback.Content = new ByteArrayContent(bytes);
                fallback.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                using var fallbackResponse = httpClient.Send(fallback);
                if (!fallbackResponse.IsSuccessStatusCode)
                {
                    return new UploadAttemptResult(
                        UploadAttemptState.RetryableFailure,
                        $"SharePoint retornou {(int)fallbackResponse.StatusCode}.");
                }
            }

            var cacheKey = $"{route.Id:N}:{normalized}";
            _fileCache[cacheKey] = bytes.ToArray();
            InvalidateDirectory(route, parentPath);
            return new UploadAttemptResult(UploadAttemptState.Succeeded);
        }
        catch
        {
            return new UploadAttemptResult(UploadAttemptState.RetryableFailure, "Não foi possível enviar o arquivo agora.");
        }
    }

    public void CacheLocalFile(DriveRoute route, string relativePath, byte[] bytes) =>
        _fileCache[$"{route.Id:N}:{NormalizeRelativePath(relativePath)}"] = bytes.ToArray();

    public bool DeleteItem(DriveRoute route, string relativePath, bool isDirectory)
    {
        var routeInfo = SharePointRouteInfo.FromRoute(route);
        if (routeInfo is null || !SharePointCookieStore.TryGetCookieHeader(routeInfo.SiteUri, out var cookieHeader))
        {
            return false;
        }

        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        try
        {
            var digest = GetRequestDigest(routeInfo, cookieHeader);
            using var httpClient = CreateHttpClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                BuildDeleteItemUrl(routeInfo, routeInfo.BuildServerRelativePath(normalized), isDirectory));
            ApplyWriteHeaders(request, cookieHeader, digest);
            request.Headers.TryAddWithoutValidation("IF-MATCH", "*");
            request.Headers.TryAddWithoutValidation("X-HTTP-Method", "DELETE");
            request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

            using var response = httpClient.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            _fileCache.TryRemove($"{route.Id:N}:{normalized}", out _);
            InvalidateDirectory(route, GetParentPath(normalized));
            return true;
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
    {
        var routeInfo = SharePointRouteInfo.FromRoute(route);
        if (routeInfo is null || !SharePointCookieStore.TryGetCookieHeader(routeInfo.SiteUri, out var cookieHeader))
        {
            return false;
        }

        var oldNormalized = NormalizeRelativePath(oldRelativePath);
        var newNormalized = NormalizeRelativePath(newRelativePath);
        if (string.IsNullOrWhiteSpace(oldNormalized) || string.IsNullOrWhiteSpace(newNormalized))
        {
            return false;
        }

        try
        {
            var digest = GetRequestDigest(routeInfo, cookieHeader);
            var oldServerRelativePath = routeInfo.BuildServerRelativePath(oldNormalized);
            var newServerRelativePath = routeInfo.BuildServerRelativePath(newNormalized);

            using var httpClient = CreateHttpClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                BuildMoveItemUrl(routeInfo, oldServerRelativePath, newServerRelativePath, isDirectory, replaceIfExists));
            ApplyWriteHeaders(request, cookieHeader, digest);
            request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

            using var response = httpClient.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                using var fallback = new HttpRequestMessage(
                    HttpMethod.Post,
                    BuildLegacyMoveItemUrl(routeInfo, oldServerRelativePath, newServerRelativePath, isDirectory, replaceIfExists));
                ApplyWriteHeaders(fallback, cookieHeader, digest);
                fallback.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

                using var fallbackResponse = httpClient.Send(fallback);
                if (!fallbackResponse.IsSuccessStatusCode)
                {
                    return false;
                }
            }

            _fileCache.TryRemove($"{route.Id:N}:{oldNormalized}", out _);
            _fileCache.TryRemove($"{route.Id:N}:{newNormalized}", out _);
            InvalidateDirectory(route, GetParentPath(oldNormalized));
            InvalidateDirectory(route, GetParentPath(newNormalized));
            return true;
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
    }

    public void ClearCache()
    {
        _directoryCache.Clear();
        _fileCache.Clear();
        _database.ClearDirectoryCache();
    }

    private static IReadOnlyList<SharePointDriveItem> GetItems(
        SharePointRouteInfo routeInfo,
        string serverRelativePath,
        string cookieHeader,
        bool isFolderRequest)
    {
        using var httpClient = CreateHttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            isFolderRequest
                ? BuildFoldersUrl(routeInfo, serverRelativePath)
                : BuildFilesUrl(routeInfo, serverRelativePath));
        ApplyHeaders(request, cookieHeader);

        using var response = httpClient.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(stream);
        return ParseItems(document.RootElement, isFolderRequest);
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

    private static HttpClient CreateHttpClient() =>
        new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

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

    private static string GetRequestDigest(SharePointRouteInfo routeInfo, string cookieHeader)
    {
        using var httpClient = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{routeInfo.SiteRoot}/_api/contextinfo"));
        ApplyHeaders(request, cookieHeader);
        request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

        using var response = httpClient.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(stream);
        return ExtractFormDigest(document.RootElement);
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

    private static string NormalizeRelativePath(string relativePath) =>
        string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : relativePath.Replace('\\', '/').Trim('/');

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
