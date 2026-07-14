namespace EasyShare.Models;

public enum DiagnosticLevel
{
    Trace,
    Information,
    Warning,
    Error,
    Critical
}

public sealed record DiagnosticLogOptions
{
    public long MaxFileBytes { get; init; } = 2L * 1024 * 1024;

    public int MaxArchiveFiles { get; init; } = 4;

    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(14);

    public int MaxEventCharacters { get; init; } = 16 * 1024;

    internal void Validate()
    {
        if (MaxFileBytes is < 64 * 1024 or > 32L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFileBytes));
        }

        if (MaxArchiveFiles is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxArchiveFiles));
        }

        if (Retention < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Retention));
        }

        if (MaxEventCharacters is < 256 or > 128 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEventCharacters));
        }
    }
}

public sealed record DiagnosticEvent(
    DateTimeOffset Timestamp,
    DiagnosticLevel Level,
    string EventName,
    string Message,
    Exception? Exception = null,
    IReadOnlyDictionary<string, string?>? Properties = null)
{
    public static DiagnosticEvent Create(
        DiagnosticLevel level,
        string eventName,
        string message,
        Exception? exception = null,
        IReadOnlyDictionary<string, string?>? properties = null) =>
        new(DateTimeOffset.UtcNow, level, eventName, message, exception, properties);
}

public sealed record SupportBundleOptions
{
    public long MaxUncompressedLogBytes { get; init; } = 20L * 1024 * 1024;

    public long MaxBundleBytes { get; init; } = 25L * 1024 * 1024;

    public int MaxMetadataEntries { get; init; } = 32;
}

public sealed record SupportBundleContext(
    EnterprisePolicySnapshot? Policy = null,
    IReadOnlyDictionary<string, string?>? Metadata = null);

public sealed record SupportBundleResult(
    bool Succeeded,
    string? Path,
    long SizeBytes,
    int IncludedLogFiles,
    string? Error = null);
