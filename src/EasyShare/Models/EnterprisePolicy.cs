namespace EasyShare.Models;

public enum EnterprisePolicySource
{
    Defaults,
    CurrentUser,
    LocalMachine
}

public enum EnterprisePolicyIssueSeverity
{
    Warning,
    Error
}

public sealed record EnterprisePolicyIssue(
    EnterprisePolicySource Source,
    EnterprisePolicyIssueSeverity Severity,
    string Field,
    string Message);

/// <summary>
/// Contains only administrative configuration. Credentials, tokens, cookies, and
/// client secrets are deliberately absent from the policy schema.
/// </summary>
public sealed record EnterprisePolicy
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public bool BrowserSessionAllowed { get; init; } = true;

    public bool InteractiveSignInAllowed { get; init; } = true;

    public bool AutomaticUpdatesRequired { get; init; }

    public bool SupportBundlesAllowed { get; init; } = true;

    public string UpdateChannel { get; init; } = "automatic";

    public long UploadQueueQuotaBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    public long MaxUploadPayloadBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public int PayloadRetentionDays { get; init; } = 7;

    public int DiagnosticRetentionDays { get; init; } = 14;

    public long DiagnosticMaxFileBytes { get; init; } = 2L * 1024 * 1024;

    public int DiagnosticMaxArchiveFiles { get; init; } = 4;

    public IReadOnlyList<string> AllowedSharePointHosts { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AllowedTenantIds { get; init; } = Array.Empty<string>();

    public string? TenantId { get; init; }

    public string? ClientId { get; init; }

    public string? MountPoint { get; init; }

    public bool? StartWithWindows { get; init; }

    public bool? AutoStartVirtualDrive { get; init; }

    public int? CacheMinutes { get; init; }

    public int? OfflineCacheLimitMb { get; init; }
}

public sealed record EnterprisePolicySnapshot(
    EnterprisePolicy Policy,
    bool IsManaged,
    IReadOnlyList<EnterprisePolicySource> AppliedSources,
    IReadOnlyList<EnterprisePolicyIssue> Issues,
    IReadOnlySet<string>? ManagedFields = null)
{
    public bool IsFieldManaged(string field) =>
        ManagedFields?.Contains(field) == true;

    public UploadPayloadStorageOptions CreateUploadPayloadStorageOptions() => new()
    {
        MaxPayloadBytes = Policy.MaxUploadPayloadBytes,
        MaxTotalBytes = Policy.UploadQueueQuotaBytes,
        OrphanRetention = TimeSpan.FromDays(Policy.PayloadRetentionDays)
    };

    public DiagnosticLogOptions CreateDiagnosticLogOptions() => new()
    {
        MaxFileBytes = Policy.DiagnosticMaxFileBytes,
        MaxArchiveFiles = Policy.DiagnosticMaxArchiveFiles,
        Retention = TimeSpan.FromDays(Policy.DiagnosticRetentionDays)
    };
}
