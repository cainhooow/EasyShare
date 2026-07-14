using System.Net;
using System.Text;
using EasyShare.Models;
using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class GraphSharePointContentServiceTests
{
    [Fact]
    public async Task ListsDirectoryAcrossPagesAndUsesStableGraphItemKeys()
    {
        using var client = new HttpClient(new AsyncCallbackHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/test/content-page-2", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""
                    {"value":[{"id":"file-id","name":"readme.txt","size":42,"lastModifiedDateTime":"2026-07-13T12:00:00Z","file":{}}]}
                    """));
            }

            return Task.FromResult(Json("""
                {"value":[{"id":"folder-id","name":"Reports","size":0,"lastModifiedDateTime":"2026-07-13T11:00:00Z","folder":{}}],
                 "@odata.nextLink":"https://graph.microsoft.com/v1.0/test/content-page-2"}
                """));
        }));
        var service = CreateService(client);

        var items = await service.ListDirectoryAsync(CreateRoute(), "Pinned");

        Assert.Equal(2, items.Count);
        Assert.Equal("Reports", items[0].Name);
        Assert.Equal("graph://drive-id/folder-id", items[0].ServerRelativeUrl);
        Assert.Equal(42, items[1].Length);
    }

    [Fact]
    public async Task DownloadCopiesTheResponseStreamToTheCallerDestination()
    {
        var payload = Enumerable.Range(0, 8192).Select(value => (byte)(value % 251)).ToArray();
        string? requestedPath = null;
        using var client = new HttpClient(new AsyncCallbackHandler((request, _) =>
        {
            requestedPath = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        }));
        var service = CreateService(client);
        using var destination = new MemoryStream();

        var succeeded = await service.DownloadFileAsync(CreateRoute("pinned-root"), "Reports/Q1.pdf", destination);

        Assert.True(succeeded);
        Assert.Equal(payload, destination.ToArray());
        Assert.Contains("/items/pinned-root:/Reports/Q1.pdf:/content", requestedPath);
    }

    [Fact]
    public async Task SimpleUploadStreamsContentAndDoesNotDisposeTheCallerStream()
    {
        var payload = "hello through graph"u8.ToArray();
        byte[]? uploaded = null;
        long? contentLength = null;
        using var client = new HttpClient(new AsyncCallbackHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            contentLength = request.Content?.Headers.ContentLength;
            uploaded = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }));
        var service = CreateService(client);
        var source = new TrackingMemoryStream(payload);

        var result = await service.TryUploadFileAsync(CreateRoute(), "notes.txt", source, null);

        Assert.Equal(UploadAttemptState.Succeeded, result.State);
        Assert.Equal(payload.Length, contentLength);
        Assert.Equal(payload, uploaded);
        Assert.False(source.WasDisposed);
        source.Dispose();
    }

    [Fact]
    public async Task ExpectedModifiedTimeDetectsAConflictBeforeUpload()
    {
        var putRequests = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                Interlocked.Increment(ref putRequests);
            }

            return Task.FromResult(Json("""
                {"id":"item-id","name":"notes.txt","size":5,"lastModifiedDateTime":"2026-07-13T12:00:00Z","file":{}}
                """));
        }));
        var service = CreateService(client);
        using var source = new MemoryStream("local"u8.ToArray());

        var result = await service.TryUploadFileAsync(
            CreateRoute(),
            "notes.txt",
            source,
            DateTimeOffset.Parse("2026-07-13T10:00:00Z"));

        Assert.Equal(UploadAttemptState.Conflict, result.State);
        Assert.Equal(0, putRequests);
    }

    [Fact]
    public async Task LargeUploadUsesChunkedSessionWithoutAuthorizationOnThePreauthenticatedUrl()
    {
        const long totalLength = 250L * 1024 * 1024 + 1;
        var ranges = new List<string>();
        using var client = new HttpClient(new AsyncCallbackHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "graph.microsoft.com")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                return Task.FromResult(Json("{\"uploadUrl\":\"https://contoso.sharepoint.com/upload/session-id\"}"));
            }

            Assert.Null(request.Headers.Authorization);
            var range = Assert.Single(request.Content!.Headers.GetValues("Content-Range"));
            ranges.Add(range);
            var isLast = range.Contains($"-{totalLength - 1}/{totalLength}", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(isLast ? HttpStatusCode.Created : HttpStatusCode.Accepted));
        }));
        var service = CreateService(client);
        using var source = new SparseReadStream(totalLength);

        var result = await service.TryUploadFileAsync(CreateRoute(), "large.bin", source, null);

        Assert.Equal(UploadAttemptState.Succeeded, result.State);
        Assert.Equal("bytes 0-10485759/262144001", ranges[0]);
        Assert.EndsWith($"-{totalLength - 1}/{totalLength}", ranges[^1], StringComparison.Ordinal);
        Assert.Equal(26, ranges.Count);
    }

    [Fact]
    public async Task UploadSessionRetriesThrottlingAndHonorsNextExpectedRanges()
    {
        const long totalLength = 15L * 1024 * 1024;
        var ranges = new List<string>();
        var uploadCalls = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "graph.microsoft.com")
            {
                return Task.FromResult(Json("{\"uploadUrl\":\"https://contoso.sharepoint.com/upload/resume\"}"));
            }

            uploadCalls++;
            ranges.Add(Assert.Single(request.Content!.Headers.GetValues("Content-Range")));
            if (uploadCalls == 1)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(throttled);
            }

            if (uploadCalls == 2)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent(
                        "{\"nextExpectedRanges\":[\"5242880-\"]}",
                        Encoding.UTF8,
                        "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }));
        var service = CreateService(client);
        using var source = new SparseReadStream(totalLength);

        var result = await service.TryUploadFileAsync(CreateRoute(), "resumable.bin", source, null);

        Assert.Equal(UploadAttemptState.Succeeded, result.State);
        Assert.Equal(3, uploadCalls);
        Assert.Equal(ranges[0], ranges[1]);
        Assert.StartsWith("bytes 5242880-", ranges[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadSessionCanResumeBackwardWithARestartOnlyDecryptionStream()
    {
        const long totalLength = 15L * 1024 * 1024;
        var uploadCalls = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "graph.microsoft.com")
            {
                return Task.FromResult(Json("{\"uploadUrl\":\"https://contoso.sharepoint.com/upload/restart\"}"));
            }

            uploadCalls++;
            return uploadCalls == 1
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent(
                        "{\"nextExpectedRanges\":[\"5242880-\"]}",
                        Encoding.UTF8,
                        "application/json")
                })
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }));
        var service = CreateService(client);
        using var source = new RestartOnlyReadStream(totalLength);

        var result = await service.TryUploadFileAsync(CreateRoute(), "restart.bin", source, null);

        Assert.Equal(UploadAttemptState.Succeeded, result.State);
        Assert.Equal(2, uploadCalls);
        Assert.Equal(1, source.RestartCount);
    }

    [Fact]
    public async Task CreateDeleteAndRenameUseGraphItemRoutes()
    {
        var calls = new List<(HttpMethod Method, string Path, string? Body)>();
        using var client = new HttpClient(new AsyncCallbackHandler(async (request, cancellationToken) =>
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            calls.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var service = CreateService(client);
        var route = CreateRoute();

        Assert.True(await service.CreateFolderAsync(route, "Projects/New"));
        Assert.True(await service.DeleteItemAsync(route, "Projects/Old.txt", false));
        Assert.True(await service.RenameItemAsync(route, "Projects/A.txt", "Projects/B.txt", false, false));

        Assert.Equal([HttpMethod.Post, HttpMethod.Delete, HttpMethod.Patch], calls.Select(call => call.Method));
        Assert.Contains("/root:/Projects:/children", calls[0].Path);
        Assert.Contains("\"name\":\"New\"", calls[0].Body);
        Assert.Contains("/root:/Projects/Old.txt:", calls[1].Path);
        Assert.Contains("\"name\":\"B.txt\"", calls[2].Body);
    }

    [Fact]
    [Trait("Gate", "Security")]
    public async Task RejectsRelativePathTraversalAcrossEveryContentOperationWithoutARequest()
    {
        var calls = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var service = CreateService(client);
        var route = CreateRoute();

        await Assert.ThrowsAsync<ArgumentException>(() => service.ListDirectoryAsync(route, "../escape"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetItemAsync(route, "%2e%2e/escape"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.DownloadFileAsync(route, "folder/../escape", new MemoryStream()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateFolderAsync(route, "../escape"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.TryUploadFileAsync(route, "../escape.txt", new MemoryStream([1]), null));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteItemAsync(route, "../escape", false));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RenameItemAsync(route, "../escape", "safe", false, false));

        Assert.Equal(0, calls);
    }

    [Fact]
    [Trait("Gate", "Security")]
    public async Task RejectsTamperedGraphIdentifiersWithoutARequest()
    {
        var calls = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var service = CreateService(client);
        var route = CreateRoute(rootItemId: "..");

        var exception = await Assert.ThrowsAsync<SharePointExplorerException>(() =>
            service.ListDirectoryAsync(route, string.Empty));

        Assert.Equal(SharePointExplorerStatus.InvalidResponse, exception.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task EmptyProtectedUploadUsesIfMatchAndCompletesWithoutAnUploadSession()
    {
        var calls = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((request, _) =>
        {
            calls++;
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Json("""
                    {"id":"item-id","name":"empty.txt","size":4,"lastModifiedDateTime":"2026-07-13T12:00:00Z","eTag":"\"etag-1\"","file":{}}
                    """));
            }

            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("\"etag-1\"", Assert.Single(request.Headers.IfMatch).Tag);
            Assert.Equal(0, request.Content!.Headers.ContentLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }));
        var service = CreateService(client);
        using var source = new MemoryStream();

        var result = await service.TryUploadFileAsync(
            CreateRoute(),
            "empty.txt",
            source,
            DateTimeOffset.Parse("2026-07-13T12:00:00Z"));

        Assert.Equal(UploadAttemptState.Succeeded, result.State);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ListFailureIsNotReportedAsAnEmptyDirectory()
    {
        using var client = new HttpClient(new AsyncCallbackHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        var service = CreateService(client);

        var exception = await Assert.ThrowsAsync<SharePointExplorerException>(() =>
            service.ListDirectoryAsync(CreateRoute(), string.Empty));

        Assert.Equal(SharePointExplorerStatus.Forbidden, exception.Status);
    }

    [Fact]
    public async Task UploadWithUnknownLengthFailsClearlyWithoutSendingARequest()
    {
        var calls = 0;
        using var client = new HttpClient(new AsyncCallbackHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }));
        var service = CreateService(client);
        using var source = new NonSeekableReadStream("payload"u8.ToArray());

        var result = await service.TryUploadFileAsync(CreateRoute(), "file.bin", source, null);

        Assert.Equal(UploadAttemptState.RetryableFailure, result.State);
        Assert.Contains("tamanho conhecido", result.Error);
        Assert.Equal(0, calls);
    }

    private static GraphSharePointContentService CreateService(HttpClient client) =>
        new(new FakeAuthentication("access-token"), client);

    private static DriveRoute CreateRoute(string rootItemId = "root") =>
        new()
        {
            SiteId = "site-id",
            DriveId = "drive-id",
            RootItemId = rootItemId
        };

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class FakeAuthentication(string? token) : IAuthenticationService
    {
        public Task<string?> GetAccessTokenAsync() => Task.FromResult(token);
    }

    private sealed class AsyncCallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public AsyncCallbackHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> callback)
            : this((request, cancellationToken) => Task.FromResult(callback(request, cancellationToken)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            callback(request, cancellationToken);
    }

    private sealed class TrackingMemoryStream(byte[] payload) : MemoryStream(payload, writable: false)
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class SparseReadStream(long length) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = (int)Math.Min(count, Length - Position);
            Position += available;
            return available;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = (int)Math.Min(buffer.Length, Length - Position);
            Position += available;
            return ValueTask.FromResult(available);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => Position
            };
            return Position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableReadStream(byte[] payload) : Stream
    {
        private readonly MemoryStream _inner = new(payload, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class RestartOnlyReadStream(long length) : Stream
    {
        private long _position;

        public int RestartCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value == _position)
                {
                    return;
                }

                if (value != 0)
                {
                    throw new NotSupportedException("Only restart-at-zero is supported.");
                }

                _position = 0;
                RestartCount++;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = (int)Math.Min(count, Length - _position);
            _position += available;
            return available;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = (int)Math.Min(buffer.Length, Length - _position);
            _position += available;
            return ValueTask.FromResult(available);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
