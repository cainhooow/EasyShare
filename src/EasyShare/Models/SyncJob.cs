using EasyShare.Resources;

namespace EasyShare.Models;

public enum SyncJobState
{
    // Values 0-4 are persisted by released versions. Never reorder or reuse them.
    Waiting = 0,
    Uploading = 1,
    Completed = 2,
    Failed = 3,
    Conflict = 4,
    PersistingLocal = 5,
    StoredLocally = 6,
    VerifyingRemote = 7,
    Discarded = 8
}

public enum SyncFailureKind
{
    None = 0,
    Network = 1,
    Session = 2,
    Permission = 3,
    Quota = 4,
    Conflict = 5,
    Integrity = 6,
    RouteUnavailable = 7,
    PayloadUnavailable = 8,
    Unknown = 9,
    ServiceBusy = 10
}

public enum SyncWaitReason
{
    None = 0,
    Network = 1,
    Session = 2,
    Backoff = 3,
    RemoteVerification = 4
}

public enum SyncOperationKind
{
    Upload = 0,
    Delete = 1
}

public enum RemoteDeleteAttemptState
{
    Succeeded = 0,
    RetryableFailure = 1,
    TerminalFailure = 2
}

public sealed record RemoteDeleteAttemptResult(
    RemoteDeleteAttemptState State,
    SyncFailureKind FailureKind = SyncFailureKind.None,
    string? Error = null,
    string? TechnicalDetails = null,
    int? HttpStatusCode = null);

public sealed record UploadTransferProgress(
    long BytesTransferred,
    long? TotalBytes,
    bool IsAcknowledged = true,
    bool MayHaveCommitted = false);

public sealed record RemoteUploadReceipt(
    string? ItemId,
    string? ETag,
    long? Size,
    DateTimeOffset? ModifiedAt);

public enum RemoteUploadVerificationState
{
    Confirmed = 0,
    NotFound = 1,
    Unavailable = 2
}

public sealed record RemoteUploadVerificationResult(
    RemoteUploadVerificationState State,
    RemoteUploadReceipt? Receipt = null,
    SyncFailureKind FailureKind = SyncFailureKind.Unknown,
    string? UserMessage = null,
    string? TechnicalDetails = null);

public sealed class SyncJob
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid? RouteId { get; set; }

    public string OperationKey { get; set; } = string.Empty;

    public SyncOperationKind OperationKind { get; set; }

    public bool IsDirectory { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string RouteDisplayName { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string PayloadPath { get; set; } = string.Empty;

    public long? PayloadLength { get; set; }

    public string PayloadSha256 { get; set; } = string.Empty;

    public DateTimeOffset? ExpectedModifiedAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>
    /// Backward-compatible friendly error persisted by older versions.
    /// New code keeps it equal to <see cref="UserMessage"/> and stores technical
    /// diagnostics separately.
    /// </summary>
    public string LastError { get; set; } = string.Empty;

    public SyncFailureKind FailureKind { get; set; }

    public SyncWaitReason WaitReason { get; set; }

    public string UserMessage { get; set; } = string.Empty;

    public string TechnicalDetails { get; set; } = string.Empty;

    public DateTimeOffset? NextAttemptAt { get; set; }

    public SyncJobState State { get; set; } = SyncJobState.PersistingLocal;

    public int Progress { get; set; }

    public long BytesTransferred { get; set; }

    public bool UploadMayHaveCommitted { get; set; }

    public DateTimeOffset? DeleteBarrierObservedAt { get; set; }

    /// <summary>
    /// Delete intents are persisted by WinFsp SetDelete before success is returned,
    /// but only become worker-eligible after CleanupDelete confirms close semantics.
    /// Existing durable deletes default to armed for migration compatibility.
    /// </summary>
    public bool DeleteArmed { get; set; } = true;

    public bool IsProgressIndeterminate { get; set; } = true;

    public double? BytesPerSecond { get; set; }

    public DateTimeOffset? EstimatedCompletionAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StoredAt { get; set; }

    public DateTimeOffset? UploadStartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? RemoteConfirmedAt { get; set; }

    public string RemoteItemId { get; set; } = string.Empty;

    public string RemoteETag { get; set; } = string.Empty;

    public string RemoteLocator { get; set; } = string.Empty;

    public long? RemoteLength { get; set; }

    public DateTimeOffset? RemoteModifiedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsActive => State is not (SyncJobState.Completed or SyncJobState.Discarded);

    public string StateText => OperationKind == SyncOperationKind.Delete
        ? State switch
        {
            SyncJobState.Uploading => AppText.Get("SyncDeleting"),
            SyncJobState.Completed => AppText.Get("SyncDeleteCompleted"),
            SyncJobState.Failed => AppText.Get("SyncNeedsAttention"),
            SyncJobState.Discarded => AppText.Get("SyncDiscarded"),
            _ => AppText.Get("SyncDeleteQueued")
        }
        : State switch
        {
            SyncJobState.PersistingLocal => AppText.Get("SyncPersistingLocal"),
            SyncJobState.StoredLocally => AppText.Get("SyncStoredLocally"),
            SyncJobState.Uploading => AppText.Get("SyncUploading"),
            SyncJobState.VerifyingRemote => AppText.Get("SyncVerifyingRemote"),
            SyncJobState.Completed => AppText.Get("SyncCompleted"),
            SyncJobState.Discarded => AppText.Get("SyncDiscarded"),
            SyncJobState.Conflict => AppText.Get("SyncConflict"),
            SyncJobState.Failed => AppText.Get("SyncNeedsAttention"),
            _ => AppText.Get("SyncQueued")
        };

    public string ProgressText
    {
        get
        {
            if (OperationKind == SyncOperationKind.Delete)
            {
                return string.Empty;
            }

            if (State == SyncJobState.Discarded)
            {
                return string.Empty;
            }

            if (IsProgressIndeterminate &&
                State is SyncJobState.PersistingLocal or SyncJobState.Uploading or SyncJobState.VerifyingRemote)
            {
                return AppText.Get("SyncProgressIndeterminate");
            }

            string baseText;
            if (PayloadLength is > 0)
            {
                baseText = AppText.Format(
                    "SyncProgressBytesFormat",
                    FormatBytes(Math.Clamp(BytesTransferred, 0, PayloadLength.Value)),
                    FormatBytes(PayloadLength.Value),
                    Math.Clamp(Progress, 0, 100));
            }
            else
            {
                baseText = AppText.Format("SyncProgressPercentFormat", Math.Clamp(Progress, 0, 100));
            }

            return !string.IsNullOrWhiteSpace(TransferRateText) &&
                   !string.IsNullOrWhiteSpace(EtaText)
                ? AppText.Format("SyncProgressWithRateFormat", baseText, TransferRateText, EtaText)
                : baseText;
        }
    }

    public string TransferRateText => BytesPerSecond is > 0
        ? AppText.Format("SyncTransferRateFormat", FormatBytes((long)BytesPerSecond.Value))
        : string.Empty;

    public string EtaText => EstimatedCompletionAt is { } estimate && estimate > DateTimeOffset.UtcNow
        ? AppText.Format("SyncEtaFormat", estimate.LocalDateTime)
        : string.Empty;

    public string NextAttemptText => NextAttemptAt is null
        ? string.Empty
        : AppText.Format("SyncNextAttemptFormat", NextAttemptAt.Value.LocalDateTime);

    public string FailureSummary => string.IsNullOrWhiteSpace(UserMessage)
        ? LastError
        : UserMessage;

    public string RecommendedActionText => OperationKind == SyncOperationKind.Delete
        ? FailureKind switch
        {
            SyncFailureKind.Network => AppText.Get("SyncDeleteActionCheckConnection"),
            SyncFailureKind.Session => AppText.Get("SyncDeleteActionSignIn"),
            SyncFailureKind.Permission => AppText.Get("SyncDeleteActionCheckPermission"),
            SyncFailureKind.Conflict => AppText.Get("SyncDeleteActionReleaseLock"),
            SyncFailureKind.RouteUnavailable => AppText.Get("SyncDeleteActionRestoreRoute"),
            SyncFailureKind.ServiceBusy => AppText.Get("SyncDeleteActionWaitOrRetry"),
            SyncFailureKind.Unknown => AppText.Get("SyncDeleteActionRetry"),
            _ when State == SyncJobState.Waiting => AppText.Get("SyncDeleteActionWaitOrRetry"),
            _ => string.Empty
        }
        : FailureKind switch
        {
            _ when UploadMayHaveCommitted && UploadStartedAt is null =>
                AppText.Get("SyncActionViewDetails"),
            SyncFailureKind.Network => AppText.Get("SyncActionCheckConnection"),
            SyncFailureKind.Session => AppText.Get("SyncActionSignIn"),
            SyncFailureKind.Permission => AppText.Get("SyncActionCheckPermission"),
            SyncFailureKind.Conflict => AppText.Get("SyncActionReviewConflict"),
            SyncFailureKind.Quota => AppText.Get("SyncActionFreeSpace"),
            SyncFailureKind.Integrity => AppText.Get("SyncActionViewDetails"),
            SyncFailureKind.RouteUnavailable => AppText.Get("SyncActionRestoreRoute"),
            SyncFailureKind.PayloadUnavailable => AppText.Get("SyncActionViewDetails"),
            SyncFailureKind.ServiceBusy => AppText.Get("SyncActionWaitOrRetry"),
            SyncFailureKind.Unknown => AppText.Get("SyncActionRetry"),
            _ when State == SyncJobState.Waiting => AppText.Get("SyncActionWaitOrRetry"),
            _ => string.Empty
        };

    public bool CanRetry =>
        !(UploadMayHaveCommitted && UploadStartedAt is null) &&
        FailureKind is not (
            SyncFailureKind.Integrity or
            SyncFailureKind.PayloadUnavailable or
            SyncFailureKind.RouteUnavailable) &&
        State is
            SyncJobState.Waiting or
            SyncJobState.Failed or
            SyncJobState.StoredLocally or
            SyncJobState.VerifyingRemote;

    public bool CanExport =>
        OperationKind == SyncOperationKind.Upload &&
        State is (
            SyncJobState.Failed or
            SyncJobState.Conflict or
            SyncJobState.VerifyingRemote) &&
        !string.IsNullOrWhiteSpace(PayloadPath) &&
        File.Exists(PayloadPath) &&
        FailureKind is not (SyncFailureKind.Integrity or SyncFailureKind.PayloadUnavailable);

    public bool CanShowDetails =>
        FailureKind != SyncFailureKind.None ||
        !string.IsNullOrWhiteSpace(TechnicalDetails);

    public string AccessibilityText => AppText.Format(
        "SyncAccessibilityFormat",
        FileName,
        StateText,
        ProgressText,
        FailureSummary,
        RecommendedActionText);

    public string RetryAutomationName => AppText.Format(
        OperationKind == SyncOperationKind.Delete
            ? "SyncDeleteRetryAutomationFormat"
            : "SyncRetryAutomationFormat",
        FileName);

    public string ExportAutomationName => AppText.Format("SyncExportAutomationFormat", FileName);

    public string DetailsAutomationName => AppText.Format(
        OperationKind == SyncOperationKind.Delete
            ? "SyncDeleteDetailsAutomationFormat"
            : "SyncDetailsAutomationFormat",
        FileName);

    public string UseRemoteAutomationName => AppText.Format("SyncUseRemoteAutomationFormat", FileName);

    public string ReplaceRemoteAutomationName => AppText.Format("SyncReplaceRemoteAutomationFormat", FileName);

    public string UpdatedText => UpdatedAt.LocalDateTime.ToString("g");

    public static string CreateOperationKey(Guid routeId, string relativePath) =>
        $"{routeId:N}:{NormalizeOperationPath(relativePath).ToUpperInvariant()}";

    private static string NormalizeOperationPath(string value) =>
        (value ?? string.Empty).Replace('\\', '/').Trim('/');

    private static string FormatBytes(long bytes)
    {
        var value = Math.Max(0, bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double display = value;
        var unit = 0;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{display:0} {units[unit]}"
            : $"{display:0.#} {units[unit]}";
    }
}
