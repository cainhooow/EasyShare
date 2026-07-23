namespace EasyShare.Models;

/// <summary>
/// Describes how an indexed item was most recently accessed.
/// </summary>
public enum ContentAccessKind
{
    Unknown = 0,
    SearchResult = 1,
    FolderOpened = 2,
    FileOpened = 3,
    VirtualDrive = 4
}

/// <summary>
/// A file or folder ready to be persisted in the local content index.
/// Scope is deliberately supplied separately to every service operation so
/// callers cannot accidentally mix metadata from different identities.
/// </summary>
public sealed record ContentIndexItem(
    Guid RouteId,
    string RouteDisplayName,
    string RelativePath,
    string Name,
    bool IsDirectory,
    long Length = 0,
    DateTimeOffset? ModifiedAt = null,
    string? RemoteLocator = null);

/// <summary>
/// A ranked local search or access-history result.
/// </summary>
public sealed record ContentSearchResult(
    Guid RouteId,
    string RouteDisplayName,
    string RelativePath,
    string Name,
    bool IsDirectory,
    long Length,
    DateTimeOffset? ModifiedAt,
    string? RemoteLocator,
    long AccessCount,
    DateTimeOffset? LastAccessedAt,
    ContentAccessKind? LastAccessKind,
    double Score);
