using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace EasyShare.Services;

public static class UpdateIntegrity
{
    public static string NormalizeSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return string.Empty;
        }

        var value = digest.Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["sha256:".Length..];
        }

        return Regex.IsMatch(value, "^[0-9a-fA-F]{64}$") ? value.ToUpperInvariant() : string.Empty;
    }

    public static bool VerifyFile(string path, string expectedSha256)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(actual, NormalizeSha256(expectedSha256), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
