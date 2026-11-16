param([Parameter(Mandatory=$true)][string]$FixtureParent)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force
. (Join-Path $PSScriptRoot 'SyncShark.Update.ps1')

# Exercise the real unelevated input fence, not a replacement predicate or
# installer. All candidate contents are inert, and no installer is executed.
$fixture=Join-Path ([IO.Path]::GetFullPath($FixtureParent)) ([guid]::NewGuid().ToString('N'))
$scripts=Join-Path $fixture 'scripts'
$package=Join-Path $fixture 'package'
$runtime=Join-Path $package 'signed-candidate'
$installer=Join-Path $scripts 'Install-SyncShark.ps1'
$module=Join-Path $scripts 'SyncShark.Release.psm1'
$payload=Join-Path $runtime 'SyncShark.Core.dll'
$rows=[Collections.Generic.List[object]]::new()
$lease=$null
function Pass([string]$Name) {$rows.Add([pscustomobject]@{name=$Name;passed=$true});[Console]::Out.WriteLine('PASS '+$Name)}
function Rejected([string]$Name,[scriptblock]$Action) {
    $rejected=$false
    try {& $Action} catch [IO.IOException] {$rejected=$true} catch [UnauthorizedAccessException] {$rejected=$true}
    if(-not $rejected){throw ('The real input fence accepted '+$Name)}
    Pass $Name
}
try {
    [void][IO.Directory]::CreateDirectory($scripts)
    [void][IO.Directory]::CreateDirectory($runtime)
    [IO.File]::WriteAllText($installer,"throw 'Fixture installer MUST NEVER execute.'")
    [IO.File]::WriteAllText($module,'# Inert module input; never imported.')
    [IO.File]::WriteAllText((Join-Path $package 'unsigned-candidate-framework-manifest.json'),'{}')
    [IO.File]::WriteAllText($payload,'inert package bytes')
    $before=Get-SyncSharkSha256 $installer
    $lease=Open-SyncSharkUpdateInputLease $installer
    if((Get-SyncSharkSha256 $installer) -cne $before -or [IO.File]::ReadAllText($payload) -cne 'inert package bytes') {throw 'Held inputs were not readable unchanged.'}
    Pass 'Legitimate local NTFS inputs remain readable under the actual fence'
    Rejected 'Installer overwrite is denied after verification' {[IO.File]::WriteAllText($installer,'substitute')}
    Rejected 'Installer replacement is denied after verification' {[IO.File]::Move($installer,$installer+'.foreign')}
    Rejected 'Module overwrite is denied through invocation' {[IO.File]::WriteAllText($module,'substitute')}
    Rejected 'Package payload overwrite is denied through invocation' {[IO.File]::WriteAllText($payload,'substitute')}
    Rejected 'Installer parent rename is denied' {[IO.Directory]::Move($scripts,$scripts+'.foreign')}
    Rejected 'Package parent rename is denied' {[IO.Directory]::Move($package,$package+'.foreign')}
    Rejected 'Release ancestor rename is denied' {[IO.Directory]::Move($fixture,$fixture+'.foreign')}
    # Holding an ancestor is not a global filesystem write lock: the existing
    # installer must still be able to create its independent transaction files.
    $independent=Join-Path $FixtureParent ([guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($independent)
    try {[IO.File]::WriteAllText((Join-Path $independent 'transaction.json'),'{}');Pass 'Independent transaction writes remain possible'}
    finally {Remove-Item -LiteralPath (Join-Path $independent 'transaction.json');Remove-Item -LiteralPath $independent}
    $lease.Dispose();$lease=$null
    [IO.File]::WriteAllText($payload,'inert package bytes after fence release')
    Pass 'Disposal releases the exact owned input handles'
    $writer=[IO.File]::Open($installer,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::ReadWrite)
    try {
        $rejected=$false
        try {$unexpected=Open-SyncSharkUpdateInputLease $installer;$unexpected.Dispose()}catch {
            $cause=$_.Exception.GetBaseException()
            if($cause -isnot [ComponentModel.Win32Exception] -or $cause.NativeErrorCode -notin @(32,33)){throw}
            $rejected=$true
        }
        if(-not $rejected){throw 'Existing mutable installer handle was accepted.'}
        Pass 'A pre-existing writer rejects admission and releases partial guards'
    } finally {$writer.Dispose()}
    # Run the actual bridge's first rejection boundary while all its real guards
    # are held. A changed hash must stop before signature parsing or invocation.
    $rejected=$false
    try {Invoke-SyncSharkUpdate -Installer $installer -ExpectedInstallerHash ('0'*64) -CurrentVersion '0.3.54'}
    catch {if($_.Exception.Message -notlike '*selected installer changed*'){throw};$rejected=$true}
    if(-not $rejected){throw 'The real bridge accepted changed installer bytes.'}
    Pass 'Shared GUI/CLI update bridge rejects changed bytes without executing candidate code'
    $alias=Join-Path $fixture 'installer-alias.ps1'
    [void](New-Item -ItemType HardLink -Path $alias -Target $installer)
    try {
        $rejected=$false
        try {$unexpected=Open-SyncSharkUpdateInputLease $installer;$unexpected.Dispose()}catch {
            if($_.Exception.GetBaseException().Message -notlike '*aliased*'){throw}
            $rejected=$true
        }
        if(-not $rejected){throw 'Aliased installer was accepted.'}
        Pass 'Hard-linked installer input is rejected'
    }finally{Remove-Item -LiteralPath $alias}
    $redirect=Join-Path $runtime 'redirected'
    [void](New-Item -ItemType Junction -Path $redirect -Target $scripts)
    try {
        $rejected=$false
        try {$unexpected=Open-SyncSharkUpdateInputLease $installer;$unexpected.Dispose()}catch {
            if($_.Exception.GetBaseException().Message -notlike '*ordinary on-disk*'){throw}
            $rejected=$true
        }
        if(-not $rejected){throw 'Redirected package directory was accepted.'}
        Pass 'Redirected package child is rejected without traversal'
    }finally{Remove-Item -LiteralPath $redirect}
    # SS-D19: the declared signed updater companion is verified and held before
    # anything becomes ready. The signed-contract reader and the Authenticode
    # observation are adapters for signed/external inputs; the native hold, its
    # size/hash binding, the release signature policy and every refusal below are
    # the production ones.
    $companion=Join-Path $scripts 'SyncSharkUpdater.exe'
    $companionBytes=[Text.Encoding]::UTF8.GetBytes('inert signed updater companion bytes')
    [IO.File]::WriteAllBytes($companion,$companionBytes)
    $companionSize=(Get-Item -LiteralPath $companion).Length
    $companionHash=(Get-FileHash -LiteralPath $companion -Algorithm SHA256).Hash.ToLowerInvariant()
    $script:declaredRelease=$null
    function Read-SyncSharkUpdaterRelease([string]$Installer,[string]$ProductVersion){ $script:declaredRelease }
    function Get-SyncSharkSignatureObservation([string]$Path){
        $publisher=(Get-SyncSharkSignerIdentity).roles.publisher
        [pscustomobject]@{Status='Valid';RawStatus='Valid';Thumbprint=@($publisher.thumbprints)[0];
            Subject=$publisher.subject;Issuer=$publisher.subject;
            SpkiSha256=@($publisher.spki)[0];
            SignerChainStatuses=@();Rfc3161Present=$true;Rfc3161CryptographicallyValid=$true;Rfc3161=$true;
            TimestampSignatureBound=$true;TimestampChainEvaluatedAtTokenTime=$true;TimestampChainValid=$true;
            TimestampUtc=$null;TimestampChainStatuses=@()}
    }
    $contract=[pscustomobject]@{version='0.3.99';sourceRevision=('a'*40)}
    $publisherPin=(Get-SyncSharkSignerIdentity).roles.publisher
    $pinThumb=@($publisherPin.thumbprints);$pinSubject=$publisherPin.subject
    $pinSpki=@($publisherPin.spki)
    function CompanionAttempt([long]$Bytes,[string]$Hash){
        # The whole production companion step, with its own lease always disposed.
        $script:declaredRelease=[pscustomobject]@{protectedUpdater=[pscustomobject]@{workerBytes=$Bytes;workerSha256=$Hash}}
        $attempt=Open-SyncSharkUpdateInputLease $installer
        try { return (Assert-SyncSharkUpdateProtectedCompanion $attempt $installer $contract $pinThumb $pinSubject $pinSpki) }
        finally { $attempt.Dispose() }
    }
    function CompanionRefusal([string]$Label,[scriptblock]$Action){
        $message=$null
        try { & $Action } catch { $message=$_.Exception.GetBaseException().Message }
        if($null -eq $message){throw ('The signed updater companion binding accepted '+$Label)}
        Pass $Label
        $message
    }
    # (a) A release without protectedUpdater keeps legacy behaviour: nothing held.
    $script:declaredRelease=$null
    $legacyLease=Open-SyncSharkUpdateInputLease $installer
    try { $legacy=Assert-SyncSharkUpdateProtectedCompanion $legacyLease $installer $contract $pinThumb $pinSubject $pinSpki }
    finally { $legacyLease.Dispose() }
    if($null -ne $legacy){throw 'A release without protectedUpdater produced a companion identity.'}
    [IO.File]::WriteAllText($companion,'legacy release keeps the companion unheld')
    [IO.File]::WriteAllBytes($companion,$companionBytes)
    Pass 'A release without protectedUpdater holds no companion and stays compatible'
    # (b) Declared and exact: the real native hold plus the release signature policy.
    $companionLease=Open-SyncSharkUpdateInputLease $installer
    try {
        $script:declaredRelease=[pscustomobject]@{protectedUpdater=[pscustomobject]@{workerBytes=$companionSize;workerSha256=$companionHash}}
        $heldPath=Assert-SyncSharkUpdateProtectedCompanion $companionLease $installer $contract $pinThumb $pinSubject $pinSpki
        if([IO.Path]::GetFullPath($heldPath) -cne [IO.Path]::GetFullPath($companion)){throw 'The held companion is not the declared package member.'}
        Pass 'The declared updater companion is held and signature-checked before ready'
        Rejected 'Held updater companion overwrite is denied' {[IO.File]::WriteAllText($companion,'substitute')}
        Rejected 'Held updater companion rename is denied' {[IO.File]::Move($companion,$companion+'.foreign')}
        Rejected 'Held updater companion ancestor rename is denied' {[IO.Directory]::Move($scripts,$scripts+'.foreign')}
    } finally { $companionLease.Dispose() }
    # (c) Missing, mismatched and malformed companion identity all refuse, and no
    # refusal ever emits a launch-ready handoff.
    $capture=[IO.StringWriter]::new();$savedOut=[Console]::Out
    try {
        [Console]::SetOut($capture)
        [IO.File]::Delete($companion)
        $missingMessage=CompanionRefusal 'Missing updater companion is refused' { [void](CompanionAttempt $companionSize $companionHash) }
    } finally {
        [Console]::SetOut($savedOut)
        [IO.File]::WriteAllBytes($companion,$companionBytes)
    }
    if($missingMessage -notlike '*missing*'){throw 'The missing companion refusal is not the declared-companion guard.'}
    if($capture.ToString() -like '*"ready":true*'){throw 'A refused updater preflight emitted a launch-ready handoff.'}
    if(-not [object]::ReferenceEquals($savedOut,[Console]::Out)){throw 'The console writer was not restored after the preflight capture.'}
    Pass 'A refused updater preflight emits no ready handoff'
    $sizeMessage=CompanionRefusal 'Mismatched updater companion size is refused' { [void](CompanionAttempt ($companionSize+1) $companionHash) }
    if($sizeMessage -notlike '*differ from the signed release*'){throw 'The size mismatch refusal is not the companion byte binding.'}
    $hashMessage=CompanionRefusal 'Mismatched updater companion hash is refused' { [void](CompanionAttempt $companionSize ('0'*64)) }
    if($hashMessage -notlike '*differ from the signed release*'){throw 'The hash mismatch refusal is not the companion byte binding.'}
    $zeroMessage=CompanionRefusal 'Zero updater companion size binding is refused' { [void](CompanionAttempt 0 $companionHash) }
    if($zeroMessage -notlike '*size binding is invalid*'){throw 'The zero-size refusal is not the binding guard.'}
    $shortMessage=CompanionRefusal 'Non-hash updater companion binding is refused' { [void](CompanionAttempt $companionSize 'not-a-hash') }
    if($shortMessage -notlike '*hash binding is invalid*'){throw 'The short-hash refusal is not the binding guard.'}
    $alias=Join-Path $fixture 'updater-alias.exe'
    [void](New-Item -ItemType HardLink -Path $alias -Target $companion)
    try {
        $aliasMessage=CompanionRefusal 'Hard-linked updater companion is refused' { [void](CompanionAttempt $companionSize $companionHash) }
        if($aliasMessage -notlike '*aliased*'){throw 'The hard-linked companion refusal is not the alias guard.'}
    } finally { Remove-Item -LiteralPath $alias -Force }
    [ordered]@{ok=$true;cases=@($rows);installer_executed=$false;installation_attempted=$false;
        ready_handoffs_emitted=0;companion_declared_hold=$true;companion_legacy_hold=$false;fixture=$fixture}|ConvertTo-Json -Depth 5 -Compress
} finally {
    if($null -ne $lease){$lease.Dispose()}
    # Only this invocation's positively known generated members. Never recursive
    # cleanup or removal of a changed, extra or refused object.
    foreach($path in @($installer,$module,$payload,$companion,(Join-Path $package 'unsigned-candidate-framework-manifest.json'))) {
        if(Test-Path -LiteralPath $path -PathType Leaf){Remove-Item -LiteralPath $path}
    }
    foreach($path in @($runtime,$package,$scripts,$fixture)) {
        if(Test-Path -LiteralPath $path -PathType Container){Remove-Item -LiteralPath $path}
    }
}
