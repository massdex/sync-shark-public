# Executes the workflow's selection script against full, quick and focused fixtures.
# Hosted executable groups must be covered once; aggregate groups expand in the workflow only.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$workflowPath = Join-Path $root '.github/workflows/sync-shark-ci.yml'
$registryPath = Join-Path $root 'tests/SyncShark.Engine.Tests/CheckRegistry.cs'
$stepName = 'Read .ci-lane.json'

function Get-StepRunBlock {
    param([string]$Yaml, [string]$Name)
    $lines = $Yaml -split "`r?`n"
    $header = '^\s*- name: ' + [regex]::Escape($Name) + '\s*$'
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $header) { $start = $i; break }
    }
    if ($start -lt 0) { throw "Workflow step '$Name' was not found." }
    $run = -1
    $runIndent = 0
    for ($i = $start + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*- name:') { break }
        if ($lines[$i] -match '^(\s*)run:\s*\|-?\s*$') { $run = $i; $runIndent = $Matches[1].Length; break }
    }
    if ($run -lt 0) { throw "Workflow step '$Name' has no block 'run:' script." }
    $body = @()
    for ($i = $run + 1; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line.Trim().Length -eq 0) { $body += ''; continue }
        $indent = ([regex]::Match($line, '^(\s*)')).Groups[1].Length
        if ($indent -le $runIndent) { break }
        $body += $line
    }
    while ($body.Count -gt 0 -and $body[-1] -eq '') {
        if ($body.Count -eq 1) { $body = @(); break }
        $body = @($body[0..($body.Count - 2)])
    }
    if ($body.Count -eq 0) { throw "Workflow step '$Name' has an empty 'run:' script." }
    $indents = @($body | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { ([regex]::Match($_, '^(\s*)')).Groups[1].Length })
    $strip = ($indents | Measure-Object -Minimum).Minimum
    return (($body | ForEach-Object { if ($_.Length -ge $strip) { $_.Substring($strip) } else { $_.Trim() } }) -join "`n")
}

# The hosted route cannot reach SYNCSHARK_LIVE_CONNECTION; its groups are named, not scheduled.
# One owner for the list: tools/ci/live-groups.txt.
$liveGroups = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'live-groups.txt') |
    Where-Object { $_.Trim() -and -not $_.TrimStart().StartsWith('#') } |
    ForEach-Object { $_.Trim() })

function Invoke-Selection([string]$Block, [string]$Ref, [string]$Registry, [string]$LaneJson = '') {
    $workspace = Join-Path ([IO.Path]::GetTempPath()) ('lane-selection-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $workspace | Out-Null
    $outputFile = Join-Path $workspace 'github-output.txt'
    $summaryFile = Join-Path $workspace 'summary.md'
    $saved = @{}
    foreach ($name in @('GITHUB_WORKSPACE', 'GITHUB_OUTPUT', 'GITHUB_STEP_SUMMARY', 'GITHUB_REF')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
    $previousLocation = Get-Location
    $outputs = @{}
    $summary = ''
    try {
        $env:GITHUB_WORKSPACE = $workspace
        $env:GITHUB_OUTPUT = $outputFile
        $env:GITHUB_STEP_SUMMARY = $summaryFile
        $env:GITHUB_REF = $Ref
        $fixtureRegistry = Join-Path $workspace 'tests/SyncShark.Engine.Tests/CheckRegistry.cs'
        New-Item -ItemType Directory -Path (Split-Path $fixtureRegistry -Parent) -Force | Out-Null
        [IO.File]::WriteAllText($fixtureRegistry, $Registry)
        $fixtureLiveGroups = Join-Path $workspace 'tools/ci/live-groups.txt'
        New-Item -ItemType Directory -Path (Split-Path $fixtureLiveGroups -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'live-groups.txt') -Destination $fixtureLiveGroups
        if ($LaneJson) { [IO.File]::WriteAllText((Join-Path $workspace '.ci-lane.json'), $LaneJson) }
        Set-Location -LiteralPath $workspace
        & ([scriptblock]::Create($Block))
        foreach ($line in [IO.File]::ReadAllLines($outputFile)) {
            $split = $line.IndexOf('=')
            if ($split -gt 0) { $outputs[$line.Substring(0, $split)] = $line.Substring($split + 1) }
        }
        if (Test-Path -LiteralPath $summaryFile) { $summary = [IO.File]::ReadAllText($summaryFile) }
    } finally {
        Set-Location $previousLocation
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
        Remove-Item -LiteralPath $workspace -Recurse -Force
    }
    return [pscustomobject]@{ Outputs = $outputs; Summary = $summary }
}

function Assert-EngineSelection($Result, [string[]]$Expected, [string]$Name) {
    $actual = @($Result.Outputs['engine'] | ConvertFrom-Json)
    if ([int]$Result.Outputs['engine_count'] -ne $Expected.Count -or $actual.Count -ne $Expected.Count -or
        @($Expected | Where-Object { $actual -cnotcontains $_ }).Count -or
        @($actual | Where-Object { $Expected -cnotcontains $_ }).Count) {
        throw "$Name expected [$($Expected -join ',')], got [$($actual -join ',')] with count $($Result.Outputs['engine_count'])."
    }
}

try {
    $block = Get-StepRunBlock -Yaml ([IO.File]::ReadAllText($workflowPath)) -Name $stepName

    # The expected groups come from the registry file itself, independently of the workflow.
    $registry = [IO.File]::ReadAllText($registryPath)
    $allGroups = @([regex]::Matches($registry, '(?m)^\s*\["([a-z0-9-]+)"\]\s*=') | ForEach-Object { $_.Groups[1].Value })
    if ($allGroups.Count -eq 0) { throw 'No engine groups found in CheckRegistry.cs.' }
    $hostedGroups = @($allGroups | Where-Object { $liveGroups -cnotcontains $_ -and $_ -cne 'budget' })
    $livePresent = @($liveGroups | Where-Object { $allGroups -ccontains $_ })

    $full = Invoke-Selection $block 'refs/heads/ci/lane-selection-check' $registry
    $build = @($full.Outputs['build'] | ConvertFrom-Json)
    $engine = @($full.Outputs['engine'] | ConvertFrom-Json)
    if ($full.Outputs['has_selection'] -ne 'false') { throw "Expected has_selection=false, got '$($full.Outputs['has_selection'])'." }
    Assert-EngineSelection $full $hostedGroups 'Full run'
    $scheduledLive = @($liveGroups | Where-Object { $engine -ccontains $_ })
    if ($scheduledLive.Count -gt 0) { throw "Full run schedules live-only groups: $($scheduledLive -join ', ')." }
    if ($build -cnotcontains 'engine-tests') { throw "Full run does not build 'engine-tests'; its engine groups would fail to run." }
    if ($full.Summary -notmatch 'not run here: needs the authorized live route') {
        throw 'Full run summary does not state that live groups are not run here.'
    }
    foreach ($group in $livePresent) {
        if ($full.Summary -notmatch [regex]::Escape($group)) { throw "Full run summary does not name live group '$group'." }
    }

    # quick/** still skips engine work and the UI fixture.
    $quick = Invoke-Selection $block 'refs/heads/quick/lane-selection-check' $registry
    $quickBuild = @($quick.Outputs['build'] | ConvertFrom-Json)
    $quickCount = [int]$quick.Outputs['engine_count']
    if ($quick.Outputs['has_selection'] -ne 'false') { throw "Expected has_selection=false on quick, got '$($quick.Outputs['has_selection'])'." }
    if ($quickCount -ne 0) { throw "Quick run schedules $quickCount engine groups; the quick lane must skip engine work." }
    if ($quickBuild -ccontains 'engine-tests') { throw "Quick run builds 'engine-tests'; the quick lane must skip engine work." }
    if ($quickBuild -ccontains 'ui-fixture') { throw "Quick run builds 'ui-fixture'; quick still skips the UI fixture." }
    foreach ($needed in @('core', 'core-tests', 'fod-tests', 'app')) {
        if ($quickBuild -cnotcontains $needed) { throw "Quick run is missing '$needed'." }
    }

    $budgetShards = @('budget-1', 'budget-2', 'budget-3', 'budget-4')
    foreach ($case in @(
        @{ Input = @('budget'); Expected = $budgetShards },
        @{ Input = @('budget-2'); Expected = @('budget-2') },
        @{ Input = @('budget', 'budget-2'); Expected = $budgetShards },
        @{ Input = @('repair'); Expected = @('repair') },
        @{ Input = @('repair', 'budget'); Expected = @($budgetShards) + 'repair' }
    )) {
        $laneJson = @{ engine = $case.Input } | ConvertTo-Json -Compress
        $focused = Invoke-Selection $block 'refs/heads/ci/lane-selection-check' $registry $laneJson
        Assert-EngineSelection $focused $case.Expected $laneJson
        $focusedBuild = @($focused.Outputs['build'] | ConvertFrom-Json)
        if ($focused.Outputs['has_selection'] -ne 'true' -or $focusedBuild.Count -ne 1 -or
            $focusedBuild[0] -cne 'engine-tests') { throw "Focused selection did not derive engine-tests: $laneJson" }
    }
    $missingShards = [regex]::Replace($registry, '(?m)^\s*\["budget-[0-9]+"\]\s*=.*$', '')
    $rejected = $false
    try { Invoke-Selection $block 'refs/heads/ci/lane-selection-check' $missingShards '{"engine":["budget"]}' | Out-Null }
    catch {
        if ($_.Exception.Message -cne 'No numeric budget shards found in CheckRegistry.') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Budget selection without numeric shards did not throw.' }

    Write-Output "PASS selection: $($hostedGroups.Count) hosted executable groups once; live groups named; quick exclusions; all focused and missing-shard cases."
}
catch {
    [Console]::Error.WriteLine("FAIL lane selection check: $($_.Exception.Message)")
    exit 1
}
