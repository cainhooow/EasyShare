using System.Text;

namespace EasyShare.Services;

public static class StartupDiagnostics
{
    private static readonly object Gate = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyShare",
        "Logs");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "startup.log");

    public static void Write(string message) =>
        Write(message, exception: null);

    public static void Write(Exception exception) =>
        Write(exception.Message, exception);

    public static void Write(string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append(" | ")
                .AppendLine(message);

            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }

            lock (Gate)
            {
                File.AppendAllText(LogPath, builder.ToString());
            }
        }
        catch
        {
            // Startup logging must never become the reason the app does not open.
        }
    }
}
