param([Parameter(Mandatory=$true)][string]$FixtureAssembly)
$ErrorActionPreference='Stop'
$allocation=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SyncShark\build'
$module=Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -PassThru -Force
Assert-SyncSharkWithin $allocation $FixtureAssembly
Assert-SyncSharkOrdinaryTree (Split-Path $FixtureAssembly -Parent) -RootOnly
$assembly=[Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($FixtureAssembly))
$policy=$assembly.GetType('SyncSharkUpdaterTask',$true)
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$xml=[string]$policy.GetMethod('Xml',$flags).Invoke($null,@($sid))
$sddl=[string]$policy.GetMethod('Security',$flags).Invoke($null,@($sid))
$script:checks=0
function CheckTask([string]$Text,[string]$Permissions,[bool]$Accept,[string]$Label) {
    $record=[pscustomobject]@{Xml=$Text;Permissions=$Permissions}
    $record|Add-Member ScriptMethod GetSecurityDescriptor {param($Sections) $this.Permissions}
    $failed=$false
    try {& $module {param($Record,$Sid) Assert-SyncSharkUpdaterTaskForInvocation $Record $Sid} $record $sid}
    catch {$failed=$true;if($Accept){throw}}
    if($failed -eq $Accept){throw ('Wrong admission result at check '+($script:checks+1)+': '+$Label)}
    $script:checks++
}
CheckTask $xml $sddl $true 'fixed production task'
foreach($bad in @($xml.Replace('SyncSharkUpdater.exe','powershell.exe'),$xml.Replace('<Exec>','<Exec><Arguments> </Arguments>'),
    $xml.Replace('<Triggers />','<Triggers><TimeTrigger /></Triggers>'),$xml.Replace('HighestAvailable','LeastPrivilege'),
    $xml.Replace('<Enabled>true','<Enabled>false'),$xml.Replace('<Exec>','<Exec><Command>other</Command>'),
    $xml.Replace($sid,'S-1-5-21-1-2-3-1001'))) {CheckTask $bad $sddl $false 'foreign or altered task'}
foreach($bad in @($sddl.Replace('GRGX','GA'),$sddl.Replace('O:BA','O:'+$sid),$sddl.Replace('D:P','D:'),($sddl+'(A;;GR;;;BU)'))){
    CheckTask $xml $bad $false 'task write/ownership drift'
}
$service=New-Object -ComObject 'Schedule.Service';$definition=$null
try {
    $service.Connect();$definition=$service.NewTask(0);$definition.XmlText=$xml
    CheckTask ([string]$definition.XmlText) $sddl $true 'actual Task Scheduler in-memory normalization'
    $policy.GetMethod('Validate',$flags).Invoke($null,@([string]$definition.XmlText,$sddl,$sid))
    $script:checks++
} finally {foreach($value in @($definition,$service)){if($null -ne $value){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)}}}
"Updater caller: $script:checks task-admission checks passed; one actual in-memory Task Scheduler definition, no registration, Run, elevation or filesystem effect."

# Exercise the production routing decision with bounded observations. These
# adapters provide no signature, protected-file or task-effect assurance.
& $module {
    param($Xml,$Sddl,$Sid)
    $worker='C:\Program Files\Massdex\SyncShark\SyncSharkUpdater.exe'
    $journal='C:\Program Files\Massdex\SyncShark\discovery-setup.json'
    $workerPresent=$false;$journalPresent=$false;$temporaryPresent=$false;$mode='';$foreign=$false;$signatureValid=$true
    $contract=[pscustomobject]@{protectedUpdater=[pscustomobject]@{workerBytes=100;workerSha256=('a'*64)}}
    $task=[pscustomobject]@{Xml=$Xml;Permissions=$Sddl};$task|Add-Member ScriptMethod GetSecurityDescriptor {param($Sections) $this.Permissions}
    function Test-Path {param($LiteralPath) if($LiteralPath -ceq $worker){return $workerPresent};if($LiteralPath -ceq $journal){return $journalPresent};if($LiteralPath -ceq ($journal+'.new')){return $temporaryPresent};throw 'Unexpected route fixture read'}
    function Get-Item {param($LiteralPath,[switch]$Force) if($LiteralPath -cnotin @($worker,$journal,($journal+'.new'))){throw 'Unexpected route fixture metadata'};[pscustomobject]@{PSIsContainer=$false;Attributes=[IO.FileAttributes]::Normal;Length=100}}
    function Get-Content {param($LiteralPath,[switch]$Raw) if($LiteralPath -cnotin @($journal,($journal+'.new'))){throw 'Unexpected route fixture content'};'{"Mode":"'+$mode+'"}'}
    function Get-SyncSharkSha256 {param($Path) if($Path -cne $worker){throw 'Unexpected route fixture hash'};if($foreign){return ('b'*64)};return ('a'*64)}
    function Get-SyncSharkSignatureObservation {param($Path) if($Path -cne $worker){throw 'Unexpected route fixture signature'};return [pscustomobject]@{Fixture=$true}}
    function Assert-SyncSharkSignatureObservation {if(-not $signatureValid){throw 'Rejected fixture signature'}}
    $count=0
    foreach($case in @(
        @($false,$false,$false,'','attended'),@($true,$false,$false,'','attended'),@($false,$true,$false,'','attended'),
        @($true,$true,$false,'','routine'),@($true,$true,$true,'EnrollUpdater','attended'),@($true,$true,$true,'Configure','routine'))) {
        $workerPresent=$case[1];$journalPresent=$case[2];$mode=$case[3]
        $observedTask=if($case[0]){$task}else{$null}
        if((Get-SyncSharkUpdaterInvocationRoute $observedTask $contract $worker) -cne $case[4]){throw 'Wrong production updater enrollment route'}
        $count++
    }
    $journalPresent=$false;$workerPresent=$true;$temporaryPresent=$true;$mode='EnrollUpdater'
    if((Get-SyncSharkUpdaterInvocationRoute $task $contract $worker) -cne 'attended'){throw 'Unpublished enrollment routed to routine update'};$count++
    $temporaryPresent=$false
    if((Get-SyncSharkUpdaterInvocationRoute $task $contract $worker $true) -cne 'attended'){throw 'Undo restoration routed to routine update'};$count++
    foreach($failure in @('hash','signature','task')) {
        $foreign=$failure -ceq 'hash';$signatureValid=$failure -cne 'signature'
        $task.Xml=if($failure -ceq 'task'){$Xml.Replace('SyncSharkUpdater.exe','foreign.exe')}else{$Xml}
        $rejected=$false;try {Get-SyncSharkUpdaterInvocationRoute $task $contract $worker|Out-Null}catch{$rejected=$true}
        if(-not $rejected){throw 'Foreign updater state reached an elevation route'};$count++
    }
    "Updater routing: $count real production decision checks with synthetic read observations; no launch, task effect or elevation."
} $xml $sddl $sid

$fixtureRoot=Join-Path (Split-Path $FixtureAssembly -Parent) ('selector-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureRoot)
$pending=Join-Path $fixtureRoot 'pending-update.json';$temporary=$pending+'.new'
$first='{"schemaVersion":1,"fixture":"first"}';$second='{"schemaVersion":1,"fixture":"second"}'
$utf8=[Text.UTF8Encoding]::new($false);$selectorChecks=0
function PublishSelector([string]$Content) {& $module {param($Path,$Text) Write-SyncSharkPendingSelector $Path $Text} $pending $Content}
function AssertSelector([bool]$Value) {if(-not $Value){throw 'Pending selector regression failed.'};$script:selectorChecks++}
try {
    PublishSelector $first
    AssertSelector (([IO.File]::ReadAllText($pending) -ceq $first) -and -not [IO.File]::Exists($temporary))
    $stamp=[DateTime]::new(2001,1,1,0,0,0,[DateTimeKind]::Utc);[IO.File]::SetLastWriteTimeUtc($pending,$stamp)
    PublishSelector $first;AssertSelector ([IO.File]::GetLastWriteTimeUtc($pending) -eq $stamp)
    [IO.File]::WriteAllText($temporary,$second,$utf8);PublishSelector $second
    AssertSelector (([IO.File]::ReadAllText($pending) -ceq $second) -and -not [IO.File]::Exists($temporary))
    [IO.File]::SetLastWriteTimeUtc($pending,$stamp);[IO.File]::WriteAllText($temporary,$second,$utf8);PublishSelector $second
    AssertSelector (([IO.File]::GetLastWriteTimeUtc($pending) -eq $stamp) -and -not [IO.File]::Exists($temporary))
    foreach($foreign in @('foreign',$second.Substring(0,4))) {
        [IO.File]::WriteAllText($temporary,$foreign,$utf8);$rejected=$false
        try {PublishSelector $second}catch{$rejected=$true}
        AssertSelector ($rejected -and ([IO.File]::ReadAllText($temporary) -ceq $foreign) -and ([IO.File]::ReadAllText($pending) -ceq $second))
        [IO.File]::Delete($temporary)
    }
    "Updater caller: $selectorChecks isolated real pending-selector assertions; no task Run or installation."
} finally {
    foreach($path in [IO.Directory]::GetFileSystemEntries($fixtureRoot)) {
        if(($path -cne $pending -and $path -cne $temporary) -or ([IO.File]::GetAttributes($path) -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint))){throw 'Unexpected selector fixture member preserved.'}
    }
    foreach($path in [IO.Directory]::GetFiles($fixtureRoot)){[IO.File]::Delete($path)}
    [IO.Directory]::Delete($fixtureRoot,$false)
}
