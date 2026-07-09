using EasyShare.Models;

namespace EasyShare.Services;

public interface IAuthenticationService
{
    Task<AuthStatus> GetStatusAsync();

    Task<AuthStatus> SignInAsync(IntPtr windowHandle);

    Task<string?> GetAccessTokenAsync();

    Task SignOutAsync();
}
