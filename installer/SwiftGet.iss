; SwiftGet installer — build with build.ps1, or: ISCC.exe /DAppVersion=1.0.0 installer\SwiftGet.iss
#ifndef AppVersion
  #define AppVersion "2.0.3"
#endif
#define AppName "SwiftGet"
#define AppExe "SwiftGet.exe"
#define AppUrl "https://github.com/EmadNajafi/SwiftGet-Download-Manager"
; Store IDs of the browser extension. When set, the installer registers the extension and the
; browser offers to turn it on at its next start (same values as BrowserIntegration.cs).
#define ChromeStoreId ""
#define EdgeStoreId ""

[Setup]
AppId={{7C2B6E14-5D3A-4F7B-9E61-2A8C4D0B9F35}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=SwiftGet contributors
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installs for the current user without admin rights; the user can choose "all users" instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=SwiftGet-Setup-{#AppVersion}
SetupIconFile=..\SwiftGet\Assets\SwiftGet.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=wizard-large.bmp,wizard-large-200.bmp
WizardSmallImageFile=wizard-small.bmp,wizard-small-200.bmp
Compression=lzma2/ultra64
SolidCompression=yes
LicenseFile=..\LICENSE
AppMutex=SwiftGet.SingleInstance
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "Start SwiftGet when Windows starts"; GroupDescription: "Options:"

[Files]
Source: "..\publish\app\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\app\*"; DestDir: "{app}"; Excludes: "{#AppExe},*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SwiftGet"; \
    ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: startup; Flags: uninsdeletevalue
; tells SwiftGet what was chosen here, so its first run doesn't override it
Root: HKCU; Subkey: "Software\SwiftGet"; ValueType: dword; ValueName: "AutostartChoice"; ValueData: 1; Tasks: startup; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\SwiftGet"; ValueType: dword; ValueName: "AutostartChoice"; ValueData: 0; Tasks: not startup; Flags: uninsdeletekey


#if ChromeStoreId != ""
Root: HKCU; Subkey: "Software\Google\Chrome\Extensions\{#ChromeStoreId}"; ValueType: string; ValueName: "update_url"; \
    ValueData: "https://clients2.google.com/service/update2/crx"; Flags: uninsdeletekey
#endif
#if EdgeStoreId != ""
Root: HKCU; Subkey: "Software\Microsoft\Edge\Extensions\{#EdgeStoreId}"; ValueType: string; ValueName: "update_url"; \
    ValueData: "https://edge.microsoft.com/extensionwebstorebase/v1/crx"; Flags: uninsdeletekey
#endif

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Parameters: "--browser-setup"; Description: "Set up the browser extension (Chrome, Edge, Brave, Opera)"; \
    Flags: nowait postinstall skipifsilent unchecked


; after a silent update started from SwiftGet (/UPDATE=1), start it again
Filename: "{app}\{#AppExe}"; Parameters: "--minimized"; Flags: nowait; Check: IsUpdate

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;