using Microsoft.UI.Xaml;
using EasyShare.Models;
using EasyShare.Resources;
using EasyShare.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace EasyShare;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly SemaphoreSlim _decisionGate = new(1, 1);
    private TrayIconService? _trayIconService;
    private bool _exitRequested;
    private bool _runtimeHandlersAttached;

    public MainWindow()
    {
        StartupDiagnostics.Write("MainWindow constructor started.");
        InitializeComponent();
        Title = AppText.Get("AppName");

        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Could not apply Mica backdrop.", ex);
        }

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Could not apply custom title bar.", ex);
        }

        try
        {
            AppWindow.SetIcon("Assets/AppIcon.ico");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Could not apply window icon.", ex);
        }

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));

        _trayIconService = new TrayIconService(
            this,
            ExitFromTray,
            TrayOperationalStateChanged);
        App.Services.UploadQueue.JobChanged += UploadQueue_JobChanged;
        AppText.LanguageChanged += AppText_LanguageChanged;
        AppWindow.Changed += AppWindow_Changed;
        Closed += MainWindow_Closed;
        _runtimeHandlersAttached = true;

        StartupDiagnostics.Write("MainWindow constructor completed.");
    }

    public void HideToTray()
    {
        if (_trayIconService?.TryHide() == true)
        {
            return;
        }

        StartupDiagnostics.Write(
            "The window remained visible because the notification-area icon is unavailable.");
        RestoreAndActivate();
    }

    public void MinimizeToTrayForStartup()
    {
        // Hiding does not transition the presenter through Minimized, so it
        // cannot leave a stale one-shot suppression that swallows the user's
        // next real minimize action.
        HideToTray();
    }

    public Task RefreshTrayUploadStatusAsync() => RefreshTrayStatusAsync();

    public void RestoreAndActivate()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, 9);
        Activate();
        SetForegroundWindow(hwnd);
    }

    public void ApplyTitleBarAppearance(bool useDarkButtons, bool highContrast)
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            var background = highContrast
                ? GetSystemColor("SystemColorWindowColor", Microsoft.UI.Colors.Black)
                : useDarkButtons
                    ? Color.FromArgb(255, 255, 255, 255)
                    : Color.FromArgb(255, 32, 32, 32);
            var foreground = highContrast
                ? GetSystemColor("SystemColorWindowTextColor", Microsoft.UI.Colors.White)
                : useDarkButtons
                    ? Color.FromArgb(255, 32, 32, 32)
                    : Microsoft.UI.Colors.White;

            AppTitleBar.Background = new SolidColorBrush(background);
            AppTitleText.Foreground = new SolidColorBrush(foreground);
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = useDarkButtons
                ? Color.FromArgb(24, 0, 0, 0)
                : Color.FromArgb(24, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = useDarkButtons
                ? Color.FromArgb(40, 0, 0, 0)
                : Color.FromArgb(40, 255, 255, 255);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Could not apply title bar appearance.", ex);
        }
    }

    private static Color GetSystemColor(string key, Color fallback)
    {
        var resources = Application.Current.Resources;
        return resources.TryGetValue(key, out var value) && value is Color color
            ? color
            : fallback;
    }

    private async void ExitFromTray()
    {
        await RunDecisionAsync(
            () => RequestExitCoreAsync(restoreWindow: true),
            "Could not process the notification-area exit request.");
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            _ = HandleMinimizeAsync();
        }
    }

    private async Task HandleMinimizeAsync()
    {
        await RunDecisionAsync(
            async () =>
            {
                var settings = await App.Services.Database.GetSettingsAsync();
                if (settings.CloseBehavior == AppCloseBehavior.Ask)
                {
                    RestoreAndActivate();
                    await ExplainFirstCloseCoreAsync(settings);
                }
                else
                {
                    HideToTray();
                }
            },
            "Could not apply the notification-area behavior after minimize.");
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_exitRequested)
        {
            DetachRuntimeHandlers();
            _trayIconService?.Dispose();
            _trayIconService = null;
            return;
        }

        args.Handled = true;
        await RunDecisionAsync(
            async () =>
            {
                var settings = await App.Services.Database.GetSettingsAsync();
                switch (settings.CloseBehavior)
                {
                    case AppCloseBehavior.KeepRunningInTray:
                        HideToTray();
                        break;
                    case AppCloseBehavior.Exit:
                        await RequestExitCoreAsync(restoreWindow: false);
                        break;
                    default:
                        await ExplainFirstCloseCoreAsync(settings);
                        break;
                }
            },
            "Could not apply the saved close behavior.");
    }

    private async Task RunDecisionAsync(Func<Task> decision, string failureMessage)
    {
        if (_exitRequested || !await _decisionGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            await decision();
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write(failureMessage, ex);
            if (!_exitRequested)
            {
                RestoreAndActivate();
            }
        }
        finally
        {
            _decisionGate.Release();
        }
    }

    private async Task ExplainFirstCloseCoreAsync(AppSettings settings)
    {
        var dialog = CreateDialog(
            AppText.Get("CloseChoiceTitle"),
            AppText.Get("CloseChoiceMessage"),
            AppText.Get("ActionKeepRunningInTray"),
            AppText.Get("ActionExitApp"),
            AppText.Get("CommonCancel"),
            ContentDialogButton.Primary);
        if (dialog is null)
        {
            HideToTray();
            return;
        }

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            settings.CloseBehavior = AppCloseBehavior.KeepRunningInTray;
            await App.Services.Database.SaveSettingsAsync(settings);
            HideToTray();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            settings.CloseBehavior = AppCloseBehavior.Exit;
            await App.Services.Database.SaveSettingsAsync(settings);
            await RequestExitCoreAsync(restoreWindow: false);
        }
    }

    private async Task RequestExitCoreAsync(bool restoreWindow)
    {
        if (_exitRequested)
        {
            return;
        }

        if (restoreWindow)
        {
            RestoreAndActivate();
        }

        var virtualDriveQuiesced = false;
        try
        {
            App.Services.VirtualDrive.QuiesceForShutdown();
            virtualDriveQuiesced = true;
            var activeJobs = await App.Services.UploadQueue.GetActiveJobsAsync();
            if (activeJobs.Count > 0)
            {
                var dialog = CreateDialog(
                    AppText.Get("ExitUploadsTitle"),
                    AppText.Format("ExitUploadsMessageFormat", activeJobs.Count),
                    AppText.Get("ActionContinueInBackground"),
                    AppText.Get("ActionExitAndResumeLater"),
                    AppText.Get("CommonCancel"),
                    ContentDialogButton.Primary);
                if (dialog is null)
                {
                    await ResumeVirtualDriveAfterCancelledExitAsync();
                    virtualDriveQuiesced = false;
                    RestoreAndActivate();
                    return;
                }

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Secondary)
                {
                    await ResumeVirtualDriveAfterCancelledExitAsync();
                    virtualDriveQuiesced = false;
                    if (result == ContentDialogResult.Primary)
                    {
                        HideToTray();
                    }
                    else
                    {
                        RestoreAndActivate();
                    }

                    return;
                }
            }

            virtualDriveQuiesced = false;
            await CompleteExitAsync();
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Could not exit EasyShare safely.", ex);
            if (virtualDriveQuiesced && !_exitRequested)
            {
                await ResumeVirtualDriveAfterCancelledExitAsync();
            }

            if (!_exitRequested)
            {
                RestoreAndActivate();
            }
        }
    }

    private async Task ResumeVirtualDriveAfterCancelledExitAsync()
    {
        try
        {
            var settings = await App.Services.Database.GetSettingsAsync();
            var routes = await App.Services.Database.GetRoutesAsync();
            await App.Services.VirtualDrive
                .ResumeAfterShutdownCancellationAsync(settings, routes);
        }
        catch (Exception ex)
        {
            App.Services.VirtualDrive.ReleaseShutdownQuiesce();
            StartupDiagnostics.Write(
                "Could not remount the virtual drive after exit was cancelled.",
                ex);
        }
    }

    private async Task CompleteExitAsync()
    {
        try
        {
            await App.Services.UploadQueue.StopAsync();
        }
        catch (Exception ex)
        {
            // The queue persists every transition. A stop failure must not leave a
            // hidden, half-disposed instance after the user confirmed final exit.
            StartupDiagnostics.Write(
                "The upload queue reported an error while stopping; durable work will recover on restart.",
                ex);
        }

        DetachRuntimeHandlers();
        App.ShutdownServices();
        _exitRequested = true;
        Close();
        Application.Current.Exit();
    }

    private void DetachRuntimeHandlers()
    {
        if (!_runtimeHandlersAttached)
        {
            return;
        }

        _runtimeHandlersAttached = false;
        AppWindow.Changed -= AppWindow_Changed;
        App.Services.UploadQueue.JobChanged -= UploadQueue_JobChanged;
        AppText.LanguageChanged -= AppText_LanguageChanged;
    }

    private ContentDialog? CreateDialog(
        string title,
        string message,
        string primaryButtonText,
        string secondaryButtonText,
        string closeButtonText,
        ContentDialogButton defaultButton)
    {
        if (RootFrame.XamlRoot is null)
        {
            return null;
        }

        return new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryButtonText,
            SecondaryButtonText = secondaryButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = defaultButton,
            XamlRoot = RootFrame.XamlRoot
        };
    }

    private void UploadQueue_JobChanged(SyncJob job)
    {
        DispatcherQueue.TryEnqueue(async () => await RefreshTrayStatusAsync());
    }

    private void TrayOperationalStateChanged(bool operational)
    {
        if (operational || _exitRequested)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_exitRequested)
            {
                RestoreAndActivate();
            }
        });
    }

    private void AppText_LanguageChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () => await RefreshTrayStatusAsync());
    }

    private async Task RefreshTrayStatusAsync()
    {
        try
        {
            var jobs = await App.Services.UploadQueue.GetActiveJobsAsync();
            var uploading = jobs.Count(job =>
                job.State is SyncJobState.Uploading or SyncJobState.VerifyingRemote);
            var attention = jobs.Count(job =>
                job.State is SyncJobState.Failed or SyncJobState.Conflict);
            var waiting = Math.Max(0, jobs.Count - uploading - attention);
            var status = jobs.Count == 0
                ? AppText.Get("TrayStatusIdle")
                : AppText.Format("TrayStatusFormat", waiting, uploading, attention);
            _trayIconService?.UpdateStatus(status);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Could not refresh the tray upload summary.", ex);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
