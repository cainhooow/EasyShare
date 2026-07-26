using EasyShare.Models;
using System.Collections.Concurrent;

namespace EasyShare.Services;

// The test project links UploadQueueService without loading the WinUI/WebView layer.
// This in-memory transport exercises the durable queue without loading WinUI/WebView.
public sealed class SharePointBrowserContentService : ISharePointContentTransfer
{
    private readonly ConcurrentDictionary<string, SharePointDriveItem> _items =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _deleteTombstones =
        new(StringComparer.OrdinalIgnoreCase);
    private int _uploadCalls;
    private int _deleteCalls;

    public Func<
        DriveRoute,
        string,
        Stream,
        DateTimeOffset?,
        CancellationToken,
        IProgress<UploadTransferProgress>?,
        Task<UploadAttemptResult>>? UploadHandler { get; set; }

    public Func<
        DriveRoute,
        string,
        CancellationToken,
        Task<SharePointDriveItem?>>? GetItemHandler { get; set; }

    public Func<
        DriveRoute,
        string,
        CancellationToken,
        Task<RemoteUploadVerificationResult>>? VerifyHandler { get; set; }

    public Func<
        DriveRoute,
        string,
        bool,
        CancellationToken,
        Task<RemoteDeleteAttemptResult>>? DeleteHandler { get; set; }

    public int UploadCalls => Volatile.Read(ref _uploadCalls);

    public int DeleteCalls => Volatile.Read(ref _deleteCalls);

    public Task<bool> DownloadFileAsync(
        DriveRoute route,
        string relativePath,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public async Task<UploadAttemptResult> TryUploadFileAsync(
        DriveRoute route,
        string relativePath,
        Stream content,
        DateTimeOffset? expectedModifiedAt,
        CancellationToken cancellationToken = default,
        IProgress<UploadTransferProgress>? progress = null)
    {
        Interlocked.Increment(ref _uploadCalls);
        if (UploadHandler is not null)
        {
            return await UploadHandler(
                route,
                relativePath,
                content,
                expectedModifiedAt,
                cancellationToken,
                progress);
        }

        var total = content.CanSeek ? content.Length - content.Position : (long?)null;
        long transferred = 0;
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await content.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            transferred += read;
            progress?.Report(new UploadTransferProgress(transferred, total));
        }

        var modifiedAt = DateTimeOffset.UtcNow;
        var item = new SharePointDriveItem(
            Path.GetFileName(relativePath),
            relativePath,
            false,
            transferred,
            modifiedAt);
        _items[Key(route, relativePath)] = item;
        return new UploadAttemptResult(
            UploadAttemptState.Succeeded,
            Receipt: new RemoteUploadReceipt(
                $"item-{route.Id:N}-{Path.GetFileName(relativePath)}",
                "\"test-etag\"",
                transferred,
                modifiedAt),
            FailureKind: SyncFailureKind.None);
    }

    public Task<SharePointDriveItem?> GetItemAsync(
        DriveRoute route,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        if (GetItemHandler is not null)
        {
            return GetItemHandler(route, relativePath, cancellationToken);
        }

        _items.TryGetValue(Key(route, relativePath), out var item);
        return Task.FromResult(item);
    }

    public Task<RemoteUploadVerificationResult> VerifyRemoteUploadAsync(
        DriveRoute route,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        if (VerifyHandler is not null)
        {
            return VerifyHandler(route, relativePath, cancellationToken);
        }

        if (GetItemHandler is not null)
        {
            return VerifyFromLegacyHandlerAsync(route, relativePath, cancellationToken);
        }

        if (!_items.TryGetValue(Key(route, relativePath), out var item))
        {
            return Task.FromResult(new RemoteUploadVerificationResult(
                RemoteUploadVerificationState.NotFound,
                FailureKind: SyncFailureKind.None));
        }

        return Task.FromResult(new RemoteUploadVerificationResult(
            RemoteUploadVerificationState.Confirmed,
            new RemoteUploadReceipt(
                $"item-{route.Id:N}-{Path.GetFileName(relativePath)}",
                "\"test-etag\"",
                item.Length,
                item.ModifiedAt),
            SyncFailureKind.None));
    }

    public async Task<RemoteDeleteAttemptResult> TryDeleteItemAsync(
        DriveRoute route,
        string relativePath,
        bool isDirectory,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _deleteCalls);
        if (DeleteHandler is not null)
        {
            return await DeleteHandler(route, relativePath, isDirectory, cancellationToken);
        }

        var normalized = relativePath.Replace('\\', '/').Trim('/');
        var removed = false;
        foreach (var key in _items.Keys)
        {
            var prefix = $"{route.Id:N}:";
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var itemPath = key[prefix.Length..];
            if (string.Equals(itemPath, normalized, StringComparison.OrdinalIgnoreCase) ||
                isDirectory && itemPath.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase))
            {
                removed |= _items.TryRemove(key, out _);
            }
        }

        return new RemoteDeleteAttemptResult(
            RemoteDeleteAttemptState.Succeeded,
            SyncFailureKind.None,
            HttpStatusCode: removed ? 204 : 404);
    }

    public void RegisterDeleteTombstone(Guid routeId, string relativePath, bool isDirectory)
    {
        _deleteTombstones[$"{routeId:N}:{relativePath.Replace('\\', '/').Trim('/')}"] = isDirectory;
    }

    public void ClearDeleteTombstone(Guid routeId, string relativePath)
    {
        _deleteTombstones.TryRemove(
            $"{routeId:N}:{relativePath.Replace('\\', '/').Trim('/')}",
            out _);
    }

    public void SetRemoteItem(DriveRoute route, string relativePath, long length, DateTimeOffset modifiedAt) =>
        _items[Key(route, relativePath)] = new SharePointDriveItem(
            Path.GetFileName(relativePath),
            relativePath,
            false,
            length,
            modifiedAt);

    public bool HasRemoteItem(DriveRoute route, string relativePath) =>
        _items.ContainsKey(Key(route, relativePath));

    public bool RemoveRemoteItem(DriveRoute route, string relativePath) =>
        _items.TryRemove(Key(route, relativePath), out _);

    public bool IsDeleteTombstoned(DriveRoute route, string relativePath) =>
        _deleteTombstones.ContainsKey(Key(route, relativePath));

    private static string Key(DriveRoute route, string relativePath) =>
        $"{route.Id:N}:{relativePath.Replace('\\', '/').Trim('/')}";

    private async Task<RemoteUploadVerificationResult> VerifyFromLegacyHandlerAsync(
        DriveRoute route,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var item = await GetItemHandler!(route, relativePath, cancellationToken);
        return item is null
            ? new RemoteUploadVerificationResult(
                RemoteUploadVerificationState.NotFound,
                FailureKind: SyncFailureKind.None)
            : new RemoteUploadVerificationResult(
                RemoteUploadVerificationState.Confirmed,
                new RemoteUploadReceipt(null, null, item.Length, item.ModifiedAt),
                SyncFailureKind.None);
    }
}

public static class StartupDiagnostics
{
    public static void Write(string message, Exception exception)
    {
    }
}
