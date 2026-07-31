@echo off
setlocal
rem Publish a framework-dependent, redistributable build into dist\
set "MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if not exist "%MSBUILD%" (
    echo [ERROR] MSBuild not found. Install Visual Studio with .NET desktop workload.
    exit /b 1
)
cd /d "%~dp0"

echo Publishing ...
"%MSBUILD%" WebDavCC.csproj -t:Publish -p:Configuration=Release -p:Platform=x64 ^
    -p:RuntimeIdentifier=win-x64 -p:PublishDir=dist\ -restore -v:m
if errorlevel 1 (
    echo [ERROR] Publish failed.
    exit /b 1
)

echo.
echo ============================================================
echo   Done. Output is in the dist folder.
echo.
echo   Target machines need these runtimes installed:
echo     1. .NET Desktop Runtime 10.0 (x64)
echo        https://dotnet.microsoft.com/download/dotnet/10.0
echo     2. Windows App Runtime 1.7 (x64)
echo        https://aka.ms/windowsappsdk/1.7/latest/windowsappruntimeinstall-x64.exe
echo.
echo   The app shows a Chinese dialog with download links if either
echo   is missing, so users are not left with a silent crash.
echo   config.json / certs / webdav.log are created next to the exe.
echo ============================================================
dir /b dist\*.exe
