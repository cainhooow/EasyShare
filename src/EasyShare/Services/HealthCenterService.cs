using EasyShare.Models;
using Microsoft.Web.WebView2.Core;

namespace EasyShare.Services;

public sealed class HealthCenterService
{
    private readonly LocalDatabase _database;
    private readonly AppNotificationService _notifications;
    private readonly AppUpdateService _updates;
    private readonly OfflineCacheService? _offlineCache;

    public HealthCenterService(
        LocalDatabase database,
        AppNotificationService notifications,
        AppUpdateService updates,
        OfflineCacheService? offlineCache = null)
    {
        _database = database;
        _notifications = notifications;
        _updates = updates;
        _offlineCache = offlineCache;
    }

    public async Task<IReadOnlyList<HealthCheckItem>> InspectAsync(
        VirtualDriveStatus drive,
        IReadOnlyCollection<DriveRoute> routes,
        IReadOnlyCollection<SyncJob> jobs,
        bool browserInitialized,
        bool browserSessionAvailable)
    {
        var now = DateTimeOffset.UtcNow;
        var results = new List<HealthCheckItem>();

        try
        {
            await _database.GetSettingsAsync();
            results.Add(Item("database", "HealthDatabaseTitle", "HealthDatabaseHealthy", HealthCheckState.Healthy, now));
        }
        catch
        {
            results.Add(Item("database", "HealthDatabaseTitle", "HealthDatabaseUnavailable", HealthCheckState.Unavailable, now));
        }

        results.Add(VirtualDriveService.IsWinFspAvailable()
            ? Item("winfsp", "HealthWinFspTitle", "HealthWinFspHealthy", HealthCheckState.Healthy, now)
            : Item("winfsp", "HealthWinFspTitle", "HealthWinFspUnavailable", HealthCheckState.Unavailable, now));

        try
        {
            var webViewVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
            results.Add(string.IsNullOrWhiteSpace(webViewVersion)
                ? Item("webview", "HealthWebViewTitle", "HealthWebViewUnavailable", HealthCheckState.Unavailable, now)
                : new HealthCheckItem(
                    "webview",
                    Resources.AppText.Get("HealthWebViewTitle"),
                    Resources.AppText.Format("HealthWebViewHealthyFormat", webViewVersion),
                    HealthCheckState.Healthy,
                    now));
        }
        catch
        {
            results.Add(Item("webview", "HealthWebViewTitle", "HealthWebViewUnavailable", HealthCheckState.Unavailable, now));
        }

        results.Add(new HealthCheckItem(
            "drive",
            Resources.AppText.Get("HealthDriveTitle"),
            drive.Detail,
            drive.CanOpenInExplorer ? HealthCheckState.Healthy : HealthCheckState.Attention,
            now));

        var queueNeedsAttention = jobs.Count(job => job.State is SyncJobState.Failed or SyncJobState.Conflict);
        results.Add(new HealthCheckItem(
            "queue",
            Resources.AppText.Get("HealthQueueTitle"),
            queueNeedsAttention == 0
                ? Resources.AppText.Get("HealthQueueHealthy")
                : Resources.AppText.Format("HealthQueueAttentionFormat", queueNeedsAttention),
            queueNeedsAttention == 0 ? HealthCheckState.Healthy : HealthCheckState.Attention,
            now));

        var hasRoutes = routes.Count > 0;
        results.Add(new HealthCheckItem(
            "session",
            Resources.AppText.Get("HealthSessionTitle"),
            !hasRoutes
                ? Resources.AppText.Get("HealthSessionNoRoutes")
                : browserSessionAvailable
                    ? Resources.AppText.Get("HealthSessionHealthy")
                    : browserInitialized
                        ? Resources.AppText.Get("HealthSessionAttention")
                        : Resources.AppText.Get("HealthSessionNotStarted"),
            !hasRoutes || browserSessionAvailable ? HealthCheckState.Healthy : HealthCheckState.Attention,
            now));

        results.Add(_notifications.IsAvailable
            ? Item("notifications", "HealthNotificationsTitle", "HealthNotificationsHealthy", HealthCheckState.Healthy, now)
            : Item("notifications", "HealthNotificationsTitle", "HealthNotificationsUnavailable", HealthCheckState.Attention, now));

        results.Add(new HealthCheckItem(
            "updates",
            Resources.AppText.Get("HealthUpdatesTitle"),
            Resources.AppText.Format("HealthUpdatesChannelFormat", _updates.UpdateChannel),
            HealthCheckState.Healthy,
            now));

        if (_offlineCache is not null)
        {
            try
            {
                var offlineEntries = await _offlineCache.GetEntriesAsync().ConfigureAwait(false);
                var offlineErrors = offlineEntries.Count(entry => entry.State == OfflineCacheState.Error);
                results.Add(new HealthCheckItem(
                    "offline-cache",
                    Resources.AppText.Get("HealthOfflineTitle"),
                    offlineErrors == 0
                        ? Resources.AppText.Format("HealthOfflineHealthyFormat", offlineEntries.Count)
                        : Resources.AppText.Format("HealthOfflineAttentionFormat", offlineErrors),
                    offlineErrors == 0 ? HealthCheckState.Healthy : HealthCheckState.Attention,
                    now));
            }
            catch
            {
                results.Add(Item(
                    "offline-cache",
                    "HealthOfflineTitle",
                    "HealthOfflineUnavailable",
                    HealthCheckState.Attention,
                    now));
            }
        }

        return results;
    }

    private static HealthCheckItem Item(
        string key,
        string titleKey,
        string detailKey,
        HealthCheckState state,
        DateTimeOffset checkedAt) =>
        new(key, Resources.AppText.Get(titleKey), Resources.AppText.Get(detailKey), state, checkedAt);
}
