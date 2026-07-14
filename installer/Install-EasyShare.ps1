param(
    [switch]$NoLaunch,
    [switch]$MachinePrerequisitesOnly,
    [switch]$SkipWinFspInstall,
    [string]$PackagePath,
    [string]$LogPath
)

$ErrorActionPreference = "Stop"

$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$isIncrementalPatchInstall = -not [string]::IsNullOrWhiteSpace($PackagePath)
$defaultPackageName = "EasyShare_1.0.26.0_x64.msix"
$package = if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    Join-Path $root $defaultPackageName
} else {
    [System.IO.Path]::GetFullPath($PackagePath)
}
$packageName = [System.IO.Path]::GetFileName($package)
$packageCacheRoot = Join-Path $env:LOCALAPPDATA "EasyShare\Packages"
$certificate = Join-Path $root "EasyShare_TestCertificate.cer"
$dependency = Join-Path $root "Dependencies\x64\Microsoft.WindowsAppRuntime.2.msix"
if (-not (Test-Path -LiteralPath $dependency)) {
    $dependency = Join-Path $root "Microsoft.WindowsAppRuntime.2.msix"
}
$aumid = "ArchGTi.Tech.EasyPointShare_qjy908w4vdt2j!App"
$expectedWindowsAppRuntimePublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
$expectedWinFspSignerThumbprint = "ECC9BCB47D6506452753F3DF19677B35AEB36E2B"

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

function Test-WindowsAppRuntimePackage {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate) {
        $status = $signature.Status
        $message = if ($signature.StatusMessage) { $signature.StatusMessage } else { "assinatura ausente ou invalida" }
        throw "O pacote incorporado do Windows App Runtime esta corrompido ou nao possui uma assinatura valida. Baixe novamente o instalador do EasyShare. Status: $status. Detalhes: $message"
    }

    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
        $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
        try {
            $requiredEntries = @("AppxManifest.xml", "AppxBlockMap.xml", "AppxSignature.p7x")
            foreach ($requiredEntry in $requiredEntries) {
                $entry = $archive.Entries | Where-Object {
                    [string]::Equals($_.FullName, $requiredEntry, [System.StringComparison]::OrdinalIgnoreCase)
                } | Select-Object -First 1
                if ($null -eq $entry) {
                    throw "entrada obrigatoria ausente: $requiredEntry"
                }
            }

            $manifestEntry = $archive.Entries | Where-Object {
                [string]::Equals($_.FullName, "AppxManifest.xml", [System.StringComparison]::OrdinalIgnoreCase)
            } | Select-Object -First 1
            $manifestStream = $manifestEntry.Open()
            try {
                $settings = [System.Xml.XmlReaderSettings]::new()
                $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
                $settings.XmlResolver = $null
                $reader = [System.Xml.XmlReader]::Create($manifestStream, $settings)
                try {
                    $manifest = [xml]::new()
                    $manifest.Load($reader)
                }
                finally {
                    $reader.Dispose()
                }
            }
            finally {
                $manifestStream.Dispose()
            }

            $identity = $manifest.Package.Identity
            $isFramework = [string]$manifest.Package.Properties.Framework
            $runtimeVersion = [version][string]$identity.Version
            if (-not [string]::Equals([string]$identity.Name, "Microsoft.WindowsAppRuntime.2", [System.StringComparison]::Ordinal) -or
                -not [string]::Equals([string]$identity.ProcessorArchitecture, "x64", [System.StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals($isFramework, "true", [System.StringComparison]::OrdinalIgnoreCase) -or
                $runtimeVersion -lt [version]"2.2.0.0") {
                throw "identidade, versao, arquitetura ou tipo de framework inesperado"
            }

            if (-not [string]::Equals(
                    [string]$signature.SignerCertificate.Subject,
                    [string]$identity.Publisher,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "o assinante nao corresponde ao publisher do manifesto"
            }

            if (-not [string]::Equals(
                    [string]$identity.Publisher,
                    $expectedWindowsAppRuntimePublisher,
                    [System.StringComparison]::Ordinal)) {
                throw "o publisher do runtime nao corresponde ao framework exigido pelo EasyShare"
            }
        }
        finally {
            if ($null -ne $archive) {
                $archive.Dispose()
            }
        }
    }
    catch {
        throw "O pacote incorporado do Windows App Runtime nao e um MSIX valido. Baixe novamente o instalador do EasyShare. Detalhes: $($_.Exception.Message)"
    }

    Write-Host "Pacote do Windows App Runtime validado."
    return $true
}

function Cache-EasySharePackage {
    $destination = Join-Path $packageCacheRoot $packageName
    $temporary = Join-Path $packageCacheRoot (".{0}.{1}.tmp" -f $packageName, [guid]::NewGuid().ToString("N"))

    try {
        New-Item -ItemType Directory -Path $packageCacheRoot -Force | Out-Null
        Copy-Item -LiteralPath $package -Destination $temporary -Force

        $sourceHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
        $temporaryHash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
        if (-not [string]::Equals($sourceHash, $temporaryHash, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "A copia temporaria do pacote nao corresponde ao MSIX instalado."
        }

        Move-Item -LiteralPath $temporary -Destination $destination -Force
        $cachedHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if (-not [string]::Equals($sourceHash, $cachedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
            throw "O pacote persistido no cache falhou na verificacao SHA-256."
        }

        Write-Host "Pacote assinado armazenado para atualizacoes incrementais: $packageName"
    }
    catch {
        throw "Nao foi possivel armazenar a base obrigatoria para futuras atualizacoes incrementais: $($_.Exception.Message)"
    }
    finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
}

function Remove-LegacyEasySharePackage {
    $legacyPackages = Get-AppxPackage -Name "AAD584E5-8AD2-4CE5-9C65-B1C66E02383A" -ErrorAction SilentlyContinue
    foreach ($legacyPackage in $legacyPackages) {
        try {
            Remove-AppxPackage -Package $legacyPackage.PackageFullName -ErrorAction Stop
            Write-Host "Instalacao anterior do EasyShare removida apos a migracao."
        }
        catch {
            Write-Warning "A nova versao foi instalada, mas a instalacao anterior nao pode ser removida agora: $($_.Exception.Message)"
        }
    }
}

function Resolve-WinFspInstaller {
    $candidates = @(
        (Join-Path $root "winfsp-2.1.25156.msi"),
        (Join-Path $root "Dependencies\winfsp-2.1.25156.msi"),
        (Join-Path (Get-Location) "winfsp-2.1.25156.msi")
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    return $null
}

function Test-WinFspInstalled {
    return (
        (Test-Path "C:\Program Files (x86)\WinFsp\bin\winfsp-x64.dll") -or
        (Test-Path "C:\Program Files\WinFsp\bin\winfsp-x64.dll")
    )
}

function Test-WinFspInstaller {
    param([Parameter(Mandatory)][string]$Path)

    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate) {
        $status = $signature.Status
        $message = if ($signature.StatusMessage) { $signature.StatusMessage } else { "assinatura ausente ou invalida" }
        throw "O instalador incorporado do WinFsp esta corrompido ou nao possui assinatura valida. Baixe novamente o instalador do EasyShare. Status: $status. Detalhes: $message"
    }

    $actualSignerThumbprint = ($signature.SignerCertificate.Thumbprint -replace '\s', '').ToUpperInvariant()
    if (-not [string]::Equals(
            $actualSignerThumbprint,
            $expectedWinFspSignerThumbprint,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "O instalador incorporado do WinFsp nao foi assinado pelo fornecedor aprovado."
    }

    Write-Host "Assinatura do instalador WinFsp validada."
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

    Test-WinFspInstaller -Path $winFspInstaller
    Write-Host "Instalando WinFsp..."
    $systemDirectory = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::System)
    $msiexecPath = Join-Path $systemDirectory "msiexec.exe"
    if (-not (Test-Path -LiteralPath $msiexecPath -PathType Leaf)) {
        throw "O Windows Installer nao foi encontrado no System32."
    }

    $process = Start-Process -FilePath $msiexecPath -ArgumentList "/i `"$winFspInstaller`" /qn /norestart" -Wait -PassThru
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
    throw "Os pre-requisitos de maquina ainda nao estao prontos. Execute o EasyPointShareSetup.exe como administrador."
}

Import-EasyShareCertificate
Test-EasySharePackageSignature

$dependencyPaths = @()
if (Test-Path -LiteralPath $dependency) {
    if (Test-WindowsAppRuntimePackage -Path $dependency) {
        $dependencyPaths += $dependency
    }
}
elseif (-not $isIncrementalPatchInstall) {
    throw "O pacote do Windows App Runtime nao foi encontrado no instalador. Baixe novamente o EasyPointShareSetup.exe."
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

Cache-EasySharePackage
Remove-LegacyEasySharePackage

if (-not (Test-WinFspInstalled)) {
    Write-Warning "WinFsp nao foi encontrado. Instale o WinFsp antes de ativar a unidade do EasyShare."
}

if (-not $NoLaunch) {
    $windowsDirectory = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Windows)
    $explorerPath = Join-Path $windowsDirectory "explorer.exe"
    Start-Process -FilePath $explorerPath -ArgumentList "shell:AppsFolder\$aumid"
}

Write-Host "EasyShare instalado."

try {
    Stop-Transcript | Out-Null
}
catch {
}
