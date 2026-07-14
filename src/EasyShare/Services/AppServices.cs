using EasyShare.Models;

namespace EasyShare.Services;

/// <summary>
/// Application composition root. UI types consume this graph instead of creating
/// infrastructure services themselves, which keeps ownership and disposal explicit.
/// </summary>
public sealed class AppServices : IDisposable
{
    private bool _disposed;

    private AppServices(
        AppDataPaths paths,
        LocalDatabase database,
        MsalAuthenticationService authentication,
        SharePointBrowserContentService browserContent,
        UploadQueueService uploadQueue,
        BrowserSessionService browserSession,
        VirtualDriveService virtualDrive,
        StartupService startup,
        GraphSharePointService graphSharePoint,
        GraphSharePointExplorerService graphSharePointExplorer,
        AppUpdateService appUpdate,
        AppNotificationService notifications,
        HealthCenterService healthCenter,
        OfflineCacheService offlineCache,
        EnterprisePolicySnapshot enterprisePolicy,
        RotatingDiagnosticLog diagnosticLog,
        SupportBundleService supportBundles)
    {
        Paths = paths;
        Database = database;
        Authentication = authentication;
        BrowserContent = browserContent;
        UploadQueue = uploadQueue;
        BrowserSession = browserSession;
        VirtualDrive = virtualDrive;
        Startup = startup;
        GraphSharePoint = graphSharePoint;
        GraphSharePointExplorer = graphSharePointExplorer;
        AppUpdate = appUpdate;
        Notifications = notifications;
        HealthCenter = healthCenter;
        OfflineCache = offlineCache;
        EnterprisePolicy = enterprisePolicy;
        DiagnosticLog = diagnosticLog;
        SupportBundles = supportBundles;
    }

    public AppDataPaths Paths { get; }

    public LocalDatabase Database { get; }

    public MsalAuthenticationService Authentication { get; }

    public SharePointBrowserContentService BrowserContent { get; }

    public UploadQueueService UploadQueue { get; }

    public BrowserSessionService BrowserSession { get; }

    public VirtualDriveService VirtualDrive { get; }

    public StartupService Startup { get; }

    public GraphSharePointService GraphSharePoint { get; }

    public GraphSharePointExplorerService GraphSharePointExplorer { get; }

    public AppUpdateService AppUpdate { get; }

    public AppNotificationService Notifications { get; }

    public HealthCenterService HealthCenter { get; }

    public OfflineCacheService OfflineCache { get; }

    public EnterprisePolicySnapshot EnterprisePolicy { get; }

    public RotatingDiagnosticLog DiagnosticLog { get; }

    public SupportBundleService SupportBundles { get; }

    public static AppServices Create()
    {
        var paths = new AppDataPaths();
        paths.EnsureCreated();
        var policy = new EnterprisePolicyLoader(paths).Load();
        StartupDiagnostics.Configure(policy.CreateDiagnosticLogOptions());
        var diagnosticLog = StartupDiagnostics.CurrentLog;
        var database = new LocalDatabase(paths);
        var authentication = new MsalAuthenticationService(paths, database);
        var browserContent = new SharePointBrowserContentService(database);
        browserContent.ConfigureEnterprisePolicy(policy.Policy);
        var graphContent = new GraphSharePointContentService(authentication);
        browserContent.ConfigureGraphContent(graphContent);
        var graphExplorer = new GraphSharePointExplorerService(authentication);
        graphExplorer.ConfigureEnterprisePolicy(policy.Policy);
        var graphSharePoint = new GraphSharePointService(authentication);
        graphSharePoint.ConfigureEnterprisePolicy(policy.Policy);
        var uploadPayloadStorage = new UploadPayloadStorage(
            paths,
            policy.CreateUploadPayloadStorageOptions());
        var uploadQueue = new UploadQueueService(database, browserContent, paths, uploadPayloadStorage);
        var offlineCache = new OfflineCacheService(paths, browserContent);
        browserContent.ConfigureOfflineCache(offlineCache);

        var managedUpdateChannel = policy.Policy.UpdateChannel switch
        {
            "microsoftStore" => AppUpdateChannel.MicrosoftStore,
            "githubReleases" => AppUpdateChannel.GitHubReleases,
            _ => (AppUpdateChannel?)null
        };
        var updates = new AppUpdateService(paths, managedUpdateChannel);
        var notifications = new AppNotificationService();

        return new AppServices(
            paths,
            database,
            authentication,
            browserContent,
            uploadQueue,
            new BrowserSessionService(paths),
            new VirtualDriveService(browserContent, uploadQueue),
            new StartupService(),
            graphSharePoint,
            graphExplorer,
            updates,
            notifications,
            new HealthCenterService(database, notifications, updates, offlineCache),
            offlineCache,
            policy,
            diagnosticLog,
            new SupportBundleService(paths, diagnosticLog));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UploadQueue.Dispose();
        VirtualDrive.Dispose();
        if (AppUpdate is IDisposable disposableUpdateService)
        {
            disposableUpdateService.Dispose();
        }

        Notifications.Dispose();
    }
}
