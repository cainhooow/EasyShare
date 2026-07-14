using System.Collections.ObjectModel;
using System.Net;
using EasyShare.Models;
using EasyShare.Resources;
using EasyShare.Services;
using Microsoft.UI.Xaml;

namespace EasyShare.ViewModels;

public sealed record SharePointExplorerBreadcrumb(
    string ItemId,
    string DisplayName,
    string WebUrl,
    string DisplayPath)
{
    public override string ToString() => DisplayName;
}

public sealed class SharePointExplorerViewModel : ObservableObject, IDisposable
{
    private readonly GraphSharePointExplorerService _service;
    private readonly object _operationSync = new();
    private CancellationTokenSource? _activeOperation;
    private long _operationVersion;
    private SharePointSiteInfo? _selectedSite;
    private SharePointLibraryInfo? _selectedLibrary;
    private string _searchQuery = string.Empty;
    private string? _nextLink;
    private string _emptyMessageKey = "ExplorerEmptyInitial";
    private string _errorMessage = string.Empty;
    private bool _isBusy;
    private bool _isInitialized;
    private bool _hasError;
    private bool _requiresAuthentication;
    private bool _isForbiddenError;
    private bool _disposed;

    public SharePointExplorerViewModel(GraphSharePointExplorerService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        AppText.LanguageChanged += AppText_LanguageChanged;
    }

    public ObservableCollection<SharePointSiteInfo> Sites { get; } = [];

    public ObservableCollection<SharePointLibraryInfo> Libraries { get; } = [];

    public ObservableCollection<SharePointExplorerBreadcrumb> Breadcrumbs { get; } = [];

    public ObservableCollection<SharePointExplorerItem> Items { get; } = [];

    public SharePointSiteInfo? SelectedSite
    {
        get => _selectedSite;
        private set
        {
            if (SetProperty(ref _selectedSite, value))
            {
                RefreshCommandState();
            }
        }
    }

    public SharePointLibraryInfo? SelectedLibrary
    {
        get => _selectedLibrary;
        private set
        {
            if (SetProperty(ref _selectedLibrary, value))
            {
                RefreshCommandState();
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value ?? string.Empty);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(BusyVisibility));
                OnPropertyChanged(nameof(ShowEmptyState));
                RefreshCommandState();
            }
        }
    }

    public bool IsInitialized
    {
        get => _isInitialized;
        private set
        {
            if (SetProperty(ref _isInitialized, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    public bool HasError
    {
        get => _hasError;
        private set
        {
            if (SetProperty(ref _hasError, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(CanPinCurrentFolder));
            }
        }
    }

    public bool RequiresAuthentication
    {
        get => _requiresAuthentication;
        private set
        {
            if (SetProperty(ref _requiresAuthentication, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(CanPinCurrentFolder));
            }
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool CanSearch => !IsBusy;

    public bool CanRefresh => !IsBusy;

    public bool CanNavigateBack => !IsBusy && Breadcrumbs.Count > 1;

    public bool CanLoadMore => !IsBusy && !string.IsNullOrWhiteSpace(_nextLink);

    public bool CanPinCurrentFolder =>
        !IsBusy &&
        !HasError &&
        !RequiresAuthentication &&
        SelectedSite is not null &&
        SelectedLibrary is not null &&
        Breadcrumbs.Count > 0;

    public bool CanSelectSite => !IsBusy && Sites.Count > 0;

    public bool CanSelectLibrary => !IsBusy && Libraries.Count > 0;

    public bool ShowEmptyState =>
        IsInitialized &&
        !IsBusy &&
        !HasError &&
        !RequiresAuthentication &&
        Items.Count == 0;

    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LoadMoreVisibility => CanLoadMore ? Visibility.Visible : Visibility.Collapsed;

    public string EmptyMessage => AppText.Get(_emptyMessageKey);

    public string ExplorerTitle => AppText.Get("ExplorerTitle");

    public string ExplorerSubtitle => AppText.Get("ExplorerSubtitle");

    public string ExplorerDiscoveryNoticeTitle => AppText.Get("ExplorerDiscoveryNoticeTitle");

    public string ExplorerDiscoveryNoticeMessage => AppText.Get("ExplorerDiscoveryNoticeMessage");

    public string ExplorerSearchPlaceholder => AppText.Get("ExplorerSearchPlaceholder");

    public string ExplorerSitePickerPlaceholder => AppText.Get("ExplorerSitePickerPlaceholder");

    public string ExplorerLibraryPickerPlaceholder => AppText.Get("ExplorerLibraryPickerPlaceholder");

    public string ExplorerActionBack => AppText.Get("ExplorerActionBack");

    public string ExplorerActionRefresh => AppText.Get("ExplorerActionRefresh");

    public string ExplorerActionPinCurrent => AppText.Get("ExplorerActionPinCurrent");

    public string ExplorerActionManualUrl => AppText.Get("ExplorerActionManualUrl");

    public string ExplorerActionSignIn => AppText.Get("ExplorerActionSignIn");

    public string ExplorerActionLoadMore => AppText.Get("ExplorerActionLoadMore");

    public string ExplorerErrorTitle => AppText.Get("ExplorerErrorTitle");

    public string ExplorerAuthTitle => AppText.Get("ExplorerAuthTitle");

    public string ExplorerAuthMessage => AppText.Get("ExplorerAuthMessage");

    public string ExplorerEmptyTitle => AppText.Get("ExplorerEmptyTitle");

    public string ExplorerItemsAutomationName => AppText.Get("ExplorerItemsAutomationName");

    public string ExplorerBreadcrumbAutomationName => AppText.Get("ExplorerBreadcrumbAutomationName");

    public string ExplorerBusyAutomationName => AppText.Get("ExplorerBusyAutomationName");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await ExecuteLatestAsync(
            async token =>
            {
                await DiscoverSitesCoreAsync(SearchQuery, token);
                var initialSite = Sites.FirstOrDefault(site => site.IsFollowed) ?? Sites.FirstOrDefault();
                if (initialSite is not null)
                {
                    await SelectSiteCoreAsync(initialSite, token);
                }
            },
            cancellationToken);
        IsInitialized = true;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return ExecuteLatestAsync(
            async token =>
            {
                if (SelectedLibrary is not null && Breadcrumbs.LastOrDefault() is { } current)
                {
                    await LoadFolderCoreAsync(current.ItemId, nextLink: null, replaceItems: true, token);
                    return;
                }

                var selectedSiteId = SelectedSite?.Id;
                await DiscoverSitesCoreAsync(SearchQuery, token);
                var site = Sites.FirstOrDefault(candidate => candidate.Id == selectedSiteId) ??
                           Sites.FirstOrDefault(candidate => candidate.IsFollowed) ??
                           Sites.FirstOrDefault();
                if (site is not null)
                {
                    await SelectSiteCoreAsync(site, token);
                }
            },
            cancellationToken);
    }

    public Task SearchAsync(string? query, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        SearchQuery = query?.Trim() ?? string.Empty;
        return ExecuteLatestAsync(
            async token =>
            {
                await DiscoverSitesCoreAsync(SearchQuery, token);
                var first = Sites.FirstOrDefault(site => site.IsFollowed) ?? Sites.FirstOrDefault();
                if (first is not null)
                {
                    await SelectSiteCoreAsync(first, token);
                }
            },
            cancellationToken);
    }

    public Task SelectSiteAsync(
        SharePointSiteInfo? site,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (site is null)
        {
            ResetSiteSelection();
            return Task.CompletedTask;
        }

        if (SelectedSite?.Id == site.Id)
        {
            return Task.CompletedTask;
        }

        return ExecuteLatestAsync(token => SelectSiteCoreAsync(site, token), cancellationToken);
    }

    public Task SelectLibraryAsync(
        SharePointLibraryInfo? library,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (library is null)
        {
            ResetLibrarySelection();
            return Task.CompletedTask;
        }

        if (SelectedLibrary?.Id == library.Id)
        {
            return Task.CompletedTask;
        }

        return ExecuteLatestAsync(token => SelectLibraryCoreAsync(library, token), cancellationToken);
    }

    public Task OpenFolderAsync(
        SharePointExplorerItem item,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsFolder || SelectedLibrary is null)
        {
            return Task.CompletedTask;
        }

        return ExecuteLatestAsync(
            async token =>
            {
                if (!await LoadFolderCoreAsync(item.Id, nextLink: null, replaceItems: true, token))
                {
                    return;
                }

                var displayPath = BuildDisplayPath(item.Name);
                Breadcrumbs.Add(new SharePointExplorerBreadcrumb(item.Id, item.Name, item.WebUrl, displayPath));
                RefreshNavigationState();
            },
            cancellationToken);
    }

    public Task NavigateBackAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Breadcrumbs.Count < 2
            ? Task.CompletedTask
            : NavigateToBreadcrumbAsync(Breadcrumbs[^2], cancellationToken);
    }

    public Task NavigateToBreadcrumbAsync(
        SharePointExplorerBreadcrumb breadcrumb,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(breadcrumb);
        var targetIndex = Breadcrumbs.IndexOf(breadcrumb);
        if (targetIndex < 0 || targetIndex == Breadcrumbs.Count - 1)
        {
            return Task.CompletedTask;
        }

        return ExecuteLatestAsync(
            async token =>
            {
                if (!await LoadFolderCoreAsync(breadcrumb.ItemId, nextLink: null, replaceItems: true, token))
                {
                    return;
                }

                while (Breadcrumbs.Count > targetIndex + 1)
                {
                    Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
                }

                RefreshNavigationState();
            },
            cancellationToken);
    }

    public Task LoadMoreAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!CanLoadMore || Breadcrumbs.LastOrDefault() is not { } current)
        {
            return Task.CompletedTask;
        }

        var nextLink = _nextLink;
        return ExecuteLatestAsync(
            token => LoadFolderCoreAsync(current.ItemId, nextLink, replaceItems: false, token),
            cancellationToken);
    }

    public bool TryBuildPinnedFolder(out SharePointPinnedFolder pinnedFolder)
    {
        if (SelectedSite is null ||
            SelectedLibrary is null ||
            Breadcrumbs.LastOrDefault() is not { } current)
        {
            pinnedFolder = default!;
            return false;
        }

        pinnedFolder = new SharePointPinnedFolder(
            SelectedSite.Id,
            SelectedLibrary.Id,
            current.ItemId,
            current.DisplayName,
            SelectedSite.WebUrl,
            current.WebUrl,
            current.DisplayPath);
        return true;
    }

    public Task<SharePointPinnedFolder> ResolveFolderAsync(
        SharePointRouteInput routeInput,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(routeInput);
        return _service.ResolveFolderAsync(routeInput, cancellationToken);
    }

    public void ReportExternalError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ApplyError(exception);
    }

    public void ResetForAuthenticationChange(bool requiresAuthentication)
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _operationVersion);
        lock (_operationSync)
        {
            _activeOperation?.Cancel();
            _activeOperation = null;
        }

        _service.ClearCache();
        IsBusy = false;
        SearchQuery = string.Empty;
        Sites.Clear();
        ResetSiteSelection();
        ClearErrorState();
        RequiresAuthentication = requiresAuthentication;
        SetEmptyMessage("ExplorerEmptyInitial");
        IsInitialized = true;
        RefreshCollectionState();
        RefreshNavigationState();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppText.LanguageChanged -= AppText_LanguageChanged;
        lock (_operationSync)
        {
            _activeOperation?.Cancel();
            _activeOperation = null;
        }
    }

    private async Task DiscoverSitesCoreAsync(string? query, CancellationToken cancellationToken)
    {
        var sites = await _service
            .DiscoverSitesAsync(string.IsNullOrWhiteSpace(query) ? null : query, cancellationToken);

        Sites.Clear();
        foreach (var site in sites.OrderByDescending(site => site.IsFollowed)
                     .ThenBy(site => site.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            Sites.Add(site);
        }

        ResetSiteSelection();
        SetEmptyMessage(Sites.Count == 0 ? "ExplorerEmptySites" : "ExplorerEmptyLibraries");
        RefreshCollectionState();
    }

    private async Task SelectSiteCoreAsync(SharePointSiteInfo site, CancellationToken cancellationToken)
    {
        SelectedSite = Sites.FirstOrDefault(candidate => candidate.Id == site.Id) ?? site;
        SelectedLibrary = null;
        Libraries.Clear();
        ResetFolderState();

        var libraries = await _service
            .GetLibrariesAsync(SelectedSite.Id, cancellationToken);
        foreach (var library in libraries.OrderBy(library => library.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Libraries.Add(library);
        }

        SetEmptyMessage(Libraries.Count == 0 ? "ExplorerEmptyLibraries" : "ExplorerEmptyItems");
        RefreshCollectionState();

        if (Libraries.FirstOrDefault() is { } firstLibrary)
        {
            await SelectLibraryCoreAsync(firstLibrary, cancellationToken);
        }
    }

    private async Task SelectLibraryCoreAsync(
        SharePointLibraryInfo library,
        CancellationToken cancellationToken)
    {
        SelectedLibrary = Libraries.FirstOrDefault(candidate => candidate.Id == library.Id) ?? library;
        ResetFolderState();

        if (!await LoadFolderCoreAsync(
                SelectedLibrary.RootItemId,
                nextLink: null,
                replaceItems: true,
                cancellationToken))
        {
            return;
        }

        var rootPath = BuildDisplayPath();
        Breadcrumbs.Add(new SharePointExplorerBreadcrumb(
            SelectedLibrary.RootItemId,
            SelectedLibrary.Name,
            SelectedLibrary.WebUrl,
            rootPath));
        RefreshNavigationState();
    }

    private async Task<bool> LoadFolderCoreAsync(
        string itemId,
        string? nextLink,
        bool replaceItems,
        CancellationToken cancellationToken)
    {
        if (SelectedLibrary is null || string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        var page = await _service
            .GetChildrenAsync(SelectedLibrary.Id, itemId, nextLink, cancellationToken);

        if (replaceItems)
        {
            Items.Clear();
        }

        var existing = Items
            .Select(item => $"{item.DriveId}\u001f{item.Id}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in page.Items
                     .OrderByDescending(item => item.IsFolder)
                     .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (existing.Add($"{item.DriveId}\u001f{item.Id}"))
            {
                Items.Add(item);
            }
        }

        _nextLink = string.IsNullOrWhiteSpace(page.NextLink) ? null : page.NextLink;
        SetEmptyMessage("ExplorerEmptyItems");
        RefreshCollectionState();
        return true;
    }

    private async Task ExecuteLatestAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        var operationVersion = Interlocked.Increment(ref _operationVersion);
        var operationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;

        lock (_operationSync)
        {
            previous = _activeOperation;
            _activeOperation = operationSource;
        }

        previous?.Cancel();
        ClearErrorState();
        IsBusy = true;

        try
        {
            await operation(operationSource.Token);
        }
        catch (OperationCanceledException) when (
            operationSource.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            // A newer UI action superseded this request.
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (operationVersion == Volatile.Read(ref _operationVersion))
            {
                ApplyError(exception);
            }
        }
        finally
        {
            lock (_operationSync)
            {
                if (ReferenceEquals(_activeOperation, operationSource))
                {
                    _activeOperation = null;
                }
            }

            if (operationVersion == Volatile.Read(ref _operationVersion))
            {
                IsBusy = false;
            }

            operationSource.Dispose();
        }
    }

    private void ResetSiteSelection()
    {
        SelectedSite = null;
        Libraries.Clear();
        ResetLibrarySelection();
        RefreshCollectionState();
    }

    private void ResetLibrarySelection()
    {
        SelectedLibrary = null;
        ResetFolderState();
        RefreshCollectionState();
    }

    private void ResetFolderState()
    {
        _nextLink = null;
        Breadcrumbs.Clear();
        Items.Clear();
        RefreshNavigationState();
        RefreshCollectionState();
    }

    private void ClearErrorState()
    {
        HasError = false;
        RequiresAuthentication = false;
        _isForbiddenError = false;
        ErrorMessage = string.Empty;
    }

    private void ApplyError(Exception exception)
    {
        if (exception is SharePointExplorerException { Status: SharePointExplorerStatus.Forbidden })
        {
            Sites.Clear();
            ResetSiteSelection();
            RequiresAuthentication = false;
            _isForbiddenError = true;
            HasError = true;
            ErrorMessage = AppText.Get("ExplorerForbiddenMessage");
            return;
        }

        if (IsAuthenticationError(exception))
        {
            Sites.Clear();
            ResetSiteSelection();
            RequiresAuthentication = true;
            HasError = false;
            ErrorMessage = string.Empty;
            return;
        }

        RequiresAuthentication = false;
        _isForbiddenError = false;
        HasError = true;
        ErrorMessage = AppText.Get("ExplorerErrorGeneric");
    }

    private static bool IsAuthenticationError(Exception exception)
    {
        if (exception is SharePointExplorerException
            {
                Status: SharePointExplorerStatus.AuthenticationRequired
            } ||
            exception is UnauthorizedAccessException ||
            exception is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
        {
            return true;
        }

        var typeName = exception.GetType().Name;
        return typeName.Contains("Authentication", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Authorization", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildDisplayPath(string? childName = null)
    {
        var segments = new List<string>();
        if (SelectedLibrary is not null)
        {
            segments.Add(SelectedLibrary.Name);
        }

        segments.AddRange(Breadcrumbs.Skip(1).Select(item => item.DisplayName));
        if (!string.IsNullOrWhiteSpace(childName))
        {
            segments.Add(childName);
        }

        return segments.Count == 0 ? "/" : $"/{string.Join('/', segments)}";
    }

    private void SetEmptyMessage(string resourceKey)
    {
        if (string.Equals(_emptyMessageKey, resourceKey, StringComparison.Ordinal))
        {
            return;
        }

        _emptyMessageKey = resourceKey;
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private void RefreshCollectionState()
    {
        OnPropertyChanged(nameof(CanSelectSite));
        OnPropertyChanged(nameof(CanSelectLibrary));
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(LoadMoreVisibility));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void RefreshNavigationState()
    {
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanPinCurrentFolder));
    }

    private void RefreshCommandState()
    {
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(LoadMoreVisibility));
        OnPropertyChanged(nameof(CanPinCurrentFolder));
        OnPropertyChanged(nameof(CanSelectSite));
        OnPropertyChanged(nameof(CanSelectLibrary));
    }

    private void AppText_LanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(ExplorerTitle));
        OnPropertyChanged(nameof(ExplorerSubtitle));
        OnPropertyChanged(nameof(ExplorerDiscoveryNoticeTitle));
        OnPropertyChanged(nameof(ExplorerDiscoveryNoticeMessage));
        OnPropertyChanged(nameof(ExplorerSearchPlaceholder));
        OnPropertyChanged(nameof(ExplorerSitePickerPlaceholder));
        OnPropertyChanged(nameof(ExplorerLibraryPickerPlaceholder));
        OnPropertyChanged(nameof(ExplorerActionBack));
        OnPropertyChanged(nameof(ExplorerActionRefresh));
        OnPropertyChanged(nameof(ExplorerActionPinCurrent));
        OnPropertyChanged(nameof(ExplorerActionManualUrl));
        OnPropertyChanged(nameof(ExplorerActionSignIn));
        OnPropertyChanged(nameof(ExplorerActionLoadMore));
        OnPropertyChanged(nameof(ExplorerErrorTitle));
        OnPropertyChanged(nameof(ExplorerAuthTitle));
        OnPropertyChanged(nameof(ExplorerAuthMessage));
        OnPropertyChanged(nameof(ExplorerEmptyTitle));
        OnPropertyChanged(nameof(ExplorerItemsAutomationName));
        OnPropertyChanged(nameof(ExplorerBreadcrumbAutomationName));
        OnPropertyChanged(nameof(ExplorerBusyAutomationName));
        if (HasError)
        {
            ErrorMessage = AppText.Get(_isForbiddenError ? "ExplorerForbiddenMessage" : "ExplorerErrorGeneric");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
