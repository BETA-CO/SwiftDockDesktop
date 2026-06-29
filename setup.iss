; Inno Setup Script for Swift Dock
; Compiler: Inno Setup 6 or newer
; Run 'dotnet publish -c Release -r win-x64 --self-contained' before compiling this script.

#define MyAppName "Swift Dock"
#define MyAppVersion "1.1.1"
#define MyAppPublisher "SwiftDock"
#define MyAppURL "https://github.com/rohit/SwiftDock"
#define MyAppExeName "desktop.exe" ; Set to "SwiftDock.exe" if you add <AssemblyName>SwiftDock</AssemblyName> to your .csproj

[Setup]
; Unique AppId generated specifically for Swift Dock
AppId={{A5C61E8D-6804-4F98-A8A2-3498DF0F5B42}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
; Require admin privileges for installing, registering firewall exceptions, and configuring startup options
PrivilegesRequired=admin
OutputDir=output
OutputBaseFilename=SwiftDockSetup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Run Swift Dock when Windows starts"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Source files should point to the publish output of the WPF project.
Source: "bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Dirs]
; Grant modify permissions to users in the app directory so config.json can be read/written without Admin rights.
Name: "{app}"; Permissions: users-modify

[Run]
; Add Windows Firewall inbound rule for Swift Dock (TCP 19001 & UDP 19002 communication)
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""Swift Dock Server"" dir=in action=allow program=""{app}\{#MyAppExeName}"" enable=yes profile=any"; Flags: runhidden; StatusMsg: "Configuring Windows Firewall exception rule..."
; Optional post-install checkbox to run the app immediately
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the Windows Firewall rule when uninstalling the application
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Swift Dock Server"" program=""{app}\{#MyAppExeName}"""; Flags: runhidden; StatusMsg: "Cleaning up Windows Firewall rules..."
