using System.Collections.ObjectModel;
using System.Text;
using EasyShare.Models;
using EasyShare.Resources;
using EasyShare.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AuthenticationModeModel = EasyShare.Models.AuthenticationMode;

namespace EasyShare.ViewModels;

public sealed class ContentAssistantViewModel : ObservableObject, IDisposable
{
    private const int MaximumIndexedItems = 5000;
    private const int MaximumFolderDepth = 32;
    private const int SearchResultLimit = 100;
    private const int HistoryResultLimit = 30;
    private readonly ContentIndexService _indexService;
    private readonly SharePointBrowserContentService _contentService;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _indexCancellationSync = new();
    private IReadOnlyList<DriveRoute> _routes = [];
    private CancellationTokenSource? _indexCancellation;
    private string _scopeKey = string.Empty;
    private string _searchQuery = string.Empty;
    private string _statusTitleKey = "AssistantStatusReadyTitle";
    private string _statusMessageKey = "AssistantStatusReadyMessage";
    private object[] _statusMessageArguments = [];
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private AuthenticationModeModel _authenticationMode;
    private bool _isConfigured;
    private bool _isBusy;
    private bool _isIndexing;
    private bool _isShowingMostAccessed = true;
    private int _busyDepth;
    private int _configurationVersion;
    private bool _disposed;

    public ContentAssistantViewModel(
        ContentIndexService indexService,
        SharePointBrowserContentService contentService)
    {
        _indexService = indexService ?? throw new ArgumentNullException(nameof(indexService));
        _contentService = contentService ?? throw new ArgumentNullException(nameof(contentService));
        AppText.LanguageChanged += AppText_LanguageChanged;
    }

    public ContentAssistantViewModel(
        ContentIndexService indexService,
        SharePointBrowserContentService contentService,
        string scopeKey,
        AuthenticationModeModel authenticationMode,
        IEnumerable<DriveRoute> routes)
        : this(indexService, contentService)
    {
        Configure(scopeKey, authenticationMode, routes);
    }

    public ObservableCollection<ContentSearchResult> Results { get; } = [];

    public ObservableCollection<ContentSearchResult> MostAccessed { get; } = [];

    public string ScopeKey => _scopeKey;

    public AuthenticationModeModel AuthenticationMode => _authenticationMode;

    public IReadOnlyList<DriveRoute> Routes => _routes;

    public bool IsConfigured => _isConfigured;

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
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            RefreshCommandState();
            OnPropertyChanged(nameof(BusyVisibility));
            OnPropertyChanged(nameof(EmptyVisibility));
        }
    }

    public bool IsIndexing
    {
        get => _isIndexing;
        private set
        {
            if (!SetProperty(ref _isIndexing, value))
            {
                return;
            }

            RefreshCommandState();
        }
    }

    public bool CanSearch => IsConfigured && !IsBusy && !IsIndexing;

    public bool CanIndex => IsConfigured && !IsBusy && !IsIndexing;

    public bool CanCancel => IsIndexing;

    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyVisibility => !IsBusy && Results.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string Title => AppText.Get("AssistantTitle");

    public string Subtitle => AppText.Get("AssistantSubtitle");

    public string SearchPlaceholder => AppText.Get("AssistantSearchPlaceholder");

    public string IndexAction => AppText.Get("AssistantActionIndex");

    public string CancelAction => AppText.Get("AssistantActionCancel");

    public string ResultsTitle => AppText.Get(
        _isShowingMostAccessed ? "AssistantMostAccessedTitle" : "AssistantSearchResultsTitle");

    public string EmptyMessage => AppText.Get(
        _isShowingMostAccessed ? "AssistantEmptyHistory" : "AssistantEmptySearch");

    public string ResultsAutomationName => AppText.Get("AssistantResultsAutomationName");

    public string OpenResultHelp => AppText.Get("AssistantOpenResultHelp");

    public string IndexProgressAutomationName => AppText.Get("AssistantIndexProgressAutomationName");

    public bool IsStatusOpen => true;

    public string StatusTitle => AppText.Get(_statusTitleKey);

    public string StatusMessage => _statusMessageArguments.Length == 0
        ? AppText.Get(_statusMessageKey)
        : AppText.Format(_statusMessageKey, _statusMessageArguments);

    public InfoBarSeverity StatusSeverity
    {
        get => _statusSeverity;
        private set => SetProperty(ref _statusSeverity, value);
    }

    public void Configure(
        string scopeKey,
        AuthenticationModeModel authenticationMode,
        IEnumerable<DriveRoute> routes)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(scopeKey))
        {
            throw new ArgumentException("Scope key is required.", nameof(scopeKey));
        }

        ArgumentNullException.ThrowIfNull(routes);
        CancelIndexing();
        Interlocked.Increment(ref _configurationVersion);

        _scopeKey = scopeKey.Trim();
        _authenticationMode = authenticationMode;
        _routes = routes
            .Where(route => route is not null && route.Id != Guid.Empty)
            .GroupBy(route => route.Id)
            .Select(group => CloneRoute(group.First(), preserveGraphIdentity: true))
            .ToArray();
        _isConfigured = true;
        _isShowingMostAccessed = true;
        SearchQuery = string.Empty;
        Results.Clear();
        MostAccessed.Clear();
        SetStatus(
            "AssistantStatusReadyTitle",
            "AssistantStatusReadyMessage",
            InfoBarSeverity.Informational);

        OnPropertyChanged(nameof(ScopeKey));
        OnPropertyChanged(nameof(AuthenticationMode));
        OnPropertyChanged(nameof(Routes));
        OnPropertyChanged(nameof(IsConfigured));
        RefreshCommandState();
        RefreshResultState();
    }

    public async Task SearchAsync(
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConfigured();
        if (query is not null)
        {
            SearchQuery = query.Trim();
        }

        var configurationVersion = Volatile.Read(ref _configurationVersion);
        var lockTaken = false;
        try
        {
            await _operationGate.WaitAsync(cancellationToken);
            lockTaken = true;
            BeginBusy();

            if (string.IsNullOrWhiteSpace(SearchQuery))
            {
                await RefreshHistoryCoreAsync(configurationVersion, updateResults: true, cancellationToken);
                SetStatus(
                    "AssistantStatusReadyTitle",
                    "AssistantStatusReadyMessage",
                    InfoBarSeverity.Informational);
                return;
            }

            SetStatus(
                "AssistantStatusSearchingTitle",
                "AssistantStatusSearchingMessage",
                InfoBarSeverity.Informational);
            var results = await _indexService.SearchAsync(
                _scopeKey,
                SearchQuery,
                SearchResultLimit,
                cancellationToken: cancellationToken);
            if (!IsCurrentConfiguration(configurationVersion))
            {
                return;
            }

            _isShowingMostAccessed = false;
            ReplaceCollection(Results, results);
            SetStatus(
                "AssistantStatusReadyTitle",
                "AssistantStatusReadyMessage",
                InfoBarSeverity.Informational);
            RefreshResultState();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus(
                "AssistantStatusCancelledTitle",
                "AssistantStatusCancelledMessage",
                InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            ReportExternalError(exception);
        }
        finally
        {
            if (lockTaken)
            {
                EndBusy();
                _operationGate.Release();
            }
        }
    }

    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConfigured();
        var configurationVersion = Volatile.Read(ref _configurationVersion);
        var lockTaken = false;
        try
        {
            await _operationGate.WaitAsync(cancellationToken);
            lockTaken = true;
            BeginBusy();
            await RefreshHistoryCoreAsync(configurationVersion, updateResults: true, cancellationToken);
            SetStatus(
                "AssistantStatusReadyTitle",
                "AssistantStatusReadyMessage",
                InfoBarSeverity.Informational);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus(
                "AssistantStatusCancelledTitle",
                "AssistantStatusCancelledMessage",
                InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            ReportExternalError(exception);
        }
        finally
        {
            if (lockTaken)
            {
                EndBusy();
                _operationGate.Release();
            }
        }
    }

    public async Task IndexAllAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConfigured();

        CancellationTokenSource indexCancellation;
        lock (_indexCancellationSync)
        {
            if (_indexCancellation is not null)
            {
                return;
            }

            indexCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _indexCancellation = indexCancellation;
        }

        var operationToken = indexCancellation.Token;
        var configurationVersion = Volatile.Read(ref _configurationVersion);
        var scopeKey = _scopeKey;
        var mode = _authenticationMode;
        var routes = _routes
            .Select(route => CloneRoute(
                route,
                preserveGraphIdentity: mode != AuthenticationModeModel.BrowserSession))
            .ToArray();
        var lockTaken = false;
        IsIndexing = true;
        BeginBusy();

        try
        {
            await _operationGate.WaitAsync(operationToken);
            lockTaken = true;
            SetStatus(
                "AssistantStatusIndexingTitle",
                "AssistantStatusIndexingMessageFormat",
                InfoBarSeverity.Informational,
                0);

            var crawlResults = await CrawlRoutesAsync(routes, operationToken);
            var indexedItemCount = 0;
            foreach (var crawlResult in crawlResults)
            {
                operationToken.ThrowIfCancellationRequested();
                indexedItemCount += crawlResult.Items.Count;
                if (crawlResult.IsComplete)
                {
                    await _indexService.ReplaceRouteItemsAsync(
                        scopeKey,
                        crawlResult.RouteId,
                        crawlResult.Items,
                        operationToken);
                }
                else
                {
                    await _indexService.UpsertItemsAsync(
                        scopeKey,
                        crawlResult.Items,
                        operationToken);
                }
            }

            operationToken.ThrowIfCancellationRequested();

            if (IsCurrentConfiguration(configurationVersion))
            {
                if (string.IsNullOrWhiteSpace(SearchQuery))
                {
                    await RefreshHistoryCoreAsync(
                        configurationVersion,
                        updateResults: true,
                        operationToken);
                }
                else
                {
                    var refreshedResults = await _indexService.SearchAsync(
                        scopeKey,
                        SearchQuery,
                        SearchResultLimit,
                        cancellationToken: operationToken);
                    if (IsCurrentConfiguration(configurationVersion))
                    {
                        _isShowingMostAccessed = false;
                        ReplaceCollection(Results, refreshedResults);
                        RefreshResultState();
                    }
                }

                var wasTruncated = crawlResults.Any(result => !result.IsComplete);
                SetStatus(
                    wasTruncated ? "AssistantStatusPartialTitle" : "AssistantStatusIndexedTitle",
                    wasTruncated
                        ? "AssistantStatusPartialMessageFormat"
                        : "AssistantStatusIndexedMessageFormat",
                    wasTruncated ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                    indexedItemCount);
            }
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            if (IsCurrentConfiguration(configurationVersion))
            {
                SetStatus(
                    "AssistantStatusCancelledTitle",
                    "AssistantStatusCancelledMessage",
                    InfoBarSeverity.Informational);
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentConfiguration(configurationVersion))
            {
                ReportExternalError(exception);
            }
        }
        finally
        {
            if (lockTaken)
            {
                _operationGate.Release();
            }

            lock (_indexCancellationSync)
            {
                if (ReferenceEquals(_indexCancellation, indexCancellation))
                {
                    _indexCancellation = null;
                }
            }

            indexCancellation.Dispose();
            IsIndexing = false;
            EndBusy();
        }
    }

    public void CancelIndexing()
    {
        lock (_indexCancellationSync)
        {
            _indexCancellation?.Cancel();
        }
    }

    public async Task RecordAccessAsync(
        ContentSearchResult result,
        ContentAccessKind? accessKind = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConfigured();
        ArgumentNullException.ThrowIfNull(result);
        var configurationVersion = Volatile.Read(ref _configurationVersion);
        var effectiveKind = accessKind ??
            (result.IsDirectory ? ContentAccessKind.FolderOpened : ContentAccessKind.FileOpened);
        var lockTaken = false;
        try
        {
            await _operationGate.WaitAsync(cancellationToken);
            lockTaken = true;
            await _indexService.RecordAccessAsync(
                _scopeKey,
                result.RouteId,
                result.RelativePath,
                effectiveKind,
                cancellationToken);
            await RefreshHistoryCoreAsync(
                configurationVersion,
                updateResults: _isShowingMostAccessed,
                cancellationToken);
        }
        finally
        {
            if (lockTaken)
            {
                _operationGate.Release();
            }
        }
    }

    public void ReportExternalError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        StartupDiagnostics.Write("Content assistant operation failed.", exception);
        SetStatus(
            "AssistantStatusErrorTitle",
            "AssistantStatusErrorMessage",
            InfoBarSeverity.Error);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppText.LanguageChanged -= AppText_LanguageChanged;
        CancelIndexing();
    }

    private async Task<IReadOnlyList<RouteCrawlResult>> CrawlRoutesAsync(
        IReadOnlyList<DriveRoute> routes,
        CancellationToken cancellationToken)
    {
        var eligibleRoutes = routes
            .Where(route => !string.IsNullOrWhiteSpace(route.SharePointUrl))
            .ToArray();
        if (eligibleRoutes.Length == 0)
        {
            return [];
        }

        var results = new List<RouteCrawlResult>(eligibleRoutes.Length);
        var baseBudget = MaximumIndexedItems / eligibleRoutes.Length;
        var extraBudget = MaximumIndexedItems % eligibleRoutes.Length;
        var progress = 0;
        for (var index = 0; index < eligibleRoutes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var budget = baseBudget + (index < extraBudget ? 1 : 0);
            var result = await CrawlRouteAsync(
                eligibleRoutes[index],
                budget,
                progress,
                cancellationToken);
            results.Add(result);
            progress += result.Items.Count;
        }

        return results;
    }

    private async Task<RouteCrawlResult> CrawlRouteAsync(
        DriveRoute route,
        int itemBudget,
        int progressOffset,
        CancellationToken cancellationToken)
    {
        var pending = new Queue<CrawlNode>();
        pending.Enqueue(new CrawlNode(route, string.Empty, 0));
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexedItems = new List<ContentIndexItem>(Math.Min(Math.Max(0, itemBudget), 512));
        var isComplete = itemBudget > 0;
        if (itemBudget > 0)
        {
            indexedPaths.Add(BuildIdentityKey(route.Id, string.Empty));
            indexedItems.Add(new ContentIndexItem(
                route.Id,
                route.DisplayName,
                string.Empty,
                route.DisplayName,
                IsDirectory: true,
                RemoteLocator: BuildRemoteLocator(route, string.Empty, serverRelativeUrl: null)));
        }

        while (pending.Count > 0 && indexedItems.Count < itemBudget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Dequeue();
            if (current.Depth >= MaximumFolderDepth)
            {
                isComplete = false;
                continue;
            }

            var directoryKey = BuildIdentityKey(current.Route.Id, current.RelativePath);
            if (!visitedDirectories.Add(directoryKey))
            {
                continue;
            }

            var children = await _contentService.ListDirectoryForExplorerAsync(
                current.Route,
                current.RelativePath,
                cancellationToken,
                queueObservation: false,
                recordUserAccess: false);
            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (indexedItems.Count >= itemBudget)
                {
                    isComplete = false;
                    break;
                }

                if (!TryAppendPath(current.RelativePath, child.Name, out var relativePath))
                {
                    continue;
                }

                var itemKey = BuildIdentityKey(current.Route.Id, relativePath);
                if (!indexedPaths.Add(itemKey))
                {
                    continue;
                }

                indexedItems.Add(new ContentIndexItem(
                    current.Route.Id,
                    current.Route.DisplayName,
                    relativePath,
                    child.Name,
                    child.IsDirectory,
                    child.Length,
                    child.ModifiedAt,
                    BuildRemoteLocator(current.Route, relativePath, child.ServerRelativeUrl)));

                var childDepth = current.Depth + 1;
                if (child.IsDirectory && childDepth < MaximumFolderDepth)
                {
                    pending.Enqueue(new CrawlNode(current.Route, relativePath, childDepth));
                }
                else if (child.IsDirectory)
                {
                    isComplete = false;
                }

                if ((progressOffset + indexedItems.Count) % 100 == 0)
                {
                    SetStatus(
                        "AssistantStatusIndexingTitle",
                        "AssistantStatusIndexingMessageFormat",
                        InfoBarSeverity.Informational,
                        progressOffset + indexedItems.Count);
                }
            }
        }

        if (pending.Count > 0)
        {
            isComplete = false;
        }

        return new RouteCrawlResult(route.Id, indexedItems, isComplete);
    }

    private async Task RefreshHistoryCoreAsync(
        int configurationVersion,
        bool updateResults,
        CancellationToken cancellationToken)
    {
        var history = await _indexService.GetMostAccessedAsync(
            _scopeKey,
            HistoryResultLimit,
            cancellationToken: cancellationToken);
        if (!IsCurrentConfiguration(configurationVersion))
        {
            return;
        }

        ReplaceCollection(MostAccessed, history);
        if (updateResults)
        {
            _isShowingMostAccessed = true;
            ReplaceCollection(Results, history);
        }

        RefreshResultState();
    }

    private void SetStatus(
        string titleKey,
        string messageKey,
        InfoBarSeverity severity,
        params object[] messageArguments)
    {
        _statusTitleKey = titleKey;
        _statusMessageKey = messageKey;
        _statusMessageArguments = messageArguments;
        StatusSeverity = severity;
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(IsStatusOpen));
    }

    private void RefreshCommandState()
    {
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(CanIndex));
        OnPropertyChanged(nameof(CanCancel));
    }

    private void BeginBusy()
    {
        if (Interlocked.Increment(ref _busyDepth) == 1)
        {
            IsBusy = true;
        }
    }

    private void EndBusy()
    {
        var depth = Interlocked.Decrement(ref _busyDepth);
        if (depth <= 0)
        {
            Interlocked.Exchange(ref _busyDepth, 0);
            IsBusy = false;
        }
    }

    private void RefreshResultState()
    {
        OnPropertyChanged(nameof(ResultsTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(EmptyVisibility));
    }

    private void AppText_LanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(IndexAction));
        OnPropertyChanged(nameof(CancelAction));
        OnPropertyChanged(nameof(ResultsTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(ResultsAutomationName));
        OnPropertyChanged(nameof(OpenResultHelp));
        OnPropertyChanged(nameof(IndexProgressAutomationName));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusMessage));
    }

    private bool IsCurrentConfiguration(int version) =>
        version == Volatile.Read(ref _configurationVersion);

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Configure the content assistant before using it.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private static bool TryAppendPath(string parentPath, string childName, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(childName) ||
            childName is "." or ".." ||
            childName.IndexOfAny(['/', '\\']) >= 0 ||
            childName.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            var normalizedParent = (parentPath ?? string.Empty).Replace('\\', '/').Trim('/');
            var normalizedChild = childName.Normalize(NormalizationForm.FormC);
            relativePath = string.IsNullOrWhiteSpace(normalizedParent)
                ? normalizedChild
                : $"{normalizedParent}/{normalizedChild}";
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string BuildIdentityKey(Guid routeId, string relativePath) =>
        $"{routeId:N}\u001f{relativePath.Replace('\\', '/').Trim('/')}";

    private static string? BuildRemoteLocator(
        DriveRoute route,
        string relativePath,
        string? serverRelativeUrl)
    {
        if (!string.IsNullOrWhiteSpace(serverRelativeUrl) &&
            (serverRelativeUrl.StartsWith("/", StringComparison.Ordinal) ||
             Uri.TryCreate(serverRelativeUrl, UriKind.Absolute, out var remoteUri) &&
             remoteUri.Scheme is "http" or "https"))
        {
            return serverRelativeUrl;
        }

        if (string.IsNullOrWhiteSpace(route.SharePointUrl))
        {
            return string.IsNullOrWhiteSpace(serverRelativeUrl) ? null : serverRelativeUrl;
        }

        var routePath = route.RemotePath?.Trim('/') ?? string.Empty;
        var combinedPath = string.IsNullOrWhiteSpace(routePath)
            ? relativePath
            : $"{routePath}/{relativePath}";
        return SharePointRouteParser.BuildDisplayUrl(route.SharePointUrl, combinedPath);
    }

    private static DriveRoute CloneRoute(DriveRoute route, bool preserveGraphIdentity) =>
        new()
        {
            Id = route.Id,
            DisplayName = route.DisplayName,
            SharePointUrl = route.SharePointUrl,
            RemotePath = route.RemotePath,
            SiteId = preserveGraphIdentity ? route.SiteId : string.Empty,
            DriveId = preserveGraphIdentity ? route.DriveId : string.Empty,
            RootItemId = preserveGraphIdentity ? route.RootItemId : string.Empty,
            FolderWebUrl = route.FolderWebUrl,
            IsConnected = route.IsConnected,
            StatusText = route.StatusText,
            LastCheckedAt = route.LastCheckedAt
        };

    private sealed record CrawlNode(DriveRoute Route, string RelativePath, int Depth);

    private sealed record RouteCrawlResult(
        Guid RouteId,
        IReadOnlyList<ContentIndexItem> Items,
        bool IsComplete);
}
