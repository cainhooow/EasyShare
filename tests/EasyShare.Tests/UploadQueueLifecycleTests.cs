using System.Text;
using EasyShare.Models;
using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class UploadQueueLifecycleTests
{
    [Fact]
    public async Task CompletesOnlyAfterRemoteVerificationAndPersistsReceiptBeforeCleanup()
    {
        await using var context = await QueueContext.CreateAsync();
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "Reports/verified.txt",
            new MemoryStream("verified payload"u8.ToArray()),
            null);

        Assert.Equal(SyncJobState.StoredLocally, job.State);
        Assert.Equal(0, job.Progress);
        Assert.True(File.Exists(job.PayloadPath));

        context.Queue.Start();
        var completed = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Completed);

        Assert.Equal(100, completed.Progress);
        Assert.Equal(completed.PayloadLength, completed.BytesTransferred);
        Assert.NotNull(completed.RemoteConfirmedAt);
        Assert.Equal(completed.PayloadLength, completed.RemoteLength);
        Assert.NotEmpty(completed.RemoteItemId);
        Assert.False(File.Exists(completed.PayloadPath));
    }

    [Fact]
    public async Task PendingUploadIsDiscardedBeforeDurableDeleteRuns()
    {
        await using var context = await QueueContext.CreateAsync();
        var upload = await context.Queue.EnqueueAsync(
            context.Route,
            "Reports/delete-before-upload.txt",
            new MemoryStream("must never be uploaded"u8.ToArray()),
            null);
        var delete = await context.Queue.QueueDeleteAsync(
            context.Route,
            upload.RelativePath,
            isDirectory: false);

        context.Queue.Start();
        var completedDelete = await context.WaitForJobAsync(
            delete.Id,
            item => item.State == SyncJobState.Completed);
        var discardedUpload = await context.WaitForJobAsync(
            upload.Id,
            item => item.State == SyncJobState.Discarded);

        Assert.Equal(SyncOperationKind.Delete, completedDelete.OperationKind);
        Assert.Equal(0, context.Content.UploadCalls);
        Assert.Equal(1, context.Content.DeleteCalls);
        Assert.False(File.Exists(discardedUpload.PayloadPath));
        Assert.False(context.Content.HasRemoteItem(context.Route, upload.RelativePath));
    }

    [Fact]
    public async Task AmbiguousInFlightUploadIsResolvedBeforeDeleteAndCannotResurrectFile()
    {
        await using var context = await QueueContext.CreateAsync();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callOrder = new List<string>();
        var callOrderGate = new object();
        context.Content.UploadHandler = async (
            route,
            relativePath,
            content,
            _,
            cancellationToken,
            progress) =>
        {
            var bytes = new byte[checked((int)content.Length)];
            await content.ReadExactlyAsync(bytes, cancellationToken);
            progress?.Report(new UploadTransferProgress(
                bytes.Length,
                bytes.Length,
                IsAcknowledged: false,
                MayHaveCommitted: true));
            context.Content.SetRemoteItem(route, relativePath, bytes.Length, DateTimeOffset.UtcNow);
            lock (callOrderGate)
            {
                callOrder.Add("upload-commit-ambiguous");
            }

            committed.TrySetResult();
            await releaseResponse.Task.WaitAsync(cancellationToken);
            return new UploadAttemptResult(
                UploadAttemptState.RetryableFailure,
                FailureKind: SyncFailureKind.Network,
                TechnicalDetails: "response lost after commit",
                IsCommitAmbiguous: true);
        };
        context.Content.DeleteHandler = (route, relativePath, _, _) =>
        {
            lock (callOrderGate)
            {
                callOrder.Add("delete");
            }

            context.Content.RemoveRemoteItem(route, relativePath);
            return Task.FromResult(new RemoteDeleteAttemptResult(
                RemoteDeleteAttemptState.Succeeded,
                HttpStatusCode: 204));
        };
        var upload = await context.Queue.EnqueueAsync(
            context.Route,
            "Reports/ambiguous-then-delete.txt",
            new MemoryStream("may already exist remotely"u8.ToArray()),
            null);
        context.Queue.Start();
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var delete = await context.Queue
            .QueueDeleteAsync(context.Route, upload.RelativePath, isDirectory: false)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, context.Content.DeleteCalls);
        Assert.True(context.Content.HasRemoteItem(context.Route, upload.RelativePath));

        releaseResponse.TrySetResult();
        await context.WaitForJobAsync(upload.Id, item => item.State == SyncJobState.Completed);
        await context.WaitForJobAsync(delete.Id, item => item.State == SyncJobState.Completed);

        Assert.Equal(["upload-commit-ambiguous", "delete"], callOrder);
        Assert.Equal(1, context.Content.UploadCalls);
        Assert.Equal(1, context.Content.DeleteCalls);
        Assert.False(context.Content.HasRemoteItem(context.Route, upload.RelativePath));
    }

    [Theory]
    [InlineData(RemoteUploadVerificationState.NotFound)]
    [InlineData(RemoteUploadVerificationState.Unavailable)]
    public async Task EarlyDeleteNotFoundStaysDurableUntilLateAmbiguousCommitIsRemoved(
        RemoteUploadVerificationState initialVerificationState)
    {
        await using var context = await QueueContext.CreateAsync();
        var verificationCalls = 0;
        context.Content.VerifyHandler = (route, relativePath, _) =>
        {
            var call = Interlocked.Increment(ref verificationCalls);
            if (call == 1)
            {
                return Task.FromResult(initialVerificationState ==
                    RemoteUploadVerificationState.NotFound
                        ? new RemoteUploadVerificationResult(
                            RemoteUploadVerificationState.NotFound,
                            FailureKind: SyncFailureKind.None)
                        : new RemoteUploadVerificationResult(
                            RemoteUploadVerificationState.Unavailable,
                            FailureKind: SyncFailureKind.Network,
                            TechnicalDetails: "initial verification unavailable"));
            }

            if (!context.Content.HasRemoteItem(route, relativePath))
            {
                return Task.FromResult(new RemoteUploadVerificationResult(
                    RemoteUploadVerificationState.NotFound,
                    FailureKind: SyncFailureKind.None));
            }

            return Task.FromResult(new RemoteUploadVerificationResult(
                RemoteUploadVerificationState.Confirmed,
                new RemoteUploadReceipt(
                    "late-item",
                    "\"late-etag\"",
                    "late commit"u8.Length,
                    DateTimeOffset.UtcNow),
                SyncFailureKind.None));
        };
        context.Content.UploadHandler = async (_, _, content, _, cancellationToken, progress) =>
        {
            var bytes = new byte[checked((int)content.Length)];
            await content.ReadExactlyAsync(bytes, cancellationToken);
            progress?.Report(new UploadTransferProgress(
                bytes.Length,
                bytes.Length,
                IsAcknowledged: false,
                MayHaveCommitted: true));
            return new UploadAttemptResult(
                UploadAttemptState.RetryableFailure,
                FailureKind: SyncFailureKind.Network,
                TechnicalDetails: "final PUT response was lost",
                IsCommitAmbiguous: true);
        };
        context.Content.DeleteHandler = (route, relativePath, _, _) =>
        {
            if (context.Content.DeleteCalls == 1)
            {
                return Task.FromResult(new RemoteDeleteAttemptResult(
                    RemoteDeleteAttemptState.Succeeded,
                    HttpStatusCode: 404));
            }

            context.Content.RemoveRemoteItem(route, relativePath);
            return Task.FromResult(new RemoteDeleteAttemptResult(
                RemoteDeleteAttemptState.Succeeded,
                HttpStatusCode: 204));
        };
        var upload = await context.Queue.EnqueueAsync(
            context.Route,
            "Reports/late-ambiguous-commit.txt",
            new MemoryStream("late commit"u8.ToArray()),
            null);
        context.Queue.Start();
        await context.WaitForJobAsync(
            upload.Id,
            item => item.State == SyncJobState.VerifyingRemote && item.Attempts == 1);

        var delete = await context.Queue.QueueDeleteAsync(
            context.Route,
            upload.RelativePath,
            isDirectory: false);
        var deferred = await context.WaitForJobAsync(
            delete.Id,
            item =>
                item.State == SyncJobState.Waiting &&
                item.WaitReason == SyncWaitReason.RemoteVerification &&
                context.Content.DeleteCalls == 1);

        Assert.NotNull(deferred.DeleteBarrierObservedAt);
        Assert.True(context.Content.IsDeleteTombstoned(context.Route, delete.RelativePath));
        Assert.Equal(SyncJobState.VerifyingRemote, (await context.Database.GetSyncJobAsync(upload.Id))!.State);

        context.Content.SetRemoteItem(
            context.Route,
            upload.RelativePath,
            "late commit"u8.Length,
            DateTimeOffset.UtcNow);
        await context.Queue.RetryNowAsync(delete.Id);
        await context.WaitForJobAsync(delete.Id, item => item.State == SyncJobState.Completed);
        var discardedUpload = await context.WaitForJobAsync(
            upload.Id,
            item => item.State == SyncJobState.Discarded);

        Assert.Equal(2, context.Content.DeleteCalls);
        Assert.False(context.Content.HasRemoteItem(context.Route, delete.RelativePath));
        Assert.False(context.Content.IsDeleteTombstoned(context.Route, delete.RelativePath));
        Assert.False(File.Exists(discardedUpload.PayloadPath));
    }

    [Fact]
    public async Task UnavailableVerificationResetsDeleteAbsenceQuiescence()
    {
        await using var context = await QueueContext.CreateAsync();
        var verificationCalls = 0;
        context.Content.VerifyHandler = (_, _, _) =>
        {
            var call = Interlocked.Increment(ref verificationCalls);
            return Task.FromResult(call == 3
                ? new RemoteUploadVerificationResult(
                    RemoteUploadVerificationState.Unavailable,
                    FailureKind: SyncFailureKind.Network,
                    TechnicalDetails: "verification interrupted")
                : new RemoteUploadVerificationResult(
                    RemoteUploadVerificationState.NotFound,
                    FailureKind: SyncFailureKind.None));
        };
        context.Content.UploadHandler = async (_, _, content, _, cancellationToken, progress) =>
        {
            var bytes = new byte[checked((int)content.Length)];
            await content.ReadExactlyAsync(bytes, cancellationToken);
            progress?.Report(new UploadTransferProgress(
                bytes.Length,
                bytes.Length,
                IsAcknowledged: false,
                MayHaveCommitted: true));
            return new UploadAttemptResult(
                UploadAttemptState.RetryableFailure,
                FailureKind: SyncFailureKind.Network,
                TechnicalDetails: "final PUT response was lost",
                IsCommitAmbiguous: true);
        };
        context.Content.DeleteHandler = (_, _, _, _) =>
            Task.FromResult(new RemoteDeleteAttemptResult(
                RemoteDeleteAttemptState.Succeeded,
                HttpStatusCode: 404));
        var upload = await context.Queue.EnqueueAsync(
            context.Route,
            "Reports/reset-delete-quiescence.txt",
            new MemoryStream("ambiguous payload"u8.ToArray()),
            null);
        context.Queue.Start();
        await context.WaitForJobAsync(
            upload.Id,
            item => item.State == SyncJobState.VerifyingRemote && item.Attempts == 1);

        var delete = await context.Queue.QueueDeleteAsync(
            context.Route,
            upload.RelativePath,
            isDirectory: false);
        var firstAbsence = await context.WaitForJobAsync(
            delete.Id,
            item => item.State == SyncJobState.Waiting &&
                    item.DeleteBarrierObservedAt is not null &&
                    context.Content.DeleteCalls == 1);
        firstAbsence.DeleteBarrierObservedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await context.Database.UpdateSyncJobAsync(firstAbsence);

        await context.Queue.RetryNowAsync(delete.Id);
        var interrupted = await context.WaitForJobAsync(
            delete.Id,
            item => item.State == SyncJobState.Waiting &&
                    item.DeleteBarrierObservedAt is null &&
                    context.Content.DeleteCalls == 2);
        Assert.Equal(SyncWaitReason.RemoteVerification, interrupted.WaitReason);

        await context.Queue.RetryNowAsync(delete.Id);
        var restartedWindow = await context.WaitForJobAsync(
            delete.Id,
            item => item.State == SyncJobState.Waiting &&
                    item.DeleteBarrierObservedAt is not null &&
                    context.Content.DeleteCalls == 3);

        Assert.True(
            restartedWindow.DeleteBarrierObservedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.True(context.Content.IsDeleteTombstoned(context.Route, delete.RelativePath));
        Assert.Equal(
            SyncJobState.VerifyingRemote,
            (await context.Database.GetSyncJobAsync(upload.Id))!.State);
    }

    [Fact]
    public async Task TerminalDeleteFailureIsDurableBlocksUploadAndCanBeRetried()
    {
        await using var context = await QueueContext.CreateAsync();
        context.Content.SetRemoteItem(
            context.Route,
            "Reports/protected-delete.txt",
            length: 10,
            modifiedAt: DateTimeOffset.UtcNow);
        context.Content.DeleteHandler = (_, _, _, _) =>
            Task.FromResult(new RemoteDeleteAttemptResult(
                RemoteDeleteAttemptState.TerminalFailure,
                SyncFailureKind.Permission,
                "forbidden",
                "HTTP 403",
                403));
        var delete = await context.Queue.QueueDeleteAsync(
            context.Route,
            "Reports/protected-delete.txt",
            isDirectory: false);
        context.Queue.Start();
        var failed = await context.WaitForJobAsync(
            delete.Id,
            item => item.State == SyncJobState.Failed);

        Assert.Equal(SyncOperationKind.Delete, failed.OperationKind);
        Assert.Equal(SyncFailureKind.Permission, failed.FailureKind);
        Assert.Equal("SyncDeleteFailurePermission", failed.FailureSummary);
        Assert.Contains("HTTP 403", failed.TechnicalDetails);
        Assert.True(failed.CanRetry);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Queue.EnqueueAsync(
                context.Route,
                failed.RelativePath,
                new MemoryStream("must remain blocked"u8.ToArray()),
                null));

        context.Content.DeleteHandler = (route, relativePath, _, _) =>
        {
            context.Content.RemoveRemoteItem(route, relativePath);
            return Task.FromResult(new RemoteDeleteAttemptResult(
                RemoteDeleteAttemptState.Succeeded,
                HttpStatusCode: 204));
        };
        await context.Queue.RetryNowAsync(delete.Id);
        var completed = await context.WaitForJobAsync(
            delete.Id,
            item => item.State == SyncJobState.Completed);

        Assert.Equal(2, context.Content.DeleteCalls);
        Assert.Equal("SyncDeleteCompleted", completed.StateText);
        Assert.False(context.Content.HasRemoteItem(context.Route, delete.RelativePath));
    }

    [Fact]
    public async Task PendingDeleteResumesAfterQueueRestart()
    {
        await using var context = await QueueContext.CreateAsync();
        context.Content.SetRemoteItem(
            context.Route,
            "Reports/restart-delete.txt",
            length: 12,
            modifiedAt: DateTimeOffset.UtcNow);
        var delete = await context.Queue.QueueDeleteAsync(
            context.Route,
            "Reports/restart-delete.txt",
            isDirectory: false);
        context.Queue.Dispose();

        using var reopened = new UploadQueueService(
            context.Database,
            context.Content,
            context.Paths,
            context.Storage);
        reopened.Start();
        var completed = await context.WaitForJobAsync(
            delete.Id,
            item => item.State == SyncJobState.Completed);

        Assert.Equal(SyncOperationKind.Delete, completed.OperationKind);
        Assert.Equal(1, context.Content.DeleteCalls);
        Assert.False(context.Content.HasRemoteItem(context.Route, delete.RelativePath));
    }

    [Fact]
    public async Task PreparedDeleteDoesNotRunBeforeCleanupArmAndCanBeCanceled()
    {
        await using var context = await QueueContext.CreateAsync();
        const string path = "Reports/cancel-staged-delete.txt";
        context.Content.SetRemoteItem(
            context.Route,
            path,
            length: 12,
            modifiedAt: DateTimeOffset.UtcNow);
        context.Queue.Start();
        var prepared = await context.Queue.PrepareDeleteIntentAsync(
            context.Route,
            path,
            isDirectory: false);

        Assert.False(prepared.DeleteArmed);
        Assert.True(context.Content.IsDeleteTombstoned(context.Route, path));
        await Task.Delay(250);

        Assert.Equal(0, context.Content.DeleteCalls);
        Assert.True(context.Content.HasRemoteItem(context.Route, path));
        Assert.True(await context.Queue.CancelDeleteIntentAsync(prepared.Id));
        var canceled = await context.WaitForJobAsync(
            prepared.Id,
            item => item.State == SyncJobState.Discarded);

        Assert.False(canceled.DeleteArmed);
        Assert.False(context.Content.IsDeleteTombstoned(context.Route, path));
        Assert.Equal(0, context.Content.DeleteCalls);
        Assert.True(context.Content.HasRemoteItem(context.Route, path));

        context.Queue.Dispose();
        using var reopened = new UploadQueueService(
            context.Database,
            context.Content,
            context.Paths,
            context.Storage);
        reopened.Start();
        await Task.Delay(250);

        Assert.Equal(
            SyncJobState.Discarded,
            (await context.Database.GetSyncJobAsync(prepared.Id))!.State);
        Assert.Equal(0, context.Content.DeleteCalls);
        Assert.True(context.Content.HasRemoteItem(context.Route, path));
    }

    [Fact]
    public async Task PreparedDeleteExecutesOnlyAfterCleanupArmIsDurable()
    {
        await using var context = await QueueContext.CreateAsync();
        const string path = "Reports/arm-staged-delete.txt";
        context.Content.SetRemoteItem(
            context.Route,
            path,
            length: 12,
            modifiedAt: DateTimeOffset.UtcNow);
        context.Queue.Start();
        var prepared = await context.Queue.PrepareDeleteIntentAsync(
            context.Route,
            path,
            isDirectory: false);
        await Task.Delay(250);
        Assert.Equal(0, context.Content.DeleteCalls);

        Assert.True(await context.Queue.ArmDeleteIntentAsync(prepared.Id));
        var durableArm = await context.Database.GetSyncJobAsync(prepared.Id);
        Assert.NotNull(durableArm);
        Assert.True(durableArm!.DeleteArmed);
        var completed = await context.WaitForJobAsync(
            prepared.Id,
            item => item.State == SyncJobState.Completed);

        Assert.True(completed.DeleteArmed);
        Assert.Equal(1, context.Content.DeleteCalls);
        Assert.False(context.Content.HasRemoteItem(context.Route, path));
    }

    [Fact]
    public async Task CrashBeforeCleanupArmsAndResumesAcceptedDelete()
    {
        await using var context = await QueueContext.CreateAsync();
        const string path = "Reports/crash-before-cleanup.txt";
        context.Content.SetRemoteItem(
            context.Route,
            path,
            length: 12,
            modifiedAt: DateTimeOffset.UtcNow);
        context.Queue.Start();
        var prepared = await context.Queue.PrepareDeleteIntentAsync(
            context.Route,
            path,
            isDirectory: false);
        context.Queue.Dispose();

        using var reopened = new UploadQueueService(
            context.Database,
            context.Content,
            context.Paths,
            context.Storage);
        reopened.Start();
        var recovered = await context.WaitForJobAsync(
            prepared.Id,
            item => item.State == SyncJobState.Completed);

        Assert.True(recovered.DeleteArmed);
        Assert.Equal(1, context.Content.DeleteCalls);
        Assert.False(context.Content.HasRemoteItem(context.Route, path));
        Assert.False(context.Content.IsDeleteTombstoned(context.Route, path));
    }

    [Fact]
    public async Task FailedLocalAdmissionCannotCreateAnExecutableDelete()
    {
        await using var context = await QueueContext.CreateAsync();
        await context.Queue.StopAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Queue.PrepareDeleteIntentAsync(
                context.Route,
                "Reports/not-admitted.txt",
                isDirectory: false));

        Assert.DoesNotContain(
            await context.Database.GetSyncJobsAsync(),
            job => job.OperationKind == SyncOperationKind.Delete);
        Assert.Equal(0, context.Content.DeleteCalls);
    }

    [Fact]
    public async Task NonSeekablePayloadIsHonestlyIndeterminateWhilePersisting()
    {
        await using var context = await QueueContext.CreateAsync();
        await using var payload = new BlockingNonSeekableStream("unknown length"u8.ToArray());

        var enqueue = context.Queue.EnqueueAsync(
            context.Route,
            "unknown-length.txt",
            payload,
            null);
        await payload.ReadStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var persisting = Assert.Single(await context.Database.GetSyncJobsAsync());

        Assert.Equal(SyncJobState.PersistingLocal, persisting.State);
        Assert.Null(persisting.PayloadLength);
        Assert.True(persisting.IsProgressIndeterminate);
        Assert.Equal(0, persisting.Progress);
        Assert.Equal("SyncProgressIndeterminate", persisting.ProgressText);

        payload.Release();
        var stored = await enqueue;
        Assert.Equal(SyncJobState.StoredLocally, stored.State);
        Assert.False(stored.IsProgressIndeterminate);
        Assert.Equal("unknown length"u8.Length, stored.PayloadLength);
    }

    [Fact]
    public async Task SixthFailureStopsWithProtectedCopyAndFriendlyTaxonomy()
    {
        await using var context = await QueueContext.CreateAsync();
        context.Content.UploadHandler = (_, _, _, _, _, _) =>
            Task.FromResult(new UploadAttemptResult(
                UploadAttemptState.RetryableFailure,
                Error: "raw network exception access_token=super-secret",
                FailureKind: SyncFailureKind.Network,
                TechnicalDetails: "access_token=super-secret"));
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "Reports/retry.txt",
            new MemoryStream("protected payload"u8.ToArray()),
            null);

        context.Queue.Start();
        for (var expectedAttempt = 1; expectedAttempt <= 6; expectedAttempt++)
        {
            var current = await context.WaitForJobAsync(
                job.Id,
                item => item.Attempts >= expectedAttempt);
            if (expectedAttempt < 6)
            {
                Assert.Equal(SyncJobState.Waiting, current.State);
                context.Queue.SignalConnectivityRestored();
            }
        }

        var failed = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Failed);
        Assert.Equal(6, failed.Attempts);
        Assert.Equal("SyncFailureStoppedAfterSix", failed.FailureSummary);
        Assert.DoesNotContain("raw network", failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", failed.TechnicalDetails);
        Assert.True(failed.CanRetry);
        Assert.True(failed.CanExport);
        Assert.True(File.Exists(failed.PayloadPath));
    }

    [Fact]
    public async Task RetryNowPreservesAttemptHistory()
    {
        await using var context = await QueueContext.CreateAsync();
        context.Content.UploadHandler = (_, _, _, _, _, _) =>
            Task.FromResult(new UploadAttemptResult(
                UploadAttemptState.RetryableFailure,
                FailureKind: SyncFailureKind.Network));
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "retry-now.txt",
            new MemoryStream("retry"u8.ToArray()),
            null);
        context.Queue.Start();
        var waiting = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Waiting && item.Attempts == 1);

        context.Content.UploadHandler = null;
        await context.Queue.RetryNowAsync(waiting.Id);
        var completed = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Completed);

        Assert.Equal(1, completed.Attempts);
    }

    [Theory]
    [InlineData(SyncFailureKind.Integrity)]
    [InlineData(SyncFailureKind.PayloadUnavailable)]
    public async Task FatalLocalFailuresCannotBeRetried(SyncFailureKind failureKind)
    {
        await using var context = await QueueContext.CreateAsync();
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            $"fatal-{failureKind}.txt",
            new MemoryStream("do not retry"u8.ToArray()),
            null);
        job.State = SyncJobState.Failed;
        job.Attempts = 2;
        job.FailureKind = failureKind;
        job.UserMessage = "fatal local failure";
        job.LastError = job.UserMessage;
        await context.Database.UpdateSyncJobAsync(job);

        await context.Queue.RetryNowAsync(job.Id);
        var preserved = await context.Database.GetSyncJobAsync(job.Id);

        Assert.NotNull(preserved);
        Assert.False(preserved!.CanRetry);
        Assert.Equal(SyncJobState.Failed, preserved.State);
        Assert.Equal(failureKind, preserved.FailureKind);
        Assert.Equal(2, preserved.Attempts);
        Assert.Null(preserved.NextAttemptAt);
        Assert.Equal(0, context.Content.UploadCalls);
    }

    [Fact]
    public async Task ConnectivitySignalImmediatelyResumesTerminalNetworkFailureWithoutResettingAttempts()
    {
        await using var context = await QueueContext.CreateAsync();
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "network-restored.txt",
            new MemoryStream("network"u8.ToArray()),
            null);
        job.State = SyncJobState.Failed;
        job.Attempts = 6;
        job.FailureKind = SyncFailureKind.Network;
        job.UserMessage = "offline";
        job.LastError = job.UserMessage;
        await context.Database.UpdateSyncJobAsync(job);
        context.Queue.Start();

        context.Queue.SignalConnectivityRestored();
        var completed = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Completed);

        Assert.Equal(6, completed.Attempts);
        Assert.Equal(1, context.Content.UploadCalls);
    }

    [Fact]
    public async Task SessionSignalImmediatelyResumesScheduledSessionFailureWithoutResettingAttempts()
    {
        await using var context = await QueueContext.CreateAsync();
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "session-restored.txt",
            new MemoryStream("session"u8.ToArray()),
            null);
        job.State = SyncJobState.Waiting;
        job.Attempts = 3;
        job.FailureKind = SyncFailureKind.Session;
        job.WaitReason = SyncWaitReason.Session;
        job.NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1);
        job.UserMessage = "sign in";
        job.LastError = job.UserMessage;
        await context.Database.UpdateSyncJobAsync(job);
        context.Queue.Start();

        context.Queue.SignalSessionRestored();
        var completed = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Completed);

        Assert.Equal(3, completed.Attempts);
        Assert.Equal(1, context.Content.UploadCalls);
    }

    [Fact]
    public async Task StartupRecoversPersistingPayloadInsteadOfForgettingIt()
    {
        await using var context = await QueueContext.CreateAsync();
        var payloadPath = context.Storage.CreatePayloadPath();
        await context.Storage.StoreAsync(payloadPath, "crash-safe"u8.ToArray());
        var job = new SyncJob
        {
            RouteId = context.Route.Id,
            OperationKey = SyncJob.CreateOperationKey(context.Route.Id, "crash-safe.txt"),
            FileName = "crash-safe.txt",
            RouteDisplayName = context.Route.DisplayName,
            RelativePath = "crash-safe.txt",
            PayloadPath = payloadPath,
            State = SyncJobState.PersistingLocal,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await context.Database.AddSyncJobAsync(job);

        context.Queue.Start();
        var completed = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Completed);

        Assert.NotNull(completed.StoredAt);
        Assert.Equal(64, completed.PayloadSha256.Length);
        Assert.Equal("crash-safe"u8.Length, completed.PayloadLength);
    }

    [Fact]
    public async Task UploadingAfterCrashReconcilesRemoteBeforeAnyReplay()
    {
        await using var context = await QueueContext.CreateAsync();
        var bytes = "already remote"u8.ToArray();
        var payloadPath = context.Storage.CreatePayloadPath();
        await context.Storage.StoreAsync(payloadPath, bytes);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var job = new SyncJob
        {
            RouteId = context.Route.Id,
            OperationKey = SyncJob.CreateOperationKey(context.Route.Id, "already.txt"),
            FileName = "already.txt",
            RouteDisplayName = context.Route.DisplayName,
            RelativePath = "already.txt",
            PayloadPath = payloadPath,
            PayloadLength = bytes.Length,
            State = SyncJobState.Uploading,
            BytesTransferred = bytes.Length,
            UploadMayHaveCommitted = true,
            UploadStartedAt = startedAt,
            StoredAt = startedAt.AddMinutes(-1),
            CreatedAt = startedAt.AddMinutes(-2),
            UpdatedAt = startedAt
        };
        await context.Database.AddSyncJobAsync(job);
        context.Content.SetRemoteItem(context.Route, job.RelativePath, bytes.Length, DateTimeOffset.UtcNow);

        context.Queue.Start();
        var completed = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Completed);

        Assert.Equal(0, context.Content.UploadCalls);
        Assert.NotNull(completed.RemoteConfirmedAt);
        Assert.False(File.Exists(payloadPath));
    }

    [Fact]
    public async Task UploadingWithoutCommitRiskReplaysSafelyAfterCrash()
    {
        await using var context = await QueueContext.CreateAsync();
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "partial-before-crash.txt",
            new MemoryStream("partial transfer"u8.ToArray()),
            null);
        job.State = SyncJobState.Uploading;
        job.UploadStartedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        job.BytesTransferred = 4;
        job.UploadMayHaveCommitted = false;
        job.UpdatedAt = job.UploadStartedAt.Value;
        await context.Database.UpdateSyncJobAsync(job);

        context.Queue.Start();
        var completed = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Completed);

        Assert.Equal(1, context.Content.UploadCalls);
        Assert.NotNull(completed.RemoteConfirmedAt);
    }

    [Fact]
    public async Task CommitRiskWithoutUploadStartCanNeverConfirmOrReplay()
    {
        await using var context = await QueueContext.CreateAsync();
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "legacy-ambiguous.txt",
            new MemoryStream("legacy ambiguous"u8.ToArray()),
            null);
        job.State = SyncJobState.Uploading;
        job.UploadStartedAt = null;
        job.BytesTransferred = job.PayloadLength ?? 0;
        job.UploadMayHaveCommitted = true;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await context.Database.UpdateSyncJobAsync(job);
        context.Content.VerifyHandler = (_, _, _) =>
            Task.FromResult(new RemoteUploadVerificationResult(
                RemoteUploadVerificationState.Confirmed,
                new RemoteUploadReceipt(
                    "unsafe",
                    "\"unsafe\"",
                    job.PayloadLength,
                    DateTimeOffset.UtcNow),
                SyncFailureKind.None));

        context.Queue.Start();
        var blocked = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.VerifyingRemote && item.Attempts == 1);

        Assert.Equal(0, context.Content.UploadCalls);
        Assert.Null(blocked.RemoteConfirmedAt);
        Assert.False(blocked.CanRetry);
        Assert.True(blocked.CanExport);
        Assert.Equal(SyncWaitReason.RemoteVerification, blocked.WaitReason);
    }

    [Fact]
    public async Task MissingVerificationAfterCommitNeverReplaysUploadAcrossRetryOrRestart()
    {
        await using var context = await QueueContext.CreateAsync();
        context.Content.VerifyHandler = (_, _, _) =>
            Task.FromResult(new RemoteUploadVerificationResult(
                RemoteUploadVerificationState.NotFound,
                FailureKind: SyncFailureKind.None));
        context.Content.UploadHandler = async (_, _, content, _, cancellationToken, progress) =>
        {
            var buffer = new byte[4096];
            long transferred = 0;
            while (true)
            {
                var read = await content.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                transferred += read;
                progress?.Report(new UploadTransferProgress(
                    transferred,
                    content.Length,
                    IsAcknowledged: false));
            }

            // Simulates a committed 2xx whose response did not include a receipt.
            return new UploadAttemptResult(
                UploadAttemptState.Succeeded,
                Receipt: null,
                FailureKind: SyncFailureKind.None);
        };
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "commit-without-receipt.txt",
            new MemoryStream("committed once"u8.ToArray()),
            null);
        // A stale local/directory cache must never substitute for the fresh
        // verification contract.
        context.Content.SetRemoteItem(
            context.Route,
            job.RelativePath,
            job.PayloadLength ?? 0,
            DateTimeOffset.UtcNow);
        context.Queue.Start();
        var firstVerification = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.VerifyingRemote && item.Attempts == 1);
        Assert.Equal(SyncWaitReason.RemoteVerification, firstVerification.WaitReason);
        Assert.Equal(1, context.Content.UploadCalls);

        await context.Queue.StopAsync();
        context.Queue.Dispose();
        using var reopened = new UploadQueueService(
            context.Database,
            context.Content,
            context.Paths,
            context.Storage);
        reopened.Start();
        await reopened.RetryNowAsync(job.Id);
        var secondVerification = await context.WaitForJobAsync(
            job.Id,
            item => item.Attempts >= 2);

        Assert.Equal(SyncJobState.VerifyingRemote, secondVerification.State);
        Assert.Equal(SyncWaitReason.RemoteVerification, secondVerification.WaitReason);
        Assert.Equal(1, context.Content.UploadCalls);
    }

    [Fact]
    public async Task RetryableFailureAfterFinalBufferOnlyVerifiesAcrossRetryAndRestart()
    {
        await using var context = await QueueContext.CreateAsync();
        context.Content.VerifyHandler = (_, _, _) =>
            Task.FromResult(new RemoteUploadVerificationResult(
                RemoteUploadVerificationState.NotFound,
                FailureKind: SyncFailureKind.None));
        context.Content.UploadHandler = async (_, _, content, _, cancellationToken, progress) =>
        {
            var bytes = new byte[checked((int)content.Length)];
            await content.ReadExactlyAsync(bytes, cancellationToken);
            progress?.Report(new UploadTransferProgress(
                bytes.Length,
                bytes.Length,
                IsAcknowledged: false,
                MayHaveCommitted: true));
            return new UploadAttemptResult(
                UploadAttemptState.RetryableFailure,
                Error: "connection dropped after request body",
                FailureKind: SyncFailureKind.Network,
                IsCommitAmbiguous: true);
        };
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "ambiguous-network-drop.txt",
            new MemoryStream("committed but response lost"u8.ToArray()),
            null);

        context.Queue.Start();
        var first = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.VerifyingRemote && item.Attempts == 1);
        Assert.True(first.UploadMayHaveCommitted);
        Assert.Equal(1, context.Content.UploadCalls);

        await context.Queue.RetryNowAsync(job.Id);
        await context.WaitForJobAsync(job.Id, item => item.Attempts >= 2);
        Assert.Equal(1, context.Content.UploadCalls);

        await context.Queue.StopAsync();
        context.Queue.Dispose();
        using var reopened = new UploadQueueService(
            context.Database,
            context.Content,
            context.Paths,
            context.Storage);
        reopened.Start();
        await reopened.RetryNowAsync(job.Id);
        var afterRestart = await context.WaitForJobAsync(job.Id, item => item.Attempts >= 3);

        Assert.Equal(SyncJobState.VerifyingRemote, afterRestart.State);
        Assert.Equal(SyncWaitReason.RemoteVerification, afterRestart.WaitReason);
        Assert.Equal(1, context.Content.UploadCalls);
    }

    [Fact]
    public async Task DefinitiveTransientResponseClearsCommitRiskAndAllowsReplay()
    {
        await using var context = await QueueContext.CreateAsync();
        context.Content.UploadHandler = async (_, _, content, _, cancellationToken, progress) =>
        {
            var bytes = new byte[checked((int)content.Length)];
            await content.ReadExactlyAsync(bytes, cancellationToken);
            progress?.Report(new UploadTransferProgress(
                bytes.Length,
                bytes.Length,
                IsAcknowledged: false,
                MayHaveCommitted: true));
            return new UploadAttemptResult(
                UploadAttemptState.RetryableFailure,
                Error: "HTTP 503",
                FailureKind: SyncFailureKind.ServiceBusy,
                IsCommitAmbiguous: false);
        };
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "definitive-503.txt",
            new MemoryStream("retry after response"u8.ToArray()),
            null);
        context.Queue.Start();
        var waiting = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Waiting && item.Attempts == 1);

        Assert.False(waiting.UploadMayHaveCommitted);
        context.Content.UploadHandler = null;
        await context.Queue.RetryNowAsync(job.Id);
        var completed = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Completed);

        Assert.Equal(2, context.Content.UploadCalls);
        Assert.NotNull(completed.RemoteConfirmedAt);
    }

    [Fact]
    public async Task EmptyPayloadPreflightFailureRetriesBecauseNoRequestRiskWasRecorded()
    {
        await using var context = await QueueContext.CreateAsync();
        context.Content.UploadHandler = (_, _, _, _, _, _) =>
            Task.FromResult(new UploadAttemptResult(
                UploadAttemptState.RetryableFailure,
                Error: "session unavailable before request",
                FailureKind: SyncFailureKind.Session));
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "empty-preflight.txt",
            new MemoryStream([]),
            null);
        context.Queue.Start();
        var waiting = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Waiting && item.Attempts == 1);

        Assert.False(waiting.UploadMayHaveCommitted);
        context.Content.UploadHandler = null;
        context.Queue.SignalSessionRestored();
        var completed = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Completed);

        Assert.Equal(2, context.Content.UploadCalls);
        Assert.Equal(0, completed.RemoteLength);
    }

    [Fact]
    public async Task EmptyPayloadAmbiguousRequestNeverReplays()
    {
        await using var context = await QueueContext.CreateAsync();
        context.Content.VerifyHandler = (_, _, _) =>
            Task.FromResult(new RemoteUploadVerificationResult(
                RemoteUploadVerificationState.NotFound,
                FailureKind: SyncFailureKind.None));
        context.Content.UploadHandler = (_, _, _, _, _, progress) =>
        {
            progress?.Report(new UploadTransferProgress(
                0,
                0,
                IsAcknowledged: false,
                MayHaveCommitted: true));
            return Task.FromResult(new UploadAttemptResult(
                UploadAttemptState.RetryableFailure,
                Error: "empty PUT response lost",
                FailureKind: SyncFailureKind.Network,
                IsCommitAmbiguous: true));
        };
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "empty-ambiguous.txt",
            new MemoryStream([]),
            null);
        context.Queue.Start();
        var verifying = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.VerifyingRemote && item.Attempts == 1);

        Assert.True(verifying.UploadMayHaveCommitted);
        await context.Queue.RetryNowAsync(job.Id);
        await context.WaitForJobAsync(job.Id, item => item.Attempts >= 2);
        Assert.Equal(1, context.Content.UploadCalls);
    }

    [Fact]
    public async Task StaleRemoteItemWithMatchingSizeNeverConfirmsInterruptedUpload()
    {
        await using var context = await QueueContext.CreateAsync();
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "stale-remote.txt",
            new MemoryStream("same size"u8.ToArray()),
            null);
        var uploadStartedAt = DateTimeOffset.UtcNow;
        job.State = SyncJobState.Uploading;
        job.UploadStartedAt = uploadStartedAt;
        job.UploadMayHaveCommitted = true;
        job.UpdatedAt = uploadStartedAt;
        await context.Database.UpdateSyncJobAsync(job);
        context.Content.VerifyHandler = (_, _, _) =>
            Task.FromResult(new RemoteUploadVerificationResult(
                RemoteUploadVerificationState.Confirmed,
                new RemoteUploadReceipt(
                    "old-item",
                    "\"old-etag\"",
                    job.PayloadLength,
                    uploadStartedAt.AddMinutes(-1)),
                SyncFailureKind.None));

        context.Queue.Start();
        var verifying = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.VerifyingRemote && item.Attempts == 1);

        Assert.Equal(0, context.Content.UploadCalls);
        Assert.Null(verifying.RemoteConfirmedAt);
        Assert.Null(verifying.RemoteLength);
        Assert.Equal(SyncWaitReason.RemoteVerification, verifying.WaitReason);
    }

    [Fact]
    public async Task StreamedProgressIsRealAndCappedUntilRemoteConfirmation()
    {
        await using var context = await QueueContext.CreateAsync();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Content.UploadHandler = async (route, path, content, _, cancellationToken, progress) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellationToken);
            var buffer = new byte[64 * 1024];
            var read = await content.ReadAsync(buffer, cancellationToken);
            progress?.Report(new UploadTransferProgress(read, content.Length, IsAcknowledged: false));
            observed.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            long transferred = read;
            while (true)
            {
                read = await content.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                transferred += read;
                progress?.Report(new UploadTransferProgress(
                    transferred,
                    content.Length,
                    IsAcknowledged: false));
            }

            var modifiedAt = DateTimeOffset.UtcNow;
            context.Content.SetRemoteItem(route, path, transferred, modifiedAt);
            return new UploadAttemptResult(
                UploadAttemptState.Succeeded,
                Receipt: new RemoteUploadReceipt("progress-item", "\"progress-etag\"", transferred, modifiedAt),
                FailureKind: SyncFailureKind.None);
        };
        var payload = Enumerable.Repeat((byte)0x5A, 256 * 1024).ToArray();
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "progress.bin",
            new MemoryStream(payload),
            null);
        context.Queue.Start();

        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var uploading = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Uploading && item.BytesTransferred > 0);
        Assert.InRange(uploading.Progress, 1, 99);
        Assert.Equal(64 * 1024, uploading.BytesTransferred);
        Assert.NotNull(uploading.BytesPerSecond);
        Assert.NotNull(uploading.EstimatedCompletionAt);
        Assert.Null(uploading.RemoteConfirmedAt);

        release.TrySetResult();
        var completed = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Completed);
        Assert.Equal(100, completed.Progress);
    }

    [Fact]
    public async Task DiscardIsTerminalWithoutPretendingRemoteCompletion()
    {
        await using var context = await QueueContext.CreateAsync();
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "discard.txt",
            new MemoryStream("discard me"u8.ToArray()),
            null);
        job.State = SyncJobState.Failed;
        job.FailureKind = SyncFailureKind.Network;
        job.UserMessage = "offline";
        job.LastError = job.UserMessage;
        await context.Database.UpdateSyncJobAsync(job);

        var result = await context.Queue.DiscardLocalPayloadAsync(job.Id);
        var discarded = Assert.IsType<SyncJob>(result.Job);

        Assert.Equal(SyncConflictActionStatus.Discarded, result.Status);
        Assert.Equal(SyncJobState.Discarded, discarded.State);
        Assert.Equal(0, discarded.Progress);
        Assert.Null(discarded.RemoteConfirmedAt);
        Assert.Null(discarded.RemoteLength);
        Assert.False(File.Exists(discarded.PayloadPath));
    }

    [Fact]
    public async Task ExportCreatesExplicitPlaintextCopyWithoutChangingProtectedOriginal()
    {
        await using var context = await QueueContext.CreateAsync();
        var plaintext = "keep the protected source"u8.ToArray();
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "export-source.txt",
            new MemoryStream(plaintext),
            null);
        job.State = SyncJobState.Failed;
        job.FailureKind = SyncFailureKind.Network;
        job.UserMessage = "offline";
        job.LastError = job.UserMessage;
        await context.Database.UpdateSyncJobAsync(job);
        var protectedBefore = await File.ReadAllBytesAsync(job.PayloadPath);
        var destination = Path.Combine(context.Paths.DataDirectory, "exported-copy.txt");

        var result = await context.Queue.ExportLocalPayloadAsync(job.Id, destination);
        var persisted = await context.Database.GetSyncJobAsync(job.Id);

        Assert.Equal(SyncConflictActionStatus.Exported, result.Status);
        Assert.Equal(destination, result.ExportedPath);
        Assert.Equal(plaintext, await File.ReadAllBytesAsync(destination));
        Assert.Equal(protectedBefore, await File.ReadAllBytesAsync(job.PayloadPath));
        Assert.NotNull(persisted);
        Assert.Equal(SyncJobState.Failed, persisted!.State);
        Assert.Equal(SyncFailureKind.Network, persisted.FailureKind);
        Assert.True(persisted.CanExport);
        Assert.True(File.Exists(persisted.PayloadPath));

        Assert.False(new SyncJob
        {
            State = SyncJobState.Failed,
            FailureKind = SyncFailureKind.Integrity
        }.CanExport);
        Assert.False(new SyncJob
        {
            State = SyncJobState.Failed,
            FailureKind = SyncFailureKind.PayloadUnavailable
        }.CanExport);
    }

    [Fact]
    public async Task ClearCompletedNeverDeletesDiscardedOrActiveRows()
    {
        await using var context = await QueueContext.CreateAsync();
        var completed = context.CreateJob("completed.txt", SyncJobState.Completed);
        var discarded = context.CreateJob("discarded.txt", SyncJobState.Discarded);
        var active = context.CreateJob("active.txt", SyncJobState.Waiting);
        await context.Database.AddSyncJobAsync(completed);
        await context.Database.AddSyncJobAsync(discarded);
        await context.Database.AddSyncJobAsync(active);

        var removed = await context.Queue.ClearCompletedAsync();
        var remaining = await context.Database.GetSyncJobsAsync();

        Assert.Equal(1, removed);
        Assert.DoesNotContain(remaining, item => item.Id == completed.Id);
        Assert.Contains(remaining, item => item.Id == discarded.Id);
        Assert.Contains(remaining, item => item.Id == active.Id);
    }

    [Fact]
    public async Task MarkRouteRemovedPreservesPayloadAndStopsFutureProcessing()
    {
        await using var context = await QueueContext.CreateAsync();
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "route-removed.txt",
            new MemoryStream("keep local"u8.ToArray()),
            null);

        var changed = await context.Queue.MarkRouteRemovedAsync(context.Route.Id);
        var failed = await context.Database.GetSyncJobAsync(job.Id);

        Assert.Equal(1, changed);
        Assert.NotNull(failed);
        Assert.Equal(SyncJobState.Failed, failed!.State);
        Assert.Equal(SyncFailureKind.RouteUnavailable, failed.FailureKind);
        Assert.False(failed.CanRetry);
        Assert.True(File.Exists(failed.PayloadPath));
        Assert.Equal(1, await context.Queue.GetActiveJobCountAsync(context.Route.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Queue.EnqueueAsync(
                context.Route,
                "blocked-after-removal.txt",
                new MemoryStream("blocked"u8.ToArray()),
                null));
    }

    [Fact]
    public async Task ExplicitRouteRemovalRollbackRestoresAdmissionAndRetryContract()
    {
        await using var context = await QueueContext.CreateAsync();
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "rollback-route-removal.txt",
            new MemoryStream("preserve"u8.ToArray()),
            null);
        await context.Queue.MarkRouteRemovedAsync(context.Route.Id);

        var restoredCount = await context.Queue.RestoreRouteAdmissionAsync(context.Route.Id);
        var restored = await context.Database.GetSyncJobAsync(job.Id);
        var accepted = await context.Queue.EnqueueAsync(
            context.Route,
            "accepted-after-rollback.txt",
            new MemoryStream("accepted"u8.ToArray()),
            null);

        Assert.Equal(1, restoredCount);
        Assert.NotNull(restored);
        Assert.Equal(SyncJobState.Failed, restored!.State);
        Assert.Equal(SyncFailureKind.Unknown, restored.FailureKind);
        Assert.True(restored.CanRetry);
        Assert.Equal(SyncJobState.StoredLocally, accepted.State);
    }

    [Fact]
    public async Task RouteRemovalCancelsAmbiguousUploadAndRetryOnlyVerifiesAfterExplicitRestore()
    {
        await using var context = await QueueContext.CreateAsync();
        var pending = await context.Queue.EnqueueAsync(
            context.Route,
            "pending-snapshot.txt",
            new MemoryStream("pending"u8.ToArray()),
            null);
        var active = await context.Queue.EnqueueAsync(
            context.Route,
            "active-removal.txt",
            new MemoryStream("may already be remote"u8.ToArray()),
            null);
        var finalBufferObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.Content.UploadHandler = async (_, _, content, _, cancellationToken, progress) =>
        {
            var bytes = new byte[checked((int)content.Length)];
            await content.ReadExactlyAsync(bytes, cancellationToken);
            progress?.Report(new UploadTransferProgress(
                bytes.Length,
                bytes.Length,
                IsAcknowledged: false,
                MayHaveCommitted: true));
            finalBufferObserved.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Route cancellation was not observed.");
        };
        context.Content.VerifyHandler = (_, _, _) =>
            Task.FromResult(new RemoteUploadVerificationResult(
                RemoteUploadVerificationState.NotFound,
                FailureKind: SyncFailureKind.None));
        context.Queue.Start();
        await finalBufferObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var changed = await context.Queue.MarkRouteRemovedAsync(context.Route.Id);
        var removedActive = await context.Database.GetSyncJobAsync(active.Id);
        var removedPending = await context.Database.GetSyncJobAsync(pending.Id);

        Assert.Equal(2, changed);
        Assert.NotNull(removedActive);
        Assert.NotNull(removedPending);
        Assert.Equal(SyncJobState.Failed, removedActive!.State);
        Assert.Equal(SyncWaitReason.RemoteVerification, removedActive.WaitReason);
        Assert.True(removedActive.UploadMayHaveCommitted);
        Assert.True(File.Exists(removedActive.PayloadPath));
        Assert.Equal(SyncJobState.Failed, removedPending!.State);
        Assert.Equal(1, context.Content.UploadCalls);

        await context.Queue.RetryNowAsync(active.Id);
        Assert.Equal(1, context.Content.UploadCalls);
        Assert.False((await context.Database.GetSyncJobAsync(active.Id))!.CanRetry);

        await context.Database.RemoveRouteAsync(context.Route.Id);
        await context.Database.AddRouteAsync(context.Route);
        Assert.Equal(2, await context.Queue.RestoreRouteAdmissionAsync(context.Route.Id));
        await context.Queue.RetryNowAsync(active.Id);
        var verifying = await context.WaitForJobAsync(
            active.Id,
            item => item.State == SyncJobState.VerifyingRemote && item.Attempts == 1);

        Assert.Equal(SyncWaitReason.RemoteVerification, verifying.WaitReason);
        Assert.Equal(1, context.Content.UploadCalls);
    }

    [Fact]
    public async Task StopBeforeRequestRiskReturnsToStoredAndReplaysOnceAfterRestart()
    {
        await using var context = await QueueContext.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Content.UploadHandler = async (_, _, _, _, cancellationToken, _) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancellation token was not observed.");
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        };
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "stop-safe.txt",
            new MemoryStream("stop safely"u8.ToArray()),
            null);
        context.Queue.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await context.Queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var durable = await context.Database.GetSyncJobAsync(job.Id);

        Assert.NotNull(durable);
        Assert.Equal(SyncJobState.StoredLocally, durable!.State);
        Assert.False(durable.UploadMayHaveCommitted);
        Assert.True(File.Exists(durable.PayloadPath));

        context.Queue.Dispose();
        var resumedContent = new SharePointBrowserContentService();
        using var reopened = new UploadQueueService(
            context.Database,
            resumedContent,
            context.Paths,
            context.Storage);
        reopened.Start();
        var completed = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Completed);

        Assert.Equal(1, resumedContent.UploadCalls);
        Assert.NotNull(completed.RemoteConfirmedAt);
    }

    [Fact]
    public async Task StopAfterFinalBufferOnlyVerifiesAfterRestart()
    {
        await using var context = await QueueContext.CreateAsync();
        var finalBufferObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.Content.UploadHandler = async (_, _, content, _, cancellationToken, progress) =>
        {
            var bytes = new byte[checked((int)content.Length)];
            await content.ReadExactlyAsync(bytes, cancellationToken);
            progress?.Report(new UploadTransferProgress(
                bytes.Length,
                bytes.Length,
                IsAcknowledged: false,
                MayHaveCommitted: true));
            finalBufferObserved.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation was not observed.");
        };
        var job = await context.Queue.EnqueueAsync(
            context.Route,
            "stop-after-final-buffer.txt",
            new MemoryStream("possibly committed"u8.ToArray()),
            null);
        context.Queue.Start();
        await finalBufferObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await context.Queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var durable = await context.Database.GetSyncJobAsync(job.Id);
        Assert.NotNull(durable);
        Assert.Equal(SyncJobState.VerifyingRemote, durable!.State);
        Assert.True(durable.UploadMayHaveCommitted);
        Assert.True(File.Exists(durable.PayloadPath));

        context.Queue.Dispose();
        var verifier = new SharePointBrowserContentService();
        verifier.SetRemoteItem(
            context.Route,
            job.RelativePath,
            job.PayloadLength ?? 0,
            DateTimeOffset.UtcNow.AddSeconds(1));
        using var reopened = new UploadQueueService(
            context.Database,
            verifier,
            context.Paths,
            context.Storage);
        reopened.Start();
        var completed = await context.WaitForJobAsync(
            job.Id,
            item => item.State == SyncJobState.Completed);

        Assert.Equal(0, verifier.UploadCalls);
        Assert.NotNull(completed.RemoteConfirmedAt);
    }

    [Fact]
    public async Task StopAsyncWaitsForInFlightPayloadCommitAndRejectsNewEnqueues()
    {
        await using var context = await QueueContext.CreateAsync();
        await using var payload = new BlockingNonSeekableStream("finish local commit"u8.ToArray());
        var enqueue = context.Queue.EnqueueAsync(
            context.Route,
            "in-flight-store.txt",
            payload,
            null);
        await payload.ReadStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var persisting = Assert.Single(await context.Database.GetSyncJobsAsync());
        Assert.Equal(SyncJobState.PersistingLocal, persisting.State);

        var stop = context.Queue.StopAsync();
        await Task.Delay(100);
        Assert.False(stop.IsCompleted);

        payload.Release();
        var stored = await enqueue.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        var durable = await context.Database.GetSyncJobAsync(stored.Id);

        Assert.NotNull(durable);
        Assert.Equal(SyncJobState.StoredLocally, durable!.State);
        Assert.True(File.Exists(durable.PayloadPath));

        using var rejectedPayload = new MemoryStream("rejected"u8.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Queue.EnqueueAsync(
                context.Route,
                "after-stop.txt",
                rejectedPayload,
                null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Queue.RetryNowAsync(stored.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Queue.ExportLocalPayloadAsync(
                stored.Id,
                Path.Combine(context.Paths.DataDirectory, "after-stop-export.txt")));
        Assert.Single(await context.Database.GetSyncJobsAsync());
    }

    private sealed class QueueContext : IAsyncDisposable
    {
        private readonly string _root;

        private QueueContext(
            string root,
            AppDataPaths paths,
            LocalDatabase database,
            UploadPayloadStorage storage,
            SharePointBrowserContentService content,
            UploadQueueService queue,
            DriveRoute route)
        {
            _root = root;
            Paths = paths;
            Database = database;
            Storage = storage;
            Content = content;
            Queue = queue;
            Route = route;
        }

        public AppDataPaths Paths { get; }

        public LocalDatabase Database { get; }

        public UploadPayloadStorage Storage { get; }

        public SharePointBrowserContentService Content { get; }

        public UploadQueueService Queue { get; }

        public DriveRoute Route { get; }

        public static async Task<QueueContext> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"EasyShareUploadQueueLifecycle-{Guid.NewGuid():N}");
            var paths = new AppDataPaths(Path.Combine(root, "Data"));
            var database = new LocalDatabase(paths);
            await database.InitializeAsync();
            var route = new DriveRoute
            {
                DisplayName = "Documents",
                SharePointUrl = "https://contoso.sharepoint.com/sites/team",
                RemotePath = "/Documents",
                IsConnected = true
            };
            await database.AddRouteAsync(route);
            var storage = new UploadPayloadStorage(
                paths,
                protector: new TestUserDataProtector());
            var content = new SharePointBrowserContentService();
            var queue = new UploadQueueService(database, content, paths, storage);
            return new QueueContext(root, paths, database, storage, content, queue, route);
        }

        public SyncJob CreateJob(string path, SyncJobState state)
        {
            var now = DateTimeOffset.UtcNow;
            return new SyncJob
            {
                RouteId = Route.Id,
                OperationKey = SyncJob.CreateOperationKey(Route.Id, path),
                FileName = Path.GetFileName(path),
                RouteDisplayName = Route.DisplayName,
                RelativePath = path,
                State = state,
                Progress = state == SyncJobState.Completed ? 100 : 0,
                CreatedAt = now,
                CompletedAt = state is SyncJobState.Completed or SyncJobState.Discarded ? now : null,
                UpdatedAt = now
            };
        }

        public async Task<SyncJob> WaitForJobAsync(
            Guid jobId,
            Func<SyncJob, bool> predicate,
            TimeSpan? timeout = null)
        {
            var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
            while (DateTimeOffset.UtcNow < deadline)
            {
                var job = await Database.GetSyncJobAsync(jobId);
                if (job is not null && predicate(job))
                {
                    return job;
                }

                await Task.Delay(25);
            }

            var latest = await Database.GetSyncJobAsync(jobId);
            throw new TimeoutException(
                $"Job {jobId} did not reach the expected state. Latest: " +
                $"{latest?.State}, attempts={latest?.Attempts}, error={latest?.FailureSummary}");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Queue.StopAsync();
            }
            catch
            {
            }

            Queue.Dispose();
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
    }

    private sealed class TestUserDataProtector : IUserDataProtector
    {
        private static readonly byte[] Prefix = "test-protected:"u8.ToArray();

        public byte[] Protect(ReadOnlySpan<byte> plaintext) =>
            Prefix.Concat(plaintext.ToArray()).ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
        {
            Assert.True(protectedData.StartsWith(Prefix));
            return protectedData[Prefix.Length..].ToArray();
        }
    }

    private sealed class BlockingNonSeekableStream(byte[] payload) : Stream
    {
        private readonly TaskCompletionSource _readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            var count = Math.Min(payload.Length, buffer.Length);
            payload.AsSpan(0, count).CopyTo(buffer.Span);
            _returnedPayload = true;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
