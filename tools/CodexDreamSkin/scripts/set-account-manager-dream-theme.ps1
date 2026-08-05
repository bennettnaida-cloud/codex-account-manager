[CmdletBinding()]
param(
  [ValidateSet('manager-light', 'manager-porcelain-light', 'manager-dark', 'manager-nebula-dark', 'preset-arina-hashimoto', 'preset-gothic-void-crusade', 'preset-midnight-aurora', 'preset-sakura-dawn', 'preset-amber-dusk', 'preset-forest-mist', 'preset-cyber-neon', 'custom')]
  [string]$PresetId = 'preset-midnight-aurora',
  [string]$CustomThemePath,
  [string]$StateRoot
)

$ErrorActionPreference = 'Stop'
$skillRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'common-windows.ps1')
. (Join-Path $PSScriptRoot 'theme-windows.ps1')

$stateRoot = if ([string]::IsNullOrWhiteSpace($StateRoot)) {
  Join-Path $env:LOCALAPPDATA 'CodexDreamSkin'
} else {
  [System.IO.Path]::GetFullPath($StateRoot)
}
$assetRoot = Join-Path $skillRoot 'assets'
$null = Initialize-DreamSkinThemeStore -SkillRoot $skillRoot -StateRoot $stateRoot

function ConvertFrom-DreamSkinHexColor {
  param([Parameter(Mandatory = $true)][string]$Value)
  if ($Value -notmatch '^#[0-9A-Fa-f]{6}$') {
    throw "Theme color must use #RRGGBB format: $Value"
  }
  return [pscustomobject]@{
    R = [Convert]::ToInt32($Value.Substring(1, 2), 16)
    G = [Convert]::ToInt32($Value.Substring(3, 2), 16)
    B = [Convert]::ToInt32($Value.Substring(5, 2), 16)
  }
}

function Mix-DreamSkinHexColor {
  param(
    [Parameter(Mandatory = $true)][string]$From,
    [Parameter(Mandatory = $true)][string]$To,
    [Parameter(Mandatory = $true)][double]$Amount
  )
  $left = ConvertFrom-DreamSkinHexColor -Value $From
  $right = ConvertFrom-DreamSkinHexColor -Value $To
  $ratio = [Math]::Max(0, [Math]::Min(1, $Amount))
  $red = [int][Math]::Round($left.R + (($right.R - $left.R) * $ratio))
  $green = [int][Math]::Round($left.G + (($right.G - $left.G) * $ratio))
  $blue = [int][Math]::Round($left.B + (($right.B - $left.B) * $ratio))
  return ('#{0:X2}{1:X2}{2:X2}' -f $red, $green, $blue)
}

function New-DreamSkinRgbaColor {
  param(
    [Parameter(Mandatory = $true)][string]$Color,
    [Parameter(Mandatory = $true)][double]$Alpha
  )
  $rgb = ConvertFrom-DreamSkinHexColor -Value $Color
  $normalizedAlpha = [Math]::Max(0, [Math]::Min(1, $Alpha)).ToString(
    '0.00',
    [System.Globalization.CultureInfo]::InvariantCulture)
  return "rgba($($rgb.R), $($rgb.G), $($rgb.B), $normalizedAlpha)"
}

function Get-AccountManagerDreamTheme {
  param([Parameter(Mandatory = $true)][string]$Id)
  switch ($Id) {
    'manager-light' {
      return [pscustomobject]@{
        Name = '极光浅色'; Appearance = 'light'; Contrast = 86
        Colors = [pscustomobject]@{
          background = '#FBFCFF'; panel = '#FFFFFF'; panelAlt = '#F7F9FF'
          accent = '#4D8DFF'; accentAlt = '#73A8FF'; secondary = '#8B5CF6'
          highlight = '#22B8CF'; text = '#172033'; muted = '#667085'
          line = 'rgba(77, 141, 255, 0.26)'
        }
      }
    }
    'manager-porcelain-light' {
      return [pscustomobject]@{
        Name = '青瓷浅色'; Appearance = 'light'; Contrast = 88
        Colors = [pscustomobject]@{
          background = '#EDF6F3'; panel = '#F5FAF8'; panelAlt = '#FFFFFF'
          accent = '#4E8F84'; accentAlt = '#75B7A9'; secondary = '#7397A4'
          highlight = '#C2A468'; text = '#183C39'; muted = '#617D78'
          line = 'rgba(78, 143, 132, 0.28)'
        }
      }
    }
    'manager-dark' {
      return [pscustomobject]@{
        Name = '深海夜色'; Appearance = 'dark'; Contrast = 92
        Colors = [pscustomobject]@{
          background = '#07101E'; panel = '#091526'; panelAlt = '#12243B'
          accent = '#60A5FA'; accentAlt = '#83BCFF'; secondary = '#A78BFA'
          highlight = '#22D3EE'; text = '#F1F6FF'; muted = '#9CB0C9'
          line = 'rgba(96, 165, 250, 0.30)'
        }
      }
    }
    'manager-nebula-dark' {
      return [pscustomobject]@{
        Name = '星云夜色'; Appearance = 'dark'; Contrast = 94
        Colors = [pscustomobject]@{
          background = '#0B0716'; panel = '#171229'; panelAlt = '#21183A'
          accent = '#B49AFF'; accentAlt = '#C084FC'; secondary = '#F472B6'
          highlight = '#22D3EE'; text = '#FCFAFF'; muted = '#B9ADCE'
          line = 'rgba(180, 154, 255, 0.32)'
        }
      }
    }
    default { return $null }
  }
}

function Get-BundledPresetRuntimeProfile {
  param([Parameter(Mandatory = $true)][string]$Id)
  switch ($Id) {
    'preset-arina-hashimoto' { return [pscustomobject]@{ Appearance = 'light'; FocusX = 0.72; FocusY = 0.45 } }
    'preset-gothic-void-crusade' { return [pscustomobject]@{ Appearance = 'dark'; FocusX = 0.76; FocusY = 0.45 } }
    'preset-midnight-aurora' { return [pscustomobject]@{ Appearance = 'dark'; FocusX = 0.72; FocusY = 0.38 } }
    'preset-sakura-dawn' { return [pscustomobject]@{ Appearance = 'light'; FocusX = 0.68; FocusY = 0.40 } }
    'preset-amber-dusk' { return [pscustomobject]@{ Appearance = 'dark'; FocusX = 0.74; FocusY = 0.42 } }
    'preset-forest-mist' { return [pscustomobject]@{ Appearance = 'dark'; FocusX = 0.70; FocusY = 0.40 } }
    'preset-cyber-neon' { return [pscustomobject]@{ Appearance = 'dark'; FocusX = 0.72; FocusY = 0.38 } }
    default { return $null }
  }
}

if ($PresetId -eq 'custom') {
  if ([string]::IsNullOrWhiteSpace($CustomThemePath) -or
      -not (Test-Path -LiteralPath $CustomThemePath -PathType Leaf)) {
    throw 'Save the custom Codex theme before applying it.'
  }
  try {
    $custom = (Read-DreamSkinUtf8File -Path ([System.IO.Path]::GetFullPath($CustomThemePath))) |
      ConvertFrom-Json -ErrorAction Stop
  } catch {
    throw "The custom Codex theme file is invalid: $($_.Exception.Message)"
  }

  $imagePath = [string]$custom.BackgroundImagePath
  if ([string]::IsNullOrWhiteSpace($imagePath)) {
    $imagePath = Join-Path $assetRoot 'presets\preset-midnight-aurora\background.jpg'
  }
  $accent = [string]$custom.AccentColor
  $surface = [string]$custom.SurfaceColor
  $ink = [string]$custom.InkColor
  if ($accent -notmatch '^#[0-9A-Fa-f]{6}$' -or
      $surface -notmatch '^#[0-9A-Fa-f]{6}$' -or
      $ink -notmatch '^#[0-9A-Fa-f]{6}$') {
    throw 'Custom Codex theme colors must use #RRGGBB format.'
  }
  $contrast = [int]$custom.Contrast
  if ($contrast -lt 70 -or $contrast -gt 100) {
    throw 'Custom Codex theme contrast must be between 70 and 100.'
  }
  $themeName = [string]$custom.Name
  if ([string]::IsNullOrWhiteSpace($themeName)) { $themeName = 'My Theme' }
  $isDark = [bool]$custom.IsDark
  $appearance = if ($isDark) { 'dark' } else { 'light' }
  $panelAlt = if ($isDark) {
    Mix-DreamSkinHexColor -From $surface -To '#FFFFFF' -Amount 0.08
  } else {
    Mix-DreamSkinHexColor -From $surface -To '#000000' -Amount 0.04
  }
  $background = if ($isDark) {
    Mix-DreamSkinHexColor -From $surface -To '#000000' -Amount 0.20
  } else {
    Mix-DreamSkinHexColor -From $surface -To '#FFFFFF' -Amount 0.16
  }
  $accentAlt = Mix-DreamSkinHexColor -From $accent -To $(if ($isDark) { '#FFFFFF' } else { '#000000' }) `
    -Amount $(if ($isDark) { 0.22 } else { 0.10 })
  $secondary = Mix-DreamSkinHexColor -From $accent -To $ink -Amount 0.24
  $muted = Mix-DreamSkinHexColor -From $ink -To $surface -Amount (0.22 + ((100 - $contrast) / 100.0))
  $line = New-DreamSkinRgbaColor -Color $accent -Alpha (0.18 + (($contrast - 70) / 100.0))
  $theme = [pscustomobject]@{
    schemaVersion = 1
    id = 'custom'
    name = $themeName
    brandSubtitle = 'CUSTOM CODEX THEME'
    tagline = 'Make your own photo part of the workspace.'
    projectPrefix = 'Project - '
    projectLabel = 'Choose project'
    statusText = 'CUSTOM THEME ONLINE'
    quote = 'MAKE IT YOURS'
    appearance = $appearance
    contrast = $contrast
    art = [pscustomobject]@{ focusX = 0.5; focusY = 0.5; safeArea = 'auto'; taskMode = 'ambient' }
    palette = [pscustomobject]@{
      accent = $accent
      surface = $surface
      ink = $ink
      contrast = $contrast
    }
    colors = [pscustomobject]@{
      background = $background
      panel = $surface
      panelAlt = $panelAlt
      accent = $accent
      accentAlt = $accentAlt
      secondary = $secondary
      highlight = $accentAlt
      text = $ink
      muted = $muted
      line = $line
    }
  }
} elseif ($PresetId -like 'manager-*') {
  $definition = Get-AccountManagerDreamTheme -Id $PresetId
  if ($null -eq $definition) { throw "Unsupported Account Manager image theme: $PresetId" }
  $managerArtwork = switch ($PresetId) {
    'manager-light' { 'account-manager-aurora-light.jpg' }
    'manager-porcelain-light' { 'account-manager-porcelain-light.jpg' }
    'manager-dark' { 'account-manager-deep-sea.jpg' }
    'manager-nebula-dark' { 'account-manager-nebula-orbit.jpg' }
    default { throw "Missing bundled Account Manager artwork mapping: $PresetId" }
  }
  $imagePath = Join-Path $assetRoot $managerArtwork
  $theme = [pscustomobject]@{
    schemaVersion = 1
    id = $PresetId
    name = $definition.Name
    brandSubtitle = 'CODEX ACCOUNT MANAGER'
    tagline = '把 Account Manager 的工作台风格延伸到 Codex。'
    projectPrefix = '选择项目 · '
    projectLabel = '选择项目'
    statusText = 'ACCOUNT MANAGER THEME ONLINE'
    quote = 'FOCUS ON THE WORK'
    appearance = $definition.Appearance
    contrast = $definition.Contrast
    art = [pscustomobject]@{
      focusX = 0.76
      focusY = 0.435
      safeArea = 'left'
      taskMode = 'ambient'
    }
    palette = [pscustomobject]@{
      accent = $definition.Colors.accent
      surface = $definition.Colors.panel
      ink = $definition.Colors.text
      contrast = $definition.Contrast
    }
    colors = $definition.Colors
  }
} else {
  $themeDirectory = Join-Path $assetRoot ('presets\' + $PresetId)
  if (-not (Test-Path -LiteralPath $themeDirectory -PathType Container)) {
    throw "Bundled Codex theme pack is missing: $PresetId"
  }
  $loaded = Read-DreamSkinTheme -ThemeDirectory $themeDirectory
  if ("$($loaded.Theme.id)" -cne $PresetId) {
    throw "Bundled Codex theme id does not match its directory: $PresetId"
  }
  $imagePath = $loaded.ImagePath
  $theme = $loaded.Theme | ConvertTo-Json -Depth 8 | ConvertFrom-Json
  # The upstream preset JSON is kept byte-for-byte intact in the bundle. Add Windows-only
  # placement metadata to the activated copy so the UI preview and renderer use the same crop,
  # safe area, task treatment, and light/dark mode.
  $runtimeProfile = Get-BundledPresetRuntimeProfile -Id $PresetId
  if ($null -eq $runtimeProfile) { throw "Missing runtime profile for bundled Codex theme: $PresetId" }
  $theme | Add-Member -NotePropertyName appearance -NotePropertyValue $runtimeProfile.Appearance -Force
  $theme | Add-Member -NotePropertyName art -NotePropertyValue ([pscustomobject]@{
      focusX = $runtimeProfile.FocusX
      focusY = $runtimeProfile.FocusY
      safeArea = 'left'
      taskMode = 'ambient'
    }) -Force
}

$active = Set-DreamSkinActiveTheme -ImagePath $imagePath -Theme $theme -StateRoot $stateRoot
Write-Host "Codex Dream Skin active theme updated to '$($active.Theme.name)'."
