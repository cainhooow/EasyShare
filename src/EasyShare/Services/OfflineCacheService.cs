using System.IO.Pipelines;
using System.Text.Json;
using System.Collections.Concurrent;
using EasyShare.Models;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace EasyShare.Services;

/// <summary>
/// Selective, encrypted offline cache. File contents are streamed directly from
/// SharePoint into the authenticated encrypted store; no plaintext staging file is used.
/// Explicitly pinned content is never evicted silently.
/// </summary>
public sealed class OfflineCacheService
{
    private const int IndexSchemaVersion = 1;
    private readonly AppDataPaths _paths;
    private readonly SharePointBrowserContentService _content;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private ConcurrentDictionary<string, OfflineCacheRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public OfflineCacheService(AppDataPaths paths, SharePointBrowserContentService content)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            _paths.EnsureOfflineCacheCreated();
            _records = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
            var missing = _records
                .Where(pair => !File.Exists(GetPayloadPath(pair.Key)))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in missing)
            {
                _records.TryRemove(key, out _);
            }

            _initialized = true;
            if (missing.Length > 0)
            {
                await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<OfflineCacheEntry>> GetEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _records.Values
                .Select(ToEntry)
                .OrderBy(entry => entry.DisplayPath, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OfflineCachePinResult> PinRouteAsync(
        DriveRoute route,
        AppSettings settings,
        IProgress<OfflineCacheProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(settings);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (ShouldPause(settings, out var reason))
        {
            return new OfflineCachePinResult(false, 0, 0, reason);
        }

        var quotaBytes = checked((long)Math.Clamp(settings.OfflineCacheLimitMb, 128, 102400) * 1024 * 1024);
        var pending = new Queue<string>();
        pending.Enqueue(string.Empty);
        var files = 0;
        long bytes = 0;
        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = pending.Dequeue();
                var children = await _content.ListDirectoryAsync(route, parent, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relativePath = CombineRelative(parent, child.Name);
                    if (child.IsDirectory)
                    {
                        pending.Enqueue(relativePath);
                        continue;
                    }

                    if (child.Length > quotaBytes)
                    {
                        return new OfflineCachePinResult(false, files, bytes, "The file exceeds the offline cache quota.");
                    }

                    progress?.Report(new OfflineCacheProgress(relativePath, files, bytes));
                    var cached = await CacheFileAsync(
                        route,
                        relativePath,
                        child.Length,
                        child.ModifiedAt,
                        quotaBytes,
                        cancellationToken).ConfigureAwait(false);
                    files++;
                    bytes = checked(bytes + cached.SizeBytes);
                }
            }

            return new OfflineCachePinResult(true, files, bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new OfflineCachePinResult(false, files, bytes, ex.Message);
        }
    }

    public async Task<bool> TryCopyToAsync(
        Guid routeId,
        string relativePath,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var key = BuildKey(routeId, relativePath);
        OfflineCacheRecord? record;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _records.TryGetValue(key, out record);
        }
        finally
        {
            _gate.Release();
        }

        if (record is null || !File.Exists(GetPayloadPath(key)))
        {
            return false;
        }

        var store = CreateStore(long.MaxValue / 4);
        try
        {
            await store.DecryptToAsync(GetPayloadPath(key), destination, cancellationToken).ConfigureAwait(false);
            await TouchAsync(key, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await MarkErrorAsync(key, ex.Message, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    public async Task<byte[]?> TryReadAllBytesAsync(
        Guid routeId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        using var destination = new MemoryStream();
        return await TryCopyToAsync(routeId, relativePath, destination, cancellationToken).ConfigureAwait(false)
            ? destination.ToArray()
            : null;
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_records.TryRemove(key, out _))
            {
                return false;
            }

            try
            {
                if (File.Exists(GetPayloadPath(key)))
                {
                    File.Delete(GetPayloadPath(key));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<SharePointDriveItem> GetDirectoryItems(DriveRoute route, string relativePath)
    {
        if (!_initialized)
        {
            return [];
        }

        var normalizedParent = NormalizeRelative(relativePath);
        var prefix = string.IsNullOrEmpty(normalizedParent) ? string.Empty : normalizedParent + "/";
        var candidates = _records.Values
                .Where(record => record.RouteId == route.Id &&
                                 record.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var results = new Dictionary<string, SharePointDriveItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in candidates)
            {
                var remainder = record.RelativePath[prefix.Length..];
                if (string.IsNullOrWhiteSpace(remainder))
                {
                    continue;
                }

                var separator = remainder.IndexOf('/');
                if (separator >= 0)
                {
                    var directoryName = remainder[..separator];
                    if (!results.ContainsKey(directoryName))
                    {
                        results[directoryName] = new SharePointDriveItem(
                            directoryName,
                            CombineRelative(normalizedParent, directoryName),
                            true,
                            0,
                            record.ModifiedAt);
                    }

                    continue;
                }

                results[remainder] = new SharePointDriveItem(
                    remainder,
                    record.RelativePath,
                    false,
                    record.SizeBytes,
                    record.ModifiedAt);
            }

        return results.Values
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private async Task<OfflineCacheRecord> CacheFileAsync(
        DriveRoute route,
        string relativePath,
        long expectedSize,
        DateTimeOffset modifiedAt,
        long quotaBytes,
        CancellationToken cancellationToken)
    {
        var key = BuildKey(route.Id, relativePath);
        var path = GetPayloadPath(key);
        var store = CreateStore(quotaBytes);
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 4L * 1024 * 1024,
            resumeWriterThreshold: 2L * 1024 * 1024,
            useSynchronizationContext: false));
        var writer = pipe.Writer.AsStream(leaveOpen: false);
        var reader = pipe.Reader.AsStream(leaveOpen: false);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var download = DownloadIntoPipeAsync(route, relativePath, writer, operationCancellation.Token);
        var encryption = store.StoreAsync(path, reader, operationCancellation.Token);
        try
        {
            var first = await Task.WhenAny(download, encryption).ConfigureAwait(false);
            if (first.IsFaulted || first.IsCanceled)
            {
                operationCancellation.Cancel();
            }

            await Task.WhenAll(download, encryption).ConfigureAwait(false);
        }
        catch
        {
            operationCancellation.Cancel();
            throw;
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            await reader.DisposeAsync().ConfigureAwait(false);
        }

        var encrypted = await encryption.ConfigureAwait(false);
        if (expectedSize >= 0 && encrypted.PlaintextBytes != expectedSize)
        {
            await store.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
            throw new IOException("The downloaded content length did not match SharePoint metadata.");
        }

        var now = DateTimeOffset.UtcNow;
        var record = new OfflineCacheRecord(
            key,
            route.Id,
            route.DisplayName,
            NormalizeRelative(relativePath),
            encrypted.PlaintextBytes,
            modifiedAt,
            now,
            now,
            OfflineCacheState.Available,
            null);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _records[key] = record;
            await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return record;
    }

    private async Task DownloadIntoPipeAsync(
        DriveRoute route,
        string relativePath,
        Stream writer,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await _content.DownloadFileAsync(route, relativePath, writer, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new IOException("SharePoint content is unavailable for offline caching.");
            }
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private EncryptedFileStore CreateStore(long quotaBytes) => new(
        _paths.OfflineCacheDirectory,
        _paths.OfflineCacheKeyPath,
        new UploadPayloadStorageOptions
        {
            MaxPayloadBytes = quotaBytes,
            MaxTotalBytes = quotaBytes,
            OrphanRetention = TimeSpan.Zero,
            TemporaryFileRetention = TimeSpan.FromHours(1)
        },
        fileExtension: ".offline");

    private async Task TouchAsync(string key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_records.TryGetValue(key, out var record))
            {
                _records[key] = record with { LastAccessedAt = DateTimeOffset.UtcNow };
                await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MarkErrorAsync(string key, string error, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_records.TryGetValue(key, out var record))
            {
                _records[key] = record with
                {
                    State = OfflineCacheState.Error,
                    Error = error.Length <= 256 ? error : error[..256]
                };
                await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ConcurrentDictionary<string, OfflineCacheRecord>> ReadIndexAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.OfflineCacheIndexPath))
        {
            return new ConcurrentDictionary<string, OfflineCacheRecord>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var stream = new FileStream(
                _paths.OfflineCacheIndexPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var index = await JsonSerializer.DeserializeAsync<OfflineCacheIndex>(
                stream,
                _jsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (index is null || index.SchemaVersion != IndexSchemaVersion)
            {
                return new ConcurrentDictionary<string, OfflineCacheRecord>(StringComparer.OrdinalIgnoreCase);
            }

            return new ConcurrentDictionary<string, OfflineCacheRecord>(index.Entries
                .GroupBy(record => record.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ConcurrentDictionary<string, OfflineCacheRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task SaveIndexAsync(CancellationToken cancellationToken)
    {
        _paths.EnsureOfflineCacheCreated();
        var temporaryPath = _paths.OfflineCacheIndexPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new OfflineCacheIndex(IndexSchemaVersion, _records.Values.ToArray()),
                    _jsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _paths.OfflineCacheIndexPath, overwrite: true);
            PrivateFilePermissions.TryHardenFile(_paths.OfflineCacheIndexPath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private string GetPayloadPath(string key) =>
        Path.Combine(_paths.OfflineCacheDirectory, key + ".offline");

    private static string BuildKey(Guid routeId, string relativePath)
    {
        var value = $"{routeId:N}:{NormalizeRelative(relativePath).ToUpperInvariant()}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string NormalizeRelative(string value) =>
        string.Join('/', (value ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string CombineRelative(string parent, string child) =>
        string.IsNullOrWhiteSpace(parent)
            ? NormalizeRelative(child)
            : $"{NormalizeRelative(parent)}/{NormalizeRelative(child)}";

    private static OfflineCacheEntry ToEntry(OfflineCacheRecord record) => new(
        record.Key,
        record.RouteId,
        record.RouteDisplayName,
        record.RelativePath,
        record.SizeBytes,
        record.CachedAt,
        record.State == OfflineCacheState.Available &&
        DateTimeOffset.UtcNow - record.CachedAt > TimeSpan.FromHours(24)
            ? OfflineCacheState.Stale
            : record.State,
        record.Error);

    private static bool ShouldPause(AppSettings settings, out string? reason)
    {
        reason = null;
        try
        {
            if (settings.OfflinePauseOnMeteredNetwork)
            {
                var cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
                if (cost is { NetworkCostType: not NetworkCostType.Unrestricted })
                {
                    reason = "Offline download is paused on the current metered network.";
                    return true;
                }
            }

            if (settings.OfflinePauseOnBattery &&
                PowerManager.PowerSupplyStatus != PowerSupplyStatus.Adequate)
            {
                reason = "Offline download is paused while the device is on battery.";
                return true;
            }
        }
        catch
        {
            // If Windows cannot report cost or power, do not strand explicitly requested work.
        }

        return false;
    }

    private sealed record OfflineCacheIndex(int SchemaVersion, IReadOnlyList<OfflineCacheRecord> Entries);

    private sealed record OfflineCacheRecord(
        string Key,
        Guid RouteId,
        string RouteDisplayName,
        string RelativePath,
        long SizeBytes,
        DateTimeOffset ModifiedAt,
        DateTimeOffset CachedAt,
        DateTimeOffset LastAccessedAt,
        OfflineCacheState State,
        string? Error);
}

public sealed record OfflineCacheProgress(string RelativePath, int CompletedFiles, long CompletedBytes);

public sealed record OfflineCachePinResult(
    bool Succeeded,
    int FileCount,
    long BytesCached,
    string? Error = null);
