using EasyShare.Models;

namespace EasyShare.Services;

public static class ExplorerAccessPolicy
{
    public static bool IsAvailable(
        AuthenticationMode authenticationMode,
        string? clientId,
        bool isGraphAuthenticated,
        bool isBrowserSessionVerified,
        bool hasAuthenticatableRoute) =>
        authenticationMode switch
        {
            AuthenticationMode.MicrosoftGraph =>
                HasValidClientId(clientId) && isGraphAuthenticated,
            AuthenticationMode.BrowserSession =>
                isBrowserSessionVerified && hasAuthenticatableRoute,
            _ => false
        };

    public static bool HasValidClientId(string? clientId) =>
        Guid.TryParse(clientId, out var parsedClientId) && parsedClientId != Guid.Empty;
}
