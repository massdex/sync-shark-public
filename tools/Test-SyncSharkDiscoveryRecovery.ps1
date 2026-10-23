param([Parameter(Mandatory=$true)][string]$TransactionAssembly,[switch]$ExpectBaselineRejection)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -ne 5){throw 'Use the actual Windows PowerShell setup host.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'This fixture must remain unelevated.'}
$allocation=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SyncShark\build\home-activity-tray'
$assemblyPath=[IO.Path]::GetFullPath($TransactionAssembly)
if(-not $assemblyPath.StartsWith($allocation+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Only the current owner-built transaction assembly is admitted.'}
[void][Reflection.Assembly]::LoadFrom($assemblyPath)
. (Join-Path $PSScriptRoot 'SyncShark.DiscoverySetup.ps1')
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SyncShark.DiscoverySetup.ps1') -Raw
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Setup parsing failed'}
$invoke=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-SyncSharkDiscoverySetup'},$true)
$guard=$invoke.Body.Find({param($n)$n -is [Management.Automation.Language.IfStatementAst] -and
    ($n.Extent.Text.StartsWith('if($task -and -not $recovering') -or $n.Extent.Text.StartsWith('if($task){Assert-SyncSharkDiscoveryTaskLimitBinding'))},$true)
if($null -eq $guard){throw 'The actual production admission guard was not found.'}
$guardCode=[scriptblock]::Create($guard.Extent.Text)
$suite=Join-Path $allocation ('discovery-task-recovery-'+[Guid]::NewGuid().ToString('N'))
$root=Join-Path $suite 'component'
$scheduler=$null;$definition=$null;$tx=$null;$checks=0
function Check([bool]$Value,[string]$Name){if(-not $Value){throw $Name};$script:checks++;'PASS '+$Name}
try {
    [void][IO.Directory]::CreateDirectory($root)
    $worker=Join-Path $root 'SyncSharkDiscovery.exe';$manifest=Join-Path $root 'discovery-manifest.json'
    $candidate=Join-Path $suite 'candidate.exe'
    [IO.File]::WriteAllText($worker,'old fixture worker')
    [IO.File]::WriteAllText($manifest,('{"version":"0.3.31+'+('a'*40)+'"}'))
    [IO.File]::WriteAllText($candidate,'new fixture worker')
    $scheduler=New-Object -ComObject Schedule.Service;$scheduler.Connect();$definition=$scheduler.NewTask(0)
    $definition.Settings.ExecutionTimeLimit='PT31M';$definition.Settings.Enabled=$false
    # Real file publication/atomic journal failure and recovery. The in-memory
    # Scheduler definition supplies the state at the registration boundary; no
    # system task is created, updated, enabled or started by this fixture.
    $constructor=[SyncSharkDiscoveryTransaction].GetConstructor([Reflection.BindingFlags]'Instance,NonPublic',$null,
        [type[]]@([string],[string],[Action[string]]),$null)
    $observer=[Action[string]]{param($step)if($step -ceq 'journal-written-Committed'){throw [IO.IOException]::new('Injected durable commit failure')}}
    $arguments=[object[]]::new(3)
    $arguments[0]=[string]$root;$arguments[1]=[string]$identity.User.Value;$arguments[2]=$observer.PSObject.BaseObject
    $tx=$constructor.Invoke($arguments)
    $tx.Prepare('Configure',$candidate,[Text.Encoding]::UTF8.GetBytes('{"version":"0.3.32+'+('b'*40)+'"}'))
    $tx.Apply()
    try{$tx.Complete();throw 'Expected the commit interruption'}catch{if($_.Exception.ToString() -notlike '*Injected durable commit failure*'){throw}}
    $tx.Recover();$tx.Dispose();$tx=$null
    Check ([IO.File]::ReadAllText($worker) -ceq 'old fixture worker') 'Actual failed file publication restores the predecessor worker'
    $old=[IO.File]::ReadAllText($manifest)|ConvertFrom-Json
    Check ($old.version -ceq ('0.3.31+'+('a'*40))) 'Actual recovery restores the predecessor version manifest'
    Check (-not [IO.File]::Exists((Join-Path $root 'discovery-setup.json'))) 'Recovery has cleared the journal before the next setup attempt'
    $recovering=$false;$taskWasEnabled=$definition.Settings.Enabled
    $task=[pscustomobject]@{Definition=$definition}
    $rejected=$false
    try{. $guardCode}catch{if($_.Exception.Message -notlike '*task limit does not match*'){throw};$rejected=$true}
    if($ExpectBaselineRejection){Check $rejected 'Pinned baseline reproduces the disabled-task retry dead end';return}
    Check (-not $rejected) 'Production admission allows repair of the verified disabled stale deadline after rollback'
    $definition.Settings.ExecutionTimeLimit=Get-SyncSharkDiscoveryTaskExecutionLimit $old.version
    Check ($definition.Settings.ExecutionTimeLimit -ceq 'PT2M') 'Retry rebinds the actual Scheduler definition to the recovered worker'
    $definition.Settings.ExecutionTimeLimit='PT31M';$taskWasEnabled=$true
    try{. $guardCode;throw 'Enabled mismatch was admitted'}catch{if($_.Exception.Message -notlike '*task limit does not match*'){throw}}
    Check $true 'The same mismatch on a previously enabled task remains rejected'
    $taskWasEnabled=$false;$definition.Settings.ExecutionTimeLimit='PT0S'
    try{. $guardCode;throw 'Unlimited task was admitted'}catch{if($_.Exception.Message -notlike '*execution limit*'){throw}}
    Check $true 'Disabled status cannot admit an unlimited or foreign deadline'
    $definition.Settings.ExecutionTimeLimit='PT31M';$taskWasEnabled=$true;$recovering=$true
    . $guardCode
    Check $true 'An actual pending-transaction recovery can still rebind its exact allowed deadline'
} finally {
    if($null -ne $tx){$tx.Dispose()}
    foreach($part in @($definition,$scheduler)){if($null -ne $part -and [Runtime.InteropServices.Marshal]::IsComObject($part)){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($part)}}
    if([IO.Directory]::Exists($suite)){
        if(-not [IO.Path]::GetFullPath($suite).StartsWith($allocation+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture cleanup containment changed'}
        $queue=[Collections.Generic.Queue[string]]::new();$queue.Enqueue($suite)
        while($queue.Count){$directory=$queue.Dequeue();foreach($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)){
            $attributes=[IO.File]::GetAttributes($entry)
            if(($attributes -band [IO.FileAttributes]::ReparsePoint)-ne 0){throw 'Unexpected fixture reparse content is preserved'}
            if(($attributes -band [IO.FileAttributes]::Directory)-ne 0){$queue.Enqueue($entry)}
        }}
        [IO.Directory]::Delete($suite,$true)
    }
    "$script:checks actual transaction/production admission checks; no protected ACL, task registration or elevation effects."
}
