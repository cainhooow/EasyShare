using System.Text.RegularExpressions;

namespace EasyShare.Services;

public sealed class SensitiveDataRedactor
{
    public const string RedactedValue = "[REDACTED]";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex AuthorizationPattern = new(
        @"(?im)\b(Authorization\s*:\s*)(?:Bearer|Basic)\s+[^\s,;]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex CookieHeaderPattern = new(
        @"(?im)\b((?:Set-)?Cookie\s*:\s*)[^\r\n]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SecretPairPattern = new(
        @"(?ix)\b(access_?token|refresh_?token|id_?token|client_?secret|password|passwd|secret|cookie|session_?id|samlresponse)\b(\s*[\""']?\s*[:=]\s*[\""']?)([^\""'\s,;}&\r\n]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SensitiveQueryPattern = new(
        @"(?ix)([?&](?:code|assertion|login_hint|user|username|email|upn)=)([^&#\s]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex JwtPattern = new(
        @"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?![A-Za-z0-9_-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex EmailPattern = new(
        @"(?<![A-Za-z0-9.!#$%&'*+/=?^_`{|}~-])[A-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Z0-9-]+(?:\.[A-Z0-9-]+)+(?![A-Za-z0-9-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex WindowsUserPathPattern = new(
        @"(?i)\b([A-Z]:\\Users\\)[^\\\s]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex IpV4Pattern = new(
        @"(?<![\d.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\d.])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly string[] SensitiveKeyFragments =
    [
        "authorization",
        "cookie",
        "credential",
        "password",
        "passwd",
        "secret",
        "token"
    ];

    private static readonly string[] PersonalDataKeyFragments =
    [
        "account",
        "device",
        "email",
        "hostname",
        "machine",
        "path",
        "upn",
        "user"
    ];

    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        try
        {
            var redacted = AuthorizationPattern.Replace(value, $"$1{RedactedValue}");
            redacted = CookieHeaderPattern.Replace(redacted, $"$1{RedactedValue}");
            redacted = SecretPairPattern.Replace(redacted, $"$1$2{RedactedValue}");
            redacted = SensitiveQueryPattern.Replace(redacted, $"$1{RedactedValue}");
            redacted = JwtPattern.Replace(redacted, RedactedValue);
            redacted = EmailPattern.Replace(redacted, "[REDACTED_EMAIL]");
            redacted = WindowsUserPathPattern.Replace(redacted, "$1[REDACTED_USER]");
            redacted = IpV4Pattern.Replace(redacted, "[REDACTED_IP]");
            return redacted;
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTION_TIMEOUT]";
        }
    }

    public bool IsSensitiveKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        SensitiveKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public bool IsPersonalDataKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        PersonalDataKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
