using EasyShare.Models;
using EasyShare.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EasyShare.Controls;

public sealed class ContentAssistantResultTemplateSelector : DataTemplateSelector
{
    public DataTemplate FolderTemplate { get; set; } = null!;

    public DataTemplate FileTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
        item is ContentSearchResult { IsDirectory: true } ? FolderTemplate : FileTemplate;
}

public sealed partial class ContentAssistantControl : UserControl
{
    private CancellationTokenSource? _searchDebounce;

    public ContentAssistantControl()
    {
        InitializeComponent();
    }

    public ContentAssistantViewModel ViewModel { get; private set; } = null!;

    public event Action<ContentSearchResult>? OpenRequested;

    public void Initialize(ContentAssistantViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = ViewModel;
        Bindings.Update();
    }

    public async Task InitializeAsync(
        ContentAssistantViewModel viewModel,
        CancellationToken cancellationToken = default)
    {
        Initialize(viewModel);
        await ViewModel.RefreshHistoryAsync(cancellationToken);
    }

    private async void SearchBox_QuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        CancelSearchDebounce();
        await RunSafeAsync(() => ViewModel.SearchAsync(args.QueryText));
    }

    private async void SearchBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || !ViewModel.IsConfigured)
        {
            return;
        }

        CancelSearchDebounce();
        var cancellation = new CancellationTokenSource();
        _searchDebounce = cancellation;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation.Token);
            await ViewModel.SearchAsync(sender.Text, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer keystroke superseded this local search.
        }
        finally
        {
            if (ReferenceEquals(_searchDebounce, cancellation))
            {
                _searchDebounce = null;
            }

            cancellation.Dispose();
        }
    }

    private async void IndexButton_Click(object sender, RoutedEventArgs e) =>
        await RunSafeAsync(() => ViewModel.IndexAllAsync());

    private void CancelButton_Click(object sender, RoutedEventArgs e) => ViewModel.CancelIndexing();

    private async void ResultsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        await OpenSelectedAsync();

    private async void ResultsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await OpenSelectedAsync();
    }

    private async Task OpenSelectedAsync()
    {
        if (ResultsList.SelectedItem is not ContentSearchResult result)
        {
            return;
        }

        OpenRequested?.Invoke(result);

        try
        {
            await ViewModel.RecordAccessAsync(result);
        }
        catch (OperationCanceledException)
        {
            // Access history is best-effort and must not block the requested open action.
        }
        catch (Exception exception)
        {
            ViewModel.ReportExternalError(exception);
        }
    }

    private async Task RunSafeAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            // A newer operation or the user cancelled the current work.
        }
        catch (Exception exception)
        {
            ViewModel.ReportExternalError(exception);
        }
    }

    private void ContentAssistantControl_Unloaded(object sender, RoutedEventArgs e) =>
        CancelSearchDebounce();

    private void CancelSearchDebounce()
    {
        _searchDebounce?.Cancel();
        _searchDebounce = null;
    }
}
