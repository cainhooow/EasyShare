using System.Security.Cryptography;
using System.Text;
using EasyShare.Models;

namespace EasyShare.Services;

/// <summary>
/// Exposes the folders of routes already known by EasyShare through the
/// authenticated WebView2 cookie session. Browser authentication cannot use
/// the tenant-wide Microsoft Graph discovery endpoints, so each configured
/// route is presented as a browsable library.
/// </summary>
public sealed class BrowserSharePointExplorerService : ISharePointExplorerService
{
    private readonly LocalDatabase _database;
    private readonly SharePointBrowserContentService _content;
    private EnterprisePolicy _enterprisePolicy = new();

    public BrowserSharePointExplorerService(
        LocalDatabase database,
        SharePointBrowserContentService content)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public void ConfigureEnterprisePolicy(EnterprisePolicy policy) =>
        _enterprisePolicy = policy ?? throw new ArgumentNullException(nameof(policy));

    public async Task<IReadOnlyList<SharePointSiteInfo>> DiscoverSitesAsync(
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedQuery = query?.Trim() ?? string.Empty;
        var routes = await GetAuthenticatedRoutesAsync(cancellationToken).ConfigureAwait(false);

        return routes
            .GroupBy(route => NormalizeSiteUrl(route.SharePointUrl), StringComparer.OrdinalIgnoreCase)
            .Where(group => string.IsNullOrWhiteSpace(normalizedQuery) ||
                            group.Key.Contains(normalizedQuery, StringComparison.CurrentCultureIgnoreCase) ||
                            group.Any(route => route.DisplayName.Contains(
                                normalizedQuery,
                                StringComparison.CurrentCultureIgnoreCase)))
            .Select(group =>
            {
                var siteUrl = group.Key;
                var displayName = GetSiteDisplayName(siteUrl);
                return new SharePointSiteInfo(
                    siteUrl,
                    displayName,
                    siteUrl,
                    null,
                    IsFollowed: true);
            })
            .OrderBy(site => site.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<SharePointLibraryInfo>> GetLibrariesAsync(
        string siteId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        var normalizedSiteId = NormalizeSiteUrl(siteId);
        var routes = await GetAuthenticatedRoutesAsync(cancellationToken).ConfigureAwait(false);

        return routes
            .Where(route => string.Equals(
                NormalizeSiteUrl(route.SharePointUrl),
                normalizedSiteId,
                StringComparison.OrdinalIgnoreCase))
            .Select(route => new SharePointLibraryInfo(
                route.Id.ToString("N"),
                normalizedSiteId,
                route.DisplayName,
                SharePointRouteParser.BuildDisplayUrl(route.SharePointUrl, route.RemotePath),
                "/"))
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
        if (!string.IsNullOrWhiteSpace(nextLink))
        {
            throw InvalidResponse("Browser-session pagination links are not supported.");
        }

        if (!Guid.TryParseExact(driveId, "N", out var routeId))
        {
            throw InvalidResponse("The browser route identifier is invalid.");
        }

        var route = (await _database.GetRoutesAsync().ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.Id == routeId);
        if (route is null)
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.NotFound,
                "The configured SharePoint route no longer exists.");
        }

        if (!Uri.TryCreate(route.SharePointUrl, UriKind.Absolute, out var siteUri) ||
            !SharePointRouteParser.IsAllowedSharePointUri(siteUri) ||
            !IsHostAllowed(siteUri))
        {
            throw InvalidResponse("The configured SharePoint route is invalid.");
        }

        if (!SharePointCookieStore.IsRouteVerified(siteUri) ||
            !SharePointCookieStore.TryGetCookieHeader(siteUri, out _))
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.AuthenticationRequired,
                "The integrated SharePoint session is not authenticated.");
        }

        var relativePath = NormalizeRelativePath(itemId);
        var browserRoute = CreateBrowserRoute(route);
        var children = await _content
            .ListDirectoryForExplorerAsync(browserRoute, relativePath, cancellationToken)
            .ConfigureAwait(false);
        var authority = new Uri(siteUri.GetLeftPart(UriPartial.Authority));
        var items = children.Select(item =>
        {
            var childPath = CombineRelativePath(relativePath, item.Name);
            var webUrl = !string.IsNullOrWhiteSpace(item.ServerRelativeUrl) &&
                         Uri.TryCreate(authority, item.ServerRelativeUrl, out var itemUri) &&
                         itemUri.Scheme == Uri.UriSchemeHttps &&
                         SharePointRouteParser.IsAllowedSharePointUri(itemUri) &&
                         string.Equals(
                             itemUri.DnsSafeHost,
                             siteUri.DnsSafeHost,
                             StringComparison.OrdinalIgnoreCase) &&
                         IsHostAllowed(itemUri)
                ? itemUri.AbsoluteUri
                : BuildRouteItemUrl(route, childPath);
            return new SharePointExplorerItem(
                childPath,
                route.Id.ToString("N"),
                item.Name,
                webUrl,
                item.IsDirectory,
                item.Length,
                item.ModifiedAt);
        }).ToArray();

        return new SharePointExplorerPage<SharePointExplorerItem>(items, NextLink: null);
    }

    public Task<SharePointPinnedFolder> ResolveFolderAsync(
        SharePointRouteInput routeInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routeInput);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsHostAllowed(routeInput.SiteUri))
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.Forbidden,
                "The SharePoint route is blocked by enterprise policy.");
        }

        if (!SharePointCookieStore.IsRouteVerified(routeInput.SiteUri) ||
            !SharePointCookieStore.TryGetCookieHeader(routeInput.SiteUri, out _))
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.AuthenticationRequired,
                "The integrated SharePoint session is not authenticated.");
        }

        var normalizedPath = SharePointRouteParser.NormalizeRemotePath(routeInput.RemotePath);
        var stableId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{routeInput.SiteUrl}\n{normalizedPath}")));
        var folderUrl = SharePointRouteParser.BuildDisplayUrl(routeInput.SiteUrl, normalizedPath);
        return Task.FromResult(new SharePointPinnedFolder(
            routeInput.SiteUrl,
            stableId,
            normalizedPath,
            routeInput.SuggestedName,
            routeInput.SiteUrl,
            folderUrl,
            normalizedPath));
    }

    public void ClearCache() => _content.ClearCache();

    private async Task<IReadOnlyList<DriveRoute>> GetAuthenticatedRoutesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var routes = await _database.GetRoutesAsync().ConfigureAwait(false);
        return routes.Where(route =>
            Uri.TryCreate(route.SharePointUrl, UriKind.Absolute, out var uri) &&
            SharePointRouteParser.IsAllowedSharePointUri(uri) &&
            IsHostAllowed(uri) &&
            SharePointCookieStore.IsRouteVerified(uri) &&
            SharePointCookieStore.TryGetCookieHeader(uri, out _)).ToArray();
    }

    private bool IsHostAllowed(Uri siteUri)
    {
        var allowedHosts = _enterprisePolicy.AllowedSharePointHosts;
        return allowedHosts.Count == 0 || allowedHosts.Any(pattern =>
            pattern.StartsWith("*.", StringComparison.Ordinal)
                ? siteUri.DnsSafeHost.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) &&
                  !string.Equals(siteUri.DnsSafeHost, pattern[2..], StringComparison.OrdinalIgnoreCase)
                : string.Equals(siteUri.DnsSafeHost, pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static DriveRoute CreateBrowserRoute(DriveRoute route) => new()
    {
        Id = route.Id,
        DisplayName = route.DisplayName,
        SharePointUrl = route.SharePointUrl,
        RemotePath = route.RemotePath,
        FolderWebUrl = route.FolderWebUrl,
        IsConnected = route.IsConnected,
        StatusText = route.StatusText,
        LastCheckedAt = route.LastCheckedAt
    };

    private static string NormalizeSiteUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            !SharePointRouteParser.IsAllowedSharePointUri(uri))
        {
            throw InvalidResponse("The configured SharePoint site URL is invalid.");
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string GetSiteDisplayName(string siteUrl)
    {
        var uri = new Uri(siteUrl);
        var lastSegment = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .LastOrDefault();
        return string.IsNullOrWhiteSpace(lastSegment) ? uri.Host : lastSegment;
    }

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "/")
        {
            return string.Empty;
        }

        var segments = value.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(segment => segment is "." or ".." ||
                                    segment.Any(character => char.IsControl(character))))
        {
            throw InvalidResponse("The browser folder path is invalid.");
        }

        return string.Join('/', segments);
    }

    private static string CombineRelativePath(string parent, string name) =>
        string.IsNullOrWhiteSpace(parent) ? name : $"{parent}/{name}";

    private static string BuildRouteItemUrl(DriveRoute route, string relativePath)
    {
        var routePath = SharePointRouteParser.NormalizeRemotePath(route.RemotePath);
        var combinedPath = string.Join(
            '/',
            new[] { routePath, relativePath }
                .Where(value => !string.IsNullOrWhiteSpace(value) && value != "/")
                .Select(value => value.Trim('/')));
        return SharePointRouteParser.BuildDisplayUrl(route.SharePointUrl, combinedPath);
    }

    private static SharePointExplorerException InvalidResponse(string message) =>
        new(SharePointExplorerStatus.InvalidResponse, message);
}
