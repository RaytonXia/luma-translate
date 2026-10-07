#define AppVersion "1.1.1"
[Setup]
AppId={{2EB9B990-70B5-4F87-ACB0-763F004BD1AD}
AppName=Luma Translate
AppVersion={#AppVersion}
AppPublisher=Luma Translate
DefaultDirName={localappdata}\Programs\Luma Translate
DefaultGroupName=Luma Translate
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=Luma-Translate-Windows-1.1.1-Setup
SetupIconFile=..\assets\luma-logo.ico
UninstallDisplayIcon={app}\LumaTranslate.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
AppMutex=Local\SGFloatingTranslator_7F0B9C12
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
[Files]
Source: "..\LumaTranslate.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs
Source: "..\INSTALL.txt"; DestDir: "{app}"; Flags: ignoreversion
[Icons]
Name: "{group}\Luma Translate"; Filename: "{app}\LumaTranslate.exe"; Parameters: "--show"
Name: "{autodesktop}\Luma Translate"; Filename: "{app}\LumaTranslate.exe"; Parameters: "--show"; Tasks: desktopicon
[Run]
Filename: "{app}\LumaTranslate.exe"; Parameters: "--show"; Description: "Open Luma Translate"; Flags: nowait postinstall skipifsilent
[Code]
function InitializeSetup(): Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    MsgBox('Luma Translate requires Microsoft .NET Framework 4.8. Please install it from Microsoft, then run Setup again.', mbError, MB_OK);
end;
