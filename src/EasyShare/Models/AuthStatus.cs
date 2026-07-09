using EasyShare.Resources;
using Microsoft.UI.Xaml.Controls;

namespace EasyShare.Models;

public enum AuthState
{
    NeedsConfiguration,
    SignedOut,
    SignedIn,
    Error
}

public sealed record AuthStatus(
    AuthState State,
    string Title,
    string Message,
    string AccountName,
    DateTimeOffset? ExpiresOn)
{
    public InfoBarSeverity Severity => State switch
    {
        AuthState.SignedIn => InfoBarSeverity.Success,
        AuthState.Error => InfoBarSeverity.Error,
        AuthState.NeedsConfiguration => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Informational
    };

    public static AuthStatus NeedsConfiguration() =>
        new(
            AuthState.NeedsConfiguration,
            AppText.Get("AuthNeedsClientTitle"),
            AppText.Get("AuthNeedsClientMessage"),
            AppText.Get("AccountNone"),
            null);

    public static AuthStatus SignedOut() =>
        new(
            AuthState.SignedOut,
            AppText.Get("AuthSignedOutTitle"),
            AppText.Get("AuthSignedOutMessage"),
            AppText.Get("AccountNone"),
            null);
}
