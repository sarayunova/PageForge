# =============================================================================
#  find-signtool.ps1 — resolve signtool.exe and print its full path.
#  signtool is rarely on PATH; it ships under the Windows 10/11 SDK kits, which
#  install under Program Files (x86) on 64-bit Windows — including on GitHub's
#  windows-latest runners — so both Program Files roots are searched, not just
#  $env:ProgramFiles. Shared by publish-release.ps1 (local/self-signed signing
#  and post-sign verification) and release.yml (verifying a SignPath-signed
#  payload), so the search logic exists in exactly one place.
#
#  Copyright (c) 2026 LiVi Software Company
#  SPDX-License-Identifier: AGPL-3.0-only
# =============================================================================
$ErrorActionPreference = "Stop"

$signtool = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
if ($signtool) {
    $signtool.Source
    return
}

$kitsRoots = @(
    (Join-Path $env:ProgramFiles "Windows Kits\10\bin"),
    (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin")
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

$candidates = @()
foreach ($kitsRoot in $kitsRoots) {
    $candidates += Get-ChildItem $kitsRoot -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" }
}
# Newest SDK version wins (directory names sort as 10.0.<build>.<rev>).
$candidates = $candidates | Sort-Object FullName -Descending

if (-not $candidates) {
    throw "signtool.exe not found (checked PATH and Windows Kits under both Program Files roots)."
}
$candidates[0].FullName
