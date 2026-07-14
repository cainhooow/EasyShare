using System.Text.RegularExpressions;

namespace EasyShare.Services;

internal static class GitHubPatchAssetPolicy
{
    private static readonly Regex CanonicalPatchName = new(
        @"\AEasySharePatch_from_(?<from>\d+_\d+_\d+_\d+)_to_(?<to>\d+_\d+_\d+_\d+)\.exe\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string ExpectedAssetName(Version currentVersion, Version latestVersion) =>
        $"EasySharePatch_from_{VersionToken(currentVersion)}_to_{VersionToken(latestVersion)}.exe";

    public static bool IsPatchAsset(string? assetName) =>
        !string.IsNullOrWhiteSpace(assetName) && CanonicalPatchName.IsMatch(assetName);

    public static bool IsPatchAssetForVersion(
        string? assetName,
        Version currentVersion,
        Version latestVersion) =>
        string.Equals(
            assetName,
            ExpectedAssetName(currentVersion, latestVersion),
            StringComparison.OrdinalIgnoreCase);

    public static T? SelectCompatiblePatch<T>(
        IEnumerable<T>? assets,
        Func<T, string?> assetNameSelector,
        Func<T, string?> downloadUrlSelector,
        Version currentVersion,
        Version latestVersion,
        bool hasCachedPackage)
        where T : class
    {
        if (!hasCachedPackage || assets is null)
        {
            return null;
        }

        var matches = assets
            .Where(asset =>
                !string.IsNullOrWhiteSpace(downloadUrlSelector(asset)) &&
                IsPatchAssetForVersion(assetNameSelector(asset), currentVersion, latestVersion))
            .Take(2)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private static string VersionToken(Version version) =>
        $"{version.Major}_{version.Minor}_{Math.Max(version.Build, 0)}_{Math.Max(version.Revision, 0)}";
}
