namespace EasyShare.Models;

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

    public bool SetupWizardCompleted { get; set; }

    public bool HasClientId => Guid.TryParse(ClientId, out var clientId) && clientId != Guid.Empty;
}
