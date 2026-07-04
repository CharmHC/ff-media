#Requires -Version 5.1
<#
.SYNOPSIS
  Downloads yt-dlp.exe and ffmpeg.exe into assets/binaries/ for local dev and packaging.
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\assets\binaries')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # faster Invoke-WebRequest

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

# --- yt-dlp (single exe, latest release) ---
$ytdlp = Join-Path $OutDir 'yt-dlp.exe'
Write-Host "Downloading yt-dlp -> $ytdlp"
Invoke-WebRequest -Uri 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile $ytdlp

# --- ffmpeg (extract ffmpeg.exe from BtbN gpl build) ---
$ffmpegExe = Join-Path $OutDir 'ffmpeg.exe'
$tmpZip = Join-Path $env:TEMP ("ffmpeg-" + [guid]::NewGuid().ToString('N') + '.zip')
$tmpDir = Join-Path $env:TEMP ("ffmpeg-" + [guid]::NewGuid().ToString('N'))
try {
    Write-Host "Downloading ffmpeg build..."
    Invoke-WebRequest -Uri 'https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip' -OutFile $tmpZip
    Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force
    $found = Get-ChildItem -Path $tmpDir -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
    if (-not $found) { throw "ffmpeg.exe not found in downloaded archive." }
    Copy-Item -Path $found.FullName -Destination $ffmpegExe -Force
    Write-Host "Extracted ffmpeg -> $ffmpegExe"
}
finally {
    Remove-Item -Path $tmpZip -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`nResolved versions:"
& $ytdlp --version
& $ffmpegExe -version | Select-Object -First 1
