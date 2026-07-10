using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class UpdateIntegrityTests
{
    [Fact]
    public void NormalizesGitHubDigest()
    {
        Assert.Equal(
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            UpdateIntegrity.NormalizeSha256("sha256:" + new string('a', 64)));
        Assert.Equal(string.Empty, UpdateIntegrity.NormalizeSha256("sha256:invalid"));
    }

    [Fact]
    public void RejectsTamperedDownloadedFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "original");
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            File.WriteAllText(path, "tampered");

            Assert.False(UpdateIntegrity.VerifyFile(path, expected));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
