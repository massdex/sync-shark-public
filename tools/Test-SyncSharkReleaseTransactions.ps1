param(
    [Parameter(Mandatory = $true)][string]$ReleaseRoot,
    [Parameter(Mandatory = $true)][string]$PredecessorRuntime,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [switch]$SmokeOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$releaseRoot=[IO.Path]::GetFullPath($ReleaseRoot); $predecessorRuntime=[IO.Path]::GetFullPath($PredecessorRuntime)
$evidenceDirectory=[IO.Path]::GetFullPath($EvidenceDirectory)
if (-not (Test-Path -LiteralPath $evidenceDirectory)) { New-Item -ItemType Directory -Path $evidenceDirectory | Out-Null }
$testBase=Join-Path ([IO.Path]::GetTempPath()) 'sync-shark-release-tests'
if (-not (Test-Path -LiteralPath $testBase)) { New-Item -ItemType Directory -Path $testBase | Out-Null }
$deploy=Join-Path $releaseRoot 'deploy-scripts'; $package=Join-Path $releaseRoot 'package\signed-candidate'
$manifest=Join-Path $releaseRoot 'package\unsigned-candidate-framework-manifest.json'
$install=Join-Path $deploy 'Install-SyncShark.ps1'; $rollback=Join-Path $deploy 'Rollback-SyncShark.ps1'
$module=Join-Path $deploy 'SyncShark.Release.psm1'
foreach($path in @($install,$rollback,$module,$manifest)){if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "Missing release input: $path"}}
$manifestDocument=Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json
$SourceCommit=[string]$manifestDocument.source_commit
if($SourceCommit -notmatch '^[0-9a-f]{40}$'){throw 'Package manifest source commit is missing or malformed.'}
$candidateVersion=[string]$manifestDocument.version
$predecessorVersion=([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $predecessorRuntime 'SyncShark.exe')).ProductVersion).Split('+')[0]
if($candidateVersion -notmatch '^\d+\.\d+\.\d+$' -or $predecessorVersion -notmatch '^\d+\.\d+\.\d+$'){throw 'Both release versions must be bound.'}
$hashes=[ordered]@{
    ExpectedManifestSha256=(Get-FileHash $manifest -Algorithm SHA256).Hash.ToLowerInvariant()
    ExpectedModuleSha256=(Get-FileHash $module -Algorithm SHA256).Hash.ToLowerInvariant()
    ExpectedSignedCoreSha256=(Get-FileHash (Join-Path $package 'SyncShark.Core.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    ExpectedSignedUiSha256=(Get-FileHash (Join-Path $package 'SyncShark.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    ExpectedSignedExeSha256=(Get-FileHash (Join-Path $package 'SyncShark.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    ExpectedSignedExplorerSha256=(Get-FileHash (Join-Path $package 'SyncShark.Explorer.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    ExpectedSignedDiscoverySha256=(Get-FileHash (Join-Path $package 'SyncSharkDiscovery.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    ExpectedSignedIdentityPackageSha256=(Get-FileHash (Join-Path $package 'SyncShark.msix') -Algorithm SHA256).Hash.ToLowerInvariant()
}
$signerIdentity=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SyncShark.SignerIdentity.json') -Raw | ConvertFrom-Json)
$publisherSigner=$signerIdentity.roles.publisher
$packageSigner=$signerIdentity.roles.package
$pins=[ordered]@{Thumbprint=@($publisherSigner.thumbprints)[0];Subject=$publisherSigner.subject;Issuer=$publisherSigner.subject;SpkiSha256=@($publisherSigner.spki)[0]}
$results=[Collections.Generic.List[object]]::new()
$pwsh=Join-Path $PSHOME 'pwsh.exe'

function Write-Observation([string]$Path,[string]$Status='Valid',[string[]]$SignerStatuses=@(),
    [bool]$Present=$true,[bool]$Crypto=$true,[bool]$Rfc=$true,[bool]$Bound=$true,
    [bool]$TimestampChain=$true,[string[]]$TimestampStatuses=@(),[bool]$AtTokenTime=$true) {
    $all=[ordered]@{}
    foreach($name in @('SyncShark.Core.dll','SyncShark.dll','SyncShark.exe','SyncShark.Explorer.dll','SyncSharkDiscovery.exe','SyncShark.msix')){
        $all[$name]=[ordered]@{Status=$Status;Thumbprint=$pins.Thumbprint;Subject=$pins.Subject;Issuer=$pins.Issuer;SpkiSha256=$pins.SpkiSha256;SignerChainStatuses=$SignerStatuses;Rfc3161Present=$Present;Rfc3161CryptographicallyValid=$Crypto;Rfc3161=$Rfc;TimestampSignatureBound=$Bound;TimestampChainEvaluatedAtTokenTime=$AtTokenTime;TimestampChainValid=$TimestampChain;TimestampChainStatuses=$TimestampStatuses}
        if($name -ceq 'SyncShark.msix') {
            $all[$name].Thumbprint=@($packageSigner.thumbprints)[0]
            $all[$name].Subject=$packageSigner.subject
            $all[$name].Issuer=$all[$name].Subject
        }
    }
    [IO.File]::WriteAllText($Path,($all|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
}

function New-TestRoot([string]$Name) {
    $root=Join-Path $testBase ($Name+'-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $root ('versions\'+$predecessorVersion)),(Join-Path $root 'state'),(Join-Path $root 'vNext\state') -Force | Out-Null
    Get-ChildItem -LiteralPath $predecessorRuntime -Force | Copy-Item -Destination (Join-Path $root ('versions\'+$predecessorVersion)) -Recurse
    [IO.File]::WriteAllText((Join-Path $root 'state\state.txt'),'predecessor-state',[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $root 'vNext\state\state.txt'),'predecessor-vnext-state',[Text.UTF8Encoding]::new($false))
    # The release transaction holds the application's own state lock for the whole
    # install, so the isolated root supplies the same lock file.
    [IO.File]::WriteAllText((Join-Path $root 'state\instance.lock'),'4242',[Text.UTF8Encoding]::new($false))
    $privateStaging=Join-Path $root 'replacement-staging'
    New-Item -ItemType Directory -Path $privateStaging | Out-Null
    $privateAcl=[Security.AccessControl.DirectorySecurity]::new()
    $privateAcl.SetAccessRuleProtection($true,$false)
    $privateAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,'FullControl','ContainerInherit, ObjectInherit','None','Allow'))
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($privateStaging),$privateAcl)
    New-Item -ItemType Junction -Path (Join-Path $root 'active') -Target (Join-Path $root ('versions\'+$predecessorVersion)) | Out-Null
    $observation=Join-Path $testBase ($Name+'-'+[guid]::NewGuid().ToString('N')+'.verification.json')
    Write-Observation $observation
    [pscustomobject]@{Root=$root;Observation=$observation}
}

function Get-InstalledRuntimeManifest([string]$Root) {
    $pending=[Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Root))
    $rows=[Collections.Generic.List[object]]::new()
    while($pending.Count -gt 0){
        $directory=$pending.Pop()
        foreach($item in @(Get-ChildItem -LiteralPath $directory -Force | Sort-Object FullName)){
            # The live process owns mutable state and its exclusive instance lock. Do not
            # stop it or pretend those bytes are an immutable installation snapshot.
            # Fixture runtime AND state are checked separately by Assert-Installed/RolledBack.
            if ($directory -ceq [IO.Path]::GetFullPath($Root) -and $item.Name -cnotin @('versions','active')) { continue }
            $relative=[IO.Path]::GetRelativePath($Root,$item.FullName).Replace('\','/')
            if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){
                $rows.Add([ordered]@{path=$relative;kind='junction';target=[string]$item.Target})
            }
            elseif($item.PSIsContainer){
                $rows.Add([ordered]@{path=$relative;kind='directory'})
                $pending.Push($item.FullName)
            }
            else{
                try{
                    $rows.Add([ordered]@{path=$relative;kind='file';length=$item.Length;sha256=(Get-FileHash $item.FullName -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()})
                }
                catch [System.UnauthorizedAccessException] {
                    # Some deliberately ACL-protected legacy recovery sentinels are unreadable.
                    # Stable identity for those entries is still bound by exact path, size,
                    # attributes and last-write ticks before and after this isolated test.
                    $rows.Add([ordered]@{path=$relative;kind='unreadable-file';length=$item.Length;attributes=[int64]$item.Attributes;last_write_utc_ticks=$item.LastWriteTimeUtc.Ticks})
                }
            }
        }
    }
    $rows=@($rows | Sort-Object {[string]$_.path})
    $rows|ConvertTo-Json -Depth 8 -Compress
}

function Invoke-Exact([string]$Script,[string]$Root,[string]$Observation,[string]$CrashPoint='') {
    $arguments=@('-NoLogo','-NoProfile','-File',$Script)
    foreach($key in $hashes.Keys){$arguments+=('-'+$key);$arguments+=$hashes[$key]}
    $arguments+=@('-TestProductRoot',$Root,'-VerificationObservationPath',$Observation)
    if($CrashPoint){$arguments+=@('-CrashAfter',$CrashPoint)}
    $start=[Diagnostics.ProcessStartInfo]::new($pwsh); foreach($item in $arguments){$start.ArgumentList.Add([string]$item)}
    $start.UseShellExecute=$false;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.Environment['SYNC_SHARK_RELEASE_TEST_MODE']='1'
    $process=[Diagnostics.Process]::Start($start)
    $stdoutTask=$process.StandardOutput.ReadToEndAsync();$stderrTask=$process.StandardError.ReadToEndAsync()
    if(-not $process.WaitForExit(60000)){try{$process.Kill($true)}catch{};throw "Release child timed out: $CrashPoint"}
    [pscustomobject]@{ExitCode=$process.ExitCode;Stdout=$stdoutTask.GetAwaiter().GetResult();Stderr=$stderrTask.GetAwaiter().GetResult()}
}

function Assert-Installed([string]$Root) {
    $active=Get-Item -LiteralPath (Join-Path $Root 'active') -Force
    if($active.LinkType -cne 'Junction' -or [IO.Path]::GetFullPath([string]$active.Target) -cne [IO.Path]::GetFullPath((Join-Path $Root ('versions\'+$candidateVersion)))){throw 'Successor is not exactly active.'}
    if((Get-Content -LiteralPath (Join-Path $Root 'state\state.txt') -Raw) -cne 'predecessor-state'){throw 'Install changed live predecessor state.'}
}
function Assert-RolledBack([string]$Root) {
    $active=Get-Item -LiteralPath (Join-Path $Root 'active') -Force
    if($active.LinkType -cne 'Junction' -or [IO.Path]::GetFullPath([string]$active.Target) -cne [IO.Path]::GetFullPath((Join-Path $Root ('versions\'+$predecessorVersion)))){throw 'Predecessor is not exactly active.'}
    if((Get-Content -LiteralPath (Join-Path $Root 'state\state.txt') -Raw) -cne 'predecessor-state' -or
       (Get-Content -LiteralPath (Join-Path $Root 'vNext\state\state.txt') -Raw) -cne 'predecessor-vnext-state'){throw 'Predecessor state was not restored.'}
    $transactions=@(Get-ChildItem -LiteralPath (Join-Path $Root 'replacement-staging\transactions') -Directory)
    if($transactions.Count -ne 1){throw "Expected exactly one release transaction; observed $($transactions.Count)."}
    $transaction=$transactions[0]
    if((Get-Content -LiteralPath (Join-Path $transaction.FullName 'state.successor\state\state.txt') -Raw) -cne 'successor-state' -or
       (Get-Content -LiteralPath (Join-Path $transaction.FullName 'state.successor\vNext\state\state.txt') -Raw) -cne 'successor-vnext-state'){throw 'Successor state was not preserved.'}
}

function Remove-TestFixture([object]$Fixture) {
    $root = [IO.Path]::GetFullPath([string]$Fixture.Root)
    $base = [IO.Path]::GetFullPath($testBase).TrimEnd('\') + '\'
    if (-not $root.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { throw "Fixture escaped test base: $root" }
    if (Test-Path -LiteralPath $root) {
        foreach ($link in @(Get-ChildItem -LiteralPath $root -Force -Recurse |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
            Sort-Object { $_.FullName.Length } -Descending)) {
            Remove-Item -LiteralPath $link.FullName -Force
        }
        Remove-Item -LiteralPath $root -Recurse -Force
    }
    if (Test-Path -LiteralPath $Fixture.Observation) { Remove-Item -LiteralPath $Fixture.Observation -Force }
}

$realRoot=Join-Path $env:LOCALAPPDATA 'Massdex\SyncShark'
$realBefore=Get-InstalledRuntimeManifest $realRoot
$installSteps=@('stage-package','publish-runtime','backup-state','create-next-link','retain-predecessor','publish-successor','verify-complete')
$installPoints=[Collections.Generic.List[string]]::new()
@('journal-created','journal-created-mid-write','journal-created-after-temp-write') | ForEach-Object { $installPoints.Add($_) }
foreach($step in $installSteps){
    foreach($suffix in @('after-pending','after-effect','after-journal','pending-journal-mid-write','pending-journal-after-temp-write','complete-journal-mid-write','complete-journal-after-temp-write')){$installPoints.Add("$step-$suffix")}
}
@('stage-package-mid-copy','backup-state-mid-copy','backup-vnext-state-mid-copy') | ForEach-Object { $installPoints.Add($_) }
if ($SmokeOnly) { $installPoints.Clear(); $installPoints.Add('stage-package-mid-copy'); $installPoints.Add('publish-runtime-after-move'); $installPoints.Add('publish-runtime-after-effect') }
foreach($point in $installPoints){
    $fixture=New-TestRoot ('install-'+$point.Replace(':','-'))
    $first=Invoke-Exact $install $fixture.Root $fixture.Observation $point
    if($first.ExitCode -eq 0){throw "Installer crash point did not terminate: $point"}
    $replay=Invoke-Exact $install $fixture.Root $fixture.Observation
    if($replay.ExitCode -ne 0){throw "Installer replay failed at $point`: $($replay.Stderr)"}
    Assert-Installed $fixture.Root
    $again=Invoke-Exact $install $fixture.Root $fixture.Observation
    if($again.ExitCode -ne 0){throw "Installer terminal replay failed at $point`: $($again.Stderr)"}
    $results.Add([ordered]@{case="install/$point";result='pass';terminal_replay='pass'})
    Remove-TestFixture $fixture
}

$rollbackSteps=@('preserve-state','preserve-vnext-state','restore-state','restore-vnext-state','retain-successor','publish-predecessor','verify-complete')
$rollbackPoints=[Collections.Generic.List[string]]::new()
@('journal-created','journal-created-mid-write','journal-created-after-temp-write') | ForEach-Object { $rollbackPoints.Add($_) }
foreach($step in $rollbackSteps){
    foreach($suffix in @('after-pending','after-effect','after-journal','pending-journal-mid-write','pending-journal-after-temp-write','complete-journal-mid-write','complete-journal-after-temp-write')){$rollbackPoints.Add("$step-$suffix")}
}
@('restore-state-mid-copy','restore-vnext-state-mid-copy') | ForEach-Object { $rollbackPoints.Add($_) }
if ($SmokeOnly) { $rollbackPoints.Clear(); $rollbackPoints.Add('preserve-state-after-effect') }
foreach($point in $rollbackPoints){
    $fixture=New-TestRoot ('rollback-'+$point.Replace(':','-'))
    $installed=Invoke-Exact $install $fixture.Root $fixture.Observation
    if($installed.ExitCode -ne 0){throw "Rollback fixture install failed: $($installed.Stderr)"}
    [IO.File]::WriteAllText((Join-Path $fixture.Root 'state\state.txt'),'successor-state',[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $fixture.Root 'vNext\state\state.txt'),'successor-vnext-state',[Text.UTF8Encoding]::new($false))
    $first=Invoke-Exact $rollback $fixture.Root $fixture.Observation $point
    if($first.ExitCode -eq 0){throw "Rollback crash point did not terminate: $point"}
    $replay=Invoke-Exact $rollback $fixture.Root $fixture.Observation
    if($replay.ExitCode -ne 0){throw "Rollback replay failed at $point`: $($replay.Stderr)"}
    Assert-RolledBack $fixture.Root
    $again=Invoke-Exact $rollback $fixture.Root $fixture.Observation
    if($again.ExitCode -ne 0){throw "Rollback terminal replay failed at $point`: $($again.Stderr)"}
    $results.Add([ordered]@{case="rollback/$point";result='pass';terminal_replay='pass'})
    Remove-TestFixture $fixture
}
if((Get-InstalledRuntimeManifest $realRoot) -cne $realBefore){throw 'The installed Sync Shark runtime or active pointer changed during isolated transaction tests.'}
$receipt=[ordered]@{result='PASS';source_commit=$SourceCommit;entry_point='exact packaged install and rollback scripts in isolated child processes';command_shape='pwsh -NoLogo -NoProfile -File <exact-script> <eight exact hashes> -TestProductRoot <fixed-temp-child> -VerificationObservationPath <fixture> [-CrashAfter <point>]';fixture_identity=[ordered]@{predecessor_runtime=$predecessorRuntime;predecessor_product_version=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $predecessorRuntime 'SyncShark.exe')).ProductVersion;package_manifest=$manifest;package_manifest_sha256=$hashes.ExpectedManifestSha256;signed_member_hashes=[ordered]@{core=$hashes.ExpectedSignedCoreSha256;ui=$hashes.ExpectedSignedUiSha256;exe=$hashes.ExpectedSignedExeSha256;explorer=$hashes.ExpectedSignedExplorerSha256;discovery=$hashes.ExpectedSignedDiscoverySha256;identity_package=$hashes.ExpectedSignedIdentityPackageSha256}};oracle='each hard crash must be nonzero; immediate replay and terminal replay must succeed; exact active runtime/state assertions must pass; registered installed root manifest must remain identical';install_script=$install;install_sha256=(Get-FileHash $install -Algorithm SHA256).Hash.ToLowerInvariant();rollback_script=$rollback;rollback_sha256=(Get-FileHash $rollback -Algorithm SHA256).Hash.ToLowerInvariant();module_sha256=$hashes.ExpectedModuleSha256;real_installed_root_unchanged=$true;case_count=$results.Count;cases=$results}
$receipt['scope'] = if ($SmokeOnly) { 'stage-copy/publish-runtime/rollback smoke; not the full crash matrix' } else { 'full-crash-matrix' }
$receipt.Remove('real_installed_root_unchanged')
$receipt['real_installed_runtime_unchanged'] = $true
$receipt['oracle'] = 'Each hard crash is nonzero; immediate and terminal replay succeed; isolated active runtime/state assertions pass; production versioned runtimes and active pointer remain identical. Live mutable production state is not snapshotted.'
$receipt['test_harness_sha256'] = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
$receiptName = if ($SmokeOnly) { 'transaction-smoke.json' } else { 'transaction-crash-matrix.json' }
$receiptPath=Join-Path $evidenceDirectory $receiptName;[IO.File]::WriteAllText($receiptPath,($receipt|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false));$receipt|ConvertTo-Json -Depth 4
