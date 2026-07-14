using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class UpdatePublisherTrustTests
{
    [Fact]
    public void PublisherPolicyMatchesExactSubjectOrThumbprintAndFailsClosedWhenEmpty()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=EasyShare Release, O=ArchGTi.Tech", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        Assert.True(UpdatePublisherPolicy.Create(certificate.Subject, null).Allows(certificate));
        Assert.True(UpdatePublisherPolicy.Create(
            null,
            certificate.GetCertHashString(HashAlgorithmName.SHA256)).Allows(certificate));
        Assert.False(UpdatePublisherPolicy.Create("CN=Another Publisher", null).Allows(certificate));
        Assert.False(UpdatePublisherPolicy.Create(null, null).Allows(certificate));
    }

    [Fact]
    public void AuthenticodeVerifierRejectsUnsignedFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not signed");
            var verifier = new AuthenticodeUpdateSignatureVerifier();

            var result = verifier.Verify(path, UpdatePublisherPolicy.Create("CN=EasyShare Release", null));

            Assert.False(result.IsTrusted);
            Assert.NotEmpty(result.FailureReason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TrustGateVerifiesDownloadedAndStagedCopies()
    {
        var root = CreateRoot();
        var sourcePath = CreateInstaller(root);
        var verifier = new SequenceVerifier(
            UpdateSignatureVerificationResult.Trusted("CN=Allowed", "AA"),
            UpdateSignatureVerificationResult.Trusted("CN=Allowed", "AA"));
        var gate = new UpdateInstallerTrustGate(
            UpdatePublisherPolicy.Create("CN=Allowed", null),
            verifier);

        try
        {
            var trusted = gate.Prepare(sourcePath, root);

            Assert.Equal(2, verifier.Paths.Count);
            Assert.Equal(Path.GetFullPath(sourcePath), Path.GetFullPath(verifier.Paths[0]));
            Assert.Equal(Path.GetFullPath(trusted.Path), Path.GetFullPath(verifier.Paths[1]));
            Assert.True(File.Exists(trusted.Path));
            Assert.True(UpdateIntegrity.VerifyFile(trusted.Path, trusted.Sha256));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TrustGateRemovesStagingWhenSecondVerificationFails()
    {
        var root = CreateRoot();
        var sourcePath = CreateInstaller(root);
        var verifier = new SequenceVerifier(
            UpdateSignatureVerificationResult.Trusted("CN=Allowed", "AA"),
            UpdateSignatureVerificationResult.Rejected("tampered"));
        var gate = new UpdateInstallerTrustGate(
            UpdatePublisherPolicy.Create("CN=Allowed", null),
            verifier);

        try
        {
            Assert.Throws<SecurityException>(() => gate.Prepare(sourcePath, root));
            var stagingRoot = Path.Combine(root, "EasyShareUpdate");
            Assert.False(Directory.Exists(stagingRoot) && Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "EasyShareTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateInstaller(string root)
    {
        var path = Path.Combine(root, "EasyShareSetup.exe");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private sealed class SequenceVerifier(params UpdateSignatureVerificationResult[] results) : IUpdateSignatureVerifier
    {
        private readonly Queue<UpdateSignatureVerificationResult> _results = new(results);

        public List<string> Paths { get; } = [];

        public UpdateSignatureVerificationResult Verify(string filePath, UpdatePublisherPolicy publisherPolicy)
        {
            Paths.Add(filePath);
            return _results.Dequeue();
        }
    }
}
