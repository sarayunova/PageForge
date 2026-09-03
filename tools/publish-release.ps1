# =============================================================================
#  publish-release.ps1 — reproducible release publish for PageForge (Phase 7).
#  Produces a self-contained, signable folder payload for the desktop app (and a
#  framework-dependent publish of the hosted API) under artifacts/release/, then
#  zips the desktop folder into a distributable pageforge-<Version>-win-x64.zip.
#  A folder layout (NOT single-file) is used deliberately: it keeps the native
#  pageforge_mupdf.dll and tessdata on disk exactly as CI's --smoke layout runs
#  them, so OCR works offline with no runtime single-file extraction surprises.
#  Signing is performed here IF a certificate is provided; otherwise the script
#  emits a clear warning that the output is unsigned.
#
#  Copyright (c) 2026 LiVi Software Company
#  SPDX-License-Identifier: AGPL-3.0-only
# =============================================================================
[CmdletBinding()]
param(
    # Semantic version stamped into the release folder + installer name.
    [string]$Version = "0.0.0-beta",

    # Publish the hosted API as well as the desktop app (default: true).
    [switch]$SkipApi
)

$ErrorActionPreference = "Stop"

$root   = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"

if (-not (Test-Path $dotnet)) {
    throw "dotnet not found at expected user-scope path '$dotnet'; set/toolchain first."
}

$relRoot    = Join-Path $root "artifacts\release"
$desktopDir = Join-Path $relRoot "PageForge-$Version"
$apiDir     = Join-Path $relRoot "server-api"
New-Item -ItemType Directory -Force -Path $desktopDir, $apiDir | Out-Null

Write-Host "== PageForge release publish ($Version) ==" -ForegroundColor Cyan

# --- 1. ensure the native MuPDF shim exists (desktop AND API need it) ------
$shim = Join-Path $root "native\out\PageForge.MuPdfShim\Release\pageforge_mupdf.dll"
if (-not (Test-Path $shim)) {
    Write-Host "native shim missing; building via build-mupdf.ps1 ..." -ForegroundColor Yellow
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root "native\build-mupdf.ps1")
    if ($LASTEXITCODE -ne 0) { throw "native shim build failed." }
}
if (-not (Test-Path $shim)) { throw "native shim still missing after build: $shim" }

# --- 2. publish the desktop app self-contained (folder layout) --------------
Write-Host "publishing desktop app (self-contained win-x64, folder layout) ..." -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\PageForge.App.Wpf\PageForge.App.Wpf.csproj") `
    -c Release -r win-x64 --self-contained true -o $desktopDir
if ($LASTEXITCODE -ne 0) { throw "desktop publish failed." }

# --- 3. publish the hosted API (framework-dependent, portable; deploys via docker) --
if (-not $SkipApi) {
    Write-Host "publishing hosted API (framework-dependent, portable) ..." -ForegroundColor Cyan
    & $dotnet publish (Join-Path $root "services\PageForge.Api\PageForge.Api.csproj") `
        -c Release --self-contained false -o $apiDir
    if ($LASTEXITCODE -ne 0) { throw "API publish failed." }
}

# --- 4. verify the shim + OCR data made it into the payloads ---------------
foreach ($dir in @($desktopDir)) {
    if (-not (Test-Path (Join-Path $dir "pageforge_mupdf.dll"))) {
        throw "published payload missing pageforge_mupdf.dll in $dir"
    }
    if (-not (Test-Path (Join-Path $dir "tessdata\eng.traineddata"))) {
        throw "published payload missing tessdata\eng.traineddata in $dir"
    }
}
if (-not $SkipApi) {
    foreach ($f in @("pageforge_mupdf.dll", "tessdata\eng.traineddata")) {
        if (-not (Test-Path (Join-Path $apiDir $f))) {
            throw "API publish missing $f in $apiDir"
        }
    }
}
Write-Host "payload verification OK (shim + tessdata present)." -ForegroundColor Green

# --- 5. sign (optional) -----------------------------------------------------
# Two supported signing sources:
#   A) A .pfx: PAGEFORGE_CERT_PFX (path) + PAGEFORGE_CERT_PASSWORD (secret)
#   B) Azure Trusted Signing: PAGEFORGE_ATS_ENDPOINT / _CERT / _ID etc. (via signtool /tr /td)
# If neither is present, we only warn: an unsigned payload is NOT a release.
$exes = @(
    (Join-Path $desktopDir "PageForge.App.Wpf.exe")
)

$signArgs = @()
$pfx = $env:PAGEFORGE_CERT_PFX
if ($pfx -and (Test-Path $pfx)) {
    $pass  = $env:PAGEFORGE_CERT_PASSWORD
    $tsUri = $env:PAGEFORGE_TSA_URL
    if (-not $tsUri) { $tsUri = "http://timestamp.digicert.com" }
    $signArgs = @("/f", $pfx, "/p", $pass, "/tr", $tsUri, "/td", "sha256", "/fd", "sha256")
} elseif ($env:PAGEFORGE_ATS_ENDPOINT) {
    # Azure Trusted Signing via signtool /fdecoded or /io; requires AzureSignTool.
    Write-Host "Azure Trusted Signing configured — use AzureSignTool for ATS signing." -ForegroundColor Yellow
    $signArgs = @()  # filled by user's signing tool; documented in CONTRIBUTING.md
}

$signtool = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
if (-not $signtool) {
    # signtool is rarely on PATH; locate it under the Windows 10/11 SDK kits.
    $kitsRoot = Join-Path $env:ProgramFiles "Windows Kits\10\bin"
    if (Test-Path $kitsRoot) {
        $candidates = Get-ChildItem $kitsRoot -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\signtool.exe$" } |
            Sort-Object FullName -Descending
        if ($candidates) {
            $signtool = Get-Item $candidates[0].FullName
        }
    }
}
if (-not $signtool -and $signArgs.Count -gt 0) {
    Write-Warning "signtool.exe not found — cannot sign."
}
# Normalize signtool to an absolute path (works whether found on PATH or via kits scan).
if ($signtool) {
    $signtoolPath = if ($signtool -is [System.IO.FileInfo]) { $signtool.FullName }
                    else { $signtool.Path }
} else {
    $signtoolPath = $null
}

if ($signArgs.Count -gt 0 -and $signtoolPath) {
    foreach ($exe in $exes) {
        if (Test-Path $exe) {
            Write-Host "signing $exe ..." -ForegroundColor Cyan
            & $signtoolPath sign @signArgs $exe
            if ($LASTEXITCODE -ne 0) { throw "signing failed: $exe" }
        }
    }
} else {
    Write-Warning "No code-signing certificate provided — OUTPUT IS UNSIGNED."
    Write-Warning "Set PAGEFORGE_CERT_PFX (+ PAGEFORGE_CERT_PASSWORD) or an Azure Trusted"
    Write-Warning "Trusted Signing profile to sign. Do not publish an unsigned payload."
}

# --- 6. zip the desktop folder for distribution -----------------------------
# Zipping happens AFTER signing: signtool rewrites the .exe in place, so a zip
# built earlier would ship an unsigned binary inside a "signed" release.
$desktopZip = Join-Path $relRoot "pageforge-$Version-win-x64.zip"
if (Test-Path $desktopZip) { Remove-Item $desktopZip -Force }
Compress-Archive -Path (Join-Path $desktopDir "*") -DestinationPath $desktopZip
Write-Host "desktop payload zipped: $desktopZip" -ForegroundColor Green

Write-Host ""
Write-Host "Release staged at: $relRoot" -ForegroundColor Green
if ($signArgs.Count -gt 0 -and $signtoolPath) {
    # Prove the signature actually took: a release must never ship an exe that
    # signtool cannot verify against a trusted chain.
    foreach ($exe in $exes) {
        if (Test-Path $exe) {
            & $signtoolPath verify /pa /v $exe
            if ($LASTEXITCODE -ne 0) { throw "signature verification failed: $exe" }
        }
    }
    Write-Host "Signed payload verified — upload to your release host." -ForegroundColor Green
} else {
    Write-Warning "Re-run with a certificate before publishing the release."
}
