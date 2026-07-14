using System.Text.RegularExpressions;
using Xunit;

namespace EasyShare.Tests;

public sealed class InstallerPayloadContractTests
{
    [Fact]
    [Trait("Gate", "Installer")]
    public void SetupMetadataIncludesWindowsAppRuntimeLengthAndSha256()
    {
        var source = ReadRepositoryFile("installer", "EasyShareSetupExe", "Program.cs");

        Assert.Matches(
            new Regex(
                @"GetManifestResourceNames\(\).*?WindowsAppRuntime",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            source);
        Assert.Matches(
            new Regex(
                @"\b(?:WindowsAppRuntime|Runtime)Length\b",
                RegexOptions.IgnoreCase),
            source);
        Assert.Matches(
            new Regex(
                @"\b(?:WindowsAppRuntime|Runtime)Sha256\b",
                RegexOptions.IgnoreCase),
            source);
        Assert.Matches(
            new Regex(
                @"var\s+(?<runtime>\w+)\s*=\s*ReadResourceMetadata\(assembly,\s*runtimeResource\).*?new\s+EmbeddedPackageMetadata\(.*?\k<runtime>\.Length.*?\k<runtime>\.Sha256",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            source);
        Assert.Matches(
            new Regex(
                @"ReadResourceMetadata\s*\(.*?SHA256\.HashData\(resource\)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            source);
    }

    [Fact]
    [Trait("Gate", "Installer")]
    public void ReleasePublicationRequiresAndVerifiesTheEmbeddedWindowsAppRuntime()
    {
        var script = ReadRepositoryFile("scripts", "Publish-GitHubRelease.ps1");

        Assert.Matches(
            new Regex(
                @"param\s*\(.*?\[string\]\s*\$WindowsAppRuntimePath\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            script);
        Assert.Contains(
            "IsNullOrWhiteSpace($WindowsAppRuntimePath)",
            script,
            StringComparison.OrdinalIgnoreCase);
        Assert.Matches(
            new Regex(
                @"Get-Item\s+-LiteralPath\s+\$(?:resolved)?WindowsAppRuntimePath\b",
                RegexOptions.IgnoreCase),
            script);
        Assert.Matches(
            new Regex(
                @"Get-FileHash\s+-LiteralPath\s+\$(?:resolved)?WindowsAppRuntimePath\s+-Algorithm\s+SHA256",
                RegexOptions.IgnoreCase),
            script);
        Assert.Matches(
            new Regex(
                @"\$setupMetadata\.(?:WindowsAppRuntime|Runtime)Length\b.*?\.Length",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            script);
        Assert.Matches(
            new Regex(
                @"\$setupMetadata\.(?:WindowsAppRuntime|Runtime)Sha256\b.*?(?:windowsAppRuntime|runtime)Sha256",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            script);
    }

    [Fact]
    [Trait("Gate", "Installer")]
    public void InstallerValidatesWindowsAppRuntimeStructureAndSignatureBeforeDeployment()
    {
        var script = ReadRepositoryFile("installer", "Install-EasyShare.ps1");
        var validationDeclaration = Regex.Match(
            script,
            @"(?im)^function\s+(?<name>(?:Test|Assert|Confirm|Validate)-WindowsAppRuntime[\w-]*)\s*\{");

        Assert.True(
            validationDeclaration.Success,
            "The installer must declare an explicit Windows App Runtime validation function.");
        Assert.Contains("AppxManifest.xml", script, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(
            new Regex(
                @"(?:ZipFile\]::OpenRead|ZipArchive)",
                RegexOptions.IgnoreCase),
            script);
        Assert.Contains("Get-AuthenticodeSignature", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SignatureStatus]::Valid", script, StringComparison.OrdinalIgnoreCase);

        var addAppxPackageIndex = script.IndexOf("Add-AppxPackage", StringComparison.OrdinalIgnoreCase);
        Assert.True(addAppxPackageIndex >= 0, "The installer must deploy through Add-AppxPackage.");

        var validationFunctionName = validationDeclaration.Groups["name"].Value;
        var validationReferences = Regex.Matches(
            script,
            Regex.Escape(validationFunctionName),
            RegexOptions.IgnoreCase);
        Assert.True(
            validationReferences.Count >= 2,
            $"{validationFunctionName} must be invoked, not only declared.");
        Assert.True(
            validationReferences[^1].Index < addAppxPackageIndex,
            $"{validationFunctionName} must run before Add-AppxPackage.");
    }

    [Theory]
    [Trait("Gate", "Installer")]
    [InlineData("WinFsp", "winFsp", "winFspResource", "winfsp-2.1.25156.msi")]
    [InlineData("InstallScript", "script", "installScriptResource", "Install-EasyShare.ps1")]
    [InlineData("Certificate", "certificate", "certificateResource", "EasyShare_TestCertificate.cer")]
    public void SetupMetadataIncludesEveryCriticalEmbeddedPayload(
        string metadataPrefix,
        string metadataVariable,
        string resourceVariable,
        string fileName)
    {
        var source = ReadRepositoryFile("installer", "EasyShareSetupExe", "Program.cs");

        Assert.Contains(fileName, source, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(
            new Regex(
                $@"var\s+{Regex.Escape(metadataVariable)}\s*=\s*ReadResourceMetadata\(assembly,\s*{Regex.Escape(resourceVariable)}\)",
                RegexOptions.IgnoreCase),
            source);
        Assert.Matches(
            new Regex($@"\b{Regex.Escape(metadataPrefix)}FileName\b", RegexOptions.IgnoreCase),
            source);
        Assert.Matches(
            new Regex($@"\b{Regex.Escape(metadataPrefix)}Length\b", RegexOptions.IgnoreCase),
            source);
        Assert.Matches(
            new Regex($@"\b{Regex.Escape(metadataPrefix)}Sha256\b", RegexOptions.IgnoreCase),
            source);
        Assert.Matches(
            new Regex(
                $@"new\s+EmbeddedPackageMetadata\(.*?{Regex.Escape(metadataVariable)}\.Length.*?{Regex.Escape(metadataVariable)}\.Sha256",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            source);
    }

    [Theory]
    [Trait("Gate", "Installer")]
    [InlineData("WinFspPath", "WinFsp", "winFsp")]
    [InlineData("InstallScriptPath", "InstallScript", "installScript")]
    [InlineData("CertificatePath", "Certificate", "certificate")]
    public void ReleasePublicationRequiresAndVerifiesEveryCriticalEmbeddedPayload(
        string pathParameter,
        string metadataPrefix,
        string localVariable)
    {
        var script = ReadRepositoryFile("scripts", "Publish-GitHubRelease.ps1");

        Assert.Matches(
            new Regex(
                $@"param\s*\(.*?\[string\]\s*\${Regex.Escape(pathParameter)}\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            script);
        Assert.Contains(
            $"IsNullOrWhiteSpace(${pathParameter})",
            script,
            StringComparison.OrdinalIgnoreCase);
        Assert.Matches(
            new Regex(
                $@"\$resolved{Regex.Escape(pathParameter)}\s*=\s*Resolve-RequiredFile\s+\${Regex.Escape(pathParameter)}\b",
                RegexOptions.IgnoreCase),
            script);
        Assert.Matches(
            new Regex(
                $@"Get-Item\s+-LiteralPath\s+\$resolved{Regex.Escape(pathParameter)}\b",
                RegexOptions.IgnoreCase),
            script);
        Assert.Matches(
            new Regex(
                $@"Get-FileHash\s+-LiteralPath\s+\$resolved{Regex.Escape(pathParameter)}\s+-Algorithm\s+SHA256",
                RegexOptions.IgnoreCase),
            script);
        Assert.Matches(
            new Regex(
                $@"\$setupMetadata\.{Regex.Escape(metadataPrefix)}FileName\b.*?\${Regex.Escape(localVariable)}FileName",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            script);
        Assert.Matches(
            new Regex(
                $@"\$setupMetadata\.{Regex.Escape(metadataPrefix)}Length\b.*?\${Regex.Escape(localVariable)}File\.Length",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            script);
        Assert.Matches(
            new Regex(
                $@"\$setupMetadata\.{Regex.Escape(metadataPrefix)}Sha256\b.*?\${Regex.Escape(localVariable)}Sha256",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            script);
    }

    [Fact]
    [Trait("Gate", "Installer")]
    public void WinFspAuthenticodeIsValidatedBeforeInstallationAndPublication()
    {
        var installer = ReadRepositoryFile("installer", "Install-EasyShare.ps1");
        var publisher = ReadRepositoryFile("scripts", "Publish-GitHubRelease.ps1");
        var declaration = Regex.Match(
            installer,
            @"(?im)^function\s+(?<name>(?:Test|Assert|Confirm|Validate)-WinFsp(?:Installer|Signature)[\w-]*)\s*\{");

        Assert.True(declaration.Success, "The installer must declare an explicit WinFsp validation function.");
        var validationFunctionName = declaration.Groups["name"].Value;
        var validationReferences = Regex.Matches(
            installer,
            Regex.Escape(validationFunctionName),
            RegexOptions.IgnoreCase);
        var msiexecInvocation = Regex.Match(
            installer,
            @"Start-Process\s+-FilePath\s+\$msiexecPath\b",
            RegexOptions.IgnoreCase);
        Assert.True(msiexecInvocation.Success, "The installer must launch the approved WinFsp MSI by absolute path.");
        Assert.True(validationReferences.Count >= 2, $"{validationFunctionName} must be invoked.");
        Assert.True(
            validationReferences[^1].Index < msiexecInvocation.Index,
            $"{validationFunctionName} must run before msiexec.");
        var nextFunctionIndex = installer.IndexOf(
            "\nfunction ",
            declaration.Index + declaration.Length,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(nextFunctionIndex > declaration.Index, "The WinFsp validation function must have a bounded body.");
        var validationFunction = installer[declaration.Index..nextFunctionIndex];
        Assert.Contains("Get-AuthenticodeSignature", validationFunction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SignatureStatus]::Valid", validationFunction, StringComparison.OrdinalIgnoreCase);

        Assert.Matches(
            new Regex(
                @"Get-AuthenticodeSignature\s+\$resolvedWinFspPath.*?\$winFspSignature\.Status\s+-ne\s+[""']Valid[""']",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            publisher);
    }

    [Fact]
    [Trait("Gate", "Installer")]
    public void WindowsAppRuntimePublisherIsPinnedToMicrosoft()
    {
        const string expectedPublisher =
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        var installer = ReadRepositoryFile("installer", "Install-EasyShare.ps1");
        var publisher = ReadRepositoryFile("scripts", "Publish-GitHubRelease.ps1");

        Assert.Contains(expectedPublisher, installer, StringComparison.Ordinal);
        Assert.Contains(expectedPublisher, publisher, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"\$identity\.Publisher\b.*?\$expectedWindowsAppRuntimePublisher\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            installer);
        Assert.Matches(
            new Regex(
                @"\$runtimeIdentity\.Publisher\b.*?\$ExpectedWindowsAppRuntimePublisher\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            publisher);
    }

    [Fact]
    [Trait("Gate", "Installer")]
    public void MissingRuntimeIsRejectedOnlyForTheCompleteSetup()
    {
        var script = ReadRepositoryFile("installer", "Install-EasyShare.ps1");

        Assert.Matches(
            new Regex(
                @"\$isIncrementalPatchInstall\s*=\s*-not\s+\[string\]::IsNullOrWhiteSpace\(\$PackagePath\)",
                RegexOptions.IgnoreCase),
            script);
        Assert.Matches(
            new Regex(
                @"elseif\s*\(\s*-not\s+\$isIncrementalPatchInstall\s*\)\s*\{\s*throw\s+[""'][^""']*Windows App Runtime",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            script);
    }

    [Fact]
    [Trait("Gate", "Installer")]
    public void WrapperElevatesOnlyProtectedMachinePrerequisitesBeforePerUserInstall()
    {
        var wrapper = ReadRepositoryFile("installer", "EasyShareSetupExe", "Program.cs");
        var installer = ReadRepositoryFile("installer", "Install-EasyShare.ps1");

        Assert.Contains("--machine-prerequisites", wrapper, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"if\s*\(machinePrerequisitesOnly\).*?IsAdministrator\(\).*?return\s+RunMachinePrerequisites\(\)",
                RegexOptions.Singleline),
            wrapper);

        var elevatedStart = wrapper.IndexOf("static int RunElevatedMachinePrerequisites()", StringComparison.Ordinal);
        var machineStart = wrapper.IndexOf("static int RunMachinePrerequisites()", StringComparison.Ordinal);
        var installScriptStart = wrapper.IndexOf("static int RunInstallScript(", StringComparison.Ordinal);
        var secureStagingStart = wrapper.IndexOf("static string CreateSecureMachineStagingDirectory()", StringComparison.Ordinal);
        var administratorCheckStart = wrapper.IndexOf("static bool IsAdministrator()", StringComparison.Ordinal);
        Assert.True(elevatedStart >= 0 && machineStart > elevatedStart, "The wrapper must isolate its elevation helper.");
        Assert.True(installScriptStart > machineStart, "The machine-prerequisite method must have a bounded body.");
        Assert.True(
            secureStagingStart > installScriptStart && administratorCheckStart > secureStagingStart,
            "The protected staging method must have a bounded body.");

        var elevatedMethod = wrapper[elevatedStart..machineStart];
        Assert.Contains("FileName = executablePath", elevatedMethod, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = true", elevatedMethod, StringComparison.Ordinal);
        Assert.Contains("Verb = \"runas\"", elevatedMethod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ArgumentList.Add(machinePrerequisitesArgument)", elevatedMethod, StringComparison.Ordinal);

        var machineMethod = wrapper[machineStart..installScriptStart];
        Assert.Contains("CreateSecureMachineStagingDirectory()", machineMethod, StringComparison.Ordinal);
        Assert.Contains("ExtractPayload(stagingDirectory)", machineMethod, StringComparison.Ordinal);
        Assert.Contains("machinePrerequisitesOnly: true", machineMethod, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"var\s+logPath\s*=\s*Path\.Combine\(\s*stagingDirectory\s*,",
                RegexOptions.Singleline),
            machineMethod);
        Assert.DoesNotContain("CommonApplicationData", machineMethod, StringComparison.Ordinal);

        var secureStagingMethod = wrapper[secureStagingStart..administratorCheckStart];
        Assert.Contains("Environment.SpecialFolder.ProgramFiles", secureStagingMethod, StringComparison.Ordinal);
        Assert.Contains("EnsureDirectoryTreeHasNoReparsePoints", secureStagingMethod, StringComparison.Ordinal);
        Assert.Contains("DirectorySecurity", secureStagingMethod, StringComparison.Ordinal);
        Assert.Contains("SetAccessRuleProtection(isProtected: true, preserveInheritance: false)", secureStagingMethod, StringComparison.Ordinal);
        Assert.Contains("WellKnownSidType.BuiltinAdministratorsSid", secureStagingMethod, StringComparison.Ordinal);
        Assert.Contains("WellKnownSidType.LocalSystemSid", secureStagingMethod, StringComparison.Ordinal);
        Assert.Contains("FileSystemRights.FullControl", secureStagingMethod, StringComparison.Ordinal);

        var topLevel = wrapper[..wrapper.IndexOf("static void ExtractPayload(", StringComparison.Ordinal)];
        var prerequisiteDispatchIndex = topLevel.IndexOf("var prerequisiteExitCode", StringComparison.Ordinal);
        var perUserTempIndex = topLevel.IndexOf("var tempRoot", StringComparison.Ordinal);
        var perUserInstallIndex = topLevel.IndexOf("machinePrerequisitesOnly: false", StringComparison.Ordinal);
        Assert.True(
            prerequisiteDispatchIndex >= 0 &&
            perUserTempIndex > prerequisiteDispatchIndex &&
            perUserInstallIndex > perUserTempIndex,
            "The original process must resume and install the MSIX in the initiating user's context.");
        Assert.DoesNotContain("return RunElevatedMachinePrerequisites()", topLevel, StringComparison.Ordinal);

        Assert.DoesNotContain("$env:ProgramData", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InstallerCache", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Copy-MachinePrerequisitesToCache", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(
            new Regex(
                @"Start-Process\s+powershell(?:\.exe)?\b[^\r\n]*-Verb\s+RunAs",
                RegexOptions.IgnoreCase),
            installer);
    }

    [Fact]
    [Trait("Gate", "Installer")]
    public void SetupInvokesWindowsPowerShellByAbsoluteSystemPath()
    {
        var wrapper = ReadRepositoryFile("installer", "EasyShareSetupExe", "Program.cs");
        var methodStart = wrapper.IndexOf("static int RunInstallScript(", StringComparison.Ordinal);
        var nextMethodStart = wrapper.IndexOf("static string CreateSecureMachineStagingDirectory()", StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && nextMethodStart > methodStart, "The PowerShell launcher must have a bounded body.");
        var launcher = wrapper[methodStart..nextMethodStart];
        Assert.Contains("Environment.SpecialFolder.System", launcher, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"Path\.Combine\(.*?SpecialFolder\.System.*?[""']WindowsPowerShell[""'].*?[""']v1\.0[""'].*?[""']powershell\.exe[""']",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            launcher);
        Assert.Contains("File.Exists(powershellPath)", launcher, StringComparison.Ordinal);
        Assert.Contains("FileName = powershellPath", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("FileName = \"powershell.exe\"", launcher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Gate", "Installer")]
    public void WinFspInstallationInvokesMsiexecByAbsoluteSystemPath()
    {
        var installer = ReadRepositoryFile("installer", "Install-EasyShare.ps1");
        var functionStart = installer.IndexOf("function Install-WinFsp", StringComparison.OrdinalIgnoreCase);
        var nextFunctionStart = installer.IndexOf("\nfunction ", functionStart + 1, StringComparison.OrdinalIgnoreCase);

        Assert.True(functionStart >= 0 && nextFunctionStart > functionStart, "The WinFsp installer function must have a bounded body.");
        var installWinFsp = installer[functionStart..nextFunctionStart];
        Assert.Matches(
            new Regex(
                @"\[System\.Environment\]::GetFolderPath\(\[System\.Environment\+SpecialFolder\]::System\)",
                RegexOptions.IgnoreCase),
            installWinFsp);
        Assert.Matches(
            new Regex(
                @"\$msiexecPath\s*=\s*Join-Path\s+\$systemDirectory\s+[""']msiexec\.exe[""']",
                RegexOptions.IgnoreCase),
            installWinFsp);
        Assert.Contains("Test-Path -LiteralPath $msiexecPath -PathType Leaf", installWinFsp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Start-Process -FilePath $msiexecPath", installWinFsp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(
            new Regex(@"Start-Process\s+(?:-FilePath\s+)?msiexec(?:\.exe)?\b", RegexOptions.IgnoreCase),
            installWinFsp);
    }

    [Fact]
    [Trait("Gate", "Installer")]
    public void SetupMetadataRejectsUnexpectedEmbeddedResources()
    {
        var source = ReadRepositoryFile("installer", "EasyShareSetupExe", "Program.cs");
        var metadataStart = source.IndexOf("static EmbeddedPackageMetadata ReadEmbeddedPackageMetadata()", StringComparison.Ordinal);
        var nextMethodStart = source.IndexOf("static string GetSingleResourceName(", StringComparison.Ordinal);

        Assert.True(metadataStart >= 0 && nextMethodStart > metadataStart, "The metadata reader must have a bounded body.");
        var metadataReader = source[metadataStart..nextMethodStart];
        Assert.Contains("expectedResourceNames", metadataReader, StringComparison.Ordinal);
        Assert.Contains("unexpectedResources", metadataReader, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"resourceNames\.Length\s*!=\s*expectedResourceNames\.Length\s*\|\|\s*unexpectedResources\.Length\s*!=\s*0",
                RegexOptions.Singleline),
            metadataReader);
        Assert.Contains("throw new InvalidOperationException", metadataReader, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Gate", "Installer")]
    public void WinFspSignerThumbprintIsPinnedForInstallAndPublication()
    {
        var installer = ReadRepositoryFile("installer", "Install-EasyShare.ps1");
        var publisher = ReadRepositoryFile("scripts", "Publish-GitHubRelease.ps1");
        var installerPin = Regex.Match(
            installer,
            @"\$expectedWinFspSignerThumbprint\s*=\s*[""'](?<thumbprint>[0-9A-F]{40})[""']",
            RegexOptions.IgnoreCase);
        var publisherPin = Regex.Match(
            publisher,
            @"\[string\]\s*\$ExpectedWinFspSignerThumbprint\s*=\s*[""'](?<thumbprint>[0-9A-F]{40})[""']",
            RegexOptions.IgnoreCase);

        Assert.True(installerPin.Success, "The installer must pin the approved WinFsp signer thumbprint.");
        Assert.True(publisherPin.Success, "The publisher must pin the approved WinFsp signer thumbprint.");
        Assert.Equal(
            installerPin.Groups["thumbprint"].Value,
            publisherPin.Groups["thumbprint"].Value,
            ignoreCase: true);
        Assert.Matches(
            new Regex(
                @"\$actualSignerThumbprint\b.*?\$expectedWinFspSignerThumbprint\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            installer);
        Assert.Matches(
            new Regex(
                @"\$actualWinFspSigner\b.*?\$normalizedExpectedWinFspSigner\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            publisher);
    }

    [Fact]
    [Trait("Gate", "Installer")]
    public void PublisherFreezesValidatedAssetDigestsBeforeUploadAndUsesThemForRemoteVerification()
    {
        var script = ReadRepositoryFile("scripts", "Publish-GitHubRelease.ps1");
        var resolvedAssetsIndex = script.IndexOf("$resolvedAssets = @(", StringComparison.OrdinalIgnoreCase);
        var digestFreezeIndex = script.IndexOf("$validatedAssetDigests = @{}", StringComparison.OrdinalIgnoreCase);
        var firstAuthenticodeIndex = script.IndexOf("Get-AuthenticodeSignature", StringComparison.OrdinalIgnoreCase);
        var metadataProbeIndex = script.IndexOf("function Read-ExecutableMetadata", StringComparison.OrdinalIgnoreCase);
        var completedValidationIndex = script.IndexOf(
            "Signatures and embedded package metadata validated",
            StringComparison.OrdinalIgnoreCase);
        var uploadIndex = script.IndexOf("gh release upload", StringComparison.OrdinalIgnoreCase);

        Assert.True(resolvedAssetsIndex >= 0, "Release assets must be resolved before their baseline is captured.");
        Assert.True(digestFreezeIndex >= 0, "Validated asset digests must be frozen.");
        Assert.True(digestFreezeIndex > resolvedAssetsIndex, "The digest baseline must be captured immediately after asset resolution.");
        Assert.True(
            firstAuthenticodeIndex > digestFreezeIndex && metadataProbeIndex > digestFreezeIndex,
            "The digest baseline must precede signature and executable-metadata validation.");
        Assert.True(uploadIndex > digestFreezeIndex, "Digests must be frozen before any GitHub upload.");
        var beforeUpload = script[digestFreezeIndex..uploadIndex];
        Assert.Matches(
            new Regex(
                @"\$validatedAssetDigests\[\$validatedAsset\]\s*=.*?Get-FileHash\s+-LiteralPath\s+\$validatedAsset",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            beforeUpload);
        var unchangedAssertions = Regex.Matches(
            beforeUpload,
            @"(?im)^Assert-ValidatedAssetsUnchanged\s*$",
            RegexOptions.IgnoreCase);
        Assert.True(unchangedAssertions.Count >= 2, "Assets must be rechecked after validation and immediately before upload.");
        var firstUnchangedAssertionIndex = digestFreezeIndex + unchangedAssertions[0].Index;
        var lastUnchangedAssertionIndex = digestFreezeIndex + unchangedAssertions[^1].Index;
        Assert.True(
            completedValidationIndex > digestFreezeIndex && firstUnchangedAssertionIndex > completedValidationIndex,
            "The first unchanged-assets assertion must run after all local validation.");
        Assert.True(
            lastUnchangedAssertionIndex < uploadIndex,
            "The final unchanged-assets assertion must run immediately before upload.");

        var afterUpload = script[uploadIndex..];
        Assert.Matches(
            new Regex(
                @"\$expectedDigest\s*=\s*\[string\]\$validatedAssetDigests\[\$localAsset\]",
                RegexOptions.IgnoreCase),
            afterUpload);
        Assert.Matches(
            new Regex(
                @"\$remoteAsset\[0\]\.digest\s*,\s*\$expectedDigest",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            afterUpload);
        Assert.DoesNotMatch(
            new Regex(
                @"\$expectedDigest\s*=.*?Get-FileHash",
                RegexOptions.IgnoreCase),
            afterUpload);
    }

    private static string ReadRepositoryFile(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EasyShare.slnx")))
            {
                return File.ReadAllText(
                    Path.Combine([directory.FullName, .. relativeSegments]));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the EasyShare repository root.");
    }
}
