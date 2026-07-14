using EasyShare.Resources;

namespace EasyShare.Models;

public enum HealthCheckState
{
    Healthy,
    Attention,
    Unavailable
}

public sealed record HealthCheckItem(
    string Key,
    string Title,
    string Detail,
    HealthCheckState State,
    DateTimeOffset CheckedAt)
{
    public string StateText => State switch
    {
        HealthCheckState.Healthy => AppText.Get("HealthStateHealthy"),
        HealthCheckState.Attention => AppText.Get("HealthStateAttention"),
        _ => AppText.Get("HealthStateUnavailable")
    };

    public string CheckedText => CheckedAt.LocalDateTime.ToString("g");
}
