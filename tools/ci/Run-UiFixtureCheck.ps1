param(
    [Parameter(Mandatory = $true)][string]$Selector,
    [ValidateSet('root', 'none', 'assembly')][string]$Arguments = 'root'
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
& $exe @testArgs
$code = $LASTEXITCODE
$watch.Stop()
$result = if ($code -eq 0) { 'PASS' } else { 'FAIL' }
Write-Host ("UI-RESULT {0} {1} exit={2} seconds={3}" -f $flag, $result, $code, [int][Math]::Round($watch.Elapsed.TotalSeconds))
exit $code
