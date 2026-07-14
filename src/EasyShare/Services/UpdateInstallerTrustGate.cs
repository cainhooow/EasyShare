using System.Security;

namespace EasyShare.Services;

public sealed record TrustedUpdateInstaller(
    string Path,
    string Sha256,
    string PublisherSubject,
    string PublisherThumbprint);

public sealed class UpdateInstallerTrustGate
{
    private readonly UpdatePublisherPolicy _publisherPolicy;
    private readonly IUpdateSignatureVerifier _signatureVerifier;

    public UpdateInstallerTrustGate(
        UpdatePublisherPolicy publisherPolicy,
        IUpdateSignatureVerifier signatureVerifier)
    {
        _publisherPolicy = publisherPolicy ?? throw new ArgumentNullException(nameof(publisherPolicy));
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
    }

    public TrustedUpdateInstaller Prepare(string installerPath, string? temporaryRoot = null)
    {
        if (!_publisherPolicy.IsConfigured)
        {
            throw new SecurityException("No trusted update publisher is configured for this build.");
        }

        var sourceVerification = _signatureVerifier.Verify(installerPath, _publisherPolicy);
        ThrowIfRejected(sourceVerification, "downloaded");

        StagedUpdateInstaller? stagedInstaller = null;
        try
        {
            stagedInstaller = UpdateInstallerStager.StageVerified(installerPath, temporaryRoot);
            var stagedVerification = _signatureVerifier.Verify(stagedInstaller.Path, _publisherPolicy);
            ThrowIfRejected(stagedVerification, "staged");

            if (!string.Equals(
                    sourceVerification.PublisherThumbprint,
                    stagedVerification.PublisherThumbprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException("The staged installer signer changed after staging.");
            }

            if (!UpdateIntegrity.VerifyFile(stagedInstaller.Path, stagedInstaller.Sha256))
            {
                throw new SecurityException("The staged installer changed after signature verification.");
            }

            return new TrustedUpdateInstaller(
                stagedInstaller.Path,
                stagedInstaller.Sha256,
                stagedVerification.PublisherSubject,
                stagedVerification.PublisherThumbprint);
        }
        catch
        {
            if (stagedInstaller is not null)
            {
                UpdateInstallerStager.TryDeleteStagedInstaller(stagedInstaller.Path);
            }

            throw;
        }
    }

    private static void ThrowIfRejected(
        UpdateSignatureVerificationResult verification,
        string stage)
    {
        if (!verification.IsTrusted)
        {
            throw new SecurityException(
                $"The {stage} update installer is not trusted. {verification.FailureReason}");
        }
    }
}
