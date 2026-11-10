<#
.SYNOPSIS
G7 (B-161): interleaved baseline and candidate runs of the item-wait-contention measurement.

.DESCRIPTION
The candidate is this checkout's HEAD. The baseline is the same tree with src/ replaced by -BaselineSource's
src/, so the two builds differ only in production code and run the same check (the test exe's --measurement
switch; it is not a CheckRegistry group, so no full CI run includes it). Each round runs both builds once, in ABBA
order across rounds. Each invocation must print exactly one well-formed row per repetition and arm, or the run
fails. Every G7 line is kept in -Out\g7-lines.txt, and -Out\summary.txt lists, per build and arm, the window
counts and, over valid windows only, each metric's values, median and spread (max - min). No threshold is
applied: the comparison against the baseline's repetition spread is read from the summary.
#>
param(
    [Parameter(Mandatory)][string]$BaselineSource,
    [ValidateRange(1, 50)][int]$Rounds = 2,
    [ValidateRange(1, 50)][int]$Repetitions = 1,
    [Parameter(Mandatory)][string]$Out
)
$ErrorActionPreference = 'Stop'

function Invoke-Native([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

# Every G7 row carries exactly these fields, in this order, each with this value shape.
$number = '-?\d+(\.\d+)?'
$fieldShapes = [ordered]@{
    build = '^(baseline|candidate)$'; arm = '^(pending-wait|no-wait)$'; rep = '^[1-9]\d*$'
    workload = '^(valid|invalid)$'; overlap_ms = "^$number$"; c_notice_in_overlap = '^(True|False)$'
    c_upload_in_overlap = '^(True|False)$'; c_latency_ms = "^(held|$number)$"; lock_contention_delta = '^-?\d+$'
    read_item_retry_raw = '^\d+$'; repair_a_ms = "^$number$"; repair_b_ms = "^$number$"; items_per_pair = '^\d+$'
    retained_keys = '^\d+$'
}

# One invocation's rows: exactly one per repetition and arm, every field well formed, labelled with its build, plus
# the missing-evidence line. A skipped, absent, duplicate or malformed row fails the run.
function Read-G7Rows([string]$Log, [string]$Build) {
    $text = @(Get-Content -LiteralPath $Log)
    if (@($text | Where-Object { $_ -clike 'G7 missing-evidence:*' }).Count -ne 1) {
        throw "G7 in $Log printed no single missing-evidence line (skipped or not run)."
    }
    $rows = foreach ($line in $text | Where-Object { $_ -clike 'G7 build=*' }) {
        $row = [ordered]@{}
        foreach ($field in @($line -split ' ' | Select-Object -Skip 1)) {
            $name, $value = $field -split '=', 2
            if ($null -eq $value -or $row.Contains($name)) { throw "Malformed G7 row in ${Log}: $line" }
            $row[$name] = $value
        }
        if ((@($row.Keys) -join ',') -cne (@($fieldShapes.Keys) -join ',')) {
            throw "G7 row in $Log does not carry exactly the expected fields: $line"
        }
        foreach ($name in $fieldShapes.Keys) {
            if ($row[$name] -cnotmatch $fieldShapes[$name]) { throw "G7 field $name is malformed in ${Log}: $line" }
        }
        if ($row.build -cne $Build) { throw "G7 row in $Log is labelled $($row.build), not ${Build}: $line" }
        [pscustomobject]$row
    }
    $expected = @(foreach ($rep in 1..$Repetitions) { foreach ($arm in 'pending-wait', 'no-wait') { "$rep $arm" } })
    $actual = @($rows | ForEach-Object { "$($_.rep) $($_.arm)" })
    $duplicates = @($actual | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
    if ($duplicates.Count) { throw "G7 rows repeat in ${Log}: $($duplicates -join '; ')" }
    $absent = @($expected | Where-Object { $actual -cnotcontains $_ })
    $extra = @($actual | Where-Object { $expected -cnotcontains $_ })
    if ($absent.Count -or $extra.Count) {
        throw "G7 rows in $Log differ from the expected set (absent: $($absent -join '; '); extra: $($extra -join '; '))."
    }
    return $rows
}

$repo = (git rev-parse --show-toplevel).Trim()
$candidateRevision = (git -C $repo rev-parse HEAD).Trim()
$baselineRevision = (git -C $repo rev-parse "$BaselineSource^{commit}").Trim()
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$baseline = Join-Path $Out 'baseline'
$lines = Join-Path $Out 'g7-lines.txt'
Set-Content -LiteralPath $lines -Value @() -Encoding utf8

Invoke-Native 'Creating the baseline worktree' { git -C $repo worktree add --detach $baseline $candidateRevision }
try {
    # Only src/ comes from the baseline; files the candidate added under src/ are removed first.
    Invoke-Native 'Removing the candidate src/' { git -C $baseline rm -r -q src }
    Invoke-Native 'Checking out the baseline src/' { git -C $baseline checkout $baselineRevision -- src }

    $builds = [ordered]@{ baseline = $baseline; candidate = $repo }
    $exe = @{}
    foreach ($name in $builds.Keys) {
        $root = $builds[$name]
        $revision = if ($name -eq 'baseline') { $baselineRevision } else { $candidateRevision }
        Invoke-Native "Building the $name" {
            dotnet build (Join-Path $root 'tests/SyncShark.Engine.Tests') -c Release -nologo -v q `
                "-p:SyncSharkSourceRevision=$revision" -p:UseSharedCompilation=false
        }
        $exe[$name] = Join-Path $root 'tests/SyncShark.Engine.Tests/bin/Release/net8.0-windows/SyncShark.Engine.Tests.exe'
        if (-not (Test-Path -LiteralPath $exe[$name] -PathType Leaf)) { throw "Test executable not found: $($exe[$name])" }
    }

    $env:G7_REPETITIONS = "$Repetitions"
    for ($round = 1; $round -le $Rounds; $round++) {
        $order = if ($round % 2 -eq 1) { @('baseline', 'candidate') } else { @('candidate', 'baseline') }
        foreach ($name in $order) {
            $env:G7_BUILD = $name
            $log = Join-Path $Out "round-$round-$name.log"
            Write-Host "::group::G7 round $round $name"
            & $exe[$name] --measurement item-wait-contention 2>&1 | Tee-Object -FilePath $log
            $code = $LASTEXITCODE
            Write-Host '::endgroup::'
            if ($code -ne 0) { throw "G7 round $round $name failed with exit code $code; see $log." }
            Read-G7Rows -Log $log -Build $name | Out-Null
            Get-Content -LiteralPath $log | Where-Object { $_ -clike 'G7 build=*' } | Add-Content -LiteralPath $lines
        }
    }
} finally {
    git -C $repo worktree remove --force $baseline
}

# The summary uses valid windows only (C's notice inside the intersection of A's and B's Repairs); a build and arm
# with no valid window has no evidence and fails the run.
$rows = @(foreach ($name in 'baseline', 'candidate') {
    for ($round = 1; $round -le $Rounds; $round++) { Read-G7Rows -Log (Join-Path $Out "round-$round-$name.log") -Build $name }
})
$metrics = 'c_latency_ms', 'lock_contention_delta', 'read_item_retry_raw', 'repair_a_ms', 'repair_b_ms'
$summary = [System.Collections.Generic.List[string]]::new()
$summary.Add("G7 baseline $baselineRevision (src/ only) against candidate $candidateRevision; rounds $Rounds, repetitions $Repetitions.")
$empty = @()
foreach ($group in $rows | Group-Object { "$($_.build) $($_.arm)" } | Sort-Object Name) {
    $valid = @($group.Group | Where-Object workload -ceq 'valid')
    $inside = @($valid | Where-Object c_upload_in_overlap -ceq 'True').Count
    $summary.Add("$($group.Name) windows=$($group.Count) valid=$($valid.Count) upload_in_overlap=$inside")
    if (-not $valid.Count) { $empty += $group.Name; continue }
    foreach ($metric in $metrics) {
        $raw = @($valid | ForEach-Object { $_.$metric })
        $numbers = @($raw | Where-Object { $_ -ne 'held' } | ForEach-Object { [double]::Parse($_, [cultureinfo]::InvariantCulture) } | Sort-Object)
        $held = @($raw | Where-Object { $_ -eq 'held' }).Count
        $stats = if ($numbers.Count) {
            $median = if ($numbers.Count % 2) { $numbers[[int][math]::Floor($numbers.Count / 2)] } else {
                ($numbers[$numbers.Count / 2 - 1] + $numbers[$numbers.Count / 2]) / 2 }
            'median {0:F1} spread {1:F1} values [{2}]' -f $median, ($numbers[-1] - $numbers[0]), ($raw -join ', ')
        } else { "values [$($raw -join ', ')]" }
        $summary.Add("$($group.Name) $metric n=$($raw.Count) held=$held $stats")
    }
}
$summary | Set-Content -LiteralPath (Join-Path $Out 'summary.txt') -Encoding utf8
$summary | ForEach-Object { Write-Host $_ }
if ($empty.Count) { throw "G7 has no valid window for: $($empty -join '; ') (C's notice never fell inside both Repairs)." }
