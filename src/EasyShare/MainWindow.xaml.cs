using Microsoft.UI.Xaml;
using EasyShare.Resources;
using EasyShare.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;

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
