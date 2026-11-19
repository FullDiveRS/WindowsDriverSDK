# Build, test and pack the Windows Driver SDK.
# Usage: .\build.ps1 [-Configuration Release]
[CmdletBinding()]
param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "== restore ==" -ForegroundColor Cyan
dotnet restore

Write-Host "== build ($Configuration) ==" -ForegroundColor Cyan
dotnet build --no-restore -c $Configuration

Write-Host "== test ==" -ForegroundColor Cyan
dotnet test --no-build -c $Configuration

Write-Host "== pack ==" -ForegroundColor Cyan
dotnet pack --no-build -c $Configuration -o artifacts/

Write-Host "== infgen smoke test ==" -ForegroundColor Cyan
dotnet tool install --prerelease --tool-path ./tools-out --add-source ./artifacts WindowsDriver.InfGen 2>$null
./tools-out/infgen generate --manifest samples/VirtualAudio/manifest.json --out $env:TEMP\virtualaudio.inf
./tools-out/infgen validate --inf $env:TEMP\virtualaudio.inf

Write-Host "Done. Packages are in ./artifacts/" -ForegroundColor Green
