using System.Diagnostics;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace EasyShare.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "EasyShare";
    private const string StartupTaskId = "EasyShareStartupTask";

    public async Task<bool> IsEnabledAsync()
    {
        var startupTask = await TryGetPackagedStartupTaskAsync();
        return startupTask is not null
            ? startupTask.State == StartupTaskState.Enabled
            : IsRegistryEnabled();
    }

    public async Task<bool> SetEnabledAsync(bool enabled, bool startMinimized)
    {
        var startupTask = await TryGetPackagedStartupTaskAsync();
        if (startupTask is not null)
        {
            return enabled
                ? await EnablePackagedStartupTaskAsync(startupTask)
                : DisablePackagedStartupTask(startupTask);
        }

        SetRegistryEnabled(enabled, startMinimized);
        return enabled && IsRegistryEnabled();
    }

    private static async Task<StartupTask?> TryGetPackagedStartupTaskAsync()
    {
        try
        {
            _ = Package.Current.Id.FamilyName;
            return await StartupTask.GetAsync(StartupTaskId);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> EnablePackagedStartupTaskAsync(StartupTask startupTask)
    {
        try
        {
            if (startupTask.State == StartupTaskState.Enabled)
            {
                return true;
            }

            var state = await startupTask.RequestEnableAsync();
            return state == StartupTaskState.Enabled;
        }
        catch
        {
            return false;
        }
    }

    private static bool DisablePackagedStartupTask(StartupTask startupTask)
    {
        try
        {
            startupTask.Disable();
        }
        catch
        {
            // The Settings > Apps > Startup page is the source of truth if Windows blocks changes here.
        }

        return false;
    }

    private static bool IsRegistryEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(AppName) is string;
    }

    private static void SetRegistryEnabled(bool enabled, bool startMinimized)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (!enabled)
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(AppName, BuildStartupCommand(startMinimized));
    }

    private static string BuildStartupCommand(bool startMinimized) =>
        TryBuildPackagedLaunchCommand() ?? BuildExecutableLaunchCommand(startMinimized);

    private static string? TryBuildPackagedLaunchCommand()
    {
        try
        {
            var familyName = Package.Current.Id.FamilyName;
            if (string.IsNullOrWhiteSpace(familyName))
            {
                return null;
            }

            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            return $"\"{explorer}\" \"shell:AppsFolder\\{familyName}!App\"";
        }
        catch
        {
            return null;
        }
    }

    private static string BuildExecutableLaunchCommand(bool startMinimized)
    {
        var executable = Environment.ProcessPath ??
                         Process.GetCurrentProcess().MainModule?.FileName ??
                         Path.Combine(AppContext.BaseDirectory, "EasyShare.exe");

        var arguments = startMinimized ? " --minimized" : string.Empty;
        return $"\"{executable}\"{arguments}";
    }
}
