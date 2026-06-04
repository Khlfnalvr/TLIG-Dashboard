; ============================================================
; TLIG Dashboard Server — Inno Setup Script
; ICO Laboratory
; ============================================================

#define AppName      "TLIG Dashboard Server"
#define AppVersion   "1.0.0-Echo"
#define AppPublisher "ICO Laboratory"
#define AppExe       "TLIGDashboard.Server.exe"

[Setup]
AppId={{A1B2C3D4-1111-2222-3333-444455556661}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=
AppSupportURL=
DefaultDirName={autopf}\TLIGDashboard\Server
DefaultGroupName={#AppName}
AllowNoIcons=yes
OutputDir=publish
OutputBaseFilename=TLIGDashboard-Server-v1.0.0-Echo-Setup
SetupIconFile=Assets\logo.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.17763
PrivilegesRequired=admin
VersionInfoVersion=1.0.0.0
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=TLIG Dashboard Server — broadcasts camera + HMI and hosts the AI proxy
VersionInfoCopyright=Copyright (C) 2026 ICO Laboratory

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Buat shortcut di &Desktop"; GroupDescription: "Shortcut tambahan:"

[Files]
Source: "publish\Server\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Jalankan {#AppName} sekarang"; Flags: nowait postinstall skipifsilent
