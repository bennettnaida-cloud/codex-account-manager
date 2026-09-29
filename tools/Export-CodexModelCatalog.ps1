[CmdletBinding(DefaultParameterSetName='Bundled')]
param(
    [Parameter(Mandatory=$true,ParameterSetName='Bundled')][string]$CodexPath,
    [Parameter(Mandatory=$true,ParameterSetName='Cache')][string]$CachePath,
    [string[]]$ModelIds = @()
)
$ErrorActionPreference = 'Stop'
if ($PSCmdlet.ParameterSetName -eq 'Cache') {
    # Export only model metadata; cache identity, timestamps and credentials are never copied.
    $catalog = Get-Content -LiteralPath $CachePath -Raw -Encoding UTF8 | ConvertFrom-Json
}
else {
    $catalog = (& $CodexPath debug models --bundled | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Bundled model catalog unavailable.' }
}
if (-not $catalog.models) { throw 'Model catalog unavailable.' }
$selectedModels = @($catalog.models | Where-Object { $ModelIds.Count -eq 0 -or $_.slug -cin $ModelIds })
foreach ($id in $ModelIds) {
    if (@($selectedModels | Where-Object slug -CEQ $id).Count -ne 1) { throw 'Requested model is missing or ambiguous.' }
}
foreach ($model in $selectedModels) {
    if ($model.slug -notmatch '^[a-z0-9.-]+$') { throw 'Invalid model slug.' }
}
$destination = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets\codex-models'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($model in $selectedModels) {
    $document = @{models=@($model)} | ConvertTo-Json -Depth 80
    [IO.File]::WriteAllText((Join-Path $destination ($model.slug + '.json')), $document, [Text.UTF8Encoding]::new($false))
}
