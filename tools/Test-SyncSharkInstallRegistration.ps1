param()
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force
$module=Get-Module SyncShark.Release
& $module {
    # In-memory Windows boundaries: run the shipped registration action without touching HKCU or Programs.
    $script:registration=@{}
    $script:keyPresent=$false
    $script:subkeys=@()
    $script:stopAfterCreate=$false
    $script:shortcutPresent=$false
    $script:failSave=$false
    $script:stopAfterMarker=$false
    $script:link=[pscustomobject]@{TargetPath='';Arguments='';WorkingDirectory=''}
    $script:link | Add-Member ScriptMethod Save {
        if($script:failSave){throw 'fixture save interruption'}
        $script:shortcutPresent=$true
    }
    $script:shell=[pscustomobject]@{}
    $script:shell | Add-Member ScriptMethod CreateShortcut { param($path) $script:link }
    function script:New-Object { param($ComObject) if($ComObject -cne 'WScript.Shell'){throw 'Unexpected COM boundary'}; $script:shell }
    function script:Resolve-SyncSharkProductRoot { param($TestProductRoot) 'D:\Product' }
    function script:Resolve-SyncSharkRuntimeRoot { param($Version) 'D:\Runtime\'+$Version }
    function script:Assert-SyncSharkJunction { param($Path,$Target) }
    function script:Assert-SyncSharkOrdinaryTree { param($Path,[switch]$RootOnly,[switch]$AllowMissing) }
    function script:Get-Item { param($LiteralPath,[switch]$Force,$ErrorAction)
        if($LiteralPath.StartsWith('HKCU:')){[pscustomobject]@{ValueCount=$script:registration.Count;SubKeyCount=$script:subkeys.Count}}
        else {[pscustomobject]@{PSIsContainer=$false;Attributes=[IO.FileAttributes]::Normal}}
    }
    function script:Get-ItemProperty { param($LiteralPath,$ErrorAction) if($script:keyPresent -and $script:registration.Count){[pscustomobject]$script:registration} }
    function script:New-Item { param($Path,[switch]$Force) $script:keyPresent=$true; if($script:stopAfterCreate){throw 'fixture termination after create'} }
    function script:New-ItemProperty {
        param($LiteralPath,$Name,$Value,$PropertyType,[switch]$Force)
        $script:registration[$Name]=$Value
        # Simulate termination immediately after the durable ownership write, before any descriptive field.
        if($script:stopAfterMarker -and $Name -ceq 'SyncSharkOwner'){throw 'fixture termination after marker'}
    }
    function script:Test-Path { param($LiteralPath,$PathType) if($LiteralPath.EndsWith('.lnk')){$script:shortcutPresent}elseif($LiteralPath.StartsWith('HKCU:')){$script:keyPresent}else{$true} }
    function script:Remove-Item { param($LiteralPath) if($LiteralPath.EndsWith('.lnk')){$script:shortcutPresent=$false}else{$script:registration=@{};$script:keyPresent=$false} }
    # Reuse an in-memory predecessor shortcut: no Programs directory is created by this fixture.
    $script:shortcutPresent=$true
    $script:link.TargetPath='D:\Product\active\SyncShark.exe'
    $script:stopAfterCreate=$true
    try { Invoke-SyncSharkAppRegistration '1.2.3' Register; throw 'Expected creation interruption' }
    catch { if($_.Exception.Message -notlike '*fixture termination after create*'){throw} }
    if(-not $script:keyPresent -or $script:registration.Count){throw 'Expected empty durable key'}
    $script:stopAfterCreate=$false
    $script:subkeys=@('ForeignChild')
    try { Invoke-SyncSharkAppRegistration '1.2.3' Register; throw 'Expected foreign subkey refusal' }
    catch { if($_.Exception.Message -notlike '*Another installation owns*'){throw} }
    $script:subkeys=@()
    $script:stopAfterMarker=$true
    try { Invoke-SyncSharkAppRegistration '1.2.3' Register; throw 'Expected marker interruption' }
    catch { if($_.Exception.Message -notlike '*fixture termination after marker*'){throw} }
    if($script:registration.Count -ne 1 -or -not $script:registration.ContainsKey('SyncSharkOwner')){throw 'Marker was not the first durable value'}
    $script:stopAfterMarker=$false
    Invoke-SyncSharkAppRegistration '1.2.3' Register
    Invoke-SyncSharkAppRegistration '1.2.3' Verify
    if($script:registration.Count -ne 6 -or $script:link.Arguments -cne '--cli engine host start --yes' -or
        $script:registration.UninstallString -notlike '* -ExecutionPolicy Bypass -File *active\tools\SyncShark.Uninstall.ps1*') {throw 'Registration contract differs'}
    Invoke-SyncSharkAppRegistration '1.2.4' Register
    Invoke-SyncSharkAppRegistration '1.2.4' Verify
    $script:failSave=$true
    try { Invoke-SyncSharkAppRegistration '1.2.5' Register; throw 'Expected interruption' }
    catch { if($_.Exception.Message -notlike '*fixture save interruption*'){throw} }
    $script:failSave=$false
    Invoke-SyncSharkAppRegistration '1.2.5' Register
    Invoke-SyncSharkAppRegistration '1.2.5' Verify
    $script:link.TargetPath='D:\Other\app.exe'
    foreach($operation in @('Register','Remove')) {
        try { Invoke-SyncSharkAppRegistration '1.2.5' $operation; throw 'Expected foreign shortcut refusal' }
        catch { if($_.Exception.Message -notlike '*Another application owns*'){throw} }
    }
    $script:link.TargetPath='D:\Product\active\SyncShark.exe'
    $owner=$script:registration.SyncSharkOwner
    $script:registration.Remove('SyncSharkOwner')
    foreach($operation in @('Register','Remove')) {
        try { Invoke-SyncSharkAppRegistration '1.2.5' $operation; throw 'Expected foreign registry refusal' }
        catch { if($_.Exception.Message -notlike '*Another installation owns*'){throw} }
    }
    $script:registration=@{SyncSharkOwner=$owner}
    Invoke-SyncSharkAppRegistration '1.2.5' Remove
    Invoke-SyncSharkAppRegistration '1.2.5' VerifyAbsent
    $script:keyPresent=$true
    Invoke-SyncSharkAppRegistration '1.2.5' Remove
    Invoke-SyncSharkAppRegistration '1.2.5' VerifyAbsent
    Invoke-SyncSharkAppRegistration '1.2.5' Remove
    'PASS per-user ARP fields, process-scoped policy, empty-key and marker-first replay, partial uninstall, interrupted save and foreign ownership'
}
Remove-Module SyncShark.Release
