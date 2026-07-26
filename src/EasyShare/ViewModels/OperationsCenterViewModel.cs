using System.Collections.ObjectModel;
using System.Collections.Specialized;
using EasyShare.Models;
using EasyShare.Resources;
using EasyShare.Services;
using Microsoft.UI.Xaml;

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
        AppText.LanguageChanged += AppText_LanguageChanged;
        RefreshJobViews();
    }

    public ObservableCollection<DriveRoute> Routes { get; }

    public ObservableCollection<SyncJob> Transfers { get; } = [];

    public ObservableCollection<SyncJob> Conflicts { get; } = [];

    public ObservableCollection<HealthCheckItem> HealthChecks { get; } = [];

    public ObservableCollection<OfflineCacheEntry> OfflineEntries { get; } = [];

    public int PendingCount =>
        Transfers.Count(job => job.State is not (SyncJobState.Completed or SyncJobState.Discarded));

    public int ConflictCount => Conflicts.Count;

    public int WaitingCount =>
        Transfers.Count(job =>
            job.State is SyncJobState.PersistingLocal or SyncJobState.StoredLocally or SyncJobState.Waiting);

    public int UploadingCount =>
        Transfers.Count(job => job.State is SyncJobState.Uploading or SyncJobState.VerifyingRemote);

    public int AttentionCount =>
        Transfers.Count(job => job.State is SyncJobState.Failed or SyncJobState.Conflict);

    public int CompletedCount => Transfers.Count(job => job.State == SyncJobState.Completed);

    public int DiscardedCount => Transfers.Count(job => job.State == SyncJobState.Discarded);

    public bool HasCompleted => CompletedCount > 0;

    public Visibility TransfersVisibility =>
        Transfers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyTransfersVisibility =>
        Transfers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string TransfersTabHeader =>
        AppText.Format("OperationsTransfersTabFormat", PendingCount);

    public string ConflictsTabHeader =>
        AppText.Format("OperationsConflictsTabFormat", ConflictCount);

    public string TransfersSummary =>
        AppText.Format(
            "OperationsSummaryFormat",
            WaitingCount,
            UploadingCount,
            AttentionCount,
            CompletedCount,
            DiscardedCount);

    public string ClearCompletedAutomationName => AppText.Get("ClearCompletedAutomationName");

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
        AppText.LanguageChanged -= AppText_LanguageChanged;
    }

    private void Jobs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (var job in e.NewItems?.OfType<SyncJob>() ?? [])
                {
                    var sourceIndex = _jobs.IndexOf(job);
                    Transfers.Insert(Math.Clamp(sourceIndex, 0, Transfers.Count), job);
                    if (job.State == SyncJobState.Conflict)
                    {
                        Conflicts.Insert(0, job);
                    }
                }
                break;

            case NotifyCollectionChangedAction.Replace:
                foreach (var job in e.NewItems?.OfType<SyncJob>() ?? [])
                {
                    var transferIndex = FindJobIndex(Transfers, job.Id);
                    if (transferIndex >= 0)
                    {
                        // Replace only the row whose state changed. Rebuilding the
                        // collection on every progress event drops keyboard focus
                        // and causes unrelated Narrator announcements.
                        Transfers[transferIndex] = job;
                    }
                    else
                    {
                        Transfers.Insert(0, job);
                    }

                    var conflictIndex = FindJobIndex(Conflicts, job.Id);
                    if (job.State == SyncJobState.Conflict)
                    {
                        if (conflictIndex >= 0)
                        {
                            Conflicts[conflictIndex] = job;
                        }
                        else
                        {
                            Conflicts.Insert(0, job);
                        }
                    }
                    else if (conflictIndex >= 0)
                    {
                        Conflicts.RemoveAt(conflictIndex);
                    }
                }
                break;

            case NotifyCollectionChangedAction.Remove:
                foreach (var job in e.OldItems?.OfType<SyncJob>() ?? [])
                {
                    RemoveJob(Transfers, job.Id);
                    RemoveJob(Conflicts, job.Id);
                }
                break;

            default:
                RefreshJobViews();
                return;
        }

        RefreshJobSummary();
    }

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

        RefreshJobSummary();
    }

    private void RefreshJobSummary()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(ConflictCount));
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(UploadingCount));
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(DiscardedCount));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(TransfersVisibility));
        OnPropertyChanged(nameof(EmptyTransfersVisibility));
        OnPropertyChanged(nameof(TransfersTabHeader));
        OnPropertyChanged(nameof(ConflictsTabHeader));
        OnPropertyChanged(nameof(TransfersSummary));
        OnPropertyChanged(nameof(ClearCompletedAutomationName));
    }

    private static int FindJobIndex(IList<SyncJob> jobs, Guid jobId)
    {
        for (var index = 0; index < jobs.Count; index++)
        {
            if (jobs[index].Id == jobId)
            {
                return index;
            }
        }

        return -1;
    }

    private static void RemoveJob(IList<SyncJob> jobs, Guid jobId)
    {
        var index = FindJobIndex(jobs, jobId);
        if (index >= 0)
        {
            jobs.RemoveAt(index);
        }
    }

    private void AppText_LanguageChanged(object? sender, EventArgs e)
    {
        RefreshJobViews();
    }
}
