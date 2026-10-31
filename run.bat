@echo off
setlocal
cd /d "%~dp0"

if exist "dist\SyncLed.exe" (
    start "" "dist\SyncLed.exe" %*
) else (
    echo dist\SyncLed.exe not found - building with dotnet...
    dotnet run --project "src\SyncLed.Desktop\SyncLed.Desktop.csproj" -c Release -- %*
)
