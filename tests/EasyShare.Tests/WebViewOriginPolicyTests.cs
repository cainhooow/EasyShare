using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class WebViewOriginPolicyTests
{
    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/team")]
    [InlineData("https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize")]
    [InlineData("https://www.office.com/?auth=2")]
    [InlineData("https://portal.cloud.microsoft/")]
    [InlineData("https://login.microsoftonline.us/")]
    public void AcceptsMicrosoft365SharePointAndEntraOrigins(string value)
    {
        Assert.True(WebViewOriginPolicy.IsTrustedMicrosoftUri(new Uri(value)));
    }

    [Theory]
    [InlineData("http://contoso.sharepoint.com/")]
    [InlineData("https://contoso.sharepoint.com.evil.example/")]
    [InlineData("https://user@contoso.sharepoint.com/")]
    [InlineData("https://contoso.sharepoint.com:8443/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("file:///C:/secret.txt")]
    public void RejectsUnsafeOrLookalikeOrigins(string value)
    {
        Assert.False(WebViewOriginPolicy.IsTrustedMicrosoftUri(new Uri(value)));
    }

    [Fact]
    public void AllowsNonInteractiveFederationOnlyFromAnIdentityAuthority()
    {
        var identityAuthority = new Uri("https://login.microsoftonline.com/organizations/saml2");
        var federatedIdentityProvider = new Uri("https://login.contoso.example/adfs/ls/");

        Assert.True(WebViewOriginPolicy.CanBeginFederatedSignIn(
            identityAuthority,
            federatedIdentityProvider,
            isUserInitiated: false));
        Assert.False(WebViewOriginPolicy.CanBeginFederatedSignIn(
            identityAuthority,
            federatedIdentityProvider,
            isUserInitiated: true));
        Assert.False(WebViewOriginPolicy.CanBeginFederatedSignIn(
            new Uri("https://contoso.sharepoint.com/"),
            federatedIdentityProvider,
            isUserInitiated: false));
    }
}
