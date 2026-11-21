; Inno Setup script of the Windows installer - compiled by build/windows/build.ps1:
;   iscc /DAppVersion=0.1.0 /DSourceDir=<publish folder> /DOutputDir=<out> MomentFinder.iss
; With /DSign and /Sartifact=<command> (build.ps1) Inno Setup signs everything it produces.
; Installs for all users into Program Files\MomentFinder (asks for administrator rights).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; Never change the AppId - Windows recognises updates and the uninstaller by it.
AppId={{79852F3D-861E-441D-AD88-C726C523A959}
AppName=MomentFinder
AppVersion={#AppVersion}
AppVerName=MomentFinder {#AppVersion}
AppPublisher=Letsnet s.r.o.
AppCopyright=Copyright (C) 2026 Letsnet s.r.o.
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\MomentFinder
DefaultGroupName=MomentFinder
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\MomentFinder.ico
UninstallDisplayName=MomentFinder
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=MomentFinder-{#AppVersion}-win-x64
; A running MomentFinder is closed before the files are replaced.
CloseApplications=yes
RestartApplications=no
#ifdef Sign
; build.ps1 defines the tool "artifact" (/Sartifact=...). Inno Setup signs the installer, the uninstaller and the setup engine
; that the installer runs from %TEMP% as MomentFinder-<version>-win-x64.tmp - Smart App Control blocks an unsigned one (error 4551).
SignTool=artifact
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

; An update first removes the libraries and tools of the previous version, so that nothing stale (a DLL that a newer
; version no longer ships) is left next to MomentFinder.exe. The folder holds no user data: the library lives in
; %LOCALAPPDATA%\MomentFinder.
[InstallDelete]
Type: files; Name: "{app}\*.dll"
Type: filesandordirs; Name: "{app}\libvlc"
Type: filesandordirs; Name: "{app}\tools"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MomentFinder"; Filename: "{app}\MomentFinder.exe"; IconFilename: "{app}\MomentFinder.ico"
Name: "{autodesktop}\MomentFinder"; Filename: "{app}\MomentFinder.exe"; IconFilename: "{app}\MomentFinder.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\MomentFinder.exe"; Description: "{cm:LaunchProgram,MomentFinder}"; Flags: nowait postinstall skipifsilent
