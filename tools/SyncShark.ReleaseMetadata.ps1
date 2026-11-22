# Shared build-time metadata. These functions do not install or authorize effects.
function New-SyncSharkReleaseMetadata {
    param([string]$Version,[string]$SourceRevision,[string]$PreviousVersion,[string]$PreviousTreeSha256)
    $semver='(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)'
    if($Version -cnotmatch ('^'+$semver+'$') -or $SourceRevision -cnotmatch '^[0-9a-f]{40}$' -or
       $PreviousVersion -cnotmatch ('^'+$semver+'\+[0-9a-f]{40}$') -or $PreviousTreeSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Release metadata must bind exact canonical versions, source and predecessor tree.'
    }
    $previous=$PreviousVersion.Split('+')[0]
    if(@(([version]$Version).Major,([version]$Version).Minor,([version]$Version).Build,([version]$previous).Major,([version]$previous).Minor,([version]$previous).Build | Where-Object {$_ -gt 65535}).Count){throw 'Release version component exceeds the native package bound.'}
    if([version]$Version -le [version]$previous){throw 'The successor must be newer than its exact predecessor.'}
    [pscustomobject]@{Version=$Version;SourceRevision=$SourceRevision;ProductVersion=($Version+'+'+$SourceRevision);
        PreviousVersion=$PreviousVersion;PreviousReleaseVersion=$previous;PreviousTreeSha256=$PreviousTreeSha256}
}

function Expand-SyncSharkReleaseTemplate {
    param([string]$Template,[object]$Metadata,[string]$ContractJson,[string]$ProofRestorationJson='')
    $checked=New-SyncSharkReleaseMetadata $Metadata.Version $Metadata.SourceRevision $Metadata.PreviousVersion $Metadata.PreviousTreeSha256
    $contract=$ContractJson|ConvertFrom-Json
    if($contract.product -cne 'Massdex.SyncShark' -or $contract.version -cne $checked.Version -or
        $contract.sourceRevision -cne $checked.SourceRevision -or $contract.previousVersion -cne $checked.PreviousVersion) {
        throw 'The generated script and signed metadata would disagree.'
    }
    if(-not $Template.StartsWith('# SyncSharkUpdateContract: __SIGNED_UPDATE_CONTRACT__',[StringComparison]::Ordinal)){
        throw 'The release template has no exact signed contract header.'
    }
    $map=[ordered]@{
        '__RELEASE_VERSION__'=$checked.Version
        '__COMMIT_FULL__'=$checked.SourceRevision
        '__COMMIT_SHORT__'=$checked.SourceRevision.Substring(0,7)
        '__PREVIOUS_PRODUCT_VERSION__'=$checked.PreviousVersion
        '__PREVIOUS_RELEASE_VERSION__'=$checked.PreviousReleaseVersion
        '__PREVIOUS_TREE_SHA256__'=$checked.PreviousTreeSha256
        '__SIGNED_UPDATE_CONTRACT__'=$ContractJson
    }
    foreach($marker in $map.Keys){
        if(-not $Template.Contains($marker)){throw ('Required release metadata marker is absent: '+$marker)}
        $Template=$Template.Replace($marker,[string]$map[$marker])
    }
    if($Template.Contains('__SIGNED_PROOF_RESTORATION__')) {
        $proofLine='# No proof-only restoration contract.'
        if($ProofRestorationJson) {
            if($ProofRestorationJson.Length -gt 16384 -or $ProofRestorationJson.Contains("`r") -or $ProofRestorationJson.Contains("`n")){throw 'Proof restoration must be one bounded signed data line.'}
            $null=$ProofRestorationJson|ConvertFrom-Json
            $proofLine='# SyncSharkProofRestoration: '+$ProofRestorationJson
        }
        $Template=$Template.Replace('__SIGNED_PROOF_RESTORATION__',$proofLine)
    }
    if($Template -cmatch '__[A-Z][A-Z0-9_]*__'){throw 'Unresolved release metadata marker.'}
    $tokens=$null;$errors=$null
    [void][Management.Automation.Language.Parser]::ParseInput($Template,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'Generated release script failed parsing.'}
    return $Template
}
