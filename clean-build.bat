@echo off
setlocal

REM Pass "nosetup" to skip the installer, which is the slow half: a second publish plus about
REM 25 seconds of LZMA2. Worth skipping when the point is only to run the app.
set "BUILD_SETUP=1"
if /i "%~1"=="nosetup" set "BUILD_SETUP="

echo Stopping any running RavensPort.exe...
taskkill /IM RavensPort.exe /F >nul 2>&1

REM taskkill returns before Windows releases the file handles, which makes the
REM clean below fail with "Access is denied". Wait for the process to actually go.
for /l %%i in (1,1,20) do (
    tasklist /FI "IMAGENAME eq RavensPort.exe" 2>nul | find /i "RavensPort.exe" >nul || goto :stopped
    ping -n 2 127.0.0.1 >nul
)
:stopped

echo Cleaning bin/obj...
for %%P in (src\RavensPort.Core src\RavensPort.App tests\RavensPort.Core.Tests) do (
    if exist "%%P\bin" rmdir /s /q "%%P\bin"
    if exist "%%P\obj" rmdir /s /q "%%P\obj"
)

REM WPF markup compilation runs through a temporary *_wpftmp project. On a freshly wiped
REM obj/ it intermittently fails to hand the generated *.g.cs files to the main compile,
REM producing bogus errors ("CS2001: MainWindow.g.cs could not be found", or "CS5001: no
REM static Main"). -m:1 (no parallel MSBuild) makes it much rarer but does NOT eliminate it;
REM the generated files exist by the second pass, so retry once before declaring failure.
echo Building Go DLL...
pushd "%~dp0src\OnePasswordNative"
set CGO_ENABLED=1
go build -buildmode=c-shared -o onepassword.dll main.go
if errorlevel 1 (
    echo Go build FAILED.
    popd
    exit /b 1
)
popd

echo Running tests...
dotnet test tests\RavensPort.Core.Tests\RavensPort.Core.Tests.csproj -c Release
if errorlevel 1 (
    echo Tests FAILED.
    pause
    exit /b 1
)

echo Building Release Single Exe...
dotnet publish src\RavensPort.App\RavensPort.App.csproj -c Release -r win-x64 -o "%~dp0publish" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --self-contained false
if errorlevel 1 (
    echo First build pass failed - retrying once ^(WPF markup-compile quirk^)...
    dotnet publish src\RavensPort.App\RavensPort.App.csproj -c Release -r win-x64 -o "%~dp0publish" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --self-contained false
    if errorlevel 1 (
        echo Build FAILED.
        exit /b 1
    )
)

echo Build succeeded.

if not defined BUILD_SETUP goto :runapp

REM The installer wraps the *self-contained* publish, not the framework-dependent one built
REM above: a downloader has no .NET runtime to depend on. Different output directory, so the
REM two do not fight, and installer\build.ps1 refuses a store-flavoured payload for us.
echo.
echo Publishing self-contained payload for the installer...
dotnet publish src\RavensPort.App\RavensPort.App.csproj -p:PublishProfile=win-x64-selfcontained -c Release
if errorlevel 1 (
    echo First self-contained pass failed - retrying once ^(WPF markup-compile quirk^)...
    dotnet publish src\RavensPort.App\RavensPort.App.csproj -p:PublishProfile=win-x64-selfcontained -c Release
    if errorlevel 1 (
        echo Self-contained publish FAILED.
        exit /b 1
    )
)

REM That publish rewrites the lock files: the App's gains win-x64 and ILLink entries, and the
REM Core's comes back with different line endings. Committing the first breaks CI, which
REM restores with --locked-mode, and both are easy to sweep up with "git add -A" without
REM noticing. Put them back.
git checkout -- src\RavensPort.App\packages.lock.json src\RavensPort.Core\packages.lock.json >nul 2>&1

REM One place declares the version, and Inno rejects a leading "v". Several PropertyGroups
REM means .Version comes back as an array in PowerShell, hence the filter.
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "[xml]$x = Get-Content '%~dp0Directory.Build.props'; @($x.Project.PropertyGroup.Version | Where-Object { $_ })[0]"`) do set "APP_VERSION=%%V"

if not defined APP_VERSION (
    echo Could not read ^<Version^> from Directory.Build.props - skipping the installer.
    goto :runapp
)

echo.
echo Building installer for %APP_VERSION%...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\build.ps1" -Version "%APP_VERSION%" -SourceExe "src\RavensPort.App\bin\Release\net10.0-windows\publish\win-x64\RavensPort.exe"
if errorlevel 1 (
    echo Installer build FAILED. If Inno Setup is missing, install it with:
    echo     winget install JRSoftware.InnoSetup
    exit /b 1
)

echo Installer written to dist\RavensPort-Setup-%APP_VERSION%.exe

:runapp
echo.
echo Starting RavensPort...
REM TargetFramework includes the Windows SDK version (see Directory.Build.props), so the
REM framework output directory is net10.0-windows10.0.19041.0 rather than net10.0-windows.
set "APP_EXE=%~dp0publish\RavensPort.exe"
if not exist "%APP_EXE%" (
    echo Build succeeded, but the application executable was not found:
    echo   %APP_EXE%
    exit /b 1
)
start "" "%APP_EXE%"

echo Done - app running in tray.
pause
endlocal
