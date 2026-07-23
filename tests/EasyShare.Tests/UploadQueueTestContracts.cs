using EasyShare.Models;

namespace EasyShare.Services;

// The test project links UploadQueueService without loading the WinUI/WebView layer.
// These minimal doubles provide only the collaborators exercised by reset coordination.
public sealed class SharePointBrowserContentService : ISharePointContentTransfer
{
    public Task<bool> DownloadFileAsync(
        DriveRoute route,
        string relativePath,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<UploadAttemptResult> TryUploadFileAsync(
        DriveRoute route,
        string relativePath,
        Stream content,
        DateTimeOffset? expectedModifiedAt,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new UploadAttemptResult(UploadAttemptState.Succeeded));
}

public static class StartupDiagnostics
{
    public static void Write(string message, Exception exception)
    {
    }
}
