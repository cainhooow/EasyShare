using EasyShare.Models;
using EasyShare.Resources;
using EasyShare.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using WinRT.Interop;

namespace EasyShare;

public sealed partial class MainPage
{
    private readonly HashSet<string> _approvedFederatedBrowserHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _browserSessionVerificationGate = new(1, 1);
    private CancellationTokenSource? _browserNavigationVerificationCancellation;
    private int _browserContentNavigationPending;
    private int _browserAuthenticationTransitionInProgress;
    private int _browserSessionClearInProgress;
    private bool _allowBrowserContentNavigationOnce;
    private bool _browserSecurityConfigured;

    private async void BrowserKeepAliveTimer_Tick(object? sender, object e)
    {
        if (!ViewModel.IsBrowserSessionMode || !ViewModel.BrowserKeepSessionAlive)
        {
            return;
        }

        try
        {
            await VerifyBrowserSessionAsync(showMessage: false);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Browser keep-alive failed.", exception);
        }
    }

    private async void BrowserGoButton_Click(object sender, RoutedEventArgs e) =>
        await RunWithLoadingAsync(
            () => NavigateBrowserAsync(BrowserAddressBox.Text),
            "LoadingBrowserTitle",
            "LoadingBrowserMessage");

    private void BrowserBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (SessionWebView.CanGoBack)
        {
            SessionWebView.GoBack();
        }
    }

    private void BrowserForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (SessionWebView.CanGoForward)
        {
            SessionWebView.GoForward();
        }
    }

    private void BrowserRefreshButton_Click(object sender, RoutedEventArgs e) => SessionWebView.Reload();

    private async void TrimBrowserCacheButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWithLoadingAsync(
            TrimBrowserCacheAsync,
            "LoadingSaveTitle",
            "LoadingSaveMessage");
        ShowActionMessage(
            AppText.Get("WebViewCacheClearedTitle"),
            AppText.Get("WebViewCacheClearedMessage"),
            InfoBarSeverity.Success);
    }

    private async void PinCurrentFolderButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWithLoadingAsync(
            () => EnsureBrowserInitializedAsync(navigate: false),
            "LoadingBrowserTitle",
            "LoadingBrowserMessage");
        var currentUrl = SessionWebView.Source?.ToString() ?? BrowserAddressBox.Text;
        if (!SharePointRouteParser.TryParse(currentUrl, out _))
        {
            ShowActionMessage(
                AppText.Get("PinNoFolderTitle"),
                AppText.Get("PinNoFolderMessage"),
                InfoBarSeverity.Warning);
            return;
        }

        await ShowRouteEditorAsync(null, currentUrl);
    }

    private async void VerifyBrowserSessionButton_Click(object sender, RoutedEventArgs e) =>
        await RunWithLoadingAsync(
            () => VerifyBrowserSessionAsync(showMessage: true),
            "LoadingTestTitle",
            "LoadingTestMessage");

    private async void ClearBrowserSessionButton_Click(object sender, RoutedEventArgs e) =>
        await RunWithLoadingAsync(
            () => ClearBrowserSessionAsync(),
            "LoadingSaveTitle",
            "LoadingSaveMessage");

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        var status = await ViewModel.CheckForUpdatesAsync();
        NotifyUpdateReady(status);
        ShowActionMessage(ViewModel.UpdateStatusTitle, ViewModel.UpdateStatusMessage, ViewModel.UpdateStatusSeverity);
    }

    private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var ownerWindow = App.MainWindow is null
            ? IntPtr.Zero
            : WindowNative.GetWindowHandle(App.MainWindow);
        await ViewModel.DownloadUpdateAsync(ownerWindow);
        ShowActionMessage(ViewModel.UpdateStatusTitle, ViewModel.UpdateStatusMessage, ViewModel.UpdateStatusSeverity);
    }

    private async void RetryUploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid jobId })
        {
            await _uploadQueue.RetryAsync(jobId);
        }
    }

    private void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.InstallDownloadedUpdate();
        ShowActionMessage(ViewModel.UpdateStatusTitle, ViewModel.UpdateStatusMessage, ViewModel.UpdateStatusSeverity);
    }

    private void OpenUpdateReleaseButton_Click(object sender, RoutedEventArgs e) => ViewModel.OpenUpdateReleasePage();

    private async void ResetAppButton_Click(object sender, RoutedEventArgs e)
    {
        LocalDataInventory inventory;
        try
        {
            inventory = await App.Services.LocalDataReset.InventoryAsync();
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Local data inventory failed.", exception);
            ShowActionMessage(
                AppText.Get("ResetFailedTitle"),
                AppText.Format("ResetInventoryFailedMessage", exception.Message),
                InfoBarSeverity.Error);
            return;
        }

        if (!await ConfirmResetAsync(inventory))
        {
            return;
        }

        try
        {
            App.Services.LocalDataReset.MarkPendingReset();
            _browserKeepAliveTimer.Stop();
            _contentAssistantViewModel.CancelIndexing();
            App.Services.VirtualDrive.StopForReset();
            await using var queueSuspension = await _uploadQueue.SuspendForResetAsync();
            await using var offlineSuspension = await App.Services.OfflineCache.SuspendForResetAsync();

            if (ViewModel.IsBrowserSessionMode)
            {
                BeginBrowserIdentityTransition(invalidateBrowserVerification: true);
            }
            else
            {
                BeginGraphIdentityTransition();
            }

            BeginBrowserSessionClearBarrier();
            await _browserSessionVerificationGate.WaitAsync();
            var browserProfileClearedByApi = false;
            try
            {
                if (_browserInitialized)
                {
                    browserProfileClearedByApi = await _browserSessionService.ClearSessionAsync(
                        SessionWebView.CoreWebView2);
                }
                else
                {
                    _browserSessionService.ClearStoredSession();
                }
            }
            finally
            {
                _browserSessionVerificationGate.Release();
                EndBrowserSessionClearBarrier();
            }

            _approvedFederatedBrowserHosts.Clear();
            _browserContent.ClearCache();
            _browserSessionCacheScope = $"BROWSER-SESSION-{Guid.NewGuid():N}";
            _graphSessionCacheScope = $"GRAPH-SESSION-{Guid.NewGuid():N}";

            LocalDataResetResult? resetResult = null;
            await RunWithLoadingAsync(
                async () =>
                {
                    await App.Services.ContentIndex.ClearAllAsync();
                    await ViewModel.ResetAppAsync();
                    AppText.ClearStartupLanguageCode();
                    App.Services.Database.ReleasePooledConnectionsForLocalDataReset();
                    resetResult = await App.Services.LocalDataReset.ResetAsync(
                        browserProfileClearedByApi
                            ? [_browserSessionService.ProfilePath]
                            : null);
                    if (resetResult.Succeeded)
                    {
                        await App.Services.RehydrateAfterLocalDataResetAsync();
                    }
                },
                "LoadingResetTitle",
                "LoadingResetMessage");

            if (resetResult?.Succeeded != true)
            {
                var failure = resetResult?.Failures.FirstOrDefault();
                var message = failure is null
                    ? AppText.Get("ResetPartialFailureMessage")
                    : AppText.Format("ResetPartialFailureItemFormat", failure.Path, failure.Reason);
                ShowActionMessage(AppText.Get("ResetPartialFailureTitle"), message, InfoBarSeverity.Error);
                ResetAppButton.Focus(FocusState.Programmatic);
                return;
            }

            _sharePointExplorerViewModel.ResetForAuthenticationChange(requiresAuthentication: true);
            ShowActionMessage(
                AppText.Get("ResetDoneTitle"),
                AppText.Format("ResetDoneSummaryFormat", inventory.ItemCount, FormatResetBytes(inventory.Bytes)),
                InfoBarSeverity.Success);
            SelectNavigationItem("Home");
            await ShowSetupWizardIfNeededAsync();
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Deleting all local application data failed.", exception);
            ShowActionMessage(
                AppText.Get("ResetFailedTitle"),
                AppText.Format("ResetFailedMessage", exception.Message),
                InfoBarSeverity.Error);
            ResetAppButton.Focus(FocusState.Programmatic);
        }
    }

    private async void BrowserAddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await RunWithLoadingAsync(
                () => NavigateBrowserAsync(BrowserAddressBox.Text),
                "LoadingBrowserTitle",
                "LoadingBrowserMessage");
        }
    }

    private void SessionWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (sender.Source is not null)
        {
            BrowserAddressBox.Text = sender.Source.ToString();
        }

        var completedSource = sender.Source;
        var completedAuthenticationTransition = completedSource is not null &&
                                                IsAuthenticationTransitionUri(
                                                    completedSource.AbsoluteUri);
        if (args.IsSuccess &&
            ViewModel.IsBrowserSessionMode &&
            completedSource is not null &&
            WebViewOriginPolicy.IsTrustedMicrosoftUri(completedSource) &&
            !completedAuthenticationTransition)
        {
            Interlocked.Exchange(ref _browserAuthenticationTransitionInProgress, 0);
        }

        if (args.IsSuccess &&
            ViewModel.IsBrowserSessionMode &&
            Volatile.Read(ref _browserSessionClearInProgress) == 0 &&
            Volatile.Read(ref _browserAuthenticationTransitionInProgress) == 0 &&
            ViewModel.Routes.Count > 0 &&
            completedSource is not null &&
            !completedAuthenticationTransition &&
            SharePointRouteParser.IsAllowedSharePointUri(completedSource))
        {
            ScheduleBrowserSessionVerification(completedSource);
        }
    }

    private void SessionWebView_NavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (args.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (TryApproveBrowserNavigation(
                args.Uri,
                args.IsUserInitiated && !args.IsRedirected,
                out _))
        {
            if (ViewModel.IsBrowserSessionMode && IsAuthenticationTransitionUri(args.Uri))
            {
                Interlocked.Exchange(ref _browserAuthenticationTransitionInProgress, 1);
                CancelPendingBrowserSessionVerification();
                BeginBrowserIdentityTransition(invalidateBrowserVerification: true);
            }

            return;
        }

        args.Cancel = true;
        BrowserInfoBar.Title = AppText.Get("InvalidUrlTitle");
        BrowserInfoBar.Message = AppText.Get("InvalidUrlMessage");
        BrowserInfoBar.Severity = InfoBarSeverity.Warning;
        StartupDiagnostics.Write($"Blocked WebView navigation to an untrusted origin: {args.Uri}");
    }

    private void ScheduleBrowserSessionVerification(Uri source)
    {
        if (Volatile.Read(ref _browserSessionClearInProgress) != 0 ||
            Volatile.Read(ref _browserAuthenticationTransitionInProgress) != 0)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(
            ref _browserNavigationVerificationCancellation,
            cancellation);
        previous?.Cancel();
        _ = VerifyBrowserSessionAfterNavigationAsync(source, cancellation);
    }

    private async Task VerifyBrowserSessionAfterNavigationAsync(
        Uri source,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _browserSessionClearInProgress) != 0 ||
                Volatile.Read(ref _browserAuthenticationTransitionInProgress) != 0 ||
                !ViewModel.IsBrowserSessionMode ||
                SessionWebView.Source is not { } currentSource ||
                !string.Equals(
                    source.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                    currentSource.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await VerifyBrowserSessionAsync(
                showMessage: false,
                restoreFromWebView: !ViewModel.IsBrowserSessionVerified,
                cancellationToken: cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer top-level navigation superseded this verification.
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Browser navigation session verification failed.", exception);
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _browserNavigationVerificationCancellation,
                null,
                cancellation);
            cancellation.Dispose();
        }
    }

    private static bool IsAuthenticationTransitionUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.DnsSafeHost;
        if (host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("login.microsoftonline.", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("login.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("login.windows.net", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("account.microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!SharePointRouteParser.IsAllowedSharePointUri(uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return path.Contains("/signout", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/signin", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/login", StringComparison.OrdinalIgnoreCase);
    }

    private void SessionWebView_NewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (TryApproveBrowserNavigation(args.Uri, isUserInitiated: false, out var targetUri))
        {
            sender.Navigate(targetUri.AbsoluteUri);
        }
        else
        {
            StartupDiagnostics.Write($"Blocked WebView new-window request to an untrusted origin: {args.Uri}");
        }
    }

    private static void SessionWebView_DownloadStarting(
        CoreWebView2 sender,
        CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
        args.Handled = true;
        StartupDiagnostics.Write("Blocked a download initiated by the browser session.");
    }

    private static void SessionWebView_PermissionRequested(
        CoreWebView2 sender,
        CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.Handled = true;
        StartupDiagnostics.Write($"Denied WebView permission request: {args.PermissionKind}.");
    }

    private static void SessionWebView_ServerCertificateErrorDetected(
        CoreWebView2 sender,
        CoreWebView2ServerCertificateErrorDetectedEventArgs args)
    {
        args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        StartupDiagnostics.Write($"Blocked WebView certificate error for {args.RequestUri}: {args.ErrorStatus}.");
    }

    private async Task CheckUpdatesOnStartupAsync()
    {
        try
        {
            var updateStatus = await ViewModel.CheckForUpdatesAsync();
            if (updateStatus?.Update is not null)
            {
                ShowActionMessage(updateStatus.Title, updateStatus.Message, updateStatus.Severity);
                NotifyUpdateReady(updateStatus);
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Automatic update check failed.", ex);
        }
    }

    private async Task RestoreBrowserSessionOnStartupAsync()
    {
        if (!ViewModel.IsBrowserSessionMode || ViewModel.Routes.Count == 0)
        {
            return;
        }

        try
        {
            await EnsureBrowserInitializedAsync(navigate: false);
            BeginBrowserIdentityTransition();
            var result = await _browserSessionService.RestoreSessionAsync(ViewModel.Routes, SessionWebView.CoreWebView2);
            ViewModel.UpdateBrowserSessionStatus(result, HasAuthenticatedBrowserRoute());
            UpdateBrowserInfo(result);
            await RefreshSharePointExplorerContextAsync();

            if (result.Success)
            {
                await ViewModel.RefreshVirtualDriveAsync();
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Could not restore browser session on startup.", ex);
        }
    }

    private async Task OpenBrowserSessionAsync(bool navigate)
    {
        SelectNavigationItem("Browser");
        await EnsureBrowserInitializedAsync(navigate);
        await RestoreBrowserMemoryAsync();
        ConfigureBrowserKeepAliveTimer();
    }

    private async Task EnsureBrowserInitializedAsync(bool navigate)
    {
        if (!_browserInitialized)
        {
            Directory.CreateDirectory(_browserSessionService.ProfilePath);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                null,
                _browserSessionService.ProfilePath,
                null);
            await SessionWebView.EnsureCoreWebView2Async(environment);
            await _browserSessionService.ApplyPendingSessionCleanupAsync(SessionWebView.CoreWebView2);
            ConfigureBrowserSecurity();
            _browserInitialized = true;
        }

        SessionWebView.Visibility = Visibility.Visible;

        if (navigate && (SessionWebView.Source is null || SessionWebView.Source.AbsoluteUri == "about:blank"))
        {
            var startUri = ViewModel.GetBrowserSessionStartUri();
            BrowserAddressBox.Text = startUri.ToString();
            SessionWebView.Source = startUri;
        }
    }

    private async Task NavigateBrowserAsync(string value)
    {
        await EnsureBrowserInitializedAsync(navigate: false);
        var uri = ParseBrowserUri(value);
        if (uri is null || !WebViewOriginPolicy.IsTrustedMicrosoftUri(uri))
        {
            BrowserInfoBar.Title = AppText.Get("InvalidUrlTitle");
            BrowserInfoBar.Message = AppText.Get("InvalidUrlMessage");
            BrowserInfoBar.Severity = InfoBarSeverity.Warning;
            return;
        }

        BrowserAddressBox.Text = uri.ToString();
        SessionWebView.Source = uri;
    }

    private void ConfigureBrowserSecurity()
    {
        if (_browserSecurityConfigured || SessionWebView.CoreWebView2 is null)
        {
            return;
        }

        var coreWebView = SessionWebView.CoreWebView2;
        var settings = coreWebView.Settings;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.IsScriptEnabled = true;
        settings.IsBuiltInErrorPageEnabled = true;
        SessionWebView.AllowDrop = false;

        coreWebView.NavigationStarting += SessionWebView_NavigationStarting;
        coreWebView.NewWindowRequested += SessionWebView_NewWindowRequested;
        coreWebView.DownloadStarting += SessionWebView_DownloadStarting;
        coreWebView.PermissionRequested += SessionWebView_PermissionRequested;
        coreWebView.ServerCertificateErrorDetected += SessionWebView_ServerCertificateErrorDetected;
        _browserSecurityConfigured = true;
    }

    private bool TryApproveBrowserNavigation(
        string value,
        bool isUserInitiated,
        out Uri targetUri)
    {
        targetUri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsedUri) ||
            !WebViewOriginPolicy.IsSecureWebUri(parsedUri))
        {
            return false;
        }

        targetUri = parsedUri;
        if (WebViewOriginPolicy.IsTrustedMicrosoftUri(parsedUri) ||
            WebViewOriginPolicy.IsApprovedFederatedUri(parsedUri, _approvedFederatedBrowserHosts))
        {
            return true;
        }

        var currentUri = SessionWebView.Source;
        if (!WebViewOriginPolicy.CanBeginFederatedSignIn(currentUri, parsedUri, isUserInitiated))
        {
            return false;
        }

        _approvedFederatedBrowserHosts.Add(parsedUri.DnsSafeHost);
        StartupDiagnostics.Write($"Temporarily allowed federated identity origin: {parsedUri.DnsSafeHost}.");
        return true;
    }

    private async Task<RouteTestResult> TestRouteWithBrowserSessionAsync(DriveRoute route)
    {
        await EnsureBrowserInitializedAsync(navigate: false);
        BeginBrowserIdentityTransition();
        var result = await _browserSessionService.TestRouteAsync(route, SessionWebView.CoreWebView2);
        ViewModel.UpdateBrowserSessionStatus(result, HasAuthenticatedBrowserRoute());
        UpdateBrowserInfo(result);
        await RefreshSharePointExplorerContextAsync();
        return result;
    }

    private async Task VerifyBrowserSessionAsync(
        bool showMessage,
        bool restoreFromWebView = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _browserSessionClearInProgress) != 0 ||
            Volatile.Read(ref _browserAuthenticationTransitionInProgress) != 0)
        {
            return;
        }

        await _browserSessionVerificationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _browserSessionClearInProgress) != 0 ||
                Volatile.Read(ref _browserAuthenticationTransitionInProgress) != 0)
            {
                return;
            }

            await EnsureBrowserInitializedAsync(navigate: false);
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _browserSessionClearInProgress) != 0 ||
                Volatile.Read(ref _browserAuthenticationTransitionInProgress) != 0)
            {
                return;
            }

            RouteTestResult result;
            if (showMessage ||
                restoreFromWebView ||
                !ViewModel.IsBrowserSessionVerified ||
                !HasAuthenticatedBrowserRoute())
            {
                BeginBrowserIdentityTransition();
                result = await _browserSessionService.RestoreSessionAsync(
                    ViewModel.Routes,
                    SessionWebView.CoreWebView2,
                    cancellationToken);
            }
            else
            {
                result = await _browserSessionService.KeepAliveAsync(
                    ViewModel.Routes,
                    SessionWebView.CoreWebView2,
                    () => BeginBrowserIdentityTransition(
                        invalidateBrowserVerification: true,
                        invalidatePublishedSession: false),
                    cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _browserSessionService.InvalidatePublishedSession();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (Volatile.Read(ref _browserSessionClearInProgress) != 0 ||
                Volatile.Read(ref _browserAuthenticationTransitionInProgress) != 0)
            {
                _browserSessionService.InvalidatePublishedSession();
                return;
            }

            ViewModel.UpdateBrowserSessionStatus(result, HasAuthenticatedBrowserRoute());
            await RefreshSharePointExplorerContextAsync();

            if (showMessage || !result.Success)
            {
                UpdateBrowserInfo(result);
            }

            if (showMessage)
            {
                ShowActionMessage(
                    result.Success ? AppText.Get("LoginReadyTitle") : AppText.Get("LoginPendingTitle"),
                    result.Message,
                    result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            }

            await RefreshOperationsHealthAsync();
        }
        finally
        {
            _browserSessionVerificationGate.Release();
        }
    }

    private void NotifyUpdateReady(AppUpdateStatus? status)
    {
        if (status?.Update is null || !ViewModel.IsNotificationDeliveryEnabled || !ViewModel.NotifyUpdateReady)
        {
            return;
        }

        App.Services.Notifications.Show(
            "update-ready",
            AppText.Get("NotificationUpdateReadyTitle"),
            AppText.Get("NotificationUpdateReadyMessage"),
            "Updates",
            status.Update.VersionText);
    }

    private async Task ClearBrowserSessionAsync(bool beginIdentityTransition = true)
    {
        BeginBrowserSessionClearBarrier();
        if (beginIdentityTransition && ViewModel.IsBrowserSessionMode)
        {
            BeginBrowserIdentityTransition(invalidateBrowserVerification: true);
        }

        await _browserSessionVerificationGate.WaitAsync();
        try
        {
            await EnsureBrowserInitializedAsync(navigate: false);
            await _browserSessionService.ClearSessionAsync(SessionWebView.CoreWebView2);
            _approvedFederatedBrowserHosts.Clear();
            _browserContent.ClearCache();
            _browserSessionCacheScope = $"BROWSER-SESSION-{Guid.NewGuid():N}";
            var result = new RouteTestResult(false, AppText.Get("LoginClearedMessage"));
            ViewModel.UpdateBrowserSessionStatus(result, hasVerifiedRoute: false);
            UpdateBrowserInfo(result);
            SessionWebView.Source = new Uri("about:blank");
            await RefreshSharePointExplorerContextAsync();
        }
        finally
        {
            _browserSessionVerificationGate.Release();
            EndBrowserSessionClearBarrier();
        }
    }

    private void BeginBrowserSessionClearBarrier()
    {
        Interlocked.Increment(ref _browserSessionClearInProgress);
        CancelPendingBrowserSessionVerification();
    }

    private void CancelPendingBrowserSessionVerification()
    {
        var pendingVerification = Interlocked.Exchange(
            ref _browserNavigationVerificationCancellation,
            null);
        pendingVerification?.Cancel();
    }

    private void EndBrowserSessionClearBarrier() =>
        Interlocked.Decrement(ref _browserSessionClearInProgress);

    private bool HasAuthenticatedBrowserRoute() =>
        ViewModel.Routes.Any(route =>
            Uri.TryCreate(route.SharePointUrl, UriKind.Absolute, out var uri) &&
            SharePointRouteParser.IsAllowedSharePointUri(uri) &&
            ViewModel.IsRouteAllowed(route.SharePointUrl) &&
            SharePointCookieStore.IsRouteVerified(uri) &&
            SharePointCookieStore.TryGetCookieHeader(uri, out _));

    private void BeginBrowserIdentityTransition(
        bool invalidateBrowserVerification = false,
        bool invalidatePublishedSession = true) =>
        BeginContentIdentityTransition(
            usesBrowserSession: true,
            invalidateBrowserVerification: invalidateBrowserVerification,
            invalidatePublishedBrowserSession: invalidatePublishedSession);

    private async Task TrimBrowserCacheAsync()
    {
        if (!_browserInitialized || SessionWebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await SessionWebView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.DiskCache |
                CoreWebView2BrowsingDataKinds.CacheStorage);
            StartupDiagnostics.Write("WebView disk and cache-storage data cleared without removing cookies.");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Could not trim WebView cache.", ex);
        }
    }

    private async Task PrepareBrowserForBackgroundAsync()
    {
        if (!_browserInitialized || SessionWebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            SessionWebView.Visibility = Visibility.Collapsed;
            SessionWebView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;

            if (!ViewModel.BrowserKeepSessionAlive && !SessionWebView.CoreWebView2.IsSuspended)
            {
                await SessionWebView.CoreWebView2.TrySuspendAsync();
            }

            await TrimBrowserCacheAsync();
            StartupDiagnostics.Write("WebView moved to low-memory background mode.");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Could not reduce WebView background memory usage.", ex);
        }
    }

    private Task RestoreBrowserMemoryAsync()
    {
        if (!_browserInitialized || SessionWebView.CoreWebView2 is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            SessionWebView.Visibility = Visibility.Visible;
            SessionWebView.CoreWebView2.Resume();
            SessionWebView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Could not restore WebView foreground memory mode.", ex);
        }

        return Task.CompletedTask;
    }

    private void ConfigureBrowserKeepAliveTimer()
    {
        _browserKeepAliveTimer.Stop();
        if (!_browserInitialized || !ViewModel.IsBrowserSessionMode || !ViewModel.BrowserKeepSessionAlive)
        {
            return;
        }

        _browserKeepAliveTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(ViewModel.BrowserKeepAliveMinutes, 5, 240));
        _browserKeepAliveTimer.Start();
    }

    private void UpdateBrowserInfo(RouteTestResult result)
    {
        BrowserInfoBar.Title = result.Success ? AppText.Get("LoginVerifiedTitle") : AppText.Get("LoginPendingTitle");
        BrowserInfoBar.Message = result.Message;
        BrowserInfoBar.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
    }
}
