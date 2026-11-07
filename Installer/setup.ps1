param([string]$Payload, [switch]$FunctionsOnly)
$ErrorActionPreference = 'Stop'
if (-not ('SetupFileSystem' -as [type])) {
    Add-Type -Path @((Join-Path $PSScriptRoot 'SetupFileSystem.cs'), (Join-Path $PSScriptRoot 'SetupMessages.cs')) -ReferencedAssemblies ([System.Xml.XmlDocument].Assembly.Location)
}
[Massdex.SyncShark.SetupMessages]::Load((Join-Path $PSScriptRoot 'setup-strings.xml'))

function Open-SetupDirectories([string]$Path, [switch]$Create) {
    $held = [Collections.Generic.List[IDisposable]]::new()
    $ancestors = [Collections.Generic.Stack[string]]::new()
    for ($entry = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Path)); $null -ne $entry; $entry = $entry.Parent) {
        $ancestors.Push($entry.FullName)
    }
    try {
        while ($ancestors.Count) {
            $path = $ancestors.Pop()
            if ($Create -and -not [SetupFileSystem]::Exists($path)) { [IO.Directory]::CreateDirectory($path) | Out-Null }
            $held.Add([SetupFileSystem]::OpenDirectory($path))
        }
        $held.Reverse()
        return ,$held
    } catch { foreach ($item in $held) { $item.Dispose() }; throw }
}

function Get-SetupVersion([string]$Executable) {
    $parents = Open-SetupDirectories ([IO.Path]::GetDirectoryName($Executable))
    $file = $null
    try {
        $file = [SetupFileSystem]::OpenFile($Executable)
        $value = [Diagnostics.FileVersionInfo]::GetVersionInfo($Executable).ProductVersion
        if ($value -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(\+[a-fA-F0-9]{40})?$') {
            throw ([Massdex.SyncShark.SetupMessages]::Text('version.invalid', [object[]]@($Executable)))
        }
        return [version]($value.Split('+')[0])
    } finally {
        if ($file) { $file.Dispose() }
        foreach ($item in $parents) { $item.Dispose() }
    }
}

function Assert-SetupUpgrade([version]$Candidate, [string]$PreviousTarget) {
    if ($PreviousTarget -and $Candidate -le (Get-SetupVersion (Join-Path $PreviousTarget 'SyncShark.exe'))) {
        throw ([Massdex.SyncShark.SetupMessages]::Text('version.upgrade', [object[]]@()))
    }
}

function Get-SetupTree([string]$Root) {
    $result = [Collections.Generic.List[object]]::new()
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($Root)
    $directories = [Collections.Generic.List[IDisposable]]::new()
    try {
        while ($pending.Count) {
            $directory = $pending.Dequeue()
            $held = [SetupFileSystem]::OpenDirectory($directory)
            $directories.Add($held)
            $result.Add([pscustomobject]@{ Path = $directory.Substring($Root.Length); Directory = $true; Identity = $held.Identity; Hash = $null })
            foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
                if ([IO.File]::GetAttributes($entry) -band [IO.FileAttributes]::Directory) { $pending.Enqueue($entry); continue }
                $file = [SetupFileSystem]::OpenFile($entry)
                try { $result.Add([pscustomobject]@{ Path = $entry.Substring($Root.Length); Directory = $false; Identity = $file.Identity; Hash = $file.Hash() }) }
                finally { $file.Dispose() }
            }
        }
        return ,$result.ToArray()
    } finally { foreach ($held in $directories) { $held.Dispose() } }
}

function Open-SetupTree([string]$Root, $Manifest, [switch]$RequireComplete, [switch]$ForRemoval) {
    $held = [Collections.Generic.List[object]]::new()
    try {
        $known = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $Manifest) {
            if (-not $known.Add($Root + $entry.Path)) { throw ([Massdex.SyncShark.SetupMessages]::Text('manifest.duplicate', [object[]]@())) }
            if ($entry.Path -and ($entry.Path -notmatch '^\\' -or $entry.Path -match '(^|[\\/])\.\.([\\/]|$)')) { throw ([Massdex.SyncShark.SetupMessages]::Text('manifest.invalid', [object[]]@())) }
            $path = $Root + $entry.Path
            if (-not ([SetupFileSystem]::Exists($path))) {
                if ($RequireComplete) { throw ([Massdex.SyncShark.SetupMessages]::Text('payload.missing', [object[]]@($path))) }
                continue
            }
            $item = if ($entry.Directory) {
                if ($ForRemoval) { [SetupFileSystem]::OpenMutableDirectory($path) } else { [SetupFileSystem]::OpenDirectory($path) }
            } else {
                if ($ForRemoval) { [SetupFileSystem]::OpenMutableFile($path) } else { [SetupFileSystem]::OpenFile($path) }
            }
            $held.Add($item)
            if ($item.Identity -cne $entry.Identity -or (-not $entry.Directory -and $item.Hash() -cne $entry.Hash)) {
                throw ([Massdex.SyncShark.SetupMessages]::Text('recovery.changed', [object[]]@($path)))
            }
        }
        # The held objects cannot be replaced or rewritten; newly added children are never deleted.
        foreach ($entry in $Manifest) {
            if (-not $entry.Directory -or -not ([SetupFileSystem]::Exists($Root + $entry.Path))) { continue }
            foreach ($actual in [IO.Directory]::EnumerateFileSystemEntries($Root + $entry.Path)) {
                if (-not $known.Contains($actual)) { throw ([Massdex.SyncShark.SetupMessages]::Text('recovery.foreign', [object[]]@($actual))) }
            }
        }
        return ,$held
    } catch { foreach ($item in $held) { $item.Dispose() }; throw }
}

function Remove-SetupTree([string]$Root, $Manifest) {
    $parents = Open-SetupDirectories ([IO.Path]::GetDirectoryName($Root))
    $tree = $null
    try {
        $tree = Open-SetupTree $Root $Manifest -ForRemoval
        foreach ($item in ($tree | Sort-Object { $_.Path.Length } -Descending)) { $item.Delete() }
    } finally {
        if ($tree) { foreach ($item in $tree) { $item.Dispose() } }
        foreach ($item in $parents) { $item.Dispose() }
    }
}

function Write-SetupFrame($Journal, $State) {
    $json = ConvertTo-Json -InputObject $State -Depth 8 -Compress
    $Journal.Append([Text.UTF8Encoding]::new($false).GetBytes($json + "`n"))
}

function Read-SetupFrame($Journal) {
    $bytes = $Journal.Read()
    $end = [Array]::LastIndexOf($bytes, [byte]10)
    if ($end -lt 0) { throw ([Massdex.SyncShark.SetupMessages]::Text('journal.empty', [object[]]@())) }
    # Decode only complete frames: an interrupted UTF-8 character belongs to the partial tail.
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes, 0, $end)
    $frames = $text.Split("`n")
    return ($frames[-1] | ConvertFrom-Json)
}

function Restore-SetupTransaction([string]$Root, $Journal) {
    $state = Read-SetupFrame $Journal
    if ($state.Id -cnotmatch '^[0-9a-f]{32}$' -or $state.Version -cnotmatch '^\d+\.\d+\.\d+$') { throw ([Massdex.SyncShark.SetupMessages]::Text('journal.invalid', [object[]]@())) }
    $stage = Join-Path $Root ('versions/.staging/' + $state.Id)
    $destination = Join-Path $Root ('versions\' + $state.Version)
    $activePath = Join-Path $Root 'active'
    $nextPath = Join-Path $Root ('versions/.staging/' + $state.Id + '.active')
    $rootHandle = [SetupFileSystem]::OpenDirectory($Root)
    $versionsHandle = [SetupFileSystem]::OpenDirectory((Join-Path $Root 'versions'))
    $active = $null; $next = $null; $tree = $null; $stagingHandle = $null
    try {
        if ($rootHandle.Identity -cne $state.RootIdentity -or $versionsHandle.Identity -cne $state.VersionsIdentity) { throw ([Massdex.SyncShark.SetupMessages]::Text('recovery.roots', [object[]]@())) }
        if ([SetupFileSystem]::Exists($activePath)) {
            $active = [SetupFileSystem]::OpenJunction($activePath)
            if ($active.Identity -cne $state.ActiveIdentity) { throw ([Massdex.SyncShark.SetupMessages]::Text('recovery.active', [object[]]@())) }
        }
        $candidate = [Convert]::FromBase64String($state.Candidate)
        if ($state.Committed) {
            if ($null -eq $active -or -not [SetupFileSystem]::Equal($active.ReadJunction(), $candidate)) { throw ([Massdex.SyncShark.SetupMessages]::Text('recovery.committed', [object[]]@())) }
            $tree = Open-SetupTree $destination $state.Manifest -RequireComplete
            $Journal.Delete()
            return
        }
        if ($state.Previous) {
            if ($null -eq $active) { throw ([Massdex.SyncShark.SetupMessages]::Text('recovery.predecessor', [object[]]@())) }
            $previous = [Convert]::FromBase64String($state.Previous)
            if (-not [SetupFileSystem]::Equal($active.ReadJunction(), $previous)) { $active.Retarget($candidate, $previous) }
        } elseif ($null -ne $active) {
            if (-not [SetupFileSystem]::Equal($active.ReadJunction(), $candidate)) { throw ([Massdex.SyncShark.SetupMessages]::Text('recovery.first', [object[]]@())) }
            $active.Delete(); $active = $null
        }
        $stagingHandle = [SetupFileSystem]::OpenDirectory((Join-Path $Root 'versions\.staging'))
        if ([SetupFileSystem]::Exists($nextPath)) {
            $next = [SetupFileSystem]::OpenJunction($nextPath)
            if ($next.Identity -cne $state.ActiveIdentity -or -not [SetupFileSystem]::Equal($next.ReadJunction(), $candidate)) { throw ([Massdex.SyncShark.SetupMessages]::Text('recovery.staged', [object[]]@())) }
            $next.Delete(); $next = $null
        }
        foreach ($path in @($destination, $stage)) {
            if (-not ([SetupFileSystem]::Exists($path))) { continue }
            Remove-SetupTree $path $state.Manifest
        }
        $Journal.Delete()
    } finally {
        if ($tree) { foreach ($item in $tree) { $item.Dispose() } }
        if ($active) { $active.Dispose() }; if ($next) { $next.Dispose() }
        if ($stagingHandle) { $stagingHandle.Dispose() }
        $versionsHandle.Dispose(); $rootHandle.Dispose()
    }
}

function Clear-SetupPreparation([string]$Root) {
    $parent = Join-Path $Root 'versions\.staging'
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $guard = [SetupFileSystem]::OpenDirectory($parent)
    try {
        $guard.Verify()
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($parent)) {
            $name = [IO.Path]::GetFileName($path)
            if ($name -cmatch '^[0-9a-f]{32}\.active$') {
                $junction = [SetupFileSystem]::OpenJunction($path)
                try { $junction.Delete() } finally { $junction.Dispose() }
            } elseif ($name -cmatch '^[0-9a-f]{32}\.journal$') {
                $file = [SetupFileSystem]::OpenMutableFile($path)
                try { $file.Delete() } finally { $file.Dispose() }
            } elseif ($name -cmatch '^[0-9a-f]{32}$') {
                $manifest = Get-SetupTree $path
                Remove-SetupTree $path $manifest
            } else { throw ([Massdex.SyncShark.SetupMessages]::Text('preparation.unknown', [object[]]@($path))) }
            Write-Output ([Massdex.SyncShark.SetupMessages]::Text('preparation.removed', [object[]]@($path)))
        }
    } finally { $guard.Dispose() }
}

function Invoke-SetupInstall([string]$Archive, [string]$Root) {
    $Root = [IO.Path]::GetFullPath($Root)
    $parents = Open-SetupDirectories $Root -Create
    $claim = $null; $journal = $null; $active = $null; $stageHandle = $null; $next = $null; $publishedTree = $null; $versions = $null
    $stage = $null; $nextPath = $null; $manifest = $null
    try {
        [IO.Directory]::CreateDirectory((Join-Path $Root 'versions')) | Out-Null
        $versions = Open-SetupDirectories (Join-Path $Root 'versions')
        $claim = [SetupFileSystem]::OpenClaim((Join-Path $Root 'versions\setup.lock'))
        $journalPath = Join-Path $Root 'versions\setup-transaction.jsonl'
        if ([SetupFileSystem]::Exists($journalPath)) {
            $journal = [SetupFileSystem]::OpenJournal($journalPath)
            Restore-SetupTransaction $Root $journal
            $journal.Dispose(); $journal = $null
        }
        Clear-SetupPreparation $Root
        $versions.Add([SetupFileSystem]::OpenDirectory((Join-Path $Root 'versions\.staging')))
        $id = [guid]::NewGuid().ToString('N')
        $stage = Join-Path $Root ('versions/.staging/' + $id)
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        $stageHandle = [SetupFileSystem]::OpenDirectory($stage)
        Expand-Archive -LiteralPath $Archive -DestinationPath $stage
        $manifest = Get-SetupTree $stage
        $version = Get-SetupVersion (Join-Path $stage 'SyncShark.exe')
        $destination = Join-Path $Root ('versions\' + $version)
        if ([SetupFileSystem]::Exists($destination)) { throw ([Massdex.SyncShark.SetupMessages]::Text('version.occupied', [object[]]@())) }
        $activePath = Join-Path $Root 'active'
        $previous = $null
        if ([SetupFileSystem]::Exists($activePath)) {
            $active = [SetupFileSystem]::OpenJunction($activePath)
            Assert-SetupUpgrade $version $active.JunctionTarget()
            $previous = [Convert]::ToBase64String($active.ReadJunction())
        }
        $candidate = [SetupFileSystem]::JunctionData($destination)
        $stageHandle.Dispose()
        $stageHandle = [SetupFileSystem]::OpenMutableDirectory($stage)
        if ($stageHandle.Identity -cne $manifest[0].Identity) { throw ([Massdex.SyncShark.SetupMessages]::Text('recovery.changed', [object[]]@($stage))) }
        if ($null -eq $active) {
            $nextPath = Join-Path $Root ('versions/.staging/' + $id + '.active')
            New-Item -ItemType Junction -Path $nextPath -Target $stage | Out-Null
            $next = [SetupFileSystem]::OpenJunction($nextPath)
            $next.Retarget($next.ReadJunction(), $candidate)
        }
        $state = [pscustomobject]@{
            Id = $id; Version = $version.ToString(); RootIdentity = $parents[0].Identity
            VersionsIdentity = $versions[0].Identity; Manifest = $manifest; Previous = $previous
            Candidate = [Convert]::ToBase64String($candidate)
            ActiveIdentity = $(if ($active) { $active.Identity } else { $next.Identity }); Committed = $false
        }
        $journalTemporary = Join-Path $Root ('versions/.staging/' + $id + '.journal')
        $created = [IO.File]::Open($journalTemporary, 'CreateNew', 'ReadWrite', 'None')
        $created.Dispose()
        $journal = [SetupFileSystem]::OpenJournal($journalTemporary)
        Write-SetupFrame $journal $state
        $journal.Rename($journalPath)
        $stageHandle.Rename($destination)
        $stageHandle.Dispose(); $stageHandle = $null
        $publishedTree = Open-SetupTree $destination $manifest -RequireComplete
        if ($active) { $active.Retarget([Convert]::FromBase64String($previous), $candidate) }
        else { $next.Rename($activePath) }
        $publishedExecutable = $publishedTree | Where-Object Path -CEQ (Join-Path $destination 'SyncShark.exe')
        $publishedExecutable.VerifyFilePath((Join-Path $activePath 'SyncShark.exe'))
        $state.Committed = $true
        Write-SetupFrame $journal $state
        $journal.Delete(); $journal = $null
        Write-Output ([Massdex.SyncShark.SetupMessages]::Text('install.success', [object[]]@($version)))
    } catch {
        $failure = $_
        if ($publishedTree) { foreach ($item in $publishedTree) { $item.Dispose() }; $publishedTree = $null }
        if ($active) { $active.Dispose(); $active = $null }; if ($next) { $next.Dispose(); $next = $null }
        if ($stageHandle) { $stageHandle.Dispose(); $stageHandle = $null }
        if ($journal) {
            try { Restore-SetupTransaction $Root $journal }
            catch { throw ([Massdex.SyncShark.SetupMessages]::Text('install.rollback', [object[]]@($failure, $_, $Root))) }
        } elseif ($manifest -and $stage -and ([SetupFileSystem]::Exists($stage))) {
            Remove-SetupTree $stage $manifest
        }
        throw $failure
    } finally {
        if ($publishedTree) { foreach ($item in $publishedTree) { $item.Dispose() } }
        if ($journal) { $journal.Dispose() }; if ($active) { $active.Dispose() }; if ($next) { $next.Dispose() }
        if ($stageHandle) { $stageHandle.Dispose() }; if ($claim) { $claim.Dispose() }
        if ($versions) { foreach ($item in $versions) { $item.Dispose() } }
        foreach ($item in $parents) { $item.Dispose() }
        if ($stage -and ([SetupFileSystem]::Exists($stage))) {
            Write-Warning ([Massdex.SyncShark.SetupMessages]::Text('preparation.retained', [object[]]@($stage)))
        }
    }
}

if (-not $FunctionsOnly) {
    try { Invoke-SetupInstall $Payload (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Massdex\SyncShark') }
    catch { [Console]::Error.WriteLine($_); exit 1 }
}
