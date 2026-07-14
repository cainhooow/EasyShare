using System.Net;

namespace EasyShare.Models;

public sealed record SharePointSiteInfo(
    string Id,
    string DisplayName,
    string WebUrl,
    string? Description,
    bool IsFollowed);

public sealed record SharePointLibraryInfo(
    string Id,
    string SiteId,
    string Name,
    string WebUrl,
    string RootItemId);

public sealed record SharePointExplorerItem(
    string Id,
    string DriveId,
    string Name,
    string WebUrl,
    bool IsFolder,
    long Size,
    DateTimeOffset? ModifiedAt);

public sealed record SharePointExplorerPage<T>(
    IReadOnlyList<T> Items,
    string? NextLink);

public sealed record SharePointPinnedFolder(
    string SiteId,
    string DriveId,
    string ItemId,
    string DisplayName,
    string SiteWebUrl,
    string FolderWebUrl,
    string DisplayPath);

public enum SharePointExplorerStatus
{
    AuthenticationRequired,
    Forbidden,
    Throttled,
    NotFound,
    ServiceUnavailable,
    InvalidResponse
}

public sealed class SharePointExplorerException : Exception
{
    public SharePointExplorerException(
        SharePointExplorerStatus status,
        string message,
        HttpStatusCode? httpStatusCode = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
        HttpStatusCode = httpStatusCode;
        RetryAfter = retryAfter;
    }

    public SharePointExplorerStatus Status { get; }

    public HttpStatusCode? HttpStatusCode { get; }

    public TimeSpan? RetryAfter { get; }
}
