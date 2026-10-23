param([Parameter(Mandatory=$true)][string]$BuildRoot,[string]$Allocation='')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$approved=if([string]::IsNullOrWhiteSpace($Allocation)){[IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'),'SyncShark','build','home-activity-tray')}else{[IO.Path]::GetFullPath($Allocation).TrimEnd('\')}
if([IO.Path]::GetFullPath($BuildRoot).TrimEnd('\') -ine $approved){throw 'Use the existing owner build allocation.'}
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force
Assert-SyncSharkOrdinaryTree $BuildRoot -RootOnly
$fixture=Join-Path $BuildRoot ('runtime-icon-access-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$exe=Join-Path $fixture 'SyncShark.exe'; $nested=Join-Path $fixture 'Assets'; $icon=Join-Path $nested 'icon.txt'
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
$acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetOwner($sid);$acl.SetAccessRuleProtection($true,$false)
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit, ObjectInherit','None','Allow'))
Set-Acl -LiteralPath $fixture -AclObject $acl
$checks=0
function Check([bool]$condition,[string]$name){if(!$condition){throw $name};$script:checks++;'PASS '+$name}
try {
    $rejected=$false;try{Set-SyncSharkRuntimeSystemReadAccess $fixture}catch{$rejected=$true}
    Check $rejected 'Refuse a directory that is not a published runtime'
    [IO.File]::WriteAllText($exe,'Inert fixture, never executed')
    New-Item -ItemType Directory -Path $nested | Out-Null
    [IO.File]::WriteAllText($icon,'Inert nested resource')
    $before=Get-SyncSharkTreeIdentity $fixture
    $rejected=$false;try{Assert-SyncSharkRuntimeSystemReadAccess $fixture}catch{$rejected=$true}
    Check $rejected 'Reproduce private staging permissions that exclude SYSTEM'
    Set-SyncSharkRuntimeSystemReadAccess $fixture
    $expectedRights=[int][Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [int][Security.AccessControl.FileSystemRights]::Synchronize
    foreach($path in @($fixture,$exe,$nested,$icon)) {
        $rules=@((Get-Acl -LiteralPath $path).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
        $system=@($rules|Where-Object {$_.IdentityReference.Value -ceq 'S-1-5-18'})
        Check ($system.Count -eq 1 -and $system[0].AccessControlType -eq 'Allow' -and
            [int]$system[0].FileSystemRights -eq $expectedRights) 'SYSTEM receives read/execute and mandatory synchronize, with no write rights'
        $owner=@($rules|Where-Object {$_.IdentityReference.Value -ceq $sid.Value})
        Check ($owner.Count -eq 1 -and $owner[0].FileSystemRights -eq 'FullControl' -and $rules.Count -eq 2) 'Owner access preserved; no other principal granted access'
    }
    Check ((Get-SyncSharkTreeIdentity $fixture) -ceq $before) 'All public runtime bytes remain unchanged'
    $first=(Get-Acl -LiteralPath $fixture).Sddl
    Set-SyncSharkRuntimeSystemReadAccess $fixture
    Check ((Get-Acl -LiteralPath $fixture).Sddl -ceq $first) 'Interrupted publish replay is idempotent'
} finally {
    foreach($path in @($icon,$exe)){if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}}
    if(Test-Path -LiteralPath $nested){[IO.Directory]::Delete($nested,$false)}
    [IO.Directory]::Delete($fixture,$false)
}
[ordered]@{checks=$checks;production_effect=$false;uac_icon_verified=$false;fixture_removed=!(Test-Path -LiteralPath $fixture)}|ConvertTo-Json -Compress
