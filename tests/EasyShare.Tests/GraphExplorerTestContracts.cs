// The test project links the Graph services without loading WinUI. These minimal contracts mirror
// the production types that are otherwise declared alongside the WinUI/browser implementation.
namespace EasyShare.Services
{
    using EasyShare.Models;

    public interface IAuthenticationService
    {
        Task<string?> GetAccessTokenAsync();
    }

    public enum UploadAttemptState
    {
        Succeeded,
        RetryableFailure,
        Conflict
    }

    public sealed record UploadAttemptResult(
        UploadAttemptState State,
        string? Error = null,
        RemoteUploadReceipt? Receipt = null,
        SyncFailureKind FailureKind = SyncFailureKind.Unknown,
        string? TechnicalDetails = null,
        bool IsCommitAmbiguous = false);

    public interface ISharePointContentTransfer
    {
        Task<bool> DownloadFileAsync(
            DriveRoute route,
            string relativePath,
            Stream destination,
            CancellationToken cancellationToken = default);

        Task<UploadAttemptResult> TryUploadFileAsync(
            DriveRoute route,
            string relativePath,
            Stream content,
            DateTimeOffset? expectedModifiedAt,
            CancellationToken cancellationToken = default,
            IProgress<UploadTransferProgress>? progress = null);

        Task<RemoteDeleteAttemptResult> TryDeleteItemAsync(
            DriveRoute route,
            string relativePath,
            bool isDirectory,
            CancellationToken cancellationToken = default);
    }
}
