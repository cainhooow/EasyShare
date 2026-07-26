using EasyShare.Models;
using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class ExplorerAccessPolicyTests
{
    private const string ValidClientId = "3f317b95-d9a7-4bac-bd9c-589fbaf28a53";

    [Theory]
    [InlineData(ValidClientId, true, true)]
    [InlineData(ValidClientId, false, false)]
    [InlineData(null, true, false)]
    [InlineData("", true, false)]
    [InlineData("not-a-guid", true, false)]
    [InlineData("00000000-0000-0000-0000-000000000000", true, false)]
    public void GraphRequiresAValidClientIdAndRealGraphAuthentication(
        string? clientId,
        bool isGraphAuthenticated,
        bool expected)
    {
        var available = ExplorerAccessPolicy.IsAvailable(
            AuthenticationMode.MicrosoftGraph,
            clientId,
            isGraphAuthenticated,
            isBrowserSessionVerified: true,
            hasAuthenticatableRoute: true);

        Assert.Equal(expected, available);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void BrowserRequiresAVerifiedWebViewSessionAndAnAuthenticatableRoute(
        bool isBrowserSessionVerified,
        bool hasAuthenticatableRoute,
        bool expected)
    {
        var available = ExplorerAccessPolicy.IsAvailable(
            AuthenticationMode.BrowserSession,
            clientId: null,
            isGraphAuthenticated: true,
            isBrowserSessionVerified,
            hasAuthenticatableRoute);

        Assert.Equal(expected, available);
    }

    [Fact]
    public void AuthenticationEvidenceCannotLeakAcrossModes()
    {
        Assert.False(ExplorerAccessPolicy.IsAvailable(
            AuthenticationMode.MicrosoftGraph,
            ValidClientId,
            isGraphAuthenticated: false,
            isBrowserSessionVerified: true,
            hasAuthenticatableRoute: true));

        Assert.False(ExplorerAccessPolicy.IsAvailable(
            AuthenticationMode.BrowserSession,
            ValidClientId,
            isGraphAuthenticated: true,
            isBrowserSessionVerified: false,
            hasAuthenticatableRoute: true));
    }

    [Fact]
    public void UnknownAuthenticationModeFailsClosed()
    {
        var available = ExplorerAccessPolicy.IsAvailable(
            (AuthenticationMode)99,
            ValidClientId,
            isGraphAuthenticated: true,
            isBrowserSessionVerified: true,
            hasAuthenticatableRoute: true);

        Assert.False(available);
    }
}
