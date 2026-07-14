using System.Globalization;
using System.Text.Json;
using EasyShare.Models;

namespace EasyShare.Services;

/// <summary>
/// Loads strict JSON policy layers with deterministic precedence:
/// built-in defaults &lt; current-user policy &lt; local-machine policy.
/// Invalid layers are rejected in full rather than partially weakening policy.
/// </summary>
public sealed class EnterprisePolicyLoader
{
    private const long MaxPolicyFileBytes = 256 * 1024;
    private const long MinQueueQuotaBytes = 16L * 1024 * 1024;
    private const long MaxQueueQuotaBytes = 10L * 1024 * 1024 * 1024 * 1024;
    private const long MinPayloadBytes = 1024L * 1024;
    private const long MaxPayloadBytes = 2L * 1024 * 1024 * 1024 * 1024;
    private const long MinDiagnosticFileBytes = 64L * 1024;
    private const long MaxDiagnosticFileBytes = 32L * 1024 * 1024;

    private static readonly HashSet<string> KnownProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "schemaVersion",
        "browserSessionAllowed",
        "interactiveSignInAllowed",
        "automaticUpdatesRequired",
        "supportBundlesAllowed",
        "updateChannel",
        "uploadQueueQuotaBytes",
        "maxUploadPayloadBytes",
        "payloadRetentionDays",
        "diagnosticRetentionDays",
        "diagnosticMaxFileBytes",
        "diagnosticMaxArchiveFiles",
        "allowedSharePointHosts",
        "allowedTenantIds",
        "tenantId",
        "clientId",
        "mountPoint",
        "startWithWindows",
        "autoStartVirtualDrive",
        "cacheMinutes",
        "offlineCacheLimitMb"
    };

    private readonly string _userPolicyPath;
    private readonly string _machinePolicyPath;

    public EnterprisePolicyLoader(
        AppDataPaths paths,
        string? userPolicyPath = null,
        string? machinePolicyPath = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _userPolicyPath = Path.GetFullPath(userPolicyPath ?? paths.UserPolicyPath);
        _machinePolicyPath = Path.GetFullPath(machinePolicyPath ?? paths.MachinePolicyPath);
    }

    public EnterprisePolicySnapshot Load() => LoadAsync().GetAwaiter().GetResult();

    public async Task<EnterprisePolicySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var effective = new EnterprisePolicy();
        var issues = new List<EnterprisePolicyIssue>();
        var sources = new List<EnterprisePolicySource> { EnterprisePolicySource.Defaults };
        var managedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var userLayer = await TryLoadLayerAsync(
            _userPolicyPath,
            EnterprisePolicySource.CurrentUser,
            issues,
            cancellationToken).ConfigureAwait(false);
        if (userLayer is not null)
        {
            effective = Apply(effective, userLayer);
            sources.Add(EnterprisePolicySource.CurrentUser);
            managedFields.UnionWith(userLayer.ConfiguredFields);
        }

        var machineLayer = await TryLoadLayerAsync(
            _machinePolicyPath,
            EnterprisePolicySource.LocalMachine,
            issues,
            cancellationToken).ConfigureAwait(false);
        if (machineLayer is not null)
        {
            effective = Apply(effective, machineLayer);
            sources.Add(EnterprisePolicySource.LocalMachine);
            managedFields.UnionWith(machineLayer.ConfiguredFields);
        }

        if (effective.MaxUploadPayloadBytes > effective.UploadQueueQuotaBytes)
        {
            issues.Add(new EnterprisePolicyIssue(
                sources[^1],
                EnterprisePolicyIssueSeverity.Warning,
                "maxUploadPayloadBytes",
                "The effective per-file limit was reduced to the effective queue quota."));
            effective = effective with { MaxUploadPayloadBytes = effective.UploadQueueQuotaBytes };
        }

        return new EnterprisePolicySnapshot(
            effective,
            IsManaged: sources.Count > 1,
            sources,
            issues,
            managedFields);
    }

    private static async Task<PolicyLayer?> TryLoadLayerAsync(
        string path,
        EnterprisePolicySource source,
        List<EnterprisePolicyIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(path);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                AddError(issues, source, "$", "Policy files cannot be symbolic links or reparse points.");
                return null;
            }

            if (file.Length > MaxPolicyFileBytes)
            {
                AddError(issues, source, "$", "The policy file exceeds the 256 KiB limit.");
                return null;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            return ParseLayer(document.RootElement, source, issues);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AddError(issues, source, "$", "The policy file could not be read as valid JSON.");
            return null;
        }
    }

    private static PolicyLayer? ParseLayer(
        JsonElement root,
        EnterprisePolicySource source,
        List<EnterprisePolicyIssue> issues)
    {
        var issueStart = issues.Count;
        if (root.ValueKind != JsonValueKind.Object)
        {
            AddError(issues, source, "$", "The policy root must be a JSON object.");
            return null;
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                AddError(issues, source, property.Name, "Duplicate policy properties are not allowed.");
                continue;
            }

            if (!KnownProperties.Contains(property.Name))
            {
                var message = LooksSensitive(property.Name)
                    ? "Secrets and credential-like properties are forbidden in policy files."
                    : "This property is not part of the supported policy schema.";
                AddError(issues, source, property.Name, message);
            }
        }

        var schemaVersion = ReadRequiredInt(properties, "schemaVersion", source, issues);
        if (schemaVersion is not null && schemaVersion != EnterprisePolicy.CurrentSchemaVersion)
        {
            AddError(issues, source, "schemaVersion", "This policy schema version is not supported.");
        }

        var layer = new PolicyLayer
        {
            BrowserSessionAllowed = ReadBool(properties, "browserSessionAllowed", source, issues),
            InteractiveSignInAllowed = ReadBool(properties, "interactiveSignInAllowed", source, issues),
            AutomaticUpdatesRequired = ReadBool(properties, "automaticUpdatesRequired", source, issues),
            SupportBundlesAllowed = ReadBool(properties, "supportBundlesAllowed", source, issues),
            UpdateChannel = ReadUpdateChannel(properties, source, issues),
            UploadQueueQuotaBytes = ReadLong(
                properties,
                "uploadQueueQuotaBytes",
                MinQueueQuotaBytes,
                MaxQueueQuotaBytes,
                source,
                issues),
            MaxUploadPayloadBytes = ReadLong(
                properties,
                "maxUploadPayloadBytes",
                MinPayloadBytes,
                MaxPayloadBytes,
                source,
                issues),
            PayloadRetentionDays = ReadInt(properties, "payloadRetentionDays", 1, 365, source, issues),
            DiagnosticRetentionDays = ReadInt(properties, "diagnosticRetentionDays", 1, 365, source, issues),
            DiagnosticMaxFileBytes = ReadLong(
                properties,
                "diagnosticMaxFileBytes",
                MinDiagnosticFileBytes,
                MaxDiagnosticFileBytes,
                source,
                issues),
            DiagnosticMaxArchiveFiles = ReadInt(
                properties,
                "diagnosticMaxArchiveFiles",
                0,
                20,
                source,
                issues),
            AllowedSharePointHosts = ReadStringArray(
                properties,
                "allowedSharePointHosts",
                ValidateHostPattern,
                source,
                issues),
            AllowedTenantIds = ReadStringArray(
                properties,
                "allowedTenantIds",
                ValidateTenant,
                source,
                issues),
            TenantId = ReadString(properties, "tenantId", ValidateTenant, source, issues),
            ClientId = ReadString(properties, "clientId", ValidateClientId, source, issues),
            MountPoint = ReadString(properties, "mountPoint", ValidateMountPoint, source, issues),
            StartWithWindows = ReadBool(properties, "startWithWindows", source, issues),
            AutoStartVirtualDrive = ReadBool(properties, "autoStartVirtualDrive", source, issues),
            CacheMinutes = ReadInt(properties, "cacheMinutes", 1, 1440, source, issues),
            OfflineCacheLimitMb = ReadInt(properties, "offlineCacheLimitMb", 128, 102400, source, issues),
            ConfiguredFields = properties.Keys
                .Where(name => !string.Equals(name, "schemaVersion", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
        };

        if (layer.MaxUploadPayloadBytes is not null &&
            layer.UploadQueueQuotaBytes is not null &&
            layer.MaxUploadPayloadBytes > layer.UploadQueueQuotaBytes)
        {
            AddError(
                issues,
                source,
                "maxUploadPayloadBytes",
                "The per-file limit cannot exceed the queue quota in the same policy layer.");
        }

        return issues.Count == issueStart ? layer : null;
    }

    private static EnterprisePolicy Apply(EnterprisePolicy policy, PolicyLayer layer) => policy with
    {
        BrowserSessionAllowed = layer.BrowserSessionAllowed ?? policy.BrowserSessionAllowed,
        InteractiveSignInAllowed = layer.InteractiveSignInAllowed ?? policy.InteractiveSignInAllowed,
        AutomaticUpdatesRequired = layer.AutomaticUpdatesRequired ?? policy.AutomaticUpdatesRequired,
        SupportBundlesAllowed = layer.SupportBundlesAllowed ?? policy.SupportBundlesAllowed,
        UpdateChannel = layer.UpdateChannel ?? policy.UpdateChannel,
        UploadQueueQuotaBytes = layer.UploadQueueQuotaBytes ?? policy.UploadQueueQuotaBytes,
        MaxUploadPayloadBytes = layer.MaxUploadPayloadBytes ?? policy.MaxUploadPayloadBytes,
        PayloadRetentionDays = layer.PayloadRetentionDays ?? policy.PayloadRetentionDays,
        DiagnosticRetentionDays = layer.DiagnosticRetentionDays ?? policy.DiagnosticRetentionDays,
        DiagnosticMaxFileBytes = layer.DiagnosticMaxFileBytes ?? policy.DiagnosticMaxFileBytes,
        DiagnosticMaxArchiveFiles = layer.DiagnosticMaxArchiveFiles ?? policy.DiagnosticMaxArchiveFiles,
        AllowedSharePointHosts = layer.AllowedSharePointHosts ?? policy.AllowedSharePointHosts,
        AllowedTenantIds = layer.AllowedTenantIds ?? policy.AllowedTenantIds,
        TenantId = layer.TenantId ?? policy.TenantId,
        ClientId = layer.ClientId ?? policy.ClientId,
        MountPoint = layer.MountPoint ?? policy.MountPoint,
        StartWithWindows = layer.StartWithWindows ?? policy.StartWithWindows,
        AutoStartVirtualDrive = layer.AutoStartVirtualDrive ?? policy.AutoStartVirtualDrive,
        CacheMinutes = layer.CacheMinutes ?? policy.CacheMinutes,
        OfflineCacheLimitMb = layer.OfflineCacheLimitMb ?? policy.OfflineCacheLimitMb
    };

    private static string? ReadString(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        Func<string, string?> validator,
        EnterprisePolicySource source,
        List<EnterprisePolicyIssue> issues)
    {
        if (!properties.TryGetValue(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            AddError(issues, source, name, "This property must be a non-empty string.");
            return null;
        }

        var normalized = validator(value.GetString()!);
        if (normalized is null)
        {
            AddError(issues, source, name, "This property contains an invalid value.");
        }

        return normalized;
    }

    private static int? ReadRequiredInt(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        EnterprisePolicySource source,
        List<EnterprisePolicyIssue> issues)
    {
        if (!properties.TryGetValue(name, out var value))
        {
            AddError(issues, source, name, "This required property is missing.");
            return null;
        }

        if (!value.TryGetInt32(out var parsed))
        {
            AddError(issues, source, name, "This property must be an integer.");
            return null;
        }

        return parsed;
    }

    private static bool? ReadBool(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        EnterprisePolicySource source,
        List<EnterprisePolicyIssue> issues)
    {
        if (!properties.TryGetValue(name, out var value))
        {
            return null;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            AddError(issues, source, name, "This property must be a boolean.");
            return null;
        }

        return value.GetBoolean();
    }

    private static int? ReadInt(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        int min,
        int max,
        EnterprisePolicySource source,
        List<EnterprisePolicyIssue> issues)
    {
        if (!properties.TryGetValue(name, out var value))
        {
            return null;
        }

        if (!value.TryGetInt32(out var parsed) || parsed < min || parsed > max)
        {
            AddError(issues, source, name, $"This property must be an integer from {min} through {max}.");
            return null;
        }

        return parsed;
    }

    private static long? ReadLong(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        long min,
        long max,
        EnterprisePolicySource source,
        List<EnterprisePolicyIssue> issues)
    {
        if (!properties.TryGetValue(name, out var value))
        {
            return null;
        }

        if (!value.TryGetInt64(out var parsed) || parsed < min || parsed > max)
        {
            AddError(issues, source, name, $"This property must be an integer from {min} through {max}.");
            return null;
        }

        return parsed;
    }

    private static string? ReadUpdateChannel(
        IReadOnlyDictionary<string, JsonElement> properties,
        EnterprisePolicySource source,
        List<EnterprisePolicyIssue> issues)
    {
        if (!properties.TryGetValue("updateChannel", out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            AddError(issues, source, "updateChannel", "The update channel must be a string.");
            return null;
        }

        var normalized = value.GetString()?.Trim() switch
        {
            var channel when string.Equals(channel, "automatic", StringComparison.OrdinalIgnoreCase) => "automatic",
            var channel when string.Equals(channel, "microsoftStore", StringComparison.OrdinalIgnoreCase) => "microsoftStore",
            var channel when string.Equals(channel, "githubReleases", StringComparison.OrdinalIgnoreCase) => "githubReleases",
            _ => null
        };
        if (normalized is null)
        {
            AddError(
                issues,
                source,
                "updateChannel",
                "Allowed values are automatic, microsoftStore, and githubReleases.");
        }

        return normalized;
    }

    private static IReadOnlyList<string>? ReadStringArray(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        Func<string, string?> validator,
        EnterprisePolicySource source,
        List<EnterprisePolicyIssue> issues)
    {
        if (!properties.TryGetValue(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 100)
        {
            AddError(issues, source, name, "This property must be an array with at most 100 strings.");
            return null;
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            {
                AddError(issues, source, name, "Every entry must be a non-empty string.");
                continue;
            }

            var normalized = validator(element.GetString()!);
            if (normalized is null)
            {
                AddError(issues, source, name, "The array contains an invalid value.");
                continue;
            }

            result.Add(normalized);
        }

        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? ValidateHostPattern(string value)
    {
        var normalized = value.Trim().TrimEnd('.').ToLowerInvariant();
        var host = normalized.StartsWith("*.", StringComparison.Ordinal) ? normalized[2..] : normalized;
        if (host.Length is 0 or > 253 ||
            !host.Contains('.') ||
            host.Contains('/') ||
            host.Contains(':') ||
            host.Contains('@') ||
            Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            return null;
        }

        return normalized;
    }

    private static string? ValidateTenant(string value)
    {
        var normalized = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (Guid.TryParse(normalized, out var tenantId))
        {
            return tenantId.ToString("D");
        }

        if (normalized.Length > 253 ||
            !normalized.Contains('.') ||
            Uri.CheckHostName(normalized) != UriHostNameType.Dns)
        {
            return null;
        }

        try
        {
            return new IdnMapping().GetAscii(normalized).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? ValidateClientId(string value) =>
        Guid.TryParse(value.Trim(), out var id) && id != Guid.Empty
            ? id.ToString("D")
            : null;

    private static string? ValidateMountPoint(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        return normalized switch
        {
            { Length: 1 } when char.IsLetter(normalized[0]) => normalized + ":",
            { Length: 2 } when char.IsLetter(normalized[0]) && normalized[1] == ':' => normalized,
            _ => null
        };
    }

    private static bool LooksSensitive(string name) =>
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("credential", StringComparison.OrdinalIgnoreCase);

    private static void AddError(
        ICollection<EnterprisePolicyIssue> issues,
        EnterprisePolicySource source,
        string field,
        string message) =>
        issues.Add(new EnterprisePolicyIssue(
            source,
            EnterprisePolicyIssueSeverity.Error,
            field,
            message));

    private sealed class PolicyLayer
    {
        public bool? BrowserSessionAllowed { get; init; }
        public bool? InteractiveSignInAllowed { get; init; }
        public bool? AutomaticUpdatesRequired { get; init; }
        public bool? SupportBundlesAllowed { get; init; }
        public string? UpdateChannel { get; init; }
        public long? UploadQueueQuotaBytes { get; init; }
        public long? MaxUploadPayloadBytes { get; init; }
        public int? PayloadRetentionDays { get; init; }
        public int? DiagnosticRetentionDays { get; init; }
        public long? DiagnosticMaxFileBytes { get; init; }
        public int? DiagnosticMaxArchiveFiles { get; init; }
        public IReadOnlyList<string>? AllowedSharePointHosts { get; init; }
        public IReadOnlyList<string>? AllowedTenantIds { get; init; }
        public string? TenantId { get; init; }
        public string? ClientId { get; init; }
        public string? MountPoint { get; init; }
        public bool? StartWithWindows { get; init; }
        public bool? AutoStartVirtualDrive { get; init; }
        public int? CacheMinutes { get; init; }
        public int? OfflineCacheLimitMb { get; init; }
        public IReadOnlySet<string> ConfiguredFields { get; init; } = new HashSet<string>();
    }
}
