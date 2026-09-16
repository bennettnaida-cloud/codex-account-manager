Set-StrictMode -Version Latest

function Sync-ManagerShortcuts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ExecutablePath,
        [Parameter(Mandatory)][string]$ManagerRoot,
        [string]$SourceRoot,
        [string[]]$ShortcutDirectories
    )

    $executable = [IO.Path]::GetFullPath($ExecutablePath)
    $dataRoot = [IO.Path]::GetFullPath($ManagerRoot).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Shortcut target is missing: $executable"
    }
    if ([IO.Path]::GetFileName($executable) -ine 'CodexAccountManager.exe') {
        throw 'Shortcut synchronization requires the manager executable.'
    }
    if (-not $ShortcutDirectories) {
        $pinned = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Internet Explorer\Quick Launch\User Pinned'
        $ShortcutDirectories = @(
            [Environment]::GetFolderPath('Desktop'),
            [Environment]::GetFolderPath('Programs'),
            (Join-Path $pinned 'TaskBar'),
            (Join-Path $pinned 'ImplicitAppShortcuts')
        )
    }
    $sourcePrefix = if ($SourceRoot) { [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\') + '\' } else { $null }
    $shell = New-Object -ComObject WScript.Shell
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $updated = [Collections.Generic.List[string]]::new()
    try {
        foreach ($directory in $ShortcutDirectories) {
            if (-not (Test-Path -LiteralPath $directory -PathType Container)) { continue }
            foreach ($file in Get-ChildItem -LiteralPath $directory -Filter '*.lnk' -File -Recurse) {
                if (-not $seen.Add($file.FullName)) { continue }
                $link = $shell.CreateShortcut($file.FullName)
                try {
                    $target = [string]$link.TargetPath
                    $arguments = [string]$link.Arguments
                    $isManager = [IO.Path]::GetFileName($target) -ieq 'CodexAccountManager.exe'
                    $isSourceLauncher = $sourcePrefix -and
                        ([IO.Path]::GetFileName($target) -in @('powershell.exe', 'pwsh.exe')) -and
                        $arguments.IndexOf(($sourcePrefix + 'Start-CodexAccountManager.ps1'), [StringComparison]::OrdinalIgnoreCase) -ge 0
                    if (-not ($isManager -or $isSourceLauncher)) { continue }

                    # An explicit data root always wins over executable location. Never
                    # redirect another portable installation or a deliberately kept fallback.
                    $rootMatch = [regex]::Match($arguments, '(?i)(?:^|\s)--manager-root(?:\s+|=)(?:"([^"]+)"|(\S+))')
                    if ($rootMatch.Success) {
                        $boundRoot = if ($rootMatch.Groups[1].Success) { $rootMatch.Groups[1].Value } else { $rootMatch.Groups[2].Value }
                        $owned = [IO.Path]::GetFullPath($boundRoot).TrimEnd('\') -ieq $dataRoot
                    }
                    else {
                        $owned = ([string]$link.WorkingDirectory).TrimEnd('\') -ieq $dataRoot
                        if (-not $owned -and $sourcePrefix) {
                            $owned = $isSourceLauncher -or $target.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)
                        }
                    }
                    if (-not $owned) { continue }
                    # Explicit historical fallbacks stay pinned to their own package.
                    if ($target -match '(?i)[\\/]CodexAccountManager-2\.2\.25[^\\/]*[\\/]' -or
                        $target -match '(?i)[\\/][^\\/]*(?:fallback|rollback)[^\\/]*[\\/]') { continue }
                    # Re-running an older build must not downgrade a newer pinned entry.
                    $oldVersion = [regex]::Match($target, '(?i)[\\/]CodexAccountManager-(\d+\.\d+\.\d+)[\\/]')
                    $newVersion = [regex]::Match($executable, '(?i)[\\/]CodexAccountManager-(\d+\.\d+\.\d+)[\\/]')
                    if ($oldVersion.Success -and $newVersion.Success -and
                        [version]$oldVersion.Groups[1].Value -gt [version]$newVersion.Groups[1].Value) { continue }
                    $newArguments = if ($isSourceLauncher) {
                        '--manager-root "' + $dataRoot + '"'
                    }
                    elseif ($rootMatch.Success) { $arguments }
                    else { ($arguments + ' --manager-root "' + $dataRoot + '"').Trim() }
                    $workingDirectory = Split-Path -Parent $executable
                    $icon = $executable + ',0'
                    if ($target -ieq $executable -and $link.Arguments -ceq $newArguments -and
                        $link.WorkingDirectory -ieq $workingDirectory -and $link.IconLocation -ieq $icon) { continue }
                    $link.TargetPath = $executable
                    $link.WorkingDirectory = $workingDirectory
                    $link.Arguments = $newArguments
                    $link.IconLocation = $icon
                    $link.Save()
                    # Verify the persisted link, not only the in-memory COM object.
                    $check = $shell.CreateShortcut($file.FullName)
                    try {
                        if ($check.TargetPath -ine $executable -or $check.Arguments -cne $newArguments) {
                            throw "Shortcut verification failed: $($file.FullName)"
                        }
                    }
                    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($check) }
                    $updated.Add($file.FullName)
                }
                finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
            }
        }
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }

    if ($updated.Count -gt 0) {
        # Notify Explorer of the changed links without restarting the desktop or
        # terminating any running manager/gateway. Keep the user's existing pins.
        if (-not ('CamShortcutShellNotification' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CamShortcutShellNotification {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);
}
'@
        }
        foreach ($path in $updated) {
            [CamShortcutShellNotification]::SHChangeNotify(0x00002000, 0x0005, $path, [IntPtr]::Zero)
        }
    }
    return $updated.ToArray()
}
