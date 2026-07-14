[CmdletBinding()]
param(
    [ValidateSet("All", "Audit", "Gates", "Test", "Package")]
    [string]$Target = "All",

    [ValidateSet("x86", "ARM64")]
    [string]$Architecture
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$solutionPath = Join-Path $repositoryRoot "EasyShare.slnx"
$applicationProject = Join-Path $repositoryRoot "src\EasyShare\EasyShare.csproj"
$testProject = Join-Path $repositoryRoot "tests\EasyShare.Tests\EasyShare.Tests.csproj"

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    Write-Host "dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

function ConvertTo-MSBuildValue {
    param([Parameter(Mandatory)][string]$Value)

    return $Value.Replace("%", "%25").Replace(";", "%3B")
}

function Invoke-NuGetAudit {
    $arguments = @(
        "restore",
        $solutionPath,
        "-p:Platform=x64",
        "-p:RuntimeIdentifier=win-x64",
        "-p:NuGetAudit=true",
        "-p:NuGetAuditMode=all",
        "-p:WarningsAsErrors=NU1901%3BNU1902%3BNU1903%3BNU1904",
        "--verbosity",
        "minimal"
    )

    Write-Host "dotnet $($arguments -join ' ')"
    $output = & dotnet @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "NuGet restore/audit exited with code $exitCode."
    }

    if (($output | Out-String) -match "NU190[1-4]") {
        throw "NuGet reported a vulnerable direct or transitive dependency."
    }
}

function Invoke-RepositoryGates {
    $filter = @(
        "FullyQualifiedName~StorePackageManifestTests",
        "FullyQualifiedName~LocalizationResourceTests",
        "FullyQualifiedName~UpdateIntegrityTests",
        "FullyQualifiedName~UpdateDownloadClientTests",
        "FullyQualifiedName~UpdateInstallerStagerTests",
        "FullyQualifiedName~UpdatePublisherTrustTests",
        "FullyQualifiedName~UpdateUriPolicyTests",
        "FullyQualifiedName~WebViewOriginPolicyTests",
        "FullyQualifiedName~SharePointRouteParserTests",
        "FullyQualifiedName~SharePointContentTransportTests",
        "FullyQualifiedName~UploadPayloadStorageTests",
        "FullyQualifiedName~DiagnosticsAndSupportBundleTests",
        "FullyQualifiedName~EnterprisePolicyLoaderTests",
        "FullyQualifiedName~SetupWizardAdvisorTests",
        "FullyQualifiedName~CiWorkflowContractTests"
    ) -join "|"

    Invoke-DotNet -Arguments @(
        "test",
        $testProject,
        "--configuration",
        "Release",
        "-p:Platform=x64",
        "-p:RuntimeIdentifier=win-x64",
        "--filter",
        $filter,
        "--verbosity",
        "minimal"
    )
}

function Invoke-X64Tests {
    Invoke-DotNet -Arguments @(
        "test",
        $testProject,
        "--configuration",
        "Release",
        "-p:Platform=x64",
        "-p:RuntimeIdentifier=win-x64",
        "--verbosity",
        "minimal"
    )
}

function Invoke-ArchitecturePackage {
    param([Parameter(Mandatory)][ValidateSet("x86", "ARM64")][string]$BuildArchitecture)

    $runtimeIdentifier = if ($BuildArchitecture -eq "ARM64") { "win-arm64" } else { "win-x86" }
    $artifactDirectory = Join-Path $repositoryRoot "dist-test\ci\$BuildArchitecture"
    [IO.Directory]::CreateDirectory($artifactDirectory) | Out-Null

    $arguments = @(
        "build",
        $applicationProject,
        "--configuration",
        "Release",
        "-p:Platform=$BuildArchitecture",
        "-p:RuntimeIdentifier=$runtimeIdentifier",
        "-p:AppxPackageSigningEnabled=false",
        "-p:GenerateAppxPackageOnBuild=true",
        "-p:AppxBundle=Never",
        "-p:UapAppxPackageBuildMode=SideloadOnly",
        "-p:AppxSymbolPackageEnabled=false",
        "-p:AppxPackageIncludePrivateSymbols=false",
        "-p:BuildAppxUploadPackageForUap=false",
        "-p:AppxPackageDir=$artifactDirectory\",
        "--verbosity",
        "minimal"
    )

    if (-not [string]::IsNullOrWhiteSpace($env:EASYSHARE_UPDATE_PUBLISHER_SUBJECTS)) {
        $subjects = ConvertTo-MSBuildValue $env:EASYSHARE_UPDATE_PUBLISHER_SUBJECTS
        $arguments += "-p:EasyShareUpdatePublisherSubjects=$subjects"
    }

    if (-not [string]::IsNullOrWhiteSpace($env:EASYSHARE_UPDATE_PUBLISHER_THUMBPRINTS)) {
        $thumbprints = ConvertTo-MSBuildValue $env:EASYSHARE_UPDATE_PUBLISHER_THUMBPRINTS
        $arguments += "-p:EasyShareUpdatePublisherThumbprints=$thumbprints"
    }

    Invoke-DotNet -Arguments $arguments
}

Push-Location $repositoryRoot
try {
    switch ($Target) {
        "Audit" { Invoke-NuGetAudit }
        "Gates" { Invoke-RepositoryGates }
        "Test" { Invoke-X64Tests }
        "Package" {
            if ([string]::IsNullOrWhiteSpace($Architecture)) {
                throw "-Architecture x86 or ARM64 is required when -Target Package is used."
            }

            Invoke-ArchitecturePackage $Architecture
        }
        "All" {
            Invoke-NuGetAudit
            Invoke-RepositoryGates
            Invoke-X64Tests
            Invoke-ArchitecturePackage "x86"
            Invoke-ArchitecturePackage "ARM64"
        }
    }
}
finally {
    Pop-Location
}
