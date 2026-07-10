param(
    [string]$Repository = "cainhooow/EasyShare",
    [string]$Version = "",
    [string]$ExePath = "dist/EasyShareSetup.exe",
    [string]$MsiPath = "dist/EasyShareSetup.msi",
    [string]$ChangelogPath = "CHANGELOG.md",
    [switch]$RequireTrustedSignature
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

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

$resolvedAssets = @(
    Join-Path $root $ExePath
    Join-Path $root $MsiPath
) | Where-Object { Test-Path $_ }

if ($resolvedAssets.Count -eq 0) {
    throw "No release assets were found. Build the installers before publishing the release."
}

foreach ($asset in $resolvedAssets) {
    $signature = Get-AuthenticodeSignature $asset
    if ($signature.Status -ne "Valid") {
        $message = "$(Split-Path -Leaf $asset) is not signed by a trusted certificate. Smart App Control may block it."
        if ($RequireTrustedSignature) {
            throw $message
        }

        Write-Warning $message
    }
}

if ($RequireTrustedSignature) {
    Write-Host "Trusted Authenticode signatures validated for all release assets."
}

$assetNotes = ($resolvedAssets | ForEach-Object { "- $(Split-Path -Leaf $_)" }) -join "`n"
$notes = @"
$releaseNotes

### Assets

$assetNotes
"@

gh release view $tag --repo $Repository | Out-Null
if ($LASTEXITCODE -eq 0) {
    gh release upload $tag $resolvedAssets --repo $Repository --clobber
    gh release edit $tag --repo $Repository --title "EasyShare $Version" --notes $notes
    Write-Host "Updated release $tag in $Repository."
    exit 0
}

gh release create $tag $resolvedAssets --repo $Repository --title "EasyShare $Version" --notes $notes
Write-Host "Created release $tag in $Repository."
