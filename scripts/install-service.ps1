#Requires -RunAsAdministrator
# Developer install from source: builds everything and installs it the same way the release installer does.
# Most people should use the installer from GitHub Releases instead.
$ErrorActionPreference = 'Stop'

$root       = Split-Path $PSScriptRoot -Parent
$installDir = Join-Path $env:ProgramFiles 'RgbControl'
$serviceExe = Join-Path $installDir 'RgbControl.Service.exe'
$name       = 'RgbControl'

Get-Process -Name 'RgbControl' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "$installDir*" } |
    Stop-Process -Force

if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
    Write-Host "Stopping existing service..."
    Stop-Service -Name $name -Force
    sc.exe delete $name | Out-Null
    Start-Sleep -Seconds 2
}

& "$PSScriptRoot\publish.ps1" -Output $installDir

& "$installDir\rgbctl.exe" init-config

# Let the (non-admin) app edit the config; the service applies whatever is saved there.
$configDir = Join-Path $env:ProgramData 'RgbControl'
icacls $configDir /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null

Write-Host "Creating Start Menu shortcut..."
$shortcutPath = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\RGB Control.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $installDir 'RgbControl.exe'
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = 'Control motherboard and RAM lighting'
$shortcut.Save()

Write-Host "Registering service..."
New-Service -Name $name `
    -BinaryPathName "`"$serviceExe`"" `
    -DisplayName 'RGB Control' `
    -Description 'Applies motherboard and RAM lighting at boot and turns it off at shutdown and sleep.' `
    -StartupType Automatic | Out-Null

# Restart automatically if it ever crashes.
sc.exe failure $name reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Start-Service -Name $name
Get-Service -Name $name | Format-Table Name, Status, StartType
Write-Host "Config: $env:ProgramData\RgbControl\config.json (changes are applied automatically)"
Write-Host "App:    Start Menu > RGB Control"
Write-Host "CLI:    $installDir\rgbctl.exe"
