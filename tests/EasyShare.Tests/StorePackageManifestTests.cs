using System.Xml.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace EasyShare.Tests;

public sealed class StorePackageManifestTests
{
    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace RestrictedCapabilities =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
    private static readonly XNamespace Uap =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Desktop =
        "http://schemas.microsoft.com/appx/manifest/desktop/windows10";

    [Fact]
    public void ManifestMatchesMicrosoftStoreAcceptanceRequirements()
    {
        var document = XDocument.Load(GetManifestPath());
        var package = Assert.IsType<XElement>(document.Root);

        var identity = Assert.Single(package.Elements(Foundation + "Identity"));
        Assert.Equal(
            "ArchGTi.Tech.EasyPointShare",
            Assert.IsType<XAttribute>(identity.Attribute("Name")).Value);
        Assert.Equal(
            "CN=EE61A1E4-12AD-426A-AE25-03DBAA7F7171",
            Assert.IsType<XAttribute>(identity.Attribute("Publisher")).Value);
        Assert.Equal(
            "ArchGTi.Tech.EasyPointShare_qjy908w4vdt2j",
            CalculatePackageFamilyName(
                Assert.IsType<XAttribute>(identity.Attribute("Name")).Value,
                Assert.IsType<XAttribute>(identity.Attribute("Publisher")).Value));
        var version = Version.Parse(Assert.IsType<XAttribute>(identity.Attribute("Version")).Value);
        Assert.Equal(0, version.Revision);

        var properties = Assert.Single(package.Elements(Foundation + "Properties"));
        Assert.Equal(
            "EasyPointShare",
            Assert.Single(properties.Elements(Foundation + "DisplayName")).Value);
        Assert.Equal(
            "ArchGTi.Tech",
            Assert.Single(properties.Elements(Foundation + "PublisherDisplayName")).Value);

        var applications = Assert.Single(package.Elements(Foundation + "Applications"));
        var application = Assert.Single(applications.Elements(Foundation + "Application"));
        var visualElements = Assert.Single(application.Elements(Uap + "VisualElements"));
        Assert.Equal(
            "EasyPointShare",
            Assert.IsType<XAttribute>(visualElements.Attribute("DisplayName")).Value);
        var extensions = Assert.Single(application.Elements(Foundation + "Extensions"));
        var startupExtension = Assert.Single(
            extensions.Elements(Desktop + "Extension"),
            element => string.Equals(
                element.Attribute("Category")?.Value,
                "windows.startupTask",
                StringComparison.Ordinal));
        var startupTask = Assert.Single(startupExtension.Elements(Desktop + "StartupTask"));
        Assert.Equal(
            "EasyPointShare",
            Assert.IsType<XAttribute>(startupTask.Attribute("DisplayName")).Value);

        var dependencies = Assert.Single(package.Elements(Foundation + "Dependencies"));
        var deviceFamilies = dependencies
            .Elements(Foundation + "TargetDeviceFamily")
            .Select(element => Assert.IsType<XAttribute>(element.Attribute("Name")).Value)
            .ToArray();
        Assert.Equal(["Windows.Desktop"], deviceFamilies);

        var resources = Assert.Single(package.Elements(Foundation + "Resources"));
        var languages = resources
            .Elements(Foundation + "Resource")
            .Select(element => Assert.IsType<XAttribute>(element.Attribute("Language")).Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["en-US", "pt-BR"], languages);

        var capabilities = Assert.Single(package.Elements(Foundation + "Capabilities"));
        Assert.Contains(
            capabilities.Elements(RestrictedCapabilities + "Capability"),
            element => string.Equals(
                element.Attribute("Name")?.Value,
                "runFullTrust",
                StringComparison.Ordinal));
    }

    private static string GetManifestPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var solutionPath = Path.Combine(directory.FullName, "EasyShare.slnx");
            if (File.Exists(solutionPath))
            {
                return Path.Combine(directory.FullName, "src", "EasyShare", "Package.appxmanifest");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Não foi possível localizar a raiz do repositório EasyShare.");
    }

    private static string CalculatePackageFamilyName(string name, string publisher)
    {
        var identity = new NativePackageId
        {
            ProcessorArchitecture = 9,
            Name = name,
            Publisher = publisher
        };
        uint length = 0;
        var result = PackageFamilyNameFromId(ref identity, ref length, IntPtr.Zero);
        Assert.Equal(122, result);

        var familyName = new StringBuilder(checked((int)length));
        result = PackageFamilyNameFromId(ref identity, ref length, familyName);
        Assert.Equal(0, result);
        return familyName.ToString();
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativePackageVersion
    {
        [FieldOffset(0)]
        public ulong Value;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativePackageId
    {
        public uint Reserved;
        public uint ProcessorArchitecture;
        public NativePackageVersion Version;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Name;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Publisher;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ResourceId;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? PublisherId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int PackageFamilyNameFromId(
        ref NativePackageId packageId,
        ref uint packageFamilyNameLength,
        IntPtr packageFamilyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int PackageFamilyNameFromId(
        ref NativePackageId packageId,
        ref uint packageFamilyNameLength,
        StringBuilder packageFamilyName);
}
