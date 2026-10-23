# One-time, elevated: put a signed Debug-built Discovery helper in place of the
# installed one so Debug builds get fast discovery. Changes exactly two files in
# C:\Program Files\Massdex\SyncShark (old copies kept as .previous):
#   SyncSharkDiscovery.exe   <- the given helper
#   discovery-manifest.json  <- workerSha256 updated to the new helper's hash
# The task "SyncSharkDiscovery" keeps its action path and limits, so it is only
# stopped (if running), not re-registered. Undo: copy both .previous files back.
param([Parameter(Mandatory)][string]$Helper)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'SyncShark.DiscoverySetup.ps1')
Assert-SyncSharkDiscoverySetupRoots 'Restore'   # administrator + protected, existing install folders
$root='C:\Program Files\Massdex\SyncShark'
$worker=Join-Path $root 'SyncSharkDiscovery.exe'; $manifest=Join-Path $root 'discovery-manifest.json'
if(Test-Path -LiteralPath (Join-Path $root 'discovery-setup.json')){throw 'A Discovery setup is pending; finish it first.'}
if(-not (Test-Path -LiteralPath $worker) -or -not (Test-Path -LiteralPath $manifest)){throw 'Fast Discovery is not installed; run the normal setup once first.'}
$workerFile=[IO.File]::Open($Helper,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try {
    # One held read-only handle denies write and delete by others across verify, hash and copy.
    $signature=Get-AuthenticodeSignature -LiteralPath $Helper
    $signer=$signature.SignerCertificate
    if($signature.Status -notin @('Valid','UnknownError') -or $null -eq $signer -or $null -eq $signature.TimeStamperCertificate -or
        $signer.Thumbprint -cnotin (Get-SyncSharkDiscoverySigner $Helper).Thumbprints){throw 'The helper is not timestamp-signed by a registered publisher key.'}
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try { $workerFile.Position=0; $new=([BitConverter]::ToString($algorithm.ComputeHash($workerFile))).Replace('-','').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
    $sha={param($p)(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()}
    $text=[IO.File]::ReadAllText($manifest)
    $field=[regex]'("workerSha256"\s*:\s*")([0-9a-f]{64})"'
    $match=$field.Match($text)
    if(-not $match.Success -or $match.Groups[2].Value -cne (& $sha $worker)){throw 'The installed helper and manifest do not match; not changing them.'}
    $updated=$field.Replace($text,'${1}'+$new+'"',1)
    Stop-ScheduledTask -TaskName 'SyncSharkDiscovery' -ErrorAction SilentlyContinue
    $temp=Join-Path $root ('SyncSharkDiscovery.exe.'+[IO.Path]::GetRandomFileName()+'.tmp')
    try {
        $copy=[IO.File]::Open($temp,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $workerFile.Position=0; $workerFile.CopyTo($copy) } finally { $copy.Dispose() }
        [IO.File]::WriteAllBytes("$worker.previous",[IO.File]::ReadAllBytes($worker))
        [IO.File]::WriteAllBytes("$manifest.previous",[IO.File]::ReadAllBytes($manifest))
        [IO.File]::Replace($temp,$worker,[NullString]::Value)  # PowerShell turns $null into "" here
        [IO.File]::WriteAllText($manifest,$updated,[Text.UTF8Encoding]::new($false))
    }
    finally { if(Test-Path -LiteralPath $temp){[IO.File]::Delete($temp)} }
    if((& $sha $worker) -cne $new){
        [IO.File]::WriteAllBytes($worker,[IO.File]::ReadAllBytes("$worker.previous"))
        [IO.File]::WriteAllBytes($manifest,[IO.File]::ReadAllBytes("$manifest.previous"))
        throw 'The helper copy did not verify; the .previous files were restored.'
    }
    "Installed dev Discovery helper $new"
}
finally { $workerFile.Dispose() }
