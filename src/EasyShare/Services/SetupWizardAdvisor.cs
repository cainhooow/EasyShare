using System.Text.RegularExpressions;
using EasyShare.Models;

namespace EasyShare.Services;

public static partial class SetupWizardAdvisor
{
    public const int CurrentVersion = 2;
    private const string DefaultBrowserUrl = "https://www.office.com/?auth=2";
    private const string PortugueseLanguageCode = "pt-BR";
    private const string EnglishLanguageCode = "en-US";

    public static SetupWizardDraft CreateDraft(
        AppSettings settings,
        EnterprisePolicySnapshot policy,
        IEnumerable<string>? occupiedMountPoints = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(policy);

        var effectiveClientId = policy.Policy.ClientId ?? settings.ClientId;
        var browserAllowed = policy.Policy.BrowserSessionAllowed;
        var recommendedMode = HasValidClientId(effectiveClientId) || !browserAllowed
            ? AuthenticationMode.MicrosoftGraph
            : AuthenticationMode.BrowserSession;
        var occupied = NormalizeMountPoints(occupiedMountPoints);
        var requestedMount = policy.Policy.MountPoint ?? settings.MountPoint;
        var mountPoint = policy.Policy.MountPoint is not null
            ? NormalizeMountPoint(policy.Policy.MountPoint) ?? policy.Policy.MountPoint
            : SelectMountPoint(requestedMount, occupied);

        var accessManagedByIdentity = IsGraphIdentityManaged(policy);
        var authenticationMode = !browserAllowed || accessManagedByIdentity
            ? AuthenticationMode.MicrosoftGraph
            : settings.SetupWizardCompleted
                ? settings.AuthenticationMode
                : recommendedMode;
        var startWithWindows = policy.Policy.StartWithWindows ?? settings.StartWithWindows;
        var requestedTenant = NormalizeTenant(policy.Policy.TenantId ?? settings.TenantId);
        var effectiveTenant = policy.Policy.AllowedTenantIds.Count > 0 &&
                              !policy.Policy.AllowedTenantIds.Contains(
                                  requestedTenant,
                                  StringComparer.OrdinalIgnoreCase)
            ? policy.Policy.AllowedTenantIds[0]
            : requestedTenant;

        return new SetupWizardDraft
        {
            LanguageCode = NormalizeLanguageCode(settings.LanguageCode),
            ThemeMode = Enum.IsDefined(settings.ThemeMode) ? settings.ThemeMode : AppThemeMode.System,
            HighContrastEnabled = settings.HighContrastEnabled,
            AuthenticationMode = authenticationMode,
            ClientId = effectiveClientId?.Trim() ?? string.Empty,
            TenantId = effectiveTenant,
            BrowserSessionStartUrl = NormalizeBrowserUrl(settings.BrowserSessionStartUrl),
            BrowserKeepSessionAlive = settings.BrowserKeepSessionAlive,
            MountPoint = mountPoint,
            AutoStartVirtualDrive = policy.Policy.AutoStartVirtualDrive ?? settings.AutoStartVirtualDrive,
            StartWithWindows = startWithWindows,
            StartMinimized = startWithWindows && settings.StartMinimized,
            CacheMinutes = policy.Policy.CacheMinutes ?? settings.CacheMinutes,
            NotificationsEnabled = settings.NotificationsEnabled,
            QuietModeEnabled = settings.QuietModeEnabled,
            OfflineCacheLimitMb = policy.Policy.OfflineCacheLimitMb ?? settings.OfflineCacheLimitMb,
            OfflinePauseOnMeteredNetwork = settings.OfflinePauseOnMeteredNetwork,
            OfflinePauseOnBattery = settings.OfflinePauseOnBattery,
            ConnectNow = policy.Policy.InteractiveSignInAllowed
        };
    }

    public static SetupWizardCapabilities CreateCapabilities(
        EnterprisePolicySnapshot policy,
        bool winFspAvailable) =>
        new(
            policy.IsManaged,
            !policy.IsFieldManaged("browserSessionAllowed") &&
            !policy.IsFieldManaged("tenantId") &&
            !policy.IsFieldManaged("clientId") &&
            !policy.IsFieldManaged("allowedTenantIds"),
            !policy.IsFieldManaged("mountPoint") &&
            !policy.IsFieldManaged("startWithWindows") &&
            !policy.IsFieldManaged("autoStartVirtualDrive"),
            !policy.IsFieldManaged("cacheMinutes") &&
            !policy.IsFieldManaged("offlineCacheLimitMb"),
            policy.Policy.BrowserSessionAllowed,
            policy.Policy.InteractiveSignInAllowed,
            winFspAvailable);

    public static IReadOnlyList<string> GetMountPointOptions(
        IEnumerable<string>? occupiedMountPoints,
        string? currentMountPoint = null)
    {
        var occupied = NormalizeMountPoints(occupiedMountPoints);
        var current = NormalizeMountPoint(currentMountPoint);
        if (current is not null)
        {
            occupied.Remove(current);
        }

        var result = new List<string>();
        if (!occupied.Contains("S:"))
        {
            result.Add("S:");
        }

        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            var candidate = $"{letter}:";
            if (!occupied.Contains(candidate) && !result.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    public static IReadOnlyList<SetupWizardValidationIssue> ValidateStep(
        SetupWizardDraft draft,
        SetupWizardStep step,
        EnterprisePolicySnapshot policy,
        IReadOnlyCollection<string>? availableMountPoints = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(policy);
        var issues = new List<SetupWizardValidationIssue>();

        switch (step)
        {
            case SetupWizardStep.Welcome:
                if (!IsSupportedLanguage(draft.LanguageCode))
                {
                    issues.Add(new(step, "WizardErrorLanguage", nameof(draft.LanguageCode)));
                }
                break;

            case SetupWizardStep.Appearance:
                if (!Enum.IsDefined(draft.ThemeMode))
                {
                    issues.Add(new(step, "WizardErrorTheme", nameof(draft.ThemeMode)));
                }
                break;

            case SetupWizardStep.Access:
                if (!Enum.IsDefined(draft.AuthenticationMode))
                {
                    issues.Add(new(step, "WizardErrorAccessMode", nameof(draft.AuthenticationMode)));
                }
                else if (IsGraphIdentityManaged(policy) &&
                         draft.AuthenticationMode != AuthenticationMode.MicrosoftGraph)
                {
                    issues.Add(new(step, "WizardErrorGraphManaged", nameof(draft.AuthenticationMode)));
                }
                else if (draft.AuthenticationMode == AuthenticationMode.BrowserSession &&
                    !policy.Policy.BrowserSessionAllowed)
                {
                    issues.Add(new(step, "WizardErrorBrowserBlocked", nameof(draft.AuthenticationMode)));
                }
                break;

            case SetupWizardStep.Connection:
                ValidateConnection(draft, policy, issues);
                break;

            case SetupWizardStep.WindowsIntegration:
                ValidateWindows(draft, policy, availableMountPoints, issues);
                break;

            case SetupWizardStep.OfflineAndNotifications:
                var effectiveCacheMinutes = policy.Policy.CacheMinutes ?? draft.CacheMinutes;
                var effectiveOfflineLimit = policy.Policy.OfflineCacheLimitMb ?? draft.OfflineCacheLimitMb;
                if (effectiveCacheMinutes is < 1 or > 1440)
                {
                    issues.Add(new(step, "WizardErrorCacheMinutes", nameof(draft.CacheMinutes)));
                }

                if (effectiveOfflineLimit is < 128 or > 102400)
                {
                    issues.Add(new(step, "WizardErrorOfflineLimit", nameof(draft.OfflineCacheLimitMb)));
                }
                break;

            case SetupWizardStep.Review:
                break;
        }

        return issues;
    }

    public static IReadOnlyList<SetupWizardValidationIssue> ValidateAll(
        SetupWizardDraft draft,
        EnterprisePolicySnapshot policy,
        IReadOnlyCollection<string>? availableMountPoints = null) =>
        Enum.GetValues<SetupWizardStep>()
            .SelectMany(step => ValidateStep(draft, step, policy, availableMountPoints))
            .ToArray();

    public static AppSettings BuildSettings(
        AppSettings current,
        SetupWizardDraft draft,
        EnterprisePolicySnapshot policy)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(policy);

        var result = CloneSettings(current);
        result.LanguageCode = NormalizeLanguageCode(draft.LanguageCode);
        result.ThemeMode = Enum.IsDefined(draft.ThemeMode) ? draft.ThemeMode : AppThemeMode.System;
        result.HighContrastEnabled = draft.HighContrastEnabled;
        result.AuthenticationMode = Enum.IsDefined(draft.AuthenticationMode)
            ? draft.AuthenticationMode
            : HasValidClientId(policy.Policy.ClientId ?? draft.ClientId) ||
              !policy.Policy.BrowserSessionAllowed
                ? AuthenticationMode.MicrosoftGraph
                : AuthenticationMode.BrowserSession;
        result.ClientId = draft.ClientId.Trim();
        result.TenantId = NormalizeTenant(draft.TenantId);
        result.BrowserSessionStartUrl = NormalizeBrowserUrl(draft.BrowserSessionStartUrl);
        result.BrowserKeepSessionAlive = draft.BrowserKeepSessionAlive;
        result.MountPoint = NormalizeMountPoint(draft.MountPoint) ?? "S:";
        result.AutoStartVirtualDrive = draft.AutoStartVirtualDrive;
        result.StartWithWindows = draft.StartWithWindows;
        result.StartMinimized = draft.StartWithWindows && draft.StartMinimized;
        result.CacheMinutes = Math.Clamp(draft.CacheMinutes, 1, 1440);
        result.NotificationsEnabled = draft.NotificationsEnabled;
        result.QuietModeEnabled = draft.NotificationsEnabled && draft.QuietModeEnabled;
        result.OfflineCacheLimitMb = Math.Clamp(draft.OfflineCacheLimitMb, 128, 102400);
        result.OfflinePauseOnMeteredNetwork = draft.OfflinePauseOnMeteredNetwork;
        result.OfflinePauseOnBattery = draft.OfflinePauseOnBattery;
        result.SetupWizardCompleted = true;
        result.SetupWizardCompletedVersion = CurrentVersion;

        ApplyPolicy(result, policy.Policy);
        if (IsGraphIdentityManaged(policy))
        {
            result.AuthenticationMode = AuthenticationMode.MicrosoftGraph;
        }

        if (!result.StartWithWindows)
        {
            result.StartMinimized = false;
        }

        return result;
    }

    public static bool HasValidClientId(string? value) =>
        Guid.TryParse(value, out var clientId) && clientId != Guid.Empty;

    public static bool IsValidTenant(string? value)
    {
        var tenant = value?.Trim() ?? string.Empty;
        return string.Equals(tenant, "organizations", StringComparison.OrdinalIgnoreCase) ||
               (Guid.TryParse(tenant, out var tenantId) && tenantId != Guid.Empty) ||
               TenantDomainRegex().IsMatch(tenant);
    }

    private static void ValidateConnection(
        SetupWizardDraft draft,
        EnterprisePolicySnapshot policy,
        ICollection<SetupWizardValidationIssue> issues)
    {
        if (draft.AuthenticationMode == AuthenticationMode.MicrosoftGraph)
        {
            var clientId = policy.Policy.ClientId ?? draft.ClientId;
            var tenant = policy.Policy.TenantId ?? draft.TenantId;
            if (!HasValidClientId(clientId))
            {
                issues.Add(new(SetupWizardStep.Connection, "WizardErrorClientId", nameof(draft.ClientId)));
            }

            if (!IsValidTenant(tenant))
            {
                issues.Add(new(SetupWizardStep.Connection, "WizardErrorTenant", nameof(draft.TenantId)));
            }
            else if (policy.Policy.AllowedTenantIds.Count > 0 &&
                     !policy.Policy.AllowedTenantIds.Contains(tenant.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new(SetupWizardStep.Connection, "WizardErrorTenantBlocked", nameof(draft.TenantId)));
            }

            return;
        }

        if (!policy.Policy.BrowserSessionAllowed)
        {
            issues.Add(new(SetupWizardStep.Connection, "WizardErrorBrowserBlocked", nameof(draft.AuthenticationMode)));
            return;
        }

        if (!IsValidBrowserUrl(draft.BrowserSessionStartUrl, policy.Policy.AllowedSharePointHosts))
        {
            issues.Add(new(SetupWizardStep.Connection, "WizardErrorSharePointUrl", nameof(draft.BrowserSessionStartUrl)));
        }
    }

    private static void ValidateWindows(
        SetupWizardDraft draft,
        EnterprisePolicySnapshot policy,
        IReadOnlyCollection<string>? availableMountPoints,
        ICollection<SetupWizardValidationIssue> issues)
    {
        if (!draft.AutoStartVirtualDrive && policy.Policy.AutoStartVirtualDrive != true)
        {
            return;
        }

        var mountPoint = NormalizeMountPoint(policy.Policy.MountPoint ?? draft.MountPoint);
        if (mountPoint is null || mountPoint[0] is < 'D' or > 'Z')
        {
            issues.Add(new(SetupWizardStep.WindowsIntegration, "WizardErrorMountPoint", nameof(draft.MountPoint)));
            return;
        }

        if (availableMountPoints is not null &&
            !availableMountPoints.Contains(mountPoint, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new(
                SetupWizardStep.WindowsIntegration,
                policy.IsFieldManaged("mountPoint")
                    ? "WizardErrorManagedMountBusy"
                    : "WizardErrorMountBusy",
                nameof(draft.MountPoint)));
        }
    }

    private static bool IsSupportedLanguage(string? languageCode) =>
        string.Equals(languageCode, PortugueseLanguageCode, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(languageCode, EnglishLanguageCode, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeLanguageCode(string? languageCode) =>
        string.Equals(languageCode, EnglishLanguageCode, StringComparison.OrdinalIgnoreCase)
            ? EnglishLanguageCode
            : PortugueseLanguageCode;

    private static bool IsGraphIdentityManaged(EnterprisePolicySnapshot policy) =>
        policy.IsFieldManaged("clientId") ||
        policy.IsFieldManaged("tenantId") ||
        policy.IsFieldManaged("allowedTenantIds");

    private static bool IsValidBrowserUrl(string? value, IReadOnlyList<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (!uri.IsDefaultPort && uri.Port != 443))
        {
            return false;
        }

        if (string.Equals(uri.DnsSafeHost, "www.office.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.DnsSafeHost, "office.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!SharePointRouteParser.IsAllowedSharePointUri(uri))
        {
            return false;
        }

        return allowedHosts.Count == 0 || allowedHosts.Any(pattern =>
            pattern.StartsWith("*.", StringComparison.Ordinal)
                ? uri.DnsSafeHost.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) &&
                  !string.Equals(uri.DnsSafeHost, pattern[2..], StringComparison.OrdinalIgnoreCase)
                : string.Equals(uri.DnsSafeHost, pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static string SelectMountPoint(string? requested, HashSet<string> occupied)
    {
        var normalized = NormalizeMountPoint(requested);
        if (normalized is not null && !occupied.Contains(normalized))
        {
            return normalized;
        }

        return GetMountPointOptions(occupied).FirstOrDefault() ?? normalized ?? "S:";
    }

    private static HashSet<string> NormalizeMountPoints(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
        .Select(NormalizeMountPoint)
        .Where(value => value is not null)
        .Cast<string>()
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? NormalizeMountPoint(string? value)
    {
        var normalized = value?.Trim().TrimEnd('\\', '/').ToUpperInvariant() ?? string.Empty;
        if (normalized.Length == 1 && char.IsAsciiLetterUpper(normalized[0]))
        {
            normalized += ":";
        }

        return normalized.Length == 2 &&
               char.IsAsciiLetterUpper(normalized[0]) &&
               normalized[1] == ':'
            ? normalized
            : null;
    }

    private static string NormalizeTenant(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "organizations" : value.Trim();

    private static string NormalizeBrowserUrl(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DefaultBrowserUrl : value.Trim();

    public static AppSettings CloneSettings(AppSettings source) => new()
    {
        AuthenticationMode = source.AuthenticationMode,
        ClientId = source.ClientId,
        TenantId = source.TenantId,
        StartWithWindows = source.StartWithWindows,
        StartMinimized = source.StartMinimized,
        AutoStartVirtualDrive = source.AutoStartVirtualDrive,
        MountPoint = source.MountPoint,
        CacheMinutes = source.CacheMinutes,
        BrowserSessionStartUrl = source.BrowserSessionStartUrl,
        BrowserKeepSessionAlive = source.BrowserKeepSessionAlive,
        BrowserKeepAliveMinutes = source.BrowserKeepAliveMinutes,
        ThemeMode = source.ThemeMode,
        AccentColor = source.AccentColor,
        HighContrastEnabled = source.HighContrastEnabled,
        LanguageCode = source.LanguageCode,
        SetupWizardCompleted = source.SetupWizardCompleted,
        SetupWizardCompletedVersion = source.SetupWizardCompletedVersion,
        NotificationsEnabled = source.NotificationsEnabled,
        NotifyUploadCompleted = source.NotifyUploadCompleted,
        NotifyUploadFailed = source.NotifyUploadFailed,
        NotifyConflict = source.NotifyConflict,
        NotifySessionExpired = source.NotifySessionExpired,
        NotifyDriveDisconnected = source.NotifyDriveDisconnected,
        NotifyUpdateReady = source.NotifyUpdateReady,
        QuietModeEnabled = source.QuietModeEnabled,
        OfflineCacheLimitMb = source.OfflineCacheLimitMb,
        OfflinePauseOnMeteredNetwork = source.OfflinePauseOnMeteredNetwork,
        OfflinePauseOnBattery = source.OfflinePauseOnBattery
    };

    private static void ApplyPolicy(AppSettings settings, EnterprisePolicy policy)
    {
        if (!policy.BrowserSessionAllowed && settings.AuthenticationMode == AuthenticationMode.BrowserSession)
        {
            settings.AuthenticationMode = AuthenticationMode.MicrosoftGraph;
        }

        settings.ClientId = policy.ClientId ?? settings.ClientId;
        settings.TenantId = policy.TenantId ??
                            (policy.AllowedTenantIds.Count > 0 &&
                             !policy.AllowedTenantIds.Contains(settings.TenantId, StringComparer.OrdinalIgnoreCase)
                                ? policy.AllowedTenantIds[0]
                                : settings.TenantId);
        settings.MountPoint = policy.MountPoint ?? settings.MountPoint;
        settings.StartWithWindows = policy.StartWithWindows ?? settings.StartWithWindows;
        settings.AutoStartVirtualDrive = policy.AutoStartVirtualDrive ?? settings.AutoStartVirtualDrive;
        settings.CacheMinutes = policy.CacheMinutes ?? settings.CacheMinutes;
        settings.OfflineCacheLimitMb = policy.OfflineCacheLimitMb ?? settings.OfflineCacheLimitMb;
    }

    [GeneratedRegex(
        "^(?=.{1,253}$)(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\\.)+[A-Za-z]{2,63}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TenantDomainRegex();
}
