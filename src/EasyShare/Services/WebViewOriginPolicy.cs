using System.Net;

namespace EasyShare.Services;

public static class WebViewOriginPolicy
{
    private static readonly string[] TrustedMicrosoftDomainSuffixes =
    [
        "sharepoint.com",
        "sharepoint.us",
        "sharepoint.de",
        "sharepoint.cn",
        "sharepoint-mil.us",
        "office.com",
        "office365.com",
        "microsoft365.com",
        "microsoft.com",
        "cloud.microsoft",
        "microsoftonline.com",
        "microsoftonline.us",
        "microsoftonline.de",
        "microsoftonline-p.com",
        "windows.net",
        "sharepointonline.com",
        "msauth.net",
        "msftauth.net",
        "msauthimages.net",
        "msftauthimages.net"
    ];

    private static readonly HashSet<string> TrustedMicrosoftHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "login.microsoft.com",
        "login.microsoft",
        "login.live.com",
        "login.windows.net",
        "login.partner.microsoftonline.cn",
        "login.chinacloudapi.cn",
        "sts.windows.net"
    };

    private static readonly HashSet<string> IdentityAuthorityHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "login.microsoft.com",
        "login.microsoft",
        "login.microsoftonline.com",
        "login.microsoftonline.us",
        "login.microsoftonline.de",
        "login.live.com",
        "login.windows.net",
        "login.partner.microsoftonline.cn",
        "login.chinacloudapi.cn",
        "sts.windows.net"
    };

    public static bool IsTrustedMicrosoftUri(Uri? uri) =>
        IsSecureWebUri(uri) && IsTrustedMicrosoftHost(uri!.DnsSafeHost);

    public static bool IsTrustedMicrosoftHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalizedHost = host.TrimEnd('.');
        if (TrustedMicrosoftHosts.Contains(normalizedHost))
        {
            return true;
        }

        return TrustedMicrosoftDomainSuffixes.Any(suffix =>
            normalizedHost.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
            normalizedHost.EndsWith($".{suffix}", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSecureWebUri(Uri? uri)
    {
        if (uri is null ||
            !uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            string.IsNullOrWhiteSpace(uri.DnsSafeHost) ||
            uri.IsLoopback ||
            IPAddress.TryParse(uri.DnsSafeHost, out _))
        {
            return false;
        }

        return uri.DnsSafeHost.Contains('.', StringComparison.Ordinal);
    }

    public static bool CanBeginFederatedSignIn(
        Uri? currentUri,
        Uri? targetUri,
        bool isUserInitiated)
    {
        if (isUserInitiated ||
            !IsSecureWebUri(currentUri) ||
            !IsSecureWebUri(targetUri) ||
            IsTrustedMicrosoftUri(targetUri))
        {
            return false;
        }

        return IdentityAuthorityHosts.Contains(currentUri!.DnsSafeHost.TrimEnd('.'));
    }

    public static bool IsApprovedFederatedUri(Uri? uri, IEnumerable<string> approvedHosts)
    {
        if (!IsSecureWebUri(uri))
        {
            return false;
        }

        return approvedHosts.Contains(uri!.DnsSafeHost, StringComparer.OrdinalIgnoreCase);
    }
}
