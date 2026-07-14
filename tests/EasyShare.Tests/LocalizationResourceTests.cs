using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace EasyShare.Tests;

public sealed partial class LocalizationResourceTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void PortugueseAndEnglishResourcesHaveTheSameKeysAndPlaceholders()
    {
        var root = GetRepositoryRoot();
        var portuguese = LoadStrings(Path.Combine(root, "src", "EasyShare", "Resources", "AppStrings.xml"));
        var english = LoadStrings(Path.Combine(root, "src", "EasyShare", "Resources", "AppStrings.en-US.xml"));

        Assert.Equal(portuguese.Keys.Order(), english.Keys.Order());
        foreach (var key in portuguese.Keys)
        {
            Assert.Equal(
                Placeholders().Matches(portuguese[key]).Select(match => match.Value).Order(),
                Placeholders().Matches(english[key]).Select(match => match.Value).Order());
            Assert.False(string.IsNullOrWhiteSpace(english[key]), $"English string '{key}' is empty.");
        }
    }

    private static IReadOnlyDictionary<string, string> LoadStrings(string path)
    {
        var document = XDocument.Load(path);
        return document
            .Descendants()
            .Where(element => element.Name.LocalName == "String")
            .ToDictionary(
                element => Assert.IsType<XAttribute>(element.Attribute(Xaml + "Key")).Value,
                element => element.Value,
                StringComparer.Ordinal);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EasyShare.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the EasyShare repository root.");
    }

    [GeneratedRegex(@"\{\d+(?::[^}]+)?\}")]
    private static partial Regex Placeholders();
}
