# Offline O2 preparation only. Synthetic observations exercise the EXISTING
# production pin predicate, not cryptography, custody, enrollment or activation.
# No certificate, key, task, trust store, product state or file is created here.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force
. (Join-Path $PSScriptRoot 'SyncShark.ReleaseMetadata.ps1')

$script:passed = 0
function Check([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw $Label }
    $script:passed++
    Write-Output ('PASS ' + $Label)
}
function Reject([scriptblock]$Action, [string]$Expected, [string]$Label) {
    try { & $Action } catch {
        if ($_.Exception.Message -notlike ('*' + $Expected + '*')) { throw }
        Check $true $Label
        return
    }
    throw ('Expected rejection: ' + $Label)
}
function Observation([string]$Thumbprint, [string]$Subject, [string]$Spki) {
    [pscustomobject]@{
        Status='Valid'; Thumbprint=$Thumbprint; Subject=$Subject; Issuer=$Subject
        SpkiSha256=$Spki; SignerChainStatuses=@()
        Rfc3161Present=$true; Rfc3161CryptographicallyValid=$true; Rfc3161=$true
        TimestampSignatureBound=$true; TimestampChainEvaluatedAtTokenTime=$true
        TimestampChainValid=$true; TimestampChainStatuses=@()
    }
}

# Intentionally synthetic public identities. These are NEVER installation pins.
$runtime = Observation ('A' * 40) 'CN=Massdex, O=Massdex' ('1' * 64)
$container = Observation ('B' * 40) 'CN=Massdex Sync Shark Code Signing v1, O=Massdex' ('2' * 64)
$signerIdentity = Get-SyncSharkSignerIdentity
$oldRuntime = Observation @($signerIdentity.roles.publisher.thumbprints)[0] $runtime.Subject @($signerIdentity.roles.publisher.spki)[0]
$oldContainer = Observation @($signerIdentity.roles.package.thumbprints)[0] $container.Subject $oldRuntime.SpkiSha256
function Verify([object]$Observation, [object]$Role) {
    Assert-SyncSharkSignatureObservation $Observation $Role.Thumbprint $Role.Subject $Role.Issuer $Role.SpkiSha256
}

foreach ($case in @(
    @{name='runtime'; current=$runtime; old=$oldRuntime; other=$container},
    @{name='container'; current=$container; old=$oldContainer; other=$runtime}
)) {
    Verify $case.current $case.current
    Check $true ($case.name + ' exact prospective public observation is accepted by the real pin predicate')
    Reject { Verify $case.old $case.current } 'Pinned signer identity mismatch' ($case.name + ' old ordinary-accessible identity cannot match the isolated identity')
    Reject { Verify $case.other $case.current } 'Pinned signer identity mismatch' ($case.name + ' wrong certificate role is rejected')
    foreach ($field in @('Thumbprint', 'Subject', 'Issuer', 'SpkiSha256')) {
        $changed = Observation $case.current.Thumbprint $case.current.Subject $case.current.SpkiSha256
        $changed.$field = 'foreign'
        Reject { Verify $changed $case.current } 'Pinned signer identity mismatch' ($case.name + ' changed ' + $field + ' is rejected')
    }
    foreach ($field in @('Rfc3161Present', 'Rfc3161CryptographicallyValid', 'Rfc3161', 'TimestampSignatureBound', 'TimestampChainEvaluatedAtTokenTime', 'TimestampChainValid')) {
        $changed = Observation $case.current.Thumbprint $case.current.Subject $case.current.SpkiSha256
        $changed.$field = $false
        Reject { Verify $changed $case.current } 'signature-bound RFC3161' ($case.name + ' missing ' + $field + ' is rejected')
    }
    $changed = Observation $case.current.Thumbprint $case.current.Subject $case.current.SpkiSha256
    $changed.Status = 'NotTrusted'; $changed.SignerChainStatuses = @('UntrustedRoot')
    Verify $changed $case.current
    Check $true ($case.name + ' isolated pinned UntrustedRoot policy remains unchanged')
    $changed.SignerChainStatuses = @('UntrustedRoot', 'NotTimeValid')
    Reject { Verify $changed $case.current } 'Authenticode status is not permitted' ($case.name + ' additional signer-chain failure is rejected')
    $changed = Observation $case.current.Thumbprint $case.current.Subject $case.current.SpkiSha256
    $changed.TimestampChainStatuses = @('UntrustedRoot')
    Reject { Verify $changed $case.current } 'signature-bound RFC3161' ($case.name + ' untrusted timestamp chain is rejected')
}

$revision = 'a' * 40
$metadata = New-SyncSharkReleaseMetadata '0.3.58' $revision ('0.3.56+' + ('b' * 40)) ('c' * 64)
Check ($metadata.ProductVersion -ceq ('0.3.58+' + $revision)) 'Existing release metadata binds the exact source and predecessor'
Reject { New-SyncSharkReleaseMetadata '0.3.56' $revision ('0.3.56+' + ('b' * 40)) ('c' * 64) } 'successor must be newer' 'Same-version replay remains refused'
Reject { New-SyncSharkReleaseMetadata '0.3.58' $revision '0.3.56' ('c' * 64) } 'canonical versions' 'Unbound predecessor remains refused'
Reject { New-SyncSharkReleaseMetadata '0.3.58' $revision ('0.3.56+' + ('b' * 40)) 'foreign' } 'canonical versions' 'Foreign predecessor tree remains refused'

[pscustomobject]@{
    passed=$script:passed
    evidence='Offline synthetic-input checks through existing production pin and metadata predicates only'
    protectedEffects=0
    cryptographicSignatureVerified=$false
    isolatedCustodyVerified=$false
    currentConsumerCutoverVerified=$false
    noUacReady=$false
} | ConvertTo-Json -Compress
