namespace EasyShare.Services;

public sealed record UpdateDownloadRequest(
    Uri DownloadUri,
    string RepositoryOwner,
    string RepositoryName,
    string TargetPath,
    long ExpectedSizeBytes,
    string ExpectedSha256);

public sealed record UpdateDownloadTransferProgress(long BytesReceived, long? TotalBytes);

public sealed class UpdateDownloadClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public UpdateDownloadClient(HttpClient httpClient, TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout != Timeout.InfiniteTimeSpan && _timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public async Task<long> DownloadAsync(
        UpdateDownloadRequest request,
        IProgress<UpdateDownloadTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!UpdateUriPolicy.IsTrustedInitialDownloadUri(
                request.DownloadUri,
                request.RepositoryOwner,
                request.RepositoryName))
        {
            throw new InvalidDataException("The update download URL is not a trusted GitHub release URL.");
        }

        if (!UpdateUriPolicy.IsValidInstallerSize(request.ExpectedSizeBytes))
        {
            throw new InvalidDataException("The update installer size is missing or exceeds the allowed limit.");
        }

        var expectedSha256 = UpdateIntegrity.NormalizeSha256(request.ExpectedSha256);
        if (string.IsNullOrEmpty(expectedSha256))
        {
            throw new InvalidDataException("The update installer does not have a valid SHA-256 digest.");
        }

        var targetPath = Path.GetFullPath(request.TargetPath);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidDataException("The update target directory is invalid.");
        Directory.CreateDirectory(targetDirectory);
        var temporaryPath = $"{targetPath}.download";
        TryDeleteFile(temporaryPath);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutSource.CancelAfter(_timeout);
        }

        try
        {
            using var response = await SendWithTrustedRedirectsAsync(request, timeoutSource.Token);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > UpdateUriPolicy.MaxInstallerBytes ||
                contentLength is > 0 && contentLength != request.ExpectedSizeBytes)
            {
                throw new InvalidDataException("The update response size does not match the signed release metadata.");
            }

            var totalBytes = contentLength is > 0 ? contentLength : request.ExpectedSizeBytes;
            var bytesReceived = 0L;
            progress?.Report(new UpdateDownloadTransferProgress(0, totalBytes));

            await using (var downloadStream = await response.Content.ReadAsStreamAsync(timeoutSource.Token))
            await using (var fileStream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await downloadStream.ReadAsync(
                           buffer.AsMemory(0, buffer.Length),
                           timeoutSource.Token)) > 0)
                {
                    bytesReceived = checked(bytesReceived + bytesRead);
                    if (bytesReceived > request.ExpectedSizeBytes ||
                        bytesReceived > UpdateUriPolicy.MaxInstallerBytes)
                    {
                        throw new InvalidDataException("The update response exceeded the expected installer size.");
                    }

                    await fileStream.WriteAsync(
                        buffer.AsMemory(0, bytesRead),
                        timeoutSource.Token);
                    progress?.Report(new UpdateDownloadTransferProgress(bytesReceived, totalBytes));
                }

                await fileStream.FlushAsync(timeoutSource.Token);
                fileStream.Flush(flushToDisk: true);
            }

            if (bytesReceived != request.ExpectedSizeBytes)
            {
                throw new InvalidDataException("The downloaded update is incomplete.");
            }

            if (!UpdateIntegrity.VerifyFile(temporaryPath, expectedSha256))
            {
                throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
            if (!UpdateIntegrity.VerifyFile(targetPath, expectedSha256))
            {
                TryDeleteFile(targetPath);
                throw new InvalidDataException("The stored update failed the final SHA-256 verification.");
            }

            progress?.Report(new UpdateDownloadTransferProgress(bytesReceived, totalBytes));
            return bytesReceived;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The update download timed out.", exception);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private async Task<HttpResponseMessage> SendWithTrustedRedirectsAsync(
        UpdateDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var currentUri = request.DownloadUri;
        for (var redirectCount = 0; redirectCount <= UpdateUriPolicy.MaxRedirects; redirectCount++)
        {
            var trusted = redirectCount == 0
                ? UpdateUriPolicy.IsTrustedInitialDownloadUri(
                    currentUri,
                    request.RepositoryOwner,
                    request.RepositoryName)
                : UpdateUriPolicy.IsTrustedDownloadRedirectUri(
                    currentUri,
                    request.RepositoryOwner,
                    request.RepositoryName);
            if (!trusted)
            {
                throw new InvalidDataException("The update redirect target is not trusted.");
            }

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, currentUri);
            var response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            try
            {
                var actualResponseUri = response.RequestMessage?.RequestUri ?? currentUri;
                var actualUriTrusted = redirectCount == 0 && actualResponseUri == currentUri
                    ? trusted
                    : UpdateUriPolicy.IsTrustedDownloadRedirectUri(
                        actualResponseUri,
                        request.RepositoryOwner,
                        request.RepositoryName);
                if (!actualUriTrusted)
                {
                    throw new InvalidDataException("The final update response origin is not trusted.");
                }

                if (!UpdateUriPolicy.IsRedirectStatusCode(response.StatusCode))
                {
                    return response;
                }

                if (redirectCount == UpdateUriPolicy.MaxRedirects ||
                    !UpdateUriPolicy.TryResolveRedirect(
                        actualResponseUri,
                        response.Headers.Location,
                        out var redirectUri) ||
                    !UpdateUriPolicy.IsTrustedDownloadRedirectUri(
                        redirectUri,
                        request.RepositoryOwner,
                        request.RepositoryName))
                {
                    throw new InvalidDataException("The update redirect chain is invalid or too long.");
                }

                currentUri = redirectUri;
            }
            catch
            {
                response.Dispose();
                throw;
            }

            response.Dispose();
        }

        throw new InvalidDataException("The update redirect chain is too long.");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A locked partial file is retried on the next attempt.
        }
    }
}
