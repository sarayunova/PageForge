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
    # Do NOT hardcode the edition, year, or Program Files root. Hosted CI images
    # move all three -- the GitHub windows-latest image is what broke this -- and
    # a developer machine may have any of them. vswhere is the supported
    # discovery tool and always installs to the same fixed path, whatever
    # version of Visual Studio is present.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $bat = & $vswhere -latest -products * `
            -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -find 'VC\Auxiliary\Build\vcvars64.bat' | Select-Object -First 1
        if ($bat -and (Test-Path $bat)) { return $bat }
    }

    # Fallback for installs vswhere does not know about: glob every year and
    # edition under both Program Files roots rather than naming them.
    foreach ($root in @("${env:ProgramFiles(x86)}\Microsoft Visual Studio",
                        "$env:ProgramFiles\Microsoft Visual Studio")) {
        if (-not (Test-Path $root)) { continue }
        $bat = Get-ChildItem -Path $root -Recurse -Depth 6 -Filter 'vcvars64.bat' `
            -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($bat) { return $bat.FullName }
    }

    # Nothing found: report what IS present, so a CI failure is diagnosable from
    # the log without burning another round trip.
    Write-Host '--- Visual Studio discovery failed; what is actually installed ---'
    Write-Host "vswhere present: $(Test-Path $vswhere)"
    foreach ($root in @("${env:ProgramFiles(x86)}\Microsoft Visual Studio",
                        "$env:ProgramFiles\Microsoft Visual Studio")) {
        Write-Host "root '$root' exists: $(Test-Path $root)"
        if (Test-Path $root) {
            Get-ChildItem $root -ErrorAction SilentlyContinue |
                ForEach-Object { Write-Host "  $($_.Name)" }
        }
    }
    throw 'vcvars64.bat not found. Install Visual Studio (or Build Tools) with the Desktop C++ workload (v143).'
}

$VcVars = Find-VcVars

function Invoke-MsBuild {
    param([string]$Project, [string]$Platform = 'x64')
    # The generated batch file MUST carry a .cmd extension. The previous version
    # wrote it as ...msbuild.log and handed that to `cmd /c`, so Windows
    # dispatched the file by its extension association rather than executing it.
    # On a headless CI runner that never returns: the hosted native build sat on
    # this step for over an hour, producing no output, until it was cancelled.
    $script = Join-Path $env:TEMP 'pageforge-mupdf-msbuild.cmd'
    $cmd = @"
@echo off
call "$VcVars" >nul 2>&1
msbuild "$Project" /t:Build /p:Configuration=Release /p:Platform=$Platform /p:PlatformToolset=v143 /m -v:m
exit /b %ERRORLEVEL%
"@
    Set-Content -Path $script -Value $cmd -Encoding Ascii
    # Let msbuild's output through to the console. Piping it to Out-Null left
    # every failure undiagnosable, and the old error message pointed at a file
    # that held the script rather than any build output.
    & cmd /c "`"$script`""
    # $? after a pipeline reports the pipeline, not msbuild, so a compile failure
    # used to pass this check silently. Test the real exit code.
    if ($LASTEXITCODE -ne 0) {
        throw "msbuild failed for $Project ($Platform, exit code $LASTEXITCODE)"
    }
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
if ($bin2coffText -notmatch '<ProjectConfiguration Include="Release\|x64">') {
    # Insert a Release|x64 configuration after the COMPLETE Release|Win32
    # element. Anchor on the whole element, not just its opening tag: anchoring
    # on the opening tag nests the new element inside the old one and yields
    # malformed XML. Also keep the -replace and both of its operands on a single
    # line -- spread across continuation lines, pwsh 7 parses this as three
    # elements and throws "The -replace operator allows only two elements to
    # follow it", which is what broke the hosted native build.
    $win32Element = '(?s)<ProjectConfiguration Include="Release\|Win32">.*?</ProjectConfiguration>'
    $x64Element = @(
        ''
        '    <ProjectConfiguration Include="Release|x64">'
        '      <Configuration>Release</Configuration>'
        '      <Platform>x64</Platform>'
        '    </ProjectConfiguration>'
    ) -join "`r`n"
    $bin2coffText = $bin2coffText -replace $win32Element, ('$&' + $x64Element)
    Write-Host 'patched bin2coff.vcxproj with Release|x64'
}
# Applied independently of the x64 patch above: on a tree that already had the
# x64 configuration, the old code skipped the toolset fix entirely.
if ($bin2coffText -notmatch 'PlatformToolset>v143') {
    $bin2coffText = $bin2coffText -replace '<PlatformToolset>v142</PlatformToolset>', '<PlatformToolset>v143</PlatformToolset>'
    Write-Host 'patched bin2coff.vcxproj to PlatformToolset v143'
}
[System.IO.File]::WriteAllText($bin2coff, $bin2coffText)

$libmutool = Join-Path $SourceDir 'platform\win32\libmutool.vcxproj'
$libmutoolText = [System.IO.File]::ReadAllText($libmutool)
if ($libmutoolText -match 'sodochandler.vcxproj') {
    $libmutoolText = [regex]::Replace($libmutoolText, '(?s)\s*<ProjectReference[^>]*sodochandler[^>]*/>', '')
    [System.IO.File]::WriteAllText($libmutool, $libmutoolText)
    Write-Host 'removed sodochandler ProjectReference from libmutool.vcxproj (thirdparty/so absent in tarball)'
}

# 3. Build bin2coff first, as a Win32 HOST tool.
# bin2coff.targets invokes the converter as a literal "Release\bin2coff.exe" --
# the Win32 output path -- no matter which platform is being built. Building it
# only under Platform=x64 puts it in x64\Release\ instead, and every font
# resource in libresources then fails with MSB3721. This never showed up on a
# developer machine that already had a stale Release\bin2coff.exe on disk.
$bin2coffProj = Join-Path $SourceDir 'platform\win32\bin2coff.vcxproj'
$bin2coffExe = Join-Path $SourceDir 'platform\win32\Release\bin2coff.exe'
if (-not (Test-Path $bin2coffExe)) {
    Write-Host 'building bin2coff.vcxproj (Win32 host tool)'
    Invoke-MsBuild $bin2coffProj -Platform Win32
}
if (-not (Test-Path $bin2coffExe)) {
    throw "bin2coff.exe was not produced at $bin2coffExe; libresources cannot convert font resources without it"
}
Write-Host "OK: host tool present at $bin2coffExe"

# 4. Build the MuPDF static libraries + mutool.exe.
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