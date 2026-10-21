<#
.SYNOPSIS
  Registers (or removes) the development sparse package so DynamicIsland.exe runs with package identity.

.DESCRIPTION
  Requires Windows Developer Mode. Registers packaging\AppxManifest.xml with the folder holding DynamicIsland.exe as
  external location; no signing is needed for this development registration. Re-run after changing the manifest or
  moving the exe.

.EXAMPLE
  .\packaging\Register-DevPackage.ps1
  .\packaging\Register-DevPackage.ps1 -AppDir .\src\DynamicIsland\bin\Debug\net10.0-windows10.0.22621.0
  .\packaging\Register-DevPackage.ps1 -Unregister
#>
param(
    [string]$AppDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\DynamicIsland'),
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
$packageName = 'DynamicIsland.Dev'
$manifest = Join-Path $PSScriptRoot 'AppxManifest.xml'

$existing = Get-AppxPackage -Name $packageName
if ($existing) {
    Write-Host "Removing $($existing.PackageFullName)"
    Remove-AppxPackage -Package $existing.PackageFullName
}
if ($Unregister) {
    return
}

$AppDir = (Resolve-Path $AppDir).Path
if (-not (Test-Path (Join-Path $AppDir 'DynamicIsland.exe'))) {
    throw "DynamicIsland.exe not found in $AppDir. Publish first: dotnet publish .\src\DynamicIsland -c Release -o .\artifacts\DynamicIsland"
}

Add-AppxPackage -Register $manifest -ExternalLocation $AppDir
Write-Host "Registered $packageName -> $AppDir"
Write-Host 'Start DynamicIsland.exe again, then allow notification access in Settings > Privacy & security > Notifications if asked.'
