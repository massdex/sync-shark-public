$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force -PassThru
$root=Join-Path ([IO.Path]::GetTempPath()) ('sync-shark-readiness-'+[guid]::NewGuid().ToString('N'))
$target=Join-Path $root 'expected'
$foreign=Join-Path $root 'foreign'
$active=Join-Path $root 'active'
[void][IO.Directory]::CreateDirectory($target)
[void][IO.Directory]::CreateDirectory($foreign)
try {
    & $module {
        $script:launches=0;$script:packages=0;$script:protectedChecks=0
        $script:exitCode=0;$script:finishes=$true;$script:killed=$false;$script:disposed=$false;$script:replaceActive=$null
        function script:Assert-SyncSharkProtectedRuntime([string]$Runtime) {
            $script:protectedChecks++
            if($Runtime -cne $script:expectedTarget){throw 'Fixture protection boundary received the wrong runtime.'}
        }
        function script:Start-Process {
            param([string]$FilePath,[string]$ArgumentList,[string]$WindowStyle,[switch]$PassThru)
            $script:launches++
            if($null -ne $script:replaceActive){& $script:replaceActive}
            if($FilePath -cne (Join-Path $script:expectedTarget 'SyncShark.exe') -or $ArgumentList -cne '--check-rollback-ready'){
                throw 'Unvalidated executable or wrong readiness arguments selected.'
            }
            $process=[pscustomobject]@{ExitCode=$script:exitCode;Finishes=$script:finishes;Killed=$false;Disposed=$false}
            $process|Add-Member ScriptMethod WaitForExit {param($timeout) $this.Finishes -or $this.Killed}
            $process|Add-Member ScriptMethod Kill {$this.Killed=$true}
            $process|Add-Member ScriptMethod Dispose {$this.Disposed=$true}
            $script:lastProcess=$process
            $process
        }
    }
    & $module {param($t) $script:expectedTarget=$t} $target
    $packageCheck={param($t) & $module {param($p) if($p -cne $script:expectedTarget){throw 'Wrong package target.'};$script:packages++} $t}.GetNewClosure()
    function Reject([string]$Label,[scriptblock]$Action) {
        $rejected=$false
        try {& $Action}catch{$rejected=$true}
        if(-not $rejected){throw ('Expected rejection: '+$Label)}
        Write-Output ('PASS '+$Label)
    }
    Reject 'missing active never launches' {Invoke-SyncSharkRollbackReadiness $active $target $packageCheck}
    [void][IO.Directory]::CreateDirectory($active)
    Reject 'ordinary active directory never launches' {Invoke-SyncSharkRollbackReadiness $active $target $packageCheck}
    [IO.Directory]::Delete($active)
    New-Item -ItemType Junction -Path $active -Target $foreign | Out-Null
    Reject 'unexpected active junction never launches' {Invoke-SyncSharkRollbackReadiness $active $target $packageCheck}
    [IO.Directory]::Delete($active)
    & $module {if($script:launches -ne 0 -or $script:packages -ne 0 -or $script:protectedChecks -ne 0){throw 'Active validation did not precede later boundaries.'}}
    New-Item -ItemType Junction -Path $active -Target $target | Out-Null
    Reject 'package rejection never launches' {Invoke-SyncSharkRollbackReadiness $active $target {throw 'Fixture package rejection.'}}
    & $module {if($script:launches -ne 0){throw 'Package rejection launched a process.'}}
    Invoke-SyncSharkRollbackReadiness $active $target $packageCheck
    & $module {if($script:launches -ne 1 -or -not $script:lastProcess.Disposed){throw 'Valid readiness did not launch and dispose exact target.'}}
    Write-Output 'PASS valid active launches the validated target, never active pathname'
    & $module {
        param($a,$f)
        $script:replaceActive={ [IO.Directory]::Delete($a); New-Item -ItemType Junction -Path $a -Target $f | Out-Null }.GetNewClosure()
    } $active $foreign
    Invoke-SyncSharkRollbackReadiness $active $target $packageCheck
    & $module {param($a,$f) Assert-SyncSharkJunction $a $f; $script:replaceActive=$null} $active $foreign
    Write-Output 'PASS active mutation after validation cannot redirect the validated readiness launch'
    [IO.Directory]::Delete($active)
    New-Item -ItemType Junction -Path $active -Target $target | Out-Null
    & $module {$script:exitCode=1}
    Reject 'nonzero readiness rejected and disposed' {Invoke-SyncSharkRollbackReadiness $active $target $packageCheck}
    & $module {if(-not $script:lastProcess.Disposed){throw 'Failed readiness leaked process handle.'};$script:exitCode=0;$script:finishes=$false}
    Reject 'timeout readiness killed and disposed' {Invoke-SyncSharkRollbackReadiness $active $target $packageCheck}
    & $module {if(-not $script:lastProcess.Disposed -or -not $script:lastProcess.Killed){throw 'Timed-out readiness was not cleaned up.'}}
} finally {
    if(Test-Path -LiteralPath $active){[IO.Directory]::Delete($active)}
    Remove-Item -LiteralPath $root -Recurse -Force
    Remove-Module $module
}
