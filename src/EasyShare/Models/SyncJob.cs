using EasyShare.Resources;

namespace EasyShare.Models;

public enum SyncJobState
{
    Waiting,
    Uploading,
    Completed,
    Failed
}

public sealed class SyncJob
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string FileName { get; set; } = string.Empty;

    public string RouteDisplayName { get; set; } = string.Empty;

    public SyncJobState State { get; set; } = SyncJobState.Waiting;

    public int Progress { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string StateText => State switch
    {
        SyncJobState.Uploading => AppText.Get("SyncUploading"),
        SyncJobState.Completed => AppText.Get("SyncCompleted"),
        SyncJobState.Failed => AppText.Get("SyncNeedsAttention"),
        _ => AppText.Get("SyncQueued")
    };

    public string UpdatedText => UpdatedAt.LocalDateTime.ToString("g");
}
