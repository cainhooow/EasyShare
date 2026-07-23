using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class SharePointCookieStoreTests : IDisposable
{
    public SharePointCookieStoreTests() => SharePointCookieStore.Clear();

    [Fact]
    public void ReplacingHostCookieInvalidatesEveryPreviouslyVerifiedRouteOnThatHost()
    {
        var firstRoute = new Uri("https://contoso.sharepoint.com/sites/finance");
        var secondRoute = new Uri("https://contoso.sharepoint.com/sites/legal");
        SharePointCookieStore.SetCookieHeader(firstRoute, "FedAuth=old");
        SharePointCookieStore.MarkRouteVerified(firstRoute);
        SharePointCookieStore.MarkRouteVerified(secondRoute);

        SharePointCookieStore.SetCookieHeader(firstRoute, "FedAuth=new");

        Assert.False(SharePointCookieStore.IsRouteVerified(firstRoute));
        Assert.False(SharePointCookieStore.IsRouteVerified(secondRoute));
        Assert.True(SharePointCookieStore.TryGetCookieHeader(firstRoute, out var header));
        Assert.Equal("FedAuth=new", header);
    }

    [Fact]
    public void ReusingSameCookiePreservesIndependentRouteVerification()
    {
        var firstRoute = new Uri("https://contoso.sharepoint.com/sites/finance");
        var secondRoute = new Uri("https://contoso.sharepoint.com/sites/legal");
        SharePointCookieStore.SetCookieHeader(firstRoute, "FedAuth=same");
        SharePointCookieStore.MarkRouteVerified(firstRoute);
        SharePointCookieStore.SetCookieHeader(secondRoute, "FedAuth=same");
        SharePointCookieStore.MarkRouteVerified(secondRoute);

        Assert.True(SharePointCookieStore.IsRouteVerified(firstRoute));
        Assert.True(SharePointCookieStore.IsRouteVerified(secondRoute));
    }

    public void Dispose() => SharePointCookieStore.Clear();
}
