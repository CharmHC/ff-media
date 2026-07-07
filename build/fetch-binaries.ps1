#Requires -Version 5.1
<#
.SYNOPSIS
  Downloads PINNED yt-dlp.exe and ffmpeg.exe into assets/binaries/ and verifies SHA-256.
.NOTES
  Versions/hashes are pinned for reproducible builds (SDD §9, §16). yt-dlp's hash is
  cross-checked against its official SHA2-256SUMS; ffmpeg's is computed from the pinned
  BtbN zip (BtbN publishes no sums). Bump these deliberately, not automatically.
#>
[CmdletBinding()]
param(
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# NOTE: the OutDir default is resolved here (not as a param default) because on
# some Windows PowerShell 5.1 builds, $PSScriptRoot is not yet populated while
# [CmdletBinding()] param defaults are evaluated, which throws Join-Path an
# empty string.
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot '..\assets\binaries' }

# --- Pinned versions + hashes ---
$YtDlpVersion    = '2026.07.04'
$YtDlpSha256     = '52fe3c26dcf71fbdc85b528589020bb0b8e383155cfa81b64dd447bbe35e24b8'
$FfmpegTag       = 'autobuild-2026-07-07-13-44'
$FfmpegAsset     = 'ffmpeg-n8.1.2-22-g94138f6973-win64-gpl-8.1.zip'
$FfmpegZipSha256 = 'f9fdfc417d5091cb3a3487b484ee824bce4fd6fa92dc85a412142f2911b7a22c'

function Assert-Hash([string]$Path, [string]$Expected, [string]$Name) {
    $actual = (Get-FileHash -Algorithm SHA256 -Path $Path).Hash
    if ($actual -ne $Expected.ToUpperInvariant()) {
        throw "$Name SHA-256 mismatch.`n  expected: $Expected`n  actual:   $actual"
    }
    Write-Host "  verified $Name ($actual)"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

# --- yt-dlp (pinned single exe) ---
$ytdlp = Join-Path $OutDir 'yt-dlp.exe'
Write-Host "Downloading yt-dlp $YtDlpVersion -> $ytdlp"
Invoke-WebRequest -Uri "https://github.com/yt-dlp/yt-dlp/releases/download/$YtDlpVersion/yt-dlp.exe" -OutFile $ytdlp
Assert-Hash -Path $ytdlp -Expected $YtDlpSha256 -Name 'yt-dlp.exe'

# --- ffmpeg (pinned BtbN gpl build; verify the zip, then extract ffmpeg.exe) ---
$ffmpegExe = Join-Path $OutDir 'ffmpeg.exe'
$tmpZip = Join-Path $env:TEMP ("ffmpeg-" + [guid]::NewGuid().ToString('N') + '.zip')
$tmpDir = Join-Path $env:TEMP ("ffmpeg-" + [guid]::NewGuid().ToString('N'))
try {
    Write-Host "Downloading ffmpeg $FfmpegTag..."
    Invoke-WebRequest -Uri "https://github.com/BtbN/FFmpeg-Builds/releases/download/$FfmpegTag/$FfmpegAsset" -OutFile $tmpZip
    Assert-Hash -Path $tmpZip -Expected $FfmpegZipSha256 -Name 'ffmpeg zip'
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
