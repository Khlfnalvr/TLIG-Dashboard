; ============================================================
; TLIG Dashboard — Inno Setup Script
; ICO Laboratory
; ============================================================

#define AppName      "TLIG Dashboard"
#define AppVersion   "2.1.1"
#define AppPublisher "ICO Laboratory"
#define AppExe       "TLIGDashboard.exe"

[Setup]
AppId={{B5C6D7E8-2222-3333-4444-555566667777}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=
AppSupportURL=
DefaultDirName={autopf}\TLIGDashboard
DefaultGroupName={#AppName}
AllowNoIcons=yes
LicenseFile=license.txt
OutputDir=Publish
OutputBaseFilename=TLIGDashboardSetup-v2.1.1
SetupIconFile=Assets\logo.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.17763
PrivilegesRequired=admin
VersionInfoVersion=2.1.1.0
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=TLIG Dashboard - Cell voltages, SOC, temperatures and balancing
VersionInfoCopyright=Copyright (C) 2026 ICO Laboratory

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Buat shortcut di &Desktop"; GroupDescription: "Shortcut tambahan:"

[Files]
Source: "Publish\AppFiles\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Jalankan {#AppName} sekarang"; Flags: nowait postinstall skipifsilent
