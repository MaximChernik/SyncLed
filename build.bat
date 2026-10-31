@echo off
setlocal
cd /d "%~dp0"

echo Generating app.ico from AppIconArt...
dotnet run --project "tools\IconGen\IconGen.csproj" -c Release
if errorlevel 1 goto :fail

echo.
echo Building SyncLed Avalonia...
dotnet publish "src\SyncLed.Desktop\SyncLed.Desktop.csproj" -c Release -o dist
if errorlevel 1 goto :fail

echo.
echo Done: %~dp0dist\SyncLed.exe
pause
exit /b 0

:fail
echo.
echo Build failed.
pause
exit /b 1
