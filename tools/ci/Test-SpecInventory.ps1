[CmdletBinding()]
param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$fixture = "$PSScriptRoot/fixtures/spec-inventory"
New-Item -ItemType Directory -Force $Output | Out-Null
$config = @(Get-Content "$root/.editorconfig" | Where-Object { $_ -match '^dotnet_' })
@('is_global = true', 'global_level = 999') + $config | Set-Content "$Output/inventory.globalconfig"
$failures = [Collections.Generic.List[string]]::new()
# Each declaration is below the adopted 300-line review trigger; together they exceed it.
foreach ($part in @('Left', 'Right')) {
    $members = 1..151 | ForEach-Object { "    public int ${part}$_ => $_;" }
    @('namespace Fixture.Partials;', 'public partial class Together', '{') + $members + @('}') |
        Set-Content "$Output/Partial.$part.cs"
}
$arguments = @("$fixture/Fixture.csproj", '-nologo', '-v:quiet', '-p:SpecInventory=true',
    "-p:SpecInventoryOutput=$Output", '-p:RestoreLockedMode=false', '-p:RestorePackagesWithLockFile=false')
& dotnet build @arguments *> "$Output/fixture-build.log"
if ($LASTEXITCODE) { $failures.Add('fixture build') }
& dotnet msbuild @arguments /t:Metrics *> "$Output/fixture-metrics.log"
if ($LASTEXITCODE) { $failures.Add('fixture metrics') }
& python -I "$fixture/check_inventory.py" $Output *> "$Output/fixture-contract.log"
if ($LASTEXITCODE) { $failures.Add('fixture contracts') }
# Independent inventory still runs after a fixture failure; no early false-green exit.
try { & "$PSScriptRoot/Collect-SpecInventory.ps1" -Root $root -Output $Output }
catch { $failures.Add('collector: ' + $_.Exception.Message) }
$leaks = "$Output/gitleaks-download/extracted/gitleaks.exe"
if (Test-Path $leaks) {
    $secrets = Join-Path $Output 'synthetic-secret-fixtures'
    New-Item -ItemType Directory $secrets | Out-Null
    # Synthetic token generated at runtime, never a credential or a tracked secret.
    ('github_token = "ghp_' + ('Ab12' * 9) + '"') | Set-Content "$secrets/positive.txt"
    & $leaks dir $secrets --redact=100 --report-format sarif --report-path "$Output/secret-positive.sarif" *> "$Output/secret-positive.log"
    if ($LASTEXITCODE -ne 1) { $failures.Add('gitleaks positive') }
    'public_value = "ordinary fixture"' | Set-Content "$secrets/positive.txt"
    & $leaks dir $secrets --redact=100 --report-format sarif --report-path "$Output/secret-negative.sarif" *> "$Output/secret-negative.log"
    if ($LASTEXITCODE -ne 0) { $failures.Add('gitleaks negative') }
} else { $failures.Add('gitleaks executable missing') }
@{ failures = @($failures); status = $(if ($failures.Count) { 'FAIL' } else { 'PASS' }) } |
    ConvertTo-Json -Depth 4 | Set-Content "$Output/fixtures.json"
if ($failures.Count) { throw ('Inventory fixtures: ' + ($failures -join '; ')) }
Write-Host 'PASS inventory fixtures; inventory gaps remain visible and non-gating'
