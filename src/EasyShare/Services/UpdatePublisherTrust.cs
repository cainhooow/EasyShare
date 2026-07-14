using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EasyShare.Services;

public sealed class UpdatePublisherPolicy
{
    private readonly HashSet<string> _allowedSubjectEncodings;
    private readonly HashSet<string> _allowedThumbprints;

    private UpdatePublisherPolicy(
        HashSet<string> allowedSubjectEncodings,
        HashSet<string> allowedThumbprints)
    {
        _allowedSubjectEncodings = allowedSubjectEncodings;
        _allowedThumbprints = allowedThumbprints;
    }

    public bool IsConfigured =>
        _allowedSubjectEncodings.Count > 0 ||
        _allowedThumbprints.Count > 0;

    public static UpdatePublisherPolicy Create(
        string? publisherSubjects,
        string? publisherThumbprints)
    {
        var subjectEncodings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subject in SplitConfigurationList(publisherSubjects))
        {
            try
            {
                var distinguishedName = new X500DistinguishedName(subject);
                subjectEncodings.Add(Convert.ToHexString(distinguishedName.RawData));
            }
            catch (CryptographicException)
            {
                // Invalid configured subjects are ignored; an empty policy fails closed.
            }
        }

        var thumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var thumbprint in SplitConfigurationList(publisherThumbprints))
        {
            var normalized = NormalizeThumbprint(thumbprint);
            if (normalized.Length is 40 or 64)
            {
                thumbprints.Add(normalized);
            }
        }

        return new UpdatePublisherPolicy(subjectEncodings, thumbprints);
    }

    public static UpdatePublisherPolicy FromAssemblyMetadata(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Key))
            .GroupBy(attribute => attribute.Key!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value,
                StringComparer.Ordinal);

        metadata.TryGetValue("UpdatePublisherSubjects", out var subjects);
        metadata.TryGetValue("UpdatePublisherThumbprints", out var thumbprints);
        return Create(subjects, thumbprints);
    }

    public bool Allows(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!IsConfigured)
        {
            return false;
        }

        var subjectEncoding = Convert.ToHexString(certificate.SubjectName.RawData);
        if (_allowedSubjectEncodings.Contains(subjectEncoding))
        {
            return true;
        }

        var sha1 = NormalizeThumbprint(certificate.GetCertHashString(HashAlgorithmName.SHA1));
        var sha256 = NormalizeThumbprint(certificate.GetCertHashString(HashAlgorithmName.SHA256));
        return _allowedThumbprints.Contains(sha1) || _allowedThumbprints.Contains(sha256);
    }

    private static IEnumerable<string> SplitConfigurationList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeThumbprint(string value) =>
        new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
}

public sealed record UpdateSignatureVerificationResult(
    bool IsTrusted,
    string PublisherSubject,
    string PublisherThumbprint,
    string FailureReason)
{
    public static UpdateSignatureVerificationResult Trusted(
        string publisherSubject,
        string publisherThumbprint) =>
        new(true, publisherSubject, publisherThumbprint, string.Empty);

    public static UpdateSignatureVerificationResult Rejected(string failureReason) =>
        new(false, string.Empty, string.Empty, failureReason);
}

public interface IUpdateSignatureVerifier
{
    UpdateSignatureVerificationResult Verify(string filePath, UpdatePublisherPolicy publisherPolicy);
}

public sealed class AuthenticodeUpdateSignatureVerifier : IUpdateSignatureVerifier
{
    private static readonly Guid GenericVerifyV2Action =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public UpdateSignatureVerificationResult Verify(
        string filePath,
        UpdatePublisherPolicy publisherPolicy)
    {
        ArgumentNullException.ThrowIfNull(publisherPolicy);
        if (!publisherPolicy.IsConfigured)
        {
            return UpdateSignatureVerificationResult.Rejected(
                "No trusted update publisher is configured for this build.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return UpdateSignatureVerificationResult.Rejected(
                "Authenticode verification is only available on Windows.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return UpdateSignatureVerificationResult.Rejected("The installer path is invalid.");
        }

        if (!File.Exists(fullPath))
        {
            return UpdateSignatureVerificationResult.Rejected("The installer file does not exist.");
        }

        var trustStatus = VerifyEmbeddedSignature(fullPath);
        if (trustStatus != 0)
        {
            return UpdateSignatureVerificationResult.Rejected(
                $"WinVerifyTrust rejected the installer signature (0x{trustStatus:X8}).");
        }

        try
        {
#pragma warning disable SYSLIB0057 // No loader API extracts a signer certificate from an Authenticode binary.
            using var signedCertificate = X509Certificate.CreateFromSignedFile(fullPath);
#pragma warning restore SYSLIB0057
            var encodedCertificate = signedCertificate.Export(X509ContentType.Cert);
            using var signerCertificate = X509CertificateLoader.LoadCertificate(encodedCertificate);
            if (!publisherPolicy.Allows(signerCertificate))
            {
                return UpdateSignatureVerificationResult.Rejected(
                    $"The trusted signature publisher is not allowed: {signerCertificate.Subject}.");
            }

            return UpdateSignatureVerificationResult.Trusted(
                signerCertificate.Subject,
                signerCertificate.GetCertHashString(HashAlgorithmName.SHA256));
        }
        catch (CryptographicException exception)
        {
            return UpdateSignatureVerificationResult.Rejected(
                $"The Authenticode signer certificate could not be read: {exception.Message}");
        }
    }

    private static int VerifyEmbeddedSignature(string filePath)
    {
        var fileInfo = new WinTrustFileInfo(filePath);
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

        var trustData = new WinTrustData(fileInfoPointer);
        var action = GenericVerifyV2Action;
        try
        {
            return WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
        }
        finally
        {
            trustData.StateAction = WinTrustDataStateAction.Close;
            _ = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [In] ref Guid actionId,
        [In, Out] ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;

        public IntPtr FileHandle;
        public IntPtr KnownSubject;

        public WinTrustFileInfo(string filePath)
        {
            StructSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>());
            FilePath = filePath;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    private enum WinTrustDataStateAction : uint
    {
        Ignore = 0,
        Verify = 1,
        Close = 2
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public WinTrustDataStateAction StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;

        public WinTrustData(IntPtr fileInfo)
        {
            StructSize = checked((uint)Marshal.SizeOf<WinTrustData>());
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2; // WTD_UI_NONE
            RevocationChecks = 1; // WTD_REVOKE_WHOLECHAIN
            UnionChoice = 1; // WTD_CHOICE_FILE
            FileInfo = fileInfo;
            StateAction = WinTrustDataStateAction.Verify;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x80 | 0x2000; // Revocation chain excluding root; reject MD2/MD4.
            UiContext = 0;
            SignatureSettings = IntPtr.Zero;
        }
    }
}
