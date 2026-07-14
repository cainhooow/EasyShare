namespace EasyShare.Models;

public enum SetupWizardStep
{
    Welcome,
    Appearance,
    Access,
    Connection,
    WindowsIntegration,
    OfflineAndNotifications,
    Review
}

public sealed class SetupWizardDraft
{
    public string LanguageCode { get; set; } = "pt-BR";

    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

    public bool HighContrastEnabled { get; set; }

    public AuthenticationMode AuthenticationMode { get; set; } = AuthenticationMode.BrowserSession;

    public string ClientId { get; set; } = string.Empty;

    public string TenantId { get; set; } = "organizations";

    public string BrowserSessionStartUrl { get; set; } = "https://www.office.com/?auth=2";

    public bool BrowserKeepSessionAlive { get; set; } = true;

    public string MountPoint { get; set; } = "S:";

    public bool AutoStartVirtualDrive { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; }

    public int CacheMinutes { get; set; } = 15;

    public bool NotificationsEnabled { get; set; } = true;

    public bool QuietModeEnabled { get; set; }

    public int OfflineCacheLimitMb { get; set; } = 2048;

    public bool OfflinePauseOnMeteredNetwork { get; set; } = true;

    public bool OfflinePauseOnBattery { get; set; } = true;

    public bool ConnectNow { get; set; } = true;
}

public sealed record SetupWizardCapabilities(
    bool IsEnterpriseManaged,
    bool CanEditAccess,
    bool CanEditDrive,
    bool CanEditCache,
    bool BrowserSessionAllowed,
    bool InteractiveSignInAllowed,
    bool WinFspAvailable);

public sealed record SetupWizardValidationIssue(
    SetupWizardStep Step,
    string MessageKey,
    string? Field = null);

public sealed record SetupWizardApplyResult(
    bool StartupRequested,
    bool StartupEnabled,
    string? WarningMessage = null);
