using System.Collections.Concurrent;

namespace EasyShare.Services;

public static class SharePointCookieStore
{
    private static readonly ConcurrentDictionary<string, string> CookiesByHost = new(StringComparer.OrdinalIgnoreCase);

    public static void SetCookieHeader(Uri uri, string cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return;
        }

        CookiesByHost[uri.Host] = cookieHeader;
    }

    public static bool TryGetCookieHeader(Uri uri, out string cookieHeader) =>
        CookiesByHost.TryGetValue(uri.Host, out cookieHeader!);

    public static void Clear() => CookiesByHost.Clear();
}
