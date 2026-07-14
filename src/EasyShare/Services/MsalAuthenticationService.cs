using System.Security.Cryptography;
using EasyShare.Models;
using EasyShare.Resources;
using Microsoft.Identity.Client;

namespace EasyShare.Services;

public sealed class MsalAuthenticationService : IAuthenticationService
{
    private static readonly string[] Scopes =
    [
        "User.Read",
        "Files.ReadWrite.All",
        "Sites.Read.All",
        "offline_access"
    ];

    private readonly AppDataPaths _paths;
    private readonly LocalDatabase _database;
    private IPublicClientApplication? _application;
    private string? _configuredClientId;
    private string? _configuredTenantId;

    public MsalAuthenticationService(AppDataPaths paths, LocalDatabase database)
    {
        _paths = paths;
        _database = database;
    }

    public async Task<AuthStatus> GetStatusAsync()
    {
        var settings = await GetSettingsAsync();
        if (!settings.HasClientId)
        {
            return AuthStatus.NeedsConfiguration();
        }

        var application = GetApplication(settings);
        var account = (await application.GetAccountsAsync()).FirstOrDefault();

        if (account is null)
        {
            return AuthStatus.SignedOut();
        }

        try
        {
            var result = await application.AcquireTokenSilent(Scopes, account).ExecuteAsync();
            return SignedIn(result.Account.Username, result.ExpiresOn);
        }
        catch (MsalUiRequiredException)
        {
            return AuthStatus.SignedOut();
        }
        catch (MsalException ex)
        {
            return Error(ex.Message);
        }
    }

    public async Task<AuthStatus> SignInAsync(IntPtr windowHandle)
    {
        var settings = await GetSettingsAsync();
        if (!settings.HasClientId)
        {
            return AuthStatus.NeedsConfiguration();
        }

        try
        {
            var application = GetApplication(settings);
            var accounts = (await application.GetAccountsAsync()).ToArray();
            foreach (var account in accounts)
            {
                try
                {
                    var silentResult = await application.AcquireTokenSilent(Scopes, account).ExecuteAsync();
                    return SignedIn(silentResult.Account.Username, silentResult.ExpiresOn);
                }
                catch (MsalUiRequiredException)
                {
                    // The cached account exists, but Microsoft still needs a visible login/consent step.
                }
            }

            var builder = application
                .AcquireTokenInteractive(Scopes)
                .WithPrompt(Prompt.SelectAccount);

            if (accounts.Length == 1 && !string.IsNullOrWhiteSpace(accounts[0].Username))
            {
                builder = builder.WithLoginHint(accounts[0].Username);
            }

            if (windowHandle != IntPtr.Zero)
            {
                builder = builder.WithParentActivityOrWindow(windowHandle);
            }

            var result = await builder.ExecuteAsync();
            return SignedIn(result.Account.Username, result.ExpiresOn);
        }
        catch (MsalException ex)
        {
            StartupDiagnostics.Write("MSAL sign-in failed.", ex);
            return Error(ex.Message);
        }
    }

    public async Task<string?> GetAccessTokenAsync()
    {
        var settings = await GetSettingsAsync();
        if (!settings.HasClientId)
        {
            return null;
        }

        var application = GetApplication(settings);
        var account = (await application.GetAccountsAsync()).FirstOrDefault();
        if (account is null)
        {
            return null;
        }

        try
        {
            var result = await application.AcquireTokenSilent(Scopes, account).ExecuteAsync();
            return result.AccessToken;
        }
        catch (MsalException)
        {
            return null;
        }
    }

    public async Task SignOutAsync()
    {
        var settings = await GetSettingsAsync();
        if (!settings.HasClientId)
        {
            DeleteTokenCache();
            return;
        }

        var application = GetApplication(settings);
        foreach (var account in await application.GetAccountsAsync())
        {
            await application.RemoveAsync(account);
        }

        DeleteTokenCache();
    }

    private IPublicClientApplication GetApplication(AppSettings settings)
    {
        var tenantId = string.IsNullOrWhiteSpace(settings.TenantId) ? "organizations" : settings.TenantId.Trim();
        var clientId = settings.ClientId.Trim();

        if (_application is not null &&
            string.Equals(_configuredClientId, clientId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_configuredTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            return _application;
        }

        _paths.EnsureCreated();

        _application = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .WithRedirectUri("http://localhost")
            .Build();

        _configuredClientId = clientId;
        _configuredTenantId = tenantId;

        ConfigureTokenCache(_application.UserTokenCache);
        return _application;
    }

    private async Task<AppSettings> GetSettingsAsync()
    {
        await _database.InitializeAsync();
        return await _database.GetSettingsAsync();
    }

    private void ConfigureTokenCache(ITokenCache tokenCache)
    {
        tokenCache.SetBeforeAccess(args =>
        {
            if (!File.Exists(_paths.TokenCachePath))
            {
                return;
            }

            try
            {
                var protectedBytes = File.ReadAllBytes(_paths.TokenCachePath);
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                args.TokenCache.DeserializeMsalV3(bytes);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                try
                {
                    File.Delete(_paths.TokenCachePath);
                }
                catch
                {
                    // A locked cache will be replaced after the next successful sign-in.
                }
            }
        });

        tokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged)
            {
                return;
            }

            _paths.EnsureCreated();
            var bytes = args.TokenCache.SerializeMsalV3();
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            var temporaryPath = $"{_paths.TokenCachePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, _paths.TokenCachePath, overwrite: true);
        });
    }

    private void DeleteTokenCache()
    {
        if (File.Exists(_paths.TokenCachePath))
        {
            File.Delete(_paths.TokenCachePath);
        }
    }

    private static AuthStatus SignedIn(string accountName, DateTimeOffset expiresOn) =>
        new(
            AuthState.SignedIn,
            AppText.Get("AuthConnectedTitle"),
            AppText.Get("AuthSignedInMessage"),
            accountName,
            expiresOn);

    private static AuthStatus Error(string message) =>
        new(
            AuthState.Error,
            AppText.Get("AuthSignInErrorTitle"),
            TranslateLoginError(message),
            AppText.Get("AccountNone"),
            null);

    private static string TranslateLoginError(string message)
    {
        if (message.Contains("AADSTS65001", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("AADSTS90094", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("admin approval", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("admin consent", StringComparison.OrdinalIgnoreCase))
        {
            return AppText.Get("AuthAdminConsentRequired");
        }

        if (message.Contains("AADSTS50011", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("reply URL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("redirect", StringComparison.OrdinalIgnoreCase))
        {
            return AppText.Get("AuthRedirectMismatch");
        }

        if (message.Contains("access_denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("user canceled", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return AppText.Get("AuthLoginCancelled");
        }

        return message;
    }
}
