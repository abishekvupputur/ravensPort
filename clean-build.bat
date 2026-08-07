@echo off
setlocal

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
dotnet publish src\RavensPort.App\RavensPort.App.csproj -c Release -f net8.0-windows10.0.19041.0 -r win-x64 -o "%~dp0publish" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --self-contained false
if errorlevel 1 (
    echo First build pass failed - retrying once ^(WPF markup-compile quirk^)...
    dotnet publish src\RavensPort.App\RavensPort.App.csproj -c Release -f net8.0-windows10.0.19041.0 -r win-x64 -o "%~dp0publish" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --self-contained false
    if errorlevel 1 (
        echo Build FAILED.
        exit /b 1
    )
)

echo Build succeeded.

echo Starting RavensPort...
REM TargetFramework includes the Windows SDK version (see Directory.Build.props), so the
REM framework output directory is net8.0-windows10.0.19041.0 rather than net8.0-windows.
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
