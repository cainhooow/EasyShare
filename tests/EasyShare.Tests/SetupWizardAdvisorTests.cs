using EasyShare.Models;
using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class SetupWizardAdvisorTests
{
    [Fact]
    public void FirstRunWithoutClientIdRecommendsBrowserSession()
    {
        var draft = SetupWizardAdvisor.CreateDraft(new AppSettings(), CreatePolicy());

        Assert.Equal(AuthenticationMode.BrowserSession, draft.AuthenticationMode);
        Assert.Equal("S:", draft.MountPoint);
        Assert.True(draft.ConnectNow);
    }

    [Fact]
    public void FirstRunWithValidClientIdRecommendsMicrosoftGraph()
    {
        var settings = new AppSettings { ClientId = Guid.NewGuid().ToString("D") };

        var draft = SetupWizardAdvisor.CreateDraft(settings, CreatePolicy());

        Assert.Equal(AuthenticationMode.MicrosoftGraph, draft.AuthenticationMode);
    }

    [Fact]
    public void ManagedClientIdForcesMicrosoftGraphEvenForCompletedBrowserSetup()
    {
        var settings = new AppSettings
        {
            SetupWizardCompleted = true,
            AuthenticationMode = AuthenticationMode.BrowserSession
        };
        var policy = CreatePolicy(
            new EnterprisePolicy { ClientId = Guid.NewGuid().ToString("D") },
            managedFields: ["clientId"]);

        var draft = SetupWizardAdvisor.CreateDraft(settings, policy);

        Assert.Equal(AuthenticationMode.MicrosoftGraph, draft.AuthenticationMode);
    }

    [Fact]
    public void BrowserBlockedByPolicyForcesMicrosoftGraphAndDisablesBrowserCapability()
    {
        var snapshot = CreatePolicy(
            new EnterprisePolicy { BrowserSessionAllowed = false },
            managedFields: ["browserSessionAllowed"]);

        var draft = SetupWizardAdvisor.CreateDraft(new AppSettings(), snapshot);
        var capabilities = SetupWizardAdvisor.CreateCapabilities(snapshot, winFspAvailable: true);

        Assert.Equal(AuthenticationMode.MicrosoftGraph, draft.AuthenticationMode);
        Assert.False(capabilities.BrowserSessionAllowed);
        Assert.False(capabilities.CanEditAccess);
        Assert.True(capabilities.WinFspAvailable);
    }

    [Fact]
    public void CompletedWizardPreservesExplicitAuthenticationChoiceWhenPolicyDoesNotManageIt()
    {
        var settings = new AppSettings
        {
            SetupWizardCompleted = true,
            AuthenticationMode = AuthenticationMode.BrowserSession,
            ClientId = Guid.NewGuid().ToString("D")
        };

        var draft = SetupWizardAdvisor.CreateDraft(settings, CreatePolicy());

        Assert.Equal(AuthenticationMode.BrowserSession, draft.AuthenticationMode);
    }

    [Fact]
    public void CreateDraftAppliesPolicyValuesAndReportsManagedCapabilities()
    {
        var clientId = Guid.NewGuid().ToString("D");
        var policy = new EnterprisePolicy
        {
            ClientId = clientId,
            TenantId = "contoso.onmicrosoft.com",
            MountPoint = "R:",
            StartWithWindows = true,
            AutoStartVirtualDrive = false,
            CacheMinutes = 45,
            OfflineCacheLimitMb = 4096,
            InteractiveSignInAllowed = false
        };
        var snapshot = CreatePolicy(
            policy,
            managedFields:
            [
                "clientId", "tenantId", "mountPoint", "startWithWindows",
                "autoStartVirtualDrive", "cacheMinutes", "offlineCacheLimitMb"
            ]);

        var draft = SetupWizardAdvisor.CreateDraft(new AppSettings(), snapshot, ["S:"]);
        var capabilities = SetupWizardAdvisor.CreateCapabilities(snapshot, winFspAvailable: false);

        Assert.Equal(clientId, draft.ClientId);
        Assert.Equal("contoso.onmicrosoft.com", draft.TenantId);
        Assert.Equal("R:", draft.MountPoint);
        Assert.True(draft.StartWithWindows);
        Assert.False(draft.AutoStartVirtualDrive);
        Assert.Equal(45, draft.CacheMinutes);
        Assert.Equal(4096, draft.OfflineCacheLimitMb);
        Assert.False(draft.ConnectNow);
        Assert.False(capabilities.CanEditAccess);
        Assert.False(capabilities.CanEditDrive);
        Assert.False(capabilities.CanEditCache);
        Assert.False(capabilities.InteractiveSignInAllowed);
    }

    [Fact]
    public void OccupiedRequestedMountSelectsTheFirstDeterministicFreeOption()
    {
        var settings = new AppSettings { MountPoint = "s" };

        var draft = SetupWizardAdvisor.CreateDraft(settings, CreatePolicy(), ["s:\\"]);

        Assert.Equal("Z:", draft.MountPoint);
    }

    [Fact]
    public void MountOptionsNormalizeOccupiedValuesAndKeepCurrentSelectionAvailable()
    {
        var options = SetupWizardAdvisor.GetMountPointOptions(["s:\\", "Z:", "y:/"], "s:");

        Assert.Equal("S:", options[0]);
        Assert.DoesNotContain("Z:", options, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Y:", options, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("X:", options, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void WelcomeAndAppearanceRejectUnsupportedValues()
    {
        var draft = new SetupWizardDraft
        {
            LanguageCode = "fr-FR",
            ThemeMode = (AppThemeMode)99
        };
        var policy = CreatePolicy();

        var languageIssues = SetupWizardAdvisor.ValidateStep(draft, SetupWizardStep.Welcome, policy);
        var appearanceIssues = SetupWizardAdvisor.ValidateStep(draft, SetupWizardStep.Appearance, policy);

        AssertIssue(languageIssues, "WizardErrorLanguage", nameof(draft.LanguageCode));
        AssertIssue(appearanceIssues, "WizardErrorTheme", nameof(draft.ThemeMode));
    }

    [Fact]
    public void GraphConnectionRequiresValidClientIdAndTenant()
    {
        var draft = new SetupWizardDraft
        {
            AuthenticationMode = AuthenticationMode.MicrosoftGraph,
            ClientId = Guid.Empty.ToString("D"),
            TenantId = "invalid tenant"
        };

        var issues = SetupWizardAdvisor.ValidateStep(
            draft,
            SetupWizardStep.Connection,
            CreatePolicy());

        AssertIssue(issues, "WizardErrorClientId", nameof(draft.ClientId));
        AssertIssue(issues, "WizardErrorTenant", nameof(draft.TenantId));
    }

    [Fact]
    public void GraphConnectionHonorsAllowedTenantPolicy()
    {
        var draft = new SetupWizardDraft
        {
            AuthenticationMode = AuthenticationMode.MicrosoftGraph,
            ClientId = Guid.NewGuid().ToString("D"),
            TenantId = "fabrikam.onmicrosoft.com"
        };
        var policy = CreatePolicy(new EnterprisePolicy
        {
            AllowedTenantIds = ["contoso.onmicrosoft.com"]
        });

        var issues = SetupWizardAdvisor.ValidateStep(
            draft,
            SetupWizardStep.Connection,
            policy);

        AssertIssue(issues, "WizardErrorTenantBlocked", nameof(draft.TenantId));
    }

    [Fact]
    public void AllowedTenantPolicySelectsFirstPermittedTenantInDraft()
    {
        var settings = new AppSettings
        {
            ClientId = Guid.NewGuid().ToString("D"),
            TenantId = "organizations"
        };
        var policy = CreatePolicy(
            new EnterprisePolicy
            {
                AllowedTenantIds =
                [
                    "contoso.onmicrosoft.com",
                    "fabrikam.onmicrosoft.com"
                ]
            },
            managedFields: ["allowedTenantIds"]);

        var draft = SetupWizardAdvisor.CreateDraft(settings, policy);

        Assert.Equal(AuthenticationMode.MicrosoftGraph, draft.AuthenticationMode);
        Assert.Equal("contoso.onmicrosoft.com", draft.TenantId);
        Assert.Empty(SetupWizardAdvisor.ValidateStep(draft, SetupWizardStep.Connection, policy));
    }

    [Theory]
    [InlineData("organizations")]
    [InlineData("ORGANIZATIONS")]
    [InlineData("2f92bc50-7e3f-4ab6-8c79-2df1e635c241")]
    [InlineData("contoso.onmicrosoft.com")]
    public void AcceptedTenantFormatsAreRecognized(string tenant)
    {
        Assert.True(SetupWizardAdvisor.IsValidTenant(tenant));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not a tenant")]
    [InlineData("localhost")]
    [InlineData("-contoso.example.com")]
    public void InvalidTenantFormatsAreRejected(string tenant)
    {
        Assert.False(SetupWizardAdvisor.IsValidTenant(tenant));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://www.office.com/?auth=2")]
    [InlineData("https://contoso.sharepoint.com/sites/team")]
    public void BrowserConnectionAcceptsSafeEntryPoints(string url)
    {
        var draft = new SetupWizardDraft
        {
            AuthenticationMode = AuthenticationMode.BrowserSession,
            BrowserSessionStartUrl = url
        };

        var issues = SetupWizardAdvisor.ValidateStep(
            draft,
            SetupWizardStep.Connection,
            CreatePolicy());

        Assert.Empty(issues);
    }

    [Theory]
    [InlineData("http://contoso.sharepoint.com/sites/team")]
    [InlineData("https://evil.example/sites/team")]
    [InlineData("https://contoso.sharepoint.com.evil.example/sites/team")]
    [InlineData("https://contoso.sharepoint.com:8443/sites/team")]
    public void BrowserConnectionRejectsUnsafeEntryPoints(string url)
    {
        var draft = new SetupWizardDraft
        {
            AuthenticationMode = AuthenticationMode.BrowserSession,
            BrowserSessionStartUrl = url
        };

        var issues = SetupWizardAdvisor.ValidateStep(
            draft,
            SetupWizardStep.Connection,
            CreatePolicy());

        AssertIssue(issues, "WizardErrorSharePointUrl", nameof(draft.BrowserSessionStartUrl));
    }

    [Fact]
    public void BrowserConnectionRejectsUrlContainingUserInfo()
    {
        var draft = new SetupWizardDraft
        {
            AuthenticationMode = AuthenticationMode.BrowserSession,
            BrowserSessionStartUrl = "https://usuario:senha@contoso.sharepoint.com/sites/team"
        };

        var issues = SetupWizardAdvisor.ValidateStep(
            draft,
            SetupWizardStep.Connection,
            CreatePolicy());

        AssertIssue(issues, "WizardErrorSharePointUrl", nameof(draft.BrowserSessionStartUrl));
    }

    [Fact]
    public void InvalidAuthenticationModeIsReportedAndNeverPersistedByBuildSettings()
    {
        var draft = new SetupWizardDraft
        {
            AuthenticationMode = (AuthenticationMode)99,
            ClientId = Guid.NewGuid().ToString("D")
        };
        var policy = CreatePolicy();

        var issues = SetupWizardAdvisor.ValidateStep(draft, SetupWizardStep.Access, policy);
        var result = SetupWizardAdvisor.BuildSettings(new AppSettings(), draft, policy);

        AssertIssue(issues, "WizardErrorAccessMode", nameof(draft.AuthenticationMode));
        Assert.True(Enum.IsDefined(result.AuthenticationMode));
        Assert.Equal(AuthenticationMode.MicrosoftGraph, result.AuthenticationMode);
        Assert.NotEqual(draft.AuthenticationMode, result.AuthenticationMode);
    }

    [Fact]
    public void BrowserConnectionHonorsAllowedSharePointHosts()
    {
        var draft = new SetupWizardDraft
        {
            AuthenticationMode = AuthenticationMode.BrowserSession,
            BrowserSessionStartUrl = "https://fabrikam.sharepoint.com/sites/team"
        };
        var policy = CreatePolicy(new EnterprisePolicy
        {
            AllowedSharePointHosts = ["contoso.sharepoint.com"]
        });

        var issues = SetupWizardAdvisor.ValidateStep(
            draft,
            SetupWizardStep.Connection,
            policy);

        AssertIssue(issues, "WizardErrorSharePointUrl", nameof(draft.BrowserSessionStartUrl));
    }

    [Fact]
    public void WindowsValidationRejectsBusyMountAndUsesManagedMessageWhenApplicable()
    {
        var draft = new SetupWizardDraft
        {
            AutoStartVirtualDrive = true,
            MountPoint = "S:"
        };
        var unmanagedIssues = SetupWizardAdvisor.ValidateStep(
            draft,
            SetupWizardStep.WindowsIntegration,
            CreatePolicy(),
            ["R:"]);
        var managedPolicy = CreatePolicy(
            new EnterprisePolicy { MountPoint = "S:" },
            managedFields: ["mountPoint"]);
        var managedIssues = SetupWizardAdvisor.ValidateStep(
            draft,
            SetupWizardStep.WindowsIntegration,
            managedPolicy,
            ["R:"]);

        AssertIssue(unmanagedIssues, "WizardErrorMountBusy", nameof(draft.MountPoint));
        AssertIssue(managedIssues, "WizardErrorManagedMountBusy", nameof(draft.MountPoint));
    }

    [Fact]
    public void DisabledVirtualDriveSkipsMountValidationUnlessPolicyForcesItOn()
    {
        var draft = new SetupWizardDraft
        {
            AutoStartVirtualDrive = false,
            MountPoint = "A:"
        };

        var issues = SetupWizardAdvisor.ValidateStep(
            draft,
            SetupWizardStep.WindowsIntegration,
            CreatePolicy(),
            []);

        Assert.Empty(issues);
    }

    [Theory]
    [InlineData(0, 2048, "WizardErrorCacheMinutes")]
    [InlineData(1441, 2048, "WizardErrorCacheMinutes")]
    [InlineData(15, 127, "WizardErrorOfflineLimit")]
    [InlineData(15, 102401, "WizardErrorOfflineLimit")]
    public void OfflineValidationEnforcesDocumentedBounds(
        int cacheMinutes,
        int offlineLimitMb,
        string expectedMessageKey)
    {
        var draft = new SetupWizardDraft
        {
            CacheMinutes = cacheMinutes,
            OfflineCacheLimitMb = offlineLimitMb
        };

        var issues = SetupWizardAdvisor.ValidateStep(
            draft,
            SetupWizardStep.OfflineAndNotifications,
            CreatePolicy());

        Assert.Contains(issues, issue => issue.MessageKey == expectedMessageKey);
    }

    [Fact]
    public void ValidateAllAggregatesIssuesFromIndependentSteps()
    {
        var draft = new SetupWizardDraft
        {
            LanguageCode = "de-DE",
            ThemeMode = (AppThemeMode)42,
            AuthenticationMode = AuthenticationMode.MicrosoftGraph,
            ClientId = "invalid",
            TenantId = "invalid",
            AutoStartVirtualDrive = true,
            MountPoint = "A:",
            CacheMinutes = 0,
            OfflineCacheLimitMb = 1
        };

        var issues = SetupWizardAdvisor.ValidateAll(draft, CreatePolicy(), ["S:"]);

        Assert.Contains(issues, issue => issue.Step == SetupWizardStep.Welcome);
        Assert.Contains(issues, issue => issue.Step == SetupWizardStep.Appearance);
        Assert.Contains(issues, issue => issue.Step == SetupWizardStep.Connection);
        Assert.Contains(issues, issue => issue.Step == SetupWizardStep.WindowsIntegration);
        Assert.Contains(issues, issue => issue.Step == SetupWizardStep.OfflineAndNotifications);
    }

    [Fact]
    public void BuildSettingsReturnsNormalizedCloneMarksVersionAndPreservesUneditedPreferences()
    {
        var current = new AppSettings
        {
            AuthenticationMode = AuthenticationMode.BrowserSession,
            ClientId = "original-client",
            TenantId = "original.example.com",
            BrowserKeepAliveMinutes = 75,
            AccentColor = "#112233",
            NotifyUploadCompleted = false,
            NotifyUploadFailed = false,
            NotifyConflict = false,
            NotifySessionExpired = false,
            NotifyDriveDisconnected = false,
            NotifyUpdateReady = false,
            SetupWizardCompleted = false,
            SetupWizardCompletedVersion = 0
        };
        var draft = new SetupWizardDraft
        {
            LanguageCode = "EN-us",
            ThemeMode = (AppThemeMode)99,
            HighContrastEnabled = true,
            AuthenticationMode = AuthenticationMode.MicrosoftGraph,
            ClientId = $"  {Guid.NewGuid():D}  ",
            TenantId = "   ",
            BrowserSessionStartUrl = "   ",
            BrowserKeepSessionAlive = false,
            MountPoint = "q:\\",
            AutoStartVirtualDrive = true,
            StartWithWindows = false,
            StartMinimized = true,
            CacheMinutes = 0,
            NotificationsEnabled = false,
            QuietModeEnabled = true,
            OfflineCacheLimitMb = 1,
            OfflinePauseOnMeteredNetwork = false,
            OfflinePauseOnBattery = false
        };

        var result = SetupWizardAdvisor.BuildSettings(current, draft, CreatePolicy());

        Assert.NotSame(current, result);
        Assert.Equal("en-US", result.LanguageCode);
        Assert.Equal(AppThemeMode.System, result.ThemeMode);
        Assert.True(result.HighContrastEnabled);
        Assert.Equal(AuthenticationMode.MicrosoftGraph, result.AuthenticationMode);
        Assert.True(Guid.TryParse(result.ClientId, out _));
        Assert.Equal("organizations", result.TenantId);
        Assert.Equal("https://www.office.com/?auth=2", result.BrowserSessionStartUrl);
        Assert.False(result.BrowserKeepSessionAlive);
        Assert.Equal("Q:", result.MountPoint);
        Assert.False(result.StartWithWindows);
        Assert.False(result.StartMinimized);
        Assert.Equal(1, result.CacheMinutes);
        Assert.False(result.NotificationsEnabled);
        Assert.False(result.QuietModeEnabled);
        Assert.Equal(128, result.OfflineCacheLimitMb);
        Assert.False(result.OfflinePauseOnMeteredNetwork);
        Assert.False(result.OfflinePauseOnBattery);
        Assert.True(result.SetupWizardCompleted);
        Assert.Equal(SetupWizardAdvisor.CurrentVersion, result.SetupWizardCompletedVersion);
        Assert.Equal(75, result.BrowserKeepAliveMinutes);
        Assert.Equal("#112233", result.AccentColor);
        Assert.False(result.NotifyUploadCompleted);
        Assert.False(result.NotifyUploadFailed);
        Assert.False(result.NotifyConflict);
        Assert.False(result.NotifySessionExpired);
        Assert.False(result.NotifyDriveDisconnected);
        Assert.False(result.NotifyUpdateReady);
        Assert.False(current.SetupWizardCompleted);
        Assert.Equal(0, current.SetupWizardCompletedVersion);
        Assert.Equal("original-client", current.ClientId);
    }

    [Fact]
    public void BuildSettingsReappliesAdministrativePolicyToTamperedDraft()
    {
        var managedClientId = Guid.NewGuid().ToString("D");
        var policy = new EnterprisePolicy
        {
            BrowserSessionAllowed = false,
            ClientId = managedClientId,
            TenantId = "contoso.onmicrosoft.com",
            MountPoint = "R:",
            StartWithWindows = false,
            AutoStartVirtualDrive = true,
            CacheMinutes = 60,
            OfflineCacheLimitMb = 8192
        };
        var draft = new SetupWizardDraft
        {
            AuthenticationMode = AuthenticationMode.BrowserSession,
            ClientId = Guid.NewGuid().ToString("D"),
            TenantId = "evil.example.com",
            MountPoint = "T:",
            StartWithWindows = true,
            StartMinimized = true,
            AutoStartVirtualDrive = false,
            CacheMinutes = 2,
            OfflineCacheLimitMb = 256
        };

        var result = SetupWizardAdvisor.BuildSettings(
            new AppSettings(),
            draft,
            CreatePolicy(policy));

        Assert.Equal(AuthenticationMode.MicrosoftGraph, result.AuthenticationMode);
        Assert.Equal(managedClientId, result.ClientId);
        Assert.Equal("contoso.onmicrosoft.com", result.TenantId);
        Assert.Equal("R:", result.MountPoint);
        Assert.False(result.StartWithWindows);
        Assert.True(result.AutoStartVirtualDrive);
        Assert.Equal(60, result.CacheMinutes);
        Assert.Equal(8192, result.OfflineCacheLimitMb);
    }

    [Fact]
    public void ManagedGraphIdentityRejectsAndOverridesTamperedBrowserMode()
    {
        var clientId = Guid.NewGuid().ToString("D");
        var policy = CreatePolicy(
            new EnterprisePolicy { ClientId = clientId, BrowserSessionAllowed = true },
            managedFields: ["clientId"]);
        var draft = new SetupWizardDraft
        {
            AuthenticationMode = AuthenticationMode.BrowserSession,
            ClientId = "tampered",
            BrowserSessionStartUrl = "https://contoso.sharepoint.com/sites/team"
        };

        var issues = SetupWizardAdvisor.ValidateStep(draft, SetupWizardStep.Access, policy);
        var result = SetupWizardAdvisor.BuildSettings(new AppSettings(), draft, policy);

        AssertIssue(issues, "WizardErrorGraphManaged", nameof(draft.AuthenticationMode));
        Assert.Equal(AuthenticationMode.MicrosoftGraph, result.AuthenticationMode);
        Assert.Equal(clientId, result.ClientId);
    }

    [Fact]
    public void PolicyDisablingStartupAlsoDisablesStartMinimizedInDraftAndSettings()
    {
        var current = new AppSettings
        {
            StartWithWindows = true,
            StartMinimized = true
        };
        var policy = CreatePolicy(
            new EnterprisePolicy { StartWithWindows = false },
            managedFields: ["startWithWindows"]);

        var draft = SetupWizardAdvisor.CreateDraft(current, policy);
        Assert.False(draft.StartWithWindows);
        Assert.False(draft.StartMinimized);

        draft.StartWithWindows = true;
        draft.StartMinimized = true;
        var result = SetupWizardAdvisor.BuildSettings(current, draft, policy);

        Assert.False(result.StartWithWindows);
        Assert.False(result.StartMinimized);
    }

    private static EnterprisePolicySnapshot CreatePolicy(
        EnterprisePolicy? policy = null,
        params string[] managedFields)
    {
        var managed = managedFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new EnterprisePolicySnapshot(
            policy ?? new EnterprisePolicy(),
            managed.Count > 0,
            [EnterprisePolicySource.Defaults],
            [],
            managed);
    }

    private static void AssertIssue(
        IEnumerable<SetupWizardValidationIssue> issues,
        string messageKey,
        string field) =>
        Assert.Contains(issues, issue =>
            issue.MessageKey == messageKey &&
            string.Equals(issue.Field, field, StringComparison.Ordinal));
}
