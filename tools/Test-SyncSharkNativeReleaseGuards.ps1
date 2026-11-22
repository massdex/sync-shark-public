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
& (Join-Path $PSScriptRoot 'Test-SyncSharkPublisherRoles.ps1') -BuildRoot $BuildRoot
# Exact published bytes, both PE pins, and the negative CLI hash case belong to Test-SyncSharkReleaseTransactions.
