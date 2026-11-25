param([string]$Root, [switch]$RequireNonAdmin, [switch]$Mutant)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
if (-not $Root) { $Root = Join-Path ([IO.Path]::GetTempPath()) ('sync-shark-setup-tests-' + [guid]::NewGuid().ToString('N')) }
[IO.Directory]::CreateDirectory($Root) | Out-Null
if ($RequireNonAdmin) {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'The required unelevated fixture is running as administrator.' }
}
. (Join-Path $repository 'Installer/setup.ps1') -FunctionsOnly
$script:SetupHost='Sync Shark.exe'
$script:SetupLayout=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('["Sync Shark.exe","syncshark.exe","content.txt"]'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Refuses([scriptblock]$Action, [string]$Message) {
    try { & $Action } catch { return }
    throw $Message
}
function Make-Payload([string]$Version) {
    $folder = Join-Path $Root ('payload-' + $Version)
    [IO.Directory]::CreateDirectory($folder) | Out-Null
    $source = Join-Path $Root ($Version + '.cs')
    [IO.File]::WriteAllText($source, "using System.Reflection; using System.Threading; [assembly:AssemblyFileVersion(""$Version.0"")] [assembly:AssemblyInformationalVersion(""$Version"")] class Program { static void Main(string[] args) { if (args.Length == 2) { using (var ready = EventWaitHandle.OpenExisting(args[0])) ready.Set(); using (var exit = EventWaitHandle.OpenExisting(args[1])) exit.WaitOne(); } } }")
    & $compiler /nologo /target:exe "/out:$folder\Sync Shark.exe" $source
    if ($LASTEXITCODE) { throw 'Synthetic payload compilation failed.' }
    Copy-Item -LiteralPath (Join-Path $folder 'Sync Shark.exe') -Destination (Join-Path $folder 'syncshark.exe')
    [IO.File]::WriteAllText((Join-Path $folder 'content.txt'), $Version)
    $archive = Join-Path $Root ($Version + '.zip')
    Compress-Archive -Path (Join-Path $folder '*') -DestinationPath $archive
    return $archive
}
function Active-Version([string]$Install) {
    return [IO.File]::ReadAllText((Join-Path $Install 'active\content.txt'))
}
# Only fixture copies are instrumented; no runtime failure hook ships in setup.
$script = [IO.File]::ReadAllText((Join-Path $repository 'Installer/setup.ps1')).Replace('@HOST@',$script:SetupHost).Replace('@LAYOUT@',$script:SetupLayout)
function Invoke-SetupVariant([string]$Source, [string]$Case, [string]$Archive, [string]$Install,
    [string]$NativeSource, [string]$Action = 'Invoke-SetupInstall $Archive $Install') {
    [IO.Directory]::CreateDirectory($Case) | Out-Null
    foreach ($resource in @('SetupFileSystem.cs','SetupMessages.cs','setup-strings.xml')) {
        Copy-Item -LiteralPath (Join-Path $repository ('Installer/' + $resource)) -Destination $Case
    }
    if ($NativeSource) { [IO.File]::WriteAllText((Join-Path $Case 'SetupFileSystem.cs'), $NativeSource) }
    [IO.File]::WriteAllText((Join-Path $Case 'setup.ps1'), $Source)
    $child = Join-Path $Case 'child.ps1'
    [IO.File]::WriteAllText($child, "param(`$Archive,`$Install)`r`ntry { . (Join-Path `$PSScriptRoot 'setup.ps1') -FunctionsOnly; $Action } catch { [Console]::Error.WriteLine(`$_.Exception.Message); exit 1 }")
    $process = Start-Process powershell.exe -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File', ('"' + $child + '"'), ('"' + $Archive + '"'), ('"' + $Install + '"')) -PassThru -Wait -RedirectStandardOutput (Join-Path $Case 'stdout.log') -RedirectStandardError (Join-Path $Case 'stderr.log')
    try { return $process.ExitCode } finally { $process.Dispose() }
}
if (-not $Mutant) {
    foreach ($owner in @('Shell','Stage')) { & (Join-Path $PSScriptRoot 'Test-SyncSharkSetupCleanupLaunch.ps1') -OwnershipCheck $owner }
}
$native = [IO.File]::ReadAllText((Join-Path $repository 'Installer/SetupFileSystem.cs'))

$terminationCase = Join-Path $Root 'rename-termination'
[IO.Directory]::CreateDirectory($terminationCase) | Out-Null
$terminationSource = Join-Path $terminationCase 'source'
$terminationTarget = Join-Path $terminationCase 'target'
[IO.File]::WriteAllText($terminationSource, 'rename')
$allocation = '        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);'
# Poison the WCHAR immediately after the name; the real writer must terminate it.
$poisonedRename = $native.Replace($allocation, '        IntPtr buffer = Marshal.AllocHGlobal(bufferSize + sizeof(char)); Marshal.WriteInt16(buffer, nameOffset + name.Length, (short)''X''); Marshal.WriteInt16(buffer, nameOffset + name.Length + sizeof(char), 0);')
Assert ($poisonedRename -cne $native) 'Rename termination fixture did not bind.'
$terminationAction = '$held = [SetupFileSystem]::OpenMutableFile($Archive); try { $held.Rename($Install) } finally { $held.Dispose() }'
$terminationExit = Invoke-SetupVariant $script $terminationCase $terminationSource $terminationTarget $poisonedRename $terminationAction
Assert (($terminationExit -eq 0) -and [IO.File]::Exists($terminationTarget)) 'Native rename did not publish the exact target name.'
Assert (([IO.File]::ReadAllText($terminationTarget) -ceq 'rename') -and -not [SetupFileSystem]::Exists($terminationSource)) 'Native rename changed file content or retained the source.'

$older = Make-Payload '1.2.2'; $old = Make-Payload '1.2.3'; $new = Make-Payload '1.2.4'
$sharingSource = Join-Path $Root 'sharing\source'; $sharingTarget = Join-Path $Root 'sharing\target'
$sharingParents = Open-SetupDirectories $sharingSource -Create
$targetParents = Open-SetupDirectories $sharingTarget -Create
$sharingPath = Join-Path $sharingSource 'journal'
[IO.File]::WriteAllText($sharingPath, 'rename')
$sharingJournal = [SetupFileSystem]::OpenMutableFile($sharingPath)
try {
    try { $sharingJournal.Rename((Join-Path $sharingTarget 'journal')) }
    catch {
        $nativeError = $_.Exception
        while ($nativeError.InnerException) { $nativeError = $nativeError.InnerException }
        # Win32 ERROR_SHARING_VIOLATION maps to HRESULT_FROM_WIN32(32), 0x80070020.
        if ($nativeError -is [ComponentModel.Win32Exception] -and $nativeError.NativeErrorCode -eq 32) {
            throw "Held-parent cross-directory rename failed with 0x80070020. $_"
        }
        throw
    }
} finally {
    $sharingJournal.Dispose()
    foreach ($item in $sharingParents) { $item.Dispose() }
    foreach ($item in $targetParents) { $item.Dispose() }
}
$install = Join-Path $Root 'ordinary'
Invoke-SetupInstall $old $install
Assert ((Active-Version $install) -ceq '1.2.3') 'First install did not publish the launcher.'
$active = [SetupFileSystem]::OpenJunction((Join-Path $install 'active'))
$identity = $active.Identity; $active.Dispose()
$readyName = 'Local\setup-ready-' + [guid]::NewGuid().ToString('N')
$exitName = 'Local\setup-exit-' + [guid]::NewGuid().ToString('N')
$ready = [Threading.EventWaitHandle]::new($false, 'ManualReset', $readyName)
$exit = [Threading.EventWaitHandle]::new($false, 'ManualReset', $exitName)
$hostProcess = Start-Process (Join-Path $install 'active\Sync Shark.exe') -ArgumentList @($readyName, $exitName) -PassThru
try {
    $ready.WaitOne() | Out-Null
    Invoke-SetupInstall $new $install
    Assert (-not $hostProcess.HasExited) 'Setup stopped the running predecessor.'
} finally {
    $exit.Set() | Out-Null
    $hostProcess.WaitForExit(); $hostProcess.Dispose(); $ready.Dispose(); $exit.Dispose()
}
Assert ((Active-Version $install) -ceq '1.2.4') 'Upgrade did not retarget the launcher.'
$active = [SetupFileSystem]::OpenJunction((Join-Path $install 'active'))
Assert ($active.Identity -ceq $identity) 'Upgrade replaced rather than retargeted the active directory.'
$active.Dispose()
Refuses { Invoke-SetupInstall $older $install } 'Direct downgrade was accepted.'
Assert ((Active-Version $install) -ceq '1.2.4') 'Downgrade changed the active launcher.'
Assert (-not (Test-Path -LiteralPath (Join-Path $install 'product-root.txt'))) 'Setup created a data pointer.'
Assert (-not (Test-Path -LiteralPath (Join-Path $install 'engine'))) 'Setup created product data.'

$preparation = Join-Path $install ('versions/.staging/' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($preparation) | Out-Null
[IO.File]::WriteAllText((Join-Path $preparation 'partial'), 'incomplete extraction')
$foreignJournal = $preparation + '.journal'
[IO.File]::WriteAllText($foreignJournal, 'keep')
$foreignActive = $preparation + '.active'
New-Item -ItemType Junction -Path $foreignActive -Target $preparation | Out-Null
Invoke-SetupInstall (Make-Payload '1.2.5') $install
Assert ([IO.File]::ReadAllText($foreignJournal) -ceq 'keep') 'Name-only cleanup changed an unproven journal.'
Assert ([SetupFileSystem]::Exists($foreignActive)) 'Name-only cleanup removed an unproven junction.'
Assert ([IO.File]::ReadAllText((Join-Path $preparation 'partial')) -ceq 'incomplete extraction') 'Name-only cleanup changed an unproven preparation.'

$protected = Join-Path $Root 'foreign-tree'; [IO.Directory]::CreateDirectory($protected) | Out-Null
[IO.File]::WriteAllText((Join-Path $protected 'owned'), 'before')
$inventory = Get-SetupTree $protected
[IO.File]::WriteAllText((Join-Path $protected 'owned'), 'after')
Refuses { $handles = Open-SetupTree $protected $inventory; foreach ($handle in $handles) { $handle.Dispose() } } 'Changed payload was admitted for publication.'
[IO.File]::WriteAllText((Join-Path $protected 'owned'), 'before')
[IO.File]::WriteAllText((Join-Path $protected 'foreign'), 'keep')
Refuses { $handles = Open-SetupTree $protected $inventory; foreach ($handle in $handles) { $handle.Dispose() } } 'Foreign child was admitted for publication.'
Assert ([IO.File]::ReadAllText((Join-Path $protected 'foreign')) -ceq 'keep') 'Foreign child was changed.'
$outside = Join-Path $Root 'outside'; [IO.Directory]::CreateDirectory($outside) | Out-Null
[IO.File]::WriteAllText((Join-Path $outside 'keep'), 'keep')
$link = Join-Path $protected 'link'; New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
Refuses { Get-SetupTree $protected } 'Payload inventory followed a junction.'
Assert ([IO.File]::ReadAllText((Join-Path $outside 'keep')) -ceq 'keep') 'External junction target was changed.'
Refuses { Invoke-SetupInstall $old (Join-Path $link 'child') } 'Setup traversed a linked ancestor.'
Assert (-not (Test-Path -LiteralPath (Join-Path $outside 'child'))) 'Setup created a directory through a linked ancestor.'
$ancestors = Open-SetupDirectories $protected
try { Refuses { [IO.Directory]::Move($protected, ($protected + '-moved')) } 'Held ancestry allowed a directory replacement.' }
finally { foreach ($item in $ancestors) { $item.Dispose() } }
# Convert only a disposable held directory; no production failure hook is used.
$junctionSource = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class SetupJunctionFixture {
    // Windows SDK access/share/create/flag/control values for this fixture-only conversion.
    private const uint GenericWrite = 0x40000000, ShareAll = 7, OpenExisting = 3;
    private const uint NoFollowDirectory = 0x02200000, SetReparsePoint = 0x000900A4;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, int length, IntPtr output, int outputLength, out uint returned, IntPtr overlapped);
    public static void Convert(string path, byte[] data) {
        using (var handle = CreateFile(path, GenericWrite, ShareAll, IntPtr.Zero, OpenExisting, NoFollowDirectory, IntPtr.Zero)) {
            if (handle.IsInvalid) throw new Win32Exception();
            uint returned;
            if (!DeviceIoControl(handle, SetReparsePoint, data, data.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero)) throw new Win32Exception();
        }
    }
}
'@
Add-Type -TypeDefinition $junctionSource
$empty = Join-Path $Root 'held-empty'; [IO.Directory]::CreateDirectory($empty) | Out-Null
$heldEmpty = [SetupFileSystem]::OpenDirectory($empty)
try {
    $heldEmpty.Verify()
    [SetupJunctionFixture]::Convert($empty, [SetupFileSystem]::JunctionData($outside))
    Refuses { $heldEmpty.Verify() } 'Held empty directory conversion was not refused.'
    Assert ([IO.File]::ReadAllText((Join-Path $outside 'keep')) -ceq 'keep') 'Held-directory refusal changed the external target.'
} finally { $heldEmpty.Dispose() }
$claimPath = Join-Path $Root 'exclusive-claim'
$claim = [SetupFileSystem]::OpenClaim($claimPath)
try { Refuses { $other = [SetupFileSystem]::OpenClaim($claimPath); $other.Dispose() } 'Concurrent setup claim was accepted.' }
finally { $claim.Dispose() }
$claimLink = Join-Path $Root 'claim-hardlink'
New-Item -ItemType HardLink -Path $claimLink -Target $claimPath | Out-Null
Refuses { $other = [SetupFileSystem]::OpenClaim($claimLink); $other.Dispose() } 'A hardlinked setup claim was accepted.'

$fixtureNative = $native + "`r`n" + $junctionSource.Substring($junctionSource.IndexOf('public static class'))
$renameCase = Join-Path $Root 'post-rename-conversion'
$renameFrom = Join-Path $renameCase 'before'; $renameTo = Join-Path $renameCase 'after'
[IO.Directory]::CreateDirectory($renameFrom) | Out-Null
[IO.Directory]::CreateDirectory($renameTo + '-outside') | Out-Null
[IO.File]::WriteAllText((Join-Path ($renameTo + '-outside') 'keep'), 'keep')
$convertedRename = $fixtureNative.Replace('            Path = target;', '            Path = target; SetupJunctionFixture.Convert(target, JunctionData(target + "-outside"));')
Assert ($convertedRename -cne $fixtureNative) 'Post-rename fixture did not bind.'
$renameAction = '$held = [SetupFileSystem]::OpenMutableDirectory($Archive); try { $held.Rename($Install) } finally { $held.Dispose() }'
$renameExit = Invoke-SetupVariant $script $renameCase $renameFrom $renameTo $convertedRename $renameAction
Assert ($renameExit -ne 0) 'Post-rename conversion was not refused.'
Assert ([IO.File]::ReadAllText((Join-Path $renameCase 'stderr.log')).Contains([Massdex.SyncShark.SetupMessages]::Text('object.invalid', [object[]]@($renameTo)))) 'Post-rename refusal did not name the offending path.'
Assert ((-not [SetupFileSystem]::Exists($renameFrom)) -and [SetupFileSystem]::Exists($renameTo)) 'Post-rename fixture did not complete the real rename.'
Assert ([IO.File]::ReadAllText((Join-Path ($renameTo + '-outside') 'keep')) -ceq 'keep') 'Post-rename refusal changed foreign content.'

# Stage ownership gate above also exercises the interruption immediately before active changes.
$case = Join-Path $Root 'changed-before-publication'; $target = Join-Path $case 'installation'
Invoke-SetupInstall $old $target
$boundary = '$stageHandle.Rename($destination)'
$variant = $script.Replace($boundary, ('[IO.File]::WriteAllText((Join-Path $stage ''content.txt''), ''foreign bytes''); ' + $boundary))
Assert ($variant -cne $script) 'Publication fixture did not bind.'
Assert ((Invoke-SetupVariant $variant $case $new $target) -ne 0) 'Changed payload was published.'
Assert ((Active-Version $target) -ceq '1.2.3') 'Changed payload switched the active launcher.'
Assert ([IO.File]::ReadAllText((Join-Path $target 'versions/1.2.4/content.txt')) -ceq 'foreign bytes') 'Changed payload was lost.'
Refuses { Invoke-SetupInstall $new $target } 'Retry adopted a different candidate.'

$manifest = [xml][IO.File]::ReadAllText((Join-Path $repository 'Installer/setup.manifest'))
Assert ($manifest.assembly.trustInfo.security.requestedPrivileges.requestedExecutionLevel.level -ceq 'asInvoker') 'Setup elevation contract changed.'
Assert ([IO.File]::ReadAllText((Join-Path $repository 'Installer/SyncSharkSetup.cs')).Contains('-ExecutionPolicy Bypass')) 'Setup lost process-scoped policy.'
Write-Host "Setup checks passed; unelevated=$RequireNonAdmin; retained synthetic evidence: $Root"
& cmd /c exit 0
