[CmdletBinding()]
param(
  [int]$Port = 9335
)

$ErrorActionPreference = 'Stop'
$skillRoot = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $PSScriptRoot 'install-dream-skin.ps1'
$stateRoot = Join-Path $env:LOCALAPPDATA 'CodexDreamSkin'

& $installer -Port $Port -NoShortcuts

$engineRoot = Join-Path $stateRoot 'engine'
$background = Join-Path $engineRoot 'assets\account-manager-nebula-orbit.jpg'
$themePath = Join-Path $engineRoot 'assets\account-manager-nebula-theme.json'
. (Join-Path $engineRoot 'scripts\common-windows.ps1')
. (Join-Path $engineRoot 'scripts\theme-windows.ps1')

$theme = (Read-DreamSkinUtf8File -Path $themePath) | ConvertFrom-Json -ErrorAction Stop
$null = Set-DreamSkinActiveTheme `
  -ImagePath $background `
  -Theme $theme `
  -StateRoot $stateRoot

$bundleVersion = (Read-DreamSkinUtf8File -Path (Join-Path $skillRoot 'bundle-version.txt')).Trim()
Write-DreamSkinUtf8FileAtomically `
  -Path (Join-Path $stateRoot 'account-manager-bundle-version.txt') `
  -Content ($bundleVersion + "`r`n")

Write-Host 'Account Manager Nebula theme installed.'
