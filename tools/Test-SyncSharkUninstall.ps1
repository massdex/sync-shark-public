# Run against a disposable built and extracted house package, never an installed copy.
param([Parameter(Mandatory)][string]$Root,[Parameter(Mandatory)][string]$Archive)
$ErrorActionPreference='Stop'
$rootPath=[IO.Path]::GetFullPath($Root)
if (-not $rootPath.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase)) { throw 'Expected a disposable fixture directory.' }
$active=Join-Path $rootPath 'active'; $junction=[SetupFileSystem]::OpenJunction($active); try { $target=$junction.JunctionTarget() } finally { $junction.Dispose() }
$drop=Join-Path $active 'tools/SyncShark.Uninstall.ps1'
. $drop
$state=Join-Path $rootPath 'state'; [void][IO.Directory]::CreateDirectory($state)
[IO.File]::WriteAllText((Join-Path $state 'keep.txt'),'user state')
[IO.File]::WriteAllText((Join-Path $target 'foreign.txt'),'not a package member')
Remove-SyncSharkPackage $rootPath { param($version) if ($version -ne $script:PackageVersion) { throw 'Wrong registered version.' } }
if ([SetupFileSystem]::Exists($active)) { throw 'Uninstall left its launcher.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($Archive)
try {
    $entries=@($zip.Entries | Where-Object Name)
    if (-not $entries.Count) { throw 'Empty package oracle.' }
    foreach ($entry in $entries) {
        if ([SetupFileSystem]::Exists((Join-Path $target $entry.FullName))) { throw "Uninstall left archive member: $($entry.FullName)" }
    }
} finally { $zip.Dispose() }
if ([IO.File]::ReadAllText((Join-Path $state 'keep.txt')) -cne 'user state' -or [IO.File]::ReadAllText((Join-Path $target 'foreign.txt')) -cne 'not a package member') { throw 'Uninstall damaged unowned files.' }
