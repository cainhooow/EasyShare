using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using EasyShare.Models;
using EasyShare.Resources;
using EasyShare.Services;
using System.Runtime.InteropServices;
using WinRT.Interop;
using Windows.UI;

namespace EasyShare;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\EasyShare.SingleInstance";
    private Mutex? _singleInstanceMutex;

    public static Window? MainWindow { get; private set; }
    public static bool StartMinimized { get; private set; }

    public App()
    {
        if (!TryClaimSingleInstance())
        {
            StartupDiagnostics.Write("Another EasyShare instance is already running. Exiting duplicate process.");
            Environment.Exit(0);
            return;
        }

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

    private bool TryClaimSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            return createdNew;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Could not create EasyShare single-instance mutex.", ex);
            return true;
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

    public static void ApplyAppearance(AppThemeMode themeMode, Color accentColor, bool highContrast)
    {
        try
        {
            if (MainWindow?.Content is FrameworkElement content)
            {
                content.RequestedTheme = highContrast
                    ? ElementTheme.Dark
                    : themeMode switch
                    {
                        AppThemeMode.Light => ElementTheme.Light,
                        AppThemeMode.Dark => ElementTheme.Dark,
                        _ => ElementTheme.Default
                    };
            }

            var resources = Current.Resources;
            var effectiveAccent = highContrast
                ? GetSystemColor(resources, "SystemColorHighlightColor", Microsoft.UI.Colors.Yellow)
                : accentColor;
            resources["SystemAccentColor"] = effectiveAccent;

            foreach (var entry in resources.ThemeDictionaries)
            {
                if (entry.Value is not ResourceDictionary themeResources)
                {
                    continue;
                }

                var themeName = entry.Key?.ToString();
                if (highContrast)
                {
                    var windowColor = GetSystemColor(resources, "SystemColorWindowColor", Microsoft.UI.Colors.Black);
                    SetThemeBrush(themeResources, "EasyShareShellBackgroundBrush", windowColor);
                    SetThemeBrush(themeResources, "EasyShareSurfaceBrush", windowColor);
                    SetThemeBrush(themeResources, "EasyShareSurfaceSubtleBrush", windowColor);
                    SetThemeBrush(themeResources, "EasyShareCommandBarBackgroundBrush", windowColor);
                    SetThemeBrush(themeResources, "EasyShareDialogBackgroundBrush", windowColor);
                    SetThemeBrush(themeResources, "EasyShareOverlayBackgroundBrush", windowColor);
                    SetThemeBrush(themeResources, "EasyShareAccentSoftBrush", windowColor);
                }
                else
                {
                    SetThemeBrush(themeResources, "EasyShareShellBackgroundBrush", Microsoft.UI.Colors.Transparent);
                    switch (themeName)
                    {
                        case "Light":
                            SetThemeBrush(themeResources, "EasyShareSurfaceBrush", Color.FromArgb(217, 255, 255, 255));
                            SetThemeBrush(themeResources, "EasyShareSurfaceSubtleBrush", Color.FromArgb(184, 255, 255, 255));
                            SetThemeBrush(themeResources, "EasyShareCommandBarBackgroundBrush", Microsoft.UI.Colors.Transparent);
                            SetThemeBrush(themeResources, "EasyShareDialogBackgroundBrush", Microsoft.UI.Colors.White);
                            SetThemeBrush(themeResources, "EasyShareOverlayBackgroundBrush", Color.FromArgb(255, 245, 247, 250));
                            break;
                        case "Dark":
                            SetThemeBrush(themeResources, "EasyShareSurfaceBrush", Color.FromArgb(184, 47, 44, 48));
                            SetThemeBrush(themeResources, "EasyShareSurfaceSubtleBrush", Color.FromArgb(168, 43, 41, 46));
                            SetThemeBrush(themeResources, "EasyShareCommandBarBackgroundBrush", Microsoft.UI.Colors.Transparent);
                            SetThemeBrush(themeResources, "EasyShareDialogBackgroundBrush", Color.FromArgb(255, 37, 41, 47));
                            SetThemeBrush(themeResources, "EasyShareOverlayBackgroundBrush", Color.FromArgb(255, 23, 25, 29));
                            break;
                        case "HighContrast":
                            SetThemeBrush(themeResources, "EasyShareSurfaceBrush", GetSystemColor(resources, "SystemColorWindowColor", Microsoft.UI.Colors.White));
                            SetThemeBrush(themeResources, "EasyShareSurfaceSubtleBrush", GetSystemColor(resources, "SystemColorWindowColor", Microsoft.UI.Colors.White));
                            SetThemeBrush(themeResources, "EasyShareCommandBarBackgroundBrush", GetSystemColor(resources, "SystemColorWindowColor", Microsoft.UI.Colors.White));
                            SetThemeBrush(themeResources, "EasyShareDialogBackgroundBrush", GetSystemColor(resources, "SystemColorWindowColor", Microsoft.UI.Colors.White));
                            SetThemeBrush(themeResources, "EasyShareOverlayBackgroundBrush", GetSystemColor(resources, "SystemColorWindowColor", Microsoft.UI.Colors.White));
                            break;
                        default:
                            SetThemeBrush(themeResources, "EasyShareSurfaceBrush", Microsoft.UI.Colors.White);
                            SetThemeBrush(themeResources, "EasyShareSurfaceSubtleBrush", Color.FromArgb(255, 238, 242, 246));
                            SetThemeBrush(themeResources, "EasyShareCommandBarBackgroundBrush", Microsoft.UI.Colors.Transparent);
                            SetThemeBrush(themeResources, "EasyShareDialogBackgroundBrush", Microsoft.UI.Colors.White);
                            SetThemeBrush(themeResources, "EasyShareOverlayBackgroundBrush", Color.FromArgb(255, 245, 247, 250));
                            break;
                    }

                    SetThemeBrush(
                        themeResources,
                        "EasyShareAccentSoftBrush",
                        Color.FromArgb(48, effectiveAccent.R, effectiveAccent.G, effectiveAccent.B));
                }

                SetThemeBrush(themeResources, "EasyShareAccentBrush", effectiveAccent);
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Could not apply saved appearance settings.", ex);
        }
    }

    private static Color GetSystemColor(ResourceDictionary resources, string key, Color fallback) =>
        resources.TryGetValue(key, out var value) && value is Color color
            ? color
            : fallback;

    private static void SetThemeBrush(ResourceDictionary resources, string key, Color color) =>
        resources[key] = new SolidColorBrush(color);

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
