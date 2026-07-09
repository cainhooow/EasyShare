namespace EasyShare.Services;

public sealed class AppDataPaths
{
    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "easyshare.db");

    public string TokenCachePath => Path.Combine(DataDirectory, "msal.cache");

    public string BrowserProfilePath => Path.Combine(DataDirectory, "BrowserProfile");

    public AppDataPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DataDirectory = Path.Combine(localAppData, "EasyShare");
    }

    public void EnsureCreated() => Directory.CreateDirectory(DataDirectory);
}
