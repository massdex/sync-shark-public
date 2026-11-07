param([Parameter(Mandatory=$true)][string]$BuildRoot,
 [Parameter(Mandatory=$true)][string]$SignedInstallerPath,
 [string]$Allocation='')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$allocation=if([string]::IsNullOrWhiteSpace($Allocation)){Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SyncShark\build\home-activity-tray'}else{[IO.Path]::GetFullPath($Allocation).TrimEnd('\')}
if([IO.Path]::GetFullPath($BuildRoot).TrimEnd('\') -ine $allocation){throw 'Use the registered owner build allocation.'}
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force
. (Join-Path $PSScriptRoot 'SyncShark.Uninstall.ps1')
$fixture=Join-Path $BuildRoot ('protected-lifecycle-'+[guid]::NewGuid().ToString('N'))
$script:checks=0; $script:effects=[Collections.Generic.List[string]]::new()
$script:failAfter=''; $script:registered=''
function Check([bool]$Good,[string]$Name){if(!$Good){throw $Name};$script:checks++;'PASS '+$Name}
function Reject([scriptblock]$Action,[string]$Name,[string]$ExpectedError){
 $rejected=$false;try{& $Action}catch{
  if($ExpectedError -and $_.Exception.Message -notlike ('*'+$ExpectedError+'*')){throw}
  $rejected=$true
 };Check $rejected $Name
}
function Step([string]$Template,[string]$Name){
 $tokens=$null;$errors=$null
 $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $Template),[ref]$tokens,[ref]$errors)
 if($errors.Count){throw 'Production template parsing failed'}
 $matches=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -ceq 'Invoke-SyncSharkJournalStep' -and $n.CommandElements[3].Value -ceq $Name},$true))
 if($matches.Count -ne 1){throw 'The exact production journal step was not found'}
 [scriptblock]::Create($matches[0].Extent.Text)
}
function Journal([string]$Operation,[string[]]$Steps){
 [pscustomobject]@{schema=1;operation=$Operation;version=$version;commit=$commit;product_root=$productRoot;status='active';pending=$null;completed=@();allowed_steps=$Steps;artifacts=[pscustomobject]@{}}
}
# Only OS deployment/package operations and their fixed root inputs are adapted.
# Actual template bodies, release journals, hashes, signatures, junctions and the
# complete non-test uninstall/undo functions execute unchanged on inert fixtures.
function Resolve-SyncSharkProductRoot([string]$TestProductRoot){$productRoot}
function Resolve-SyncSharkRuntimeRoot([string]$Version,[string]$TestProductRoot=''){Join-Path $protectedRoot $Version}
function Assert-SyncSharkProtectedRuntime([string]$Runtime){
 Assert-SyncSharkWithin $protectedRoot $Runtime;Assert-SyncSharkOrdinaryTree $Runtime
 $script:effects.Add('protected-verify:'+([IO.Path]::GetFileName($Runtime)))
}
function Invoke-SyncSharkProtectedRuntime([string]$SourceRuntime,[string]$Operation,[switch]$RestoringUninstall){
 Assert-SyncSharkWithin $productRoot $SourceRuntime
 if($SourceRuntime -ine (Join-Path $productRoot ('versions\'+$version))){throw 'Wrong fixture source'}
 $script:effects.Add($Operation)
 switch($Operation){
  RuntimeInstall {
   if(Test-Path -LiteralPath (Join-Path $transactionRoot 'active.uninstalled')){
    Check $RestoringUninstall.IsPresent 'Uninstall restoration explicitly selects the attended recovery route'
   }elseif($RestoringUninstall){throw 'Ordinary publication unexpectedly selected uninstall restoration'}
   if(Test-Path -LiteralPath ($target+'.uninstalled')){Move-Item -LiteralPath ($target+'.uninstalled') -Destination $target}
   elseif(!(Test-Path -LiteralPath $target)){Copy-SyncSharkTreeToStaging $SourceRuntime $target}
   Assert-SyncSharkTreeMatch $SourceRuntime $target
  }
  RuntimeRemove {Move-Item -LiteralPath $target -Destination ($target+'.uninstalled')}
  RuntimeRollback {Assert-SyncSharkOrdinaryTree $previousTarget}
  default {throw 'Unexpected privileged operation'}
 }
 if($script:failAfter -ceq $Operation){$script:failAfter='';throw 'Injected OS result loss after actual fixture placement'}
}
function Invoke-SyncSharkExplorerPackage([string]$Runtime,[string]$Operation,[string]$ExpectedPreviousRuntime=''){
 Assert-SyncSharkWithin $protectedRoot $Runtime
 $script:effects.Add($Operation+':'+[IO.Path]::GetFileName($Runtime))
 switch($Operation){
  Register {$script:registered=$Runtime}
  Remove {if($script:registered -ceq $Runtime){$script:registered=''}}
  Verify {if($script:registered -cne $Runtime){throw 'Wrong registered fixture root'}}
  VerifyAbsent {if($script:registered -ceq $Runtime){throw 'Fixture package remains registered'}}
  default {throw 'Unexpected package operation'}
 }
 if($script:failAfter -ceq $Operation){$script:failAfter='';throw 'Injected package response loss'}
}
$testMode=$false;$CrashAfter='';$ExpectedSignedIdentityPackageSha256='a'*64
$expectedPublisher=(Get-SyncSharkSignerIdentity).roles.publisher
$expectedThumbprint=@($expectedPublisher.thumbprints)
$expectedSubject=$expectedIssuer=$expectedPublisher.subject
$expectedSpkiSha256=@($expectedPublisher.spki)
$installerIdentityPath=[IO.Path]::GetFullPath($SignedInstallerPath)
Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $installerIdentityPath) $expectedThumbprint $expectedSubject $expectedIssuer $expectedSpkiSha256
$signedHeader=Get-Content -LiteralPath $installerIdentityPath -TotalCount 1
$prefix='# SyncSharkUpdateContract: '
if(-not $signedHeader.StartsWith($prefix,[StringComparison]::Ordinal)){throw 'A retained signed release contract is required.'}
$signedIdentity=$signedHeader.Substring($prefix.Length)|ConvertFrom-Json
$version=[string]$signedIdentity.version;$commit=[string]$signedIdentity.sourceRevision
if($version -cnotmatch '^\d+\.\d+\.\d+$' -or $commit -cnotmatch '^[0-9a-f]{40}$'){throw 'Fixture release identity is not frozen.'}
$expectedVersion=$version+'+'+$commit
$productRoot=Join-Path $fixture 'product';$protectedRoot=Join-Path $fixture 'protected'
$target=Join-Path $protectedRoot $version;$previousTarget=Join-Path $protectedRoot '0.3.33'
$sourceTarget=Join-Path $productRoot ('versions\'+$version)
$transactionRoot=Join-Path $productRoot ('replacement-staging\transactions\'+$version+'-'+$commit.Substring(0,7))
$staging=Join-Path $transactionRoot 'runtime.staging';$journalPath=Join-Path $transactionRoot 'install-journal.json'
$active=Join-Path $productRoot 'active';$rollbackLink=Join-Path $transactionRoot 'active.previous';$successorLink=Join-Path $transactionRoot 'active.successor'
$assertPackage={param($Runtime) Assert-SyncSharkOrdinaryTree $Runtime;if((Get-SyncSharkTreeIdentity $Runtime) -cne $runtimeHash){throw 'Exact fixture package mismatch'}}
try {
 foreach($dir in @($staging,$previousTarget,(Split-Path $sourceTarget))){[void][IO.Directory]::CreateDirectory($dir)}
 [IO.File]::WriteAllText((Join-Path $staging 'fixture.txt'),'Inert new runtime; never executable')
 [IO.File]::WriteAllText((Join-Path $previousTarget 'fixture.txt'),'Inert predecessor; never executable')
 $runtimeHash=Get-SyncSharkTreeIdentity $staging
 $journal=Journal install @('publish-runtime','register-explorer-package')
 $publish=Step 'Install-SyncShark.ps1.template' 'publish-runtime'
 $register=Step 'Install-SyncShark.ps1.template' 'register-explorer-package'
 $script:failAfter='RuntimeInstall'
 Reject {. $publish} 'Actual production publish path retains a pending journal after placement-result loss' 'Injected OS result loss'
 $journal=Read-SyncSharkJournal $journalPath install $version $commit $productRoot @('publish-runtime','register-explorer-package')
 Check ($journal.pending -ceq 'publish-runtime' -and (Test-Path $target) -and (Test-Path $sourceTarget) -and !(Test-Path $staging)) 'Published source and protected fixture are retained for exact replay'
 . $publish
 Check ($journal.completed -contains 'publish-runtime') 'Actual publish replay completes through the protected branch, not testMode'
 Check ((Get-SyncSharkSha256 (Join-Path $transactionRoot 'Install-SyncShark.ps1')) -ceq (Get-SyncSharkSha256 $installerIdentityPath)) 'Real signed installer contract is retained byte-for-byte'
 $script:failAfter='Register'
 Reject {. $register} 'Actual registration phase preserves its pending journal on OS result loss' 'Injected package response loss'
 . $register
 Check ($journal.status -ceq 'complete' -and $script:registered -ceq $target) 'Actual registration replay converges on the protected successor'
 $effectCount=$script:effects.Count
 . $publish;. $register
 Check (($script:effects|Where-Object {$_ -ceq 'RuntimeInstall'}).Count -eq 2) 'Completed installation replay does not place the runtime again'
 # Rollback ordering uses the exact production template command bodies and real
 # active/predecessor junction moves. OS package and protected placement only are adapters.
 New-Item -ItemType Junction -Path $active -Target $target|Out-Null
 New-Item -ItemType Junction -Path $rollbackLink -Target $previousTarget|Out-Null
 $rollbackSteps=@('remove-successor-explorer','retain-successor','publish-predecessor','restore-predecessor-explorer')
 $journal=Journal rollback $rollbackSteps;$journalPath=Join-Path $transactionRoot 'rollback-journal.json'
 $script:effects.Clear();$script:failAfter='RuntimeRollback'
 $remove=Step 'Rollback-SyncShark.ps1.template' $rollbackSteps[0]
 Reject {. $remove} 'Actual rollback stops before pointer movement when protected restore reports failure' 'Injected OS result loss'
 Assert-SyncSharkJunction $active $target
 Check ($journal.pending -ceq 'remove-successor-explorer' -and !$script:registered) 'Rollback failure retains the successor pointer with its Explorer package removed'
 foreach($name in $rollbackSteps){. (Step 'Rollback-SyncShark.ps1.template' $name)}
 Assert-SyncSharkJunction $active $previousTarget;Assert-SyncSharkJunction $successorLink $target
 Check ($journal.status -ceq 'complete' -and $script:registered -ceq $previousTarget) 'Rollback publishes and registers only the protected predecessor'
 $order=$script:effects.ToArray()
 Check ([Array]::IndexOf($order,'RuntimeRollback') -lt [Array]::IndexOf($order,'Register:0.3.33')) 'Protected rollback precedes exact protected33 predecessor Explorer registration'
 # Return fixture navigation to the successor for full non-test uninstall/undo.
 [IO.Directory]::Delete($active);Move-Item -LiteralPath $successorLink -Destination $active
 $plan=[pscustomobject]@{root=$productRoot;testMode=$false;version=$version;commit=$commit;target=$target;transaction=$transactionRoot;journal=(Join-Path $transactionRoot 'uninstall-journal.json');retired=($target+'.uninstalled');pointer=(Join-Path $transactionRoot 'active.uninstalled');tree=$runtimeHash;previous_receipt=$null}
 [IO.Directory]::CreateDirectory((Join-Path $productRoot 'state'))|Out-Null
 [IO.File]::WriteAllText((Join-Path $productRoot 'state\settings.txt'),'Retain user settings unchanged')
 $stateHash=Get-SyncSharkTreeIdentity (Join-Path $productRoot 'state')
 $script:failAfter='RuntimeRemove'
 Reject {Complete-SyncSharkUninstall $plan} 'Full production uninstall catches protected retirement failure' 'Injected OS result loss'
 Assert-SyncSharkJunction $active $target
 Check ((Get-SyncSharkTreeIdentity $target) -ceq $runtimeHash -and !(Test-Path $plan.retired)) 'Full uninstall catch restores the same runtime and active pointer'
 $plan.previous_receipt=[pscustomobject]@{sha256=(Get-SyncSharkSha256 $plan.journal)}
 Complete-SyncSharkUninstall $plan
 Check (!(Test-Path $active) -and !(Test-Path $target) -and (Test-Path $plan.retired)) 'Full non-test uninstall retires only the bounded fixture runtime'
 Restore-SyncSharkUninstalledRuntime $plan
 Assert-SyncSharkJunction $active $target
 Check ((Get-SyncSharkTreeIdentity $target) -ceq $runtimeHash) 'Full non-test undo restores the exact retained runtime'
 Check ((Get-SyncSharkTreeIdentity (Join-Path $productRoot 'state')) -ceq $stateHash) 'Install rollback, uninstall and undo preserve settings bytes'
 Check ($script:effects -contains 'RuntimeRemove' -and $script:effects -contains 'RuntimeInstall') 'Both production protected uninstall and undo consumers were exercised'
} finally {
 foreach($link in @($active,$rollbackLink,$successorLink,(Join-Path $transactionRoot 'active.uninstalled'))){
  $entry=Get-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
  if($entry -and ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)){Assert-SyncSharkWithin $fixture $link;[IO.Directory]::Delete($link)}
 }
 Assert-SyncSharkWithin $BuildRoot $fixture;Assert-SyncSharkOrdinaryTree $fixture
 Remove-Item -LiteralPath $fixture -Recurse -Force
}
[ordered]@{checks=$checks;test_mode=$false;production_bodies_unchanged=$true;real_signature_check=$true;real_journals_and_pointers=$true;protected_os_effects_adapted=$true;actual_privileged_lifecycle_proven=$false;fixture_removed=!(Test-Path -LiteralPath $fixture)}|ConvertTo-Json -Compress
