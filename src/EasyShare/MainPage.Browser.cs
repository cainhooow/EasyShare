using EasyShare.Models;
using EasyShare.Resources;
using EasyShare.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace EasyShare;

public sealed partial class MainPage
{
    private async void BrowserKeepAliveTimer_Tick(object? sender, object e)
    {
        if (!ViewModel.IsBrowserSessionMode || !ViewModel.BrowserKeepSessionAlive)
        {
            return;
        }

        await VerifyBrowserSessionAsync(showMessage: false);
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
            ClearBrowserSessionAsync,
            "LoadingSaveTitle",
            "LoadingSaveMessage");

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CheckForUpdatesAsync();
        ShowActionMessage(ViewModel.UpdateStatusTitle, ViewModel.UpdateStatusMessage, ViewModel.UpdateStatusSeverity);
    }

    private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DownloadUpdateAsync();
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
        if (!await ConfirmResetAsync())
        {
            return;
        }

        if (_browserInitialized)
        {
            await _browserSessionService.ClearSessionAsync(SessionWebView.CoreWebView2);
        }
        else
        {
            _browserSessionService.ClearStoredSession();
        }

        _browserContent.ClearCache();

        await RunWithLoadingAsync(
            ViewModel.ResetAppAsync,
            "LoadingResetTitle",
            "LoadingResetMessage");
        ConfigureBrowserKeepAliveTimer();
        ShowActionMessage(AppText.Get("ResetDoneTitle"), AppText.Get("ResetDoneMessage"), InfoBarSeverity.Success);
        SelectNavigationItem("Home");
        await ShowSetupWizardIfNeededAsync();
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
    }

    private async Task CheckUpdatesOnStartupAsync()
    {
        try
        {
            var updateStatus = await ViewModel.CheckForUpdatesAsync();
            if (updateStatus?.Update is not null)
            {
                ShowActionMessage(updateStatus.Title, updateStatus.Message, updateStatus.Severity);
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
            var result = await _browserSessionService.RestoreSessionAsync(ViewModel.Routes, SessionWebView.CoreWebView2);
            ViewModel.UpdateBrowserSessionStatus(result);
            UpdateBrowserInfo(result);

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
            await SessionWebView.EnsureCoreWebView2Async();
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
        if (uri is null)
        {
            BrowserInfoBar.Title = AppText.Get("InvalidUrlTitle");
            BrowserInfoBar.Message = AppText.Get("InvalidUrlMessage");
            BrowserInfoBar.Severity = InfoBarSeverity.Warning;
            return;
        }

        BrowserAddressBox.Text = uri.ToString();
        SessionWebView.Source = uri;
    }

    private async Task<RouteTestResult> TestRouteWithBrowserSessionAsync(DriveRoute route)
    {
        await EnsureBrowserInitializedAsync(navigate: false);
        var result = await _browserSessionService.TestRouteAsync(route, SessionWebView.CoreWebView2);
        ViewModel.UpdateBrowserSessionStatus(result);
        UpdateBrowserInfo(result);
        return result;
    }

    private async Task VerifyBrowserSessionAsync(bool showMessage)
    {
        await EnsureBrowserInitializedAsync(navigate: false);
        var result = await _browserSessionService.KeepAliveAsync(ViewModel.Routes, SessionWebView.CoreWebView2);
        ViewModel.UpdateBrowserSessionStatus(result);

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
    }

    private async Task ClearBrowserSessionAsync()
    {
        await EnsureBrowserInitializedAsync(navigate: false);
        await _browserSessionService.ClearSessionAsync(SessionWebView.CoreWebView2);
        _browserContent.ClearCache();
        var result = new RouteTestResult(false, AppText.Get("LoginClearedMessage"));
        ViewModel.UpdateBrowserSessionStatus(result);
        UpdateBrowserInfo(result);
        SessionWebView.Source = ViewModel.GetBrowserSessionStartUri();
    }

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
