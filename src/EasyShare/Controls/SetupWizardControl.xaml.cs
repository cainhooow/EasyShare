using EasyShare.Models;
using EasyShare.Resources;
using EasyShare.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace EasyShare.Controls;

public sealed partial class SetupWizardControl : UserControl
{
    private const int StepCount = 7;
    private const double RailBreakpoint = 920;
    private const double StackedCardsBreakpoint = 720;
    private const double StackedFooterBreakpoint = 600;

    private TaskCompletionSource<SetupWizardDraft?>? _completion;
    private SetupWizardDraft? _draft;
    private SetupWizardDraft? _originalDraft;
    private EnterprisePolicySnapshot? _policy;
    private SetupWizardCapabilities? _capabilities;
    private IReadOnlyCollection<string> _availableMountPoints = Array.Empty<string>();
    private int _currentStepIndex;
    private int _maxVisitedStepIndex;
    private int _busyGate;
    private bool _isPopulating;
    private bool _languageSubscribed;

    public SetupWizardControl()
    {
        InitializeComponent();
        AutomationProperties.SetLiveSetting(ValidationInfoBar, AutomationLiveSetting.Assertive);
        Localize();
    }

    public event Func<SetupWizardDraft, Task<SetupWizardApplyResult>>? ApplyRequested;

    public event Action<SetupWizardDraft>? AppearancePreviewRequested;

    public bool IsShowing =>
        _completion is { Task.IsCompleted: false } && Visibility == Visibility.Visible;

    public SetupWizardApplyResult? LastApplyResult { get; private set; }

    public Task<SetupWizardDraft?> ShowAsync(
        SetupWizardDraft draft,
        EnterprisePolicySnapshot policy,
        SetupWizardCapabilities capabilities,
        IReadOnlyList<string> mountOptions)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(mountOptions);

        if (IsShowing)
        {
            throw new InvalidOperationException("The setup wizard is already open.");
        }

        _originalDraft = CloneDraft(draft);
        _draft = CloneDraft(draft);
        _policy = policy;
        _capabilities = capabilities;
        _availableMountPoints = mountOptions
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _currentStepIndex = 0;
        _maxVisitedStepIndex = 0;
        _busyGate = 0;
        LastApplyResult = null;

        ApplyCapabilityDefaults();
        PopulateControls();
        SubscribeLanguageChanged();
        Localize();

        _completion = new TaskCompletionSource<SetupWizardDraft?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Visibility = Visibility.Visible;
        IsEnabled = true;
        SetBusy(false);
        UpdateResponsiveLayout(ActualWidth);
        UpdateStepPresentation(moveFocus: true);
        return _completion.Task;
    }

    private SetupWizardStep CurrentStep => (SetupWizardStep)_currentStepIndex;

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (IsShowing)
        {
            SubscribeLanguageChanged();
        }
    }

    private void Root_Unloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeLanguageChanged();
        if (IsShowing && Volatile.Read(ref _busyGate) == 0)
        {
            Complete(null, restorePreview: true);
        }
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveLayout(e.NewSize.Width);

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsShowing || Volatile.Read(ref _busyGate) != 0)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CancelWizard();
            return;
        }

        if (e.Key == VirtualKey.Enter && CurrentStep == SetupWizardStep.Review &&
            FocusManager.GetFocusedElement(XamlRoot) is not TextBox)
        {
            e.Handled = true;
            _ = FinishAsync();
        }
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulating || _draft is null ||
            LanguageComboBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string languageCode)
        {
            return;
        }

        _draft.LanguageCode = AppText.NormalizeLanguageCode(languageCode);
        AppText.SetLanguage(_draft.LanguageCode);
        InvokeAppearancePreview();
    }

    private void AppearanceControl_Changed(object sender, object e)
    {
        if (_isPopulating || _draft is null)
        {
            return;
        }

        ReadAppearanceControls();
        InvokeAppearancePreview();
    }

    private void AccessMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_isPopulating || _draft is null)
        {
            return;
        }

        _draft.AuthenticationMode = GraphAccessRadio.IsChecked == true
            ? AuthenticationMode.MicrosoftGraph
            : AuthenticationMode.BrowserSession;
        UpdateConnectionPanels();
        LocalizeRecommendation();
    }

    private void DraftTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isPopulating || _draft is null)
        {
            return;
        }

        _draft.ClientId = ClientIdTextBox.Text;
        _draft.TenantId = TenantTextBox.Text;
        _draft.BrowserSessionStartUrl = BrowserUrlTextBox.Text;
        LocalizeRecommendation();
    }

    private void WindowsControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_isPopulating)
        {
            return;
        }

        UpdateDependentControls();
    }

    private void NotificationsControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_isPopulating)
        {
            return;
        }

        UpdateDependentControls();
    }

    private void RailStep_Click(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _busyGate) != 0 ||
            sender is not Button { Tag: string tag } ||
            !int.TryParse(tag, out var requestedIndex) ||
            requestedIndex < 0 || requestedIndex > _maxVisitedStepIndex)
        {
            return;
        }

        ReadControlsIntoDraft();
        _currentStepIndex = requestedIndex;
        UpdateStepPresentation(moveFocus: true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => CancelWizard();

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStepIndex == 0 || Volatile.Read(ref _busyGate) != 0)
        {
            return;
        }

        ReadControlsIntoDraft();
        _currentStepIndex--;
        UpdateStepPresentation(moveFocus: true);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStepIndex >= StepCount - 1 || Volatile.Read(ref _busyGate) != 0)
        {
            return;
        }

        ReadControlsIntoDraft();
        var issues = Validate(CurrentStep);
        if (issues.Count > 0)
        {
            ShowValidationIssue(issues[0], moveToStep: false);
            return;
        }

        _currentStepIndex++;
        _maxVisitedStepIndex = Math.Max(_maxVisitedStepIndex, _currentStepIndex);
        UpdateStepPresentation(moveFocus: true);
    }

    private async void FinishButton_Click(object sender, RoutedEventArgs e) =>
        await FinishAsync();

    private async Task FinishAsync()
    {
        if (_draft is null || _policy is null ||
            Interlocked.CompareExchange(ref _busyGate, 1, 0) != 0)
        {
            return;
        }

        ReadControlsIntoDraft();
        var issues = SetupWizardAdvisor.ValidateAll(_draft, _policy, _availableMountPoints);
        if (issues.Count > 0)
        {
            Interlocked.Exchange(ref _busyGate, 0);
            ShowValidationIssue(issues[0], moveToStep: true);
            return;
        }

        SetBusy(true);
        try
        {
            var candidate = CloneDraft(_draft);
            LastApplyResult = await InvokeApplyRequestedAsync(candidate);
            Complete(candidate, restorePreview: false);
        }
        catch
        {
            LastApplyResult = null;
            Interlocked.Exchange(ref _busyGate, 0);
            SetBusy(false);
            ValidationInfoBar.Title = AppText.Get("WizardValidationTitle");
            ValidationInfoBar.Message = AppText.Get("WizardErrorApply");
            ValidationInfoBar.Severity = InfoBarSeverity.Error;
            ValidationInfoBar.IsOpen = true;
            FinishButton.Focus(FocusState.Programmatic);
        }
    }

    private void CancelWizard()
    {
        if (!IsShowing || Volatile.Read(ref _busyGate) != 0)
        {
            return;
        }

        Complete(null, restorePreview: true);
    }

    private void Complete(SetupWizardDraft? result, bool restorePreview)
    {
        var completion = _completion;
        if (completion is null)
        {
            return;
        }

        if (restorePreview && _originalDraft is not null)
        {
            var original = CloneDraft(_originalDraft);
            AppText.SetLanguage(original.LanguageCode);
            try
            {
                AppearancePreviewRequested?.Invoke(original);
            }
            catch
            {
                // A preview restoration must not prevent the wizard from closing.
            }
        }

        Visibility = Visibility.Collapsed;
        UnsubscribeLanguageChanged();
        _completion = null;
        _draft = null;
        _originalDraft = null;
        _policy = null;
        _capabilities = null;
        Interlocked.Exchange(ref _busyGate, 0);
        completion.TrySetResult(result);
    }

    private void ApplyCapabilityDefaults()
    {
        if (_draft is null || _policy is null || _capabilities is null)
        {
            return;
        }

        if (!_capabilities.BrowserSessionAllowed)
        {
            _draft.AuthenticationMode = AuthenticationMode.MicrosoftGraph;
        }

        if (!_capabilities.InteractiveSignInAllowed)
        {
            _draft.ConnectNow = false;
        }

        if (!_capabilities.WinFspAvailable &&
            !_policy.IsFieldManaged("autoStartVirtualDrive"))
        {
            _draft.AutoStartVirtualDrive = false;
        }
    }

    private void PopulateControls()
    {
        if (_draft is null || _policy is null || _capabilities is null)
        {
            return;
        }

        _isPopulating = true;
        try
        {
            SelectComboItemByTag(LanguageComboBox, _draft.LanguageCode);
            SelectComboItemByTag(ThemeComboBox, _draft.ThemeMode.ToString());
            HighContrastToggle.IsOn = _draft.HighContrastEnabled;
            GraphAccessRadio.IsChecked = _draft.AuthenticationMode == AuthenticationMode.MicrosoftGraph;
            BrowserAccessRadio.IsChecked = _draft.AuthenticationMode == AuthenticationMode.BrowserSession;
            ClientIdTextBox.Text = _draft.ClientId;
            TenantTextBox.Text = _draft.TenantId;
            BrowserUrlTextBox.Text = _draft.BrowserSessionStartUrl;
            BrowserKeepAliveToggle.IsOn = _draft.BrowserKeepSessionAlive;

            var mountItems = _availableMountPoints.ToList();
            if (!mountItems.Contains(_draft.MountPoint, StringComparer.OrdinalIgnoreCase))
            {
                mountItems.Insert(0, _draft.MountPoint);
            }

            MountPointComboBox.ItemsSource = mountItems;
            MountPointComboBox.SelectedItem = mountItems.FirstOrDefault(item =>
                string.Equals(item, _draft.MountPoint, StringComparison.OrdinalIgnoreCase));
            AutoDriveToggle.IsOn = _draft.AutoStartVirtualDrive;
            StartWithWindowsToggle.IsOn = _draft.StartWithWindows;
            StartMinimizedToggle.IsOn = _draft.StartMinimized;
            CacheMinutesNumberBox.Value = Math.Clamp(_draft.CacheMinutes, 1, 1440);
            NotificationsToggle.IsOn = _draft.NotificationsEnabled;
            QuietModeToggle.IsOn = _draft.QuietModeEnabled;
            OfflineLimitNumberBox.Value = Math.Clamp(_draft.OfflineCacheLimitMb, 128, 102400);
            PauseMeteredCheckBox.IsChecked = _draft.OfflinePauseOnMeteredNetwork;
            PauseBatteryCheckBox.IsChecked = _draft.OfflinePauseOnBattery;
            ConnectNowToggle.IsOn = _draft.ConnectNow;

            GraphAccessRadio.IsEnabled = _capabilities.CanEditAccess;
            BrowserAccessRadio.IsEnabled = _capabilities.CanEditAccess &&
                                           _capabilities.BrowserSessionAllowed;
            ClientIdTextBox.IsEnabled = !_policy.IsFieldManaged("clientId");
            TenantTextBox.IsEnabled = !_policy.IsFieldManaged("tenantId") &&
                                      !_policy.IsFieldManaged("allowedTenantIds");
            AutoDriveToggle.IsEnabled = !_policy.IsFieldManaged("autoStartVirtualDrive") &&
                                        _capabilities.WinFspAvailable;
            MountPointComboBox.IsEnabled = !_policy.IsFieldManaged("mountPoint");
            StartWithWindowsToggle.IsEnabled = !_policy.IsFieldManaged("startWithWindows");
            CacheMinutesNumberBox.IsEnabled = !_policy.IsFieldManaged("cacheMinutes");
            OfflineLimitNumberBox.IsEnabled = !_policy.IsFieldManaged("offlineCacheLimitMb");
            ConnectNowToggle.IsEnabled = _capabilities.InteractiveSignInAllowed;
            ManagedBadge.Visibility = _capabilities.IsEnterpriseManaged
                ? Visibility.Visible
                : Visibility.Collapsed;
            ManagedInfoBar.IsOpen = _capabilities.IsEnterpriseManaged;
            WinFspInfoBar.IsOpen = !_capabilities.WinFspAvailable;
        }
        finally
        {
            _isPopulating = false;
        }

        UpdateConnectionPanels();
        UpdateDependentControls();
    }

    private void ReadControlsIntoDraft()
    {
        if (_draft is null)
        {
            return;
        }

        if (LanguageComboBox.SelectedItem is ComboBoxItem { Tag: string languageCode })
        {
            _draft.LanguageCode = AppText.NormalizeLanguageCode(languageCode);
        }

        ReadAppearanceControls();
        _draft.AuthenticationMode = GraphAccessRadio.IsChecked == true
            ? AuthenticationMode.MicrosoftGraph
            : AuthenticationMode.BrowserSession;
        _draft.ClientId = ClientIdTextBox.Text;
        _draft.TenantId = TenantTextBox.Text;
        _draft.BrowserSessionStartUrl = BrowserUrlTextBox.Text;
        _draft.BrowserKeepSessionAlive = BrowserKeepAliveToggle.IsOn;
        _draft.MountPoint = MountPointComboBox.SelectedItem?.ToString() ?? _draft.MountPoint;
        _draft.AutoStartVirtualDrive = AutoDriveToggle.IsOn;
        _draft.StartWithWindows = StartWithWindowsToggle.IsOn;
        _draft.StartMinimized = StartMinimizedToggle.IsOn;
        _draft.CacheMinutes = ReadNumberBoxValue(CacheMinutesNumberBox);
        _draft.NotificationsEnabled = NotificationsToggle.IsOn;
        _draft.QuietModeEnabled = QuietModeToggle.IsOn;
        _draft.OfflineCacheLimitMb = ReadNumberBoxValue(OfflineLimitNumberBox);
        _draft.OfflinePauseOnMeteredNetwork = PauseMeteredCheckBox.IsChecked == true;
        _draft.OfflinePauseOnBattery = PauseBatteryCheckBox.IsChecked == true;
        _draft.ConnectNow = ConnectNowToggle.IsOn;
    }

    private void ReadAppearanceControls()
    {
        if (_draft is null)
        {
            return;
        }

        if (ThemeComboBox.SelectedItem is ComboBoxItem { Tag: string themeName } &&
            Enum.TryParse<AppThemeMode>(themeName, ignoreCase: true, out var themeMode))
        {
            _draft.ThemeMode = themeMode;
        }

        _draft.HighContrastEnabled = HighContrastToggle.IsOn;
    }

    private IReadOnlyList<SetupWizardValidationIssue> Validate(SetupWizardStep step)
    {
        if (_draft is null || _policy is null)
        {
            return Array.Empty<SetupWizardValidationIssue>();
        }

        return SetupWizardAdvisor.ValidateStep(_draft, step, _policy, _availableMountPoints);
    }

    private void ShowValidationIssue(SetupWizardValidationIssue issue, bool moveToStep)
    {
        if (moveToStep)
        {
            _currentStepIndex = (int)issue.Step;
            _maxVisitedStepIndex = Math.Max(_maxVisitedStepIndex, _currentStepIndex);
            UpdateStepPresentation(moveFocus: false);
        }

        ValidationInfoBar.Title = AppText.Get("WizardValidationTitle");
        ValidationInfoBar.Message = AppText.Get(issue.MessageKey);
        ValidationInfoBar.Severity = InfoBarSeverity.Error;
        ValidationInfoBar.IsOpen = true;
        FocusIssueField(issue.Field);
    }

    private void FocusIssueField(string? field)
    {
        Control target = field switch
        {
            nameof(SetupWizardDraft.LanguageCode) => LanguageComboBox,
            nameof(SetupWizardDraft.ThemeMode) => ThemeComboBox,
            nameof(SetupWizardDraft.AuthenticationMode) => GraphAccessRadio,
            nameof(SetupWizardDraft.ClientId) => ClientIdTextBox,
            nameof(SetupWizardDraft.TenantId) => TenantTextBox,
            nameof(SetupWizardDraft.BrowserSessionStartUrl) => BrowserUrlTextBox,
            nameof(SetupWizardDraft.MountPoint) => MountPointComboBox,
            nameof(SetupWizardDraft.CacheMinutes) => CacheMinutesNumberBox,
            nameof(SetupWizardDraft.OfflineCacheLimitMb) => OfflineLimitNumberBox,
            _ => NextButton
        };

        if (!target.IsEnabled || target.Visibility != Visibility.Visible)
        {
            target = GetFirstEnabledControlForCurrentStep();
        }

        DispatcherQueue.TryEnqueue(() => target.Focus(FocusState.Programmatic));
    }

    private void UpdateStepPresentation(bool moveFocus)
    {
        WelcomePanel.Visibility = CurrentStep == SetupWizardStep.Welcome ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = CurrentStep == SetupWizardStep.Appearance ? Visibility.Visible : Visibility.Collapsed;
        AccessPanel.Visibility = CurrentStep == SetupWizardStep.Access ? Visibility.Visible : Visibility.Collapsed;
        ConnectionPanel.Visibility = CurrentStep == SetupWizardStep.Connection ? Visibility.Visible : Visibility.Collapsed;
        WindowsIntegrationPanel.Visibility = CurrentStep == SetupWizardStep.WindowsIntegration ? Visibility.Visible : Visibility.Collapsed;
        OfflineAndNotificationsPanel.Visibility = CurrentStep == SetupWizardStep.OfflineAndNotifications ? Visibility.Visible : Visibility.Collapsed;
        ReviewPanel.Visibility = CurrentStep == SetupWizardStep.Review ? Visibility.Visible : Visibility.Collapsed;

        BackButton.Visibility = _currentStepIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = CurrentStep == SetupWizardStep.Review ? Visibility.Collapsed : Visibility.Visible;
        FinishButton.Visibility = CurrentStep == SetupWizardStep.Review ? Visibility.Visible : Visibility.Collapsed;
        CompactProgressBar.Value = _currentStepIndex + 1;
        ValidationInfoBar.IsOpen = false;

        UpdateRailPresentation();
        UpdateConnectionPanels();
        UpdateDependentControls();
        if (CurrentStep == SetupWizardStep.Review)
        {
            UpdateReviewSummary();
        }

        LocalizeCurrentStep();
        ContentScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        if (moveFocus)
        {
            DispatcherQueue.TryEnqueue(FocusCurrentStep);
        }
    }

    private void UpdateRailPresentation()
    {
        var buttons = GetRailButtons();
        var markers = GetRailMarkers();
        var selectedBackground = GetBrush("EasyShareAccentSoftBrush");
        var accentBackground = GetBrush("EasyShareAccentBrush");
        var regularBackground = GetBrush("EasyShareSurfaceSubtleBrush");
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        for (var index = 0; index < buttons.Length; index++)
        {
            buttons[index].IsEnabled = index <= _maxVisitedStepIndex;
            buttons[index].Background = index == _currentStepIndex
                ? selectedBackground
                : transparent;
            markers[index].Background = index <= _currentStepIndex
                ? accentBackground
                : regularBackground;
        }
    }

    private void UpdateConnectionPanels()
    {
        var graphSelected = GraphAccessRadio.IsChecked == true;
        GraphConnectionPanel.Visibility = graphSelected ? Visibility.Visible : Visibility.Collapsed;
        BrowserConnectionPanel.Visibility = graphSelected ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateDependentControls()
    {
        if (_policy is null || _capabilities is null)
        {
            return;
        }

        MountPointComboBox.IsEnabled = AutoDriveToggle.IsOn &&
                                       !_policy.IsFieldManaged("mountPoint") &&
                                       _capabilities.WinFspAvailable;
        StartMinimizedToggle.IsEnabled = StartWithWindowsToggle.IsOn;
        QuietModeToggle.IsEnabled = NotificationsToggle.IsOn;
    }

    private void UpdateResponsiveLayout(double width)
    {
        var showRail = width >= RailBreakpoint && ActualHeight >= 740;
        StepRail.Visibility = showRail ? Visibility.Visible : Visibility.Collapsed;
        StepRailColumn.Width = showRail ? new GridLength(288) : new GridLength(0);
        CompactProgressPanel.Visibility = showRail ? Visibility.Collapsed : Visibility.Visible;
        ShellGrid.Margin = width < 560
            ? new Thickness(16, 14, 16, 16)
            : width < RailBreakpoint
                ? new Thickness(24, 20, 24, 22)
                : new Thickness(36, 24, 36, 28);
        ContentScrollViewer.Padding = width < 560
            ? new Thickness(18, 20, 18, 22)
            : new Thickness(30, 26, 30, 30);

        var stackCards = width < StackedCardsBreakpoint;
        OfflineColumnsGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        OfflineColumnsGrid.ColumnDefinitions[1].Width = stackCards
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(CacheCard, 0);
        Grid.SetColumn(CacheCard, 0);
        Grid.SetColumnSpan(CacheCard, stackCards ? 2 : 1);
        Grid.SetRow(OfflineStorageCard, stackCards ? 1 : 0);
        Grid.SetColumn(OfflineStorageCard, stackCards ? 0 : 1);
        Grid.SetColumnSpan(OfflineStorageCard, stackCards ? 2 : 1);

        var stackFooter = width < StackedFooterBreakpoint;
        FooterGrid.RowDefinitions[1].Height = stackFooter ? GridLength.Auto : new GridLength(0);
        Grid.SetRow(ActionButtonsPanel, stackFooter ? 1 : 0);
        Grid.SetColumn(ActionButtonsPanel, stackFooter ? 0 : 2);
        Grid.SetColumnSpan(ActionButtonsPanel, stackFooter ? 3 : 1);
    }

    private void SetBusy(bool busy)
    {
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyProgressRing.IsActive = busy;
        CancelButton.IsEnabled = !busy;
        BackButton.IsEnabled = !busy && _currentStepIndex > 0;
        NextButton.IsEnabled = !busy;
        FinishButton.IsEnabled = !busy;
        foreach (var railButton in GetRailButtons())
        {
            railButton.IsEnabled = !busy &&
                                   int.TryParse(railButton.Tag?.ToString(), out var index) &&
                                   index <= _maxVisitedStepIndex;
        }
    }

    private async Task<SetupWizardApplyResult> InvokeApplyRequestedAsync(SetupWizardDraft draft)
    {
        var handlers = ApplyRequested;
        if (handlers is null)
        {
            throw new InvalidOperationException("The setup wizard has no persistence handler.");
        }

        SetupWizardApplyResult? result = null;
        foreach (Func<SetupWizardDraft, Task<SetupWizardApplyResult>> handler in handlers.GetInvocationList())
        {
            result = await handler(CloneDraft(draft));
        }

        return result ?? new SetupWizardApplyResult(false, false);
    }

    private void InvokeAppearancePreview()
    {
        if (_draft is null)
        {
            return;
        }

        try
        {
            AppearancePreviewRequested?.Invoke(CloneDraft(_draft));
        }
        catch
        {
            // Preview failures are non-fatal; final application is validated on Finish.
        }
    }

    private void SubscribeLanguageChanged()
    {
        if (_languageSubscribed)
        {
            return;
        }

        AppText.LanguageChanged += AppText_LanguageChanged;
        _languageSubscribed = true;
    }

    private void UnsubscribeLanguageChanged()
    {
        if (!_languageSubscribed)
        {
            return;
        }

        AppText.LanguageChanged -= AppText_LanguageChanged;
        _languageSubscribed = false;
    }

    private void AppText_LanguageChanged(object? sender, EventArgs e)
    {
        Localize();
        if (CurrentStep == SetupWizardStep.Review)
        {
            UpdateReviewSummary();
        }
    }

    private void Localize()
    {
        WizardTitleText.Text = AppText.Get("WizardTitle");
        WizardSubtitleText.Text = AppText.Get("WizardSubtitle");
        ManagedBadgeText.Text = AppText.Get("WizardManagedBadge");

        SetRailText(0, "WizardStepWelcomeTitle", "WizardStepWelcomeDescription");
        SetRailText(1, "WizardStepAppearanceTitle", "WizardStepAppearanceDescription");
        SetRailText(2, "WizardStepAccessTitle", "WizardStepAccessDescription");
        SetRailText(3, "WizardStepConnectionTitle", "WizardStepConnectionDescription");
        SetRailText(4, "WizardStepWindowsTitle", "WizardStepWindowsDescription");
        SetRailText(5, "WizardStepOfflineTitle", "WizardStepOfflineDescription");
        SetRailText(6, "WizardStepReviewTitle", "WizardStepReviewDescription");

        WelcomeLeadText.Text = AppText.Get("WizardWelcomeLead");
        LanguageLabelText.Text = AppText.Get("SettingsLanguageHeader");
        LanguageHelpText.Text = AppText.Get("SettingsLanguageHelp");
        PortugueseLanguageItem.Content = AppText.Get("SettingsLanguagePortuguese");
        EnglishLanguageItem.Content = AppText.Get("SettingsLanguageEnglish");
        GuidedBenefitTitleText.Text = AppText.Get("WizardWelcomeGuidedTitle");
        GuidedBenefitMessageText.Text = AppText.Get("WizardWelcomeGuidedMessage");
        SecureBenefitTitleText.Text = AppText.Get("WizardWelcomeSecureTitle");
        SecureBenefitMessageText.Text = AppText.Get("WizardWelcomeSecureMessage");
        FlexibleBenefitTitleText.Text = AppText.Get("WizardWelcomeFlexibleTitle");
        FlexibleBenefitMessageText.Text = AppText.Get("WizardWelcomeFlexibleMessage");

        AppearanceLeadText.Text = AppText.Get("WizardAppearanceLead");
        ThemeLabelText.Text = AppText.Get("SettingsThemeHeader");
        SystemThemeItem.Content = AppText.Get("SettingsThemeSystem");
        LightThemeItem.Content = AppText.Get("SettingsThemeLight");
        DarkThemeItem.Content = AppText.Get("SettingsThemeDark");
        HighContrastToggle.Header = AppText.Get("WizardHighContrastLabel");
        HighContrastToggle.OnContent = AppText.Get("WizardValueEnabled");
        HighContrastToggle.OffContent = AppText.Get("WizardValueDisabled");
        AutomationProperties.SetHelpText(HighContrastToggle, AppText.Get("WizardHighContrastHelp"));
        AppearancePreviewTitleText.Text = AppText.Get("WizardAppearancePreviewTitle");
        AppearancePreviewMessageText.Text = AppText.Get("WizardAppearancePreviewMessage");

        ManagedInfoBar.Title = AppText.Get("SettingsManagedTitle");
        ManagedInfoBar.Message = AppText.Get("SettingsManagedMessage");
        GraphAccessTitleText.Text = AppText.Get("SettingsAccessModeGraph");
        GraphAccessDescriptionText.Text = AppText.Get("WizardGraphAccessDescription");
        BrowserAccessTitleText.Text = AppText.Get("SettingsAccessModeBrowser");
        BrowserAccessDescriptionText.Text = AppText.Get("WizardBrowserAccessDescription");
        LocalizeRecommendation();

        GraphConnectionIntroText.Text = AppText.Get("WizardGraphConnectionIntro");
        ClientIdLabelText.Text = AppText.Get("WizardClientIdLabel");
        ClientIdTextBox.PlaceholderText = AppText.Get("WizardClientIdPlaceholder");
        TenantLabelText.Text = AppText.Get("WizardTenantLabel");
        TenantTextBox.PlaceholderText = AppText.Get("WizardTenantPlaceholder");
        GraphSecurityInfoBar.Title = AppText.Get("WizardGraphSecurityTitle");
        GraphSecurityInfoBar.Message = AppText.Get("WizardGraphSecurityMessage");
        BrowserConnectionIntroText.Text = AppText.Get("WizardBrowserConnectionIntro");
        BrowserUrlLabelText.Text = AppText.Get("SettingsBrowserStartUrl");
        BrowserUrlTextBox.PlaceholderText = AppText.Get("SettingsBrowserStartUrlPlaceholder");
        BrowserUrlHelpText.Text = AppText.Get("WizardBrowserUrlHelp");
        BrowserKeepAliveToggle.Header = AppText.Get("WizardBrowserKeepAliveLabel");
        BrowserKeepAliveToggle.OnContent = AppText.Get("WizardValueEnabled");
        BrowserKeepAliveToggle.OffContent = AppText.Get("WizardValueDisabled");
        BrowserKeepAliveHelpText.Text = AppText.Get("WizardBrowserKeepAliveHelp");

        WindowsLeadText.Text = AppText.Get("WizardWindowsLead");
        WinFspInfoBar.Title = AppText.Get("WizardWinFspMissingTitle");
        WinFspInfoBar.Message = AppText.Get("WizardWinFspMissingMessage");
        AutoDriveToggle.Header = AppText.Get("WizardAutoDriveLabel");
        AutoDriveToggle.OnContent = AppText.Get("WizardValueEnabled");
        AutoDriveToggle.OffContent = AppText.Get("WizardValueDisabled");
        AutoDriveHelpText.Text = AppText.Get("WizardAutoDriveHelp");
        MountPointLabelText.Text = AppText.Get("SettingsMountPoint");
        MountPointHelpText.Text = AppText.Get("WizardMountPointHelp");
        StartWithWindowsToggle.Header = AppText.Get("SettingsStartWithWindows");
        StartWithWindowsToggle.OnContent = AppText.Get("WizardValueEnabled");
        StartWithWindowsToggle.OffContent = AppText.Get("WizardValueDisabled");
        StartWithWindowsHelpText.Text = AppText.Get("WizardStartWithWindowsHelp");
        StartMinimizedToggle.Header = AppText.Get("SettingsStartMinimized");
        StartMinimizedToggle.OnContent = AppText.Get("WizardValueEnabled");
        StartMinimizedToggle.OffContent = AppText.Get("WizardValueDisabled");
        StartMinimizedHelpText.Text = AppText.Get("WizardStartMinimizedHelp");

        OfflineLeadText.Text = AppText.Get("WizardOfflineLead");
        CacheTitleText.Text = AppText.Get("WizardCacheTitle");
        CacheMinutesLabelText.Text = AppText.Get("SettingsCacheMinutes");
        CacheHelpText.Text = AppText.Get("WizardCacheHelp");
        OfflineStorageTitleText.Text = AppText.Get("WizardOfflineStorageTitle");
        OfflineLimitLabelText.Text = AppText.Get("SettingsOfflineCacheLimit");
        OfflineLimitHelpText.Text = AppText.Get("WizardOfflineLimitHelp");
        PauseMeteredCheckBox.Content = AppText.Get("SettingsOfflinePauseMetered");
        PauseBatteryCheckBox.Content = AppText.Get("SettingsOfflinePauseBattery");
        NotificationsTitleText.Text = AppText.Get("WizardNotificationsTitle");
        NotificationsToggle.Header = AppText.Get("SettingsNotificationsEnabled");
        NotificationsToggle.OnContent = AppText.Get("WizardValueEnabled");
        NotificationsToggle.OffContent = AppText.Get("WizardValueDisabled");
        NotificationsHelpText.Text = AppText.Get("SettingsNotificationsHelp");
        QuietModeToggle.Header = AppText.Get("WizardQuietModeLabel");
        QuietModeToggle.OnContent = AppText.Get("WizardValueEnabled");
        QuietModeToggle.OffContent = AppText.Get("WizardValueDisabled");
        QuietModeHelpText.Text = AppText.Get("WizardQuietModeHelp");

        ReviewLeadText.Text = AppText.Get("WizardReviewLead");
        ReviewConfigurationTitleText.Text = AppText.Get("WizardReviewConfigurationTitle");
        ReviewSecurityInfoBar.Title = AppText.Get("WizardReviewSecurityTitle");
        ReviewSecurityInfoBar.Message = AppText.Get("WizardReviewSecurityMessage");
        ConnectNowToggle.Header = AppText.Get("WizardConnectNowLabel");
        ConnectNowToggle.OnContent = AppText.Get("WizardValueEnabled");
        ConnectNowToggle.OffContent = AppText.Get("WizardValueDisabled");
        ConnectNowHelpText.Text = AppText.Get("WizardConnectNowHelp");

        CancelButton.Content = AppText.Get("CommonCancel");
        BackButton.Content = AppText.Get("ActionBack");
        NextButton.Content = AppText.Get("WizardActionNext");
        FinishButton.Content = AppText.Get("WizardActionFinish");
        BusyText.Text = AppText.Get("WizardApplying");
        ValidationInfoBar.Title = AppText.Get("WizardValidationTitle");

        AutomationProperties.SetName(this, AppText.Get("WizardTitle"));
        AutomationProperties.SetName(StepRail, AppText.Get("WizardStepsAccessibleName"));
        AutomationProperties.SetName(ContentScrollViewer, AppText.Get("WizardContentAccessibleName"));
        AutomationProperties.SetName(LanguageComboBox, AppText.Get("SettingsLanguageHeader"));
        AutomationProperties.SetHelpText(LanguageComboBox, AppText.Get("SettingsLanguageHelp"));
        AutomationProperties.SetName(ThemeComboBox, AppText.Get("SettingsThemeHeader"));
        AutomationProperties.SetName(GraphAccessRadio, AppText.Get("SettingsAccessModeGraph"));
        AutomationProperties.SetHelpText(GraphAccessRadio, AppText.Get("WizardGraphAccessDescription"));
        AutomationProperties.SetName(BrowserAccessRadio, AppText.Get("SettingsAccessModeBrowser"));
        AutomationProperties.SetHelpText(BrowserAccessRadio, AppText.Get("WizardBrowserAccessDescription"));
        AutomationProperties.SetName(ClientIdTextBox, AppText.Get("WizardClientIdLabel"));
        AutomationProperties.SetName(TenantTextBox, AppText.Get("WizardTenantLabel"));
        AutomationProperties.SetName(BrowserUrlTextBox, AppText.Get("SettingsBrowserStartUrl"));
        AutomationProperties.SetName(MountPointComboBox, AppText.Get("SettingsMountPoint"));
        AutomationProperties.SetName(CacheMinutesNumberBox, AppText.Get("SettingsCacheMinutes"));
        AutomationProperties.SetName(OfflineLimitNumberBox, AppText.Get("SettingsOfflineCacheLimit"));
        AutomationProperties.SetName(CancelButton, AppText.Get("CommonCancel"));
        AutomationProperties.SetName(BackButton, AppText.Get("ActionBack"));
        AutomationProperties.SetName(NextButton, AppText.Get("WizardActionNext"));
        AutomationProperties.SetName(FinishButton, AppText.Get("WizardActionFinish"));

        LocalizeCurrentStep();
    }

    private void LocalizeCurrentStep()
    {
        var (titleKey, descriptionKey) = GetStepKeys(CurrentStep);
        CurrentStepTitleText.Text = AppText.Get(titleKey);
        CurrentStepDescriptionText.Text = AppText.Get(descriptionKey);
        CompactProgressText.Text = AppText.Format(
            "WizardStepProgressFormat",
            _currentStepIndex + 1,
            StepCount,
            AppText.Get(titleKey));
    }

    private void LocalizeRecommendation()
    {
        if (_draft is null || _policy is null || _capabilities is null)
        {
            return;
        }

        var effectiveClientId = _policy.Policy.ClientId ?? _draft.ClientId;
        var graphRecommended = !_capabilities.BrowserSessionAllowed ||
                               SetupWizardAdvisor.HasValidClientId(effectiveClientId);
        RecommendationInfoBar.Title = AppText.Get("WizardAccessRecommendationTitle");
        RecommendationInfoBar.Message = AppText.Get(graphRecommended
            ? "WizardAccessRecommendationGraphMessage"
            : "WizardAccessRecommendationBrowserMessage");
    }

    private void UpdateReviewSummary()
    {
        if (_draft is null || _capabilities is null)
        {
            return;
        }

        ReadControlsIntoDraft();
        var language = string.Equals(_draft.LanguageCode, AppText.EnglishLanguageCode, StringComparison.OrdinalIgnoreCase)
            ? AppText.Get("SettingsLanguageEnglish")
            : AppText.Get("SettingsLanguagePortuguese");
        var theme = AppText.Get(_draft.ThemeMode switch
        {
            AppThemeMode.Light => "SettingsThemeLight",
            AppThemeMode.Dark => "SettingsThemeDark",
            _ => "SettingsThemeSystem"
        });
        var access = AppText.Get(_draft.AuthenticationMode == AuthenticationMode.MicrosoftGraph
            ? "SettingsAccessModeGraph"
            : "SettingsAccessModeBrowser");
        var connection = _draft.AuthenticationMode == AuthenticationMode.MicrosoftGraph
            ? AppText.Format("WizardReviewGraphConnectionFormat", _draft.TenantId)
            : AppText.Format("WizardReviewBrowserConnectionFormat", _draft.BrowserSessionStartUrl);
        var drive = !_capabilities.WinFspAvailable
            ? AppText.Get("WizardReviewDriveUnavailable")
            : _draft.AutoStartVirtualDrive
                ? AppText.Format("WizardReviewDriveFormat", _draft.MountPoint)
                : AppText.Get("WizardValueDisabled");
        var startup = _draft.StartWithWindows
            ? AppText.Get("WizardValueEnabled")
            : AppText.Get("WizardValueDisabled");
        var notifications = _draft.NotificationsEnabled
            ? AppText.Get("WizardValueEnabled")
            : AppText.Get("WizardValueDisabled");

        ReviewSummaryText.Text = AppText.Format(
            "WizardReviewSummaryFormat",
            language,
            theme,
            access,
            connection,
            drive,
            startup,
            _draft.CacheMinutes,
            _draft.OfflineCacheLimitMb,
            notifications);
    }

    private void SetRailText(int index, string titleKey, string descriptionKey)
    {
        var titles = new[]
        {
            WelcomeRailTitle, AppearanceRailTitle, AccessRailTitle, ConnectionRailTitle,
            WindowsRailTitle, OfflineRailTitle, ReviewRailTitle
        };
        var descriptions = new[]
        {
            WelcomeRailDescription, AppearanceRailDescription, AccessRailDescription,
            ConnectionRailDescription, WindowsRailDescription, OfflineRailDescription,
            ReviewRailDescription
        };

        titles[index].Text = AppText.Get(titleKey);
        descriptions[index].Text = AppText.Get(descriptionKey);
        AutomationProperties.SetName(GetRailButtons()[index],
            AppText.Format("WizardStepAccessibleNameFormat", index + 1, StepCount, AppText.Get(titleKey)));
    }

    private static (string TitleKey, string DescriptionKey) GetStepKeys(SetupWizardStep step) => step switch
    {
        SetupWizardStep.Welcome => ("WizardStepWelcomeTitle", "WizardStepWelcomeDescription"),
        SetupWizardStep.Appearance => ("WizardStepAppearanceTitle", "WizardStepAppearanceDescription"),
        SetupWizardStep.Access => ("WizardStepAccessTitle", "WizardStepAccessDescription"),
        SetupWizardStep.Connection => ("WizardStepConnectionTitle", "WizardStepConnectionDescription"),
        SetupWizardStep.WindowsIntegration => ("WizardStepWindowsTitle", "WizardStepWindowsDescription"),
        SetupWizardStep.OfflineAndNotifications => ("WizardStepOfflineTitle", "WizardStepOfflineDescription"),
        _ => ("WizardStepReviewTitle", "WizardStepReviewDescription")
    };

    private void FocusCurrentStep()
    {
        GetFirstEnabledControlForCurrentStep().Focus(FocusState.Programmatic);
    }

    private Control GetFirstEnabledControlForCurrentStep()
    {
        Control[] candidates = CurrentStep switch
        {
            SetupWizardStep.Welcome => [LanguageComboBox],
            SetupWizardStep.Appearance => [ThemeComboBox, HighContrastToggle],
            SetupWizardStep.Access => [
                GraphAccessRadio.IsChecked == true ? GraphAccessRadio : BrowserAccessRadio,
                GraphAccessRadio,
                BrowserAccessRadio],
            SetupWizardStep.Connection => GraphAccessRadio.IsChecked == true
                ? [ClientIdTextBox, TenantTextBox]
                : [BrowserUrlTextBox, BrowserKeepAliveToggle],
            SetupWizardStep.WindowsIntegration => [
                AutoDriveToggle,
                MountPointComboBox,
                StartWithWindowsToggle,
                StartMinimizedToggle],
            SetupWizardStep.OfflineAndNotifications => [
                CacheMinutesNumberBox,
                OfflineLimitNumberBox,
                NotificationsToggle,
                QuietModeToggle],
            _ => [ConnectNowToggle]
        };

        return candidates.FirstOrDefault(control =>
                   control.IsEnabled && control.Visibility == Visibility.Visible) ??
               GetEnabledNavigationControl();
    }

    private Control GetEnabledNavigationControl()
    {
        if (NextButton is { IsEnabled: true, Visibility: Visibility.Visible })
        {
            return NextButton;
        }

        if (FinishButton is { IsEnabled: true, Visibility: Visibility.Visible })
        {
            return FinishButton;
        }

        return CancelButton;
    }

    private Button[] GetRailButtons() =>
    [
        WelcomeRailButton, AppearanceRailButton, AccessRailButton, ConnectionRailButton,
        WindowsRailButton, OfflineRailButton, ReviewRailButton
    ];

    private Border[] GetRailMarkers() =>
    [
        WelcomeRailMarker, AppearanceRailMarker, AccessRailMarker, ConnectionRailMarker,
        WindowsRailMarker, OfflineRailMarker, ReviewRailMarker
    ];

    private static Brush GetBrush(string key) =>
        Application.Current.Resources[key] as Brush ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private static int ReadNumberBoxValue(NumberBox numberBox) =>
        double.IsNaN(numberBox.Value)
            ? 0
            : (int)Math.Round(numberBox.Value, MidpointRounding.AwayFromZero);

    private static void SelectComboItemByTag(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static SetupWizardDraft CloneDraft(SetupWizardDraft source) => new()
    {
        LanguageCode = source.LanguageCode,
        ThemeMode = source.ThemeMode,
        HighContrastEnabled = source.HighContrastEnabled,
        AuthenticationMode = source.AuthenticationMode,
        ClientId = source.ClientId,
        TenantId = source.TenantId,
        BrowserSessionStartUrl = source.BrowserSessionStartUrl,
        BrowserKeepSessionAlive = source.BrowserKeepSessionAlive,
        MountPoint = source.MountPoint,
        AutoStartVirtualDrive = source.AutoStartVirtualDrive,
        StartWithWindows = source.StartWithWindows,
        StartMinimized = source.StartMinimized,
        CacheMinutes = source.CacheMinutes,
        NotificationsEnabled = source.NotificationsEnabled,
        QuietModeEnabled = source.QuietModeEnabled,
        OfflineCacheLimitMb = source.OfflineCacheLimitMb,
        OfflinePauseOnMeteredNetwork = source.OfflinePauseOnMeteredNetwork,
        OfflinePauseOnBattery = source.OfflinePauseOnBattery,
        ConnectNow = source.ConnectNow
    };
}
