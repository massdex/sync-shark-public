param([Parameter(Mandatory=$true)][string]$BuildRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force
. (Join-Path $PSScriptRoot 'SyncShark.DiscoverySetup.ps1')
$build=[IO.Path]::GetFullPath($BuildRoot)
$allocation=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SyncShark\build'
Assert-SyncSharkWithin $allocation $build
Assert-SyncSharkOrdinaryTree $build -RootOnly
$scope=Join-Path $build 'native-release-checks'
Assert-SyncSharkOrdinaryTree $scope -AllowMissing
$root=Join-Path $scope ([guid]::NewGuid().ToString('N'))
Assert-SyncSharkWithin $scope $root
[void][IO.Directory]::CreateDirectory($root)
$signerIdentity=Get-SyncSharkSignerIdentity
$current=@($signerIdentity.roles.publisher.thumbprints)[0]
$legacy=@($signerIdentity.roles.package.thumbprints)[0]
$subject=$signerIdentity.roles.publisher.subject
$legacySubject=$signerIdentity.roles.package.subject
$spki=@($signerIdentity.roles.publisher.spki)[0]
$script:passed=0
function Check([bool]$Value,[string]$Label){if(!$Value){throw $Label};$script:passed++;'PASS '+$Label}
function Reject([scriptblock]$Action,[string]$Expected,[string]$Label){try{& $Action}catch{if($_.Exception.Message -notlike ('*'+$Expected+'*')){throw};Check $true $Label;return};throw ('Expected rejection: '+$Label)}
function Observation([bool]$Container){
    [ordered]@{Status='Valid';Thumbprint=$(if($Container){$legacy}else{$current});Subject=$(if($Container){$legacySubject}else{$subject});Issuer=$(if($Container){$legacySubject}else{$subject});SpkiSha256=$spki;SignerChainStatuses=@();Rfc3161Present=$true;Rfc3161CryptographicallyValid=$true;Rfc3161=$true;TimestampSignatureBound=$true;TimestampChainEvaluatedAtTokenTime=$true;TimestampChainValid=$true;TimestampChainStatuses=@()}
}
try {
    $installed=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Massdex\SyncShark\versions\0.3.30'
    $oldObservation=Get-SyncSharkSignatureObservation (Join-Path $installed 'SyncShark.exe')
    Assert-SyncSharkSignatureObservation $oldObservation $legacy $legacySubject $legacySubject $spki
    Reject {Assert-SyncSharkSignatureObservation $oldObservation $current $subject $subject $spki} 'Pinned signer identity mismatch' 'Actual valid predecessor signature cannot authorize a current executable even with the same key'
    $package=Join-Path $root 'package';[void][IO.Directory]::CreateDirectory($package)
    $members=@();$hashes=@{};$observations=@{}
    foreach($name in @('SyncShark.exe','SyncShark.dll','SyncShark.Core.dll','SyncShark.Explorer.dll','SyncSharkDiscovery.exe','SyncShark.msix')) {
        [IO.File]::Copy((Join-Path $installed $name),(Join-Path $package $name))
        $hashes[$name]=Get-SyncSharkSha256 (Join-Path $package $name)
        $members+=@{path=$name;sha256=$hashes[$name]}
        $observations[$name]=Observation ($name -ceq 'SyncShark.msix')
    }
    $manifest=Join-Path $root 'manifest.json';$observationPath=Join-Path $root 'observations.json'
    [IO.File]::WriteAllText($manifest,(@{members=$members}|ConvertTo-Json -Depth 5))
    $manifestHash=Get-SyncSharkSha256 $manifest
    $version=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $package 'SyncShark.exe')).ProductVersion
    function Verify {
        [IO.File]::WriteAllText($observationPath,($observations|ConvertTo-Json -Depth 6))
        Assert-SyncSharkExactPackage $package $manifest $manifestHash $version $hashes $current $subject $subject $spki $observationPath $true
    }
    Verify;Check $true 'Production six-member package policy accepts current executable and legacy container roles through the explicit isolated observation seam'
    foreach($name in $hashes.Keys) {
        $correct=$observations[$name]
        $observations[$name]=Observation ($name -cne 'SyncShark.msix')
        Reject {Verify} 'Pinned signer identity mismatch' ('Wrong certificate role is rejected for '+$name)
        $observations[$name]=$correct
    }
    $observations['SyncShark.msix'].TimestampSignatureBound=$false
    Reject {Verify} 'signature-bound RFC3161' 'Container role still requires its signature-bound timestamp'
    $observations['SyncShark.msix'].TimestampSignatureBound=$true
    $observations['SyncSharkDiscovery.exe'].TimestampChainValid=$false
    Reject {Verify} 'signature-bound RFC3161' 'Current helper role still requires valid timestamp trust'
    $observations['SyncSharkDiscovery.exe'].TimestampChainValid=$true
    [IO.File]::WriteAllText($observationPath,($observations|ConvertTo-Json -Depth 6))
    Reject {Assert-SyncSharkExactPackage $package $manifest $manifestHash $version $hashes $current $subject $subject $spki $observationPath $false} 'isolated release-test seam' 'Production package admission cannot inject signature observations'
    $worker='C:\Program Files\Massdex\SyncShark\SyncSharkDiscovery.exe'
    $oldHash='d8a3faa5ef1c3f760e346d21b0925f420536e224070fa33c6a0ec1bdef05e256'
    $oldManifest='1ef7f297db09cc97d1231108789b2ecc38e9c431ef947966d28ca8af9e3e57b1'
    Check (@((Get-SyncSharkDiscoverySigner $worker).Thumbprints) -contains $current) 'Current Discovery role never automatically admits the old certificate'
    Check (@((Get-SyncSharkDiscoverySigner $worker $oldHash $oldManifest -RetainedProtectedPair).Thumbprints) -contains $legacy) 'Exact protected29 predecessor retains its own signer for setup rollback'
    Check (@((Get-SyncSharkDiscoverySigner ($worker+'.previous') $oldHash $oldManifest -RetainedProtectedPair).Thumbprints) -contains $legacy) 'The same exact predecessor remains recognizable after the protected transaction moves it'
    Check (@((Get-SyncSharkDiscoverySigner ($worker+'.previous') 'c879d94e2b7fba765f06e6913c3273737af8a029fa918f329579e3a4ddfc5b20' 'ebad10fd3214afc2fa9c46f2b07e5ba7bfefac2c90f499917f537472bfb2312a' -RetainedProtectedPair).Thumbprints) -contains $legacy) 'Existing protected28 rollback can be verified before replacement'
    Reject {Get-SyncSharkDiscoverySigner $worker $oldHash ('0'*64) -RetainedProtectedPair} 'exact retained preimage' 'Legacy binary with substituted protected manifest is rejected'
    Reject {Get-SyncSharkDiscoverySigner (Join-Path $installed 'SyncSharkDiscovery.exe') $oldHash $oldManifest -RetainedProtectedPair} 'exact protected Discovery pair' 'Legacy admission cannot escape to a user-owned current executable'
    Check (@((Get-SyncSharkDiscoverySigner $worker ('a'*64) ('b'*64) -RetainedProtectedPair).Thumbprints) -contains $current) 'An unrecognized protected binary must satisfy the current signer, not an open legacy allowlist'
    Write-Output "$script:passed publisher role checks passed. Role decisions and previous-signature rejection only; no successor signing, trust, installation or protected setup claimed."
} finally {
    Assert-SyncSharkWithin $scope $root
    Assert-SyncSharkOrdinaryTree $root
    Remove-Item -LiteralPath $root -Recurse -Force
    if(Test-Path -LiteralPath $root){throw 'Publisher role fixture cleanup failed'}
}
