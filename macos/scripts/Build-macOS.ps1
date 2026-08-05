[CmdletBinding()]
param(
    [string]$Output,
    [ValidatePattern('^\d{8}$')]
    [string]$ReleaseDate,
    [switch]$KeepStage,
    [switch]$SkipNpmInstall
)

$ErrorActionPreference = 'Stop'
$MacOSRoot = Split-Path -Parent $PSScriptRoot

Push-Location $MacOSRoot
try {
    if (-not $SkipNpmInstall) {
        npm install --no-package-lock --ignore-scripts=false
        if ($LASTEXITCODE -ne 0) { throw 'npm dependency installation failed.' }
    }

    $BuildArgs = @('run', 'build:mac', '--')
    if ($Output) { $BuildArgs += @('--output', $Output) }
    if ($ReleaseDate) { $BuildArgs += @('--date', $ReleaseDate) }
    if ($KeepStage) { $BuildArgs += '--keep-stage' }

    & npm @BuildArgs
    if ($LASTEXITCODE -ne 0) { throw 'macOS package build failed.' }
}
finally {
    Pop-Location
}
