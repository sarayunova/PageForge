# Copyright (c) 2026 LiVi Software Company
# SPDX-License-Identifier: AGPL-3.0-only
# This file is part of PageForge. See LICENSE for the full license text.
#
# Reproducible MuPDF (AGPLv3, Artifex Software) + PageForge shim build.
# Mirrors the Phase 0 spike that produced native/out/.../pageforge_mupdf.dll.
#
# Prerequisites (non-admin OK):
#   - Visual Studio Build Tools 2022 with "Desktop development with C++" (v143).
#     MSVC is located via vcvars64.bat, so Developer Command Prompt is NOT required.
#   - curl.exe (ships with Windows) and `tar` (ships with Windows 10 1803+).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File native/build-mupdf.ps1
#
# Outputs (gitignored):
#   native/out/_work/mupdf-1.28.3-source/platform/win32/x64/Release/   static libs + mutool.exe
#   native/out/PageForge.MuPdfShim/Release/pageforge_mupdf.dll         P/Invoke shim DLL

$ErrorActionPreference = 'Stop'

$RepoRoot  = Split-Path $PSScriptRoot -Parent
$WorkDir   = Join-Path $RepoRoot 'native\out\_work'
$SourceDir = Join-Path $WorkDir 'mupdf-1.28.3-source'
$LibDir    = Join-Path $SourceDir 'platform\win32\x64\Release'
$ShimProj  = Join-Path $RepoRoot 'native\PageForge.MuPdfShim\PageForge.MuPdfShim.vcxproj'
$ShimOut   = Join-Path $RepoRoot 'native\out\PageForge.MuPdfShim\Release\pageforge_mupdf.dll'

$MupdfVersion = '1.28.3'
$SourceTarball = Join-Path $WorkDir "mupdf-$MupdfVersion-source.tar.gz"
$SourceUrl = "https://github.com/ArtifexSoftware/mupdf-downloads/releases/download/$MupdfVersion/mupdf-$MupdfVersion-source.tar.gz"

# FR-OCR-01: Tesseract OCR traineddata (Apache-2.0, tesseract-ocr/tessdata_fast).
# Vendored at tools/tessdata for offline reproducibility. The build copies it
# into native/out/tessdata so the runtime OCR primitive has a guaranteed
# datadir, and records a pin for CI offline reproduction.
$TessdataVendored   = Join-Path $RepoRoot 'tools\tessdata\eng.traineddata'
$TessdataSourceUrl  = 'https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata'
$TessdataOutDir     = Join-Path $RepoRoot 'native\out\tessdata'
$TessdataOut        = Join-Path $TessdataOutDir 'eng.traineddata'
$TessdataSha256     = '7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2'
if (-not (Test-Path $TessdataVendored) -or
    (Get-FileHash $TessdataVendored -Algorithm SHA256).Hash -ne $TessdataSha256) {
    if (-not $env:PF_MUPDF_SKIP_DOWNLOAD) {
        Write-Host "downloading $TessdataSourceUrl"
        New-Item -ItemType Directory -Path (Split-Path $TessdataVendored) -Force | Out-Null
        curl.exe -L -sS -o $TessdataVendored $TessdataSourceUrl
        if ($LASTEXITCODE -ne 0 -or
            (Get-FileHash $TessdataVendored -Algorithm SHA256).Hash -ne $TessdataSha256) {
            throw 'eng.traineddata fetch or checksum failed; use a PROXY or vendor tools/tessdata/eng.traineddata'
        }
    } else {
        throw 'eng.traineddata missing; run without PF_MUPDF_SKIP_DOWNLOAD once to fetch it'
    }
}
New-Item -ItemType Directory -Path $TessdataOutDir -Force | Out-Null
Copy-Item $TessdataVendored $TessdataOut -Force
Write-Host "OK: tessdata staged at $TessdataOut"

if (-not $env:PF_MUPDF_SKIP_DOWNLOAD) { New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null }

function Find-VcVars {
    foreach ($base in @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022",
        "$env:ProgramFiles\Microsoft Visual Studio\2022")) {
        $bat = Join-Path $base 'BuildTools\VC\Auxiliary\Build\vcvars64.bat'
        if (Test-Path $bat) { return $bat }
        $bat = Join-Path $base 'Enterprise\VC\Auxiliary\Build\vcvars64.bat'
        if (Test-Path $bat) { return $bat }
    }
    throw 'vcvars64.bat not found. Install VS Build Tools 2022 with Desktop C++ (v143).'
}

$VcVars = Find-VcVars

function Invoke-MsBuild {
    param([string]$Project)
    $log = Join-Path $env:TEMP 'pageforge-mupdf-msbuild.log'
    $cmd = @"
call "$VcVars" >nul 2>&1
msbuild "$Project" /t:Build /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v143 /m -v:m
"@
    Set-Content -Path $log -Value $cmd -Encoding Ascii
    cmd /c "`"$log`"" | Out-Null
    if (-not $?) { throw "msbuild failed for $Project (see $log)" }
}

# 1. Obtain the AGPL source tarball.
if (-not (Test-Path $SourceDir)) {
    if (-not (Test-Path $SourceTarball)) {
        Write-Host "downloading $SourceUrl"
        curl.exe -L -sS -o $SourceTarball $SourceUrl
        if ($LASTEXITCODE -ne 0) { throw 'download failed; use a PROXY or vendored tarball (PF_MUPDF_SKIP_DOWNLOAD=1)' }
    }
    tar -xf $SourceTarball -C $WorkDir
    if ($LASTEXITCODE -ne 0) { throw 'tar extraction failed' }
}

# 2. Apply the two local build patches (idempotent).
$bin2coff = Join-Path $SourceDir 'platform\win32\bin2coff.vcxproj'
$bin2coffText = [System.IO.File]::ReadAllText($bin2coff)
if ($bin2coffText -notmatch '<ProjectConfiguration Include="Release\|x64"') {
    $bin2coffText = $bin2coffText -replace
        '<ProjectConfiguration Include="Release\|Win32">',
        '<ProjectConfiguration Include="Release|Win32">' +
        "`r`n<ProjectConfiguration Include='Release|x64'>`r`n<Configuration>Release</Configuration>`r`n<Platform>x64</Platform>`r`n</ProjectConfiguration>"
    if ($bin2coffText -notmatch 'PlatformToolset>v143') {
        $bin2coffText = $bin2coffText -replace '<PlatformToolset>v142</PlatformToolset>', '<PlatformToolset>v143</PlatformToolset>'
    }
    [System.IO.File]::WriteAllText($bin2coff, $bin2coffText)
    Write-Host 'patched bin2coff.vcxproj with Release|x64'
}

$libmutool = Join-Path $SourceDir 'platform\win32\libmutool.vcxproj'
$libmutoolText = [System.IO.File]::ReadAllText($libmutool)
if ($libmutoolText -match 'sodochandler.vcxproj') {
    $libmutoolText = [regex]::Replace($libmutoolText, '(?s)\s*<ProjectReference[^>]*sodochandler[^>]*/>', '')
    [System.IO.File]::WriteAllText($libmutool, $libmutoolText)
    Write-Host 'removed sodochandler ProjectReference from libmutool.vcxproj (thirdparty/so absent in tarball)'
}

# 3. Build the MuPDF static libraries + mutool.exe.
$Projects = @(
    'libmupdf', 'libresources', 'libthirdparty', 'libmuthreads',
    'libtesseract', 'libleptonica', 'libharfbuzz', 'libzxing',
    'libmubarcode', 'libpkcs7', 'libextract', 'libmutool', 'mutool'
)
foreach ($p in $Projects) {
    $proj = Join-Path $SourceDir "platform\win32\$p.vcxproj"
    if (Test-Path $proj) {
        Write-Host "building $p.vcxproj"
        Invoke-MsBuild $proj
    }
}

# 4. Build the P/Invoke shim DLL.
Write-Host 'building PageForge.MuPdfShim'
Invoke-MsBuild $ShimProj
if (-not (Test-Path $ShimOut)) { throw "shim not produced: $ShimOut" }

# 5. Smoking gun: the built mutool must run.
$Mutool = Join-Path $LibDir 'mutool.exe'
if (-not (Test-Path $Mutool)) { throw "mutool.exe not produced: $Mutool" }
& $Mutool info (Join-Path $RepoRoot 'tools\sample-pdf\sample-phase0.pdf') 2>&1 | Select-Object -First 3

Write-Host "OK: $ShimOut"