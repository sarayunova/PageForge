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
    [switch]$SkipApi,

    # Stop after publishing + verifying the payload, leaving the folder unzipped
    # and unsigned. Used by the Azure Artifact Signing lane, which signs the
    # binaries in the staged folder with an external action and then re-invokes
    # this script with -ZipOnly. Zipping must never precede signing: signtool
    # rewrites the .exe in place, so an earlier zip would ship an unsigned file.
    [switch]$NoZip,

    # Skip publishing entirely and only zip the already-staged (and by now
    # signed) folder. Pair with -RequireSignature so an unsigned payload can
    # never be packaged as a release.
    [switch]$ZipOnly,

    # Fail unless every shipped executable carries a signature that signtool can
    # verify against a trusted chain.
    [switch]$RequireSignature
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

# Publish stages are skipped in -ZipOnly mode: the folder is already staged and
# has just been signed by the external signing lane.
if (-not $ZipOnly) {
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

}

# --- 5. sign (optional) -----------------------------------------------------
# Two supported signing sources:
#   A) Azure Artifact Signing (formerly Trusted Signing) — the production path.
#      Signing happens OUTSIDE this script, in release.yml's
#      azure/artifact-signing-action step, because the signing key never leaves
#      Azure. That lane runs this script with -NoZip, signs the staged folder,
#      then re-runs it with -ZipOnly -RequireSignature.
#   B) A local .pfx: PAGEFORGE_CERT_PFX (path) + PAGEFORGE_CERT_PASSWORD. Public
#      CAs no longer issue exportable .pfx files, so this is for self-signed
#      dry runs and internal-CA certificates only.
# If neither is present the payload stays unsigned, which is NOT a release.
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
} elseif (-not $RequireSignature) {
    Write-Warning "No code-signing certificate provided — OUTPUT IS UNSIGNED."
    Write-Warning "Sign via the Azure Artifact Signing lane in release.yml, or set"
    Write-Warning "PAGEFORGE_CERT_PFX (+ PAGEFORGE_CERT_PASSWORD) for a local/self-signed"
    Write-Warning "dry run. Do not publish an unsigned payload."
}

# --- 5b. verify signatures BEFORE packaging ---------------------------------
# Runs when this script signed the payload itself, and whenever the caller
# passes -RequireSignature (the Azure Artifact Signing lane's -ZipOnly pass,
# where signing was done by an external action). Verifying before the zip is
# built is what guarantees a "signed release" zip can never contain an unsigned
# binary.
$signedHere = ($signArgs.Count -gt 0 -and $signtoolPath)
if ($RequireSignature -or $signedHere) {
    if (-not $signtoolPath) { throw "signtool.exe not found — cannot verify signatures." }
    foreach ($exe in $exes) {
        if (-not (Test-Path $exe)) { throw "expected payload executable missing: $exe" }
        & $signtoolPath verify /pa /v $exe
        if ($LASTEXITCODE -ne 0) { throw "signature verification failed (unsigned or untrusted): $exe" }
        Write-Host "signature verified: $exe" -ForegroundColor Green
    }
}

# --- 6. zip the desktop folder for distribution -----------------------------
# Zipping happens AFTER signing: signtool rewrites the .exe in place, so a zip
# built earlier would ship an unsigned binary inside a "signed" release.
if ($NoZip) {
    Write-Host ""
    Write-Host "-NoZip: payload staged unsigned at $desktopDir" -ForegroundColor Yellow
    Write-Host "Sign it, then re-run with -ZipOnly -RequireSignature to package." -ForegroundColor Yellow
    return
}

$desktopZip = Join-Path $relRoot "pageforge-$Version-win-x64.zip"
if (Test-Path $desktopZip) { Remove-Item $desktopZip -Force }
Compress-Archive -Path (Join-Path $desktopDir "*") -DestinationPath $desktopZip
Write-Host "desktop payload zipped: $desktopZip" -ForegroundColor Green

Write-Host ""
Write-Host "Release staged at: $relRoot" -ForegroundColor Green
if ($RequireSignature -or $signedHere) {
    Write-Host "Signed payload verified — ready to publish." -ForegroundColor Green
} else {
    Write-Warning "Re-run with a certificate before publishing the release."
}
