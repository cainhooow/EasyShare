using System.Text.RegularExpressions;
using Xunit;

namespace EasyShare.Tests;

public sealed partial class CiWorkflowContractTests
{
    [Fact]
    [Trait("Gate", "CI")]
    public void WindowsWorkflowCoversTestsAuditAndCrossArchitecturePackages()
    {
        var root = GetRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        var script = File.ReadAllText(Path.Combine(root, ".github", "scripts", "Invoke-CI.ps1"));

        Assert.Contains("runs-on: windows-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("-Target Audit", workflow, StringComparison.Ordinal);
        Assert.Contains("-Target Gates", workflow, StringComparison.Ordinal);
        Assert.Contains("-Target Test", workflow, StringComparison.Ordinal);
        Assert.Contains("-Target Package", workflow, StringComparison.Ordinal);
        Assert.Contains("- x86", workflow, StringComparison.Ordinal);
        Assert.Contains("- ARM64", workflow, StringComparison.Ordinal);
        Assert.Equal(
            ActionReference().Matches(workflow).Count,
            PinnedActionReference().Matches(workflow).Count);

        Assert.Contains("-p:RuntimeIdentifier=win-x64", script, StringComparison.Ordinal);
        Assert.Contains("NU1901%3BNU1902%3BNU1903%3BNU1904", script, StringComparison.Ordinal);
        Assert.Contains("StorePackageManifestTests", script, StringComparison.Ordinal);
        Assert.Contains("LocalizationResourceTests", script, StringComparison.Ordinal);
        Assert.Contains("SharePointContentTransportTests", script, StringComparison.Ordinal);
        Assert.Contains("UpdateUriPolicyTests", script, StringComparison.Ordinal);
        Assert.Contains("WebViewOriginPolicyTests", script, StringComparison.Ordinal);
        Assert.Contains("UploadPayloadStorageTests", script, StringComparison.Ordinal);
        Assert.Contains("-p:GenerateAppxPackageOnBuild=true", script, StringComparison.Ordinal);
        Assert.Contains("-p:AppxPackageSigningEnabled=false", script, StringComparison.Ordinal);
        Assert.Contains("-p:UapAppxPackageBuildMode=SideloadOnly", script, StringComparison.Ordinal);
        Assert.Contains("-p:AppxSymbolPackageEnabled=false", script, StringComparison.Ordinal);
        Assert.Contains("win-x86", script, StringComparison.Ordinal);
        Assert.Contains("win-arm64", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Gate", "CI")]
    public void DocumentationUsesTheSameEntrypointAsGitHubActions()
    {
        var root = GetRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        var documentation = File.ReadAllText(Path.Combine(root, ".github", "CI.md"));
        const string entrypoint = ".github/scripts/Invoke-CI.ps1";

        Assert.Contains(entrypoint, workflow, StringComparison.Ordinal);
        Assert.Contains(
            $"pwsh -NoProfile -File {entrypoint} -Target All",
            documentation,
            StringComparison.Ordinal);
    }

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

    [GeneratedRegex(@"uses:\s+actions/(?:checkout|setup-dotnet|upload-artifact)@[0-9a-f]{40}\b")]
    private static partial Regex PinnedActionReference();

    [GeneratedRegex(@"uses:\s+actions/(?:checkout|setup-dotnet|upload-artifact)@\S+")]
    private static partial Regex ActionReference();
}
