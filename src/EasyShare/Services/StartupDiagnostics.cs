using EasyShare.Models;

namespace EasyShare.Services;

public static class StartupDiagnostics
{
    private static readonly AppDataPaths Paths = new();
    private static readonly object Gate = new();
    private static RotatingDiagnosticLog? _log;

    public static string LogDirectory { get; } = Paths.LogDirectory;

    public static string LogPath { get; } = Path.Combine(LogDirectory, "startup.log");

    public static RotatingDiagnosticLog CurrentLog
    {
        get
        {
            lock (Gate)
            {
                return _log ??= new RotatingDiagnosticLog(LogPath);
            }
        }
    }

    public static void Configure(DiagnosticLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var replacement = new RotatingDiagnosticLog(LogPath, options);
        lock (Gate)
        {
            _log = replacement;
        }
    }

    public static void Write(string message) =>
        Write(message, exception: null);

    public static void Write(Exception exception) =>
        Write(exception.Message, exception);

    public static void Write(string message, Exception? exception)
    {
        try
        {
            CurrentLog.Write(DiagnosticEvent.Create(
                exception is null ? DiagnosticLevel.Information : DiagnosticLevel.Error,
                "application",
                message,
                exception));
        }
        catch
        {
            // Startup logging must never become the reason the app does not open.
        }
    }
}
