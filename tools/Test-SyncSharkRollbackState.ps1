param([Parameter(Mandatory=$true)][string]$DataAllocation)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force
$env:SYNC_SHARK_RELEASE_TEST_MODE='1'
if(Test-Path -LiteralPath $DataAllocation){throw 'Refuse to reuse a data allocation.'}
$installation=Join-Path ([IO.Path]::GetTempPath()) ('sync-shark-release-tests\rollback-'+[guid]::NewGuid().ToString('N'))
$dataRoot=[IO.Path]::GetFullPath($DataAllocation)
if([IO.Path]::GetPathRoot($installation) -ieq [IO.Path]::GetPathRoot($dataRoot)){throw 'This fixture requires different volumes.'}
$tokens=$null;$errors=$null
$text=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Rollback-SyncShark.ps1.template')).Replace('__SIGNED_PROOF_RESTORATION__','# signed proof')
$ast=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
if($errors.Count){throw ($errors | Out-String)}
foreach($name in @('Get-RollbackReadinessStamp','Assert-RollbackReadinessStamp','Remove-PreservedStateContents','Preserve-LiveState','Assert-PreservedState','Restore-PredecessorState','Assert-RestoredState','Get-ExpectedSuccessorStateIdentity')){
    $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true)
    if($null -eq $node){throw "Missing production function: $name"}
    . ([scriptblock]::Create($node.Extent.Text))
}
$stateBackup=Join-Path $installation 'state.predecessor'
$successorState=Join-Path $dataRoot 'replacement-staging\rollback\state.successor'
$restoreStagingRoot=Join-Path $dataRoot 'replacement-staging\rollback\state.restore.preparing'
$preserveStaging=Join-Path $dataRoot 'replacement-staging\rollback\state.preserve.preparing'
$CrashAfter=''
$testMode=$true
$journal=[pscustomobject]@{completed=@();artifacts=[pscustomobject]@{successor_state_sha256=$null;pending_successor_state_sha256=$null}}
try {
    [void][IO.Directory]::CreateDirectory($installation)
    foreach($relative in @('state','vNext\state','engine')){
        [void][IO.Directory]::CreateDirectory((Join-Path $dataRoot $relative))
        [void][IO.Directory]::CreateDirectory((Join-Path $stateBackup $relative))
        [IO.File]::WriteAllText((Join-Path $dataRoot "$relative\fixture.db"),'successor-'+$relative)
        [IO.File]::WriteAllText((Join-Path $stateBackup "$relative\fixture.db"),'predecessor-'+$relative)
    }
    [IO.File]::WriteAllText((Join-Path $dataRoot 'engine\fixture.db-wal'),'successor-wal')
    [IO.File]::WriteAllText((Join-Path $stateBackup 'engine\fixture.db-wal'),'predecessor-wal')
    $lock=Join-Path $dataRoot 'state\instance.lock'
    [IO.File]::WriteAllText($lock,'live-lock')
    [IO.File]::WriteAllText((Join-Path $stateBackup 'state\instance.lock'),'old-lock')
    [IO.File]::WriteAllText((Join-Path $installation 'product-root.txt'),$dataRoot)
    function Assert-WriterExcluded {
        $writer=$null
        try {$writer=[IO.File]::Open($lock,'Open','ReadWrite','None')} catch [IO.IOException] {return}
        finally {if($null -ne $writer){$writer.Dispose()}}
        throw 'Engine writer admitted during rollback.'
    }
    $preflight = Get-RollbackReadinessStamp
    [IO.File]::WriteAllText((Join-Path $dataRoot 'engine\fixture.db-wal'),'changed-after-preflight')
    $refused=$false
    try { Invoke-SyncSharkWithInstanceMaintenanceLease $installation { Assert-RollbackReadinessStamp $preflight } }
    catch { if($_.Exception.Message -notlike '*readiness changed*'){throw};$refused=$true }
    if(-not $refused -or (Test-Path -LiteralPath $successorState)){throw 'Preflight race was not refused before effects.'}
    [IO.File]::WriteAllText((Join-Path $dataRoot 'engine\fixture.db-wal'),'successor-wal')
    Invoke-SyncSharkWithInstanceMaintenanceLease $installation {
        Assert-RollbackReadinessStamp $preflight
        Assert-WriterExcluded
        [void][IO.Directory]::CreateDirectory($preserveStaging)
        [IO.File]::WriteAllText((Join-Path $preserveStaging 'fixture.db'),'partial-preservation')
        foreach($relative in @('state','vNext\state','engine')){
            $journal.artifacts.pending_successor_state_sha256=Get-ExpectedSuccessorStateIdentity $relative
            Preserve-LiveState $relative
            Assert-PreservedState $relative
            # Replay the exact durable after-effect boundary before recording completion.
            Preserve-LiveState $relative
            Assert-WriterExcluded
        }
        if([IO.File]::ReadAllText($lock) -cne 'live-lock'){throw 'Held lock was replaced.'}
        foreach($relative in @('engine','state','vNext\state')){
            [void][IO.Directory]::CreateDirectory((Join-Path $restoreStagingRoot $relative))
            [IO.File]::WriteAllText((Join-Path $restoreStagingRoot "$relative\fixture.db"),'partial-restoration')
            Restore-PredecessorState $relative
            Assert-RestoredState $relative
            Restore-PredecessorState $relative
            Assert-WriterExcluded
        }
        if([IO.File]::ReadAllText($lock) -cne 'live-lock'){throw 'Restoration replaced the held lock.'}
        if([IO.File]::ReadAllText((Join-Path $successorState 'engine\fixture.db-wal')) -cne 'successor-wal'){throw 'Successor WAL lost.'}
        if([IO.File]::ReadAllText((Join-Path $dataRoot 'engine\fixture.db-wal')) -cne 'predecessor-wal'){throw 'Predecessor WAL not restored.'}
    }
    $writer=[IO.File]::Open($lock,'Open','ReadWrite','None');$writer.Dispose()
    'PASS cross-volume rollback, replay, WAL preservation and continuous shared writer exclusion'
} finally {
    if(Test-Path -LiteralPath $installation){Remove-Item -LiteralPath $installation -Recurse -Force}
    if(Test-Path -LiteralPath $dataRoot){Remove-Item -LiteralPath $dataRoot -Recurse -Force}
}
