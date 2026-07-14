using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using EasyShare.Models;
using EasyShare.Resources;
using EasyShare.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Services.Store;
using Windows.UI;

namespace EasyShare.ViewModels;

public sealed class MainPageViewModel : ObservableObject
{
    private readonly LocalDatabase _database;
    private readonly IAuthenticationService _authentication;
    private readonly IVirtualDriveService _virtualDrive;
    private readonly StartupService _startupService;
    private readonly GraphSharePointService _sharePointService;
    private readonly AppUpdateService _appUpdateService;
    private readonly EnterprisePolicySnapshot _enterprisePolicy;
    private readonly SemaphoreSlim _routeMutationGate = new(1, 1);
    private readonly SemaphoreSlim _settingsMutationGate = new(1, 1);
    private AuthStatus _authStatus = AuthStatus.SignedOut();
    private VirtualDriveStatus _virtualDriveStatus = new(@"\\EasyShare\", AppText.Get("StatusLoading"), AppText.Get("StatusLoadingDetail"), false);
    private AppSettings _settings = new();
    private AppSettings _persistedSettings = new();
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

    public ObservableCollection<string> AuthenticationModeOptions { get; } = [];

    public ObservableCollection<string> LanguageOptions { get; } = [];

    public ObservableCollection<string> ThemeModeOptions { get; } = [];

    public string ConnectionTitle => _authStatus.Title;

    public string ConnectionMessage => _authStatus.Message;

    public string AccountName => _authStatus.AccountName;

    public InfoBarSeverity ConnectionSeverity => _authStatus.Severity;

    public string VirtualRoot => _virtualDriveStatus.RootPath;

    public string VirtualDriveState => _virtualDriveStatus.State;

    public string VirtualDriveDetail => _virtualDriveStatus.Detail;

    public bool CanOpenInExplorer => _virtualDriveStatus.CanOpenInExplorer;

    public VirtualDriveStatus CurrentVirtualDriveStatus => _virtualDriveStatus;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public int RouteCount => Routes.Count;

    public int ConnectedRouteCount => Routes.Count(route => route.IsConnected);

    public int PendingUploadCount => SyncJobs.Count(job => job.State is SyncJobState.Waiting or SyncJobState.Uploading or SyncJobState.Failed or SyncJobState.Conflict);

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

    public int ThemeModeIndex
    {
        get => (int)_settings.ThemeMode;
        set
        {
            var mode = Enum.IsDefined(typeof(AppThemeMode), value)
                ? (AppThemeMode)value
                : AppThemeMode.System;
            if (_settings.ThemeMode == mode)
            {
                return;
            }

            _settings.ThemeMode = mode;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThemeMode));
        }
    }

    public AppThemeMode ThemeMode => _settings.ThemeMode;

    public int LanguageIndex
    {
        get => string.Equals(
            _settings.LanguageCode,
            AppText.EnglishLanguageCode,
            StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
        set
        {
            var languageCode = value == 1
                ? AppText.EnglishLanguageCode
                : AppText.PortugueseLanguageCode;
            if (string.Equals(_settings.LanguageCode, languageCode, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.LanguageCode = languageCode;
            AppText.SetLanguage(languageCode);
            RefreshLocalizedOptions();
            SettingsMessage = BuildSettingsMessage();

            if (_availableUpdate is null && !IsCheckingUpdates && !IsDownloadingUpdate)
            {
                _updateStatus = CreateIdleUpdateStatus();
            }

            OnPropertyChanged();
            RefreshState();
        }
    }

    public Color AccentColorValue
    {
        get => ParseAccentColor(_settings.AccentColor);
        set
        {
            var normalized = ToAccentColorString(value);
            if (string.Equals(_settings.AccentColor, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.AccentColor = normalized;
            OnPropertyChanged();
        }
    }

    public bool HighContrastEnabled
    {
        get => _settings.HighContrastEnabled;
        set
        {
            if (_settings.HighContrastEnabled == value)
            {
                return;
            }

            _settings.HighContrastEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool NotificationsEnabled
    {
        get => _settings.NotificationsEnabled;
        set => SetBooleanSetting(_settings.NotificationsEnabled, value, setting => _settings.NotificationsEnabled = setting);
    }

    public bool NotifyUploadCompleted
    {
        get => _settings.NotifyUploadCompleted;
        set => SetBooleanSetting(_settings.NotifyUploadCompleted, value, setting => _settings.NotifyUploadCompleted = setting);
    }

    public bool NotifyUploadFailed
    {
        get => _settings.NotifyUploadFailed;
        set => SetBooleanSetting(_settings.NotifyUploadFailed, value, setting => _settings.NotifyUploadFailed = setting);
    }

    public bool NotifyConflict
    {
        get => _settings.NotifyConflict;
        set => SetBooleanSetting(_settings.NotifyConflict, value, setting => _settings.NotifyConflict = setting);
    }

    public bool NotifySessionExpired
    {
        get => _settings.NotifySessionExpired;
        set => SetBooleanSetting(_settings.NotifySessionExpired, value, setting => _settings.NotifySessionExpired = setting);
    }

    public bool NotifyDriveDisconnected
    {
        get => _settings.NotifyDriveDisconnected;
        set => SetBooleanSetting(_settings.NotifyDriveDisconnected, value, setting => _settings.NotifyDriveDisconnected = setting);
    }

    public bool NotifyUpdateReady
    {
        get => _settings.NotifyUpdateReady;
        set => SetBooleanSetting(_settings.NotifyUpdateReady, value, setting => _settings.NotifyUpdateReady = setting);
    }

    public bool QuietModeEnabled
    {
        get => _settings.QuietModeEnabled;
        set => SetBooleanSetting(_settings.QuietModeEnabled, value, setting => _settings.QuietModeEnabled = setting);
    }

    public int OfflineCacheLimitMb
    {
        get => _settings.OfflineCacheLimitMb;
        set
        {
            var normalized = Math.Clamp(value, 128, 102400);
            if (_settings.OfflineCacheLimitMb == normalized)
            {
                return;
            }

            _settings.OfflineCacheLimitMb = normalized;
            OnPropertyChanged();
        }
    }

    public bool OfflinePauseOnMeteredNetwork
    {
        get => _settings.OfflinePauseOnMeteredNetwork;
        set => SetBooleanSetting(_settings.OfflinePauseOnMeteredNetwork, value, setting => _settings.OfflinePauseOnMeteredNetwork = setting);
    }

    public bool OfflinePauseOnBattery
    {
        get => _settings.OfflinePauseOnBattery;
        set => SetBooleanSetting(_settings.OfflinePauseOnBattery, value, setting => _settings.OfflinePauseOnBattery = setting);
    }

    public bool IsNotificationDeliveryEnabled => NotificationsEnabled && !QuietModeEnabled;

    public AppSettings CurrentSettings => _settings;

    public EnterprisePolicySnapshot EnterprisePolicy => _enterprisePolicy;

    public bool IsEnterpriseManaged => _enterprisePolicy.IsManaged;

    public Visibility EnterpriseManagedVisibility => IsEnterpriseManaged
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool CanEditAccessSettings =>
        !_enterprisePolicy.IsFieldManaged("browserSessionAllowed") &&
        !_enterprisePolicy.IsFieldManaged("interactiveSignInAllowed") &&
        !_enterprisePolicy.IsFieldManaged("tenantId") &&
        !_enterprisePolicy.IsFieldManaged("clientId") &&
        !_enterprisePolicy.IsFieldManaged("allowedTenantIds");

    public bool CanEditGraphSettings => IsGraphAuthMode && CanEditAccessSettings;

    public bool CanEditDriveSettings =>
        !_enterprisePolicy.IsFieldManaged("mountPoint") &&
        !_enterprisePolicy.IsFieldManaged("startWithWindows") &&
        !_enterprisePolicy.IsFieldManaged("autoStartVirtualDrive");

    public bool CanEditCacheSettings =>
        !_enterprisePolicy.IsFieldManaged("cacheMinutes") &&
        !_enterprisePolicy.IsFieldManaged("offlineCacheLimitMb");

    public bool IsInteractiveSignInAllowed => _enterprisePolicy.Policy.InteractiveSignInAllowed;

    public bool IsRouteAllowed(string siteUrl)
    {
        var allowedHosts = _enterprisePolicy.Policy.AllowedSharePointHosts;
        if (allowedHosts.Count == 0 || !Uri.TryCreate(siteUrl, UriKind.Absolute, out var uri))
        {
            return allowedHosts.Count == 0;
        }

        return allowedHosts.Any(pattern =>
            pattern.StartsWith("*.", StringComparison.Ordinal)
                ? uri.DnsSafeHost.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) &&
                  !string.Equals(uri.DnsSafeHost, pattern[2..], StringComparison.OrdinalIgnoreCase)
                : string.Equals(uri.DnsSafeHost, pattern, StringComparison.OrdinalIgnoreCase));
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

    public string UpdateChannelText => _appUpdateService.UpdateChannel == AppUpdateChannel.MicrosoftStore
        ? AppText.Get("UpdateChannelMicrosoftStore")
        : AppText.Get("UpdateChannelGitHub");

    public string UpdatePrimaryActionLabel => _availableUpdate?.Channel == AppUpdateChannel.MicrosoftStore
        ? AppText.Get("ActionInstallStoreUpdate")
        : AppText.Get("ActionDownloadUpdate");

    public string UpdateChangelog => _availableUpdate?.Changelog ?? string.Empty;

    public string UpdateChangelogVersion => _availableUpdate is null
        ? string.Empty
        : AppText.Format("UpdateChangelogVersionFormat", _availableUpdate.VersionText);

    public Visibility UpdateChangelogVisibility =>
        _availableUpdate is null ||
        _availableUpdate.Channel != AppUpdateChannel.GitHubReleases ||
        string.IsNullOrWhiteSpace(_availableUpdate.Changelog)
        ? Visibility.Collapsed
        : Visibility.Visible;

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

    public bool CanOpenUpdateRelease =>
        _availableUpdate?.Channel == AppUpdateChannel.GitHubReleases &&
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

    public Visibility OpenUpdateReleaseButtonVisibility =>
        _availableUpdate?.Channel == AppUpdateChannel.GitHubReleases &&
        _downloadedUpdatePath is null
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility EmptyRoutesVisibility => Routes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RoutesListVisibility => Routes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility EmptySyncVisibility => SyncJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SyncListVisibility => SyncJobs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public bool ShouldShowSetupWizard =>
        !_settings.SetupWizardCompleted ||
        _settings.SetupWizardCompletedVersion < SetupWizardAdvisor.CurrentVersion;

    public MainPageViewModel(
        LocalDatabase database,
        IAuthenticationService authentication,
        IVirtualDriveService virtualDrive,
        StartupService startupService,
        GraphSharePointService sharePointService,
        AppUpdateService appUpdateService,
        EnterprisePolicySnapshot? enterprisePolicy = null)
    {
        _database = database;
        _authentication = authentication;
        _virtualDrive = virtualDrive;
        _startupService = startupService;
        _sharePointService = sharePointService;
        _appUpdateService = appUpdateService;
        _enterprisePolicy = enterprisePolicy ?? new EnterprisePolicySnapshot(
            new EnterprisePolicy(),
            false,
            [EnterprisePolicySource.Defaults],
            []);
        RefreshLocalizedOptions();
        _updateStatus = CreateIdleUpdateStatus();
    }

    public async Task LoadAsync()
    {
        await RunBusyAsync(async () =>
        {
            await _database.InitializeAsync();
            _settings = await _database.GetSettingsAsync();
            ApplyEnterprisePolicy();
            _settings.LanguageCode = AppText.NormalizeLanguageCode(_settings.LanguageCode);
            AppText.SetLanguage(_settings.LanguageCode);
            AppText.SaveStartupLanguageCode(_settings.LanguageCode);
            RefreshLocalizedOptions();
            _updateStatus = CreateIdleUpdateStatus();
            var requestedStartup = _settings.StartWithWindows;
            var actualStartup = await _startupService.IsEnabledAsync();
            if (requestedStartup != actualStartup)
            {
                actualStartup = await _startupService.SetEnabledAsync(requestedStartup, _settings.StartMinimized);
            }

            // The persisted preference is authoritative. If Windows refuses a change,
            // do not silently turn a disabled preference back on during startup.
            _settings.StartWithWindows = requestedStartup && actualStartup;

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
            _persistedSettings = SetupWizardAdvisor.CloneSettings(_settings);
            SettingsMessage = BuildSettingsMessage();

            RefreshState();
        });
    }

    public void ApplySyncJob(SyncJob job)
    {
        var index = SyncJobs.IndexOf(SyncJobs.FirstOrDefault(item => item.Id == job.Id)!);
        if (index >= 0)
        {
            SyncJobs[index] = job;
        }
        else
        {
            SyncJobs.Insert(0, job);
        }

        OnPropertyChanged(nameof(PendingUploadCount));
        OnPropertyChanged(nameof(EmptySyncVisibility));
        OnPropertyChanged(nameof(SyncListVisibility));
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
                await _authentication.SignOutAsync();
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
        await _settingsMutationGate.WaitAsync();
        try
        {
            await RunBusyAsync(async () =>
            {
                ApplyEnterprisePolicy();
                var requestedStartWithWindows = StartWithWindows;
                _settings.StartWithWindows = await _startupService.SetEnabledAsync(requestedStartWithWindows, _settings.StartMinimized);
                await _database.SaveSettingsAsync(_settings);
                _persistedSettings = SetupWizardAdvisor.CloneSettings(_settings);
                AppText.SaveStartupLanguageCode(_settings.LanguageCode);
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
        finally
        {
            _settingsMutationGate.Release();
        }
    }

    public SetupWizardDraft CreateSetupWizardDraft()
    {
        var occupiedMountPoints = GetSetupWizardOccupiedMountPoints();
        return SetupWizardAdvisor.CreateDraft(_persistedSettings, _enterprisePolicy, occupiedMountPoints);
    }

    public SetupWizardCapabilities CreateSetupWizardCapabilities() =>
        SetupWizardAdvisor.CreateCapabilities(
            _enterprisePolicy,
            VirtualDriveService.IsWinFspAvailable());

    public IReadOnlyList<string> GetSetupWizardMountPointOptions() =>
        SetupWizardAdvisor.GetMountPointOptions(GetSetupWizardOccupiedMountPoints());

    public async Task<SetupWizardApplyResult> ApplySetupWizardAsync(SetupWizardDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await _settingsMutationGate.WaitAsync();
        var ownsBusyState = false;
        try
        {
            if (IsBusy)
            {
                throw new InvalidOperationException(AppText.Get("WizardBusyMessage"));
            }

            var mountPointOptions = GetSetupWizardMountPointOptions();
            var validationIssues = SetupWizardAdvisor.ValidateAll(
                draft,
                _enterprisePolicy,
                mountPointOptions);
            if (validationIssues.Count > 0)
            {
                throw new InvalidOperationException(AppText.Get(validationIssues[0].MessageKey));
            }

            IsBusy = true;
            ownsBusyState = true;
            var candidate = SetupWizardAdvisor.BuildSettings(_persistedSettings, draft, _enterprisePolicy);

            // Persist the complete, policy-normalized candidate before replacing the
            // in-memory settings. A failed write therefore leaves the active session intact.
            await _database.SaveSettingsAsync(candidate);
            _settings = candidate;
            _persistedSettings = SetupWizardAdvisor.CloneSettings(candidate);

            var startupRequested = _settings.StartWithWindows;
            var startupEnabled = false;
            string? warningMessage = null;

            try
            {
                AppText.SetLanguage(_settings.LanguageCode);
                AppText.SaveStartupLanguageCode(_settings.LanguageCode);
                RefreshLocalizedOptions();
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Write("Setup wizard language reconciliation failed.", ex);
                warningMessage = AppText.Get("WizardReconcileWarning");
            }

            try
            {
                startupEnabled = await _startupService.SetEnabledAsync(
                    startupRequested,
                    _settings.StartMinimized);
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Write("Setup wizard startup reconciliation failed.", ex);
                warningMessage ??= AppText.Get("WizardReconcileWarning");
                try
                {
                    startupEnabled = await _startupService.IsEnabledAsync();
                }
                catch (Exception statusException)
                {
                    StartupDiagnostics.Write("Could not read Windows startup status.", statusException);
                }
            }

            if (startupRequested != startupEnabled)
            {
                _settings.StartWithWindows = startupEnabled;
                _settings.StartMinimized = startupEnabled && _settings.StartMinimized;
                warningMessage ??= startupRequested
                    ? AppText.Get("StartupEnableBlocked")
                    : AppText.Get("WizardReconcileWarning");
                try
                {
                    await _database.SaveSettingsAsync(_settings);
                    _persistedSettings = SetupWizardAdvisor.CloneSettings(_settings);
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.Write("Could not persist reconciled startup status.", ex);
                    warningMessage = AppText.Get("WizardReconcileWarning");
                }
            }

            try
            {
                _authStatus = IsBrowserSessionMode
                    ? BrowserSessionStatus(AppText.Get("BrowserStatusNeedSession"), signedIn: false)
                    : await _authentication.GetStatusAsync();
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Write("Setup wizard authentication status refresh failed.", ex);
                _authStatus = AuthStatus.SignedOut();
                warningMessage ??= AppText.Get("WizardReconcileWarning");
            }

            try
            {
                _virtualDriveStatus = await _virtualDrive.GetStatusAsync(_settings, Routes);
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Write("Setup wizard virtual drive refresh failed.", ex);
                warningMessage ??= AppText.Get("WizardReconcileWarning");
            }

            try
            {
                SettingsMessage = startupRequested && !startupEnabled
                    ? AppText.Get("StartupEnableBlocked")
                    : BuildSettingsMessage();
                RefreshState();
            }
            catch (Exception ex)
            {
                // A UI subscriber must never make a committed setup look like a failed write.
                StartupDiagnostics.Write("Setup wizard UI refresh failed after commit.", ex);
                warningMessage ??= AppText.Get("WizardReconcileWarning");
            }

            return new SetupWizardApplyResult(startupRequested, startupEnabled, warningMessage);
        }
        finally
        {
            if (ownsBusyState)
            {
                try
                {
                    IsBusy = false;
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.Write("Could not publish setup wizard busy state.", ex);
                }
            }

            _settingsMutationGate.Release();
        }
    }

    public async Task RefreshVirtualDriveAsync()
    {
        _virtualDriveStatus = await _virtualDrive.GetStatusAsync(_settings, Routes);
        RefreshState();
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
                _appUpdateService.UpdateChannel == AppUpdateChannel.MicrosoftStore
                    ? AppText.Get("UpdateStatusStoreCheckingMessage")
                    : AppText.Get("UpdateStatusCheckingMessage"),
                InfoBarSeverity.Informational);
            RefreshUpdateState();

            _updateStatus = await _appUpdateService.CheckForUpdatesAsync();
            _availableUpdate = _updateStatus.Update;
            if (_availableUpdate?.Channel == AppUpdateChannel.GitHubReleases)
            {
                UseDownloadedUpdateIfAvailable(_availableUpdate);
            }

            RefreshUpdateState();
            return _updateStatus;
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    public async Task DownloadUpdateAsync(IntPtr ownerWindow = default)
    {
        if (!CanDownloadUpdate || _availableUpdate is null)
        {
            return;
        }

        try
        {
            IsDownloadingUpdate = true;
            _downloadedUpdatePath = null;
            if (_availableUpdate.Channel == AppUpdateChannel.MicrosoftStore)
            {
                await InstallMicrosoftStoreUpdateAsync(ownerWindow);
                return;
            }

            if (UseDownloadedUpdateIfAvailable(_availableUpdate))
            {
                RefreshUpdateState();
                return;
            }

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
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Update download or installation failed.", ex);
            _downloadedUpdatePath = null;
            var isStoreUpdate = _availableUpdate?.Channel == AppUpdateChannel.MicrosoftStore;
            if (isStoreUpdate)
            {
                UpdateProgressText = string.Empty;
                UpdateProgressValue = 0;
                IsUpdateProgressIndeterminate = false;
            }

            _updateStatus = new AppUpdateStatus(
                AppText.Get("UpdateStatusDownloadErrorTitle"),
                isStoreUpdate
                    ? AppText.Get("UpdateStatusStoreInstallErrorMessage")
                    : AppText.Get("UpdateStatusDownloadErrorMessage"),
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
        if (_availableUpdate?.Channel == AppUpdateChannel.GitHubReleases)
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
            ApplyEnterprisePolicy();
            _persistedSettings = SetupWizardAdvisor.CloneSettings(_settings);
            AppText.SetLanguage(_settings.LanguageCode);
            AppText.SaveStartupLanguageCode(_settings.LanguageCode);
            RefreshLocalizedOptions();
            _updateStatus = CreateIdleUpdateStatus();

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

    public async Task<DriveRoute> AddGraphRouteAsync(SharePointPinnedFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        await _routeMutationGate.WaitAsync();
        try
        {
            var existing = Routes.FirstOrDefault(route =>
                route.HasGraphIdentity &&
                string.Equals(route.DriveId, folder.DriveId, StringComparison.Ordinal) &&
                string.Equals(route.RootItemId, folder.ItemId, StringComparison.Ordinal));
            if (existing is not null)
            {
                return existing;
            }

            var route = new DriveRoute
            {
                DisplayName = CreateUniqueRouteName(folder.DisplayName),
                SharePointUrl = folder.SiteWebUrl.Trim().TrimEnd('/'),
                RemotePath = SharePointRouteParser.NormalizeRemotePath(folder.DisplayPath),
                SiteId = folder.SiteId.Trim(),
                DriveId = folder.DriveId.Trim(),
                RootItemId = folder.ItemId.Trim(),
                FolderWebUrl = folder.FolderWebUrl.Trim().TrimEnd('/'),
                IsConnected = true,
                StatusText = AppText.Get("BrowserRouteConnected"),
                LastCheckedAt = DateTimeOffset.UtcNow
            };

            await _database.AddRouteAsync(route);
            Routes.Add(route);
            _virtualDriveStatus = await _virtualDrive.GetStatusAsync(_settings, Routes);
            RefreshState();
            return route;
        }
        finally
        {
            _routeMutationGate.Release();
        }
    }

    public async Task UpdateRouteAsync(Guid routeId, string displayName, string sharePointUrl, string remotePath)
    {
        var route = Routes.FirstOrDefault(item => item.Id == routeId);
        if (route is null)
        {
            return;
        }

        var normalizedSiteUrl = sharePointUrl.Trim().TrimEnd('/');
        var normalizedRemotePath = SharePointRouteParser.NormalizeRemotePath(remotePath);
        var locationChanged =
            !string.Equals(route.SharePointUrl, normalizedSiteUrl, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(route.RemotePath, normalizedRemotePath, StringComparison.OrdinalIgnoreCase);

        if (locationChanged)
        {
            return;
        }

        route.DisplayName = displayName.Trim();
        route.SharePointUrl = normalizedSiteUrl;
        route.RemotePath = normalizedRemotePath;

        await _database.UpdateRouteAsync(route);
        var index = Routes.IndexOf(route);
        Routes.RemoveAt(index);
        Routes.Insert(index, route);
        _virtualDriveStatus = await _virtualDrive.GetStatusAsync(_settings, Routes);
        RefreshState();
    }

    private string CreateUniqueRouteName(string proposedName)
    {
        var baseName = string.IsNullOrWhiteSpace(proposedName)
            ? AppText.Get("ExplorerPinnedFolderFallbackName")
            : proposedName.Trim();
        var candidate = baseName;
        var suffix = 2;
        while (Routes.Any(route => string.Equals(route.DisplayName, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} ({suffix++})";
        }

        return candidate;
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

        var result = route.HasGraphIdentity || !IsBrowserSessionMode || browserSessionTester is null
            ? await _sharePointService.TestRouteAsync(route)
            : await browserSessionTester(route);

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

    private IReadOnlyList<string> GetSetupWizardOccupiedMountPoints()
    {
        try
        {
            var occupied = DriveInfo.GetDrives()
                .Select(drive => drive.Name.TrimEnd('\\', '/'))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            // A drive currently mounted by this EasyShare instance may remain selected.
            // The default S: is not exempted unless the active status proves ownership.
            if (_virtualDriveStatus.CanOpenInExplorer)
            {
                occupied.RemoveAll(name => string.Equals(
                    name,
                    _persistedSettings.MountPoint.Trim().TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase));
            }

            return occupied;
        }
        catch
        {
            // The wizard can still validate the requested letter syntactically if
            // Windows temporarily refuses drive enumeration.
            return Array.Empty<string>();
        }
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
        UpdateProgressText = _availableUpdate?.Channel == AppUpdateChannel.MicrosoftStore
            ? AppText.Format("UpdateProgressStoreFormat", progress.Percentage)
            : progress.IsIndeterminate
            ? AppText.Format("UpdateProgressIndeterminateFormat", FormatBytes(progress.BytesReceived))
            : AppText.Format(
                "UpdateProgressFormat",
                progress.Percentage,
                FormatBytes(progress.BytesReceived),
                FormatBytes(progress.TotalBytes.GetValueOrDefault()));
        OnPropertyChanged(nameof(UpdateProgressVisibility));
    }

    private async Task InstallMicrosoftStoreUpdateAsync(IntPtr ownerWindow)
    {
        if (_availableUpdate is null)
        {
            return;
        }

        _updateStatus = new AppUpdateStatus(
            AppText.Get("UpdateStatusStoreInstallingTitle"),
            AppText.Get("UpdateStatusStoreInstallingMessage"),
            InfoBarSeverity.Informational,
            _availableUpdate);
        IsUpdateProgressIndeterminate = true;
        UpdateProgressText = AppText.Format("UpdateProgressStoreFormat", 0d);
        RefreshUpdateState();

        var progress = new Progress<AppUpdateProgress>(ReportDownloadProgress);
        var result = await _appUpdateService.InstallMicrosoftStoreUpdateAsync(ownerWindow, progress);
        IsUpdateProgressIndeterminate = false;

        if (result == StorePackageUpdateState.Completed)
        {
            UpdateProgressValue = 100;
            UpdateProgressText = AppText.Get("UpdateProgressStoreCompleted");
            _availableUpdate = null;
            _updateStatus = new AppUpdateStatus(
                AppText.Get("UpdateStatusStoreInstalledTitle"),
                AppText.Get("UpdateStatusStoreInstalledMessage"),
                InfoBarSeverity.Success);
            RefreshUpdateState();
            return;
        }

        UpdateProgressText = string.Empty;
        _updateStatus = result == StorePackageUpdateState.Canceled
            ? new AppUpdateStatus(
                AppText.Get("UpdateStatusStoreCanceledTitle"),
                AppText.Get("UpdateStatusStoreCanceledMessage"),
                InfoBarSeverity.Informational,
                _availableUpdate)
            : new AppUpdateStatus(
                AppText.Get("UpdateStatusDownloadErrorTitle"),
                AppText.Format("UpdateStatusStoreInstallStateMessage", result),
                InfoBarSeverity.Warning,
                _availableUpdate);
        RefreshUpdateState();
    }

    private bool UseDownloadedUpdateIfAvailable(AppUpdateInfo update)
    {
        if (!_appUpdateService.TryGetDownloadedUpdatePath(update, out var downloadedPath))
        {
            UpdateProgressText = AppText.Format(
                "UpdateProgressReadyFormat",
                update.AssetName,
                FormatBytes(update.AssetSizeBytes));
            return false;
        }

        _downloadedUpdatePath = downloadedPath;
        UpdateProgressValue = 100;
        IsUpdateProgressIndeterminate = false;
        UpdateProgressText = AppText.Format("UpdateProgressDownloadedFormat", Path.GetFileName(downloadedPath));
        _updateStatus = new AppUpdateStatus(
            AppText.Get("UpdateStatusDownloadedTitle"),
            AppText.Get("UpdateStatusDownloadedMessage"),
            InfoBarSeverity.Warning,
            update);
        return true;
    }

    private void SetBooleanSetting(
        bool current,
        bool value,
        Action<bool> apply,
        [CallerMemberName] string? propertyName = null)
    {
        if (current == value)
        {
            return;
        }

        apply(value);
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(NotificationsEnabled) or nameof(QuietModeEnabled))
        {
            OnPropertyChanged(nameof(IsNotificationDeliveryEnabled));
        }
    }

    private void ApplyEnterprisePolicy()
    {
        var policy = _enterprisePolicy.Policy;
        if (!policy.BrowserSessionAllowed && _settings.AuthenticationMode == AuthenticationMode.BrowserSession)
        {
            _settings.AuthenticationMode = AuthenticationMode.MicrosoftGraph;
        }

        if (!string.IsNullOrWhiteSpace(policy.ClientId))
        {
            _settings.ClientId = policy.ClientId;
        }

        if (!string.IsNullOrWhiteSpace(policy.TenantId))
        {
            _settings.TenantId = policy.TenantId;
        }
        else if (policy.AllowedTenantIds.Count > 0 &&
                 !policy.AllowedTenantIds.Contains(_settings.TenantId, StringComparer.OrdinalIgnoreCase))
        {
            _settings.TenantId = policy.AllowedTenantIds[0];
        }

        if (!string.IsNullOrWhiteSpace(policy.MountPoint))
        {
            _settings.MountPoint = policy.MountPoint;
        }

        if (policy.StartWithWindows is bool startWithWindows)
        {
            _settings.StartWithWindows = startWithWindows;
        }

        if (policy.AutoStartVirtualDrive is bool autoStartVirtualDrive)
        {
            _settings.AutoStartVirtualDrive = autoStartVirtualDrive;
        }

        if (policy.CacheMinutes is int cacheMinutes)
        {
            _settings.CacheMinutes = cacheMinutes;
        }

        if (policy.OfflineCacheLimitMb is int offlineCacheLimitMb)
        {
            _settings.OfflineCacheLimitMb = offlineCacheLimitMb;
        }
    }

    private void RefreshUpdateCommandState()
    {
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(CanDownloadUpdate));
        OnPropertyChanged(nameof(CanInstallUpdate));
        OnPropertyChanged(nameof(CanOpenUpdateRelease));
        OnPropertyChanged(nameof(UpdateProgressVisibility));
        OnPropertyChanged(nameof(DownloadUpdateButtonVisibility));
        OnPropertyChanged(nameof(InstallUpdateButtonVisibility));
        OnPropertyChanged(nameof(OpenUpdateReleaseButtonVisibility));
        OnPropertyChanged(nameof(UpdatePrimaryActionLabel));
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
        OnPropertyChanged(nameof(CanEditGraphSettings));
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
        OnPropertyChanged(nameof(ThemeModeIndex));
        OnPropertyChanged(nameof(ThemeMode));
        OnPropertyChanged(nameof(LanguageIndex));
        OnPropertyChanged(nameof(AccentColorValue));
        OnPropertyChanged(nameof(HighContrastEnabled));
        OnPropertyChanged(nameof(NotificationsEnabled));
        OnPropertyChanged(nameof(NotifyUploadCompleted));
        OnPropertyChanged(nameof(NotifyUploadFailed));
        OnPropertyChanged(nameof(NotifyConflict));
        OnPropertyChanged(nameof(NotifySessionExpired));
        OnPropertyChanged(nameof(NotifyDriveDisconnected));
        OnPropertyChanged(nameof(NotifyUpdateReady));
        OnPropertyChanged(nameof(QuietModeEnabled));
        OnPropertyChanged(nameof(OfflineCacheLimitMb));
        OnPropertyChanged(nameof(OfflinePauseOnMeteredNetwork));
        OnPropertyChanged(nameof(OfflinePauseOnBattery));
        OnPropertyChanged(nameof(IsNotificationDeliveryEnabled));
        OnPropertyChanged(nameof(IsEnterpriseManaged));
        OnPropertyChanged(nameof(EnterpriseManagedVisibility));
        OnPropertyChanged(nameof(CanEditAccessSettings));
        OnPropertyChanged(nameof(CanEditDriveSettings));
        OnPropertyChanged(nameof(CanEditCacheSettings));
        OnPropertyChanged(nameof(IsInteractiveSignInAllowed));
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
        OnPropertyChanged(nameof(UpdateChannelText));
        OnPropertyChanged(nameof(UpdateChangelog));
        OnPropertyChanged(nameof(UpdateChangelogVersion));
        OnPropertyChanged(nameof(UpdateChangelogVisibility));
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

    private AppUpdateStatus CreateIdleUpdateStatus() =>
        new(
            AppText.Get("UpdateStatusIdleTitle"),
            _appUpdateService.UpdateChannel == AppUpdateChannel.MicrosoftStore
                ? AppText.Get("UpdateStatusStoreIdleMessage")
                : AppText.Get("UpdateStatusIdleMessage"),
            InfoBarSeverity.Informational);

    private void RefreshLocalizedOptions()
    {
        SetOption(AuthenticationModeOptions, 0, AppText.Get("SettingsAccessModeBrowser"));
        SetOption(AuthenticationModeOptions, 1, AppText.Get("SettingsAccessModeGraph"));
        SetOption(LanguageOptions, 0, AppText.Get("SettingsLanguagePortuguese"));
        SetOption(LanguageOptions, 1, AppText.Get("SettingsLanguageEnglish"));
        SetOption(ThemeModeOptions, 0, AppText.Get("SettingsThemeSystem"));
        SetOption(ThemeModeOptions, 1, AppText.Get("SettingsThemeLight"));
        SetOption(ThemeModeOptions, 2, AppText.Get("SettingsThemeDark"));
    }

    private static void SetOption(ObservableCollection<string> options, int index, string value)
    {
        while (options.Count <= index)
        {
            options.Add(string.Empty);
        }

        if (!string.Equals(options[index], value, StringComparison.Ordinal))
        {
            options[index] = value;
        }
    }

    private static Color ParseAccentColor(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 7 && normalized[0] == '#' && normalized[1..].All(Uri.IsHexDigit))
        {
            return Color.FromArgb(
                255,
                Convert.ToByte(normalized[1..3], 16),
                Convert.ToByte(normalized[3..5], 16),
                Convert.ToByte(normalized[5..7], 16));
        }

        return Color.FromArgb(255, 232, 111, 45);
    }

    private static string ToAccentColorString(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static AuthStatus BrowserSessionStatus(string message, bool signedIn) =>
        new(
            signedIn ? AuthState.SignedIn : AuthState.SignedOut,
            signedIn ? AppText.Get("AuthBrowserSignedInTitle") : AppText.Get("AuthBrowserSignedOutTitle"),
            message,
            signedIn ? AppText.Get("AuthBrowserAccount") : AppText.Get("AccountNone"),
            null);
}
