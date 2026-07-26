using System.Text.RegularExpressions;
using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class DebugVisualTestIsolationTests
{
    [Fact]
    [Trait("Gate", "Stage02")]
    public void IsolationRootRequiresANonRootFullyQualifiedPath()
    {
        Assert.Null(DebugVisualTestIsolation.ResolveDataDirectory(null));
        Assert.Null(DebugVisualTestIsolation.ResolveDataDirectory("  "));
        Assert.Throws<ArgumentException>(
            () => DebugVisualTestIsolation.ResolveDataDirectory(
                Path.Combine("relative", "fixture")));

        var filesystemRoot = Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory));
        Assert.False(string.IsNullOrWhiteSpace(filesystemRoot));
        Assert.Throws<ArgumentException>(
            () => DebugVisualTestIsolation.ResolveDataDirectory(filesystemRoot));
    }

    [Fact]
    [Trait("Gate", "Stage02")]
    public void IsolationRootIsNormalizedWithoutCreatingOrLoggingIt()
    {
        var candidate = Path.Combine(
            Path.GetTempPath(),
            "EasyShare-visual-fixture",
            "..",
            $"EasyShare-visual-fixture-{Guid.NewGuid():N}");
        var expected = Path.GetFullPath(candidate).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        var actual = DebugVisualTestIsolation.ResolveDataDirectory(candidate);

        Assert.Equal(expected, actual);
        Assert.False(Directory.Exists(expected));
        var helper = ReadRepositoryFile(
            "src",
            "EasyShare",
            "Services",
            "DebugVisualTestIsolation.cs");
        Assert.DoesNotContain("StartupDiagnostics.Write", helper, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Gate", "Stage02")]
    public void PackagedDebugActivationCanSelectIsolationWithoutEnvironmentInheritance()
    {
        var commandLinePath = Path.Combine(
            Path.GetTempPath(),
            $"EasyShare-packaged-fixture-{Guid.NewGuid():N}");
        var requested = DebugVisualTestIsolation.ResolveDataDirectoryRequest(
            environmentValue: null,
            [
                "EasyShare.exe",
                DebugVisualTestIsolation.DataDirectoryArgumentPrefix + commandLinePath
            ]);

        Assert.Equal(commandLinePath, requested);
        Assert.Equal(
            "environment-wins",
            DebugVisualTestIsolation.ResolveDataDirectoryRequest(
                "environment-wins",
                [
                    DebugVisualTestIsolation.DataDirectoryArgumentPrefix +
                    "command-line-loses"
                ]));

        var markerFile = Path.Combine(
            Path.GetTempPath(),
            $"EasyShare-visual-marker-{Guid.NewGuid():N}");
        var markerPath = Path.Combine(
            Path.GetTempPath(),
            $"EasyShare-marker-root-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(markerFile, markerPath);
            Assert.Equal(
                markerPath,
                DebugVisualTestIsolation.ResolveDataDirectoryRequest(
                    environmentValue: null,
                    commandLineArguments: ["EasyShare.exe"],
                    markerFilePath: markerFile));
        }
        finally
        {
            File.Delete(markerFile);
        }
    }

    [Fact]
    [Trait("Gate", "Stage02")]
    public void IsolationIsDebugOnlyAndRoutesStartupDiagnosticsToTheFixtureRoot()
    {
        var helper = ReadRepositoryFile(
            "src",
            "EasyShare",
            "Services",
            "DebugVisualTestIsolation.cs");
        var diagnostics = ReadRepositoryFile(
            "src",
            "EasyShare",
            "Services",
            "StartupDiagnostics.cs");

        Assert.Matches(
            new Regex(
                @"#if DEBUG.*Environment\.GetEnvironmentVariable\(DataDirectoryEnvironmentVariable\).*#else.*IsActive => false",
                RegexOptions.Singleline),
            helper);
        Assert.Contains("DebugVisualTestIsolation.DataDirectory", diagnostics, StringComparison.Ordinal);
        Assert.Contains(
            "DebugVisualTestIsolation.PackageWebViewProfileDirectory",
            diagnostics,
            StringComparison.Ordinal);
        Assert.Contains("new AppDataPaths(", diagnostics, StringComparison.Ordinal);
        Assert.Contains("isolatedDataDirectory", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Path.Combine(isolatedDataDirectory",
            diagnostics,
            StringComparison.Ordinal);

        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"EasyShare-isolated-roots-{Guid.NewGuid():N}");
        var packageProfile = Path.Combine(
            dataDirectory + ".PackageLocalState",
            "EBWebView");
        var paths = new AppDataPaths(
            dataDirectory,
            packageWebViewProfilePath: packageProfile);
        _ = new LocalDataResetService(paths);
        Assert.False(
            packageProfile.StartsWith(
                dataDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Gate", "Stage02")]
    public void IsolationSkipsWindowsLanguageStores()
    {
        var source = ReadRepositoryFile(
            "src",
            "EasyShare",
            "Resources",
            "AppText.cs");

        AssertIsolationGuardPrecedes(source, "LoadStartupLanguageCode", "ApplicationData.Current.LocalSettings");
        AssertIsolationGuardPrecedes(source, "SaveStartupLanguageCode", "ApplicationData.Current.LocalSettings");
        AssertIsolationGuardPrecedes(source, "ClearStartupLanguageCode", "ApplicationData.Current.LocalSettings");
        AssertIsolationGuardPrecedes(source, "TrySetPrimaryLanguageOverride", "ApplicationLanguages.PrimaryLanguageOverride");
    }

    [Fact]
    [Trait("Gate", "Stage02")]
    public void IsolationSkipsEveryStartupServiceMutationAndAutomaticUpdateCheck()
    {
        var viewModel = ReadRepositoryFile(
            "src",
            "EasyShare",
            "ViewModels",
            "MainPageViewModel.cs");
        var page = ReadRepositoryFile(
            "src",
            "EasyShare",
            "MainPage.xaml.cs");
        var services = ReadRepositoryFile(
            "src",
            "EasyShare",
            "Services",
            "AppServices.cs");

        AssertIsolationGuardPrecedes(viewModel, "LoadAsync", "_startupService.IsEnabledAsync");
        AssertIsolationGuardPrecedes(viewModel, "SaveSettingsAsync", "_startupService.SetEnabledAsync");
        Assert.Contains(
            "if (DebugVisualTestIsolation.IsActive)",
            ExtractMethod(viewModel, "ApplySetupWizardAsync"),
            StringComparison.Ordinal);
        AssertIsolationGuardPrecedes(viewModel, "ResetAppAsync", "_startupService.SetEnabledAsync");
        AssertIsolationGuardPrecedes(page, "Page_Loaded", "CheckUpdatesOnStartupAsync");
        Assert.Contains(
            "DebugVisualTestIsolation.DataDirectory",
            services,
            StringComparison.Ordinal);
        Assert.Contains(
            "DebugVisualTestIsolation.DisableUploadWorker",
            ExtractMethod(page, "Page_Loaded"),
            StringComparison.Ordinal);
    }

    private static void AssertIsolationGuardPrecedes(
        string source,
        string methodName,
        string protectedOperation)
    {
        var method = ExtractMethod(source, methodName);
        var guard = method.IndexOf("DebugVisualTestIsolation.IsActive", StringComparison.Ordinal);
        var operation = method.IndexOf(protectedOperation, StringComparison.Ordinal);
        Assert.True(guard >= 0, $"{methodName} has no DEBUG visual isolation guard.");
        Assert.True(operation > guard, $"{protectedOperation} is not protected in {methodName}.");
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var declaration = Regex.Match(
            source,
            $@"(?m)^\s*(?:public|private|internal|protected)\s+.*\b{Regex.Escape(methodName)}\s*\(");
        Assert.True(declaration.Success, $"Method {methodName} was not found.");
        var openingBrace = source.IndexOf('{', declaration.Index);
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
