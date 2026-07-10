param(
    [Parameter(Mandatory = $true)]
    [string]$BaseMsixPath,
    [Parameter(Mandatory = $true)]
    [string]$TargetMsixPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$baseCandidate = if ([System.IO.Path]::IsPathRooted($BaseMsixPath)) { $BaseMsixPath } else { Join-Path $root $BaseMsixPath }
$targetCandidate = if ([System.IO.Path]::IsPathRooted($TargetMsixPath)) { $TargetMsixPath } else { Join-Path $root $TargetMsixPath }
$base = (Resolve-Path $baseCandidate).Path
$target = (Resolve-Path $targetCandidate).Path
$output = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
} else {
    Join-Path $root $OutputPath
}

dotnet run --project (Join-Path $root "tools/EasySharePatchTool/EasySharePatchTool.csproj") --configuration Release --no-restore -- build --base $base --target $target --output $output
if ($LASTEXITCODE -ne 0) {
    throw "Falha ao gerar o patch incremental."
}

Write-Host "Patch incremental gerado: $output"
