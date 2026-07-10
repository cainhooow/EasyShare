using EasyShare.Services;
using EasyShare.ViewModels;
using EasyShare.Models;
using EasyShare.Resources;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using WinRT.Interop;

namespace EasyShare;

/// <summary>
/// The main content page displayed inside the application window.
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly BrowserSessionService _browserSessionService;
    private readonly SharePointBrowserContentService _browserContent;
    private readonly UploadQueueService _uploadQueue;
    private readonly DispatcherTimer _browserKeepAliveTimer = new();
    private readonly DispatcherTimer _actionMessageTimer = new();
    private bool _browserInitialized;
    private bool _pageLoaded;
    private int _localBusyDepth;

    public MainPageViewModel ViewModel { get; }

    public MainPage()
    {
        var paths = new AppDataPaths();
        var database = new LocalDatabase(paths);
        var authentication = new MsalAuthenticationService(paths, database);
        _browserContent = new SharePointBrowserContentService(database);
        _uploadQueue = new UploadQueueService(database, _browserContent, paths);
        _browserSessionService = new BrowserSessionService(paths);

        ViewModel = new MainPageViewModel(
            database,
            authentication,
            new VirtualDriveService(_browserContent, _uploadQueue),
            new StartupService(),
            new GraphSharePointService(authentication),
            new AppUpdateService(paths));

        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _uploadQueue.JobChanged += UploadQueue_JobChanged;
        _browserKeepAliveTimer.Tick += BrowserKeepAliveTimer_Tick;
        _actionMessageTimer.Interval = TimeSpan.FromSeconds(5);
        _actionMessageTimer.Tick += (_, _) =>
        {
            _actionMessageTimer.Stop();
            ActionInfoBar.IsOpen = false;
        };
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_pageLoaded)
            {
                return;
            }

            _pageLoaded = true;
            StartupDiagnostics.Write("MainPage loaded.");
            await RunWithLoadingAsync(
                async () =>
                {
                    await ViewModel.LoadAsync();
                    _browserContent.ConfigureCache(TimeSpan.FromMinutes(ViewModel.CacheMinutes));
                    _uploadQueue.Start();
                    await RestoreBrowserSessionOnStartupAsync();
                },
                "LoadingStartupTitle",
                "LoadingStartupMessage");
            ApplyStartupWindowState();
            ConfigureBrowserKeepAliveTimer();
            await ShowSetupWizardIfNeededAsync();
            _ = CheckUpdatesOnStartupAsync();

            StartupDiagnostics.Write("MainPage startup completed.");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("MainPage startup failed.", ex);
            ViewModel.ReportStartupError(AppText.Get("StartupLoadFailed"), ex);
        }
    }

    private void ContentHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        ContentHost.Padding = width switch
        {
            >= 1200 => new Thickness(40, 28, 40, 32),
            >= 760 => new Thickness(28, 22, 28, 28),
            _ => new Thickness(16, 16, 16, 20)
        };

        ApplySummaryCardLayout(width);
        ApplyResponsivePageLayouts(width);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsBusy))
        {
            UpdateLoadingOverlay();
        }
    }

    private void UploadQueue_JobChanged(SyncJob job)
    {
        DispatcherQueue.TryEnqueue(() => ViewModel.ApplySyncJob(job));
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBrowserSessionMode)
        {
            await RunWithLoadingAsync(
                () => OpenBrowserSessionAsync(navigate: true),
                "LoadingBrowserTitle",
                "LoadingBrowserMessage");
            return;
        }

        var hwnd = App.MainWindow is null ? IntPtr.Zero : WindowNative.GetWindowHandle(App.MainWindow);
        await RunWithLoadingAsync(
            () => ViewModel.SignInAsync(hwnd),
            "LoadingBrowserTitle",
            "LoadingBrowserMessage");
    }

    private async void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBrowserSessionMode)
        {
            await RunWithLoadingAsync(
                ClearBrowserSessionAsync,
                "LoadingSaveTitle",
                "LoadingSaveMessage");
            return;
        }

        await RunWithLoadingAsync(
            ViewModel.SignOutAsync,
            "LoadingSaveTitle",
            "LoadingSaveMessage");
        _browserContent.ClearCache();
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWithLoadingAsync(
            ViewModel.SaveSettingsAsync,
            "LoadingSaveTitle",
            "LoadingSaveMessage");
        _browserContent.ConfigureCache(TimeSpan.FromMinutes(ViewModel.CacheMinutes));
        ConfigureBrowserKeepAliveTimer();
        ShowActionMessage(AppText.Get("SettingsSavedTitle"), AppText.Get("SettingsSavedMessage"), InfoBarSeverity.Success);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWithLoadingAsync(
            ViewModel.LoadAsync,
            "LoadingRefreshTitle",
            "LoadingRefreshMessage");
        ShowActionMessage(AppText.Get("RefreshTitle"), AppText.Get("RefreshMessage"), InfoBarSeverity.Success);
    }

    private void OpenVirtualDriveButton_Click(object sender, RoutedEventArgs e) => ViewModel.OpenVirtualDrive();

    private async void OpenBrowserSessionButton_Click(object sender, RoutedEventArgs e) =>
        await RunWithLoadingAsync(
            () => OpenBrowserSessionAsync(navigate: true),
            "LoadingBrowserTitle",
            "LoadingBrowserMessage");

    private async void AddRouteButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowRouteEditorAsync(null);
    }

    private async void EditRouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid routeId })
        {
            await ShowRouteEditorAsync(ViewModel.Routes.FirstOrDefault(route => route.Id == routeId));
        }
    }

    private async void TestRouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid routeId })
        {
            await RunWithLoadingAsync(
                () => ViewModel.TestRouteAsync(routeId, TestRouteWithBrowserSessionAsync),
                "LoadingTestTitle",
                "LoadingTestMessage");
            ShowActionMessage(AppText.Get("TestDoneTitle"), AppText.Get("TestDoneMessage"), InfoBarSeverity.Success);
        }
    }

    private async void RemoveRouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid routeId })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = AppText.Get("RemoveRouteDialogTitle"),
            Content = AppText.Get("RemoveRouteDialogMessage"),
            PrimaryButtonText = AppText.Get("ActionRemove"),
            CloseButtonText = AppText.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunWithLoadingAsync(
                () => ViewModel.RemoveRouteAsync(routeId),
                "LoadingSaveTitle",
                "LoadingSaveMessage");
            ShowActionMessage(AppText.Get("RouteRemovedTitle"), AppText.Get("RouteRemovedMessage"), InfoBarSeverity.Success);
        }
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString();

        HomeView.Visibility = tag == "Home" ? Visibility.Visible : Visibility.Collapsed;
        RoutesView.Visibility = tag == "Routes" ? Visibility.Visible : Visibility.Collapsed;
        SyncView.Visibility = tag == "Sync" ? Visibility.Visible : Visibility.Collapsed;
        BrowserView.Visibility = tag == "Browser" ? Visibility.Visible : Visibility.Collapsed;
        HelpView.Visibility = tag == "Help" ? Visibility.Visible : Visibility.Collapsed;
        AboutView.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        if (tag != "Browser" && _browserInitialized)
        {
            _ = PrepareBrowserForBackgroundAsync();
        }

        if (tag == "Browser")
        {
            _ = RunWithLoadingAsync(
                () => OpenBrowserSessionAsync(navigate: true),
                "LoadingBrowserTitle",
                "LoadingBrowserMessage");
        }
    }

    private async Task<bool> ConfirmResetAsync()
    {
        var confirmationBox = new TextBox
        {
            Header = AppText.Get("ResetDialogConfirmationHeader"),
            PlaceholderText = AppText.Get("ResetDialogConfirmationText")
        };
        var info = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Error,
            Title = AppText.Get("ResetDialogTitle"),
            Message = AppText.Get("ResetDialogWarning")
        };
        var form = new StackPanel { Spacing = 12, MaxWidth = 620 };
        form.Children.Add(info);
        form.Children.Add(confirmationBox);

        var dialog = new ContentDialog
        {
            Title = AppText.Get("ResetDialogTitle"),
            Content = form,
            PrimaryButtonText = AppText.Get("ActionResetApp"),
            CloseButtonText = AppText.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!string.Equals(
                    confirmationBox.Text.Trim(),
                    AppText.Get("ResetDialogConfirmationText"),
                    StringComparison.Ordinal))
            {
                args.Cancel = true;
                confirmationBox.Focus(FocusState.Programmatic);
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowSetupWizardIfNeededAsync()
    {
        if (!ViewModel.ShouldShowSetupWizard)
        {
            return;
        }

        var mode = await ShowAccessModeDialogAsync();
        if (mode is null)
        {
            await ViewModel.CompleteSetupWizardAsync();
            ShowActionMessage(AppText.Get("SetupSkippedTitle"), AppText.Get("SetupSkippedMessage"), InfoBarSeverity.Informational);
            return;
        }

        await ViewModel.SetAuthenticationModeAsync(mode.Value);

        if (mode == AuthenticationMode.BrowserSession)
        {
            var sharePointAddress = await ShowSharePointAddressDialogAsync();
            if (sharePointAddress is null)
            {
                await ViewModel.CompleteSetupWizardAsync();
                return;
            }

            await ViewModel.SetBrowserSessionStartUrlAsync(sharePointAddress);
            SelectNavigationItem("Browser");
            await OpenBrowserSessionAsync(navigate: false);
            await NavigateBrowserAsync(sharePointAddress);
            BrowserInfoBar.Title = AppText.Get("WizardBrowserLoginTitle");
            BrowserInfoBar.Message = AppText.Get("WizardBrowserLoginMessage");
            BrowserInfoBar.Severity = InfoBarSeverity.Informational;
            await ViewModel.CompleteSetupWizardAsync();
            ShowActionMessage(AppText.Get("WizardLoginOpenedTitle"), AppText.Get("WizardLoginOpenedMessage"), InfoBarSeverity.Informational);
            return;
        }

        if (await ShowClientIdDialogAsync())
        {
            var hwnd = App.MainWindow is null ? IntPtr.Zero : WindowNative.GetWindowHandle(App.MainWindow);
            await ViewModel.SignInAsync(hwnd);
            await ViewModel.CompleteSetupWizardAsync();
            await ShowRouteEditorAsync(null);
        }
    }

    private async Task<AuthenticationMode?> ShowAccessModeDialogAsync()
    {
        var browserOption = new RadioButton
        {
            IsChecked = true,
            Content = BuildOptionContent(
                AppText.Get("SettingsAccessModeBrowser"),
                AppText.Get("AccessOptionBrowserDescription"))
        };
        var graphOption = new RadioButton
        {
            Content = BuildOptionContent(
                AppText.Get("SettingsAccessModeGraph"),
                AppText.Get("AccessOptionGraphDescription"))
        };

        var form = new StackPanel { Spacing = 12, MaxWidth = 620 };
        form.Children.Add(new TextBlock
        {
            Text = AppText.Get("AccessModePrompt"),
            TextWrapping = TextWrapping.Wrap
        });
        form.Children.Add(browserOption);
        form.Children.Add(graphOption);

        var dialog = new ContentDialog
        {
            Title = AppText.Get("AccessDialogTitle"),
            Content = form,
            PrimaryButtonText = AppText.Get("CommonContinue"),
            CloseButtonText = AppText.Get("CommonNotNow"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? graphOption.IsChecked == true ? AuthenticationMode.MicrosoftGraph : AuthenticationMode.BrowserSession
            : null;
    }

    private async Task<string?> ShowSharePointAddressDialogAsync()
    {
        var addressBox = new TextBox
        {
            Header = AppText.Get("SharePointAddressHeader"),
            PlaceholderText = AppText.Get("SharePointAddressPlaceholder"),
            Text = ViewModel.BrowserSessionStartUrl.Contains("office.com", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : ViewModel.BrowserSessionStartUrl
        };
        var info = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = AppText.Get("SharePointAddressInfoTitle"),
            Message = AppText.Get("SharePointAddressInfoMessage")
        };

        var form = new StackPanel { Spacing = 12, MaxWidth = 620 };
        form.Children.Add(info);
        form.Children.Add(addressBox);
        form.Children.Add(new TextBlock
        {
            Text = AppText.Get("WizardKeepConnectedMessage"),
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            Title = AppText.Get("SharePointAddressDialogTitle"),
            Content = form,
            PrimaryButtonText = AppText.Get("ActionOpenLogin"),
            CloseButtonText = AppText.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        dialog.PrimaryButtonClick += (dialogSender, args) =>
        {
            if (!SharePointRouteParser.TryParse(addressBox.Text, out _))
            {
                args.Cancel = true;
                info.Severity = InfoBarSeverity.Warning;
                info.Title = AppText.Get("InvalidAddressTitle");
                info.Message = AppText.Get("InvalidAddressMessage");
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? addressBox.Text : null;
    }

    private async Task<bool> ShowClientIdDialogAsync()
    {
        var clientIdBox = new TextBox
        {
            Header = AppText.Get("SettingsClientIdHeader"),
            PlaceholderText = AppText.Get("SettingsClientIdPlaceholder"),
            Text = ViewModel.ClientId
        };
        var tenantBox = new TextBox
        {
            Header = AppText.Get("SettingsTenantHeader"),
            PlaceholderText = AppText.Get("SettingsTenantPlaceholder"),
            Text = string.IsNullOrWhiteSpace(ViewModel.TenantId) ? "organizations" : ViewModel.TenantId
        };
        var info = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = AppText.Get("ClientInfoTitle"),
            Message = AppText.Get("ClientInfoMessage")
        };

        var form = new StackPanel { Spacing = 12, MaxWidth = 620 };
        form.Children.Add(info);
        form.Children.Add(clientIdBox);
        form.Children.Add(tenantBox);

        var dialog = new ContentDialog
        {
            Title = AppText.Get("ClientDialogTitle"),
            Content = form,
            PrimaryButtonText = AppText.Get("ActionSignIn"),
            CloseButtonText = AppText.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!Guid.TryParse(clientIdBox.Text.Trim(), out var clientId) || clientId == Guid.Empty)
            {
                args.Cancel = true;
                info.Severity = InfoBarSeverity.Warning;
                info.Title = AppText.Get("InvalidClientIdTitle");
                info.Message = AppText.Get("InvalidClientIdMessage");
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        ViewModel.ClientId = clientIdBox.Text.Trim();
        ViewModel.TenantId = string.IsNullOrWhiteSpace(tenantBox.Text) ? "organizations" : tenantBox.Text.Trim();
        await ViewModel.SaveSettingsAsync();
        return true;
    }

    private async Task ShowRouteEditorAsync(DriveRoute? route, string? initialUrl = null)
    {
        var parsedInitialUrl = initialUrl ?? (route is null
            ? string.Empty
            : SharePointRouteParser.BuildDisplayUrl(route.SharePointUrl, route.RemotePath));

        var nameBox = new TextBox
        {
            Header = AppText.Get("RouteNameHeader"),
            PlaceholderText = AppText.Get("RouteNamePlaceholder"),
            Text = route?.DisplayName ?? string.Empty
        };
        var linkBox = new TextBox
        {
            Header = AppText.Get("RouteLinkHeader"),
            PlaceholderText = AppText.Get("RouteLinkPlaceholder"),
            Text = parsedInitialUrl
        };
        var preview = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = AppText.Get("RouteLinkInfoTitle"),
            Message = AppText.Get("RouteLinkInfoMessage")
        };

        var form = new StackPanel { Spacing = 12, MaxWidth = 680 };
        form.Children.Add(preview);
        form.Children.Add(nameBox);
        form.Children.Add(linkBox);
        form.Children.Add(new TextBlock
        {
            Text = AppText.Get("RouteEditorHelp"),
            TextWrapping = TextWrapping.Wrap
        });

        var userChangedName = route is not null;
        nameBox.TextChanged += (_, _) => userChangedName = true;
        linkBox.TextChanged += (_, _) => UpdateRoutePreview(linkBox.Text, nameBox, preview, userChangedName);
        UpdateRoutePreview(linkBox.Text, nameBox, preview, userChangedName);

        var dialog = new ContentDialog
        {
            Title = route is null ? AppText.Get("RouteEditorAddTitle") : AppText.Get("RouteEditorEditTitle"),
            Content = form,
            PrimaryButtonText = route is null ? AppText.Get("RouteEditorAddTitle") : AppText.Get("CommonSave"),
            CloseButtonText = AppText.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        SharePointRouteInput parsed = default!;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!SharePointRouteParser.TryParse(linkBox.Text, out parsed))
            {
                args.Cancel = true;
                preview.Severity = InfoBarSeverity.Warning;
                preview.Title = AppText.Get("InvalidLinkTitle");
                preview.Message = AppText.Get("InvalidLinkMessage");
                return;
            }

            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                args.Cancel = true;
                preview.Severity = InfoBarSeverity.Warning;
                preview.Title = AppText.Get("MissingRouteNameTitle");
                preview.Message = AppText.Get("MissingRouteNameMessage");
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (route is null)
        {
            await RunWithLoadingAsync(
                () => ViewModel.AddRouteAsync(nameBox.Text, parsed.SiteUrl, parsed.RemotePath),
                "LoadingSaveTitle",
                "LoadingSaveMessage");
            ShowActionMessage(AppText.Get("RoutePinnedTitle"), AppText.Get("RoutePinnedMessage"), InfoBarSeverity.Success);
            return;
        }

        await RunWithLoadingAsync(
            () => ViewModel.UpdateRouteAsync(route.Id, nameBox.Text, parsed.SiteUrl, parsed.RemotePath),
            "LoadingSaveTitle",
            "LoadingSaveMessage");
        ShowActionMessage(AppText.Get("RouteUpdatedTitle"), AppText.Get("RouteUpdatedMessage"), InfoBarSeverity.Success);
    }

    private static void UpdateRoutePreview(string link, TextBox nameBox, InfoBar preview, bool userChangedName)
    {
        if (!SharePointRouteParser.TryParse(link, out var parsed))
        {
            preview.Severity = InfoBarSeverity.Informational;
            preview.Title = AppText.Get("RouteLinkInfoTitle");
            preview.Message = AppText.Get("RouteLinkInfoMessage");
            return;
        }

        if (!userChangedName && string.IsNullOrWhiteSpace(nameBox.Text))
        {
            nameBox.Text = parsed.SuggestedName;
        }

        preview.Severity = InfoBarSeverity.Success;
        preview.Title = AppText.Get("LinkRecognizedTitle");
        preview.Message = AppText.Format("LinkPreviewFormat", parsed.SiteUrl, parsed.RemotePath);
    }

    private static StackPanel BuildOptionContent(string title, string description)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 540
        });
        return panel;
    }

    private async Task RunWithLoadingAsync(Func<Task> action, string titleKey, string messageKey)
    {
        _localBusyDepth++;
        SetLoadingText(titleKey, messageKey);
        UpdateLoadingOverlay();

        try
        {
            await Task.Yield();
            await action();
        }
        finally
        {
            _localBusyDepth = Math.Max(0, _localBusyDepth - 1);
            if (_localBusyDepth == 0)
            {
                SetLoadingText("LoadingTitle", "LoadingMessage");
            }

            UpdateLoadingOverlay();
        }
    }

    private void SetLoadingText(string titleKey, string messageKey)
    {
        LoadingTitleText.Text = AppText.Get(titleKey);
        LoadingMessageText.Text = AppText.Get(messageKey);
    }

    private void UpdateLoadingOverlay()
    {
        var isActive = ViewModel.IsBusy || _localBusyDepth > 0;
        LoadingRing.IsActive = isActive;
        LoadingOverlay.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowActionMessage(string title, string message, InfoBarSeverity severity)
    {
        _actionMessageTimer.Stop();
        ActionInfoBar.Title = title;
        ActionInfoBar.Message = message;
        ActionInfoBar.Severity = severity;
        ActionInfoBar.IsOpen = true;
        _actionMessageTimer.Start();
    }

    private void SelectNavigationItem(string tag)
    {
        foreach (var item in RootNavigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                RootNavigation.SelectedItem = item;
                break;
            }
        }
    }

    private static Uri? ParseBrowserUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"https://{normalized}";
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ? uri : null;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = AppText.Get("CommonOk"),
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    private void ApplySummaryCardLayout(double width)
    {
        if (width >= 900)
        {
            SummaryCards.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            SummaryCards.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            SummaryCards.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);

            PlaceCard(AccountCard, row: 0, column: 0, columnSpan: 1);
            PlaceCard(VirtualRootCard, row: 0, column: 1, columnSpan: 1);
            PlaceCard(RoutesCountCard, row: 0, column: 2, columnSpan: 1);
            return;
        }

        if (width >= 620)
        {
            SummaryCards.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            SummaryCards.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            SummaryCards.ColumnDefinitions[2].Width = new GridLength(0);

            PlaceCard(AccountCard, row: 0, column: 0, columnSpan: 1);
            PlaceCard(VirtualRootCard, row: 0, column: 1, columnSpan: 1);
            PlaceCard(RoutesCountCard, row: 1, column: 0, columnSpan: 2);
            return;
        }

        SummaryCards.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        SummaryCards.ColumnDefinitions[1].Width = new GridLength(0);
        SummaryCards.ColumnDefinitions[2].Width = new GridLength(0);

        PlaceCard(AccountCard, row: 0, column: 0, columnSpan: 1);
        PlaceCard(VirtualRootCard, row: 1, column: 0, columnSpan: 1);
        PlaceCard(RoutesCountCard, row: 2, column: 0, columnSpan: 1);
    }

    private void ApplyResponsivePageLayouts(double width)
    {
        var isNarrow = width < 820;
        var compactAbout = width < 1280;
        var compactFooter = width < 900;

        HelpLayoutGrid.ColumnDefinitions[0].Width = isNarrow
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(320);
        HelpLayoutGrid.ColumnDefinitions[1].Width = isNarrow
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(HelpIntroCard, 0);
        Grid.SetColumn(HelpIntroCard, 0);
        Grid.SetRowSpan(HelpIntroCard, isNarrow ? 1 : 2);
        Grid.SetRow(HelpQuestionsPanel, isNarrow ? 1 : 0);
        Grid.SetColumn(HelpQuestionsPanel, isNarrow ? 0 : 1);
        Grid.SetRowSpan(HelpQuestionsPanel, isNarrow ? 1 : 2);

        AboutLayoutGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        AboutLayoutGrid.ColumnDefinitions[1].Width = compactAbout
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(AboutHeroCard, 0);
        Grid.SetColumnSpan(AboutHeroCard, compactAbout ? 1 : 2);
        Grid.SetRow(AboutHeroCard, 0);
        Grid.SetColumn(AboutInfoCard, 0);
        Grid.SetRow(AboutInfoCard, 1);
        Grid.SetColumn(AboutUpdateCard, compactAbout ? 0 : 1);
        Grid.SetRow(AboutUpdateCard, compactAbout ? 2 : 1);
        Grid.SetColumn(AboutFooterCard, 0);
        Grid.SetColumnSpan(AboutFooterCard, compactAbout ? 1 : 2);
        Grid.SetRow(AboutFooterCard, compactAbout ? 3 : 2);

        AboutFooterGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        AboutFooterGrid.ColumnDefinitions[1].Width = compactFooter
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        AboutFooterGrid.ColumnDefinitions[2].Width = compactFooter
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(AboutFooterAccess, 0);
        Grid.SetRow(AboutFooterAccess, 0);
        Grid.SetColumn(AboutFooterSession, compactFooter ? 0 : 1);
        Grid.SetRow(AboutFooterSession, compactFooter ? 1 : 0);
        Grid.SetColumn(AboutFooterUpdates, compactFooter ? 0 : 2);
        Grid.SetRow(AboutFooterUpdates, compactFooter ? 2 : 0);

        var compactSettings = width < 900;
        SettingsLayoutGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        SettingsLayoutGrid.ColumnDefinitions[1].Width = compactSettings
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(SettingsAccessCard, 0);
        Grid.SetColumnSpan(SettingsAccessCard, compactSettings ? 1 : 2);
        Grid.SetRow(SettingsAccessCard, 0);
        Grid.SetColumn(SettingsDriveCard, 0);
        Grid.SetRow(SettingsDriveCard, 1);
        Grid.SetColumn(SettingsSessionCard, compactSettings ? 0 : 1);
        Grid.SetRow(SettingsSessionCard, compactSettings ? 2 : 1);
        Grid.SetColumn(SettingsActionsPanel, 0);
        Grid.SetColumnSpan(SettingsActionsPanel, compactSettings ? 1 : 2);
        Grid.SetRow(SettingsActionsPanel, compactSettings ? 3 : 2);

        var compactAccessFields = width < 720;
        SettingsAccessFieldsGrid.ColumnDefinitions[1].Width = compactAccessFields
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumnSpan(SettingsAccessModeHelp, compactAccessFields ? 2 : 1);
        Grid.SetRow(SettingsAccessModeHelp, compactAccessFields ? 1 : 0);
        Grid.SetColumn(SettingsAccessModeHelp, compactAccessFields ? 0 : 1);
        Grid.SetRow(SettingsClientId, compactAccessFields ? 2 : 1);
        Grid.SetColumn(SettingsClientId, 0);
        Grid.SetColumnSpan(SettingsClientId, compactAccessFields ? 2 : 1);
        Grid.SetRow(SettingsTenant, compactAccessFields ? 3 : 1);
        Grid.SetColumn(SettingsTenant, compactAccessFields ? 0 : 1);
        Grid.SetColumnSpan(SettingsTenant, compactAccessFields ? 2 : 1);
        Grid.SetRow(SettingsClientIdHelp, compactAccessFields ? 4 : 2);
    }

    private static void PlaceCard(FrameworkElement card, int row, int column, int columnSpan)
    {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        Grid.SetColumnSpan(card, columnSpan);
    }

    private void ApplyStartupWindowState()
    {
        if ((!App.StartMinimized && !ViewModel.StartMinimized) || App.MainWindow is null)
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        ShowWindow(hwnd, 6);
        if (App.MainWindow is MainWindow mainWindow)
        {
            mainWindow.HideToTray();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
