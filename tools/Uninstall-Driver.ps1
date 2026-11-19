<#
.SYNOPSIS
    Uninstalls a previously installed driver package and removes its devices.

.PARAMETER InfName
    The published INF name (oemXX.inf) or the original file name. When omitted,
    the script lists matching packages instead of removing anything.

.PARAMETER HardwareId
    Optional hardware ID to find the devices that use the driver.

.EXAMPLE
    .\Uninstall-Driver.ps1                      # list installed SDK driver packages
    .\Uninstall-Driver.ps1 -InfName VirtualAudio.inf
#>
[CmdletBinding()]
param(
    [string]$InfName,
    [string]$HardwareId
)

$ErrorActionPreference = 'Stop'

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Administrator rights are required to uninstall drivers. Run from an elevated prompt."
    }
}

Assert-Admin

# Map the original INF name to its published oemXX.inf name.
$published = pnputil /enum-drivers | Out-String
$blocks = $published -split "`r?`n`r?`n" | Where-Object { $_ -match 'Original Name' }

$target = $null
foreach ($block in $blocks) {
    $original = [regex]::Match($block, 'Original Name:\s*(.+)$').Groups[1].Value.Trim()
    if ($original -eq $InfName -or ($InfName -and $original -ieq $InfName)) {
        $target = [regex]::Match($block, 'Published Name:\s*(.+)$').Groups[1].Value.Trim()
        break
    }
    if ($HardwareId -and $block -match [regex]::Escape($HardwareId)) {
        $target = [regex]::Match($block, 'Published Name:\s*(.+)$').Groups[1].Value.Trim()
        break
    }
}

if (-not $target) {
    Write-Host "Installed driver packages (Original Name / Published Name):" -ForegroundColor Cyan
    foreach ($block in $blocks) {
        $o = [regex]::Match($block, 'Original Name:\s*(.+)$').Groups[1].Value.Trim()
        $p = [regex]::Match($block, 'Published Name:\s*(.+)$').Groups[1].Value.Trim()
        "$o  →  $p"
    }
    if ($InfName) {
        Write-Warning "No package matched '$InfName'."
    }
    return
}

Write-Host "Removing devices that use $target..." -ForegroundColor Cyan
if ($HardwareId) {
    Get-PnpDevice | Where-Object { $_.InstanceId -like "*$HardwareId*" } | ForEach-Object {
        Write-Host "  removing device $($_.InstanceId)"
        pnputil /remove-device $_.InstanceId | Out-Null
    }
}

Write-Host "Deleting driver package $target ..." -ForegroundColor Cyan
pnputil /delete-driver $target /uninstall /force
if ($LASTEXITCODE -ne 0) {
    throw "pnputil failed with exit code $LASTEXITCODE."
}
Write-Host "Uninstalled." -ForegroundColor Green
