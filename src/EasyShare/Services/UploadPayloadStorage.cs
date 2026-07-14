using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using EasyShare.Models;

namespace EasyShare.Services;

public interface IUserDataProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> protectedData);
}

/// <summary>Protects key material for the current Windows user with DPAPI.</summary>
public sealed class DpapiUserDataProtector : IUserDataProtector
{
    private static readonly byte[] OptionalEntropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("EasyShare.EncryptedFileStore.Key.v1"));

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var copy = plaintext.ToArray();
        try
        {
            return ProtectedData.Protect(copy, OptionalEntropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        var copy = protectedData.ToArray();
        try
        {
            return ProtectedData.Unprotect(copy, OptionalEntropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }
}

/// <summary>
/// Stores queued uploads in an authenticated, independently encrypted chunk format.
/// Only encrypted temporary files are written, and commits stay on the same volume.
/// </summary>
public class EncryptedFileStore
{
    private static readonly byte[] Magic = "EASYENC1"u8.ToArray();
    private static readonly byte[] KeyDerivationLabel = "EasyShare.EncryptedFileStore.DataKey.v1"u8.ToArray();
    private const byte FormatVersion = 2;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 8 + 1 + sizeof(int) + SaltSize;
    private const int RecordOverhead = sizeof(int) + NonceSize + TagSize;
    private const int FooterSize = sizeof(int) + sizeof(long) + sizeof(long) + NonceSize + TagSize;
    private const int MasterKeySize = 32;

    private readonly string _storageDirectory;
    private readonly string _keyPath;
    private readonly string _fileExtension;
    private readonly UploadPayloadStorageOptions _options;
    private readonly IUserDataProtector _protector;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly SemaphoreSlim _keyGate = new(1, 1);

    public EncryptedFileStore(
        string storageDirectory,
        string keyPath,
        UploadPayloadStorageOptions? options = null,
        IUserDataProtector? protector = null,
        string fileExtension = ".encrypted")
    {
        if (string.IsNullOrWhiteSpace(storageDirectory))
        {
            throw new ArgumentException("A private storage directory is required.", nameof(storageDirectory));
        }

        if (string.IsNullOrWhiteSpace(keyPath))
        {
            throw new ArgumentException("A protected key path is required.", nameof(keyPath));
        }

        if (string.IsNullOrWhiteSpace(fileExtension) ||
            fileExtension.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("A simple encrypted-file extension is required.", nameof(fileExtension));
        }

        _storageDirectory = Path.GetFullPath(storageDirectory);
        _keyPath = Path.GetFullPath(keyPath);
        _fileExtension = fileExtension.StartsWith('.') ? fileExtension : $".{fileExtension}";
        _options = options ?? new UploadPayloadStorageOptions();
        _options.Validate();
        _protector = protector ?? new DpapiUserDataProtector();
    }

    public UploadPayloadStorageOptions Options => _options;

    public string StorageDirectory => _storageDirectory;

    public string CreateFilePath()
    {
        EnsureStorageCreated();
        return Path.Combine(_storageDirectory, $"{Guid.NewGuid():N}{_fileExtension}");
    }

    public Task<EncryptedPayloadInfo> StoreAsync(
        string destinationPath,
        byte[] plaintext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return StoreFromMemoryAsync(destinationPath, plaintext, cancellationToken);
    }

    public async Task<EncryptedPayloadInfo> StoreAsync(
        string destinationPath,
        Stream plaintext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!plaintext.CanRead)
        {
            throw new ArgumentException("The payload stream must be readable.", nameof(plaintext));
        }

        destinationPath = ValidatePayloadPath(destinationPath);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStorageCreated();
            var existingLength = TryGetLength(destinationPath);
            var currentUsage = GetQueueUsageBytes() - existingLength;
            EnsureKnownLengthFits(plaintext, currentUsage);

            var temporaryPath = CreateTemporaryPath(destinationPath);
            try
            {
                var result = await EncryptToFileAsync(
                    temporaryPath,
                    plaintext,
                    currentUsage,
                    cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, destinationPath, overwrite: true);
                PrivateFilePermissions.TryHardenFile(destinationPath);
                return result with { Path = destinationPath };
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<bool> IsEncryptedAsync(string path, CancellationToken cancellationToken = default)
    {
        path = ValidatePayloadPath(path);
        if (!File.Exists(path))
        {
            return false;
        }

        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var prefix = new byte[Magic.Length];
        var bytesRead = await ReadUpToAsync(input, prefix, cancellationToken).ConfigureAwait(false);
        return bytesRead == Magic.Length && prefix.AsSpan().SequenceEqual(Magic);
    }

    /// <summary>
    /// Encrypts a payload left by versions that stored queue files as plaintext. The
    /// replacement is committed only after the encrypted file has been flushed.
    /// </summary>
    public async Task<bool> MigrateLegacyPayloadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        path = ValidatePayloadPath(path);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            await using (var probe = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var prefix = new byte[Magic.Length];
                var read = await ReadUpToAsync(probe, prefix, cancellationToken).ConfigureAwait(false);
                if (read == Magic.Length && prefix.AsSpan().SequenceEqual(Magic))
                {
                    return false;
                }
            }

            var originalLength = TryGetLength(path);
            var currentUsage = GetQueueUsageBytes() - originalLength;
            var temporaryPath = CreateTemporaryPath(path);
            try
            {
                await using (var plaintext = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: _options.ChunkSizeBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    EnsureKnownLengthFits(plaintext, currentUsage);
                    await EncryptToFileAsync(
                        temporaryPath,
                        plaintext,
                        currentUsage,
                        cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, path, overwrite: true);
                PrivateFilePermissions.TryHardenFile(path);
                return true;
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task DecryptToAsync(
        string path,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        path = ValidatePayloadPath(path);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DecryptToCoreAsync(path, destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Opens a bounded-memory, restartable plaintext view over an encrypted file. The
    /// returned stream owns a read lease and must be disposed. Its length/footer is
    /// authenticated before return, and every plaintext chunk is authenticated as read.
    /// </summary>
    public async Task<Stream> OpenReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        path = ValidatePayloadPath(path);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plaintextLength = await GetVerifiedPlaintextLengthAsync(path, cancellationToken)
                .ConfigureAwait(false);
            return new RestartableDecryptionStream(
                this,
                path,
                plaintextLength,
                () => _mutationGate.Release());
        }
        catch
        {
            _mutationGate.Release();
            throw;
        }
    }

    private async Task DecryptToCoreAsync(
        string path,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        }

        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: _options.ChunkSizeBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var header = new byte[HeaderSize];
        await ReadExactlyOrThrowAsync(input, header, "The encrypted payload header is incomplete.", cancellationToken)
            .ConfigureAwait(false);
        ValidateHeader(header, out var chunkSize, out var salt);

        var masterKey = await GetOrCreateMasterKeyAsync(cancellationToken).ConfigureAwait(false);
        byte[]? dataKey = null;
        try
        {
            dataKey = DeriveDataKey(masterKey, salt);
            using var aes = new AesGcm(dataKey, TagSize);
            var ciphertext = ArrayPool<byte>.Shared.Rent(chunkSize);
            var plaintext = ArrayPool<byte>.Shared.Rent(chunkSize);
            try
            {
                long totalPlaintext = 0;
                long chunkIndex = 0;
                while (true)
                {
                    var lengthBytes = new byte[sizeof(int)];
                    await ReadExactlyOrThrowAsync(
                        input,
                        lengthBytes,
                        "The encrypted payload ended before its authenticated terminator.",
                        cancellationToken).ConfigureAwait(false);
                    var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                    if (length is < 0 || length > chunkSize)
                    {
                        throw new InvalidEncryptedPayloadException("The encrypted payload contains an invalid chunk length.");
                    }

                    if (length == 0)
                    {
                        var chunkCountBytes = new byte[sizeof(long)];
                        var plaintextLengthBytes = new byte[sizeof(long)];
                        var finalNonce = new byte[NonceSize];
                        var finalTag = new byte[TagSize];
                        await ReadExactlyOrThrowAsync(
                            input,
                            chunkCountBytes,
                            "The encrypted payload footer is incomplete.",
                            cancellationToken).ConfigureAwait(false);
                        await ReadExactlyOrThrowAsync(
                            input,
                            plaintextLengthBytes,
                            "The encrypted payload footer is incomplete.",
                            cancellationToken).ConfigureAwait(false);
                        await ReadExactlyOrThrowAsync(
                            input,
                            finalNonce,
                            "The encrypted payload footer nonce is incomplete.",
                            cancellationToken).ConfigureAwait(false);
                        await ReadExactlyOrThrowAsync(
                            input,
                            finalTag,
                            "The encrypted payload footer tag is incomplete.",
                            cancellationToken).ConfigureAwait(false);

                        var storedChunkCount = BinaryPrimitives.ReadInt64LittleEndian(chunkCountBytes);
                        var storedPlaintextLength = BinaryPrimitives.ReadInt64LittleEndian(plaintextLengthBytes);
                        if (storedChunkCount != chunkIndex || storedPlaintextLength != totalPlaintext)
                        {
                            throw new InvalidEncryptedPayloadException(
                                "The encrypted payload footer does not match the authenticated chunks.");
                        }

                        try
                        {
                            aes.Decrypt(
                                finalNonce,
                                ReadOnlySpan<byte>.Empty,
                                finalTag,
                                Span<byte>.Empty,
                                BuildFinalAssociatedData(header, storedChunkCount, storedPlaintextLength));
                        }
                        catch (CryptographicException ex)
                        {
                            throw new InvalidEncryptedPayloadException(
                                "The encrypted payload footer failed authentication.",
                                ex);
                        }

                        var trailing = new byte[1];
                        if (await input.ReadAsync(trailing, cancellationToken).ConfigureAwait(false) != 0)
                        {
                            throw new InvalidEncryptedPayloadException("The encrypted payload contains trailing data.");
                        }

                        break;
                    }

                    var nonce = new byte[NonceSize];
                    var tag = new byte[TagSize];
                    await ReadExactlyOrThrowAsync(input, nonce, "The encrypted payload nonce is incomplete.", cancellationToken)
                        .ConfigureAwait(false);
                    await ReadExactlyOrThrowAsync(input, tag, "The encrypted payload tag is incomplete.", cancellationToken)
                        .ConfigureAwait(false);

                    await ReadExactlyOrThrowAsync(
                        input,
                        ciphertext.AsMemory(0, length),
                        "The encrypted payload chunk is incomplete.",
                        cancellationToken).ConfigureAwait(false);

                    var associatedData = BuildAssociatedData(header, chunkIndex, length);
                    try
                    {
                        aes.Decrypt(
                            nonce,
                            ciphertext.AsSpan(0, length),
                            tag,
                            plaintext.AsSpan(0, length),
                            associatedData);
                    }
                    catch (CryptographicException ex)
                    {
                        throw new InvalidEncryptedPayloadException(
                            "The encrypted payload failed authentication.",
                            ex);
                    }

                    totalPlaintext = checked(totalPlaintext + length);
                    if (totalPlaintext > _options.MaxPayloadBytes)
                    {
                        throw new InvalidEncryptedPayloadException("The decrypted payload exceeds the configured size limit.");
                    }

                    await destination.WriteAsync(plaintext.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    chunkIndex++;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(plaintext);
                ArrayPool<byte>.Shared.Return(ciphertext);
                ArrayPool<byte>.Shared.Return(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            if (dataKey is not null)
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
    }

    private async Task<long> GetVerifiedPlaintextLengthAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (input.Length < HeaderSize + FooterSize)
        {
            throw new InvalidEncryptedPayloadException("The encrypted payload is too short.");
        }

        var header = new byte[HeaderSize];
        await ReadExactlyOrThrowAsync(
            input,
            header,
            "The encrypted payload header is incomplete.",
            cancellationToken).ConfigureAwait(false);
        ValidateHeader(header, out var chunkSize, out var salt);

        input.Seek(-FooterSize, SeekOrigin.End);
        var footer = new byte[FooterSize];
        await ReadExactlyOrThrowAsync(
            input,
            footer,
            "The encrypted payload footer is incomplete.",
            cancellationToken).ConfigureAwait(false);
        var terminator = BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(0, sizeof(int)));
        var chunkCount = BinaryPrimitives.ReadInt64LittleEndian(
            footer.AsSpan(sizeof(int), sizeof(long)));
        var plaintextLength = BinaryPrimitives.ReadInt64LittleEndian(
            footer.AsSpan(sizeof(int) + sizeof(long), sizeof(long)));
        if (terminator != 0 || chunkCount < 0 || plaintextLength < 0 ||
            plaintextLength > _options.MaxPayloadBytes)
        {
            throw new InvalidEncryptedPayloadException("The encrypted payload footer is invalid.");
        }

        var expectedChunkCount = plaintextLength == 0
            ? 0
            : checked((plaintextLength + chunkSize - 1) / chunkSize);
        if (chunkCount != expectedChunkCount)
        {
            throw new InvalidEncryptedPayloadException("The encrypted payload chunk count is invalid.");
        }

        long expectedFileLength;
        try
        {
            expectedFileLength = checked(
                HeaderSize + plaintextLength + (chunkCount * RecordOverhead) + FooterSize);
        }
        catch (OverflowException ex)
        {
            throw new InvalidEncryptedPayloadException("The encrypted payload length is invalid.", ex);
        }

        if (input.Length != expectedFileLength)
        {
            throw new InvalidEncryptedPayloadException("The encrypted payload file length is invalid.");
        }

        var nonceOffset = sizeof(int) + sizeof(long) + sizeof(long);
        var nonce = footer.AsSpan(nonceOffset, NonceSize).ToArray();
        var tag = footer.AsSpan(nonceOffset + NonceSize, TagSize).ToArray();
        var masterKey = await GetOrCreateMasterKeyAsync(cancellationToken).ConfigureAwait(false);
        byte[]? dataKey = null;
        try
        {
            dataKey = DeriveDataKey(masterKey, salt);
            using var aes = new AesGcm(dataKey, TagSize);
            try
            {
                aes.Decrypt(
                    nonce,
                    ReadOnlySpan<byte>.Empty,
                    tag,
                    Span<byte>.Empty,
                    BuildFinalAssociatedData(header, chunkCount, plaintextLength));
            }
            catch (CryptographicException ex)
            {
                throw new InvalidEncryptedPayloadException(
                    "The encrypted payload footer failed authentication.",
                    ex);
            }

            return plaintextLength;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            if (dataKey is not null)
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
    }

    public async Task<byte[]> ReadAllBytesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var encryptedLength = TryGetLength(ValidatePayloadPath(path));
        var initialCapacity = encryptedLength is > 0 and <= 32 * 1024 * 1024
            ? (int)encryptedLength
            : 0;
        using var output = initialCapacity > 0 ? new MemoryStream(initialCapacity) : new MemoryStream();
        await DecryptToAsync(path, output, cancellationToken).ConfigureAwait(false);
        if (output.Length > Array.MaxLength)
        {
            throw new IOException("The upload API cannot materialize a payload this large in memory.");
        }

        return output.ToArray();
    }

    public async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        path = ValidatePayloadPath(path);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return TryDelete(path);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<PayloadCleanupResult> CleanupAsync(
        IEnumerable<string> retainedPayloadPaths,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retainedPayloadPaths);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStorageCreated();
            var timestamp = now ?? DateTimeOffset.UtcNow;
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in retainedPayloadPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                try
                {
                    retained.Add(ValidatePayloadPath(path));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
                                           IOException or UnauthorizedAccessException)
                {
                    // A malformed database path cannot suppress cleanup of valid store files.
                }
            }
            var temporaryDeleted = 0;
            var orphanDeleted = 0;
            long bytesDeleted = 0;

            foreach (var file in new DirectoryInfo(_storageDirectory).EnumerateFiles("*.tmp"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timestamp - file.LastWriteTimeUtc < _options.TemporaryFileRetention)
                {
                    continue;
                }

                var length = file.Length;
                if (TryDelete(file.FullName))
                {
                    temporaryDeleted++;
                    bytesDeleted += length;
                }
            }

            var orphanCandidates = new DirectoryInfo(_storageDirectory)
                .EnumerateFiles($"*{_fileExtension}")
                .Where(file => !retained.Contains(file.FullName))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToArray();
            foreach (var file in orphanCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timestamp - file.LastWriteTimeUtc < _options.OrphanRetention)
                {
                    continue;
                }

                var length = file.Length;
                if (TryDelete(file.FullName))
                {
                    orphanDeleted++;
                    bytesDeleted += length;
                }
            }

            var remaining = GetQueueUsageBytes();
            if (remaining > _options.MaxTotalBytes)
            {
                foreach (var file in orphanCandidates.Where(file => file.Exists))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = file.Length;
                    if (!TryDelete(file.FullName))
                    {
                        continue;
                    }

                    orphanDeleted++;
                    bytesDeleted += length;
                    remaining -= length;
                    if (remaining <= _options.MaxTotalBytes)
                    {
                        break;
                    }
                }
            }

            remaining = GetQueueUsageBytes();
            return new PayloadCleanupResult(
                temporaryDeleted,
                orphanDeleted,
                bytesDeleted,
                remaining,
                Math.Max(0, remaining - _options.MaxTotalBytes));
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<EncryptedPayloadInfo> StoreFromMemoryAsync(
        string destinationPath,
        byte[] plaintext,
        CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(plaintext, writable: false);
        return await StoreAsync(destinationPath, input, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EncryptedPayloadInfo> EncryptToFileAsync(
        string temporaryPath,
        Stream plaintext,
        long existingQueueUsage,
        CancellationToken cancellationToken)
    {
        var masterKey = await GetOrCreateMasterKeyAsync(cancellationToken).ConfigureAwait(false);
        byte[]? dataKey = null;
        try
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var header = BuildHeader(_options.ChunkSizeBytes, salt);
            dataKey = DeriveDataKey(masterKey, salt);
            using var aes = new AesGcm(dataKey, TagSize);
            await using var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: _options.ChunkSizeBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);

            EnsureQuota(existingQueueUsage, header.Length);
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            var plainBuffer = ArrayPool<byte>.Shared.Rent(_options.ChunkSizeBytes);
            var cipherBuffer = ArrayPool<byte>.Shared.Rent(_options.ChunkSizeBytes);
            try
            {
                long totalPlaintext = 0;
                long chunkIndex = 0;
                while (true)
                {
                    var count = await ReadChunkAsync(
                        plaintext,
                        plainBuffer.AsMemory(0, _options.ChunkSizeBytes),
                        cancellationToken)
                        .ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    totalPlaintext = checked(totalPlaintext + count);
                    if (totalPlaintext > _options.MaxPayloadBytes)
                    {
                        throw new PayloadQuotaExceededException("The payload exceeds the configured per-file limit.");
                    }

                    EnsureQuota(existingQueueUsage, checked(output.Position + RecordOverhead + count));
                    var nonce = RandomNumberGenerator.GetBytes(NonceSize);
                    var tag = new byte[TagSize];
                    var associatedData = BuildAssociatedData(header, chunkIndex, count);
                    aes.Encrypt(
                        nonce,
                        plainBuffer.AsSpan(0, count),
                        cipherBuffer.AsSpan(0, count),
                        tag,
                        associatedData);
                    await WriteRecordAsync(
                        output,
                        count,
                        nonce,
                        tag,
                        cipherBuffer.AsMemory(0, count),
                        cancellationToken).ConfigureAwait(false);
                    chunkIndex++;
                }

                EnsureQuota(existingQueueUsage, checked(output.Position + FooterSize));
                var finalNonce = RandomNumberGenerator.GetBytes(NonceSize);
                var finalTag = new byte[TagSize];
                aes.Encrypt(
                    finalNonce,
                    ReadOnlySpan<byte>.Empty,
                    Span<byte>.Empty,
                    finalTag,
                    BuildFinalAssociatedData(header, chunkIndex, totalPlaintext));
                await WriteFinalRecordAsync(
                    output,
                    chunkIndex,
                    totalPlaintext,
                    finalNonce,
                    finalTag,
                    cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                return new EncryptedPayloadInfo(temporaryPath, totalPlaintext, output.Length, checked((int)chunkIndex));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBuffer);
                CryptographicOperations.ZeroMemory(cipherBuffer);
                ArrayPool<byte>.Shared.Return(plainBuffer);
                ArrayPool<byte>.Shared.Return(cipherBuffer);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            if (dataKey is not null)
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
    }

    private async Task<byte[]> GetOrCreateMasterKeyAsync(CancellationToken cancellationToken)
    {
        await _keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStorageCreated();
            var keyDirectory = Path.GetDirectoryName(_keyPath);
            if (!string.IsNullOrWhiteSpace(keyDirectory))
            {
                Directory.CreateDirectory(keyDirectory);
                PrivateFilePermissions.TryHardenDirectory(keyDirectory);
            }

            if (File.Exists(_keyPath))
            {
                var protectedKey = await File.ReadAllBytesAsync(_keyPath, cancellationToken)
                    .ConfigureAwait(false);
                var key = _protector.Unprotect(protectedKey);
                CryptographicOperations.ZeroMemory(protectedKey);
                if (key.Length != MasterKeySize)
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw new CryptographicException("The protected upload key has an invalid length.");
                }

                return key;
            }

            var newKey = RandomNumberGenerator.GetBytes(MasterKeySize);
            try
            {
                var protectedNewKey = _protector.Protect(newKey);
                var temporaryPath = $"{_keyPath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await using (var keyOutput = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 4096,
                        FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await keyOutput.WriteAsync(protectedNewKey, cancellationToken).ConfigureAwait(false);
                        await keyOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
                        keyOutput.Flush(flushToDisk: true);
                    }

                    PrivateFilePermissions.TryHardenFile(temporaryPath);
                    try
                    {
                        File.Move(temporaryPath, _keyPath, overwrite: false);
                    }
                    catch (IOException) when (File.Exists(_keyPath))
                    {
                        CryptographicOperations.ZeroMemory(newKey);
                        var winner = await File.ReadAllBytesAsync(_keyPath, cancellationToken)
                            .ConfigureAwait(false);
                        newKey = _protector.Unprotect(winner);
                        CryptographicOperations.ZeroMemory(winner);
                        if (newKey.Length != MasterKeySize)
                        {
                            CryptographicOperations.ZeroMemory(newKey);
                            throw new CryptographicException("The protected upload key has an invalid length.");
                        }
                    }

                    PrivateFilePermissions.TryHardenFile(_keyPath);
                    return newKey;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedNewKey);
                    TryDelete(temporaryPath);
                }
            }
            catch
            {
                CryptographicOperations.ZeroMemory(newKey);
                throw;
            }
        }
        finally
        {
            _keyGate.Release();
        }
    }

    private void EnsureKnownLengthFits(Stream plaintext, long existingQueueUsage)
    {
        if (!plaintext.CanSeek)
        {
            return;
        }

        var remaining = Math.Max(0, plaintext.Length - plaintext.Position);
        if (remaining > _options.MaxPayloadBytes)
        {
            throw new PayloadQuotaExceededException("The payload exceeds the configured per-file limit.");
        }

        var chunks = remaining == 0 ? 0 : checked((remaining + _options.ChunkSizeBytes - 1) / _options.ChunkSizeBytes);
        var encryptedLength = checked(HeaderSize + remaining + (chunks * RecordOverhead) + FooterSize);
        EnsureQuota(existingQueueUsage, encryptedLength);
    }

    private void EnsureQuota(long existingQueueUsage, long newFileLength)
    {
        if (existingQueueUsage < 0 || newFileLength < 0 ||
            newFileLength > _options.MaxTotalBytes - Math.Min(existingQueueUsage, _options.MaxTotalBytes))
        {
            throw new PayloadQuotaExceededException("The encrypted upload queue has reached its configured quota.");
        }
    }

    private long GetQueueUsageBytes()
    {
        try
        {
            if (!Directory.Exists(_storageDirectory))
            {
                return 0;
            }

            long total = 0;
            foreach (var file in new DirectoryInfo(_storageDirectory).EnumerateFiles())
            {
                total = checked(total + file.Length);
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            // Quota checks fail closed if any existing storage cannot be measured.
            return long.MaxValue;
        }
    }

    private string ValidatePayloadPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A payload path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var queuePath = _storageDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(fullPath)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!string.Equals(parent, queuePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(fullPath), _fileExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Upload payloads must stay in the private upload queue directory.");
        }

        return fullPath;
    }

    private void EnsureStorageCreated()
    {
        Directory.CreateDirectory(_storageDirectory);
        PrivateFilePermissions.TryHardenDirectory(_storageDirectory);
    }

    private static byte[] BuildHeader(int chunkSize, ReadOnlySpan<byte> salt)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[Magic.Length] = FormatVersion;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(Magic.Length + 1, sizeof(int)), chunkSize);
        salt.CopyTo(header.AsSpan(Magic.Length + 1 + sizeof(int), SaltSize));
        return header;
    }

    private static void ValidateHeader(byte[] header, out int chunkSize, out byte[] salt)
    {
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidEncryptedPayloadException("The upload payload is not encrypted in a supported format.");
        }

        if (header[Magic.Length] != FormatVersion)
        {
            throw new InvalidEncryptedPayloadException("The encrypted upload payload version is not supported.");
        }

        chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(Magic.Length + 1, sizeof(int)));
        if (chunkSize is < 64 * 1024 or > 8 * 1024 * 1024)
        {
            throw new InvalidEncryptedPayloadException("The encrypted payload chunk size is invalid.");
        }

        salt = header.AsSpan(Magic.Length + 1 + sizeof(int), SaltSize).ToArray();
    }

    private static byte[] DeriveDataKey(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> salt)
    {
        var material = new byte[KeyDerivationLabel.Length + salt.Length];
        KeyDerivationLabel.CopyTo(material, 0);
        salt.CopyTo(material.AsSpan(KeyDerivationLabel.Length));
        try
        {
            return HMACSHA256.HashData(masterKey, material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static byte[] BuildAssociatedData(byte[] header, long chunkIndex, int length)
    {
        var associatedData = new byte[header.Length + sizeof(long) + sizeof(int)];
        header.CopyTo(associatedData, 0);
        BinaryPrimitives.WriteInt64LittleEndian(associatedData.AsSpan(header.Length, sizeof(long)), chunkIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            associatedData.AsSpan(header.Length + sizeof(long), sizeof(int)),
            length);
        return associatedData;
    }

    private static byte[] BuildFinalAssociatedData(
        byte[] header,
        long chunkCount,
        long plaintextLength)
    {
        var associatedData = new byte[header.Length + sizeof(long) + sizeof(int) + sizeof(long)];
        header.CopyTo(associatedData, 0);
        BinaryPrimitives.WriteInt64LittleEndian(
            associatedData.AsSpan(header.Length, sizeof(long)),
            chunkCount);
        BinaryPrimitives.WriteInt32LittleEndian(
            associatedData.AsSpan(header.Length + sizeof(long), sizeof(int)),
            0);
        BinaryPrimitives.WriteInt64LittleEndian(
            associatedData.AsSpan(header.Length + sizeof(long) + sizeof(int), sizeof(long)),
            plaintextLength);
        return associatedData;
    }

    private static async Task WriteRecordAsync(
        Stream output,
        int length,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> tag,
        ReadOnlyMemory<byte> ciphertext,
        CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, length);
        await output.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(nonce, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
        if (!ciphertext.IsEmpty)
        {
            await output.WriteAsync(ciphertext, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteFinalRecordAsync(
        Stream output,
        long chunkCount,
        long plaintextLength,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> tag,
        CancellationToken cancellationToken)
    {
        var footerPrefix = new byte[sizeof(int) + sizeof(long) + sizeof(long)];
        BinaryPrimitives.WriteInt32LittleEndian(footerPrefix.AsSpan(0, sizeof(int)), 0);
        BinaryPrimitives.WriteInt64LittleEndian(
            footerPrefix.AsSpan(sizeof(int), sizeof(long)),
            chunkCount);
        BinaryPrimitives.WriteInt64LittleEndian(
            footerPrefix.AsSpan(sizeof(int) + sizeof(long), sizeof(long)),
            plaintextLength);
        await output.WriteAsync(footerPrefix, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(nonce, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadChunkAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task ReadExactlyOrThrowAsync(
        Stream input,
        Memory<byte> buffer,
        string error,
        CancellationToken cancellationToken)
    {
        try
        {
            await input.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidEncryptedPayloadException(error, ex);
        }
    }

    private static async Task<int> ReadUpToAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static long TryGetLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string CreateTemporaryPath(string destinationPath) =>
        $"{destinationPath}.{Guid.NewGuid():N}.tmp";

    private static bool TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }

            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class RestartableDecryptionStream : Stream
    {
        private readonly EncryptedFileStore _owner;
        private readonly string _path;
        private readonly long _length;
        private readonly Action _releaseLease;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _ioGate = new(1, 1);
        private Generation _generation;
        private byte[]? _current;
        private int _currentOffset;
        private long _position;
        private int _disposed;

        public RestartableDecryptionStream(
            EncryptedFileStore owner,
            string path,
            long length,
            Action releaseLease)
        {
            _owner = owner;
            _path = path;
            _length = length;
            _releaseLease = releaseLease;
            _generation = StartGeneration();
        }

        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => Volatile.Read(ref _disposed) == 0;
        public override bool CanWrite => false;
        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                return _length;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return _position;
            }
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (buffer.IsEmpty)
            {
                return 0;
            }

            await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                while (_current is null || _currentOffset >= _current.Length)
                {
                    ClearCurrent();
                    if (_generation.Channel.Reader.TryRead(out var next))
                    {
                        _current = next;
                        _currentOffset = 0;
                        break;
                    }

                    bool canRead;
                    try
                    {
                        canRead = await _generation.Channel.Reader
                            .WaitToReadAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (ChannelClosedException)
                    {
                        await _generation.Producer.ConfigureAwait(false);
                        canRead = false;
                    }

                    if (canRead)
                    {
                        continue;
                    }

                    await _generation.Producer.ConfigureAwait(false);
                    if (_position != _length)
                    {
                        throw new InvalidEncryptedPayloadException(
                            "The encrypted stream length changed during authenticated reading.");
                    }

                    return 0;
                }

                var count = Math.Min(buffer.Length, _current!.Length - _currentOffset);
                _current.AsMemory(_currentOffset, count).CopyTo(buffer);
                _currentOffset += count;
                _position = checked(_position + count);
                if (_position > _length)
                {
                    throw new InvalidEncryptedPayloadException(
                        "The encrypted stream produced more plaintext than its verified length.");
                }

                if (_currentOffset >= _current.Length)
                {
                    ClearCurrent();
                }

                return count;
            }
            finally
            {
                _ioGate.Release();
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target == _position)
            {
                return _position;
            }

            if (target != 0)
            {
                throw new NotSupportedException(
                    "Encrypted streams support replay from the beginning, but not arbitrary seeking.");
            }

            _ioGate.Wait();
            try
            {
                ThrowIfDisposed();
                StopGenerationAsync(_generation).GetAwaiter().GetResult();
                ClearCurrent();
                _position = 0;
                _generation = StartGeneration();
                return 0;
            }
            finally
            {
                _ioGate.Release();
            }
        }

        public override void Flush()
        {
            ThrowIfDisposed();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return Task.CompletedTask;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                base.Dispose(disposing);
                return;
            }

            _lifetime.Cancel();
            _ioGate.Wait();
            try
            {
                StopGenerationAsync(_generation).GetAwaiter().GetResult();
                ClearCurrent();
            }
            finally
            {
                _ioGate.Release();
                _ioGate.Dispose();
                _lifetime.Dispose();
                _releaseLease();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _lifetime.Cancel();
            await _ioGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopGenerationAsync(_generation).ConfigureAwait(false);
                ClearCurrent();
            }
            finally
            {
                _ioGate.Release();
                _ioGate.Dispose();
                _lifetime.Dispose();
                _releaseLease();
            }

            GC.SuppressFinalize(this);
        }

        private Generation StartGeneration()
        {
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var generation = new Generation(channel, cancellation);
            generation.Producer = ProduceAsync(generation);
            return generation;
        }

        private async Task ProduceAsync(Generation generation)
        {
            try
            {
                await using var destination = new ChannelWriteStream(
                    generation.Channel.Writer,
                    generation.Cancellation.Token);
                await _owner
                    .DecryptToCoreAsync(_path, destination, generation.Cancellation.Token)
                    .ConfigureAwait(false);
                generation.Channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                generation.Channel.Writer.TryComplete(ex);
                throw;
            }
        }

        private static async Task StopGenerationAsync(Generation generation)
        {
            generation.Cancellation.Cancel();
            try
            {
                await generation.Producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (generation.Cancellation.IsCancellationRequested)
            {
                // Replay and disposal intentionally cancel the previous producer.
            }
            catch
            {
                // A consumer observes producer failures while reading. Cleanup still
                // drains plaintext buffers without replacing that earlier result.
            }

            while (generation.Channel.Reader.TryRead(out var buffered))
            {
                CryptographicOperations.ZeroMemory(buffered);
            }

            generation.Cancellation.Dispose();
        }

        private void ClearCurrent()
        {
            if (_current is not null)
            {
                CryptographicOperations.ZeroMemory(_current);
                _current = null;
            }

            _currentOffset = 0;
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        private sealed class Generation(
            Channel<byte[]> channel,
            CancellationTokenSource cancellation)
        {
            public Channel<byte[]> Channel { get; } = channel;
            public CancellationTokenSource Cancellation { get; } = cancellation;
            public Task Producer { get; set; } = Task.CompletedTask;
        }
    }

    private sealed class ChannelWriteStream(
        ChannelWriter<byte[]> writer,
        CancellationToken lifetimeToken) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeToken,
                cancellationToken);
            var copy = buffer.ToArray();
            try
            {
                await writer.WriteAsync(copy, linkedCancellation.Token).ConfigureAwait(false);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(copy);
                throw;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
