param()
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -ne 5){throw 'Run this check with the actual Windows PowerShell setup host.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'This proof must remain unelevated.'}
. (Join-Path $PSScriptRoot 'SyncShark.DiscoverySetup.ps1')
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SyncShark.DiscoverySetup.ps1') -Raw
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Setup parsing failed'}
$script:passed=0
function Check([bool]$Value,[string]$Label){if(-not $Value){throw $Label};$script:passed++;'PASS '+$Label}
function Reject([scriptblock]$Action,[string]$Expected,[string]$Label){try{& $Action}catch{if($_.Exception.Message -notlike ('*'+$Expected+'*')){throw};Check $true $Label;return};throw ('Expected rejection: '+$Label)}
foreach($mode in @('Configure','Restore','Remove')) {
    Reject {Invoke-SyncSharkDiscoverySetup -Mode $mode -ParentProcessId $PID -AppDirectory 'C:\invalid'} 'Administrator setup is required once' ($mode+' rejects a medium token at the real setup entry, before effects')
    Reject {Assert-SyncSharkDiscoverySetupRoots $mode} 'Administrator setup is required once' ($mode+' root entry independently rejects a medium token before directory effects')
}
foreach($mode in @('RuntimeInstall','RuntimeRemove','RuntimeRollback')) {
    Reject {Invoke-SyncSharkProtectedRuntimeSetup -Mode $mode -ParentProcessId $PID -AppDirectory 'C:\invalid'} 'administrator approval' ($mode+' rejects a medium token before contract reads or protected effects')
}
# Execute the unchanged production ACL predicate against existing paths only.
# This is a read-only predicate check, not elevated setup or transaction proof.
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ProtectedDirectory'},$true)
if($null -eq $definition){throw 'The production ACL predicate was not found.'}
$trustedOwners=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
. ([scriptblock]::Create($definition.Extent.Text))
ProtectedDirectory 'C:\' $true;Check $true 'Actual volume ancestor passes the restrictive ACL predicate'
ProtectedDirectory 'C:\Program Files';Check $true 'Actual Program Files passes the restrictive ACL predicate'
Reject {ProtectedDirectory ([Environment]::GetFolderPath('LocalApplicationData'))} 'untrusted owner' 'User-owned storage is refused by the production ACL predicate'
foreach($phase in @(0,1,7,14,17,20)) {
    $script:DiscoverySetupPhase=$phase
    $failure=[ComponentModel.Win32Exception]::new(5)
    $actual=Get-SyncSharkDiscoverySetupFailureCode $failure
    Check ($actual -eq (0x20000000 -bor ($phase -shl 16) -bor 5)) ('Bounded setup phase '+$phase+' preserves the Windows error without exception text')
}
$script:DiscoverySetupPhase=17
$wrapped=[Exception]::new('Do not disclose account or path text',[ComponentModel.Win32Exception]::new(1346))
Check ((Get-SyncSharkDiscoverySetupFailureCode $wrapped) -eq (0x20110000 -bor 1346)) 'Inner Windows error survives the PowerShell exception wrapper'
$comFailure=[Runtime.InteropServices.COMException]::new('Private text is not emitted',[int]0xc000005a)
Check ((Get-SyncSharkDiscoverySetupFailureCode $comFailure) -eq [int]0xc000005a) 'A COM NTSTATUS retains every bit instead of being reduced to ambiguous low word 90'
$script:DiscoverySetupPhase=999
Check ((Get-SyncSharkDiscoverySetupFailureCode ([ComponentModel.Win32Exception]::new(2))) -eq 0x20000002) 'Unexpected phase is bounded to unknown rather than encoded as untrusted data'
$script:DiscoverySetupPhase=14
$ioFailure=[IO.IOException]::new('Private path must never be emitted')
Check ((Get-SyncSharkDiscoverySetupFailureCode $ioFailure) -eq (0x200e0000 -bor ($ioFailure.HResult -band 0xffff))) 'Managed failure returns only phase and numeric HRESULT'
$scheduler=$null;$definition=$null
try {
    $scheduler=New-Object -ComObject Schedule.Service;$scheduler.Connect()
    # NewTask only creates an in-memory COM definition; nothing is registered.
    $definition=$scheduler.NewTask(0);$expectedSid=$identity.User.Value
    $definition.Principal.UserId=$expectedSid
    Check ($definition.Principal.UserId -cne $expectedSid) 'Real Task Scheduler reproduces SID-to-account-name normalization'
    Check ((Get-SyncSharkDiscoveryTaskUserSid $definition.Principal.UserId) -ceq $expectedSid) 'Production account predicate accepts the same Windows identity returned by real Task Scheduler'
    Check ((Get-SyncSharkDiscoveryTaskUserSid $expectedSid) -ceq $expectedSid) 'An unchanged SID also identifies the exact approved account'
    Check ((Get-SyncSharkDiscoveryTaskUserSid 'S-1-5-18') -cne $expectedSid) 'A different Windows identity remains different and cannot pass the task-account check'
    Reject {Get-SyncSharkDiscoveryTaskUserSid ''} 'no Windows account' 'An empty task principal is rejected'
    if($source -notmatch '(?s)\$xml=@"\r?\n(.*?)\r?\n"@'){throw 'Fixed task XML not found'}
    $taskRoot='C:\Program Files\Massdex\SyncShark';$taskWorker=Join-Path $taskRoot 'SyncSharkDiscovery.exe'
    $xml=$Matches[1].Replace('$user',$expectedSid).Replace('$worker',$taskWorker).Replace('$root',$taskRoot).Replace('$executionLimit',(Get-SyncSharkDiscoveryTaskExecutionLimit ('0.3.32+'+('a'*40))))
    $definition.XmlText=$xml
    $taskSddl=Get-SyncSharkDiscoveryTaskSddl $expectedSid
    $descriptor=[Security.AccessControl.RawSecurityDescriptor]::new($taskSddl)
    Check ($descriptor.Owner.Value -ceq 'S-1-5-32-544' -and $descriptor.Group.Value -ceq 'S-1-5-32-544') 'Production task ownership uses the assignable administrator group, never the interactive user'
    $taskFolder=$scheduler.GetFolder('\')
    try {
        # TASK_VALIDATE_ONLY is deliberately syntax-only: no registration or
        # activation is represented by this result.
        $validated=$taskFolder.RegisterTask('SyncSharkDiscovery',$xml,1,$expectedSid,$null,3,$taskSddl)
        Check ($null -eq $validated) 'Real Scheduler accepts production XML with validation-only and no registered object'
    } finally {[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskFolder)}
    Check ($source.Contains('$flags=2 -bor 0x10;if($task){$flags=4 -bor 0x10}')) 'Both registration paths forbid Scheduler from adding broader principal permissions'
    $action=$definition.Actions.Item(1)
    Check ([string]::IsNullOrEmpty($action.Arguments)) 'Real task definition has no arguments even when COM represents absence as null'
    Check ($definition.Actions.Count -eq 1 -and $action.Type -eq 0 -and $action.Path -ieq $taskWorker -and $action.WorkingDirectory -ieq $taskRoot) 'Actual COM task definition retains the one fixed executable and working directory'
    Check ($definition.Principal.LogonType -eq 3 -and $definition.Principal.RunLevel -eq 1 -and $definition.Triggers.Count -eq 0) 'Actual task definition remains interactive, on-demand and trigger-free'
    Check ($definition.Settings.ExecutionTimeLimit -eq 'PT31M' -and $definition.Settings.MultipleInstances -eq 2 -and -not $definition.Settings.Enabled) 'Actual task gives the bounded progress-aware worker time to exit before the outer limit'
    Check ((Get-SyncSharkDiscoveryTaskExecutionLimit ('0.3.31+'+('a'*40))) -ceq 'PT2M') 'Restored predecessor retains its original task deadline'
    Reject {Get-SyncSharkDiscoveryTaskExecutionLimit '0.3.32+UNBOUND'} 'unbound' 'An unbound worker cannot choose a longer task deadline'
    Check ($source.Contains('$registered.Definition.Settings.ExecutionTimeLimit -cne $executionLimit')) 'Post-registration verification binds the limit to the published worker'
    $capture=$source.IndexOf('$taskWasEnabled=[bool]$task.Enabled',[StringComparison]::Ordinal)
    $disable=$source.IndexOf('$task.Enabled=$false',[StringComparison]::Ordinal)
    Check ($capture -gt 0 -and $capture -lt $disable) 'Production caller captures enabled state after identity validation and before disabling the existing task'
    Assert-SyncSharkDiscoveryTaskLimitBinding 'PT2M' ('0.3.31+'+('a'*40)) $true $false
    Check $true 'An enabled predecessor with its exact deadline is admitted'
    Assert-SyncSharkDiscoveryTaskLimitBinding 'PT31M' ('0.3.32+'+('a'*40)) $true $false
    Check $true 'An enabled successor with its exact deadline is admitted'
    Reject {Assert-SyncSharkDiscoveryTaskLimitBinding 'PT0S' ('0.3.31+'+('a'*40)) $false $true} 'execution limit' 'Recovery does not waive the fixed deadline allowlist'
    $action.Arguments=' '
    Check (-not [string]::IsNullOrEmpty($action.Arguments)) 'Nonempty whitespace remains an argument and is not silently admitted'
} finally {
    foreach($part in @($definition,$scheduler)){if($null -ne $part -and [Runtime.InteropServices.Marshal]::IsComObject($part)){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($part)}}
}
"$script:passed Discovery setup guards passed. No elevation, task, protected-path, compiler or provider mutation. Durable recovery and actual protected setup remain separate checks."
