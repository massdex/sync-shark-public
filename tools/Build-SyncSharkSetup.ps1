[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishedApp,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$PublishedApp = [IO.Path]::GetFullPath($PublishedApp).TrimEnd('\','/')
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $PublishedApp 'SyncShark.exe')).ProductVersion.Split('+')[0]
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'The published application has no stable version.' }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$work = Join-Path $OutputDirectory ('setup-build-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'app'
[IO.Directory]::CreateDirectory($payload) | Out-Null
# Copy the published runtime, excluding unused native integrations, tools and symbols.
Get-ChildItem -LiteralPath $PublishedApp -File -Recurse | Where-Object {
    $_.Name -notmatch '(?i)(Discovery|Explorer|Updater|Setup)' -and $_.Extension -ne '.pdb' -and
    $_.FullName.Substring($PublishedApp.Length + 1) -notmatch '^tools[\\/]'
} | ForEach-Object {
    $target = Join-Path $payload ($_.FullName.Substring($PublishedApp.Length + 1))
    [IO.Directory]::CreateDirectory((Split-Path $target)) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $target
}
foreach ($name in @('SyncShark.exe','SyncShark.dll','SyncShark.Core.dll','SyncShark.Engine.dll','SyncShark.deps.json','SyncShark.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $name))) { throw "Missing published runtime member: $name" }
}
$archive = Join-Path $work 'payload.zip'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$setup = Join-Path $OutputDirectory "SyncShark-Setup-v$version.exe"
if (Test-Path -LiteralPath $setup) { throw 'Setup output already exists; use a new output directory.' }
& $compiler /nologo /target:exe /optimize+ /reference:System.Xml.dll "/out:$setup" "/resource:$archive,payload.zip" "/resource:$(Join-Path $repository 'Installer/setup.ps1'),setup.ps1" "/resource:$(Join-Path $repository 'Installer/SetupFileSystem.cs'),SetupFileSystem.cs" "/resource:$(Join-Path $repository 'Installer/SetupMessages.cs'),SetupMessages.cs" "/resource:$(Join-Path $repository 'Installer/setup-strings.xml'),setup-strings.xml" "/win32manifest:$(Join-Path $repository 'Installer/setup.manifest')" (Join-Path $repository 'Installer/SyncSharkSetup.cs') (Join-Path $repository 'Installer/SetupMessages.cs')
if ($LASTEXITCODE -ne 0) { throw "Setup compilation failed: $LASTEXITCODE" }
Write-Host "Unsigned setup: $setup. Owner signs this final executable separately. Retained build inputs: $work"
