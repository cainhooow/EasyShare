param(
    [string]$MsixPath,
    [string]$ExePath,
    [string]$PatchExePath,
    [string]$MsiPath,
    [string]$CertificateThumbprint = $env:EASYSHARE_SIGNING_CERT_THUMBPRINT,
    [string]$SignToolPath = "",
    [string]$TimestampUrl = $env:EASYSHARE_SIGNING_TIMESTAMP_URL,
    [switch]$RequireTrustedSignature
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($SignToolPath)) {
    $signToolFromPath = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
    $signToolCandidates = @(
        $signToolFromPath,
        (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe"),
        (Get-ChildItem -Path (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin") -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName)
    )
    $SignToolPath = $signToolCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if (-not $SignToolPath -or -not (Test-Path -LiteralPath $SignToolPath)) {
    throw "signtool.exe nao foi encontrado. Informe -SignToolPath ou instale o Windows SDK."
}

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw "Informe -CertificateThumbprint ou defina EASYSHARE_SIGNING_CERT_THUMBPRINT."
}

$normalizedThumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
$certificate = Get-ChildItem Cert:\CurrentUser\My,Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $normalizedThumbprint -and $_.HasPrivateKey } |
    Select-Object -First 1

if (-not $certificate) {
    throw "O certificado de assinatura $normalizedThumbprint nao foi encontrado com chave privada."
}

$assets = @(
    @{ Label = "MSIX"; Path = $MsixPath },
    @{ Label = "EXE"; Path = $ExePath },
    @{ Label = "Patch EXE"; Path = $PatchExePath },
    @{ Label = "MSI"; Path = $MsiPath }
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Path) }

if ($assets.Count -eq 0) {
    throw "Informe pelo menos um de -MsixPath, -ExePath ou -MsiPath."
}

foreach ($asset in $assets) {
    $resolvedPath = if ([System.IO.Path]::IsPathRooted($asset.Path)) {
        [System.IO.Path]::GetFullPath($asset.Path)
    }
    else {
        Join-Path $root $asset.Path
    }
    if (-not (Test-Path -LiteralPath $resolvedPath)) {
        throw "$($asset.Label) nao encontrado: $($asset.Path)"
    }

    Write-Host "Assinando $($asset.Label): $resolvedPath"
    $signArguments = @("sign", "/fd", "SHA256", "/sha1", $normalizedThumbprint, "/d", "EasyShare")
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $signArguments += @("/tr", $TimestampUrl, "/td", "SHA256")
    }
    $signArguments += $resolvedPath
    & $SignToolPath @signArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao assinar $($asset.Label)."
    }

    $signature = Get-AuthenticodeSignature -FilePath $resolvedPath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "A assinatura do $($asset.Label) nao foi validada: $($signature.StatusMessage)"
    }

    & $SignToolPath verify /pa /all $resolvedPath
    if ($LASTEXITCODE -ne 0 -and $RequireTrustedSignature) {
        throw "A cadeia de confianca do $($asset.Label) nao foi validada pelo signtool /pa."
    }
}

$timestampMessage = if ([string]::IsNullOrWhiteSpace($TimestampUrl)) { "sem timestamp" } else { "timestamp $TimestampUrl" }
Write-Host "Artefatos assinados com $($certificate.Subject) ($normalizedThumbprint; $timestampMessage)."
