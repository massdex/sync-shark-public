[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$ItemsJson,
    [Parameter(Mandatory)][string]$Title,
    [Parameter(Mandatory)][int]$NameWidth
)

$ErrorActionPreference = 'Stop'
$results = @()
foreach ($item in ($ItemsJson | ConvertFrom-Json)) {
    $suite = $item.name
    Write-Host "::group::SUITE $suite"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $code = 1
    try {
        New-Item -ItemType Directory -Force -Path $item.scratch | Out-Null
        $env:TEMP = $item.scratch
        $env:TMP = $item.scratch
        $env:SYNCSHARK_TEST_ROOT = $item.scratch
        if (-not (Test-Path $Exe)) { throw "Test executable not found: $Exe" }
        $testArgs = if ($item.PSObject.Properties['flag']) {
            @(& "$PSScriptRoot/Get-CoreCheckArguments.ps1" -Flag $item.flag -Root $item.root)
        } else { @($item.arguments) }
        Write-Host "Running: $Exe $($testArgs -join ' ')"
        & $Exe @testArgs
        $code = $LASTEXITCODE
        Write-Host "Exit code: $code"
    } catch {
        Write-Host "ERROR: $($_.Exception.Message)"
        $code = 1
    } finally {
        $sw.Stop()
    }
    $seconds = [int][Math]::Round($sw.Elapsed.TotalSeconds)
    $result = if ($code -eq 0) { 'PASS' } else { 'FAIL' }
    Write-Host "SUITE-RESULT $suite $result exit=$code seconds=$seconds"
    Write-Host '::endgroup::'
    $results += [pscustomobject]@{ Suite = $suite; Result = $result; Exit = $code; Seconds = $seconds }
}

Write-Host ''
Write-Host 'SUMMARY'
$format = "{0,-$NameWidth} {1,-6} {2,-6} {3,7}"
Write-Host ($format -f 'SUITE', 'RESULT', 'EXIT', 'SECONDS')
foreach ($r in $results) {
    Write-Host ($format -f $r.Suite, $r.Result, $r.Exit, $r.Seconds)
}
$failed = @($results | Where-Object { $_.Result -ne 'PASS' })
Write-Host "TOTAL suites=$($results.Count) failed=$($failed.Count)"
if ($env:GITHUB_STEP_SUMMARY) {
    $md = @("## $Title", '', '| Suite | Result | Exit | Seconds |', '| --- | --- | --- | --- |')
    foreach ($r in $results) {
        $md += "| $($r.Suite) | $($r.Result) | $($r.Exit) | $($r.Seconds) |"
    }
    $md += ''
    $md | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}
if ($failed.Count -gt 0) { exit 1 }
exit 0
