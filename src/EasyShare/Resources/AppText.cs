using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.UI.Xaml;

namespace EasyShare.Resources;

public static class AppText
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Strings = new(LoadStrings);

    public static void RegisterResources(ResourceDictionary resources)
    {
        foreach (var item in Strings.Value)
        {
            resources[item.Key] = item.Value;
        }
    }

    public static string Get(string key)
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is string text)
            {
                return text;
            }
        }
        catch
        {
            // During very early startup the WinUI resource dictionary can be unavailable.
        }

        return Strings.Value.TryGetValue(key, out var fallback)
            ? fallback
            : key;
    }

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    private static IReadOnlyDictionary<string, string> LoadStrings()
    {
        var assembly = typeof(AppText).GetTypeInfo().Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(".Resources.AppStrings.xml", StringComparison.Ordinal));

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
