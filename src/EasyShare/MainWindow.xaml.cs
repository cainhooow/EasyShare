using Microsoft.UI.Xaml;
using EasyShare.Resources;
using EasyShare.Services;
using Microsoft.UI.Windowing;
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
    private TrayIconService? _trayIconService;
    private bool _exitRequested;

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

        _trayIconService = new TrayIconService(this, ExitFromTray);
        AppWindow.Changed += AppWindow_Changed;
        Closed += MainWindow_Closed;

        StartupDiagnostics.Write("MainWindow constructor completed.");
    }

    public void HideToTray() => _trayIconService?.Hide();

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

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
        Application.Current.Exit();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            HideToTray();
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (!_exitRequested)
        {
            args.Handled = true;
            HideToTray();
            return;
        }

        AppWindow.Changed -= AppWindow_Changed;
        _trayIconService?.Dispose();
        _trayIconService = null;
    }
}
