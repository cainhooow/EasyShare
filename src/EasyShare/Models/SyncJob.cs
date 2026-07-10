using EasyShare.Resources;

namespace EasyShare.Models;

public enum SyncJobState
{
    Waiting,
    Uploading,
    Completed,
    Failed,
    Conflict
}

public sealed class SyncJob
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid? RouteId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string RouteDisplayName { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string PayloadPath { get; set; } = string.Empty;

    public DateTimeOffset? ExpectedModifiedAt { get; set; }

    public int Attempts { get; set; }

    public string LastError { get; set; } = string.Empty;

    public DateTimeOffset? NextAttemptAt { get; set; }

    public SyncJobState State { get; set; } = SyncJobState.Waiting;

    public int Progress { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string StateText => State switch
    {
        SyncJobState.Uploading => AppText.Get("SyncUploading"),
        SyncJobState.Completed => AppText.Get("SyncCompleted"),
        SyncJobState.Conflict => AppText.Get("SyncConflict"),
        SyncJobState.Failed => AppText.Get("SyncNeedsAttention"),
        _ => AppText.Get("SyncQueued")
    };

    public string UpdatedText => UpdatedAt.LocalDateTime.ToString("g");
}
