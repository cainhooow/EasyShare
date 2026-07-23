using EasyShare.Models;

namespace EasyShare.Services;

public interface ISharePointExplorerService
{
    Task<IReadOnlyList<SharePointSiteInfo>> DiscoverSitesAsync(
        string? query = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SharePointLibraryInfo>> GetLibrariesAsync(
        string siteId,
        CancellationToken cancellationToken = default);

    Task<SharePointExplorerPage<SharePointExplorerItem>> GetChildrenAsync(
        string driveId,
        string itemId,
        string? nextLink = null,
        CancellationToken cancellationToken = default);

    Task<SharePointPinnedFolder> ResolveFolderAsync(
        SharePointRouteInput routeInput,
        CancellationToken cancellationToken = default);

    void ClearCache();
}
