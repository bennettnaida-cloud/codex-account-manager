param([Parameter(Mandatory=$true)][string]$CodexPath)
$ErrorActionPreference = 'Stop'
$catalog = (& $CodexPath debug models --bundled | Out-String) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $catalog.models) { throw 'Bundled model catalog unavailable.' }
$destination = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets\codex-models'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($model in $catalog.models) {
    if ($model.slug -notmatch '^[a-z0-9.-]+$') { throw 'Invalid bundled model slug.' }
    $document = @{models=@($model)} | ConvertTo-Json -Depth 80
    [IO.File]::WriteAllText((Join-Path $destination ($model.slug + '.json')), $document, [Text.UTF8Encoding]::new($false))
}
