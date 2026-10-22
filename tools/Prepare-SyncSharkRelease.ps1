param(
    [Parameter(Mandatory=$true)][ValidateSet('ShellAssets','Stage','IdentityPackage','SignedScripts','PublicArchive')][string]$Step,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory=$true)][string]$BuildRoot,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$PreviousProductVersion,
    [Parameter(Mandatory=$true)][string]$PreviousTreeSha256,
    [string]$ProtectedUpdaterFile='',
    [string]$PredecessorReleaseRoot='',
    [string]$ProofSuccessorVersion='',
    [string]$Allocation=''
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=Split-Path -Parent $PSScriptRoot
$build=[IO.Path]::GetFullPath($BuildRoot)
$allocation=if([string]::IsNullOrWhiteSpace($Allocation)){Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SyncShark\build'}else{[IO.Path]::GetFullPath($Allocation).TrimEnd('\')}
# Regenerate every derived copy of the signer identity from the canonical JSON
# before any staging write, so the module, the embedded Discovery script and the
# managed constants cannot drift from tools/SyncShark.SignerIdentity.json.
& (Join-Path $PSScriptRoot 'New-SyncSharkSignerIdentity.ps1') -JsonPath (Join-Path $PSScriptRoot 'SyncShark.SignerIdentity.json') -ModulePath (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -DiscoverySetupPath (Join-Path $PSScriptRoot 'SyncShark.DiscoverySetup.ps1') -ManagedOutput (Join-Path $PSScriptRoot 'SyncShark.SignerIdentity.cs')
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force
. (Join-Path $PSScriptRoot 'SyncShark.ReleaseMetadata.ps1')
$metadata=New-SyncSharkReleaseMetadata $Version $SourceCommit $PreviousProductVersion $PreviousTreeSha256
Assert-SyncSharkWithin $allocation $build
Assert-SyncSharkOrdinaryTree $build -RootOnly
if ((git -C $repo rev-parse HEAD).Trim() -cne $SourceCommit) { throw 'Source commit changed.' }
git -C $repo diff --quiet HEAD -- Directory.Build.props README.md apps src tests tools
if ($LASTEXITCODE -ne 0) { throw 'Release source is not frozen.' }
if (@(git -C $repo ls-files --others --exclude-standard -- apps src tests tools).Count) { throw 'Uncommitted release source exists.' }
$version=$metadata.Version
$expectedVersion=$version+'+'+$SourceCommit
$release=Join-Path $build ('release-'+$version)
$package=Join-Path $release 'package\signed-candidate'
$manifestPath=Join-Path $release 'package\unsigned-candidate-framework-manifest.json'
$deploy=Join-Path $release 'deploy-scripts'
$publish=Join-Path $build 'publish'
$shellResources=Join-Path $build 'shell-assets'
$signerIdentity=Get-SyncSharkSignerIdentity
$publisher=$signerIdentity.roles.publisher
$thumb=@($publisher.thumbprints)
$subject=$publisher.subject
$spki=@($publisher.spki)
$encoding=[Text.UTF8Encoding]::new($false)
$managed=@('SyncShark.Core.dll','SyncShark.dll','SyncShark.exe')
$signable=@($managed)+@('SyncShark.Explorer.dll','SyncSharkDiscovery.exe','SyncShark.msix')

function Get-ReleasePredecessor {
    $preparedPredecessor=$null
    if($PredecessorReleaseRoot) {
        $predecessorRelease=[IO.Path]::GetFullPath($PredecessorReleaseRoot)
        Assert-SyncSharkWithin $allocation $predecessorRelease;Assert-SyncSharkOrdinaryTree $predecessorRelease -RootOnly
        $previous=Join-Path $predecessorRelease 'package\signed-candidate';Assert-SyncSharkOrdinaryTree $previous
        $previousInstaller=Join-Path $predecessorRelease 'deploy-scripts\Install-SyncShark.ps1'
        Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $previousInstaller) $thumb $subject $subject $spki
        $reader=[IO.File]::OpenText($previousInstaller)
        try{$line=$reader.ReadLine()}finally{$reader.Dispose()}
        if(-not $line.StartsWith('# SyncSharkUpdateContract: ',[StringComparison]::Ordinal) -or $line.Length -gt 16384){throw 'Prepared predecessor contract is missing or oversized.'}
        $preparedPredecessor=$line.Substring(('# SyncSharkUpdateContract: ').Length)|ConvertFrom-Json
        if($preparedPredecessor.product -cne 'Massdex.SyncShark' -or
            ($preparedPredecessor.version+'+'+$preparedPredecessor.sourceRevision) -cne $metadata.PreviousVersion){throw 'Prepared predecessor identity changed.'}
        foreach($member in $preparedPredecessor.runtimeMembers) {
            $path=Join-Path $previous $member.path;Assert-SyncSharkWithin $previous $path
            if((Get-Item -LiteralPath $path).Length -ne $member.bytes -or (Get-SyncSharkSha256 $path) -cne $member.sha256){throw 'Prepared predecessor members differ from the signed contract.'}
        }
    } else {
        $previous=Resolve-SyncSharkRuntimeRoot $metadata.PreviousVersion.Split('+')[0]
        Assert-SyncSharkProtectedRuntime $previous
    }
    if([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $previous 'SyncShark.exe')).ProductVersion -cne $metadata.PreviousVersion){throw 'Updater predecessor revision changed.'}
    if((Get-SyncSharkTreeIdentity $previous) -cne $metadata.PreviousTreeSha256){throw 'Predecessor tree differs from the release metadata.'}
    return [pscustomobject]@{Root=$previous;Contract=$preparedPredecessor}
}

function Assert-ShellAssetInputs {
    Add-Type -Path (Join-Path $PSScriptRoot 'SyncShark.IdentityPackage.cs')
    $sourceAssets=Join-Path $repo 'apps\SyncShark\Assets'
    [void][SyncSharkIdentityPackage]::ValidateShellResources($shellResources,(Join-Path $sourceAssets 'SyncShark-64.png'),$BuildRoot)
    foreach($name in [SyncSharkIdentityPackage]::ShellAssetNames()) {
        if($name -notmatch 'targetsize-([0-9]+)'){throw 'Unexpected shell asset name.'}
        if((Get-SyncSharkSha256 (Join-Path $sourceAssets ('SyncShark-'+$Matches[1]+'.png'))) -cne
           (Get-SyncSharkSha256 (Join-Path $shellResources $name))){throw 'Indexed shell artwork differs from frozen source.'}
    }
    if([IO.File]::ReadAllText((Join-Path $shellResources 'AppxManifest.xml')) -cne
       [SyncSharkIdentityPackage]::Manifest($version+'.0')){throw 'Shell index staging manifest differs from the release identity.'}
}

function Assert-IdentityResourceContinuity([string]$Original,[string]$Prepared) {
    $before=[IO.Compression.ZipFile]::OpenRead($Original)
    try {
        $after=[IO.Compression.ZipFile]::OpenRead($Prepared)
        try {
            $names=@('AppxManifest.xml','Assets/SyncShark-64.png','resources.pri')+
                @([SyncSharkIdentityPackage]::ShellAssetNames() | ForEach-Object {$_.Replace('\','/')})
            foreach($name in $names) {
                $left=$before.GetEntry($name);$right=$after.GetEntry($name)
                if($null -eq $left -or $null -eq $right -or $left.Length -ne $right.Length){throw ('Identity resource changed during repack: '+$name)}
                $a=$left.Open();$b=$right.Open();$hash=[Security.Cryptography.SHA256]::Create()
                try {
                    if([Convert]::ToBase64String($hash.ComputeHash($a)) -cne [Convert]::ToBase64String($hash.ComputeHash($b))){throw ('Identity resource changed during repack: '+$name)}
                } finally {$hash.Dispose();$b.Dispose();$a.Dispose()}
            }
        } finally {$after.Dispose()}
    } finally {$before.Dispose()}
}

if ($Step -eq 'ShellAssets') {
    if(Test-Path -LiteralPath $shellResources){throw 'Shell asset staging already exists; reconcile it rather than overwriting it.'}
    Add-Type -Path (Join-Path $PSScriptRoot 'SyncShark.IdentityPackage.cs')
    $assetOutput=Join-Path $shellResources 'Assets'
    [void][IO.Directory]::CreateDirectory($assetOutput)
    $sourceAssets=Join-Path $repo 'apps\SyncShark\Assets'
    Copy-Item -LiteralPath (Join-Path $sourceAssets 'SyncShark-64.png') -Destination (Join-Path $assetOutput 'SyncShark-64.png')
    foreach($name in [SyncSharkIdentityPackage]::ShellAssetNames()) {
        if($name -notmatch 'targetsize-([0-9]+)'){throw 'Unexpected shell asset name.'}
        $source=Join-Path $sourceAssets ('SyncShark-'+$Matches[1]+'.png')
        $destination=Join-Path $shellResources $name
        Copy-Item -LiteralPath $source -Destination $destination
        if((Get-SyncSharkSha256 $source) -cne (Get-SyncSharkSha256 $destination)){throw 'Shell asset staging mismatch.'}
    }
    [IO.File]::WriteAllText((Join-Path $shellResources 'AppxManifest.xml'),[SyncSharkIdentityPackage]::Manifest($version+'.0'),$encoding)
    # No external executable is selected or launched here. The protected shared
    # tool owner must approve/provide MakePri before its real index is generated.
    [ordered]@{step=$Step;source_commit=$SourceCommit;resource_root=$shellResources;index_ready=$false;next='Generate and verify resources.pri with the approved registered MakePri tool before Stage.'}|ConvertTo-Json -Compress
    exit
}

if ($Step -eq 'Stage') {
    # Exercise the real elevated contract predicate against this release's two
    # predecessor pins before producing signing inputs. A stale embedded pin must
    # fail here, not at the administrator prompt.
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'Test-SyncSharkProtectedRuntimeContract.ps1') -BuildRoot $build -ReleaseVersion $version -PreviousProductVersion $metadata.PreviousVersion -Allocation $allocation
    if($LASTEXITCODE -ne 0){throw 'Protected helper and release predecessor contracts disagree.'}
    # Fail before any release staging writes if the approved real index/artwork
    # is absent. This checks input binding, not PRI semantic resolution.
    Assert-ShellAssetInputs
    # Bind both exact rollback pins before producing signing inputs. A version-only
    # bump must not leave the previous release's tree fingerprint in either script.
    $previousPins=@(foreach($templateName in @('Install-SyncShark.ps1.template','Rollback-SyncShark.ps1.template')) {
        $tokens=$null;$parseErrors=$null
        $templateContract=@{product='Massdex.SyncShark';version=$version;sourceRevision=$SourceCommit;previousVersion=$metadata.PreviousVersion}|ConvertTo-Json -Compress
        $rendered=Expand-SyncSharkReleaseTemplate ([IO.File]::ReadAllText((Join-Path $PSScriptRoot $templateName))) $metadata $templateContract
        $ast=[Management.Automation.Language.Parser]::ParseInput($rendered,[ref]$tokens,[ref]$parseErrors)
        if($parseErrors.Count){throw 'Release template parsing failed.'}
        $values=@{}
        foreach($variable in @('expectedPreviousVersion','expectedPreviousTreeIdentity')) {
            $assignments=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left -is [Management.Automation.Language.VariableExpressionAst] -and $n.Left.VariablePath.UserPath -ceq $variable},$true))
            if($assignments.Count -ne 1){throw 'The predecessor pin must have exactly one literal assignment.'}
            $right=$assignments[0].Right
            if($right -isnot [Management.Automation.Language.CommandExpressionAst] -or $right.Expression -isnot [Management.Automation.Language.StringConstantExpressionAst]){throw 'The predecessor pin is not a literal string.'}
            $values[$variable]=[string]$right.Expression.Value
        }
        [pscustomobject]$values
    })
    if($previousPins[0].expectedPreviousVersion -cne $previousPins[1].expectedPreviousVersion -or
       $previousPins[0].expectedPreviousTreeIdentity -cne $previousPins[1].expectedPreviousTreeIdentity){throw 'Installer and rollback predecessor pins differ.'}
    $previousInput=Get-ReleasePredecessor;$previousRuntime=$previousInput.Root
    Assert-SyncSharkOrdinaryTree $publish
    Assert-SyncSharkOrdinaryTree $release -AllowMissing
    if (Test-Path -LiteralPath $release) { throw 'Release staging already exists; reconcile its exact state before retry.' }
    foreach ($name in ($managed+@('SyncSharkDiscovery.exe'))) {
        if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish $name)).ProductVersion -cne $expectedVersion) {
            throw ('Published version/source mismatch: '+$name)
        }
    }
    $members=@('e_sqlite3.dll','Microsoft.Data.Sqlite.dll','Microsoft.Windows.SDK.NET.dll',
        'SQLitePCLRaw.batteries_v2.dll','SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.e_sqlite3.dll',
        'SyncShark.Core.dll','SyncShark.deps.json','SyncShark.dll','SyncShark.exe',
        'SyncShark.runtimeconfig.json','WinRT.Runtime.dll','SyncShark.Explorer.dll','SyncSharkDiscovery.exe','Assets/SyncShark-64.png')
    $inputs=@{}
    foreach ($name in $members) {
        $path=Join-Path $publish $name
        if($name -ceq 'Assets/SyncShark-64.png'){$path=Join-Path $repo 'apps\SyncShark\Assets\SyncShark-64.png'}
        if ($name -eq 'e_sqlite3.dll' -and !(Test-Path -LiteralPath $path)) {
            $path=Join-Path $publish 'runtimes\win-x64\native\e_sqlite3.dll'
        }
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Missing published member: '+$name) }
        $inputs[$name]=@{path=$path;sha256=(Get-SyncSharkSha256 $path)}
    }
    [void][IO.Directory]::CreateDirectory($package)
    [void][IO.Directory]::CreateDirectory($deploy)
    foreach ($name in $members) {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName((Join-Path $package $name)))
        Copy-Item -LiteralPath $inputs[$name].path -Destination (Join-Path $package $name)
        if ((Get-SyncSharkSha256 (Join-Path $package $name)) -cne $inputs[$name].sha256) { throw 'Staging copy mismatch.' }
    }
    Add-Type -Path (Join-Path $PSScriptRoot 'SyncShark.IdentityPackage.cs')
    [SyncSharkIdentityPackage]::Create((Join-Path $package 'SyncShark.msix'),(Join-Path $repo 'apps\SyncShark\Assets\SyncShark-64.png'),(Join-Path $package 'SyncShark.Explorer.dll'),($version+'.0'),$shellResources,$BuildRoot)
    $members+=@('SyncShark.msix')
    $rows=@(foreach ($name in ($members | Sort-Object)) {
        $path=Join-Path $package $name
        [ordered]@{path=$name;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-SyncSharkSha256 $path)}
    })
    [IO.File]::WriteAllText($manifestPath,([ordered]@{schema=1;product='Sync Shark';version=$version;source_commit=$SourceCommit;members=$rows}|ConvertTo-Json -Depth 6),$encoding)
    [ordered]@{step=$Step;release=$release;source_commit=$SourceCommit;manifest_sha256=(Get-SyncSharkSha256 $manifestPath);signable=$signable;members=$rows}|ConvertTo-Json -Depth 6
    exit
}

Assert-SyncSharkOrdinaryTree $release
$manifest=Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.source_commit -cne $SourceCommit -or $manifest.version -cne $version) { throw 'Frozen manifest changed.' }
if($Step -eq 'IdentityPackage') {
    Assert-ShellAssetInputs
    # Sign the changed Explorer DLL first, then rebuild the still-unsigned
    # container around those exact final bytes. The MSIX retains its own signer.
    # The original unsigned manifest remains an immutable source-input record;
    # the final signed contract binds both post-signing member hashes separately.
    $identity=Join-Path $package 'SyncShark.msix'
    $original=@($manifest.members|Where-Object path -CEQ 'SyncShark.msix')
    if($original.Count -ne 1 -or (Get-SyncSharkSha256 $identity) -cne $original[0].sha256 -or
        (Get-AuthenticodeSignature -LiteralPath $identity).Status -ne 'NotSigned') {
        throw 'Identity packaging requires the exact original unsigned staging member; never overwrite a signed or already prepared container.'
    }
    $explorer=Join-Path $package 'SyncShark.Explorer.dll'
    $heldExplorer=[IO.File]::Open($explorer,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $prepared=Join-Path $package 'SyncShark.prepared.msix'
    if(Test-Path -LiteralPath $prepared){$heldExplorer.Dispose();throw 'Prepared identity output already exists; reconcile it instead of repeating packaging.'}
    try {
        Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $explorer) $thumb $subject $subject $spki
        $explorerHash=Get-SyncSharkSha256 $explorer
        Add-Type -Path (Join-Path $PSScriptRoot 'SyncShark.IdentityPackage.cs')
        [SyncSharkIdentityPackage]::Create($prepared,(Join-Path $repo 'apps\SyncShark\Assets\SyncShark-64.png'),$explorer,($version+'.0'),$shellResources,$BuildRoot)
        $zip=[IO.Compression.ZipFile]::OpenRead($prepared)
        try {
            $entry=$zip.GetEntry('SyncShark.Explorer.dll')
            if($null -eq $entry){throw 'Prepared container has no Explorer payload.'}
            $entryStream=$entry.Open();$algorithm=[Security.Cryptography.SHA256]::Create()
            try {$embeddedHash=([BitConverter]::ToString($algorithm.ComputeHash($entryStream))).Replace('-','').ToLowerInvariant()}
            finally {$algorithm.Dispose();$entryStream.Dispose()}
            if($embeddedHash -cne $explorerHash){throw 'Prepared Explorer payload is not the exact signed executable.'}
        } finally {$zip.Dispose()}
        # Repacking may change only the now-signed Explorer payload and package
        # metadata. Do not silently rebuild against different index/icon bytes.
        Assert-IdentityResourceContinuity $identity $prepared
        if((Get-SyncSharkSha256 $identity) -cne $original[0].sha256){throw 'Original staging container changed during preparation.'}
        Move-Item -LiteralPath $prepared -Destination $identity -Force
        [ordered]@{step=$Step;source_commit=$SourceCommit;explorer_sha256=$explorerHash;container=(Get-SyncSharkSha256 $identity);container_signer=($signerIdentity.roles.package.thumbprints -join ',');signed=$false}|ConvertTo-Json -Compress
    } finally {$heldExplorer.Dispose()}
    exit
}
$signed=@{}
foreach ($name in $signable) { $signed[$name]=Get-SyncSharkSha256 (Join-Path $package $name) }
$manifestHash=Get-SyncSharkSha256 $manifestPath
Assert-SyncSharkExactPackage $package $manifestPath $manifestHash $expectedVersion $signed $thumb $subject $subject $spki '' $false

if ($Step -eq 'SignedScripts') {
    foreach ($name in @('Install-SyncShark.ps1','Rollback-SyncShark.ps1','SyncShark.Release.psm1')) {
        if (Test-Path -LiteralPath (Join-Path $deploy $name)) { throw 'Generated scripts already exist; do not overwrite or repeat signing.' }
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Destination $deploy
    $contract=[ordered]@{
        product='Massdex.SyncShark';version=$version;sourceRevision=$SourceCommit
        previousVersion=$metadata.PreviousVersion
        moduleSha256=(Get-SyncSharkSha256 (Join-Path $deploy 'SyncShark.Release.psm1'));manifestSha256=$manifestHash
        signedCoreSha256=$signed['SyncShark.Core.dll'];signedUiSha256=$signed['SyncShark.dll'];signedExeSha256=$signed['SyncShark.exe']
        signedExplorerSha256=$signed['SyncShark.Explorer.dll'];signedDiscoverySha256=$signed['SyncSharkDiscovery.exe'];signedIdentityPackageSha256=$signed['SyncShark.msix']
        protectedRuntimeRoot=('C:\Program Files\Massdex\SyncShark\versions\'+$version)
        runtimeMembers=@(foreach($member in $manifest.members){$path=Join-Path $package $member.path;
            [ordered]@{path=$member.path;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-SyncSharkSha256 $path)}})
    }
    if($ProtectedUpdaterFile) {
        # Optional future reviewed updater lane. Ordinary release48 generation
        # remains unchanged when no accepted fixed updater is supplied.
        $worker=[IO.Path]::GetFullPath($ProtectedUpdaterFile)
        $item=Get-Item -LiteralPath $worker -Force
        if($item.PSIsContainer -or $item.Length -lt 1 -or $item.Length -gt 16MB -or
            ($item.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline))){throw 'Invalid fixed updater source.'}
        Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $worker) $thumb $subject $subject $spki
        if([Diagnostics.FileVersionInfo]::GetVersionInfo($worker).ProductVersion -cnotmatch '^\d+\.\d+\.\d+\+[0-9a-f]{40}$'){throw 'Updater source must be frozen, not UNBOUND.'}
        $previousInput=Get-ReleasePredecessor;$previous=$previousInput.Root;$preparedPredecessor=$previousInput.Contract
        $discovery=[ordered]@{enabled=$false;beforeWorkerSha256=$null;beforeManifestSha256=$null;afterWorkerSha256=$null;afterManifestSha256=$null;recovery='leave-disabled'}
        if($null -ne $preparedPredecessor) {
            if($null -eq $preparedPredecessor.PSObject.Properties['protectedUpdater']){throw 'Prepared predecessor lacks its signed updater disposition.'}
            $priorDiscovery=$preparedPredecessor.protectedUpdater.discovery
            if($priorDiscovery.enabled) {
                $discovery.enabled=$true;$discovery.recovery='restore-exact-preimage'
                $discovery.beforeWorkerSha256=$priorDiscovery.afterWorkerSha256;$discovery.beforeManifestSha256=$priorDiscovery.afterManifestSha256
                $discovery.afterWorkerSha256=$signed['SyncSharkDiscovery.exe'];$discovery.afterManifestSha256=('0'*64)
            }
        } else {
        $service=New-Object -ComObject 'Schedule.Service';$folder=$null;$task=$null
        try {
            $service.Connect();$folder=$service.GetFolder('\')
            try {$task=$folder.GetTask('SyncSharkDiscovery')}catch{
                $failure=$_.Exception;while($failure.InnerException){$failure=$failure.InnerException}
                if($failure.HResult -ne -2147024894){throw}
            }
            if($null -ne $task -and $task.Enabled) {
                $discovery.enabled=$true;$discovery.recovery='restore-exact-preimage'
                $discovery.beforeWorkerSha256=Get-SyncSharkSha256 'C:\Program Files\Massdex\SyncShark\SyncSharkDiscovery.exe'
                $discovery.beforeManifestSha256=Get-SyncSharkSha256 'C:\Program Files\Massdex\SyncShark\discovery-manifest.json'
                $discovery.afterWorkerSha256=$signed['SyncSharkDiscovery.exe'];$discovery.afterManifestSha256=('0'*64)
            }
        } finally {foreach($com in @($task,$folder,$service)){if($null -ne $com){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($com)}}}
        }
        $contract.protectedUpdater=[ordered]@{schemaVersion=1;userSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;
            previousTreeSha256=(Get-SyncSharkTreeIdentity $previous);workerSha256=(Get-SyncSharkSha256 $worker);workerBytes=$item.Length;discovery=$discovery}
        if($discovery.enabled) {
            $sources=@('SyncShark.DiscoverySetupNative.cs','SyncShark.DiscoveryTransaction.cs','SyncShark.UpdaterContract.cs','SyncShark.UpdaterData.cs','SyncShark.UpdaterManifest.cs')
            Add-Type -Path @($sources|ForEach-Object {Join-Path $PSScriptRoot $_})
            $bytes=[SyncSharkUpdaterManifest]::FromReleaseHeader(('# SyncSharkUpdateContract: '+($contract|ConvertTo-Json -Depth 8 -Compress)),$version,$SourceCommit)
            $discovery.afterManifestSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        }
        Copy-Item -LiteralPath $worker -Destination (Join-Path $deploy 'SyncSharkUpdater.exe')
        if((Get-SyncSharkSha256 (Join-Path $deploy 'SyncSharkUpdater.exe')) -cne $contract.protectedUpdater.workerSha256){throw 'Fixed updater changed during retention.'}
    }
    $proofRestorationJson=''
    if($ProofSuccessorVersion) {
        if(-not $ProtectedUpdaterFile -or $PredecessorReleaseRoot -or -not $contract.protectedUpdater.discovery.enabled){throw 'Only the attended proof release may capture its original enabled Discovery pair.'}
        $null=New-SyncSharkReleaseMetadata $ProofSuccessorVersion $SourceCommit $expectedVersion ('0'*64)
        $originals=@()
        foreach($suffix in @('','.previous')) {
            $originalManifest='C:\Program Files\Massdex\SyncShark\discovery-manifest.json'+$suffix
            $originalWorker='C:\Program Files\Massdex\SyncShark\SyncSharkDiscovery.exe'+$suffix
            $raw=[IO.File]::ReadAllBytes($originalManifest)
            if($raw.Length -lt 1 -or $raw.Length -gt 8192){throw 'Original Discovery manifest exceeds the bounded proof contract.'}
            $saved=[Text.Encoding]::UTF8.GetString($raw)|ConvertFrom-Json
            $root='C:\Program Files\Massdex\SyncShark\versions\'+$saved.version.Split('+')[0]
            if($saved.appDirectory -cne $root -or $saved.userSid -cne $contract.protectedUpdater.userSid -or
                $saved.workerSha256 -cne (Get-SyncSharkSha256 $originalWorker)){throw 'Original Discovery pair identity changed.'}
            Assert-SyncSharkProtectedRuntime $root
            if([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'SyncShark.exe')).ProductVersion -cne $saved.version){throw 'Original runtime version changed.'}
            $originals+=,[ordered]@{manifestBase64=[Convert]::ToBase64String($raw);manifestSha256=(Get-SyncSharkSha256 $originalManifest);treeSha256=(Get-SyncSharkTreeIdentity $root)}
        }
        if($originals[0].manifestSha256 -cne $contract.protectedUpdater.discovery.beforeManifestSha256){throw 'Original Discovery changed during proof capture.'}
        $proofRestorationJson=([ordered]@{schemaVersion=1;successorVersion=$ProofSuccessorVersion;current=$originals[0];previous=$originals[1]}|ConvertTo-Json -Depth 5 -Compress)
    }
    foreach ($name in @('Install-SyncShark.ps1','Rollback-SyncShark.ps1')) {
        $template=[IO.File]::ReadAllText((Join-Path $PSScriptRoot ($name+'.template')))
        $generated=Expand-SyncSharkReleaseTemplate $template $metadata ($contract|ConvertTo-Json -Depth 8 -Compress) $proofRestorationJson
        $path=Join-Path $deploy $name
        [IO.File]::WriteAllText($path,$generated,$encoding)
        $tokens=$null;$errors=$null
        [void][Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
        if ($errors.Count) { throw ('Generated script parse failed: '+$name) }
    }
    $contract|ConvertTo-Json -Depth 5
    exit
}

foreach ($name in @('Install-SyncShark.ps1','Rollback-SyncShark.ps1')) {
    $scriptPath=Join-Path $deploy $name
    Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $scriptPath) $thumb $subject $subject $spki
    if ((Get-Item -LiteralPath $scriptPath).Length -gt 1MB) { throw 'Signed release script exceeds the update admission limit.' }
    $header=Get-Content -LiteralPath $scriptPath -TotalCount 1
    $prefix='# SyncSharkUpdateContract: '
    if (!$header.StartsWith($prefix,[StringComparison]::Ordinal) -or $header.Length -gt 16384) { throw 'Signed release contract is missing or oversized.' }
    $contract=$header.Substring($prefix.Length)|ConvertFrom-Json
    if ($contract.product -cne 'Massdex.SyncShark' -or $contract.version -cne $version -or
        $contract.sourceRevision -cne $SourceCommit -or
        $contract.previousVersion -cne $metadata.PreviousVersion -or
        $contract.manifestSha256 -cne $manifestHash -or
        $contract.moduleSha256 -cne (Get-SyncSharkSha256 (Join-Path $deploy 'SyncShark.Release.psm1'))) {
        throw 'Signed release contract no longer binds this exact package and rollback.'
    }
    $contractHashes=Get-SyncSharkSignedMemberHashes $contract
    if ($contractHashes.Count -ne $signable.Count) { throw 'Signed release contract omits a required signed member.' }
    foreach ($member in $signable) {
        if ($contractHashes[$member] -cne $signed[$member]) { throw 'Signed release contract member hash changed.' }
    }
    if($null -ne $contract.PSObject.Properties['protectedUpdater']) {
        $updater=Join-Path $deploy 'SyncSharkUpdater.exe'
        if((Get-Item -LiteralPath $updater).Length -ne $contract.protectedUpdater.workerBytes -or
            (Get-SyncSharkSha256 $updater) -cne $contract.protectedUpdater.workerSha256){throw 'Signed updater contract differs from the retained worker.'}
        Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $updater) $thumb $subject $subject $spki
    }
}
$archive=Join-Path $release ('SyncShark-v'+$version+'-win-x64.zip')
if (Test-Path -LiteralPath $archive) { throw 'Public archive already exists; verify it rather than overwriting.' }
Add-Type -AssemblyName System.IO.Compression
$stream=[IO.File]::Open($archive,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
    $zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        $sources=@{ 'package/unsigned-candidate-framework-manifest.json'=$manifestPath }
        foreach ($name in @('Install-SyncShark.ps1','Rollback-SyncShark.ps1','SyncShark.Release.psm1')) { $sources['scripts/'+$name]=Join-Path $deploy $name }
        if($null -ne $contract.PSObject.Properties['protectedUpdater']){$sources['scripts/SyncSharkUpdater.exe']=Join-Path $deploy 'SyncSharkUpdater.exe'}
        foreach ($member in $manifest.members) { $sources['package/signed-candidate/'+$member.path]=Join-Path $package $member.path }
        foreach ($name in ($sources.Keys|Sort-Object)) {
            $entry=$zip.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal)
            $destination=$entry.Open();$source=[IO.File]::OpenRead($sources[$name])
            try { $source.CopyTo($destination) } finally { $source.Dispose();$destination.Dispose() }
        }
    } finally { $zip.Dispose() }
    $stream.Flush($true)
} finally { $stream.Dispose() }
[ordered]@{step=$Step;archive=$archive;bytes=(Get-Item -LiteralPath $archive).Length;sha256=(Get-SyncSharkSha256 $archive);installer=(Join-Path $deploy 'Install-SyncShark.ps1')}|ConvertTo-Json
