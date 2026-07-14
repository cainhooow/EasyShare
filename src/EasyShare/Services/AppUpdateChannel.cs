using Windows.ApplicationModel;

namespace EasyShare.Services;

public enum AppUpdateChannel
{
    GitHubReleases,
    MicrosoftStore
}

public static class AppUpdateChannelResolver
{
    public static AppUpdateChannel Resolve(PackageSignatureKind signatureKind) =>
        signatureKind == PackageSignatureKind.Store
            ? AppUpdateChannel.MicrosoftStore
            : AppUpdateChannel.GitHubReleases;

    public static AppUpdateChannel ResolveCurrent()
    {
        try
        {
            return Resolve(Package.Current.SignatureKind);
        }
        catch
        {
            // Unpackaged and development launches keep using the external release channel.
            return AppUpdateChannel.GitHubReleases;
        }
    }
}
