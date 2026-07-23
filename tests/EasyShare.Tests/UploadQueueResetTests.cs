using EasyShare.Models;
using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class UploadQueueResetTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"EasyShareUploadQueueResetTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExportRejectedDuringResetReleasesEnqueueGate()
    {
        var (queue, route) = await CreateQueueAsync();
        using (queue)
        await using (var blockingPayload = new BlockingReadStream("queued payload"u8.ToArray()))
        {
            var enqueueTask = queue.EnqueueAsync(route, "queued.txt", blockingPayload, null);
            await blockingPayload.ReadStarted;

            var exportTask = queue.ExportLocalPayloadAsync(
                Guid.NewGuid(),
                Path.Combine(_root, "exported.txt"));
            var suspensionTask = queue.SuspendForResetAsync();

            blockingPayload.Release();
            await enqueueTask;
            await Assert.ThrowsAsync<InvalidOperationException>(() => exportTask);

            var completed = await Task.WhenAny(suspensionTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(suspensionTask, completed);
            await using var suspension = await suspensionTask;
        }
    }

    [Fact]
    public async Task CancelledResetSuspensionRestoresQueueAvailability()
    {
        var (queue, route) = await CreateQueueAsync();
        using (queue)
        await using (var blockingPayload = new BlockingReadStream("first payload"u8.ToArray()))
        {
            var firstEnqueue = queue.EnqueueAsync(route, "first.txt", blockingPayload, null);
            await blockingPayload.ReadStarted;

            using var cancellation = new CancellationTokenSource();
            var suspensionTask = queue.SuspendForResetAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => suspensionTask);

            blockingPayload.Release();
            await firstEnqueue;

            await using var secondPayload = new MemoryStream("second payload"u8.ToArray());
            var secondJob = await queue.EnqueueAsync(route, "second.txt", secondPayload, null);
            Assert.Equal("second.txt", secondJob.RelativePath);
        }
    }

    [Fact]
    public async Task AResetSuspensionHasExactlyOneOwnerAndReleasesItsGatesOnce()
    {
        var (queue, route) = await CreateQueueAsync();
        using (queue)
        {
            var firstSuspension = await queue.SuspendForResetAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => queue.SuspendForResetAsync());

            await firstSuspension.DisposeAsync();
            await firstSuspension.DisposeAsync();

            await using var payload = new MemoryStream("payload after reset"u8.ToArray());
            var job = await queue.EnqueueAsync(route, "after-reset.txt", payload, null);
            Assert.Equal("after-reset.txt", job.RelativePath);
        }
    }

    [Fact]
    public async Task ConcurrentResetSuspensionsHaveExactlyOneOwner()
    {
        const int participantCount = 8;
        var (queue, _) = await CreateQueueAsync();
        using (queue)
        using (var start = new Barrier(participantCount))
        {
            var releaseOwner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allNonOwnersRejected = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var ownerCount = 0;
            var rejectedCount = 0;

            var attempts = Enumerable.Range(0, participantCount)
                .Select(_ => Task.Factory.StartNew(
                        async () =>
                        {
                            start.SignalAndWait();
                            try
                            {
                                await using var suspension = await queue.SuspendForResetAsync();
                                Interlocked.Increment(ref ownerCount);
                                await releaseOwner.Task;
                                return true;
                            }
                            catch (InvalidOperationException)
                            {
                                if (Interlocked.Increment(ref rejectedCount) == participantCount - 1)
                                {
                                    allNonOwnersRejected.TrySetResult();
                                }

                                return false;
                            }
                        },
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default)
                    .Unwrap())
                .ToArray();

            var rejectionResult = await Task.WhenAny(
                allNonOwnersRejected.Task,
                Task.Delay(TimeSpan.FromSeconds(5)));
            releaseOwner.TrySetResult();
            var results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Same(allNonOwnersRejected.Task, rejectionResult);
            Assert.Equal(1, ownerCount);
            Assert.Single(results, acquired => acquired);
        }
    }

    [Fact]
    public async Task WorkerCleanupWaitsForResetBarrierBeforeCreatingQueueStorage()
    {
        var paths = new AppDataPaths(Path.Combine(_root, "EasyShare"));
        var (queue, _) = await CreateQueueAsync(paths);
        using (queue)
        {
            var suspension = await queue.SuspendForResetAsync();
            queue.Start();

            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.False(Directory.Exists(paths.UploadQueueDirectory));

            await suspension.DisposeAsync();
            await WaitUntilAsync(
                () => Directory.Exists(paths.UploadQueueDirectory),
                TimeSpan.FromSeconds(5));
        }
    }

    private async Task<(UploadQueueService Queue, DriveRoute Route)> CreateQueueAsync(
        AppDataPaths? paths = null)
    {
        paths ??= new AppDataPaths(Path.Combine(_root, "EasyShare"));
        var database = new LocalDatabase(paths);
        await database.InitializeAsync();
        var storage = new UploadPayloadStorage(
            paths,
            protector: new PassthroughProtector());
        var queue = new UploadQueueService(
            database,
            new SharePointBrowserContentService(),
            paths,
            storage);
        return (queue, new DriveRoute
        {
            DisplayName = "Documents",
            SharePointUrl = "https://contoso.sharepoint.com/sites/team"
        });
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected upload queue state was not reached in time.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class PassthroughProtector : IUserDataProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => protectedData.ToArray();
    }

    private sealed class BlockingReadStream(byte[] payload) : Stream
    {
        private readonly TaskCompletionSource _readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _returnedPayload;

        public Task ReadStarted => _readStarted.Task;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Release() => _release.TrySetResult();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_returnedPayload)
            {
                return 0;
            }

            _readStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            var count = Math.Min(buffer.Length, payload.Length);
            payload.AsMemory(0, count).CopyTo(buffer);
            _returnedPayload = true;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
