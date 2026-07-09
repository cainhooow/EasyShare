using System.Text.RegularExpressions;
using EasyShare.Resources;

namespace EasyShare.Services;

public sealed record SharePointRouteInput(
    Uri SiteUri,
    string SiteUrl,
    string RemotePath,
    string SuggestedName,
    Uri NavigationUri);

public static class SharePointRouteParser
{
    public static bool TryParse(string value, out SharePointRouteInput route)
    {
        route = default!;
        if (!TryCreateUri(value, out var uri) ||
            !uri.Host.Contains("sharepoint.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pathSegments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        var siteSegmentCount = GetSiteSegmentCount(pathSegments);
        var sitePath = siteSegmentCount == 0
            ? string.Empty
            : "/" + string.Join("/", pathSegments.Take(siteSegmentCount).Select(Uri.EscapeDataString));
        var siteUri = new Uri($"{uri.Scheme}://{uri.Host}{sitePath}");
        var siteServerRelativePath = Uri.UnescapeDataString(siteUri.AbsolutePath).TrimEnd('/');

        var remotePath = TryGetServerRelativePathFromQuery(uri, siteServerRelativePath, out var queryPath)
            ? queryPath
            : BuildRemotePathFromUrlSegments(pathSegments, siteSegmentCount);
        remotePath = NormalizeRemotePath(remotePath);

        var suggestedName = GetSuggestedName(remotePath, pathSegments, siteSegmentCount);
        route = new SharePointRouteInput(
            siteUri,
            siteUri.ToString().TrimEnd('/'),
            remotePath,
            suggestedName,
            uri);
        return true;
    }

    public static Uri NormalizeNavigationUri(string value)
    {
        if (TryCreateUri(value, out var uri))
        {
            return uri;
        }

        return new Uri("https://www.office.com/?auth=2");
    }

    public static string BuildDisplayUrl(string siteUrl, string remotePath)
    {
        var normalizedPath = NormalizeRemotePath(remotePath);
        if (string.IsNullOrWhiteSpace(normalizedPath) || normalizedPath == "/")
        {
            return siteUrl.TrimEnd('/');
        }

        return $"{siteUrl.TrimEnd('/')}/{EscapePath(normalizedPath)}";
    }

    public static string NormalizeRemotePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "/";
        }

        var normalized = Uri.UnescapeDataString(value)
            .Replace('\\', '/')
            .Trim();
        normalized = Regex.Replace(normalized, "/{2,}", "/").Trim('/');
        return string.IsNullOrWhiteSpace(normalized) ? "/" : normalized;
    }

    private static bool TryCreateUri(string value, out Uri uri)
    {
        uri = default!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"https://{normalized}";
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out uri!);
    }

    private static int GetSiteSegmentCount(IReadOnlyList<string> segments)
    {
        if (segments.Count >= 2 &&
            (string.Equals(segments[0], "sites", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(segments[0], "teams", StringComparison.OrdinalIgnoreCase)))
        {
            return 2;
        }

        return 0;
    }

    private static string BuildRemotePathFromUrlSegments(IReadOnlyList<string> segments, int siteSegmentCount)
    {
        var remoteSegments = segments.Skip(siteSegmentCount).ToList();
        var formsIndex = remoteSegments.FindIndex(segment => string.Equals(segment, "Forms", StringComparison.OrdinalIgnoreCase));
        if (formsIndex >= 0)
        {
            remoteSegments = remoteSegments.Take(formsIndex).ToList();
        }

        if (remoteSegments.Count > 0 &&
            remoteSegments[^1].EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
        {
            remoteSegments.RemoveAt(remoteSegments.Count - 1);
        }

        return string.Join("/", remoteSegments);
    }

    private static bool TryGetServerRelativePathFromQuery(Uri uri, string siteServerRelativePath, out string remotePath)
    {
        foreach (var name in new[] { "id", "RootFolder", "rootFolder" })
        {
            var value = GetQueryValue(uri.Query, name);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var decoded = Uri.UnescapeDataString(value).Replace('\\', '/').Trim();
            if (decoded.StartsWith(siteServerRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                remotePath = decoded[siteServerRelativePath.Length..].Trim('/');
                return true;
            }
        }

        remotePath = string.Empty;
        return false;
    }

    private static string? GetQueryValue(string query, string key)
    {
        var trimmed = query.TrimStart('?');
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), key, StringComparison.OrdinalIgnoreCase))
            {
                return pair[1].Replace('+', ' ');
            }
        }

        return null;
    }

    private static string GetSuggestedName(string remotePath, IReadOnlyList<string> segments, int siteSegmentCount)
    {
        var normalized = NormalizeRemotePath(remotePath);
        if (normalized != "/")
        {
            return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? AppText.Get("VirtualDriveDefaultFolderName");
        }

        return segments.Skip(siteSegmentCount - 1).FirstOrDefault(segment => !string.IsNullOrWhiteSpace(segment)) ?? AppText.Get("VirtualDriveDefaultFolderName");
    }

    private static string EscapePath(string path) =>
        string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}
