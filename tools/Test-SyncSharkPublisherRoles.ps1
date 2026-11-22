# Windows PowerShell 7.4+. Exercise the publisher boundary without touching an installed runtime.
param([Parameter(Mandatory)][string]$BuildRoot)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force
$roles=(Get-SyncSharkSignerIdentity).roles
foreach ($role in @($roles.publisher,$roles.package)) {
    $observation=[pscustomobject]@{Status='Valid';Thumbprint=$role.thumbprints[0];Subject=$role.subject;Issuer=$role.subject;SpkiSha256=$role.spki[0];SignerChainStatuses=@();Rfc3161Present=$true;Rfc3161CryptographicallyValid=$true;Rfc3161=$true;TimestampSignatureBound=$true;TimestampChainEvaluatedAtTokenTime=$true;TimestampChainValid=$true;TimestampChainStatuses=@()}
    $allowed=$true
    try { Assert-SyncSharkSignatureObservation $observation $roles.publisher.thumbprints $roles.publisher.subject $roles.publisher.subject $roles.publisher.spki }
    catch { if ($_.Exception.Message -notlike '*Pinned signer identity mismatch*') { throw }; $allowed=$false }
    if ($allowed -ne ($role.subject -ceq $roles.publisher.subject)) { throw 'Wrong executable publisher decision.' }
}
