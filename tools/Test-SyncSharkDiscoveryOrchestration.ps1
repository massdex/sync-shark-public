param([Parameter(Mandatory=$true)][string]$TransactionAssembly,[string]$ChildRoot='',[string]$ChildMode='Configure',[string]$FailurePoint='',[switch]$Crash)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -ne 5){throw 'Use the actual Windows PowerShell setup host.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'The orchestration fixture must remain unelevated.'}
$allocation=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SyncShark\build\home-activity-tray'
$assemblyPath=[IO.Path]::GetFullPath($TransactionAssembly)
if(-not $assemblyPath.StartsWith($allocation+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Foreign transaction assembly path'}
[void][Reflection.Assembly]::LoadFrom($assemblyPath)
$setupPath=Join-Path $PSScriptRoot 'SyncShark.DiscoverySetup.ps1'
. $setupPath
$source=Get-Content -LiteralPath $setupPath -Raw
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Production setup parsing failed'}
$entry=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-SyncSharkDiscoverySetup'},$true)
$try=$entry.Body.Find({param($n)$n -is [Management.Automation.Language.TryStatementAst] -and $n.Body.Extent.Text.Contains('$script:DiscoverySetupPhase=2')},$true)
$start=$source.IndexOf('$script:DiscoverySetupPhase=10',[StringComparison]::Ordinal)
if($null -eq $try -or $start -lt 0){throw 'Actual setup transition block was not found'}
$statements=@($try.Body.Statements|Where-Object {$_.Extent.StartOffset -ge $start})
# Execute the unchanged production post-admission try/catch/finally, not a second
# implementation of recovery. Fixtures supply admitted pair records and the OS
# task boundary. Authentication/ACL and actual registration remain separate proof.
$body='try {'+($statements.Extent.Text -join "`n")+'} '+($try.CatchClauses.Extent.Text -join "`n")+' finally '+$try.Finally.Extent.Text
$transition=[scriptblock]::Create($body)
$taskIdentity=$entry.Body.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'TaskIdentity'},$true)
$script:passed=0
function Check([bool]$Good,[string]$Name){if(-not $Good){throw $Name};$script:passed++;'PASS '+$Name}
function Save-FixtureTask([object]$Value,[string]$Path){
 if($null -eq $Value){if([IO.File]::Exists($Path)){[IO.File]::Delete($Path)};return}
 [IO.File]::WriteAllText($Path,(@{xml=$Value.Definition.XmlText;enabled=$Value.Enabled}|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
}
function New-FixtureTask([object]$Definition,[bool]$Enabled,[string]$Sddl){
 $value=[pscustomobject]@{Definition=$Definition;Enabled=$Enabled;Sddl=$Sddl}
 $value|Add-Member ScriptMethod GetSecurityDescriptor {param($flags)return $this.Sddl}
 $value|Add-Member ScriptMethod Stop {param($flags)}
 $value|Add-Member ScriptMethod GetInstances {param($flags)return [pscustomobject]@{Count=0}}
 return $value
}
function Invoke-Fixture([string]$Fixture,[string]$Mode,[string]$Interrupt='',[bool]$ProcessCrash=$false){
 $root=Join-Path $Fixture 'component';$app=Join-Path $Fixture 'app';$worker=Join-Path $root 'SyncSharkDiscovery.exe'
 $manifestPath=Join-Path $root 'discovery-manifest.json';$previousWorker=$worker+'.previous';$previousManifest=$manifestPath+'.previous'
 $user=$identity.User.Value;$taskSddl=Get-SyncSharkDiscoveryTaskSddl $user;$boundVersion='0.3.32+'+('b'*40)
 $parent=Get-Process -Id $PID;$registered=$null;$recoveryAllowed=$false;$medium=[IntPtr]::Zero
 $service=New-Object -ComObject Schedule.Service;$service.Connect()
 $store=Join-Path $Fixture 'task.json';$task=$null
 if([IO.File]::Exists($store)){
  $saved=Get-Content -LiteralPath $store -Raw|ConvertFrom-Json;$definition=$service.NewTask(0);$definition.XmlText=$saved.xml
  $task=New-FixtureTask $definition $saved.enabled $taskSddl
 }
 $folder=[pscustomobject]@{Current=$task;Service=$service;Store=$store;Sddl=$taskSddl}
 $folder|Add-Member ScriptMethod RegisterTask {param($name,$xml,$flags,$user,$password,$logon,$sddl)
  if($name -cne 'SyncSharkDiscovery' -or $flags -notin @(18,20) -or $logon -ne 3 -or $sddl -cne $this.Sddl){throw 'Foreign task effect reached the adapter'}
  $definition=$this.Service.NewTask(0);$definition.XmlText=$xml
  $this.Current=New-FixtureTask $definition $definition.Settings.Enabled $sddl
  Save-FixtureTask $this.Current $this.Store
  return $this.Current
 }
 $folder|Add-Member ScriptMethod DeleteTask {param($name,$flags)
  if($name -cne 'SyncSharkDiscovery' -or $flags -ne 0){throw 'Foreign task delete reached the adapter'}
  $this.Current=$null;Save-FixtureTask $null $this.Store
 }
 function Sha([string]$Path){return (Get-FileHash -LiteralPath $Path).Hash.ToLowerInvariant()}
 function ReleaseFiles {}
 function ReadPair([string]$Binary,[string]$Manifest){
  $pair=Get-Content -LiteralPath $Manifest -Raw|ConvertFrom-Json
  if($pair.workerSha256 -cne (Sha $Binary) -or $pair.userSid -cne $user -or $pair.schemaVersion -ne 1){throw 'Fixture pair identity mismatch'}
  return $pair
 }
 . ([scriptblock]::Create($taskIdentity.Extent.Text))
 $members=[Collections.Generic.List[object]]::new()
 $members.Add(@{path='SyncSharkDiscovery.exe';sha256=(Sha (Join-Path $app 'SyncSharkDiscovery.exe'))})
 $script:injected=$false
 $observe=[Action[string]]{
  param($step)
  if(-not $script:injected -and $Interrupt -and $step -ceq $Interrupt){
   $script:injected=$true
   if($ProcessCrash){[Environment]::Exit(71)}
   throw [IO.IOException]::new('Injected orchestration commit failure')
  }
 }
 $constructor=[SyncSharkDiscoveryTransaction].GetConstructor([Reflection.BindingFlags]'Instance,NonPublic',$null,[type[]]@([string],[string],[Action[string]]),$null)
 $arguments=[object[]]::new(3);$arguments[0]=[string]$root;$arguments[1]=[string]$user;$arguments[2]=$observe.PSObject.BaseObject
 $transaction=$constructor.Invoke($arguments)
 try{. $transition}finally{Save-FixtureTask $folder.Current $store}
}
function Seed([string]$Fixture){
 $root=Join-Path $Fixture 'component';$app=Join-Path $Fixture 'app'
 [void][IO.Directory]::CreateDirectory($root);[void][IO.Directory]::CreateDirectory($app)
 [IO.File]::WriteAllText((Join-Path $app 'SyncSharkDiscovery.exe'),'candidate worker')
 foreach($entry in @(@{suffix='';version='0.3.31';text='old worker'},@{suffix='.previous';version='0.3.29';text='previous worker'})){
  $path=Join-Path $root ('SyncSharkDiscovery.exe'+$entry.suffix);[IO.File]::WriteAllText($path,$entry.text)
  $pair=@{schemaVersion=1;version=$entry.version+'+'+('a'*40);userSid=$identity.User.Value;workerSha256=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()}
  [IO.File]::WriteAllText((Join-Path $root ('discovery-manifest.json'+$entry.suffix)),($pair|ConvertTo-Json -Compress))
 }
 $scheduler=New-Object -ComObject Schedule.Service;$scheduler.Connect();$definition=$scheduler.NewTask(0)
 try{
  if($source -notmatch '(?s)\$xml=@"\r?\n(.*?)\r?\n"@'){throw 'Task XML not found'}
  $definition.XmlText=$Matches[1].Replace('$user',$identity.User.Value).Replace('$worker',(Join-Path $root 'SyncSharkDiscovery.exe')).Replace('$root',$root).Replace('$executionLimit','PT2M')
  Save-FixtureTask (New-FixtureTask $definition $true (Get-SyncSharkDiscoveryTaskSddl $identity.User.Value)) (Join-Path $Fixture 'task.json')
 }finally{foreach($part in @($definition,$scheduler)){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($part)}}
}
function Assert-Completed([string]$Fixture,[string]$Mode){
 $root=Join-Path $Fixture 'component';$store=Join-Path $Fixture 'task.json'
 Check (-not [IO.File]::Exists((Join-Path $root 'discovery-setup.json'))) ($Mode+' closes the real transaction journal')
 if($Mode -eq 'Remove'){
  Check (-not [IO.File]::Exists($store) -and -not [IO.File]::Exists((Join-Path $root 'SyncSharkDiscovery.exe'))) 'Remove deletes only the fixture task and component pair'
 }else{
  $saved=Get-Content -LiteralPath $store -Raw|ConvertFrom-Json;$pair=Get-Content -LiteralPath (Join-Path $root 'discovery-manifest.json') -Raw|ConvertFrom-Json
  $xml=[xml]$saved.xml
  Check ($saved.enabled -and $xml.Task.Settings.ExecutionTimeLimit -ceq (Get-SyncSharkDiscoveryTaskExecutionLimit $pair.version)) ($Mode+' leaves the exact matching enabled task deadline and worker')
 }
}
if($ChildRoot){
 if(-not [IO.Path]::GetFullPath($ChildRoot).StartsWith($allocation+'\',[StringComparison]::OrdinalIgnoreCase) -or -not [IO.Directory]::Exists($ChildRoot)){throw 'Foreign child fixture path'}
 Invoke-Fixture $ChildRoot $ChildMode $FailurePoint $Crash.IsPresent
 exit 0
}
$suite=Join-Path $allocation ('discovery-orchestration-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($suite)
try{
 foreach($mode in @('Configure','Restore','Remove')){
  $fixture=Join-Path $suite ('caught-'+$mode);Seed $fixture
  try{Invoke-Fixture $fixture Configure 'journal-written-Committed';throw 'Expected caught failure'}catch{if($_.Exception.ToString() -notlike '*Injected orchestration commit failure*'){throw}}
  $saved=Get-Content -LiteralPath (Join-Path $fixture 'task.json') -Raw|ConvertFrom-Json
  $pair=Get-Content -LiteralPath (Join-Path $fixture 'component\discovery-manifest.json') -Raw|ConvertFrom-Json
  Check (-not $saved.enabled -and ([xml]$saved.xml).Task.Settings.ExecutionTimeLimit -ceq 'PT31M' -and $pair.version.StartsWith('0.3.31+')) ('Caught failure retains the exact disabled31/PT31M repair state before '+$mode)
  Invoke-Fixture $fixture $mode;Assert-Completed $fixture $mode
 }
 foreach($point in @('journal-written-Committed','journal-published-Committed')){
  $fixture=Join-Path $suite $point;Seed $fixture
  $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'powershell.exe'))
  $start.Arguments='-NoLogo -NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -TransactionAssembly "'+$assemblyPath+'" -ChildRoot "'+$fixture+'" -FailurePoint "'+$point+'" -Crash'
  $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
  $process=[Diagnostics.Process]::Start($start);$out=$process.StandardOutput.ReadToEndAsync();$err=$process.StandardError.ReadToEndAsync()
  if(-not $process.WaitForExit(30000)){$process.Kill();throw 'Fixture subprocess exceeded its bound'}
  if($process.ExitCode -ne 71){throw ('Fixture did not reach process exit: '+$err.GetAwaiter().GetResult())}
  Check ([IO.File]::Exists((Join-Path $fixture 'component\discovery-setup.json'))) ($point+' retains the real durable transaction at OS process exit')
  Invoke-Fixture $fixture Configure;Assert-Completed $fixture Configure
  Invoke-Fixture $fixture Configure;Assert-Completed $fixture Configure
 }
 "PASS $script:passed production task/file orchestration controls; real file transaction, unchanged transition try/catch/finally, actual in-memory Scheduler definitions. Task effects and already-authenticated pair input are fixture adapters, not privileged acceptance."
}finally{
 if(-not [IO.Path]::GetFullPath($suite).StartsWith($allocation+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup containment changed'}
 $queue=[Collections.Generic.Queue[string]]::new();$queue.Enqueue($suite)
 while($queue.Count){foreach($path in [IO.Directory]::EnumerateFileSystemEntries($queue.Dequeue())){$attributes=[IO.File]::GetAttributes($path);if(($attributes -band [IO.FileAttributes]::ReparsePoint)-ne 0){throw 'Unexpected fixture reparse is preserved'};if(($attributes -band [IO.FileAttributes]::Directory)-ne 0){$queue.Enqueue($path)}}}
 [IO.Directory]::Delete($suite,$true)
}
