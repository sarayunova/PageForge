<#
.SYNOPSIS
Builds every managed PageForge project -- that is, the whole solution except
the WinUI 3 spike.

.DESCRIPTION
`dotnet build PageForge.sln` cannot be the managed build command, because the
solution contains src/PageForge.App (the retained WinUI 3 spike, TSD 12.1).
That project needs the Visual Studio UWP/MSIX MSBuild tasks
(Microsoft.Build.AppxPackage.dll, Microsoft.Build.Packaging.Pri.Tasks.dll),
which a user-scope .NET SDK does not ship, so on a dev machine the solution
build fails with MSB4062 every time. The spike is built only by the
`winui-build` CI lane, on an image that has those tasks.

The project list therefore has to be written out somewhere. Keeping it here,
rather than in both AGENTS.md and .github/workflows/ci.yml, is the point of
this script: the two copies drifted, and the documented build command was one
that could not run on the machine it documented.

Note the exit-code handling below. A failing `dotnet build` inside a foreach
neither stops the loop nor fails a pwsh step, so a naive loop reports only the
last project's result -- the same trap ci.yml calls out for `dotnet test`.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools/build-managed.ps1
.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools/build-managed.ps1 -Configuration Release -WarnAsError
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    # What CI builds with. Off by default so a local loop is not blocked by a
    # warning, on in CI so one cannot be merged.
    [switch] $WarnAsError
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

# This dev machine's SDK is user-scope and installed with -NoPath, so the bare
# `dotnet` alias resolves to nothing in a non-path shell. CI's dotnet is on the
# path, so prefer the local one only when it is actually there.
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

# Every project in PageForge.sln except src/PageForge.App.
$managed = @(
    'src/PageForge.Core/PageForge.Core.csproj',
    'src/PageForge.MuPdfInterop/PageForge.MuPdfInterop.csproj',
    'src/PageForge.App.Wpf/PageForge.App.Wpf.csproj',
    'tests/PageForge.RenderSpike/PageForge.RenderSpike.csproj',
    'tests/PageForge.Core.Tests/PageForge.Core.Tests.csproj',
    'tests/PageForge.Fidelity.Tests/PageForge.Fidelity.Tests.csproj',
    'services/PageForge.Api/PageForge.Api.csproj',
    'tests/PageForge.Api.Tests/PageForge.Api.Tests.csproj',
    'tests/PageForge.UiSmoke.Tests/PageForge.UiSmoke.Tests.csproj')

# A project that is in the solution but not in the list above would silently go
# unbuilt, which is exactly how a stale list stops being noticed. Fail instead.
$slnProjects = Select-String -Path (Join-Path $repoRoot 'PageForge.sln') -Pattern '^Project\("\{FAE04EC0' |
    ForEach-Object { if ($_.Line -match '"([^"]*\.csproj)"') { $Matches[1].Replace('\', '/') } }
$expected = @($managed) + 'src/PageForge.App/PageForge.App.csproj'
$unlisted = $slnProjects | Where-Object { $_ -notin $expected }
if ($unlisted) {
    throw "PageForge.sln has project(s) this script does not know about: $($unlisted -join ', '). Add them to the managed list, or to the WinUI exclusion."
}

$failed = @()
foreach ($project in $managed) {
    Write-Host "::group::build $project"
    $buildArgs = @('build', (Join-Path $repoRoot $project), '-c', $Configuration, '--nologo')
    if ($WarnAsError) { $buildArgs += '-warnaserror' }
    & $dotnet @buildArgs
    $code = $LASTEXITCODE
    Write-Host '::endgroup::'
    if ($code -ne 0) { $failed += "$project (exit $code)" }
}

if ($failed) {
    Write-Host "Managed build FAILED for:`n  $($failed -join "`n  ")"
    exit 1
}

Write-Host "Managed build clean ($Configuration, $($managed.Count) projects)."
# Explicit, so a caller reading $LASTEXITCODE gets this script's result rather
# than whatever the last native command happened to leave behind.
exit 0
