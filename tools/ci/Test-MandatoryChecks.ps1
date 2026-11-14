# Enforcement fixtures: run on hosted CI, never build or run product tests.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'MandatoryChecks.ps1')
$required = @('ratchet', 'layout', 'dependencies', 'literals', 'strings')
foreach ($case in @(
    @{ Result = 'success'; State = 'PASS'; Code = 0 },
    @{ Result = 'failure'; State = 'FAIL'; Code = 1 },
    @{ Result = 'skipped'; State = 'INCOMPLETE'; Code = 2 },
    @{ Result = 'cancelled'; State = 'INCOMPLETE'; Code = 2 },
    @{ Result = $null; State = 'INCOMPLETE'; Code = 2 }
)) {
    foreach ($check in $required) {
        $results = @{}
        foreach ($name in $required) { $results[$name] = 'success' }
        $results[$check] = $case.Result
        $actual = Get-MandatoryCheckOutcome -Results $results -Required $required
        if ($actual.State -ne $case.State -or $actual.ExitCode -ne $case.Code) {
            throw "$check=$($case.Result): expected $($case.State)/$($case.Code), got $actual"
        }
        Write-Host "PASS fixture $check=$($case.Result) -> $($actual.State)/$($actual.ExitCode)"
    }
}
$mixed = Get-MandatoryCheckOutcome -Required $required -Results @{ ratchet = 'failure' }
if ($mixed.State -ne 'INCOMPLETE' -or $mixed.Failed -notcontains 'ratchet') {
    throw 'Incomplete evidence must retain known failures.'
}

# Exercise the real entry point as a child process, including its exit status.
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = & pwsh -NoProfile -File (Join-Path $repo 'tools/Test-PreReview.ps1') -Repo $repo -Base HEAD -SkipRatchet 2>&1
$code = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($code -ne 2 -or ($output -join "`n") -notmatch 'SUMMARY  INCOMPLETE' -or
    ($output -join "`n") -notmatch 'mandatory check incomplete: ratchet' -or
    ($output -join "`n") -match 'SUMMARY\s+PASS') {
    throw "Skipped ratchet must be INCOMPLETE/2, not PASS (exit $code)."
}
if (($output -join "`n") -match 'CHECK receipt\s+ERROR|engine omits ''(dependencies|literals|strings)''') {
    throw 'Pre-review prerequisite or effective mandatory selection is incompatible.'
}
Write-Host 'PASS negative fixture: real pre-review -SkipRatchet -> INCOMPLETE/2'
# The expected child exit code must not become the hosted fixture step's exit code.
exit 0
