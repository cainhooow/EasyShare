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
    private readonly AppDataPaths _paths;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _enqueueGate = new(1, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _workerGate = new();
    private Task? _worker;

    public event Action<SyncJob>? JobChanged;

    public UploadQueueService(
        LocalDatabase database,
        SharePointBrowserContentService contentService,
        AppDataPaths paths)
    {
        _database = database;
        _contentService = contentService;
        _paths = paths;
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
        _enqueueGate.Wait();
        try
        {
            _paths.EnsureCreated();
            var queueDirectory = Path.Combine(_paths.DataDirectory, "UploadQueue");
            Directory.CreateDirectory(queueDirectory);

            var existing = _database
                .FindPendingSyncJobAsync(route.Id, NormalizePath(relativePath))
                .GetAwaiter()
                .GetResult();
            var job = existing ?? new SyncJob
            {
                RouteId = route.Id,
                FileName = Path.GetFileName(relativePath),
                RouteDisplayName = route.DisplayName,
                RelativePath = NormalizePath(relativePath),
                PayloadPath = Path.Combine(queueDirectory, $"{Guid.NewGuid():N}.upload")
            };

            if (string.IsNullOrWhiteSpace(job.PayloadPath))
            {
                job.PayloadPath = Path.Combine(queueDirectory, $"{job.Id:N}.upload");
            }

            WritePayloadAtomically(job.PayloadPath, bytes);
            job.RouteId = route.Id;
            job.FileName = Path.GetFileName(relativePath);
            job.RouteDisplayName = route.DisplayName;
            job.RelativePath = NormalizePath(relativePath);
            job.ExpectedModifiedAt ??= expectedModifiedAt;
            job.State = SyncJobState.Waiting;
            job.Progress = 0;
            job.Attempts = 0;
            job.LastError = string.Empty;
            job.NextAttemptAt = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;

            if (existing is null)
            {
                _database.AddSyncJobAsync(job).GetAwaiter().GetResult();
            }
            else
            {
                _database.UpdateSyncJobAsync(job).GetAwaiter().GetResult();
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
            var bytes = await File.ReadAllBytesAsync(job.PayloadPath, _cancellation.Token);
            result = _contentService.TryUploadFile(route, job.RelativePath, bytes, job.ExpectedModifiedAt);
        }
        catch (OperationCanceledException)
        {
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

    private static void WritePayloadAtomically(string path, byte[] bytes)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temporaryPath, bytes);
        File.Move(temporaryPath, path, overwrite: true);
    }

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
