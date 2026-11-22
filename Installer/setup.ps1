param([string]$Payload, [switch]$FunctionsOnly)
$ErrorActionPreference = 'Stop'
$script:SetupHost = '@HOST@'
$script:SetupLayout = '@LAYOUT@'
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
    $hostPath = Join-Path $PreviousTarget $script:SetupHost
    if (-not [SetupFileSystem]::Exists($hostPath)) { $hostPath = Join-Path $PreviousTarget 'SyncShark.exe' }
    if ($Candidate -le (Get-SetupVersion $hostPath)) {
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

function Open-SetupTree([string]$Root, $Manifest, [switch]$RequireComplete) {
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
                [SetupFileSystem]::OpenDirectory($path)
            } else {
                [SetupFileSystem]::OpenFile($path)
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

# Earlier unproved preparations belong to nobody we can identify. Never enumerate them for cleanup.
function Clear-SetupPreparation([string]$Root) {
    [IO.Directory]::CreateDirectory((Join-Path $Root 'versions/.staging')) | Out-Null
}

# Compare the observed, no-follow tree to the signed setup's layout and archive bytes, never to itself.
function Assert-SetupPayload($Tree, [string]$Archive) {
    $files = [string[]](ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($script:SetupLayout))))
    $expected = [Collections.Generic.HashSet[string]]::new($files,[StringComparer]::Ordinal)
    $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [void]$directories.Add('')
    foreach ($name in $files) {
        for ($parent=[IO.Path]::GetDirectoryName($name); $parent; $parent=[IO.Path]::GetDirectoryName($parent)) { [void]$directories.Add($parent.Replace('\','/')) }
    }
    $observed = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Tree) {
        $name = $item.Path.TrimStart('\').Replace('\','/')
        if ($item.Directory) { if ($directories.Remove($name)) { continue } }
        elseif ($expected.Remove($name)) { $observed.Add($name,$item.Hash); continue }
        throw ([Massdex.SyncShark.SetupMessages]::Text('recovery.foreign',[object[]]@($name)))
    }
    if ($expected.Count) { throw ([Massdex.SyncShark.SetupMessages]::Text('payload.missing',[object[]]@(($expected -join ', ')))) }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($Archive); $sha=[Security.Cryptography.SHA256]::Create()
    try {
        foreach ($entry in $zip.Entries) {
            if (-not $entry.Name) { continue }
            $name=$entry.FullName.Replace('\','/'); $inputStream=$entry.Open()
            try { $hash=[BitConverter]::ToString($sha.ComputeHash($inputStream)).Replace('-','') } finally { $inputStream.Dispose() }
            if (-not $observed.ContainsKey($name) -or $hash -cne $observed[$name]) { throw ([Massdex.SyncShark.SetupMessages]::Text('recovery.changed',[object[]]@($name))) }
            [void]$observed.Remove($name)
        }
        if ($observed.Count) { throw ([Massdex.SyncShark.SetupMessages]::Text('manifest.invalid',[object[]]@())) }
    } finally { $sha.Dispose(); $zip.Dispose() }
}

function Invoke-SetupInstall([string]$Archive, [string]$Root) {
    $Root = [IO.Path]::GetFullPath($Root)
    $parents = Open-SetupDirectories $Root -Create
    $alreadyActive = $false
    $versions = $claim = $active = $stageHandle = $published = $null
    try {
        $versions = Open-SetupDirectories (Join-Path $Root 'versions') -Create
        $claim = [SetupFileSystem]::OpenClaim((Join-Path $Root 'versions/setup.lock'))
        Clear-SetupPreparation $Root
        $versions.Add([SetupFileSystem]::OpenDirectory((Join-Path $Root 'versions/.staging')))
        $stage = Join-Path $Root ('versions/.staging/' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null
        $stageHandle = [SetupFileSystem]::OpenDirectory($stage)
        Expand-Archive -LiteralPath $Archive -DestinationPath $stage
        $version = Get-SetupVersion (Join-Path $stage $script:SetupHost)
        $destination = Join-Path $Root ('versions/' + $version)
        $activePath = Join-Path $Root 'active'
        if ([SetupFileSystem]::Exists($activePath)) {
            $active = [SetupFileSystem]::OpenJunction($activePath)
            $alreadyActive = [StringComparer]::OrdinalIgnoreCase.Equals($active.JunctionTarget(),$destination)
            if (-not $alreadyActive) { Assert-SetupUpgrade $version $active.JunctionTarget() }
        }
        $expected = Get-SetupTree $stage
        Assert-SetupPayload $expected $Archive
        if ([SetupFileSystem]::Exists($destination)) {
            # Interrupted after the completed folder was published: reuse only the same package bytes.
            $observed = Get-SetupTree $destination
            $published = Open-SetupTree $destination $observed -RequireComplete
            $byPath = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
            foreach ($entry in $expected) { $byPath.Add($entry.Path,$entry) }
            if ($observed.Count -ne $expected.Count) { throw ([Massdex.SyncShark.SetupMessages]::Text('version.occupied',[object[]]@())) }
            foreach ($entry in $observed) {
                $want = $byPath[$entry.Path]
                if ($null -eq $want -or $entry.Directory -ne $want.Directory -or $entry.Hash -cne $want.Hash) { throw ([Massdex.SyncShark.SetupMessages]::Text('version.occupied',[object[]]@())) }
            }
        } else {
            $stageHandle.Dispose(); $stageHandle = [SetupFileSystem]::OpenMutableDirectory($stage)
            if ($stageHandle.Identity -cne $expected[0].Identity) { throw ([Massdex.SyncShark.SetupMessages]::Text('recovery.changed',[object[]]@($stage))) }
            $stageHandle.Rename($destination); $stageHandle.Dispose(); $stageHandle = $null
            $published = Open-SetupTree $destination $expected -RequireComplete
        }
        if ($alreadyActive) { return $version }
        if ($active) { $active.Retarget($active.ReadJunction(),[SetupFileSystem]::JunctionData($destination)) }
        else {
            $pointer = $stage + '.active'
            New-Item -ItemType Junction -Path $pointer -Target $destination | Out-Null
            $active = [SetupFileSystem]::OpenJunction($pointer); $active.Rename($activePath)
        }
        return $version
    } finally {
        foreach ($item in $published) { $item.Dispose() }
        if ($active) { $active.Dispose() }; if ($stageHandle) { $stageHandle.Dispose() }; if ($claim) { $claim.Dispose() }
        foreach ($item in $versions) { $item.Dispose() }; foreach ($item in $parents) { $item.Dispose() }
    }
}

if (-not $FunctionsOnly) {
    try {
        $root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) '@PRODUCT@'
        $version = Invoke-SetupInstall $Payload $root
        Import-Module (Join-Path (Join-Path $root 'active') '@REGISTRATION@') -Force
        Invoke-SyncSharkAppRegistration $version Register -HostExecutable $script:SetupHost
        Invoke-SyncSharkAppRegistration $version Verify -HostExecutable $script:SetupHost
        Write-Output ([Massdex.SyncShark.SetupMessages]::Text('install.success',[object[]]@($version)))
    }
    catch { [Console]::Error.WriteLine($_); exit 1 }
}
