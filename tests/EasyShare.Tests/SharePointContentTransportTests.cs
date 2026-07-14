using System.Net;
using System.Net.Http.Headers;
using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class SharePointContentTransportTests
{
    private static readonly Uri RequestUri = new("https://contoso.sharepoint.com/sites/team/_api/test");

    [Fact]
    [Trait("Gate", "Security")]
    public async Task RetriesTransientResponseAndUsesRetryAfter()
    {
        var requests = 0;
        using var transport = CreateTransport(
            (_, _) =>
            {
                requests++;
                if (requests == 1)
                {
                    var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                    return Task.FromResult(throttled);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("ready")
                });
            },
            maximumRetries: 1);

        var body = await SendGetAsync(
            transport,
            RequestUri,
            retryable: true,
            (response, token) => response.Content.ReadAsStringAsync(token));

        Assert.Equal("ready", body);
        Assert.Equal(2, requests);
    }

    [Fact]
    [Trait("Gate", "Security")]
    public async Task CapsUntrustedRetryAfterValue()
    {
        var requests = 0;
        using var transport = CreateTransport(
            (_, _) =>
            {
                requests++;
                if (requests == 1)
                {
                    var throttled = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(1));
                    return Task.FromResult(throttled);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            },
            maximumRetries: 1,
            maximumRetryDelay: TimeSpan.Zero);

        var status = await SendGetAsync(
            transport,
            RequestUri,
            retryable: true,
            (response, _) => Task.FromResult(response.StatusCode));

        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Equal(2, requests);
    }

    [Fact]
    [Trait("Gate", "Security")]
    public async Task RefusesCrossOriginRedirectWithoutLeakingHeaders()
    {
        var requestedUris = new List<Uri>();
        using var transport = CreateTransport(
            (request, _) =>
            {
                requestedUris.Add(Assert.IsType<Uri>(request.RequestUri));
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://attacker.example/collect");
                return Task.FromResult(response);
            });

        var status = await SendGetAsync(
            transport,
            RequestUri,
            retryable: true,
            (response, _) => Task.FromResult(response.StatusCode));

        Assert.Equal(HttpStatusCode.Redirect, status);
        Assert.Equal([RequestUri], requestedUris);
    }

    [Fact]
    [Trait("Gate", "Security")]
    public async Task FollowsOnlySameOriginGetRedirect()
    {
        var requestedUris = new List<Uri>();
        using var transport = CreateTransport(
            (request, _) =>
            {
                var uri = Assert.IsType<Uri>(request.RequestUri);
                requestedUris.Add(uri);
                if (uri.AbsolutePath.EndsWith("/_api/test", StringComparison.Ordinal))
                {
                    var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                    response.Headers.Location = new Uri("next", UriKind.Relative);
                    return Task.FromResult(response);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });

        var status = await SendGetAsync(
            transport,
            RequestUri,
            retryable: true,
            (response, _) => Task.FromResult(response.StatusCode));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            [RequestUri, new Uri("https://contoso.sharepoint.com/sites/team/_api/next")],
            requestedUris);
    }

    [Fact]
    [Trait("Gate", "Security")]
    public async Task DoesNotRetryWriteUnlessCallerMarksItReplayable()
    {
        var requests = 0;
        using var transport = CreateTransport(
            (_, _) =>
            {
                requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            },
            maximumRetries: 2);

        var status = await transport.SendAsync(
            RequestUri,
            uri => new HttpRequestMessage(HttpMethod.Post, uri),
            HttpCompletionOption.ResponseHeadersRead,
            retryable: false,
            (response, _) => Task.FromResult(response.StatusCode));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(1, requests);
    }

    [Fact]
    [Trait("Gate", "Security")]
    public async Task DoesNotFollowRedirectForWriteEvenOnSameOrigin()
    {
        var requests = 0;
        using var transport = CreateTransport(
            (_, _) =>
            {
                requests++;
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                response.Headers.Location = new Uri("next", UriKind.Relative);
                return Task.FromResult(response);
            });

        var status = await transport.SendAsync(
            RequestUri,
            uri => new HttpRequestMessage(HttpMethod.Post, uri),
            HttpCompletionOption.ResponseHeadersRead,
            retryable: true,
            (response, _) => Task.FromResult(response.StatusCode));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, status);
        Assert.Equal(1, requests);
    }

    [Fact]
    [Trait("Gate", "Security")]
    public async Task AppliesTimeoutToTheWholeResponseConsumer()
    {
        using var transport = CreateTransport(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            requestTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            SendGetAsync(
                transport,
                RequestUri,
                retryable: false,
                async (_, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return true;
                }));
    }

    [Fact]
    public async Task PropagatesCallerCancellationWithoutConvertingItToTimeout()
    {
        using var transport = CreateTransport(
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            requestTimeout: TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SendGetAsync(
                transport,
                RequestUri,
                retryable: false,
                (response, _) => Task.FromResult(response.StatusCode),
                cancellation.Token));
    }

    [Fact]
    public async Task HoldsConcurrencyLeaseUntilStreamConsumerFinishes()
    {
        var handlerRequests = 0;
        var consumerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConsumer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = CreateTransport(
            (_, _) =>
            {
                Interlocked.Increment(ref handlerRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            },
            maximumConcurrentRequests: 1,
            requestTimeout: TimeSpan.FromSeconds(5));

        var first = SendGetAsync(
            transport,
            RequestUri,
            retryable: false,
            async (response, token) =>
            {
                consumerEntered.TrySetResult();
                await releaseConsumer.Task.WaitAsync(token);
                return response.StatusCode;
            });
        await consumerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                SendGetAsync(
                    transport,
                    new Uri(RequestUri, "queued"),
                    retryable: false,
                    (response, _) => Task.FromResult(response.StatusCode),
                    cancellation.Token));

            Assert.Equal(1, Volatile.Read(ref handlerRequests));
        }
        finally
        {
            releaseConsumer.TrySetResult();
            await first;
        }
    }

    private static RemoteHttpTransport CreateTransport(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        int maximumRetries = 0,
        int maximumConcurrentRequests = 4,
        TimeSpan? requestTimeout = null,
        TimeSpan? maximumRetryDelay = null) =>
        new(
            new StubHttpMessageHandler(handler),
            new RemoteHttpTransportOptions
            {
                BaseRetryDelay = TimeSpan.Zero,
                MaximumRetryDelay = maximumRetryDelay ?? TimeSpan.FromMilliseconds(10),
                MaximumRetries = maximumRetries,
                MaximumRedirects = 3,
                MaximumConcurrentRequests = maximumConcurrentRequests,
                RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(2)
            });

    private static Task<T> SendGetAsync<T>(
        IRemoteHttpTransport transport,
        Uri uri,
        bool retryable,
        Func<HttpResponseMessage, CancellationToken, Task<T>> responseHandler,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync(
            uri,
            requestUri =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.TryAddWithoutValidation("Cookie", "FedAuth=secret");
                return request;
            },
            HttpCompletionOption.ResponseHeadersRead,
            retryable,
            responseHandler,
            cancellationToken);

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
