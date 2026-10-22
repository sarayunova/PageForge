# =============================================================================
#  build-installer.ps1 — compile the Inno Setup script into
#  artifacts/release/pageforge-<Version>-win-x64-setup.exe.
#
#  Run it after tools/publish-release.ps1, against the same -Version. The
#  payload folder must already contain the published desktop app. Signing of
#  the finished setup.exe happens outside this script (SignPath, in
#  release.yml) so the private key never lands on the build machine.
#
#  -SignToolCommand is the hook for signing DURING compilation, which is the
#  only way to sign the uninstaller Inno embeds. It is not used by CI. Example,
#  once a local signer exists (no secret is stored in the repo):
#
#    -SignToolCommand 'signtool.exe sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $f'
#
#  Copyright (c) 2026 LiVi Software Company
#  SPDX-License-Identifier: AGPL-3.0-only
# =============================================================================
[CmdletBinding()]
param(
    [string]$Version = "0.0.0-beta",

    # Published desktop folder. Defaults to the directory publish-release.ps1
    # writes for the same -Version.
    [string]$PayloadDir = "",

    # Optional ISCC SignTool command. $f is Inno's placeholder for the file
    # being signed. Leave empty to produce an unsigned setup.exe.
    [string]$SignToolCommand = ""
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-version.ps1")
. (Join-Path $PSScriptRoot "install-innosetup.ps1")

$root = Split-Path -Parent $PSScriptRoot
$ver = ConvertTo-PageForgeVersion -Version $Version

if (-not $PayloadDir) {
    $PayloadDir = Join-Path $root "artifacts\release\PageForge-$($ver.Display)"
}
$PayloadDir = $PayloadDir.TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $PayloadDir)) {
    throw "payload folder not found: $PayloadDir. Run tools/publish-release.ps1 -Version $($ver.Display) first."
}
$PayloadDir = (Resolve-Path -LiteralPath $PayloadDir).Path

$exe = Join-Path $PayloadDir "PageForge.App.Wpf.exe"
$shim = Join-Path $PayloadDir "pageforge_mupdf.dll"
$tess = Join-Path $PayloadDir "tessdata\eng.traineddata"
foreach ($required in @($exe, $shim, $tess)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "payload is missing $required. The installer would ship an app that cannot open a PDF."
    }
}

$iss = Join-Path $root "installer\pageforge.iss"
if (-not (Test-Path -LiteralPath $iss)) {
    throw "installer script not found: $iss"
}

$iscc = Get-InnoSetupCompiler
Write-Host "using $iscc" -ForegroundColor Cyan
Write-Host "compiling installer $($ver.Display) from $PayloadDir" -ForegroundColor Cyan

$isccArgs = @(
    "/DMyAppVersion=$($ver.Display)",
    "/DMyAppVersionNumeric=$($ver.File)",
    "/DPayloadDir=$PayloadDir"
)
if ($SignToolCommand) {
    $isccArgs += "/DSignSetup=1"
    # One argument. ISCC splits the SignTool command itself; $f is its placeholder.
    $isccArgs += "/Ssignpageforge=$SignToolCommand"
}
$isccArgs += $iss

& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed (exit $LASTEXITCODE)."
}

$setup = Join-Path $root "artifacts\release\pageforge-$($ver.Display)-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $setup)) {
    throw "ISCC reported success but did not write $setup"
}

Write-Host "installer: $setup" -ForegroundColor Green
if ($SignToolCommand) {
    Write-Host "SignTool ran during compilation (setup and embedded uninstaller)." -ForegroundColor Green
} else {
    Write-Warning "The setup executable is UNSIGNED."
    Write-Warning "release.yml submits it to SignPath after this step when signing is configured."
    Write-Warning "Pass -SignToolCommand to sign during compilation (required for the embedded uninstaller)."
}
