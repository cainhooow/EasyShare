using System.Net.Http.Headers;
using System.Text.Json;
using EasyShare.Models;
using EasyShare.Resources;

namespace EasyShare.Services;

public sealed class GraphSharePointService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpClient SharedHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly IAuthenticationService _authentication;
    private readonly HttpClient _httpClient;
    private EnterprisePolicy _enterprisePolicy = new();

    public GraphSharePointService(IAuthenticationService authentication)
        : this(authentication, SharedHttpClient)
    {
    }

    internal GraphSharePointService(IAuthenticationService authentication, HttpClient httpClient)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public void ConfigureEnterprisePolicy(EnterprisePolicy policy) =>
        _enterprisePolicy = policy ?? throw new ArgumentNullException(nameof(policy));

    public async Task<RouteTestResult> TestRouteAsync(
        DriveRoute route,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(route.SharePointUrl, UriKind.Absolute, out var sharePointUri) ||
            !SharePointRouteParser.IsAllowedSharePointUri(sharePointUri) ||
            !IsHostAllowed(sharePointUri))
        {
            return new RouteTestResult(false, AppText.Get("BrowserRouteInvalid"));
        }

        var token = await _authentication.GetAccessTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new RouteTestResult(false, AppText.Get("GraphSignInBeforeTest"));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            if (route.HasGraphIdentity)
            {
                using var pinnedChildrenDocument = await GetJsonAsync(BuildPinnedChildrenUrl(route), token, timeout.Token);
                return new RouteTestResult(true, AppText.Get("BrowserRouteConnected"));
            }

            using var siteDocument = await GetJsonAsync(BuildSiteUrl(sharePointUri), token, timeout.Token);
            if (!siteDocument.RootElement.TryGetProperty("id", out var siteIdProperty))
            {
                return new RouteTestResult(false, AppText.Get("BrowserRouteNotFound"));
            }

            var siteId = siteIdProperty.GetString();
            if (string.IsNullOrWhiteSpace(siteId))
            {
                return new RouteTestResult(false, AppText.Get("BrowserRouteNotFound"));
            }

            using var childrenDocument = await GetJsonAsync(BuildChildrenUrl(siteId, route.RemotePath), token, timeout.Token);
            return new RouteTestResult(true, AppText.Get("BrowserRouteConnected"));
        }
        catch (HttpRequestException ex)
        {
            return new RouteTestResult(false, TranslateGraphError(ex));
        }
        catch (JsonException)
        {
            return new RouteTestResult(false, AppText.Get("GraphUnexpectedResponse"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RouteTestResult(false, AppText.Get("GraphRequestTimedOut"));
        }
    }

    private async Task<JsonDocument> GetJsonAsync(
        string url,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Microsoft Graph route validation failed.", null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string BuildSiteUrl(Uri sharePointUri)
    {
        var sitePath = sharePointUri.AbsolutePath.TrimEnd('/');
        return string.IsNullOrWhiteSpace(sitePath) || sitePath == "/"
            ? $"https://graph.microsoft.com/v1.0/sites/{sharePointUri.Host}:/"
            : $"https://graph.microsoft.com/v1.0/sites/{sharePointUri.Host}:{sitePath}";
    }

    private static string BuildChildrenUrl(string siteId, string remotePath)
    {
        var path = NormalizeRemotePath(remotePath);
        return path == "/"
            ? $"https://graph.microsoft.com/v1.0/sites/{Uri.EscapeDataString(siteId)}/drive/root/children?$top=1"
            : $"https://graph.microsoft.com/v1.0/sites/{Uri.EscapeDataString(siteId)}/drive/root:/{EscapeGraphPath(path)}:/children?$top=1";
    }

    private static string BuildPinnedChildrenUrl(DriveRoute route)
    {
        var driveId = Uri.EscapeDataString(route.DriveId.Trim());
        var itemSegment = string.Equals(route.RootItemId.Trim(), "root", StringComparison.OrdinalIgnoreCase)
            ? "root"
            : $"items/{Uri.EscapeDataString(route.RootItemId.Trim())}";
        return $"https://graph.microsoft.com/v1.0/drives/{driveId}/{itemSegment}/children?$top=1&$select=id";
    }

    private static string NormalizeRemotePath(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath) || remotePath.Trim() == "/")
        {
            return "/";
        }

        return remotePath.Trim().Trim('/');
    }

    private static string EscapeGraphPath(string path) =>
        string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

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

    private static string TranslateGraphError(HttpRequestException ex) =>
        ex.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => AppText.Get("BrowserRouteExpired"),
            System.Net.HttpStatusCode.Forbidden => AppText.Get("GraphForbiddenFolder"),
            System.Net.HttpStatusCode.NotFound => AppText.Get("GraphPathNotFound"),
            _ => AppText.Get("GraphCannotValidate")
        };
}
