using EasyShare.Resources;

namespace EasyShare.Models;

public enum OfflineCacheState
{
    Available,
    Updating,
    Stale,
    Error
}

public sealed record OfflineCacheEntry(
    string Key,
    Guid RouteId,
    string RouteDisplayName,
    string RelativePath,
    long SizeBytes,
    DateTimeOffset CachedAt,
    OfflineCacheState State,
    string? Error = null)
{
    public string DisplayPath => string.IsNullOrWhiteSpace(RelativePath)
        ? RouteDisplayName
        : $"{RouteDisplayName} / {RelativePath.Replace('\\', '/')}";

    public string SizeText => SizeBytes switch
    {
        >= 1024L * 1024L * 1024L => $"{SizeBytes / (1024d * 1024d * 1024d):0.##} GB",
        >= 1024L * 1024L => $"{SizeBytes / (1024d * 1024d):0.##} MB",
        >= 1024L => $"{SizeBytes / 1024d:0.##} KB",
        _ => $"{SizeBytes} B"
    };

    public string StateText => State switch
    {
        OfflineCacheState.Available => AppText.Get("OfflineStateAvailable"),
        OfflineCacheState.Updating => AppText.Get("OfflineStateUpdating"),
        OfflineCacheState.Stale => AppText.Get("OfflineStateStale"),
        _ => AppText.Get("OfflineStateError")
    };

    public string FreshnessText => AppText.Format(
        "OfflineCachedAtFormat",
        CachedAt.ToLocalTime().ToString("g"));
}
