using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class UpdateUriPolicyTests
{
    [Fact]
    public void AcceptsOnlyExactGitHubReleaseOrigins()
    {
        Assert.True(UpdateUriPolicy.IsTrustedGitHubApiUri(
            new Uri("https://api.github.com/repos/cainhooow/EasyShare/releases/latest"),
            "cainhooow",
            "EasyShare"));
        Assert.True(UpdateUriPolicy.IsTrustedInitialDownloadUri(
            new Uri("https://github.com/cainhooow/EasyShare/releases/download/v1/EasyShareSetup.exe"),
            "cainhooow",
            "EasyShare"));

        Assert.False(UpdateUriPolicy.IsTrustedGitHubApiUri(
            new Uri("https://api.github.com.evil.example/repos/cainhooow/EasyShare/releases/latest"),
            "cainhooow",
            "EasyShare"));
        Assert.False(UpdateUriPolicy.IsTrustedGitHubApiUri(
            new Uri("https://api.github.com/repos/cainhooow/EasyShare/releases/latest?redirect=1"),
            "cainhooow",
            "EasyShare"));
        Assert.False(UpdateUriPolicy.IsTrustedInitialDownloadUri(
            new Uri("http://github.com/cainhooow/EasyShare/releases/download/v1/EasyShareSetup.exe"),
            "cainhooow",
            "EasyShare"));
    }

    [Fact]
    public void AcceptsOnlyKnownGitHubAssetRedirectHosts()
    {
        Assert.True(UpdateUriPolicy.IsTrustedDownloadRedirectUri(
            new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/file"),
            "cainhooow",
            "EasyShare"));
        Assert.False(UpdateUriPolicy.IsTrustedDownloadRedirectUri(
            new Uri("https://release-assets.githubusercontent.com.evil.example/file"),
            "cainhooow",
            "EasyShare"));
        Assert.False(UpdateUriPolicy.IsTrustedDownloadRedirectUri(
            new Uri("https://raw.githubusercontent.com/cainhooow/EasyShare/main/EasyShareSetup.exe"),
            "cainhooow",
            "EasyShare"));
    }
}
