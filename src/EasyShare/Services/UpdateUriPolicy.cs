using System.Net;

namespace EasyShare.Services;

public static class UpdateUriPolicy
{
    public const long MaxInstallerBytes = 512L * 1024 * 1024;
    public const long MaxReleaseMetadataBytes = 2L * 1024 * 1024;
    public const int MaxRedirects = 5;

    private static readonly HashSet<string> TrustedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com"
    };

    public static bool IsValidInstallerSize(long sizeBytes) =>
        sizeBytes > 0 && sizeBytes <= MaxInstallerBytes;

    public static bool IsTrustedGitHubApiUri(Uri? uri, string repositoryOwner, string repositoryName)
    {
        if (!IsSecureHttpsUri(uri) ||
            !uri!.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedPath = $"/repos/{repositoryOwner}/{repositoryName}/releases/latest";
        return uri.AbsolutePath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment);
    }

    public static bool IsTrustedReleasePageUri(Uri? uri, string repositoryOwner, string repositoryName)
    {
        if (!IsSecureHttpsUri(uri) ||
            !uri!.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedPrefix = $"/{repositoryOwner}/{repositoryName}/releases/";
        return uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTrustedInitialDownloadUri(Uri? uri, string repositoryOwner, string repositoryName)
    {
        if (!IsSecureHttpsUri(uri) ||
            !uri!.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var releaseDownloadPrefix = $"/{repositoryOwner}/{repositoryName}/releases/download/";
        var latestDownloadPrefix = $"/{repositoryOwner}/{repositoryName}/releases/latest/download/";
        return uri.AbsolutePath.StartsWith(releaseDownloadPrefix, StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.StartsWith(latestDownloadPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTrustedDownloadRedirectUri(Uri? uri, string repositoryOwner, string repositoryName)
    {
        if (!IsSecureHttpsUri(uri))
        {
            return false;
        }

        if (uri!.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return IsTrustedInitialDownloadUri(uri, repositoryOwner, repositoryName);
        }

        return TrustedDownloadHosts.Contains(uri.Host) &&
               uri.AbsolutePath.Length > 1;
    }

    public static bool TryResolveRedirect(Uri currentUri, Uri? location, out Uri redirectUri)
    {
        redirectUri = null!;
        if (location is null)
        {
            return false;
        }

        if (!location.IsAbsoluteUri && !Uri.TryCreate(currentUri, location, out location))
        {
            return false;
        }

        redirectUri = location;
        return true;
    }

    public static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Redirect or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static bool IsSecureHttpsUri(Uri? uri)
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

        return true;
    }
}
