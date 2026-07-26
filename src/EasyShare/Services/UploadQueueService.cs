using System.Diagnostics;
using System.Security.Cryptography;
using EasyShare.Models;
using EasyShare.Resources;
using Windows.Networking.Connectivity;

namespace EasyShare.Services;

/// <summary>
/// Durable upload state machine. A payload is reserved in SQLite before it is
/// written, remote completion is persisted before local cleanup, and interrupted
/// transfers are reconciled with SharePoint before they are replayed.
/// </summary>
public sealed class UploadQueueService : IDisposable
{
    private const int MaxAttempts = 6;
    private static readonly TimeSpan ProgressPersistenceInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReliableRateSample = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DeleteCommitQuiescenceWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeleteBarrierRetryDelay = TimeSpan.FromSeconds(5);

    private readonly LocalDatabase _database;
    private readonly SharePointBrowserContentService _contentService;
    private readonly UploadPayloadStorage _payloadStorage;
    private readonly SensitiveDataRedactor _redactor = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _enqueueGate = new(1, 1);
    private readonly SemaphoreSlim _activeTransferGate = new(1, 1);
    private readonly SemaphoreSlim _routeLifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _startupRecoveryCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _workerGate = new();
    private readonly object _routeAdmissionGate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _routeCancellationSources = [];
    private readonly HashSet<Guid> _removedRouteIds = [];
    private CancellationTokenSource _resetCancellation = new();
    private int _resetSuspended;
    private int _stopRequested;
    private int _disposed;
    private Task? _worker;

    public event Action<SyncJob>? JobChanged;

    public UploadQueueService(
        LocalDatabase database,
        SharePointBrowserContentService contentService,
        AppDataPaths paths,
        UploadPayloadStorage? payloadStorage = null)
    {
        _database = database;
        _contentService = contentService;
        _payloadStorage = payloadStorage ?? new UploadPayloadStorage(paths);
        NetworkInformation.NetworkStatusChanged += NetworkInformation_NetworkStatusChanged;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _stopRequested) != 0)
        {
            throw new InvalidOperationException("The upload queue has already been stopped.");
        }

        lock (_workerGate)
        {
            _worker ??= Task.Run(ProcessLoopAsync);
        }
    }

    public SyncJob Enqueue(
        DriveRoute route,
        string relativePath,
        byte[] bytes,
        DateTimeOffset? expectedModifiedAt)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var payload = new MemoryStream(bytes, writable: false);
        return EnqueueAsync(route, relativePath, payload, expectedModifiedAt)
            .GetAwaiter()
            .GetResult();
    }

    public async Task<SyncJob> EnqueueAsync(
        DriveRoute route,
        string relativePath,
        Stream payload,
        DateTimeOffset? expectedModifiedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.CanRead)
        {
            throw new ArgumentException("The upload payload must be readable.", nameof(payload));
        }

        ThrowIfStopping();
        ThrowIfRouteRemoved(route.Id);
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var transferGateAcquired = false;
        try
        {
            // StopAsync uses this gate as a lifecycle barrier. Recheck after
            // entering it so an enqueue that was already waiting cannot begin
            // persistence after shutdown has started.
            ThrowIfStopping();
            ThrowIfRouteRemoved(route.Id);
            ThrowIfResetSuspended();
            var normalizedPath = NormalizePath(relativePath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                throw new ArgumentException("The destination path is required.", nameof(relativePath));
            }

            await ThrowIfDeleteBarrierAsync(route.Id, normalizedPath).ConfigureAwait(false);
            await _activeTransferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            transferGateAcquired = true;
            ThrowIfStopping();
            ThrowIfRouteRemoved(route.Id);
            ThrowIfResetSuspended();
            await ThrowIfDeleteBarrierAsync(route.Id, normalizedPath).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var operationKey = SyncJob.CreateOperationKey(route.Id, normalizedPath);
            var supersededJob = (await _database.GetActiveSyncJobsAsync(route.Id).ConfigureAwait(false))
                .Where(item =>
                    item.OperationKind == SyncOperationKind.Upload &&
                    string.Equals(item.OperationKey, operationKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.UpdatedAt)
                .FirstOrDefault();
            var job = new SyncJob
            {
                RouteId = route.Id,
                OperationKey = operationKey,
                OperationKind = SyncOperationKind.Upload,
                FileName = Path.GetFileName(normalizedPath),
                RouteDisplayName = route.DisplayName,
                RelativePath = normalizedPath,
                PayloadPath = _payloadStorage.CreatePayloadPath(),
                CreatedAt = now
            };

            job.RouteId = route.Id;
            job.OperationKey = operationKey;
            job.FileName = Path.GetFileName(normalizedPath);
            job.RouteDisplayName = route.DisplayName;
            job.RelativePath = normalizedPath;
            job.ExpectedModifiedAt = expectedModifiedAt;
            job.State = SyncJobState.PersistingLocal;
            job.Progress = 0;
            job.BytesTransferred = 0;
            job.UploadMayHaveCommitted = false;
            job.PayloadLength = payload.CanSeek ? Math.Max(0, payload.Length - payload.Position) : null;
            job.PayloadSha256 = string.Empty;
            job.IsProgressIndeterminate = job.PayloadLength is null;
            job.BytesPerSecond = null;
            job.EstimatedCompletionAt = null;
            job.StoredAt = null;
            job.UploadStartedAt = null;
            job.CompletedAt = null;
            job.RemoteConfirmedAt = null;
            job.RemoteItemId = string.Empty;
            job.RemoteETag = string.Empty;
            job.RemoteLocator = string.Empty;
            job.RemoteLength = null;
            job.RemoteModifiedAt = null;
            job.Attempts = 0;
            ClearFailure(job);
            job.NextAttemptAt = null;
            job.UpdatedAt = now;

            await _database.AddSyncJobAsync(job).ConfigureAwait(false);

            Publish(job);

            try
            {
                var stored = await _payloadStorage
                    .StoreAsync(job.PayloadPath, payload, cancellationToken)
                    .ConfigureAwait(false);
                job.PayloadLength = stored.PlaintextBytes;
                job.PayloadSha256 = await ComputePayloadSha256Async(job.PayloadPath, cancellationToken)
                    .ConfigureAwait(false);
                job.State = SyncJobState.StoredLocally;
                job.StoredAt = DateTimeOffset.UtcNow;
                job.IsProgressIndeterminate = false;
                job.UpdatedAt = job.StoredAt.Value;
                await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
                Publish(job);
                if (supersededJob is not null)
                {
                    await SupersedeJobAsync(supersededJob, cancellationToken).ConfigureAwait(false);
                }

                SignalWorker();
                return job;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var kind = ex is PayloadQuotaExceededException
                    ? SyncFailureKind.Quota
                    : SyncFailureKind.PayloadUnavailable;
                ApplyFailure(job, kind, TechnicalDetails(ex));
                job.State = SyncJobState.Failed;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
                Publish(job);
                throw;
            }
        }
        finally
        {
            if (transferGateAcquired)
            {
                _activeTransferGate.Release();
            }

            _enqueueGate.Release();
        }
    }

    public SyncJob QueueDelete(
        DriveRoute route,
        string relativePath,
        bool isDirectory) =>
        QueueDeleteAsync(route, relativePath, isDirectory).GetAwaiter().GetResult();

    public SyncJob PrepareDeleteIntent(
        DriveRoute route,
        string relativePath,
        bool isDirectory) =>
        PrepareDeleteIntentAsync(route, relativePath, isDirectory).GetAwaiter().GetResult();

    /// <summary>
    /// Persists and arms a delete intent without waiting for an active HTTP
    /// transfer. Non-WinFsp callers use this one-step admission path.
    /// </summary>
    public async Task<SyncJob> QueueDeleteAsync(
        DriveRoute route,
        string relativePath,
        bool isDirectory,
        CancellationToken cancellationToken = default) =>
        await PersistDeleteIntentAsync(
                route,
                relativePath,
                isDirectory,
                armForWorker: true,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// WinFsp SetDelete persists this non-armed intent before returning success.
    /// This method performs local SQLite and tombstone work only; the worker must
    /// not issue HTTP until CleanupDelete explicitly arms the row.
    /// </summary>
    public async Task<SyncJob> PrepareDeleteIntentAsync(
        DriveRoute route,
        string relativePath,
        bool isDirectory,
        CancellationToken cancellationToken = default) =>
        await PersistDeleteIntentAsync(
                route,
                relativePath,
                isDirectory,
                armForWorker: false,
                cancellationToken)
            .ConfigureAwait(false);

    public bool ArmDeleteIntent(Guid jobId) =>
        ArmDeleteIntentAsync(jobId).GetAwaiter().GetResult();

    public async Task<bool> ArmDeleteIntentAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfStopping();
        await WaitForStartupRecoveryIfStartedAsync(cancellationToken).ConfigureAwait(false);
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            ThrowIfResetSuspended();
            var job = await _database.GetSyncJobAsync(jobId).ConfigureAwait(false);
            if (job is null ||
                job.OperationKind != SyncOperationKind.Delete ||
                !job.IsActive)
            {
                return false;
            }

            if (!job.DeleteArmed)
            {
                job.DeleteArmed = true;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
                Publish(job);
            }

            if (job.RouteId is { } routeId)
            {
                _contentService.RegisterDeleteTombstone(
                    routeId,
                    job.RelativePath,
                    job.IsDirectory);
            }

            SignalWorker();
            return true;
        }
        finally
        {
            _enqueueGate.Release();
        }
    }

    public bool CancelDeleteIntent(Guid jobId) =>
        CancelDeleteIntentAsync(jobId).GetAwaiter().GetResult();

    public async Task<bool> CancelDeleteIntentAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfStopping();
        await WaitForStartupRecoveryIfStartedAsync(cancellationToken).ConfigureAwait(false);
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            ThrowIfResetSuspended();
            var job = await _database.GetSyncJobAsync(jobId).ConfigureAwait(false);
            if (job is null)
            {
                return true;
            }

            if (job.OperationKind != SyncOperationKind.Delete ||
                job.DeleteArmed ||
                !job.IsActive)
            {
                return false;
            }

            job.State = SyncJobState.Discarded;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.NextAttemptAt = null;
            job.WaitReason = SyncWaitReason.None;
            job.UpdatedAt = job.CompletedAt.Value;
            ClearFailure(job);
            await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
            if (job.RouteId is { } routeId)
            {
                _contentService.ClearDeleteTombstone(routeId, job.RelativePath);
            }

            Publish(job);
            return true;
        }
        finally
        {
            _enqueueGate.Release();
        }
    }

    private async Task<SyncJob> PersistDeleteIntentAsync(
        DriveRoute route,
        string relativePath,
        bool isDirectory,
        bool armForWorker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        ThrowIfStopping();
        ThrowIfRouteRemoved(route.Id);
        await WaitForStartupRecoveryIfStartedAsync(cancellationToken).ConfigureAwait(false);
        var normalizedPath = NormalizePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            throw new ArgumentException("The item path is required.", nameof(relativePath));
        }

        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            ThrowIfRouteRemoved(route.Id);
            ThrowIfResetSuspended();
            var existing = (await _database.GetActiveSyncJobsAsync(route.Id).ConfigureAwait(false))
                .Where(item =>
                    item.OperationKind == SyncOperationKind.Delete &&
                    string.Equals(
                        NormalizePath(item.RelativePath),
                        normalizedPath,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.UpdatedAt)
                .FirstOrDefault();
            if (existing is not null)
            {
                var directoryChanged = isDirectory && !existing.IsDirectory;
                var armedChanged = armForWorker && !existing.DeleteArmed;
                existing.IsDirectory |= isDirectory;
                existing.DeleteArmed |= armForWorker;
                if (existing.State == SyncJobState.Failed && existing.CanRetry)
                {
                    existing.State = SyncJobState.Waiting;
                    existing.NextAttemptAt = null;
                    existing.WaitReason = SyncWaitReason.None;
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                    ClearFailure(existing);
                    await _database.UpdateSyncJobAsync(existing).ConfigureAwait(false);
                    Publish(existing);
                }
                else if (directoryChanged || armedChanged)
                {
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                    await _database.UpdateSyncJobAsync(existing).ConfigureAwait(false);
                    Publish(existing);
                }

                if (existing.State is SyncJobState.Waiting or SyncJobState.Uploading)
                {
                    _contentService.RegisterDeleteTombstone(
                        route.Id,
                        normalizedPath,
                        existing.IsDirectory);
                    await ClearDeleteTombstoneIfNoLongerActiveAsync(existing)
                        .ConfigureAwait(false);
                }

                if (existing.DeleteArmed)
                {
                    SignalWorker();
                }

                return existing;
            }

            var now = DateTimeOffset.UtcNow;
            var job = new SyncJob
            {
                RouteId = route.Id,
                OperationKey = SyncJob.CreateOperationKey(route.Id, normalizedPath),
                OperationKind = SyncOperationKind.Delete,
                IsDirectory = isDirectory,
                DeleteArmed = armForWorker,
                FileName = Path.GetFileName(normalizedPath),
                RouteDisplayName = route.DisplayName,
                RelativePath = normalizedPath,
                PayloadPath = string.Empty,
                PayloadLength = null,
                PayloadSha256 = string.Empty,
                State = SyncJobState.Waiting,
                Progress = 0,
                BytesTransferred = 0,
                IsProgressIndeterminate = false,
                CreatedAt = now,
                UpdatedAt = now
            };
            ClearFailure(job);
            await _database.AddSyncJobAsync(job).ConfigureAwait(false);

            // The durable row exists before the optimistic Explorer hide. A
            // crash can therefore restore this tombstone from SQLite.
            _contentService.RegisterDeleteTombstone(route.Id, normalizedPath, isDirectory);
            await ClearDeleteTombstoneIfNoLongerActiveAsync(job).ConfigureAwait(false);
            Publish(job);
            if (job.DeleteArmed)
            {
                SignalWorker();
            }

            return job;
        }
        finally
        {
            _enqueueGate.Release();
        }
    }

    public Task RetryAsync(Guid jobId) => RetryNowAsync(jobId);

    public async Task RetryNowAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        ThrowIfStopping();
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var transferGateAcquired = false;
        try
        {
            ThrowIfStopping();
            ThrowIfResetSuspended();
            await _activeTransferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            transferGateAcquired = true;
            ThrowIfStopping();
            var job = await _database.GetSyncJobAsync(jobId).ConfigureAwait(false);
            if (job is null || !job.CanRetry)
            {
                return;
            }

            if (job.OperationKind == SyncOperationKind.Delete)
            {
                job.State = SyncJobState.Waiting;
                job.Progress = 0;
                job.BytesTransferred = 0;
                job.IsProgressIndeterminate = false;
                job.NextAttemptAt = null;
                job.WaitReason = SyncWaitReason.None;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                ClearFailure(job);
                await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
                if (job.RouteId is { } deleteRouteId)
                {
                    _contentService.RegisterDeleteTombstone(
                        deleteRouteId,
                        job.RelativePath,
                        job.IsDirectory);
                }

                Publish(job);
                SignalWorker();
                return;
            }

            // Attempts is deliberately preserved. "Try now" changes the schedule,
            // not the history that explains why the item needs attention.
            var repeatsRemoteVerification =
                job.State == SyncJobState.VerifyingRemote ||
                job.WaitReason == SyncWaitReason.RemoteVerification;
            job.State = repeatsRemoteVerification
                ? SyncJobState.VerifyingRemote
                : SyncJobState.Waiting;
            if (!repeatsRemoteVerification)
            {
                job.Progress = 0;
                job.BytesTransferred = 0;
                job.UploadMayHaveCommitted = false;
                job.BytesPerSecond = null;
                job.EstimatedCompletionAt = null;
            }

            job.NextAttemptAt = null;
            job.WaitReason = repeatsRemoteVerification
                ? SyncWaitReason.RemoteVerification
                : SyncWaitReason.None;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
            Publish(job);
            SignalWorker();
        }
        finally
        {
            if (transferGateAcquired)
            {
                _activeTransferGate.Release();
            }

            _enqueueGate.Release();
        }
    }

    public async Task<SyncConflictActionResult> DiscardLocalPayloadAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfStopping();
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var transferGateAcquired = false;
        try
        {
            ThrowIfStopping();
            ThrowIfResetSuspended();
            await _activeTransferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            transferGateAcquired = true;
            ThrowIfStopping();
            var job = await _database.GetSyncJobAsync(jobId).ConfigureAwait(false);
            if (job is null)
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.NotFound);
            }

            if (job.OperationKind != SyncOperationKind.Upload ||
                job.State is not (SyncJobState.Conflict or SyncJobState.Failed))
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.InvalidState, job);
            }

            // Persist the user's explicit discard before cleanup. A crash can leave
            // an orphan for maintenance, but can never turn a discard into 100%.
            job.State = SyncJobState.Discarded;
            job.Progress = 0;
            job.BytesTransferred = 0;
            job.UploadMayHaveCommitted = false;
            job.IsProgressIndeterminate = false;
            job.BytesPerSecond = null;
            job.EstimatedCompletionAt = null;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.RemoteConfirmedAt = null;
            job.RemoteItemId = string.Empty;
            job.RemoteETag = string.Empty;
            job.RemoteLocator = string.Empty;
            job.RemoteLength = null;
            job.RemoteModifiedAt = null;
            ClearFailure(job);
            job.NextAttemptAt = null;
            job.UpdatedAt = job.CompletedAt.Value;
            await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
            Publish(job);

            if (!string.IsNullOrWhiteSpace(job.PayloadPath) &&
                !await _payloadStorage.DeleteAsync(job.PayloadPath, cancellationToken).ConfigureAwait(false))
            {
                job.State = SyncJobState.Failed;
                job.CompletedAt = null;
                ApplyFailure(
                    job,
                    SyncFailureKind.PayloadUnavailable,
                    "The explicitly discarded payload could not be deleted.");
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
                Publish(job);
                return new SyncConflictActionResult(
                    SyncConflictActionStatus.Failed,
                    job,
                    Error: job.UserMessage);
            }

            return new SyncConflictActionResult(SyncConflictActionStatus.Discarded, job);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Discarding a local upload payload failed.", ex);
            return new SyncConflictActionResult(
                SyncConflictActionStatus.Failed,
                Error: FriendlyMessage(SyncFailureKind.PayloadUnavailable));
        }
        finally
        {
            if (transferGateAcquired)
            {
                _activeTransferGate.Release();
            }

            _enqueueGate.Release();
        }
    }

    /// <summary>
    /// Writes the explicitly requested plaintext copy directly to a new
    /// caller-selected destination. No plaintext staging file is created.
    /// </summary>
    public async Task<SyncConflictActionResult> ExportLocalPayloadAsync(
        Guid jobId,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfStopping();
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            ThrowIfResetSuspended();
        }
        catch
        {
            _enqueueGate.Release();
            throw;
        }

        var createdDestination = false;
        string? fullDestination = null;
        try
        {
            var job = await _database.GetSyncJobAsync(jobId).ConfigureAwait(false);
            if (job is null)
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.NotFound);
            }

            if (!job.CanExport)
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.InvalidState, job);
            }

            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.InvalidDestination, job);
            }

            fullDestination = Path.GetFullPath(destinationPath);
            var queueRoot = Path.GetFullPath(_payloadStorage.StorageDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (fullDestination.StartsWith(queueRoot, StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(fullDestination))
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.InvalidDestination, job);
            }

            if (!File.Exists(job.PayloadPath))
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.PayloadUnavailable, job);
            }

            if (File.Exists(fullDestination))
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.DestinationAlreadyExists, job);
            }

            var destinationDirectory = Path.GetDirectoryName(fullDestination);
            if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.InvalidDestination, job);
            }

            await _payloadStorage
                .MigrateLegacyPayloadAsync(job.PayloadPath, cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                fullDestination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            createdDestination = true;
            await _payloadStorage
                .DecryptToAsync(job.PayloadPath, output, cancellationToken)
                .ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            return new SyncConflictActionResult(
                SyncConflictActionStatus.Exported,
                job,
                fullDestination);
        }
        catch (OperationCanceledException)
        {
            if (createdDestination && fullDestination is not null)
            {
                TryDeleteFile(fullDestination);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (createdDestination && fullDestination is not null)
            {
                TryDeleteFile(fullDestination);
            }

            StartupDiagnostics.Write("Exporting a local upload payload failed.", ex);
            return new SyncConflictActionResult(
                SyncConflictActionStatus.Failed,
                Error: FriendlyMessage(SyncFailureKind.PayloadUnavailable));
        }
        finally
        {
            _enqueueGate.Release();
        }
    }

    public async Task<SyncConflictActionResult> ForceReplaceAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfStopping();
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var transferGateAcquired = false;
        try
        {
            ThrowIfStopping();
            ThrowIfResetSuspended();
            await _activeTransferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            transferGateAcquired = true;
            ThrowIfStopping();
            var job = await _database.GetSyncJobAsync(jobId).ConfigureAwait(false);
            if (job is null)
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.NotFound);
            }

            if (job.OperationKind != SyncOperationKind.Upload ||
                job.State is not (SyncJobState.Conflict or SyncJobState.Failed))
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.InvalidState, job);
            }

            if (job.FailureKind == SyncFailureKind.RouteUnavailable)
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.InvalidState, job);
            }

            if (job.UploadMayHaveCommitted && job.UploadStartedAt is null)
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.InvalidState, job);
            }

            if (string.IsNullOrWhiteSpace(job.PayloadPath) || !File.Exists(job.PayloadPath))
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.PayloadUnavailable, job);
            }

            job.ExpectedModifiedAt = null;
            job.State = SyncJobState.Waiting;
            job.Progress = 0;
            job.BytesTransferred = 0;
            job.UploadMayHaveCommitted = false;
            job.BytesPerSecond = null;
            job.EstimatedCompletionAt = null;
            job.NextAttemptAt = null;
            job.WaitReason = SyncWaitReason.None;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
            Publish(job);
            SignalWorker();
            return new SyncConflictActionResult(SyncConflictActionStatus.QueuedForReplace, job);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Queueing an explicit conflict replacement failed.", ex);
            return new SyncConflictActionResult(
                SyncConflictActionStatus.Failed,
                Error: FriendlyMessage(SyncFailureKind.Unknown));
        }
        finally
        {
            if (transferGateAcquired)
            {
                _activeTransferGate.Release();
            }

            _enqueueGate.Release();
        }
    }

    public Task<IReadOnlyList<SyncJob>> GetActiveJobsAsync(Guid? routeId = null) =>
        _database.GetActiveSyncJobsAsync(routeId);

    public async Task<int> GetActiveJobCountAsync(Guid? routeId = null) =>
        (await GetActiveJobsAsync(routeId).ConfigureAwait(false)).Count;

    public async Task<int> ClearCompletedAsync()
    {
        ThrowIfStopping();
        await _enqueueGate.WaitAsync().ConfigureAwait(false);
        var transferGateAcquired = false;
        try
        {
            ThrowIfStopping();
            ThrowIfResetSuspended();
            await _activeTransferGate.WaitAsync().ConfigureAwait(false);
            transferGateAcquired = true;
            ThrowIfStopping();
            var completed = (await _database.GetSyncJobsAsync().ConfigureAwait(false))
                .Where(job => job.State == SyncJobState.Completed)
                .ToArray();
            var removed = 0;
            foreach (var job in completed)
            {
                if (!string.IsNullOrWhiteSpace(job.PayloadPath) && File.Exists(job.PayloadPath))
                {
                    await _payloadStorage.DeleteAsync(job.PayloadPath).ConfigureAwait(false);
                }

                if (await _database
                    .DeleteSyncJobAsync(job.Id, SyncJobState.Completed)
                    .ConfigureAwait(false))
                {
                    removed++;
                }
            }

            return removed;
        }
        finally
        {
            if (transferGateAcquired)
            {
                _activeTransferGate.Release();
            }

            _enqueueGate.Release();
        }
    }

    public async Task<int> MarkRouteRemovedAsync(Guid routeId)
    {
        ThrowIfStopping();
        await BeginRouteRemovalAsync(routeId).ConfigureAwait(false);
        await _enqueueGate.WaitAsync().ConfigureAwait(false);
        var transferGateAcquired = false;
        try
        {
            ThrowIfStopping();
            ThrowIfResetSuspended();
            await _activeTransferGate.WaitAsync().ConfigureAwait(false);
            transferGateAcquired = true;
            ThrowIfStopping();
            var jobs = await _database.GetActiveSyncJobsAsync(routeId).ConfigureAwait(false);
            var changed = 0;
            foreach (var job in jobs)
            {
                if (job.State is SyncJobState.Completed or SyncJobState.Discarded)
                {
                    continue;
                }

                var requiresRemoteVerification =
                    job.OperationKind == SyncOperationKind.Upload &&
                    (job.State is (SyncJobState.Uploading or SyncJobState.VerifyingRemote) ||
                     job.WaitReason == SyncWaitReason.RemoteVerification ||
                     job.BytesTransferred > 0);
                job.State = SyncJobState.Failed;
                job.Progress = Math.Clamp(job.Progress, 0, 99);
                job.BytesPerSecond = null;
                job.EstimatedCompletionAt = null;
                job.NextAttemptAt = null;
                ApplyFailure(job, SyncFailureKind.RouteUnavailable);
                job.WaitReason = requiresRemoteVerification
                    ? SyncWaitReason.RemoteVerification
                    : SyncWaitReason.None;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
                if (job.OperationKind == SyncOperationKind.Delete)
                {
                    _contentService.ClearDeleteTombstone(routeId, job.RelativePath);
                }

                Publish(job);
                changed++;
            }

            return changed;
        }
        finally
        {
            if (transferGateAcquired)
            {
                _activeTransferGate.Release();
            }

            _enqueueGate.Release();
        }
    }

    /// <summary>
    /// Explicitly reopens a route only after its database record has been
    /// deliberately recreated. This is intentionally separate from RetryNow so
    /// a retry racing with removal can never undo the removal admission barrier.
    /// </summary>
    public async Task<int> RestoreRouteAdmissionAsync(
        Guid routeId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfStopping();
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var transferGateAcquired = false;
        var lifecycleGateAcquired = false;
        try
        {
            ThrowIfStopping();
            await _activeTransferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            transferGateAcquired = true;
            ThrowIfStopping();

            if (!(await _database.GetRoutesAsync().ConfigureAwait(false))
                .Any(route => route.Id == routeId))
            {
                return 0;
            }

            await _routeLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            lifecycleGateAcquired = true;
            RestoreRouteAdmission(routeId);

            var jobs = await _database.GetActiveSyncJobsAsync(routeId).ConfigureAwait(false);
            var changed = 0;
            foreach (var job in jobs.Where(job =>
                         job.State == SyncJobState.Failed &&
                         job.FailureKind == SyncFailureKind.RouteUnavailable))
            {
                job.FailureKind = SyncFailureKind.Unknown;
                job.UserMessage = job.WaitReason == SyncWaitReason.RemoteVerification
                    ? AppText.Get("SyncFailureRemoteVerification")
                    : AppText.Get("SyncFailureUnknown");
                job.LastError = job.UserMessage;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
                Publish(job);
                changed++;
            }

            return changed;
        }
        finally
        {
            if (lifecycleGateAcquired)
            {
                _routeLifecycleGate.Release();
            }

            if (transferGateAcquired)
            {
                _activeTransferGate.Release();
            }

            _enqueueGate.Release();
        }
    }

    public void SignalConnectivityRestored()
    {
        if (Volatile.Read(ref _stopRequested) == 0 &&
            Volatile.Read(ref _disposed) == 0)
        {
            _ = ResumeForConditionAsync(SyncFailureKind.Network);
        }
    }

    public void SignalSessionRestored()
    {
        if (Volatile.Read(ref _stopRequested) == 0 &&
            Volatile.Read(ref _disposed) == 0)
        {
            _ = ResumeForConditionAsync(SyncFailureKind.Session);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
        {
            _cancellation.Cancel();
            SignalWorker();
        }

        // Drain an enqueue that may currently be between the durable database
        // reservation and encrypted payload commit, then wait for any active
        // transfer/reset operation to leave its critical section. New enqueues
        // are rejected by the stop checks above.
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _activeTransferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _routeLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                _routeLifecycleGate.Release();
            }
            finally
            {
                _activeTransferGate.Release();
            }
        }
        finally
        {
            _enqueueGate.Release();
        }

        Task? worker;
        lock (_workerGate)
        {
            worker = _worker;
        }

        if (worker is not null)
        {
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IAsyncDisposable> SuspendForResetAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfStopping();
        if (Interlocked.CompareExchange(ref _resetSuspended, 1, 0) != 0)
        {
            throw new InvalidOperationException("The upload queue is already paused for local data deletion.");
        }

        _resetCancellation.Cancel();
        SignalWorker();
        var enqueueGateAcquired = false;
        try
        {
            await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enqueueGateAcquired = true;
            ThrowIfStopping();
            await _activeTransferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ResetSuspension(this);
        }
        catch
        {
            ResumeAfterReset();
            if (enqueueGateAcquired)
            {
                _enqueueGate.Release();
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        NetworkInformation.NetworkStatusChanged -= NetworkInformation_NetworkStatusChanged;
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Shutdown is best effort; durable state remains recoverable.
        }

        _signal.Dispose();
        _enqueueGate.Dispose();
        _activeTransferGate.Dispose();
        _routeLifecycleGate.Dispose();
        _resetCancellation.Dispose();
        _cancellation.Dispose();
        CancellationTokenSource[] routeCancellationSources;
        lock (_routeAdmissionGate)
        {
            routeCancellationSources = _routeCancellationSources.Values.ToArray();
            _routeCancellationSources.Clear();
            _removedRouteIds.Clear();
        }

        foreach (var routeCancellationSource in routeCancellationSources)
        {
            routeCancellationSource.Dispose();
        }
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            try
            {
                await RecoverInterruptedJobsUnderResetBarrierAsync().ConfigureAwait(false);
            }
            finally
            {
                _startupRecoveryCompleted.TrySetResult();
            }

            await CleanupPayloadStorageUnderResetBarrierAsync().ConfigureAwait(false);

            while (!_cancellation.IsCancellationRequested)
            {
                var processed = false;
                try
                {
                    var jobs = await GetPendingSyncJobsUnderResetBarrierAsync().ConfigureAwait(false);
                    foreach (var job in jobs)
                    {
                        if (Volatile.Read(ref _resetSuspended) != 0)
                        {
                            break;
                        }

                        if (job.NextAttemptAt > DateTimeOffset.UtcNow)
                        {
                            continue;
                        }

                        processed = true;
                        await ProcessJobAsync(job).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.Write("Upload queue iteration failed.", ex);
                }

                if (processed)
                {
                    continue;
                }

                try
                {
                    await _signal.WaitAsync(TimeSpan.FromSeconds(10), _cancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task WaitForStartupRecoveryIfStartedAsync(CancellationToken cancellationToken)
    {
        Task? recovery = null;
        lock (_workerGate)
        {
            if (_worker is not null)
            {
                recovery = _startupRecoveryCompleted.Task;
            }
        }

        if (recovery is not null)
        {
            await recovery.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessJobAsync(SyncJob job)
    {
        await _activeTransferGate.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _resetSuspended) != 0)
            {
                return;
            }

            // The pending list is only a scheduling hint. Reload under the active
            // gate so a route removal or manual action cannot leave a stale
            // Waiting snapshot eligible for upload.
            var current = await _database.GetSyncJobAsync(job.Id).ConfigureAwait(false);
            if (current is null || !IsWorkerEligible(current.State))
            {
                return;
            }

            if (current.OperationKind == SyncOperationKind.Delete &&
                !current.DeleteArmed)
            {
                return;
            }

            if (current.RouteId is { } currentRouteId &&
                IsRouteRemoved(currentRouteId))
            {
                return;
            }

            if (current.OperationKind == SyncOperationKind.Upload &&
                current.RouteId is { } uploadRouteId &&
                await FindDeleteBarrierAsync(uploadRouteId, current.RelativePath)
                    .ConfigureAwait(false) is { } deleteBarrier)
            {
                if (RequiresDeleteCommitBarrier(current))
                {
                    await DeferUploadBehindDeleteAsync(current, deleteBarrier).ConfigureAwait(false);
                }
                else
                {
                    await SupersedeJobAsync(current, _cancellation.Token).ConfigureAwait(false);
                }

                return;
            }

            var routeCancellationToken = current.RouteId is { } routeId
                ? GetRouteCancellationToken(routeId)
                : CancellationToken.None;
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _cancellation.Token,
                _resetCancellation.Token,
                routeCancellationToken);
            await ProcessJobCoreAsync(current, operationCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _activeTransferGate.Release();
        }
    }

    private async Task ProcessJobCoreAsync(SyncJob job, CancellationToken cancellationToken)
    {
        if (job.State == SyncJobState.PersistingLocal)
        {
            await RecoverPersistingJobAsync(job, cancellationToken).ConfigureAwait(false);
            if (job.State == SyncJobState.Failed)
            {
                return;
            }
        }

        if (job.RouteId is null || string.IsNullOrWhiteSpace(job.RelativePath))
        {
            await MarkTerminalFailureAsync(job, SyncFailureKind.RouteUnavailable).ConfigureAwait(false);
            return;
        }

        var route = (await _database.GetRoutesAsync().ConfigureAwait(false))
            .FirstOrDefault(item => item.Id == job.RouteId.Value);
        if (route is null)
        {
            await MarkTerminalFailureAsync(job, SyncFailureKind.RouteUnavailable).ConfigureAwait(false);
            return;
        }

        if (job.OperationKind == SyncOperationKind.Delete)
        {
            await ProcessDeleteJobCoreAsync(route, job, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(job.PayloadPath))
        {
            await MarkTerminalFailureAsync(job, SyncFailureKind.PayloadUnavailable).ConfigureAwait(false);
            return;
        }

        if (job.PayloadLength is null || string.IsNullOrWhiteSpace(job.PayloadSha256))
        {
            await PopulatePayloadMetadataAsync(job, cancellationToken).ConfigureAwait(false);
            await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
        }

        if (job.State is SyncJobState.Uploading or SyncJobState.VerifyingRemote)
        {
            if (job.State == SyncJobState.Uploading)
            {
                job.State = SyncJobState.VerifyingRemote;
                job.WaitReason = SyncWaitReason.RemoteVerification;
                job.NextAttemptAt = null;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
                Publish(job);
            }

            var reconciled = await TryReconcileRemoteAsync(route, job, cancellationToken)
                .ConfigureAwait(false);
            if (reconciled)
            {
                await CompleteConfirmedJobAsync(job, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await MarkRemoteVerificationFailureAsync(job, job.TechnicalDetails)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (job.State == SyncJobState.StoredLocally)
        {
            job.State = SyncJobState.Waiting;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
            Publish(job);
        }

        job.State = SyncJobState.Uploading;
        job.Progress = 0;
        job.BytesTransferred = 0;
        job.UploadMayHaveCommitted = false;
        job.IsProgressIndeterminate = job.PayloadLength is null;
        job.BytesPerSecond = null;
        job.EstimatedCompletionAt = null;
        job.UploadStartedAt = DateTimeOffset.UtcNow;
        job.NextAttemptAt = null;
        job.WaitReason = SyncWaitReason.None;
        job.UpdatedAt = job.UploadStartedAt.Value;
        await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
        Publish(job);

        UploadAttemptResult result;
        try
        {
            await _payloadStorage
                .MigrateLegacyPayloadAsync(job.PayloadPath, cancellationToken)
                .ConfigureAwait(false);
            await using var content = await _payloadStorage
                .OpenReadAsync(job.PayloadPath, cancellationToken)
                .ConfigureAwait(false);
            var progress = new DurableUploadProgress(this, job);
            result = await _contentService
                .TryUploadFileAsync(
                    route,
                    job.RelativePath,
                    content,
                    job.ExpectedModifiedAt,
                    cancellationToken,
                    progress)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation before any durably observed byte is safe to replay.
            // Once a byte may have reached the server, only fresh remote
            // verification may advance the job.
            await PreserveInterruptedUploadAsync(job).ConfigureAwait(false);
            return;
        }
        catch (InvalidEncryptedPayloadException ex)
        {
            await MarkTerminalFailureAsync(
                    job,
                    SyncFailureKind.Integrity,
                    TechnicalDetails(ex))
                .ConfigureAwait(false);
            return;
        }
        catch (CryptographicException ex)
        {
            await MarkTerminalFailureAsync(
                    job,
                    SyncFailureKind.Integrity,
                    TechnicalDetails(ex))
                .ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            var kind = ClassifyFailure(ex.Message);
            result = new UploadAttemptResult(
                UploadAttemptState.RetryableFailure,
                Receipt: null,
                FailureKind: kind,
                TechnicalDetails: TechnicalDetails(ex));
        }

        switch (result.State)
        {
            case UploadAttemptState.Succeeded:
                ApplyReceipt(job, result.Receipt);
                if (IsValidReceipt(job, result.Receipt))
                {
                    job.RemoteConfirmedAt = DateTimeOffset.UtcNow;
                    await CompleteConfirmedJobAsync(job, cancellationToken).ConfigureAwait(false);
                    break;
                }

                await VerifyAmbiguousCommitAsync(
                        route,
                        job,
                        result.TechnicalDetails ?? result.Error,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;

            case UploadAttemptState.Conflict:
                job.State = SyncJobState.Conflict;
                job.Progress = 0;
                job.BytesTransferred = 0;
                job.BytesPerSecond = null;
                job.EstimatedCompletionAt = null;
                job.NextAttemptAt = null;
                ApplyFailure(
                    job,
                    SyncFailureKind.Conflict,
                    result.TechnicalDetails ?? result.Error);
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
                Publish(job);
                break;

            default:
                var failureKind = result.FailureKind is SyncFailureKind.None or SyncFailureKind.Unknown
                    ? ClassifyFailure(result.Error)
                    : result.FailureKind;
                if (result.IsCommitAmbiguous &&
                    job.UploadMayHaveCommitted)
                {
                    ApplyFailure(
                        job,
                        failureKind,
                        result.TechnicalDetails ?? result.Error);
                    await VerifyAmbiguousCommitAsync(
                            route,
                            job,
                            result.TechnicalDetails ?? result.Error,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }

                await MarkRetryableFailureAsync(
                        job,
                        failureKind,
                        WaitReasonFor(failureKind),
                        result.TechnicalDetails ?? result.Error)
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task ProcessDeleteJobCoreAsync(
        DriveRoute route,
        SyncJob job,
        CancellationToken cancellationToken)
    {
        // The active transfer gate held by ProcessJobAsync is the remote
        // ordering barrier. Any upload that committed before this lease is
        // removed by DELETE; no covered upload can start until this method exits.
        var barrier = await PrepareDeleteCommitBarrierAsync(route, job, cancellationToken)
            .ConfigureAwait(false);

        job.State = SyncJobState.Uploading;
        job.Progress = 0;
        job.BytesTransferred = 0;
        job.IsProgressIndeterminate = false;
        job.NextAttemptAt = null;
        job.WaitReason = SyncWaitReason.None;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
        Publish(job);

        RemoteDeleteAttemptResult result;
        try
        {
            result = await _contentService
                .TryDeleteItemAsync(
                    route,
                    job.RelativePath,
                    job.IsDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job.State = SyncJobState.Waiting;
            job.NextAttemptAt = null;
            job.WaitReason = SyncWaitReason.None;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
            Publish(job);
            return;
        }
        catch (Exception ex)
        {
            result = new RemoteDeleteAttemptResult(
                RemoteDeleteAttemptState.RetryableFailure,
                ClassifyFailure(ex.Message),
                TechnicalDetails: TechnicalDetails(ex));
        }

        switch (result.State)
        {
            case RemoteDeleteAttemptState.Succeeded:
                if (!barrier.IsResolved)
                {
                    await DeferDeleteForCommitBarrierAsync(job, barrier.TechnicalDetails)
                        .ConfigureAwait(false);
                    break;
                }

                job.State = SyncJobState.Completed;
                job.Progress = 100;
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.DeleteBarrierObservedAt = null;
                job.NextAttemptAt = null;
                ClearFailure(job);
                job.UpdatedAt = job.CompletedAt.Value;
                await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
                _contentService.ClearDeleteTombstone(route.Id, job.RelativePath);
                Publish(job);
                break;

            case RemoteDeleteAttemptState.TerminalFailure:
                await MarkTerminalFailureAsync(
                        job,
                        result.FailureKind is SyncFailureKind.None
                            ? SyncFailureKind.Unknown
                            : result.FailureKind,
                        result.TechnicalDetails ?? result.Error)
                    .ConfigureAwait(false);
                break;

            default:
                var failureKind = result.FailureKind is SyncFailureKind.None
                    ? SyncFailureKind.Unknown
                    : result.FailureKind;
                await MarkRetryableFailureAsync(
                        job,
                        failureKind,
                        WaitReasonFor(failureKind),
                        result.TechnicalDetails ?? result.Error)
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task RecoverInterruptedJobsUnderResetBarrierAsync()
    {
        await _activeTransferGate.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _resetSuspended) != 0)
            {
                return;
            }

            var jobs = await _database.GetSyncJobsAsync().ConfigureAwait(false);
            foreach (var job in jobs)
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                if (job.OperationKind == SyncOperationKind.Delete)
                {
                    if (!job.DeleteArmed && job.IsActive)
                    {
                        // SetDelete already returned success only after this row
                        // became durable. If the process stopped before/during
                        // CleanupDelete, resume that accepted intent. An explicit
                        // SetDelete(false) is persisted as Discarded instead.
                        job.DeleteArmed = true;
                        job.UpdatedAt = DateTimeOffset.UtcNow;
                        await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
                        if (job.RouteId is { } stagedRouteId)
                        {
                            _contentService.RegisterDeleteTombstone(
                                stagedRouteId,
                                job.RelativePath,
                                job.IsDirectory);
                        }

                        Publish(job);
                    }

                    if (job.State == SyncJobState.Uploading)
                    {
                        // DELETE is idempotent. An interrupted request resumes
                        // as a delete attempt; it never enters upload verification.
                        job.State = SyncJobState.Waiting;
                        job.NextAttemptAt = null;
                        job.WaitReason = SyncWaitReason.None;
                        job.UpdatedAt = DateTimeOffset.UtcNow;
                        await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
                        Publish(job);
                    }

                    if (job.State == SyncJobState.Waiting && job.RouteId is { } deleteRouteId)
                    {
                        _contentService.RegisterDeleteTombstone(
                            deleteRouteId,
                            job.RelativePath,
                            job.IsDirectory);
                    }

                    continue;
                }

                switch (job.State)
                {
                    case SyncJobState.PersistingLocal:
                        await RecoverPersistingJobAsync(job, _cancellation.Token).ConfigureAwait(false);
                        break;
                    case SyncJobState.Uploading:
                        await PreserveInterruptedUploadAsync(job).ConfigureAwait(false);
                        break;
                }
            }

            var refreshed = await _database.GetSyncJobsAsync().ConfigureAwait(false);
            foreach (var duplicates in refreshed
                         .Where(job => job.IsActive && !string.IsNullOrWhiteSpace(job.OperationKey))
                         .GroupBy(
                             job => $"{(int)job.OperationKind}:{job.OperationKey}",
                             StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
            {
                var ordered = duplicates
                    .OrderByDescending(job =>
                        !string.IsNullOrWhiteSpace(job.PayloadPath) && File.Exists(job.PayloadPath))
                    .ThenByDescending(job => job.CreatedAt)
                    .ThenByDescending(job => job.UpdatedAt)
                    .ToArray();
                foreach (var duplicate in ordered.Skip(1))
                {
                    await SupersedeJobAsync(duplicate, _cancellation.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _activeTransferGate.Release();
        }
    }

    private async Task RecoverPersistingJobAsync(SyncJob job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.PayloadPath) || !File.Exists(job.PayloadPath))
        {
            await MarkTerminalFailureAsync(job, SyncFailureKind.PayloadUnavailable).ConfigureAwait(false);
            return;
        }

        try
        {
            await PopulatePayloadMetadataAsync(job, cancellationToken).ConfigureAwait(false);
            job.State = SyncJobState.StoredLocally;
            job.StoredAt ??= DateTimeOffset.UtcNow;
            job.Progress = 0;
            job.BytesTransferred = 0;
            job.IsProgressIndeterminate = false;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            ClearFailure(job);
            await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
            Publish(job);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await MarkTerminalFailureAsync(
                    job,
                    ex is InvalidEncryptedPayloadException or CryptographicException
                        ? SyncFailureKind.Integrity
                        : SyncFailureKind.PayloadUnavailable,
                    TechnicalDetails(ex))
                .ConfigureAwait(false);
        }
    }

    private async Task PreserveInterruptedUploadAsync(
        SyncJob job,
        bool forceRemoteVerification = false)
    {
        if (forceRemoteVerification)
        {
            job.UploadMayHaveCommitted = true;
        }

        var requiresRemoteVerification =
            forceRemoteVerification ||
            job.UploadMayHaveCommitted;
        if (requiresRemoteVerification)
        {
            job.State = SyncJobState.VerifyingRemote;
            job.Progress = Math.Clamp(job.Progress, 0, 99);
            job.WaitReason = SyncWaitReason.RemoteVerification;
        }
        else
        {
            job.State = SyncJobState.StoredLocally;
            job.Progress = 0;
            job.BytesTransferred = 0;
            job.UploadMayHaveCommitted = false;
            job.IsProgressIndeterminate = job.PayloadLength is null;
            job.UploadStartedAt = null;
            ClearFailure(job);
        }

        job.BytesPerSecond = null;
        job.EstimatedCompletionAt = null;
        job.NextAttemptAt = null;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
        Publish(job);
    }

    private async Task SupersedeJobAsync(SyncJob job, CancellationToken cancellationToken)
    {
        job.State = SyncJobState.Discarded;
        job.Progress = 0;
        job.BytesTransferred = 0;
        job.UploadMayHaveCommitted = false;
        job.IsProgressIndeterminate = false;
        job.BytesPerSecond = null;
        job.EstimatedCompletionAt = null;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.RemoteConfirmedAt = null;
        job.RemoteItemId = string.Empty;
        job.RemoteETag = string.Empty;
        job.RemoteLocator = string.Empty;
        job.RemoteLength = null;
        job.RemoteModifiedAt = null;
        job.NextAttemptAt = null;
        ClearFailure(job);
        job.UpdatedAt = job.CompletedAt.Value;
        await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
        Publish(job);

        try
        {
            if (!string.IsNullOrWhiteSpace(job.PayloadPath))
            {
                await _payloadStorage.DeleteAsync(job.PayloadPath, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The terminal state is durable; storage maintenance can finish cleanup.
        }
        catch (Exception ex)
        {
            // The replacement payload is already durable. Failure to clean the
            // superseded ciphertext must not turn that new upload into a failure;
            // startup orphan maintenance can retry this deletion safely.
            StartupDiagnostics.Write("Superseded upload payload cleanup failed.", ex);
        }
    }

    private async Task<DeleteCommitBarrierResult> PrepareDeleteCommitBarrierAsync(
        DriveRoute route,
        SyncJob deleteJob,
        CancellationToken cancellationToken)
    {
        if (deleteJob.RouteId is not { } routeId)
        {
            return new DeleteCommitBarrierResult(
                IsResolved: false,
                "The delete job has no route id.");
        }

        var active = await _database.GetActiveSyncJobsAsync(routeId).ConfigureAwait(false);
        var coveredUploads = active
            .Where(item =>
                item.Id != deleteJob.Id &&
                item.OperationKind == SyncOperationKind.Upload &&
                IsPathCoveredByDelete(deleteJob, item.RelativePath))
            .OrderBy(item => item.CreatedAt)
            .ToArray();
        if (coveredUploads.Length == 0)
        {
            deleteJob.DeleteBarrierObservedAt = null;
            return new DeleteCommitBarrierResult(IsResolved: true);
        }

        var now = DateTimeOffset.UtcNow;
        var unresolved = false;
        var absenceContinuityInterrupted = false;
        var absentUploads = new List<SyncJob>();
        var technicalDetails = new List<string>();
        foreach (var upload in coveredUploads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RequiresDeleteCommitBarrier(upload))
            {
                await SupersedeJobAsync(upload, cancellationToken).ConfigureAwait(false);
                continue;
            }

            RemoteUploadVerificationResult verification;
            try
            {
                verification = await _contentService
                    .VerifyRemoteUploadAsync(route, upload.RelativePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                verification = new RemoteUploadVerificationResult(
                    RemoteUploadVerificationState.Unavailable,
                    FailureKind: ClassifyFailure(ex.Message),
                    TechnicalDetails: TechnicalDetails(ex));
            }

            switch (verification.State)
            {
                case RemoteUploadVerificationState.Confirmed:
                    // The uncertain PUT has materialized. With the transfer gate
                    // still held, DELETE can now be ordered strictly after it.
                    absenceContinuityInterrupted = true;
                    await SupersedeJobAsync(upload, cancellationToken).ConfigureAwait(false);
                    break;

                case RemoteUploadVerificationState.NotFound:
                    absentUploads.Add(upload);
                    break;

                default:
                    absenceContinuityInterrupted = true;
                    unresolved = true;
                    technicalDetails.Add(
                        verification.TechnicalDetails ??
                        $"Remote upload {upload.Id} could not be verified before delete.");
                    break;
            }
        }

        if (absenceContinuityInterrupted)
        {
            // The timestamp represents uninterrupted collective absence for
            // every ambiguous upload covered by this delete. A confirmed item
            // or an unavailable verification invalidates the prior interval.
            deleteJob.DeleteBarrierObservedAt = null;
        }

        if (absentUploads.Count > 0)
        {
            if (!absenceContinuityInterrupted &&
                deleteJob.DeleteBarrierObservedAt is { } observedAt &&
                now - observedAt >= DeleteCommitQuiescenceWindow)
            {
                foreach (var absentUpload in absentUploads)
                {
                    // Every unresolved upload has remained absent throughout
                    // the same quiet window, so none may be replayed.
                    await SupersedeJobAsync(absentUpload, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                unresolved = true;
                foreach (var absentUpload in absentUploads)
                {
                    technicalDetails.Add(
                        $"Remote upload {absentUpload.Id} is still absent inside the delete commit-quiescence window.");
                }
            }
        }

        if (!unresolved)
        {
            deleteJob.DeleteBarrierObservedAt = null;
            return new DeleteCommitBarrierResult(IsResolved: true);
        }

        if (!absenceContinuityInterrupted &&
            absentUploads.Count > 0 &&
            deleteJob.DeleteBarrierObservedAt is null)
        {
            deleteJob.DeleteBarrierObservedAt = now;
        }

        deleteJob.WaitReason = SyncWaitReason.RemoteVerification;
        deleteJob.UpdatedAt = now;
        await _database.UpdateSyncJobAsync(deleteJob).ConfigureAwait(false);
        Publish(deleteJob);
        return new DeleteCommitBarrierResult(
            IsResolved: false,
            NormalizeTechnicalDetails(string.Join(" ", technicalDetails)));
    }

    private async Task DeferDeleteForCommitBarrierAsync(
        SyncJob job,
        string? technicalDetails)
    {
        job.State = SyncJobState.Waiting;
        job.Progress = 0;
        job.BytesTransferred = 0;
        job.IsProgressIndeterminate = false;
        job.NextAttemptAt = DateTimeOffset.UtcNow.Add(DeleteBarrierRetryDelay);
        job.FailureKind = SyncFailureKind.None;
        job.WaitReason = SyncWaitReason.RemoteVerification;
        job.UserMessage = string.Empty;
        job.LastError = string.Empty;
        job.TechnicalDetails = NormalizeTechnicalDetails(technicalDetails);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
        Publish(job);
    }

    private async Task DeferUploadBehindDeleteAsync(SyncJob upload, SyncJob deleteBarrier)
    {
        if (deleteBarrier.State == SyncJobState.Failed)
        {
            upload.State = SyncJobState.Failed;
            upload.NextAttemptAt = null;
            upload.FailureKind = upload.FailureKind == SyncFailureKind.None
                ? SyncFailureKind.Unknown
                : upload.FailureKind;
            upload.UserMessage = AppText.Get("SyncFailureRemoteVerification");
            upload.LastError = upload.UserMessage;
        }
        else
        {
            var earliestRetry = DateTimeOffset.UtcNow.Add(DeleteBarrierRetryDelay);
            upload.NextAttemptAt = deleteBarrier.NextAttemptAt is { } barrierRetry &&
                                   barrierRetry > earliestRetry
                ? barrierRetry
                : earliestRetry;
        }

        upload.WaitReason = SyncWaitReason.RemoteVerification;
        upload.UpdatedAt = DateTimeOffset.UtcNow;
        await _database.UpdateSyncJobAsync(upload).ConfigureAwait(false);
        Publish(upload);
    }

    private async Task<SyncJob?> FindDeleteBarrierAsync(Guid routeId, string relativePath)
    {
        var active = await _database.GetActiveSyncJobsAsync(routeId).ConfigureAwait(false);
        return active
            .Where(item =>
                item.OperationKind == SyncOperationKind.Delete &&
                IsPathCoveredByDelete(item, relativePath))
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
    }

    private async Task ThrowIfDeleteBarrierAsync(Guid routeId, string relativePath)
    {
        if (await FindDeleteBarrierAsync(routeId, relativePath).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException(
                "This SharePoint item is waiting to be deleted and cannot accept a new upload yet.");
        }
    }

    private async Task ClearDeleteTombstoneIfNoLongerActiveAsync(SyncJob deleteJob)
    {
        if (deleteJob.RouteId is not { } routeId)
        {
            return;
        }

        var current = await _database.GetSyncJobAsync(deleteJob.Id).ConfigureAwait(false);
        if (current is null ||
            current.OperationKind != SyncOperationKind.Delete ||
            current.State is not (SyncJobState.Waiting or SyncJobState.Uploading))
        {
            _contentService.ClearDeleteTombstone(routeId, deleteJob.RelativePath);
        }
    }

    private static bool IsPathCoveredByDelete(SyncJob deleteJob, string candidatePath)
    {
        var deletedPath = NormalizePath(deleteJob.RelativePath);
        var normalizedCandidate = NormalizePath(candidatePath);
        return string.Equals(deletedPath, normalizedCandidate, StringComparison.OrdinalIgnoreCase) ||
               deleteJob.IsDirectory &&
               normalizedCandidate.StartsWith(deletedPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresDeleteCommitBarrier(SyncJob upload) =>
        upload.OperationKind == SyncOperationKind.Upload &&
        (upload.UploadMayHaveCommitted ||
         upload.State is SyncJobState.Uploading or SyncJobState.VerifyingRemote ||
         upload.WaitReason == SyncWaitReason.RemoteVerification ||
         upload.BytesTransferred > 0);

    private async Task PopulatePayloadMetadataAsync(SyncJob job, CancellationToken cancellationToken)
    {
        await _payloadStorage
            .MigrateLegacyPayloadAsync(job.PayloadPath, cancellationToken)
            .ConfigureAwait(false);
        await using var stream = await _payloadStorage
            .OpenReadAsync(job.PayloadPath, cancellationToken)
            .ConfigureAwait(false);
        job.PayloadLength = stream.CanSeek ? stream.Length : null;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        job.PayloadSha256 = Convert.ToHexString(hash);
    }

    private async Task<bool> TryReconcileRemoteAsync(
        DriveRoute route,
        SyncJob job,
        CancellationToken cancellationToken)
    {
        if (job.UploadStartedAt is null)
        {
            job.FailureKind = SyncFailureKind.Unknown;
            job.UserMessage = AppText.Get("SyncFailureRemoteVerification");
            job.LastError = job.UserMessage;
            job.TechnicalDetails = NormalizeTechnicalDetails(
                "Remote confirmation was refused because the durable upload start timestamp is missing.");
            return false;
        }

        try
        {
            var verification = await _contentService
                .VerifyRemoteUploadAsync(route, job.RelativePath, cancellationToken)
                .ConfigureAwait(false);
            if (verification.State != RemoteUploadVerificationState.Confirmed ||
                !IsValidReceipt(job, verification.Receipt) ||
                verification.Receipt?.ModifiedAt is not { } remoteModifiedAt)
            {
                job.FailureKind = verification.FailureKind;
                job.UserMessage = verification.UserMessage ?? string.Empty;
                job.LastError = job.UserMessage;
                job.TechnicalDetails = NormalizeTechnicalDetails(verification.TechnicalDetails);
                return false;
            }

            if (job.UploadStartedAt is { } startedAt &&
                remoteModifiedAt < startedAt)
            {
                return false;
            }

            if (job.ExpectedModifiedAt is { } expectedModifiedAt &&
                remoteModifiedAt <= expectedModifiedAt)
            {
                return false;
            }

            ApplyReceipt(job, verification.Receipt);
            job.RemoteConfirmedAt = DateTimeOffset.UtcNow;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            job.TechnicalDetails = TechnicalDetails(ex);
            return false;
        }
    }

    private async Task VerifyAmbiguousCommitAsync(
        DriveRoute route,
        SyncJob job,
        string? technicalDetails,
        CancellationToken cancellationToken)
    {
        job.State = SyncJobState.VerifyingRemote;
        job.Progress = Math.Clamp(job.Progress, 0, 99);
        job.NextAttemptAt = null;
        job.WaitReason = SyncWaitReason.RemoteVerification;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
        Publish(job);

        if (await TryReconcileRemoteAsync(route, job, cancellationToken).ConfigureAwait(false))
        {
            await CompleteConfirmedJobAsync(job, cancellationToken).ConfigureAwait(false);
            return;
        }

        await MarkRemoteVerificationFailureAsync(
                job,
                technicalDetails ?? job.TechnicalDetails)
            .ConfigureAwait(false);
    }

    private async Task CompleteConfirmedJobAsync(SyncJob job, CancellationToken cancellationToken)
    {
        await _routeLifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Check, durable terminal transition, and payload cleanup share one
            // lifecycle lease with route removal. Whichever acquires it first is
            // the linearized outcome.
            if (job.RouteId is { } routeId && IsRouteRemoved(routeId))
            {
                await PreserveInterruptedUploadAsync(job, forceRemoteVerification: true)
                    .ConfigureAwait(false);
                return;
            }

            job.State = SyncJobState.Completed;
            job.Progress = 100;
            job.BytesTransferred = job.PayloadLength ?? Math.Max(0, job.BytesTransferred);
            job.IsProgressIndeterminate = false;
            job.EstimatedCompletionAt = null;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.RemoteConfirmedAt ??= job.CompletedAt;
            job.NextAttemptAt = null;
            ClearFailure(job);
            job.UpdatedAt = job.CompletedAt.Value;

            // Confirmation is durable before cleanup. If the process stops after
            // this UPDATE, startup/retention can safely remove the orphan.
            await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
            Publish(job);

            try
            {
                if (!string.IsNullOrWhiteSpace(job.PayloadPath))
                {
                    await _payloadStorage.DeleteAsync(job.PayloadPath, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Remote confirmation is already durable; cleanup is safe later.
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Write("Confirmed upload payload cleanup failed.", ex);
            }
        }
        finally
        {
            _routeLifecycleGate.Release();
        }
    }

    private async Task MarkRetryableFailureAsync(
        SyncJob job,
        SyncFailureKind failureKind,
        SyncWaitReason waitReason,
        string? technicalDetails)
    {
        job.Attempts++;
        job.State = job.Attempts >= MaxAttempts ? SyncJobState.Failed : SyncJobState.Waiting;
        job.Progress = 0;
        job.BytesTransferred = 0;
        job.UploadMayHaveCommitted = false;
        job.IsProgressIndeterminate =
            job.OperationKind == SyncOperationKind.Upload &&
            job.PayloadLength is null;
        job.BytesPerSecond = null;
        job.EstimatedCompletionAt = null;
        ApplyFailure(job, failureKind, technicalDetails);
        if (job.State == SyncJobState.Failed && job.Attempts >= MaxAttempts)
        {
            job.UserMessage = AppText.Get(
                job.OperationKind == SyncOperationKind.Delete
                    ? "SyncDeleteFailureStoppedAfterSix"
                    : "SyncFailureStoppedAfterSix");
            job.LastError = job.UserMessage;
        }

        job.WaitReason = job.State == SyncJobState.Waiting ? waitReason : SyncWaitReason.None;
        job.NextAttemptAt = job.State == SyncJobState.Waiting
            ? DateTimeOffset.UtcNow.Add(GetRetryDelay(job.Attempts))
            : null;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
        if (job.OperationKind == SyncOperationKind.Delete &&
            job.State == SyncJobState.Failed &&
            job.RouteId is { } deleteRouteId)
        {
            _contentService.ClearDeleteTombstone(deleteRouteId, job.RelativePath);
        }

        Publish(job);
    }

    private async Task MarkRemoteVerificationFailureAsync(
        SyncJob job,
        string? technicalDetails)
    {
        job.Attempts++;
        job.State = job.Attempts >= MaxAttempts
            ? SyncJobState.Failed
            : SyncJobState.VerifyingRemote;
        job.Progress = Math.Min(99, Math.Max(0, job.Progress));
        job.WaitReason = SyncWaitReason.RemoteVerification;
        if (job.FailureKind == SyncFailureKind.None)
        {
            job.FailureKind = SyncFailureKind.Unknown;
        }

        job.UserMessage = job.Attempts >= MaxAttempts
            ? AppText.Get("SyncFailureStoppedAfterSix")
            : string.IsNullOrWhiteSpace(job.UserMessage)
                ? AppText.Get("SyncFailureRemoteVerification")
                : job.UserMessage;
        job.LastError = job.UserMessage;
        if (!string.IsNullOrWhiteSpace(technicalDetails))
        {
            job.TechnicalDetails = NormalizeTechnicalDetails(technicalDetails);
        }

        job.NextAttemptAt = job.State == SyncJobState.VerifyingRemote
            ? DateTimeOffset.UtcNow.Add(GetRetryDelay(job.Attempts))
            : null;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
        Publish(job);
    }

    private async Task MarkTerminalFailureAsync(
        SyncJob job,
        SyncFailureKind failureKind,
        string? technicalDetails = null)
    {
        job.State = SyncJobState.Failed;
        job.Progress = 0;
        job.BytesTransferred = 0;
        job.UploadMayHaveCommitted = false;
        job.IsProgressIndeterminate =
            job.OperationKind == SyncOperationKind.Upload &&
            job.PayloadLength is null;
        job.BytesPerSecond = null;
        job.EstimatedCompletionAt = null;
        job.NextAttemptAt = null;
        ApplyFailure(job, failureKind, technicalDetails);
        job.WaitReason = SyncWaitReason.None;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
        if (job.OperationKind == SyncOperationKind.Delete &&
            job.RouteId is { } deleteRouteId)
        {
            _contentService.ClearDeleteTombstone(deleteRouteId, job.RelativePath);
        }

        Publish(job);
    }

    private async Task ResumeForConditionAsync(SyncFailureKind failureKind)
    {
        try
        {
            await _enqueueGate.WaitAsync(_cancellation.Token).ConfigureAwait(false);
            var transferGateAcquired = false;
            try
            {
                if (Volatile.Read(ref _resetSuspended) != 0)
                {
                    return;
                }

                await _activeTransferGate.WaitAsync(_cancellation.Token).ConfigureAwait(false);
                transferGateAcquired = true;
                var jobs = await _database.GetSyncJobsAsync().ConfigureAwait(false);
                foreach (var job in jobs.Where(job =>
                             job.FailureKind == failureKind &&
                             job.State is
                                 SyncJobState.Waiting or
                                 SyncJobState.Failed or
                                 SyncJobState.VerifyingRemote))
                {
                    var repeatsRemoteVerification =
                        job.State == SyncJobState.VerifyingRemote ||
                        job.WaitReason == SyncWaitReason.RemoteVerification;
                    job.State = repeatsRemoteVerification
                        ? SyncJobState.VerifyingRemote
                        : SyncJobState.Waiting;
                    job.NextAttemptAt = null;
                    job.WaitReason = repeatsRemoteVerification
                        ? SyncWaitReason.RemoteVerification
                        : SyncWaitReason.None;
                    job.UpdatedAt = DateTimeOffset.UtcNow;
                    await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
                    if (job.OperationKind == SyncOperationKind.Delete &&
                        job.RouteId is { } deleteRouteId)
                    {
                        _contentService.RegisterDeleteTombstone(
                            deleteRouteId,
                            job.RelativePath,
                            job.IsDirectory);
                    }

                    Publish(job);
                }
            }
            finally
            {
                if (transferGateAcquired)
                {
                    _activeTransferGate.Release();
                }

                _enqueueGate.Release();
            }

            SignalWorker();
        }
        catch (OperationCanceledException)
        {
            // Queue is stopping.
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Resuming uploads after connectivity/session restoration failed.", ex);
        }
    }

    private async Task CleanupPayloadStorageUnderResetBarrierAsync()
    {
        await _activeTransferGate.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _resetSuspended) != 0)
            {
                return;
            }

            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _cancellation.Token,
                _resetCancellation.Token);
            await CleanupPayloadStorageAsync(operationCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _activeTransferGate.Release();
        }
    }

    private async Task<IReadOnlyList<SyncJob>> GetPendingSyncJobsUnderResetBarrierAsync()
    {
        await _activeTransferGate.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        try
        {
            return Volatile.Read(ref _resetSuspended) != 0
                ? []
                : (await _database.GetPendingSyncJobsAsync().ConfigureAwait(false))
                    .Where(job =>
                        job.OperationKind != SyncOperationKind.Delete ||
                        job.DeleteArmed)
                    .ToArray();
        }
        finally
        {
            _activeTransferGate.Release();
        }
    }

    private async Task CleanupPayloadStorageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var jobs = await _database.GetSyncJobsAsync().ConfigureAwait(false);
            var retainedPaths = jobs
                .Where(job => job.IsActive)
                .Select(job => job.PayloadPath)
                .Where(path => !string.IsNullOrWhiteSpace(path));
            await _payloadStorage
                .CleanupAsync(retainedPaths, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // App shutdown or local reset interrupts maintenance without changing queue state.
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Upload payload maintenance failed.", ex);
        }
    }

    private async Task<string> ComputePayloadSha256Async(
        string payloadPath,
        CancellationToken cancellationToken)
    {
        await using var stream = await _payloadStorage
            .OpenReadAsync(payloadPath, cancellationToken)
            .ConfigureAwait(false);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private void ApplyFailure(
        SyncJob job,
        SyncFailureKind failureKind,
        string? technicalDetails = null)
    {
        job.FailureKind = failureKind;
        job.UserMessage = job.OperationKind == SyncOperationKind.Delete
            ? FriendlyDeleteMessage(failureKind)
            : FriendlyMessage(failureKind);
        job.LastError = job.UserMessage;
        job.TechnicalDetails = NormalizeTechnicalDetails(technicalDetails);
    }

    private static void ClearFailure(SyncJob job)
    {
        job.FailureKind = SyncFailureKind.None;
        job.WaitReason = SyncWaitReason.None;
        job.UserMessage = string.Empty;
        job.LastError = string.Empty;
        job.TechnicalDetails = string.Empty;
    }

    private static string FriendlyMessage(SyncFailureKind kind) => kind switch
    {
        SyncFailureKind.Network => AppText.Get("SyncFailureNetwork"),
        SyncFailureKind.Session => AppText.Get("SyncFailureSession"),
        SyncFailureKind.Permission => AppText.Get("SyncFailurePermission"),
        SyncFailureKind.Quota => AppText.Get("SyncFailureQuota"),
        SyncFailureKind.Conflict => AppText.Get("SyncFailureConflict"),
        SyncFailureKind.Integrity => AppText.Get("SyncFailureIntegrity"),
        SyncFailureKind.RouteUnavailable => AppText.Get("SyncFailureRouteUnavailable"),
        SyncFailureKind.PayloadUnavailable => AppText.Get("SyncFailurePayloadUnavailable"),
        SyncFailureKind.ServiceBusy => AppText.Get("SyncFailureServiceBusy"),
        _ => AppText.Get("SyncFailureUnknown")
    };

    private static string FriendlyDeleteMessage(SyncFailureKind kind) => kind switch
    {
        SyncFailureKind.Network => AppText.Get("SyncDeleteFailureNetwork"),
        SyncFailureKind.Session => AppText.Get("SyncDeleteFailureSession"),
        SyncFailureKind.Permission => AppText.Get("SyncDeleteFailurePermission"),
        SyncFailureKind.Conflict => AppText.Get("SyncDeleteFailureConflict"),
        SyncFailureKind.RouteUnavailable => AppText.Get("SyncDeleteFailureRouteUnavailable"),
        SyncFailureKind.ServiceBusy => AppText.Get("SyncDeleteFailureServiceBusy"),
        _ => AppText.Get("SyncDeleteFailureUnknown")
    };

    private static SyncFailureKind ClassifyFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return SyncFailureKind.Unknown;
        }

        var value = error.ToLowerInvariant();
        if (value.Contains("429") ||
            value.Contains("503") ||
            value.Contains("502") ||
            value.Contains("504") ||
            value.Contains("throttl") ||
            value.Contains("ocupado") ||
            value.Contains("indisponível"))
        {
            return SyncFailureKind.ServiceBusy;
        }

        if (value.Contains("sessão") ||
            value.Contains("session") ||
            value.Contains("entre novamente") ||
            value.Contains("401") ||
            value.Contains("unauthorized"))
        {
            return SyncFailureKind.Session;
        }

        if (value.Contains("403") ||
            value.Contains("forbidden") ||
            value.Contains("permiss"))
        {
            return SyncFailureKind.Permission;
        }

        if (value.Contains("quota") ||
            value.Contains("espaço") ||
            value.Contains("space"))
        {
            return SyncFailureKind.Quota;
        }

        if (value.Contains("conflit") ||
            value.Contains("conflict") ||
            value.Contains("412"))
        {
            return SyncFailureKind.Conflict;
        }

        if (value.Contains("integridade") ||
            value.Contains("integrity") ||
            value.Contains("criptograf"))
        {
            return SyncFailureKind.Integrity;
        }

        if (value.Contains("rede") ||
            value.Contains("network") ||
            value.Contains("conex") ||
            value.Contains("timeout") ||
            value.Contains("timed out"))
        {
            return SyncFailureKind.Network;
        }

        return SyncFailureKind.Unknown;
    }

    private static SyncWaitReason WaitReasonFor(SyncFailureKind kind) => kind switch
    {
        SyncFailureKind.Network => SyncWaitReason.Network,
        SyncFailureKind.Session => SyncWaitReason.Session,
        _ => SyncWaitReason.Backoff
    };

    private string TechnicalDetails(Exception exception) =>
        NormalizeTechnicalDetails($"{exception.GetType().Name}: {exception.Message}");

    private string NormalizeTechnicalDetails(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var redacted = _redactor.Redact(value);
        return redacted.Length <= 2048 ? redacted : redacted[..2048];
    }

    private static void ApplyReceipt(SyncJob job, RemoteUploadReceipt? receipt)
    {
        if (receipt is null)
        {
            return;
        }

        job.RemoteItemId = receipt.ItemId ?? string.Empty;
        job.RemoteETag = receipt.ETag ?? string.Empty;
        job.RemoteLength = receipt.Size;
        job.RemoteModifiedAt = receipt.ModifiedAt;
    }

    private static bool IsValidReceipt(SyncJob job, RemoteUploadReceipt? receipt)
    {
        if (receipt?.Size is not { } remoteSize || remoteSize < 0)
        {
            return false;
        }

        if (job.PayloadLength is { } expectedSize && remoteSize != expectedSize)
        {
            return false;
        }

        job.PayloadLength ??= remoteSize;
        return true;
    }

    private static TimeSpan GetRetryDelay(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(900, 5 * Math.Pow(2, Math.Max(0, attempts - 1))));

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup is retried by storage maintenance.
        }
    }

    private void Publish(SyncJob job)
    {
        try
        {
            JobChanged?.Invoke(job);
        }
        catch
        {
            // Observers must never stop the durable worker.
        }
    }

    private void SignalWorker()
    {
        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
            // The queue is already disposed.
        }
    }

    private void ThrowIfResetSuspended()
    {
        if (Volatile.Read(ref _resetSuspended) != 0)
        {
            throw new InvalidOperationException(
                "Local data is being deleted. New upload operations are temporarily blocked.");
        }
    }

    private void ThrowIfStopping()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _stopRequested) != 0)
        {
            throw new InvalidOperationException(
                "The upload queue is stopping and cannot accept new uploads.");
        }
    }

    private void ThrowIfRouteRemoved(Guid routeId)
    {
        if (IsRouteRemoved(routeId))
        {
            throw new InvalidOperationException(
                "This SharePoint destination is being removed and cannot accept new uploads.");
        }
    }

    private bool IsRouteRemoved(Guid routeId)
    {
        lock (_routeAdmissionGate)
        {
            return _removedRouteIds.Contains(routeId);
        }
    }

    private async Task BeginRouteRemovalAsync(Guid routeId)
    {
        await _routeLifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            BeginRouteRemoval(routeId);
        }
        finally
        {
            _routeLifecycleGate.Release();
        }
    }

    private void BeginRouteRemoval(Guid routeId)
    {
        CancellationTokenSource cancellationSource;
        lock (_routeAdmissionGate)
        {
            _removedRouteIds.Add(routeId);
            if (!_routeCancellationSources.TryGetValue(routeId, out cancellationSource!))
            {
                cancellationSource = new CancellationTokenSource();
                _routeCancellationSources[routeId] = cancellationSource;
            }
        }

        try
        {
            cancellationSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Queue shutdown already owns the remaining lifecycle.
        }
    }

    private CancellationToken GetRouteCancellationToken(Guid routeId)
    {
        CancellationTokenSource cancellationSource;
        var cancel = false;
        lock (_routeAdmissionGate)
        {
            if (!_routeCancellationSources.TryGetValue(routeId, out cancellationSource!))
            {
                cancellationSource = new CancellationTokenSource();
                _routeCancellationSources[routeId] = cancellationSource;
            }

            cancel = _removedRouteIds.Contains(routeId) &&
                !cancellationSource.IsCancellationRequested;
        }

        if (cancel)
        {
            cancellationSource.Cancel();
        }

        return cancellationSource.Token;
    }

    private void RestoreRouteAdmission(Guid routeId)
    {
        CancellationTokenSource? previous = null;
        lock (_routeAdmissionGate)
        {
            _removedRouteIds.Remove(routeId);
            if (_routeCancellationSources.Remove(routeId, out var existing))
            {
                previous = existing;
            }

            _routeCancellationSources[routeId] = new CancellationTokenSource();
        }

        previous?.Dispose();
    }

    private static bool IsWorkerEligible(SyncJobState state) =>
        state is
            SyncJobState.PersistingLocal or
            SyncJobState.StoredLocally or
            SyncJobState.Waiting or
            SyncJobState.Uploading or
            SyncJobState.VerifyingRemote;

    private void ResumeAfterReset()
    {
        var previous = _resetCancellation;
        _resetCancellation = new CancellationTokenSource();
        Volatile.Write(ref _resetSuspended, 0);
        previous.Dispose();
        SignalWorker();
    }

    private void NetworkInformation_NetworkStatusChanged(object sender)
    {
        try
        {
            if (NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel() ==
                NetworkConnectivityLevel.InternetAccess)
            {
                SignalConnectivityRestored();
            }
        }
        catch
        {
            // The scheduled retry remains the fallback if Windows cannot report status.
        }
    }

    private static string NormalizePath(string value) =>
        (value ?? string.Empty).Replace('\\', '/').Trim('/');

    private sealed record DeleteCommitBarrierResult(
        bool IsResolved,
        string? TechnicalDetails = null);

    private sealed class ResetSuspension(UploadQueueService owner) : IAsyncDisposable
    {
        private UploadQueueService? _owner = owner;

        public ValueTask DisposeAsync()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null)
            {
                current.ResumeAfterReset();
                current._activeTransferGate.Release();
                current._enqueueGate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class DurableUploadProgress(
        UploadQueueService owner,
        SyncJob job) : IProgress<UploadTransferProgress>
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _lastPersistedAt;
        private long _maximumTransferred;
        private int _firstTransferEvidencePersisted;
        private int _commitRiskPersisted = job.UploadMayHaveCommitted ? 1 : 0;

        public void Report(UploadTransferProgress value)
        {
            var total = value.TotalBytes ?? job.PayloadLength;
            var acknowledged = Math.Max(0, value.BytesTransferred);
            if (total is { } knownTotal)
            {
                acknowledged = Math.Min(acknowledged, Math.Max(0, knownTotal));
                job.PayloadLength ??= knownTotal;
            }

            // Both streamed and server-acknowledged byte events are real transfer
            // evidence. Neither can move the state past Uploading or progress past
            // 99%; only remote verification completes the job.
            _maximumTransferred = Math.Max(_maximumTransferred, acknowledged);
            job.BytesTransferred = _maximumTransferred;
            job.IsProgressIndeterminate = total is null;
            job.Progress = total is > 0
                ? Math.Min(99, (int)Math.Floor(_maximumTransferred * 100d / total.Value))
                : 0;

            if (_stopwatch.Elapsed >= ReliableRateSample && _maximumTransferred >= 64 * 1024)
            {
                var bytesPerSecond = _maximumTransferred / _stopwatch.Elapsed.TotalSeconds;
                if (bytesPerSecond > 0)
                {
                    job.BytesPerSecond = bytesPerSecond;
                    job.EstimatedCompletionAt = total is > 0 && _maximumTransferred < total.Value
                        ? DateTimeOffset.UtcNow.AddSeconds(
                            (total.Value - _maximumTransferred) / bytesPerSecond)
                        : null;
                }
            }

            job.UpdatedAt = DateTimeOffset.UtcNow;
            if (value.MayHaveCommitted)
            {
                job.UploadMayHaveCommitted = true;
            }

            var elapsedTicks = _stopwatch.ElapsedTicks;
            var intervalTicks = (long)(ProgressPersistenceInterval.TotalSeconds * Stopwatch.Frequency);
            var firstTransferEvidence =
                _maximumTransferred > 0 &&
                Volatile.Read(ref _firstTransferEvidencePersisted) == 0;
            var firstCommitRisk =
                value.MayHaveCommitted &&
                Volatile.Read(ref _commitRiskPersisted) == 0;
            var criticalPersistence = firstTransferEvidence || firstCommitRisk;
            if (!criticalPersistence &&
                elapsedTicks - Interlocked.Read(ref _lastPersistedAt) < intervalTicks &&
                !(total is { } finalTotal && _maximumTransferred >= finalTotal))
            {
                return;
            }

            try
            {
                owner._database.UpdateSyncJobAsync(job).GetAwaiter().GetResult();
                Interlocked.Exchange(ref _lastPersistedAt, elapsedTicks);
                if (_maximumTransferred > 0)
                {
                    Volatile.Write(ref _firstTransferEvidencePersisted, 1);
                }

                if (job.UploadMayHaveCommitted)
                {
                    Volatile.Write(ref _commitRiskPersisted, 1);
                }

                owner.Publish(job);
            }
            catch (Exception ex)
            {
                if (criticalPersistence)
                {
                    // ProgressReadStream invokes Report before returning the read
                    // bytes to HttpClient. Fail closed here: without a durable
                    // first-byte marker, no caller may send that buffer remotely.
                    if (firstTransferEvidence)
                    {
                        _maximumTransferred = 0;
                        job.BytesTransferred = 0;
                        job.Progress = 0;
                        job.BytesPerSecond = null;
                        job.EstimatedCompletionAt = null;
                    }

                    if (firstCommitRisk)
                    {
                        job.UploadMayHaveCommitted = false;
                    }

                    throw new InvalidOperationException(
                        "Critical remote-transfer evidence could not be persisted.",
                        ex);
                }

                StartupDiagnostics.Write("Persisting upload progress failed.", ex);
            }
        }
    }
}
