@echo off
setlocal
rem This machine's dotnet SDK lacks Microsoft.Build.Packaging.Pri.Tasks.dll,
rem so WinUI3 projects must be built with the MSBuild shipped with Visual Studio.
set "MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if not exist "%MSBUILD%" (
    echo [ERROR] MSBuild not found. Install Visual Studio with .NET desktop workload.
    echo         Expected: %MSBUILD%
    exit /b 1
)
cd /d "%~dp0"
"%MSBUILD%" WebDavCC.csproj -p:Configuration=Release -p:Platform=x64 -restore -v:m %*
