using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EasyShare.Models;

namespace EasyShare.Services;

/// <summary>
/// Creates a bounded support archive from the explicit diagnostic allowlist. Token
/// caches, browser profiles/cookies, the database, and upload payloads are never read.
/// </summary>
public sealed class SupportBundleService
{
    private readonly AppDataPaths _paths;
    private readonly RotatingDiagnosticLog _diagnosticLog;
    private readonly SensitiveDataRedactor _redactor;
    private readonly SupportBundleOptions _options;

    public SupportBundleService(
        AppDataPaths paths,
        RotatingDiagnosticLog? diagnosticLog = null,
        SensitiveDataRedactor? redactor = null,
        SupportBundleOptions? options = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _redactor = redactor ?? new SensitiveDataRedactor();
        _diagnosticLog = diagnosticLog ?? new RotatingDiagnosticLog(
            Path.Combine(paths.LogDirectory, "startup.log"),
            redactor: _redactor);
        _options = options ?? new SupportBundleOptions();
        if (_options.MaxUncompressedLogBytes <= 0 || _options.MaxBundleBytes <= 0 ||
            _options.MaxMetadataEntries is < 0 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public async Task<SupportBundleResult> CreateAsync(
        string destinationPath,
        SupportBundleContext? context = null,
        CancellationToken cancellationToken = default)
    {
        context ??= new SupportBundleContext();
        if (context.Policy is { Policy.SupportBundlesAllowed: false })
        {
            return Failed("Support bundles are disabled by enterprise policy.");
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return Failed("A destination path is required.");
        }

        var fullDestination = Path.GetFullPath(destinationPath);
        if (IsWithin(fullDestination, _paths.DataDirectory))
        {
            return Failed("The support bundle must be saved outside EasyShare private data.");
        }

        var directory = Path.GetDirectoryName(fullDestination);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Failed("The destination directory does not exist.");
        }

        if (File.Exists(fullDestination))
        {
            return Failed("The destination file already exists.");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.tmp");
        var includedLogs = 0;
        try
        {
            var logFiles = GetAllowedLogFiles();
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await WriteManifestAsync(archive, context, logFiles.Count, cancellationToken)
                        .ConfigureAwait(false);

                    long remainingLogBytes = _options.MaxUncompressedLogBytes;
                    foreach (var logFile in logFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (remainingLogBytes <= 0)
                        {
                            break;
                        }

                        var written = await AddRedactedLogAsync(
                            archive,
                            logFile,
                            remainingLogBytes,
                            cancellationToken).ConfigureAwait(false);
                        if (written <= 0)
                        {
                            continue;
                        }

                        remainingLogBytes -= written;
                        includedLogs++;
                    }
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            var bundleLength = new FileInfo(temporaryPath).Length;
            if (bundleLength > _options.MaxBundleBytes)
            {
                return Failed("The support bundle exceeded its configured size limit.");
            }

            File.Move(temporaryPath, fullDestination, overwrite: false);
            PrivateFilePermissions.TryHardenFile(fullDestination);
            return new SupportBundleResult(
                true,
                fullDestination,
                bundleLength,
                includedLogs);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidDataException or JsonException)
        {
            return Failed("The support bundle could not be created.");
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private IReadOnlyList<FileInfo> GetAllowedLogFiles()
    {
        var expectedDirectory = Path.GetFullPath(_paths.LogDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return _diagnosticLog
            .GetLogFiles()
            .Select(path => new FileInfo(path))
            .Where(file =>
                file.Exists &&
                (file.Attributes & FileAttributes.ReparsePoint) == 0 &&
                string.Equals(
                    file.Directory?.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    expectedDirectory,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private async Task WriteManifestAsync(
        ZipArchive archive,
        SupportBundleContext context,
        int availableLogFiles,
        CancellationToken cancellationToken)
    {
        var safeMetadata = context.Metadata?
            .Where(pair => !_redactor.IsSensitiveKey(pair.Key) && !_redactor.IsPersonalDataKey(pair.Key))
            .Take(_options.MaxMetadataEntries)
            .ToDictionary(
                pair => Limit(_redactor.Redact(pair.Key), 128),
                pair => Limit(_redactor.Redact(pair.Value), 1024),
                StringComparer.OrdinalIgnoreCase);

        var policySummary = context.Policy is null
            ? null
            : new
            {
                context.Policy.IsManaged,
                sources = context.Policy.AppliedSources.Select(source => source.ToString()).ToArray(),
                issueCount = context.Policy.Issues.Count,
                effective = new
                {
                    context.Policy.Policy.BrowserSessionAllowed,
                    context.Policy.Policy.InteractiveSignInAllowed,
                    context.Policy.Policy.AutomaticUpdatesRequired,
                    context.Policy.Policy.SupportBundlesAllowed,
                    context.Policy.Policy.UpdateChannel,
                    context.Policy.Policy.UploadQueueQuotaBytes,
                    context.Policy.Policy.MaxUploadPayloadBytes,
                    context.Policy.Policy.PayloadRetentionDays,
                    context.Policy.Policy.DiagnosticRetentionDays,
                    context.Policy.Policy.DiagnosticMaxFileBytes,
                    context.Policy.Policy.DiagnosticMaxArchiveFiles,
                    allowedSharePointHostCount = context.Policy.Policy.AllowedSharePointHosts.Count,
                    allowedTenantCount = context.Policy.Policy.AllowedTenantIds.Count
                }
            };

        var manifest = new
        {
            schemaVersion = 1,
            createdAt = DateTimeOffset.UtcNow,
            appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            availableLogFiles,
            policy = policySummary,
            metadata = safeMetadata,
            exclusions = new[]
            {
                "authentication tokens",
                "browser cookies and profile",
                "upload payloads",
                "local database"
            }
        };

        var entry = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(
            stream,
            manifest,
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> AddRedactedLogAsync(
        ZipArchive archive,
        FileInfo logFile,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            logFile.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024,
            leaveOpen: false);
        var entry = archive.CreateEntry($"logs/{logFile.Name}", CompressionLevel.SmallestSize);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(
            entryStream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 64 * 1024,
            leaveOpen: false)
        {
            NewLine = "\n"
        };

        long written = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var redacted = _redactor.Redact(line);
            var lineBytes = Encoding.UTF8.GetByteCount(redacted) + 1;
            if (lineBytes > maxBytes - written)
            {
                const string marker = "{\"truncated\":true}";
                var markerBytes = Encoding.UTF8.GetByteCount(marker) + 1;
                if (markerBytes <= maxBytes - written)
                {
                    await writer.WriteLineAsync(marker.AsMemory(), cancellationToken).ConfigureAwait(false);
                    written += markerBytes;
                }

                break;
            }

            await writer.WriteLineAsync(redacted.AsMemory(), cancellationToken).ConfigureAwait(false);
            written += lineBytes;
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    private static bool IsWithin(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Limit(string? value, int maxCharacters)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxCharacters ? text : text[..maxCharacters] + "[TRUNCATED]";
    }

    private static SupportBundleResult Failed(string error) =>
        new(false, null, 0, 0, error);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale redacted temporary bundle can be removed by later maintenance.
        }
    }
}
