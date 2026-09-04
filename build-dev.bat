@echo off
setlocal
cd /d "%~dp0"

rem one-shot development loop: build, test, single-file publish (compiler +
rem language server), repackage the extension with the fresh server bundled,
rem install it into VS Code, and check whether the Inno Setup installer needs
rem rebuilding.
rem
rem flags:  --no-test     skip the test suite
rem         --no-install  package the vsix but do not install it
rem         --installer   also rebuild the setup exe with ISCC when needed
rem         --fast        skip tests AND install

set EXITCODE=0
set SKIPTEST=0
set SKIPINSTALL=0
set BUILDINSTALLER=0
for %%a in (%*) do (
    if /i "%%a"=="--no-test" set SKIPTEST=1
    if /i "%%a"=="--no-install" set SKIPINSTALL=1
    if /i "%%a"=="--installer" set BUILDINSTALLER=1
    if /i "%%a"=="--fast" (set SKIPTEST=1& set SKIPINSTALL=1)
)

echo === [1/5] build solution ===
dotnet build src\HSharp.sln -c Release -v q --nologo
if errorlevel 1 goto :fail

if "%SKIPTEST%"=="1" goto :publish
echo === [2/5] tests ===
dotnet test src\HSharp.Tests -c Release -v q --nologo
if errorlevel 1 (
    echo tests failed - retrying once \(the network e2e tests are load-sensitive\)
    dotnet test src\HSharp.Tests -c Release -v q --nologo
    if errorlevel 1 goto :fail
)

:publish
echo === [3/5] single-file publish ===
dotnet publish src\HSharp\compiler -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -v q --nologo
if errorlevel 1 goto :fail
dotnet publish src\HSharp\lsp -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -v q --nologo
if errorlevel 1 goto :fail
if not exist "src\HSharp\lsp\bin\Release\net8.0\win-x64\publish\hsharp-lsp.exe" goto :fail
if not exist "src\HSharp\compiler\bin\Release\net8.0\win-x64\publish\compiler.exe" goto :fail

echo === [4/5] package + install extension ===
copy /y "src\HSharp\lsp\bin\Release\net8.0\win-x64\publish\hsharp-lsp.exe" "vsExtention\server\hsharp-lsp.exe" >nul
if errorlevel 1 goto :fail

for /f "usebackq" %%v in (`powershell -NoProfile -Command "(Get-Content 'vsExtention\package.json' -Raw | ConvertFrom-Json).version"`) do set EXTVER=%%v
pushd vsExtention
call npx --yes @vscode/vsce package --allow-missing-repository
if errorlevel 1 (popd & goto :fail)
popd

set "VSIX=%~dp0vsExtention\hsharp-language-%EXTVER%.vsix"
if not exist "%VSIX%" goto :fail
echo vsix: %VSIX%

if "%SKIPINSTALL%"=="1" (
    echo skipping install ^(--no-install^)
    goto :installer_check
)

rem locate the VS Code CLI as a full path (a quoted plain name would skip
rem PATHEXT resolution and fail). PATH first, then the usual install dirs
set "CODECMD="
for /f "delims=" %%i in ('where code.cmd 2^>nul') do if not defined CODECMD set "CODECMD=%%i"
if not defined CODECMD if exist "%LOCALAPPDATA%\Programs\Microsoft VS Code\bin\code.cmd" set "CODECMD=%LOCALAPPDATA%\Programs\Microsoft VS Code\bin\code.cmd"
if not defined CODECMD if exist "%ProgramFiles%\Microsoft VS Code\bin\code.cmd" set "CODECMD=%ProgramFiles%\Microsoft VS Code\bin\code.cmd"
if not defined CODECMD if exist "%ProgramFiles(x86)%\Microsoft VS Code\bin\code.cmd" set "CODECMD=%ProgramFiles(x86)%\Microsoft VS Code\bin\code.cmd"

if not defined CODECMD (
    echo WARNING: VS Code CLI not found on PATH or in its usual locations.
    echo Install the vsix manually from the VS Code Extensions panel: %VSIX%
    goto :installer_check
)

call "%CODECMD%" --install-extension "%VSIX%" --force
if errorlevel 1 goto :fail
echo installed. RELOAD THE VS CODE WINDOW to run the new version ^(Ctrl+Shift+P -^> "Developer: Reload Window"^)

:installer_check

echo === [5/5] installer check ===
set PSARGS=
if "%BUILDINSTALLER%"=="1" set PSARGS=-Build
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-installer.ps1 %PSARGS%
if errorlevel 1 set EXITCODE=2

echo.
if "%EXITCODE%"=="2" (
    echo DONE with warnings: the installer needs rebuilding ^(see above^).
) else (
    echo DONE. everything is current.
)
exit /b %EXITCODE%

:fail
echo.
echo BUILD FAILED - fix the errors above and re-run build-dev.bat
exit /b 1
