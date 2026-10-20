param([Parameter(Mandatory=$true)][string]$BuildRoot,[string]$ChildRoot,[ValidateSet('Configure','Restore','Remove','Claim')][string]$ChildMode='Configure',[string]$CrashAt,[string]$Allocation='')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -ne 5){throw 'Use the actual Windows PowerShell framework host.'}
$allocation=if([string]::IsNullOrWhiteSpace($Allocation)){[IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'),'SyncShark','build','home-activity-tray')}else{[IO.Path]::GetFullPath($Allocation).TrimEnd('\')}
if([IO.Path]::GetFullPath($BuildRoot).TrimEnd('\') -ine $allocation){throw 'Only the registered active build allocation is permitted.'}
for($parent=[IO.DirectoryInfo]$allocation;$parent;$parent=$parent.Parent){if(($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Reparse fixture allocation refused.'}}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Ordinary fixture proof must remain unelevated.'}
$assemblyPath=Join-Path $allocation 'obj\SyncShark\native\DiscoverySetup.dll'
if(([IO.File]::GetAttributes($assemblyPath) -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline)) -ne 0){throw 'The embedded assembly build member must be ordinary.'}
$assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes($assemblyPath))
$type=$assembly.GetType('SyncSharkDiscoveryTransaction',$true)
$constructor=$type.GetConstructor([Reflection.BindingFlags]'Instance,NonPublic',$null,[type[]]@([string],[string],[Action[string]]),$null)
if($null -eq $constructor){throw 'The ordinary fixture constructor is unavailable.'}
if($ChildRoot){
    $childPath=[IO.Path]::GetFullPath($ChildRoot)
    if(-not $childPath.StartsWith($allocation+'\discovery-framework-checks-',[StringComparison]::OrdinalIgnoreCase) -or -not [IO.Directory]::Exists($childPath)){throw 'Child requires the existing exact isolated fixture.'}
    for($parent=[IO.DirectoryInfo]$childPath;$parent;$parent=$parent.Parent){if(($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Reparse child fixture refused.'}}
    if($ChildMode -eq 'Claim'){
        try{$child=$constructor.Invoke([object[]]@($childPath,'S-1-5-21-123-456-789-1001',$null));$child.Dispose();exit 1}
        catch{$failure=$_.Exception;while($failure.InnerException){$failure=$failure.InnerException};if($failure -is [IO.IOException] -or $failure -is [ComponentModel.Win32Exception]){exit 73};throw}
    }
    if($CrashAt -notin @('journal-written-Preparing','member-published-0','member-published-1','journal-written-Committed','cleanup-old-0')){throw 'Unknown child interruption point.'}
    $script:childCrashAt=$CrashAt
    $observer=[Action[string]]{param($at)if($at -eq $script:childCrashAt){[Environment]::Exit(71)}}
    $child=$constructor.Invoke([object[]]@($childPath,'S-1-5-21-123-456-789-1001',$observer))
    try{$child.Prepare($ChildMode,(Join-Path ([IO.Path]::GetDirectoryName($childPath)) 'candidate.exe'),[Text.Encoding]::UTF8.GetBytes('new manifest'));$child.Apply();$child.Complete();exit 1}finally{$child.Dispose()}
}
$root=Join-Path $allocation ('discovery-framework-checks-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$names=@('SyncSharkDiscovery.exe','discovery-manifest.json','SyncSharkDiscovery.exe.previous','discovery-manifest.json.previous')
$checks=0
function Check([bool]$Value,[string]$Label){if(-not $Value){throw $Label};$script:checks++;'PASS '+$Label}
function Transaction([string]$Path,[Action[string]]$Observe=$null){return $constructor.Invoke([object[]]@($Path,'S-1-5-21-123-456-789-1001',$Observe))}
function Seed([string]$Path){
    [void][IO.Directory]::CreateDirectory($Path)
    for($i=0;$i -lt 4;$i++){[IO.File]::WriteAllText((Join-Path $Path $names[$i]),'original-'+$i)}
}
function Snapshot([string]$Path){return (@($names|ForEach-Object {$file=Join-Path $Path $_;if([IO.File]::Exists($file)){[IO.File]::ReadAllText($file)}else{'absent'}}) -join '|')}
function RunChild([string]$Path,[string]$Mode,[string]$Point){
    foreach($value in @($PSCommandPath,$allocation,$Path)){if($value.Contains('"')){throw 'Unexpected quote in child path.'}}
    $arguments='-NoProfile -NonInteractive -File "'+$PSCommandPath+'" -BuildRoot "'+$allocation+'" -ChildRoot "'+$Path+'" -ChildMode '+$Mode+' -CrashAt '+$Point
    $start=[Diagnostics.ProcessStartInfo]::new('C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe',$arguments)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden';$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    try{
        $output=$process.StandardOutput.ReadToEndAsync();$errors=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(15000)){$process.Kill();[void]$process.WaitForExit(5000);throw ('Framework child timed out at '+$Point)}
        if($process.ExitCode -notin @(71,73)){throw ('Framework child failed: '+$errors.GetAwaiter().GetResult())}
        return $process.ExitCode
    }finally{$process.Dispose()}
}
try {
    $candidate=Join-Path $root 'candidate.exe';[IO.File]::WriteAllText($candidate,'new worker')
    $manifest=[Text.Encoding]::UTF8.GetBytes('new manifest')
    $success=Join-Path $root 'success';Seed $success
    $tx=Transaction $success
    try{
        $tx.Recover();$tx.Prepare('Configure',$candidate,$manifest);$tx.Apply();$tx.Complete()
        Check ((Snapshot $success) -ceq 'new worker|new manifest|original-0|original-1') 'Actual framework assembly publishes current and immediate rollback'
        $tx.Prepare('Restore',$null,$null);$tx.Apply();$tx.Complete()
        Check ((Snapshot $success) -ceq 'original-0|original-1|new worker|new manifest') 'Actual framework assembly restores the matching generation'
        $tx.Prepare('Remove',$null,$null);$tx.Apply();$tx.Complete()
        Check ((Snapshot $success) -ceq 'absent|absent|absent|absent') 'Actual framework assembly removes only fixed generation members'
    }finally{$tx.Dispose()}
    foreach($mode in @('Configure','Restore','Remove')){
        $reference=Join-Path $root ('reference-'+$mode);Seed $reference
        $before=Snapshot $reference;$script:steps=[Collections.Generic.List[string]]::new()
        $tx=Transaction $reference ([Action[string]]{param($at)$script:steps.Add($at)})
        try{$tx.Prepare($mode,$candidate,$manifest);$tx.Apply();$tx.Complete()}finally{$tx.Dispose()}
        $after=Snapshot $reference;$points=@($script:steps|Select-Object -Unique);$number=0
        foreach($point in $points){
            $case=Join-Path $root ($mode+'-'+$number++);Seed $case
            $script:failurePoint=$point;$script:observed=$false
            $observer=[Action[string]]{param($at)if($at -eq $script:failurePoint){$script:observed=$true;throw 'Bounded framework interruption'}}
            $tx=Transaction $case $observer
            try{
                try{$tx.Prepare($mode,$candidate,$manifest);$tx.Apply();$tx.Complete();throw 'Checkpoint not reached'}
                catch{if(-not $script:observed){throw}}
            }finally{$tx.Dispose()}
            $tx=Transaction $case
            try{
                $tx.Recover();$expected=$before
                if($script:steps.IndexOf($point) -ge $script:steps.IndexOf('journal-published-Committed')){$expected=$after}
                if((Snapshot $case) -cne $expected){throw ('Wrong durable generation at '+$mode+'/'+$point)};$script:checks++
                if($tx.HasPending -or [IO.File]::Exists((Join-Path $case 'discovery-setup.json')) -or [IO.Directory]::GetDirectories($case).Length){throw ('Unclosed recovery at '+$mode+'/'+$point)};$script:checks++
                $tx.Recover();if((Snapshot $case) -cne $expected){throw ('Recovery replay changed generation at '+$mode+'/'+$point)};$script:checks++
            }finally{$tx.Dispose()}
        }
        'PASS Actual framework '+$mode+': '+$points.Count+' interruptions obey exact durable commit, close journal/stage and replay safely'
    }
    foreach($point in @('journal-written-Preparing','member-published-0','member-published-1','journal-written-Committed','cleanup-old-0')){
        $case=Join-Path $root ('process-'+$point);Seed $case;$before=Snapshot $case
        Check ((RunChild $case Configure $point) -eq 71) ('Real child process terminates at '+$point)
        $tx=Transaction $case
        try{$tx.Recover();$expected=$before;if($point -eq 'cleanup-old-0'){$expected='new worker|new manifest|original-0|original-1'};Check ((Snapshot $case) -ceq $expected) 'Recovery after process termination respects durable commit';$tx.Recover();Check ((Snapshot $case) -ceq $expected) 'Recovery after process termination is replay-safe'}finally{$tx.Dispose()}
    }
    $case=Join-Path $root 'competing';Seed $case;$tx=Transaction $case
    try{Check ((RunChild $case Claim unused) -eq 73) 'A competing actual process cannot acquire the transaction claim'}finally{$tx.Dispose()}
    [pscustomobject]@{checks=$checks;result='PASS';assembly_bytes=(Get-Item -LiteralPath $assemblyPath).Length;assembly_sha256=(Get-FileHash -LiteralPath $assemblyPath).Hash.ToLowerInvariant();peak_working_set_bytes=(Get-Process -Id $PID).PeakWorkingSet64;scope='Actual build-time .NET Framework assembly, ordinary isolated fixed-member I/O, real process termination and competing claim; no runtime compiler or privileged effects. UAC/task proof is separate.'} | ConvertTo-Json -Compress
} finally {
    if(-not [IO.Path]::GetFullPath($root).StartsWith($allocation+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup containment changed.'}
    $queue=[Collections.Generic.Queue[string]]::new();$queue.Enqueue($root)
    while($queue.Count){
        $directory=$queue.Dequeue()
        foreach($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)){
            $attributes=[IO.File]::GetAttributes($entry)
            if(($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Unexpected reparse fixture; preserve cleanup hold.'}
            if(($attributes -band [IO.FileAttributes]::Directory) -ne 0){$queue.Enqueue($entry)}
        }
    }
    [IO.Directory]::Delete($root,$true)
}
