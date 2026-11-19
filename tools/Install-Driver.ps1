<#
.SYNOPSIS
    Installs a driver package (INF + binaries) with pnputil and starts the device.

.DESCRIPTION
    Wraps `pnputil /add-driver /install` with prechecks: admin rights, INF
    presence, and (optionally) test-signing detection. Works for UMDF and KMDF
    driver packages produced by the Windows Driver SDK.

.PARAMETER InfPath
    Path to the .inf file to install.

.PARAMETER CertPath
    Optional path to a .cer certificate to add to the machine TrustedPublishers
    store first (useful for test-signed packages).

.EXAMPLE
    .\Install-Driver.ps1 -InfPath .\VirtualAudio.inf

.EXAMPLE
    .\Install-Driver.ps1 -InfPath .\WdsKmdfShim.inf -CertPath .\test.cer
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InfPath,

    [string]$CertPath
)

$ErrorActionPreference = 'Stop'

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Administrator rights are required to install drivers. Run from an elevated prompt."
    }
}

Assert-Admin

$InfPath = (Resolve-Path $InfPath).Path
if (-not (Test-Path $InfPath)) {
    throw "INF not found: $InfPath"
}

if ($CertPath) {
    $CertPath = (Resolve-Path $CertPath).Path
    Write-Host "Importing certificate $CertPath into TrustedPublishers..." -ForegroundColor Cyan
    Import-Certificate -FilePath $CertPath -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
}

# Detect test-signed packages and warn about the requirement.
$testSigning = (bcdedit /enum '{current}' | Select-String -Pattern 'testsigning\s+Yes') -ne $null
if (-not $testSigning) {
    Write-Warning "Test signing is not enabled. If this package is test-signed, enable it first:" 
    Write-Warning "    .\Enable-TestSigning.ps1   (then reboot)"
}

Write-Host "Installing $InfPath ..." -ForegroundColor Cyan
pnputil /add-driver $InfPath /install
if ($LASTEXITCODE -ne 0) {
    throw "pnputil failed with exit code $LASTEXITCODE."
}

$infName = Split-Path $InfPath -Leaf
Write-Host ""
Write-Host "Installed. Device status:" -ForegroundColor Green
Get-PnpDevice -Class '*' | Where-Object { $_.InstanceId -like "*$infName*" -or $_.FriendlyName -like "*$infName*" } |
    Format-Table Status, Class, FriendlyName, InstanceId -AutoSize
Write-Host "To remove it again: .\Uninstall-Driver.ps1 -InfName $infName"
