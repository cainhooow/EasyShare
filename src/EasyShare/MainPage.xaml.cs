using EasyShare.Services;
using EasyShare.ViewModels;
using EasyShare.Models;
using EasyShare.Resources;
using EasyShare.Controls;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EasyShare;

/// <summary>
/// The main content page displayed inside the application window.
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly BrowserSessionService _browserSessionService;
    private readonly SharePointBrowserContentService _browserContent;
    private readonly GraphSharePointExplorerService _graphSharePointExplorer;
    private readonly BrowserSharePointExplorerService _browserSharePointExplorer;
    private readonly UploadQueueService _uploadQueue;
    private readonly DispatcherTimer _browserKeepAliveTimer = new();
    private readonly DispatcherTimer _actionMessageTimer = new();
    private bool _browserInitialized;
    private bool _pageLoaded;
    private int _localBusyDepth;
    private readonly OperationsCenterViewModel _operationsViewModel;
    private readonly SharePointExplorerViewModel _sharePointExplorerViewModel;
    private readonly ContentAssistantViewModel _contentAssistantViewModel;
    private readonly SemaphoreSlim _sharePointExplorerInitializationLock = new(1, 1);
    private readonly SemaphoreSlim _sharePointExplorerContextLock = new(1, 1);
    private readonly SemaphoreSlim _sharePointExplorerPinLock = new(1, 1);
    private readonly SemaphoreSlim _setupWizardDisplayGate = new(1, 1);
    private bool _sharePointExplorerInitialized;
    private bool _contentAssistantInitialized;
    private readonly Dictionary<Guid, SyncJobState> _lastNotifiedStates = [];
    private bool? _lastDriveHealthy;
    private bool? _lastSessionHealthy;
    private bool _subscriptionsDisposed;
    private string _browserSessionCacheScope = $"BROWSER-SESSION-{Guid.NewGuid():N}";
    private string _graphSessionCacheScope = $"GRAPH-SESSION-{Guid.NewGuid():N}";
    private string _contentAccountScope = ContentIdentityScope.Disconnected;
    private bool? _configuredExplorerUsesBrowser;
    private string _observedAuthenticationContext = string.Empty;
    private long _contentIdentityGeneration;
    private long _contentContextRevision;

    public MainPageViewModel ViewModel { get; }

    public MainPage()
    {
        var services = App.Services;
        _browserContent = services.BrowserContent;
        _uploadQueue = services.UploadQueue;
        _browserSessionService = services.BrowserSession;
        _graphSharePointExplorer = services.GraphSharePointExplorer;
        _browserSharePointExplorer = services.BrowserSharePointExplorer;

        ViewModel = new MainPageViewModel(
            services.Database,
            services.Authentication,
            services.VirtualDrive,
            services.Startup,
            services.GraphSharePoint,
            services.AppUpdate,
            services.EnterprisePolicy);
        _observedAuthenticationContext = BuildAuthenticationContextKey();

        _operationsViewModel = new OperationsCenterViewModel(
            ViewModel.Routes,
            ViewModel.SyncJobs,
            services.HealthCenter);
        _sharePointExplorerViewModel = new SharePointExplorerViewModel(_graphSharePointExplorer);
        _contentAssistantViewModel = new ContentAssistantViewModel(
            services.ContentIndex,
            services.BrowserContent);

        InitializeComponent();
        SetupWizard.ApplyRequested += SetupWizard_ApplyRequested;
        SetupWizard.AppearancePreviewRequested += SetupWizard_AppearancePreviewRequested;
        SharePointExplorer.Initialize(_sharePointExplorerViewModel);
        SharePointExplorer.SignInRequested += SharePointExplorer_SignInRequested;
        SharePointExplorer.PinRequested += SharePointExplorer_PinRequested;
        SharePointExplorer.ManualRouteRequested += SharePointExplorer_ManualRouteRequested;
        ContentAssistant.Initialize(_contentAssistantViewModel);
        ContentAssistant.OpenRequested += ContentAssistant_OpenRequested;
        OperationsCenter.Initialize(_operationsViewModel);
        OperationsCenter.RetryRequested += OperationsCenter_RetryRequested;
        OperationsCenter.ConflictResolutionRequested += OperationsCenter_ConflictResolutionRequested;
        OperationsCenter.HealthRefreshRequested += OperationsCenter_HealthRefreshRequested;
        OperationsCenter.SupportExportRequested += OperationsCenter_SupportExportRequested;
        OperationsCenter.OfflinePinRequested += OperationsCenter_OfflinePinRequested;
        OperationsCenter.OfflineRemoveRequested += OperationsCenter_OfflineRemoveRequested;
        services.Notifications.Activated += Notifications_Activated;
        foreach (var activation in services.Notifications.DrainPendingActivations())
        {
            Notifications_Activated(activation);
        }
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _uploadQueue.JobChanged += UploadQueue_JobChanged;
        _browserKeepAliveTimer.Tick += BrowserKeepAliveTimer_Tick;
        _actionMessageTimer.Interval = TimeSpan.FromSeconds(5);
        _actionMessageTimer.Tick += (_, _) =>
        {
            _actionMessageTimer.Stop();
            ActionInfoBar.IsOpen = false;
        };
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_subscriptionsDisposed)
        {
            return;
        }

        _subscriptionsDisposed = true;
        _browserKeepAliveTimer.Stop();
        _actionMessageTimer.Stop();
        var browserVerification = Interlocked.Exchange(
            ref _browserNavigationVerificationCancellation,
            null);
        browserVerification?.Cancel();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _uploadQueue.JobChanged -= UploadQueue_JobChanged;
        App.Services.Notifications.Activated -= Notifications_Activated;
        SetupWizard.ApplyRequested -= SetupWizard_ApplyRequested;
        SetupWizard.AppearancePreviewRequested -= SetupWizard_AppearancePreviewRequested;
        SharePointExplorer.SignInRequested -= SharePointExplorer_SignInRequested;
        SharePointExplorer.PinRequested -= SharePointExplorer_PinRequested;
        SharePointExplorer.ManualRouteRequested -= SharePointExplorer_ManualRouteRequested;
        ContentAssistant.OpenRequested -= ContentAssistant_OpenRequested;
        _sharePointExplorerViewModel.Dispose();
        _contentAssistantViewModel.Dispose();
        _operationsViewModel.Dispose();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_pageLoaded)
            {
                return;
            }

            _pageLoaded = true;
            StartupDiagnostics.Write("MainPage loaded.");
            await RunWithLoadingAsync(
                async () =>
                {
                    await ViewModel.LoadAsync();
                    ApplyAppearance();
                    _browserContent.ConfigureCache(TimeSpan.FromMinutes(ViewModel.CacheMinutes));
                    _uploadQueue.Start();
                    await RestoreBrowserSessionOnStartupAsync();
                    await RefreshSharePointExplorerContextAsync();
                    await RefreshOperationsHealthAsync();
                },
                "LoadingStartupTitle",
                "LoadingStartupMessage");
            ApplyStartupWindowState();
            ConfigureBrowserKeepAliveTimer();
            await ShowSetupWizardIfNeededAsync();
            _ = CheckUpdatesOnStartupAsync();

            StartupDiagnostics.Write("MainPage startup completed.");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("MainPage startup failed.", ex);
            ViewModel.ReportStartupError(AppText.Get("StartupLoadFailed"), ex);
        }
    }

    private void ContentHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        ContentHost.Padding = width switch
        {
            >= 1200 => new Thickness(40, 28, 40, 32),
            >= 760 => new Thickness(28, 22, 28, 28),
            _ => new Thickness(16, 16, 16, 20)
        };

        ApplySummaryCardLayout(width);
        ApplyResponsivePageLayouts(width);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsBusy))
        {
            UpdateLoadingOverlay();
        }

        if (e.PropertyName is nameof(ViewModel.ThemeModeIndex) or
            nameof(ViewModel.AccentColorValue) or
            nameof(ViewModel.HighContrastEnabled))
        {
            ApplyAppearance();
        }

        if (e.PropertyName is nameof(ViewModel.ClientId) or
            nameof(ViewModel.TenantId) or
            nameof(ViewModel.IsGraphAuthMode))
        {
            var authenticationContext = BuildAuthenticationContextKey();
            if (string.Equals(
                    _observedAuthenticationContext,
                    authenticationContext,
                    StringComparison.Ordinal))
            {
                return;
            }

            _observedAuthenticationContext = authenticationContext;
            if (ViewModel.IsBrowserSessionMode)
            {
                BeginBrowserIdentityTransition();
            }
            else
            {
                BeginGraphIdentityTransition();
            }

            _ = RefreshSharePointExplorerContextAsync();
        }

        if (e.PropertyName == nameof(ViewModel.ExplorerNavigationVisibility) &&
            !ViewModel.IsExplorerAvailable &&
            RootNavigation.SelectedItem is NavigationViewItem { Tag: "Explorer" })
        {
            SelectNavigationItem("Home");
        }

        if (e.PropertyName == nameof(ViewModel.AssistantNavigationVisibility) &&
            !ViewModel.IsAssistantAvailable &&
            RootNavigation.SelectedItem is NavigationViewItem { Tag: "Assistant" })
        {
            SelectNavigationItem("Home");
        }
    }

    private string BuildAuthenticationContextKey() =>
        $"{ViewModel.CurrentAuthenticationMode}|{ViewModel.ClientId.Trim()}|{ViewModel.TenantId.Trim()}";

    private void BeginGraphIdentityTransition() =>
        BeginContentIdentityTransition(usesBrowserSession: false, invalidateBrowserVerification: false);

    private void BeginContentIdentityTransition(
        bool usesBrowserSession,
        bool invalidateBrowserVerification,
        bool invalidatePublishedBrowserSession = true)
    {
        Interlocked.Increment(ref _contentIdentityGeneration);
        Interlocked.Increment(ref _contentContextRevision);
        _contentAssistantViewModel.CancelIndexing();
        ViewModel.UpdateContentIdentityAvailability(false);
        var transientScope = usesBrowserSession
            ? _browserSessionCacheScope = $"BROWSER-SESSION-{Guid.NewGuid():N}"
            : _graphSessionCacheScope = $"GRAPH-SESSION-{Guid.NewGuid():N}";
        _browserContent.ConfigureAccountScope(transientScope);
        _contentAccountScope = transientScope;
        _configuredExplorerUsesBrowser = usesBrowserSession;
        _contentAssistantViewModel.Configure(
            transientScope,
            ViewModel.CurrentAuthenticationMode,
            []);
        _contentAssistantInitialized = false;

        var explorerService = usesBrowserSession
            ? (ISharePointExplorerService)_browserSharePointExplorer
            : _graphSharePointExplorer;
        _sharePointExplorerViewModel.ConfigureService(explorerService, usesBrowserSession);
        _sharePointExplorerViewModel.ResetForAuthenticationChange(requiresAuthentication: true);
        _sharePointExplorerInitialized = false;

        if (usesBrowserSession &&
            invalidateBrowserVerification &&
            ViewModel.IsBrowserSessionMode)
        {
            if (invalidatePublishedBrowserSession)
            {
                _browserSessionService.InvalidatePublishedSession();
            }

            ViewModel.UpdateBrowserSessionStatus(
                new RouteTestResult(false, AppText.Get("BrowserRouteNeedLogin")),
                hasVerifiedRoute: false);
        }
    }

    private void UploadQueue_JobChanged(SyncJob job)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.ApplySyncJob(job);
            NotifySyncState(job);
            _ = RefreshOperationsHealthAsync();
        });
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsInteractiveSignInAllowed)
        {
            ShowActionMessage(
                AppText.Get("SettingsManagedTitle"),
                AppText.Get("PolicySignInBlocked"),
                InfoBarSeverity.Warning);
            return;
        }

        if (ViewModel.IsBrowserSessionMode)
        {
            await RunWithLoadingAsync(
                () => OpenBrowserSessionAsync(navigate: true),
                "LoadingBrowserTitle",
                "LoadingBrowserMessage");
            return;
        }

        var hwnd = App.MainWindow is null ? IntPtr.Zero : WindowNative.GetWindowHandle(App.MainWindow);
        BeginGraphIdentityTransition();
        await RunWithLoadingAsync(
            () => ViewModel.SignInAsync(hwnd),
            "LoadingBrowserTitle",
            "LoadingBrowserMessage");
        await RefreshSharePointExplorerContextAsync();
        if (ViewModel.IsExplorerAvailable)
        {
            await ReinitializeSharePointExplorerAsync();
        }
    }

    private async void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsGraphAuthMode)
        {
            BeginGraphIdentityTransition();
        }
        else
        {
            BeginBrowserIdentityTransition(invalidateBrowserVerification: true);
        }

        await RunWithLoadingAsync(
            async () =>
            {
                if (ViewModel.IsBrowserSessionMode)
                {
                    await ClearBrowserSessionAsync(beginIdentityTransition: false);
                    await ViewModel.SignOutAsync();
                }
                else
                {
                    await ViewModel.SignOutAsync();
                    await ClearBrowserSessionAsync(beginIdentityTransition: false);
                }
            },
            "LoadingSaveTitle",
            "LoadingSaveMessage");
        _browserContent.ClearCache();
        _browserSessionCacheScope = $"BROWSER-SESSION-{Guid.NewGuid():N}";
        _graphSessionCacheScope = $"GRAPH-SESSION-{Guid.NewGuid():N}";
        _sharePointExplorerViewModel.ResetForAuthenticationChange(requiresAuthentication: true);
        _sharePointExplorerInitialized = false;
        await RefreshSharePointExplorerContextAsync();
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWithLoadingAsync(
            ViewModel.SaveSettingsAsync,
            "LoadingSaveTitle",
            "LoadingSaveMessage");
        ApplyAppearance();
        _browserContent.ConfigureCache(TimeSpan.FromMinutes(ViewModel.CacheMinutes));
        ConfigureBrowserKeepAliveTimer();
        await RefreshOperationsHealthAsync();
        ShowActionMessage(AppText.Get("SettingsSavedTitle"), AppText.Get("SettingsSavedMessage"), InfoBarSeverity.Success);
    }

    private async void RunSetupWizardButton_Click(object sender, RoutedEventArgs e) =>
        await ShowSetupWizardAsync(automatic: false);

    private void SettingsLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedIndex: >= 0 } comboBox)
        {
            ViewModel.LanguageIndex = comboBox.SelectedIndex;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWithLoadingAsync(
            ViewModel.LoadAsync,
            "LoadingRefreshTitle",
            "LoadingRefreshMessage");
        ApplyAppearance();
        ShowActionMessage(AppText.Get("RefreshTitle"), AppText.Get("RefreshMessage"), InfoBarSeverity.Success);
    }

    private void ApplyAppearance() =>
        App.ApplyAppearance(ViewModel.ThemeMode, ViewModel.AccentColorValue, ViewModel.HighContrastEnabled);

    private void OpenVirtualDriveButton_Click(object sender, RoutedEventArgs e) => ViewModel.OpenVirtualDrive();

    private async void OpenBrowserSessionButton_Click(object sender, RoutedEventArgs e) =>
        await RunWithLoadingAsync(
            () => OpenBrowserSessionAsync(navigate: true),
            "LoadingBrowserTitle",
            "LoadingBrowserMessage");

    private async void AddRouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsGraphAuthMode)
        {
            if (!ViewModel.IsExplorerAvailable)
            {
                SelectNavigationItem("Settings");
                ShowActionMessage(
                    AppText.Get("ExplorerAuthTitle"),
                    AppText.Get("ExplorerAuthMessage"),
                    InfoBarSeverity.Warning);
                return;
            }

            SelectNavigationItem("Explorer");
            await EnsureSharePointExplorerInitializedAsync();
            return;
        }

        await ShowRouteEditorAsync(null);
    }

    private async Task EnsureSharePointExplorerInitializedAsync(bool refresh = false)
    {
        await _sharePointExplorerInitializationLock.WaitAsync();
        try
        {
            if (!_sharePointExplorerInitialized)
            {
                await SharePointExplorer.InitializeAsync(_sharePointExplorerViewModel);
                _sharePointExplorerInitialized = true;
                return;
            }

            if (refresh)
            {
                await _sharePointExplorerViewModel.RefreshAsync();
            }
        }
        finally
        {
            _sharePointExplorerInitializationLock.Release();
        }
    }

    private async Task RefreshSharePointExplorerContextAsync()
    {
        var initializeExplorer = false;
        var initializeAssistant = false;
        var appliedContextRevision = 0L;
        await _sharePointExplorerContextLock.WaitAsync();
        try
        {
            var identityGeneration = Volatile.Read(ref _contentIdentityGeneration);
            var authenticationContext = BuildAuthenticationContextKey();
            var usesBrowserSession = ViewModel.IsBrowserSessionMode;
            ISharePointExplorerService service = usesBrowserSession
                ? _browserSharePointExplorer
                : _graphSharePointExplorer;
            var accountScope = ContentIdentityScope.Disconnected;
            var hasPersistentContentIdentity = false;

            if (usesBrowserSession && ViewModel.IsBrowserSessionVerified)
            {
                var resolvedScope = await _browserSessionService.GetAccountScopeAsync(ViewModel.Routes);
                hasPersistentContentIdentity = ContentIdentityScope.IsPersistentIdentityScope(resolvedScope);
                if (hasPersistentContentIdentity)
                {
                    accountScope = resolvedScope!;
                }
                else
                {
                    _browserSessionCacheScope = $"BROWSER-SESSION-{Guid.NewGuid():N}";
                    accountScope = _browserSessionCacheScope;
                }
            }
            else if (!usesBrowserSession && ViewModel.IsGraphAuthenticated)
            {
                var resolvedScope = await App.Services.Authentication.GetAccountScopeAsync();
                hasPersistentContentIdentity = ContentIdentityScope.IsPersistentIdentityScope(resolvedScope);
                if (hasPersistentContentIdentity)
                {
                    accountScope = resolvedScope!;
                }
                else
                {
                    _graphSessionCacheScope = $"GRAPH-SESSION-{Guid.NewGuid():N}";
                    accountScope = _graphSessionCacheScope;
                }
            }

            if (identityGeneration != Volatile.Read(ref _contentIdentityGeneration) ||
                !string.Equals(
                    authenticationContext,
                    BuildAuthenticationContextKey(),
                    StringComparison.Ordinal))
            {
                return;
            }

            ViewModel.UpdateContentIdentityAvailability(hasPersistentContentIdentity);

            var explorerContextChanged =
                _configuredExplorerUsesBrowser != usesBrowserSession ||
                !string.Equals(_contentAccountScope, accountScope, StringComparison.Ordinal);
            _browserContent.ConfigureAccountScope(accountScope);
            _contentAccountScope = accountScope;
            _configuredExplorerUsesBrowser = usesBrowserSession;
            _sharePointExplorerViewModel.ConfigureService(service, usesBrowserSession);
            var assistantRoutes = ViewModel.Routes
                .Where(route =>
                    hasPersistentContentIdentity &&
                    ViewModel.IsAssistantRouteAvailable(route) &&
                    (!usesBrowserSession ||
                     Uri.TryCreate(route.SharePointUrl, UriKind.Absolute, out var uri) &&
                     SharePointCookieStore.IsRouteVerified(uri)))
                .ToArray();
            var assistantContextChanged = explorerContextChanged ||
                                          HaveAssistantRoutesChanged(
                                              _contentAssistantViewModel.Routes,
                                              assistantRoutes);
            if (assistantContextChanged)
            {
                _contentAssistantViewModel.Configure(
                    accountScope,
                    ViewModel.CurrentAuthenticationMode,
                    assistantRoutes);
            }
            if (explorerContextChanged)
            {
                _sharePointExplorerInitialized = false;
            }
            if (assistantContextChanged)
            {
                _contentAssistantInitialized = false;
            }

            appliedContextRevision = Interlocked.Increment(ref _contentContextRevision);
            initializeExplorer = ViewModel.IsExplorerAvailable &&
                                 RootNavigation.SelectedItem is NavigationViewItem { Tag: "Explorer" } &&
                                 (explorerContextChanged || !_sharePointExplorerInitialized);
            initializeAssistant = ViewModel.IsAssistantAvailable &&
                                  RootNavigation.SelectedItem is NavigationViewItem { Tag: "Assistant" } &&
                                  (assistantContextChanged || !_contentAssistantInitialized);
        }
        finally
        {
            _sharePointExplorerContextLock.Release();
        }

        if (appliedContextRevision != Volatile.Read(ref _contentContextRevision))
        {
            return;
        }

        if (initializeExplorer && ViewModel.IsExplorerAvailable)
        {
            await EnsureSharePointExplorerInitializedAsync();
        }

        if (appliedContextRevision == Volatile.Read(ref _contentContextRevision) &&
            initializeAssistant &&
            ViewModel.IsAssistantAvailable)
        {
            await EnsureContentAssistantInitializedAsync();
        }
    }

    private async Task EnsureContentAssistantInitializedAsync()
    {
        if (_contentAssistantInitialized)
        {
            await _contentAssistantViewModel.RefreshHistoryAsync();
            return;
        }

        await ContentAssistant.InitializeAsync(_contentAssistantViewModel);
        _contentAssistantInitialized = true;
    }

    private static bool HaveAssistantRoutesChanged(
        IReadOnlyList<DriveRoute> configured,
        IReadOnlyCollection<DriveRoute> current)
    {
        if (configured.Count != current.Count)
        {
            return true;
        }

        var currentById = current.ToDictionary(route => route.Id);
        return configured.Any(route =>
            !currentById.TryGetValue(route.Id, out var candidate) ||
            !string.Equals(route.DisplayName, candidate.DisplayName, StringComparison.Ordinal) ||
            !string.Equals(route.SharePointUrl, candidate.SharePointUrl, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(route.RemotePath, candidate.RemotePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(route.DriveId, candidate.DriveId, StringComparison.Ordinal) ||
            !string.Equals(route.RootItemId, candidate.RootItemId, StringComparison.Ordinal));
    }

    private async void ContentAssistant_OpenRequested(ContentSearchResult result)
    {
        try
        {
            var route = ViewModel.Routes.FirstOrDefault(candidate => candidate.Id == result.RouteId);
            if (route is null)
            {
                return;
            }

            if (!ViewModel.IsRouteAllowed(route.SharePointUrl))
            {
                ShowActionMessage(
                    AppText.Get("PolicyRouteBlockedTitle"),
                    AppText.Get("PolicyRouteBlockedMessage"),
                    InfoBarSeverity.Warning);
                return;
            }

            var mountedRoutePath = ViewModel.CanOpenInExplorer
                ? App.Services.VirtualDrive.GetMountedRoutePath(route.Id)
                : null;
            if (!string.IsNullOrWhiteSpace(mountedRoutePath))
            {
                var path = result.RelativePath
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Aggregate(mountedRoutePath, Path.Combine);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
                return;
            }

            var target = ResolveContentResultUri(route, result);
            if (target is null)
            {
                return;
            }

            if (!ViewModel.IsRouteAllowed(target.AbsoluteUri))
            {
                ShowActionMessage(
                    AppText.Get("PolicyRouteBlockedTitle"),
                    AppText.Get("PolicyRouteBlockedMessage"),
                    InfoBarSeverity.Warning);
                return;
            }

            if (ViewModel.IsBrowserSessionMode)
            {
                SelectNavigationItem("Browser");
                await OpenBrowserSessionAsync(navigate: false);
                await NavigateBrowserAsync(target.AbsoluteUri);
                return;
            }

            await Launcher.LaunchUriAsync(target);
        }
        catch (Exception exception)
        {
            _contentAssistantViewModel.ReportExternalError(exception);
        }
    }

    private static Uri? ResolveContentResultUri(DriveRoute route, ContentSearchResult result)
    {
        if (Uri.TryCreate(result.RemoteLocator, UriKind.Absolute, out var absolute) &&
            absolute.Scheme == Uri.UriSchemeHttps &&
            SharePointRouteParser.IsAllowedSharePointUri(absolute))
        {
            return absolute;
        }

        if (Uri.TryCreate(route.SharePointUrl, UriKind.Absolute, out var siteUri) &&
            !string.IsNullOrWhiteSpace(result.RemoteLocator) &&
            result.RemoteLocator.StartsWith("/", StringComparison.Ordinal) &&
            Uri.TryCreate(new Uri(siteUri.GetLeftPart(UriPartial.Authority)), result.RemoteLocator, out var resolved) &&
            SharePointRouteParser.IsAllowedSharePointUri(resolved))
        {
            return resolved;
        }

        var routePath = SharePointRouteParser.NormalizeRemotePath(route.RemotePath);
        var relativePath = string.Join(
            '/',
            new[] { routePath, result.RelativePath }
                .Where(value => !string.IsNullOrWhiteSpace(value) && value != "/")
                .Select(value => value.Trim('/')));
        var fallback = SharePointRouteParser.BuildDisplayUrl(route.SharePointUrl, relativePath);
        return Uri.TryCreate(fallback, UriKind.Absolute, out var fallbackUri) &&
               SharePointRouteParser.IsAllowedSharePointUri(fallbackUri)
            ? fallbackUri
            : null;
    }

    private async Task ReinitializeSharePointExplorerAsync()
    {
        _sharePointExplorerViewModel.ResetForAuthenticationChange(requiresAuthentication: false);
        _sharePointExplorerInitialized = false;
        await EnsureSharePointExplorerInitializedAsync();
    }

    private async Task SharePointExplorer_SignInRequested()
    {
        if (!ViewModel.IsInteractiveSignInAllowed)
        {
            ShowActionMessage(
                AppText.Get("SettingsManagedTitle"),
                AppText.Get("PolicySignInBlocked"),
                InfoBarSeverity.Warning);
            return;
        }

        if (ViewModel.IsBrowserSessionMode)
        {
            await OpenBrowserSessionAsync(navigate: true);
            return;
        }

        if (!Guid.TryParse(ViewModel.ClientId, out _) && !await ShowClientIdDialogAsync())
        {
            return;
        }

        var hwnd = App.MainWindow is null ? IntPtr.Zero : WindowNative.GetWindowHandle(App.MainWindow);
        BeginGraphIdentityTransition();
        await ViewModel.SignInAsync(hwnd);
        await RefreshSharePointExplorerContextAsync();
        if (ViewModel.IsExplorerAvailable)
        {
            await ReinitializeSharePointExplorerAsync();
        }
    }

    private async Task SharePointExplorer_PinRequested(SharePointPinnedFolder folder)
    {
        if (!await _sharePointExplorerPinLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (!ViewModel.IsRouteAllowed(folder.SiteWebUrl))
            {
                ShowActionMessage(
                    AppText.Get("PolicyRouteBlockedTitle"),
                    AppText.Get("PolicyRouteBlockedMessage"),
                    InfoBarSeverity.Warning);
                return;
            }

            if (ViewModel.IsBrowserSessionMode)
            {
                if (!SharePointRouteParser.TryParse(folder.FolderWebUrl, out var browserFolder))
                {
                    throw new SharePointExplorerException(
                        SharePointExplorerStatus.InvalidResponse,
                        AppText.Get("InvalidLinkMessage"));
                }

                if (!Uri.TryCreate(folder.SiteWebUrl, UriKind.Absolute, out var sourceSiteUri) ||
                    !Uri.TryCreate(browserFolder.SiteUrl, UriKind.Absolute, out var parsedSiteUri) ||
                    !string.Equals(
                        sourceSiteUri.DnsSafeHost,
                        parsedSiteUri.DnsSafeHost,
                        StringComparison.OrdinalIgnoreCase) ||
                    !ViewModel.IsRouteAllowed(browserFolder.SiteUrl))
                {
                    ShowActionMessage(
                        AppText.Get("PolicyRouteBlockedTitle"),
                        AppText.Get("PolicyRouteBlockedMessage"),
                        InfoBarSeverity.Warning);
                    return;
                }

                var normalizedRemotePath = SharePointRouteParser.NormalizeRemotePath(browserFolder.RemotePath);
                if (ViewModel.Routes.Any(route =>
                        string.Equals(
                            route.SharePointUrl.TrimEnd('/'),
                            browserFolder.SiteUrl.TrimEnd('/'),
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            SharePointRouteParser.NormalizeRemotePath(route.RemotePath),
                            normalizedRemotePath,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    ShowActionMessage(
                        AppText.Get("ExplorerAlreadyPinnedTitle"),
                        AppText.Get("ExplorerAlreadyPinnedMessage"),
                        InfoBarSeverity.Informational);
                    return;
                }

                await ViewModel.AddRouteAsync(
                    folder.DisplayName,
                    browserFolder.SiteUrl,
                    normalizedRemotePath);
                await RefreshSharePointExplorerContextAsync();
                ShowActionMessage(
                    AppText.Get("ExplorerPinnedTitle"),
                    AppText.Format("ExplorerPinnedMessageFormat", folder.DisplayName),
                    InfoBarSeverity.Success);
                return;
            }

            if (ViewModel.Routes.Any(route =>
                    route.HasGraphIdentity &&
                    string.Equals(route.DriveId, folder.DriveId, StringComparison.Ordinal) &&
                    string.Equals(route.RootItemId, folder.ItemId, StringComparison.Ordinal)))
            {
                ShowActionMessage(
                    AppText.Get("ExplorerAlreadyPinnedTitle"),
                    AppText.Get("ExplorerAlreadyPinnedMessage"),
                    InfoBarSeverity.Informational);
                return;
            }

            var route = await ViewModel.AddGraphRouteAsync(folder);
            await RefreshSharePointExplorerContextAsync();
            ShowActionMessage(
                AppText.Get("ExplorerPinnedTitle"),
                AppText.Format("ExplorerPinnedMessageFormat", route.DisplayName),
                InfoBarSeverity.Success);
        }
        finally
        {
            _sharePointExplorerPinLock.Release();
        }
    }

    private Task SharePointExplorer_ManualRouteRequested() => ShowRouteEditorAsync(null);

    private async void EditRouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid routeId })
        {
            await ShowRouteEditorAsync(ViewModel.Routes.FirstOrDefault(route => route.Id == routeId));
        }
    }

    private async void TestRouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid routeId })
        {
            await RunWithLoadingAsync(
                () => ViewModel.TestRouteAsync(routeId, TestRouteWithBrowserSessionAsync),
                "LoadingTestTitle",
                "LoadingTestMessage");
            ShowActionMessage(AppText.Get("TestDoneTitle"), AppText.Get("TestDoneMessage"), InfoBarSeverity.Success);
        }
    }

    private async void RemoveRouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid routeId })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = AppText.Get("RemoveRouteDialogTitle"),
            Content = AppText.Get("RemoveRouteDialogMessage"),
            PrimaryButtonText = AppText.Get("ActionRemove"),
            CloseButtonText = AppText.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var route = ViewModel.Routes.FirstOrDefault(item => item.Id == routeId);
            if (route is not null)
            {
                _browserContent.InvalidateRoute(route);
            }

            await RunWithLoadingAsync(
                async () =>
                {
                    await App.Services.ContentIndex.RemoveRouteFromAllScopesAsync(routeId);
                    await ViewModel.RemoveRouteAsync(routeId);
                },
                "LoadingSaveTitle",
                "LoadingSaveMessage");
            await RefreshSharePointExplorerContextAsync();
            ShowActionMessage(AppText.Get("RouteRemovedTitle"), AppText.Get("RouteRemovedMessage"), InfoBarSeverity.Success);
        }
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString();
        if (tag == "Explorer" && !ViewModel.IsExplorerAvailable)
        {
            SelectNavigationItem("Home");
            return;
        }

        if (tag == "Assistant" && !ViewModel.IsAssistantAvailable)
        {
            SelectNavigationItem("Home");
            return;
        }

        if (tag is "Explorer" or "Assistant" && ViewModel.IsBrowserSessionMode)
        {
            if (_allowBrowserContentNavigationOnce)
            {
                _allowBrowserContentNavigationOnce = false;
            }
            else
            {
                SelectNavigationItem("Home");
                _ = VerifyAndOpenBrowserContentAsync(tag);
                return;
            }
        }

        HomeView.Visibility = tag == "Home" ? Visibility.Visible : Visibility.Collapsed;
        SharePointExplorer.Visibility = tag == "Explorer" ? Visibility.Visible : Visibility.Collapsed;
        ContentAssistant.Visibility = tag == "Assistant" ? Visibility.Visible : Visibility.Collapsed;
        RoutesView.Visibility = tag == "Routes" ? Visibility.Visible : Visibility.Collapsed;
        OperationsCenter.Visibility = tag == "Sync" ? Visibility.Visible : Visibility.Collapsed;
        BrowserView.Visibility = tag == "Browser" ? Visibility.Visible : Visibility.Collapsed;
        HelpView.Visibility = tag == "Help" ? Visibility.Visible : Visibility.Collapsed;
        AboutView.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        if (tag != "Browser" && _browserInitialized)
        {
            _ = PrepareBrowserForBackgroundAsync();
        }

        if (tag == "Browser")
        {
            _ = RunWithLoadingAsync(
                () => OpenBrowserSessionAsync(navigate: true),
                "LoadingBrowserTitle",
                "LoadingBrowserMessage");
        }

        if (tag == "Explorer")
        {
            _ = EnsureSharePointExplorerInitializedAsync();
        }

        if (tag == "Assistant")
        {
            _ = EnsureContentAssistantInitializedAsync();
        }
    }

    private async Task VerifyAndOpenBrowserContentAsync(string tag)
    {
        if (Interlocked.Exchange(ref _browserContentNavigationPending, 1) != 0)
        {
            return;
        }

        try
        {
            BeginBrowserIdentityTransition(invalidateBrowserVerification: true);
            await VerifyBrowserSessionAsync(
                showMessage: false,
                restoreFromWebView: true);

            var canOpen = tag switch
            {
                "Explorer" => ViewModel.IsExplorerAvailable,
                "Assistant" => ViewModel.IsAssistantAvailable,
                _ => false
            };
            if (canOpen)
            {
                _allowBrowserContentNavigationOnce = true;
                SelectNavigationItem(tag);
            }
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Browser content identity verification failed.", exception);
        }
        finally
        {
            Interlocked.Exchange(ref _browserContentNavigationPending, 0);
        }
    }

    private async Task<bool> ConfirmResetAsync(LocalDataInventory inventory)
    {
        var info = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Error,
            Title = AppText.Get("ResetDialogTitle"),
            Message = AppText.Get("ResetDialogWarning")
        };
        var form = new StackPanel { Spacing = 12, MaxWidth = 620 };
        form.Children.Add(info);
        form.Children.Add(new TextBlock
        {
            Text = AppText.Format(
                "ResetDialogInventoryFormat",
                inventory.ItemCount,
                FormatResetBytes(inventory.Bytes)),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        foreach (var category in inventory.Categories)
        {
            form.Children.Add(new TextBlock
            {
                Text = AppText.Format(
                    "ResetDialogCategoryFormat",
                    category.Name,
                    category.ItemCount,
                    FormatResetBytes(category.Bytes)),
                TextWrapping = TextWrapping.Wrap
            });
        }
        form.Children.Add(new TextBlock
        {
            Text = AppText.Get("ResetDialogAccessibleConfirmation"),
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            Title = AppText.Get("ResetDialogTitle"),
            Content = form,
            PrimaryButtonText = AppText.Get("ActionResetApp"),
            CloseButtonText = AppText.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string FormatResetBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        double value = Math.Max(0, bytes);
        var index = 0;
        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return index == 0 ? $"{value:0} {suffixes[index]}" : $"{value:0.0} {suffixes[index]}";
    }

    private async Task ShowSetupWizardIfNeededAsync()
    {
        if (!ViewModel.ShouldShowSetupWizard)
        {
            return;
        }

        await ShowSetupWizardAsync(automatic: true);
    }

    private async Task ShowSetupWizardAsync(bool automatic)
    {
        if (!await _setupWizardDisplayGate.WaitAsync(0))
        {
            return;
        }

        var originalLanguage = ViewModel.CurrentSettings.LanguageCode;
        var settingsApplied = false;
        RootNavigation.IsEnabled = false;
        try
        {
            var result = await SetupWizard.ShowAsync(
                ViewModel.CreateSetupWizardDraft(),
                ViewModel.EnterprisePolicy,
                ViewModel.CreateSetupWizardCapabilities(),
                ViewModel.GetSetupWizardMountPointOptions());

            if (_subscriptionsDisposed)
            {
                return;
            }

            if (result is null)
            {
                AppText.SetLanguage(originalLanguage);
                ApplyAppearance();
                if (automatic)
                {
                    ShowActionMessage(
                        AppText.Get("WizardPostponedTitle"),
                        AppText.Get("WizardPostponedMessage"),
                        InfoBarSeverity.Informational);
                }

                return;
            }

            settingsApplied = true;
            ApplyAppearance();
            _browserContent.ConfigureCache(TimeSpan.FromMinutes(ViewModel.CacheMinutes));
            ConfigureBrowserKeepAliveTimer();
            await RefreshOperationsHealthAsync();

            var applyWarning = SetupWizard.LastApplyResult?.WarningMessage;
            ShowActionMessage(
                string.IsNullOrWhiteSpace(applyWarning)
                    ? AppText.Get("WizardCompletedTitle")
                    : AppText.Get("WizardCompletedWarningTitle"),
                string.IsNullOrWhiteSpace(applyWarning)
                    ? AppText.Get("WizardCompletedMessage")
                    : applyWarning,
                string.IsNullOrWhiteSpace(applyWarning)
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning);

            if (!result.ConnectNow)
            {
                return;
            }

            if (!ViewModel.IsInteractiveSignInAllowed)
            {
                ShowActionMessage(
                    AppText.Get("WizardConnectionDeferredTitle"),
                    AppText.Get("PolicySignInBlocked"),
                    InfoBarSeverity.Warning);
                return;
            }

            if (result.AuthenticationMode == AuthenticationMode.BrowserSession)
            {
                SelectNavigationItem("Browser");
                await RunWithLoadingAsync(
                    async () =>
                    {
                        await OpenBrowserSessionAsync(navigate: false);
                        await NavigateBrowserAsync(result.BrowserSessionStartUrl);
                    },
                    "LoadingBrowserTitle",
                    "LoadingBrowserMessage");
                BrowserInfoBar.Title = AppText.Get("WizardBrowserLoginTitle");
                BrowserInfoBar.Message = AppText.Get("WizardBrowserLoginMessage");
                BrowserInfoBar.Severity = InfoBarSeverity.Informational;
                return;
            }

            var hwnd = App.MainWindow is null
                ? IntPtr.Zero
                : WindowNative.GetWindowHandle(App.MainWindow);
            BeginGraphIdentityTransition();
            await RunWithLoadingAsync(
                () => ViewModel.SignInAsync(hwnd),
                "LoadingBrowserTitle",
                "LoadingBrowserMessage");
            SelectNavigationItem("Explorer");
            await EnsureSharePointExplorerInitializedAsync(refresh: true);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Setup wizard failed.", ex);
            if (!settingsApplied)
            {
                AppText.SetLanguage(originalLanguage);
                ApplyAppearance();
                ShowActionMessage(
                    AppText.Get("WizardApplyErrorTitle"),
                    AppText.Get("WizardApplyErrorMessage"),
                    InfoBarSeverity.Error);
            }
            else
            {
                ApplyAppearance();
                ShowActionMessage(
                    AppText.Get("WizardConnectionDeferredTitle"),
                    AppText.Get("WizardConnectionDeferredMessage"),
                    InfoBarSeverity.Warning);
            }
        }
        finally
        {
            if (!_subscriptionsDisposed)
            {
                RootNavigation.IsEnabled = true;
                RootNavigation.Focus(FocusState.Programmatic);
            }

            _setupWizardDisplayGate.Release();
        }
    }

    private Task<SetupWizardApplyResult> SetupWizard_ApplyRequested(SetupWizardDraft draft) =>
        ViewModel.ApplySetupWizardAsync(draft);

    private void SetupWizard_AppearancePreviewRequested(SetupWizardDraft draft)
    {
        AppText.SetLanguage(draft.LanguageCode);
        App.ApplyAppearance(
            draft.ThemeMode,
            ViewModel.AccentColorValue,
            draft.HighContrastEnabled);
    }

    private async Task<bool> ShowClientIdDialogAsync()
    {
        var clientIdBox = new TextBox
        {
            Header = AppText.Get("SettingsClientIdHeader"),
            PlaceholderText = AppText.Get("SettingsClientIdPlaceholder"),
            Text = ViewModel.ClientId
        };
        var tenantBox = new TextBox
        {
            Header = AppText.Get("SettingsTenantHeader"),
            PlaceholderText = AppText.Get("SettingsTenantPlaceholder"),
            Text = string.IsNullOrWhiteSpace(ViewModel.TenantId) ? "organizations" : ViewModel.TenantId
        };
        var info = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = AppText.Get("ClientInfoTitle"),
            Message = AppText.Get("ClientInfoMessage")
        };

        var form = new StackPanel { Spacing = 12, MaxWidth = 620 };
        form.Children.Add(info);
        form.Children.Add(clientIdBox);
        form.Children.Add(tenantBox);

        var dialog = new ContentDialog
        {
            Title = AppText.Get("ClientDialogTitle"),
            Content = form,
            PrimaryButtonText = AppText.Get("ActionSignIn"),
            CloseButtonText = AppText.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!Guid.TryParse(clientIdBox.Text.Trim(), out var clientId) || clientId == Guid.Empty)
            {
                args.Cancel = true;
                info.Severity = InfoBarSeverity.Warning;
                info.Title = AppText.Get("InvalidClientIdTitle");
                info.Message = AppText.Get("InvalidClientIdMessage");
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        ViewModel.ClientId = clientIdBox.Text.Trim();
        ViewModel.TenantId = string.IsNullOrWhiteSpace(tenantBox.Text) ? "organizations" : tenantBox.Text.Trim();
        await ViewModel.SaveSettingsAsync();
        return true;
    }

    private async Task ShowRouteEditorAsync(DriveRoute? route, string? initialUrl = null)
    {
        var parsedInitialUrl = initialUrl ?? (route is null
            ? string.Empty
            : SharePointRouteParser.BuildDisplayUrl(route.SharePointUrl, route.RemotePath));

        var nameBox = new TextBox
        {
            Header = AppText.Get("RouteNameHeader"),
            PlaceholderText = AppText.Get("RouteNamePlaceholder"),
            Text = route?.DisplayName ?? string.Empty
        };
        var linkBox = new TextBox
        {
            Header = AppText.Get("RouteLinkHeader"),
            PlaceholderText = AppText.Get("RouteLinkPlaceholder"),
            Text = parsedInitialUrl,
            IsReadOnly = route is not null
        };
        var preview = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = AppText.Get("RouteLinkInfoTitle"),
            Message = AppText.Get("RouteLinkInfoMessage")
        };

        var form = new StackPanel { Spacing = 12, MaxWidth = 680 };
        form.Children.Add(preview);
        form.Children.Add(nameBox);
        form.Children.Add(linkBox);
        form.Children.Add(new TextBlock
        {
            Text = AppText.Get(route is null ? "RouteEditorHelp" : "RouteLocationImmutableHelp"),
            TextWrapping = TextWrapping.Wrap
        });

        var userChangedName = route is not null;
        nameBox.TextChanged += (_, _) => userChangedName = true;
        linkBox.TextChanged += (_, _) => UpdateRoutePreview(linkBox.Text, nameBox, preview, userChangedName);
        UpdateRoutePreview(linkBox.Text, nameBox, preview, userChangedName);

        var dialog = new ContentDialog
        {
            Title = route is null ? AppText.Get("RouteEditorAddTitle") : AppText.Get("RouteEditorEditTitle"),
            Content = form,
            PrimaryButtonText = route is null ? AppText.Get("RouteEditorAddTitle") : AppText.Get("CommonSave"),
            CloseButtonText = AppText.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        SharePointRouteInput parsed = default!;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!SharePointRouteParser.TryParse(linkBox.Text, out parsed))
            {
                args.Cancel = true;
                preview.Severity = InfoBarSeverity.Warning;
                preview.Title = AppText.Get("InvalidLinkTitle");
                preview.Message = AppText.Get("InvalidLinkMessage");
                return;
            }

            if (!ViewModel.IsRouteAllowed(parsed.SiteUrl))
            {
                args.Cancel = true;
                preview.Severity = InfoBarSeverity.Warning;
                preview.Title = AppText.Get("PolicyRouteBlockedTitle");
                preview.Message = AppText.Get("PolicyRouteBlockedMessage");
                return;
            }

            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                args.Cancel = true;
                preview.Severity = InfoBarSeverity.Warning;
                preview.Title = AppText.Get("MissingRouteNameTitle");
                preview.Message = AppText.Get("MissingRouteNameMessage");
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (route is null)
        {
            if (ViewModel.IsGraphAuthMode)
            {
                try
                {
                    await RunWithLoadingAsync(
                        async () =>
                        {
                            var folder = await _sharePointExplorerViewModel.ResolveFolderAsync(parsed);
                            await SharePointExplorer_PinRequested(folder with
                            {
                                DisplayName = nameBox.Text.Trim()
                            });
                        },
                        "LoadingSaveTitle",
                        "LoadingSaveMessage");
                }
                catch (Exception exception)
                {
                    StartupDiagnostics.Write("Resolving a manual SharePoint folder failed.", exception);
                    ShowActionMessage(
                        AppText.Get("ExplorerErrorTitle"),
                        GetSharePointResolutionErrorMessage(exception),
                        InfoBarSeverity.Error);
                }

                return;
            }

            await RunWithLoadingAsync(
                () => ViewModel.AddRouteAsync(nameBox.Text, parsed.SiteUrl, parsed.RemotePath),
                "LoadingSaveTitle",
                "LoadingSaveMessage");
            await RefreshSharePointExplorerContextAsync();
            ShowActionMessage(AppText.Get("RoutePinnedTitle"), AppText.Get("RoutePinnedMessage"), InfoBarSeverity.Success);
            return;
        }

        await RunWithLoadingAsync(
            () => ViewModel.UpdateRouteAsync(route.Id, nameBox.Text, parsed.SiteUrl, parsed.RemotePath),
            "LoadingSaveTitle",
            "LoadingSaveMessage");
        ShowActionMessage(AppText.Get("RouteUpdatedTitle"), AppText.Get("RouteUpdatedMessage"), InfoBarSeverity.Success);
    }

    private static string GetSharePointResolutionErrorMessage(Exception exception) =>
        exception is SharePointExplorerException explorerException
            ? explorerException.Status switch
            {
                SharePointExplorerStatus.AuthenticationRequired => AppText.Get("BrowserRouteExpired"),
                SharePointExplorerStatus.Forbidden => AppText.Get("GraphForbiddenFolder"),
                SharePointExplorerStatus.NotFound => AppText.Get("GraphPathNotFound"),
                _ => AppText.Get("GraphCannotValidate")
            }
            : AppText.Get("GraphCannotValidate");

    private static void UpdateRoutePreview(string link, TextBox nameBox, InfoBar preview, bool userChangedName)
    {
        if (!SharePointRouteParser.TryParse(link, out var parsed))
        {
            preview.Severity = InfoBarSeverity.Informational;
            preview.Title = AppText.Get("RouteLinkInfoTitle");
            preview.Message = AppText.Get("RouteLinkInfoMessage");
            return;
        }

        if (!userChangedName && string.IsNullOrWhiteSpace(nameBox.Text))
        {
            nameBox.Text = parsed.SuggestedName;
        }

        preview.Severity = InfoBarSeverity.Success;
        preview.Title = AppText.Get("LinkRecognizedTitle");
        preview.Message = AppText.Format("LinkPreviewFormat", parsed.SiteUrl, parsed.RemotePath);
    }

    private static StackPanel BuildOptionContent(string title, string description)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 540
        });
        return panel;
    }

    private async Task RunWithLoadingAsync(Func<Task> action, string titleKey, string messageKey)
    {
        _localBusyDepth++;
        SetLoadingText(titleKey, messageKey);
        UpdateLoadingOverlay();

        try
        {
            await Task.Yield();
            await action();
        }
        finally
        {
            _localBusyDepth = Math.Max(0, _localBusyDepth - 1);
            if (_localBusyDepth == 0)
            {
                SetLoadingText("LoadingTitle", "LoadingMessage");
            }

            UpdateLoadingOverlay();
        }
    }

    private void SetLoadingText(string titleKey, string messageKey)
    {
        LoadingTitleText.Text = AppText.Get(titleKey);
        LoadingMessageText.Text = AppText.Get(messageKey);
    }

    private void UpdateLoadingOverlay()
    {
        var isActive = ViewModel.IsBusy || _localBusyDepth > 0;
        LoadingRing.IsActive = isActive;
        LoadingOverlay.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OperationsCenter_RetryRequested(Guid jobId)
    {
        try
        {
            await _uploadQueue.RetryAsync(jobId);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Retrying an upload failed.", ex);
            ShowActionMessage(AppText.Get("OperationFailedTitle"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OperationsCenter_ConflictResolutionRequested(ConflictResolutionRequest request)
    {
        try
        {
            SyncConflictActionResult? result = null;
            switch (request.Action)
            {
                case ConflictResolutionAction.ExportLocalCopy:
                {
                    var job = ViewModel.SyncJobs.FirstOrDefault(item => item.Id == request.JobId);
                    if (job is null)
                    {
                        return;
                    }

                    var destination = await PickNewFileAsync(job.FileName, "Local copy");
                    if (destination is null)
                    {
                        return;
                    }

                    await RunWithLoadingAsync(
                        async () =>
                        {
                            result = await _uploadQueue.ExportLocalPayloadAsync(request.JobId, destination);
                        },
                        "LoadingSaveTitle",
                        "LoadingSaveMessage");
                    break;
                }

                case ConflictResolutionAction.UseRemoteVersion:
                    if (!await ConfirmConflictActionAsync(
                            AppText.Get("ConflictUseRemoteTitle"),
                            AppText.Get("ConflictUseRemoteMessage"),
                            requireAcknowledgement: false))
                    {
                        return;
                    }

                    await RunWithLoadingAsync(
                        async () =>
                        {
                            result = await _uploadQueue.DiscardLocalPayloadAsync(request.JobId);
                        },
                        "LoadingSaveTitle",
                        "LoadingSaveMessage");
                    break;

                case ConflictResolutionAction.ReplaceRemote:
                    if (!await ConfirmConflictActionAsync(
                            AppText.Get("ConflictReplaceTitle"),
                            AppText.Get("ConflictReplaceMessage"),
                            requireAcknowledgement: true))
                    {
                        return;
                    }

                    await RunWithLoadingAsync(
                        async () =>
                        {
                            result = await _uploadQueue.ForceReplaceAsync(request.JobId);
                        },
                        "LoadingSaveTitle",
                        "LoadingSaveMessage");
                    break;
            }

            if (result?.Succeeded == true)
            {
                App.Services.DiagnosticLog.Write(DiagnosticEvent.Create(
                    DiagnosticLevel.Information,
                    "conflict.decision",
                    "A user-approved conflict action was applied.",
                    properties: new Dictionary<string, string?>
                    {
                        ["operationId"] = request.JobId.ToString("N"),
                        ["action"] = request.Action.ToString()
                    }));
                var exported = result.Status == SyncConflictActionStatus.Exported;
                ShowActionMessage(
                    AppText.Get(exported ? "ConflictExportedTitle" : "ConflictResolvedTitle"),
                    AppText.Get(exported ? "ConflictExportedMessage" : "ConflictResolvedMessage"),
                    InfoBarSeverity.Success);
            }
            else if (result is not null)
            {
                ShowActionMessage(
                    AppText.Get("OperationFailedTitle"),
                    result.Error ?? AppText.Get("OperationFailedTitle"),
                    InfoBarSeverity.Error);
            }

            await RefreshOperationsHealthAsync();
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Resolving a synchronization conflict failed.", ex);
            ShowActionMessage(AppText.Get("OperationFailedTitle"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task<bool> ConfirmConflictActionAsync(
        string title,
        string message,
        bool requireAcknowledgement)
    {
        var content = new StackPanel { Spacing = 12, MaxWidth = 560 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        CheckBox? acknowledgement = null;
        if (requireAcknowledgement)
        {
            acknowledgement = new CheckBox
            {
                Content = AppText.Get("ConflictReplaceAcknowledgement")
            };
            content.Children.Add(acknowledgement);
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = AppText.Get("CommonContinue"),
            CloseButtonText = AppText.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (acknowledgement is not null && acknowledgement.IsChecked != true)
            {
                args.Cancel = true;
                acknowledgement.Focus(FocusState.Programmatic);
            }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OperationsCenter_HealthRefreshRequested() => await RefreshOperationsHealthAsync();

    private async void OperationsCenter_SupportExportRequested()
    {
        try
        {
            var consent = new ContentDialog
            {
                Title = AppText.Get("SupportBundleConsentTitle"),
                Content = AppText.Get("SupportBundleConsentMessage"),
                PrimaryButtonText = AppText.Get("ActionExport"),
                CloseButtonText = AppText.Get("CommonCancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await consent.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var destination = await PickNewFileAsync("EasyShare-support.zip", "ZIP archive", ".zip");
            if (destination is null)
            {
                return;
            }

            SupportBundleResult? result = null;
            await RunWithLoadingAsync(
                async () =>
                {
                    result = await App.Services.SupportBundles.CreateAsync(
                        destination,
                        new SupportBundleContext(
                            App.Services.EnterprisePolicy,
                            new Dictionary<string, string?>
                            {
                                ["routeCount"] = ViewModel.Routes.Count.ToString(),
                                ["pendingUploadCount"] = ViewModel.PendingUploadCount.ToString(),
                                ["healthSummary"] = _operationsViewModel.HealthSummary
                            }));
                },
                "LoadingSaveTitle",
                "LoadingSaveMessage");

            ShowActionMessage(
                AppText.Get(result?.Succeeded == true ? "SupportBundleSavedTitle" : "OperationFailedTitle"),
                result?.Succeeded == true
                    ? AppText.Get("SupportBundleSavedMessage")
                    : result?.Error ?? AppText.Get("OperationFailedTitle"),
                result?.Succeeded == true ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Exporting a support bundle failed.", ex);
            ShowActionMessage(AppText.Get("OperationFailedTitle"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OperationsCenter_OfflinePinRequested(Guid routeId)
    {
        var route = ViewModel.Routes.FirstOrDefault(item => item.Id == routeId);
        if (route is null)
        {
            return;
        }

        try
        {
            OfflineCachePinResult? result = null;
            await RunWithLoadingAsync(
                async () =>
                {
                    result = await App.Services.OfflineCache.PinRouteAsync(
                        route,
                        ViewModel.CurrentSettings);
                },
                "LoadingSaveTitle",
                "LoadingSaveMessage");
            await RefreshOperationsHealthAsync();
            ShowActionMessage(
                AppText.Get(result?.Succeeded == true ? "OfflinePinnedTitle" : "OperationFailedTitle"),
                result?.Succeeded == true
                    ? AppText.Get("OfflinePinnedMessage")
                    : result?.Error ?? AppText.Get("OperationFailedTitle"),
                result?.Succeeded == true ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Caching SharePoint content for offline use failed.", ex);
            ShowActionMessage(AppText.Get("OperationFailedTitle"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OperationsCenter_OfflineRemoveRequested(string key)
    {
        try
        {
            var removed = await App.Services.OfflineCache.RemoveAsync(key);
            await RefreshOperationsHealthAsync();
            ShowActionMessage(
                AppText.Get(removed ? "OfflineRemovedTitle" : "OperationFailedTitle"),
                AppText.Get(removed ? "OfflineRemovedMessage" : "OperationFailedTitle"),
                removed ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Removing an offline cache entry failed.", ex);
            ShowActionMessage(AppText.Get("OperationFailedTitle"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private void Notifications_Activated(AppNotificationActivation activation)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (App.MainWindow is MainWindow mainWindow)
            {
                mainWindow.RestoreAndActivate();
            }

            switch (activation.Destination)
            {
                case "Conflicts":
                    SelectNavigationItem("Sync");
                    OperationsCenter.SelectConflicts();
                    break;
                case "Health":
                    SelectNavigationItem("Sync");
                    OperationsCenter.SelectHealth();
                    break;
                case "Offline":
                    SelectNavigationItem("Sync");
                    OperationsCenter.SelectOffline();
                    break;
                case "Updates":
                    SelectNavigationItem("About");
                    break;
                default:
                    SelectNavigationItem("Home");
                    break;
            }
        });
    }

    private async Task RefreshOperationsHealthAsync()
    {
        var sessionAvailable = ViewModel.IsBrowserSessionMode
            ? ViewModel.Routes.Count == 0 || ViewModel.Routes.Any(route =>
                Uri.TryCreate(route.SharePointUrl, UriKind.Absolute, out var uri) &&
                ViewModel.IsRouteAllowed(route.SharePointUrl) &&
                SharePointCookieStore.IsRouteVerified(uri) &&
                SharePointCookieStore.TryGetCookieHeader(uri, out _))
            : ViewModel.ConnectionSeverity == InfoBarSeverity.Success;

        await _operationsViewModel.RefreshHealthAsync(
            ViewModel.CurrentVirtualDriveStatus,
            _browserInitialized,
            sessionAvailable);
        _operationsViewModel.ReplaceOfflineEntries(
            await App.Services.OfflineCache.GetEntriesAsync());

        var driveHealthy = ViewModel.CurrentVirtualDriveStatus.CanOpenInExplorer;
        if (_lastDriveHealthy == true && !driveHealthy &&
            ViewModel.IsNotificationDeliveryEnabled && ViewModel.NotifyDriveDisconnected)
        {
            App.Services.Notifications.Show(
                "drive",
                AppText.Get("NotificationDriveDisconnectedTitle"),
                AppText.Get("NotificationDriveDisconnectedMessage"),
                "Health");
        }

        if (_lastSessionHealthy == true && !sessionAvailable &&
            ViewModel.IsNotificationDeliveryEnabled && ViewModel.NotifySessionExpired)
        {
            App.Services.Notifications.Show(
                "session",
                AppText.Get("NotificationSessionExpiredTitle"),
                AppText.Get("NotificationSessionExpiredMessage"),
                "Health");
        }

        _lastDriveHealthy = driveHealthy;
        _lastSessionHealthy = sessionAvailable;
    }

    private void NotifySyncState(SyncJob job)
    {
        if (_lastNotifiedStates.TryGetValue(job.Id, out var previous) && previous == job.State)
        {
            return;
        }

        _lastNotifiedStates[job.Id] = job.State;
        if (!ViewModel.IsNotificationDeliveryEnabled)
        {
            return;
        }

        switch (job.State)
        {
            case SyncJobState.Conflict when ViewModel.NotifyConflict:
                App.Services.Notifications.Show(
                    "conflict",
                    AppText.Get("NotificationConflictTitle"),
                    AppText.Get("NotificationConflictMessage"),
                    "Conflicts",
                    job.Id.ToString("N"));
                break;
            case SyncJobState.Failed when ViewModel.NotifyUploadFailed:
                App.Services.Notifications.Show(
                    "upload-failed",
                    AppText.Get("NotificationUploadFailedTitle"),
                    AppText.Get("NotificationUploadFailedMessage"),
                    "Conflicts",
                    job.Id.ToString("N"));
                break;
            case SyncJobState.Completed when ViewModel.NotifyUploadCompleted:
                App.Services.Notifications.Show(
                    "upload-completed",
                    AppText.Get("NotificationUploadCompletedTitle"),
                    AppText.Get("NotificationUploadCompletedMessage"),
                    "Home",
                    job.Id.ToString("N"));
                break;
        }
    }

    private async Task<string?> PickNewFileAsync(
        string suggestedFileName,
        string typeLabel,
        string? forcedExtension = null)
    {
        var extension = forcedExtension ?? Path.GetExtension(suggestedFileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".bin";
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedFileName),
            DefaultFileExtension = extension
        };
        picker.FileTypeChoices.Add(typeLabel, [extension]);
        if (App.MainWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return null;
        }

        var path = file.Path;
        await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
        return path;
    }

    private void ShowActionMessage(string title, string message, InfoBarSeverity severity)
    {
        _actionMessageTimer.Stop();
        ActionInfoBar.Title = title;
        ActionInfoBar.Message = message;
        ActionInfoBar.Severity = severity;
        ActionInfoBar.IsOpen = true;
        _actionMessageTimer.Start();
    }

    private void SelectNavigationItem(string tag)
    {
        foreach (var item in RootNavigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                RootNavigation.SelectedItem = item;
                break;
            }
        }
    }

    private static Uri? ParseBrowserUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"https://{normalized}";
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ? uri : null;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = AppText.Get("CommonOk"),
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    private void ApplySummaryCardLayout(double width)
    {
        if (width >= 900)
        {
            SummaryCards.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            SummaryCards.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            SummaryCards.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);

            PlaceCard(AccountCard, row: 0, column: 0, columnSpan: 1);
            PlaceCard(VirtualRootCard, row: 0, column: 1, columnSpan: 1);
            PlaceCard(RoutesCountCard, row: 0, column: 2, columnSpan: 1);
            return;
        }

        if (width >= 620)
        {
            SummaryCards.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            SummaryCards.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            SummaryCards.ColumnDefinitions[2].Width = new GridLength(0);

            PlaceCard(AccountCard, row: 0, column: 0, columnSpan: 1);
            PlaceCard(VirtualRootCard, row: 0, column: 1, columnSpan: 1);
            PlaceCard(RoutesCountCard, row: 1, column: 0, columnSpan: 2);
            return;
        }

        SummaryCards.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        SummaryCards.ColumnDefinitions[1].Width = new GridLength(0);
        SummaryCards.ColumnDefinitions[2].Width = new GridLength(0);

        PlaceCard(AccountCard, row: 0, column: 0, columnSpan: 1);
        PlaceCard(VirtualRootCard, row: 1, column: 0, columnSpan: 1);
        PlaceCard(RoutesCountCard, row: 2, column: 0, columnSpan: 1);
    }

    private void ApplyResponsivePageLayouts(double width)
    {
        var isNarrow = width < 820;
        var compactAbout = width < 1280;
        var compactFooter = width < 900;

        HelpLayoutGrid.ColumnDefinitions[0].Width = isNarrow
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(320);
        HelpLayoutGrid.ColumnDefinitions[1].Width = isNarrow
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(HelpIntroCard, 0);
        Grid.SetColumn(HelpIntroCard, 0);
        Grid.SetRowSpan(HelpIntroCard, isNarrow ? 1 : 2);
        Grid.SetRow(HelpQuestionsPanel, isNarrow ? 1 : 0);
        Grid.SetColumn(HelpQuestionsPanel, isNarrow ? 0 : 1);
        Grid.SetRowSpan(HelpQuestionsPanel, isNarrow ? 1 : 2);

        AboutLayoutGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        AboutLayoutGrid.ColumnDefinitions[1].Width = compactAbout
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(AboutHeroCard, 0);
        Grid.SetColumnSpan(AboutHeroCard, compactAbout ? 1 : 2);
        Grid.SetRow(AboutHeroCard, 0);
        Grid.SetColumn(AboutInfoCard, 0);
        Grid.SetRow(AboutInfoCard, 1);
        Grid.SetColumn(AboutUpdateCard, compactAbout ? 0 : 1);
        Grid.SetRow(AboutUpdateCard, compactAbout ? 2 : 1);
        Grid.SetColumn(AboutFooterCard, 0);
        Grid.SetColumnSpan(AboutFooterCard, compactAbout ? 1 : 2);
        Grid.SetRow(AboutFooterCard, compactAbout ? 3 : 2);

        AboutFooterGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        AboutFooterGrid.ColumnDefinitions[1].Width = compactFooter
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        AboutFooterGrid.ColumnDefinitions[2].Width = compactFooter
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(AboutFooterAccess, 0);
        Grid.SetRow(AboutFooterAccess, 0);
        Grid.SetColumn(AboutFooterSession, compactFooter ? 0 : 1);
        Grid.SetRow(AboutFooterSession, compactFooter ? 1 : 0);
        Grid.SetColumn(AboutFooterUpdates, compactFooter ? 0 : 2);
        Grid.SetRow(AboutFooterUpdates, compactFooter ? 2 : 0);

        var compactSettings = width < 900;
        SettingsLayoutGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        SettingsLayoutGrid.ColumnDefinitions[1].Width = compactSettings
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(SettingsAccessCard, 0);
        Grid.SetColumnSpan(SettingsAccessCard, compactSettings ? 1 : 2);
        Grid.SetRow(SettingsAccessCard, 0);
        Grid.SetColumn(SettingsDriveCard, 0);
        Grid.SetRow(SettingsDriveCard, 1);
        Grid.SetColumn(SettingsSessionCard, compactSettings ? 0 : 1);
        Grid.SetRow(SettingsSessionCard, compactSettings ? 2 : 1);
        Grid.SetColumn(SettingsPersonalizationCard, 0);
        Grid.SetColumnSpan(SettingsPersonalizationCard, compactSettings ? 1 : 2);
        Grid.SetRow(SettingsPersonalizationCard, compactSettings ? 3 : 2);
        SettingsPersonalizationOptionsGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        SettingsPersonalizationOptionsGrid.ColumnDefinitions[1].Width = compactSettings
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        SettingsPersonalizationOptionsGrid.RowDefinitions[1].Height = compactSettings
            ? GridLength.Auto
            : new GridLength(0);
        Grid.SetColumn(SettingsHighContrastToggle, compactSettings ? 0 : 1);
        Grid.SetRow(SettingsHighContrastToggle, compactSettings ? 1 : 0);
        Grid.SetColumn(SettingsNotificationsCard, 0);
        Grid.SetColumnSpan(SettingsNotificationsCard, compactSettings ? 1 : 2);
        Grid.SetRow(SettingsNotificationsCard, compactSettings ? 4 : 3);
        SettingsNotificationsGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        SettingsNotificationsGrid.ColumnDefinitions[1].Width = compactSettings
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        SettingsNotificationsGrid.RowDefinitions[1].Height = compactSettings
            ? GridLength.Auto
            : new GridLength(0);
        Grid.SetColumn(SettingsOfflinePanel, compactSettings ? 0 : 1);
        Grid.SetRow(SettingsOfflinePanel, compactSettings ? 1 : 0);
        Grid.SetColumn(SettingsActionsPanel, 0);
        Grid.SetColumnSpan(SettingsActionsPanel, compactSettings ? 1 : 2);
        Grid.SetRow(SettingsActionsPanel, compactSettings ? 5 : 4);

        var compactAccessFields = width < 720;
        SettingsAccessFieldsGrid.ColumnDefinitions[1].Width = compactAccessFields
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumnSpan(SettingsAccessModeHelp, compactAccessFields ? 2 : 1);
        Grid.SetRow(SettingsAccessModeHelp, compactAccessFields ? 1 : 0);
        Grid.SetColumn(SettingsAccessModeHelp, compactAccessFields ? 0 : 1);
        Grid.SetRow(SettingsClientId, compactAccessFields ? 2 : 1);
        Grid.SetColumn(SettingsClientId, 0);
        Grid.SetColumnSpan(SettingsClientId, compactAccessFields ? 2 : 1);
        Grid.SetRow(SettingsTenant, compactAccessFields ? 3 : 1);
        Grid.SetColumn(SettingsTenant, compactAccessFields ? 0 : 1);
        Grid.SetColumnSpan(SettingsTenant, compactAccessFields ? 2 : 1);
        Grid.SetRow(SettingsClientIdHelp, compactAccessFields ? 4 : 2);
    }

    private static void PlaceCard(FrameworkElement card, int row, int column, int columnSpan)
    {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        Grid.SetColumnSpan(card, columnSpan);
    }

    private void ApplyStartupWindowState()
    {
        if ((!App.StartMinimized && !ViewModel.StartMinimized) || App.MainWindow is null)
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        ShowWindow(hwnd, 6);
        if (App.MainWindow is MainWindow mainWindow)
        {
            mainWindow.HideToTray();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
