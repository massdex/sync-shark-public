# Guards the full-run default in .github/workflows/sync-shark-ci.yml: with no
# .ci-lane.json the select step must build engine-tests and cover every group in
# CheckRegistry.cs. The step's own script is executed here, so there is no second
# copy of the selection rule to drift. Fails on a workflow whose default skips
# the engine groups (engine_count 0 vs the registry count).
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

try {
    $block = Get-StepRunBlock -Yaml ([IO.File]::ReadAllText($workflowPath)) -Name $stepName

    # The expected groups come from the registry file itself, independently of the workflow.
    $registry = [IO.File]::ReadAllText($registryPath)
    $expected = @([regex]::Matches($registry, '(?m)^\s*\["([a-z0-9-]+)"\]\s*=') | ForEach-Object { $_.Groups[1].Value })
    if ($expected.Count -eq 0) { throw 'No engine groups found in CheckRegistry.cs.' }

    # A workspace with no .ci-lane.json is what a full run sees.
    $workspace = Join-Path ([IO.Path]::GetTempPath()) ('lane-selection-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $workspace | Out-Null
    $outputFile = Join-Path $workspace 'github-output.txt'
    $saved = @{}
    foreach ($name in @('GITHUB_WORKSPACE', 'GITHUB_OUTPUT', 'GITHUB_REF')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
    $previousLocation = Get-Location
    $outputs = @{}
    try {
        $env:GITHUB_WORKSPACE = $workspace
        $env:GITHUB_OUTPUT = $outputFile
        $env:GITHUB_REF = 'refs/heads/ci/lane-selection-check'
        Set-Location -LiteralPath $root
        & ([scriptblock]::Create($block))
        foreach ($line in [IO.File]::ReadAllLines($outputFile)) {
            $split = $line.IndexOf('=')
            if ($split -gt 0) { $outputs[$line.Substring(0, $split)] = $line.Substring($split + 1) }
        }
        foreach ($selection in @(
            '{"core":["--cli-checks"]}',
            '{"ui":["assembly-checks"]}',
            '{"build":["core"]}',
            '{"build":["core"],"engine":["strings","strings"]}'
        )) {
            Set-Content -LiteralPath (Join-Path $workspace '.ci-lane.json') -Value $selection
            Clear-Content -LiteralPath $outputFile
            & ([scriptblock]::Create($block))
            $lane = @{}
            foreach ($line in [IO.File]::ReadAllLines($outputFile)) {
                $key, $value = $line -split '=', 2
                $lane[$key] = $value
            }
            $laneBuild = @($lane.build | ConvertFrom-Json)
            $laneEngine = @($lane.engine | ConvertFrom-Json)
            foreach ($group in @('dependencies', 'literals', 'strings')) {
                if (@($laneEngine | Where-Object { $_ -ceq $group }).Count -ne 1) { throw "Lane must run $group exactly once: $selection" }
            }
            foreach ($project in @('core-tests', 'engine-tests')) {
                if ($laneBuild -cnotcontains $project) { throw "Lane misses $project`: $selection" }
            }
            if ($lane.has_selection -ne 'true') { throw 'Lane selection lost its identity.' }
            Write-Output "PASS mandatory lane fences: $selection"
        }
    } finally {
        Set-Location $previousLocation
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
        Remove-Item -LiteralPath $workspace -Recurse -Force
    }

    if ($outputs['has_selection'] -ne 'false') { throw "Expected has_selection=false, got '$($outputs['has_selection'])'." }
    $build = @($outputs['build'] | ConvertFrom-Json)
    $engine = @($outputs['engine'] | ConvertFrom-Json)
    $engineCount = [int]$outputs['engine_count']

    if ($engineCount -ne $expected.Count) {
        throw "Full run selects $engineCount engine groups but CheckRegistry.cs holds $($expected.Count); the default is skipping work."
    }
    $missing = @($expected | Where-Object { $engine -cnotcontains $_ })
    if ($missing.Count -gt 0) { throw "Full run is missing engine groups: $($missing -join ', ')." }
    if ($build -cnotcontains 'engine-tests') { throw "Full run does not build 'engine-tests'; its engine groups would fail to run." }

    Write-Output "PASS full-run selection: engine-tests plus $($expected.Count) registry groups."
}
catch {
    [Console]::Error.WriteLine("FAIL lane selection check: $($_.Exception.Message)")
    exit 1
}
