; ============================================================
; TLIG Dashboard Client — Inno Setup Script
; ICO Laboratory
; ============================================================

#define AppName      "TLIG Dashboard Client"
#define AppVersion   "1.0.0-beta"
#define AppPublisher "ICO Laboratory"
#define AppExe       "TLIGDashboard.Client.exe"

[Setup]
AppId={{A1B2C3D4-1111-2222-3333-444455556662}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=
AppSupportURL=
DefaultDirName={autopf}\TLIGDashboard\Client
DefaultGroupName={#AppName}
AllowNoIcons=yes
OutputDir=publish
OutputBaseFilename=TLIGDashboard-Client-v1.0.0-beta-Setup
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
VersionInfoDescription=TLIG Dashboard Client — receives camera + HMI and chats via the server AI proxy
VersionInfoCopyright=Copyright (C) 2026 ICO Laboratory

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Buat shortcut di &Desktop"; GroupDescription: "Shortcut tambahan:"

[Files]
Source: "publish\Client\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Jalankan {#AppName} sekarang"; Flags: nowait postinstall skipifsilent
