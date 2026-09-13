# Publishes the app, service and CLI as self-contained win-x64 builds into one folder
# (no .NET install needed on the target PC). Used by the installer and the release workflow.
param(
    [string]$Version = '1.0.0',
    [string]$Output
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
if (-not $Output) { $Output = Join-Path $root 'artifacts\publish' }

if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }

foreach ($project in 'RgbControl.App', 'RgbControl.Service', 'RgbControl.Cli') {
    Write-Host "Publishing $project $Version..."
    dotnet publish "$root\src\$project" -c Release -r win-x64 --self-contained true `
        "-p:Version=$Version" -p:DebugType=none -o $Output --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "$project publish failed" }
}

Write-Host "Published to $Output"
