# Copyright (c) 2026 LiVi Software Company
# SPDX-License-Identifier: AGPL-3.0-only
# This file is part of PageForge. See LICENSE for the full license text.

<#
.SYNOPSIS
    Regenerates the application icon from the brand source art.

.DESCRIPTION
    The brand source (assets/brand/pageforge-icon-source.png) is the flat
    full-bleed app tile: a bold white document-and-pencil glyph on solid LiVi
    blue, drawn edge to edge with the tile's own rounded corners and a white
    field outside them. That white field must not ship inside the icon, so this
    script masks the rounded corners to transparent and writes:

      src/PageForge.App.Wpf/Assets/pageforge.ico      - 16..256px, for Windows chrome
      src/PageForge.App.Wpf/Assets/pageforge-256.png  - for in-app display

    The crop parameters are defaults rather than constants so the script still
    works if the source art is re-exported at different framing; the defaults
    take the whole 1024px square, which is how the current art is framed.

    The flat art was chosen over the earlier gradient badge (kept alongside it
    as assets/brand/pageforge-icon-alt-badge.png) precisely because its heavy
    white strokes survive the downscale: the motif stays legible at 16px, where
    the gradient badge's thin navy outlines collapsed into a blue blob.

    Re-run this script and commit the outputs whenever the source art changes -
    the .ico is a build input, and leaving it as an unreproducible binary would
    make it impossible to revise.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
#>
param(
    [string] $Source,
    [string] $OutDir,
    # Badge bounding box within the source render.
    [int] $X = 0,
    [int] $Y = 0,
    [int] $Size = 1024,
    # Corner radius as a fraction of the badge width, matching the artwork's own
    # rounding. 0 leaves the crop square.
    [double] $CornerFraction = 0.16
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $repoRoot 'assets/brand/pageforge-icon-source.png' }
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'src/PageForge.App.Wpf/Assets' }

if (-not (Test-Path $Source)) { throw "Brand source art not found: $Source" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

$src = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
Write-Host "source $($src.Width)x$($src.Height) -> badge ${Size}px at ($X,$Y)"

$badge = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($badge)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $Size, $Size), $X, $Y, $Size, $Size, [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()
$src.Dispose()

if ($CornerFraction -gt 0) {
    $d = [int]($Size * $CornerFraction) * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($Size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($Size - $d, $Size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $Size - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $masked = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $mg = [System.Drawing.Graphics]::FromImage($masked)
    $mg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $mg.Clear([System.Drawing.Color]::Transparent)
    $mg.SetClip($path)
    $mg.DrawImage($badge, 0, 0, $Size, $Size)
    $mg.Dispose(); $path.Dispose(); $badge.Dispose()
    $badge = $masked
}

# 256 is the shell's large-icon size; the rest are the sizes Windows asks for
# across Explorer views, the taskbar, Alt-Tab and window chrome.
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bg = [System.Drawing.Graphics]::FromImage($bmp)
    $bg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $bg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $bg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $bg.Clear([System.Drawing.Color]::Transparent)
    $bg.DrawImage($badge, (New-Object System.Drawing.Rectangle 0, 0, $s, $s))
    $bg.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}
$badge.Dispose()

$pngPath = Join-Path $OutDir 'pageforge-256.png'
[System.IO.File]::WriteAllBytes($pngPath, ($frames | Where-Object { $_.Size -eq 256 }).Bytes)
Write-Host "wrote $pngPath"

# ICO container: 6-byte ICONDIR, one 16-byte ICONDIRENTRY per frame, then the
# payloads. PNG-compressed entries are valid in ICO from Windows Vista onward,
# well below this app's Windows 10/11 floor.
$icoPath = Join-Path $OutDir 'pageforge.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)                # reserved
$bw.Write([uint16]1)                # type 1 = icon
$bw.Write([uint16]$frames.Count)

$offset = 6 + (16 * $frames.Count)
foreach ($f in $frames) {
    # 256 is encoded as 0 in the single-byte width/height fields.
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)              # palette entries (0 = truecolour)
    $bw.Write([byte]0)              # reserved
    $bw.Write([uint16]1)            # colour planes
    $bw.Write([uint16]32)           # bits per pixel
    $bw.Write([uint32]$f.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Write-Host "wrote $icoPath ($((Get-Item $icoPath).Length) bytes, $($frames.Count) sizes: $($sizes -join ', '))"
