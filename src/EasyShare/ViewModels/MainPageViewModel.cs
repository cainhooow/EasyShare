using System.Collections.ObjectModel;
using System.Diagnostics;
using EasyShare.Models;
using EasyShare.Resources;
using EasyShare.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyShare.ViewModels;

public sealed class MainPageViewModel : ObservableObject
{
    private readonly LocalDatabase _database;
    private readonly IAuthenticationService _authentication;
    private readonly IVirtualDriveService _virtualDrive;
    private readonly StartupService _startupService;
    private readonly GraphSharePointService _sharePointService;
    private readonly AppUpdateService _appUpdateService;
    private AuthStatus _authStatus = AuthStatus.SignedOut();
    private VirtualDriveStatus _virtualDriveStatus = new(@"\\EasyShare\", AppText.Get("StatusLoading"), AppText.Get("StatusLoadingDetail"), false);
    private AppSettings _settings = new();
    private bool _isBusy;
    private bool _isCheckingUpdates;
    private bool _isDownloadingUpdate;
    private AppUpdateInfo? _availableUpdate;
    private string? _downloadedUpdatePath;
    private double _updateProgressValue;
    private bool _isUpdateProgressIndeterminate;
    private string _updateProgressText = string.Empty;
    private string _settingsMessage = AppText.Get("SettingsDefaultMessage");
    private AppUpdateStatus _updateStatus = new(
        AppText.Get("UpdateStatusIdleTitle"),
        AppText.Get("UpdateStatusIdleMessage"),
        InfoBarSeverity.Informational);

    public ObservableCollection<DriveRoute> Routes { get; } = [];

    public ObservableCollection<SyncJob> SyncJobs { get; } = [];

    public string ConnectionTitle => _authStatus.Title;

    public string ConnectionMessage => _authStatus.Message;

    public string AccountName => _authStatus.AccountName;

    public InfoBarSeverity ConnectionSeverity => _authStatus.Severity;

    public string VirtualRoot => _virtualDriveStatus.RootPath;

    public string VirtualDriveState => _virtualDriveStatus.State;

    public string VirtualDriveDetail => _virtualDriveStatus.Detail;

    public bool CanOpenInExplorer => _virtualDriveStatus.CanOpenInExplorer;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public int RouteCount => Routes.Count;

    public int ConnectedRouteCount => Routes.Count(route => route.IsConnected);

    public int PendingUploadCount => SyncJobs.Count(job => job.State is SyncJobState.Waiting or SyncJobState.Uploading or SyncJobState.Failed);

    public string DatabasePath => _database.DatabasePath;

    public int AuthenticationModeIndex
    {
        get => _settings.AuthenticationMode == AuthenticationMode.MicrosoftGraph ? 1 : 0;
        set
        {
            var mode = value == 1 ? AuthenticationMode.MicrosoftGraph : AuthenticationMode.BrowserSession;
            if (_settings.AuthenticationMode == mode)
            {
                return;
            }

            _settings.AuthenticationMode = mode;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBrowserSessionMode));
            OnPropertyChanged(nameof(IsGraphAuthMode));
            OnPropertyChanged(nameof(SettingsMessage));
        }
    }

    public bool IsBrowserSessionMode => _settings.AuthenticationMode == AuthenticationMode.BrowserSession;

    public bool IsGraphAuthMode => _settings.AuthenticationMode == AuthenticationMode.MicrosoftGraph;

    public string ClientId
    {
        get => _settings.ClientId;
        set
        {
            if (_settings.ClientId == value)
            {
                return;
            }

            _settings.ClientId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SettingsMessage));
        }
    }

    public string TenantId
    {
        get => _settings.TenantId;
        set
        {
            if (_settings.TenantId == value)
            {
                return;
            }

            _settings.TenantId = value;
            OnPropertyChanged();
        }
    }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value)
            {
                return;
            }

            _settings.StartWithWindows = value;
            OnPropertyChanged();
        }
    }

    public bool StartMinimized
    {
        get => _settings.StartMinimized;
        set
        {
            if (_settings.StartMinimized == value)
            {
                return;
            }

            _settings.StartMinimized = value;
            OnPropertyChanged();
        }
    }

    public bool AutoStartVirtualDrive
    {
        get => _settings.AutoStartVirtualDrive;
        set
        {
            if (_settings.AutoStartVirtualDrive == value)
            {
                return;
            }

            _settings.AutoStartVirtualDrive = value;
            OnPropertyChanged();
        }
    }

    public string MountPoint
    {
        get => _settings.MountPoint;
        set
        {
            if (_settings.MountPoint == value)
            {
                return;
            }

            _settings.MountPoint = value;
            OnPropertyChanged();
        }
    }

    public int CacheMinutes
    {
        get => _settings.CacheMinutes;
        set
        {
            var normalized = Math.Clamp(value, 1, 1440);
            if (_settings.CacheMinutes == normalized)
            {
                return;
            }

            _settings.CacheMinutes = normalized;
            OnPropertyChanged();
        }
    }

    public string BrowserSessionStartUrl
    {
        get => _settings.BrowserSessionStartUrl;
        set
        {
            if (_settings.BrowserSessionStartUrl == value)
            {
                return;
            }

            _settings.BrowserSessionStartUrl = value;
            OnPropertyChanged();
        }
    }

    public bool BrowserKeepSessionAlive
    {
        get => _settings.BrowserKeepSessionAlive;
        set
        {
            if (_settings.BrowserKeepSessionAlive == value)
            {
                return;
            }

            _settings.BrowserKeepSessionAlive = value;
            OnPropertyChanged();
        }
    }

    public int BrowserKeepAliveMinutes
    {
        get => _settings.BrowserKeepAliveMinutes;
        set
        {
            var normalized = Math.Clamp(value, 5, 240);
            if (_settings.BrowserKeepAliveMinutes == normalized)
            {
                return;
            }

            _settings.BrowserKeepAliveMinutes = normalized;
            OnPropertyChanged();
        }
    }

    public string SettingsMessage
    {
        get => _settingsMessage;
        private set => SetProperty(ref _settingsMessage, value);
    }

    public string AppVersion => _appUpdateService.CurrentVersion;

    public string UpdateStatusTitle => _updateStatus.Title;

    public string UpdateStatusMessage => _updateStatus.Message;

    public InfoBarSeverity UpdateStatusSeverity => _updateStatus.Severity;

    public bool IsCheckingUpdates
    {
        get => _isCheckingUpdates;
        private set
        {
            if (SetProperty(ref _isCheckingUpdates, value))
            {
                RefreshUpdateCommandState();
            }
        }
    }

    public bool IsDownloadingUpdate
    {
        get => _isDownloadingUpdate;
        private set
        {
            if (SetProperty(ref _isDownloadingUpdate, value))
            {
                RefreshUpdateCommandState();
            }
        }
    }

    public double UpdateProgressValue
    {
        get => _updateProgressValue;
        private set => SetProperty(ref _updateProgressValue, value);
    }

    public bool IsUpdateProgressIndeterminate
    {
        get => _isUpdateProgressIndeterminate;
        private set => SetProperty(ref _isUpdateProgressIndeterminate, value);
    }

    public string UpdateProgressText
    {
        get => _updateProgressText;
        private set => SetProperty(ref _updateProgressText, value);
    }

    public bool CanCheckForUpdates => !IsCheckingUpdates && !IsDownloadingUpdate;

    public bool CanDownloadUpdate =>
        _availableUpdate is not null &&
        _downloadedUpdatePath is null &&
        !IsCheckingUpdates &&
        !IsDownloadingUpdate;

    public bool CanInstallUpdate =>
        !string.IsNullOrWhiteSpace(_downloadedUpdatePath) &&
        !IsCheckingUpdates &&
        !IsDownloadingUpdate;

    public Visibility UpdateProgressVisibility =>
        IsDownloadingUpdate || !string.IsNullOrWhiteSpace(UpdateProgressText)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility DownloadUpdateButtonVisibility =>
        _availableUpdate is not null && _downloadedUpdatePath is null
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility InstallUpdateButtonVisibility =>
        string.IsNullOrWhiteSpace(_downloadedUpdatePath)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public Visibility EmptyRoutesVisibility => Routes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RoutesListVisibility => Routes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility EmptySyncVisibility => SyncJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SyncListVisibility => SyncJobs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public bool ShouldShowSetupWizard => !_settings.SetupWizardCompleted && Routes.Count == 0;

    public MainPageViewModel(
        LocalDatabase database,
        IAuthenticationService authentication,
        IVirtualDriveService virtualDrive,
        StartupService startupService,
        GraphSharePointService sharePointService,
        AppUpdateService appUpdateService)
    {
        _database = database;
        _authentication = authentication;
        _virtualDrive = virtualDrive;
        _startupService = startupService;
        _sharePointService = sharePointService;
        _appUpdateService = appUpdateService;
    }

    public async Task LoadAsync()
    {
        await RunBusyAsync(async () =>
        {
            await _database.InitializeAsync();
            _settings = await _database.GetSettingsAsync();
            var requestedStartup = _settings.StartWithWindows;
            var actualStartup = await _startupService.IsEnabledAsync();
            if (requestedStartup && !actualStartup)
            {
                actualStartup = await _startupService.SetEnabledAsync(true, _settings.StartMinimized);
            }

            _settings.StartWithWindows = actualStartup;

            _authStatus = IsBrowserSessionMode
                ? BrowserSessionStatus(AppText.Get("BrowserStatusNeedSession"), signedIn: false)
                : await _authentication.GetStatusAsync();

            Routes.Clear();
            foreach (var route in await _database.GetRoutesAsync())
            {
                Routes.Add(route);
            }

            SyncJobs.Clear();
            foreach (var job in await _database.GetSyncJobsAsync())
            {
                SyncJobs.Add(job);
            }

            _virtualDriveStatus = await _virtualDrive.GetStatusAsync(_settings, Routes);
            SettingsMessage = BuildSettingsMessage();

            RefreshState();
        });
    }

    public async Task SignInAsync(IntPtr windowHandle)
    {
        await RunBusyAsync(async () =>
        {
            if (IsBrowserSessionMode)
            {
                _authStatus = BrowserSessionStatus(AppText.Get("BrowserStatusOpenSession"), signedIn: false);
                SettingsMessage = AppText.Get("SettingsBrowserModeMessage");
                RefreshState();
                return;
            }

            _authStatus = await _authentication.SignInAsync(windowHandle);
            SettingsMessage = _authStatus.State == AuthState.SignedIn
                ? AppText.Get("SettingsLoginActive")
                : _authStatus.Message;
            RefreshState();
        });
    }

    public async Task SignOutAsync()
    {
        await RunBusyAsync(async () =>
        {
            if (IsBrowserSessionMode)
            {
                _authStatus = BrowserSessionStatus(AppText.Get("BrowserStatusSignedOut"), signedIn: false);
                SettingsMessage = AppText.Get("SettingsLoginRemoved");
                RefreshState();
                return;
            }

            await _authentication.SignOutAsync();
            _authStatus = await _authentication.GetStatusAsync();
            SettingsMessage = AppText.Get("SettingsAccountRemoved");
            RefreshState();
        });
    }

    public async Task SaveSettingsAsync()
    {
        await RunBusyAsync(async () =>
        {
            var requestedStartWithWindows = StartWithWindows;
            _settings.StartWithWindows = await _startupService.SetEnabledAsync(requestedStartWithWindows, _settings.StartMinimized);
            await _database.SaveSettingsAsync(_settings);
            _authStatus = IsBrowserSessionMode
                ? BrowserSessionStatus(AppText.Get("BrowserStatusNeedSession"), signedIn: false)
                : await _authentication.GetStatusAsync();
            _virtualDriveStatus = await _virtualDrive.GetStatusAsync(_settings, Routes);
            SettingsMessage = requestedStartWithWindows && !_settings.StartWithWindows
                ? AppText.Get("StartupEnableBlocked")
                : BuildSettingsMessage();
            RefreshState();
        });
    }

    public async Task SetAuthenticationModeAsync(AuthenticationMode mode)
    {
        _settings.AuthenticationMode = mode;
        await SaveSettingsAsync();
    }

    public async Task SetBrowserSessionStartUrlAsync(string value)
    {
        _settings.BrowserSessionStartUrl = SharePointRouteParser.NormalizeNavigationUri(value).ToString();
        await SaveSettingsAsync();
    }

    public async Task CompleteSetupWizardAsync()
    {
        _settings.SetupWizardCompleted = true;
        await SaveSettingsAsync();
    }

    public async Task<AppUpdateStatus?> CheckForUpdatesAsync()
    {
        if (IsCheckingUpdates || IsDownloadingUpdate)
        {
            return null;
        }

        try
        {
            IsCheckingUpdates = true;
            _availableUpdate = null;
            _downloadedUpdatePath = null;
            UpdateProgressText = string.Empty;
            UpdateProgressValue = 0;
            IsUpdateProgressIndeterminate = false;
            _updateStatus = new AppUpdateStatus(
                AppText.Get("UpdateStatusCheckingTitle"),
                AppText.Get("UpdateStatusCheckingMessage"),
                InfoBarSeverity.Informational);
            RefreshUpdateState();

            _updateStatus = await _appUpdateService.CheckForUpdatesAsync();
            _availableUpdate = _updateStatus.Update;
            if (_availableUpdate is not null)
            {
                UpdateProgressText = AppText.Format(
                    "UpdateProgressReadyFormat",
                    _availableUpdate.AssetName,
                    FormatBytes(_availableUpdate.AssetSizeBytes));
            }

            RefreshUpdateState();
            return _updateStatus;
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    public async Task DownloadUpdateAsync()
    {
        if (!CanDownloadUpdate || _availableUpdate is null)
        {
            return;
        }

        try
        {
            IsDownloadingUpdate = true;
            _downloadedUpdatePath = null;
            _updateStatus = new AppUpdateStatus(
                AppText.Get("UpdateStatusDownloadTitle"),
                AppText.Format("UpdateStatusDownloadMessage", _availableUpdate.VersionText),
                InfoBarSeverity.Informational,
                _availableUpdate);
            RefreshUpdateState();

            var progress = new Progress<AppUpdateProgress>(ReportDownloadProgress);
            _downloadedUpdatePath = await _appUpdateService.DownloadUpdateAsync(_availableUpdate, progress);
            UpdateProgressValue = 100;
            IsUpdateProgressIndeterminate = false;
            UpdateProgressText = AppText.Format("UpdateProgressDownloadedFormat", Path.GetFileName(_downloadedUpdatePath));
            _updateStatus = new AppUpdateStatus(
                AppText.Get("UpdateStatusDownloadedTitle"),
                AppText.Get("UpdateStatusDownloadedMessage"),
                InfoBarSeverity.Warning,
                _availableUpdate);
            RefreshUpdateState();
        }
        catch
        {
            _downloadedUpdatePath = null;
            _updateStatus = new AppUpdateStatus(
                AppText.Get("UpdateStatusDownloadErrorTitle"),
                AppText.Get("UpdateStatusDownloadErrorMessage"),
                InfoBarSeverity.Warning,
                _availableUpdate);
            RefreshUpdateState();
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    public void InstallDownloadedUpdate()
    {
        if (!CanInstallUpdate || string.IsNullOrWhiteSpace(_downloadedUpdatePath))
        {
            return;
        }

        var installerStarted = _appUpdateService.TryStartInstaller(_downloadedUpdatePath);
        _updateStatus = installerStarted
            ? new AppUpdateStatus(
                AppText.Get("UpdateStatusInstallStartedTitle"),
                AppText.Get("UpdateStatusInstallStartedMessage"),
                InfoBarSeverity.Warning,
                _availableUpdate)
            : new AppUpdateStatus(
                AppText.Get("UpdateStatusInstallBlockedTitle"),
                AppText.Get("UpdateStatusInstallBlockedMessage"),
                InfoBarSeverity.Warning,
                _availableUpdate);
        RefreshUpdateState();
    }

    public void OpenUpdateReleasePage()
    {
        if (_availableUpdate is not null)
        {
            _appUpdateService.OpenReleasePage(_availableUpdate);
        }
    }

    public async Task ResetAppAsync()
    {
        await RunBusyAsync(async () =>
        {
            await _authentication.SignOutAsync();
            await _startupService.SetEnabledAsync(false, startMinimized: false);
            await _database.ResetAsync();
            _settings = new AppSettings();

            Routes.Clear();
            SyncJobs.Clear();
            _authStatus = BrowserSessionStatus(AppText.Get("BrowserStatusNeedSession"), signedIn: false);
            _virtualDriveStatus = await _virtualDrive.GetStatusAsync(_settings, Routes);
            SettingsMessage = BuildSettingsMessage();
            RefreshState();
        });
    }

    public void OpenVirtualDrive()
    {
        if (!CanOpenInExplorer || string.IsNullOrWhiteSpace(VirtualRoot))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = VirtualRoot,
            UseShellExecute = true
        });
    }

    public async Task AddRouteAsync(string displayName, string sharePointUrl, string remotePath)
    {
        var route = new DriveRoute
        {
            DisplayName = displayName.Trim(),
            SharePointUrl = sharePointUrl.Trim().TrimEnd('/'),
            RemotePath = SharePointRouteParser.NormalizeRemotePath(remotePath),
            StatusText = AppText.Get("DriveRouteUntested")
        };

        await _database.AddRouteAsync(route);
        Routes.Add(route);
        _virtualDriveStatus = await _virtualDrive.GetStatusAsync(_settings, Routes);
        RefreshState();
    }

    public async Task UpdateRouteAsync(Guid routeId, string displayName, string sharePointUrl, string remotePath)
    {
        var route = Routes.FirstOrDefault(item => item.Id == routeId);
        if (route is null)
        {
            return;
        }

        route.DisplayName = displayName.Trim();
        route.SharePointUrl = sharePointUrl.Trim().TrimEnd('/');
        route.RemotePath = SharePointRouteParser.NormalizeRemotePath(remotePath);
        route.IsConnected = false;
        route.StatusText = AppText.Get("DriveRouteUntested");
        route.LastCheckedAt = null;

        await _database.UpdateRouteAsync(route);
        var index = Routes.IndexOf(route);
        Routes.RemoveAt(index);
        Routes.Insert(index, route);
        _virtualDriveStatus = await _virtualDrive.GetStatusAsync(_settings, Routes);
        RefreshState();
    }

    public async Task RemoveRouteAsync(Guid routeId)
    {
        var route = Routes.FirstOrDefault(item => item.Id == routeId);
        if (route is null)
        {
            return;
        }

        await _database.RemoveRouteAsync(routeId);
        Routes.Remove(route);
        _virtualDriveStatus = await _virtualDrive.GetStatusAsync(_settings, Routes);
        RefreshState();
    }

    public async Task TestRouteAsync(Guid routeId, Func<DriveRoute, Task<RouteTestResult>>? browserSessionTester = null)
    {
        var route = Routes.FirstOrDefault(item => item.Id == routeId);
        if (route is null)
        {
            return;
        }

        var result = IsBrowserSessionMode && browserSessionTester is not null
            ? await browserSessionTester(route)
            : await _sharePointService.TestRouteAsync(route);

        await ApplyRouteTestResultAsync(route, result);
    }

    public async Task ApplyRouteTestResultAsync(DriveRoute route, RouteTestResult result)
    {
        route.IsConnected = result.Success;
        route.LastCheckedAt = DateTimeOffset.UtcNow;
        route.StatusText = result.Message;
        await _database.UpdateRouteAsync(route);

        var index = Routes.IndexOf(route);
        Routes.RemoveAt(index);
        Routes.Insert(index, route);
        _virtualDriveStatus = await _virtualDrive.GetStatusAsync(_settings, Routes);
        RefreshState();
    }

    public void UpdateBrowserSessionStatus(RouteTestResult result)
    {
        if (!IsBrowserSessionMode)
        {
            return;
        }

        _authStatus = BrowserSessionStatus(result.Message, result.Success);
        SettingsMessage = result.Message;
        RefreshState();
    }

    public void ReportStartupError(string message, Exception exception)
    {
        _authStatus = new AuthStatus(
            AuthState.Error,
            AppText.Get("StartupErrorTitle"),
            $"{message} {exception.Message}",
            AppText.Get("StartupErrorAccount"),
            null);
        SettingsMessage = AppText.Format("StartupLogFormat", StartupDiagnostics.LogPath);
        RefreshState();
    }

    public Uri GetBrowserSessionStartUri()
    {
        if (Uri.TryCreate(_settings.BrowserSessionStartUrl, UriKind.Absolute, out var configured))
        {
            return configured;
        }

        var firstRoute = Routes.FirstOrDefault(route => Uri.TryCreate(route.SharePointUrl, UriKind.Absolute, out _));
        return firstRoute is not null && Uri.TryCreate(firstRoute.SharePointUrl, UriKind.Absolute, out var routeUri)
            ? routeUri
            : new Uri("https://www.office.com/");
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await action();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReportDownloadProgress(AppUpdateProgress progress)
    {
        UpdateProgressValue = progress.Percentage;
        IsUpdateProgressIndeterminate = progress.IsIndeterminate;
        UpdateProgressText = progress.IsIndeterminate
            ? AppText.Format("UpdateProgressIndeterminateFormat", FormatBytes(progress.BytesReceived))
            : AppText.Format(
                "UpdateProgressFormat",
                progress.Percentage,
                FormatBytes(progress.BytesReceived),
                FormatBytes(progress.TotalBytes.GetValueOrDefault()));
        OnPropertyChanged(nameof(UpdateProgressVisibility));
    }

    private void RefreshUpdateCommandState()
    {
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(CanDownloadUpdate));
        OnPropertyChanged(nameof(CanInstallUpdate));
        OnPropertyChanged(nameof(UpdateProgressVisibility));
        OnPropertyChanged(nameof(DownloadUpdateButtonVisibility));
        OnPropertyChanged(nameof(InstallUpdateButtonVisibility));
    }

    private void RefreshState()
    {
        OnPropertyChanged(nameof(ConnectionTitle));
        OnPropertyChanged(nameof(ConnectionMessage));
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(ConnectionSeverity));
        OnPropertyChanged(nameof(VirtualRoot));
        OnPropertyChanged(nameof(VirtualDriveState));
        OnPropertyChanged(nameof(VirtualDriveDetail));
        OnPropertyChanged(nameof(CanOpenInExplorer));
        OnPropertyChanged(nameof(RouteCount));
        OnPropertyChanged(nameof(ConnectedRouteCount));
        OnPropertyChanged(nameof(PendingUploadCount));
        OnPropertyChanged(nameof(DatabasePath));
        OnPropertyChanged(nameof(AuthenticationModeIndex));
        OnPropertyChanged(nameof(IsBrowserSessionMode));
        OnPropertyChanged(nameof(IsGraphAuthMode));
        OnPropertyChanged(nameof(ClientId));
        OnPropertyChanged(nameof(TenantId));
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(StartMinimized));
        OnPropertyChanged(nameof(AutoStartVirtualDrive));
        OnPropertyChanged(nameof(MountPoint));
        OnPropertyChanged(nameof(CacheMinutes));
        OnPropertyChanged(nameof(BrowserSessionStartUrl));
        OnPropertyChanged(nameof(BrowserKeepSessionAlive));
        OnPropertyChanged(nameof(BrowserKeepAliveMinutes));
        OnPropertyChanged(nameof(SettingsMessage));
        OnPropertyChanged(nameof(AppVersion));
        RefreshUpdateState();
        OnPropertyChanged(nameof(EmptyRoutesVisibility));
        OnPropertyChanged(nameof(RoutesListVisibility));
        OnPropertyChanged(nameof(EmptySyncVisibility));
        OnPropertyChanged(nameof(SyncListVisibility));
        OnPropertyChanged(nameof(ShouldShowSetupWizard));
    }

    private void RefreshUpdateState()
    {
        OnPropertyChanged(nameof(UpdateStatusTitle));
        OnPropertyChanged(nameof(UpdateStatusMessage));
        OnPropertyChanged(nameof(UpdateStatusSeverity));
        OnPropertyChanged(nameof(IsCheckingUpdates));
        OnPropertyChanged(nameof(IsDownloadingUpdate));
        OnPropertyChanged(nameof(UpdateProgressValue));
        OnPropertyChanged(nameof(IsUpdateProgressIndeterminate));
        OnPropertyChanged(nameof(UpdateProgressText));
        RefreshUpdateCommandState();
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        var size = Math.Max(0, bytes);
        var suffixIndex = 0;
        double formatted = size;

        while (formatted >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            formatted /= 1024;
            suffixIndex++;
        }

        return suffixIndex == 0
            ? $"{formatted:0} {suffixes[suffixIndex]}"
            : $"{formatted:0.0} {suffixes[suffixIndex]}";
    }

    private string BuildSettingsMessage()
    {
        if (IsBrowserSessionMode)
        {
            return AppText.Get("SettingsBrowserModeMessage");
        }

        return _settings.HasClientId
            ? AppText.Get("SettingsGraphReady")
            : AppText.Get("SettingsMissingClientId");
    }

    private static AuthStatus BrowserSessionStatus(string message, bool signedIn) =>
        new(
            signedIn ? AuthState.SignedIn : AuthState.SignedOut,
            signedIn ? AppText.Get("AuthBrowserSignedInTitle") : AppText.Get("AuthBrowserSignedOutTitle"),
            message,
            signedIn ? AppText.Get("AuthBrowserAccount") : AppText.Get("AccountNone"),
            null);
}
