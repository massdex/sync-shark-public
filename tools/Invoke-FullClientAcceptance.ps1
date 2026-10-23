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
    [string]$ExpectedVersion,
    [Parameter(Mandatory)]
    [string]$DotNetPath,
    [Parameter(Mandatory)]
    [string]$ContractZip,
    [Parameter(Mandatory)]
    [string]$InstalledStateRoot,
    [Parameter(Mandatory)]
    [string]$InstalledVersion2Database,
    [string]$RealDpiCaptureRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-EmptyOutputRoot {
    param([string]$Path, [string]$Role, [string]$RepositoryRoot)
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -match '(?i)[\\/]Dropbox[\\/]') { throw "$Role must be outside Dropbox." }
    if ($full.Equals($RepositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $full.StartsWith($RepositoryRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Role must be outside the frozen repository."
    }
    if (Test-Path -LiteralPath $full) {
        if (@(Get-ChildItem -LiteralPath $full -Force).Count -ne 0) {
            throw "$Role must be empty for one immutable acceptance run."
        }
    }
    else { New-Item -ItemType Directory -Path $full | Out-Null }
    return $full
}

function Invoke-Git {
    param([string[]]$Arguments)
    $value = & git -C $script:RepositoryRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed: $value" }
    return ($value -join "`n").Trim()
}

function Get-FileManifest {
    param([string]$Root)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    return @(Get-ChildItem -LiteralPath $fullRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($fullRoot.Length).TrimStart('\').Replace('\', '/')
            bytes = $_.Length
            last_write_utc = $_.LastWriteTimeUtc.ToString('O')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
}

function Get-EntryMetadataManifest {
    param([string]$Root, [string]$Filter = '*')
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $Root -Force -Filter $Filter | Sort-Object Name | ForEach-Object {
        [ordered]@{
            name = $_.Name
            kind = if ($_.PSIsContainer) { 'directory' } else { 'file' }
            bytes = if ($_.PSIsContainer) { $null } else { $_.Length }
            attributes = [string]$_.Attributes
            link_type = [string]$_.LinkType
            link_target = @($_.LinkTarget)
            creation_utc = $_.CreationTimeUtc.ToString('O')
            last_write_utc = $_.LastWriteTimeUtc.ToString('O')
        }
    })
}

function Invoke-AcceptancePhase {
    param([string]$Name, [string[]]$AcceptanceIds, [string[]]$Arguments)
    $logPath = Join-Path $script:EvidenceRootFull ($Name + '.log')
    $started = [DateTimeOffset]::UtcNow
    $output = & $script:DotNetFull @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $logLines = [string[]]@($output | ForEach-Object { $_.ToString() })
    [IO.File]::WriteAllLines($logPath, $logLines, [Text.UTF8Encoding]::new($false))
    $script:PhaseResults.Add([ordered]@{
        name = $Name
        acceptance_ids = $AcceptanceIds
        exit_code = $exitCode
        started_utc = $started.ToString('O')
        completed_utc = [DateTimeOffset]::UtcNow.ToString('O')
        log = [IO.Path]::GetFileName($logPath)
        log_sha256 = (Get-FileHash -LiteralPath $logPath -Algorithm SHA256).Hash.ToLowerInvariant()
    })
}

function Get-ExactPhaseMetric {
    param(
        [string]$PhaseName,
        [string]$MetricName,
        [string]$ExpectedLogSha256
    )
    $phase = @($script:PhaseResults | Where-Object { $_.name -eq $PhaseName })
    if ($phase.Count -ne 1) { throw "Metric $MetricName has no unique producing phase $PhaseName." }
    $logPath = Join-Path $script:EvidenceRootFull $phase[0].log
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        throw "Metric $MetricName producing log is absent."
    }
    $actualLogSha256 = (Get-FileHash -LiteralPath $logPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $actualLogSha256.Equals($ExpectedLogSha256, [StringComparison]::OrdinalIgnoreCase) -or
        -not $actualLogSha256.Equals($phase[0].log_sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Metric $MetricName producing log identity is stale or mismatched."
    }
    $matches = [Collections.Generic.List[object]]::new()
    foreach ($line in [IO.File]::ReadAllLines($logPath)) {
        $trimmed = $line.Trim()
        if (-not $trimmed.StartsWith('{', [StringComparison]::Ordinal)) { continue }
        try { $candidate = $trimmed | ConvertFrom-Json -ErrorAction Stop }
        catch { continue }
        if ($candidate.PSObject.Properties.Name -contains 'metric' -and
            [string]$candidate.metric -eq $MetricName) {
            $matches.Add([pscustomobject]@{ line=$trimmed; value=$candidate })
        }
    }
    if ($matches.Count -ne 1) {
        throw "Metric $MetricName must occur exactly once in its producing phase; found $($matches.Count)."
    }
    $lineBytes = [Text.Encoding]::UTF8.GetBytes($matches[0].line)
    $lineHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($lineBytes)).ToLowerInvariant()
    return [ordered]@{
        metric = $MetricName
        producing_phase = $PhaseName
        log = $phase[0].log
        log_sha256 = $actualLogSha256
        metric_line_sha256 = $lineHash
        value = $matches[0].value
    }
}

function Get-PhaseJsonMetricRows {
    param(
        [string]$PhaseName,
        [string]$ExpectedLogSha256
    )
    $phase = @($script:PhaseResults | Where-Object { $_.name -eq $PhaseName })
    if ($phase.Count -ne 1) { throw "JSON metrics have no unique producing phase $PhaseName." }
    $logPath = Join-Path $script:EvidenceRootFull $phase[0].log
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        throw "JSON metric producing log is absent for $PhaseName."
    }
    $actualLogSha256 = (Get-FileHash -LiteralPath $logPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $actualLogSha256.Equals($ExpectedLogSha256, [StringComparison]::OrdinalIgnoreCase) -or
        -not $actualLogSha256.Equals($phase[0].log_sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "JSON metric producing log identity is stale or mismatched for $PhaseName."
    }
    foreach ($line in [IO.File]::ReadAllLines($logPath)) {
        $trimmed = $line.Trim()
        if (-not $trimmed.StartsWith('{', [StringComparison]::Ordinal)) { continue }
        try { $candidate = $trimmed | ConvertFrom-Json -ErrorAction Stop }
        catch { continue }
        if ($candidate.PSObject.Properties.Name -contains 'metric') { $candidate }
    }
}

function Get-ExactCorrectionWitness {
    param(
        [string]$PhaseName,
        [string]$CorrectionId,
        [string]$ExpectedLogSha256
    )
    $phase = @($script:PhaseResults | Where-Object { $_.name -eq $PhaseName })
    if ($phase.Count -ne 1 -or $phase[0].exit_code -ne 0) {
        throw "Correction $CorrectionId has no unique passing phase $PhaseName."
    }
    $logPath = Join-Path $script:EvidenceRootFull $phase[0].log
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        throw "Correction $CorrectionId producing log is absent."
    }
    $actualLogSha256 = (Get-FileHash -LiteralPath $logPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $actualLogSha256.Equals($ExpectedLogSha256, [StringComparison]::OrdinalIgnoreCase) -or
        -not $actualLogSha256.Equals($phase[0].log_sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Correction $CorrectionId producing log identity is stale or mismatched."
    }
    $matches = [Collections.Generic.List[object]]::new()
    foreach ($line in [IO.File]::ReadAllLines($logPath)) {
        $trimmed = $line.Trim()
        if (-not $trimmed.StartsWith('{', [StringComparison]::Ordinal)) { continue }
        try { $candidate = $trimmed | ConvertFrom-Json -ErrorAction Stop }
        catch { continue }
        if ($candidate.PSObject.Properties.Name -contains 'correction_id' -and
            [string]$candidate.correction_id -eq $CorrectionId) {
            $matches.Add([pscustomobject]@{ line=$trimmed; value=$candidate })
        }
    }
    if ($matches.Count -ne 1 -or
        [string]$matches[0].value.status -ne 'executed_pass' -or
        [string]::IsNullOrWhiteSpace([string]$matches[0].value.witness)) {
        throw "Correction $CorrectionId must have one exact executed-pass witness; found $($matches.Count)."
    }
    $lineBytes = [Text.Encoding]::UTF8.GetBytes($matches[0].line)
    return [ordered]@{
        correction_id = $CorrectionId
        producing_phase = $PhaseName
        log = $phase[0].log
        log_sha256 = $actualLogSha256
        witness_line_sha256 = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($lineBytes)).ToLowerInvariant()
        witness = [string]$matches[0].value.witness
        status = [string]$matches[0].value.status
    }
}

function Test-MetricProjectionRejects {
    param([scriptblock]$Action)
    try { & $Action | Out-Null; return $false }
    catch { return $true }
}

function Assert-ExactJsonObject {
    param([object]$Value, [string[]]$Properties, [string]$Context)
    if ($null -eq $Value -or $Value -isnot [pscustomobject]) {
        throw "$Context must be one JSON object."
    }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $Properties) {
        if (-not $expected.Add($name)) { throw "$Context schema repeats property $name." }
    }
    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $expected.Count -or
        @($actual | Where-Object { -not $expected.Contains($_) }).Count -ne 0) {
        throw "$Context has a missing, extra, or incorrectly-cased property."
    }
}

function Assert-JsonString {
    param([object]$Value, [string]$Context, [string[]]$Allowed = @())
    if ($null -eq $Value -or $Value.GetType() -ne [string] -or
        [string]::IsNullOrWhiteSpace($Value)) { throw "$Context must be a non-empty JSON string." }
    if ($Allowed.Count -gt 0 -and -not $Allowed.Contains([string]$Value)) {
        throw "$Context has an unsupported value."
    }
}

function Assert-JsonInteger {
    param([object]$Value, [string]$Context, [long]$Minimum = [long]::MinValue,
        [long]$Maximum = [long]::MaxValue)
    if ($null -eq $Value -or $Value.GetType() -ne [long] -or
        $Value -lt $Minimum -or $Value -gt $Maximum) {
        throw "$Context must be an in-range JSON integer."
    }
}

function Assert-JsonBoolean {
    param([object]$Value, [string]$Context)
    if ($null -eq $Value -or $Value.GetType() -ne [bool]) {
        throw "$Context must be a JSON boolean."
    }
}

function Assert-JsonArray {
    param([object]$Value, [string]$Context)
    if ($null -eq $Value -or $Value.GetType() -ne [object[]]) {
        throw "$Context must be a JSON array."
    }
}

function Assert-JsonNull {
    param([object]$Value, [string]$Context)
    if ($null -ne $Value) { throw "$Context must be JSON null." }
}

function Assert-Sha256String {
    param([object]$Value, [string]$Context)
    Assert-JsonString -Value $Value -Context $Context
    if (-not ([string]$Value -cmatch '^[0-9a-f]{64}$')) {
        throw "$Context must be one lowercase 64-hex SHA-256 value."
    }
}

function Assert-OperationMemberInventory {
    param([object]$Value, [string]$Context, [bool]$HasPresence = $false)
    Assert-JsonArray -Value $Value -Context $Context
    $required = @('MemberId','PairId','ObjectId','Role','RootBindingId','ExpectedPathKey')
    if ($HasPresence) { $required += 'present' }
    $memberIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($member in @($Value)) {
        Assert-ExactJsonObject -Value $member -Properties $required -Context "$Context member"
        foreach ($name in @('MemberId','PairId','ObjectId','RootBindingId','ExpectedPathKey')) {
            Assert-JsonString -Value $member.$name -Context "$Context.$name"
        }
        Assert-JsonString -Value $member.Role -Context "$Context.Role" `
            -Allowed @('source','destination','lock','affected')
        if ($HasPresence) { Assert-JsonBoolean -Value $member.present -Context "$Context.present" }
        if (-not $memberIds.Add([string]$member.MemberId)) {
            throw "$Context repeats an immutable member identity."
        }
    }
}

function Assert-PlanningParticipantInventory {
    param([object]$Value, [string]$Context, [int]$ExpectedCount = -1)
    Assert-JsonArray -Value $Value -Context $Context
    if ($ExpectedCount -ge 0 -and @($Value).Count -ne $ExpectedCount) {
        throw "$Context has the wrong participant count."
    }
    $pairIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $observationSetIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($participant in @($Value)) {
        Assert-ExactJsonObject -Value $participant -Properties @('ObservationSetId','PairId','ConnectionId',
            'LocalScanGenerationId','DropboxScanGenerationId','ObservationContentFingerprint',
            'SynchronizationPointSetFingerprint','RootAuthorityFingerprint') -Context "$Context participant"
        foreach ($name in @('ObservationSetId','PairId','ConnectionId','LocalScanGenerationId',
                'DropboxScanGenerationId')) {
            Assert-JsonString -Value $participant.$name -Context "$Context.$name"
        }
        foreach ($name in @('ObservationContentFingerprint','SynchronizationPointSetFingerprint',
                'RootAuthorityFingerprint')) {
            Assert-Sha256String -Value $participant.$name -Context "$Context.$name"
        }
        if (-not $pairIds.Add([string]$participant.PairId)) {
            throw "$Context repeats a participant pair."
        }
        $null = $observationSetIds.Add([string]$participant.ObservationSetId)
    }
    if ($observationSetIds.Count -ne 1) { throw "$Context spans multiple observation sets." }
}

function Assert-CrossPairReadyAuthorityMetric {
    param([object]$Row)
    Assert-ExactJsonObject -Value $Row -Properties @('metric','dimension','readyOperationPersisted',
        'planningParticipants','invalidatedAuthorities','retainedConnectionInventory',
        'inactiveConnectionRegistered','inactiveConnectionEligibleForPlanning',
        'dropboxRootNamespaceBoundToActiveConnection','supportedEntryPoint','supportedEntryPointFenced',
        'readyOperationUnchangedByRejectedEntry','finalAdmission','attempts',
        'providerOrFilesystemEffects','destinationVersionPreserved','unaffectedPairProgressed') `
        -Context 'cross-pair Ready authority metric'
    Assert-JsonString $Row.metric 'ready.metric' @('cross_pair_ready_authority_transition')
    Assert-JsonString $Row.dimension 'ready.dimension' @('configuration-revision','connection-account',
        'destination-content','root-authority','selection-definition','synchronization-point-set')
    Assert-JsonBoolean $Row.readyOperationPersisted 'ready.readyOperationPersisted'
    Assert-PlanningParticipantInventory $Row.planningParticipants 'ready.planningParticipants' 3
    Assert-JsonInteger $Row.invalidatedAuthorities 'ready.invalidatedAuthorities' 1 3
    Assert-JsonString $Row.finalAdmission 'ready.finalAdmission' @('Superseded')
    Assert-JsonInteger $Row.attempts 'ready.attempts' 0 0
    Assert-JsonInteger $Row.providerOrFilesystemEffects 'ready.providerOrFilesystemEffects' 0 0
    Assert-JsonBoolean $Row.destinationVersionPreserved 'ready.destinationVersionPreserved'
    Assert-JsonBoolean $Row.unaffectedPairProgressed 'ready.unaffectedPairProgressed'
    if (-not $Row.readyOperationPersisted -or -not $Row.destinationVersionPreserved -or
        -not $Row.unaffectedPairProgressed) { throw 'Ready authority summary values are inconsistent.' }
    if ($Row.dimension -eq 'connection-account') {
        Assert-JsonArray $Row.retainedConnectionInventory 'ready.retainedConnectionInventory'
        if (@($Row.retainedConnectionInventory).Count -ne 2) {
            throw 'Ready connection inventory must contain exactly active and inactive Dropbox records.'
        }
        foreach ($connection in @($Row.retainedConnectionInventory)) {
            Assert-ExactJsonObject $connection @('ConnectionId','Provider','AccountId','RootNamespaceId','IsActive') `
                'ready retained connection'
            foreach ($name in @('ConnectionId','Provider','AccountId','RootNamespaceId')) {
                Assert-JsonString $connection.$name "ready connection.$name"
            }
            Assert-JsonBoolean $connection.IsActive 'ready connection.IsActive'
        }
        if (@($Row.retainedConnectionInventory | Where-Object { $_.IsActive }).Count -ne 1 -or
            @($Row.retainedConnectionInventory | Where-Object { $_.Provider -cne 'dropbox' }).Count -ne 0) {
            throw 'Ready connection inventory did not isolate one active Dropbox identity.'
        }
        foreach ($name in @('inactiveConnectionRegistered','inactiveConnectionEligibleForPlanning',
                'dropboxRootNamespaceBoundToActiveConnection')) {
            Assert-JsonBoolean $Row.$name "ready.$name"
        }
        if (-not $Row.inactiveConnectionRegistered -or $Row.inactiveConnectionEligibleForPlanning -or
            -not $Row.dropboxRootNamespaceBoundToActiveConnection) {
            throw 'Ready account/root namespace summary values are inconsistent.'
        }
    }
    else {
        Assert-JsonNull $Row.retainedConnectionInventory 'ready.retainedConnectionInventory'
        Assert-JsonNull $Row.inactiveConnectionRegistered 'ready.inactiveConnectionRegistered'
        Assert-JsonNull $Row.inactiveConnectionEligibleForPlanning 'ready.inactiveConnectionEligibleForPlanning'
        Assert-JsonNull $Row.dropboxRootNamespaceBoundToActiveConnection `
            'ready.dropboxRootNamespaceBoundToActiveConnection'
    }
    $expectedEntryPoint = switch ([string]$Row.dimension) {
        'selection-definition' { 'SetFolderSelectionAsync' }
        'configuration-revision' { 'BindPairAsync' }
        'root-authority' { 'BindPairAsync' }
        'connection-account' { 'DropboxConnectionStore.SaveAsync' }
        default { $null }
    }
    if ($null -ne $expectedEntryPoint) {
        Assert-JsonString $Row.supportedEntryPoint 'ready.supportedEntryPoint' @($expectedEntryPoint)
        Assert-JsonBoolean $Row.supportedEntryPointFenced 'ready.supportedEntryPointFenced'
        Assert-JsonBoolean $Row.readyOperationUnchangedByRejectedEntry `
            'ready.readyOperationUnchangedByRejectedEntry'
        if (-not $Row.supportedEntryPointFenced -or -not $Row.readyOperationUnchangedByRejectedEntry) {
            throw 'Ready selection entry-point summary values are inconsistent.'
        }
    }
    else {
        Assert-JsonNull $Row.supportedEntryPoint 'ready.supportedEntryPoint'
        Assert-JsonNull $Row.supportedEntryPointFenced 'ready.supportedEntryPointFenced'
        Assert-JsonNull $Row.readyOperationUnchangedByRejectedEntry `
            'ready.readyOperationUnchangedByRejectedEntry'
    }
}

function Assert-OrdinaryReadyInvalidationMetric {
    param([object]$Row)
    Assert-ExactJsonObject $Row @('metric','scenario','operationKind','staleOperation',
        'successorOperation','staleEffects','authorityCurrentSuccessorOnly','finalContentConverged',
        'totalEffectDelta') 'ordinary Ready invalidation metric'
    Assert-JsonString $Row.metric 'ordinary.metric' @('ordinary_ready_operation_invalidation')
    $mapping = [ordered]@{
        'create-local'='CreateLocal'; 'create-remote'='CreateRemote'; 'delete-local'='DeleteLocal'
        'delete-remote'='DeleteRemote'; 'move-local'='MoveLocal'; 'move-remote'='MoveRemote'
    }
    Assert-JsonString $Row.scenario 'ordinary.scenario' @($mapping.Keys)
    Assert-JsonString $Row.operationKind 'ordinary.operationKind' @($mapping.Values)
    if ($mapping[[string]$Row.scenario] -cne [string]$Row.operationKind) {
        throw 'Ordinary invalidation scenario and operation kind disagree.'
    }
    Assert-ExactJsonObject $Row.staleOperation @('OperationId','status','attempts','members',
        'planningAuthority') 'ordinary.staleOperation'
    Assert-ExactJsonObject $Row.successorOperation @('OperationId','status','attempts','members') `
        'ordinary.successorOperation'
    Assert-JsonString $Row.staleOperation.OperationId 'ordinary stale OperationId'
    Assert-JsonString $Row.successorOperation.OperationId 'ordinary successor OperationId'
    if ([string]$Row.staleOperation.OperationId -ceq [string]$Row.successorOperation.OperationId) {
        throw 'Ordinary stale and successor operations must have distinct identities.'
    }
    Assert-JsonString $Row.staleOperation.status 'ordinary stale status' @('Superseded')
    Assert-JsonInteger $Row.staleOperation.attempts 'ordinary stale attempts' 0 0
    Assert-OperationMemberInventory $Row.staleOperation.members 'ordinary stale members'
    Assert-PlanningParticipantInventory $Row.staleOperation.planningAuthority `
        'ordinary stale planning authority' 1
    Assert-JsonString $Row.successorOperation.status 'ordinary successor status' @('Completed')
    Assert-JsonInteger $Row.successorOperation.attempts 'ordinary successor attempts' 1 8
    Assert-OperationMemberInventory $Row.successorOperation.members 'ordinary successor members'
    Assert-JsonInteger $Row.staleEffects 'ordinary.staleEffects' 0 0
    Assert-JsonBoolean $Row.authorityCurrentSuccessorOnly 'ordinary.authorityCurrentSuccessorOnly'
    Assert-JsonBoolean $Row.finalContentConverged 'ordinary.finalContentConverged'
    if (-not $Row.authorityCurrentSuccessorOnly -or -not $Row.finalContentConverged) {
        throw 'Ordinary invalidation summary values are inconsistent.'
    }
    Assert-ExactJsonObject $Row.totalEffectDelta @('uploads','downloads','archives','deletes','moves',
        'directoryCreates') 'ordinary.totalEffectDelta'
    foreach ($name in @('uploads','downloads','archives','deletes','moves','directoryCreates')) {
        Assert-JsonInteger $Row.totalEffectDelta.$name "ordinary.totalEffectDelta.$name" 0 2
    }
    $expectedEffects = [ordered]@{
        'create-local'=@(0,1,0,0,0,0); 'create-remote'=@(1,0,0,0,0,0)
        'delete-local'=@(0,0,0,0,0,0); 'delete-remote'=@(0,0,1,1,0,0)
        'move-local'=@(0,0,0,0,0,0); 'move-remote'=@(0,0,0,0,1,0)
    }
    $actualEffects = @($Row.totalEffectDelta.uploads,$Row.totalEffectDelta.downloads,
        $Row.totalEffectDelta.archives,$Row.totalEffectDelta.deletes,$Row.totalEffectDelta.moves,
        $Row.totalEffectDelta.directoryCreates)
    if (@(Compare-Object $expectedEffects[[string]$Row.scenario] $actualEffects -SyncWindow 0).Count -ne 0) {
        throw 'Ordinary invalidation effect summary disagrees with the scenario.'
    }
}

function Assert-CrossPairDirectoryRuntimeMetric {
    param([object]$Row)
    Assert-ExactJsonObject $Row @('metric','direction','deletionEnabled','populatedDescendants',
        'observedWorkers','adverseOrdering','rootEffectOrder','unrelatedEffectOrder',
        'ordinaryDescendantPlans','forbiddenOrdinaryOperationInventory','rootMoveEffects',
        'fallbackUploads','fallbackDownloads','fallbackArchives','fallbackDeletes',
        'fallbackDirectoryCreates','logicalIdentitiesPreserved','ntfsIdentitiesPreserved',
        'providerIdentitiesPreserved','retainedSubtreePrefixCount','immutableBindingPathSetClosed',
        'unrelatedPairProgressed','operationMemberCount',
        'operationMemberRoleCounts','operationMemberInventory','recoveryArtifactInventory',
        'effectAdmissionTrace') 'directory runtime metric'
    Assert-JsonString $Row.metric 'directory runtime.metric' @('cross_pair_directory_descendant_full_runtime')
    Assert-JsonString $Row.direction 'directory runtime.direction' @('local-to-dropbox','dropbox-to-local')
    Assert-JsonBoolean $Row.deletionEnabled 'directory runtime.deletionEnabled'
    foreach ($name in @('populatedDescendants','ordinaryDescendantPlans','fallbackUploads',
            'fallbackDownloads','fallbackArchives','fallbackDeletes','fallbackDirectoryCreates')) {
        Assert-JsonInteger $Row.$name "directory runtime.$name" 0 3
    }
    Assert-JsonInteger $Row.observedWorkers 'directory runtime.observedWorkers' 2 64
    Assert-JsonString $Row.adverseOrdering 'directory runtime.adverseOrdering' @('root-first')
    Assert-JsonInteger $Row.rootEffectOrder 'directory runtime.rootEffectOrder' 1 1
    Assert-JsonInteger $Row.unrelatedEffectOrder 'directory runtime.unrelatedEffectOrder' 2 2
    Assert-JsonInteger $Row.rootMoveEffects 'directory runtime.rootMoveEffects' 1 1
    foreach ($name in @('logicalIdentitiesPreserved','ntfsIdentitiesPreserved',
            'providerIdentitiesPreserved')) {
        Assert-JsonInteger $Row.$name "directory runtime.$name" 4 4
    }
    Assert-JsonInteger $Row.retainedSubtreePrefixCount `
        'directory runtime.retainedSubtreePrefixCount' 4 4
    Assert-JsonBoolean $Row.immutableBindingPathSetClosed `
        'directory runtime.immutableBindingPathSetClosed'
    Assert-JsonBoolean $Row.unrelatedPairProgressed 'directory runtime.unrelatedPairProgressed'
    if (-not $Row.deletionEnabled -or -not $Row.unrelatedPairProgressed -or
        $Row.populatedDescendants -ne 3 -or $Row.ordinaryDescendantPlans -ne 0 -or
        $Row.retainedSubtreePrefixCount -ne 4 -or
        $Row.immutableBindingPathSetClosed -ne $true -or
        ($Row.fallbackUploads + $Row.fallbackDownloads + $Row.fallbackArchives +
         $Row.fallbackDeletes + $Row.fallbackDirectoryCreates) -ne 0) {
        throw 'Directory runtime summary values are inconsistent.'
    }
    Assert-JsonArray $Row.forbiddenOrdinaryOperationInventory `
        'directory runtime.forbiddenOrdinaryOperationInventory'
    Assert-JsonArray $Row.recoveryArtifactInventory 'directory runtime.recoveryArtifactInventory'
    if (@($Row.forbiddenOrdinaryOperationInventory).Count -ne 0 -or
        @($Row.recoveryArtifactInventory).Count -ne 0) {
        throw 'Directory runtime retained a forbidden operation or recovery artifact.'
    }
    Assert-JsonInteger $Row.operationMemberCount 'directory runtime.operationMemberCount' 29 29
    Assert-ExactJsonObject $Row.operationMemberRoleCounts @('source','lock','affected','destination') `
        'directory runtime.operationMemberRoleCounts'
    $expectedRoles = [ordered]@{ source=8; lock=16; affected=4; destination=1 }
    foreach ($name in $expectedRoles.Keys) {
        Assert-JsonInteger $Row.operationMemberRoleCounts.$name "directory runtime role $name" `
            $expectedRoles[$name] $expectedRoles[$name]
    }
    Assert-OperationMemberInventory $Row.operationMemberInventory `
        'directory runtime.operationMemberInventory' $true
    if (@($Row.operationMemberInventory).Count -ne $Row.operationMemberCount) {
        throw 'Directory runtime member count and inventory disagree.'
    }
    Assert-ExactJsonObject $Row.effectAdmissionTrace @('phase','OperationId','status','participantCount',
        'participants') 'directory runtime.effectAdmissionTrace'
    Assert-JsonString $Row.effectAdmissionTrace.phase 'directory admission.phase' `
        @('cross-pair-effect-admitted-before-physical-move')
    Assert-JsonString $Row.effectAdmissionTrace.OperationId 'directory admission.OperationId'
    Assert-JsonString $Row.effectAdmissionTrace.status 'directory admission.status' @('EffectIssuing')
    Assert-JsonInteger $Row.effectAdmissionTrace.participantCount 'directory admission.participantCount' 3 3
    Assert-PlanningParticipantInventory $Row.effectAdmissionTrace.participants `
        'directory admission.participants' 3
}

function Assert-CrossPairDirectoryCrashMetric {
    param([object]$Row)
    Assert-ExactJsonObject $Row @('metric','direction','abruptRecoveryEquivalenceKey','childExitCode',
        'interruptedStatus','completedStatus','retainedAttempts','retainedSourceMembers',
        'retainedRootPathClaims','deletionEnabled','completedMoves','additionalEffectsDuringRecovery',
        'additionalRootMoveEffectsAfterRestart','fallbackUploads','fallbackDownloads','fallbackArchives',
        'fallbackDeletes','fallbackDirectoryCreates','unexpectedRecoveryArtifacts',
        'logicalIdentitiesPreserved','ntfsIdentitiesPreserved','providerIdentitiesPreserved',
        'unrelatedPairProgressed') 'directory process-recovery metric'
    Assert-JsonString $Row.metric 'directory crash.metric' `
        @('cross_pair_directory_post_effect_process_recovery')
    Assert-JsonString $Row.direction 'directory crash.direction' @('local-to-dropbox','dropbox-to-local')
    Assert-Sha256String $Row.abruptRecoveryEquivalenceKey 'directory crash.abruptRecoveryEquivalenceKey'
    Assert-JsonInteger $Row.childExitCode 'directory crash.childExitCode' 215 215
    Assert-JsonString $Row.interruptedStatus 'directory crash.interruptedStatus' @('EffectIssuing')
    Assert-JsonString $Row.completedStatus 'directory crash.completedStatus' @('Completed')
    Assert-JsonInteger $Row.retainedAttempts 'directory crash.retainedAttempts' 1 1
    Assert-JsonInteger $Row.retainedSourceMembers 'directory crash.retainedSourceMembers' 8 8
    Assert-JsonInteger $Row.retainedRootPathClaims 'directory crash.retainedRootPathClaims' 16 16
    Assert-JsonBoolean $Row.deletionEnabled 'directory crash.deletionEnabled'
    Assert-JsonInteger $Row.completedMoves 'directory crash.completedMoves' 1 1
    foreach ($name in @('additionalEffectsDuringRecovery','additionalRootMoveEffectsAfterRestart',
            'fallbackUploads','fallbackDownloads','fallbackArchives','fallbackDeletes',
            'fallbackDirectoryCreates','unexpectedRecoveryArtifacts')) {
        Assert-JsonInteger $Row.$name "directory crash.$name" 0 0
    }
    foreach ($name in @('logicalIdentitiesPreserved','ntfsIdentitiesPreserved',
            'providerIdentitiesPreserved')) {
        Assert-JsonInteger $Row.$name "directory crash.$name" 4 4
    }
    Assert-JsonBoolean $Row.unrelatedPairProgressed 'directory crash.unrelatedPairProgressed'
    if (-not $Row.deletionEnabled -or -not $Row.unrelatedPairProgressed) {
        throw 'Directory process-recovery summary values are inconsistent.'
    }
}

function Assert-CrossPairDirectoryDivergenceMetric {
    param([object]$Row)
    Assert-ExactJsonObject $Row @('metric','direction','mutation','timing','deletionEnabled',
        'readyOperationPersisted','operationStatus','restartStatus','retainedNoEffectAttempt',
        'attemptStatus','effectKind','rootMoveEffectsAfterDivergenceObservation','fallbackUploads',
        'fallbackDownloads','fallbackArchives','fallbackDeletes','fallbackDirectoryCreates',
        'unexpectedRecoveryArtifacts','sourceContentPreserved','divergentContentPreserved',
        'unrelatedPairProgressed','forbiddenOrdinaryOperationInventory','operationMemberCount',
        'operationMemberRoleCounts','crossPairStatusCounts','operationStatusInventory',
        'operationMemberInventory') 'directory divergence metric'
    Assert-JsonString $Row.metric 'divergence.metric' @('cross_pair_directory_descendant_divergence')
    Assert-JsonString $Row.direction 'divergence.direction' @('local-to-dropbox','dropbox-to-local')
    Assert-JsonString $Row.mutation 'divergence.mutation' @('edit','add','delete','matching-add')
    Assert-JsonString $Row.timing 'divergence.timing' `
        @('after-root-effect','before-planning','ready-before-effect')
    $mutationTiming = "$($Row.mutation)|$($Row.timing)"
    if ($mutationTiming -notin @('edit|after-root-effect','matching-add|after-root-effect',
            'edit|before-planning','add|before-planning','delete|before-planning',
            'edit|ready-before-effect')) {
        throw 'Directory divergence mutation/timing combination is not in the exact matrix.'
    }
    Assert-JsonBoolean $Row.deletionEnabled 'divergence.deletionEnabled'
    Assert-JsonBoolean $Row.readyOperationPersisted 'divergence.readyOperationPersisted'
    if (-not $Row.deletionEnabled -or
        $Row.readyOperationPersisted -ne ($Row.timing -in @('ready-before-effect','after-root-effect'))) {
        throw 'Directory divergence Ready/deletion summary is inconsistent.'
    }
    Assert-JsonString $Row.operationStatus 'divergence.operationStatus' @('Blocked','Superseded')
    Assert-JsonString $Row.restartStatus 'divergence.restartStatus' @('Blocked','Superseded')
    if ($Row.operationStatus -cne $Row.restartStatus) {
        throw 'Directory divergence status changed across restart.'
    }
    Assert-JsonInteger $Row.retainedNoEffectAttempt 'divergence.retainedNoEffectAttempt' 0 1
    Assert-JsonString $Row.attemptStatus 'divergence.attemptStatus' @('None','Blocked')
    Assert-JsonString $Row.effectKind 'divergence.effectKind' @('none','cross-pair-precondition-rejected-no-effect',
        'dropbox-cross-root-native-move','local-cross-root-move')
    if (($Row.retainedNoEffectAttempt -eq 0 -and
         ($Row.attemptStatus -cne 'None' -or $Row.effectKind -cne 'none')) -or
        ($Row.retainedNoEffectAttempt -eq 1 -and $Row.attemptStatus -cne 'Blocked')) {
        throw 'Directory divergence attempt summary is inconsistent.'
    }
    Assert-JsonInteger $Row.rootMoveEffectsAfterDivergenceObservation `
        'divergence.rootMoveEffectsAfterDivergenceObservation' 0 1
    if ($Row.rootMoveEffectsAfterDivergenceObservation -ne
        $(if ($Row.timing -eq 'after-root-effect') { 1 } else { 0 })) {
        throw 'Directory divergence root-effect count disagrees with timing.'
    }
    foreach ($name in @('fallbackUploads','fallbackDownloads','fallbackArchives','fallbackDeletes',
            'fallbackDirectoryCreates','unexpectedRecoveryArtifacts')) {
        Assert-JsonInteger $Row.$name "divergence.$name" 0 0
    }
    foreach ($name in @('sourceContentPreserved','divergentContentPreserved','unrelatedPairProgressed')) {
        Assert-JsonBoolean $Row.$name "divergence.$name"
        if (-not $Row.$name) { throw "Directory divergence $name was not preserved." }
    }
    Assert-JsonArray $Row.forbiddenOrdinaryOperationInventory `
        'divergence.forbiddenOrdinaryOperationInventory'
    if (@($Row.forbiddenOrdinaryOperationInventory).Count -ne 0) {
        throw 'Directory divergence admitted a forbidden ordinary operation.'
    }
    Assert-JsonInteger $Row.operationMemberCount 'divergence.operationMemberCount' 1 256
    Assert-ExactJsonObject $Row.operationMemberRoleCounts @('source','lock','affected','destination') `
        'divergence.operationMemberRoleCounts'
    $roleSum = 0
    foreach ($name in @('source','lock','affected','destination')) {
        Assert-JsonInteger $Row.operationMemberRoleCounts.$name "divergence role $name" 0 256
        $roleSum += $Row.operationMemberRoleCounts.$name
    }
    if ($roleSum -ne $Row.operationMemberCount) { throw 'Divergence role and member counts disagree.' }
    Assert-OperationMemberInventory $Row.operationMemberInventory 'divergence.operationMemberInventory' $true
    if (@($Row.operationMemberInventory).Count -ne $Row.operationMemberCount) {
        throw 'Divergence member count and inventory disagree.'
    }
    $operationStatuses = @('Planned','Ready','Leased','Validating','AllocationAuthorized','Staged',
        'EffectIssuing','EffectUnknown','EffectObserved','Reconciling','Verifying','Committed',
        'CleanupPending','Completed','Blocked','Cancelled','Superseded')
    Assert-ExactJsonObject $Row.crossPairStatusCounts $operationStatuses 'divergence.crossPairStatusCounts'
    foreach ($status in $operationStatuses) {
        Assert-JsonInteger $Row.crossPairStatusCounts.$status "divergence status $status" 0 256
    }
    Assert-JsonArray $Row.operationStatusInventory 'divergence.operationStatusInventory'
    if (@($Row.operationStatusInventory).Count -lt 1) {
        throw 'Divergence operation status inventory is empty.'
    }
    foreach ($operation in @($Row.operationStatusInventory)) {
        Assert-ExactJsonObject $operation @('OperationId','kind','status','attemptCount','attemptStatuses',
            'effectKinds') 'divergence operation status'
        Assert-JsonString $operation.OperationId 'divergence operation.OperationId'
        Assert-JsonString $operation.kind 'divergence operation.kind' @('CrossPairMove')
        Assert-JsonString $operation.status 'divergence operation.status' @('Blocked','Superseded')
        Assert-JsonInteger $operation.attemptCount 'divergence operation.attemptCount' 0 1
        Assert-JsonArray $operation.attemptStatuses 'divergence operation.attemptStatuses'
        Assert-JsonArray $operation.effectKinds 'divergence operation.effectKinds'
        if (@($operation.attemptStatuses).Count -ne $operation.attemptCount -or
            @($operation.effectKinds).Count -ne $operation.attemptCount) {
            throw 'Divergence operation attempt arrays disagree with their count.'
        }
        foreach ($status in @($operation.attemptStatuses)) {
            Assert-JsonString $status 'divergence attempt status' @('Blocked')
        }
        foreach ($kind in @($operation.effectKinds)) {
            Assert-JsonString $kind 'divergence effect kind' @('cross-pair-precondition-rejected-no-effect',
                'dropbox-cross-root-native-move','local-cross-root-move')
        }
    }
    foreach ($status in $operationStatuses) {
        $observedCount = @($Row.operationStatusInventory | Where-Object { $_.status -ceq $status }).Count
        if ($Row.crossPairStatusCounts.$status -ne $observedCount) {
            throw "Divergence status count for $status disagrees with the exact inventory."
        }
    }
}

function Assert-CrossPairDirectorySubtreePrefixAuthorityMetric {
    param([object]$Row)
    Assert-ExactJsonObject $Row @('metric','direction','retainedPrefixCount',
        'injectedAfterReservation','blockedAcrossRestart','localOnlyLaterChildRejected',
        'dropboxOnlyLaterChildRejected','forbiddenOrdinaryOperationCount','fallbackUploads',
        'fallbackDownloads','fallbackArchives','fallbackDeletes','fallbackDirectoryCreates',
        'unrelatedPairProgressed') 'directory subtree-prefix authority metric'
    Assert-JsonString $Row.metric 'subtree prefix.metric' `
        @('cross_pair_directory_subtree_prefix_authority')
    Assert-JsonString $Row.direction 'subtree prefix.direction' `
        @('local-to-dropbox','dropbox-to-local')
    Assert-JsonInteger $Row.retainedPrefixCount 'subtree prefix.retainedPrefixCount' 4 4
    foreach ($name in @('injectedAfterReservation','blockedAcrossRestart',
            'localOnlyLaterChildRejected','dropboxOnlyLaterChildRejected',
            'unrelatedPairProgressed')) {
        Assert-JsonBoolean $Row.$name "subtree prefix.$name"
        if (-not $Row.$name) { throw "Directory subtree-prefix $name was not proved." }
    }
    foreach ($name in @('forbiddenOrdinaryOperationCount','fallbackUploads','fallbackDownloads',
            'fallbackArchives','fallbackDeletes','fallbackDirectoryCreates')) {
        Assert-JsonInteger $Row.$name "subtree prefix.$name" 0 0
    }
}

function Assert-CrossPairDirectoryOrderingMetric {
    param([object]$Row)
    Assert-ExactJsonObject $Row @('metric','direction','adverseOrdering','staleDescendantKind',
        'staleDescendantStatus','staleDescendantAttempts','rootMoveCompleted','fallbackUploads',
        'fallbackDownloads','fallbackArchives','fallbackDeletes','fallbackDirectoryCreates',
        'logicalIdentitiesPreserved','ntfsIdentitiesPreserved','providerIdentitiesPreserved',
        'unrelatedPairProgressed') 'directory adverse-ordering metric'
    Assert-JsonString $Row.metric 'ordering.metric' @('cross_pair_directory_adverse_ordering')
    Assert-JsonString $Row.direction 'ordering.direction' @('local-to-dropbox','dropbox-to-local')
    Assert-JsonString $Row.adverseOrdering 'ordering.adverseOrdering' `
        @('stale-descendant-ready-before-root')
    Assert-JsonString $Row.staleDescendantKind 'ordering.staleDescendantKind' @('UpdateLocal','UpdateRemote')
    $expectedKind = if ($Row.direction -eq 'local-to-dropbox') { 'UpdateRemote' } else { 'UpdateLocal' }
    if ($Row.staleDescendantKind -cne $expectedKind) { throw 'Adverse ordering direction/kind mismatch.' }
    Assert-JsonString $Row.staleDescendantStatus 'ordering.staleDescendantStatus' @('Superseded')
    Assert-JsonInteger $Row.staleDescendantAttempts 'ordering.staleDescendantAttempts' 0 0
    Assert-JsonInteger $Row.rootMoveCompleted 'ordering.rootMoveCompleted' 1 1
    foreach ($name in @('fallbackUploads','fallbackDownloads','fallbackArchives','fallbackDeletes',
            'fallbackDirectoryCreates')) { Assert-JsonInteger $Row.$name "ordering.$name" 0 0 }
    foreach ($name in @('logicalIdentitiesPreserved','ntfsIdentitiesPreserved',
            'providerIdentitiesPreserved')) { Assert-JsonInteger $Row.$name "ordering.$name" 4 4 }
    Assert-JsonBoolean $Row.unrelatedPairProgressed 'ordering.unrelatedPairProgressed'
    if (-not $Row.unrelatedPairProgressed) { throw 'Adverse ordering did not preserve unrelated progress.' }
}

function Assert-CrossPairDirectoryCollisionMetric {
    param([object]$Row)
    Assert-ExactJsonObject $Row @('metric','direction','collision','operationStatus',
        'retainedNoEffectAttempt','sourceContentPreserved','destinationOccupantPreserved',
        'rootMoveEffectsAfterCollisionObservation','fallbackUploads','fallbackDownloads',
        'fallbackArchives','fallbackDeletes','fallbackDirectoryCreates','unexpectedRecoveryArtifacts',
        'unrelatedPairProgressed') 'directory collision metric'
    Assert-JsonString $Row.metric 'collision.metric' @('cross_pair_directory_destination_collision')
    Assert-JsonString $Row.direction 'collision.direction' @('local-to-dropbox','dropbox-to-local')
    Assert-JsonString $Row.collision 'collision.collision' @('populated-destination-subtree')
    Assert-JsonString $Row.operationStatus 'collision.operationStatus' @('Blocked')
    Assert-JsonInteger $Row.retainedNoEffectAttempt 'collision.retainedNoEffectAttempt' 1 1
    foreach ($name in @('sourceContentPreserved','destinationOccupantPreserved','unrelatedPairProgressed')) {
        Assert-JsonBoolean $Row.$name "collision.$name"
        if (-not $Row.$name) { throw "Collision $name was not preserved." }
    }
    foreach ($name in @('rootMoveEffectsAfterCollisionObservation','fallbackUploads','fallbackDownloads',
            'fallbackArchives','fallbackDeletes','fallbackDirectoryCreates','unexpectedRecoveryArtifacts')) {
        Assert-JsonInteger $Row.$name "collision.$name" 0 0
    }
}

function Assert-DependentRecoveryMetric {
    param([object]$Row)
    Assert-ExactJsonObject $Row @('metric','scenario','direction',
        'exactObjectBlockedBeforeParticipantRecovery','identityMovedWithoutDeleteResync',
        'startupRecoveryCompleted','interruptedStatus','completedStatus','retainedAttempts',
        'additionalEffectsDuringRecovery','localIdentityPreserved','providerIdentityPreserved',
        'unexpectedRecoveryArtifacts','abruptRecoveryEquivalenceKey','equivalentOperationKind',
        'equivalentChildExitCode') 'dependent recovery metric'
    Assert-JsonString $Row.metric 'dependent.metric' @('dependent_cross_pair_startup_recovery')
    Assert-JsonString $Row.scenario 'dependent.scenario' @('provider-observation','local-observation')
    Assert-JsonString $Row.direction 'dependent.direction' @('local-to-dropbox','dropbox-to-local')
    if (($Row.direction -eq 'local-to-dropbox' -and $Row.scenario -cne 'provider-observation') -or
        ($Row.direction -eq 'dropbox-to-local' -and $Row.scenario -cne 'local-observation')) {
        throw 'Dependent recovery direction/scenario mismatch.'
    }
    foreach ($name in @('exactObjectBlockedBeforeParticipantRecovery','identityMovedWithoutDeleteResync',
            'startupRecoveryCompleted','localIdentityPreserved','providerIdentityPreserved')) {
        Assert-JsonBoolean $Row.$name "dependent.$name"
        if (-not $Row.$name) { throw "Dependent recovery $name is false." }
    }
    Assert-JsonString $Row.interruptedStatus 'dependent.interruptedStatus' @('EffectIssuing')
    Assert-JsonString $Row.completedStatus 'dependent.completedStatus' @('Completed')
    Assert-JsonInteger $Row.retainedAttempts 'dependent.retainedAttempts' 1 1
    Assert-JsonInteger $Row.additionalEffectsDuringRecovery 'dependent.additionalEffectsDuringRecovery' 0 0
    Assert-JsonInteger $Row.unexpectedRecoveryArtifacts 'dependent.unexpectedRecoveryArtifacts' 0 0
    Assert-Sha256String $Row.abruptRecoveryEquivalenceKey 'dependent.abruptRecoveryEquivalenceKey'
    Assert-JsonString $Row.equivalentOperationKind 'dependent.equivalentOperationKind' @('CrossPairMove')
    Assert-JsonInteger $Row.equivalentChildExitCode 'dependent.equivalentChildExitCode' 215 215
}

function Assert-ConcurrentIndependentCrossPairMetric {
    param([object]$Row)
    Assert-ExactJsonObject $Row @('metric','configuredWorkers','observedWorkers','effectAdmissions',
        'operationStatusInventory','crossPairStatusCounts','providerMoveEffects','fallbackUploads',
        'fallbackDownloads','archiveOrDeleteEffects') 'concurrent independent cross-pair metric'
    Assert-JsonString $Row.metric 'concurrent.metric' @('concurrent_independent_cross_pair_moves')
    Assert-JsonInteger $Row.configuredWorkers 'concurrent.configuredWorkers' 2 2
    Assert-JsonInteger $Row.observedWorkers 'concurrent.observedWorkers' 2 2
    Assert-JsonInteger $Row.effectAdmissions 'concurrent.effectAdmissions' 2 2
    foreach ($name in @('providerMoveEffects')) {
        Assert-JsonInteger $Row.$name "concurrent.$name" 2 2
    }
    foreach ($name in @('fallbackUploads','fallbackDownloads','archiveOrDeleteEffects')) {
        Assert-JsonInteger $Row.$name "concurrent.$name" 0 0
    }

    $expectedStatuses = @('Planned','Ready','Leased','Validating','AllocationAuthorized','Staged',
        'EffectIssuing','EffectUnknown','EffectObserved','Reconciling','Verifying','Committed',
        'CleanupPending','Completed','Blocked','Cancelled','Superseded')
    Assert-ExactJsonObject $Row.crossPairStatusCounts $expectedStatuses 'concurrent status counts'
    Assert-JsonArray $Row.operationStatusInventory 'concurrent.operationStatusInventory'
    if (@($Row.operationStatusInventory).Count -ne 2) {
        throw 'Concurrent operation inventory must contain exactly two operations.'
    }
    $operationIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($operation in @($Row.operationStatusInventory)) {
        Assert-ExactJsonObject $operation @('OperationId','kind','status','attempts') `
            'concurrent operation inventory item'
        Assert-JsonString $operation.OperationId 'concurrent operation.OperationId'
        Assert-JsonString $operation.kind 'concurrent operation.kind' @('CrossPairMove')
        Assert-JsonString $operation.status 'concurrent operation.status' $expectedStatuses
        Assert-JsonInteger $operation.attempts 'concurrent operation.attempts' 1 1
        if (-not $operationIds.Add([string]$operation.OperationId)) {
            throw 'Concurrent operation inventory repeats an operation identity.'
        }
    }
    if ($operationIds.Count -ne 2) { throw 'Concurrent move evidence requires two distinct operations.' }
    foreach ($status in $expectedStatuses) {
        $expected = if ($status -eq 'Completed') { 2L } else { 0L }
        Assert-JsonInteger $Row.crossPairStatusCounts.$status "concurrent status $status" $expected $expected
        $observed = @($Row.operationStatusInventory | Where-Object { $_.status -ceq $status }).Count
        if ($Row.crossPairStatusCounts.$status -ne $observed) {
            throw "Concurrent status count for $status disagrees with the exact operation inventory."
        }
    }
}

function New-ValidOrdinaryInvalidationMetricForSelfTest {
    $fingerprint = 'a' * 64
    return ([ordered]@{
        metric = 'ordinary_ready_operation_invalidation'
        scenario = 'create-local'
        operationKind = 'CreateLocal'
        staleOperation = [ordered]@{
            OperationId = 'operation-stale-self-test'; status = 'Superseded'; attempts = 0
            members = @([ordered]@{ MemberId='member-stale'; PairId='pair-self-test';
                ObjectId='object-self-test'; Role='source'; RootBindingId='root-self-test';
                ExpectedPathKey='new.txt' })
            planningAuthority = @([ordered]@{ ObservationSetId='observation-self-test';
                PairId='pair-self-test'; ConnectionId='connection-self-test';
                LocalScanGenerationId='local-scan-self-test';
                DropboxScanGenerationId='dropbox-scan-self-test';
                ObservationContentFingerprint=$fingerprint;
                SynchronizationPointSetFingerprint=$fingerprint; RootAuthorityFingerprint=$fingerprint })
        }
        successorOperation = [ordered]@{
            OperationId = 'operation-successor-self-test'; status = 'Completed'; attempts = 1
            members = @([ordered]@{ MemberId='member-successor'; PairId='pair-self-test';
                ObjectId='object-self-test'; Role='destination'; RootBindingId='root-self-test';
                ExpectedPathKey='new.txt' })
        }
        staleEffects = 0; authorityCurrentSuccessorOnly = $true; finalContentConverged = $true
        totalEffectDelta = [ordered]@{ uploads=0; downloads=1; archives=0; deletes=0; moves=0;
            directoryCreates=0 }
    } | ConvertTo-Json -Depth 12 -Compress | ConvertFrom-Json)
}

function Test-CriticalMetricSchemaMutations {
    $witnesses = [ordered]@{}
    $valid = New-ValidOrdinaryInvalidationMetricForSelfTest
    try { Assert-OrdinaryReadyInvalidationMetric $valid; $validAccepted = $true }
    catch { $validAccepted = $false }
    $witnesses.valid_fixture_accepted = $validAccepted

    foreach ($property in @($valid.PSObject.Properties.Name)) {
        $candidate = New-ValidOrdinaryInvalidationMetricForSelfTest
        $candidate.PSObject.Properties.Remove($property)
        $witnesses["missing_top_level_$property"] = Test-MetricProjectionRejects {
            Assert-OrdinaryReadyInvalidationMetric $candidate
        }
    }
    foreach ($property in @($valid.staleOperation.PSObject.Properties.Name)) {
        $candidate = New-ValidOrdinaryInvalidationMetricForSelfTest
        $candidate.staleOperation.PSObject.Properties.Remove($property)
        $witnesses["missing_stale_$property"] = Test-MetricProjectionRejects {
            Assert-OrdinaryReadyInvalidationMetric $candidate
        }
    }
    foreach ($property in @($valid.staleOperation.planningAuthority[0].PSObject.Properties.Name)) {
        $candidate = New-ValidOrdinaryInvalidationMetricForSelfTest
        $candidate.staleOperation.planningAuthority[0].PSObject.Properties.Remove($property)
        $witnesses["missing_planning_$property"] = Test-MetricProjectionRejects {
            Assert-OrdinaryReadyInvalidationMetric $candidate
        }
    }
    foreach ($property in @($valid.staleOperation.members[0].PSObject.Properties.Name)) {
        $candidate = New-ValidOrdinaryInvalidationMetricForSelfTest
        $candidate.staleOperation.members[0].PSObject.Properties.Remove($property)
        $witnesses["missing_member_$property"] = Test-MetricProjectionRejects {
            Assert-OrdinaryReadyInvalidationMetric $candidate
        }
    }
    foreach ($property in @($valid.totalEffectDelta.PSObject.Properties.Name)) {
        $candidate = New-ValidOrdinaryInvalidationMetricForSelfTest
        $candidate.totalEffectDelta.PSObject.Properties.Remove($property)
        $witnesses["missing_effect_$property"] = Test-MetricProjectionRejects {
            Assert-OrdinaryReadyInvalidationMetric $candidate
        }
    }

    $mutations = [ordered]@{
        boolean_string = { param($value) $value.finalContentConverged = 'true' }
        integer_double = { param($value) $value.staleEffects = [double]0 }
        invalid_enum = { param($value) $value.operationKind = 'ExecuteAnything' }
        invalid_fingerprint = { param($value)
            $value.staleOperation.planningAuthority[0].ObservationContentFingerprint = 'abc' }
        extra_property = { param($value) $value | Add-Member -NotePropertyName unexpected -NotePropertyValue $true }
        inconsistent_summary = { param($value) $value.operationKind = 'CreateRemote' }
        nested_array_as_string = { param($value) $value.staleOperation.members = 'member-stale' }
    }
    foreach ($name in $mutations.Keys) {
        $candidate = New-ValidOrdinaryInvalidationMetricForSelfTest
        & $mutations[$name] $candidate
        $witnesses[$name] = Test-MetricProjectionRejects {
            Assert-OrdinaryReadyInvalidationMetric $candidate
        }
    }
    $passed = $validAccepted -and
        @($witnesses.GetEnumerator() | Where-Object { $_.Key -ne 'valid_fixture_accepted' -and
            -not $_.Value }).Count -eq 0
    return [ordered]@{
        passed = $passed
        validation_failure_exit_code = 1
        source_package_ready_on_any_validation_failure = $false
        witnesses = $witnesses
    }
}

function Get-JsonObjectPropertyPaths {
    param([object]$Value, [object[]]$Segments = @())
    if ($null -eq $Value) { return }
    if ($Value -is [pscustomobject]) {
        foreach ($property in $Value.PSObject.Properties) {
            $next = @($Segments) + @([string]$property.Name)
            [pscustomobject]@{
                segments = [object[]]$next
                display = '$' + (($next | ForEach-Object {
                    if ($_ -is [int]) { "[$_]" } else { ".$($_)" }
                }) -join '')
            }
            Get-JsonObjectPropertyPaths -Value $property.Value -Segments $next
        }
        return
    }
    if ($Value.GetType() -eq [object[]]) {
        for ($index = 0; $index -lt $Value.Count; $index++) {
            Get-JsonObjectPropertyPaths -Value $Value[$index] `
                -Segments (@($Segments) + @([int]$index))
        }
    }
}

function Get-JsonObjectPaths {
    param([object]$Value, [object[]]$Segments = @())
    if ($null -eq $Value) { return }
    if ($Value -is [pscustomobject]) {
        [pscustomobject]@{
            segments = [object[]]@($Segments)
            display = if ($Segments.Count -eq 0) { '$' } else {
                '$' + (($Segments | ForEach-Object {
                    if ($_ -is [int]) { "[$_]" } else { ".$($_)" }
                }) -join '')
            }
        }
        foreach ($property in $Value.PSObject.Properties) {
            Get-JsonObjectPaths -Value $property.Value `
                -Segments (@($Segments) + @([string]$property.Name))
        }
        return
    }
    if ($Value.GetType() -eq [object[]]) {
        for ($index = 0; $index -lt $Value.Count; $index++) {
            Get-JsonObjectPaths -Value $Value[$index] `
                -Segments (@($Segments) + @([int]$index))
        }
    }
}

function Get-JsonMutationTarget {
    param([object]$Value, [object[]]$Segments)
    if ($Segments.Count -eq 0 -or $Segments[-1] -isnot [string]) {
        throw 'A JSON mutation target must terminate at an object property.'
    }
    $parent = $Value
    for ($index = 0; $index -lt $Segments.Count - 1; $index++) {
        $segment = $Segments[$index]
        $parent = if ($segment -is [int]) { @($parent)[$segment] }
            else { $parent.PSObject.Properties[[string]$segment].Value }
    }
    return [pscustomobject]@{
        parent = $parent
        property = $parent.PSObject.Properties[[string]$Segments[-1]]
    }
}

function Test-StrictMetricValidatorCoverage {
    param([object]$Row, [scriptblock]$Validator, [string]$Context)
    try { & $Validator $Row | Out-Null; $validAccepted = $true }
    catch { $validAccepted = $false }
    $failures = [Collections.Generic.List[string]]::new()
    $mutations = 0
    foreach ($path in @(Get-JsonObjectPropertyPaths -Value $Row)) {
        $missing = $Row | ConvertTo-Json -Depth 100 -Compress | ConvertFrom-Json
        $missingTarget = Get-JsonMutationTarget -Value $missing -Segments $path.segments
        $missingTarget.parent.PSObject.Properties.Remove($missingTarget.property.Name)
        $mutations++
        if (-not (Test-MetricProjectionRejects { & $Validator $missing })) {
            $failures.Add("missing:$($path.display)")
        }

        $wrongType = $Row | ConvertTo-Json -Depth 100 -Compress | ConvertFrom-Json
        $wrongTypeTarget = Get-JsonMutationTarget -Value $wrongType -Segments $path.segments
        $wrongTypeTarget.property.Value = if ($wrongTypeTarget.property.Value -is [string]) {
            $false
        } else {
            'schema-wrong-type'
        }
        $mutations++
        if (-not (Test-MetricProjectionRejects { & $Validator $wrongType })) {
            $failures.Add("wrong-type:$($path.display)")
        }
    }
    foreach ($objectPath in @(Get-JsonObjectPaths -Value $Row)) {
        $extra = $Row | ConvertTo-Json -Depth 100 -Compress | ConvertFrom-Json
        $extraTarget = $extra
        foreach ($segment in @($objectPath.segments)) {
            $extraTarget = if ($segment -is [int]) { @($extraTarget)[$segment] }
                else { $extraTarget.PSObject.Properties[[string]$segment].Value }
        }
        $extraTarget | Add-Member -NotePropertyName unexpected_schema_property -NotePropertyValue $true
        $mutations++
        if (-not (Test-MetricProjectionRejects { & $Validator $extra })) {
            $failures.Add("extra:$($objectPath.display).unexpected_schema_property")
        }
    }
    return [pscustomobject][ordered]@{
        context = $Context
        valid_fixture_accepted = $validAccepted
        mutation_count = $mutations
        rejected_mutation_count = $mutations - $failures.Count
        failures = @($failures)
        passed = $validAccepted -and $mutations -gt 0 -and $failures.Count -eq 0
    }
}

$RepositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$script:RepositoryRoot = $RepositoryRoot
if ($ExpectedRevision -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'ExpectedRevision must be one exact 40-hex commit.'
}
if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw 'ExpectedVersion must be a three-part semantic version.'
}
$DotNetFull = [IO.Path]::GetFullPath($DotNetPath)
if (-not (Test-Path -LiteralPath $DotNetFull -PathType Leaf)) {
    throw 'The exact registered dotnet runtime is unavailable.'
}
$script:DotNetFull = $DotNetFull
$ContractZipFull = [IO.Path]::GetFullPath($ContractZip)
if (-not (Test-Path -LiteralPath $ContractZipFull -PathType Leaf)) {
    throw 'The exact reviewed true-sync contract package is unavailable.'
}

$InstalledStateRootFull = [IO.Path]::GetFullPath($InstalledStateRoot)
$InstalledVersion2DatabaseFull = [IO.Path]::GetFullPath($InstalledVersion2Database)
$expectedProductRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Massdex\SyncShark'))
$installedReplacementStagingRoot = Join-Path $expectedProductRoot 'replacement-staging'
$installedActivePointer = Join-Path $expectedProductRoot 'active'
$testProductRootBase = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'sync-shark-pilot-tests'))
if (-not $InstalledStateRootFull.Equals((Join-Path $expectedProductRoot 'state'),
        [StringComparison]::OrdinalIgnoreCase) -or
    -not $InstalledVersion2DatabaseFull.Equals((Join-Path $expectedProductRoot 'vNext\state\syncshark.db.v2-rollback'),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installed migration inputs must be the exact registered Sync Shark v0.2.4 rollback locations.'
}
if (-not (Test-Path -LiteralPath $InstalledStateRootFull -PathType Container) -or
    -not (Test-Path -LiteralPath $InstalledVersion2DatabaseFull -PathType Leaf)) {
    throw 'The exact installed v0.2.4 rollback state is unavailable.'
}

$BuildRootFull = Resolve-EmptyOutputRoot -Path $BuildRoot -Role 'BuildRoot' -RepositoryRoot $RepositoryRoot
$EvidenceRootFull = Resolve-EmptyOutputRoot -Path $EvidenceRoot -Role 'EvidenceRoot' -RepositoryRoot $RepositoryRoot
$script:EvidenceRootFull = $EvidenceRootFull
$criticalMetricSchemaSelfTest = Test-CriticalMetricSchemaMutations
$criticalMetricSchemaSelfTest | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $EvidenceRootFull 'critical-metric-schema-self-test.json') `
        -Encoding utf8NoBOM
if (-not $criticalMetricSchemaSelfTest.passed) {
    throw 'Critical metric schema mutation tests did not all fail closed.'
}
$RealDpiCaptureRootFull = if ([string]::IsNullOrWhiteSpace($RealDpiCaptureRoot)) { $null }
    else { [IO.Path]::GetFullPath($RealDpiCaptureRoot) }
if ($null -ne $RealDpiCaptureRootFull -and
    -not (Test-Path -LiteralPath $RealDpiCaptureRootFull -PathType Container)) {
    throw 'RealDpiCaptureRoot must be an existing directory containing immutable real-DPI receipts.'
}
if ($BuildRootFull.Equals($EvidenceRootFull, [StringComparison]::OrdinalIgnoreCase) -or
    $BuildRootFull.StartsWith($EvidenceRootFull + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -or
    $EvidenceRootFull.StartsWith($BuildRootFull + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'BuildRoot and EvidenceRoot must be separate non-overlapping directories.'
}
if ($null -ne $RealDpiCaptureRootFull -and (
    $RealDpiCaptureRootFull.Equals($BuildRootFull, [StringComparison]::OrdinalIgnoreCase) -or
    $RealDpiCaptureRootFull.Equals($EvidenceRootFull, [StringComparison]::OrdinalIgnoreCase) -or
    $RealDpiCaptureRootFull.StartsWith($BuildRootFull + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -or
    $RealDpiCaptureRootFull.StartsWith($EvidenceRootFull + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -or
    $BuildRootFull.StartsWith($RealDpiCaptureRootFull + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -or
    $EvidenceRootFull.StartsWith($RealDpiCaptureRootFull + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase))) {
    throw 'RealDpiCaptureRoot must not overlap BuildRoot or EvidenceRoot.'
}

$head = Invoke-Git @('rev-parse', 'HEAD')
if (-not $head.Equals($ExpectedRevision, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Frozen revision mismatch: expected $ExpectedRevision but HEAD is $head."
}
$status = Invoke-Git @('status', '--porcelain=v1', '--untracked-files=all')
if ($status.Length -ne 0) { throw 'Full-client acceptance requires an exactly clean checkout.' }

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
$sourceManifest | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $EvidenceRootFull 'source-manifest.json') -Encoding utf8NoBOM

$installedStateBefore = Get-FileManifest $InstalledStateRootFull
$installedDatabaseBefore = [ordered]@{
    bytes = (Get-Item -LiteralPath $InstalledVersion2DatabaseFull).Length
    last_write_utc = (Get-Item -LiteralPath $InstalledVersion2DatabaseFull).LastWriteTimeUtc.ToString('O')
    sha256 = (Get-FileHash -LiteralPath $InstalledVersion2DatabaseFull -Algorithm SHA256).Hash.ToLowerInvariant()
}
$installedStagingBefore = Get-EntryMetadataManifest $installedReplacementStagingRoot
$installedActiveBefore = Get-EntryMetadataManifest $expectedProductRoot 'active'
$testProductRootsBefore = Get-EntryMetadataManifest $testProductRootBase 'product-*'
[ordered]@{
    state_root = $InstalledStateRootFull
    state_manifest = $installedStateBefore
    version_2_database = $installedDatabaseBefore
    replacement_staging_root = $installedReplacementStagingRoot
    replacement_staging_manifest = $installedStagingBefore
    active_pointer = $installedActiveBefore
    temporary_test_product_roots = $testProductRootsBefore
} | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $EvidenceRootFull 'installed-input-before.json') -Encoding utf8NoBOM

[ordered]@{
    repository = $RepositoryRoot
    commit = $head
    tree = Invoke-Git @('rev-parse', 'HEAD^{tree}')
    parent = Invoke-Git @('rev-parse', 'HEAD^')
    branch = Invoke-Git @('branch', '--show-current')
    configuration = $Configuration
    expected_version = $ExpectedVersion
    dotnet_path = $DotNetFull
    dotnet_sha256 = (Get-FileHash -LiteralPath $DotNetFull -Algorithm SHA256).Hash.ToLowerInvariant()
    true_sync_contract = $ContractZipFull
    true_sync_contract_sha256 = (Get-FileHash -LiteralPath $ContractZipFull -Algorithm SHA256).Hash.ToLowerInvariant()
    installed_state_root = $InstalledStateRootFull
    installed_version_2_database = $InstalledVersion2DatabaseFull
    installed_replacement_staging_root = $installedReplacementStagingRoot
    installed_active_pointer = $installedActivePointer
    temporary_test_product_root_base = $testProductRootBase
    started_utc = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $EvidenceRootFull 'frozen-object.json') -Encoding utf8NoBOM

$restoreLog = Join-Path $EvidenceRootFull 'restore.log'
$restoreOutput = & $DotNetFull restore (Join-Path $RepositoryRoot 'SyncShark.sln') --locked-mode `
    "-p:SyncSharkBuildRoot=$BuildRootFull" "-p:ContinuousIntegrationBuild=true" 2>&1
$restoreExit = $LASTEXITCODE
@($restoreOutput | ForEach-Object { $_.ToString() }) |
    Set-Content -LiteralPath $restoreLog -Encoding utf8NoBOM
if ($restoreExit -ne 0) { throw "Locked restore failed; see $restoreLog" }

$buildLog = Join-Path $EvidenceRootFull 'build.log'
$buildOutput = & $DotNetFull build (Join-Path $RepositoryRoot 'SyncShark.sln') --no-restore `
    -c $Configuration "-p:SyncSharkBuildRoot=$BuildRootFull" `
    "-p:SyncSharkSourceRevision=$ExpectedRevision" "-p:ContinuousIntegrationBuild=true" 2>&1
$buildExit = $LASTEXITCODE
@($buildOutput | ForEach-Object { $_.ToString() }) |
    Set-Content -LiteralPath $buildLog -Encoding utf8NoBOM
if ($buildExit -ne 0) { throw "Frozen build failed; see $buildLog" }

$testDll = Join-Path $BuildRootFull "bin\SyncShark.Core.Tests\$Configuration\net8.0\SyncShark.Core.Tests.dll"
$uiDll = Join-Path $BuildRootFull "bin\SyncShark.UiFixture\$Configuration\net8.0-windows\SyncShark.UiFixture.dll"
$appDll = Join-Path $BuildRootFull "bin\SyncShark\$Configuration\net8.0-windows\SyncShark.dll"
foreach ($required in @($testDll, $uiDll, $appDll)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "A required frozen output is absent: $required"
    }
}
$appVersion = (Get-Item -LiteralPath $appDll).VersionInfo
$expectedFileVersion = "$ExpectedVersion.0"
$versionBound = $appVersion.FileVersion -eq $expectedFileVersion -and
    $appVersion.ProductVersion -eq "$ExpectedVersion+$ExpectedRevision"

$PhaseResults = [Collections.Generic.List[object]]::new()
$script:PhaseResults = $PhaseResults
$phaseSpecs = @(
    [pscustomobject]@{ Name='complete-test-registry'; AcceptanceIds=@('TREE-01','PERF-01','BOOT-01','SELECT-01','DELETE-01','RECOVERY-01','EDGE-01'); Arguments=@($testDll) }
    [pscustomobject]@{ Name='architecture-boundaries'; AcceptanceIds=@('DELETE-01','RECOVERY-01','EDGE-01'); Arguments=@($testDll, '--reviewer-2-rejection-preflight') }
    [pscustomobject]@{ Name='sqlite-authority'; AcceptanceIds=@('ACCT-02','BG-01','PERF-01','DELETE-01','RECOVERY-01','MOVE-01','EDGE-01','MIGRATE-01'); Arguments=@($testDll, '--cloud-pro-authority-preflight') }
    [pscustomobject]@{ Name='true-sync-functional'; AcceptanceIds=@('TREE-01','BOOT-01','DIFF-01','DELETE-01','RECOVERY-01'); Arguments=@($testDll, '--true-sync-functional-preflight') }
    [pscustomobject]@{ Name='true-sync-process-crash'; AcceptanceIds=@('BG-01','TREE-01','DELETE-01','RECOVERY-01','EDGE-01'); Arguments=@($testDll, '--true-sync-crash-preflight') }
    [pscustomobject]@{ Name='broad-failure-correction'; AcceptanceIds=@('DELETE-01','RECOVERY-01','EDGE-01'); Arguments=@($testDll, '--broad-failure-correction-preflight') }
    [pscustomobject]@{ Name='recovery-registry'; AcceptanceIds=@('RECOVERY-01','EDGE-01'); Arguments=@($testDll, '--recovery-registry-preflight') }
    [pscustomobject]@{ Name='recovery-browser'; AcceptanceIds=@('ACCT-01','RECOVERY-01','SCALE-01'); Arguments=@($testDll, '--recovery-browser-contract') }
    [pscustomobject]@{ Name='dropbox-folder-browser'; AcceptanceIds=@('ACCT-01','TREE-01','SCALE-01'); Arguments=@($testDll, '--dropbox-browser-contract') }
    [pscustomobject]@{ Name='dropbox-download-and-archive'; AcceptanceIds=@('TREE-01','DELETE-01','RECOVERY-01'); Arguments=@($testDll, '--dropbox-download-contract') }
    [pscustomobject]@{ Name='resumable-upload-sessions'; AcceptanceIds=@('TREE-01','PERF-01','EDGE-01'); Arguments=@($testDll, '--upload-session-contract') }
    [pscustomobject]@{ Name='account-connection'; AcceptanceIds=@('ACCT-01','ACCT-02'); Arguments=@($testDll, '--account-connection-contract') }
    [pscustomobject]@{ Name='automation-background'; AcceptanceIds=@('AUTO-01','BG-01','PERF-01','MIGRATE-01'); Arguments=@($testDll, '--automation-background-contract') }
    [pscustomobject]@{ Name='selective-bootstrap-scale'; AcceptanceIds=@('TREE-01','BOOT-01','SELECT-01','SCALE-01'); Arguments=@($testDll, '--selective-scale-contract') }
    [pscustomobject]@{ Name='windows-cloud-files'; AcceptanceIds=@('CLOUD-01','EDGE-01'); Arguments=@($testDll, '--cloud-files-contract') }
    [pscustomobject]@{ Name='performance-throughput'; AcceptanceIds=@('PERF-01','TREE-01'); Arguments=@($testDll, '--performance-contract') }
    [pscustomobject]@{ Name='release-scale'; AcceptanceIds=@('TREE-01','BOOT-01','SCALE-01'); Arguments=@($testDll, '--scale-performance-contract') }
    [pscustomobject]@{ Name='dropbox-rewind'; AcceptanceIds=@('REWIND-01','RECOVERY-01','EDGE-01'); Arguments=@($testDll, '--rewind-contract') }
    [pscustomobject]@{ Name='cross-pair-movement'; AcceptanceIds=@('MOVE-01','RECOVERY-01','EDGE-01'); Arguments=@($testDll, '--cross-pair-move-contract') }
    [pscustomobject]@{ Name='cross-pair-generation-correction'; AcceptanceIds=@('MOVE-01','PERF-01','DELETE-01','RECOVERY-01','EDGE-01'); Arguments=@($testDll, '--cross-pair-generation-reproduction') }
    [pscustomobject]@{ Name='cross-pair-directory-divergence-correction'; AcceptanceIds=@('MOVE-01','DELETE-01','RECOVERY-01','EDGE-01'); Arguments=@($testDll, '--cross-pair-directory-divergence-contract') }
    [pscustomobject]@{ Name='ordinary-ready-invalidation'; AcceptanceIds=@('MOVE-01','DELETE-01','RECOVERY-01','EDGE-01'); Arguments=@($testDll, '--ordinary-ready-invalidation-matrix-contract') }
    [pscustomobject]@{ Name='recursive-namespace-edge'; AcceptanceIds=@('EDGE-01','TREE-01','SCALE-01'); Arguments=@($testDll, '--edge-contract') }
    [pscustomobject]@{ Name='authority-version-migration'; AcceptanceIds=@('MIGRATE-01'); Arguments=@($testDll, '--authority-migration-contract') }
    [pscustomobject]@{ Name='exact-installed-state-migration'; AcceptanceIds=@('MIGRATE-01','ACCT-02'); Arguments=@($testDll, '--installed-state-migration-contract', $InstalledStateRootFull, $InstalledVersion2DatabaseFull) }
    [pscustomobject]@{ Name='discovery-correction-regressions'; AcceptanceIds=@('ACCT-01','BG-01','TREE-01','PERF-01','CLOUD-01','DELETE-01','RECOVERY-01','REWIND-01','MOVE-01','EDGE-01','SCALE-01'); Arguments=@($testDll, '--discovery-correction-preflight') }
)
foreach ($phase in $phaseSpecs) {
    Invoke-AcceptancePhase -Name $phase.Name -AcceptanceIds $phase.AcceptanceIds -Arguments $phase.Arguments
}

$generationPhase = @($PhaseResults | Where-Object { $_.name -eq 'cross-pair-generation-correction' })
if ($generationPhase.Count -ne 1) {
    throw 'Cross-pair generation correction requires one exact producing phase.'
}
$generationMetrics = @(Get-PhaseJsonMetricRows -PhaseName 'cross-pair-generation-correction' `
    -ExpectedLogSha256 $generationPhase[0].log_sha256)
$generationControls = @($generationMetrics | Where-Object {
    $_.metric -eq 'ss_917e_f01_control_disabled_comparison'
})
$generationCoordinated = @($generationMetrics | Where-Object {
    $_.metric -eq 'ss_917e_f01_coordinated_observation_set'
})
$expectedDirections = @('dropbox-to-local','local-to-dropbox')
$actualControlDirections = @($generationControls.direction | Sort-Object -Unique)
$actualCoordinatedDirections = @($generationCoordinated.direction | Sort-Object -Unique)
if ($generationControls.Count -ne 2 -or
    @(Compare-Object $expectedDirections $actualControlDirections).Count -ne 0 -or
    @($generationControls | Where-Object {
        ($_.direction -eq 'local-to-dropbox' -and ($_.fallbackUploads -le 0 -or $_.fallbackDownloads -ne 0)) -or
        ($_.direction -eq 'dropbox-to-local' -and ($_.fallbackDownloads -le 0 -or $_.fallbackUploads -ne 0))
    }).Count -ne 0) {
    throw 'The disabled-control comparison did not reproduce exactly one directional fallback per direction.'
}
if ($generationCoordinated.Count -ne 16 -or
    @(Compare-Object $expectedDirections $actualCoordinatedDirections).Count -ne 0 -or
    @($generationCoordinated | Where-Object {
        $_.fallbackUploads -ne 0 -or $_.fallbackDownloads -ne 0 -or
        $_.fallbackArchives -ne 0 -or $_.fallbackDeletes -ne 0 -or
        $_.providerMoves -ne 1 -or $_.crossPairOperations -ne 1 -or
        $_.crossPairStatusCounts.Completed -ne 1 -or
        $_.localIdentityPreserved -ne $true -or $_.providerIdentityPreserved -ne $true -or
        $_.sourcePresent.local -ne $false -or $_.sourcePresent.dropbox -ne $false -or
        $_.destinationPresent.local -ne $true -or $_.destinationPresent.dropbox -ne $true
    }).Count -ne 0) {
    throw 'The coordinated generation matrix did not preserve exact identity, status, and zero-fallback invariants.'
}
$unexpectedGenerationStatuses = @($generationCoordinated | Where-Object {
    $statusMetric = $_
    @($statusMetric.crossPairStatusCounts.PSObject.Properties | Where-Object {
        $_.Name -ne 'Completed' -and [int]$_.Value -ne 0
    }).Count -ne 0
})
if ($unexpectedGenerationStatuses.Count -ne 0) {
    throw 'A coordinated generation witness retained a non-completed cross-pair status.'
}
$manualMetrics = @($generationCoordinated | Where-Object { $_.injectedBoundary -eq 'manual-single-pair' })
$workerMetrics = @($generationCoordinated | Where-Object {
    $_.injectedBoundary -eq 'after-ordinary-planning-before-dispatch'
})
if ($manualMetrics.Count -ne 2 -or
    @($manualMetrics | Where-Object {
        $_.selectedPairCount -ne 1 -or $_.unrelatedPairOrdinaryEffect -ne $false
    }).Count -ne 0 -or
    $workerMetrics.Count -ne 2 -or
    @($workerMetrics | Where-Object { $_.observedWorkers -lt 2 }).Count -ne 0) {
    throw 'Manual single-pair participation or real two-worker execution evidence is incomplete.'
}
$monitorMetrics = @($generationMetrics | Where-Object { $_.metric -eq 'cross_pair_monitor_entry_point' })
$concurrentMetrics = @($generationMetrics | Where-Object {
    $_.metric -eq 'concurrent_independent_cross_pair_moves'
})
if ($concurrentMetrics.Count -ne 1) {
    throw 'Concurrent independent-move evidence requires one exact closed result.'
}
Assert-ConcurrentIndependentCrossPairMetric $concurrentMetrics[0]
if ($monitorMetrics.Count -ne 2 -or
    @(Compare-Object $expectedDirections @($monitorMetrics.direction | Sort-Object -Unique)).Count -ne 0 -or
    @($monitorMetrics | Where-Object {
        $_.callbackStarted -ne 1 -or $_.fallbackUploads -ne 0 -or $_.fallbackDownloads -ne 0 -or
        $_.completedMoves -ne 1 -or $_.allOtherCrossPairStatuses -ne 0
    }).Count -ne 0) {
    throw 'Monitor callback or concurrent independent-move evidence is incomplete.'
}
$processCrashMetrics = @($generationMetrics | Where-Object {
    $_.metric -eq 'cross_pair_process_crash_before_dispatch'
})
$restartMetrics = @($generationMetrics | Where-Object {
    $_.metric -eq 'coordinated_restart_before_dispatch'
})
$crossPairPauseMetrics = @($generationMetrics | Where-Object {
    $_.metric -eq 'cross_pair_pause_before_effect'
})
$ordinaryPauseMetrics = @($generationMetrics | Where-Object {
    $_.metric -eq 'coordinated_pause_before_effect'
})
if ($processCrashMetrics.Count -ne 2 -or
    @(Compare-Object $expectedDirections @($processCrashMetrics.direction | Sort-Object -Unique)).Count -ne 0 -or
    @($processCrashMetrics | Where-Object {
        $_.childExitCode -ne 214 -or $_.staleOperationSuperseded -ne 1 -or
        $_.latestRelocationCompleted -ne 1 -or $_.allOtherCrossPairStatuses -ne 0 -or
        $_.fallbackUploads -ne 0 -or $_.fallbackDownloads -ne 0 -or
        $_.archiveOrDeleteEffects -ne 0 -or $_.unrelatedPairPreserved -ne $true
    }).Count -ne 0 -or
    $restartMetrics.Count -ne 2 -or
    @(Compare-Object $expectedDirections @($restartMetrics.direction | Sort-Object -Unique)).Count -ne 0 -or
    @($restartMetrics | Where-Object {
        $_.crossPairStatusCounts.Superseded -ne 1 -or $_.crossPairStatusCounts.Completed -ne 1 -or
        $_.batchFailureMessages.Count -ne 0 -or $_.fallbackUploads -ne 0 -or
        $_.fallbackDownloads -ne 0 -or $_.unrelatedPairProgress -ne $true
    }).Count -ne 0 -or
    $crossPairPauseMetrics.Count -ne 2 -or
    @(Compare-Object $expectedDirections @($crossPairPauseMetrics.direction | Sort-Object -Unique)).Count -ne 0 -or
    @($crossPairPauseMetrics | Where-Object {
        $_.effectWhilePaused -ne $false -or $_.staleOperationSuperseded -ne 1 -or
        $_.successorCompleted -ne 1 -or $_.fallbackUploads -ne 0 -or $_.fallbackDownloads -ne 0
    }).Count -ne 0 -or
    $ordinaryPauseMetrics.Count -ne 1 -or $ordinaryPauseMetrics[0].effectWhilePaused -ne $false -or
    $ordinaryPauseMetrics[0].resumedEffectCount -ne 1 -or
    $ordinaryPauseMetrics[0].staleOperationSuperseded -ne $true -or
    $ordinaryPauseMetrics[0].uploadedVersion -ne 'version-3') {
    throw 'Process restart, host restart, or pause-boundary evidence is incomplete.'
}
$guardMetrics = @($generationMetrics | Where-Object {
    $_.metric -eq 'final_effect_admission_guard_comparison'
})
$dimensionMetrics = @($generationMetrics | Where-Object {
    $_.metric -eq 'planning_authority_dimension_invalidation'
})
$expectedDimensions = @('configuration-revision','connection-account','content-fingerprint',
    'root-authority','selection-definition','synchronization-point-set')
if ($guardMetrics.Count -ne 1 -or $guardMetrics[0].sameScenarioAndTiming -ne $true -or
    $guardMetrics[0].guardEnabled.status -ne 'superseded' -or
    $guardMetrics[0].guardEnabled.uploads -ne 0 -or
    $guardMetrics[0].guardEnabled.destinationPresent -ne $false -or
    $guardMetrics[0].guardDisabledControl.status -ne 'completed' -or
    $guardMetrics[0].guardDisabledControl.uploads -ne 1 -or
    $guardMetrics[0].guardDisabledControl.destinationPresent -ne $true -or
    $dimensionMetrics.Count -ne 6 -or
    @(Compare-Object $expectedDimensions @($dimensionMetrics.dimension | Sort-Object -Unique)).Count -ne 0 -or
    @($dimensionMetrics | Where-Object {
        $_.unaffectedParticipantRemainsCurrent -ne $true -or $_.changedParticipantInvalidated -ne $true
    }).Count -ne 0) {
    throw 'Final effect-admission guard or planning-authority dimension evidence is incomplete.'
}

$directoryRuntimeMetrics = @($generationMetrics | Where-Object {
    $_.metric -eq 'cross_pair_directory_descendant_full_runtime'
})
$directoryCrashMetrics = @($generationMetrics | Where-Object {
    $_.metric -eq 'cross_pair_directory_post_effect_process_recovery'
})
$directoryOrderingMetrics = @($generationMetrics | Where-Object {
    $_.metric -eq 'cross_pair_directory_adverse_ordering'
})
$directoryCollisionMetrics = @($generationMetrics | Where-Object {
    $_.metric -eq 'cross_pair_directory_destination_collision'
})
$readyAuthorityMetrics = @($generationMetrics | Where-Object {
    $_.metric -eq 'cross_pair_ready_authority_transition'
})
$expectedReadyAuthorityDimensions = @('configuration-revision','connection-account',
    'destination-content','root-authority','selection-definition','synchronization-point-set')
if ($directoryRuntimeMetrics.Count -ne 2 -or
    @(Compare-Object $expectedDirections @($directoryRuntimeMetrics.direction | Sort-Object -Unique)).Count -ne 0 -or
    @($directoryRuntimeMetrics | Where-Object {
        $_.deletionEnabled -ne $true -or $_.populatedDescendants -ne 3 -or
        $_.observedWorkers -lt 2 -or $_.adverseOrdering -ne 'root-first' -or
        $_.rootEffectOrder -ne 1 -or $_.unrelatedEffectOrder -ne 2 -or
        $_.ordinaryDescendantPlans -ne 0 -or $_.rootMoveEffects -ne 1 -or
        $_.fallbackUploads -ne 0 -or $_.fallbackDownloads -ne 0 -or
        $_.fallbackArchives -ne 0 -or $_.fallbackDeletes -ne 0 -or
        $_.fallbackDirectoryCreates -ne 0 -or
        $_.logicalIdentitiesPreserved -ne 4 -or $_.ntfsIdentitiesPreserved -ne 4 -or
        $_.providerIdentitiesPreserved -ne 4 -or $_.unrelatedPairProgressed -ne $true
    }).Count -ne 0) {
    throw 'Populated-directory full-runtime descendant exclusion evidence is incomplete.'
}
if ($directoryCrashMetrics.Count -ne 2 -or
    @(Compare-Object $expectedDirections @($directoryCrashMetrics.direction | Sort-Object -Unique)).Count -ne 0 -or
    @($directoryCrashMetrics | Where-Object {
        $_.childExitCode -ne 215 -or $_.interruptedStatus -ne 'EffectIssuing' -or
        $_.retainedAttempts -ne 1 -or $_.retainedSourceMembers -ne 8 -or
        $_.retainedRootPathClaims -ne 16 -or $_.deletionEnabled -ne $true -or
        $_.completedMoves -ne 1 -or $_.additionalRootMoveEffectsAfterRestart -ne 0 -or
        $_.fallbackUploads -ne 0 -or $_.fallbackDownloads -ne 0 -or
        $_.fallbackArchives -ne 0 -or $_.fallbackDeletes -ne 0 -or
        $_.fallbackDirectoryCreates -ne 0 -or $_.unexpectedRecoveryArtifacts -ne 0 -or
        $_.logicalIdentitiesPreserved -ne 4 -or $_.ntfsIdentitiesPreserved -ne 4 -or
        $_.providerIdentitiesPreserved -ne 4 -or $_.unrelatedPairProgressed -ne $true
    }).Count -ne 0) {
    throw 'Populated-directory process-recovery evidence is incomplete.'
}
if ($directoryOrderingMetrics.Count -ne 2 -or
    @(Compare-Object $expectedDirections @($directoryOrderingMetrics.direction | Sort-Object -Unique)).Count -ne 0 -or
    @($directoryOrderingMetrics | Where-Object {
        $_.adverseOrdering -ne 'stale-descendant-ready-before-root' -or
        ($_.direction -eq 'local-to-dropbox' -and $_.staleDescendantKind -ne 'UpdateRemote') -or
        ($_.direction -eq 'dropbox-to-local' -and $_.staleDescendantKind -ne 'UpdateLocal') -or
        $_.staleDescendantStatus -ne 'Superseded' -or $_.staleDescendantAttempts -ne 0 -or
        $_.rootMoveCompleted -ne 1 -or $_.fallbackUploads -ne 0 -or
        $_.fallbackDownloads -ne 0 -or $_.fallbackArchives -ne 0 -or
        $_.fallbackDeletes -ne 0 -or $_.fallbackDirectoryCreates -ne 0 -or
        $_.logicalIdentitiesPreserved -ne 4 -or $_.ntfsIdentitiesPreserved -ne 4 -or
        $_.providerIdentitiesPreserved -ne 4 -or $_.unrelatedPairProgressed -ne $true
    }).Count -ne 0) {
    throw 'Populated-directory adverse-ordering evidence is incomplete.'
}
if ($directoryCollisionMetrics.Count -ne 2 -or
    @(Compare-Object $expectedDirections @($directoryCollisionMetrics.direction | Sort-Object -Unique)).Count -ne 0 -or
    @($directoryCollisionMetrics | Where-Object {
        $_.collision -ne 'populated-destination-subtree' -or $_.operationStatus -ne 'Blocked' -or
        $_.retainedNoEffectAttempt -ne 1 -or $_.sourceContentPreserved -ne $true -or
        $_.destinationOccupantPreserved -ne $true -or
        $_.rootMoveEffectsAfterCollisionObservation -ne 0 -or
        $_.fallbackUploads -ne 0 -or $_.fallbackDownloads -ne 0 -or
        $_.fallbackArchives -ne 0 -or $_.fallbackDeletes -ne 0 -or
        $_.fallbackDirectoryCreates -ne 0 -or $_.unexpectedRecoveryArtifacts -ne 0 -or
        $_.unrelatedPairProgressed -ne $true
    }).Count -ne 0) {
    throw 'Populated-directory destination-collision evidence is incomplete.'
}
if ($readyAuthorityMetrics.Count -ne 6 -or
    @(Compare-Object $expectedReadyAuthorityDimensions @($readyAuthorityMetrics.dimension | Sort-Object -Unique)).Count -ne 0 -or
    @($readyAuthorityMetrics | Where-Object {
        $_.readyOperationPersisted -ne $true -or @($_.planningParticipants).Count -ne 3 -or
        @($_.planningParticipants.PairId | Sort-Object -Unique).Count -ne 3 -or
        @($_.planningParticipants | Where-Object {
            [string]::IsNullOrWhiteSpace($_.LocalScanGenerationId) -or
            [string]::IsNullOrWhiteSpace($_.DropboxScanGenerationId) -or
            [string]::IsNullOrWhiteSpace($_.ObservationContentFingerprint) -or
            [string]::IsNullOrWhiteSpace($_.SynchronizationPointSetFingerprint) -or
            [string]::IsNullOrWhiteSpace($_.RootAuthorityFingerprint)
        }).Count -ne 0 -or $_.invalidatedAuthorities -lt 1 -or
        $_.finalAdmission -ne 'Superseded' -or $_.attempts -ne 0 -or
        $_.providerOrFilesystemEffects -ne 0 -or $_.destinationVersionPreserved -ne $true -or
        $_.unaffectedPairProgressed -ne $true
    }).Count -ne 0) {
    throw 'Cross-pair Ready authority-transition evidence is incomplete.'
}
foreach ($row in $directoryRuntimeMetrics) { Assert-CrossPairDirectoryRuntimeMetric $row }
foreach ($row in $directoryCrashMetrics) { Assert-CrossPairDirectoryCrashMetric $row }
foreach ($row in $directoryOrderingMetrics) { Assert-CrossPairDirectoryOrderingMetric $row }
foreach ($row in $directoryCollisionMetrics) { Assert-CrossPairDirectoryCollisionMetric $row }
foreach ($row in $readyAuthorityMetrics) { Assert-CrossPairReadyAuthorityMetric $row }

$divergencePhase = @($PhaseResults | Where-Object {
    $_.name -eq 'cross-pair-directory-divergence-correction'
})
if ($divergencePhase.Count -ne 1 -or $divergencePhase[0].exit_code -ne 0) {
    throw 'Directory divergence correction requires one exact passing phase.'
}
$divergenceMetrics = @(Get-PhaseJsonMetricRows `
    -PhaseName 'cross-pair-directory-divergence-correction' `
    -ExpectedLogSha256 $divergencePhase[0].log_sha256 | Where-Object {
        $_.metric -eq 'cross_pair_directory_descendant_divergence'
    })
$expectedDivergenceRows = foreach ($direction in $expectedDirections) {
    foreach ($entry in @('matching-add|after-root-effect','edit|after-root-effect',
            'edit|before-planning','add|before-planning','delete|before-planning',
            'edit|ready-before-effect')) {
        "$direction|$entry"
    }
}
$actualDivergenceRows = @($divergenceMetrics | ForEach-Object {
    "$($_.direction)|$($_.mutation)|$($_.timing)"
} | Sort-Object -Unique)
if ($divergenceMetrics.Count -ne 12 -or
    @(Compare-Object ($expectedDivergenceRows | Sort-Object) $actualDivergenceRows).Count -ne 0) {
    throw 'Directory divergence evidence does not contain the exact twelve-row matrix.'
}
foreach ($row in $divergenceMetrics) { Assert-CrossPairDirectoryDivergenceMetric $row }

$prefixAuthorityMetrics = @(Get-PhaseJsonMetricRows `
    -PhaseName 'cross-pair-directory-divergence-correction' `
    -ExpectedLogSha256 $divergencePhase[0].log_sha256 | Where-Object {
        $_.metric -eq 'cross_pair_directory_subtree_prefix_authority'
    })
if ($prefixAuthorityMetrics.Count -ne 2 -or
    @(Compare-Object $expectedDirections `
        @($prefixAuthorityMetrics.direction | Sort-Object -Unique)).Count -ne 0) {
    throw 'Directory subtree-prefix authority evidence does not contain both exact directions.'
}
foreach ($row in $prefixAuthorityMetrics) {
    Assert-CrossPairDirectorySubtreePrefixAuthorityMetric $row
}

$ordinaryInvalidationPhase = @($PhaseResults | Where-Object {
    $_.name -eq 'ordinary-ready-invalidation'
})
if ($ordinaryInvalidationPhase.Count -ne 1 -or $ordinaryInvalidationPhase[0].exit_code -ne 0) {
    throw 'Ordinary Ready invalidation requires one exact passing phase.'
}
$ordinaryInvalidationMetrics = @(Get-PhaseJsonMetricRows -PhaseName 'ordinary-ready-invalidation' `
    -ExpectedLogSha256 $ordinaryInvalidationPhase[0].log_sha256 | Where-Object {
        $_.metric -eq 'ordinary_ready_operation_invalidation'
    })
$expectedOrdinaryInvalidationRows = @('create-local','create-remote','delete-local','delete-remote',
    'move-local','move-remote')
if ($ordinaryInvalidationMetrics.Count -ne 6 -or
    @(Compare-Object $expectedOrdinaryInvalidationRows `
        @($ordinaryInvalidationMetrics.scenario | Sort-Object -Unique)).Count -ne 0) {
    throw 'Ordinary Ready invalidation evidence does not contain the exact six-row matrix.'
}
foreach ($row in $ordinaryInvalidationMetrics) { Assert-OrdinaryReadyInvalidationMetric $row }

$contractEvidence = Join-Path $EvidenceRootFull 'true-sync-contract'
New-Item -ItemType Directory -Path $contractEvidence | Out-Null
$witnessReceiptPath = Join-Path $EvidenceRootFull 'production-witness-execution.json'
[ordered]@{
    revision = $head
    generated_utc = [DateTimeOffset]::UtcNow.ToString('O')
    phases = @($PhaseResults | ForEach-Object {
        [ordered]@{
            name = $_.name
            exit_code = $_.exit_code
            started_utc = $_.started_utc
            completed_utc = $_.completed_utc
            log = $_.log
            log_sha256 = $_.log_sha256
        }
    })
} | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $witnessReceiptPath -Encoding utf8NoBOM
Invoke-AcceptancePhase -Name 'true-sync-contract-inventory' `
    -AcceptanceIds @('TREE-01','BOOT-01','DELETE-01','RECOVERY-01','EDGE-01') `
    -Arguments @($testDll, '--true-sync-contract-preflight', $ContractZipFull, $contractEvidence,
        $witnessReceiptPath)
$contractCoverage = Get-Content -LiteralPath (Join-Path $contractEvidence 'contract-coverage.json') -Raw |
    ConvertFrom-Json
$structuralContractValid = $contractCoverage.missing.Count -eq 0 -and
    $contractCoverage.structural_dependency_mapping_complete -eq $true -and
    $contractCoverage.generated_case_tests_executed -eq 0 -and
    $contractCoverage.case_specific_execution_claim -eq $false

$renderRoot = Join-Path $EvidenceRootFull 'ui-renders'
New-Item -ItemType Directory -Path $renderRoot | Out-Null
Invoke-AcceptancePhase -Name 'ui-accessibility-and-layout' `
    -AcceptanceIds @('UX-01','ACCT-01','AUTO-01','BG-01','BOOT-01','DIFF-01','SELECT-01','CLOUD-01','RECOVERY-01','REWIND-01','SCALE-01') `
    -Arguments @($uiDll, $renderRoot)
$renderManifest = Get-FileManifest $renderRoot
$renderSetValid = $renderManifest.Count -ge 35 -and @($renderManifest | Where-Object { $_.bytes -le 0 }).Count -eq 0
$renderManifest | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $EvidenceRootFull 'ui-render-manifest.json') -Encoding utf8NoBOM

$buildManifest = Get-FileManifest $BuildRootFull
$buildManifest | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $EvidenceRootFull 'build-manifest.json') -Encoding utf8NoBOM

$performancePhases = @($PhaseResults | Where-Object { $_.name -eq 'performance-throughput' })
$scalePhases = @($PhaseResults | Where-Object { $_.name -eq 'release-scale' })
if ($performancePhases.Count -ne 1 -or $scalePhases.Count -ne 1) {
    throw 'Exact metric projection requires one performance and one release-scale phase.'
}
$performancePhase = $performancePhases[0]
$scalePhase = $scalePhases[0]
$performanceMetrics = @(Get-PhaseJsonMetricRows -PhaseName 'performance-throughput' `
    -ExpectedLogSha256 $performancePhase.log_sha256)
$pairConcurrencyMetrics = @($performanceMetrics | Where-Object { $_.metric -eq 'pair_concurrency' })
if ($pairConcurrencyMetrics.Count -ne 1 -or
    $pairConcurrencyMetrics[0].configuredMaximum -ne 2 -or
    $pairConcurrencyMetrics[0].observedMaximum -ne 2 -or
    $pairConcurrencyMetrics[0].discoveryMaximum -ne 2 -or
    $pairConcurrencyMetrics[0].coordinatedObservationMaximum -ne 2) {
    throw 'Pair concurrency must use exactly two workers during discovery and the coordinated observation cut.'
}
$dependentRecoveryMetrics = @($performanceMetrics | Where-Object {
    $_.metric -eq 'dependent_cross_pair_startup_recovery'
})
$selectionFailureMetrics = @($performanceMetrics | Where-Object {
    $_.metric -eq 'selective_exclusion_failure_isolation'
})
if ($dependentRecoveryMetrics.Count -ne 2 -or
    @(Compare-Object $expectedDirections @($dependentRecoveryMetrics.direction | Sort-Object -Unique)).Count -ne 0 -or
    @($dependentRecoveryMetrics | Where-Object {
        ($_.direction -eq 'local-to-dropbox' -and $_.scenario -ne 'provider-observation') -or
        ($_.direction -eq 'dropbox-to-local' -and $_.scenario -ne 'local-observation') -or
        $_.exactObjectBlockedBeforeParticipantRecovery -ne $true -or
        $_.identityMovedWithoutDeleteResync -ne $true -or $_.startupRecoveryCompleted -ne $true -or
        $_.interruptedStatus -ne 'EffectIssuing' -or $_.completedStatus -ne 'Completed' -or
        $_.retainedAttempts -ne 1 -or $_.additionalEffectsDuringRecovery -ne 0 -or
        $_.localIdentityPreserved -ne $true -or $_.providerIdentityPreserved -ne $true -or
        $_.unexpectedRecoveryArtifacts -ne 0 -or
        $_.equivalentOperationKind -ne 'CrossPairMove' -or $_.equivalentChildExitCode -ne 215
    }).Count -ne 0) {
    throw 'Dependent cross-pair startup-recovery evidence is incomplete.'
}
foreach ($row in $dependentRecoveryMetrics) {
    Assert-DependentRecoveryMetric $row
    $matchingProcessWitness = @($directoryCrashMetrics | Where-Object {
        $_.direction -ceq $row.direction -and
        $_.abruptRecoveryEquivalenceKey -ceq $row.abruptRecoveryEquivalenceKey -and
        $_.childExitCode -eq $row.equivalentChildExitCode -and
        $_.interruptedStatus -ceq $row.interruptedStatus -and
        $_.completedStatus -ceq $row.completedStatus -and
        $_.retainedAttempts -eq $row.retainedAttempts -and
        $_.additionalEffectsDuringRecovery -eq $row.additionalEffectsDuringRecovery -and
        $_.unexpectedRecoveryArtifacts -eq $row.unexpectedRecoveryArtifacts
    })
    if ($matchingProcessWitness.Count -ne 1) {
        throw 'Dependent recovery has no exact same-direction child-process equivalence witness.'
    }
}
$strictMetricMutationRows = [Collections.Generic.List[object]]::new()
$strictMetricMutationIndex = 0
foreach ($entry in @(
    [pscustomobject]@{ rows=$directoryRuntimeMetrics; name='directory-runtime';
        validator={ param($value) Assert-CrossPairDirectoryRuntimeMetric $value } },
    [pscustomobject]@{ rows=$directoryCrashMetrics; name='directory-process-recovery';
        validator={ param($value) Assert-CrossPairDirectoryCrashMetric $value } },
    [pscustomobject]@{ rows=$directoryOrderingMetrics; name='directory-adverse-ordering';
        validator={ param($value) Assert-CrossPairDirectoryOrderingMetric $value } },
    [pscustomobject]@{ rows=$directoryCollisionMetrics; name='directory-collision';
        validator={ param($value) Assert-CrossPairDirectoryCollisionMetric $value } },
    [pscustomobject]@{ rows=$readyAuthorityMetrics; name='ready-authority';
        validator={ param($value) Assert-CrossPairReadyAuthorityMetric $value } },
    [pscustomobject]@{ rows=$divergenceMetrics; name='directory-divergence';
        validator={ param($value) Assert-CrossPairDirectoryDivergenceMetric $value } },
    [pscustomobject]@{ rows=$prefixAuthorityMetrics; name='directory-subtree-prefix-authority';
        validator={ param($value) Assert-CrossPairDirectorySubtreePrefixAuthorityMetric $value } },
    [pscustomobject]@{ rows=$ordinaryInvalidationMetrics; name='ordinary-ready-invalidation';
        validator={ param($value) Assert-OrdinaryReadyInvalidationMetric $value } },
    [pscustomobject]@{ rows=$dependentRecoveryMetrics; name='dependent-process-recovery';
        validator={ param($value) Assert-DependentRecoveryMetric $value } },
    [pscustomobject]@{ rows=$concurrentMetrics; name='concurrent-independent-moves';
        validator={ param($value) Assert-ConcurrentIndependentCrossPairMetric $value } }
)) {
    foreach ($row in @($entry.rows)) {
        $strictMetricMutationIndex++
        $strictMetricMutationRows.Add((Test-StrictMetricValidatorCoverage -Row $row `
            -Validator $entry.validator -Context "$($entry.name)-$strictMetricMutationIndex"))
    }
}
$strictMetricRuntimeCoverage = [ordered]@{
    passed = @($strictMetricMutationRows | Where-Object { -not $_.passed }).Count -eq 0
    metric_row_count = $strictMetricMutationRows.Count
    mutation_count = ($strictMetricMutationRows | Measure-Object -Property mutation_count -Sum).Sum
    rejected_mutation_count = ($strictMetricMutationRows |
        Measure-Object -Property rejected_mutation_count -Sum).Sum
    validation_failure_exit_code = 1
    source_package_ready_on_any_validation_failure = $false
    rows = @($strictMetricMutationRows)
}
$strictMetricRuntimeCoverage | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath (Join-Path $EvidenceRootFull 'critical-metric-runtime-mutation-coverage.json') `
        -Encoding utf8NoBOM
if (-not $strictMetricRuntimeCoverage.passed -or $strictMetricRuntimeCoverage.metric_row_count -ne 37 -or
    $strictMetricRuntimeCoverage.mutation_count -ne $strictMetricRuntimeCoverage.rejected_mutation_count) {
    throw 'Runtime critical-metric mutation coverage did not fail closed for every required field.'
}
if ($selectionFailureMetrics.Count -ne 1 -or
    $selectionFailureMetrics[0].exclusionRejectedBeforeAuthorityLoss -ne $true -or
    $selectionFailureMetrics[0].retainedSelectionIncludesUncertainSubtree -ne $true -or
    $selectionFailureMetrics[0].failedPeerIdentityFenced -ne $true -or
    $selectionFailureMetrics[0].uncertainPathBlocked -ne $true -or
    $selectionFailureMetrics[0].destructiveEffects -ne 0 -or
    $selectionFailureMetrics[0].independentHealthyChangesCompleted -ne 2) {
    throw 'Selective-exclusion peer-failure isolation evidence is incomplete.'
}
$measurements = @(
    Get-ExactPhaseMetric -PhaseName 'performance-throughput' -MetricName 'pair_concurrency' `
        -ExpectedLogSha256 $performancePhase.log_sha256
    Get-ExactPhaseMetric -PhaseName 'performance-throughput' -MetricName 'local_detection_latency' `
        -ExpectedLogSha256 $performancePhase.log_sha256
    Get-ExactPhaseMetric -PhaseName 'performance-throughput' -MetricName 'provider_call_accounting' `
        -ExpectedLogSha256 $performancePhase.log_sha256
    Get-ExactPhaseMetric -PhaseName 'release-scale' -MetricName 'release_scale_targets' `
        -ExpectedLogSha256 $scalePhase.log_sha256
)
$metricProjectionNegativeWitnesses = [ordered]@{
    absent_metric_rejected = Test-MetricProjectionRejects {
        Get-ExactPhaseMetric -PhaseName 'performance-throughput' -MetricName 'absent_metric' `
            -ExpectedLogSha256 $performancePhase.log_sha256
    }
    cross_phase_metric_rejected = Test-MetricProjectionRejects {
        Get-ExactPhaseMetric -PhaseName 'performance-throughput' -MetricName 'release_scale_targets' `
            -ExpectedLogSha256 $performancePhase.log_sha256
    }
    stale_log_identity_rejected = Test-MetricProjectionRejects {
        Get-ExactPhaseMetric -PhaseName 'release-scale' -MetricName 'release_scale_targets' `
            -ExpectedLogSha256 ('0' * 64)
    }
}
if (@($metricProjectionNegativeWitnesses.Values | Where-Object { -not $_ }).Count -ne 0) {
    throw 'Metric projection negative witnesses did not all fail closed.'
}
[ordered]@{
    revision = $head
    measurements = $measurements
    negative_witnesses = $metricProjectionNegativeWitnesses
} | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath (Join-Path $EvidenceRootFull 'exact-measurements.json') -Encoding utf8NoBOM

$installedStateAfter = Get-FileManifest $InstalledStateRootFull
$installedDatabaseAfter = [ordered]@{
    bytes = (Get-Item -LiteralPath $InstalledVersion2DatabaseFull).Length
    last_write_utc = (Get-Item -LiteralPath $InstalledVersion2DatabaseFull).LastWriteTimeUtc.ToString('O')
    sha256 = (Get-FileHash -LiteralPath $InstalledVersion2DatabaseFull -Algorithm SHA256).Hash.ToLowerInvariant()
}
$installedStagingAfter = Get-EntryMetadataManifest $installedReplacementStagingRoot
$installedActiveAfter = Get-EntryMetadataManifest $expectedProductRoot 'active'
$testProductRootsAfter = Get-EntryMetadataManifest $testProductRootBase 'product-*'
$installedInputsUnchanged = ($installedStateBefore | ConvertTo-Json -Depth 8 -Compress) -eq
    ($installedStateAfter | ConvertTo-Json -Depth 8 -Compress) -and
    ($installedDatabaseBefore | ConvertTo-Json -Depth 4 -Compress) -eq
    ($installedDatabaseAfter | ConvertTo-Json -Depth 4 -Compress) -and
    ($installedStagingBefore | ConvertTo-Json -Depth 5 -Compress) -eq
    ($installedStagingAfter | ConvertTo-Json -Depth 5 -Compress) -and
    ($installedActiveBefore | ConvertTo-Json -Depth 5 -Compress) -eq
    ($installedActiveAfter | ConvertTo-Json -Depth 5 -Compress) -and
    ($testProductRootsBefore | ConvertTo-Json -Depth 5 -Compress) -eq
    ($testProductRootsAfter | ConvertTo-Json -Depth 5 -Compress)
[ordered]@{
    state_root = $InstalledStateRootFull
    state_manifest = $installedStateAfter
    version_2_database = $installedDatabaseAfter
    replacement_staging_root = $installedReplacementStagingRoot
    replacement_staging_manifest = $installedStagingAfter
    active_pointer = $installedActiveAfter
    temporary_test_product_roots = $testProductRootsAfter
    unchanged = $installedInputsUnchanged
} | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $EvidenceRootFull 'installed-input-after.json') -Encoding utf8NoBOM

$requiredAcceptanceIds = @('UX-01','ACCT-01','ACCT-02','AUTO-01','BG-01','TREE-01','PERF-01','BOOT-01',
    'DIFF-01','SELECT-01','CLOUD-01','DELETE-01','RECOVERY-01','REWIND-01','MOVE-01','EDGE-01','SCALE-01',
    'MIGRATE-01','RELEASE-01')
$acceptance = foreach ($id in $requiredAcceptanceIds) {
    $mapped = @($PhaseResults | Where-Object { $_.acceptance_ids -contains $id })
    $passed = $mapped.Count -gt 0 -and @($mapped | Where-Object { $_.exit_code -ne 0 }).Count -eq 0
    if ($id -eq 'UX-01') { $passed = $passed -and $renderSetValid }
    if ($id -eq 'MIGRATE-01') { $passed = $passed -and $installedInputsUnchanged }
    if ($id -in @('TREE-01','BOOT-01','DELETE-01','RECOVERY-01','EDGE-01')) {
        $passed = $passed -and $structuralContractValid
    }
    $status = if ($passed) { 'passed' } else { 'failed' }
    $pendingRequirements = @()
    if ($id -eq 'RELEASE-01') {
        $passed = $false
        $status = 'pending'
        $pendingRequirements = @('independent-terminal-acceptance','protected-signing','versioned-installation',
            'active-pointer-transition','installed-readback','bounded-real-dropbox-acceptance',
            'tag-and-release-publication','rollback-readback')
    }
    [ordered]@{
        id = $id
        passed = $passed
        status = $status
        phases = @($mapped | ForEach-Object { $_.name })
        pending_requirements = $pendingRequirements
    }
}
$releaseEmptyPhaseNegativeWitness = @($acceptance | Where-Object { $_.id -eq 'RELEASE-01' -and
    $_.status -eq 'pending' -and -not $_.passed -and $_.phases.Count -eq 0 }).Count -eq 1
if (-not $releaseEmptyPhaseNegativeWitness) {
    throw 'An empty or unmapped phase set was able to satisfy RELEASE-01.'
}
$predeploymentAcceptance = @($acceptance | Where-Object { $_.id -ne 'RELEASE-01' })
$allPredeploymentPassed = @($predeploymentAcceptance | Where-Object { $_.status -ne 'passed' }).Count -eq 0

$coreCorrectionIds = @('C02','C03','C04','C05','C06','C07','C09','C13','C14','C15','C16')
$correctionPhase = @($PhaseResults | Where-Object { $_.name -eq 'discovery-correction-regressions' })
if ($correctionPhase.Count -ne 1) {
    throw 'Correction coverage requires one discovery-correction-regressions phase.'
}
$coreCorrectionEvidence = @($coreCorrectionIds | ForEach-Object {
    Get-ExactCorrectionWitness -PhaseName 'discovery-correction-regressions' -CorrectionId $_ `
        -ExpectedLogSha256 $correctionPhase[0].log_sha256
})
$coreCorrectionIdsFound = @($coreCorrectionEvidence | ForEach-Object { $_.correction_id })
if ($coreCorrectionEvidence.Count -ne $coreCorrectionIds.Count -or
    @(Compare-Object $coreCorrectionIds $coreCorrectionIdsFound).Count -ne 0) {
    throw 'The executed correction witness set is incomplete, duplicated, or unexpected.'
}
$coreCorrectionById = @{}
foreach ($witness in $coreCorrectionEvidence) { $coreCorrectionById[$witness.correction_id] = $witness }

$uiPhase = @($PhaseResults | Where-Object { $_.name -eq 'ui-accessibility-and-layout' })
if ($uiPhase.Count -ne 1) { throw 'Correction coverage requires one UI phase.' }
$compactRenderNames = @(
    'dashboard-amoled-compact-successor.png',
    'pairs-amoled-compact-successor.png',
    'pairs-amoled-compact-focus-successor.png',
    'activity-amoled-compact-successor.png',
    'queue-amoled-compact-successor.png',
    'conflicts-amoled-compact-focus-successor.png',
    'settings-amoled-compact-focus-successor.png'
)
$compactRenderEvidence = @($renderManifest | Where-Object { $compactRenderNames -contains $_.path })
$compactUiCorrectionPassed = $uiPhase[0].exit_code -eq 0 -and
    $compactRenderEvidence.Count -eq $compactRenderNames.Count -and
    @($compactRenderEvidence | Where-Object { $_.bytes -le 0 }).Count -eq 0
$uiLogPath = Join-Path $EvidenceRootFull $uiPhase[0].log
$currentDpiMetrics = @()
foreach ($line in [IO.File]::ReadAllLines($uiLogPath)) {
    $trimmed = $line.Trim()
    if (-not $trimmed.StartsWith('{', [StringComparison]::Ordinal)) { continue }
    try { $candidate = $trimmed | ConvertFrom-Json -ErrorAction Stop }
    catch { continue }
    if ($candidate.PSObject.Properties.Name -contains 'metric' -and
        [string]$candidate.metric -eq 'loaded_popup_dpi_context') {
        $currentDpiMetrics += $candidate
    }
}
$requiredDpiContexts = @(96, 144, 192)
$expectedUiAssemblySha256 = (Get-FileHash -LiteralPath $uiDll -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedApplicationAssemblySha256 = (Get-FileHash -LiteralPath $appDll -Algorithm SHA256).Hash.ToLowerInvariant()
$dpiCaptureReceipts = @()
$dpiContextRenderEvidence = @()
$dpiCaptureRevision = $null
$dpiCaptureApplicationSha256 = $null
$dpiCaptureFixtureSha256 = $null
$dpiCaptureMode = 'invalid'
$dpiUnchangedUiInputs = @()
$dpiCaptureValid = $null -ne $RealDpiCaptureRootFull
if ($dpiCaptureValid) {
    $dpiEvidenceRoot = Join-Path $EvidenceRootFull 'real-dpi-captures'
    New-Item -ItemType Directory -Path $dpiEvidenceRoot | Out-Null
    Get-ChildItem -LiteralPath $RealDpiCaptureRootFull -Force | Copy-Item -Destination $dpiEvidenceRoot -Recurse
    $dpiCaptureManifest = Get-FileManifest $dpiEvidenceRoot
    foreach ($dpi in $requiredDpiContexts) {
        $receiptRelative = "real-dpi-$dpi-receipt.json"
        $receiptPath = Join-Path $dpiEvidenceRoot $receiptRelative
        if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
            $dpiCaptureValid = $false
            continue
        }
        try { $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -ErrorAction Stop }
        catch {
            $dpiCaptureValid = $false
            continue
        }
        $captureVersion = [string]$receipt.applicationInformationalVersion
        $captureVersionMatch = [regex]::Match($captureVersion,
            '^' + [regex]::Escape($ExpectedVersion) + '\+([0-9a-fA-F]{40})$')
        $receiptRevision = if ($captureVersionMatch.Success) {
            $captureVersionMatch.Groups[1].Value.ToLowerInvariant()
        } else { $null }
        $receiptFixtureSha256 = ([string]$receipt.fixtureAssemblySha256).ToLowerInvariant()
        $receiptApplicationSha256 = ([string]$receipt.applicationAssemblySha256).ToLowerInvariant()
        if ($null -eq $dpiCaptureRevision -and $null -ne $receiptRevision) {
            $dpiCaptureRevision = $receiptRevision
            $dpiCaptureFixtureSha256 = $receiptFixtureSha256
            $dpiCaptureApplicationSha256 = $receiptApplicationSha256
        }
        $receiptValid = [string]$receipt.schema -eq 'sync-shark-real-dpi-capture-v1' -and
            [double]$receipt.targetDpi -eq $dpi -and
            @($receipt.observedWindowDpis).Count -eq 1 -and
            [double](@($receipt.observedWindowDpis)[0]) -eq $dpi -and
            [double]$receipt.observedPopupDpi -eq $dpi -and
            [int]$receipt.syntheticDpiOperations -eq 0 -and
            $null -ne $receiptRevision -and
            $receiptFixtureSha256 -match '^[0-9a-f]{64}$' -and
            $receiptApplicationSha256 -match '^[0-9a-f]{64}$' -and
            $receiptRevision -eq $dpiCaptureRevision -and
            $receiptFixtureSha256 -eq $dpiCaptureFixtureSha256 -and
            $receiptApplicationSha256 -eq $dpiCaptureApplicationSha256
        $expectedNames = @()
        foreach ($page in @('dashboard','pairs','connections','first-sync','activity','queue','conflicts',
                'recovery','rewind','settings')) {
            $expectedNames += "$page-amoled-context-$dpi-dpi-successor.png"
        }
        $expectedNames += "conflict-context-menu-amoled-context-$dpi-dpi-successor.png"
        $expectedNames += "conflict-group-popup-amoled-context-$dpi-dpi-successor.png"
        $expectedNames += "header-account-tooltip-amoled-context-$dpi-dpi-successor.png"
        $expectedNames += "dropbox-browser-amoled-context-$dpi-dpi-successor.png"
        $receiptFiles = @($receipt.files)
        if ($receiptFiles.Count -ne $expectedNames.Count -or
            @(Compare-Object $expectedNames @($receiptFiles | ForEach-Object { [string]$_.path })).Count -ne 0) {
            $receiptValid = $false
        }
        foreach ($file in $receiptFiles) {
            $relative = [string]$file.path
            if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('..')) {
                $receiptValid = $false
                continue
            }
            $capturedPath = Join-Path $dpiEvidenceRoot $relative
            if (-not (Test-Path -LiteralPath $capturedPath -PathType Leaf)) {
                $receiptValid = $false
                continue
            }
            $actual = Get-Item -LiteralPath $capturedPath
            $actualHash = (Get-FileHash -LiteralPath $capturedPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual.Length -ne [long]$file.bytes -or
                -not $actualHash.Equals([string]$file.sha256, [StringComparison]::OrdinalIgnoreCase)) {
                $receiptValid = $false
            }
        }
        $dpiCaptureValid = $dpiCaptureValid -and $receiptValid
        $dpiCaptureReceipts += [ordered]@{
            target_dpi = $dpi
            valid = $receiptValid
            receipt = $receiptRelative
            receipt_sha256 = (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash.ToLowerInvariant()
            observed_window_dpis = @($receipt.observedWindowDpis)
            observed_popup_dpi = $receipt.observedPopupDpi
            synthetic_dpi_operations = $receipt.syntheticDpiOperations
            application_informational_version = $receipt.applicationInformationalVersion
            fixture_assembly_sha256 = $receipt.fixtureAssemblySha256
            application_assembly_sha256 = $receipt.applicationAssemblySha256
        }
    }
    if ($null -ne $dpiCaptureRevision) {
        if ($dpiCaptureRevision.Equals($head, [StringComparison]::OrdinalIgnoreCase)) {
            $dpiCaptureMode = 'current-revision'
            $dpiCaptureValid = $dpiCaptureValid -and
                $dpiCaptureFixtureSha256.Equals($expectedUiAssemblySha256,
                    [StringComparison]::OrdinalIgnoreCase) -and
                $dpiCaptureApplicationSha256.Equals($expectedApplicationAssemblySha256,
                    [StringComparison]::OrdinalIgnoreCase)
        }
        else {
            $ancestorOutput = & git -C $RepositoryRoot merge-base --is-ancestor $dpiCaptureRevision $head 2>&1
            $ancestorExit = $LASTEXITCODE
            $dpiCaptureValid = $dpiCaptureValid -and $ancestorExit -eq 0
            if ($ancestorExit -eq 0) {
                foreach ($path in @('apps/SyncShark','tests/SyncShark.UiFixture',
                        'Directory.Build.props','global.json')) {
                    try {
                        $captureObject = Invoke-Git @('rev-parse', "${dpiCaptureRevision}:$path")
                        $candidateObject = Invoke-Git @('rev-parse', "${head}:$path")
                        $same = $captureObject.Equals($candidateObject,
                            [StringComparison]::OrdinalIgnoreCase)
                        $dpiCaptureValid = $dpiCaptureValid -and $same
                        $dpiUnchangedUiInputs += [ordered]@{
                            path = $path
                            capture_object = $captureObject
                            candidate_object = $candidateObject
                            unchanged = $same
                        }
                    }
                    catch {
                        $dpiCaptureValid = $false
                        $dpiUnchangedUiInputs += [ordered]@{
                            path = $path
                            capture_object = $null
                            candidate_object = $null
                            unchanged = $false
                        }
                    }
                }
                if ($dpiCaptureValid) { $dpiCaptureMode = 'unchanged-ui-source-reuse' }
            }
        }
    }
    $expectedCaptureCount = ($requiredDpiContexts.Count * 15)
    if ($dpiCaptureManifest.Count -ne $expectedCaptureCount) { $dpiCaptureValid = $false }
    $dpiContextRenderEvidence = @($dpiCaptureManifest | Where-Object { $_.path -like '*.png' })
}
$dpiContextCorrectionPassed = $uiPhase[0].exit_code -eq 0 -and $dpiCaptureValid -and
    $dpiCaptureReceipts.Count -eq $requiredDpiContexts.Count -and
    @($dpiCaptureReceipts | Where-Object { -not $_.valid }).Count -eq 0 -and
    $dpiContextRenderEvidence.Count -eq ($requiredDpiContexts.Count * 14) -and
    @($dpiContextRenderEvidence | Where-Object { $_.bytes -le 0 }).Count -eq 0

$findingNames = [ordered]@{
    C01 = 'Release acceptance falsely passed without deployment evidence'
    C02 = 'Product exit could terminate before the durable checkpoint'
    C03 = 'Recovery and Keep both buffered whole objects in memory'
    C04 = 'Rewind could resume from an unsafe or ambiguous state'
    C05 = 'The local watcher queue could grow without a bounded convergence path'
    C06 = 'One unhealthy pair could abort the global pair batch'
    C07 = 'Cloud Files range hydration downloaded the full object'
    C08 = 'Compact layouts clipped required controls'
    C09 = 'Mutable state exhausted a fixed write-generation ceiling'
    C10 = 'Performance evidence could project absent, cross-phase, or stale metrics'
    C11 = 'Structural contract mappings were represented as executed case tests'
    C12 = 'DPI evidence scaled bitmaps without exercising a loaded WPF window'
    C13 = 'Ancestor uncertainty did not fence the full descendant subtree'
    C14 = 'Ancestor type conflicts did not fence the full descendant subtree'
    C15 = 'Provider source operations could escape the selected pair root'
    C16 = 'Dropbox browser pagination and response accumulation lacked hard ceilings'
}
$correctionCoverage = foreach ($correctionId in $findingNames.Keys) {
    $passed = $false
    $evidence = @()
    switch ($correctionId) {
        'C01' {
            $passed = $releaseEmptyPhaseNegativeWitness
            $evidence = @([ordered]@{
                kind = 'release-empty-phase-negative-witness'
                value = $releaseEmptyPhaseNegativeWitness
                release_status = ($acceptance | Where-Object { $_.id -eq 'RELEASE-01' }).status
                acceptance_script = 'tools/Invoke-FullClientAcceptance.ps1'
                acceptance_script_sha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
            })
        }
        'C08' {
            $passed = $compactUiCorrectionPassed
            $evidence = @([ordered]@{
                kind = 'loaded-ui-compact-render-set'
                producing_phase = $uiPhase[0].name
                log = $uiPhase[0].log
                log_sha256 = $uiPhase[0].log_sha256
                required_renders = $compactRenderEvidence
            })
        }
        'C10' {
            $passed = @($metricProjectionNegativeWitnesses.Values | Where-Object { -not $_ }).Count -eq 0
            $evidence = @([ordered]@{
                kind = 'exact-metric-projection-and-negative-witnesses'
                artifact = 'exact-measurements.json'
                artifact_sha256 = (Get-FileHash -LiteralPath (Join-Path $EvidenceRootFull 'exact-measurements.json') -Algorithm SHA256).Hash.ToLowerInvariant()
                negative_witnesses = $metricProjectionNegativeWitnesses
            })
        }
        'C11' {
            $passed = $structuralContractValid -and
                [int]$contractCoverage.generated_case_tests_executed -eq 0 -and
                [bool]$contractCoverage.case_specific_execution_claim -eq $false
            $evidence = @([ordered]@{
                kind = 'structural-mapping-truth-boundary'
                artifact = 'true-sync-contract/contract-coverage.json'
                artifact_sha256 = (Get-FileHash -LiteralPath (Join-Path $contractEvidence 'contract-coverage.json') -Algorithm SHA256).Hash.ToLowerInvariant()
                cases_structurally_mapped = [int]$contractCoverage.cases_structurally_mapped_to_production_witnesses
                generated_case_tests_executed = [int]$contractCoverage.generated_case_tests_executed
                case_specific_execution_claim = [bool]$contractCoverage.case_specific_execution_claim
                phase_receipt = 'production-witness-execution.json'
                phase_receipt_sha256 = (Get-FileHash -LiteralPath $witnessReceiptPath -Algorithm SHA256).Hash.ToLowerInvariant()
            })
        }
        'C12' {
            $passed = $dpiContextCorrectionPassed
            $evidence = @([ordered]@{
                kind = 'loaded-window-dpi-context-render-set'
                producing_phase = $uiPhase[0].name
                log = $uiPhase[0].log
                log_sha256 = $uiPhase[0].log_sha256
                current_host_popup_metrics = $currentDpiMetrics
                immutable_real_dpi_receipts = $dpiCaptureReceipts
                real_dpi_capture_mode = $dpiCaptureMode
                real_dpi_capture_revision = $dpiCaptureRevision
                unchanged_ui_source_inputs = $dpiUnchangedUiInputs
                required_renders = $dpiContextRenderEvidence
            })
        }
        default {
            if (-not $coreCorrectionById.ContainsKey($correctionId)) {
                throw "Correction $correctionId has no evidence mapping."
            }
            $passed = $coreCorrectionById[$correctionId].status -eq 'executed_pass'
            $evidence = @($coreCorrectionById[$correctionId])
        }
    }
    [ordered]@{
        correction_id = $correctionId
        finding = $findingNames[$correctionId]
        passed = $passed
        evidence = $evidence
    }
}
$allCorrectionCoveragePassed = $correctionCoverage.Count -eq 16 -and
    @($correctionCoverage | Where-Object { -not $_.passed }).Count -eq 0
[ordered]@{
    revision = $head
    findings_expected = 16
    findings_mapped = $correctionCoverage.Count
    all_corrections_passed = $allCorrectionCoveragePassed
    corrections = $correctionCoverage
} | ConvertTo-Json -Depth 14 |
    Set-Content -LiteralPath (Join-Path $EvidenceRootFull 'correction-coverage.json') -Encoding utf8NoBOM

$sourcePackageReady = $allPredeploymentPassed -and $renderSetValid -and $versionBound -and
    $installedInputsUnchanged -and $structuralContractValid -and $allCorrectionCoveragePassed -and
    $criticalMetricSchemaSelfTest.passed -and $strictMetricRuntimeCoverage.passed
$result = [ordered]@{
    revision = $head
    tree = Invoke-Git @('rev-parse', 'HEAD^{tree}')
    expected_version = $ExpectedVersion
    file_version = $appVersion.FileVersion
    product_version = $appVersion.ProductVersion
    version_bound = $versionBound
    installed_inputs_unchanged = $installedInputsUnchanged
    ui_render_count = $renderManifest.Count
    ui_render_set_valid = $renderSetValid
    structural_contract_valid = $structuralContractValid
    generated_contract_case_tests_executed = [int]$contractCoverage.generated_case_tests_executed
    contract_case_specific_execution_claim = [bool]$contractCoverage.case_specific_execution_claim
    exact_measurements = 'exact-measurements.json'
    metric_projection_negative_witnesses = $metricProjectionNegativeWitnesses
    critical_metric_schema_self_test = 'critical-metric-schema-self-test.json'
    critical_metric_schema_self_test_passed = $criticalMetricSchemaSelfTest.passed
    critical_metric_runtime_mutation_coverage = 'critical-metric-runtime-mutation-coverage.json'
    critical_metric_runtime_mutation_coverage_passed = $strictMetricRuntimeCoverage.passed
    critical_metric_runtime_mutation_count = $strictMetricRuntimeCoverage.mutation_count
    release_empty_phase_negative_witness = $releaseEmptyPhaseNegativeWitness
    correction_coverage = 'correction-coverage.json'
    all_discovery_corrections_passed = $allCorrectionCoveragePassed
    acceptance = $acceptance
    phases = $PhaseResults
    all_predeployment_acceptance_ids_passed = $allPredeploymentPassed
    source_package_ready = $sourcePackageReady
    release_ready = $false
    all_acceptance_ids_passed = $false
    completed_utc = [DateTimeOffset]::UtcNow.ToString('O')
}
$result | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath (Join-Path $EvidenceRootFull 'full-client-acceptance.json') -Encoding utf8NoBOM
if (-not $sourcePackageReady) {
    throw 'The frozen object failed one or more predeployment acceptance IDs; inspect full-client-acceptance.json and phase logs.'
}
