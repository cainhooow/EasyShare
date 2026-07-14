using EasyShare.Resources;

namespace EasyShare.Models;

public sealed class DriveRoute
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string DisplayName { get; set; } = string.Empty;

    public string SharePointUrl { get; set; } = string.Empty;

    public string RemotePath { get; set; } = "/";

    public string SiteId { get; set; } = string.Empty;

    public string DriveId { get; set; } = string.Empty;

    public string RootItemId { get; set; } = string.Empty;

    public string FolderWebUrl { get; set; } = string.Empty;

    public bool HasGraphIdentity =>
        !string.IsNullOrWhiteSpace(SiteId) &&
        !string.IsNullOrWhiteSpace(DriveId) &&
        !string.IsNullOrWhiteSpace(RootItemId);

    public string LocationUrl => string.IsNullOrWhiteSpace(FolderWebUrl)
        ? SharePointUrl
        : FolderWebUrl;

    public bool IsConnected { get; set; }

    public string StatusText { get; set; } = AppText.Get("DriveRouteUntested");

    public DateTimeOffset? LastCheckedAt { get; set; }

    public string VirtualPath => $@"\\EasyShare\{DisplayName}";

    public string LastCheckedText =>
        LastCheckedAt is null
            ? AppText.Get("DriveRouteUntested")
            : AppText.Format("DriveRouteLastCheckedFormat", LastCheckedAt.Value.LocalDateTime);
}
