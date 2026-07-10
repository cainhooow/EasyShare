using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class IncrementalPatchTests
{
    [Fact]
    public void BuildAndApplyReconstructsTheExactTargetFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "EasySharePatchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var basePath = Path.Combine(root, "EasyShare_1.0.0.21_x64.msix");
        var targetPath = Path.Combine(root, "EasyShare_1.0.0.22_x64.msix");
        var patchPath = Path.Combine(root, "EasySharePatch.bin");
        var outputPath = Path.Combine(root, "reconstructed.msix");

        try
        {
            var baseBytes = Enumerable.Range(0, 160_000).Select(value => (byte)(value % 251)).ToArray();
            var targetBytes = baseBytes.ToArray();
            for (var index = 70_000; index < 75_000; index++)
            {
                targetBytes[index] ^= 0x5A;
            }

            File.WriteAllBytes(basePath, baseBytes);
            File.WriteAllBytes(targetPath, targetBytes);

            var metadata = IncrementalPatch.Build(basePath, targetPath, patchPath);
            var applied = IncrementalPatch.Apply(basePath, patchPath, outputPath);

            Assert.Equal(metadata.TargetFileName, applied.TargetFileName);
            Assert.Equal(targetBytes, File.ReadAllBytes(outputPath));
            Assert.Equal(metadata.TargetSha256, applied.TargetSha256);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
