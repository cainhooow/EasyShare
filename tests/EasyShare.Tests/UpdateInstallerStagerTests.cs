using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class UpdateInstallerStagerTests
{
    [Fact]
    public void Stage_CopiesInstallerToTemporaryExecutionDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "EasyShareTests", Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(root, "Updates", "v1.0.0.22");
        var sourcePath = Path.Combine(sourceDirectory, "EasyShareSetup.exe");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);

        try
        {
            var staged = UpdateInstallerStager.StageVerified(sourcePath, root);
            var stagedPath = staged.Path;

            Assert.True(File.Exists(stagedPath));
            Assert.NotEqual(Path.GetFullPath(sourcePath), Path.GetFullPath(stagedPath));
            var relativeStagedPath = Path.GetRelativePath(root, stagedPath);
            Assert.StartsWith(Path.Combine("EasyShareUpdate", string.Empty), relativeStagedPath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("EasyShareSetup.exe", stagedPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(stagedPath));
            Assert.True(UpdateIntegrity.VerifyFile(stagedPath, staged.Sha256));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
