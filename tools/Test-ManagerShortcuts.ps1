Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\packaging\installer\Sync-ManagerShortcuts.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('cam-shortcut-test-' + [guid]::NewGuid().ToString('N'))
$links = Join-Path $fixture 'links'
$data = Join-Path $fixture 'data'
$source = Join-Path $fixture 'source'
$target = Join-Path $source 'dist\CodexAccountManager-2.3.22\CodexAccountManager.exe'
$old = Join-Path $source 'dist\CodexAccountManager-2.3.21\CodexAccountManager.exe'
$shell = New-Object -ComObject WScript.Shell
try {
    New-Item -ItemType Directory -Path $links,$data,(Split-Path -Parent $target) -Force | Out-Null
    # COM shortcuts accept a placeholder target; this test never launches it.
    [IO.File]::WriteAllText($target, '')
    function Add-TestLink([string]$name, [string]$exe, [string]$arguments, [string]$working) {
        $lnk = $shell.CreateShortcut((Join-Path $links ($name + '.lnk')))
        try {
            $lnk.TargetPath = $exe
            $lnk.Arguments = $arguments
            $lnk.WorkingDirectory = $working
            $lnk.Save()
        }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($lnk) }
    }
    Add-TestLink 'taskbar' $old ('--manager-root "' + $data + '" --preserve-existing-pat-gateway') $source
    Add-TestLink 'start-menu' $old '' $data
    Add-TestLink 'desktop' $old '' (Split-Path -Parent $old)
    Add-TestLink 'other-account-data' $old ('--manager-root "' + (Join-Path $fixture 'other-data') + '"') $data
    Add-TestLink 'fallback' (Join-Path $source 'dist\CodexAccountManager-2.2.25\CodexAccountManager.exe') ('--manager-root "' + $data + '"') $data
    Add-TestLink 'newer' (Join-Path $source 'dist\CodexAccountManager-2.3.23\CodexAccountManager.exe') ('--manager-root "' + $data + '"') $data
    Add-TestLink 'unrelated' (Join-Path $source 'Other.exe') '' $data
    $before = @{}
    Get-ChildItem -LiteralPath $links -Filter '*.lnk' | ForEach-Object { $before[$_.BaseName] = (Get-FileHash -LiteralPath $_.FullName).Hash }
    $changed = @(Sync-ManagerShortcuts -ExecutablePath $target -ManagerRoot $data -SourceRoot $source -ShortcutDirectories $links)
    if ($changed.Count -ne 3) { throw "Expected 3 owned shortcut updates, got $($changed.Count)." }
    foreach ($name in @('taskbar','start-menu','desktop')) {
        $lnk = $shell.CreateShortcut((Join-Path $links ($name + '.lnk')))
        try {
            if ($lnk.TargetPath -ine $target -or $lnk.Arguments -notlike '*--manager-root*') { throw "Incorrect repaired link: $name" }
            if ($name -eq 'taskbar' -and $lnk.Arguments -notlike '*--preserve-existing-pat-gateway*') { throw 'Existing arguments were lost.' }
        }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($lnk) }
    }
    foreach ($name in @('other-account-data','fallback','newer','unrelated')) {
        if ((Get-FileHash -LiteralPath (Join-Path $links ($name + '.lnk'))).Hash -ne $before[$name]) { throw "Unrelated link changed: $name" }
    }
    $again = @(Sync-ManagerShortcuts -ExecutablePath $target -ManagerRoot $data -SourceRoot $source -ShortcutDirectories $links)
    if ($again.Count -ne 0) { throw 'Shortcut repair is not idempotent.' }
    # Installer upgrades use the explicit manager root without a source-tree hint.
    Add-TestLink 'installer-pin' $old ('--manager-root "' + $data + '"') $source
    $installed = @(Sync-ManagerShortcuts -ExecutablePath $target -ManagerRoot $data -ShortcutDirectories $links)
    if ($installed.Count -ne 1) { throw 'Installer upgrade did not update its pinned shortcut.' }
    Write-Output 'Shortcut regression tests passed: taskbar, desktop, start menu, installer, data isolation, fallback, downgrade protection, arguments, idempotence.'
}
finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    # Only remove this test's exact GUID directory after verifying its temp parent.
    if ((Split-Path -Parent $fixture).TrimEnd('\') -ieq ([IO.Path]::GetTempPath()).TrimEnd('\') -and
        (Split-Path -Leaf $fixture) -like 'cam-shortcut-test-*') {
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
