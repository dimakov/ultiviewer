@echo off
rem Usage: build.cmd [build|test|run|publish]   (default: build + test)
setlocal
set "LOCALDOTNET=%LOCALAPPDATA%\Microsoft\dotnet"
if exist "%LOCALDOTNET%\dotnet.exe" (
  set "DOTNET_ROOT=%LOCALDOTNET%"
  set "PATH=%LOCALDOTNET%;%PATH%"
)
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
cd /d "%~dp0"

set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=all"

if /i "%TARGET%"=="publish" goto publish
if /i "%TARGET%"=="run" goto run

dotnet build Ultiviewer.slnx -c Debug -v minimal -nologo || exit /b 1
if /i "%TARGET%"=="build" exit /b 0
dotnet test tests\Ultiviewer.Core.Tests\Ultiviewer.Core.Tests.csproj -c Debug --no-build -nologo --blame-hang-timeout 2m || exit /b 1
exit /b 0

:run
dotnet build src\Ultiviewer.App\Ultiviewer.App.csproj -c Debug -v minimal -nologo || exit /b 1
start "" "src\Ultiviewer.App\bin\Debug\net10.0-windows\win-x64\Ultiviewer.exe" %2 %3 %4
exit /b 0

:publish
dotnet publish src\Ultiviewer.App\Ultiviewer.App.csproj -c Release -o artifacts\publish\Ultiviewer -p:PublishReadyToRun=true -nologo || exit /b 1
powershell -NoProfile -Command "Compress-Archive -Force -Path artifacts\publish\Ultiviewer -DestinationPath artifacts\Ultiviewer-win-x64.zip" || exit /b 1
echo Published to artifacts\Ultiviewer-win-x64.zip
exit /b 0
