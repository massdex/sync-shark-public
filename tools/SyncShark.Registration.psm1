#Requires -Version 5.1
# Per-user Windows registration shared by the house setup and its uninstall entry.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SyncSharkNormalizedPath([string]$Path) {
    [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
}

function Assert-SyncSharkOrdinaryTree([string]$Root, [switch]$AllowMissing, [switch]$RootOnly) {
    if (-not (Test-Path -LiteralPath $Root)) {
        if ($AllowMissing) { return }
        throw "Missing tree: $Root"
    }
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (-not $rootItem.PSIsContainer) { throw "Expected a directory: $Root" }
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Mutable tree root is a reparse point: $Root"
    }
    if ($RootOnly) { return }
    $reparse = Get-ChildItem -LiteralPath $Root -Force -Recurse |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
        Select-Object -First 1
    if ($reparse) { throw "Mutable tree contains a reparse point: $($reparse.FullName)" }
}

function Get-SyncSharkJunctionTarget([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.LinkType -cne 'Junction') { throw "Expected a junction: $Path" }
    Get-SyncSharkNormalizedPath ([string]$item.Target)
}

function Assert-SyncSharkJunction([string]$Path, [string]$Target) {
    $actual = Get-SyncSharkJunctionTarget $Path
    if ($null -eq $actual -or $actual -cne (Get-SyncSharkNormalizedPath $Target)) {
        throw "Junction target mismatch: $Path"
    }
}

function Resolve-SyncSharkProductRoot([string]$TestProductRoot) {
    if ([string]::IsNullOrWhiteSpace($TestProductRoot)) {
        $localApplicationData = [Environment]::GetFolderPath('LocalApplicationData')
        if ([string]::IsNullOrWhiteSpace($localApplicationData) -or
            ([IO.Path]::GetFullPath($localApplicationData) -ine $localApplicationData)) {
            throw 'The current Windows user has no absolute LocalApplicationData folder.'
        }
        return Join-Path $localApplicationData 'Massdex\SyncShark'
    }
    if ($env:SYNC_SHARK_RELEASE_TEST_MODE -cne '1') {
        throw 'The test product-root seam is disabled.'
    }
    $testBase = Join-Path ([IO.Path]::GetTempPath()) 'sync-shark-release-tests'
    $base = (Get-SyncSharkNormalizedPath $testBase) + [IO.Path]::DirectorySeparatorChar
    $resolved = Get-SyncSharkNormalizedPath $TestProductRoot
    if (-not $resolved.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The test product root must be a child of the fixed Sync Shark release-test root.'
    }
    $resolved
}

function Resolve-SyncSharkRuntimeRoot([string]$Version,[string]$TestProductRoot='') {
    if($Version -cnotmatch '^\d+\.\d+\.\d+$'){throw 'An exact runtime version is required.'}
    Join-Path (Resolve-SyncSharkProductRoot $TestProductRoot) ('versions/'+$Version)
}

function Invoke-SyncSharkAppRegistration {
    param([string]$Version,[ValidateSet('Register','Remove','Verify','VerifyAbsent')][string]$Operation,[Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9 .-]+[.]exe$')][string]$HostExecutable)
    if ($Version -cnotmatch '^\d+\.\d+\.\d+$') { throw 'An exact registration version is required.' }
    $root = Resolve-SyncSharkProductRoot ''
    $runtime = Join-Path $root 'active'
    $executable = Join-Path $runtime $HostExecutable
    $oldExecutable = Join-Path $runtime 'SyncShark.exe'
    $uninstall = '"' + (Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe') + '" -NoProfile -ExecutionPolicy Bypass -File "' +
        (Join-Path $runtime 'tools\SyncShark.Uninstall.ps1') + '"'
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Massdex.SyncShark'
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Massdex\Sync Shark.lnk'
    $owner = 'Massdex.SyncShark:' + $root
    $values = @{ DisplayName='Sync Shark'; DisplayVersion=$Version; Publisher='Massdex';
        InstallLocation=(Resolve-SyncSharkRuntimeRoot $Version); UninstallString=$uninstall; SyncSharkOwner=$owner }
    $existing = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
    # An empty key can survive creation before the first value; provider metadata is not a value.
    $keyExists = Test-Path -LiteralPath $key
    $emptyKey = $false
    if ($keyExists) {
        $registryKey = Get-Item -LiteralPath $key -ErrorAction Stop
        try { $emptyKey = $registryKey.ValueCount -eq 0 -and $registryKey.SubKeyCount -eq 0 }
        finally { if ($registryKey -is [IDisposable]) { $registryKey.Dispose() } }
    }
    if ($keyExists -and -not $emptyKey -and ($null -eq $existing -or $null -eq $existing.PSObject.Properties['SyncSharkOwner'] -or
            $existing.SyncSharkOwner -cne $owner)) {
        throw 'Another installation owns the uninstall registration. Nothing was changed.'
    }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $null
        if (Test-Path -LiteralPath $shortcut) {
            $item = Get-Item -LiteralPath $shortcut -Force
            if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'The Start-menu shortcut must be an ordinary file.'
            }
            $link = $shell.CreateShortcut($shortcut)
            $ours = $link.TargetPath -ieq $executable -and $link.Arguments -ceq ''
            $predecessor = $link.TargetPath -ieq $oldExecutable -and $link.Arguments -in @('', '--cli engine host start --yes')
            if (!$ours -and !$predecessor) {
                throw 'Another application owns the Start-menu shortcut. Nothing was changed.'
            }
        }
        if ($Operation -eq 'Register') {
            Assert-SyncSharkJunction $runtime $values.InstallLocation
            if (-not (Test-Path -LiteralPath (Join-Path $runtime 'tools\SyncShark.Uninstall.ps1') -PathType Leaf)) {
                throw 'The installed uninstall entry point is missing.'
            }
            if (-not $keyExists) { New-Item -Path $key | Out-Null }
            # Ownership precedes descriptive fields so an interrupted owned write can be replayed.
            New-ItemProperty -LiteralPath $key -Name 'SyncSharkOwner' -Value $owner -PropertyType String -Force | Out-Null
            foreach ($name in $values.Keys | Where-Object { $_ -cne 'SyncSharkOwner' }) {
                New-ItemProperty -LiteralPath $key -Name $name -Value $values[$name] -PropertyType String -Force | Out-Null
            }
            if ($null -eq $link) {
                $folder = Split-Path -Parent $shortcut
                Assert-SyncSharkOrdinaryTree $folder -RootOnly -AllowMissing
                [void][IO.Directory]::CreateDirectory($folder)
                $link = $shell.CreateShortcut($shortcut)
            }
            $link.TargetPath=$executable; $link.Arguments=''; $link.WorkingDirectory=$runtime; $link.Save()
        } elseif ($Operation -eq 'Remove') {
            if ($null -ne $link) { Remove-Item -LiteralPath $shortcut }
            if ($keyExists) { Remove-Item -LiteralPath $key }
        } elseif ($Operation -eq 'VerifyAbsent') {
            if ($null -ne $link -or $keyExists) { throw 'Application registration remains.' }
        } else {
            if ($null -eq $link -or $null -eq $existing) { throw 'Application registration is missing.' }
            if ($link.TargetPath -ine $executable -or $link.Arguments -cne '') { throw 'Start menu must launch the engine host.' }
            foreach ($name in $values.Keys) {
                if ($existing.$name -cne $values[$name]) { throw "Application registration differs: $name" }
            }
        }
    } finally {
        if ($null -ne $link -and [Runtime.InteropServices.Marshal]::IsComObject($link)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
        if ([Runtime.InteropServices.Marshal]::IsComObject($shell)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    }
}

Export-ModuleMember -Function Get-SyncSharkNormalizedPath, Assert-SyncSharkOrdinaryTree, Get-SyncSharkJunctionTarget, Assert-SyncSharkJunction, Resolve-SyncSharkProductRoot, Resolve-SyncSharkRuntimeRoot, Invoke-SyncSharkAppRegistration
