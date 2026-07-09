using System.Net.Http.Headers;
using System.Text.Json;
using EasyShare.Models;
using EasyShare.Resources;

namespace EasyShare.Services;

public sealed class GraphSharePointService
{
    private readonly IAuthenticationService _authentication;
    private readonly HttpClient _httpClient = new();

    public GraphSharePointService(IAuthenticationService authentication)
    {
        _authentication = authentication;
    }

    public async Task<RouteTestResult> TestRouteAsync(DriveRoute route)
    {
        var token = await _authentication.GetAccessTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new RouteTestResult(false, AppText.Get("GraphSignInBeforeTest"));
        }

        if (!Uri.TryCreate(route.SharePointUrl, UriKind.Absolute, out var sharePointUri) ||
            !sharePointUri.Host.Contains("sharepoint.com", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteTestResult(false, AppText.Get("BrowserRouteInvalid"));
        }

        try
        {
            using var siteDocument = await GetJsonAsync(BuildSiteUrl(sharePointUri), token);
            if (!siteDocument.RootElement.TryGetProperty("id", out var siteIdProperty))
            {
                return new RouteTestResult(false, AppText.Get("BrowserRouteNotFound"));
            }

            var siteId = siteIdProperty.GetString();
            if (string.IsNullOrWhiteSpace(siteId))
            {
                return new RouteTestResult(false, AppText.Get("BrowserRouteNotFound"));
            }

            using var _ = await GetJsonAsync(BuildChildrenUrl(siteId, route.RemotePath), token);
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
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(body, null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
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

    private static string TranslateGraphError(HttpRequestException ex) =>
        ex.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => AppText.Get("BrowserRouteExpired"),
            System.Net.HttpStatusCode.Forbidden => AppText.Get("GraphForbiddenFolder"),
            System.Net.HttpStatusCode.NotFound => AppText.Get("GraphPathNotFound"),
            _ => AppText.Get("GraphCannotValidate")
        };
}
