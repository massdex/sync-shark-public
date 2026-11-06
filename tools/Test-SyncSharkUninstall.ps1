param()
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force
. (Join-Path $PSScriptRoot 'SyncShark.Uninstall.ps1')
$env:SYNC_SHARK_RELEASE_TEST_MODE='1'
$scope=Join-Path ([IO.Path]::GetTempPath()) 'sync-shark-release-tests'
$root=Join-Path $scope ([guid]::NewGuid().ToString('N'))
$version='0.3.19'; $commit='a'*40
$target=Join-Path $root "versions\$version"
$transaction=Join-Path $root 'replacement-staging\transactions\0.3.19-aaaaaaa'
$active=Join-Path $root 'active'; $passed=0
$extraRoots=[Collections.Generic.List[object]]::new()
function Check([bool]$Value,[string]$Label) { if(-not $Value){throw $Label}; $script:passed++; "PASS $Label" }
function Reject([scriptblock]$Action,[string]$Label) {
    try { & $Action } catch { $script:passed++; "PASS $Label"; return }; throw "Expected rejection: $Label"
}
try {
    foreach($path in @($target,$transaction,(Join-Path $root 'state'),(Join-Path $root 'vNext\state'))) { [void][IO.Directory]::CreateDirectory($path) }
    [IO.File]::WriteAllText((Join-Path $target 'fixture.txt'),'exact disposable runtime')
    [IO.File]::WriteAllText((Join-Path $root 'state\settings.txt'),'retained settings')
    [IO.File]::WriteAllText((Join-Path $root 'vNext\state\recovery.txt'),'retained recovery')
    [IO.File]::WriteAllText((Join-Path $transaction 'install-journal.json'),(@{operation='install';version=$version;commit=$commit;status='complete';product_root=$root}|ConvertTo-Json))
    New-Item -ItemType Junction -Path $active -Target $target | Out-Null
    $settings=Get-SyncSharkTreeIdentity (Join-Path $root 'state'); $recovery=Get-SyncSharkTreeIdentity (Join-Path $root 'vNext\state')
    $plan=Get-SyncSharkUninstallPlan "$version+$commit" $root
    Check ($plan.tree -eq (Get-SyncSharkTreeIdentity $target)) 'Plan binds the exact current runtime before safe exit'
    $bad=$plan.PSObject.Copy(); $bad.target=Join-Path $root 'state'
    Reject { Complete-SyncSharkUninstall $bad } 'Settings cannot be substituted as a runtime target'
    Check (Test-Path -LiteralPath $active) 'Rejected plan leaves the active application intact'
    [IO.File]::AppendAllText((Join-Path $target 'fixture.txt'),' changed')
    Reject { Complete-SyncSharkUninstall $plan } 'Changed bytes after preparation stop uninstall'
    [IO.File]::WriteAllText((Join-Path $target 'fixture.txt'),'exact disposable runtime')
    Complete-SyncSharkUninstall $plan
    Check (-not (Test-Path -LiteralPath $active) -and -not (Test-Path -LiteralPath $target)) 'Actual isolated uninstall removes the active pointer and installed runtime'
    Check ((Get-SyncSharkTreeIdentity $plan.retired) -eq $plan.tree) 'One exact runtime is retained for undo without a second copy'
    Check ((Get-SyncSharkTreeIdentity (Join-Path $root 'state')) -eq $settings) 'Saved settings are byte-for-byte unchanged'
    Check ((Get-SyncSharkTreeIdentity (Join-Path $root 'vNext\state')) -eq $recovery) 'Recovery state is byte-for-byte unchanged'
    Restore-SyncSharkUninstalledRuntime $plan
    Check ((Get-SyncSharkJunctionTarget $active) -eq (Get-SyncSharkNormalizedPath $target)) 'Undo restores the exact active application pointer'
    Check ((Get-SyncSharkTreeIdentity $target) -eq $plan.tree) 'Undo restores the exact runtime bytes'
    Reject { Restore-SyncSharkUninstalledRuntime $plan } 'Repeated undo never overwrites a current installation'
    $retry=Get-SyncSharkUninstallPlan "$version+$commit" $root
    Check ($retry.previous_receipt.sha256 -eq (Get-SyncSharkSha256 $plan.journal)) 'A new uninstall after undo binds the previous restored receipt'
    Reject { Complete-SyncSharkUninstall $plan } 'The original uninstall plan cannot replay after undo'
    Complete-SyncSharkUninstall $retry
    Check (-not (Test-Path -LiteralPath $active)) 'A freshly prepared uninstall works after undo'
    # Reproduce interruption after the runtime rename but before its pointer.
    Move-Item -LiteralPath $retry.retired -Destination $retry.target
    Restore-SyncSharkUninstalledRuntime $retry
    Check ((Get-SyncSharkTreeIdentity $target) -eq $plan.tree) 'Interrupted runtime restore resumes without accumulating copies'
    $pointerRetry=Get-SyncSharkUninstallPlan "$version+$commit" $root
    Complete-SyncSharkUninstall $pointerRetry
    Move-Item -LiteralPath $pointerRetry.retired -Destination $pointerRetry.target
    Move-Item -LiteralPath $pointerRetry.pointer -Destination $active
    Restore-SyncSharkUninstalledRuntime $pointerRetry
    Check ((Get-SyncSharkTreeIdentity $target) -eq $plan.tree) 'Interrupted pointer publication completes only its missing receipt'
    $foreignRetry=Get-SyncSharkUninstallPlan "$version+$commit" $root
    Complete-SyncSharkUninstall $foreignRetry
    Move-Item -LiteralPath $foreignRetry.retired -Destination $foreignRetry.target
    [IO.File]::AppendAllText((Join-Path $target 'fixture.txt'),' foreign')
    Reject { Restore-SyncSharkUninstalledRuntime $foreignRetry } 'Foreign restored runtime is rejected before pointer publication'
    Check (-not (Test-Path -LiteralPath $active)) 'Rejected foreign runtime cannot reactivate the app'
    [IO.File]::WriteAllText((Join-Path $target 'fixture.txt'),'exact disposable runtime')
    Restore-SyncSharkUninstalledRuntime $foreignRetry
    Check ((Get-SyncSharkTreeIdentity (Join-Path $root 'state')) -eq $settings -and (Get-SyncSharkTreeIdentity (Join-Path $root 'vNext\state')) -eq $recovery) 'Undo leaves both settings and recovery unchanged'
    # SS-D20: a failure after pointer retirement stays durable, and a later
    # attempt is admitted only from its own freshly matched verified rollback.
    # The injection is a real native read-share hold on the runtime directory, so
    # the retirement rename is denied by the filesystem itself.
    $failurePlan=Get-SyncSharkUninstallPlan "$version+$commit" $root
    $runtimeHold=[IO.File]::Open($target,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $originalFailure=$null
    try {
        try { Complete-SyncSharkUninstall $failurePlan } catch { $originalFailure=$_.Exception.Message }
        Check ($null -ne $originalFailure -and $originalFailure.Length -gt 0) 'A denied runtime retirement fails instead of reporting success'
    } finally { $runtimeHold.Dispose() }
    Check ((Get-SyncSharkTreeIdentity $target) -eq $failurePlan.tree) 'The failed attempt restores the exact installed runtime'
    Check ((Get-SyncSharkJunctionTarget $active) -eq (Get-SyncSharkNormalizedPath $target)) 'The failed attempt restores the exact active pointer'
    Check (-not (Test-Path -LiteralPath $failurePlan.retired) -and -not (Test-Path -LiteralPath $failurePlan.pointer)) 'No retired uninstall location is left behind'
    $rolled=Read-SyncSharkJournal $failurePlan.journal 'uninstall' $version $commit $root @('retain-active-pointer','retain-runtime','verify-uninstalled')
    Check ($rolled.status -ceq 'active') 'A rolled-back uninstall journal stays active, never fictitiously complete'
    Check ($rolled.artifacts.PSObject.Properties['rollback'] -and $rolled.artifacts.rollback.failure_reason.Length -gt 0 -and
        $rolled.artifacts.rollback.failure_class -match 'Exception' -and $rolled.artifacts.rollback.tree -ceq $failurePlan.tree -and
        $rolled.artifacts.rollback.pending_phase -ceq 'retain-runtime' -and
        @($rolled.artifacts.rollback.completed_phases) -contains 'retain-active-pointer' -and
        $rolled.artifacts.rollback.journal_preimage_sha256 -match '^[0-9a-f]{64}$') 'The verified rollback retains the original failure, phases, tree and journal preimage'
    Reject { Restore-SyncSharkUninstalledRuntime $failurePlan } 'A failed but rolled-back uninstall is never treated as a completed uninstall'
    # Plain copies of the record identity, because the tampering cases below share
    # the very object a shallow journal copy retains.
    $rollbackPreimage=$rolled.artifacts.rollback.journal_preimage_sha256
    $rollbackPending=$rolled.artifacts.rollback.pending_phase
    $journalBackup=Join-Path $root 'uninstall-journal.backup.json'
    [IO.File]::Copy($failurePlan.journal,$journalBackup,$true)
    $tampered=$rolled.PSObject.Copy();$tampered.artifacts.rollback.tree=('0'*64)
    Write-SyncSharkJournal $failurePlan.journal $tampered
    Reject { Get-SyncSharkUninstallPlan "$version+$commit" $root } 'A rollback record that no longer matches the restored runtime refuses'
    $partial=$rolled.PSObject.Copy()
    [void]$partial.artifacts.PSObject.Properties.Remove('rollback')
    Write-SyncSharkJournal $failurePlan.journal $partial
    Reject { Get-SyncSharkUninstallPlan "$version+$commit" $root } 'A partial failure without a verified rollback refuses a retry'
    [IO.File]::Copy($journalBackup,$failurePlan.journal,$true)
    $dual=Join-Path $failurePlan.transaction 'runtime.uninstalled'
    [void][IO.Directory]::CreateDirectory($dual)
    Reject { Get-SyncSharkUninstallPlan "$version+$commit" $root } 'A dual retained runtime refuses a retry'
    Check ([IO.Directory]::Exists($dual)) 'The refused dual state is preserved for recovery'
    [IO.Directory]::Delete($dual)
    [IO.File]::AppendAllText((Join-Path $target 'fixture.txt'),' foreign')
    Reject { Get-SyncSharkUninstallPlan "$version+$commit" $root } 'A foreign restored runtime refuses a retry'
    [IO.File]::WriteAllText((Join-Path $target 'fixture.txt'),'exact disposable runtime')
    $retryPlan=Get-SyncSharkUninstallPlan "$version+$commit" $root
    Check ($retryPlan.previous_receipt.kind -ceq 'verified-rollback' -and
        $retryPlan.previous_receipt.sha256 -eq (Get-SyncSharkSha256 $failurePlan.journal)) 'The next plan binds the verified rollback journal hash'
    Complete-SyncSharkUninstall $retryPlan
    Check (-not (Test-Path -LiteralPath $active) -and -not (Test-Path -LiteralPath $target)) 'The admitted retry completes the real uninstall'
    $rolledForward=Read-SyncSharkJournal $retryPlan.journal 'uninstall' $version $commit $root @('retain-active-pointer','retain-runtime','verify-uninstalled')
    Check ($rolledForward.status -ceq 'complete' -and @($rolledForward.artifacts.history).Count -eq 1 -and
        $rolledForward.artifacts.history[0].journal_preimage_sha256 -ceq $rollbackPreimage -and
        $rolledForward.artifacts.history[0].pending_phase -ceq $rollbackPending) 'The next journal carries the prior failure as one flat history entry'
    function New-UninstallFixtureRoot([string]$Name) {
        # A second, independent isolated installation for the failure-permanence
        # cases; the first fixture is left in its own terminal state.
        $fixtureRoot=Join-Path $scope ($Name+'-'+[guid]::NewGuid().ToString('N'))
        $fixtureTarget=Join-Path $fixtureRoot ('versions\'+$version)
        $fixtureTransaction=Join-Path $fixtureRoot ('replacement-staging\transactions\'+$version+'-'+$commit.Substring(0,7))
        foreach($path in @($fixtureTarget,$fixtureTransaction,(Join-Path $fixtureRoot 'state'),(Join-Path $fixtureRoot 'vNext\state'))) { [void][IO.Directory]::CreateDirectory($path) }
        [IO.File]::WriteAllText((Join-Path $fixtureTarget 'fixture.txt'),'exact disposable runtime')
        [IO.File]::WriteAllText((Join-Path $fixtureRoot 'state\settings.txt'),'retained settings')
        [IO.File]::WriteAllText((Join-Path $fixtureTransaction 'install-journal.json'),(@{operation='install';version=$version;commit=$commit;status='complete';product_root=$fixtureRoot}|ConvertTo-Json))
        $fixtureActive=Join-Path $fixtureRoot 'active'
        New-Item -ItemType Junction -Path $fixtureActive -Target $fixtureTarget | Out-Null
        $fixture=[pscustomobject]@{root=$fixtureRoot;target=$fixtureTarget;transaction=$fixtureTransaction;active=$fixtureActive}
        [void]$extraRoots.Add($fixture)
        $fixture
    }
    # A resolved state change between preparation and completion makes the
    # retirement step fail and then its rollback fail: both failures must be
    # preserved as an UNVERIFIED record with no verified rollback and no retry.
    $dual=New-UninstallFixtureRoot 'dual-failure'
    $dualPlan=Get-SyncSharkUninstallPlan "$version+$commit" $dual.root
    [void][IO.Directory]::CreateDirectory($dualPlan.retired)
    $dualMessage=$null
    try { Complete-SyncSharkUninstall $dualPlan } catch { $dualMessage=$_.Exception.Message }
    Check ($null -ne $dualMessage -and $dualMessage -like '*rollback could not be verified*' -and
        $dualMessage -like '*no retry is granted*') 'A real dual failure reports the original failure and the failed rollback together'
    $dualJournal=Read-SyncSharkJournal $dualPlan.journal 'uninstall' $version $commit $dual.root @('retain-active-pointer','retain-runtime','verify-uninstalled')
    Check ($dualJournal.status -ceq 'active' -and $dualJournal.artifacts.PSObject.Properties['rollback_failure'] -and
        -not $dualJournal.artifacts.PSObject.Properties['rollback'] -and
        $dualJournal.artifacts.rollback_failure.verified -eq $false -and
        $dualJournal.artifacts.rollback_failure.original_failure_reason.Length -gt 0 -and
        $dualJournal.artifacts.rollback_failure.rollback_failure_reason.Length -gt 0 -and
        $dualJournal.artifacts.rollback_failure.pending_phase -ceq 'retain-runtime' -and
        @($dualJournal.artifacts.rollback_failure.completed_phases) -contains 'retain-active-pointer' -and
        $dualJournal.artifacts.rollback_failure.journal_preimage_sha256 -match '^[0-9a-f]{64}$') 'The dual failure is recorded as an unverified artifact with both failures, the prior phases and the preimage hash'
    Reject { Get-SyncSharkUninstallPlan "$version+$commit" $dual.root } 'The unresolved dual failure grants no retry'
    Check ((Test-Path -LiteralPath $dualPlan.retired) -and -not (Test-Path -LiteralPath $dualPlan.target)) 'The dual failure preserves both retained locations for recovery'
    # The same real dual failure with the failure-record write failing: all three
    # failures must surface and the preimage must stay unresolved.
    $script:journalWriteAttempts=0
    $realWriteJournal=(Get-Command Write-SyncSharkJournal -CommandType Function).ScriptBlock
    function Write-SyncSharkJournal([string]$Path,[object]$Journal,[string]$CrashAfter='',[string]$CrashPrefix='journal') {
        if ($Journal.PSObject.Properties['artifacts'] -and $Journal.artifacts.PSObject.Properties['rollback_failure']) {
            $script:journalWriteAttempts=$script:journalWriteAttempts+1
            throw [IO.IOException]::new('injected unverified-failure persistence failure')
        }
        & $realWriteJournal -Path $Path -Journal $Journal -CrashAfter $CrashAfter -CrashPrefix $CrashPrefix
    }
    $writeFail=New-UninstallFixtureRoot 'write-failure'
    $writeFailPlan=Get-SyncSharkUninstallPlan "$version+$commit" $writeFail.root
    [void][IO.Directory]::CreateDirectory($writeFailPlan.retired)
    $writeMessage=$null
    try { Complete-SyncSharkUninstall $writeFailPlan } catch { $writeMessage=$_.Exception.Message }
    Check ($script:journalWriteAttempts -ge 1) 'The unverified failure record write was attempted'
    Check ($null -ne $writeMessage -and $writeMessage -like '*rollback could not be verified*' -and
        $writeMessage -like '*could not be persisted*') 'A failed failure-record write surfaces the original failure, the rollback failure and the persistence failure'
    $writeJournal=Read-SyncSharkJournal $writeFailPlan.journal 'uninstall' $version $commit $writeFail.root @('retain-active-pointer','retain-runtime','verify-uninstalled')
    Check ($writeJournal.status -ceq 'active' -and $writeJournal.pending -ceq 'retain-runtime' -and
        -not $writeJournal.artifacts.PSObject.Properties['rollback_failure'] -and
        -not $writeJournal.artifacts.PSObject.Properties['rollback']) 'A failed persistence leaves the journal unresolved with no failure or rollback authority recorded'
    Reject { Get-SyncSharkUninstallPlan "$version+$commit" $writeFail.root } 'A failed persistence grants no retry'
    "$passed uninstall checks passed; isolated filesystem only, no live application or Windows integration removed."
}
finally {
    # Exactly this disposable fixture; detach only our known junction first.
    if(Test-Path -LiteralPath $active) { Assert-SyncSharkJunction $active $target; [IO.Directory]::Delete($active) }
    $retiredPointer=Join-Path $transaction 'active.uninstalled'
    if([IO.Directory]::Exists($retiredPointer) -or (Get-Item -LiteralPath $retiredPointer -Force -ErrorAction SilentlyContinue)) {
        $link=Get-Item -LiteralPath $retiredPointer -Force
        if($link.LinkType -cne 'Junction' -or [string]$link.Target -ine $target){throw 'Unexpected fixture pointer; cleanup stopped.'}
        [IO.Directory]::Delete($retiredPointer)
    }
    Assert-SyncSharkWithin $scope $root
    Assert-SyncSharkOrdinaryTree $root
    Remove-Item -LiteralPath $root -Recurse -Force
    if(Test-Path -LiteralPath $root){throw 'Disposable uninstall fixture remains.'}
    foreach($fixture in $extraRoots) {
        # Detach only junctions this fixture owns, including a dangling pointer
        # whose retired target was moved, then remove the isolated tree.
        foreach($junction in @($fixture.active,(Join-Path $fixture.transaction 'active.uninstalled'))) {
            $link=Get-Item -LiteralPath $junction -Force -ErrorAction SilentlyContinue
            if($null -eq $link){continue}
            if($link.LinkType -cne 'Junction'){throw ('Unexpected fixture object; cleanup stopped: '+$junction)}
            [IO.Directory]::Delete($junction)
        }
        Assert-SyncSharkWithin $scope $fixture.root
        Remove-Item -LiteralPath $fixture.root -Recurse -Force
        if(Test-Path -LiteralPath $fixture.root){throw ('Disposable uninstall fixture remains: '+$fixture.root)}
    }
    Remove-Item Env:SYNC_SHARK_RELEASE_TEST_MODE
}
