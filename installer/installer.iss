; H# compiler installer for Windows. builds hsc from the single-file publish
; output and adds it to PATH so "hsc program.hs -o program.exe" works in any
; terminal.
;
; build with:
;   ISCC.exe installer\installer.iss
; expected inputs (relative to this file):
;   ..\src\HSharp\compiler\bin\Release\net8.0\win-x64\publish\compiler.exe
;   ..\src\HSharp\compiler\bin\Release\net8.0\win-x64\publish\LLVM-C.dll
;   ..\src\HSharp\compiler\bin\Release\net8.0\win-x64\publish\rt.c
;
; the linux-x64 publish is distributed separately (a Windows installer cannot
; run on Linux); zip or tar that publish folder as its own artifact

#define AppName "H# Compiler"
#define AppVersion "0.7.3"
#define AppId "{{8E4F2C1A-6B7D-4E93-9C55-A3F1D2E4B5C6}"
#define PubDir "..\src\HSharp\compiler\bin\Release\net8.0"
#define LspDir "..\src\HSharp\lsp\bin\Release\net8.0"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={autopf}\HSharp
PrivilegesRequired=lowest
ChangesEnvironment=yes
OutputDir=.
OutputBaseFileName=HSharp-Compiler-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\hsc.exe

[Files]
Source: "{#PubDir}\win-x64\publish\compiler.exe"; DestName: "hsc.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PubDir}\win-x64\publish\LLVM-C.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PubDir}\win-x64\publish\rt.c"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#LspDir}\win-x64\publish\hsharp-lsp.exe"; DestName: "hsharp-lsp.exe"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Check: NeedsAddPath('{app}')

[Code]
function NeedsAddPath(Param: string): Boolean;
var
  OrigPath: string;
  AppDir: string;
begin
  AppDir := ExpandConstant(Param);
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(AppDir) + ';', ';' + Uppercase(OrigPath) + ';') = 0;
end;

// searches every directory in PATH for exeName, plus the default LLVM
// install locations
function FindOnPath(const ExeName: string): Boolean;
var
  Rest, Dir: string;
  P: Integer;
begin
  Result := False;

  Rest := ExpandConstant('{%PATH}');
  while True do
  begin
    P := Pos(';', Rest);
    if P = 0 then
      Dir := Rest
    else
      Dir := Copy(Rest, 1, P - 1);
    if (Dir <> '') and (Copy(Dir, Length(Dir), 1) <> '\') then
      Dir := Dir + '\';
    if FileExists(Dir + ExeName) then
    begin
      Result := True;
      exit;
    end;
    if P = 0 then
      break;
    Rest := Copy(Rest, P + 1, MaxInt);
  end;

  if not Result then
    Result := FileExists(ExpandConstant('{pf}\LLVM\bin\') + ExeName);
end;

function HasClang(): Boolean;
begin
  Result := FindOnPath('clang.exe');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Msg, NL: string;
begin
  Result := True;

  if CurPageID = wpReady then
  begin
    if not HasClang() then
    begin
      NL := #13#10;
      Msg :=
        'clang.exe was not found on this computer.' + NL +
        NL +
        'H# needs clang (from LLVM 18) to link compiled programs, and on ' +
        'Windows linking also uses the MSVC / Windows SDK libraries that ' +
        'come with Visual Studio or Build Tools.' + NL +
        NL +
        'The LLVM-C.dll library itself ships with this installer, so only ' +
        'clang is missing.' + NL +
        NL +
        'Install LLVM 18 (winget install LLVM.LLVM, or from ' +
        'https://releases.llvm.org) and Visual Studio Build Tools, then ' +
        're-run this installer.' + NL +
        NL +
        'Install H# anyway? (it installs fine, but building will fail until ' +
        'clang is available)';
      if MsgBox(Msg, mbConfirmation, MB_YESNO) = IDNO then
        Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    MsgBox('H# installed as "hsc". Open a NEW terminal so the updated PATH takes effect, then run: hsc program.hs -o program.exe',
      mbInformation, MB_OK);
end;

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
