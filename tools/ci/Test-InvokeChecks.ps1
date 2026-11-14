# Exercises the shared check loop's observations, argument shapes and failure continuation.
$ErrorActionPreference = 'Stop'
try {
    $observationRoot = Join-Path ([IO.Path]::GetTempPath()) ('check-observation-' + [guid]::NewGuid().ToString('N'))
    try {
        $items = @(foreach ($name in @('first', 'later')) {
            @{ name = $name; scratch = "$observationRoot/$name"; arguments = @('-NoProfile', '-Command', 'exit 0') }
        }) | ConvertTo-Json -Compress
        $loopPath = (Join-Path $PSScriptRoot 'Invoke-Checks.ps1').Replace("'", "''")
        $shell = (Get-Process -Id $PID).Path
        $exePath = $shell.Replace("'", "''")
        $command = "function Get-CimInstance { throw 'synthetic observation failure' }; " +
            "`$env:GITHUB_STEP_SUMMARY = ''; & '$loopPath' -Exe '$exePath' -ItemsJson '" +
            $items.Replace("'", "''") + "' -Title 'Observation fixture' -NameWidth 30; exit `$LASTEXITCODE"
        $output = (& $shell -NoProfile -Command $command 2>&1) -join "`n"
        if ($LASTEXITCODE -ne 0 -or $output -notmatch 'SUITE-RESULT first PASS' -or
            $output -notmatch 'SUITE-RESULT later PASS' -or $output -notmatch 'TOTAL suites=2 failed=0' -or
            ([regex]::Matches($output, 'observation failed: synthetic observation failure')).Count -ne 2) {
            throw "Observation failure changed execution/results: $output"
        }

        $capture = Join-Path $observationRoot 'capture.ps1'
        [IO.File]::WriteAllText($capture, @'
$name = Split-Path $env:SYNCSHARK_TEST_ROOT -Leaf
Write-Host ('CAPTURE ' + $name + ' ' + (ConvertTo-Json -InputObject @($args) -Compress))
if ($name -ceq 'fails') { exit 7 }
exit 0
'@)
        $cases = @(
            @{ name = 'zero'; arguments = @(); expected = '[]' }
            @{ name = 'one'; arguments = @('--single'); expected = '["--single"]' }
            @{ name = 'several'; arguments = @('--first', 'two words'); expected = '["--first","two words"]' }
            @{ name = 'core-one'; flag = '--single'; expected = '["--single"]' }
            @{ name = 'core-several'; flag = '--root'; expected = '["--root","fixture-root"]' }
            @{ name = 'fails'; arguments = @('--failure'); expected = '["--failure"]' }
            @{ name = 'after-fail'; arguments = @('--later'); expected = '["--later"]' }
        )
        $items = @($cases | ForEach-Object {
            $item = @{ name = $_.name; scratch = "$observationRoot/$($_.name)"; root = 'fixture-root' }
            if ($_.ContainsKey('flag')) { $item.flag = $_.flag } else { $item.arguments = $_.arguments }
            $item
        }) | ConvertTo-Json -Compress
        $command = "function Get-CimInstance { @() }; `$env:SUITE_ROOT_ARGS = '--root'; `$env:SUITE_EXTRA_ARGS = ''; " +
            "`$env:GITHUB_STEP_SUMMARY = ''; & '$loopPath' -Exe '" + $capture.Replace("'", "''") + "' -ItemsJson '" +
            $items.Replace("'", "''") + "' -Title 'Argument fixture' -NameWidth 30; exit `$LASTEXITCODE"
        $output = (& $shell -NoProfile -Command $command 2>&1) -join "`n"
        if ($LASTEXITCODE -ne 1 -or $output -notmatch 'TOTAL suites=7 failed=1') { throw "Argument fixture totals: $output" }
        foreach ($case in $cases) {
            $expectedResult = if ($case.name -ceq 'fails') { 'FAIL exit=7' } else { 'PASS exit=0' }
            if (-not $output.Contains("CAPTURE $($case.name) $($case.expected)") -or
                -not $output.Contains("SUITE-RESULT $($case.name) $expectedResult")) { throw "Argument shape/continuation: $output" }
        }
    } finally {
        if (Test-Path -LiteralPath $observationRoot) { Remove-Item -LiteralPath $observationRoot -Recurse -Force }
    }
    Write-Output 'PASS check loop: observation failures, argument shapes and failure continuation.'
    # The expected child exit code must not become the hosted fixture step's exit code.
    exit 0
}
catch {
    [Console]::Error.WriteLine("FAIL check loop: $($_.Exception.Message)")
    exit 1
}
