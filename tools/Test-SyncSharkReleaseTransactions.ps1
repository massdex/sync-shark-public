# Windows, PowerShell 7.4+: verify a prepared signed house release without launching setup or opening the live store.
param([Parameter(Mandatory)][string]$ReleaseRoot,[Parameter(Mandatory)][string]$PredecessorRuntime,
      [Parameter(Mandatory)][string]$SourceCommit)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force
$installer=Join-Path $ReleaseRoot 'deploy-scripts/Install-SyncShark.ps1'
$signer=(Get-SyncSharkSignerIdentity).roles.publisher
Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $installer) $signer.thumbprints $signer.subject $signer.subject $signer.spki
$prefix='# SyncSharkUpdateContract: '
$header=[IO.File]::ReadLines($installer) | Select-Object -First 1
if (!$header.StartsWith($prefix,[StringComparison]::Ordinal)) { throw 'Missing installer contract.' }
$contract=$header.Substring($prefix.Length) | ConvertFrom-Json
if ($contract.product -cne 'Massdex.SyncShark' -or $contract.sourceRevision -cne $SourceCommit) { throw 'Wrong release identity.' }
$expected=$contract.version+'+'+$SourceCommit
$previousHost=Join-Path $PredecessorRuntime $contract.hostExecutable
if (![IO.File]::Exists($previousHost)) { $previousHost=Join-Path $PredecessorRuntime 'SyncShark.exe' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($previousHost).ProductVersion -cne $contract.previousVersion -or
    (Get-SyncSharkTreeIdentity $PredecessorRuntime) -cne $contract.previousTreeSha256) { throw 'Incorrect predecessor.' }
$hashes=Get-SyncSharkSignedMemberHashes $contract
$package=Join-Path $ReleaseRoot 'house-stage/app'
$manifest=Join-Path $ReleaseRoot 'package/payload-manifest.json'
Assert-SyncSharkExactPackage $package $manifest $contract.manifestSha256 $expected $hashes $signer.thumbprints $signer.subject $signer.subject $signer.spki '' $false
# Exercise the staging copy across the first-read split, including the remaining stream and an empty file.
$fixture = [IO.Directory]::CreateTempSubdirectory()
try {
    $source = $fixture.CreateSubdirectory('source').FullName
    $staged = Join-Path $fixture.FullName 'staged'
    $bytes = [byte[]]::new([IO.FileStreamOptions]::new().BufferSize + 1)
    $bytes[0] = 1; $bytes[-1] = 2
    [IO.File]::WriteAllBytes((Join-Path $source 'payload'), $bytes)
    [IO.File]::WriteAllBytes((Join-Path $source 'empty'), [byte[]]@())
    Copy-SyncSharkTreeToStaging $source $staged
    if ((Get-SyncSharkSha256 (Join-Path $staged 'payload')) -cne (Get-SyncSharkSha256 (Join-Path $source 'payload')) -or
        [IO.File]::ReadAllBytes((Join-Path $staged 'empty')).Length -ne 0) { throw 'Staged file contents differ.' }
} finally { $fixture.Delete($true) }
$wrong=$hashes.Clone(); $wrong[$contract.cliExecutable]='0'*64
try { Assert-SyncSharkExactPackage $package $manifest $contract.manifestSha256 $expected $wrong $signer.thumbprints $signer.subject $signer.subject $signer.spki '' $false; throw 'Altered CLI pin was admitted.' }
catch { if ($_.Exception.Message -notlike 'Package identity mismatch:*') { throw } }
$setup=Join-Path $ReleaseRoot ('deploy-scripts/SyncShark-Setup-v'+$contract.version+'.exe')
if ((Get-SyncSharkSha256 $setup) -cne $contract.setupSha256) { throw 'Incorrect setup hash.' }
Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $setup) $signer.thumbprints $signer.subject $signer.subject $signer.spki
