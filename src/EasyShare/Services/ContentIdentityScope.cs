using System.Security.Cryptography;
using System.Text;

namespace EasyShare.Services;

public static class ContentIdentityScope
{
    public const string Disconnected = "DISCONNECTED-CONTENT-SCOPE";

    public static string FromGraphAccount(string homeAccountId, string clientId) =>
        Hash(
            "graph-account",
            homeAccountId.Trim().ToUpperInvariant(),
            clientId.Trim().ToUpperInvariant());

    public static string FromBrowserIdentity(string host, string loginName) =>
        Hash(
            "browser-identity",
            host.Trim().ToUpperInvariant(),
            loginName.Trim().ToUpperInvariant());

    public static string FromBrowserIdentitySet(IEnumerable<string> identityScopes)
    {
        ArgumentNullException.ThrowIfNull(identityScopes);
        var scopes = identityScopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return scopes.Length == 0 ? string.Empty : Hash(["browser-set", .. scopes]);
    }

    public static bool IsPersistentIdentityScope(string? scope) =>
        scope is { Length: 64 } && scope.All(Uri.IsHexDigit);

    private static string Hash(params string[] values)
    {
        var material = string.Join('\n', values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

}
