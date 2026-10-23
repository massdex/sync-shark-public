param([Parameter(Mandatory=$true)][string]$BuildRoot,[string]$Allocation='')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$approved=if([string]::IsNullOrWhiteSpace($Allocation)){Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SyncShark\build\home-activity-tray'}else{[IO.Path]::GetFullPath($Allocation).TrimEnd('\')}
if([IO.Path]::GetFullPath($BuildRoot).TrimEnd('\') -ine $approved){throw 'Use the existing owner build allocation.'}
$module=Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force -PassThru
Assert-SyncSharkOrdinaryTree $BuildRoot -RootOnly
$inputHelper='C:\Program Files\Massdex\SyncShark\versions\0.3.31\SyncSharkDiscovery.exe'
if((Get-SyncSharkSha256 $inputHelper) -cne 'b8bd8d2eb0cb17ebe2793d4a181321d213e98b7cf11fadd427e70526f6886994'){throw 'Exact retained signed helper required.'}
$fixture=Join-Path $BuildRoot ('runtime-icon-access-'+[guid]::NewGuid().ToString('N'))
$source=Join-Path $fixture 'versions\0.3.31'
[void][IO.Directory]::CreateDirectory($source)
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
function SetPrivateFixtureAcl {
    # Use a fresh access-only descriptor each time. Reusing a Set-Acl descriptor
    # can retain audit/owner flags and request SeSecurityPrivilege on replay.
    $privateAcl=[Security.AccessControl.DirectorySecurity]::new()
    $privateAcl.SetAccessRuleProtection($true,$false)
    $privateAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit, ObjectInherit','None','Allow'))
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($source),$privateAcl)
}
$checks=0
function Check([bool]$condition,[string]$name){if(!$condition){throw $name};$script:checks++;'PASS '+$name}
try {
    SetPrivateFixtureAcl
    Copy-Item -LiteralPath $inputHelper -Destination (Join-Path $source 'SyncSharkDiscovery.exe')
    Copy-Item -LiteralPath $inputHelper -Destination (Join-Path $source 'SyncShark.exe')
    $before=Get-SyncSharkTreeIdentity $source
    # Redirect only the supplied product-root input and the final OS launch.
    # The production source admission, actual signature verification and real
    # filesystem ACL repair all run unchanged. No UAC or helper is executed.
    & $module {
        param($root)
        $script:iconFixtureRoot=$root
        $script:iconLaunches=[Collections.Generic.List[string]]::new()
        function script:Resolve-SyncSharkProductRoot([string]$TestProductRoot){$script:iconFixtureRoot}
        function script:Start-Process {
            param([string]$FilePath,[string[]]$ArgumentList,[string]$Verb,[string]$WindowStyle,[switch]$PassThru)
            if($Verb -cne 'RunAs' -or $WindowStyle -cne 'Hidden' -or !$PassThru -or $ArgumentList.Count -ne 2 -or
                $ArgumentList[1] -cne [string]$PID){throw 'The production launch contract changed.'}
            Assert-SyncSharkRuntimeSystemReadAccess ([IO.Path]::GetDirectoryName($FilePath))
            $script:iconLaunches.Add($ArgumentList[0])
            $process=[pscustomobject]@{ExitCode=0}
            $process|Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {param($milliseconds) $true}
            $process|Add-Member -MemberType ScriptMethod -Name Dispose -Value {}
            return $process
        }
    } $fixture
    foreach($operation in @('RuntimeInstall','RuntimeRemove','RuntimeRollback')) {
        SetPrivateFixtureAcl
        $reproduced=$false;try{Assert-SyncSharkRuntimeSystemReadAccess $source}catch{$reproduced=$true}
        Check $reproduced ($operation+' starts with the actual private-source ACL defect')
        Invoke-SyncSharkProtectedRuntime $source $operation
        Assert-SyncSharkRuntimeSystemReadAccess $source
        Check ((Get-SyncSharkTreeIdentity $source) -ceq $before) ($operation+' repairs only public-code permissions before its OS-launch boundary')
    }
    $launched=@(& $module {$script:iconLaunches.ToArray()})
    Check (($launched -join ',') -ceq 'RuntimeInstall,RuntimeRemove,RuntimeRollback') 'All three real production callers reach launch only after SYSTEM read verification'
    SetPrivateFixtureAcl
    [IO.File]::WriteAllText((Join-Path $source 'SyncSharkDiscovery.exe'),'Inert unsigned fixture; never executed')
    $aclBefore=(Get-Acl -LiteralPath $source).Sddl
    $rejected=$false;try{Invoke-SyncSharkProtectedRuntime $source RuntimeInstall}catch{$rejected=$true}
    Check $rejected 'An unsigned bootstrap is rejected by actual signature verification'
    Check ((Get-Acl -LiteralPath $source).Sddl -ceq $aclBefore) 'Rejected bootstrap receives no ACL change'
    Check ((@(& $module {$script:iconLaunches.ToArray()})).Count -eq 3) 'Rejected bootstrap never reaches the OS launch boundary'
} finally {
    Assert-SyncSharkWithin $BuildRoot $fixture
    Assert-SyncSharkOrdinaryTree $fixture
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
[ordered]@{checks=$checks;production_entry='Invoke-SyncSharkProtectedRuntime';real_signature_verification=$true;real_acl_effects_on_fixture=$true;launch_stubbed=$true;uac_prompt_verified=$false;fixture_removed=!(Test-Path -LiteralPath $fixture)}|ConvertTo-Json -Compress
