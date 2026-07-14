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

    public sealed record UploadAttemptResult(UploadAttemptState State, string? Error = null);

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
            CancellationToken cancellationToken = default);
    }
}
