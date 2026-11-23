param(
    [ValidateSet('Run','Hold','Probe')][string]$Mode='Run',
    [string]$Root,
    [string]$Ready,
    [string]$Release
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force -PassThru
$env:SYNC_SHARK_RELEASE_TEST_MODE='1'
if($Mode -ne 'Run') {
    try {
        Invoke-SyncSharkWithInstanceMaintenanceLease $Root {
            if($Mode -eq 'Hold') {
                $readyEvent=[Threading.EventWaitHandle]::OpenExisting($Ready)
                $releaseEvent=[Threading.EventWaitHandle]::OpenExisting($Release)
                try {
                    [void]$readyEvent.Set()
                    if(-not $releaseEvent.WaitOne(60000)){throw 'Fixture release barrier timed out.'}
                } finally {$readyEvent.Dispose();$releaseEvent.Dispose()}
            } else {[IO.File]::WriteAllText((Join-Path $Root 'entered.txt'),'entered')}
        }
        exit 0
    } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 23 }
}
function Start-Child([string]$ChildMode,[string]$ChildRoot,[string]$ReadyName,[string]$ReleaseName) {
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $start.UseShellExecute=$false;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($arg in @('-NoLogo','-NoProfile','-File',$PSCommandPath,'-Mode',$ChildMode,'-Root',$ChildRoot,'-Ready',$ReadyName,'-Release',$ReleaseName)){
        [void]$start.ArgumentList.Add($arg)
    }
    [Diagnostics.Process]::Start($start)
}
function Wait-Child($Child) {
    if(-not $Child.WaitForExit(60000)){$Child.Kill();$Child.WaitForExit();throw 'Fixture child timed out.'}
    if($Child.ExitCode -notin @(0,23)){throw "Unexpected child failure: $($Child.StandardError.ReadToEnd())"}
    $Child.ExitCode
}
$allocation=Join-Path (Join-Path ([IO.Path]::GetTempPath()) 'sync-shark-release-tests') ('maintenance-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $allocation 'state'))
[IO.File]::WriteAllText((Join-Path $allocation 'state\instance.lock'),'fixture-lock')
$eventId=[guid]::NewGuid().ToString('N')
$readyName='Local\SyncShark.Test.Ready.'+$eventId
$releaseName='Local\SyncShark.Test.Release.'+$eventId
$readyEvent=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$readyName)
$releaseEvent=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$releaseName)
$holder=$null
try {
    foreach($abandon in @($false,$true)) {
        [void]$readyEvent.Reset();[void]$releaseEvent.Reset()
        $holder=Start-Child 'Hold' $allocation $readyName $releaseName
        if(-not $readyEvent.WaitOne(60000)){throw 'Holder did not acquire the actual maintenance boundary.'}
        $probe=Start-Child 'Probe' ($allocation.ToUpperInvariant()+'\') $readyName $releaseName
        try {
            if((Wait-Child $probe) -ne 23 -or (Test-Path (Join-Path $allocation 'entered.txt'))){throw 'Independent maintenance reader entered a held installation.'}
            if($probe.StandardError.ReadToEnd() -notlike '*state lock could not be held for maintenance*'){throw 'Contention did not use the existing admission message.'}
        } finally {$probe.Dispose()}
        if($abandon){$holder.Kill();$holder.WaitForExit()}else{[void]$releaseEvent.Set();if((Wait-Child $holder) -ne 0){throw 'Normal maintenance completion failed.'}}
        $holder.Dispose();$holder=$null
        $probe=Start-Child 'Probe' $allocation $readyName $releaseName
        try {if((Wait-Child $probe) -ne 0){throw 'Released or abandoned installation could not be reacquired.'}}finally{$probe.Dispose()}
        Remove-Item -LiteralPath (Join-Path $allocation 'entered.txt')
        if([IO.File]::ReadAllText((Join-Path $allocation 'state\instance.lock')) -cne 'fixture-lock'){throw 'Maintenance rewrote the application lock.'}
        Write-Output "PASS cross-process exclusion, canonical path alias, reacquisition; abandoned=$abandon"
    }
    $nestedRefused=$false
    Invoke-SyncSharkWithInstanceMaintenanceLease $allocation {
        try {Invoke-SyncSharkWithInstanceMaintenanceLease $allocation {throw 'Nested body entered.'}}
        catch {if($_.Exception.Message -notlike '*nested acquisition is refused*'){throw};$script:nestedRefused=$true}
        & $module {param($r) $lease=Get-SyncSharkLiveMaintenanceLease $r;if($null -eq $lease){throw 'Module-owned lease not available to transaction consumers.'}} $allocation
        $reader=[IO.File]::Open((Join-Path $allocation 'state\instance.lock'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        try {if($reader.ReadByte() -ne [int][char]'f'){throw 'Maintenance made backup reads unavailable.'}}finally{$reader.Dispose()}
        $writer=$null;$blocked=$false
        try {$writer=[IO.File]::Open((Join-Path $allocation 'state\instance.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)}
        catch [IO.IOException] {$blocked=$true}
        finally {if($null -ne $writer){$writer.Dispose()}}
        if(-not $blocked){throw 'Maintenance permitted the application write-lock shape.'}
    }
    if(-not $nestedRefused){throw 'Existing same-session nesting contract changed.'}
    Write-Output 'PASS existing module-owned reuse, nested refusal, application exclusion and backup readability'
    $bodyFailed=$false
    try {Invoke-SyncSharkWithInstanceMaintenanceLease $allocation {throw 'Fixture body failure.'}}
    catch {if($_.Exception.Message -cne 'Fixture body failure.'){throw};$bodyFailed=$true}
    if(-not $bodyFailed){throw 'Maintenance swallowed the body failure.'}
    Invoke-SyncSharkWithInstanceMaintenanceLease $allocation {}
    Write-Output 'PASS body failure releases maintenance admission for replay'
} finally {
    if($null -ne $holder){if(-not $holder.HasExited){$holder.Kill();$holder.WaitForExit()};$holder.Dispose()}
    $readyEvent.Dispose();$releaseEvent.Dispose()
    Remove-Item -LiteralPath $allocation -Recurse -Force
}
