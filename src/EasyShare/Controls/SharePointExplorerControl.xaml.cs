using EasyShare.Models;
using EasyShare.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EasyShare.Controls;

public sealed class SharePointExplorerItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate FolderTemplate { get; set; } = null!;

    public DataTemplate FileTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
        item is SharePointExplorerItem { IsFolder: true } ? FolderTemplate : FileTemplate;
}

public sealed partial class SharePointExplorerControl : UserControl
{
    public SharePointExplorerControl()
    {
        InitializeComponent();
    }

    public SharePointExplorerViewModel ViewModel { get; private set; } = null!;

    public event Func<Task>? SignInRequested;

    public event Func<SharePointPinnedFolder, Task>? PinRequested;

    public event Func<Task>? ManualRouteRequested;

    public void Initialize(SharePointExplorerViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = ViewModel;
        Bindings.Update();
    }

    public async Task InitializeAsync(
        SharePointExplorerViewModel viewModel,
        CancellationToken cancellationToken = default)
    {
        Initialize(viewModel);
        await ViewModel.InitializeAsync(cancellationToken);
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e) =>
        await RunSafeAsync(() => ViewModel.NavigateBackAsync());

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RunSafeAsync(() => ViewModel.RefreshAsync());

    private async void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.TryBuildPinnedFolder(out var pinnedFolder))
        {
            return;
        }

        await RunSafeAsync(() => InvokeAsync(PinRequested, pinnedFolder));
    }

    private async void ManualRouteButton_Click(object sender, RoutedEventArgs e) =>
        await RunSafeAsync(() => InvokeAsync(ManualRouteRequested));

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        await RunSafeAsync(async () =>
        {
            await InvokeAsync(SignInRequested);
        });
    }

    private async void SiteSearchBox_QuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args) =>
        await RunSafeAsync(() => ViewModel.SearchAsync(args.QueryText));

    private async void SitePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SitePicker.SelectedItem is SharePointSiteInfo site)
        {
            await RunSafeAsync(() => ViewModel.SelectSiteAsync(site));
        }
    }

    private async void LibraryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryPicker.SelectedItem is SharePointLibraryInfo library)
        {
            await RunSafeAsync(() => ViewModel.SelectLibraryAsync(library));
        }
    }

    private async void FolderBreadcrumb_ItemClicked(
        BreadcrumbBar sender,
        BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is SharePointExplorerBreadcrumb breadcrumb)
        {
            await RunSafeAsync(() => ViewModel.NavigateToBreadcrumbAsync(breadcrumb));
        }
    }

    private async void ItemsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        await OpenSelectedFolderAsync();

    private async void ItemsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await OpenSelectedFolderAsync();
    }

    private async void LoadMoreButton_Click(object sender, RoutedEventArgs e) =>
        await RunSafeAsync(() => ViewModel.LoadMoreAsync());

    private Task OpenSelectedFolderAsync()
    {
        if (ItemsList.SelectedItem is not SharePointExplorerItem { IsFolder: true } folder)
        {
            return Task.CompletedTask;
        }

        return RunSafeAsync(() => ViewModel.OpenFolderAsync(folder));
    }

    private async Task RunSafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // The host or a newer explorer action cancelled this request.
        }
        catch (Exception exception)
        {
            ViewModel.ReportExternalError(exception);
        }
    }

    private static async Task InvokeAsync(Func<Task>? handlers)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Func<Task> handler in handlers.GetInvocationList())
        {
            await handler();
        }
    }

    private static async Task InvokeAsync(
        Func<SharePointPinnedFolder, Task>? handlers,
        SharePointPinnedFolder pinnedFolder)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Func<SharePointPinnedFolder, Task> handler in handlers.GetInvocationList())
        {
            await handler(pinnedFolder);
        }
    }
}
