# =============================================================================
#  install-innosetup.ps1 — locate ISCC.exe, installing a pinned Inno Setup 6
#  if it is not already on the machine.
#
#  The compiler is not vendored. CI and a local release build download the
#  official installer from the jrsoftware/issrc release and check its SHA-256
#  before running it. The pin is the version installer/pageforge.iss is written
#  against; bump the URL and the hash together.
#
#  Dot-source and call Get-InnoSetupCompiler, or run the script to print the
#  path of ISCC.exe.
#
#  Copyright (c) 2026 LiVi Software Company
#  SPDX-License-Identifier: AGPL-3.0-only
# =============================================================================

$script:InnoSetupVersion = '6.7.3'
$script:InnoSetupUrl = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
# sha256 of innosetup-6.7.3.exe from that GitHub release asset.
$script:InnoSetupSha256 = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'

function Get-InnoSetupCompiler {
    $candidates = @(
        $env:INNO_SETUP_PATH,
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    Write-Host "Inno Setup $($script:InnoSetupVersion) not found; downloading the pinned compiler..." -ForegroundColor Cyan
    $dest = Join-Path $env:TEMP "innosetup-$($script:InnoSetupVersion).exe"
    if (Test-Path -LiteralPath $dest) {
        Remove-Item -LiteralPath $dest -Force
    }

    & curl.exe -fsSL -L --retry 3 -o $dest $script:InnoSetupUrl
    if ($LASTEXITCODE -ne 0) {
        throw "download of Inno Setup failed (curl exit $LASTEXITCODE): $($script:InnoSetupUrl)"
    }

    $hash = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash
    if ($hash -ne $script:InnoSetupSha256) {
        Remove-Item -LiteralPath $dest -Force
        throw "Inno Setup hash mismatch. Expected $($script:InnoSetupSha256), got $hash. Refusing to run the downloaded installer."
    }

    Unblock-File -LiteralPath $dest
    # The compiler's own installer is a GUI-subsystem executable. `&` returns
    # before it finishes; Start-Process -Wait does not.
    # Separate arguments. A single string would be one argv element, and the
    # compiler's installer would not see the individual silent-install switches.
    $install = Start-Process -FilePath $dest -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-'
    ) -Wait -PassThru
    if ($install.ExitCode -ne 0) {
        throw "Inno Setup installer failed (exit $($install.ExitCode))."
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "Inno Setup $($script:InnoSetupVersion) installed but ISCC.exe was not found under Program Files."
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    Write-Output (Get-InnoSetupCompiler)
}
