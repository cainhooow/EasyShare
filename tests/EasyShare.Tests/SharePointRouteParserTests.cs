using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class SharePointRouteParserTests
{
    [Theory]
    [InlineData("http://contoso.sharepoint.com/sites/team")]
    [InlineData("https://sharepoint.com.evil.test/sites/team")]
    [InlineData("https://contoso.sharepoint.com.evil.test")]
    public void RejectsNonSharePointOrUnsafeHosts(string value)
    {
        Assert.False(SharePointRouteParser.TryParse(value, out _));
    }

    [Fact]
    public void AcceptsSharePointSubdomainAndNormalizesFolder()
    {
        var parsed = SharePointRouteParser.TryParse(
            "https://contoso.sharepoint.com/sites/team/Shared%20Documents/Finance",
            out var route);

        Assert.True(parsed);
        Assert.Equal("https://contoso.sharepoint.com/sites/team", route.SiteUrl);
        Assert.Equal("Shared Documents/Finance", route.RemotePath);
        Assert.Equal("Finance", route.SuggestedName);
    }

    [Fact]
    public void RejectsMissingSchemeInsteadOfSilentlyAllowingHttp()
    {
        Assert.False(SharePointRouteParser.TryParse("http://contoso.sharepoint.com", out _));
    }
}
