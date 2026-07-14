using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EasyShare.Models;
using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class GraphSharePointExplorerServiceTests
{
    [Fact]
    public async Task DiscoveryCombinesPagesDeduplicatesAndCachesByTenantAndUserClaims()
    {
        var calls = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((request, _) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/me/followedSites", StringComparison.Ordinal))
            {
                return Json("""
                    {"value":[{"id":"site-a","displayName":"Alpha","webUrl":"https://contoso.sharepoint.com/sites/alpha","description":"Followed"}],
                     "@odata.nextLink":"https://graph.microsoft.com/v1.0/test/followed-page-2"}
                    """);
            }

            if (path.EndsWith("/test/followed-page-2", StringComparison.Ordinal))
            {
                return Json("""
                    {"value":[{"id":"site-b","displayName":"Beta","webUrl":"https://contoso.sharepoint.com/sites/beta"}]}
                    """);
            }

            return Json("""
                {"value":[
                    {"id":"search-copy","displayName":"Alpha duplicate","webUrl":"https://contoso.sharepoint.com/sites/alpha/"},
                    {"id":"site-c","displayName":"Gamma","webUrl":"https://contoso.sharepoint.com/sites/gamma"}]}
                """);
        }));
        var authentication = new FakeAuthentication(CreateToken("tenant-1", "user-1", "signature-one"));
        var service = new GraphSharePointExplorerService(authentication, client);

        var first = await service.DiscoverSitesAsync();
        authentication.Token = CreateToken("tenant-1", "user-1", "signature-two");
        var second = await service.DiscoverSitesAsync();

        Assert.Equal(3, first.Count);
        Assert.Equal(first, second);
        Assert.True(first.Single(site => site.Id == "site-a").IsFollowed);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task DiscoveryReturnsTypedAuthenticationFailureWithoutSendingARequest()
    {
        var calls = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Json("{\"value\":[]}");
        }));
        var service = new GraphSharePointExplorerService(new FakeAuthentication(null), client);

        var exception = await Assert.ThrowsAsync<SharePointExplorerException>(() =>
            service.DiscoverSitesAsync());

        Assert.Equal(SharePointExplorerStatus.AuthenticationRequired, exception.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DiscoveryExposesTypedThrottlingAndRetryAfter()
    {
        using var client = new HttpClient(new AsyncCallbackHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        }));
        var service = new GraphSharePointExplorerService(
            new FakeAuthentication(CreateToken("tenant", "user", "signature")),
            client);

        var exception = await Assert.ThrowsAsync<SharePointExplorerException>(() =>
            service.DiscoverSitesAsync("finance"));

        Assert.Equal(SharePointExplorerStatus.Throttled, exception.Status);
        Assert.Equal(TimeSpan.FromSeconds(30), exception.RetryAfter);
    }

    [Fact]
    public async Task PartialWildcardDiscoveryIsReturnedButNotCached()
    {
        var searchCalls = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/me/followedSites", StringComparison.Ordinal))
            {
                return Json("""
                    {"value":[{"id":"followed","displayName":"Followed","webUrl":"https://contoso.sharepoint.com/sites/followed"}]}
                    """);
            }

            if (Interlocked.Increment(ref searchCalls) == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }

            return Json("""
                {"value":[{"id":"searched","displayName":"Searched","webUrl":"https://contoso.sharepoint.com/sites/searched"}]}
                """);
        }));
        var service = CreateService(client);

        var partial = await service.DiscoverSitesAsync();
        var complete = await service.DiscoverSitesAsync();

        Assert.Equal("followed", Assert.Single(partial).Id);
        Assert.Equal(2, complete.Count);
        Assert.Equal(2, searchCalls);
    }

    [Fact]
    public async Task LibrariesFollowPaginationAndPreferResolvedRootItemIds()
    {
        using var client = new HttpClient(new AsyncCallbackHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/drives/drive-a/root", StringComparison.Ordinal))
            {
                return Json("{\"id\":\"root-a\"}");
            }

            if (path.EndsWith("/drives/drive-b/root", StringComparison.Ordinal))
            {
                return Json("{\"id\":\"root-b\"}");
            }

            if (path.EndsWith("/test/drives-page-2", StringComparison.Ordinal))
            {
                return Json("""
                    {"value":[{"id":"drive-b","name":"Shared","webUrl":"https://contoso.sharepoint.com/shared"}]}
                    """);
            }

            return Json("""
                {"value":[{"id":"drive-a","name":"Documents","webUrl":"https://contoso.sharepoint.com/docs"}],
                 "@odata.nextLink":"https://graph.microsoft.com/v1.0/test/drives-page-2"}
                """);
        }));
        var service = CreateService(client);

        var libraries = await service.GetLibrariesAsync("contoso,site-id,web-id");

        Assert.Equal(2, libraries.Count);
        Assert.Equal("root-a", libraries.Single(item => item.Id == "drive-a").RootItemId);
        Assert.Equal("root-b", libraries.Single(item => item.Id == "drive-b").RootItemId);
        Assert.All(libraries, item => Assert.Equal("contoso,site-id,web-id", item.SiteId));
    }

    [Fact]
    public async Task ResolveFolderFromManualUrlResolvesSiteLibraryRootAndFolderIdentity()
    {
        var requestedPaths = new ConcurrentQueue<string>();
        using var client = new HttpClient(new AsyncCallbackHandler((request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            requestedPaths.Enqueue(request.RequestUri!.PathAndQuery);
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/sites/contoso.sharepoint.com:/sites/Finance", StringComparison.Ordinal))
            {
                return Json("""
                    {"id":"contoso.sharepoint.com,site-id,web-id","displayName":"Finance","webUrl":"https://contoso.sharepoint.com/sites/Finance"}
                    """);
            }

            if (path.EndsWith("/sites/contoso.sharepoint.com%2Csite-id%2Cweb-id/drives", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/sites/contoso.sharepoint.com,site-id,web-id/drives", StringComparison.OrdinalIgnoreCase))
            {
                return Json("""
                    {"value":[
                        {"id":"drive-assets","name":"Assets","webUrl":"https://contoso.sharepoint.com/sites/Finance/Assets"},
                        {"id":"drive-documents","name":"Shared Documents","webUrl":"https://contoso.sharepoint.com/sites/Finance/Shared%20Documents"}]}
                    """);
            }

            if (path.EndsWith("/drives/drive-assets/root", StringComparison.Ordinal))
            {
                return Json("{\"id\":\"root-assets\"}");
            }

            if (path.EndsWith("/drives/drive-documents/root", StringComparison.Ordinal))
            {
                return Json("{\"id\":\"root-documents\"}");
            }

            if (path.Contains("/drives/drive-documents/root:/Quarterly/2026", StringComparison.Ordinal))
            {
                return Json("""
                    {"id":"folder-2026","name":"2026","webUrl":"https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/Quarterly/2026","folder":{"childCount":3}}
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var service = CreateService(client);
        Assert.True(SharePointRouteParser.TryParse(
            "https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/Quarterly/2026",
            out var route));

        var folder = await service.ResolveFolderAsync(route);

        Assert.Equal("contoso.sharepoint.com,site-id,web-id", folder.SiteId);
        Assert.Equal("drive-documents", folder.DriveId);
        Assert.Equal("folder-2026", folder.ItemId);
        Assert.Equal("2026", folder.DisplayName);
        Assert.Equal("https://contoso.sharepoint.com/sites/Finance", folder.SiteWebUrl);
        Assert.Equal(
            "https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/Quarterly/2026",
            folder.FolderWebUrl);
        Assert.Equal("/Shared Documents/Quarterly/2026", folder.DisplayPath);
        Assert.DoesNotContain(
            requestedPaths,
            requestPath => requestPath.Contains("drive-assets/root:", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Gate", "Security")]
    public async Task ResolveFolderRejectsInvalidPathBeforeLeakingAuthorization()
    {
        var calls = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((request, _) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Null(request.Headers.Authorization);
            return Json("{}");
        }));
        var service = CreateService(client);
        var siteUri = new Uri("https://contoso.sharepoint.com/sites/Finance");
        var route = new SharePointRouteInput(
            siteUri,
            siteUri.AbsoluteUri.TrimEnd('/'),
            "Shared Documents/%2e%2e/Secrets",
            "Secrets",
            siteUri);

        var exception = await Assert.ThrowsAsync<SharePointExplorerException>(() =>
            service.ResolveFolderAsync(route));

        Assert.Equal(SharePointExplorerStatus.InvalidResponse, exception.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    [Trait("Gate", "Security")]
    public async Task ResolveFolderRejectsAdministrativelyBlockedHostBeforeLeakingAuthorization()
    {
        var calls = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((request, _) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Null(request.Headers.Authorization);
            return Json("{}");
        }));
        var service = CreateService(client);
        service.ConfigureEnterprisePolicy(new EnterprisePolicy
        {
            AllowedSharePointHosts = ["fabrikam.sharepoint.com"]
        });
        Assert.True(SharePointRouteParser.TryParse(
            "https://contoso.sharepoint.com/sites/Finance/Shared%20Documents",
            out var route));

        var exception = await Assert.ThrowsAsync<SharePointExplorerException>(() =>
            service.ResolveFolderAsync(route));

        Assert.Equal(SharePointExplorerStatus.Forbidden, exception.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task LibraryRootResolutionPropagatesThrottlingInsteadOfMaskingIt()
    {
        using var client = new HttpClient(new AsyncCallbackHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/drives/drive-a/root", StringComparison.Ordinal))
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(15));
                return throttled;
            }

            return Json("""
                {"value":[{"id":"drive-a","name":"Documents","webUrl":"https://contoso.sharepoint.com/docs"}]}
                """);
        }));
        var service = CreateService(client);

        var exception = await Assert.ThrowsAsync<SharePointExplorerException>(() =>
            service.GetLibrariesAsync("contoso,site-id,web-id"));

        Assert.Equal(SharePointExplorerStatus.Throttled, exception.Status);
        Assert.Equal(TimeSpan.FromSeconds(15), exception.RetryAfter);
    }

    [Fact]
    public async Task ChildrenReturnFoldersFirstAndAValidatedNextLink()
    {
        using var client = new HttpClient(new AsyncCallbackHandler((_, _) => Json("""
            {"value":[
                {"id":"file-1","name":"notes.txt","webUrl":"https://contoso.sharepoint.com/notes.txt","size":12,"lastModifiedDateTime":"2026-07-13T12:00:00Z","file":{}},
                {"id":"folder-1","name":"Reports","webUrl":"https://contoso.sharepoint.com/Reports","size":0,"lastModifiedDateTime":"2026-07-13T11:00:00Z","folder":{}}],
             "@odata.nextLink":"https://graph.microsoft.com/v1.0/drives/drive/items/root/children?$skiptoken=next"}
            """)));
        var service = CreateService(client);

        var page = await service.GetChildrenAsync("drive", "root");

        Assert.Equal("Reports", page.Items[0].Name);
        Assert.True(page.Items[0].IsFolder);
        Assert.Contains("$skiptoken=next", page.NextLink);
    }

    [Fact]
    [Trait("Gate", "Security")]
    public async Task ChildrenRejectAnUntrustedNextLinkBeforeLeakingAuthorization()
    {
        var calls = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Json("{\"value\":[]}");
        }));
        var service = CreateService(client);

        var exception = await Assert.ThrowsAsync<SharePointExplorerException>(() =>
            service.GetChildrenAsync("drive", "root", "https://attacker.example/collect"));

        Assert.Equal(SharePointExplorerStatus.InvalidResponse, exception.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    [Trait("Gate", "Security")]
    public async Task ExplorerRejectsDotSegmentIdentifiersBeforeLeakingAuthorization()
    {
        var calls = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Json("{\"value\":[]}");
        }));
        var service = CreateService(client);

        var exception = await Assert.ThrowsAsync<SharePointExplorerException>(() =>
            service.GetChildrenAsync("drive", ".."));

        Assert.Equal(SharePointExplorerStatus.InvalidResponse, exception.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DiscoveryMapsResponseBodyTimeoutToTypedServiceUnavailable()
    {
        using var client = new HttpClient(new AsyncCallbackHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new CancellationOnlyStream())
            }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var service = new GraphSharePointExplorerService(
            new FakeAuthentication(CreateToken("tenant", "user", "signature")),
            client,
            operationTimeout: TimeSpan.FromMilliseconds(30));

        var exception = await Assert.ThrowsAsync<SharePointExplorerException>(() =>
            service.DiscoverSitesAsync());

        Assert.Equal(SharePointExplorerStatus.ServiceUnavailable, exception.Status);
    }

    [Fact]
    [Trait("Gate", "Security")]
    public async Task DiscoveryFiltersInvalidAndAdministrativelyDisallowedHostsEvenFromCache()
    {
        var calls = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Json("""
                {"value":[
                    {"id":"contoso","displayName":"Contoso","webUrl":"https://contoso.sharepoint.com/sites/team"},
                    {"id":"fabrikam","displayName":"Fabrikam","webUrl":"https://fabrikam.sharepoint.com/sites/team"},
                    {"id":"attacker","displayName":"Attacker","webUrl":"http://attacker.example/sites/team"}]}
                """);
        }));
        var service = CreateService(client);

        var beforePolicy = await service.DiscoverSitesAsync();
        service.ConfigureEnterprisePolicy(new EnterprisePolicy
        {
            AllowedSharePointHosts = ["contoso.sharepoint.com"]
        });
        var afterPolicy = await service.DiscoverSitesAsync();

        Assert.Equal(2, beforePolicy.Count);
        Assert.Equal("contoso", Assert.Single(afterPolicy).Id);
        Assert.Equal(2, calls);
    }

    [Fact]
    [Trait("Gate", "Security")]
    public async Task DiscoveryCacheIsPartitionedByClientAndDelegatedScopesAsWellAsTenantAndUser()
    {
        var calls = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Json("{\"value\":[]}");
        }));
        var authentication = new FakeAuthentication(CreateToken(
            "tenant",
            "user",
            "signature-1",
            authorizedParty: "client-a",
            scopes: "Files.ReadWrite.All Sites.Read.All"));
        var service = new GraphSharePointExplorerService(authentication, client);

        await service.DiscoverSitesAsync();
        authentication.Token = CreateToken(
            "tenant",
            "user",
            "signature-2",
            authorizedParty: "client-a",
            scopes: "Files.ReadWrite.All Sites.Read.All");
        await service.DiscoverSitesAsync();
        Assert.Equal(2, calls);

        authentication.Token = CreateToken(
            "tenant",
            "user",
            "signature-3",
            appId: "client-b",
            scopes: "Files.ReadWrite.All Sites.Read.All");
        await service.DiscoverSitesAsync();
        Assert.Equal(4, calls);

        authentication.Token = CreateToken(
            "tenant",
            "user",
            "signature-4",
            appId: "client-b",
            scopes: "Sites.Read.All");
        await service.DiscoverSitesAsync();
        Assert.Equal(6, calls);
    }

    private static GraphSharePointExplorerService CreateService(HttpClient client) =>
        new(new FakeAuthentication(CreateToken("tenant", "user", "signature")), client);

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string CreateToken(
        string tenantId,
        string objectId,
        string signature,
        string? authorizedParty = null,
        string? appId = null,
        string? scopes = null)
    {
        var claims = new List<string>
        {
            $"\"tid\":\"{tenantId}\"",
            $"\"oid\":\"{objectId}\""
        };
        if (!string.IsNullOrWhiteSpace(authorizedParty))
        {
            claims.Add($"\"azp\":\"{authorizedParty}\"");
        }

        if (!string.IsNullOrWhiteSpace(appId))
        {
            claims.Add($"\"appid\":\"{appId}\"");
        }

        if (!string.IsNullOrWhiteSpace(scopes))
        {
            claims.Add($"\"scp\":\"{scopes}\"");
        }

        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{{{string.Join(',', claims)}}}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"e30.{payload}.{signature}";
    }

    private sealed class FakeAuthentication(string? token) : IAuthenticationService
    {
        public string? Token { get; set; } = token;

        public Task<string?> GetAccessTokenAsync() => Task.FromResult(Token);
    }

    private sealed class AsyncCallbackHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(callback(request, cancellationToken));
    }

    private sealed class CancellationOnlyStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
