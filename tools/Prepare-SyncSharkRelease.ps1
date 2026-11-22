param(
    [Parameter(Mandatory=$true)][ValidateSet('Stage','Setup','SignedScripts','PublicArchive')][string]$Step,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory=$true)][string]$BuildRoot,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$PreviousProductVersion,
    [Parameter(Mandatory=$true)][string]$PreviousTreeSha256,
    [string]$PredecessorReleaseRoot='',
    [string]$PredecessorRuntime='',
    [string]$Allocation=''
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=Split-Path -Parent $PSScriptRoot
$build=[IO.Path]::GetFullPath($BuildRoot)
$allocation=if([string]::IsNullOrWhiteSpace($Allocation)){Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SyncShark\build'}else{[IO.Path]::GetFullPath($Allocation).TrimEnd('\')}
& (Join-Path $PSScriptRoot 'New-SyncSharkSignerIdentity.ps1') -JsonPath (Join-Path $PSScriptRoot 'SyncShark.SignerIdentity.json') -ModulePath (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -ManagedOutput (Join-Path $PSScriptRoot 'SyncShark.SignerIdentity.cs')
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
$signerIdentity=Get-SyncSharkSignerIdentity
$publisher=$signerIdentity.roles.publisher
$thumb=@($publisher.thumbprints)
$subject=$publisher.subject
$spki=@($publisher.spki)
$encoding=[Text.UTF8Encoding]::new($false)
# Read the frozen build's product layout once, including the paths supplied by the house builder.
Assert-SyncSharkOrdinaryTree $publish
$engine = Join-Path $publish 'SyncShark.Engine.dll'
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($engine).ProductVersion -cne $expectedVersion) { throw 'Published engine revision mismatch.' }
$names = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($engine)).GetType('Massdex.SyncShark.Engine.ResourceNames',$true)
$installedFiles = @($names::InstalledFiles)
$signable = @($names::HostExecutable,$names::CliExecutable)
$runtimeFiles = @($installedFiles | Where-Object { !$_.StartsWith('tools/',[StringComparison]::Ordinal) })
$setupName = "SyncShark-Setup-v$version.exe"
$setup = Join-Path $deploy $setupName
function Get-ReleasePredecessor {
    $preparedPredecessor=$null
    if($PredecessorReleaseRoot) {
        $predecessorRelease=[IO.Path]::GetFullPath($PredecessorReleaseRoot)
        Assert-SyncSharkWithin $allocation $predecessorRelease;Assert-SyncSharkOrdinaryTree $predecessorRelease -RootOnly
        $previous=Join-Path $predecessorRelease 'house-stage/app'
        if (![IO.Directory]::Exists($previous)) { $previous=Join-Path $predecessorRelease 'package/signed-candidate' }
        Assert-SyncSharkOrdinaryTree $previous
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
        $previous=if ($PredecessorRuntime) { [IO.Path]::GetFullPath($PredecessorRuntime) } else { Resolve-SyncSharkRuntimeRoot $metadata.PreviousReleaseVersion }
        Assert-SyncSharkOrdinaryTree $previous
    }
    $previousHost = Join-Path $previous $signable[0]
    if (-not [IO.File]::Exists($previousHost)) { $previousHost=Join-Path $previous 'SyncShark.exe' }
    if([Diagnostics.FileVersionInfo]::GetVersionInfo($previousHost).ProductVersion -cne $metadata.PreviousVersion){throw 'Updater predecessor revision changed.'}
    if((Get-SyncSharkTreeIdentity $previous) -cne $metadata.PreviousTreeSha256){throw 'Predecessor tree differs from the release metadata.'}
    return [pscustomobject]@{Root=$previous;Contract=$preparedPredecessor}
}

if ($Step -eq 'Stage') {
    $previousPins=@(foreach($templateName in @('Install-SyncShark.ps1.template')) {
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
    $null=Get-ReleasePredecessor
    if ($previousPins[0].expectedPreviousVersion -cne $metadata.PreviousVersion -or $previousPins[0].expectedPreviousTreeIdentity -cne $metadata.PreviousTreeSha256) { throw 'Incorrect installer predecessor pins.' }
    Assert-SyncSharkOrdinaryTree $publish
    Assert-SyncSharkOrdinaryTree $release -AllowMissing
    if (Test-Path -LiteralPath $release) { throw 'Release staging already exists; reconcile its exact state before retry.' }
    foreach ($name in $signable) {
        if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish $name)).ProductVersion -cne $expectedVersion) {
            throw ('Published version/source mismatch: '+$name)
        }
    }
    $members = $runtimeFiles
    $published = @(Get-ChildItem -LiteralPath $publish -File -Recurse -Force | Where-Object Extension -ine '.pdb' | ForEach-Object {
        [IO.Path]::GetRelativePath($publish,$_.FullName).Replace([IO.Path]::DirectorySeparatorChar,'/')
    })
    if (Compare-Object $members $published -CaseSensitive) { throw 'Published runtime differs from ResourceNames.InstalledFiles.' }
    $inputs=@{}
    foreach ($name in $members) {
        $path=Join-Path $publish $name
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
$signed=@{}
foreach ($name in $signable) { $signed[$name]=Get-SyncSharkSha256 (Join-Path $package $name) }
$manifestHash=Get-SyncSharkSha256 $manifestPath
Assert-SyncSharkExactPackage $package $manifestPath $manifestHash $expectedVersion $signed $thumb $subject $subject $spki '' $false

$stageOutput = Join-Path $release 'house-stage'
$payloadRoot = Join-Path $stageOutput 'app'
$payloadManifest = Join-Path $release 'package/payload-manifest.json'
if ($Step -eq 'Setup') {
    & (Join-Path $PSScriptRoot 'Build-SyncSharkSetup.ps1') -PublishedApp $package -OutputDirectory $stageOutput -StageOnly
    Assert-SyncSharkOrdinaryTree $payloadRoot
    $rows = @(Get-SyncSharkTreeManifest $payloadRoot)
    if (Compare-Object $installedFiles @($rows.path) -CaseSensitive) { throw 'Staged house payload differs from ResourceNames.InstalledFiles.' }
    foreach ($member in $runtimeFiles) {
        if ((Get-SyncSharkSha256 (Join-Path $payloadRoot $member)) -cne (Get-SyncSharkSha256 (Join-Path $package $member))) { throw "Staging changed a runtime member: $member" }
    }
    if ([IO.File]::Exists($payloadManifest)) { throw 'Refusing to replace a staged payload manifest.' }
    [IO.File]::WriteAllText($payloadManifest,([ordered]@{version=$version;source_commit=$SourceCommit;members=$rows}|ConvertTo-Json -Depth 6),$encoding)
    & (Join-Path $PSScriptRoot 'Build-SyncSharkSetup.ps1') -PublishedApp $package -OutputDirectory $deploy
    exit
}
$package = $payloadRoot
$manifestPath = $payloadManifest
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.source_commit -cne $SourceCommit -or $manifest.version -cne $version -or (Compare-Object $installedFiles @($manifest.members.path) -CaseSensitive)) { throw 'Staged payload contract differs from the release.' }
$manifestHash=Get-SyncSharkSha256 $manifestPath
Assert-SyncSharkExactPackage $package $manifestPath $manifestHash $expectedVersion $signed $thumb $subject $subject $spki '' $false
Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $setup) $thumb $subject $subject $spki
# Bind the actual embedded payload, not just the setup filename or signer.
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($setup))
$payload = $assembly.GetManifestResourceStream('payload.zip')
$zip = [IO.Compression.ZipArchive]::new($payload,[IO.Compression.ZipArchiveMode]::Read)
try {
    $remaining = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($member in $manifest.members) { $remaining.Add($member.path,$member.sha256) }
    foreach ($entry in $zip.Entries) {
        if (!$entry.Name) { continue }
        $name = $entry.FullName.Replace([IO.Path]::DirectorySeparatorChar,'/'); $source=$entry.Open()
        try { $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($source)).ToLowerInvariant() } finally { $source.Dispose() }
        if (!$remaining.ContainsKey($name) -or $hash -cne $remaining[$name]) { throw "Unexpected setup payload: $name" }; [void]$remaining.Remove($name)
    }
    if ($remaining.Count) { throw 'Incomplete setup payload.' }
} finally { $zip.Dispose(); $payload.Dispose() }
if ($Step -eq 'SignedScripts') {
    foreach ($name in @('Install-SyncShark.ps1','SyncShark.Release.psm1','SyncShark.Registration.psm1')) {
        if (Test-Path -LiteralPath (Join-Path $deploy $name)) { throw 'Generated scripts already exist; do not overwrite or repeat signing.' }
    }
    foreach ($module in 'SyncShark.Release.psm1','SyncShark.Registration.psm1') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $module) -Destination $deploy }
    $contract=[ordered]@{
        product='Massdex.SyncShark';version=$version;sourceRevision=$SourceCommit
        previousVersion=$metadata.PreviousVersion;previousTreeSha256=$metadata.PreviousTreeSha256
        registrationSha256=(Get-SyncSharkSha256 (Join-Path $deploy 'SyncShark.Registration.psm1'))
        moduleSha256=(Get-SyncSharkSha256 (Join-Path $deploy 'SyncShark.Release.psm1'));manifestSha256=$manifestHash
        signedMembers=$signed; setupSha256=(Get-SyncSharkSha256 $setup); hostExecutable=$signable[0];cliExecutable=$signable[1]
        runtimeMembers=@(foreach($member in $manifest.members){$path=Join-Path $package $member.path;
            [ordered]@{path=$member.path;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-SyncSharkSha256 $path)}})
    }
    foreach ($name in @('Install-SyncShark.ps1')) {
        $template=[IO.File]::ReadAllText((Join-Path $PSScriptRoot ($name+'.template')))
        $generated=Expand-SyncSharkReleaseTemplate $template $metadata ($contract|ConvertTo-Json -Depth 8 -Compress)
        $path=Join-Path $deploy $name
        [IO.File]::WriteAllText($path,$generated,$encoding)
        $tokens=$null;$errors=$null
        [void][Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
        if ($errors.Count) { throw ('Generated script parse failed: '+$name) }
    }
    $contract|ConvertTo-Json -Depth 5
    exit
}

foreach ($name in @('Install-SyncShark.ps1')) {
    $scriptPath=Join-Path $deploy $name
    Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $scriptPath) $thumb $subject $subject $spki
    if ((Get-Item -LiteralPath $scriptPath).Length -gt 1MB) { throw 'Signed release script exceeds the update admission limit.' }
    $header=Get-Content -LiteralPath $scriptPath -TotalCount 1
    $prefix='# SyncSharkUpdateContract: '
    if (!$header.StartsWith($prefix,[StringComparison]::Ordinal) -or $header.Length -gt 16384) { throw 'Signed release contract is missing or oversized.' }
    $contract=$header.Substring($prefix.Length)|ConvertFrom-Json
    if ($contract.product -cne 'Massdex.SyncShark' -or $contract.version -cne $version -or
        $contract.sourceRevision -cne $SourceCommit -or
        $contract.previousVersion -cne $metadata.PreviousVersion -or $contract.previousTreeSha256 -cne $metadata.PreviousTreeSha256 -or
        $contract.hostExecutable -cne $signable[0] -or $contract.cliExecutable -cne $signable[1] -or
        $contract.manifestSha256 -cne $manifestHash -or
        $contract.registrationSha256 -cne (Get-SyncSharkSha256 (Join-Path $deploy 'SyncShark.Registration.psm1')) -or
        $contract.moduleSha256 -cne (Get-SyncSharkSha256 (Join-Path $deploy 'SyncShark.Release.psm1'))) {
        throw 'Signed release contract no longer binds this exact package and predecessor.'
    }
    $contractHashes=Get-SyncSharkSignedMemberHashes $contract
    if ($contractHashes.Count -ne $signable.Count) { throw 'Signed release contract omits a required signed member.' }
    foreach ($member in $signable) {
        if ($contractHashes[$member] -cne $signed[$member]) { throw 'Signed release contract member hash changed.' }
    }
    if ((Get-SyncSharkSha256 $setup) -cne $contract.setupSha256) { throw 'Setup differs from the signed installer contract.' }
}
$archive=Join-Path $release ('SyncShark-v'+$version+'-win-x64.zip')
if (Test-Path -LiteralPath $archive) { throw 'Public archive already exists; verify it rather than overwriting.' }
Add-Type -AssemblyName System.IO.Compression
$stream=[IO.File]::Open($archive,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
    $zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        $sources=@{ 'package/payload-manifest.json'=$manifestPath }
        foreach ($name in @('Install-SyncShark.ps1','SyncShark.Release.psm1','SyncShark.Registration.psm1')) { $sources['scripts/'+$name]=Join-Path $deploy $name }
        $sources['scripts/'+$setupName]=$setup
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
