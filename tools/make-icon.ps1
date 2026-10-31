# Генератор иконки. Раньше рисовал app.ico отдельными командами; теперь
# иконка рисуется тем же кодом, что и знак в интерфейсе (AppIconArt), поэтому
# скрипт оставлен как удобная обёртка над tools/IconGen.
#
#   .\tools\make-icon.ps1              обновить assets\app.ico
#   .\tools\make-icon.ps1 -Preview dir  ещё и PNG-превью всех размеров

param(
    [string]$Preview
)

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$dotnetArgs = @('run', '--project', 'tools\IconGen\IconGen.csproj', '-c', 'Release')
if ($Preview) {
    $dotnetArgs += @('--', '--preview', $Preview)
}

& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "app.ico обновлён. Пересоберите приложение, чтобы иконка попала в exe и трей."
