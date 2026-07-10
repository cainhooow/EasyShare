param(
    [switch]$NoLaunch,
    [switch]$MachinePrerequisitesOnly,
    [switch]$SkipWinFspInstall,
    [string]$LogPath
)

$ErrorActionPreference = "Stop"

$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$cacheRoot = Join-Path $env:ProgramData "EasyShare\InstallerCache"
$packageName = "EasyShare_1.0.0.21_x64.msix"
$package = Join-Path $root $packageName
$certificate = Join-Path $root "EasyShare_TestCertificate.cer"
$dependency = Join-Path $root "Dependencies\x64\Microsoft.WindowsAppRuntime.2.msix"
if (-not (Test-Path -LiteralPath $dependency)) {
    $dependency = Join-Path $root "Microsoft.WindowsAppRuntime.2.msix"
}
$aumid = "AAD584E5-8AD2-4CE5-9C65-B1C66E02383A_1z32rh13vfry6!App"

if (-not $MachinePrerequisitesOnly -and -not (Test-Path -LiteralPath $package)) {
    throw "Pacote EasyShare nao encontrado: $package"
}

if (-not (Test-Path -LiteralPath $certificate)) {
    throw "Certificado EasyShare nao encontrado: $certificate"
}

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $logRoot = Join-Path $env:TEMP "EasyShareInstallerLogs"
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    $LogPath = Join-Path $logRoot ("install-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
}

try {
    $logParent = Split-Path -Parent $LogPath
    if ($logParent) {
        New-Item -ItemType Directory -Path $logParent -Force | Out-Null
    }

    Start-Transcript -Path $LogPath -Append | Out-Null
}
catch {
    Write-Warning "Nao foi possivel iniciar o log detalhado: $($_.Exception.Message)"
}

Write-Host "Log do instalador: $LogPath"
Write-Host "Pasta do instalador: $root"

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Import-EasyShareCertificate {
    param([switch]$Machine)

    if ($Machine) {
        Import-Certificate -FilePath $certificate -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
        Import-Certificate -FilePath $certificate -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
        return
    }

    Import-Certificate -FilePath $certificate -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
    Import-Certificate -FilePath $certificate -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
}

function Test-MachineCertificateTrusted {
    try {
        $fileCert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate)
        $thumbprint = $fileCert.Thumbprint
        $trustedRoot = Test-Path -LiteralPath "Cert:\LocalMachine\Root\$thumbprint"
        $trustedPeople = Test-Path -LiteralPath "Cert:\LocalMachine\TrustedPeople\$thumbprint"
        return ($trustedRoot -and $trustedPeople)
    }
    catch {
        return $false
    }
}

function Test-EasySharePackageSignature {
    $signature = Get-AuthenticodeSignature -FilePath $package
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        $status = $signature.Status
        $message = if ($signature.StatusMessage) { $signature.StatusMessage } else { "assinatura ausente ou invalida" }
        throw "O pacote MSIX do EasyShare nao esta assinado corretamente. Status: $status. Detalhes: $message"
    }

    $signer = $signature.SignerCertificate.Subject
    Write-Host "Assinatura do pacote MSIX validada: $signer"
}

function Resolve-WinFspInstaller {
    $candidates = @(
        (Join-Path $root "winfsp-2.1.25156.msi"),
        (Join-Path $root "Dependencies\winfsp-2.1.25156.msi"),
        (Join-Path $cacheRoot "winfsp-2.1.25156.msi"),
        (Join-Path (Get-Location) "winfsp-2.1.25156.msi")
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    return $null
}

function Copy-MachinePrerequisitesToCache {
    New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null

    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $cacheRoot "Install-EasyShare.ps1") -Force
    Copy-Item -LiteralPath $certificate -Destination (Join-Path $cacheRoot "EasyShare_TestCertificate.cer") -Force

    $winFspInstaller = Resolve-WinFspInstaller
    if ($winFspInstaller) {
        Copy-Item -LiteralPath $winFspInstaller -Destination (Join-Path $cacheRoot "winfsp-2.1.25156.msi") -Force
    }
}

function Test-WinFspInstalled {
    return (
        (Test-Path "C:\Program Files (x86)\WinFsp\bin\winfsp-x64.dll") -or
        (Test-Path "C:\Program Files\WinFsp\bin\winfsp-x64.dll")
    )
}

function Install-WinFsp {
    if ($SkipWinFspInstall) {
        Write-Host "Instalacao do WinFsp ignorada por parametro."
        return
    }

    if (Test-WinFspInstalled) {
        Write-Host "WinFsp ja esta instalado."
        return
    }

    $winFspInstaller = Resolve-WinFspInstaller
    if (-not $winFspInstaller -or -not (Test-Path -LiteralPath $winFspInstaller)) {
        throw "Instalador do WinFsp nao foi encontrado perto do instalador do EasyShare."
    }

    Write-Host "Instalando WinFsp..."
    $process = Start-Process msiexec.exe -ArgumentList "/i `"$winFspInstaller`" /qn /norestart" -Wait -PassThru
    if ($process.ExitCode -ne 0 -and $process.ExitCode -ne 3010) {
        throw "Falha ao instalar WinFsp. Codigo: $($process.ExitCode)"
    }
}

function Test-MachinePrerequisitesReady {
    $certificateReady = Test-MachineCertificateTrusted
    $winFspReady = $SkipWinFspInstall -or (Test-WinFspInstalled)
    return ($certificateReady -and $winFspReady)
}

if ($MachinePrerequisitesOnly) {
    if (-not (Test-Admin)) {
        throw "A preparacao dos pre-requisitos precisa ser executada como administrador."
    }

    Write-Host "Instalando pre-requisitos do EasyShare no Windows..."
    Import-EasyShareCertificate -Machine
    Install-WinFsp
    exit 0
}

Write-Host "Preparando confianca do certificado EasyShare..."
if (Test-Admin) {
    Import-EasyShareCertificate -Machine
    Install-WinFsp
}
elseif (Test-MachinePrerequisitesReady) {
    Write-Host "Pre-requisitos de maquina ja estao prontos."
}
else {
    Copy-MachinePrerequisitesToCache
    $cachedScript = Join-Path $cacheRoot "Install-EasyShare.ps1"
    $machineLogPath = Join-Path (Split-Path -Parent $LogPath) ("machine-prerequisites-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$cachedScript`" -MachinePrerequisitesOnly -LogPath `"$machineLogPath`""
    if ($SkipWinFspInstall) {
        $arguments += " -SkipWinFspInstall"
    }

    $elevated = Start-Process powershell.exe -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    if ($elevated.ExitCode -ne 0) {
        throw "Nao foi possivel preparar os pre-requisitos do EasyShare no Windows."
    }
}

Import-EasyShareCertificate
Test-EasySharePackageSignature

$dependencyPaths = @()
if (Test-Path -LiteralPath $dependency) {
    $dependencyPaths += $dependency
}

Write-Host "Instalando EasyShare..."
$appxArgs = @{
    Path = $package
    ForceApplicationShutdown = $true
    ForceUpdateFromAnyVersion = $true
}

if ($dependencyPaths.Count -gt 0) {
    $appxArgs.DependencyPath = $dependencyPaths
}

try {
    Add-AppxPackage @appxArgs -ErrorAction Stop
}
catch {
    $message = $_.Exception.Message
    if ($message -notmatch "0x80073CFB|already installed|ja esta instalado|já está instalado") {
        throw
    }
}

if (-not (Test-WinFspInstalled)) {
    Write-Warning "WinFsp nao foi encontrado. Instale o WinFsp antes de ativar a unidade do EasyShare."
}

if (-not $NoLaunch) {
    Start-Process explorer.exe "shell:AppsFolder\$aumid"
}

Write-Host "EasyShare instalado."

try {
    Stop-Transcript | Out-Null
}
catch {
}
