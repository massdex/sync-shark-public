# The full solution owns all four production and test project builds.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRevision)
$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot '../../SyncShark.sln'
& dotnet restore $solution --locked-mode
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& dotnet build $solution -c Release --no-restore "-p:SyncSharkSourceRevision=$SourceRevision" --nologo
exit $LASTEXITCODE
