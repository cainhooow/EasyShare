namespace EasyShare.Models;

public sealed record UploadPayloadStorageOptions
{
    public const long Mebibyte = 1024L * 1024L;
    public const long Gibibyte = 1024L * Mebibyte;

    public long MaxPayloadBytes { get; init; } = 2L * Gibibyte;

    public long MaxTotalBytes { get; init; } = 10L * Gibibyte;

    public int ChunkSizeBytes { get; init; } = 1024 * 1024;

    public TimeSpan OrphanRetention { get; init; } = TimeSpan.FromDays(7);

    public TimeSpan TemporaryFileRetention { get; init; } = TimeSpan.FromHours(6);

    internal void Validate()
    {
        if (MaxPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPayloadBytes));
        }

        if (MaxTotalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTotalBytes));
        }

        if (ChunkSizeBytes is < 64 * 1024 or > 8 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(ChunkSizeBytes));
        }

        if (OrphanRetention < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(OrphanRetention));
        }

        if (TemporaryFileRetention < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(TemporaryFileRetention));
        }
    }
}

public sealed record EncryptedPayloadInfo(
    string Path,
    long PlaintextBytes,
    long EncryptedBytes,
    int ChunkCount);

public sealed record PayloadCleanupResult(
    int TemporaryFilesDeleted,
    int OrphanFilesDeleted,
    long BytesDeleted,
    long RemainingBytes,
    long BytesOverQuota);

public sealed class PayloadQuotaExceededException : IOException
{
    public PayloadQuotaExceededException(string message)
        : base(message)
    {
    }
}

public sealed class InvalidEncryptedPayloadException : IOException
{
    public InvalidEncryptedPayloadException(string message)
        : base(message)
    {
    }

    public InvalidEncryptedPayloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
