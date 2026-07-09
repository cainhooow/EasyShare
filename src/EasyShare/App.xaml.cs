using Microsoft.UI.Xaml;
using EasyShare.Resources;
using EasyShare.Services;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace EasyShare;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    public static bool StartMinimized { get; private set; }

    public App()
    {
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        StartupDiagnostics.Write("App constructor started.");

        try
        {
            InitializeComponent();
            StartupDiagnostics.Write("App XAML loaded.");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("App XAML failed.", ex);
            throw;
        }
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            StartupDiagnostics.Write($"OnLaunched started. Arguments: {args.Arguments}");
            StartMinimized = args.Arguments.Contains("--minimized", StringComparison.OrdinalIgnoreCase);
            MainWindow = new MainWindow();
            MainWindow.Activate();

            if (StartMinimized)
            {
                var hwnd = WindowNative.GetWindowHandle(MainWindow);
                ShowWindow(hwnd, 6);
            }

            StartupDiagnostics.Write("OnLaunched completed.");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("OnLaunched failed.", ex);
            throw;
        }
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        StartupDiagnostics.Write("WinUI unhandled exception.", e.Exception);
    }

    private static void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            StartupDiagnostics.Write("AppDomain unhandled exception.", exception);
        }
        else
        {
            StartupDiagnostics.Write($"AppDomain unhandled exception object: {e.ExceptionObject}");
        }
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        StartupDiagnostics.Write("TaskScheduler unobserved task exception.", e.Exception);
        e.SetObserved();
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
