using System.Net;
using System.Net.Http.Headers;
using EasyShare.Models;
using EasyShare.Resources;
using Microsoft.Web.WebView2.Core;

namespace EasyShare.Services;

public sealed class BrowserSessionService
{
    private readonly AppDataPaths _paths;

    public BrowserSessionService(AppDataPaths paths)
    {
        _paths = paths;
    }

    public string ProfilePath => _paths.BrowserProfilePath;

    public Uri GetStartUri(AppSettings settings, IEnumerable<DriveRoute> routes)
    {
        if (Uri.TryCreate(settings.BrowserSessionStartUrl, UriKind.Absolute, out var configured) &&
            WebViewOriginPolicy.IsTrustedMicrosoftUri(configured))
        {
            return configured;
        }

        var firstRouteUrl = routes
            .Select(route => route.SharePointUrl)
            .FirstOrDefault(url =>
                Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                WebViewOriginPolicy.IsTrustedMicrosoftUri(uri));

        return Uri.TryCreate(firstRouteUrl, UriKind.Absolute, out var routeUri)
            ? routeUri
            : new Uri("https://www.office.com/");
    }

    public async Task<RouteTestResult> TestRouteAsync(DriveRoute route, CoreWebView2 coreWebView)
    {
        if (!Uri.TryCreate(route.SharePointUrl, UriKind.Absolute, out var sharePointUri) ||
            !SharePointRouteParser.IsAllowedSharePointUri(sharePointUri))
        {
            return new RouteTestResult(false, AppText.Get("BrowserRouteInvalid"));
        }

        var endpoint = BuildSharePointRestEndpoint(sharePointUri);
        var cookieHeader = await BuildCookieHeaderAsync(coreWebView, endpoint);
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return new RouteTestResult(false, AppText.Get("BrowserRouteNeedLogin"));
        }

        SharePointCookieStore.SetCookieHeader(endpoint, cookieHeader);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        };
        using var httpClient = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", "EasyShare");

        try
        {
            using var response = await httpClient.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                return new RouteTestResult(false, AppText.Get("BrowserRouteNotAuthenticated"));
            }

            return response.StatusCode switch
            {
                HttpStatusCode.OK => new RouteTestResult(true, AppText.Get("BrowserRouteConnected")),
                HttpStatusCode.Unauthorized => new RouteTestResult(false, AppText.Get("BrowserRouteExpired")),
                HttpStatusCode.Forbidden => new RouteTestResult(false, AppText.Get("BrowserRouteForbidden")),
                HttpStatusCode.NotFound => new RouteTestResult(false, AppText.Get("BrowserRouteNotFound")),
                _ => new RouteTestResult(false, AppText.Format("BrowserRouteStatusFormat", (int)response.StatusCode))
            };
        }
        catch (HttpRequestException)
        {
            return new RouteTestResult(false, AppText.Get("BrowserRouteUnavailable"));
        }
    }

    public async Task<RouteTestResult> KeepAliveAsync(IEnumerable<DriveRoute> routes, CoreWebView2 coreWebView)
    {
        var route = routes.FirstOrDefault(route =>
            Uri.TryCreate(route.SharePointUrl, UriKind.Absolute, out var uri) &&
            SharePointRouteParser.IsAllowedSharePointUri(uri));

        if (route is null)
        {
            return new RouteTestResult(true, AppText.Get("BrowserKeepAliveNoRoutes"));
        }

        return await TestRouteAsync(route, coreWebView);
    }

    public async Task<RouteTestResult> RestoreSessionAsync(IEnumerable<DriveRoute> routes, CoreWebView2 coreWebView)
    {
        var sharePointRoutes = routes
            .Select(route => new
            {
                Route = route,
                SharePointUri = TryGetSharePointUri(route)
            })
            .Where(item => item.SharePointUri is not null)
            .ToArray();

        if (sharePointRoutes.Length == 0)
        {
            return new RouteTestResult(true, AppText.Get("BrowserKeepAliveNoRoutes"));
        }

        var restoredRoutes = new List<DriveRoute>();
        foreach (var item in sharePointRoutes)
        {
            var endpoint = BuildSharePointRestEndpoint(item.SharePointUri!);
            var cookieHeader = await BuildCookieHeaderAsync(coreWebView, endpoint);
            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                continue;
            }

            SharePointCookieStore.SetCookieHeader(endpoint, cookieHeader);
            restoredRoutes.Add(item.Route);
        }

        if (restoredRoutes.Count == 0)
        {
            return new RouteTestResult(false, AppText.Get("BrowserRouteNeedLogin"));
        }

        return await TestRouteAsync(restoredRoutes[0], coreWebView);
    }

    public void ClearSession(CoreWebView2 coreWebView)
    {
        coreWebView.CookieManager.DeleteAllCookies();
        SharePointCookieStore.Clear();
    }

    public async Task ClearSessionAsync(CoreWebView2 coreWebView)
    {
        coreWebView.CookieManager.DeleteAllCookies();
        try
        {
            await coreWebView.Profile.ClearBrowsingDataAsync();
        }
        catch
        {
            // WebView may reject profile cleanup while navigation is active; cookies are still removed above.
        }

        SharePointCookieStore.Clear();
    }

    public void ClearStoredSession()
    {
        SharePointCookieStore.Clear();
        if (!Directory.Exists(ProfilePath))
        {
            return;
        }

        try
        {
            Directory.Delete(ProfilePath, recursive: true);
        }
        catch
        {
            // The profile can be locked by WebView2; it will be cleaned through ClearSessionAsync when loaded.
        }
    }

    private static Uri BuildSharePointRestEndpoint(Uri sharePointUri)
    {
        var siteRoot = sharePointUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri($"{siteRoot}/_api/web?$select=Title");
    }

    private static Uri? TryGetSharePointUri(DriveRoute route)
    {
        return Uri.TryCreate(route.SharePointUrl, UriKind.Absolute, out var sharePointUri) &&
               SharePointRouteParser.IsAllowedSharePointUri(sharePointUri)
            ? sharePointUri
            : null;
    }

    private static async Task<string> BuildCookieHeaderAsync(CoreWebView2 coreWebView, Uri endpoint)
    {
        var cookies = await coreWebView.CookieManager.GetCookiesAsync(endpoint.GetLeftPart(UriPartial.Authority));
        return string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }
}
