[CmdletBinding()]
param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$fixture = "$PSScriptRoot/fixtures/spec-inventory"
New-Item -ItemType Directory -Force $Output | Out-Null
$clock = [Diagnostics.Stopwatch]::StartNew()
$failures = [Collections.Generic.List[string]]::new()
# A zero-wait probe must refuse a still-running child rather than report success.
try {
    & "$PSScriptRoot/Invoke-SpecInventoryProcess.ps1" -Executable pwsh -Arguments @('-NoProfile', '-Command', 'while ($true) {}') -OutputPath "$Output/timeout-probe.log" -TimeoutSeconds 0
    $failures.Add('native timeout did not refuse')
} catch [TimeoutException] {
    $probe = Get-Content "$Output/timeout-probe.log.execution.json" -Raw | ConvertFrom-Json
    if (-not $probe.timedOut -or $probe.elapsedSeconds -lt 0) { $failures.Add('native timeout evidence missing') }
} catch { $failures.Add('native timeout probe: ' + $_.Exception.Message) }
# Each declaration is below the adopted 300-line review trigger; together they exceed it.
foreach ($part in @('Left', 'Right')) {
    $members = 1..151 | ForEach-Object { "    public int ${part}$_ => $_;" }
    @('namespace Fixture.Partials;', 'public partial class Together', '{') + $members + @('}') |
        Set-Content "$Output/Partial.$part.cs"
}
# Production and fixture projects use one invocation/configuration owner.
try { & "$PSScriptRoot/Collect-SpecInventory.ps1" -Root $root -Output $Output }
catch { $failures.Add('collector: ' + $_.Exception.Message) }
& python -I "$fixture/check_inventory.py" $Output *> "$Output/fixture-contract.log"
if ($LASTEXITCODE) { $failures.Add('fixture contracts') }
$leaks = "$Output/gitleaks-download/extracted/gitleaks.exe"
if (Test-Path $leaks) {
    $secrets = Join-Path $Output 'synthetic-secret-fixtures'
    New-Item -ItemType Directory $secrets | Out-Null
    # Synthetic token generated at runtime, never a credential or a tracked secret.
    # Randomize unique bytes: fixed entropy, without gitleaks' alphabetical stopword.
    $token = -join (([char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789') | Get-Random -Shuffle)
    ('github_token = "ghp_' + $token + '"') | Set-Content "$secrets/positive.txt"
    & $leaks dir $secrets --redact=100 --report-format sarif --report-path "$Output/secret-positive.sarif" *> "$Output/secret-positive.log"
    if ($LASTEXITCODE -ne 1) { $failures.Add('gitleaks positive') }
    'public_value = "ordinary fixture"' | Set-Content "$secrets/positive.txt"
    & $leaks dir $secrets --redact=100 --report-format sarif --report-path "$Output/secret-negative.sarif" *> "$Output/secret-negative.log"
    if ($LASTEXITCODE -ne 0) { $failures.Add('gitleaks negative') }
} else { $failures.Add('gitleaks executable missing') }
$clock.Stop()
@{ elapsedSeconds = $clock.Elapsed.TotalSeconds; scope = 'collection plus calibration' } |
    ConvertTo-Json | Set-Content "$Output/job-runtime.json"
@{ failures = @($failures); status = $(if ($failures.Count) { 'FAIL' } else { 'PASS' }) } |
    ConvertTo-Json -Depth 4 | Set-Content "$Output/fixtures.json"
if ($failures.Count) { throw ('Inventory fixtures: ' + ($failures -join '; ')) }
Write-Host 'PASS inventory fixtures; inventory gaps remain visible and non-gating'
