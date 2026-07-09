using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EasyShare.Resources;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;

namespace EasyShare.Services;

public sealed record AppUpdateStatus(
    string Title,
    string Message,
    InfoBarSeverity Severity,
    AppUpdateInfo? Update = null);

public sealed record AppUpdateInfo(
    string VersionText,
    Version Version,
    string TagName,
    string ReleaseName,
    Uri ReleasePageUrl,
    string AssetName,
    long AssetSizeBytes,
    Uri DownloadUrl);

public sealed record AppUpdateProgress(long BytesReceived, long? TotalBytes)
{
    public double Percentage
    {
        get
        {
            var totalBytes = TotalBytes.GetValueOrDefault();
            return totalBytes > 0
                ? Math.Clamp(BytesReceived * 100d / totalBytes, 0d, 100d)
                : 0d;
        }
    }

    public bool IsIndeterminate => TotalBytes.GetValueOrDefault() <= 0;
}

public sealed class AppUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex VersionPattern = new(@"\d+(?:\.\d+){0,3}", RegexOptions.Compiled);
    private readonly AppDataPaths _paths;
    private readonly HttpClient _httpClient;

    public string RepositoryOwner { get; }

    public string RepositoryName { get; }

    public string RepositoryFullName => $"{RepositoryOwner}/{RepositoryName}";

    public AppUpdateService(AppDataPaths paths)
    {
        _paths = paths;
        RepositoryOwner = ReadAssemblyMetadata("GitHubRepositoryOwner", "cainhooow");
        RepositoryName = ReadAssemblyMetadata("GitHubRepositoryName", "EasyShare");

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EasyShare-Updater", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public string CurrentVersion
    {
        get
        {
            try
            {
                var version = Package.Current.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch
            {
                return typeof(AppUpdateService).Assembly.GetName().Version?.ToString() ?? "1.0.0.0";
            }
        }
    }

    public async Task<AppUpdateStatus> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(RepositoryOwner) || string.IsNullOrWhiteSpace(RepositoryName))
        {
            return new AppUpdateStatus(
                AppText.Get("UpdateStatusUnknownTitle"),
                AppText.Get("UpdateStatusRepositoryMissingMessage"),
                InfoBarSeverity.Warning);
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new AppUpdateStatus(
                    AppText.Get("UpdateStatusNoReleaseTitle"),
                    AppText.Format("UpdateStatusNoReleaseMessage", RepositoryFullName),
                    InfoBarSeverity.Informational);
            }

            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(
                responseStream,
                JsonOptions,
                cancellationToken);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return new AppUpdateStatus(
                    AppText.Get("UpdateStatusUnknownTitle"),
                    AppText.Get("UpdateStatusUnknownMessage"),
                    InfoBarSeverity.Informational);
            }

            if (!TryParseVersion(release.TagName, out var latestVersion))
            {
                return new AppUpdateStatus(
                    AppText.Get("UpdateStatusUnknownTitle"),
                    AppText.Format("UpdateStatusInvalidVersionMessage", release.TagName),
                    InfoBarSeverity.Warning);
            }

            var currentVersion = ParseCurrentVersion();
            if (latestVersion <= currentVersion)
            {
                return new AppUpdateStatus(
                    AppText.Get("UpdateStatusNoneTitle"),
                    AppText.Format("UpdateStatusNoneMessage", CurrentVersion),
                    InfoBarSeverity.Success);
            }

            var asset = SelectInstallerAsset(release.Assets);
            if (asset is null ||
                string.IsNullOrWhiteSpace(asset.Name) ||
                string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl) ||
                !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUrl))
            {
                return new AppUpdateStatus(
                    AppText.Get("UpdateStatusNoAssetTitle"),
                    AppText.Format("UpdateStatusNoAssetMessage", release.TagName),
                    InfoBarSeverity.Warning);
            }

            var releasePageUrl = Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var parsedReleaseUrl)
                ? parsedReleaseUrl
                : new Uri($"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/latest");

            var update = new AppUpdateInfo(
                latestVersion.ToString(),
                latestVersion,
                release.TagName,
                string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
                releasePageUrl,
                asset.Name,
                asset.Size,
                downloadUrl);

            return new AppUpdateStatus(
                AppText.Get("UpdateStatusAvailableTitle"),
                AppText.Format("UpdateStatusAvailableMessage", update.VersionText, update.AssetName),
                InfoBarSeverity.Warning,
                update);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new AppUpdateStatus(
                AppText.Get("UpdateStatusErrorTitle"),
                AppText.Format("UpdateStatusErrorMessage", RepositoryFullName),
                InfoBarSeverity.Warning);
        }
    }

    public async Task<string> DownloadUpdateAsync(
        AppUpdateInfo update,
        IProgress<AppUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var updatesDirectory = Path.Combine(_paths.DataDirectory, "Updates");
        Directory.CreateDirectory(updatesDirectory);

        var safeAssetName = SanitizeFileName(update.AssetName);
        var targetPath = Path.Combine(updatesDirectory, safeAssetName);
        var temporaryPath = Path.Combine(updatesDirectory, $"{safeAssetName}.download");

        using var response = await _httpClient.GetAsync(
            update.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength > 0
            ? response.Content.Headers.ContentLength
            : update.AssetSizeBytes > 0 ? update.AssetSizeBytes : null;

        var bytesReceived = 0L;
        progress?.Report(new AppUpdateProgress(bytesReceived, totalBytes));

        await using (var downloadStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var fileStream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 81920,
                         useAsync: true))
        {
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await downloadStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                bytesReceived += bytesRead;
                progress?.Report(new AppUpdateProgress(bytesReceived, totalBytes));
            }
        }

        File.Move(temporaryPath, targetPath, overwrite: true);
        progress?.Report(new AppUpdateProgress(bytesReceived, totalBytes ?? bytesReceived));
        return targetPath;
    }

    public bool TryStartInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                Verb = "runas"
            });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public void OpenReleasePage(AppUpdateInfo update)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = update.ReleasePageUrl.ToString(),
            UseShellExecute = true
        });
    }

    private Version ParseCurrentVersion() =>
        TryParseVersion(CurrentVersion, out var version) ? version : new Version(0, 0, 0, 0);

    private static bool TryParseVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var match = VersionPattern.Match(value);
        if (!match.Success)
        {
            return false;
        }

        var parts = match.Value.Split('.', StringSplitOptions.RemoveEmptyEntries).Take(4).ToList();
        while (parts.Count < 4)
        {
            parts.Add("0");
        }

        if (!Version.TryParse(string.Join('.', parts), out var parsedVersion) || parsedVersion is null)
        {
            return false;
        }

        version = parsedVersion;
        return true;
    }

    private static GitHubReleaseAssetResponse? SelectInstallerAsset(IReadOnlyList<GitHubReleaseAssetResponse>? assets) =>
        assets?
            .Where(asset => !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            .OrderBy(asset => AssetPreference(asset.Name))
            .FirstOrDefault(asset => AssetPreference(asset.Name) < int.MaxValue);

    private static int AssetPreference(string? assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName))
        {
            return int.MaxValue;
        }

        var name = assetName.ToLowerInvariant();
        if (name.Equals("easysharesetup.exe", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Contains("easyshare", StringComparison.Ordinal))
        {
            return 1;
        }

        if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) && name.Contains("easyshare", StringComparison.Ordinal))
        {
            return 2;
        }

        if (name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) && name.Contains("easyshare", StringComparison.Ordinal))
        {
            return 3;
        }

        return int.MaxValue;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "EasyShareSetup.exe" : sanitized;
    }

    private static string ReadAssemblyMetadata(string key, string fallback)
    {
        var metadata = typeof(AppUpdateService)
            .Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal));

        return string.IsNullOrWhiteSpace(metadata?.Value) ? fallback : metadata.Value;
    }

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("assets")]
        public IReadOnlyList<GitHubReleaseAssetResponse>? Assets { get; init; }
    }

    private sealed class GitHubReleaseAssetResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }
    }
}
