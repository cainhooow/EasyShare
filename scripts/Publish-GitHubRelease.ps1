param(
    [string]$Repository = "cainhooow/EasyShare",
    [string]$Version = "",
    [string]$ExePath = "dist/EasyPointShareSetup.exe",
    [string]$PatchExePath = "",
    [string]$MsixPath = "",
    [string]$BaseMsixPath = "",
    [string]$WindowsAppRuntimePath = "",
    [string]$WinFspPath = "dist/payload-exe/winfsp-2.1.25156.msi",
    [string]$InstallScriptPath = "installer/Install-EasyShare.ps1",
    [string]$CertificatePath = "dist/payload-exe/EasyShare_TestCertificate.cer",
    [string]$ExpectedBaseSha256 = "",
    [string[]]$AdditionalAssetPaths = @(),
    [string]$ChangelogPath = "CHANGELOG.md",
    [string]$TargetCommitish = "",
    [string]$ExpectedPatchSignerThumbprint = $env:EASYSHARE_PATCH_SIGNING_CERT_THUMBPRINT,
    [string]$ExpectedPackageIdentityName = "ArchGTi.Tech.EasyPointShare",
    [string]$ExpectedPackagePublisher = "CN=EE61A1E4-12AD-426A-AE25-03DBAA7F7171",
    [string]$ExpectedPublisherDisplayName = "ArchGTi.Tech",
    [string]$ExpectedWindowsAppRuntimePublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
    [string]$ExpectedWinFspSignerThumbprint = "ECC9BCB47D6506452753F3DF19677B35AEB36E2B",
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Resolve-RequiredFile([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Label path is required."
    }

    $resolvedPath = if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    }
    else {
        Join-Path $root $Path
    }
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "$Label was not found: $Path"
    }

    return $resolvedPath
}

if ([string]::IsNullOrWhiteSpace($TargetCommitish)) {
    $TargetCommitish = (& git -C $root rev-parse HEAD 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($TargetCommitish)) {
        throw "Could not resolve the source commit for the release."
    }
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is not installed or is not in PATH."
}

gh auth status | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "GitHub CLI is not authenticated. Run gh auth login first."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$manifest = Get-Content -Raw (Join-Path $root "src/EasyShare/Package.appxmanifest")
    $Version = $manifest.Package.Identity.Version
}

$tag = "v$Version"
$resolvedChangelogPath = Join-Path $root $ChangelogPath
if (-not (Test-Path $resolvedChangelogPath)) {
    throw "Changelog file was not found: $ChangelogPath"
}

$changelog = Get-Content -Raw $resolvedChangelogPath
$escapedVersion = [regex]::Escape($Version)
$versionHeaderPattern = "(?m)^##\s+\[?v?$escapedVersion\]?(?:\s+-.*)?\s*$"
$versionHeader = [regex]::Match($changelog, $versionHeaderPattern)
if (-not $versionHeader.Success) {
    throw "No changelog section was found for version $Version in $ChangelogPath. Add a '## [$Version]' section before publishing."
}

$notesStart = $versionHeader.Index + $versionHeader.Length
$notesTail = $changelog.Substring($notesStart)
$nextVersionHeader = [regex]::Match($notesTail, "(?m)^##\s+")
$releaseNotes = if ($nextVersionHeader.Success) {
    $notesTail.Substring(0, $nextVersionHeader.Index).Trim()
} else {
    $notesTail.Trim()
}

if ([string]::IsNullOrWhiteSpace($releaseNotes)) {
    throw "The changelog section for version $Version is empty. Describe what changed before publishing."
}

$requestedAssets = @($ExePath, $PatchExePath, $MsixPath) + @($AdditionalAssetPaths) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$resolvedAssets = @($requestedAssets | ForEach-Object {
    $resolvedAsset = if ([System.IO.Path]::IsPathRooted($_)) {
        [System.IO.Path]::GetFullPath($_)
    }
    else {
        Join-Path $root $_
    }
    if (-not (Test-Path -LiteralPath $resolvedAsset -PathType Leaf)) {
        throw "Release asset was not found: $_"
    }

    $resolvedAsset
})

if ($resolvedAssets.Count -eq 0) {
    throw "No release assets were found. Build the installers before publishing the release."
}

$validatedAssetDigests = @{}
foreach ($validatedAsset in $resolvedAssets) {
    $validatedAssetDigests[$validatedAsset] =
        "sha256:$((Get-FileHash -LiteralPath $validatedAsset -Algorithm SHA256).Hash.ToLowerInvariant())"
}

if ([string]::IsNullOrWhiteSpace($ExePath) -or
    [string]::IsNullOrWhiteSpace($PatchExePath) -or
    [string]::IsNullOrWhiteSpace($MsixPath) -or
    [string]::IsNullOrWhiteSpace($BaseMsixPath) -or
    [string]::IsNullOrWhiteSpace($WindowsAppRuntimePath) -or
    [string]::IsNullOrWhiteSpace($WinFspPath) -or
    [string]::IsNullOrWhiteSpace($InstallScriptPath) -or
    [string]::IsNullOrWhiteSpace($CertificatePath) -or
    [string]::IsNullOrWhiteSpace($ExpectedBaseSha256)) {
    throw "The manual setup EXE, canonical patch EXE, target MSIX, base MSIX, Windows App Runtime MSIX, WinFsp MSI, install script, certificate and expected base SHA-256 are all required."
}

$resolvedBaseMsixPath = Resolve-RequiredFile $BaseMsixPath "Base MSIX"
$resolvedWindowsAppRuntimePath = Resolve-RequiredFile $WindowsAppRuntimePath "Windows App Runtime MSIX"
$resolvedWinFspPath = Resolve-RequiredFile $WinFspPath "WinFsp MSI"
$resolvedInstallScriptPath = Resolve-RequiredFile $InstallScriptPath "Install script"
$resolvedCertificatePath = Resolve-RequiredFile $CertificatePath "Sideload certificate"

$assetNames = @($resolvedAssets | ForEach-Object { Split-Path -Leaf $_ })
$duplicateAssetNames = @($assetNames | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
if ($duplicateAssetNames.Count -gt 0) {
    throw "Release asset names must be unique: $($duplicateAssetNames -join ', ')"
}

$msiAssets = @($assetNames | Where-Object { [System.IO.Path]::GetExtension($_) -ieq '.msi' })
if ($msiAssets.Count -gt 0) {
    throw "MSI assets are not allowed in this release: $($msiAssets -join ', ')"
}

$patchAssetName = Split-Path -Leaf $PatchExePath
$patchNameMatch = [regex]::Match(
    $patchAssetName,
    '^EasySharePatch_from_(?<from>\d+_\d+_\d+_\d+)_to_(?<to>\d+_\d+_\d+_\d+)\.exe$',
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
if (-not $patchNameMatch.Success) {
    throw "Patch asset name is not canonical: $patchAssetName"
}

$expectedTargetToken = $Version.Replace('.', '_')
if (-not [string]::Equals(
        $patchNameMatch.Groups['to'].Value,
        $expectedTargetToken,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Patch target does not match release version ${Version}: $patchAssetName"
}

$normalizedExpectedBaseSha256 = ($ExpectedBaseSha256 -replace '\s', '').ToUpperInvariant()
if ($normalizedExpectedBaseSha256 -notmatch '^[0-9A-F]{64}$') {
    throw "ExpectedBaseSha256 must contain exactly 64 hexadecimal characters."
}

$actualBaseSha256 = (Get-FileHash -LiteralPath $resolvedBaseMsixPath -Algorithm SHA256).Hash
if (-not [string]::Equals(
        $actualBaseSha256,
        $normalizedExpectedBaseSha256,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The base MSIX does not match the explicitly approved SHA-256."
}

if (-not [string]::IsNullOrWhiteSpace($ExePath) -and
    (Split-Path -Leaf $ExePath) -match 'EasyShare') {
    throw "The manual recovery EXE name must not match the legacy EasyShare updater asset rules."
}

if ([string]::IsNullOrWhiteSpace($ExpectedPatchSignerThumbprint)) {
    throw "ExpectedPatchSignerThumbprint (or EASYSHARE_PATCH_SIGNING_CERT_THUMBPRINT) is required for the legacy bridge patch."
}

$codeAssets = @($resolvedAssets | Where-Object {
    [System.IO.Path]::GetExtension($_) -in @('.exe', '.msix')
})
$signatures = @{}
foreach ($asset in $codeAssets) {
    $signature = Get-AuthenticodeSignature $asset
    if ($signature.Status -ne "Valid" -or $null -eq $signature.SignerCertificate) {
        throw "$(Split-Path -Leaf $asset) does not have a locally trusted Authenticode signature."
    }

    $signatures[$asset] = $signature
}

$resolvedExePath = $resolvedAssets | Where-Object {
    [string]::Equals((Split-Path -Leaf $_), (Split-Path -Leaf $ExePath), [System.StringComparison]::OrdinalIgnoreCase)
} | Select-Object -First 1
$resolvedPatchExePath = $resolvedAssets | Where-Object {
    [string]::Equals((Split-Path -Leaf $_), $patchAssetName, [System.StringComparison]::OrdinalIgnoreCase)
} | Select-Object -First 1
$resolvedMsixPath = $resolvedAssets | Where-Object {
    [string]::Equals((Split-Path -Leaf $_), (Split-Path -Leaf $MsixPath), [System.StringComparison]::OrdinalIgnoreCase)
} | Select-Object -First 1

$normalizedExpectedPatchSigner = ($ExpectedPatchSignerThumbprint -replace '\s', '').ToUpperInvariant()
$actualPatchSigner = ($signatures[$resolvedPatchExePath].SignerCertificate.Thumbprint -replace '\s', '').ToUpperInvariant()
if (-not [string]::Equals(
        $actualPatchSigner,
        $normalizedExpectedPatchSigner,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The canonical bridge patch is not signed by the explicitly approved legacy signer."
}

$setupSigner = ($signatures[$resolvedExePath].SignerCertificate.Thumbprint -replace '\s', '').ToUpperInvariant()
$msixSigner = ($signatures[$resolvedMsixPath].SignerCertificate.Thumbprint -replace '\s', '').ToUpperInvariant()
if (-not [string]::Equals($setupSigner, $msixSigner, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The manual setup and target MSIX are not signed by the same release signer."
}

function Read-MsixManifest([string]$PackagePath, [string]$Label = "MSIX") {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $manifestEntry = $archive.Entries | Where-Object {
            [string]::Equals($_.FullName, 'AppxManifest.xml', [System.StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        if ($null -eq $manifestEntry) {
            throw "$Label does not contain AppxManifest.xml."
        }

        $manifestStream = $manifestEntry.Open()
        try {
            $reader = [System.IO.StreamReader]::new($manifestStream, [System.Text.Encoding]::UTF8, $true)
            try {
                return [xml]$reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $manifestStream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

$packageManifest = Read-MsixManifest $resolvedMsixPath "The target MSIX"
$packageIdentity = $packageManifest.Package.Identity
$packagePublisherDisplayName = [string]$packageManifest.Package.Properties.PublisherDisplayName
if (-not [string]::Equals([string]$packageIdentity.Name, $ExpectedPackageIdentityName, [System.StringComparison]::Ordinal) -or
    -not [string]::Equals([string]$packageIdentity.Publisher, $ExpectedPackagePublisher, [System.StringComparison]::Ordinal) -or
    -not [string]::Equals([string]$packageIdentity.Version, $Version, [System.StringComparison]::Ordinal) -or
    -not [string]::Equals([string]$packageIdentity.ProcessorArchitecture, 'x64', [System.StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals($packagePublisherDisplayName, $ExpectedPublisherDisplayName, [System.StringComparison]::Ordinal)) {
    throw "The target MSIX manifest does not match the approved package identity, version, architecture and display metadata."
}

$msixSignerSubject = [string]$signatures[$resolvedMsixPath].SignerCertificate.Subject
if (-not [string]::Equals($msixSignerSubject, $ExpectedPackagePublisher, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The target MSIX signer does not match its approved manifest publisher."
}

$runtimeSignature = Get-AuthenticodeSignature $resolvedWindowsAppRuntimePath
if ($runtimeSignature.Status -ne "Valid" -or $null -eq $runtimeSignature.SignerCertificate) {
    throw "The Windows App Runtime MSIX does not have a locally trusted Authenticode signature."
}

$winFspSignature = Get-AuthenticodeSignature $resolvedWinFspPath
if ($winFspSignature.Status -ne "Valid" -or $null -eq $winFspSignature.SignerCertificate) {
    throw "The WinFsp MSI does not have a locally trusted Authenticode signature."
}
$actualWinFspSigner = ($winFspSignature.SignerCertificate.Thumbprint -replace '\s', '').ToUpperInvariant()
$normalizedExpectedWinFspSigner = ($ExpectedWinFspSignerThumbprint -replace '\s', '').ToUpperInvariant()
if (-not [string]::Equals(
        $actualWinFspSigner,
        $normalizedExpectedWinFspSigner,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The WinFsp MSI is not signed by the explicitly approved vendor signer."
}

try {
    $releaseCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($resolvedCertificatePath)
}
catch {
    throw "The sideload certificate is not a valid X.509 certificate: $($_.Exception.Message)"
}
if (-not [string]::Equals(
        ($releaseCertificate.Thumbprint -replace '\s', '').ToUpperInvariant(),
        $msixSigner,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The embedded sideload certificate does not match the target MSIX signer."
}

try {
    $runtimeManifest = Read-MsixManifest $resolvedWindowsAppRuntimePath "The Windows App Runtime MSIX"
}
catch {
    throw "The Windows App Runtime dependency is not a structurally valid MSIX: $($_.Exception.Message)"
}

$runtimeIdentity = $runtimeManifest.Package.Identity
$runtimeFramework = [string]$runtimeManifest.Package.Properties.Framework
$runtimeVersion = [version][string]$runtimeIdentity.Version
if (-not [string]::Equals([string]$runtimeIdentity.Name, "Microsoft.WindowsAppRuntime.2", [System.StringComparison]::Ordinal) -or
    -not [string]::Equals([string]$runtimeIdentity.ProcessorArchitecture, "x64", [System.StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals($runtimeFramework, "true", [System.StringComparison]::OrdinalIgnoreCase) -or
    $runtimeVersion -lt [version]"2.2.0.0" -or
    -not [string]::Equals(
        [string]$runtimeIdentity.Publisher,
        $ExpectedWindowsAppRuntimePublisher,
        [System.StringComparison]::Ordinal) -or
    -not [string]::Equals(
        [string]$runtimeSignature.SignerCertificate.Subject,
        [string]$runtimeIdentity.Publisher,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The Windows App Runtime dependency has an unexpected identity, version, architecture, framework type or signer."
}

function Read-ExecutableMetadata([string]$ExecutablePath, [string]$Label, [string[]]$Arguments) {
    $metadataOutput = @(& $ExecutablePath @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "$Label metadata probe failed: $($metadataOutput -join ' ')"
    }

    try {
        return ($metadataOutput -join "`n") | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "$Label returned invalid metadata JSON: $($_.Exception.Message)"
    }
}

$patchMetadata = Read-ExecutableMetadata $resolvedPatchExePath "Patch EXE" @('--verify-base-target-json', $resolvedBaseMsixPath, $resolvedMsixPath)
$setupMetadata = Read-ExecutableMetadata $resolvedExePath "Setup EXE" @('--metadata-json')
$msixFile = Get-Item -LiteralPath $resolvedMsixPath
$msixSha256 = (Get-FileHash -LiteralPath $resolvedMsixPath -Algorithm SHA256).Hash
$msixAssetName = Split-Path -Leaf $resolvedMsixPath
$runtimeFile = Get-Item -LiteralPath $resolvedWindowsAppRuntimePath
$runtimeSha256 = (Get-FileHash -LiteralPath $resolvedWindowsAppRuntimePath -Algorithm SHA256).Hash
$runtimeFileName = Split-Path -Leaf $resolvedWindowsAppRuntimePath
$winFspFile = Get-Item -LiteralPath $resolvedWinFspPath
$winFspSha256 = (Get-FileHash -LiteralPath $resolvedWinFspPath -Algorithm SHA256).Hash
$winFspFileName = Split-Path -Leaf $resolvedWinFspPath
$installScriptFile = Get-Item -LiteralPath $resolvedInstallScriptPath
$installScriptSha256 = (Get-FileHash -LiteralPath $resolvedInstallScriptPath -Algorithm SHA256).Hash
$installScriptFileName = Split-Path -Leaf $resolvedInstallScriptPath
$certificateFile = Get-Item -LiteralPath $resolvedCertificatePath
$certificateSha256 = (Get-FileHash -LiteralPath $resolvedCertificatePath -Algorithm SHA256).Hash
$certificateFileName = Split-Path -Leaf $resolvedCertificatePath

$baseMsixFile = Get-Item -LiteralPath $resolvedBaseMsixPath
$baseMsixAssetName = Split-Path -Leaf $resolvedBaseMsixPath
if (-not [string]::Equals($patchMetadata.BaseFileName, $baseMsixAssetName, [System.StringComparison]::OrdinalIgnoreCase) -or
    [long]$patchMetadata.BaseLength -ne $baseMsixFile.Length -or
    -not [string]::Equals($patchMetadata.BaseSha256, $actualBaseSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The embedded patch was not built from the explicitly approved base MSIX."
}

if (-not [string]::Equals($patchMetadata.TargetFileName, $msixAssetName, [System.StringComparison]::OrdinalIgnoreCase) -or
    [long]$patchMetadata.TargetLength -ne $msixFile.Length -or
    -not [string]::Equals($patchMetadata.TargetSha256, $msixSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The embedded patch does not reconstruct the target MSIX selected for this release."
}

if (-not [string]::Equals($setupMetadata.FileName, $msixAssetName, [System.StringComparison]::OrdinalIgnoreCase) -or
    [long]$setupMetadata.Length -ne $msixFile.Length -or
    -not [string]::Equals($setupMetadata.Sha256, $msixSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The manual setup does not embed the target MSIX selected for this release."
}

if (-not [string]::Equals($setupMetadata.RuntimeFileName, $runtimeFileName, [System.StringComparison]::OrdinalIgnoreCase) -or
    [long]$setupMetadata.RuntimeLength -ne $runtimeFile.Length -or
    -not [string]::Equals($setupMetadata.RuntimeSha256, $runtimeSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The manual setup does not embed the validated Windows App Runtime MSIX selected for this release."
}

if (-not [string]::Equals($setupMetadata.WinFspFileName, $winFspFileName, [System.StringComparison]::OrdinalIgnoreCase) -or
    [long]$setupMetadata.WinFspLength -ne $winFspFile.Length -or
    -not [string]::Equals($setupMetadata.WinFspSha256, $winFspSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The manual setup does not embed the validated WinFsp MSI selected for this release."
}

if (-not [string]::Equals($setupMetadata.InstallScriptFileName, $installScriptFileName, [System.StringComparison]::OrdinalIgnoreCase) -or
    [long]$setupMetadata.InstallScriptLength -ne $installScriptFile.Length -or
    -not [string]::Equals($setupMetadata.InstallScriptSha256, $installScriptSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The manual setup does not embed the install script from the release source."
}

if (-not [string]::Equals($setupMetadata.CertificateFileName, $certificateFileName, [System.StringComparison]::OrdinalIgnoreCase) -or
    [long]$setupMetadata.CertificateLength -ne $certificateFile.Length -or
    -not [string]::Equals($setupMetadata.CertificateSha256, $certificateSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The manual setup does not embed the validated sideload certificate selected for this release."
}

$basePackageMatch = [regex]::Match(
    [string]$patchMetadata.BaseFileName,
    '^EasyShare_(?<version>\d+\.\d+\.\d+\.\d+)_x64\.msix$',
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
$targetPackageMatch = [regex]::Match(
    [string]$patchMetadata.TargetFileName,
    '^EasyShare_(?<version>\d+\.\d+\.\d+\.\d+)_x64\.msix$',
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
if (-not $basePackageMatch.Success -or -not $targetPackageMatch.Success -or
    -not [string]::Equals(
        $basePackageMatch.Groups['version'].Value.Replace('.', '_'),
        $patchNameMatch.Groups['from'].Value,
        [System.StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals(
        $targetPackageMatch.Groups['version'].Value,
        $Version,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The patch asset name, embedded base/target metadata and release version are inconsistent."
}

Write-Host "Signatures and embedded package metadata validated for all executable release assets."

function Assert-ValidatedAssetsUnchanged {
    foreach ($localAsset in $resolvedAssets) {
        $currentDigest = "sha256:$((Get-FileHash -LiteralPath $localAsset -Algorithm SHA256).Hash.ToLowerInvariant())"
        if (-not [string]::Equals(
                $currentDigest,
                [string]$validatedAssetDigests[$localAsset],
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Release asset changed after validation: $(Split-Path -Leaf $localAsset)"
        }
    }
}

Assert-ValidatedAssetsUnchanged

$assetNotes = ($resolvedAssets | ForEach-Object { "- $(Split-Path -Leaf $_)" }) -join "`n"
$notes = @"
$releaseNotes

### Assets

$assetNotes
"@

if ($ValidateOnly) {
    Write-Host "Release validation completed without changing GitHub."
    exit 0
}

function Get-ReleaseState {
    $stateJson = & gh release view $tag --repo $Repository --json isDraft,assets 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    return ($stateJson | ConvertFrom-Json -ErrorAction Stop)
}

$releaseState = Get-ReleaseState
if ($null -ne $releaseState -and -not $releaseState.isDraft) {
    throw "Release $tag is already public. Refusing to mutate its executable asset set in place."
}

if ($null -eq $releaseState) {
    & gh release create $tag --repo $Repository --title "EasyShare $Version" --notes $notes --target $TargetCommitish --draft
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create draft release $tag."
    }

    $releaseState = Get-ReleaseState
    if ($null -eq $releaseState -or -not $releaseState.isDraft) {
        throw "Draft release $tag could not be verified after creation."
    }
}

$expectedAssetNames = @($assetNames)
$obsoleteAssetNames = @($releaseState.assets | ForEach-Object name | Where-Object {
    $expectedAssetNames -notcontains $_
})
foreach ($obsoleteAssetName in $obsoleteAssetNames) {
    & gh release delete-asset $tag $obsoleteAssetName --repo $Repository --yes
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to remove obsolete release asset: $obsoleteAssetName"
    }
}

Assert-ValidatedAssetsUnchanged
& gh release upload $tag $resolvedAssets --repo $Repository --clobber
if ($LASTEXITCODE -ne 0) {
    throw "Failed to upload assets for $tag."
}

$verifiedReleaseState = Get-ReleaseState
if ($null -eq $verifiedReleaseState -or -not $verifiedReleaseState.isDraft) {
    throw "Draft release $tag could not be verified after asset upload."
}

$remoteAssetNames = @($verifiedReleaseState.assets | ForEach-Object name)
$missingAssetNames = @($expectedAssetNames | Where-Object { $remoteAssetNames -notcontains $_ })
$unexpectedAssetNames = @($remoteAssetNames | Where-Object { $expectedAssetNames -notcontains $_ })
if ($missingAssetNames.Count -gt 0 -or $unexpectedAssetNames.Count -gt 0 -or
    $remoteAssetNames.Count -ne $expectedAssetNames.Count) {
    throw "Draft asset verification failed. Missing: $($missingAssetNames -join ', '); unexpected: $($unexpectedAssetNames -join ', ')."
}

foreach ($localAsset in $resolvedAssets) {
    $localAssetName = Split-Path -Leaf $localAsset
    $remoteAsset = @($verifiedReleaseState.assets | Where-Object {
        [string]::Equals($_.name, $localAssetName, [System.StringComparison]::OrdinalIgnoreCase)
    })
    $expectedDigest = [string]$validatedAssetDigests[$localAsset]
    $currentLocalDigest = "sha256:$((Get-FileHash -LiteralPath $localAsset -Algorithm SHA256).Hash.ToLowerInvariant())"
    if ($remoteAsset.Count -ne 1 -or
        -not [string]::Equals(
            [string]$remoteAsset[0].digest,
            $expectedDigest,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $currentLocalDigest,
            $expectedDigest,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "GitHub digest verification failed for release asset: $localAssetName"
    }
}

& gh release edit $tag --repo $Repository --title "EasyShare $Version" --notes $notes --target $TargetCommitish --draft=false --latest
if ($LASTEXITCODE -ne 0) {
    throw "Failed to publish $tag."
}

Write-Host "Published verified release $tag in $Repository."
