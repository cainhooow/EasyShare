namespace EasyShare.Services;

public static class UpdateInstallerStager
{
    public static string Stage(string installerPath, string? temporaryRoot = null)
    {
        if (string.IsNullOrWhiteSpace(installerPath))
        {
            throw new ArgumentException("An installer path is required.", nameof(installerPath));
        }

        var sourcePath = Path.GetFullPath(installerPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The update installer was not found.", sourcePath);
        }

        var stagingRoot = Path.Combine(
            string.IsNullOrWhiteSpace(temporaryRoot) ? Path.GetTempPath() : temporaryRoot,
            "EasyShareUpdate");
        var stagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);

        var stagedPath = Path.Combine(stagingDirectory, Path.GetFileName(sourcePath));
        try
        {
            File.Copy(sourcePath, stagedPath, overwrite: false);
            return stagedPath;
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // The next installer attempt can use another staging directory.
        }
    }
}
