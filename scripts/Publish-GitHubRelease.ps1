param(
    [string]$Repository = "cainhooow/EasyShare",
    [string]$Version = "",
    [string]$ExePath = "dist/EasyShareSetup.exe",
    [string]$MsiPath = "dist/EasyShareSetup.msi"
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
        Write-Warning "$(Split-Path -Leaf $asset) is not signed by a trusted certificate. Smart App Control may block it."
    }
}

$notes = @"
EasyShare $Version

Assets:
$($resolvedAssets | ForEach-Object { "- $(Split-Path -Leaf $_)" } | Out-String)
"@

gh release view $tag --repo $Repository | Out-Null
if ($LASTEXITCODE -eq 0) {
    gh release upload $tag $resolvedAssets --repo $Repository --clobber
    Write-Host "Updated release $tag in $Repository."
    exit 0
}

gh release create $tag $resolvedAssets --repo $Repository --title "EasyShare $Version" --notes $notes
Write-Host "Created release $tag in $Repository."
