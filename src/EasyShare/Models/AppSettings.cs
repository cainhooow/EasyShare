namespace EasyShare.Models;

public enum AppThemeMode
{
    System,
    Light,
    Dark
}

public sealed class AppSettings
{
    public AuthenticationMode AuthenticationMode { get; set; } = AuthenticationMode.BrowserSession;

    public string ClientId { get; set; } = string.Empty;

    public string TenantId { get; set; } = "organizations";

    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; }

    public bool AutoStartVirtualDrive { get; set; } = true;

    public string MountPoint { get; set; } = "S:";

    public int CacheMinutes { get; set; } = 15;

    public string BrowserSessionStartUrl { get; set; } = "https://www.office.com/?auth=2";

    public bool BrowserKeepSessionAlive { get; set; } = true;

    public int BrowserKeepAliveMinutes { get; set; } = 20;

    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

    public string AccentColor { get; set; } = "#F97316";

    public bool HighContrastEnabled { get; set; }

    public string LanguageCode { get; set; } = "pt-BR";

    public bool SetupWizardCompleted { get; set; }

    public int SetupWizardCompletedVersion { get; set; }

    public bool NotificationsEnabled { get; set; } = true;

    public bool NotifyUploadCompleted { get; set; } = true;

    public bool NotifyUploadFailed { get; set; } = true;

    public bool NotifyConflict { get; set; } = true;

    public bool NotifySessionExpired { get; set; } = true;

    public bool NotifyDriveDisconnected { get; set; } = true;

    public bool NotifyUpdateReady { get; set; } = true;

    public bool QuietModeEnabled { get; set; }

    public int OfflineCacheLimitMb { get; set; } = 2048;

    public bool OfflinePauseOnMeteredNetwork { get; set; } = true;

    public bool OfflinePauseOnBattery { get; set; } = true;

    public bool HasClientId => Guid.TryParse(ClientId, out var clientId) && clientId != Guid.Empty;
}
