; SPDX-License-Identifier: GPL-3.0-or-later
; SPDX-FileCopyrightText: 2026 KC2RGW Chris
; Windows installer for SimpleScanner (Inno Setup 6). Built by CI after
; scripts/make-windows.sh: iscc /DAppVersion=<ver> /DSourceDir=<folder> this file.
; Output: artifacts/SimpleScanner-<ver>-win-x64-setup.exe
;
; Installs for the current user only (no administrator password) in
; %LOCALAPPDATA%\Programs\SimpleScanner, with a Start menu entry, an optional
; desktop icon and an uninstaller. Settings and stored channels live in
; %LOCALAPPDATA%\SimpleScanner, outside the program folder, so upgrades keep them
; and uninstalling leaves them. Not code-signed: SmartScreen warns the first time.
; The SDRplay API is not included: it comes from SDRplay's installer (sdrplay.com/api).

#ifndef AppVersion
  #error AppVersion must be defined (/DAppVersion=...)
#endif
#ifndef SourceDir
  #error SourceDir must be defined (/DSourceDir=...)
#endif

[Setup]
; The AppId identifies SimpleScanner to Windows across versions: never change it.
AppId={{53DAED9B-79BE-4555-8D9D-3D517CFECDD2}
AppName=SimpleScanner
AppVersion={#AppVersion}
AppVerName=SimpleScanner {#AppVersion}
AppPublisher=KC2RGW
AppCopyright=Copyright (C) 2026 KC2RGW Chris. GPL-3.0-or-later.
AppPublisherURL=https://github.com/kingsqueak/SimpleScanner
AppSupportURL=https://github.com/kingsqueak/SimpleScanner/issues
AppUpdatesURL=https://github.com/kingsqueak/SimpleScanner/releases
DefaultDirName={localappdata}\Programs\SimpleScanner
DefaultGroupName=SimpleScanner
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
SetupIconFile=..\src\SimpleScanner\Assets\icon.ico
UninstallDisplayIcon={app}\SimpleScanner.exe
UninstallDisplayName=SimpleScanner
OutputDir=..\artifacts
OutputBaseFilename=SimpleScanner-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Close a running SimpleScanner (and its radio helpers) before replacing files.
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; An upgrade starts from a clean program folder, so no file from an older
; version is left behind. Settings aren't in here.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\SimpleScanner"; Filename: "{app}\SimpleScanner.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\SimpleScanner"; Filename: "{app}\SimpleScanner.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\SimpleScanner.exe"; Description: "{cm:LaunchProgram,SimpleScanner}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop SimpleScanner and its radio helpers (/T: the app's own process tree).
Filename: "{sys}\taskkill.exe"; Parameters: "/F /T /IM SimpleScanner.exe"; Flags: runhidden; RunOnceId: "StopApp"
