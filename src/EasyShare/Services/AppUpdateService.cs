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
using Windows.Services.Store;
using WinRT.Interop;

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
    string Changelog,
    Uri ReleasePageUrl,
    string AssetName,
    long AssetSizeBytes,
    Uri DownloadUrl,
    string ExpectedSha256,
    bool IsIncremental,
    AppUpdateChannel Channel = AppUpdateChannel.GitHubReleases);

public sealed record AppUpdateProgress(
    long BytesReceived,
    long? TotalBytes,
    double? PercentageOverride = null)
{
    public double Percentage
    {
        get
        {
            if (PercentageOverride is double percentage)
            {
                return Math.Clamp(percentage, 0d, 100d);
            }

            var totalBytes = TotalBytes.GetValueOrDefault();
            return totalBytes > 0
                ? Math.Clamp(BytesReceived * 100d / totalBytes, 0d, 100d)
                : 0d;
        }
    }

    public bool IsIndeterminate =>
        PercentageOverride is null &&
        TotalBytes.GetValueOrDefault() <= 0;
}

public sealed class AppUpdateService : IDisposable
{
    private static readonly TimeSpan ReleaseMetadataTimeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex VersionPattern = new(@"\d+(?:\.\d+){0,3}", RegexOptions.Compiled);
    private readonly AppDataPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly UpdateDownloadClient _downloadClient;
    private readonly UpdateInstallerTrustGate _installerTrustGate;
    private readonly Action<ProcessStartInfo> _processStarter;
    private IReadOnlyList<StorePackageUpdate> _pendingStoreUpdates = [];
    private StoreContext? _storeContext;
    private IntPtr _storeContextWindow;

    public string RepositoryOwner { get; }

    public string RepositoryName { get; }

    public string RepositoryFullName => $"{RepositoryOwner}/{RepositoryName}";

    public AppUpdateChannel UpdateChannel { get; }

    public AppUpdateService(
        AppDataPaths paths,
        AppUpdateChannel? updateChannel = null,
        HttpMessageHandler? httpMessageHandler = null,
        UpdateInstallerTrustGate? installerTrustGate = null,
        Action<ProcessStartInfo>? processStarter = null,
        TimeSpan? downloadTimeout = null)
    {
        _paths = paths;
        UpdateChannel = updateChannel ?? AppUpdateChannelResolver.ResolveCurrent();
        RepositoryOwner = ReadAssemblyMetadata("GitHubRepositoryOwner", "cainhooow");
        RepositoryName = ReadAssemblyMetadata("GitHubRepositoryName", "EasyShare");

        httpMessageHandler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        _httpClient = new HttpClient(httpMessageHandler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EasyShare-Updater", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _downloadClient = new UpdateDownloadClient(_httpClient, downloadTimeout);

        var publisherPolicy = UpdatePublisherPolicy.FromAssemblyMetadata(typeof(AppUpdateService).Assembly);
        _installerTrustGate = installerTrustGate ?? new UpdateInstallerTrustGate(
            publisherPolicy,
            new AuthenticodeUpdateSignatureVerifier());
        _processStarter = processStarter ?? (startInfo =>
        {
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the update installer.");
        });
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

    public Task<AppUpdateStatus> CheckForUpdatesAsync(CancellationToken cancellationToken = default) =>
        UpdateChannel == AppUpdateChannel.MicrosoftStore
            ? CheckMicrosoftStoreForUpdatesAsync(cancellationToken)
            : CheckGitHubForUpdatesAsync(cancellationToken);

    private async Task<AppUpdateStatus> CheckGitHubForUpdatesAsync(CancellationToken cancellationToken)
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
            using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operationSource.CancelAfter(ReleaseMetadataTimeout);
            var operationToken = operationSource.Token;
            using var response = await GetLatestReleaseResponseAsync(operationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new AppUpdateStatus(
                    AppText.Get("UpdateStatusNoReleaseTitle"),
                    AppText.Format("UpdateStatusNoReleaseMessage", RepositoryFullName),
                    InfoBarSeverity.Informational);
            }

            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > UpdateUriPolicy.MaxReleaseMetadataBytes)
            {
                throw new InvalidDataException("The GitHub release metadata response is too large.");
            }

            await response.Content.LoadIntoBufferAsync(UpdateUriPolicy.MaxReleaseMetadataBytes, operationToken);
            await using var responseStream = await response.Content.ReadAsStreamAsync(operationToken);
            var release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(
                responseStream,
                JsonOptions,
                operationToken);

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

            var asset = SelectInstallerAsset(
                release.Assets,
                currentVersion,
                latestVersion,
                HasCachedPackage(currentVersion));
            if (asset is null ||
                string.IsNullOrWhiteSpace(asset.Name) ||
                string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl) ||
                !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUrl) ||
                !UpdateUriPolicy.IsTrustedInitialDownloadUri(
                    downloadUrl,
                    RepositoryOwner,
                    RepositoryName) ||
                !UpdateUriPolicy.IsValidInstallerSize(asset.Size))
            {
                return new AppUpdateStatus(
                    AppText.Get("UpdateStatusNoAssetTitle"),
                    AppText.Format("UpdateStatusNoAssetMessage", release.TagName),
                    InfoBarSeverity.Warning);
            }

            var releasePageUrl = Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var parsedReleaseUrl) &&
                                 UpdateUriPolicy.IsTrustedReleasePageUri(
                                     parsedReleaseUrl,
                                     RepositoryOwner,
                                     RepositoryName)
                ? parsedReleaseUrl
                : new Uri($"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/latest");

            var expectedSha256 = UpdateIntegrity.NormalizeSha256(asset.Digest);
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                return new AppUpdateStatus(
                    AppText.Get("UpdateStatusIntegrityTitle"),
                    AppText.Format("UpdateStatusIntegrityMessage", release.TagName),
                    InfoBarSeverity.Warning);
            }

            var update = new AppUpdateInfo(
                latestVersion.ToString(),
                latestVersion,
                release.TagName,
                string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
                NormalizeReleaseNotes(release.Body),
                releasePageUrl,
                asset.Name,
                asset.Size,
                downloadUrl,
                expectedSha256,
                IsIncrementalAsset(asset.Name));

            return new AppUpdateStatus(
                AppText.Get("UpdateStatusAvailableTitle"),
                AppText.Format("UpdateStatusAvailableMessage", update.VersionText, update.AssetName),
                InfoBarSeverity.Warning,
                update);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Update check failed.", ex);
            return new AppUpdateStatus(
                AppText.Get("UpdateStatusErrorTitle"),
                AppText.Format("UpdateStatusErrorMessage", RepositoryFullName),
                InfoBarSeverity.Warning);
        }
    }

    private async Task<AppUpdateStatus> CheckMicrosoftStoreForUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updates = await GetStoreContext().GetAppAndOptionalStorePackageUpdatesAsync();
            cancellationToken.ThrowIfCancellationRequested();
            _pendingStoreUpdates = updates.ToArray();

            if (_pendingStoreUpdates.Count == 0)
            {
                return new AppUpdateStatus(
                    AppText.Get("UpdateStatusNoneTitle"),
                    AppText.Format("UpdateStatusStoreNoneMessage", CurrentVersion),
                    InfoBarSeverity.Success);
            }

            var latestVersion = _pendingStoreUpdates
                .Select(update => ToVersion(update.Package.Id.Version))
                .OrderDescending()
                .First();
            var storeUri = new Uri("ms-windows-store://downloadsandupdates");
            var updateInfo = new AppUpdateInfo(
                latestVersion.ToString(4),
                latestVersion,
                "Microsoft Store",
                "Microsoft Store",
                string.Empty,
                storeUri,
                "Microsoft Store",
                0,
                storeUri,
                string.Empty,
                false,
                AppUpdateChannel.MicrosoftStore);

            return new AppUpdateStatus(
                AppText.Get("UpdateStatusAvailableTitle"),
                AppText.Format("UpdateStatusStoreAvailableMessage", updateInfo.VersionText),
                InfoBarSeverity.Warning,
                updateInfo);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Microsoft Store update check failed.", ex);
            _pendingStoreUpdates = [];
            return new AppUpdateStatus(
                AppText.Get("UpdateStatusErrorTitle"),
                AppText.Get("UpdateStatusStoreErrorMessage"),
                InfoBarSeverity.Warning);
        }
    }

    public async Task<StorePackageUpdateState> InstallMicrosoftStoreUpdateAsync(
        IntPtr ownerWindow,
        IProgress<AppUpdateProgress>? progress = null)
    {
        if (UpdateChannel != AppUpdateChannel.MicrosoftStore || _pendingStoreUpdates.Count == 0)
        {
            throw new InvalidOperationException("No Microsoft Store update is ready to install.");
        }

        var context = GetStoreContext();
        if (ownerWindow != IntPtr.Zero && _storeContextWindow != ownerWindow)
        {
            InitializeWithWindow.Initialize(context, ownerWindow);
            _storeContextWindow = ownerWindow;
        }

        var operation = context.RequestDownloadAndInstallStorePackageUpdatesAsync(_pendingStoreUpdates);
        operation.Progress = (_, status) =>
        {
            var totalBytes = status.PackageDownloadSizeInBytes > 0
                ? checked((long)Math.Min(status.PackageDownloadSizeInBytes, long.MaxValue))
                : (long?)null;
            var bytesReceived = checked((long)Math.Min(status.PackageBytesDownloaded, long.MaxValue));
            progress?.Report(new AppUpdateProgress(
                bytesReceived,
                totalBytes,
                status.TotalDownloadProgress * 100d));
        };

        var result = await operation;
        if (result.OverallState == StorePackageUpdateState.Completed)
        {
            _pendingStoreUpdates = [];
        }

        return result.OverallState;
    }

    private async Task<HttpResponseMessage> GetLatestReleaseResponseAsync(CancellationToken cancellationToken)
    {
        var apiUri = new Uri($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");
        if (!UpdateUriPolicy.IsTrustedGitHubApiUri(apiUri, RepositoryOwner, RepositoryName))
        {
            throw new InvalidDataException("The configured update metadata endpoint is invalid.");
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await SendTrustedMetadataRequestAsync(apiUri, cancellationToken);
            }
            catch (HttpRequestException) when (attempt == 0 && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
            }
        }

        return await SendTrustedMetadataRequestAsync(apiUri, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendTrustedMetadataRequestAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirectCount = 0; redirectCount <= UpdateUriPolicy.MaxRedirects; redirectCount++)
        {
            if (!UpdateUriPolicy.IsTrustedGitHubApiUri(currentUri, RepositoryOwner, RepositoryName))
            {
                throw new InvalidDataException("The release metadata redirect target is not trusted.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            try
            {
                var actualUri = response.RequestMessage?.RequestUri ?? currentUri;
                if (!UpdateUriPolicy.IsTrustedGitHubApiUri(actualUri, RepositoryOwner, RepositoryName))
                {
                    throw new InvalidDataException("The final release metadata origin is not trusted.");
                }

                if (!UpdateUriPolicy.IsRedirectStatusCode(response.StatusCode))
                {
                    return response;
                }

                if (redirectCount == UpdateUriPolicy.MaxRedirects ||
                    !UpdateUriPolicy.TryResolveRedirect(
                        actualUri,
                        response.Headers.Location,
                        out var redirectUri) ||
                    !UpdateUriPolicy.IsTrustedGitHubApiUri(
                        redirectUri,
                        RepositoryOwner,
                        RepositoryName))
                {
                    throw new InvalidDataException("The release metadata redirect chain is invalid or too long.");
                }

                currentUri = redirectUri;
            }
            catch
            {
                response.Dispose();
                throw;
            }

            response.Dispose();
        }

        throw new InvalidDataException("The release metadata redirect chain is too long.");
    }

    public async Task<string> DownloadUpdateAsync(
        AppUpdateInfo update,
        IProgress<AppUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (update.Channel != AppUpdateChannel.GitHubReleases)
        {
            throw new InvalidOperationException("Microsoft Store updates must be installed through StoreContext.");
        }

        _paths.EnsureCreated();
        var updatesDirectory = GetUpdateDirectory(update);
        Directory.CreateDirectory(updatesDirectory);

        var targetPath = GetDownloadedUpdatePath(update);
        if (TryUseExistingDownload(update, targetPath))
        {
            var existingLength = new FileInfo(targetPath).Length;
            progress?.Report(new AppUpdateProgress(existingLength, update.AssetSizeBytes > 0 ? update.AssetSizeBytes : existingLength));
            return targetPath;
        }

        if (string.IsNullOrWhiteSpace(update.ExpectedSha256))
        {
            throw new InvalidDataException("A release asset without a SHA-256 digest cannot be installed.");
        }

        var transferProgress = progress is null
            ? null
            : new Progress<UpdateDownloadTransferProgress>(value =>
                progress.Report(new AppUpdateProgress(value.BytesReceived, value.TotalBytes)));
        await _downloadClient.DownloadAsync(
            new UpdateDownloadRequest(
                update.DownloadUrl,
                RepositoryOwner,
                RepositoryName,
                targetPath,
                update.AssetSizeBytes,
                update.ExpectedSha256),
            transferProgress,
            cancellationToken);
        return targetPath;
    }

    public bool TryGetDownloadedUpdatePath(AppUpdateInfo update, out string downloadedPath)
    {
        if (update.Channel != AppUpdateChannel.GitHubReleases)
        {
            downloadedPath = string.Empty;
            return false;
        }

        downloadedPath = GetDownloadedUpdatePath(update);
        return TryUseExistingDownload(update, downloadedPath);
    }

    public bool TryStartInstaller(string installerPath)
    {
        try
        {
            var trustedInstaller = _installerTrustGate.Prepare(installerPath);
            var fullPath = Path.GetFullPath(trustedInstaller.Path);
            _processStarter(new ProcessStartInfo
            {
                FileName = fullPath,
                WorkingDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Update installer launch failed.", ex);
            return false;
        }
    }

    public void OpenReleasePage(AppUpdateInfo update)
    {
        if (update.Channel != AppUpdateChannel.GitHubReleases)
        {
            return;
        }

        if (!UpdateUriPolicy.IsTrustedReleasePageUri(
                update.ReleasePageUrl,
                RepositoryOwner,
                RepositoryName))
        {
            StartupDiagnostics.Write("Blocked an untrusted update release-page URL.");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = update.ReleasePageUrl.ToString(),
            UseShellExecute = true
        });
    }

    public void Dispose() => _httpClient.Dispose();

    private Version ParseCurrentVersion() =>
        TryParseVersion(CurrentVersion, out var version) ? version : new Version(0, 0, 0, 0);

    private StoreContext GetStoreContext() => _storeContext ??= StoreContext.GetDefault();

    private static Version ToVersion(PackageVersion version) =>
        new(version.Major, version.Minor, version.Build, version.Revision);

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

    private static string NormalizeReleaseNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return AppText.Get("UpdateChangelogMissingMessage");
        }

        var lines = body
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line =>
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("### ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("## ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("# ", StringComparison.Ordinal))
                {
                    return trimmed[(trimmed.IndexOf(' ') + 1)..];
                }

                if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("* ", StringComparison.Ordinal))
                {
                    return $"• {trimmed[2..]}";
                }

                return line;
            });

        var normalized = string.Join(Environment.NewLine, lines).Trim();
        return normalized.Length <= 12000
            ? normalized
            : $"{normalized[..12000].TrimEnd()}…";
    }

    private static GitHubReleaseAssetResponse? SelectInstallerAsset(
        IReadOnlyList<GitHubReleaseAssetResponse>? assets,
        Version currentVersion,
        Version latestVersion,
        bool hasCachedPackage) =>
        assets?
            .Where(asset => !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            .Select(asset => new
            {
                Asset = asset,
                Preference = AssetPreference(asset.Name, currentVersion, latestVersion, hasCachedPackage)
            })
            .Where(candidate => candidate.Preference < int.MaxValue)
            .OrderBy(candidate => candidate.Preference)
            .Select(candidate => candidate.Asset)
            .FirstOrDefault();

    private static int AssetPreference(
        string? assetName,
        Version currentVersion,
        Version latestVersion,
        bool hasCachedPackage)
    {
        if (string.IsNullOrWhiteSpace(assetName))
        {
            return int.MaxValue;
        }

        var name = assetName.ToLowerInvariant();
        if (hasCachedPackage && IsIncrementalAssetForVersion(name, currentVersion, latestVersion))
        {
            return 0;
        }

        if (name.Equals("easysharesetup.exe", StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Contains("easyshare", StringComparison.Ordinal))
        {
            return 11;
        }

        if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) && name.Contains("easyshare", StringComparison.Ordinal))
        {
            return 12;
        }

        if (name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) && name.Contains("easyshare", StringComparison.Ordinal))
        {
            return 13;
        }

        return int.MaxValue;
    }

    private static bool IsIncrementalAsset(string? assetName) =>
        !string.IsNullOrWhiteSpace(assetName) &&
        assetName.Contains("patch", StringComparison.OrdinalIgnoreCase);

    private static bool IsIncrementalAssetForVersion(string assetName, Version currentVersion, Version latestVersion)
    {
        if (!IsIncrementalAsset(assetName))
        {
            return false;
        }

        var current = currentVersion.ToString(4);
        var latest = latestVersion.ToString(4);
        var normalized = assetName.Replace('.', '_')
            .Replace('-', '_');
        return normalized.Contains($"from_{current}", StringComparison.OrdinalIgnoreCase) &&
               normalized.Contains($"to_{latest}", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasCachedPackage(Version version) =>
        File.Exists(Path.Combine(
            _paths.DataDirectory,
            "Packages",
            $"EasyShare_{version.ToString(4)}_x64.msix"));

    private string GetUpdateDirectory(AppUpdateInfo update)
    {
        var updateFolderName = SanitizeFileName(string.IsNullOrWhiteSpace(update.TagName)
            ? update.VersionText
            : update.TagName);
        return Path.Combine(_paths.DataDirectory, "Updates", updateFolderName);
    }

    private string GetDownloadedUpdatePath(AppUpdateInfo update) =>
        Path.Combine(GetUpdateDirectory(update), SanitizeFileName(update.AssetName));

    private static bool TryUseExistingDownload(AppUpdateInfo update, string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var fileInfo = new FileInfo(path);
        if (update.AssetSizeBytes > 0 && fileInfo.Length != update.AssetSizeBytes)
        {
            TryDeleteFile(path);
            return false;
        }

        if (fileInfo.Length <= 0 || string.IsNullOrWhiteSpace(update.ExpectedSha256))
        {
            return false;
        }

        if (!UpdateIntegrity.VerifyFile(path, update.ExpectedSha256))
        {
            TryDeleteFile(path);
            return false;
        }

        return true;
    }


    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A locked partial download is harmless; the next attempt can overwrite it if available.
        }
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

        [JsonPropertyName("body")]
        public string? Body { get; init; }

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

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }
}
