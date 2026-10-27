[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [Parameter(Mandatory)]
    [string]$BuildRoot,
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,
    [Parameter(Mandatory)]
    [string]$ExpectedRevision,
    [Parameter(Mandatory)]
    [string]$ContractZip,
    [Parameter(Mandatory)]
    [string]$DotNetPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-EmptyOutputRoot {
    param([string]$Path, [string]$Role, [string]$RepositoryRoot)
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -match '(?i)[\\/]Dropbox[\\/]') {
        throw "$Role must be outside Dropbox."
    }
    if ($full.Equals($RepositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $full.StartsWith($RepositoryRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Role must be outside the frozen repository."
    }
    if (Test-Path -LiteralPath $full) {
        if (@(Get-ChildItem -LiteralPath $full -Force).Count -ne 0) {
            throw "$Role must be empty for one immutable preflight run."
        }
    }
    else {
        New-Item -ItemType Directory -Path $full | Out-Null
    }
    return $full
}

function Invoke-Git {
    param([string[]]$Arguments)
    $value = & git -C $script:RepositoryRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $value"
    }
    return ($value -join "`n").Trim()
}

function Invoke-Phase {
    param([string]$Name, [string[]]$Arguments)
    $logPath = Join-Path $script:EvidenceRootFull ($Name + '.log')
    $started = [DateTimeOffset]::UtcNow
    $output = & $script:DotNetFull @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    @($output | ForEach-Object { $_.ToString() }) | Set-Content -LiteralPath $logPath -Encoding utf8NoBOM
    $script:PhaseResults.Add([ordered]@{
        name = $Name
        exit_code = $exitCode
        started_utc = $started.ToString('O')
        completed_utc = [DateTimeOffset]::UtcNow.ToString('O')
        log = [IO.Path]::GetFileName($logPath)
    })
}

$RepositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$script:RepositoryRoot = $RepositoryRoot
if ($ExpectedRevision -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'ExpectedRevision must be one exact 40-hex commit.'
}
$DotNetFull = [IO.Path]::GetFullPath($DotNetPath)
$ContractZipFull = [IO.Path]::GetFullPath($ContractZip)
if (-not (Test-Path -LiteralPath $DotNetFull -PathType Leaf)) { throw 'The exact dotnet runtime is unavailable.' }
if (-not (Test-Path -LiteralPath $ContractZipFull -PathType Leaf)) { throw 'The reviewed contract package is unavailable.' }
$script:DotNetFull = $DotNetFull

$BuildRootFull = Resolve-EmptyOutputRoot -Path $BuildRoot -Role 'BuildRoot' -RepositoryRoot $RepositoryRoot
$EvidenceRootFull = Resolve-EmptyOutputRoot -Path $EvidenceRoot -Role 'EvidenceRoot' -RepositoryRoot $RepositoryRoot
$script:EvidenceRootFull = $EvidenceRootFull
if ($BuildRootFull.Equals($EvidenceRootFull, [StringComparison]::OrdinalIgnoreCase) -or
    $BuildRootFull.StartsWith($EvidenceRootFull + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -or
    $EvidenceRootFull.StartsWith($BuildRootFull + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'BuildRoot and EvidenceRoot must be separate non-overlapping directories.'
}

$head = Invoke-Git @('rev-parse', 'HEAD')
if (-not $head.Equals($ExpectedRevision, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Frozen revision mismatch: expected $ExpectedRevision but HEAD is $head."
}
$status = Invoke-Git @('status', '--porcelain=v1', '--untracked-files=all')
if ($status.Length -ne 0) { throw 'The review preflight requires an exactly clean checkout.' }

$sourceManifest = @()
foreach ($relativePath in @((Invoke-Git @('ls-files')) -split "`n")) {
    if ([string]::IsNullOrWhiteSpace($relativePath)) { continue }
    $absolutePath = Join-Path $RepositoryRoot $relativePath
    $item = Get-Item -LiteralPath $absolutePath
    $sourceManifest += [ordered]@{
        path = $relativePath.Replace('\', '/')
        bytes = $item.Length
        sha256 = (Get-FileHash -LiteralPath $absolutePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$sourceManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceRootFull 'source-manifest.json') -Encoding utf8NoBOM

$metadata = [ordered]@{
    repository = $RepositoryRoot
    commit = $head
    tree = Invoke-Git @('rev-parse', 'HEAD^{tree}')
    parent = Invoke-Git @('rev-parse', 'HEAD^')
    branch = Invoke-Git @('branch', '--show-current')
    upstream = (& git -C $RepositoryRoot rev-parse --abbrev-ref '@{upstream}' 2>$null)
    remote_names = @(& git -C $RepositoryRoot remote)
    configuration = $Configuration
    dotnet_path = $DotNetFull
    dotnet_sha256 = (Get-FileHash -LiteralPath $DotNetFull -Algorithm SHA256).Hash.ToLowerInvariant()
    contract_zip = $ContractZipFull
    contract_sha256 = (Get-FileHash -LiteralPath $ContractZipFull -Algorithm SHA256).Hash.ToLowerInvariant()
    started_utc = [DateTimeOffset]::UtcNow.ToString('O')
}
$metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceRootFull 'frozen-object.json') -Encoding utf8NoBOM

$restoreLog = Join-Path $EvidenceRootFull 'restore.log'
$restoreOutput = & $DotNetFull restore (Join-Path $RepositoryRoot 'SyncShark.sln') --locked-mode `
    "-p:SyncSharkBuildRoot=$BuildRootFull" "-p:ContinuousIntegrationBuild=true" 2>&1
$restoreExit = $LASTEXITCODE
@($restoreOutput | ForEach-Object { $_.ToString() }) | Set-Content -LiteralPath $restoreLog -Encoding utf8NoBOM
if ($restoreExit -ne 0) { throw "Locked restore failed; see $restoreLog" }

$buildLog = Join-Path $EvidenceRootFull 'build.log'
$buildOutput = & $DotNetFull build (Join-Path $RepositoryRoot 'SyncShark.sln') --no-restore `
    -c $Configuration "-p:SyncSharkBuildRoot=$BuildRootFull" `
    "-p:SyncSharkSourceRevision=$ExpectedRevision" "-p:ContinuousIntegrationBuild=true" 2>&1
$buildExit = $LASTEXITCODE
@($buildOutput | ForEach-Object { $_.ToString() }) | Set-Content -LiteralPath $buildLog -Encoding utf8NoBOM
if ($buildExit -ne 0) { throw "Frozen build failed; see $buildLog" }

$testDll = Get-ChildItem -LiteralPath $BuildRootFull -Recurse -File -Filter 'SyncShark.Core.Tests.dll' |
    Where-Object { $_.FullName -match "[\\/]$Configuration[\\/]" } |
    Select-Object -First 1 -ExpandProperty FullName
if ([string]::IsNullOrWhiteSpace($testDll)) { throw 'The frozen test assembly was not found under BuildRoot.' }

$PhaseResults = [Collections.Generic.List[object]]::new()
$script:PhaseResults = $PhaseResults
$contractEvidence = Join-Path $EvidenceRootFull 'contract'
New-Item -ItemType Directory -Path $contractEvidence | Out-Null
Invoke-Phase 'architecture-boundaries' @($testDll, '--reviewer-2-rejection-preflight')
Invoke-Phase 'sqlite-authority' @($testDll, '--cloud-pro-authority-preflight')
Invoke-Phase 'true-sync-functional' @($testDll, '--true-sync-functional-preflight')
Invoke-Phase 'true-sync-process-crash' @($testDll, '--true-sync-crash-preflight')
Invoke-Phase 'complete-test-registry' @($testDll)

$witnessReceiptPath = Join-Path $EvidenceRootFull 'production-witness-execution.json'
$witnessPhases = @($PhaseResults | ForEach-Object {
    $logPath = Join-Path $EvidenceRootFull $_.log
    [ordered]@{
        name = $_.name
        exit_code = $_.exit_code
        started_utc = $_.started_utc
        completed_utc = $_.completed_utc
        log = $_.log
        log_sha256 = (Get-FileHash -LiteralPath $logPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
[ordered]@{
    revision = $head
    generated_utc = [DateTimeOffset]::UtcNow.ToString('O')
    phases = $witnessPhases
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $witnessReceiptPath -Encoding utf8NoBOM
Invoke-Phase 'contract-inventory' @($testDll, '--true-sync-contract-preflight', $ContractZipFull,
    $contractEvidence, $witnessReceiptPath)

$contractCoverage = Get-Content -LiteralPath (Join-Path $contractEvidence 'contract-coverage.json') -Raw | ConvertFrom-Json
$phaseFailures = @($PhaseResults | Where-Object { $_.exit_code -ne 0 })
$reviewReady = $phaseFailures.Count -eq 0 -and
    $contractCoverage.missing.Count -eq 0 -and
    $contractCoverage.structural_dependency_mapping_complete -eq $true -and
    $contractCoverage.generated_case_tests_executed -eq 0 -and
    $contractCoverage.case_specific_execution_claim -eq $false
$result = [ordered]@{
    revision = $head
    review_ready = $reviewReady
    structural_contract_complete = $contractCoverage.missing.Count -eq 0
    structural_dependency_mapping_complete = [bool]$contractCoverage.structural_dependency_mapping_complete
    generated_case_test_ids_declared = [int]$contractCoverage.generated_case_test_ids_declared
    generated_case_tests_executed = [int]$contractCoverage.generated_case_tests_executed
    case_specific_execution_claim = [bool]$contractCoverage.case_specific_execution_claim
    concrete_production_phase_receipts_passed = $phaseFailures.Count -eq 0
    phases = $PhaseResults
    completed_utc = [DateTimeOffset]::UtcNow.ToString('O')
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $EvidenceRootFull 'preflight-result.json') -Encoding utf8NoBOM
if (-not $reviewReady) {
    throw 'The frozen object is not review-ready; inspect preflight-result.json and phase logs.'
}
