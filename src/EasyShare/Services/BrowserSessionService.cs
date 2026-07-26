using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using EasyShare.Models;
using EasyShare.Resources;
using Microsoft.Web.WebView2.Core;

namespace EasyShare.Services;

public sealed class BrowserSessionService
{
    private readonly AppDataPaths _paths;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private long _sessionGeneration;

    public BrowserSessionService(AppDataPaths paths)
    {
        _paths = paths;
    }

    public string ProfilePath => _paths.BrowserProfilePath;

    private string CleanupMarkerPath => $"{ProfilePath}.clear.pending";

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
        var expectedGeneration = Volatile.Read(ref _sessionGeneration);
        await _sessionGate.WaitAsync();
        try
        {
            if (expectedGeneration != Volatile.Read(ref _sessionGeneration))
            {
                return new RouteTestResult(false, AppText.Get("BrowserRouteNeedLogin"));
            }

            return await TestRouteCoreAsync(
                route,
                coreWebView,
                expectedGeneration,
                CancellationToken.None);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<RouteTestResult> KeepAliveAsync(
        IEnumerable<DriveRoute> routes,
        CoreWebView2 coreWebView,
        Action? identityChanging = null,
        CancellationToken cancellationToken = default)
    {
        var expectedGeneration = Volatile.Read(ref _sessionGeneration);
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedGeneration != Volatile.Read(ref _sessionGeneration))
            {
                return new RouteTestResult(false, AppText.Get("BrowserRouteNeedLogin"));
            }

            var generation = expectedGeneration;
            var verifiedRoutes = routes
                .Select(TryGetSharePointUri)
                .Where(siteUri => siteUri is not null && SharePointCookieStore.IsRouteVerified(siteUri))
                .Select(siteUri => siteUri!)
                .GroupBy(
                    siteUri => siteUri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            if (verifiedRoutes.Length == 0)
            {
                return new RouteTestResult(false, AppText.Get("BrowserRouteNeedLogin"));
            }

            RouteTestResult? firstFailure = null;
            var currentHeaders = new List<(Uri SiteUri, string? CookieHeader)>();
            var identityTransitionRequired = false;
            foreach (var siteUri in verifiedRoutes)
            {
                var endpoint = BuildSharePointRestEndpoint(siteUri);
                try
                {
                    var currentCookieHeader = await BuildCookieHeaderAsync(coreWebView, endpoint);
                    cancellationToken.ThrowIfCancellationRequested();
                    currentHeaders.Add((siteUri, currentCookieHeader));
                    var hasStoredCookie = SharePointCookieStore.TryGetCookieHeader(
                        siteUri,
                        out var storedCookieHeader);
                    identityTransitionRequired |= !hasStoredCookie ||
                                                  !AreCookieHeadersEquivalent(
                                                      storedCookieHeader,
                                                      currentCookieHeader);
                }
                catch (Exception exception) when (
                    exception is COMException or InvalidOperationException)
                {
                    identityTransitionRequired = true;
                    currentHeaders.Add((siteUri, null));
                    firstFailure ??= new RouteTestResult(
                        false,
                        AppText.Get("BrowserRouteUnavailable"));
                }
            }

            if (generation != Volatile.Read(ref _sessionGeneration))
            {
                return new RouteTestResult(false, AppText.Get("BrowserRouteNeedLogin"));
            }

            if (identityTransitionRequired)
            {
                if (!TryInvalidatePublishedSession(generation, out generation))
                {
                    return new RouteTestResult(false, AppText.Get("BrowserRouteNeedLogin"));
                }

                identityChanging?.Invoke();
                if (generation != Volatile.Read(ref _sessionGeneration))
                {
                    return new RouteTestResult(false, AppText.Get("BrowserRouteNeedLogin"));
                }
            }

            var validCount = 0;
            foreach (var (siteUri, currentCookieHeader) in currentHeaders)
            {
                if (string.IsNullOrWhiteSpace(currentCookieHeader))
                {
                    if (generation == Volatile.Read(ref _sessionGeneration))
                    {
                        SharePointCookieStore.RemoveHost(siteUri);
                    }

                    firstFailure ??= new RouteTestResult(
                        false,
                        AppText.Get("BrowserRouteNeedLogin"));
                    continue;
                }

                var result = await ValidateCookieAsync(
                    siteUri,
                    currentCookieHeader!,
                    generation,
                    cancellationToken);
                if (result.Success)
                {
                    validCount++;
                }
                else
                {
                    firstFailure ??= result;
                }
            }

            return validCount > 0
                ? new RouteTestResult(true, AppText.Get("BrowserRouteConnected"))
                : firstFailure ?? new RouteTestResult(false, AppText.Get("BrowserRouteNeedLogin"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            InvalidatePublishedSession();
            throw;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<RouteTestResult> RestoreSessionAsync(
        IEnumerable<DriveRoute> routes,
        CoreWebView2 coreWebView,
        CancellationToken cancellationToken = default)
    {
        var expectedGeneration = Volatile.Read(ref _sessionGeneration);
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

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedGeneration != Volatile.Read(ref _sessionGeneration))
            {
                return new RouteTestResult(false, AppText.Get("BrowserRouteNeedLogin"));
            }

            var generation = expectedGeneration;
            SharePointCookieStore.Clear();
            RouteTestResult? firstFailure = null;
            var restoredCount = 0;
            foreach (var item in sharePointRoutes)
            {
                var result = await TestRouteCoreAsync(
                    item.Route,
                    coreWebView,
                    generation,
                    cancellationToken);
                if (result.Success)
                {
                    restoredCount++;
                }
                else
                {
                    firstFailure ??= result;
                }
            }

            return restoredCount > 0
                ? new RouteTestResult(true, AppText.Get("BrowserRouteConnected"))
                : firstFailure ?? new RouteTestResult(false, AppText.Get("BrowserRouteNeedLogin"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            InvalidatePublishedSession();
            throw;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<string?> GetAccountScopeAsync(
        IEnumerable<DriveRoute> routes,
        CancellationToken cancellationToken = default)
    {
        var expectedGeneration = Volatile.Read(ref _sessionGeneration);
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (expectedGeneration != Volatile.Read(ref _sessionGeneration))
            {
                return null;
            }

            var generation = expectedGeneration;
            var identityScopes = new HashSet<string>(StringComparer.Ordinal);
            var verifiedSites = routes
                .Select(TryGetSharePointUri)
                .Where(siteUri => siteUri is not null && SharePointCookieStore.IsRouteVerified(siteUri))
                .Select(siteUri => siteUri!)
                .GroupBy(
                    siteUri => siteUri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            foreach (var siteUri in verifiedSites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SharePointCookieStore.TryGetCookieHeader(siteUri, out var cookieHeader))
                {
                    return null;
                }

                var siteRoot = siteUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
                var endpoint = new Uri($"{siteRoot}/_api/web/currentuser?$select=LoginName");
                using var handler = CreateHttpHandler();
                using var httpClient = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(20)
                };
                using var request = CreateRequest(endpoint, cookieHeader);

                try
                {
                    using var response = await httpClient
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    await using var stream = await response.Content
                        .ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    using var document = await JsonDocument
                        .ParseAsync(stream, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    var user = document.RootElement.TryGetProperty("d", out var legacy)
                        ? legacy
                        : document.RootElement;
                    var loginName = ReadJsonValue(user, "LoginName");
                    if (string.IsNullOrWhiteSpace(loginName))
                    {
                        return null;
                    }

                    identityScopes.Add(ContentIdentityScope.FromBrowserIdentity(siteUri.Host, loginName));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or IOException or JsonException)
                {
                    return null;
                }
            }

            if (generation != Volatile.Read(ref _sessionGeneration))
            {
                return null;
            }

            var compositeScope = ContentIdentityScope.FromBrowserIdentitySet(identityScopes);
            return string.IsNullOrWhiteSpace(compositeScope) ? null : compositeScope;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<bool> ClearSessionAsync(CoreWebView2 coreWebView)
    {
        InvalidatePublishedSession();
        await _sessionGate.WaitAsync();
        var profileCleared = true;
        try
        {
            coreWebView.CookieManager.DeleteAllCookies();
            try
            {
                await coreWebView.Profile.ClearBrowsingDataAsync();
            }
            catch
            {
                // WebView may reject profile cleanup while navigation is active; cookies are still removed above.
                profileCleared = false;
            }

            SharePointCookieStore.Clear();
        }
        finally
        {
            _sessionGate.Release();
        }

        if (!profileCleared)
        {
            MarkCleanupPending();
        }

        return profileCleared;
    }

    public void ClearStoredSession()
    {
        InvalidatePublishedSession();
        // There is no active CoreWebView2 instance here, so leave a durable
        // request instead of recursively traversing the profile. The global
        // reset service removes only its authorized roots without following
        // reparse points; otherwise this marker asks WebView2 to clear the
        // profile through its API on the next initialization.
        MarkCleanupPending();
    }

    public void InvalidatePublishedSession()
    {
        Interlocked.Increment(ref _sessionGeneration);
        SharePointCookieStore.Clear();
    }

    private bool TryInvalidatePublishedSession(long expectedGeneration, out long newGeneration)
    {
        newGeneration = unchecked(expectedGeneration + 1);
        if (Interlocked.CompareExchange(
                ref _sessionGeneration,
                newGeneration,
                expectedGeneration) != expectedGeneration)
        {
            newGeneration = Volatile.Read(ref _sessionGeneration);
            return false;
        }

        SharePointCookieStore.Clear();
        return true;
    }

    public async Task ApplyPendingSessionCleanupAsync(CoreWebView2 coreWebView)
    {
        if (!File.Exists(CleanupMarkerPath))
        {
            return;
        }

        if (await ClearSessionAsync(coreWebView))
        {
            TryDeleteCleanupMarker();
        }
    }

    private async Task<RouteTestResult> TestRouteCoreAsync(
        DriveRoute route,
        CoreWebView2 coreWebView,
        long generation,
        CancellationToken cancellationToken)
    {
        var sharePointUri = TryGetSharePointUri(route);
        if (sharePointUri is null)
        {
            return new RouteTestResult(false, AppText.Get("BrowserRouteInvalid"));
        }

        var endpoint = BuildSharePointRestEndpoint(sharePointUri);
        string cookieHeader;
        try
        {
            cookieHeader = await BuildCookieHeaderAsync(coreWebView, endpoint).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            if (generation == Volatile.Read(ref _sessionGeneration))
            {
                SharePointCookieStore.RemoveHost(sharePointUri);
            }

            return new RouteTestResult(false, AppText.Get("BrowserRouteUnavailable"));
        }
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            if (generation == Volatile.Read(ref _sessionGeneration))
            {
                SharePointCookieStore.RemoveHost(sharePointUri);
            }

            return new RouteTestResult(false, AppText.Get("BrowserRouteNeedLogin"));
        }

        return await ValidateCookieAsync(
                sharePointUri,
                cookieHeader,
                generation,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<RouteTestResult> ValidateCookieAsync(
        Uri sharePointUri,
        string cookieHeader,
        long generation,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildSharePointRestEndpoint(sharePointUri);
        using var handler = CreateHttpHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        using var request = CreateRequest(endpoint, cookieHeader);
        try
        {
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var result = CreateRouteTestResult(response.StatusCode);
            if (generation == Volatile.Read(ref _sessionGeneration))
            {
                if (result.Success)
                {
                    SharePointCookieStore.SetCookieHeader(endpoint, cookieHeader);
                    SharePointCookieStore.MarkRouteVerified(sharePointUri);
                }
                else
                {
                    SharePointCookieStore.UnmarkRouteVerified(sharePointUri);
                }
            }

            return result;
        }
        catch (HttpRequestException)
        {
            if (generation == Volatile.Read(ref _sessionGeneration))
            {
                SharePointCookieStore.UnmarkRouteVerified(sharePointUri);
            }

            return new RouteTestResult(false, AppText.Get("BrowserRouteUnavailable"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (generation == Volatile.Read(ref _sessionGeneration))
            {
                SharePointCookieStore.UnmarkRouteVerified(sharePointUri);
            }

            return new RouteTestResult(false, AppText.Get("BrowserRouteUnavailable"));
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

    private static bool AreCookieHeadersEquivalent(string left, string right) =>
        NormalizeCookieHeader(left).SequenceEqual(
            NormalizeCookieHeader(right),
            StringComparer.Ordinal);

    private static IEnumerable<string> NormalizeCookieHeader(string header) =>
        header
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .OrderBy(value => value, StringComparer.Ordinal);

    private static HttpClientHandler CreateHttpHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false
    };

    private static HttpRequestMessage CreateRequest(Uri endpoint, string cookieHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", "EasyShare");
        return request;
    }

    private static RouteTestResult CreateRouteTestResult(HttpStatusCode statusCode)
    {
        if (statusCode is HttpStatusCode.Moved or
            HttpStatusCode.Redirect or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect)
        {
            return new RouteTestResult(false, AppText.Get("BrowserRouteNotAuthenticated"));
        }

        return statusCode switch
        {
            HttpStatusCode.OK => new RouteTestResult(true, AppText.Get("BrowserRouteConnected")),
            HttpStatusCode.Unauthorized => new RouteTestResult(false, AppText.Get("BrowserRouteExpired")),
            HttpStatusCode.Forbidden => new RouteTestResult(false, AppText.Get("BrowserRouteForbidden")),
            HttpStatusCode.NotFound => new RouteTestResult(false, AppText.Get("BrowserRouteNotFound")),
            _ => new RouteTestResult(false, AppText.Format("BrowserRouteStatusFormat", (int)statusCode))
        };
    }

    private void MarkCleanupPending()
    {
        try
        {
            _paths.EnsureCreated();
            File.WriteAllText(CleanupMarkerPath, "pending");
        }
        catch
        {
            // The next explicit WebView cleanup remains the fallback.
        }
    }

    private void TryDeleteCleanupMarker()
    {
        try
        {
            if (File.Exists(CleanupMarkerPath))
            {
                File.Delete(CleanupMarkerPath);
            }
        }
        catch
        {
            // A stale marker only causes another safe cleanup attempt.
        }
    }

    private static string ReadJsonValue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
    }
}
