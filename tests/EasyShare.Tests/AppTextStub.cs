namespace EasyShare.Resources;

internal static class AppText
{
    public const string PortugueseLanguageCode = "pt-BR";

    public static string Get(string key) => key;

    public static string Format(string key, params object[] args) => key;

    public static string NormalizeLanguageCode(string? languageCode) =>
        string.Equals(languageCode, "en-US", StringComparison.OrdinalIgnoreCase)
            ? "en-US"
            : PortugueseLanguageCode;
}
