using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EasyShare.Models;

namespace EasyShare.Services;

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The upload stream must be readable.", nameof(content));
        }

        if (!TryGetRoute(route, out var graphRoute))
        {
            return Retryable("A rota ainda não possui identificadores do Microsoft Graph.");
        }

        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(GetFileName(normalized)))
        {
            return Retryable("Caminho de arquivo inválido.");
        }

        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return Retryable("Entre novamente para enviar arquivos ao SharePoint.");
        }

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
                        "O arquivo remoto mudou enquanto o arquivo local estava sendo editado.");
                }

                if (string.IsNullOrWhiteSpace(current.ETag))
                {
                    return new UploadAttemptResult(
                        UploadAttemptState.Conflict,
                        "O Microsoft Graph não forneceu a versão necessária para substituir o arquivo com segurança.");
                }

                expectedETag = current.ETag;
                existingItemId = current.Id;
            }

            var remainingLength = content.CanSeek ? content.Length - content.Position : (long?)null;
            if (remainingLength is null)
            {
                return Retryable(
                    "O Microsoft Graph exige um fluxo reposicionável com tamanho conhecido para uploads seguros.");
            }

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
                    cancellationToken).ConfigureAwait(false);
            }

            using var streamContent = new StreamContent(new NonDisposingReadStream(content), 81_920);
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
            return ToUploadResult(response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Retryable("Não foi possível enviar o arquivo pelo Microsoft Graph agora.");
        }
    }

    public async Task<bool> DeleteItemAsync(
        DriveRoute route,
        string relativePath,
        bool isDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        _ = isDirectory;
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
            using var response = await SendGraphAsync(
                HttpMethod.Delete,
                BuildItemUrl(graphRoute, normalized),
                token,
                content: null,
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
        CancellationToken cancellationToken)
    {
        if (!source.CanSeek)
        {
            return Retryable("Arquivos acima de 250 MB precisam de um fluxo com tamanho conhecido para envio em partes.");
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
            return Retryable("O Microsoft Graph não retornou uma sessão de upload confiável.");
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
                    return Retryable("A sessão de upload não avançou após três tentativas no mesmo intervalo.");
                }

                offsetAttempts[offset] = attemptsAtOffset + 1;
                if (!await TryPositionSourceAsync(
                        source,
                        sourceStart + offset,
                        buffer,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return Retryable("Não foi possível reposicionar o fluxo para retomar a sessão de upload.");
                }

                var requested = (int)Math.Min(UploadChunkSize, totalLength - offset);
                var read = await ReadChunkAsync(source, buffer, requested, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    return Retryable("O fluxo de upload terminou antes do tamanho esperado.");
                }

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
                    return new UploadAttemptResult(UploadAttemptState.Succeeded);
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
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    var serverOffset = await QueryUploadOffsetAsync(uploadUrl, totalLength, cancellationToken)
                        .ConfigureAwait(false);
                    if (serverOffset is not null)
                    {
                        offset = serverOffset.Value;
                        continue;
                    }
                }

                return ToUploadResult(response.StatusCode);
            }

            return Retryable("A sessão de upload foi encerrada sem confirmação do Microsoft Graph.");
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
                if (attempt >= maximumAttempts || !IsTransientUploadStatus(response.StatusCode))
                {
                    return response;
                }

                var delay = GetRetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < maximumAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && attempt < maximumAttempts)
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
        return ParseGraphItem(document.RootElement);
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
        var length = element.TryGetProperty("size", out var size) && size.TryGetInt64(out var parsedSize)
            ? parsedSize
            : 0;
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
        statusCode switch
        {
            HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent =>
                new UploadAttemptResult(UploadAttemptState.Succeeded),
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed =>
                new UploadAttemptResult(
                    UploadAttemptState.Conflict,
                    "O item remoto foi alterado ou já existe no destino."),
            _ => Retryable($"O Microsoft Graph retornou HTTP {(int)statusCode} durante o upload.")
        };

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

    private static UploadAttemptResult Retryable(string message) =>
        new(UploadAttemptState.RetryableFailure, message);

    private readonly record struct GraphRoute(string DriveId, string RootItemId);

    private sealed record GraphItem(
        string Id,
        string Name,
        bool IsDirectory,
        long Length,
        DateTimeOffset ModifiedAt,
        string? ETag)
    {
        public SharePointDriveItem ToSharePointDriveItem(string driveId) =>
            new(Name, $"graph://{driveId}/{Id}", IsDirectory, Length, ModifiedAt);
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
