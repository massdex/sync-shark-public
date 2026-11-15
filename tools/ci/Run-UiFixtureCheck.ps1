param(
    [Parameter(Mandatory = $true)][string]$Selector,
    # Omitted: the selector's own default below decides, so every caller agrees.
    [ValidateSet('root', 'none', 'assembly')][string]$Arguments,
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
# One owner of each selector's argument shape: these take no fixture root, and
# mixed-file-retry also takes the core test assembly.
if (-not $Arguments) {
    $Arguments = switch -CaseSensitive ($name) {
        'tray-tooltip-contract' { 'none' }
        'window-close-contract' { 'none' }
        'pair-failure-presentation-checks' { 'none' }
        'tray-client-checks' { 'none' }
        'engine-startup-readiness-checks' { 'none' }
        'mixed-file-retry-ui-checks' { 'assembly' }
        default { 'root' }
    }
}
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

$stdout = [IO.Path]::GetTempFileName()
$stderr = "$stdout.stderr.log"
Write-Host "Running: $exe $($testArgs -join ' ')"
$code = 0
$timedOut = $false
try {
    & (Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1') -Executable $exe -Arguments $testArgs `
        -OutputPath $stdout -TimeoutSeconds $TimeoutSeconds
    $code = $LASTEXITCODE
} catch [TimeoutException] { $timedOut = $true }
$seconds = [int][Math]::Round((Get-Content -Raw -LiteralPath "$stdout.execution.json" | ConvertFrom-Json).elapsedSeconds)
Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
Get-Content -LiteralPath $stderr -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
Remove-Item -LiteralPath $stdout, $stderr, "$stdout.execution.json" -Force -ErrorAction SilentlyContinue
if ($timedOut) {
    Write-Host ("UI-RESULT {0} FAIL timeout seconds={1}" -f $flag, $seconds)
    exit 124
}
$result = if ($code -eq 0) { 'PASS' } else { 'FAIL' }
Write-Host ("UI-RESULT {0} {1} exit={2} seconds={3}" -f $flag, $result, $code, $seconds)
exit $code
