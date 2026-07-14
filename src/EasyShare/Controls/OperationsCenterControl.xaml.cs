using EasyShare.Models;
using EasyShare.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyShare.Controls;

public enum ConflictResolutionAction
{
    ExportLocalCopy,
    UseRemoteVersion,
    ReplaceRemote
}

public sealed record ConflictResolutionRequest(Guid JobId, ConflictResolutionAction Action);

public sealed partial class OperationsCenterControl : UserControl
{
    public OperationsCenterControl()
    {
        InitializeComponent();
    }

    public OperationsCenterViewModel ViewModel { get; private set; } = null!;

    public event Action<Guid>? RetryRequested;

    public event Action<ConflictResolutionRequest>? ConflictResolutionRequested;

    public event Action? HealthRefreshRequested;

    public event Action? SupportExportRequested;

    public event Action<Guid>? OfflinePinRequested;

    public event Action<string>? OfflineRemoveRequested;

    public void Initialize(OperationsCenterViewModel viewModel)
    {
        ViewModel = viewModel;
        Bindings.Update();
    }

    public void SelectConflicts() => OperationsTabs.SelectedIndex = 1;

    public void SelectHealth() => OperationsTabs.SelectedIndex = 2;

    public void SelectOffline() => OperationsTabs.SelectedIndex = 3;

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetGuid(sender, out var jobId))
        {
            RetryRequested?.Invoke(jobId);
        }
    }

    private void ExportConflictButton_Click(object sender, RoutedEventArgs e) =>
        RaiseConflict(sender, ConflictResolutionAction.ExportLocalCopy);

    private void DiscardConflictButton_Click(object sender, RoutedEventArgs e) =>
        RaiseConflict(sender, ConflictResolutionAction.UseRemoteVersion);

    private void ReplaceRemoteButton_Click(object sender, RoutedEventArgs e) =>
        RaiseConflict(sender, ConflictResolutionAction.ReplaceRemote);

    private void RefreshHealthButton_Click(object sender, RoutedEventArgs e) => HealthRefreshRequested?.Invoke();

    private void ExportSupportButton_Click(object sender, RoutedEventArgs e) => SupportExportRequested?.Invoke();

    private void PinOfflineButton_Click(object sender, RoutedEventArgs e)
    {
        if (OfflineRoutePicker.SelectedItem is DriveRoute route)
        {
            OfflinePinRequested?.Invoke(route.Id);
        }
    }

    private void RemoveOfflineButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key } && !string.IsNullOrWhiteSpace(key))
        {
            OfflineRemoveRequested?.Invoke(key);
        }
    }

    private void RaiseConflict(object sender, ConflictResolutionAction action)
    {
        if (TryGetGuid(sender, out var jobId))
        {
            ConflictResolutionRequested?.Invoke(new ConflictResolutionRequest(jobId, action));
        }
    }

    private static bool TryGetGuid(object sender, out Guid value)
    {
        value = Guid.Empty;
        return sender is FrameworkElement { Tag: not null } element &&
               Guid.TryParse(element.Tag.ToString(), out value);
    }

    private void TabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        // Tabs are fixed destinations; this handler intentionally prevents close behavior.
    }
}
