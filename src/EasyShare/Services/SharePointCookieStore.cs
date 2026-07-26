using System.Collections.Concurrent;

namespace EasyShare.Services;

public static class SharePointCookieStore
{
    private static readonly ConcurrentDictionary<string, string> CookiesByHost = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> VerifiedRoutes = new(StringComparer.OrdinalIgnoreCase);

    public static void SetCookieHeader(Uri uri, string cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return;
        }

        if (CookiesByHost.TryGetValue(uri.Host, out var previousHeader) &&
            !string.Equals(previousHeader, cookieHeader, StringComparison.Ordinal))
        {
            ClearVerifiedRoutesForHost(uri);
        }

        CookiesByHost[uri.Host] = cookieHeader;
    }

    public static bool TryGetCookieHeader(Uri uri, out string cookieHeader) =>
        CookiesByHost.TryGetValue(uri.Host, out cookieHeader!);

    public static void MarkRouteVerified(Uri siteUri) =>
        VerifiedRoutes[CreateRouteKey(siteUri)] = 0;

    public static bool IsRouteVerified(Uri siteUri) =>
        VerifiedRoutes.ContainsKey(CreateRouteKey(siteUri));

    public static void UnmarkRouteVerified(Uri siteUri) =>
        VerifiedRoutes.TryRemove(CreateRouteKey(siteUri), out _);

    public static void RemoveHost(Uri uri)
    {
        CookiesByHost.TryRemove(uri.Host, out _);
        ClearVerifiedRoutesForHost(uri);
    }

    private static void ClearVerifiedRoutesForHost(Uri uri)
    {
        var prefix = $"{uri.Host.TrimEnd('.')}|";
        foreach (var key in VerifiedRoutes.Keys.Where(
                     key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            VerifiedRoutes.TryRemove(key, out _);
        }
    }

    public static void Clear()
    {
        CookiesByHost.Clear();
        VerifiedRoutes.Clear();
    }

    private static string CreateRouteKey(Uri siteUri) =>
        $"{siteUri.Host.TrimEnd('.')}|{siteUri.AbsolutePath.TrimEnd('/')}";
}
