using System.Collections.ObjectModel;
using System.Collections.Specialized;
using EasyShare.Models;
using EasyShare.Resources;
using EasyShare.Services;

namespace EasyShare.ViewModels;

public sealed class OperationsCenterViewModel : ObservableObject, IDisposable
{
    private readonly ObservableCollection<SyncJob> _jobs;
    private readonly HealthCenterService _healthCenter;
    private bool _isRefreshingHealth;
    private string _healthSummary = AppText.Get("HealthSummaryPending");

    public OperationsCenterViewModel(
        ObservableCollection<DriveRoute> routes,
        ObservableCollection<SyncJob> jobs,
        HealthCenterService healthCenter)
    {
        Routes = routes;
        _jobs = jobs;
        _healthCenter = healthCenter;
        _jobs.CollectionChanged += Jobs_CollectionChanged;
        RefreshJobViews();
    }

    public ObservableCollection<DriveRoute> Routes { get; }

    public ObservableCollection<SyncJob> Transfers { get; } = [];

    public ObservableCollection<SyncJob> Conflicts { get; } = [];

    public ObservableCollection<HealthCheckItem> HealthChecks { get; } = [];

    public ObservableCollection<OfflineCacheEntry> OfflineEntries { get; } = [];

    public int PendingCount => Transfers.Count(job => job.State is not SyncJobState.Completed);

    public int ConflictCount => Conflicts.Count;

    public bool IsRefreshingHealth
    {
        get => _isRefreshingHealth;
        private set => SetProperty(ref _isRefreshingHealth, value);
    }

    public string HealthSummary
    {
        get => _healthSummary;
        private set => SetProperty(ref _healthSummary, value);
    }

    public async Task RefreshHealthAsync(
        VirtualDriveStatus drive,
        bool browserInitialized,
        bool browserSessionAvailable)
    {
        if (IsRefreshingHealth)
        {
            return;
        }

        IsRefreshingHealth = true;
        try
        {
            var checks = await _healthCenter.InspectAsync(
                drive,
                Routes,
                Transfers,
                browserInitialized,
                browserSessionAvailable);
            HealthChecks.Clear();
            foreach (var check in checks)
            {
                HealthChecks.Add(check);
            }

            var unavailable = checks.Count(check => check.State == HealthCheckState.Unavailable);
            var attention = checks.Count(check => check.State == HealthCheckState.Attention);
            HealthSummary = unavailable > 0
                ? AppText.Format("HealthSummaryUnavailableFormat", unavailable)
                : attention > 0
                    ? AppText.Format("HealthSummaryAttentionFormat", attention)
                    : AppText.Get("HealthSummaryHealthy");
        }
        finally
        {
            IsRefreshingHealth = false;
        }
    }

    public void ReplaceOfflineEntries(IEnumerable<OfflineCacheEntry> entries)
    {
        OfflineEntries.Clear();
        foreach (var entry in entries.OrderBy(entry => entry.DisplayPath, StringComparer.CurrentCultureIgnoreCase))
        {
            OfflineEntries.Add(entry);
        }
    }

    public void Dispose()
    {
        _jobs.CollectionChanged -= Jobs_CollectionChanged;
    }

    private void Jobs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshJobViews();

    private void RefreshJobViews()
    {
        Transfers.Clear();
        foreach (var job in _jobs.OrderByDescending(job => job.UpdatedAt))
        {
            Transfers.Add(job);
        }

        Conflicts.Clear();
        foreach (var job in _jobs
                     .Where(job => job.State == SyncJobState.Conflict)
                     .OrderByDescending(job => job.UpdatedAt))
        {
            Conflicts.Add(job);
        }

        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(ConflictCount));
    }
}
