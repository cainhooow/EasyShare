using EasyShare.Models;
using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class EnterprisePolicyLoaderTests
{
    [Fact]
    public async Task MachinePolicyOverridesOnlySpecifiedUserValues()
    {
        using var environment = new TestDirectory();
        var user = Path.Combine(environment.Root, "user.json");
        var machine = Path.Combine(environment.Root, "machine.json");
        await File.WriteAllTextAsync(
            user,
            """
            {
              "schemaVersion": 1,
              "browserSessionAllowed": false,
              "interactiveSignInAllowed": false,
              "uploadQueueQuotaBytes": 1073741824,
              "maxUploadPayloadBytes": 536870912
            }
            """);
        await File.WriteAllTextAsync(
            machine,
            """
            {
              "schemaVersion": 1,
              "browserSessionAllowed": true,
              "supportBundlesAllowed": false,
              "allowedSharePointHosts": ["*.sharepoint.com"]
            }
            """);

        var snapshot = await new EnterprisePolicyLoader(
            new AppDataPaths(Path.Combine(environment.Root, "data")),
            user,
            machine).LoadAsync();

        Assert.True(snapshot.IsManaged);
        Assert.True(snapshot.Policy.BrowserSessionAllowed);
        Assert.False(snapshot.Policy.InteractiveSignInAllowed);
        Assert.False(snapshot.Policy.SupportBundlesAllowed);
        Assert.Equal(1_073_741_824, snapshot.Policy.UploadQueueQuotaBytes);
        Assert.Equal("*.sharepoint.com", Assert.Single(snapshot.Policy.AllowedSharePointHosts));
        Assert.Equal(
            [EnterprisePolicySource.Defaults, EnterprisePolicySource.CurrentUser, EnterprisePolicySource.LocalMachine],
            snapshot.AppliedSources);
        Assert.Empty(snapshot.Issues);
    }

    [Fact]
    public async Task RejectsEntireLayerContainingCredentialLikeUnknownProperty()
    {
        using var environment = new TestDirectory();
        var machine = Path.Combine(environment.Root, "machine.json");
        await File.WriteAllTextAsync(
            machine,
            """
            {
              "schemaVersion": 1,
              "supportBundlesAllowed": false,
              "clientSecret": "must-never-be-policy"
            }
            """);

        var snapshot = await new EnterprisePolicyLoader(
            new AppDataPaths(Path.Combine(environment.Root, "data")),
            machinePolicyPath: machine).LoadAsync();

        Assert.False(snapshot.IsManaged);
        Assert.True(snapshot.Policy.SupportBundlesAllowed);
        var issue = Assert.Single(snapshot.Issues);
        Assert.Equal("clientSecret", issue.Field);
        Assert.Contains("forbidden", issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("must-never-be-policy", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LowerMachineQueueQuotaClampsInheritedPerFileLimit()
    {
        using var environment = new TestDirectory();
        var machine = Path.Combine(environment.Root, "machine.json");
        await File.WriteAllTextAsync(
            machine,
            """
            {
              "schemaVersion": 1,
              "uploadQueueQuotaBytes": 16777216
            }
            """);

        var snapshot = await new EnterprisePolicyLoader(
            new AppDataPaths(Path.Combine(environment.Root, "data")),
            machinePolicyPath: machine).LoadAsync();

        Assert.Equal(16_777_216, snapshot.Policy.UploadQueueQuotaBytes);
        Assert.Equal(snapshot.Policy.UploadQueueQuotaBytes, snapshot.Policy.MaxUploadPayloadBytes);
        Assert.Contains(snapshot.Issues, issue =>
            issue.Severity == EnterprisePolicyIssueSeverity.Warning &&
            issue.Field == "maxUploadPayloadBytes");
    }

    [Fact]
    public async Task RejectsUnsupportedSchemaWithoutApplyingValues()
    {
        using var environment = new TestDirectory();
        var user = Path.Combine(environment.Root, "user.json");
        await File.WriteAllTextAsync(
            user,
            """
            {
              "schemaVersion": 99,
              "browserSessionAllowed": false
            }
            """);

        var snapshot = await new EnterprisePolicyLoader(
            new AppDataPaths(Path.Combine(environment.Root, "data")),
            userPolicyPath: user).LoadAsync();

        Assert.False(snapshot.IsManaged);
        Assert.True(snapshot.Policy.BrowserSessionAllowed);
        Assert.Contains(snapshot.Issues, issue => issue.Field == "schemaVersion");
    }

    [Fact]
    public async Task LoadsManagedAppConfigurationAndTracksLockedFields()
    {
        using var environment = new TestDirectory();
        var machine = Path.Combine(environment.Root, "machine.json");
        var clientId = Guid.NewGuid();
        await File.WriteAllTextAsync(
            machine,
            $$"""
            {
              "schemaVersion": 1,
              "tenantId": "contoso.onmicrosoft.com",
              "clientId": "{{clientId:D}}",
              "mountPoint": "R",
              "startWithWindows": true,
              "autoStartVirtualDrive": false,
              "cacheMinutes": 60,
              "offlineCacheLimitMb": 4096,
              "updateChannel": "microsoftStore"
            }
            """);

        var snapshot = await new EnterprisePolicyLoader(
            new AppDataPaths(Path.Combine(environment.Root, "data")),
            machinePolicyPath: machine).LoadAsync();

        Assert.True(snapshot.IsManaged);
        Assert.Equal("contoso.onmicrosoft.com", snapshot.Policy.TenantId);
        Assert.Equal(clientId.ToString("D"), snapshot.Policy.ClientId);
        Assert.Equal("R:", snapshot.Policy.MountPoint);
        Assert.True(snapshot.Policy.StartWithWindows);
        Assert.False(snapshot.Policy.AutoStartVirtualDrive);
        Assert.Equal(60, snapshot.Policy.CacheMinutes);
        Assert.Equal(4096, snapshot.Policy.OfflineCacheLimitMb);
        Assert.Equal("microsoftStore", snapshot.Policy.UpdateChannel);
        Assert.True(snapshot.IsFieldManaged("tenantId"));
        Assert.True(snapshot.IsFieldManaged("mountPoint"));
        Assert.Empty(snapshot.Issues);
    }
}
