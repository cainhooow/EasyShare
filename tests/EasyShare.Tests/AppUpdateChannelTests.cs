using EasyShare.Services;
using Windows.ApplicationModel;
using Xunit;

namespace EasyShare.Tests;

public sealed class AppUpdateChannelTests
{
    [Fact]
    public void StoreSignatureUsesMicrosoftStoreChannel()
    {
        Assert.Equal(
            AppUpdateChannel.MicrosoftStore,
            AppUpdateChannelResolver.Resolve(PackageSignatureKind.Store));
    }

    [Theory]
    [InlineData(PackageSignatureKind.None)]
    [InlineData(PackageSignatureKind.Developer)]
    [InlineData(PackageSignatureKind.Enterprise)]
    [InlineData(PackageSignatureKind.System)]
    public void NonStoreSignaturesKeepGitHubReleaseChannel(PackageSignatureKind signatureKind)
    {
        Assert.Equal(
            AppUpdateChannel.GitHubReleases,
            AppUpdateChannelResolver.Resolve(signatureKind));
    }
}
