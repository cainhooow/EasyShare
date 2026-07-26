namespace EasyShare.Services;

/// <summary>
/// Keeps DEBUG visual fixtures inside an explicit, isolated data root and prevents
/// them from changing user-wide Windows state.
/// </summary>
public static class DebugVisualTestIsolation
{
    public const string DataDirectoryEnvironmentVariable = "EASYSHARE_TEST_DATA_DIRECTORY";
    public const string DisableUploadWorkerEnvironmentVariable =
        "EASYSHARE_TEST_DISABLE_UPLOAD_WORKER";
    public const string DataDirectoryArgumentPrefix =
        "--easyshare-test-data-directory=";
    public const string DisableUploadWorkerArgument =
        "--easyshare-test-disable-upload-worker";
    public const string MarkerFileName =
        ".easyshare-debug-visual-test";

#if DEBUG
    private static readonly string MarkerFilePath =
        Path.Combine(AppContext.BaseDirectory, MarkerFileName);
    private static readonly bool MarkerIsPresent = File.Exists(MarkerFilePath);
    private static readonly string? ResolvedDataDirectory = ResolveDataDirectory(
        ResolveDataDirectoryRequest(
            Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable),
            Environment.GetCommandLineArgs(),
            MarkerFilePath));

    public static bool IsActive => ResolvedDataDirectory is not null;

    public static string? DataDirectory => ResolvedDataDirectory;

    public static string? PackageWebViewProfileDirectory =>
        ResolvedDataDirectory is null
            ? null
            : Path.Combine(
                ResolvedDataDirectory + ".PackageLocalState",
                "EBWebView");

    public static bool DisableUploadWorker =>
        IsActive &&
        (MarkerIsPresent ||
         string.Equals(
             Environment.GetEnvironmentVariable(DisableUploadWorkerEnvironmentVariable),
             "1",
             StringComparison.Ordinal) ||
         Environment.GetCommandLineArgs().Any(argument =>
             string.Equals(
                 argument,
                 DisableUploadWorkerArgument,
                 StringComparison.OrdinalIgnoreCase)));
#else
    public static bool IsActive => false;

    public static string? DataDirectory => null;

    public static string? PackageWebViewProfileDirectory => null;

    public static bool DisableUploadWorker => false;
#endif

    internal static string? ResolveDataDirectoryRequest(
        string? environmentValue,
        IEnumerable<string> commandLineArguments,
        string? markerFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        ArgumentNullException.ThrowIfNull(commandLineArguments);
        var commandLineValue = commandLineArguments
            .FirstOrDefault(argument =>
                argument.StartsWith(
                    DataDirectoryArgumentPrefix,
                    StringComparison.OrdinalIgnoreCase))
            ?[DataDirectoryArgumentPrefix.Length..];
        if (!string.IsNullOrWhiteSpace(commandLineValue))
        {
            return commandLineValue;
        }

        if (string.IsNullOrWhiteSpace(markerFilePath) ||
            !File.Exists(markerFilePath))
        {
            return null;
        }

        // DEBUG-only marker used when MSIX activation does not inherit the
        // launching shell's environment. Never log its contents.
        return File.ReadLines(markerFilePath)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
            ?.Trim();
    }

    internal static string? ResolveDataDirectory(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(candidate))
        {
            throw new ArgumentException(
                $"{DataDirectoryEnvironmentVariable} must contain a fully qualified path.",
                nameof(candidate));
        }

        var fullPath = Path.GetFullPath(candidate).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) ||
            string.Equals(fullPath, root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"{DataDirectoryEnvironmentVariable} cannot target a filesystem root.",
                nameof(candidate));
        }

        return fullPath;
    }
}
