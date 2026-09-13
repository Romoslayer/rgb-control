#Requires -RunAsAdministrator
# Removes the RGB Control service. Your config in ProgramData is kept.
$ErrorActionPreference = 'Stop'

$name       = 'RgbControl'
$installDir = Join-Path $env:ProgramFiles 'RgbControl'

if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
    Stop-Service -Name $name -Force
    sc.exe delete $name | Out-Null
    Write-Host "Service removed."
}

Get-Process -Name 'RgbControl' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "$installDir*" } |
    Stop-Process -Force

$shortcutPath = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\RGB Control.lnk'
if (Test-Path $shortcutPath) {
    Remove-Item $shortcutPath
    Write-Host "Start Menu shortcut removed."
}

if (Test-Path $installDir) {
    Start-Sleep -Seconds 2
    Remove-Item -Recurse -Force $installDir
    Write-Host "Removed $installDir"
}
