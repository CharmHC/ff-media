#Requires -Version 5.1
<#
.SYNOPSIS
  Generates a multi-resolution app.ico from assets/branding/logo.png (16..256, PNG-compressed).
#>
[CmdletBinding()]
param(
    [string]$Png,
    [string]$Ico
)
$ErrorActionPreference = 'Stop'

# NOTE: these defaults are resolved here (not as param defaults) because on some
# Windows PowerShell 5.1 builds, $PSScriptRoot is not yet populated while
# [CmdletBinding()] param defaults are evaluated, which throws Join-Path an
# empty string (same issue documented in fetch-binaries.ps1).
if (-not $Png) { $Png = Join-Path $PSScriptRoot '..\assets\branding\logo.png' }
if (-not $Ico) { $Ico = Join-Path $PSScriptRoot '..\assets\branding\app.ico' }

Add-Type -AssemblyName System.Drawing

$sizes = 16, 32, 48, 64, 128, 256
$src = [System.Drawing.Image]::FromFile((Resolve-Path $Png).Path)
try {
    $pngStreams = @()
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawImage($src, 0, 0, $s, $s)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $pngStreams += ,($ms.ToArray())
        $ms.Dispose()
    }

    $out = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $out
    # ICONDIR
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $bytes = $pngStreams[$i]
        $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))  # width  (0 = 256)
        $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))  # height (0 = 256)
        $bw.Write([Byte]0); $bw.Write([Byte]0)                   # colors, reserved
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)              # planes, bpp
        $bw.Write([UInt32]$bytes.Length)                         # size
        $bw.Write([UInt32]$offset)                               # offset
        $offset += $bytes.Length
    }
    foreach ($bytes in $pngStreams) { $bw.Write($bytes) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path (Split-Path $Ico) (Split-Path $Ico -Leaf)), $out.ToArray())
    $bw.Dispose(); $out.Dispose()
    Write-Host "Wrote $Ico"
}
finally {
    $src.Dispose()
}
