# Run against a disposable built and extracted house package, never an installed copy.
param([Parameter(Mandatory)][string]$Root)
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
if ([IO.Directory]::Exists($active) -or [IO.File]::Exists((Join-Path $target 'Sync Shark.exe')) -or [IO.File]::Exists((Join-Path $target 'syncshark.exe'))) { throw 'Uninstall did not remove both executables and their launcher.' }
if ([IO.File]::ReadAllText((Join-Path $state 'keep.txt')) -cne 'user state' -or [IO.File]::ReadAllText((Join-Path $target 'foreign.txt')) -cne 'not a package member') { throw 'Uninstall damaged unowned files.' }
