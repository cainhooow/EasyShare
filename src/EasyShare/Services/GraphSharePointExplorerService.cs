using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EasyShare.Models;

namespace EasyShare.Services;

public sealed class GraphSharePointExplorerService
{
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private static readonly HttpClient SharedHttpClient = new();
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);
    private readonly IAuthenticationService _authentication;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _cacheTtl;
    private readonly TimeSpan _operationTimeout;
    private readonly ConcurrentDictionary<string, SiteCacheEntry> _siteCache = new(StringComparer.Ordinal);
    private EnterprisePolicy _enterprisePolicy = new();

    public GraphSharePointExplorerService(IAuthenticationService authentication)
        : this(authentication, SharedHttpClient)
    {
    }

    public GraphSharePointExplorerService(
        IAuthenticationService authentication,
        HttpClient httpClient,
        TimeSpan? cacheTtl = null,
        TimeSpan? operationTimeout = null)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _cacheTtl = cacheTtl is null || cacheTtl <= TimeSpan.Zero ? DefaultCacheTtl : cacheTtl.Value;
        _operationTimeout = operationTimeout is null || operationTimeout <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(2)
            : operationTimeout.Value;
    }

    public void ConfigureEnterprisePolicy(EnterprisePolicy policy) =>
        Volatile.Write(ref _enterprisePolicy, policy ?? throw new ArgumentNullException(nameof(policy)));

    public void ClearCache() => _siteCache.Clear();

    public async Task<IReadOnlyList<SharePointSiteInfo>> DiscoverSitesAsync(
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        var token = await GetRequiredTokenAsync(cancellationToken).ConfigureAwait(false);
        var normalizedQuery = NormalizeQuery(query);
        var identityKey = TryCreateIdentityCacheKey(token);
        var cacheKey = identityKey is null ? null : $"{identityKey}:{normalizedQuery.ToUpperInvariant()}";
        if (cacheKey is not null &&
            _siteCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.CreatedAt < _cacheTtl)
        {
            return ApplyEnterprisePolicy(cached.Sites, Volatile.Read(ref _enterprisePolicy));
        }

        var followedUrl = $"{GraphBaseUrl}/me/followedSites?$select=id,displayName,webUrl,description&$top=200";
        var searchTerm = string.IsNullOrWhiteSpace(normalizedQuery) ? "*" : normalizedQuery;
        var searchUrl = $"{GraphBaseUrl}/sites?search={Uri.EscapeDataString(searchTerm)}&$select=id,displayName,webUrl,description&$top=200";

        var followedTask = TryGetAllSitesAsync(followedUrl, token, isFollowed: true, cancellationToken);
        var searchTask = TryGetAllSitesAsync(searchUrl, token, isFollowed: false, cancellationToken);
        await Task.WhenAll(followedTask, searchTask).ConfigureAwait(false);

        var followed = await followedTask.ConfigureAwait(false);
        var searched = await searchTask.ConfigureAwait(false);
        if (!followed.Succeeded && !searched.Succeeded)
        {
            throw ChooseFailure(followed.Error, searched.Error);
        }

        if (!string.IsNullOrWhiteSpace(normalizedQuery) && !searched.Succeeded)
        {
            throw searched.Error ?? InvalidResponse("SharePoint site search failed without an error response.");
        }

        IEnumerable<SharePointSiteInfo> followedSites = followed.Items;
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            followedSites = followedSites.Where(site => MatchesQuery(site, normalizedQuery));
        }

        var sites = DeduplicateSites(followedSites.Concat(searched.Items));
        if (cacheKey is not null && followed.Succeeded && searched.Succeeded)
        {
            _siteCache[cacheKey] = new SiteCacheEntry(DateTimeOffset.UtcNow, sites);
        }

        return ApplyEnterprisePolicy(sites, Volatile.Read(ref _enterprisePolicy));
    }

    public async Task<IReadOnlyList<SharePointLibraryInfo>> GetLibrariesAsync(
        string siteId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        siteId = ValidateGraphIdentifier(siteId, nameof(siteId));
        var token = await GetRequiredTokenAsync(cancellationToken).ConfigureAwait(false);
        var url = $"{GraphBaseUrl}/sites/{Uri.EscapeDataString(siteId)}/drives" +
                  "?$select=id,name,webUrl,driveType&$top=200";
        var drives = await GetAllLibrariesAsync(url, siteId, token, cancellationToken).ConfigureAwait(false);
        var resolved = new List<SharePointLibraryInfo>(drives.Count);

        foreach (var drive in drives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootItemId = "root";
            try
            {
                rootItemId = await ResolveRootItemIdAsync(drive.Id, token, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SharePointExplorerException exception) when (
                exception.Status is SharePointExplorerStatus.NotFound or SharePointExplorerStatus.InvalidResponse)
            {
                // The symbolic "root" segment remains a valid Graph route when root metadata is unavailable.
            }

            resolved.Add(drive with { RootItemId = rootItemId });
        }

        return resolved
            .OrderBy(library => library.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<SharePointExplorerPage<SharePointExplorerItem>> GetChildrenAsync(
        string driveId,
        string itemId,
        string? nextLink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        driveId = ValidateGraphIdentifier(driveId, nameof(driveId));
        itemId = ValidateGraphIdentifier(itemId, nameof(itemId));
        var validatedNextLink = string.IsNullOrWhiteSpace(nextLink) ? null : ValidateGraphUrl(nextLink);
        var token = await GetRequiredTokenAsync(cancellationToken).ConfigureAwait(false);
        var url = validatedNextLink is null
            ? BuildChildrenUrl(driveId, itemId)
            : validatedNextLink;

        using var document = await GetJsonAsync(url, token, cancellationToken).ConfigureAwait(false);
        var items = ParseExplorerItems(document.RootElement, driveId);
        var responseNextLink = ReadNextLink(document.RootElement);
        return new SharePointExplorerPage<SharePointExplorerItem>(items, responseNextLink);
    }

    public async Task<SharePointPinnedFolder> ResolveFolderAsync(
        SharePointRouteInput routeInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routeInput);
        var policy = Volatile.Read(ref _enterprisePolicy);
        if (!IsAllowedSiteUrl(routeInput.SiteUri.AbsoluteUri, policy.AllowedSharePointHosts))
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.Forbidden,
                "The SharePoint URL is blocked by the enterprise policy.");
        }

        var routeSegments = ParseSafePathSegments(routeInput.RemotePath, "remotePath");
        var token = await GetRequiredTokenAsync(cancellationToken).ConfigureAwait(false);
        var siteUrl = BuildSiteResolutionUrl(routeInput.SiteUri);
        using var siteDocument = await GetJsonAsync(siteUrl, token, cancellationToken).ConfigureAwait(false);
        var siteId = ValidateGraphIdentifier(
            ReadRequiredString(siteDocument.RootElement, "id", "The SharePoint site did not include an id."),
            "siteId");
        var resolvedSiteWebUrl = ReadRequiredString(
            siteDocument.RootElement,
            "webUrl",
            "The SharePoint site did not include a webUrl.");
        if (!IsAllowedSiteUrl(resolvedSiteWebUrl, policy.AllowedSharePointHosts))
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.Forbidden,
                "Microsoft Graph resolved the URL to a site blocked by the enterprise policy.");
        }

        var libraries = await GetLibrariesAsync(siteId, cancellationToken).ConfigureAwait(false);
        if (libraries.Count == 0)
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.NotFound,
                "The SharePoint site does not expose a document library to this account.");
        }

        var selection = SelectLibrary(routeInput.SiteUri, routeSegments, libraries);
        if (selection is null)
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.NotFound,
                "The SharePoint URL does not identify an accessible document library.");
        }

        var (library, folderSegments) = selection.Value;
        if (folderSegments.Count == 0)
        {
            if (!Uri.TryCreate(library.WebUrl, UriKind.Absolute, out var libraryWebUri) ||
                !SharePointRouteParser.IsAllowedSharePointUri(libraryWebUri))
            {
                throw InvalidResponse("The document library did not include a valid SharePoint webUrl.");
            }

            if (!IsAllowedSiteUrl(library.WebUrl, policy.AllowedSharePointHosts))
            {
                throw new SharePointExplorerException(
                    SharePointExplorerStatus.Forbidden,
                    "Microsoft Graph resolved the URL to a library blocked by the enterprise policy.");
            }

            return new SharePointPinnedFolder(
                siteId,
                library.Id,
                library.RootItemId,
                library.Name,
                resolvedSiteWebUrl,
                library.WebUrl,
                $"/{library.Name}");
        }

        var folderUrl = BuildFolderResolutionUrl(library.Id, folderSegments);
        using var folderDocument = await GetJsonAsync(folderUrl, token, cancellationToken).ConfigureAwait(false);
        if (!folderDocument.RootElement.TryGetProperty("folder", out var folderFacet) ||
            folderFacet.ValueKind != JsonValueKind.Object)
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.NotFound,
                "The SharePoint URL points to a file instead of a folder.");
        }

        var itemId = ValidateGraphIdentifier(
            ReadRequiredString(folderDocument.RootElement, "id", "The SharePoint folder did not include an id."),
            "itemId");
        var folderName = ReadRequiredString(
            folderDocument.RootElement,
            "name",
            "The SharePoint folder did not include a name.");
        var folderWebUrl = ReadRequiredString(
            folderDocument.RootElement,
            "webUrl",
            "The SharePoint folder did not include a webUrl.");
        if (!IsAllowedSiteUrl(folderWebUrl, policy.AllowedSharePointHosts))
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.Forbidden,
                "Microsoft Graph resolved the URL to a folder blocked by the enterprise policy.");
        }

        return new SharePointPinnedFolder(
            siteId,
            library.Id,
            itemId,
            folderName,
            resolvedSiteWebUrl,
            folderWebUrl,
            $"/{string.Join('/', new[] { library.Name }.Concat(folderSegments))}");
    }

    private async Task<SiteFetchResult> TryGetAllSitesAsync(
        string initialUrl,
        string token,
        bool isFollowed,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = new List<SharePointSiteInfo>();
            var nextUrl = initialUrl;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrWhiteSpace(nextUrl))
            {
                cancellationToken.ThrowIfCancellationRequested();
                nextUrl = ValidateGraphUrl(nextUrl);
                if (!visited.Add(nextUrl))
                {
                    throw InvalidResponse("Microsoft Graph returned a repeated pagination link.");
                }

                using var document = await GetJsonAsync(nextUrl, token, cancellationToken).ConfigureAwait(false);
                items.AddRange(ParseSites(document.RootElement, isFollowed));
                nextUrl = ReadNextLink(document.RootElement);
            }

            return new SiteFetchResult(true, items, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SharePointExplorerException ex)
        {
            return new SiteFetchResult(false, [], ex);
        }
    }

    private async Task<List<SharePointLibraryInfo>> GetAllLibrariesAsync(
        string initialUrl,
        string siteId,
        string token,
        CancellationToken cancellationToken)
    {
        var libraries = new List<SharePointLibraryInfo>();
        var nextUrl = initialUrl;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();
            nextUrl = ValidateGraphUrl(nextUrl);
            if (!visited.Add(nextUrl))
            {
                throw InvalidResponse("Microsoft Graph returned a repeated pagination link.");
            }

            using var document = await GetJsonAsync(nextUrl, token, cancellationToken).ConfigureAwait(false);
            libraries.AddRange(ParseLibraries(document.RootElement, siteId));
            nextUrl = ReadNextLink(document.RootElement);
        }

        return libraries
            .GroupBy(library => library.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private async Task<string> ResolveRootItemIdAsync(
        string driveId,
        string token,
        CancellationToken cancellationToken)
    {
        driveId = ValidateGraphIdentifier(driveId, nameof(driveId));
        var url = $"{GraphBaseUrl}/drives/{Uri.EscapeDataString(driveId)}/root?$select=id";
        using var document = await GetJsonAsync(url, token, cancellationToken).ConfigureAwait(false);
        return ValidateGraphIdentifier(
            ReadRequiredString(document.RootElement, "id", "The drive root did not include an id."),
            "rootItemId");
    }

    private async Task<string> GetRequiredTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = await _authentication.GetAccessTokenAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.AuthenticationRequired,
                "Sign in is required before browsing SharePoint.");
        }

        return token;
    }

    private async Task<JsonDocument> GetJsonAsync(
        string url,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ValidateGraphUrl(url));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_operationTimeout);

        try
        {
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateHttpFailure(response);
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SharePointExplorerException)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.ServiceUnavailable,
                "Microsoft Graph exceeded the response timeout.",
                innerException: ex);
        }
        catch (JsonException ex)
        {
            throw InvalidResponse("Microsoft Graph returned malformed JSON.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.ServiceUnavailable,
                "Microsoft Graph is temporarily unavailable.",
                innerException: ex);
        }
    }

    private static IReadOnlyList<SharePointSiteInfo> ParseSites(JsonElement root, bool isFollowed)
    {
        var value = ReadValueArray(root);
        var sites = new List<SharePointSiteInfo>();
        foreach (var element in value.EnumerateArray())
        {
            var id = ReadOptionalString(element, "id");
            var webUrl = ReadOptionalString(element, "webUrl");
            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(webUrl) ||
                !IsSafeGraphIdentifier(id))
            {
                continue;
            }

            var displayName = ReadOptionalString(element, "displayName");
            sites.Add(new SharePointSiteInfo(
                id,
                string.IsNullOrWhiteSpace(displayName) ? GetFallbackName(webUrl) : displayName,
                webUrl,
                ReadOptionalString(element, "description"),
                isFollowed));
        }

        return sites;
    }

    private static IReadOnlyList<SharePointLibraryInfo> ParseLibraries(JsonElement root, string siteId)
    {
        var value = ReadValueArray(root);
        var libraries = new List<SharePointLibraryInfo>();
        foreach (var element in value.EnumerateArray())
        {
            var id = ReadOptionalString(element, "id");
            var name = ReadOptionalString(element, "name");
            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(name) ||
                !IsSafeGraphIdentifier(id))
            {
                continue;
            }

            libraries.Add(new SharePointLibraryInfo(
                id,
                siteId,
                name,
                ReadOptionalString(element, "webUrl") ?? string.Empty,
                "root"));
        }

        return libraries;
    }

    private static IReadOnlyList<SharePointExplorerItem> ParseExplorerItems(JsonElement root, string driveId)
    {
        var value = ReadValueArray(root);
        var items = new List<SharePointExplorerItem>();
        foreach (var element in value.EnumerateArray())
        {
            var id = ReadOptionalString(element, "id");
            var name = ReadOptionalString(element, "name");
            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(name) ||
                !IsSafeGraphIdentifier(id))
            {
                continue;
            }

            var isFolder = element.TryGetProperty("folder", out var folder) && folder.ValueKind == JsonValueKind.Object;
            var size = element.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : 0;
            var modifiedAt = DateTimeOffset.TryParse(
                ReadOptionalString(element, "lastModifiedDateTime"),
                out var parsedModifiedAt)
                ? parsedModifiedAt
                : (DateTimeOffset?)null;

            items.Add(new SharePointExplorerItem(
                id,
                driveId,
                name,
                ReadOptionalString(element, "webUrl") ?? string.Empty,
                isFolder,
                size,
                modifiedAt));
        }

        return items
            .OrderByDescending(item => item.IsFolder)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static JsonElement ReadValueArray(JsonElement root)
    {
        if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidResponse("Microsoft Graph response did not include a value array.");
        }

        return value;
    }

    private static string? ReadNextLink(JsonElement root)
    {
        var nextLink = ReadOptionalString(root, "@odata.nextLink");
        return string.IsNullOrWhiteSpace(nextLink) ? null : ValidateGraphUrl(nextLink);
    }

    private static string ReadRequiredString(JsonElement element, string propertyName, string message)
    {
        var value = ReadOptionalString(element, propertyName);
        return string.IsNullOrWhiteSpace(value) ? throw InvalidResponse(message) : value;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static IReadOnlyList<SharePointSiteInfo> DeduplicateSites(IEnumerable<SharePointSiteInfo> source)
    {
        var sites = new List<SharePointSiteInfo>();
        foreach (var candidate in source)
        {
            var index = sites.FindIndex(existing =>
                string.Equals(existing.Id, candidate.Id, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(existing.WebUrl) &&
                 string.Equals(existing.WebUrl.TrimEnd('/'), candidate.WebUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));
            if (index < 0)
            {
                sites.Add(candidate);
                continue;
            }

            var existing = sites[index];
            sites[index] = existing with
            {
                DisplayName = PreferRequiredText(existing.DisplayName, candidate.DisplayName),
                WebUrl = PreferRequiredText(existing.WebUrl, candidate.WebUrl),
                Description = PreferText(existing.Description, candidate.Description),
                IsFollowed = existing.IsFollowed || candidate.IsFollowed
            };
        }

        return sites
            .OrderByDescending(site => site.IsFollowed)
            .ThenBy(site => site.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<SharePointSiteInfo> ApplyEnterprisePolicy(
        IReadOnlyList<SharePointSiteInfo> sites,
        EnterprisePolicy policy) =>
        sites.Where(site => IsAllowedSiteUrl(site.WebUrl, policy.AllowedSharePointHosts)).ToArray();

    private static bool IsAllowedSiteUrl(string webUrl, IReadOnlyList<string> allowedHosts)
    {
        if (!Uri.TryCreate(webUrl, UriKind.Absolute, out var uri) ||
            (!uri.IsDefaultPort && uri.Port != 443) ||
            !SharePointRouteParser.IsAllowedSharePointUri(uri))
        {
            return false;
        }

        if (allowedHosts.Count == 0)
        {
            return true;
        }

        return allowedHosts.Any(pattern =>
            pattern.StartsWith("*.", StringComparison.Ordinal)
                ? uri.DnsSafeHost.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) &&
                  !string.Equals(uri.DnsSafeHost, pattern[2..], StringComparison.OrdinalIgnoreCase)
                : string.Equals(uri.DnsSafeHost, pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static string? PreferText(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) ? second : first;

    private static string PreferRequiredText(string first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : first;

    private static bool MatchesQuery(SharePointSiteInfo site, string query) =>
        site.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        site.WebUrl.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (site.Description?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);

    private static string GetFallbackName(string webUrl)
    {
        if (!Uri.TryCreate(webUrl, UriKind.Absolute, out var uri))
        {
            return webUrl;
        }

        var segment = uri.Segments.LastOrDefault()?.Trim('/');
        return string.IsNullOrWhiteSpace(segment) ? uri.Host : Uri.UnescapeDataString(segment);
    }

    private static string BuildChildrenUrl(string driveId, string itemId)
    {
        var drive = Uri.EscapeDataString(driveId);
        var itemSegment = string.Equals(itemId, "root", StringComparison.OrdinalIgnoreCase)
            ? "root"
            : $"items/{Uri.EscapeDataString(itemId)}";
        return $"{GraphBaseUrl}/drives/{drive}/{itemSegment}/children" +
               "?$select=id,name,webUrl,size,lastModifiedDateTime,folder,file,parentReference&$top=200";
    }

    private static string BuildSiteResolutionUrl(Uri siteUri)
    {
        var path = ParseSafePathSegments(siteUri.AbsolutePath, "sitePath");
        var escapedPath = path.Count == 0
            ? "/"
            : $"/{string.Join('/', path.Select(Uri.EscapeDataString))}";
        return $"{GraphBaseUrl}/sites/{siteUri.DnsSafeHost}:{escapedPath}" +
               "?$select=id,displayName,webUrl";
    }

    private static string BuildFolderResolutionUrl(string driveId, IReadOnlyList<string> folderSegments)
    {
        driveId = ValidateGraphIdentifier(driveId, nameof(driveId));
        var escapedPath = string.Join('/', folderSegments.Select(Uri.EscapeDataString));
        return $"{GraphBaseUrl}/drives/{Uri.EscapeDataString(driveId)}/root:/{escapedPath}" +
               "?$select=id,name,webUrl,folder";
    }

    private static (SharePointLibraryInfo Library, IReadOnlyList<string> FolderSegments)? SelectLibrary(
        Uri siteUri,
        IReadOnlyList<string> routeSegments,
        IReadOnlyList<SharePointLibraryInfo> libraries)
    {
        if (routeSegments.Count == 0)
        {
            var preferred = libraries.FirstOrDefault(library =>
                                string.Equals(library.Name, "Documents", StringComparison.OrdinalIgnoreCase)) ??
                            libraries.FirstOrDefault(library =>
                                string.Equals(
                                    GetLastSafeWebPathSegment(library.WebUrl),
                                    "Shared Documents",
                                    StringComparison.OrdinalIgnoreCase)) ??
                            libraries[0];
            return (preferred, Array.Empty<string>());
        }

        var candidates = libraries
            .Select(library => new
            {
                Library = library,
                Prefix = GetLibraryRoutePrefix(siteUri, library)
            })
            .Where(candidate =>
                candidate.Prefix.Count > 0 &&
                StartsWithSegments(routeSegments, candidate.Prefix))
            .OrderByDescending(candidate => candidate.Prefix.Count)
            .ToArray();
        if (candidates.FirstOrDefault() is { } match)
        {
            return (match.Library, routeSegments.Skip(match.Prefix.Count).ToArray());
        }

        var nameMatch = libraries.FirstOrDefault(library =>
            string.Equals(library.Name, routeSegments[0], StringComparison.OrdinalIgnoreCase));
        return nameMatch is null
            ? null
            : (nameMatch, routeSegments.Skip(1).ToArray());
    }

    private static IReadOnlyList<string> GetLibraryRoutePrefix(Uri siteUri, SharePointLibraryInfo library)
    {
        if (Uri.TryCreate(library.WebUrl, UriKind.Absolute, out var libraryUri) &&
            string.Equals(libraryUri.DnsSafeHost, siteUri.DnsSafeHost, StringComparison.OrdinalIgnoreCase))
        {
            var siteSegments = ParseSafePathSegments(siteUri.AbsolutePath, "sitePath");
            var librarySegments = ParseSafePathSegments(libraryUri.AbsolutePath, "libraryPath");
            if (StartsWithSegments(librarySegments, siteSegments) && librarySegments.Count > siteSegments.Count)
            {
                return librarySegments.Skip(siteSegments.Count).ToArray();
            }
        }

        return [library.Name];
    }

    private static string? GetLastSafeWebPathSegment(string webUrl)
    {
        if (!Uri.TryCreate(webUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = ParseSafePathSegments(uri.AbsolutePath, "libraryPath");
        return segments.LastOrDefault();
    }

    private static bool StartsWithSegments(
        IReadOnlyList<string> value,
        IReadOnlyList<string> prefix)
    {
        if (prefix.Count > value.Count)
        {
            return false;
        }

        for (var index = 0; index < prefix.Count; index++)
        {
            if (!string.Equals(value[index], prefix[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<string> ParseSafePathSegments(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "/")
        {
            return Array.Empty<string>();
        }

        try
        {
            var segments = value
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => DecodePathSegment(segment, parameterName))
                .ToArray();
            if (segments.Sum(segment => segment.Length) > 4000)
            {
                throw InvalidResponse($"The {parameterName} is too long.");
            }

            return segments;
        }
        catch (UriFormatException ex)
        {
            throw InvalidResponse($"The {parameterName} is invalid.", ex);
        }
    }

    private static string DecodePathSegment(string value, string parameterName)
    {
        var decoded = value.Trim();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var next = Uri.UnescapeDataString(decoded);
            if (string.Equals(next, decoded, StringComparison.Ordinal))
            {
                break;
            }

            decoded = next;
        }

        if (string.IsNullOrWhiteSpace(decoded) ||
            decoded.Length > 400 ||
            decoded.Any(character => char.IsControl(character) || character is '/' or '\\') ||
            decoded is "." or "..")
        {
            throw InvalidResponse($"The {parameterName} contains an invalid path segment.");
        }

        return decoded;
    }

    private static string NormalizeQuery(string? query) => query?.Trim() ?? string.Empty;

    private static string ValidateGraphUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidResponse("Microsoft Graph returned an untrusted pagination link.");
        }

        return uri.AbsoluteUri;
    }

    private static string ValidateGraphIdentifier(string value, string parameterName)
    {
        var normalized = value.Trim();
        if (!IsSafeGraphIdentifier(normalized))
        {
            throw InvalidResponse($"The {parameterName} Graph identifier is invalid.");
        }

        return normalized;
    }

    private static bool IsSafeGraphIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 2048 ||
            value.Any(character => char.IsControl(character) || character is '/' or '\\'))
        {
            return false;
        }

        var decoded = Uri.UnescapeDataString(value.Trim());
        return !string.Equals(decoded, ".", StringComparison.Ordinal) &&
               !string.Equals(decoded, "..", StringComparison.Ordinal) &&
               !decoded.Contains('/') &&
               !decoded.Contains('\\');
    }

    private static string? TryCreateIdentityCacheKey(string token)
    {
        try
        {
            var segments = token.Split('.');
            if (segments.Length < 2)
            {
                return null;
            }

            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var tenantId = ReadOptionalString(document.RootElement, "tid");
            var objectId = ReadOptionalString(document.RootElement, "oid");
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId))
            {
                return null;
            }

            var clientId = ReadOptionalString(document.RootElement, "azp") ??
                           ReadOptionalString(document.RootElement, "appid") ??
                           string.Empty;
            var scopes = (ReadOptionalString(document.RootElement, "scp") ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var identityBytes = Encoding.UTF8.GetBytes(
                $"{tenantId}\n{objectId}\n{clientId}\n{string.Join(' ', scopes)}");
            return Convert.ToHexString(SHA256.HashData(identityBytes));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SharePointExplorerException CreateHttpFailure(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryDate)
        {
            retryAfter = retryDate - DateTimeOffset.UtcNow;
            if (retryAfter < TimeSpan.Zero)
            {
                retryAfter = TimeSpan.Zero;
            }
        }

        var status = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => SharePointExplorerStatus.AuthenticationRequired,
            HttpStatusCode.Forbidden => SharePointExplorerStatus.Forbidden,
            HttpStatusCode.TooManyRequests => SharePointExplorerStatus.Throttled,
            HttpStatusCode.NotFound => SharePointExplorerStatus.NotFound,
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                SharePointExplorerStatus.ServiceUnavailable,
            _ => SharePointExplorerStatus.InvalidResponse
        };

        var message = status switch
        {
            SharePointExplorerStatus.AuthenticationRequired => "The Microsoft Graph session has expired.",
            SharePointExplorerStatus.Forbidden => "The signed-in account cannot access this SharePoint resource.",
            SharePointExplorerStatus.Throttled => "Microsoft Graph is throttling requests. Try again later.",
            SharePointExplorerStatus.NotFound => "The SharePoint resource was not found.",
            SharePointExplorerStatus.ServiceUnavailable => "Microsoft Graph is temporarily unavailable.",
            _ => $"Microsoft Graph returned HTTP {(int)response.StatusCode}."
        };

        return new SharePointExplorerException(status, message, response.StatusCode, retryAfter);
    }

    private static SharePointExplorerException ChooseFailure(
        SharePointExplorerException? first,
        SharePointExplorerException? second)
    {
        var failures = new[] { first, second }.Where(error => error is not null).Cast<SharePointExplorerException>();
        return failures
            .OrderBy(error => error.Status switch
            {
                SharePointExplorerStatus.AuthenticationRequired => 0,
                SharePointExplorerStatus.Forbidden => 1,
                SharePointExplorerStatus.Throttled => 2,
                SharePointExplorerStatus.ServiceUnavailable => 3,
                _ => 4
            })
            .FirstOrDefault() ?? InvalidResponse("SharePoint discovery failed without an error response.");
    }

    private static SharePointExplorerException InvalidResponse(string message, Exception? innerException = null) =>
        new(SharePointExplorerStatus.InvalidResponse, message, innerException: innerException);

    private sealed record SiteCacheEntry(DateTimeOffset CreatedAt, IReadOnlyList<SharePointSiteInfo> Sites);

    private sealed record SiteFetchResult(
        bool Succeeded,
        IReadOnlyList<SharePointSiteInfo> Items,
        SharePointExplorerException? Error);
}
