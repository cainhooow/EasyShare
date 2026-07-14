using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Windows.Globalization;
using Windows.Storage;

namespace EasyShare.Resources;

public static class AppText
{
    public const string PortugueseLanguageCode = "pt-BR";
    public const string EnglishLanguageCode = "en-US";
    private const string StartupLanguageSettingKey = "EasyShare.LanguageCode";

    private static readonly object SyncRoot = new();
    private static IReadOnlyDictionary<string, string> _strings = LoadStrings(PortugueseLanguageCode);
    private static string _currentLanguageCode = PortugueseLanguageCode;

    public static event EventHandler? LanguageChanged;

    public static string CurrentLanguageCode
    {
        get
        {
            lock (SyncRoot)
            {
                return _currentLanguageCode;
            }
        }
    }

    public static void RegisterResources(ResourceDictionary resources)
    {
        foreach (var item in GetCurrentStrings())
        {
            resources[item.Key] = item.Value;
        }
    }

    public static bool SetLanguage(string? languageCode)
    {
        var normalized = NormalizeLanguageCode(languageCode);
        TrySetPrimaryLanguageOverride(normalized);
        lock (SyncRoot)
        {
            if (string.Equals(_currentLanguageCode, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _strings = LoadStrings(normalized);
            _currentLanguageCode = normalized;
        }

        try
        {
            if (Application.Current?.Resources is { } resources)
            {
                RegisterResources(resources);
            }
        }
        catch
        {
            // During very early startup the WinUI resource dictionary can be unavailable.
        }

        LanguageChanged?.Invoke(null, EventArgs.Empty);
        return true;
    }

    public static string Get(string key)
    {
        var strings = GetCurrentStrings();
        return strings.TryGetValue(key, out var value)
            ? value
            : key;
    }

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.GetCultureInfo(CurrentLanguageCode), Get(key), args);

    public static string NormalizeLanguageCode(string? languageCode) =>
        string.Equals(languageCode, EnglishLanguageCode, StringComparison.OrdinalIgnoreCase)
            ? EnglishLanguageCode
            : PortugueseLanguageCode;

    public static string LoadStartupLanguageCode()
    {
        try
        {
            return NormalizeLanguageCode(
                ApplicationData.Current.LocalSettings.Values[StartupLanguageSettingKey]?.ToString());
        }
        catch
        {
            return PortugueseLanguageCode;
        }
    }

    public static void SaveStartupLanguageCode(string? languageCode)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[StartupLanguageSettingKey] =
                NormalizeLanguageCode(languageCode);
        }
        catch
        {
            // The SQLite setting remains authoritative if local app settings are unavailable.
        }
    }

    private static IReadOnlyDictionary<string, string> GetCurrentStrings()
    {
        lock (SyncRoot)
        {
            return _strings;
        }
    }

    private static void TrySetPrimaryLanguageOverride(string languageCode)
    {
        try
        {
            ApplicationLanguages.PrimaryLanguageOverride = languageCode;
        }
        catch
        {
            // Unpackaged or early-startup environments may not expose the globalization API.
        }
    }

    private static IReadOnlyDictionary<string, string> LoadStrings(string languageCode)
    {
        var portuguese = LoadResource(PortugueseLanguageCode);
        if (string.Equals(languageCode, PortugueseLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            return portuguese;
        }

        var localized = new Dictionary<string, string>(portuguese, StringComparer.Ordinal);
        foreach (var item in LoadResource(languageCode))
        {
            localized[item.Key] = item.Value;
        }

        return localized;
    }

    private static IReadOnlyDictionary<string, string> LoadResource(string languageCode)
    {
        var assembly = typeof(AppText).GetTypeInfo().Assembly;
        var resourceSuffix = string.Equals(languageCode, PortugueseLanguageCode, StringComparison.OrdinalIgnoreCase)
            ? ".Resources.AppStrings.xml"
            : $".Resources.AppStrings.{languageCode}.xml";
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.Ordinal));

        if (resourceName is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AppName"] = "EasyShare"
            };
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AppName"] = "EasyShare"
            };
        }

        var document = XDocument.Load(stream);
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var element in document.Descendants().Where(element => element.Name.LocalName == "String"))
        {
            var key = element.Attribute(xamlNamespace + "Key")?.Value;
            if (!string.IsNullOrWhiteSpace(key))
            {
                strings[key] = element.Value;
            }
        }

        return strings;
    }
}
