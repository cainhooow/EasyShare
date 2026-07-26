using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EasyShare.Models;

namespace EasyShare.Services;

internal sealed class UploadProgressReporter(
    IProgress<UploadTransferProgress>? progress,
    long? totalBytes)
{
    private long _maximumObservedBytes;
    private long _maximumAcknowledgedBytes;
    private int _commitRiskReported;

    public long ObservedBytes => Volatile.Read(ref _maximumObservedBytes);

    public long AcknowledgedBytes => Volatile.Read(ref _maximumAcknowledgedBytes);

    public bool CommitRiskReported => Volatile.Read(ref _commitRiskReported) != 0;

    public void ReportObserved(long bytesTransferred) =>
        Report(bytesTransferred, isAcknowledged: false, ref _maximumObservedBytes);

    public void ReportAcknowledged(long bytesTransferred) =>
        Report(bytesTransferred, isAcknowledged: true, ref _maximumAcknowledgedBytes);

    public void ReportCommitRisk()
    {
        Volatile.Write(ref _commitRiskReported, 1);
        progress?.Report(new UploadTransferProgress(
            Math.Max(ObservedBytes, AcknowledgedBytes),
            totalBytes,
            IsAcknowledged: false,
            MayHaveCommitted: true));
    }

    private void Report(
        long bytesTransferred,
        bool isAcknowledged,
        ref long maximumForProgressType)
    {
        var normalized = Math.Max(0, bytesTransferred);
        if (totalBytes is long knownTotal)
        {
            normalized = Math.Min(normalized, Math.Max(0, knownTotal));
        }

        while (true)
        {
            var current = Volatile.Read(ref maximumForProgressType);
            if (normalized <= current)
            {
                normalized = current;
                break;
            }

            if (Interlocked.CompareExchange(ref maximumForProgressType, normalized, current) == current)
            {
                break;
            }
        }

        var mayHaveCommitted = totalBytes is { } commitTotal && normalized >= commitTotal;
        if (mayHaveCommitted)
        {
            Volatile.Write(ref _commitRiskReported, 1);
        }

        progress?.Report(new UploadTransferProgress(
            normalized,
            totalBytes,
            isAcknowledged,
            MayHaveCommitted: mayHaveCommitted));
    }
}

internal static class UploadTechnicalDetails
{
    internal const int MaximumLength = 2_048;
    private static readonly SensitiveDataRedactor Redactor = new();

    public static string? Sanitize(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return null;
        }

        var redacted = Redactor.Redact(details).Trim();
        if (redacted.Length <= MaximumLength)
        {
            return redacted;
        }

        return redacted[..(MaximumLength - 3)] + "...";
    }

    public static string FromException(string context, Exception exception) =>
        Sanitize($"{context}: {exception.GetType().Name}: {exception.Message}") ?? context;
}

internal static class RemoteUploadReceiptParser
{
    public static RemoteUploadReceipt? ParseGraph(JsonElement element) =>
        Build(
            ReadString(element, "id"),
            ReadString(element, "eTag") ?? ReadString(element, "@odata.etag"),
            ReadLong(element, "size"),
            ReadDate(element, "lastModifiedDateTime"));

    public static RemoteUploadReceipt? ParseSharePoint(JsonElement root)
    {
        var item = root;
        if (root.TryGetProperty("d", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
        {
            item = legacy;
        }

        return Build(
            ReadString(item, "UniqueId"),
            ReadString(item, "ETag") ??
            ReadString(item, "@odata.etag") ??
            ReadString(item, "odata.etag"),
            ReadLong(item, "Length"),
            ReadDate(item, "TimeLastModified"));
    }

    private static RemoteUploadReceipt? Build(
        string? itemId,
        string? etag,
        long? size,
        DateTimeOffset? modifiedAt) =>
        string.IsNullOrWhiteSpace(itemId) &&
        string.IsNullOrWhiteSpace(etag) &&
        size is null &&
        modifiedAt is null
            ? null
            : new RemoteUploadReceipt(itemId, etag, size, modifiedAt);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string propertyName) =>
        DateTimeOffset.TryParse(ReadString(element, propertyName), out var value)
            ? value
            : null;
}

internal sealed class ProgressReadStream(
    Stream inner,
    long startPosition,
    UploadProgressReporter progress) : Stream
{
    private long _bytesRead;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        ReportRead(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        ReportRead(read);
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        ReportRead(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        ReportRead(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // HttpContent owns this wrapper, not the caller's source stream.
    }

    private void ReportRead(int read)
    {
        if (read <= 0)
        {
            return;
        }

        var sequentialBytes = Interlocked.Add(ref _bytesRead, read);
        if (inner.CanSeek)
        {
            try
            {
                progress.ReportObserved(Math.Max(0, inner.Position - startPosition));
                return;
            }
            catch (NotSupportedException)
            {
            }
            catch (IOException)
            {
            }
        }

        progress.ReportObserved(sequentialBytes);
    }
}

internal static class UploadFailureClassifier
{
    public static UploadAttemptResult FromStatus(
        HttpStatusCode statusCode,
        string serviceName,
        string technicalContext)
    {
        var (state, kind, message) = statusCode switch
        {
            HttpStatusCode.Unauthorized => (
                UploadAttemptState.RetryableFailure,
                SyncFailureKind.Session,
                $"Sua sessão do {serviceName} expirou. Entre novamente e tente de novo."),
            HttpStatusCode.Forbidden => (
                UploadAttemptState.RetryableFailure,
                SyncFailureKind.Permission,
                $"Sua conta não tem permissão para enviar este arquivo ao {serviceName}."),
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => (
                UploadAttemptState.Conflict,
                SyncFailureKind.Conflict,
                "O item remoto foi alterado ou já existe no destino."),
            HttpStatusCode.TooManyRequests => (
                UploadAttemptState.RetryableFailure,
                SyncFailureKind.ServiceBusy,
                $"{serviceName} está ocupado agora. Aguarde um pouco e tente novamente."),
            HttpStatusCode.InsufficientStorage => (
                UploadAttemptState.RetryableFailure,
                SyncFailureKind.Quota,
                $"Não há espaço suficiente no {serviceName} para concluir o envio."),
            HttpStatusCode.RequestTimeout => (
                UploadAttemptState.RetryableFailure,
                SyncFailureKind.Network,
                $"A conexão com o {serviceName} demorou demais. Tente novamente."),
            HttpStatusCode.NotFound => (
                UploadAttemptState.RetryableFailure,
                SyncFailureKind.RouteUnavailable,
                "A pasta de destino não foi encontrada. Revise a conexão e tente novamente."),
            _ when (int)statusCode >= 500 => (
                UploadAttemptState.RetryableFailure,
                SyncFailureKind.ServiceBusy,
                $"{serviceName} está temporariamente indisponível. Aguarde um pouco e tente novamente."),
            _ => (
                UploadAttemptState.RetryableFailure,
                SyncFailureKind.Unknown,
                $"Não foi possível concluir o envio ao {serviceName}. Tente novamente.")
        };

        return new UploadAttemptResult(
            state,
            message,
            FailureKind: kind,
            TechnicalDetails: UploadTechnicalDetails.Sanitize(
                $"{technicalContext} returned HTTP {(int)statusCode} ({statusCode})."));
    }
}

internal static class RemoteUploadVerificationResults
{
    public static RemoteUploadVerificationResult Confirmed(RemoteUploadReceipt receipt) =>
        new(
            RemoteUploadVerificationState.Confirmed,
            receipt,
            SyncFailureKind.None);

    public static RemoteUploadVerificationResult NotFound() =>
        new(
            RemoteUploadVerificationState.NotFound,
            FailureKind: SyncFailureKind.None);

    public static RemoteUploadVerificationResult Unavailable(
        string userMessage,
        SyncFailureKind failureKind,
        string? technicalDetails = null) =>
        new(
            RemoteUploadVerificationState.Unavailable,
            FailureKind: failureKind,
            UserMessage: userMessage,
            TechnicalDetails: UploadTechnicalDetails.Sanitize(technicalDetails));

    public static RemoteUploadVerificationResult FromStatus(
        HttpStatusCode statusCode,
        string serviceName,
        string technicalContext)
    {
        if (statusCode == HttpStatusCode.NotFound)
        {
            return NotFound();
        }

        var classified = UploadFailureClassifier.FromStatus(statusCode, serviceName, technicalContext);
        return Unavailable(
            classified.Error ?? $"Não foi possível consultar o item no {serviceName}.",
            classified.FailureKind,
            classified.TechnicalDetails);
    }
}

internal static class RemoteDeleteResults
{
    public static RemoteDeleteAttemptResult FromStatus(
        HttpStatusCode statusCode,
        string serviceName,
        string technicalContext)
    {
        if ((int)statusCode is >= 200 and <= 299 || statusCode == HttpStatusCode.NotFound)
        {
            return new RemoteDeleteAttemptResult(
                RemoteDeleteAttemptState.Succeeded,
                SyncFailureKind.None,
                HttpStatusCode: (int)statusCode);
        }

        var (state, kind, message) = statusCode switch
        {
            HttpStatusCode.Unauthorized => (
                RemoteDeleteAttemptState.RetryableFailure,
                SyncFailureKind.Session,
                $"Sua sessão do {serviceName} expirou. Entre novamente e tente excluir de novo."),
            HttpStatusCode.Forbidden => (
                RemoteDeleteAttemptState.TerminalFailure,
                SyncFailureKind.Permission,
                $"Sua conta não tem permissão para excluir este item no {serviceName}."),
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => (
                RemoteDeleteAttemptState.TerminalFailure,
                SyncFailureKind.Conflict,
                "O item está em uso, bloqueado ou foi alterado no destino."),
            _ when (int)statusCode == 423 => (
                RemoteDeleteAttemptState.TerminalFailure,
                SyncFailureKind.Conflict,
                "O item está bloqueado no destino. Libere-o e tente novamente."),
            HttpStatusCode.RequestTimeout => (
                RemoteDeleteAttemptState.RetryableFailure,
                SyncFailureKind.Network,
                $"A conexão com o {serviceName} demorou demais. A exclusão será tentada novamente."),
            HttpStatusCode.TooManyRequests => (
                RemoteDeleteAttemptState.RetryableFailure,
                SyncFailureKind.ServiceBusy,
                $"{serviceName} está ocupado agora. A exclusão será tentada novamente."),
            _ when (int)statusCode >= 500 => (
                RemoteDeleteAttemptState.RetryableFailure,
                SyncFailureKind.ServiceBusy,
                $"{serviceName} está temporariamente indisponível. A exclusão será tentada novamente."),
            _ => (
                RemoteDeleteAttemptState.TerminalFailure,
                SyncFailureKind.Unknown,
                $"Não foi possível excluir o item no {serviceName}. Verifique os detalhes e tente novamente.")
        };

        return new RemoteDeleteAttemptResult(
            state,
            kind,
            message,
            UploadTechnicalDetails.Sanitize(
                $"{technicalContext} returned HTTP {(int)statusCode} ({statusCode})."),
            (int)statusCode);
    }

    public static RemoteDeleteAttemptResult Retryable(
        SyncFailureKind kind,
        string message,
        string technicalDetails) =>
        new(
            RemoteDeleteAttemptState.RetryableFailure,
            kind,
            message,
            UploadTechnicalDetails.Sanitize(technicalDetails));

    public static RemoteDeleteAttemptResult Terminal(
        SyncFailureKind kind,
        string message,
        string? technicalDetails = null) =>
        new(
            RemoteDeleteAttemptState.TerminalFailure,
            kind,
            message,
            UploadTechnicalDetails.Sanitize(technicalDetails));
}

internal sealed class GraphSharePointContentService : ISharePointContentTransfer
{
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private const long UploadSessionThresholdBytes = 10L * 1024 * 1024;
    private const int UploadChunkSize = 10 * 1024 * 1024;
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly IAuthenticationService _authentication;
    private readonly HttpClient _httpClient;

    public GraphSharePointContentService(IAuthenticationService authentication)
        : this(authentication, SharedHttpClient)
    {
    }

    public GraphSharePointContentService(IAuthenticationService authentication, HttpClient httpClient)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<IReadOnlyList<SharePointDriveItem>> ListDirectoryAsync(
        DriveRoute route,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!TryGetRoute(route, out var graphRoute))
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.InvalidResponse,
                "A rota não possui uma identidade completa do Microsoft Graph.");
        }

        var normalized = NormalizeRelativePath(relativePath);
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.AuthenticationRequired,
                "Entre novamente para listar esta pasta do SharePoint.");
        }

        try
        {
            var nextUrl = BuildItemUrl(graphRoute, normalized) +
                          "/children?$select=id,name,size,lastModifiedDateTime,folder,file&$top=200";
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var items = new List<SharePointDriveItem>();
            while (!string.IsNullOrWhiteSpace(nextUrl))
            {
                cancellationToken.ThrowIfCancellationRequested();
                nextUrl = ValidateGraphUrl(nextUrl);
                if (!visited.Add(nextUrl))
                {
                    throw new SharePointExplorerException(
                        SharePointExplorerStatus.InvalidResponse,
                        "O Microsoft Graph repetiu um link de paginação.");
                }

                using var response = await SendGraphAsync(
                    HttpMethod.Get,
                    nextUrl,
                    token,
                    content: null,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw CreateGraphFailure(response);
                }

                using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                if (!document.RootElement.TryGetProperty("value", out var value) ||
                    value.ValueKind != JsonValueKind.Array)
                {
                    throw new SharePointExplorerException(
                        SharePointExplorerStatus.InvalidResponse,
                        "A resposta do Microsoft Graph não contém uma lista de itens válida.");
                }

                foreach (var element in value.EnumerateArray())
                {
                    var item = ParseDriveItem(element, graphRoute.DriveId);
                    if (item is not null)
                    {
                        items.Add(item);
                    }
                }

                nextUrl = ReadOptionalString(document.RootElement, "@odata.nextLink") ?? string.Empty;
            }

            return items
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SharePointExplorerException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.InvalidResponse,
                "O Microsoft Graph retornou JSON inválido ao listar a pasta.",
                innerException: ex);
        }
        catch (TimeoutException ex)
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.ServiceUnavailable,
                "O Microsoft Graph excedeu o tempo limite ao listar a pasta.",
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.ServiceUnavailable,
                "Não foi possível consultar o Microsoft Graph agora.",
                innerException: ex);
        }
        catch (Exception ex)
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.InvalidResponse,
                "Não foi possível processar a resposta do Microsoft Graph.",
                innerException: ex);
        }
    }

    public async Task<SharePointDriveItem?> GetItemAsync(
        DriveRoute route,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!TryGetRoute(route, out var graphRoute))
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.InvalidResponse,
                "A rota não possui uma identidade completa do Microsoft Graph.");
        }

        var normalized = NormalizeRelativePath(relativePath);
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.AuthenticationRequired,
                "Entre novamente para consultar este item do SharePoint.");
        }

        try
        {
            var item = await GetGraphItemAsync(graphRoute, normalized, token, cancellationToken)
                .ConfigureAwait(false);
            return item?.ToSharePointDriveItem(graphRoute.DriveId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SharePointExplorerException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.InvalidResponse,
                "O Microsoft Graph retornou JSON inválido ao consultar o item.",
                innerException: ex);
        }
        catch (TimeoutException ex)
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.ServiceUnavailable,
                "O Microsoft Graph excedeu o tempo limite ao consultar o item.",
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SharePointExplorerException(
                SharePointExplorerStatus.ServiceUnavailable,
                "Não foi possível consultar o Microsoft Graph agora.",
                innerException: ex);
        }
    }

    public async Task<RemoteUploadVerificationResult> VerifyRemoteUploadAsync(
        DriveRoute route,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!TryGetRoute(route, out var graphRoute))
        {
            return RemoteUploadVerificationResults.Unavailable(
                "A pasta de destino não está pronta para verificação remota.",
                SyncFailureKind.RouteUnavailable);
        }

        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(GetFileName(normalized)))
        {
            return RemoteUploadVerificationResults.Unavailable(
                "Não foi possível identificar o arquivo para verificação remota.",
                SyncFailureKind.RouteUnavailable);
        }

        try
        {
            var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return RemoteUploadVerificationResults.Unavailable(
                    "Sua sessão expirou. Entre novamente para confirmar o arquivo no SharePoint.",
                    SyncFailureKind.Session);
            }

            var item = await GetGraphItemAsync(graphRoute, normalized, token, cancellationToken)
                .ConfigureAwait(false);
            if (item is null)
            {
                return RemoteUploadVerificationResults.NotFound();
            }

            return RemoteUploadVerificationResults.Confirmed(
                new RemoteUploadReceipt(
                    item.Id,
                    item.ETag,
                    item.Length,
                    item.ModifiedAt == DateTimeOffset.MinValue ? null : item.ModifiedAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return RemoteUploadVerificationResults.Unavailable(
                "O Microsoft Graph demorou demais para confirmar o arquivo. Aguarde e tente novamente.",
                SyncFailureKind.Network,
                UploadTechnicalDetails.FromException(
                    "Microsoft Graph remote verification timed out",
                    exception));
        }
        catch (SharePointExplorerException exception)
        {
            if (exception.Status == SharePointExplorerStatus.NotFound)
            {
                return RemoteUploadVerificationResults.NotFound();
            }

            return exception.HttpStatusCode is HttpStatusCode statusCode
                ? RemoteUploadVerificationResults.FromStatus(
                    statusCode,
                    "Microsoft Graph",
                    "Microsoft Graph remote verification")
                : RemoteUploadVerificationResults.Unavailable(
                    "Não foi possível confirmar o arquivo no Microsoft Graph agora.",
                    exception.Status switch
                    {
                        SharePointExplorerStatus.AuthenticationRequired => SyncFailureKind.Session,
                        SharePointExplorerStatus.Forbidden => SyncFailureKind.Permission,
                        SharePointExplorerStatus.Throttled or SharePointExplorerStatus.ServiceUnavailable =>
                            SyncFailureKind.ServiceBusy,
                        SharePointExplorerStatus.InvalidResponse => SyncFailureKind.Integrity,
                        _ => SyncFailureKind.Unknown
                    },
                    UploadTechnicalDetails.FromException(
                        "Microsoft Graph remote verification failed",
                        exception));
        }
        catch (TimeoutException exception)
        {
            return RemoteUploadVerificationResults.Unavailable(
                "O Microsoft Graph demorou demais para confirmar o arquivo. Aguarde e tente novamente.",
                SyncFailureKind.Network,
                UploadTechnicalDetails.FromException(
                    "Microsoft Graph remote verification timed out",
                    exception));
        }
        catch (HttpRequestException exception)
        {
            return RemoteUploadVerificationResults.Unavailable(
                "Não foi possível consultar o Microsoft Graph. Verifique a conexão e tente novamente.",
                SyncFailureKind.Network,
                UploadTechnicalDetails.FromException(
                    "Microsoft Graph remote verification request failed",
                    exception));
        }
        catch (IOException exception)
        {
            return RemoteUploadVerificationResults.Unavailable(
                "Não foi possível concluir a leitura da confirmação remota. Tente novamente.",
                SyncFailureKind.Network,
                UploadTechnicalDetails.FromException(
                    "Microsoft Graph remote verification response failed",
                    exception));
        }
        catch (JsonException exception)
        {
            return RemoteUploadVerificationResults.Unavailable(
                "O Microsoft Graph retornou uma confirmação que não pôde ser validada.",
                SyncFailureKind.Integrity,
                UploadTechnicalDetails.FromException(
                    "Microsoft Graph remote verification response was invalid",
                    exception));
        }
        catch (Exception exception)
        {
            return RemoteUploadVerificationResults.Unavailable(
                "Não foi possível confirmar o arquivo no Microsoft Graph agora.",
                SyncFailureKind.Unknown,
                UploadTechnicalDetails.FromException(
                    "Microsoft Graph remote verification failed",
                    exception));
        }
    }

    public async Task<bool> DownloadFileAsync(
        DriveRoute route,
        string relativePath,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        }

        if (!TryGetRoute(route, out var graphRoute))
        {
            return false;
        }

        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return false;
        }

        try
        {
            var url = BuildItemUrl(graphRoute, normalized) + "/content";
            using var response = await SendGraphAsync(
                HttpMethod.Get,
                url,
                token,
                content: null,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var transferTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            transferTimeout.CancelAfter(TimeSpan.FromMinutes(15));
            await using var source = await response.Content
                .ReadAsStreamAsync(transferTimeout.Token)
                .ConfigureAwait(false);
            await source.CopyToAsync(destination, 81_920, transferTimeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> CreateFolderAsync(
        DriveRoute route,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!TryGetRoute(route, out var graphRoute))
        {
            return false;
        }

        var normalized = NormalizeRelativePath(relativePath);
        var folderName = GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return false;
        }

        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return false;
        }

        try
        {
            var parentPath = GetParentPath(normalized);
            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["name"] = folderName,
                ["folder"] = new { },
                ["@microsoft.graph.conflictBehavior"] = "fail"
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await SendGraphAsync(
                HttpMethod.Post,
                BuildItemUrl(graphRoute, parentPath) + "/children",
                token,
                content,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task<UploadAttemptResult> TryUploadFileAsync(
        DriveRoute route,
        string relativePath,
        Stream content,
        DateTimeOffset? expectedModifiedAt,
        CancellationToken cancellationToken = default,
        IProgress<UploadTransferProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The upload stream must be readable.", nameof(content));
        }

        if (!TryGetRoute(route, out var graphRoute))
        {
            return Retryable(
                "Esta pasta ainda não está pronta para usar o Microsoft Graph.",
                SyncFailureKind.RouteUnavailable);
        }

        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(GetFileName(normalized)))
        {
            return Retryable(
                "Não foi possível identificar o arquivo que deve ser enviado.",
                SyncFailureKind.RouteUnavailable);
        }

        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return Retryable(
                "Sua sessão expirou. Entre novamente para enviar arquivos ao SharePoint.",
                SyncFailureKind.Session);
        }

        UploadProgressReporter? progressReporter = null;
        try
        {
            string? expectedETag = null;
            string? existingItemId = null;
            if (expectedModifiedAt is not null)
            {
                var current = await GetGraphItemAsync(graphRoute, normalized, token, cancellationToken)
                    .ConfigureAwait(false);
                if (current is null ||
                    Math.Abs((current.ModifiedAt - expectedModifiedAt.Value).TotalSeconds) > 2)
                {
                    return new UploadAttemptResult(
                        UploadAttemptState.Conflict,
                        "O arquivo remoto mudou enquanto o arquivo local estava sendo editado.",
                        FailureKind: SyncFailureKind.Conflict);
                }

                if (string.IsNullOrWhiteSpace(current.ETag))
                {
                    return new UploadAttemptResult(
                        UploadAttemptState.Conflict,
                        "Não foi possível confirmar a versão remota antes de substituir o arquivo.",
                        FailureKind: SyncFailureKind.Conflict,
                        TechnicalDetails: UploadTechnicalDetails.Sanitize(
                            "Microsoft Graph did not return an ETag for the existing driveItem."));
                }

                expectedETag = current.ETag;
                existingItemId = current.Id;
            }

            var remainingLength = content.CanSeek ? content.Length - content.Position : (long?)null;
            if (remainingLength is null)
            {
                return Retryable(
                    "Não foi possível determinar o tamanho do arquivo para enviá-lo com segurança.",
                    SyncFailureKind.PayloadUnavailable);
            }

            progressReporter = new UploadProgressReporter(progress, remainingLength);
            if (remainingLength > UploadSessionThresholdBytes ||
                (expectedETag is not null && remainingLength > 0))
            {
                return await UploadLargeFileAsync(
                    graphRoute,
                    normalized,
                    content,
                    remainingLength.Value,
                    token,
                    expectedETag,
                    existingItemId,
                    progressReporter,
                    cancellationToken).ConfigureAwait(false);
            }

            var contentStart = content.Position;
            if (remainingLength == 0)
            {
                progressReporter.ReportCommitRisk();
            }

            using var streamContent = new StreamContent(
                new ProgressReadStream(content, contentStart, progressReporter),
                81_920);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            streamContent.Headers.ContentLength = remainingLength;
            using var response = await SendGraphAsync(
                HttpMethod.Put,
                BuildItemUrl(graphRoute, normalized) + "/content",
                token,
                streamContent,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken,
                expectedETag).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ToUploadResult(response.StatusCode);
            }

            var receiptRead = await ReadCommittedReceiptSafelyAsync(
                    response,
                    "Microsoft Graph simple upload receipt",
                    cancellationToken)
                .ConfigureAwait(false);
            progressReporter.ReportAcknowledged(remainingLength.Value);
            return new UploadAttemptResult(
                UploadAttemptState.Succeeded,
                Receipt: receiptRead.Receipt,
                FailureKind: SyncFailureKind.None,
                TechnicalDetails: receiptRead.TechnicalDetails);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return Retryable(
                "O Microsoft Graph demorou demais para responder. Aguarde um pouco e tente novamente.",
                SyncFailureKind.Network,
                UploadTechnicalDetails.FromException("Microsoft Graph upload timed out", exception),
                isCommitAmbiguous: progressReporter?.CommitRiskReported == true);
        }
        catch (TimeoutException exception)
        {
            return Retryable(
                "O Microsoft Graph demorou demais para responder. Verifique a conexão e tente novamente.",
                SyncFailureKind.Network,
                UploadTechnicalDetails.FromException("Microsoft Graph upload timed out", exception),
                isCommitAmbiguous: progressReporter?.CommitRiskReported == true);
        }
        catch (HttpRequestException exception)
        {
            return Retryable(
                "Não foi possível alcançar o Microsoft Graph. Verifique a conexão e tente novamente.",
                SyncFailureKind.Network,
                UploadTechnicalDetails.FromException("Microsoft Graph upload request failed", exception),
                isCommitAmbiguous: progressReporter?.CommitRiskReported == true);
        }
        catch (IOException exception)
        {
            return Retryable(
                "Não foi possível ler o arquivo local para concluir o envio.",
                SyncFailureKind.PayloadUnavailable,
                UploadTechnicalDetails.FromException("Local upload stream failed", exception));
        }
        catch (JsonException exception)
        {
            return Retryable(
                "O Microsoft Graph retornou uma confirmação que não pôde ser validada.",
                SyncFailureKind.Integrity,
                UploadTechnicalDetails.FromException(
                    "Microsoft Graph upload response was invalid",
                    exception));
        }
        catch (SharePointExplorerException exception)
        {
            return exception.HttpStatusCode is HttpStatusCode statusCode
                ? ToUploadResult(statusCode)
                : Retryable(
                    "Não foi possível confirmar o destino no Microsoft Graph.",
                    SyncFailureKind.Unknown,
                    UploadTechnicalDetails.FromException(
                        "Microsoft Graph destination verification failed",
                        exception));
        }
        catch (Exception exception)
        {
            return Retryable(
                "Não foi possível enviar o arquivo pelo Microsoft Graph agora.",
                SyncFailureKind.Unknown,
                UploadTechnicalDetails.FromException("Microsoft Graph upload failed", exception));
        }
    }

    public async Task<RemoteDeleteAttemptResult> TryDeleteItemAsync(
        DriveRoute route,
        string relativePath,
        bool isDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        _ = isDirectory;
        if (!TryGetRoute(route, out var graphRoute))
        {
            return RemoteDeleteResults.Terminal(
                SyncFailureKind.RouteUnavailable,
                "A pasta de destino não está disponível para exclusão.");
        }

        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return RemoteDeleteResults.Terminal(
                SyncFailureKind.RouteUnavailable,
                "Não foi possível identificar o item que deve ser excluído.");
        }

        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return RemoteDeleteResults.Retryable(
                SyncFailureKind.Session,
                "Sua sessão do Microsoft Graph expirou. Entre novamente para concluir a exclusão.",
                "Microsoft Graph delete did not start because no access token was available.");
        }

        try
        {
            using var response = await SendGraphAsync(
                HttpMethod.Delete,
                BuildItemUrl(graphRoute, normalized),
                token,
                content: null,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            return RemoteDeleteResults.FromStatus(
                response.StatusCode,
                "Microsoft Graph",
                "Microsoft Graph delete");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return RemoteDeleteResults.Retryable(
                SyncFailureKind.Network,
                "O Microsoft Graph demorou demais para responder. A exclusão será tentada novamente.",
                UploadTechnicalDetails.FromException("Microsoft Graph delete timed out", exception));
        }
        catch (TimeoutException exception)
        {
            return RemoteDeleteResults.Retryable(
                SyncFailureKind.Network,
                "O Microsoft Graph demorou demais para responder. A exclusão será tentada novamente.",
                UploadTechnicalDetails.FromException("Microsoft Graph delete timed out", exception));
        }
        catch (HttpRequestException exception)
        {
            return RemoteDeleteResults.Retryable(
                SyncFailureKind.Network,
                "Não foi possível alcançar o Microsoft Graph. A exclusão será tentada novamente.",
                UploadTechnicalDetails.FromException("Microsoft Graph delete request failed", exception));
        }
        catch (Exception exception)
        {
            return RemoteDeleteResults.Retryable(
                SyncFailureKind.Unknown,
                "Não foi possível concluir a exclusão pelo Microsoft Graph agora.",
                UploadTechnicalDetails.FromException("Microsoft Graph delete failed", exception));
        }
    }

    public async Task<bool> DeleteItemAsync(
        DriveRoute route,
        string relativePath,
        bool isDirectory,
        CancellationToken cancellationToken = default) =>
        (await TryDeleteItemAsync(route, relativePath, isDirectory, cancellationToken)
            .ConfigureAwait(false)).State == RemoteDeleteAttemptState.Succeeded;

    public async Task<bool> RenameItemAsync(
        DriveRoute route,
        string oldRelativePath,
        string newRelativePath,
        bool isDirectory,
        bool replaceIfExists,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        _ = isDirectory;
        if (!TryGetRoute(route, out var graphRoute))
        {
            return false;
        }

        var oldNormalized = NormalizeRelativePath(oldRelativePath);
        var newNormalized = NormalizeRelativePath(newRelativePath);
        var newName = GetFileName(newNormalized);
        if (string.IsNullOrWhiteSpace(oldNormalized) || string.IsNullOrWhiteSpace(newName))
        {
            return false;
        }

        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return false;
        }

        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["name"] = newName
            };
            var oldParent = GetParentPath(oldNormalized);
            var newParent = GetParentPath(newNormalized);
            if (!string.Equals(oldParent, newParent, StringComparison.OrdinalIgnoreCase))
            {
                var newParentItem = await GetGraphItemAsync(graphRoute, newParent, token, cancellationToken)
                    .ConfigureAwait(false);
                if (newParentItem is null || !newParentItem.IsDirectory)
                {
                    return false;
                }

                payload["parentReference"] = new { id = newParentItem.Id };
            }

            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await SendGraphAsync(
                HttpMethod.Patch,
                BuildItemUrl(graphRoute, oldNormalized) +
                $"?@microsoft.graph.conflictBehavior={(replaceIfExists ? "replace" : "fail")}",
                token,
                content,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<UploadAttemptResult> UploadLargeFileAsync(
        GraphRoute graphRoute,
        string relativePath,
        Stream source,
        long totalLength,
        string token,
        string? expectedETag,
        string? existingItemId,
        UploadProgressReporter progress,
        CancellationToken cancellationToken)
    {
        if (!source.CanSeek)
        {
            return Retryable(
                "Arquivos grandes precisam de um fluxo reposicionável para envio em partes.",
                SyncFailureKind.PayloadUnavailable);
        }

        var fileName = GetFileName(relativePath);
        var sessionBody = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["item"] = new Dictionary<string, object?>
            {
                ["@microsoft.graph.conflictBehavior"] = "replace",
                ["name"] = fileName
            }
        });
        using var sessionContent = new StringContent(sessionBody, Encoding.UTF8, "application/json");
        var sessionUrl = string.IsNullOrWhiteSpace(existingItemId)
            ? BuildItemUrl(graphRoute, relativePath) + "/createUploadSession"
            : $"{GraphBaseUrl}/drives/{Uri.EscapeDataString(graphRoute.DriveId)}/items/" +
              $"{Uri.EscapeDataString(existingItemId)}/createUploadSession";
        using var sessionResponse = await SendGraphAsync(
            HttpMethod.Post,
            sessionUrl,
            token,
            sessionContent,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken,
            expectedETag).ConfigureAwait(false);
        if (!sessionResponse.IsSuccessStatusCode)
        {
            return ToUploadResult(sessionResponse.StatusCode);
        }

        using var sessionDocument = await ReadJsonAsync(sessionResponse, cancellationToken).ConfigureAwait(false);
        var uploadUrlText = ReadOptionalString(sessionDocument.RootElement, "uploadUrl");
        if (!TryValidateUploadUrl(uploadUrlText, out var uploadUrl))
        {
            return Retryable(
                "O Microsoft Graph não retornou uma sessão de upload confiável.",
                SyncFailureKind.Integrity,
                "The createUploadSession response did not contain a validated HTTPS upload URL.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(UploadChunkSize);
        var sourceStart = source.Position;
        var committed = false;
        try
        {
            long offset = 0;
            var offsetAttempts = new Dictionary<long, int>();
            while (offset < totalLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                offsetAttempts.TryGetValue(offset, out var attemptsAtOffset);
                if (attemptsAtOffset >= 3)
                {
                    return Retryable(
                        "A sessão de upload não avançou. Aguarde um pouco e tente novamente.",
                        SyncFailureKind.ServiceBusy,
                        $"The upload session did not advance after {attemptsAtOffset} attempts at byte {offset}.");
                }

                offsetAttempts[offset] = attemptsAtOffset + 1;
                if (!await TryPositionSourceAsync(
                        source,
                        sourceStart + offset,
                        buffer,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return Retryable(
                        "Não foi possível reler o arquivo local para retomar o envio.",
                        SyncFailureKind.PayloadUnavailable,
                        $"The upload source could not be positioned at byte {sourceStart + offset}.");
                }

                var requested = (int)Math.Min(UploadChunkSize, totalLength - offset);
                var read = await ReadChunkAsync(source, buffer, requested, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    return Retryable(
                        "O arquivo local terminou antes do tamanho esperado.",
                        SyncFailureKind.PayloadUnavailable,
                        $"The upload source ended at byte {offset} before the expected length {totalLength}.");
                }

                // Persist remote-risk evidence before this chunk is handed to
                // HttpClient. A crash after this point must verify remotely.
                progress.ReportObserved(offset + read);
                using var response = await SendUploadChunkWithRetryAsync(
                    uploadUrl,
                    buffer,
                    read,
                    offset,
                    totalLength,
                    cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                {
                    committed = true;
                    var receiptRead = await ReadCommittedReceiptSafelyAsync(
                            response,
                            "Microsoft Graph upload-session receipt",
                            cancellationToken)
                        .ConfigureAwait(false);
                    progress.ReportAcknowledged(totalLength);
                    return new UploadAttemptResult(
                        UploadAttemptState.Succeeded,
                        Receipt: receiptRead.Receipt,
                        FailureKind: SyncFailureKind.None,
                        TechnicalDetails: receiptRead.TechnicalDetails);
                }

                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    var fallbackOffset = offset + read;
                    offset = await ReadNextExpectedOffsetAsync(
                            response,
                            fallbackOffset,
                            totalLength,
                            cancellationToken)
                        .ConfigureAwait(false);
                    progress.ReportAcknowledged(offset);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    var serverOffset = await QueryUploadOffsetAsync(uploadUrl, totalLength, cancellationToken)
                        .ConfigureAwait(false);
                    if (serverOffset is not null)
                    {
                        offset = serverOffset.Value;
                        progress.ReportAcknowledged(offset);
                        continue;
                    }
                }

                return ToUploadResult(response.StatusCode);
            }

            return Retryable(
                "A sessão de upload terminou sem confirmação remota.",
                SyncFailureKind.Integrity,
                $"The upload session loop ended at acknowledged byte {progress.AcknowledgedBytes} " +
                $"for expected length {totalLength}.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            if (!committed)
            {
                await CancelUploadSessionAsync(uploadUrl).ConfigureAwait(false);
            }
        }
    }

    private async Task<HttpResponseMessage> SendUploadChunkWithRetryAsync(
        Uri uploadUrl,
        byte[] buffer,
        int count,
        long offset,
        long totalLength,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
                request.Content = new ByteArrayContent(buffer, 0, count);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Content.Headers.ContentLength = count;
                request.Content.Headers.TryAddWithoutValidation(
                    "Content-Range",
                    $"bytes {offset}-{offset + count - 1}/{totalLength}");
                var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                var isFinalChunk = offset + count >= totalLength;
                if (attempt >= maximumAttempts ||
                    isFinalChunk ||
                    !IsTransientUploadStatus(response.StatusCode))
                {
                    return response;
                }

                var delay = GetRetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (
                offset + count < totalLength &&
                attempt < maximumAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                offset + count < totalLength &&
                attempt < maximumAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<long?> QueryUploadOffsetAsync(
        Uri uploadUrl,
        long totalLength,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uploadUrl);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var offset = await ReadNextExpectedOffsetAsync(
                response,
                fallbackOffset: -1,
                totalLength,
                cancellationToken)
            .ConfigureAwait(false);
        return offset >= 0 ? offset : null;
    }

    private static async Task<long> ReadNextExpectedOffsetAsync(
        HttpResponseMessage response,
        long fallbackOffset,
        long totalLength,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("nextExpectedRanges", out var ranges) &&
                ranges.ValueKind == JsonValueKind.Array)
            {
                foreach (var range in ranges.EnumerateArray())
                {
                    var value = range.GetString();
                    var separator = value?.IndexOf('-') ?? -1;
                    var start = separator >= 0 ? value![..separator] : value;
                    if (long.TryParse(start, out var parsed) && parsed >= 0 && parsed <= totalLength)
                    {
                        return parsed;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // A valid 202 response may omit a body; sequential progress remains safe.
        }

        return fallbackOffset;
    }

    private async Task CancelUploadSessionAsync(Uri uploadUrl)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Delete, uploadUrl);
            using var _ = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // Cancellation is best effort; the preauthenticated session also expires server-side.
        }
    }

    private static bool IsTransientUploadStatus(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var delay = response.Headers.RetryAfter?.Delta;
        if (delay is null && response.Headers.RetryAfter?.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
        }

        if (delay is not null)
        {
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay.Value;
        }

        return TimeSpan.FromSeconds(Math.Min(30, attempt));
    }

    private async Task<GraphItem?> GetGraphItemAsync(
        GraphRoute graphRoute,
        string relativePath,
        string token,
        CancellationToken cancellationToken)
    {
        using var response = await SendGraphAsync(
            HttpMethod.Get,
            BuildItemUrl(graphRoute, NormalizeRelativePath(relativePath)) +
            "?$select=id,name,size,lastModifiedDateTime,eTag,folder,file",
            token,
            content: null,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw CreateGraphFailure(response);
        }

        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseGraphItem(document.RootElement) ??
               throw new SharePointExplorerException(
                   SharePointExplorerStatus.InvalidResponse,
                   "O Microsoft Graph não retornou metadados completos para o item solicitado.");
    }

    private async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = await _authentication.GetAccessTokenAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private async Task<HttpResponseMessage> SendGraphAsync(
        HttpMethod method,
        string url,
        string token,
        HttpContent? content,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken,
        string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, ValidateGraphUrl(url));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(ifMatch))
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        request.Content = content;
        return await _httpClient.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var parseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        parseTimeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await using var stream = await response.Content
                .ReadAsStreamAsync(parseTimeout.Token)
                .ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: parseTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("O Microsoft Graph excedeu o tempo limite ao retornar JSON.", ex);
        }
    }

    private static async Task<CommittedReceiptReadResult> ReadCommittedReceiptSafelyAsync(
        HttpResponseMessage response,
        string technicalContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var receipt = await ReadRemoteUploadReceiptAsync(response, cancellationToken).ConfigureAwait(false);
            return receipt is not null
                ? new CommittedReceiptReadResult(receipt, null)
                : new CommittedReceiptReadResult(
                    null,
                    UploadTechnicalDetails.Sanitize(
                        $"{technicalContext} did not include supported remote confirmation fields."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new CommittedReceiptReadResult(
                null,
                UploadTechnicalDetails.FromException(
                    $"{technicalContext} could not be parsed after the remote commit",
                    exception));
        }
    }

    private static async Task<RemoteUploadReceipt?> ReadRemoteUploadReceiptAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength == 0)
        {
            return null;
        }

        using var parseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        parseTimeout.CancelAfter(TimeSpan.FromMinutes(2));
        await using var stream = await response.Content
            .ReadAsStreamAsync(parseTimeout.Token)
            .ConfigureAwait(false);
        if (stream.CanSeek && stream.Length == 0)
        {
            return null;
        }

        using var document = await JsonDocument
            .ParseAsync(stream, cancellationToken: parseTimeout.Token)
            .ConfigureAwait(false);
        return RemoteUploadReceiptParser.ParseGraph(document.RootElement);
    }

    private static GraphItem? ParseGraphItem(JsonElement element)
    {
        var id = ReadOptionalString(element, "id");
        var name = ReadOptionalString(element, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var modifiedAt = DateTimeOffset.TryParse(
            ReadOptionalString(element, "lastModifiedDateTime"),
            out var parsedModifiedAt)
            ? parsedModifiedAt
            : DateTimeOffset.MinValue;
        long? length = element.TryGetProperty("size", out var size) && size.TryGetInt64(out var parsedSize)
            ? parsedSize
            : null;
        var isDirectory = element.TryGetProperty("folder", out var folder) && folder.ValueKind == JsonValueKind.Object;
        return new GraphItem(id, name, isDirectory, length, modifiedAt, ReadOptionalString(element, "eTag"));
    }

    private static SharePointDriveItem? ParseDriveItem(JsonElement element, string driveId) =>
        ParseGraphItem(element)?.ToSharePointDriveItem(driveId);

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string BuildItemUrl(GraphRoute route, string relativePath)
    {
        var drive = Uri.EscapeDataString(route.DriveId);
        var root = string.Equals(route.RootItemId, "root", StringComparison.OrdinalIgnoreCase)
            ? $"{GraphBaseUrl}/drives/{drive}/root"
            : $"{GraphBaseUrl}/drives/{drive}/items/{Uri.EscapeDataString(route.RootItemId)}";
        var normalized = NormalizeRelativePath(relativePath);
        return string.IsNullOrWhiteSpace(normalized)
            ? root
            : $"{root}:/{EscapeGraphPath(normalized)}:";
    }

    private static string EscapeGraphPath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private static string NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Trim() == "/")
        {
            return string.Empty;
        }

        var segments = path.Replace('\\', '/')
            .Trim()
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (string.Equals(decoded, ".", StringComparison.Ordinal) ||
                string.Equals(decoded, "..", StringComparison.Ordinal))
            {
                throw new ArgumentException("Relative SharePoint paths cannot contain dot segments.", nameof(path));
            }
        }

        return string.Join('/', segments);
    }

    private static string GetParentPath(string path)
    {
        var normalized = NormalizeRelativePath(path);
        var separator = normalized.LastIndexOf('/');
        return separator <= 0 ? string.Empty : normalized[..separator];
    }

    private static string GetFileName(string path)
    {
        var normalized = NormalizeRelativePath(path);
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    private static bool TryGetRoute(DriveRoute route, out GraphRoute graphRoute)
    {
        if (route.HasGraphIdentity &&
            IsSafeGraphIdentifier(route.DriveId) &&
            IsSafeGraphIdentifier(route.RootItemId))
        {
            graphRoute = new GraphRoute(
                route.DriveId.Trim(),
                route.RootItemId.Trim());
            return true;
        }

        graphRoute = default;
        return false;
    }

    private static bool IsSafeGraphIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 2048 ||
            value.Any(character => char.IsControl(character) || character is '/' or '\\'))
        {
            return false;
        }

        var decoded = Uri.UnescapeDataString(value.Trim());
        return !string.Equals(decoded, ".", StringComparison.Ordinal) &&
               !string.Equals(decoded, "..", StringComparison.Ordinal) &&
               !decoded.Contains('/') &&
               !decoded.Contains('\\');
    }

    private static string ValidateGraphUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Microsoft Graph returned an untrusted URL.");
        }

        return uri.AbsoluteUri;
    }

    private static bool TryValidateUploadUrl(string? value, out Uri uploadUri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            candidate.Scheme == Uri.UriSchemeHttps &&
            IsMicrosoftUploadHost(candidate.Host))
        {
            uploadUri = candidate;
            return true;
        }

        uploadUri = null!;
        return false;
    }

    private static bool IsMicrosoftUploadHost(string host) =>
        string.Equals(host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".sharepoint-df.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".1drv.com", StringComparison.OrdinalIgnoreCase);

    private static async Task<int> ReadChunkAsync(
        Stream stream,
        byte[] buffer,
        int requested,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < requested)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, requested - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task<bool> TryPositionSourceAsync(
        Stream source,
        long absoluteTarget,
        byte[] scratchBuffer,
        CancellationToken cancellationToken)
    {
        if (source.Position == absoluteTarget)
        {
            return true;
        }

        try
        {
            source.Position = absoluteTarget;
            return true;
        }
        catch (NotSupportedException)
        {
            // Restart-only decrypted streams can return to zero but not seek to arbitrary offsets.
        }
        catch (IOException)
        {
            // Fall through to restart-and-discard below.
        }

        try
        {
            source.Position = 0;
            long remaining = absoluteTarget;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(Math.Min(81_920, scratchBuffer.Length), remaining);
                var read = await source
                    .ReadAsync(scratchBuffer.AsMemory(0, requested), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return false;
                }

                remaining -= read;
            }

            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static UploadAttemptResult ToUploadResult(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent
            ? new UploadAttemptResult(
                UploadAttemptState.Succeeded,
                FailureKind: SyncFailureKind.None)
            : UploadFailureClassifier.FromStatus(
                statusCode,
                "Microsoft Graph",
                "Microsoft Graph upload");

    private static SharePointExplorerException CreateGraphFailure(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryDate)
        {
            retryAfter = retryDate > DateTimeOffset.UtcNow
                ? retryDate - DateTimeOffset.UtcNow
                : TimeSpan.Zero;
        }

        var status = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => SharePointExplorerStatus.AuthenticationRequired,
            HttpStatusCode.Forbidden => SharePointExplorerStatus.Forbidden,
            HttpStatusCode.TooManyRequests => SharePointExplorerStatus.Throttled,
            HttpStatusCode.NotFound => SharePointExplorerStatus.NotFound,
            HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                SharePointExplorerStatus.ServiceUnavailable,
            _ => SharePointExplorerStatus.InvalidResponse
        };
        return new SharePointExplorerException(
            status,
            $"O Microsoft Graph retornou HTTP {(int)response.StatusCode}.",
            response.StatusCode,
            retryAfter);
    }

    private static UploadAttemptResult Retryable(
        string message,
        SyncFailureKind failureKind = SyncFailureKind.Unknown,
        string? technicalDetails = null,
        bool isCommitAmbiguous = false) =>
        new(
            UploadAttemptState.RetryableFailure,
            message,
            FailureKind: failureKind,
            TechnicalDetails: UploadTechnicalDetails.Sanitize(technicalDetails),
            IsCommitAmbiguous: isCommitAmbiguous);

    private readonly record struct GraphRoute(string DriveId, string RootItemId);

    private readonly record struct CommittedReceiptReadResult(
        RemoteUploadReceipt? Receipt,
        string? TechnicalDetails);

    private sealed record GraphItem(
        string Id,
        string Name,
        bool IsDirectory,
        long? Length,
        DateTimeOffset ModifiedAt,
        string? ETag)
    {
        public SharePointDriveItem ToSharePointDriveItem(string driveId) =>
            new(Name, $"graph://{driveId}/{Id}", IsDirectory, Length ?? 0, ModifiedAt);
    }

    private sealed class NonDisposingReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // StreamContent owns this wrapper, not the caller's stream.
        }
    }
}
