using EasyShare.Resources;

namespace EasyShare.Models;

public sealed class DriveRoute
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string DisplayName { get; set; } = string.Empty;

    public string SharePointUrl { get; set; } = string.Empty;

    public string RemotePath { get; set; } = "/";

    public bool IsConnected { get; set; }

    public string StatusText { get; set; } = AppText.Get("DriveRouteUntested");

    public DateTimeOffset? LastCheckedAt { get; set; }

    public string VirtualPath => $@"\\EasyShare\{DisplayName}";

    public string LastCheckedText =>
        LastCheckedAt is null
            ? AppText.Get("DriveRouteUntested")
            : AppText.Format("DriveRouteLastCheckedFormat", LastCheckedAt.Value.LocalDateTime);
}
