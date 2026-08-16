; RavensPort installer.
;
; Why this exists at all: the Microsoft Store submission is the EXE/MSI type, and what was
; submitted before was the application itself. Running it starts a tray app; it does not install
; anything, so there was no Add or Remove Programs entry (Store policy 10.2.7), no Start menu
; shortcut and therefore no way to launch the app again after the tray menu's Exit (10.1.2.10),
; and the Store's own install step had nothing to detect (10.3.4). One missing installer, three
; findings.
;
; Per-user by design. PrivilegesRequired=lowest installs under the user's profile and writes the
; uninstall entry to HKCU, which means no elevation prompt — and a silent install that cannot
; raise UAC is a silent install that cannot fail on it. RavensPort has nothing that needs machine
; scope: its configuration lives in the user's password manager, and its session key is already
; bound to the Windows account.
;
; Build:  ISCC.exe installer\RavensPort.iss /DAppVersion=4.1.5
; The publish step must have run first — see SourceExe below.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "RavensPort"
#define AppPublisher "Abishek Narasimhan"
#define AppUrl "https://github.com/abishekvupputur/ravensPort"
#define AppExeName "RavensPort.exe"

; Overridable so the payload location can be pointed elsewhere without editing this file.
#ifndef SourceExe
  #define SourceExe "..\src\RavensPort.App\bin\Release\net10.0-windows\publish\win-x64\RavensPort.exe"
#endif

; Also overridable, so the PR build can prove this script still compiles in seconds rather than
; spending 80 of them on LZMA2 to produce an installer nobody will ever run. Releases must use the
; default -- see the Compression note below for why the size matters.
#ifndef CompressionMode
  #define CompressionMode "lzma2/max"
#endif

[Setup]
; Never change AppId. It is what lets a later version recognise, and replace, an existing
; install rather than sitting beside it as a second entry.
AppId={{C47DF74F-150F-4AD3-9B12-46A8BF02BE9C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; The name the reviewer looks for in Add or Remove Programs.
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
; "x64compatible" and not "x64": the latter is deprecated and now resolves to "x64os", meaning
; x64 hardware only, which refuses to install on an ARM64 Windows device even though the payload
; runs there perfectly well under emulation. Recent Surface Laptops are ARM64 -- and a Surface
; Laptop is the machine Store certification tested on -- so that distinction is the difference
; between passing and failing 10.3.4 again.
;
; Guarded because "x64compatible" is a hard error before Inno 6.3, which some build images still
; carry. The fallback narrows the audience rather than failing the build.
#if VER >= EncodeVer(6,3,0)
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
#endif

; Matches SupportedOSPlatformVersion in Directory.Build.props: Windows 10 1809.
MinVersion=10.0.17763

OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\src\RavensPort.App\Assets\tray.ico
LicenseFile=..\LICENSE

; The payload must be published with EnableCompressionInSingleFile=false and compressed here
; instead. Measured: the already-deflated 99 MB exe gives 101.6 MB at Compression=none and 94 MB
; at lzma2/max, while the raw 243 MB payload gives 67.9 MB. The limit that matters is GitHub's
; 100 MB per file -- the Store submission is served from a blob in dist/, because it needs a
; redirect-free raw.githubusercontent.com URL -- so the first is unusable and the second leaves
; no room to grow. Compressing the raw payload also drops the runtime self-extraction step.
Compression={#CompressionMode}
SolidCompression=yes

WizardStyle=modern
DisableProgramGroupPage=yes
DisableDirPage=auto

; Setup itself must not need a console or a restart; the Store runs it unattended.
RestartIfNeededByRun=no
CloseApplications=no

; A running copy is detected in [Code] rather than by AppMutex here. Both notice the same mutex and
; both refuse to overwrite a locked exe; the difference is the exit code. AppMutex checks before
; Setup has properly started and aborts with 1, "Setup failed to initialize" -- which is also what a
; corrupt download or the wrong architecture returns, so it cannot be told apart from those. The
; PrepareToInstall check below aborts with 7 instead, a code nothing else in Setup produces, so
; winget can map it to "close the application and try again". See the [Code] section.
;
; Deliberately not CloseApplications=force: exiting RavensPort can prompt about vault changes
; that exist only in memory, and a forced kill would discard them silently.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; The Start menu entry. This is the "clear method to launch the product" that was missing.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; skipifsilent, so the Store's unattended install does not leave a window on the reviewer's
; desktop — and so the install step ends when the installer does, rather than when the app is
; closed. nowait for the same reason.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// Pascal Script, so comments here are // and not the ; used by the sections above.
//
// Replaces AppMutex. Same mutex, same refusal, but an exit code that says why -- see the note in
// [Setup]. The name is App.xaml.cs's SingleInstanceMutexName; the two have to stay in step, because
// nothing checks them against each other at build time.
const
  AppMutexName = 'RavensPort_SingleInstance';
  RunningMessage =
    'RavensPort is running. Right-click its tray icon, choose Exit, then run this installer again.';

// Interactive runs are told at once, instead of after clicking through the whole wizard only to be
// turned away at the last step. Retry loops, so closing the app and clicking Retry carries on.
//
// Guarded by WizardSilent because MsgBox ignores /SUPPRESSMSGBOXES -- an unattended install would
// sit on this dialog forever. Silent runs fall through to PrepareToInstall.
function InitializeSetup(): Boolean;
begin
  Result := True;
  if WizardSilent then
    Exit;
  while CheckForMutexes(AppMutexName) do
    if MsgBox(RunningMessage, mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
end;

// A non-empty result here aborts before any file is touched, with exit code 7. Measured, not
// assumed: 7 with the mutex held, 0 without, for /VERYSILENT /SUPPRESSMSGBOXES /NORESTART, which is
// what winget passes. packaging/winget maps 7 to packageInUse so `winget upgrade` prints the reason
// rather than a bare failure.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if CheckForMutexes(AppMutexName) then
    Result := RunningMessage
  else
    Result := '';
end;

// AppMutex covered the uninstaller too, so this keeps that half. Suppressible, because a silent
// uninstall has no one to answer the box: it is then taken as Cancel and the uninstall stops with
// the app still installed, rather than deleting an exe that is running.
function InitializeUninstall(): Boolean;
begin
  Result := True;
  while CheckForMutexes(AppMutexName) do
    if SuppressibleMsgBox(RunningMessage, mbError, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
end;
