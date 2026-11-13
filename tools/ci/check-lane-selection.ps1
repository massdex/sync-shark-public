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
. (Join-Path $PSScriptRoot 'LaneSelection.ps1')
. (Join-Path $PSScriptRoot 'EngineJobs.ps1')
$liveGroups = @(Get-GroupList -Path (Join-Path $PSScriptRoot 'live-groups.txt'))

function Invoke-Selection([string]$Block, [string]$Ref, [string]$Registry, [string]$LaneJson = '', [string]$LaneRunner = '', [string]$EventName = 'push') {
    $workspace = Join-Path ([IO.Path]::GetTempPath()) ('lane-selection-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $workspace | Out-Null
    $outputFile = Join-Path $workspace 'github-output.txt'
    $summaryFile = Join-Path $workspace 'summary.md'
    $saved = @{}
    foreach ($name in @('GITHUB_WORKSPACE', 'GITHUB_OUTPUT', 'GITHUB_STEP_SUMMARY', 'GITHUB_REF', 'LANE_RUNNER', 'EVENT_NAME')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
    $previousLocation = Get-Location
    $outputs = @{}
    $summary = ''
    try {
        $env:GITHUB_WORKSPACE = $workspace
        $env:GITHUB_OUTPUT = $outputFile
        $env:GITHUB_STEP_SUMMARY = $summaryFile
        $env:GITHUB_REF = $Ref
        $env:LANE_RUNNER = $LaneRunner
        $env:EVENT_NAME = $EventName
        $fixtureRegistry = Join-Path $workspace 'tests/SyncShark.Engine.Tests/CheckRegistry.cs'
        New-Item -ItemType Directory -Path (Split-Path $fixtureRegistry -Parent) -Force | Out-Null
        [IO.File]::WriteAllText($fixtureRegistry, $Registry)
        $fixtureLiveGroups = Join-Path $workspace 'tools/ci/live-groups.txt'
        New-Item -ItemType Directory -Path (Split-Path $fixtureLiveGroups -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'live-groups.txt') -Destination $fixtureLiveGroups
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'MandatoryChecks.ps1') -Destination (Split-Path $fixtureLiveGroups -Parent)
        foreach ($file in @('LaneSelection.ps1', 'EngineJobs.ps1', 'engine-group-seconds.txt', 'hosted-only.txt')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Split-Path $fixtureLiveGroups -Parent)
        }
        [IO.File]::WriteAllText((Join-Path $workspace 'ci-quick-suites.txt'), '--cli-checks')
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
    $actual = @($Result.Outputs['engine'] | ConvertFrom-Json | ForEach-Object { $_.groups -split ' ' })
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

    $full = Invoke-Selection $block 'refs/heads/ci/int-lane-selection-check' $registry
    $build = @($full.Outputs['build'] | ConvertFrom-Json)
    $engine = @($full.Outputs['engine'] | ConvertFrom-Json | ForEach-Object { $_.groups -split ' ' })
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

    # Every lane runs mandatory checks; quick/** still skips the UI fixture.
    $quick = Invoke-Selection $block 'refs/heads/quick/lane-selection-check' $registry
    $quickBuild = @($quick.Outputs['build'] | ConvertFrom-Json)
    $quickCount = [int]$quick.Outputs['engine_count']
    if ($quick.Outputs['has_selection'] -ne 'false') { throw "Expected has_selection=false on quick, got '$($quick.Outputs['has_selection'])'." }
    Assert-EngineSelection $quick @('dependencies', 'literals', 'strings') 'Quick mandatory checks'
    if ($quickBuild -cnotcontains 'engine-tests') { throw 'Quick run misses mandatory engine-tests.' }
    if ($quickBuild -ccontains 'ui-fixture') { throw "Quick run builds 'ui-fixture'; quick still skips the UI fixture." }
    foreach ($needed in @('core', 'core-tests', 'fod-tests', 'app')) {
        if ($quickBuild -cnotcontains $needed) { throw "Quick run is missing '$needed'." }
    }

    $compile = @('core', 'core-tests', 'fod-tests', 'engine-tests', 'app', 'ui-fixture')
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
        Assert-EngineSelection $focused @(@($case.Expected) + @('dependencies', 'literals', 'strings') | Select-Object -Unique) $laneJson
        $focusedBuild = @($focused.Outputs['build'] | ConvertFrom-Json)
        if ($focused.Outputs['has_selection'] -ne 'true' -or $focusedBuild.Count -ne $compile.Count -or
            @($compile | Where-Object { $focusedBuild -cnotcontains $_ }).Count) { throw "Focused selection did not derive engine-tests: $laneJson" }
    }
    foreach ($selection in @(
        '{"core":["--cli-checks"]}',
        '{"ui":["assembly-checks"]}',
        '{"build":["core"]}',
        '{"build":["core"],"engine":["strings","strings"]}'
    )) {
        $result = Invoke-Selection $block 'refs/heads/ci/lane-selection-check' $registry $selection
        $lane = $result.Outputs
        $laneBuild = @($lane.build | ConvertFrom-Json)
        $laneEngine = @($lane.engine | ConvertFrom-Json | ForEach-Object { $_.groups -split ' ' })
        # Fixture copies deliberately pin the mandatory contract independently of its implementation.
        foreach ($group in @('dependencies', 'literals', 'strings')) {
            if (@($laneEngine | Where-Object { $_ -ceq $group }).Count -ne 1) { throw "Lane must run $group exactly once: $selection" }
        }
        foreach ($project in $compile) {
            if ($laneBuild -cnotcontains $project) { throw "Lane misses $project`: $selection" }
        }
        if ($laneBuild -ccontains 'reachability') { throw 'Lane unexpectedly selected reachability.' }
        $requested = $selection | ConvertFrom-Json -AsHashtable
        foreach ($kind in $requested.Keys) {
            $actual = if ($kind -ceq 'engine') { $laneEngine } else { @($lane[$kind] | ConvertFrom-Json) }
            foreach ($item in $requested[$kind]) {
                if ($actual -cnotcontains $item) { throw "Lane lost requested $kind item $item`: $selection" }
            }
        }
        if ($lane.has_selection -ne 'true') { throw 'Lane selection lost its identity.' }
        Write-Output "PASS mandatory lane fences: $selection"
    }
    foreach ($ref in @('refs/heads/main', 'refs/heads/ci/rc-fixture')) {
        $result = Invoke-Selection $block $ref $registry
        if ($result.Outputs.has_selection -ne 'false') { throw "Expected full run on $ref" }
        Assert-EngineSelection $result $hostedGroups $ref
    }
    $empty = Invoke-Selection $block 'refs/heads/ci/fixture' $registry '{}'
    Assert-EngineSelection $empty @('dependencies', 'literals', 'strings') 'Empty selection mandatory union'
    $emptyBuild = @($empty.Outputs.build | ConvertFrom-Json)
    if ($emptyBuild.Count -ne $compile.Count -or @($compile | Where-Object { $emptyBuild -cnotcontains $_ }).Count) { throw 'Empty lane does not compile all projects.' }
    foreach ($case in @(
        @{ Ref = 'refs/heads/ci/fixture'; Json = ''; Error = 'A lane run needs .ci-lane.json' },
        @{ Ref = 'refs/heads/ci/int-fixture'; Json = '{}'; Error = 'Integration and RC runs are full' }
    )) {
        $rejected = $false
        try { Invoke-Selection $block $case.Ref $registry $case.Json | Out-Null }
        catch { if (-not $_.Exception.Message.StartsWith($case.Error)) { throw }; $rejected = $true }
        if (-not $rejected) { throw "Missing run-kind refusal: $($case.Ref)" }
    }
    $labels = '["self-hosted","windows","lane"]'
    foreach ($case in @(
        @{ Ref = 'refs/heads/ci/fixture'; Json = '{}'; Runner = ''; Event = 'push'; Expected = '["windows-latest"]' },
        @{ Ref = 'refs/heads/ci/fixture'; Json = '{}'; Runner = $labels; Event = 'push'; Expected = $labels },
        @{ Ref = 'refs/heads/ci/int-fixture'; Json = ''; Runner = $labels; Event = 'push'; Expected = '["windows-latest"]' },
        @{ Ref = 'refs/heads/ci/fixture'; Json = '{}'; Runner = $labels; Event = 'pull_request'; Expected = '["windows-latest"]' }
    )) {
        $result = Invoke-Selection $block $case.Ref $registry $case.Json $case.Runner $case.Event
        if ($result.Outputs.runner -cne $case.Expected) { throw "Wrong runner for $($case.Ref)/$($case.Event)" }
    }
    $hosted = Invoke-Selection $block 'refs/heads/ci/fixture' $registry '{"core":["--setup-update-checks"]}' $labels
    if ($hosted.Outputs.core_runner -cne '["windows-latest"]') { throw 'Setup-update must stay hosted.' }
    $receipt = Invoke-Selection $block 'refs/heads/ci/fixture' $registry '{"engine":["receipt-walk-measure","budget"]}' $labels
    foreach ($job in ($receipt.Outputs.engine | ConvertFrom-Json)) {
        $expected = if (($job.groups -split ' ') -ccontains 'receipt-walk-measure') { '["windows-latest"]' } else { $labels }
        if ((ConvertTo-Json -InputObject @($job.runner) -Compress) -cne $expected) { throw "Wrong engine job runner: $($job.groups)" }
    }
    $yaml = [IO.File]::ReadAllText($workflowPath)
    foreach ($job in @('setup-transitions', 'setup-cleanup-launch', 'setup-mutants', 'spec-ratchet', 'literal-sites', 'spec-inventory', 'tests', 'ui-tests', 'lane-ui')) {
        $match = [regex]::Match($yaml, '(?ms)^  ' + [regex]::Escape($job) + ':\r?\n(?<body>.*?)(?=^  [a-z][a-z-]+:|\z)')
        # The inventory job lands independently; enforce its placement when present.
        if (-not $match.Success -and $job -eq 'spec-inventory') { continue }
        if (-not $match.Success -or $match.Groups['body'].Value -notmatch '(?m)^    runs-on: windows-latest\r?$') { throw "$job must remain hosted." }
    }
    foreach ($invalid in @('not-json', '{}', '["self-hosted",""]', '["self-hosted",3]', '["windows","lane"]')) {
        $rejected = $false
        try { Invoke-Selection $block 'refs/heads/ci/int-fixture' $registry '' $invalid | Out-Null }
        catch { $rejected = $true }
        if (-not $rejected) { throw "Invalid runner was accepted: $invalid" }
    }
    $secondsPath = Join-Path ([IO.Path]::GetTempPath()) ('engine-seconds-' + [guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($secondsPath, "heavy 20`nlight 2`nmedium 6`n")
        $jobs = @(Get-EngineJobs -Groups @('light','unknown','medium','heavy') -SecondsPath $secondsPath -TargetSeconds 15)
        if ($jobs.Count -ne 3 -or ($jobs[0].Groups -join ' ') -cne 'heavy' -or
            ($jobs[1].Groups -join ' ') -cne 'medium light' -or ($jobs[2].Groups -join ' ') -cne 'unknown' -or
            $jobs[2].Weight -ne 6) { throw 'Packing changed coverage, median or deterministic ties.' }
        foreach ($missing in @($false, $true)) {
            [IO.File]::WriteAllText($secondsPath, '')
            $path = if ($missing) { "$secondsPath-missing" } else { $secondsPath }
            $rejected = $false
            try { Get-EngineJobs -Groups @('a') -SecondsPath $path -TargetSeconds 15 | Out-Null }
            catch { $rejected = $true }
            if (-not $rejected) { throw 'Missing/empty weights accepted.' }
        }
    } finally { Remove-Item -LiteralPath $secondsPath -Force }

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
