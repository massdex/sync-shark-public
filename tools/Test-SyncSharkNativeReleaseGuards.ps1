param([Parameter(Mandatory=$true)][string]$BuildRoot, [switch]$ProductRootOnly)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force
if ($ProductRootOnly) {
    # Invoke the actual module resolver without staging, signing or installation.
    $expected = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Massdex\SyncShark'
    if ((Resolve-SyncSharkProductRoot '') -cne $expected -or
        (Resolve-SyncSharkProductRoot '  ') -cne $expected) {
        throw 'The normal product root is not the current Windows known folder.'
    }
    Write-Output 'PASS Normal and blank routing use the current Windows LocalApplicationData folder.'
    $prior = $env:SYNC_SHARK_RELEASE_TEST_MODE
    try {
        $testRoot = Join-Path (Join-Path ([IO.Path]::GetTempPath()) 'sync-shark-release-tests') 'profile-routing-read-only'
        $env:SYNC_SHARK_RELEASE_TEST_MODE = '0'
        try { Resolve-SyncSharkProductRoot $testRoot; throw 'Disabled test root was admitted.' }
        catch { if ($_.Exception.Message -cne 'The test product-root seam is disabled.') { throw } }
        Write-Output 'PASS The test product-root seam is refused outside the explicit existing test mode.'
        $env:SYNC_SHARK_RELEASE_TEST_MODE = '1'
        if ((Resolve-SyncSharkProductRoot $testRoot) -cne (Get-SyncSharkNormalizedPath $testRoot)) {
            throw 'The existing bounded test-root route changed.'
        }
        Write-Output 'PASS The existing fixed child test-root route remains available.'
        try { Resolve-SyncSharkProductRoot ([IO.Path]::GetTempPath()); throw 'Escaped test root was admitted.' }
        catch { if ($_.Exception.Message -cne 'The test product root must be a child of the fixed Sync Shark release-test root.') { throw } }
        Write-Output 'PASS A parent or escaped test product-root route remains rejected.'
    }
    finally { $env:SYNC_SHARK_RELEASE_TEST_MODE = $prior }
    Write-Output 'PASS Product-root routing only: no directory, package, credential or installed runtime changed.'
    return
}
$build=[IO.Path]::GetFullPath($BuildRoot)
Assert-SyncSharkOrdinaryTree $build -RootOnly
$source=Join-Path $build 'bin\SyncShark\Debug\net8.0-windows10.0.19041.0'
$scope=Join-Path $build 'native-release-checks'
Assert-SyncSharkWithin $build $scope
Assert-SyncSharkOrdinaryTree $scope -AllowMissing
$root=Join-Path $scope ([guid]::NewGuid().ToString('N'))
Assert-SyncSharkWithin $scope $root
[void][IO.Directory]::CreateDirectory($root)
$package=Join-Path $root 'package'
[void][IO.Directory]::CreateDirectory($package)
$manifest=Join-Path $root 'manifest.json'
$observationPath=Join-Path $root 'observations.json'
$packageSigner=(Get-SyncSharkSignerIdentity).roles.package
$thumb=@($packageSigner.thumbprints)[0]
$subject=$packageSigner.subject
$spki=@($packageSigner.spki)[0]
$script:passed=0
function Check([bool]$Value,[string]$Label) {
    if(-not $Value){throw $Label}; $script:passed++; Write-Output "PASS $Label"
}
function Reject([scriptblock]$Action,[string]$Expected,[string]$Label) {
    try { & $Action } catch {
        if($_.Exception.Message -notlike "*$Expected*"){throw}
        $script:passed++; Write-Output "PASS $Label"; return
    }
    throw "Expected rejection: $Label"
}
try {
    Check ((Get-Command Invoke-SyncSharkExplorerPackage -CommandType Function -ErrorAction Stop).ModuleName -eq 'SyncShark.Release') 'Ordinary installer caller can resolve the exported Explorer lifecycle command'
    $contract=[pscustomobject]@{signedCoreSha256=('a'*64);signedUiSha256=('b'*64);signedExeSha256=('c'*64)}
    Check ((Get-SyncSharkSignedMemberHashes $contract).Count -eq 3) 'Legacy three-binary signed contracts remain readable'
    $contract | Add-Member signedExplorerSha256 ('d'*64)
    $contract | Add-Member signedDiscoverySha256 ('e'*64)
    $five=Get-SyncSharkSignedMemberHashes $contract
    Check ($five.Count -eq 5 -and $five.ContainsKey('SyncSharkDiscovery.exe')) 'Explorer and exact non-dotted Discovery identity are bound'
    $contract | Add-Member signedIdentityPackageSha256 ('f'*64)
    Check ((Get-SyncSharkSignedMemberHashes $contract)['SyncShark.msix'] -ceq ('f'*64)) 'Modern Explorer package has its own signed identity'
    $contract.signedIdentityPackageSha256=''
    Reject { Get-SyncSharkSignedMemberHashes $contract } 'invalid signedIdentityPackageSha256' 'An empty identity-package hash cannot downgrade its signature requirement'
    $contract.signedIdentityPackageSha256='f'*64
    $contract.signedExplorerSha256=''
    Reject { Get-SyncSharkSignedMemberHashes $contract } 'invalid signedExplorerSha256' 'Present empty native hash cannot downgrade to unsigned'
    $contract.signedExplorerSha256='D'*64
    Reject { Get-SyncSharkSignedMemberHashes $contract } 'invalid signedExplorerSha256' 'Malformed native hashes fail closed'

    $installed=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Massdex\SyncShark\versions\0.3.18\SyncShark.exe'
    $observation=Get-SyncSharkSignatureObservation $installed
    Assert-SyncSharkSignatureObservation $observation $thumb $subject $subject $spki
    Check $true 'Actual installed signature and signature-bound RFC3161 timestamp pass the Windows SIP and pinned policy'
    $altered=Join-Path $root 'altered.exe'
    [IO.File]::Copy($installed,$altered)
    $stream=[IO.File]::Open($altered,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {$stream.Position=1024;$byte=$stream.ReadByte();$stream.Position=1024;$stream.WriteByte([byte]($byte -bxor 1))} finally {$stream.Dispose()}
    Reject { Get-SyncSharkSignatureObservation $altered } 'Windows signature integrity verification failed' 'A changed signed executable is rejected even though its certificate and timestamp remain present'

    $names=@('SyncShark.Core.dll','SyncShark.dll','SyncShark.exe','SyncShark.Explorer.dll')
    foreach($name in $names) { [IO.File]::Copy((Join-Path $source $name),(Join-Path $package $name)) }
    $version=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $package 'SyncShark.dll')).ProductVersion
    $hashes=@{}; $members=@(); $observations=@{}
    foreach($name in $names) {
        $hashes[$name]=Get-SyncSharkSha256 (Join-Path $package $name)
        $members+=@{path=$name;sha256=$hashes[$name]}
        # The existing explicit release-test seam tests the production decision, not
        # cryptographic signing. Final release still requires real signer/timestamp proof.
        $observations[$name]=[pscustomobject]@{
            Thumbprint=$thumb;Subject=$subject;Issuer=$subject;SpkiSha256=$spki
            Status='NotTrusted';SignerChainStatuses=@('UntrustedRoot')
            Rfc3161Present=$true;Rfc3161CryptographicallyValid=$true;Rfc3161=$true
            TimestampSignatureBound=$true;TimestampChainEvaluatedAtTokenTime=$true
            TimestampChainValid=$true;TimestampChainStatuses=@()
        }
    }
    [IO.File]::WriteAllText($manifest,(@{members=$members}|ConvertTo-Json -Depth 5))
    [IO.File]::WriteAllText($observationPath,($observations|ConvertTo-Json -Depth 5))
    $manifestHash=Get-SyncSharkSha256 $manifest
    function Verify([hashtable]$Bound=$hashes) {
        Assert-SyncSharkExactPackage $package $manifest $manifestHash $version $Bound $thumb $subject $subject $spki $observationPath $true
    }
    Verify
    Check $true 'Actual four-member package reaches every signed-component decision'
    $missing=$hashes.Clone(); $missing.Remove('SyncShark.Explorer.dll')
    Reject { Verify $missing } 'no signed-member identity' 'Native component cannot be accepted using only its unsigned manifest hash'
    $extra=$hashes.Clone(); $extra['other.exe']='a'*64
    Reject { Verify $extra } 'Unexpected signed member' 'Arbitrary extra executable cannot enter the native signature contract'
    $extra=$hashes.Clone(); $extra['SyncSharkDiscovery.exe']='a'*64
    Reject { Verify $extra } 'absent from the manifest' 'A claimed native component must actually be in the package'
    $wrong=$hashes.Clone(); $wrong['SyncShark.Explorer.dll']='0'*64
    Reject { Verify $wrong } 'Package identity mismatch' 'Changed native bytes fail before any install mutation'
    $observations['SyncShark.Explorer.dll'].Status='NotSigned'
    [IO.File]::WriteAllText($observationPath,($observations|ConvertTo-Json -Depth 5))
    Reject { Verify } 'Authenticode status is not permitted' 'Explorer goes through the actual signature-acceptance guard'
    $observations['SyncShark.Explorer.dll'].Status='NotTrusted'
    $observations['SyncShark.Explorer.dll'].TimestampSignatureBound=$false
    [IO.File]::WriteAllText($observationPath,($observations|ConvertTo-Json -Depth 5))
    Reject { Verify } 'signature-bound RFC3161' 'Native timestamp must be bound to that binary signature'
    [IO.File]::WriteAllText($manifest,(@{members=@($members)+@($members[0])}|ConvertTo-Json -Depth 5))
    $manifestHash=Get-SyncSharkSha256 $manifest
    Reject { Verify } 'duplicate member path' 'Duplicate package members are rejected'
    Write-Output "$script:passed native release-contract checks passed; no cryptographic signing or installation claimed."
} finally {
    Assert-SyncSharkWithin $scope $root
    Assert-SyncSharkOrdinaryTree $root
    Remove-Item -LiteralPath $root -Recurse -Force
    if(Test-Path -LiteralPath $root){throw 'The isolated release fixture was not cleaned'}
}
