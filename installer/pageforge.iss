; Copyright (c) 2026 LiVi Software Company
; SPDX-License-Identifier: AGPL-3.0-only
; This file is part of PageForge. See LICENSE for the full license text.
;
; Per-user setup for LiVi's PageForge. Compile with tools/build-installer.ps1,
; which pins Inno Setup 6.7.3 and passes the three defines below.
;
; The AppId is the upgrade identity. It must stay stable across versions:
; changing it makes the next install a second copy instead of an upgrade.
;
; MSIX is not used. An unsigned MSIX cannot be installed by a normal user
; (Windows requires a signature that chains to a trusted root; the Windows 11
; -AllowUnsigned switch is an administrator-only test hook and Microsoft says
; not to distribute that way). This beta ships unsigned until SignPath is
; configured, so the installer has to run without a trusted signature.

#ifndef MyAppVersion
  #error MyAppVersion must be set (tools/build-installer.ps1 passes it)
#endif
#ifndef MyAppVersionNumeric
  #error MyAppVersionNumeric must be a four-part numeric version
#endif
#ifndef PayloadDir
  #error PayloadDir must be the published desktop folder
#endif
#ifndef SignSetup
  #define SignSetup 0
#endif

#define MyAppName "LiVi's PageForge"
#define MyAppPublisher "LiVi Software Company"
#define MyAppURL "https://github.com/sarayunova/PageForge"
#define MyAppExe "PageForge.App.Wpf.exe"

[Setup]
; Doubled braces: { starts a constant, so the stored AppId is the single-braced GUID.
AppId={{B4FDFF96-31C0-492B-BF86-E9A99B79CF97}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
AppCopyright=Copyright (c) 2026 LiVi Software Company
; These land in a fixed-width placeholder inside SetupLdr, so the setup
; executable's Details tab shows the strings padded with trailing spaces.
; The wizard and Apps & Features use AppVersion, which is not padded.
VersionInfoVersion={#MyAppVersionNumeric}
VersionInfoProductVersion={#MyAppVersionNumeric}
VersionInfoTextVersion={#MyAppVersion}
VersionInfoProductTextVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=LiVi's PageForge setup
VersionInfoCopyright=Copyright (c) 2026 LiVi Software Company
VersionInfoProductName={#MyAppName}
VersionInfoOriginalFileName=pageforge-{#MyAppVersion}-win-x64-setup.exe
DefaultDirName={autopf}\PageForge
; The directory page is off because [InstallDelete] clears {app} on every
; upgrade. A fixed location keeps that from ever pointing at a folder the
; user typed by mistake. {autopf} is the per-user Programs folder when the
; install is not elevated, and Program Files when it is.
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName={#MyAppName}
; lowest = no UAC prompt. The wizard still offers a per-machine install;
; choosing it elevates. /CURRENTUSER and /ALLUSERS select the same choice
; for a silent install.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts\release
OutputBaseFilename=pageforge-{#MyAppVersion}-win-x64-setup
SetupIconFile=..\src\PageForge.App.Wpf\Assets\pageforge.ico
WizardSmallImageFile=..\src\PageForge.App.Wpf\Assets\pageforge-256.png
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
LicenseFile=..\LICENSE
InfoBeforeFile=INFO.txt
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
ChangesAssociations=yes
CloseApplications=yes
CloseApplicationsFilter={#MyAppExe}
RestartApplications=no
#if SignSetup != 0
; Defined only when build-installer.ps1 was given -SignToolCommand. ISCC then
; needs /Ssignpageforge=... so it can sign the setup and the embedded
; uninstaller while compiling. The release workflow does not set this: SignPath
; signs the finished setup.exe after compilation, which cannot reach the
; uninstaller already embedded inside it. See CONTRIBUTING.md.
SignTool=signpageforge
SignedUninstaller=yes
#endif

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; ignoreversion: replace every file on upgrade. A self-contained publish
; carries runtime DLLs whose file versions do not track the app version, and
; the default comparison would leave a stale one in place.
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Drop the previous payload first so a file this version no longer ships
; cannot linger beside the new one and be loaded instead. This is {app} only.
; Settings live in {localappdata}\PageForge, which is a different directory
; even for a per-user install ({localappdata}\Programs\PageForge).
Type: filesandordirs; Name: "{app}\*"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; IconFilename: "{app}\{#MyAppExe}"; Comment: "View and edit PDFs"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; IconFilename: "{app}\{#MyAppExe}"; Comment: "View and edit PDFs"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Registry]
; An available handler for .pdf, not the default.
;
; What is written:
;   a ProgID, an Applications registration, OpenWithProgids, and a
;   RegisteredApplications capability, so Open with and Settings > Default
;   apps can both offer PageForge.
; What is deliberately not written:
;   the default value of Software\Classes\.pdf (that would become the
;   per-user default), and Explorer\FileExts\.pdf\UserChoice (that is the
;   hash-protected default, and writing it hijacks the user's choice).
; HKA is HKCU for a per-user install and HKLM for a per-machine one.
Root: HKA; Subkey: "Software\Classes\PageForge.PDF"; ValueType: string; ValueName: ""; ValueData: "PDF document"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\PageForge.PDF"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "LiVi's PageForge PDF"
Root: HKA; Subkey: "Software\Classes\PageForge.PDF"; ValueType: string; ValueName: "Content Type"; ValueData: "application/pdf"
Root: HKA; Subkey: "Software\Classes\PageForge.PDF\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExe},0"
Root: HKA; Subkey: "Software\Classes\PageForge.PDF\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExe}"" ""%1"""

Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "PageForge.PDF"; ValueData: ""; Flags: uninsdeletevalue

Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExe}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExe},0"
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExe}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExe}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""

Root: HKA; Subkey: "Software\PageForge"; ValueType: none; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\PageForge\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#MyAppName}"
Root: HKA; Subkey: "Software\PageForge\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "View and edit PDF documents"
Root: HKA; Subkey: "Software\PageForge\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "PageForge.PDF"
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: "Software\PageForge\Capabilities"; Flags: uninsdeletevalue

[Code]
var
  DeleteUserData: Boolean;

function WantsUserDataDeleted: Boolean;
begin
  // /DELETEUSERDATA=yes on the uninstaller. Absent means "not asked".
  Result := CompareText(ExpandConstant('{param:DELETEUSERDATA|}'), 'yes') = 0;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  DataDir := ExpandConstant('{localappdata}\PageForge');

  if CurUninstallStep = usUninstall then
  begin
    DeleteUserData := False;
    if not DirExists(DataDir) then
      Exit;

    if WantsUserDataDeleted then
    begin
      DeleteUserData := True;
      Exit;
    end;

    // A silent uninstall keeps the data. The prompt defaults to No, and a
    // silent run never shows it, so an unattended uninstall cannot wipe
    // settings, logs or crash-recovery files by accident.
    if UninstallSilent then
      Exit;

    DeleteUserData :=
      MsgBox('PageForge keeps your settings, logs and crash-recovery files in:' + #13#10 +
             DataDir + #13#10 + #13#10 +
             'Delete those too?' + #13#10 + #13#10 +
             'Choose No to keep them. A later install will keep using them.',
             mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end
  else if (CurUninstallStep = usPostUninstall) and DeleteUserData then
  begin
    DelTree(DataDir, True, True, True);
  end;
end;
