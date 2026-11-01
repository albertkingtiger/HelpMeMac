; Built by scripts/build-installer.ps1, which supplies Bundle, OutputFolder and AppVersion.
#define AppName "Help Me!"

[Setup]
AppId={{6E0C4F52-8A7B-4D3E-9C21-5B7F1A2D9E44}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Help Me!
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\HelpMe.exe
; Per-user install: no elevation, matching the per-user startup entry and settings.
PrivilegesRequired=lowest
DefaultDirName={autopf}\HelpMe
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputFolder}
OutputBaseFilename=HelpMe-Setup-{#AppVersion}
; The models barely compress, so solid mode would cost time for little size.
Compression=lzma2/normal
SolidCompression=no
LZMAUseSeparateProcess=yes
; Files in use cannot be replaced, so the running app must be closed first.
AppMutex=Local\HelpMe.SingleInstance
SetupLogging=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#Bundle}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\HelpMe.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\HelpMe.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; The app writes this value itself; the entry exists so uninstalling removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "HelpMe"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\HelpMe.exe"; WorkingDir: "{app}"; Description: "Start Help Me! now (it opens in the system tray)"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Python writes bytecode caches after install, which would otherwise be left behind.
Type: filesandordirs; Name: "{app}\python"
Type: filesandordirs; Name: "{app}\translation"
Type: files; Name: "{app}\user-settings.json"
