using System.Net;
using System.Security.Cryptography;
using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class UpdateDownloadClientTests
{
    private static readonly Uri InitialUri =
        new("https://github.com/cainhooow/EasyShare/releases/download/v1/EasyShareSetup.exe");

    [Fact]
    public async Task DownloadsAcrossTrustedRedirectAndVerifiesHash()
    {
        var payload = "signed installer payload"u8.ToArray();
        var targetPath = CreateTargetPath();
        using var httpClient = new HttpClient(new CallbackHandler(request =>
        {
            if (request.RequestUri == InitialUri)
            {
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://release-assets.githubusercontent.com/release/file") }
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        try
        {
            var client = new UpdateDownloadClient(httpClient, TimeSpan.FromSeconds(5));
            var bytes = await client.DownloadAsync(CreateRequest(targetPath, payload));

            Assert.Equal(payload.Length, bytes);
            Assert.Equal(payload, File.ReadAllBytes(targetPath));
            Assert.False(File.Exists($"{targetPath}.download"));
        }
        finally
        {
            DeleteTargetDirectory(targetPath);
        }
    }

    [Fact]
    public async Task RejectsUntrustedRedirectAndRemovesPartialState()
    {
        var payload = "payload"u8.ToArray();
        var targetPath = CreateTargetPath();
        using var httpClient = new HttpClient(new CallbackHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://evil.example/EasyShareSetup.exe") }
            }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        try
        {
            var client = new UpdateDownloadClient(httpClient);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.DownloadAsync(CreateRequest(targetPath, payload)));

            Assert.False(File.Exists(targetPath));
            Assert.False(File.Exists($"{targetPath}.download"));
        }
        finally
        {
            DeleteTargetDirectory(targetPath);
        }
    }

    [Fact]
    public async Task RejectsAnUntrustedFinalResponseOrigin()
    {
        var payload = "payload"u8.ToArray();
        var targetPath = CreateTargetPath();
        using var httpClient = new HttpClient(new CallbackHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/file"),
                Content = new ByteArrayContent(payload)
            }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        try
        {
            var client = new UpdateDownloadClient(httpClient);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.DownloadAsync(CreateRequest(targetPath, payload)));
        }
        finally
        {
            DeleteTargetDirectory(targetPath);
        }
    }

    [Fact]
    public async Task CancellationRemovesPartiallyDownloadedFile()
    {
        var payload = new byte[64];
        RandomNumberGenerator.Fill(payload);
        var targetPath = CreateTargetPath();
        using var httpClient = new HttpClient(new CallbackHandler(_ =>
        {
            var content = new StreamContent(new BlockingAfterFirstReadStream(payload));
            content.Headers.ContentLength = payload.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            var client = new UpdateDownloadClient(httpClient, TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.DownloadAsync(CreateRequest(targetPath, payload), cancellationToken: cancellationSource.Token));

            Assert.False(File.Exists(targetPath));
            Assert.False(File.Exists($"{targetPath}.download"));
        }
        finally
        {
            DeleteTargetDirectory(targetPath);
        }
    }

    [Fact]
    public async Task TimeoutRemovesPartiallyDownloadedFile()
    {
        var payload = new byte[64];
        RandomNumberGenerator.Fill(payload);
        var targetPath = CreateTargetPath();
        using var httpClient = new HttpClient(new CallbackHandler(_ =>
        {
            var content = new StreamContent(new BlockingAfterFirstReadStream(payload));
            content.Headers.ContentLength = payload.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        try
        {
            var client = new UpdateDownloadClient(httpClient, TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<TimeoutException>(() =>
                client.DownloadAsync(CreateRequest(targetPath, payload)));

            Assert.False(File.Exists(targetPath));
            Assert.False(File.Exists($"{targetPath}.download"));
        }
        finally
        {
            DeleteTargetDirectory(targetPath);
        }
    }

    private static UpdateDownloadRequest CreateRequest(string targetPath, byte[] payload) =>
        new(
            InitialUri,
            "cainhooow",
            "EasyShare",
            targetPath,
            payload.Length,
            Convert.ToHexString(SHA256.HashData(payload)));

    private static string CreateTargetPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EasyShareTests", Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "EasyShareSetup.exe");
    }

    private static void DeleteTargetDirectory(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }

    private sealed class BlockingAfterFirstReadStream(byte[] firstChunk) : Stream
    {
        private bool _firstRead = true;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => firstChunk.Length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_firstRead)
            {
                _firstRead = false;
                firstChunk.CopyTo(buffer);
                Position += firstChunk.Length;
                return ValueTask.FromResult(firstChunk.Length);
            }

            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
