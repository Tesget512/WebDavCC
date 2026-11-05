@echo off
setlocal
rem Build into bin\.
rem Prefer the MSBuild shipped with Visual Studio; fall back to the dotnet CLI.
rem NOTE: the RuntimeIdentifier is required - without it the native
rem Microsoft.WindowsAppRuntime.Bootstrap.dll is not copied and the exe
rem cannot start ("missing runtime" dialog).
set "MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
cd /d "%~dp0"

if exist "%MSBUILD%" (
    "%MSBUILD%" WebDavCC.csproj -p:Configuration=Release -p:Platform=x64 ^
        -p:RuntimeIdentifier=win-x64 -restore -v:m %*
    goto :eof
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
dotnet build WebDavCC.csproj -c Release -p:Platform=x64 ^
    -p:RuntimeIdentifier=win-x64 -v:m %*
