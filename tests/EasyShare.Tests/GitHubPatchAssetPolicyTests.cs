using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class GitHubPatchAssetPolicyTests
{
    private static readonly Version Current = new(1, 0, 0, 25);
    private static readonly Version Latest = new(1, 0, 26, 0);

    [Fact]
    public void BuildsCanonicalFourPartPatchName()
    {
        Assert.Equal(
            "EasySharePatch_from_1_0_0_25_to_1_0_26_0.exe",
            GitHubPatchAssetPolicy.ExpectedAssetName(Current, Latest));
    }

    [Fact]
    public void SelectsOnlyExactPatchWhenBaseCacheExists()
    {
        var assets = new[]
        {
            new Asset("EasyShareSetup.exe", "https://example.test/full"),
            new Asset("EasyShare_1.0.26.0_x64.msix", "https://example.test/msix"),
            new Asset("EasySharePatch_from_1_0_0_25_to_1_0_26_0.exe", "https://example.test/patch")
        };

        var selected = Select(assets, hasCachedPackage: true);

        Assert.Equal("https://example.test/patch", selected?.Url);
    }

    [Theory]
    [InlineData("EasyShareSetup.exe")]
    [InlineData("EasyShareFullSetup.exe")]
    [InlineData("EasyShareSetup.msi")]
    [InlineData("EasyShare_1.0.26.0_x64.msix")]
    [InlineData("EasySharePatch.exe")]
    [InlineData("EasySharePatch_from_1.0.0.25_to_1.0.26.0.exe")]
    [InlineData("prefix_EasySharePatch_from_1_0_0_25_to_1_0_26_0.exe")]
    public void RejectsFullOrMalformedAssets(string assetName)
    {
        Assert.False(GitHubPatchAssetPolicy.IsPatchAssetForVersion(assetName, Current, Latest));
        Assert.Null(Select([new Asset(assetName, "https://example.test/asset")], hasCachedPackage: true));
    }

    [Fact]
    public void RejectsPatchWhenBaseCacheIsMissing()
    {
        Assert.Null(Select(
            [new Asset("EasySharePatch_from_1_0_0_25_to_1_0_26_0.exe", "https://example.test/patch")],
            hasCachedPackage: false));
    }

    [Theory]
    [InlineData("EasySharePatch_from_1_0_0_24_to_1_0_26_0.exe")]
    [InlineData("EasySharePatch_from_1_0_0_25_to_1_0_27_0.exe")]
    public void RejectsPatchForDifferentVersion(string assetName)
    {
        Assert.Null(Select([new Asset(assetName, "https://example.test/patch")], hasCachedPackage: true));
    }

    [Fact]
    public void RejectsPatchWithoutDownloadUrl()
    {
        Assert.Null(Select(
            [new Asset("EasySharePatch_from_1_0_0_25_to_1_0_26_0.exe", null)],
            hasCachedPackage: true));
    }

    [Fact]
    public void FailsClosedWhenExactPatchIsDuplicated()
    {
        var name = "EasySharePatch_from_1_0_0_25_to_1_0_26_0.exe";

        Assert.Null(Select(
            [new Asset(name, "https://example.test/one"), new Asset(name.ToUpperInvariant(), "https://example.test/two")],
            hasCachedPackage: true));
    }

    [Fact]
    public void MatchesCanonicalNameCaseInsensitively()
    {
        Assert.True(GitHubPatchAssetPolicy.IsPatchAssetForVersion(
            "easysharepatch_FROM_1_0_0_25_TO_1_0_26_0.EXE",
            Current,
            Latest));
    }

    private static Asset? Select(IEnumerable<Asset> assets, bool hasCachedPackage) =>
        GitHubPatchAssetPolicy.SelectCompatiblePatch(
            assets,
            asset => asset.Name,
            asset => asset.Url,
            Current,
            Latest,
            hasCachedPackage);

    private sealed record Asset(string Name, string? Url);
}
