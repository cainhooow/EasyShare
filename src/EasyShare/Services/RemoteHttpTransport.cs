using System.Net;
using System.Net.Http.Headers;

namespace EasyShare.Services;

public sealed class RemoteHttpTransportOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    public int MaximumRetries { get; init; } = 2;

    public int MaximumRedirects { get; init; } = 3;

    public int MaximumConcurrentRequests { get; init; } = 4;

    internal void Validate()
    {
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (BaseRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BaseRetryDelay));
        }

        if (MaximumRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRetryDelay));
        }

        if (MaximumRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRetries));
        }

        if (MaximumRedirects < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRedirects));
        }

        if (MaximumConcurrentRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentRequests));
        }
    }
}

public interface IRemoteHttpTransport
{
    Task<T> SendAsync<T>(
        Uri requestUri,
        Func<Uri, HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        bool retryable,
        Func<HttpResponseMessage, CancellationToken, Task<T>> responseHandler,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reuses one HTTP connection pool and bounds complete operations, including streamed
/// response consumption. Redirects are followed only for same-origin GET/HEAD requests.
/// </summary>
public sealed class RemoteHttpTransport : IRemoteHttpTransport, IDisposable
{
    private static readonly HttpStatusCode[] RedirectStatusCodes =
    [
        HttpStatusCode.MovedPermanently,
        HttpStatusCode.Redirect,
        HttpStatusCode.SeeOther,
        HttpStatusCode.TemporaryRedirect,
        HttpStatusCode.PermanentRedirect
    ];

    private readonly HttpClient _httpClient;
    private readonly RemoteHttpTransportOptions _options;
    private readonly SemaphoreSlim _requestGate;
    private bool _disposed;

    public RemoteHttpTransport(RemoteHttpTransportOptions? options = null)
        : this(CreateDefaultHandler(options ?? new RemoteHttpTransportOptions()), options)
    {
    }

    public RemoteHttpTransport(HttpMessageHandler handler, RemoteHttpTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _options = options ?? new RemoteHttpTransportOptions();
        _options.Validate();
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            // The linked per-operation token below also covers streamed content reads.
            Timeout = Timeout.InfiniteTimeSpan
        };
        _requestGate = new SemaphoreSlim(
            _options.MaximumConcurrentRequests,
            _options.MaximumConcurrentRequests);
    }

    public async Task<T> SendAsync<T>(
        Uri requestUri,
        Func<Uri, HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        bool retryable,
        Func<HttpResponseMessage, CancellationToken, Task<T>> responseHandler,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(requestUri);
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(responseHandler);
        ValidateAbsoluteHttpUri(requestUri, nameof(requestUri));

        var originalUri = requestUri;
        var currentUri = requestUri;
        var retryCount = 0;
        var redirectCount = 0;

        while (true)
        {
            Uri? redirectUri = null;
            TimeSpan? retryDelay = null;

            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(_options.RequestTimeout);

                using var request = requestFactory(currentUri);
                ValidateRequest(request, currentUri);

                try
                {
                    using var response = await _httpClient
                        .SendAsync(request, completionOption, timeoutSource.Token)
                        .ConfigureAwait(false);

                    if (redirectCount < _options.MaximumRedirects &&
                        TryGetSafeRedirectUri(originalUri, request, response, out var safeRedirectUri))
                    {
                        redirectUri = safeRedirectUri;
                    }
                    else if (retryable &&
                             retryCount < _options.MaximumRetries &&
                             IsTransient(response.StatusCode))
                    {
                        retryDelay = GetRetryDelay(response.Headers.RetryAfter, retryCount);
                    }
                    else
                    {
                        return await responseHandler(response, timeoutSource.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    if (!retryable || retryCount >= _options.MaximumRetries)
                    {
                        throw new TimeoutException(
                            $"The remote request exceeded the {_options.RequestTimeout.TotalSeconds:0.###} second timeout.",
                            exception);
                    }

                    retryDelay = GetRetryDelay(retryAfter: null, retryCount);
                }
                catch (HttpRequestException) when (retryable && retryCount < _options.MaximumRetries)
                {
                    retryDelay = GetRetryDelay(retryAfter: null, retryCount);
                }
            }
            finally
            {
                _requestGate.Release();
            }

            if (redirectUri is not null)
            {
                currentUri = redirectUri;
                redirectCount++;
                continue;
            }

            retryCount++;
            await Task.Delay(retryDelay ?? TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
        _requestGate.Dispose();
    }

    private static SocketsHttpHandler CreateDefaultHandler(RemoteHttpTransportOptions options)
    {
        options.Validate();
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = options.RequestTimeout < TimeSpan.FromSeconds(10)
                ? options.RequestTimeout
                : TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = options.MaximumConcurrentRequests,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            EnableMultipleHttp2Connections = true
        };
    }

    private static void ValidateRequest(HttpRequestMessage request, Uri expectedUri)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is null || !request.RequestUri.Equals(expectedUri))
        {
            throw new InvalidOperationException("The request factory must use the URI supplied by the transport.");
        }

        ValidateAbsoluteHttpUri(request.RequestUri, nameof(request));
    }

    private static void ValidateAbsoluteHttpUri(Uri uri, string parameterName)
    {
        if (!uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Only absolute HTTP or HTTPS URIs are supported.", parameterName);
        }
    }

    private static bool TryGetSafeRedirectUri(
        Uri originalUri,
        HttpRequestMessage request,
        HttpResponseMessage response,
        out Uri redirectUri)
    {
        redirectUri = default!;
        if (!RedirectStatusCodes.Contains(response.StatusCode) ||
            (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head) ||
            request.RequestUri is null ||
            response.Headers.Location is null)
        {
            return false;
        }

        var candidate = response.Headers.Location.IsAbsoluteUri
            ? response.Headers.Location
            : new Uri(request.RequestUri, response.Headers.Location);

        if (!candidate.IsAbsoluteUri ||
            (candidate.Scheme != Uri.UriSchemeHttps && candidate.Scheme != Uri.UriSchemeHttp) ||
            !HasSameOrigin(originalUri, candidate) ||
            (originalUri.Scheme == Uri.UriSchemeHttps && candidate.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        redirectUri = candidate;
        return true;
    }

    private static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.DnsSafeHost, right.DnsSafeHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private TimeSpan GetRetryDelay(RetryConditionHeaderValue? retryAfter, int retryCount)
    {
        TimeSpan delay;
        if (retryAfter?.Delta is { } delta)
        {
            delay = delta;
        }
        else if (retryAfter?.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
        }
        else
        {
            var multiplier = Math.Pow(2, retryCount);
            var ticks = Math.Min(_options.MaximumRetryDelay.Ticks, _options.BaseRetryDelay.Ticks * multiplier);
            delay = TimeSpan.FromTicks(checked((long)ticks));
        }

        if (delay < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay > _options.MaximumRetryDelay ? _options.MaximumRetryDelay : delay;
    }
}
