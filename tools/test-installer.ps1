# =============================================================================
#  test-installer.ps1 — install, upgrade and uninstall the per-user setup on
#  this machine and check the promises the script makes.
#
#  This is destructive to a per-user PageForge install in this Windows account.
#  It refuses to start when one already exists, or when %LOCALAPPDATA%\PageForge
#  already exists, unless -AllowUserDataDeletion is passed. CI passes that
#  switch: the runner has nothing of the user's to lose, and the switch is what
#  lets the test prove the "delete my settings" uninstall option.
#
#  Copyright (c) 2026 LiVi Software Company
#  SPDX-License-Identifier: AGPL-3.0-only
# =============================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SetupExe,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    # Required when this account already has a per-user install or a
    # %LOCALAPPDATA%\PageForge directory. The test removes both.
    [switch]$AllowUserDataDeletion
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-version.ps1")
$ver = ConvertTo-PageForgeVersion -Version $Version

if (-not (Test-Path -LiteralPath $SetupExe)) {
    throw "setup executable not found: $SetupExe"
}
$SetupExe = (Resolve-Path -LiteralPath $SetupExe).Path

$setupLength = (Get-Item -LiteralPath $SetupExe).Length
# A stub installer with no payload is about 2 MB. The self-contained app is
# far larger even after compression; this only catches a compile that packed
# an empty folder. The install checks below are the real proof.
if ($setupLength -lt 4MB) {
    throw "setup is only $setupLength bytes. The published payload was not included."
}

$installDir = Join-Path $env:LOCALAPPDATA "Programs\PageForge"
$dataDir = Join-Path $env:LOCALAPPDATA "PageForge"
$programs = [Environment]::GetFolderPath("Programs")
$desktop = [Environment]::GetFolderPath("Desktop")
$startLink = Join-Path $programs "LiVi's PageForge.lnk"
$desktopLink = Join-Path $desktop "LiVi's PageForge.lnk"
$logDir = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\installer-test"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Invoke-Gui {
    param(
        [Parameter(Mandatory = $true)][string]$File,
        [Parameter(Mandatory = $true)][string]$Arguments,
        [Parameter(Mandatory = $true)][string]$What
    )
    # ProcessStartInfo.Arguments is one command line, so a quoted /LOG= path
    # is parsed by Windows rather than split by PowerShell. The setup and the
    # uninstaller are GUI executables; waiting on the process is required,
    # because the call operator returns before a WinExe finishes.
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $File
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    $proc = [System.Diagnostics.Process]::Start($start)
    $proc.WaitForExit()
    if ($proc.ExitCode -ne 0) {
        throw "$What failed (exit $($proc.ExitCode)). Arguments: $Arguments"
    }
}

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) {
        throw $Message
    }
}

function Get-VersionText {
    param($Value)
    # Inno Setup writes the setup executable's version resource into a
    # fixed-width placeholder, so ProductName and ProductVersion come back
    # padded with spaces. The installed app is a normal .NET binary and is
    # not padded; trimming both keeps the comparison on the text.
    if ($null -eq $Value) { return "" }
    return (($Value -replace "`0", "").Trim())
}

function Get-UninstallEntry {
    $root = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall"
    if (-not (Test-Path $root)) { return $null }
    foreach ($key in Get-ChildItem $root) {
        $item = Get-ItemProperty $key.PSPath
        if ($item.DisplayName -eq "LiVi's PageForge") {
            return $item
        }
    }
    return $null
}

function Test-RegistryValueName {
    param([string]$Key, [string]$Name)
    if (-not (Test-Path $Key)) { return $false }
    return (@((Get-Item $Key).GetValueNames()) -contains $Name)
}

function Install-PageForge {
    param(
        [Parameter(Mandatory = $true)][string]$LogName,
        [string]$ExtraArgs = ""
    )
    $log = Join-Path $logDir $LogName
    $args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /LOG=`"$log`""
    if ($ExtraArgs) { $args = "$args $ExtraArgs" }
    Invoke-Gui -File $SetupExe -Arguments $args -What "install ($LogName)"
}

function Wait-PathGone {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$What,
        [int]$TimeoutSeconds = 120
    )
    # Inno's uninstaller copies itself to TEMP and the process we started
    # exits while that copy is still deleting {app} and running the
    # usPostUninstall step. Waiting on the process is not waiting on the
    # uninstall.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (Test-Path -LiteralPath $Path) {
        if ((Get-Date) -gt $deadline) {
            $left = @(Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction SilentlyContinue |
                Select-Object -ExpandProperty FullName)
            $shown = ($left | Select-Object -First 20) -join ", "
            throw "$What still present after ${TimeoutSeconds}s: $Path ($($left.Count) entries). $shown"
        }
        Start-Sleep -Milliseconds 200
    }
}

function Uninstall-PageForge {
    param(
        [Parameter(Mandatory = $true)][string]$LogName,
        [switch]$DeleteUserData
    )
    $unins = Join-Path $installDir "unins000.exe"
    if (-not (Test-Path -LiteralPath $unins)) {
        throw "uninstaller not found at $unins"
    }
    $log = Join-Path $logDir $LogName
    $args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$log`""
    if ($DeleteUserData) { $args = "$args /DELETEUSERDATA=yes" }
    Invoke-Gui -File $unins -Arguments $args -What "uninstall ($LogName)"
    Wait-PathGone -Path $installDir -What "install directory"
    if ($DeleteUserData) {
        Wait-PathGone -Path $dataDir -What "user data directory"
    }
}

function Assert-Installed {
    param([switch]$ExpectDesktop)
    $app = Join-Path $installDir "PageForge.App.Wpf.exe"
    Assert-That (Test-Path -LiteralPath $app) "installed executable missing: $app"
    Assert-That (Test-Path -LiteralPath (Join-Path $installDir "pageforge_mupdf.dll")) "installed payload missing pageforge_mupdf.dll"
    Assert-That (Test-Path -LiteralPath (Join-Path $installDir "tessdata\eng.traineddata")) "installed payload missing tessdata\eng.traineddata"
    Assert-That (Test-Path -LiteralPath (Join-Path $installDir "unins000.exe")) "uninstaller missing from $installDir"

    $info = (Get-Item -LiteralPath $app).VersionInfo
    Assert-That ((Get-VersionText $info.ProductName) -eq "LiVi's PageForge") "installed product name was '$($info.ProductName)'"
    Assert-That ((Get-VersionText $info.CompanyName) -eq "LiVi Software Company") "installed company was '$($info.CompanyName)'"
    Assert-That ((Get-VersionText $info.ProductVersion) -eq $ver.Display) "installed product version was '$($info.ProductVersion)', expected '$($ver.Display)'"
    Assert-That ($info.FileVersionRaw -eq [version]$ver.File) "installed file version was '$($info.FileVersionRaw)', expected '$($ver.File)'"

    Assert-That (Test-Path -LiteralPath $startLink) "Start menu shortcut missing: $startLink. Programs folder contains: $((Get-ChildItem $programs -ErrorAction SilentlyContinue | ForEach-Object Name) -join ', ')"
    if ($ExpectDesktop) {
        Assert-That (Test-Path -LiteralPath $desktopLink) "desktop shortcut missing: $desktopLink"
    } else {
        Assert-That (-not (Test-Path -LiteralPath $desktopLink)) "desktop shortcut was created even though the task is off: $desktopLink"
    }

    $entry = Get-UninstallEntry
    Assert-That ($null -ne $entry) "Apps & Features entry for LiVi's PageForge was not written"
    Assert-That ($entry.DisplayVersion -eq $ver.Display) "uninstall DisplayVersion was '$($entry.DisplayVersion)', expected '$($ver.Display)'"

    $progId = "HKCU:\Software\Classes\PageForge.PDF"
    Assert-That (Test-Path $progId) "ProgID PageForge.PDF was not registered"
    $command = (Get-Item "$progId\shell\open\command").GetValue("")
    Assert-That ($command -like "*PageForge.App.Wpf.exe*") "open command does not launch the app: $command"
    Assert-That ($command -like '*"%1"*') "open command does not receive the document path: $command"

    Assert-That (Test-RegistryValueName "HKCU:\Software\Classes\.pdf\OpenWithProgids" "PageForge.PDF") "PageForge.PDF was not added to OpenWithProgids"
    Assert-That (Test-RegistryValueName "HKCU:\Software\RegisteredApplications" "LiVi's PageForge") "PageForge was not registered as an available Default apps handler"
    $association = (Get-Item "HKCU:\Software\PageForge\Capabilities\FileAssociations").GetValue(".pdf")
    Assert-That ($association -eq "PageForge.PDF") "capability association was '$association'"

    # The default handler is the (Default) value of .pdf, and the hash-protected
    # UserChoice. Neither may name us.
    if (Test-Path "HKCU:\Software\Classes\.pdf") {
        $defaultHandler = (Get-Item "HKCU:\Software\Classes\.pdf").GetValue("")
        Assert-That ($defaultHandler -ne "PageForge.PDF") "installer set the per-user default PDF handler to PageForge.PDF"
    }
    $userChoice = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice"
    if (Test-Path $userChoice) {
        $chosen = (Get-Item $userChoice).GetValue("ProgId")
        Assert-That ($chosen -ne "PageForge.PDF") "installer wrote UserChoice ProgId=$chosen"
    }
}

function Assert-Removed {
    param([switch]$ExpectData)
    Assert-That (-not (Test-Path -LiteralPath $installDir)) "install directory still present after uninstall: $installDir"
    Assert-That (-not (Test-Path -LiteralPath $startLink)) "Start menu shortcut still present after uninstall"
    Assert-That (-not (Test-Path -LiteralPath $desktopLink)) "desktop shortcut still present after uninstall"
    Assert-That (-not (Test-Path "HKCU:\Software\Classes\PageForge.PDF")) "ProgID was not removed"
    Assert-That (-not (Test-RegistryValueName "HKCU:\Software\Classes\.pdf\OpenWithProgids" "PageForge.PDF")) "OpenWithProgids value was not removed"
    Assert-That (-not (Test-RegistryValueName "HKCU:\Software\RegisteredApplications" "LiVi's PageForge")) "RegisteredApplications value was not removed"
    Assert-That (-not (Test-Path "HKCU:\Software\PageForge")) "capability key was not removed"
    Assert-That ($null -eq (Get-UninstallEntry)) "uninstall entry still present"
    if ($ExpectData) {
        Assert-That (Test-Path -LiteralPath (Join-Path $dataDir "settings.json")) "uninstall removed settings.json without being asked"
        Assert-That (Test-Path -LiteralPath (Join-Path $dataDir "recovery\keep.txt")) "uninstall removed crash-recovery files without being asked"
    }
}

$existed = (Test-Path -LiteralPath $installDir) -or (Test-Path -LiteralPath $dataDir)
if ($existed -and -not $AllowUserDataDeletion) {
    throw @"
Refusing to run: $installDir or $dataDir already exists.
This test installs and uninstalls the per-user copy, and one of its checks
deletes $dataDir. Pass -AllowUserDataDeletion only on a machine that has
nothing you need in those folders.
"@
}

try {
    if (Test-Path -LiteralPath (Join-Path $installDir "unins000.exe")) {
        Uninstall-PageForge -LogName "reset-uninstall.log"
    }
    if (Test-Path -LiteralPath $dataDir) {
        Remove-Item -LiteralPath $dataDir -Recurse -Force
    }

    $setupInfo = (Get-Item -LiteralPath $SetupExe).VersionInfo
    Assert-That ((Get-VersionText $setupInfo.ProductName) -eq "LiVi's PageForge") "setup product name was '$($setupInfo.ProductName)'"
    Assert-That ((Get-VersionText $setupInfo.ProductVersion) -eq $ver.Display) "setup product version was '$($setupInfo.ProductVersion)', expected '$($ver.Display)'"
    Assert-That ($setupInfo.FileVersionRaw -eq [version]$ver.File) "setup file version was '$($setupInfo.FileVersionRaw)', expected '$($ver.File)'"

    Install-PageForge -LogName "install.log"
    Assert-Installed

    # A file the new payload does not ship, plus the user-data the uninstaller
    # must leave alone. The data directory is NOT the install directory.
    Set-Content -LiteralPath (Join-Path $installDir "stray-upgrade.bin") -Value "not part of the payload" -Encoding ascii
    New-Item -ItemType Directory -Force -Path (Join-Path $dataDir "recovery") | Out-Null
    Set-Content -LiteralPath (Join-Path $dataDir "settings.json") -Value '{"Theme":"dark"}' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $dataDir "recovery\keep.txt") -Value "recovery" -Encoding ascii

    Install-PageForge -LogName "upgrade.log"
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $installDir "stray-upgrade.bin"))) "upgrade left stray-upgrade.bin in the install directory"
    Assert-That (Test-Path -LiteralPath (Join-Path $dataDir "settings.json")) "upgrade removed settings.json"
    Assert-Installed

    Uninstall-PageForge -LogName "uninstall-keep.log"
    Assert-Removed -ExpectData

    Install-PageForge -LogName "reinstall.log" -ExtraArgs "/TASKS=desktopicon"
    Assert-Installed -ExpectDesktop
    Uninstall-PageForge -LogName "uninstall-delete.log" -DeleteUserData
    Assert-Removed
    Assert-That (-not (Test-Path -LiteralPath $dataDir)) "uninstall /DELETEUSERDATA=yes left $dataDir in place"

    Write-Host "installer test passed ($($ver.Display))" -ForegroundColor Green
} finally {
    $unins = Join-Path $installDir "unins000.exe"
    if (Test-Path -LiteralPath $unins) {
        Write-Host "cleaning up a leftover install" -ForegroundColor Yellow
        $log = Join-Path $logDir "cleanup.log"
        try {
            Invoke-Gui -File $unins -Arguments "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$log`"" -What "cleanup uninstall"
        } catch {
            Write-Warning $_.Exception.Message
        }
    }
    if (Test-Path -LiteralPath $installDir) {
        try {
            Wait-PathGone -Path $installDir -What "cleanup install directory"
        } catch {
            Write-Warning $_.Exception.Message
        }
    }
}
