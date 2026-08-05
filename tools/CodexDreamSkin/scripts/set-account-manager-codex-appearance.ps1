[CmdletBinding()]
param(
  [ValidateSet('System', 'Light', 'Dark', 'PorcelainLight', 'NebulaDark')]
  [string]$Mode = 'System',
  [ValidateSet('manager', 'manager-light', 'manager-porcelain-light', 'manager-dark', 'manager-nebula-dark', 'custom', 'preset-arina-hashimoto', 'preset-gothic-void-crusade', 'preset-midnight-aurora', 'preset-sakura-dawn', 'preset-amber-dusk', 'preset-forest-mist', 'preset-cyber-neon')]
  [string]$PresetId = 'manager',
  [string]$CustomThemePath,
  [switch]$Restore,
  [string]$ConfigPath,
  [string]$StateRoot
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'config-utf8.ps1')

$configPath = if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
  Join-Path $HOME '.codex\config.toml'
} else {
  [System.IO.Path]::GetFullPath($ConfigPath)
}
$stateRoot = if ([string]::IsNullOrWhiteSpace($StateRoot)) {
  Join-Path $env:LOCALAPPDATA 'CodexDreamSkin'
} else {
  [System.IO.Path]::GetFullPath($StateRoot)
}
$backupPath = Join-Path $stateRoot 'config.before-account-manager-appearance.toml'
$appearanceKeys = @(
  'appearanceTheme',
  'appearanceLightCodeThemeId',
  'appearanceDarkCodeThemeId',
  'appearanceLightChromeTheme',
  'appearanceDarkChromeTheme'
)

function Assert-AccountManagerAppearanceShape {
  param([Parameter(Mandatory = $true)][string]$Content)

  Assert-DreamSkinDesktopShapeSupported -Content $Content
  foreach ($key in $appearanceKeys) {
    if (Test-DreamSkinDesktopNestedTable -Content $Content -Key $key) {
      throw "Codex config stores '$key' as a nested table and cannot be updated safely."
    }
  }
}

function Get-AccountManagerThemeSettings {
  param([Parameter(Mandatory = $true)][string]$SelectedMode)

  switch ($SelectedMode) {
    'Light' {
      return [ordered]@{
        appearanceTheme = 'appearanceTheme = "light"'
        appearanceLightCodeThemeId = 'appearanceLightCodeThemeId = "github"'
        appearanceDarkCodeThemeId = 'appearanceDarkCodeThemeId = "tokyo-night"'
        appearanceLightChromeTheme = 'appearanceLightChromeTheme = { accent = "#4D8DFF", contrast = 86, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#172033", opaqueWindows = true, semanticColors = { diffAdded = "#DDF5E8", diffRemoved = "#FFE2E6", skill = "#DCEBFF" }, surface = "#FFFFFF" }'
        appearanceDarkChromeTheme = 'appearanceDarkChromeTheme = { accent = "#73A8FF", contrast = 88, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#EEF4FF", opaqueWindows = true, semanticColors = { diffAdded = "#174B3D", diffRemoved = "#592E39", skill = "#283C67" }, surface = "#0C1526" }'
      }
    }
    'PorcelainLight' {
      return [ordered]@{
        appearanceTheme = 'appearanceTheme = "light"'
        appearanceLightCodeThemeId = 'appearanceLightCodeThemeId = "everforest"'
        appearanceDarkCodeThemeId = 'appearanceDarkCodeThemeId = "everforest"'
        appearanceLightChromeTheme = 'appearanceLightChromeTheme = { accent = "#4E8F84", contrast = 88, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#183C39", opaqueWindows = true, semanticColors = { diffAdded = "#D7EFE3", diffRemoved = "#F9DFDD", skill = "#DDEFEA" }, surface = "#F5FAF8" }'
        appearanceDarkChromeTheme = 'appearanceDarkChromeTheme = { accent = "#75B7A9", contrast = 90, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#EDF8F4", opaqueWindows = true, semanticColors = { diffAdded = "#1A4B40", diffRemoved = "#5C3031", skill = "#274F49" }, surface = "#102A27" }'
      }
    }
    'Dark' {
      return [ordered]@{
        appearanceTheme = 'appearanceTheme = "dark"'
        appearanceLightCodeThemeId = 'appearanceLightCodeThemeId = "github"'
        appearanceDarkCodeThemeId = 'appearanceDarkCodeThemeId = "tokyo-night"'
        appearanceLightChromeTheme = 'appearanceLightChromeTheme = { accent = "#4D8DFF", contrast = 86, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#172033", opaqueWindows = true, semanticColors = { diffAdded = "#DDF5E8", diffRemoved = "#FFE2E6", skill = "#DCEBFF" }, surface = "#FFFFFF" }'
        appearanceDarkChromeTheme = 'appearanceDarkChromeTheme = { accent = "#60A5FA", contrast = 92, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#F1F6FF", opaqueWindows = true, semanticColors = { diffAdded = "#154B3A", diffRemoved = "#5B2E3A", skill = "#263E6B" }, surface = "#091526" }'
      }
    }
    'NebulaDark' {
      return [ordered]@{
        appearanceTheme = 'appearanceTheme = "dark"'
        appearanceLightCodeThemeId = 'appearanceLightCodeThemeId = "codex"'
        appearanceDarkCodeThemeId = 'appearanceDarkCodeThemeId = "night-owl"'
        appearanceLightChromeTheme = 'appearanceLightChromeTheme = { accent = "#8A72F7", contrast = 88, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#2A1E48", opaqueWindows = true, semanticColors = { diffAdded = "#DDF3E7", diffRemoved = "#FCE1EA", skill = "#E9E2FF" }, surface = "#FAF8FF" }'
        appearanceDarkChromeTheme = 'appearanceDarkChromeTheme = { accent = "#B49AFF", contrast = 94, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#FCFAFF", opaqueWindows = true, semanticColors = { diffAdded = "#1D503F", diffRemoved = "#63313F", skill = "#3B3565" }, surface = "#171229" }'
      }
    }
    'preset-arina-hashimoto' {
      return [ordered]@{
        appearanceTheme = 'appearanceTheme = "light"'
        appearanceLightCodeThemeId = 'appearanceLightCodeThemeId = "rose-pine"'
        appearanceDarkCodeThemeId = 'appearanceDarkCodeThemeId = "rose-pine"'
        appearanceLightChromeTheme = 'appearanceLightChromeTheme = { accent = "#D86A83", contrast = 88, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#402C33", opaqueWindows = true, semanticColors = { diffAdded = "#E5F4E8", diffRemoved = "#FFE0E5", skill = "#F7E0E8" }, surface = "#FFF7F8" }'
        appearanceDarkChromeTheme = 'appearanceDarkChromeTheme = { accent = "#E28AA0", contrast = 92, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#FFF4F6", opaqueWindows = true, semanticColors = { diffAdded = "#1A4C3D", diffRemoved = "#64313E", skill = "#5B354C" }, surface = "#27171E" }'
      }
    }
    'preset-gothic-void-crusade' {
      return [ordered]@{
        appearanceTheme = 'appearanceTheme = "dark"'
        appearanceLightCodeThemeId = 'appearanceLightCodeThemeId = "github"'
        appearanceDarkCodeThemeId = 'appearanceDarkCodeThemeId = "tokyo-night"'
        appearanceLightChromeTheme = 'appearanceLightChromeTheme = { accent = "#C8A55A", contrast = 88, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#342A1E", opaqueWindows = true, semanticColors = { diffAdded = "#E7F1D7", diffRemoved = "#F8DCD3", skill = "#F2E3B5" }, surface = "#F6F0E4" }'
        appearanceDarkChromeTheme = 'appearanceDarkChromeTheme = { accent = "#C8A55A", contrast = 94, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#F3EAD7", opaqueWindows = true, semanticColors = { diffAdded = "#314A32", diffRemoved = "#67332C", skill = "#5A4824" }, surface = "#171513" }'
      }
    }
    'preset-midnight-aurora' {
      return [ordered]@{
        appearanceTheme = 'appearanceTheme = "dark"'
        appearanceLightCodeThemeId = 'appearanceLightCodeThemeId = "github"'
        appearanceDarkCodeThemeId = 'appearanceDarkCodeThemeId = "tokyo-night"'
        appearanceLightChromeTheme = 'appearanceLightChromeTheme = { accent = "#2DE1C2", contrast = 86, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#17323A", opaqueWindows = true, semanticColors = { diffAdded = "#DDF7F0", diffRemoved = "#FFE1E8", skill = "#E4E2FF" }, surface = "#F5FCFC" }'
        appearanceDarkChromeTheme = 'appearanceDarkChromeTheme = { accent = "#2DE1C2", contrast = 94, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#EAF4FF", opaqueWindows = true, semanticColors = { diffAdded = "#164B40", diffRemoved = "#552E43", skill = "#393568" }, surface = "#0A0E1A" }'
      }
    }
    'preset-sakura-dawn' {
      return [ordered]@{
        appearanceTheme = 'appearanceTheme = "light"'
        appearanceLightCodeThemeId = 'appearanceLightCodeThemeId = "rose-pine"'
        appearanceDarkCodeThemeId = 'appearanceDarkCodeThemeId = "rose-pine"'
        appearanceLightChromeTheme = 'appearanceLightChromeTheme = { accent = "#F0607A", contrast = 88, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#3A2A30", opaqueWindows = true, semanticColors = { diffAdded = "#E3F4E8", diffRemoved = "#FFE0E5", skill = "#F8E1E8" }, surface = "#FDF3F5" }'
        appearanceDarkChromeTheme = 'appearanceDarkChromeTheme = { accent = "#F7889C", contrast = 92, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#FFF5F7", opaqueWindows = true, semanticColors = { diffAdded = "#1B4C3E", diffRemoved = "#63303D", skill = "#5C354B" }, surface = "#25151D" }'
      }
    }
    'preset-amber-dusk' {
      return [ordered]@{
        appearanceTheme = 'appearanceTheme = "dark"'
        appearanceLightCodeThemeId = 'appearanceLightCodeThemeId = "gruvbox"'
        appearanceDarkCodeThemeId = 'appearanceDarkCodeThemeId = "gruvbox"'
        appearanceLightChromeTheme = 'appearanceLightChromeTheme = { accent = "#D8902D", contrast = 88, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#4D3014", opaqueWindows = true, semanticColors = { diffAdded = "#E7F2DE", diffRemoved = "#FBE0D7", skill = "#FFF0D1" }, surface = "#FFF8EE" }'
        appearanceDarkChromeTheme = 'appearanceDarkChromeTheme = { accent = "#FFB347", contrast = 94, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#FFF3E6", opaqueWindows = true, semanticColors = { diffAdded = "#31513B", diffRemoved = "#653132", skill = "#59401E" }, surface = "#17110C" }'
      }
    }
    'preset-forest-mist' {
      return [ordered]@{
        appearanceTheme = 'appearanceTheme = "dark"'
        appearanceLightCodeThemeId = 'appearanceLightCodeThemeId = "everforest"'
        appearanceDarkCodeThemeId = 'appearanceDarkCodeThemeId = "everforest"'
        appearanceLightChromeTheme = 'appearanceLightChromeTheme = { accent = "#4DB892", contrast = 88, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#1A3A30", opaqueWindows = true, semanticColors = { diffAdded = "#DDF1E6", diffRemoved = "#FBE1DF", skill = "#DCEFE7" }, surface = "#F4FBF7" }'
        appearanceDarkChromeTheme = 'appearanceDarkChromeTheme = { accent = "#7FD1B9", contrast = 94, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#E8F5EE", opaqueWindows = true, semanticColors = { diffAdded = "#1B4A3D", diffRemoved = "#5B3032", skill = "#294F46" }, surface = "#0D1A16" }'
      }
    }
    'preset-cyber-neon' {
      return [ordered]@{
        appearanceTheme = 'appearanceTheme = "dark"'
        appearanceLightCodeThemeId = 'appearanceLightCodeThemeId = "vscode-plus"'
        appearanceDarkCodeThemeId = 'appearanceDarkCodeThemeId = "matrix"'
        appearanceLightChromeTheme = 'appearanceLightChromeTheme = { accent = "#16B8D8", contrast = 88, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#15303B", opaqueWindows = true, semanticColors = { diffAdded = "#DCF7F0", diffRemoved = "#FFE0EC", skill = "#EDE2FF" }, surface = "#F4FCFF" }'
        appearanceDarkChromeTheme = 'appearanceDarkChromeTheme = { accent = "#16E0FF", contrast = 96, fonts = { code = "Cascadia Code", ui = "Microsoft YaHei UI" }, ink = "#EAFCFF", opaqueWindows = true, semanticColors = { diffAdded = "#164C43", diffRemoved = "#672D4E", skill = "#4A2B6B" }, surface = "#07070D" }'
      }
    }
    default {
      return $null
    }
  }
}

function Get-CustomThemeSettings {
  param([Parameter(Mandatory = $true)][string]$Path)

  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Custom Codex theme not found: $Path"
  }
  try {
    $theme = [System.IO.File]::ReadAllText([System.IO.Path]::GetFullPath($Path)) | ConvertFrom-Json
  } catch {
    throw "Custom Codex theme is not valid JSON: $($_.Exception.Message)"
  }

  $hexPattern = '^#[0-9A-Fa-f]{6}$'
  $accent = [string]$theme.AccentColor
  $surface = [string]$theme.SurfaceColor
  $ink = [string]$theme.InkColor
  if ($accent -notmatch $hexPattern -or $surface -notmatch $hexPattern -or $ink -notmatch $hexPattern) {
    throw 'Custom Codex theme colors must use #RRGGBB format.'
  }
  $contrast = [int]$theme.Contrast
  if ($contrast -lt 70 -or $contrast -gt 100) {
    throw 'Custom Codex theme contrast must be between 70 and 100.'
  }
  $allowedCodeThemes = @('tokyo-night', 'everforest', 'rose-pine', 'gruvbox', 'night-owl', 'matrix', 'vscode-plus', 'github')
  $codeTheme = [string]$theme.CodeThemeId
  if ($codeTheme -notin $allowedCodeThemes) {
    throw "Unsupported Codex code theme: $codeTheme"
  }
  $appearance = if ([bool]$theme.IsDark) { 'dark' } else { 'light' }
  $semantic = if ([bool]$theme.IsDark) {
    'semanticColors = { diffAdded = "#174B3D", diffRemoved = "#592E39", skill = "#283C67" }'
  } else {
    'semanticColors = { diffAdded = "#DDF5E8", diffRemoved = "#FFE2E6", skill = "#DCEBFF" }'
  }
  $chrome = "{ accent = `"$accent`", contrast = $contrast, fonts = { code = `"Cascadia Code`", ui = `"Microsoft YaHei UI`" }, ink = `"$ink`", opaqueWindows = true, $semantic, surface = `"$surface`" }"

  return [ordered]@{
    appearanceTheme = "appearanceTheme = `"$appearance`""
    appearanceLightCodeThemeId = "appearanceLightCodeThemeId = `"$codeTheme`""
    appearanceDarkCodeThemeId = "appearanceDarkCodeThemeId = `"$codeTheme`""
    appearanceLightChromeTheme = "appearanceLightChromeTheme = $chrome"
    appearanceDarkChromeTheme = "appearanceDarkChromeTheme = $chrome"
  }
}

if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
  throw "Codex config not found: $configPath"
}

$originalBytes = [System.IO.File]::ReadAllBytes($configPath)
$content = ConvertFrom-DreamSkinUtf8Bytes -Bytes $originalBytes -Path $configPath
Assert-AccountManagerAppearanceShape -Content $content
$newLine = Get-DreamSkinNewLine -Content $content

if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
  Write-DreamSkinBytesAtomically -Path $backupPath -Bytes $originalBytes -ExpectedBytes $null
}

$desktop = Get-DreamSkinDesktopSection -Content $content
if ($null -eq $desktop) {
  $content = Add-DreamSkinDesktopSection -Content $content -NewLine $newLine
  $desktop = Get-DreamSkinDesktopSection -Content $content
}

$body = $desktop.Body
if ($Restore -or ($PresetId -eq 'manager' -and $Mode -eq 'System')) {
  $backupContent = ConvertFrom-DreamSkinUtf8Bytes -Bytes ([System.IO.File]::ReadAllBytes($backupPath)) -Path $backupPath
  Assert-AccountManagerAppearanceShape -Content $backupContent
  $backupDesktop = Get-DreamSkinDesktopSection -Content $backupContent
  foreach ($key in $appearanceKeys) {
    $line = if ($null -ne $backupDesktop) {
      Get-DreamSkinSectionSettingLine -Body $backupDesktop.Body -Key $key
    } else {
      $null
    }
    $body = Set-DreamSkinSectionSetting -Body $body -Key $key -Line $line -NewLine $newLine
  }
} else {
  $settings = if ($PresetId -eq 'custom') {
    Get-CustomThemeSettings -Path $CustomThemePath
  } else {
    $selectedStyle = switch ($PresetId) {
      'manager' { $Mode }
      'manager-light' { 'Light' }
      'manager-porcelain-light' { 'PorcelainLight' }
      'manager-dark' { 'Dark' }
      'manager-nebula-dark' { 'NebulaDark' }
      default { $PresetId }
    }
    Get-AccountManagerThemeSettings -SelectedMode $selectedStyle
  }
  if ($null -eq $settings) {
    throw "Unsupported Codex appearance selection: $PresetId"
  }
  foreach ($key in $appearanceKeys) {
    $body = Set-DreamSkinSectionSetting -Body $body -Key $key -Line $settings[$key] -NewLine $newLine
  }
}

$content = $content.Substring(0, $desktop.BodyStart) + $body +
  $content.Substring($desktop.BodyStart + $desktop.BodyLength)
Write-DreamSkinUtf8FileAtomically -Path $configPath -Content $content -ExpectedBytes $originalBytes

if ($Restore -or ($PresetId -eq 'manager' -and $Mode -eq 'System')) {
  Write-Host 'Codex appearance restored from the Account Manager backup.'
} else {
  Write-Host "Codex appearance updated to $Mode."
}
