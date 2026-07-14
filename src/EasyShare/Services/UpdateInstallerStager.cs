using System.Security.Cryptography;

namespace EasyShare.Services;

public sealed record StagedUpdateInstaller(string Path, string Sha256);

public static class UpdateInstallerStager
{
    public static string Stage(string installerPath, string? temporaryRoot = null)
        => StageVerified(installerPath, temporaryRoot).Path;

    public static StagedUpdateInstaller StageVerified(string installerPath, string? temporaryRoot = null)
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

        var sourceInfo = new FileInfo(sourcePath);
        if (sourceInfo.Length <= 0 || sourceInfo.Length > UpdateUriPolicy.MaxInstallerBytes)
        {
            throw new InvalidDataException("The update installer is empty or exceeds the staging size limit.");
        }

        var stagingRoot = Path.Combine(
            string.IsNullOrWhiteSpace(temporaryRoot) ? Path.GetTempPath() : temporaryRoot,
            "EasyShareUpdate");
        var stagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);

        var stagedPath = Path.Combine(stagingDirectory, Path.GetFileName(sourcePath));
        try
        {
            string sourceSha256;
            using (var sourceStream = new FileStream(
                       sourcePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            using (var stagedStream = new FileStream(
                       stagedPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                if (sourceStream.Length <= 0 || sourceStream.Length > UpdateUriPolicy.MaxInstallerBytes)
                {
                    throw new InvalidDataException("The update installer changed to an invalid size before staging.");
                }

                sourceStream.CopyTo(stagedStream);
                stagedStream.Flush(flushToDisk: true);
                sourceStream.Position = 0;
                sourceSha256 = Convert.ToHexString(SHA256.HashData(sourceStream));
            }

            if (!UpdateIntegrity.VerifyFile(stagedPath, sourceSha256))
            {
                throw new InvalidDataException("The staged installer does not match the downloaded installer.");
            }

            return new StagedUpdateInstaller(stagedPath, sourceSha256);
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    internal static void TryDeleteStagedInstaller(string stagedPath)
    {
        if (string.IsNullOrWhiteSpace(stagedPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(stagedPath));
        if (directory is not null &&
            string.Equals(
                Path.GetFileName(Path.GetDirectoryName(directory)),
                "EasyShareUpdate",
                StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteDirectory(directory);
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
