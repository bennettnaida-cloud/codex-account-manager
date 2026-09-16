param([Parameter(Mandatory=$true)][string]$ManagerExe)
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('cam-compaction-migration-' + [guid]::NewGuid().ToString('N'))
$accountHome = Join-Path $fixture 'account'
New-Item -ItemType Directory -Path $accountHome -Force | Out-Null
$account = @([ordered]@{name='migration-fixture';authKind='compatible_api';codexHome=$accountHome;apiModel='gpt-6-astra';apiBaseUrl='https://example.test';apiWireApi='responses'})
[IO.File]::WriteAllText((Join-Path $fixture 'accounts.json'), (ConvertTo-Json -InputObject $account -Depth 3))
$configPath = Join-Path $accountHome 'config.toml'
$authPath = Join-Path $accountHome 'auth.json'
[IO.File]::WriteAllText($configPath, "model = `"gpt-6-astra`"`nmodel_auto_compact_token_limit = 1000000000`n[features]`nremote_compaction_v2 = false`n[model_providers.test]`nbase_url = `"https://example.test`"`nexperimental_bearer_token = `"dummy-fixture-only`"`n")
[IO.File]::WriteAllText($authPath, '{"OPENAI_API_KEY":"dummy-fixture-only"}')
$authHash = (Get-FileHash -LiteralPath $authPath).Hash
function Invoke-FixtureMigration([string]$Switch, [string]$Expected) {
    $out = Join-Path $fixture ('out-' + [guid]::NewGuid().ToString('N') + '.txt')
    $err = $out + '.err'
    $p = Start-Process -FilePath $ManagerExe -ArgumentList @('--manager-root', ('"' + $fixture + '"'), $Switch) -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $out -RedirectStandardError $err
    if ($p.ExitCode -ne 0) { throw ('Fixture migration failed: ' + (Get-Content -LiteralPath $err -Raw)) }
    if ((Get-Content -LiteralPath $out -Raw).Trim() -ne $Expected) { throw 'Migration was not idempotent.' }
}
Invoke-FixtureMigration '--sync-native-compaction' 'Native compaction configs updated: 1'
Invoke-FixtureMigration '--sync-native-compaction' 'Native compaction configs updated: 0'
Invoke-FixtureMigration '--sync-compatible-model-catalogs' 'Model catalogs updated: 1'
Invoke-FixtureMigration '--sync-compatible-model-catalogs' 'Model catalogs updated: 0'
$result = Get-Content -LiteralPath $configPath -Raw
if ($result -notmatch 'remote_compaction_v2 = true' -or $result -match '1000000000' -or $result -notmatch 'model_catalog_json' -or $result -notmatch 'dummy-fixture-only') { throw 'Unexpected configuration changes.' }
if ((Get-FileHash -LiteralPath $authPath).Hash -ne $authHash) { throw 'Fixture credential changed.' }
if (@(Get-ChildItem -LiteralPath (Join-Path $accountHome 'backups') -Directory).Count -ne 2) { throw 'Migration backups missing or duplicated.' }
Write-Output 'PASS: configuration migration, backup creation, idempotence, model catalog sync and credential preservation.'
Write-Output ('Fixture: ' + $fixture)
