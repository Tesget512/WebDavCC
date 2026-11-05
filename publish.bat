@echo off
setlocal
rem Publish a framework-dependent, redistributable build into dist\.
rem Prefer the MSBuild shipped with Visual Studio; fall back to the dotnet CLI.
rem NOTE: -p:RuntimeIdentifier=win-x64 is essential - without it the native
rem Microsoft.WindowsAppRuntime.Bootstrap.dll is missing from the output and
rem the app cannot start on target machines.
set "MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
cd /d "%~dp0"

if exist "%MSBUILD%" (
    "%MSBUILD%" WebDavCC.csproj -t:Publish -p:Configuration=Release -p:Platform=x64 ^
        -p:RuntimeIdentifier=win-x64 -p:PublishDir=dist\ -restore -v:m
    goto :check
)

where dotnet >nul 2>nul
if errorlevel 1 (
    if exist "%LOCALAPPDATA%\dotnet\dotnet.exe" (
        set "PATH=%LOCALAPPDATA%\dotnet;%PATH%"
    ) else (
        echo [ERROR] Neither Visual Studio MSBuild nor the dotnet SDK was found.
        echo         Install the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0
        exit /b 1
    )
)
dotnet publish WebDavCC.csproj -c Release -p:Platform=x64 ^
    -p:RuntimeIdentifier=win-x64 -p:PublishDir=dist\ -v:m

:check
if errorlevel 1 (
    echo [ERROR] Publish failed.
    exit /b 1
)

echo.
echo ============================================================
echo   Done. Output is in the dist folder.
echo.
echo   Sanity check: dist\ must contain
echo   Microsoft.WindowsAppRuntime.Bootstrap.dll (native, not .Net).
echo   Target machines need these runtimes installed:
echo     1. .NET Desktop Runtime 10.0 (x64)
echo        https://dotnet.microsoft.com/download/dotnet/10.0
echo     2. Windows App Runtime 1.7 (x64)
echo        https://aka.ms/windowsappsdk/1.7/latest/windowsappruntimeinstall-x64.exe
echo.
echo   config.json / certs / webdav.log are created next to the exe.
echo ============================================================
dir /b dist\*.exe
