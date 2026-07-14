namespace EasyShare.Models;

public enum SyncConflictActionStatus
{
    Discarded,
    Exported,
    QueuedForReplace,
    NotFound,
    InvalidState,
    DestinationAlreadyExists,
    InvalidDestination,
    PayloadUnavailable,
    Failed
}

public sealed record SyncConflictActionResult(
    SyncConflictActionStatus Status,
    SyncJob? Job = null,
    string? ExportedPath = null,
    string? Error = null)
{
    public bool Succeeded => Status is SyncConflictActionStatus.Discarded or
        SyncConflictActionStatus.Exported or
        SyncConflictActionStatus.QueuedForReplace;
}
