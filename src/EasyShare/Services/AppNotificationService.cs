using System.Collections.Concurrent;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace EasyShare.Services;

public sealed record AppNotificationActivation(string Destination, string? ItemId);

/// <summary>
/// Privacy-preserving local Windows notifications for actionable app state.
/// Notification bodies intentionally accept only already-sanitized product text.
/// </summary>
public sealed class AppNotificationService : IDisposable
{
    private readonly ConcurrentQueue<AppNotificationActivation> _pendingActivations = new();
    private bool _registered;

    public event Action<AppNotificationActivation>? Activated;

    public bool IsAvailable { get; private set; }

    public void Register()
    {
        if (_registered)
        {
            return;
        }

        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += Manager_NotificationInvoked;
            manager.Register();
            _registered = true;
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            StartupDiagnostics.Write("Windows app notifications are unavailable.", ex);
        }
    }

    public void Show(
        string category,
        string title,
        string message,
        string destination,
        string? itemId = null)
    {
        if (!_registered || !IsAvailable || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("destination", SanitizeArgument(destination))
                .AddArgument("category", SanitizeArgument(category))
                .AddText(Truncate(title, 96))
                .AddText(Truncate(message, 180));

            if (!string.IsNullOrWhiteSpace(itemId))
            {
                builder.AddArgument("item", SanitizeArgument(itemId));
            }

            var notification = builder.BuildNotification();
            notification.Group = SanitizeTag(category);
            notification.Tag = SanitizeTag(itemId ?? category);
            notification.Expiration = DateTimeOffset.Now.AddHours(12);
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Could not show a Windows app notification.", ex);
        }
    }

    public IReadOnlyList<AppNotificationActivation> DrainPendingActivations()
    {
        var activations = new List<AppNotificationActivation>();
        while (_pendingActivations.TryDequeue(out var activation))
        {
            activations.Add(activation);
        }

        return activations;
    }

    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked -= Manager_NotificationInvoked;
            manager.Unregister();
        }
        catch
        {
            // Shutdown must remain reliable when the notification platform is unavailable.
        }

        _registered = false;
        IsAvailable = false;
    }

    private void Manager_NotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        var values = ParseArguments(args.Argument);
        var activation = new AppNotificationActivation(
            values.GetValueOrDefault("destination") ?? "Home",
            values.GetValueOrDefault("item"));

        if (Activated is { } handler)
        {
            handler(activation);
        }
        else
        {
            _pendingActivations.Enqueue(activation);
        }
    }

    private static Dictionary<string, string> ParseArguments(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2)
            {
                result[Uri.UnescapeDataString(pair[0])] = Uri.UnescapeDataString(pair[1]);
            }
        }

        return result;
    }

    private static string SanitizeArgument(string value) =>
        Truncate(value.Replace("&", string.Empty).Replace("=", string.Empty), 96);

    private static string SanitizeTag(string value)
    {
        var safe = new string(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        return Truncate(string.IsNullOrWhiteSpace(safe) ? "easyshare" : safe, 16);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
