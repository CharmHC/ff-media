#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes FFMedia self-contained and packs a Velopack release (installer + deltas).
  Local dry-run: run this, then install the produced Setup.exe to smoke-test updates.
.NOTES
  Prerequisite: dotnet tool install -g vpk   (Velopack CLI)
#>
[CmdletBinding()]
param(
    [string]$Version = '0.9.0',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$proj = Join-Path $root 'src/FFMedia.App/FFMedia.App.csproj'
$publishDir = Join-Path $root 'artifacts/publish'
$releaseDir = Join-Path $root 'artifacts/releases'

# 1. Ensure the bundled binaries are present (yt-dlp.exe / ffmpeg.exe).
& (Join-Path $PSScriptRoot 'fetch-binaries.ps1')

# 2. Publish self-contained (SDD §15). The csproj Content rule copies assets/binaries/*.exe.
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $proj -c Release -r $Runtime --self-contained true -o $publishDir -p:Version=$Version

# 3. Pack a Velopack release. UNSIGNED for v1 — add --signParams "..." here when a cert exists.
vpk pack `
    --packId FFMedia `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe FFMedia.App.exe `
    --packTitle 'FFMedia' `
    --icon (Join-Path $root 'assets/branding/app.ico') `
    --outputDir $releaseDir

Write-Host "`nVelopack release created in $releaseDir" -ForegroundColor Green
Write-Host "Dry-run install: run the Setup.exe there, then bump -Version and re-pack to test the update loop."
