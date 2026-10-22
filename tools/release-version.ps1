# =============================================================================
#  release-version.ps1 — split a tag version into the forms Windows will accept.
#
#  The tag (0.1.0-beta) is what people read: the installer wizard, Apps &
#  Features, and the executable's product version. The PE file version and the
#  assembly version can only hold numbers, so the prerelease label is kept on
#  the display string and stripped from the numeric forms.
#
#  Dot-source this file. Running it directly checks the parser.
#
#  Copyright (c) 2026 LiVi Software Company
#  SPDX-License-Identifier: AGPL-3.0-only
# =============================================================================

function ConvertTo-PageForgeVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $trimmed = $Version.Trim()
    if ($trimmed -match '[\\/:*?"<>|]') {
        throw "Version '$trimmed' contains a character that cannot appear in a file name."
    }

    # major.minor.patch, then anything (a prerelease, a build label, nothing).
    # Each numeric component has to fit in a Windows file-version field.
    if ($trimmed -notmatch '^(?<maj>0|[1-9]\d*)\.(?<min>0|[1-9]\d*)\.(?<pat>0|[1-9]\d*)(?<rest>.*)$') {
        throw "Version '$trimmed' must start with major.minor.patch (for example 0.1.0-beta)."
    }

    $maj = [int]$Matches.maj
    $min = [int]$Matches.min
    $pat = [int]$Matches.pat
    foreach ($part in @($maj, $min, $pat)) {
        if ($part -gt 65535) {
            throw "Version component $part exceeds 65535, which a Windows file version cannot store."
        }
    }

    $assembly = "$maj.$min.$pat"
    [pscustomobject]@{
        Display  = $trimmed
        Assembly = $assembly
        File     = "$assembly.0"
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    $samples = @(
        @{ In = '0.1.0-beta'; Assembly = '0.1.0'; File = '0.1.0.0' },
        @{ In = '0.0.0-ci';   Assembly = '0.0.0'; File = '0.0.0.0' },
        @{ In = '1.2.3';      Assembly = '1.2.3'; File = '1.2.3.0' },
        @{ In = '2.0.0-rc.1'; Assembly = '2.0.0'; File = '2.0.0.0' }
    )
    foreach ($sample in $samples) {
        $got = ConvertTo-PageForgeVersion -Version $sample.In
        if ($got.Display -ne $sample.In -or $got.Assembly -ne $sample.Assembly -or $got.File -ne $sample.File) {
            throw "version parse failed for $($sample.In): display=$($got.Display) assembly=$($got.Assembly) file=$($got.File)"
        }
    }

    $rejected = $false
    try {
        ConvertTo-PageForgeVersion -Version 'beta'
    } catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'a version without major.minor.patch was accepted'
    }

    Write-Host 'release-version self-test ok'
}
