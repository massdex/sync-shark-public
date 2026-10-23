# Embedded in the signed app; runs through the existing PowerShell release bridge.
# Uninstall removes the active installation by moving only the exact installed
# runtime into its existing transaction. No settings, credentials or synced paths
# are deleted. The immediate runtime and existing state rollback remain intact.
function Get-SyncSharkVerifiedUninstallRollback([object]$Prior,[string]$Version,[string]$Commit,[string]$Root,
    [string]$Target,[string]$Retired,[string]$Pointer) {
    # A failed uninstall stays unresolved unless it carries its own verified
    # rollback record that still matches the exact restored state. The recorded
    # claim alone is never enough.
    if($Prior.status -cne 'active' -or -not $Prior.artifacts.PSObject.Properties['rollback']) {
        throw 'An earlier uninstall transaction must be reconciled before another attempt.'
    }
    $records=@($Prior.artifacts.rollback)
    if($records.Count -ne 1){throw 'An earlier uninstall transaction must be reconciled before another attempt.'}
    $record=$records[0]
    foreach($name in @('verified_utc','failure_class','failure_reason','version','commit','tree','journal_preimage_sha256')) {
        if($null -eq $record.PSObject.Properties[$name]){throw 'An earlier uninstall transaction must be reconciled before another attempt.'}
    }
    if([string]::IsNullOrWhiteSpace([string]$record.verified_utc) -or
        [string]::IsNullOrWhiteSpace([string]$record.failure_reason) -or
        $record.version -cne $Version -or $record.commit -cne $Commit -or
        $record.tree -cnotmatch '^[0-9a-f]{64}$' -or $record.journal_preimage_sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'An earlier uninstall transaction must be reconciled before another attempt.'
    }
    if((Get-SyncSharkTreeIdentity $Target) -cne $record.tree){
        throw 'The restored runtime does not match the recorded rollback.'}
    Assert-SyncSharkJunction (Join-Path $Root 'active') $Target
    foreach($path in @($Retired,$Pointer)) {
        if(Test-Path -LiteralPath $path){throw 'An earlier uninstall transaction must be reconciled before another attempt.'}
    }
    $priorHistory=@()
    if($Prior.artifacts.PSObject.Properties['history']){$priorHistory=@($Prior.artifacts.history)}
    [pscustomobject]@{ verified_utc=$record.verified_utc; failure_class=$record.failure_class;
        failure_reason=$record.failure_reason; version=$record.version; commit=$record.commit; tree=$record.tree;
        journal_preimage_sha256=$record.journal_preimage_sha256; history=@($priorHistory) }
}

function Get-SyncSharkUninstallHistory([object]$Plan) {
    # Prior failure and rollback records carry forward as one flat history array.
    # Journals are never nested and sole evidence is never discarded.
    $history=@()
    if($null -ne $Plan.previous_receipt -and $Plan.previous_receipt.PSObject.Properties['kind'] -and
        $Plan.previous_receipt.kind -ceq 'verified-rollback') {
        $history=@($Plan.previous_receipt.record.history)+@($Plan.previous_receipt.record)
    }
    $history
}

function Get-SyncSharkUninstallPlan {
    param([string]$CurrentVersion, [string]$TestProductRoot = '')
    if ($CurrentVersion -cnotmatch '^(\d+\.\d+\.\d+)\+([0-9a-f]{40})$') { throw 'Uninstall requires an exact installed source/version identity.' }
    $version = $Matches[1]; $commit = $Matches[2]
    $root = Resolve-SyncSharkProductRoot $TestProductRoot
    $target = Resolve-SyncSharkRuntimeRoot $version $TestProductRoot
    $transaction = Join-Path $root ("replacement-staging\transactions\$version-" + $commit.Substring(0,7))
    Assert-SyncSharkWithin $root $transaction
    Assert-SyncSharkOrdinaryTree $root -RootOnly
    Assert-SyncSharkOrdinaryTree (Join-Path $root 'versions')
    Assert-SyncSharkJunction (Join-Path $root 'active') $target
    Assert-SyncSharkOrdinaryTree $transaction -RootOnly
    $install = Get-Content -LiteralPath (Join-Path $transaction 'install-journal.json') -Raw | ConvertFrom-Json -Depth 20
    if ($install.operation -cne 'install' -or $install.version -cne $version -or $install.commit -cne $commit -or
        $install.status -cne 'complete' -or (Get-SyncSharkNormalizedPath $install.product_root) -cne (Get-SyncSharkNormalizedPath $root)) {
        throw 'The installed release transaction is not complete or has a different identity.'
    }
    if (-not $TestProductRoot) {
        $publisher = (Get-SyncSharkSignerIdentity).roles.publisher
        $thumb = @($publisher.thumbprints)
        $subject = $publisher.subject
        $spki = @($publisher.spki)
        foreach ($name in @('SyncShark.exe','SyncShark.dll','SyncShark.Core.dll')) {
            $path = Join-Path $target $name
            if ([Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion -cne $CurrentVersion) { throw 'Installed uninstall member identity changed.' }
            Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $path) $thumb $subject $subject $spki
        }
        Assert-SyncSharkProtectedRuntime $target
    }
    $journal = Join-Path $transaction 'uninstall-journal.json'
    $retired = if($TestProductRoot){Join-Path $transaction 'runtime.uninstalled'}else{$target+'.uninstalled'}
    $pointer = Join-Path $transaction 'active.uninstalled'
    foreach ($path in @($retired, $pointer)) {
        if($path -eq $pointer -or $TestProductRoot){Assert-SyncSharkWithin $transaction $path}
        if (Test-Path -LiteralPath $path) { throw 'An earlier uninstall transaction must be reconciled before another attempt.' }
    }
    $previousReceipt=$null
    if(Test-Path -LiteralPath $journal) {
        $prior=Read-SyncSharkJournal $journal 'uninstall' $version $commit $root @('retain-active-pointer','retain-runtime','verify-uninstalled')
        if($prior.status -ceq 'complete' -and $prior.artifacts.PSObject.Properties['restored_utc'] -and
            -not [string]::IsNullOrWhiteSpace([string]$prior.artifacts.restored_utc)) {
            # The existing successful uninstall + undo retry path.
            if($prior.artifacts.runtime_sha256 -cne (Get-SyncSharkTreeIdentity $target)) {
                throw 'An earlier uninstall transaction must be reconciled before another attempt.'
            }
            $previousReceipt=[pscustomobject]@{sha256=(Get-SyncSharkSha256 $journal);kind='restored-undo';restored_utc=$prior.artifacts.restored_utc}
        } else {
            $rollback=Get-SyncSharkVerifiedUninstallRollback $prior $version $commit $root $target $retired $pointer
            $previousReceipt=[pscustomobject]@{sha256=(Get-SyncSharkSha256 $journal);kind='verified-rollback';record=$rollback}
        }
    }
    [pscustomobject]@{ root=$root; testMode=(-not [string]::IsNullOrWhiteSpace($TestProductRoot)); version=$version; commit=$commit; target=$target; transaction=$transaction;
        journal=$journal; retired=$retired; pointer=$pointer; tree=(Get-SyncSharkTreeIdentity $target); previous_receipt=$previousReceipt }
}

function Assert-SyncSharkUninstallPlan([object]$Plan) {
    if ($Plan.version -cnotmatch '^\d+\.\d+\.\d+$' -or $Plan.commit -cnotmatch '^[0-9a-f]{40}$' -or $Plan.tree -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid uninstall identity.' }
    $root = Resolve-SyncSharkProductRoot $(if ($Plan.testMode) { $Plan.root } else { '' })
    $transaction = Join-Path $root ("replacement-staging\transactions\" + $Plan.version + '-' + $Plan.commit.Substring(0,7))
    $runtime=Resolve-SyncSharkRuntimeRoot $Plan.version $(if($Plan.testMode){$Plan.root}else{''})
    $exact = @{ root=$root; target=$runtime; transaction=$transaction;
        journal=(Join-Path $transaction 'uninstall-journal.json'); retired=$(if($Plan.testMode){Join-Path $transaction 'runtime.uninstalled'}else{$runtime+'.uninstalled'}); pointer=(Join-Path $transaction 'active.uninstalled') }
    foreach ($name in $exact.Keys) {
        if ((Get-SyncSharkNormalizedPath $Plan.$name) -cne (Get-SyncSharkNormalizedPath $exact[$name])) { throw 'Uninstall targets changed. Nothing was removed.' }
    }
    Assert-SyncSharkOrdinaryTree $root -RootOnly
    Assert-SyncSharkOrdinaryTree (Join-Path $root 'versions') -RootOnly
    Assert-SyncSharkOrdinaryTree $transaction -RootOnly
}

function Complete-SyncSharkUninstall {
    param([object]$Plan)
    Assert-SyncSharkUninstallPlan $Plan
    $root = $Plan.root; $active = Join-Path $root 'active'
    foreach ($path in @($Plan.transaction,$Plan.journal,$Plan.pointer)) { Assert-SyncSharkWithin $root $path }
    if ((Get-SyncSharkTreeIdentity $Plan.target) -cne $Plan.tree) { throw 'Installed bytes changed while waiting for exit. Uninstall was not attempted.' }
    Assert-SyncSharkJunction $active $Plan.target
    if($Plan.previous_receipt) {
        if((Get-SyncSharkSha256 $Plan.journal) -cne $Plan.previous_receipt.sha256){throw 'The previous undo receipt changed. Uninstall was not attempted.'}
    } elseif(Test-Path -LiteralPath $Plan.journal){throw 'An uninstall transaction appeared after preparation. Nothing was removed.'}
    $steps = @('retain-active-pointer','retain-runtime','verify-uninstalled')
    $journal = [pscustomobject]@{ schema=1; operation='uninstall'; version=$Plan.version; commit=$Plan.commit; product_root=$root;
        status='active'; pending=$null; completed=@(); allowed_steps=$steps;
        artifacts=[pscustomobject]@{ runtime_sha256=$Plan.tree; settings_retained=$true; recovery_retained=$true;
            restored_utc=$null; previous_undo_receipt=$Plan.previous_receipt; history=(Get-SyncSharkUninstallHistory $Plan) } }
    Write-SyncSharkJournal $Plan.journal $journal
    try {
        Invoke-SyncSharkJournalStep $journal $Plan.journal 'retain-active-pointer' '' {
            Assert-SyncSharkJunction $active $Plan.target
            Move-Item -LiteralPath $active -Destination $Plan.pointer
        } { Assert-SyncSharkJunction $Plan.pointer $Plan.target; if (Test-Path -LiteralPath $active) { throw 'The active application pointer remains.' } }
        Invoke-SyncSharkJournalStep $journal $Plan.journal 'retain-runtime' '' {
            if ((Get-SyncSharkTreeIdentity $Plan.target) -cne $Plan.tree) { throw 'Runtime identity changed before retirement.' }
            if($Plan.testMode){Move-Item -LiteralPath $Plan.target -Destination $Plan.retired}
            else {Invoke-SyncSharkProtectedRuntime (Join-Path $root ('versions\'+$Plan.version)) RuntimeRemove}
        } { if ((Get-SyncSharkTreeIdentity $Plan.retired) -cne $Plan.tree) { throw 'Retained runtime identity mismatch.' } }
        Invoke-SyncSharkJournalStep $journal $Plan.journal 'verify-uninstalled' '' {} {
            if (Test-Path -LiteralPath $active) { throw 'The active installation still exists.' }
            if (Test-Path -LiteralPath $Plan.target) { throw 'The installed version still exists.' }
            if ((Get-SyncSharkTreeIdentity $Plan.retired) -cne $Plan.tree) { throw 'Uninstall rollback identity mismatch.' }
        }
    }
    catch {
        # The same bounded object is restored; never reconstruct or delete user state.
        $originalFailure = $_
        $failureClass = $originalFailure.Exception.GetType().Name
        $failureReason = [string]$originalFailure.Exception.Message
        if ($failureReason.Length -gt 512) { $failureReason = $failureReason.Substring(0,512) }
        $journalPreimage = Get-SyncSharkSha256 $Plan.journal
        $pendingPhase = $journal.pending
        $completedPhases = @($journal.completed)
        try {
        if (Test-Path -LiteralPath $Plan.retired) {
            if (Test-Path -LiteralPath $Plan.target) { throw 'Both runtime locations exist; preserve them for recovery.' }
            if ((Get-SyncSharkTreeIdentity $Plan.retired) -cne $Plan.tree) { throw 'Retained runtime changed; preserve it for recovery.' }
            if($Plan.testMode){Move-Item -LiteralPath $Plan.retired -Destination $Plan.target}
            else {Invoke-SyncSharkProtectedRuntime (Join-Path $root ('versions\'+$Plan.version)) RuntimeInstall -RestoringUninstall}
        }
        if (Test-Path -LiteralPath $Plan.pointer) {
            Assert-SyncSharkJunction $Plan.pointer $Plan.target
            if (Test-Path -LiteralPath $active) { throw 'Both application pointers exist; preserve them for recovery.' }
            Move-Item -LiteralPath $Plan.pointer -Destination $active
        }
        # Verify the exact restored identities, never the attempt's intent.
        if ((Get-SyncSharkTreeIdentity $Plan.target) -cne $Plan.tree) { throw 'The restored runtime does not match the uninstall plan.' }
        Assert-SyncSharkJunction $active $Plan.target
        foreach ($path in @($Plan.retired, $Plan.pointer)) {
            if (Test-Path -LiteralPath $path) { throw 'A retired uninstall location remains.' }
        }
        if (-not $Plan.testMode) { Assert-SyncSharkProtectedRuntime $Plan.target }
        }
        catch {
            # Preserve both failures. The exact journal still passes its own
            # identity/ownership checks and its expected preimage, so the pair is
            # recorded as an UNVERIFIED failure artifact with the existing journal
            # writer. This is never a verified rollback and never grants a retry.
            $rollbackFailure = $_
            $rollbackClass = $rollbackFailure.Exception.GetType().Name
            $rollbackReason = [string]$rollbackFailure.Exception.Message
            if ($rollbackReason.Length -gt 512) { $rollbackReason = $rollbackReason.Substring(0,512) }
            $persistenceFailure = $null
            try {
                $confirmed = Read-SyncSharkJournal $Plan.journal 'uninstall' $Plan.version $Plan.commit $root $steps
                if ($null -eq $confirmed -or (Get-SyncSharkSha256 $Plan.journal) -cne $journalPreimage) {
                    throw 'The uninstall journal no longer matches its verified preimage.'
                }
                $failureRecord = [pscustomobject]@{
                    verified = $false
                    recorded_utc = [DateTimeOffset]::UtcNow.ToString('O')
                    original_failure_class = $failureClass
                    original_failure_reason = $failureReason
                    rollback_failure_class = $rollbackClass
                    rollback_failure_reason = $rollbackReason
                    version = $Plan.version
                    commit = $Plan.commit
                    tree = $Plan.tree
                    journal_preimage_sha256 = $journalPreimage
                    pending_phase = $pendingPhase
                    completed_phases = @($completedPhases)
                }
                $journal.artifacts | Add-Member -NotePropertyName rollback_failure -NotePropertyValue $failureRecord -Force
                Write-SyncSharkJournal $Plan.journal $journal
            }
            catch { $persistenceFailure = $_ }
            if ($null -ne $persistenceFailure) {
                # A failed persistence must not hide either prior failure.
                throw ('Uninstall failed (' + $failureClass + ': ' + $failureReason +
                    ') and its rollback could not be verified (' + $rollbackClass + ': ' + $rollbackReason +
                    '); the unverified failure record could not be persisted (' +
                    $persistenceFailure.Exception.GetType().Name + ': ' + $persistenceFailure.Exception.Message +
                    '). Both retained locations are preserved, the journal stays unresolved and no retry is granted.')
            }
            throw ('Uninstall failed (' + $failureClass + ': ' + $failureReason +
                ') and its rollback could not be verified (' + $rollbackClass + ': ' + $rollbackReason +
                '). Both retained locations are preserved, the unresolved failure is recorded as unverified and no retry is granted.')
        }
        # Only a completely verified rollback earns a durable rollback record. The
        # original pending/completed phases, failure class/reason, exact
        # source/version/tree and journal preimage hash are retained, and the
        # journal status stays active rather than fictitiously complete.
        $rollbackRecord = [pscustomobject]@{
            verified_utc = [DateTimeOffset]::UtcNow.ToString('O')
            failure_class = $failureClass
            failure_reason = $failureReason
            version = $Plan.version
            commit = $Plan.commit
            tree = $Plan.tree
            journal_preimage_sha256 = $journalPreimage
            pending_phase = $pendingPhase
            completed_phases = @($completedPhases)
            history = Get-SyncSharkUninstallHistory $Plan
        }
        $journal.artifacts | Add-Member -NotePropertyName rollback -NotePropertyValue $rollbackRecord -Force
        Write-SyncSharkJournal $Plan.journal $journal
        throw $originalFailure
    }
}

function Restore-SyncSharkUninstalledRuntime {
    param([object]$Plan)
    Assert-SyncSharkUninstallPlan $Plan
    $active = Join-Path $Plan.root 'active'
    foreach ($path in @($Plan.pointer,$Plan.journal)) { Assert-SyncSharkWithin $Plan.root $path }
    $journal = Read-SyncSharkJournal $Plan.journal 'uninstall' $Plan.version $Plan.commit $Plan.root @('retain-active-pointer','retain-runtime','verify-uninstalled')
    if ($null -eq $journal -or $journal.status -cne 'complete' -or $journal.artifacts.runtime_sha256 -cne $Plan.tree) {
        throw 'The completed uninstall receipt does not match this retained runtime.'
    }
    if ($journal.artifacts.PSObject.Properties['restored_utc'] -and
        -not [string]::IsNullOrWhiteSpace($journal.artifacts.restored_utc)) { throw 'This uninstall was already restored. Nothing was overwritten.' }
    $activeItem=Get-Item -LiteralPath $active -Force -ErrorAction SilentlyContinue
    $pointerItem=Get-Item -LiteralPath $Plan.pointer -Force -ErrorAction SilentlyContinue
    if (($null -eq $activeItem) -eq ($null -eq $pointerItem)) { throw 'Expected exactly one retained or restored application pointer.' }
    $pointerToCheck=if($null -ne $pointerItem){$pointerItem}else{$activeItem}
    # Inspect the link itself even while its retained target is absent. Never
    # restore runtime bytes before rejecting a foreign/dangling redirection.
    if($pointerToCheck.LinkType -cne 'Junction' -or
        (Get-SyncSharkNormalizedPath ([string]$pointerToCheck.Target)) -cne (Get-SyncSharkNormalizedPath $Plan.target)) {
        throw 'The uninstall application pointer changed. Nothing was restored.'
    }
    $targetPresent=Test-Path -LiteralPath $Plan.target
    $retiredPresent=Test-Path -LiteralPath $Plan.retired
    if($targetPresent -eq $retiredPresent){throw 'Expected exactly one retained or restored runtime; preserve both locations.'}
    $runtimeToCheck=if($targetPresent){$Plan.target}else{$Plan.retired}
    if ((Get-SyncSharkTreeIdentity $runtimeToCheck) -cne $Plan.tree) { throw 'The retained runtime no longer matches the uninstall receipt.' }
    if($null -ne $activeItem -and -not $targetPresent){throw 'An active pointer appeared before runtime restoration.'}
    if(-not $targetPresent) {
        if($Plan.testMode){Move-Item -LiteralPath $Plan.retired -Destination $Plan.target}
        else {Invoke-SyncSharkProtectedRuntime (Join-Path $Plan.root ('versions\'+$Plan.version)) RuntimeInstall -RestoringUninstall}
    } elseif(-not $Plan.testMode) {
        Assert-SyncSharkProtectedRuntime $Plan.target
    }
    if($null -ne $pointerItem) {
        Assert-SyncSharkJunction $Plan.pointer $Plan.target
        Move-Item -LiteralPath $Plan.pointer -Destination $active
    }
    Assert-SyncSharkJunction $active $Plan.target
    $journal.artifacts | Add-Member -NotePropertyName restored_utc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O')) -Force
    Write-SyncSharkJournal $Plan.journal $journal
}

function Invoke-SyncSharkUninstall {
    param([string]$CurrentVersion, [int]$ParentProcessId)
    $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
    if ($ParentProcessId -le 0) { throw 'The installed application must request its own safe exit.' }
    $plan = Get-SyncSharkUninstallPlan $CurrentVersion
    $parent = Get-Process -Id $ParentProcessId -ErrorAction Stop
    $expected = Join-Path $plan.target 'SyncShark.exe'
    $path = Get-SyncSharkNormalizedPath $parent.Path
    if ($path -ine $expected -and $path -ine (Join-Path $plan.root 'active\SyncShark.exe')) { throw 'The uninstall caller is not the installed application.' }
    $script:SyncSharkReleaseReady=$true
    [Console]::Out.WriteLine('{"ready":true}'); [Console]::Out.Flush()
    if (-not $parent.WaitForExit(90000)) { throw 'The app did not complete its safe exit. Nothing was uninstalled.' }
    if (@(Get-Process -Name SyncShark -ErrorAction SilentlyContinue).Count) { throw 'Sync Shark restarted. Nothing was uninstalled.' }
    Complete-SyncSharkUninstall $plan
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class SyncSharkRemovalMessage { [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int MessageBox(IntPtr owner, string text, string caption, uint kind); }'
    $answer = [SyncSharkRemovalMessage]::MessageBox([IntPtr]::Zero,
        "Sync Shark is uninstalled. Your local and cloud files, saved accounts, settings and recovery records were kept. The exact app runtime is retained only for rollback.`n`nUndo uninstall and reopen Sync Shark? Sync remains paused until you resume it.", 'Sync Shark Uninstalled', 0x124)
    if ($answer -eq 6) {
        Restore-SyncSharkUninstalledRuntime $plan
        Start-Process -FilePath (Join-Path $plan.root 'active\SyncShark.exe') -WindowStyle Hidden
    }
}
