using System.Text.RegularExpressions;
using Xunit;

namespace EasyShare.Tests;

public sealed class WindowLifecycleContractTests
{
    [Fact]
    [Trait("Gate", "Stage02")]
    public void ExitFlowQuiescesTheDriveBeforeSnapshotAndStopsOnlyAfterConfirmation()
    {
        var source = ReadRepositoryFile("src", "EasyShare", "MainWindow.xaml.cs");
        var requestExit = ExtractMethod(source, "RequestExitCoreAsync");

        AssertOrdered(
            requestExit,
            "VirtualDrive.QuiesceForShutdown()",
            "UploadQueue.GetActiveJobsAsync()",
            "dialog.ShowAsync()",
            "CompleteExitAsync()");
        Assert.Contains("ResumeVirtualDriveAfterCancelledExitAsync", requestExit, StringComparison.Ordinal);

        var completeExit = ExtractMethod(source, "CompleteExitAsync");
        AssertOrdered(
            completeExit,
            "UploadQueue.StopAsync()",
            "App.ShutdownServices()",
            "_exitRequested = true",
            "Close()");
    }

    [Fact]
    [Trait("Gate", "Stage02")]
    public void CloseMinimizeAndTrayExitShareOneNonReentrantDecisionGate()
    {
        var source = ReadRepositoryFile("src", "EasyShare", "MainWindow.xaml.cs");

        Assert.Contains("SemaphoreSlim _decisionGate", source, StringComparison.Ordinal);
        Assert.Contains("_decisionGate.WaitAsync(0)", source, StringComparison.Ordinal);
        Assert.Contains("RunDecisionAsync(", ExtractMethod(source, "ExitFromTray"), StringComparison.Ordinal);
        Assert.Contains("RunDecisionAsync(", ExtractMethod(source, "HandleMinimizeAsync"), StringComparison.Ordinal);
        Assert.Contains("RunDecisionAsync(", ExtractMethod(source, "MainWindow_Closed"), StringComparison.Ordinal);
        Assert.DoesNotContain("_closeDecisionInProgress", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_minimizeDecisionInProgress", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_exitDecisionInProgress", source, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Gate", "Stage02")]
    public void TrayRequiresAWorkingSubclassIconAndVersionFourContract()
    {
        var source = ReadRepositoryFile("src", "EasyShare", "Services", "TrayIconService.cs");

        Assert.Contains("IsOperational", source, StringComparison.Ordinal);
        Assert.Contains("TryHide()", source, StringComparison.Ordinal);
        Assert.Contains("TaskbarCreated", source, StringComparison.Ordinal);
        Assert.Contains("NimSetVersion", source, StringComparison.Ordinal);
        Assert.Contains("NotifyIconVersion4", source, StringComparison.Ordinal);
        Assert.Contains("NifShowTip", source, StringComparison.Ordinal);
        Assert.Contains("NinSelect", source, StringComparison.Ordinal);
        Assert.Contains("NinKeySelect", source, StringComparison.Ordinal);
        Assert.Contains("PointFromCallback(wParam)", source, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"if\s*\(\s*!ShellNotifyIcon\(NimAdd,\s*data\)\s*\).*?SetOperational\(false",
                RegexOptions.Singleline),
            source);
        Assert.Matches(
            new Regex(
                @"if\s*\(\s*!ShellNotifyIcon\(NimSetVersion,\s*data\)\s*\).*?SetOperational\(false",
                RegexOptions.Singleline),
            source);
    }

    [Fact]
    [Trait("Gate", "Stage02")]
    public void UnavailableTrayKeepsTheWindowVisibleAndStartupHideCannotSwallowNextMinimize()
    {
        var window = ReadRepositoryFile("src", "EasyShare", "MainWindow.xaml.cs");
        var page = ReadRepositoryFile("src", "EasyShare", "MainPage.xaml.cs");
        var hide = ExtractMethod(window, "HideToTray");
        var startupHide = ExtractMethod(window, "MinimizeToTrayForStartup");

        Assert.Contains("_trayIconService?.TryHide() == true", hide, StringComparison.Ordinal);
        Assert.Contains("RestoreAndActivate()", hide, StringComparison.Ordinal);
        Assert.Contains("MinimizeToTrayForStartup", page, StringComparison.Ordinal);
        Assert.Contains("HideToTray()", startupHide, StringComparison.Ordinal);
        Assert.DoesNotContain("_suppressNextMinimize", window, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowWindow(hwnd, 6)", startupHide, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Gate", "Stage02")]
    public void ApplicationShutdownDisposesEveryResourceBestEffort()
    {
        var services = ReadRepositoryFile("src", "EasyShare", "Services", "AppServices.cs");
        var app = ReadRepositoryFile("src", "EasyShare", "App.xaml.cs");

        Assert.Contains("DisposeBestEffort(nameof(VirtualDrive)", services, StringComparison.Ordinal);
        Assert.Contains("DisposeBestEffort(nameof(UploadQueue)", services, StringComparison.Ordinal);
        Assert.Contains("DisposeBestEffort(nameof(AppUpdate)", services, StringComparison.Ordinal);
        Assert.Contains("DisposeBestEffort(nameof(Notifications)", services, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)", ExtractMethod(services, "DisposeBestEffort"), StringComparison.Ordinal);
        Assert.Contains("DisposeBestEffort(nameof(AppServices), Services)", app, StringComparison.Ordinal);
        Assert.Contains("single-instance mutex", app, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Gate", "SharePointDelete")]
    public void VirtualDriveDeleteUsesCleanupFlagAndQueuesNoRemoteCallFromWinFsp()
    {
        var source = ReadRepositoryFile(
            "src",
            "EasyShare",
            "Services",
            "VirtualDriveService.cs");
        var cleanup = ExtractMethod(source, "Cleanup");
        var flush = ExtractMethod(source, "FlushWritableHandle");
        var setDelete = ExtractMethod(source, "SetDelete");

        Assert.Contains("(flags & CleanupDelete) != 0", cleanup, StringComparison.Ordinal);
        AssertOrdered(cleanup, "_uploadQueue.ArmDeleteIntent(", "MarkDeletePending()");
        Assert.DoesNotContain("_contentService.DeleteItem", cleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("_deleteOnClose", source, StringComparison.Ordinal);
        AssertOrdered(
            setDelete,
            "_uploadQueue.PrepareDeleteIntent(",
            "_stagedDeleteIntents[intentKey] = intent.Id",
            "return STATUS_SUCCESS");
        Assert.Contains("_uploadQueue.CancelDeleteIntent(", setDelete, StringComparison.Ordinal);
        Assert.Contains(
            "return STATUS_ACCESS_DENIED;",
            setDelete,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_contentService.DeleteItem", setDelete, StringComparison.Ordinal);
        Assert.Contains("handle.IsDeletePending", flush, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Gate", "SharePointDelete")]
    public void ConfirmedDeleteInvalidatesRemoteAndLocalListingSources()
    {
        var browser = ReadRepositoryFile(
            "src",
            "EasyShare",
            "Services",
            "SharePointBrowserContentService.cs");
        var completion = ExtractMethod(browser, "CompleteSuccessfulDeleteAsync");
        var listing = ExtractMethod(browser, "ListDirectoryAsync");
        var explorerListing = ExtractMethod(browser, "ListDirectoryForExplorerAsync");
        var invalidation = ExtractMethod(browser, "InvalidateDeletePathCaches");

        AssertOrdered(
            completion,
            "InvalidateDeletePathCaches(",
            "_offlineCache",
            "RemovePathAsync(",
            "_contentIndex",
            "RemoveItemFromAllScopesAsync(");
        Assert.Contains("FilterDeletedItems(", listing, StringComparison.Ordinal);
        Assert.Contains("FilterDeletedItems(", explorerListing, StringComparison.Ordinal);
        Assert.Contains("_directoryCache.TryRemove", invalidation, StringComparison.Ordinal);
        Assert.Contains("_fileCache.TryRemove", invalidation, StringComparison.Ordinal);
        Assert.Contains(
            "_database.InvalidateDeletePathDirectoryCache",
            invalidation,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Gate", "Stage02")]
    public void OperationsCenterAnnouncesStateProgressRetryFailureAndRecommendedAction()
    {
        var xaml = ReadRepositoryFile(
            "src",
            "EasyShare",
            "Controls",
            "OperationsCenterControl.xaml");

        Assert.Matches(
            new Regex(
                @"AutomationProperties\.LiveSetting=""Polite""[^>]*Text=""\{x:Bind StateText\}""",
                RegexOptions.Singleline),
            xaml);
        Assert.Matches(
            new Regex(
                @"AutomationProperties\.LiveSetting=""Polite""[^>]*Text=""\{x:Bind ProgressText\}""",
                RegexOptions.Singleline),
            xaml);
        Assert.Matches(
            new Regex(
                @"AutomationProperties\.LiveSetting=""Polite""[^>]*Text=""\{x:Bind NextAttemptText\}""",
                RegexOptions.Singleline),
            xaml);
        Assert.Matches(
            new Regex(
                @"AutomationProperties\.LiveSetting=""Assertive""[^>]*Text=""\{x:Bind FailureSummary\}""",
                RegexOptions.Singleline),
            xaml);
        Assert.Matches(
            new Regex(
                @"AutomationProperties\.LiveSetting=""Polite""[^>]*Text=""\{x:Bind RecommendedActionText\}""",
                RegexOptions.Singleline),
            xaml);
        Assert.Contains(
            "AutomationProperties.Name=\"{x:Bind RetryAutomationName}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{x:Bind ExportAutomationName}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{x:Bind DetailsAutomationName}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Gate", "Stage02")]
    public void TraySummaryWaitsUntilDatabaseInitializationAndMigrationsComplete()
    {
        var window = ReadRepositoryFile("src", "EasyShare", "MainWindow.xaml.cs");
        var page = ReadRepositoryFile("src", "EasyShare", "MainPage.xaml.cs");
        var pageLoaded = ExtractMethod(page, "Page_Loaded");

        Assert.DoesNotContain("_ = RefreshTrayStatusAsync();", window, StringComparison.Ordinal);
        AssertOrdered(
            pageLoaded,
            "await ViewModel.LoadAsync()",
            "await mainWindow.RefreshTrayUploadStatusAsync()");
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var declaration = Regex.Match(
            source,
            $@"(?m)^\s*(?:public|private|internal|protected)\s+.*\b{Regex.Escape(methodName)}\s*\(");
        Assert.True(declaration.Success, $"Method {methodName} was not found.");
        var signature = declaration.Index;
        var openingBrace = source.IndexOf('{', signature);
        Assert.True(openingBrace >= 0, $"Method {methodName} has no body.");

        var depth = 0;
        for (var index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}' && --depth == 0)
            {
                return source[openingBrace..(index + 1)];
            }
        }

        throw new InvalidDataException($"Method {methodName} has an incomplete body.");
    }

    private static void AssertOrdered(string source, params string[] fragments)
    {
        var previous = -1;
        foreach (var fragment in fragments)
        {
            var current = source.IndexOf(fragment, StringComparison.Ordinal);
            Assert.True(current >= 0, $"Expected fragment was not found: {fragment}");
            Assert.True(current > previous, $"Fragment is out of order: {fragment}");
            previous = current;
        }
    }

    private static string ReadRepositoryFile(params string[] segments) =>
        File.ReadAllText(Path.Combine([GetRepositoryRoot(), .. segments]));

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EasyShare.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the EasyShare repository root.");
    }
}
