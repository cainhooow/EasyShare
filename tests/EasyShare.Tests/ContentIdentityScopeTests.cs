using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class ContentIdentityScopeTests
{
    [Fact]
    public void BrowserIdentityIsStableAcrossCasingButPartitionedByHost()
    {
        var first = ContentIdentityScope.FromBrowserIdentity(
            "Contoso.SharePoint.com",
            "i:0#.f|membership|USER@contoso.com");
        var same = ContentIdentityScope.FromBrowserIdentity(
            "contoso.sharepoint.com",
            "i:0#.f|membership|user@CONTOSO.com");
        var otherHost = ContentIdentityScope.FromBrowserIdentity(
            "fabrikam.sharepoint.com",
            "i:0#.f|membership|user@contoso.com");

        Assert.Equal(first, same);
        Assert.NotEqual(first, otherHost);
        Assert.True(ContentIdentityScope.IsPersistentIdentityScope(first));
    }

    [Fact]
    public void BrowserIdentitySetIsOrderIndependentAndIncludesEveryIdentity()
    {
        var first = ContentIdentityScope.FromBrowserIdentity("a.sharepoint.com", "alice@a.com");
        var second = ContentIdentityScope.FromBrowserIdentity("b.sharepoint.com", "bob@b.com");

        var combined = ContentIdentityScope.FromBrowserIdentitySet([first, second, first]);
        var reordered = ContentIdentityScope.FromBrowserIdentitySet([second, first]);

        Assert.Equal(combined, reordered);
        Assert.NotEqual(combined, ContentIdentityScope.FromBrowserIdentitySet([first]));
        Assert.True(ContentIdentityScope.IsPersistentIdentityScope(combined));
    }

    [Fact]
    public void GraphAccountScopeIncludesClientRegistration()
    {
        var first = ContentIdentityScope.FromGraphAccount("uid.utid", "client-a");
        var same = ContentIdentityScope.FromGraphAccount("UID.UTID", "CLIENT-A");
        var otherClient = ContentIdentityScope.FromGraphAccount("uid.utid", "client-b");

        Assert.Equal(first, same);
        Assert.NotEqual(first, otherClient);
        Assert.False(ContentIdentityScope.IsPersistentIdentityScope("GRAPH-SESSION-temporary"));
    }
}
