using EasyShare.Models;

namespace EasyShare.Services;

/// <summary>
/// Persists file uploads before attempting network I/O so a disconnected SharePoint
/// session cannot discard the user's local edits.
/// </summary>
public sealed class UploadQueueService : IDisposable
{
    private const int MaxAttempts = 6;
    private readonly LocalDatabase _database;
    private readonly SharePointBrowserContentService _contentService;
    private readonly UploadPayloadStorage _payloadStorage;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _enqueueGate = new(1, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _workerGate = new();
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
    }

    public void Start()
    {
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
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalizedPath = NormalizePath(relativePath);
            var existing = await _database
                .FindPendingSyncJobAsync(route.Id, normalizedPath)
                .ConfigureAwait(false);
            var job = existing ?? new SyncJob
            {
                RouteId = route.Id,
                FileName = Path.GetFileName(relativePath),
                RouteDisplayName = route.DisplayName,
                RelativePath = normalizedPath,
                PayloadPath = _payloadStorage.CreatePayloadPath()
            };

            if (string.IsNullOrWhiteSpace(job.PayloadPath))
            {
                job.PayloadPath = _payloadStorage.CreatePayloadPath();
            }

            await _payloadStorage
                .StoreAsync(job.PayloadPath, payload, cancellationToken)
                .ConfigureAwait(false);
            job.RouteId = route.Id;
            job.FileName = Path.GetFileName(relativePath);
            job.RouteDisplayName = route.DisplayName;
            job.RelativePath = normalizedPath;
            job.ExpectedModifiedAt ??= expectedModifiedAt;
            job.State = SyncJobState.Waiting;
            job.Progress = 0;
            job.Attempts = 0;
            job.LastError = string.Empty;
            job.NextAttemptAt = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;

            if (existing is null)
            {
                await _database.AddSyncJobAsync(job).ConfigureAwait(false);
            }
            else
            {
                await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
            }

            Publish(job);
            _signal.Release();
            return job;
        }
        finally
        {
            _enqueueGate.Release();
        }
    }

    public async Task RetryAsync(Guid jobId)
    {
        var job = (await _database.GetSyncJobsAsync()).FirstOrDefault(item => item.Id == jobId);
        if (job is null || job.State is SyncJobState.Completed or SyncJobState.Uploading)
        {
            return;
        }

        job.State = SyncJobState.Waiting;
        job.Progress = 0;
        job.Attempts = 0;
        job.LastError = string.Empty;
        job.NextAttemptAt = null;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _database.UpdateSyncJobAsync(job);
        Publish(job);
        _signal.Release();
    }

    public async Task<SyncConflictActionResult> DiscardLocalPayloadAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = (await _database.GetSyncJobsAsync().ConfigureAwait(false))
                .FirstOrDefault(item => item.Id == jobId);
            if (job is null)
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.NotFound);
            }

            if (job.State is not (SyncJobState.Conflict or SyncJobState.Failed))
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.InvalidState, job);
            }

            if (!string.IsNullOrWhiteSpace(job.PayloadPath) &&
                !await _payloadStorage.DeleteAsync(job.PayloadPath, cancellationToken).ConfigureAwait(false))
            {
                return new SyncConflictActionResult(
                    SyncConflictActionStatus.Failed,
                    job,
                    Error: "O conteúdo local não pôde ser removido com segurança.");
            }

            job.State = SyncJobState.Completed;
            job.Progress = 100;
            job.LastError = string.Empty;
            job.NextAttemptAt = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
            Publish(job);
            return new SyncConflictActionResult(SyncConflictActionStatus.Discarded, job);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Discarding a conflicted upload payload failed.", ex);
            return new SyncConflictActionResult(
                SyncConflictActionStatus.Failed,
                Error: "O conteúdo local não pôde ser descartado.");
        }
        finally
        {
            _enqueueGate.Release();
        }
    }

    /// <summary>
    /// Writes the explicitly requested plaintext copy directly to a new caller-selected
    /// destination. A plaintext staging file is never created.
    /// </summary>
    public async Task<SyncConflictActionResult> ExportLocalPayloadAsync(
        Guid jobId,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var createdDestination = false;
        string? fullDestination = null;
        try
        {
            var job = (await _database.GetSyncJobsAsync().ConfigureAwait(false))
                .FirstOrDefault(item => item.Id == jobId);
            if (job is null)
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.NotFound);
            }

            if (job.State is not (SyncJobState.Conflict or SyncJobState.Failed))
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
                TryDeletePayload(fullDestination);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (createdDestination && fullDestination is not null)
            {
                TryDeletePayload(fullDestination);
            }

            StartupDiagnostics.Write("Exporting a conflicted upload payload failed.", ex);
            return new SyncConflictActionResult(
                SyncConflictActionStatus.Failed,
                Error: "A cópia local não pôde ser exportada.");
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
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = (await _database.GetSyncJobsAsync().ConfigureAwait(false))
                .FirstOrDefault(item => item.Id == jobId);
            if (job is null)
            {
                return new SyncConflictActionResult(SyncConflictActionStatus.NotFound);
            }

            if (job.State is not (SyncJobState.Conflict or SyncJobState.Failed))
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
            job.Attempts = 0;
            job.LastError = string.Empty;
            job.NextAttemptAt = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await _database.UpdateSyncJobAsync(job).ConfigureAwait(false);
            Publish(job);
            _signal.Release();
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
                Error: "O envio local não pôde ser preparado para substituição.");
        }
        finally
        {
            _enqueueGate.Release();
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _signal.Release();
        _signal.Dispose();
        _enqueueGate.Dispose();
        _cancellation.Dispose();
    }

    private async Task ProcessLoopAsync()
    {
        await CleanupPayloadStorageAsync().ConfigureAwait(false);

        while (!_cancellation.IsCancellationRequested)
        {
            var processed = false;
            try
            {
                var jobs = await _database.GetPendingSyncJobsAsync();
                foreach (var job in jobs)
                {
                    if (job.NextAttemptAt > DateTimeOffset.UtcNow)
                    {
                        continue;
                    }

                    processed = true;
                    await ProcessJobAsync(job);
                }
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
                await _signal.WaitAsync(TimeSpan.FromSeconds(10), _cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ProcessJobAsync(SyncJob job)
    {
        if (job.RouteId is null || string.IsNullOrWhiteSpace(job.RelativePath))
        {
            await MarkFailedAsync(job, "A rota do envio não está mais disponível.", terminal: true);
            return;
        }

        var route = (await _database.GetRoutesAsync()).FirstOrDefault(item => item.Id == job.RouteId.Value);
        if (route is null)
        {
            await MarkFailedAsync(job, "A pasta fixada foi removida.", terminal: true);
            return;
        }

        if (!File.Exists(job.PayloadPath))
        {
            await MarkFailedAsync(job, "O conteúdo local do envio não foi encontrado.", terminal: true);
            return;
        }

        job.State = SyncJobState.Uploading;
        job.Progress = 10;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _database.UpdateSyncJobAsync(job);
        Publish(job);

        UploadAttemptResult result;
        try
        {
            await _payloadStorage
                .MigrateLegacyPayloadAsync(job.PayloadPath, _cancellation.Token)
                .ConfigureAwait(false);
            await using var content = await _payloadStorage
                .OpenReadAsync(job.PayloadPath, _cancellation.Token)
                .ConfigureAwait(false);
            result = await _contentService
                .TryUploadFileAsync(
                    route,
                    job.RelativePath,
                    content,
                    job.ExpectedModifiedAt,
                    _cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (InvalidEncryptedPayloadException)
        {
            await MarkFailedAsync(
                job,
                "O conteúdo local criptografado não passou na verificação de integridade.",
                terminal: true);
            return;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            await MarkFailedAsync(
                job,
                "O conteúdo local criptografado não pôde ser aberto por este usuário.",
                terminal: true);
            return;
        }
        catch (Exception ex)
        {
            result = new UploadAttemptResult(UploadAttemptState.RetryableFailure, ex.Message);
        }

        switch (result.State)
        {
            case UploadAttemptState.Succeeded:
                job.State = SyncJobState.Completed;
                job.Progress = 100;
                job.LastError = string.Empty;
                job.NextAttemptAt = null;
                TryDeletePayload(job.PayloadPath);
                break;

            case UploadAttemptState.Conflict:
                job.State = SyncJobState.Conflict;
                job.Progress = 0;
                job.LastError = result.Error ?? "O arquivo remoto mudou.";
                job.NextAttemptAt = null;
                break;

            default:
                job.Attempts++;
                job.State = job.Attempts >= MaxAttempts ? SyncJobState.Failed : SyncJobState.Waiting;
                job.Progress = 0;
                job.LastError = result.Error ?? "O envio falhou.";
                job.NextAttemptAt = job.State == SyncJobState.Waiting
                    ? DateTimeOffset.UtcNow.Add(GetRetryDelay(job.Attempts))
                    : null;
                break;
        }

        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _database.UpdateSyncJobAsync(job);
        Publish(job);
    }

    private async Task MarkFailedAsync(SyncJob job, string message, bool terminal)
    {
        job.State = terminal ? SyncJobState.Failed : SyncJobState.Waiting;
        job.Progress = 0;
        job.LastError = message;
        job.NextAttemptAt = terminal ? null : DateTimeOffset.UtcNow.AddMinutes(1);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _database.UpdateSyncJobAsync(job);
        Publish(job);
    }

    private static TimeSpan GetRetryDelay(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(900, 5 * Math.Pow(2, Math.Max(0, attempts - 1))));

    private static void TryDeletePayload(string path)
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
            // Cleanup can be retried on a later startup.
        }
    }

    private async Task CleanupPayloadStorageAsync()
    {
        try
        {
            var jobs = await _database.GetSyncJobsAsync().ConfigureAwait(false);
            var retainedPaths = jobs
                .Where(job => job.State != SyncJobState.Completed)
                .Select(job => job.PayloadPath)
                .Where(path => !string.IsNullOrWhiteSpace(path));
            await _payloadStorage
                .CleanupAsync(retainedPaths, cancellationToken: _cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // App shutdown interrupts maintenance without changing queue state.
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Upload payload maintenance failed.", ex);
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
            // UI observers must never stop the upload worker.
        }
    }

    private static string NormalizePath(string value) =>
        value.Replace('\\', '/').Trim('/');
}
