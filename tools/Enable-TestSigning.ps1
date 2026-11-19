<#
.SYNOPSIS
    Enables Windows test signing so test-signed driver packages load.

.DESCRIPTION
    Runs `bcdedit /set testsigning on`. Requires admin rights and a reboot to
    take effect. A watermark appears on the desktop while test signing is on.

.EXAMPLE
    .\Enable-TestSigning.ps1
#>
[CmdletBinding()]
param(
    [switch]$Off
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Administrator rights are required. Run from an elevated prompt."
}

if ($Off) {
    bcdedit /set testsigning off
    Write-Host "Test signing disabled. Reboot to apply." -ForegroundColor Yellow
    return
}

bcdedit /set testsigning on
if ($LASTEXITCODE -ne 0) {
    throw "bcdedit failed with exit code $LASTEXITCODE (is this an elevated, UEFI/BIOS-compatible environment?)."
}
Write-Host "Test signing ENABLED. Reboot to apply." -ForegroundColor Green
Write-Host "Remember to import your test certificate:" -ForegroundColor Cyan
Write-Host "    Import-Certificate -FilePath .\test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher"
