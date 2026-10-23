param(
    [Parameter(Mandatory=$true)][string]$Dotnet,
    [Parameter(Mandatory=$true)][string]$TestAssembly,
    [Parameter(Mandatory=$true)][string]$FixtureRoot,
    [switch]$Physical200k,
    [switch]$MillionRowProtection
)
$ErrorActionPreference = 'Stop'
# Runs an already-built test assembly; no installs, elevation, build or process cleanup.
foreach ($name in @('HTTPS_PROXY','HTTP_PROXY','ALL_PROXY')) {
    [Environment]::SetEnvironmentVariable($name, $null, 'Process')
}
$cases = @('hardlinks','rehash','drain','vanish')
if ($Physical200k) { $cases += 'physical-large' } else { $cases += 'physical' }
if ($MillionRowProtection) { $cases += 'protection' }
foreach ($case in $cases) {
    $fixture = Join-Path $FixtureRoot ($case + '-' + [Guid]::NewGuid().ToString('N'))
    & $Dotnet $TestAssembly --v2-usn-regression $case $fixture
    if ($LASTEXITCODE -ne 0) { throw "Local journal regression failed: $case ($LASTEXITCODE)" }
}
# Fixtures are deliberately retained. The caller decides when to remove them.
