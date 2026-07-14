namespace EasyShare.Services;

public sealed class AppDataPaths
{
    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "easyshare.db");

    public string TokenCachePath => Path.Combine(DataDirectory, "msal.cache");

    public string BrowserProfilePath => Path.Combine(DataDirectory, "BrowserProfile");

    public string UploadQueueDirectory => Path.Combine(DataDirectory, "UploadQueue");

    public string UploadPayloadKeyPath => Path.Combine(DataDirectory, "upload-payload.key");

    public string OfflineCacheDirectory => Path.Combine(DataDirectory, "OfflineCache");

    public string OfflineCacheKeyPath => Path.Combine(DataDirectory, "offline-cache.key");

    public string OfflineCacheIndexPath => Path.Combine(OfflineCacheDirectory, "index.json");

    public string LogDirectory => Path.Combine(DataDirectory, "Logs");

    public string UserPolicyPath => Path.Combine(DataDirectory, "Policies", "policy.json");

    public string MachinePolicyPath { get; }

    public AppDataPaths(string? dataDirectory = null, string? machinePolicyPath = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(localAppData, "EasyShare"));
        MachinePolicyPath = Path.GetFullPath(
            machinePolicyPath ?? Path.Combine(programData, "EasyShare", "Policies", "policy.json"));
    }

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
