using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Windows.Storage;

namespace EasyShare.Services;

public sealed class AppDataPaths
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "easyshare.db");

    public string TokenCachePath => Path.Combine(DataDirectory, "msal.cache");

    public string BrowserProfilePath => Path.Combine(DataDirectory, "BrowserProfile");

    /// <summary>
    /// The default WebView2 profile created by older packaged builds under
    /// Package LocalState. It is outside the virtualized LocalCache data root and
    /// must therefore be inventoried and cleared explicitly during a full reset.
    /// </summary>
    public string? PackageWebViewProfilePath { get; }

    /// <summary>
    /// The current package's LocalState boundary. A full privacy reset clears all
    /// content inside this directory, while retaining the boundary itself so no
    /// parent or sibling package storage can be affected.
    /// </summary>
    public string? PackageLocalStatePath { get; }

    public string UploadQueueDirectory => Path.Combine(DataDirectory, "UploadQueue");

    public string UploadPayloadKeyPath => Path.Combine(DataDirectory, "upload-payload.key");

    public string OfflineCacheDirectory => Path.Combine(DataDirectory, "OfflineCache");

    public string OfflineCacheKeyPath => Path.Combine(DataDirectory, "offline-cache.key");

    public string OfflineCacheIndexPath => Path.Combine(OfflineCacheDirectory, "index.json");

    public string LogDirectory => Path.Combine(DataDirectory, "Logs");

    public string UserPolicyPath => Path.Combine(DataDirectory, "Policies", "policy.json");

    public string MachinePolicyPath { get; }

    public AppDataPaths(
        string? dataDirectory = null,
        string? machinePolicyPath = null,
        string? packageWebViewProfilePath = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(localAppData, "EasyShare"));
        PackageWebViewProfilePath = NormalizePackageWebViewProfilePath(
            packageWebViewProfilePath ?? TryResolvePackageWebViewProfilePath());
        PackageLocalStatePath = PackageWebViewProfilePath is null
            ? null
            : Path.GetDirectoryName(PackageWebViewProfilePath)
              ?? throw new InvalidOperationException("The package LocalState boundary is invalid.");
        MachinePolicyPath = Path.GetFullPath(
            machinePolicyPath ?? Path.Combine(programData, "EasyShare", "Policies", "policy.json"));
    }

    private static string? TryResolvePackageWebViewProfilePath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        uint familyNameLength = 0;
        var result = GetCurrentPackageFamilyName(ref familyNameLength, null);
        if (result == AppModelErrorNoPackage)
        {
            return null;
        }

        if (result != ErrorInsufficientBuffer || familyNameLength == 0 || familyNameLength > 1024)
        {
            throw new InvalidOperationException(
                $"Could not resolve the current package identity (Win32 error {result}).");
        }

        var familyNameBuffer = new StringBuilder(checked((int)familyNameLength));
        result = GetCurrentPackageFamilyName(ref familyNameLength, familyNameBuffer);
        if (result != ErrorSuccess)
        {
            throw new InvalidOperationException(
                $"Could not read the current package identity (Win32 error {result}).");
        }

        var familyName = familyNameBuffer.ToString();
        if (string.IsNullOrWhiteSpace(familyName) ||
            familyName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidOperationException("The current package identity is invalid.");
        }

        try
        {
            var localState = ResolvePackageLocalStatePath(
                ApplicationData.Current.LocalFolder.Path,
                familyName);
            if (string.IsNullOrWhiteSpace(localState))
            {
                throw new InvalidOperationException("The package LocalState path is empty.");
            }

            return Path.Combine(localState, "EBWebView");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Could not resolve LocalState for package '{familyName}'.",
                ex);
        }
    }

    private static string ResolvePackageLocalStatePath(string path, string familyName)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var expectedVirtualPath = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            familyName,
            "LocalState"));
        if (!string.Equals(fullPath, expectedVirtualPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The package LocalState path returned by Windows is outside the current package boundary.");
        }

        var attributes = File.GetAttributes(fullPath);
        if ((attributes & System.IO.FileAttributes.Directory) == 0)
        {
            throw new InvalidOperationException("The package LocalState path is not a directory.");
        }

        if ((attributes & System.IO.FileAttributes.ReparsePoint) == 0)
        {
            return fullPath;
        }

        var resolved = new DirectoryInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true);
        if (resolved is null || (resolved.Attributes & System.IO.FileAttributes.Directory) == 0)
        {
            throw new InvalidOperationException("Windows did not resolve the package LocalState junction.");
        }

        var resolvedPath = Path.GetFullPath(resolved.FullName).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var resolvedRoot = Path.GetPathRoot(resolvedPath);
        var currentSid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(resolvedRoot) || string.IsNullOrWhiteSpace(currentSid))
        {
            throw new InvalidOperationException("The current Windows package storage identity is unavailable.");
        }

        var expectedRelativeTarget = Path.Combine(
            "WpSystem",
            currentSid,
            "AppData",
            "Local",
            "Packages",
            familyName,
            "LocalState");
        var actualRelativeTarget = Path.GetRelativePath(resolvedRoot, resolvedPath);
        if (!string.Equals(
                actualRelativeTarget,
                expectedRelativeTarget,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The package LocalState junction target is outside Windows package storage for the current user.");
        }

        return resolvedPath;
    }

    private static string? NormalizePackageWebViewProfilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!string.Equals(
                Path.GetFileName(fullPath),
                "EBWebView",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The packaged WebView profile root must end with 'EBWebView'.",
                nameof(path));
        }

        return fullPath;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(
        ref uint packageFamilyNameLength,
        StringBuilder? packageFamilyName);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        PrivateFilePermissions.TryHardenDirectory(DataDirectory);
    }

    public void EnsureUploadQueueCreated()
    {
        EnsureCreated();
        Directory.CreateDirectory(UploadQueueDirectory);
        PrivateFilePermissions.TryHardenDirectory(UploadQueueDirectory);
    }

    public void EnsureLogDirectoryCreated()
    {
        EnsureCreated();
        Directory.CreateDirectory(LogDirectory);
        PrivateFilePermissions.TryHardenDirectory(LogDirectory);
    }

    public void EnsureOfflineCacheCreated()
    {
        EnsureCreated();
        Directory.CreateDirectory(OfflineCacheDirectory);
        PrivateFilePermissions.TryHardenDirectory(OfflineCacheDirectory);
    }
}
