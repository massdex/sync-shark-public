param(
    [Parameter(Mandatory = $true)][string]$Selector,
    [ValidateSet('root', 'none', 'assembly')][string]$Arguments = 'root',
    # Hard per-selector bound. A hung fixture is killed and still emits a UI-RESULT line, so one
    # selector can never block the remaining checks until the job timeout.
    [int]$TimeoutSeconds = 300
)

# Runs one tests/SyncShark.UiFixture selector as a bounded check. The scratch and
# fixture roots follow the core shard convention: a per-check scratch directory on
# the local volume, handed to the process through TEMP/TMP/SYNCSHARK_TEST_ROOT, and
# a fixture root that must not pre-exist (only the scratch directory is created).
$ErrorActionPreference = 'Stop'
$flag = '--' + $Selector.TrimStart('-')
$name = $Selector.TrimStart('-')
$workspace = if ($env:GITHUB_WORKSPACE) { $env:GITHUB_WORKSPACE } else { (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path }
$configuration = if ($env:CONFIGURATION) { $env:CONFIGURATION } else { 'Debug' }
$base = $env:LOCALAPPDATA

$scratch = Join-Path $base "syncshark-ci\ui-$name-scratch"
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$env:TEMP = $scratch
$env:TMP = $scratch
$env:SYNCSHARK_TEST_ROOT = $scratch

$root = Join-Path $base "syncshark-ci\ui-$name-root"
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }

$exe = Join-Path $workspace "build\ci\bin\SyncShark.UiFixture\$configuration\net8.0-windows10.0.19041.0\SyncShark.UiFixture.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "UI fixture executable not found: $exe" }

$testArgs = @($flag)
if ($Arguments -ne 'none') { $testArgs += $root }
if ($Arguments -eq 'assembly') {
    $tests = Join-Path $workspace "build\ci\bin\SyncShark.Core.Tests\$configuration\net8.0\SyncShark.Core.Tests.dll"
    if (-not (Test-Path -LiteralPath $tests -PathType Leaf)) { throw "Core test fixture assembly not found: $tests" }
    $testArgs += $tests
}

$watch = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "Running: $exe $($testArgs -join ' ')"
$stdout = [IO.Path]::GetTempFileName()
$stderr = [IO.Path]::GetTempFileName()
# Start-Process joins -ArgumentList with spaces, so quote any path that carries whitespace.
$quotedArgs = @($testArgs | ForEach-Object {
    if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
})
$process = Start-Process -FilePath $exe -ArgumentList $quotedArgs -NoNewWindow -PassThru `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr
$timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
if ($timedOut) {
    try { $process.Kill($true) } catch { try { $process.Kill() } catch { } }
    try { $process.WaitForExit(10000) | Out-Null } catch { }
}
$watch.Stop()
Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
Get-Content -LiteralPath $stderr -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
if ($timedOut) {
    Write-Host ("UI-RESULT {0} FAIL timeout seconds={1}" -f $flag, [int][Math]::Round($watch.Elapsed.TotalSeconds))
    exit 124
}
$code = $process.ExitCode
$result = if ($code -eq 0) { 'PASS' } else { 'FAIL' }
Write-Host ("UI-RESULT {0} {1} exit={2} seconds={3}" -f $flag, $result, $code, [int][Math]::Round($watch.Elapsed.TotalSeconds))
exit $code
