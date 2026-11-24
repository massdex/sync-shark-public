# Shared mandatory fence selection and completion semantics for CI and pre-review.
function Get-MandatoryEngineGroups {
    @('dependencies', 'literals', 'strings')
}

function Get-MandatoryCheckOutcome {
    param([hashtable]$Results, [string[]]$Required)
    $incomplete = @($Required | Where-Object { $Results[$_] -notin @('success', 'failure') })
    $failed = @($Required | Where-Object { $Results[$_] -eq 'failure' })
    [pscustomobject]@{
        State = $(if ($incomplete.Count) { 'INCOMPLETE' } elseif ($failed.Count) { 'FAIL' } else { 'PASS' })
        ExitCode = $(if ($incomplete.Count) { 2 } elseif ($failed.Count) { 1 } else { 0 })
        Incomplete = $incomplete
        Failed = $failed
    }
}
