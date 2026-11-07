param([string]$Root, [switch]$RequireNonAdmin)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
if (-not $Root) { $Root = Join-Path ([IO.Path]::GetTempPath()) ('sync-shark-setup-tests-' + [guid]::NewGuid().ToString('N')) }
[IO.Directory]::CreateDirectory($Root) | Out-Null
if ($RequireNonAdmin) {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'The required unelevated fixture is running as administrator.' }
}
. (Join-Path $repository 'Installer/setup.ps1') -FunctionsOnly
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
    & $compiler /nologo /target:exe "/out:$folder\SyncShark.exe" $source
    if ($LASTEXITCODE) { throw 'Synthetic payload compilation failed.' }
    [IO.File]::WriteAllText((Join-Path $folder 'content.txt'), $Version)
    $archive = Join-Path $Root ($Version + '.zip')
    Compress-Archive -Path (Join-Path $folder '*') -DestinationPath $archive
    return $archive
}
function Active-Version([string]$Install) {
    return [IO.File]::ReadAllText((Join-Path $Install 'active\content.txt'))
}
$older = Make-Payload '1.2.2'; $old = Make-Payload '1.2.3'; $new = Make-Payload '1.2.4'
$sharingSource = Join-Path $Root 'sharing\source'; $sharingTarget = Join-Path $Root 'sharing\target'
$sharingParents = Open-SetupDirectories $sharingSource -Create
$targetParents = Open-SetupDirectories $sharingTarget -Create
$sharingPath = Join-Path $sharingSource 'journal'
[IO.File]::WriteAllText($sharingPath, 'rename')
$sharingJournal = [SetupFileSystem]::OpenJournal($sharingPath)
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
$hostProcess = Start-Process (Join-Path $install 'active\SyncShark.exe') -ArgumentList @($readyName, $exitName) -PassThru
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
$cleared = @(Clear-SetupPreparation $install)
Assert (-not (Test-Path -LiteralPath $preparation)) 'Interrupted preparation accumulated.'
Assert (($cleared -join '').Contains($preparation)) 'Preparation cleanup did not report its path.'
$protected = Join-Path $Root 'foreign-tree'; [IO.Directory]::CreateDirectory($protected) | Out-Null
[IO.File]::WriteAllText((Join-Path $protected 'owned'), 'before')
$inventory = Get-SetupTree $protected
[IO.File]::WriteAllText((Join-Path $protected 'owned'), 'after')
Refuses { $handles = Open-SetupTree $protected $inventory; foreach ($handle in $handles) { $handle.Dispose() } } 'Changed payload was admitted for deletion.'
[IO.File]::WriteAllText((Join-Path $protected 'owned'), 'before')
[IO.File]::WriteAllText((Join-Path $protected 'foreign'), 'keep')
Refuses { $handles = Open-SetupTree $protected $inventory; foreach ($handle in $handles) { $handle.Dispose() } } 'Foreign child was admitted for deletion.'
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

$journalPath = Join-Path $Root 'torn-journal'
[IO.File]::WriteAllBytes($journalPath, [byte[]]@())
$journal = [SetupFileSystem]::OpenJournal($journalPath)
try {
    Write-SetupFrame $journal ([pscustomobject]@{ Value = 'durable' })
    $journal.Append([byte[]]@(0xE2, 0x82))
    try { $frame = Read-SetupFrame $journal }
    catch { throw "Torn UTF-8 tail hid the durable journal frame. $_" }
    Assert ($frame.Value -ceq 'durable') 'Torn UTF-8 tail hid the durable journal frame.'
} finally { $journal.Dispose() }

# Exercise the same program-file transaction with injected interruption at each effect boundary.
# Only fixture copies are instrumented; no runtime failure hook ships in setup.
$script = [IO.File]::ReadAllText((Join-Path $repository 'Installer/setup.ps1'))
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
$native = [IO.File]::ReadAllText((Join-Path $repository 'Installer/SetupFileSystem.cs'))
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

$preparationCase = Join-Path $Root 'converted-preparation'
$preparationInstall = Join-Path $preparationCase 'installation'
$preparationTarget = Join-Path $preparationCase 'foreign'
[IO.Directory]::CreateDirectory($preparationTarget) | Out-Null
$foreignJournal = Join-Path $preparationTarget ('a' * 32 + '.journal')
$foreignActive = Join-Path $preparationTarget ('b' * 32 + '.active')
[IO.File]::WriteAllText($foreignJournal, 'preserve')
New-Item -ItemType Junction -Path $foreignActive -Target $outside | Out-Null
$guardBoundary = '    $guard = [SetupFileSystem]::OpenDirectory($parent)'
$convertedPreparation = $script.Replace($guardBoundary, ($guardBoundary + "`r`n" + '    [SetupJunctionFixture]::Convert($parent, [SetupFileSystem]::JunctionData($Archive))'))
Assert ($convertedPreparation -cne $script) 'Preparation conversion fixture did not bind.'
$preparationExit = Invoke-SetupVariant $convertedPreparation $preparationCase $preparationTarget $preparationInstall $fixtureNative 'Clear-SetupPreparation $Install'
Assert (([SetupFileSystem]::Exists($foreignJournal)) -and ([SetupFileSystem]::Exists($foreignActive))) 'Converted staging deleted foreign evidence.'
Assert ([IO.File]::ReadAllText($foreignJournal) -ceq 'preserve') 'Converted staging changed foreign evidence.'
Assert ([IO.File]::ReadAllText((Join-Path $outside 'keep')) -ceq 'keep') 'Converted staging changed the junction target.'
Assert ($preparationExit -ne 0) 'Converted staging was not refused.'
Assert ([IO.File]::ReadAllText((Join-Path $preparationCase 'stderr.log')).Contains([Massdex.SyncShark.SetupMessages]::Text('object.invalid', [object[]]@((Join-Path $preparationInstall 'versions\.staging'))))) 'Preparation refusal did not name the offending path.'

$boundaries = @(
    '        $version = Get-SetupVersion (Join-Path $stage ''SyncShark.exe'')',
    '        $stageHandle.Rename($destination)',
    '        $state.Committed = $true',
    '        $journal.Delete(); $journal = $null'
)
foreach ($scenario in @('first','upgrade')) {
foreach ($mode in @('throw','crash')) {
    foreach ($boundary in $boundaries) {
        $case = Join-Path $Root ($scenario + '-' + $mode + '-' + [array]::IndexOf($boundaries, $boundary))
        $target = Join-Path $case 'installation'
        [IO.Directory]::CreateDirectory($case) | Out-Null
        if ($scenario -eq 'upgrade') { Invoke-SetupInstall $old $target }
        $effect = if ($mode -eq 'throw') { 'throw "fixture interruption"' } else { '[Environment]::FailFast("fixture interruption")' }
        $instrumented = $script.Replace($boundary, ('            ' + $effect + "`r`n" + $boundary))
        Assert ($instrumented -cne $script) 'Fixture boundary did not bind.'
        $exitCode = Invoke-SetupVariant $instrumented $case $new $target
        Assert ($exitCode -ne 0) 'Injected interruption unexpectedly succeeded.'
        if ($scenario -eq 'upgrade') {
            Assert ([SetupFileSystem]::Exists((Join-Path $target 'active\content.txt'))) 'Interrupted switch lost the active launcher.'
            $observed = Active-Version $target
            Assert ($observed -in @('1.2.3','1.2.4')) 'Interrupted switch lost the active launcher.'
        }
        $journalPath = Join-Path $target 'versions\setup-transaction.jsonl'
        if (Test-Path -LiteralPath $journalPath) {
            $journal = [SetupFileSystem]::OpenJournal($journalPath)
            try { Restore-SetupTransaction $target $journal } finally { $journal.Dispose() }
        }
        if ($boundary -ne $boundaries[3]) {
            if ($scenario -eq 'upgrade') {
                Assert ((Active-Version $target) -ceq '1.2.3') 'Uncommitted rollback did not restore the predecessor.'
            } else {
                Assert (-not [SetupFileSystem]::Exists((Join-Path $target 'active'))) 'First-install rollback retained the launcher.'
            }
            Assert (-not (Test-Path -LiteralPath (Join-Path $target 'versions\1.2.4'))) 'Rollback retained the owned destination and blocks retry.'
            $retried = @(Invoke-SetupInstall $new $target)
            if ($mode -eq 'crash' -and $boundary -eq $boundaries[0]) {
                Assert (($retried -join '').Contains('Removed interrupted setup preparation:')) 'Next setup did not report interrupted preparation cleanup.'
                Assert (@([IO.Directory]::EnumerateFileSystemEntries((Join-Path $target 'versions\.staging'))).Count -eq 0) 'Next setup retained interrupted preparation.'
            }
        }
        Assert ((Active-Version $target) -ceq '1.2.4') 'Install retry failed.'
    }
}
}

$changedCase = Join-Path $Root 'changed-before-publication'
$changedInstall = Join-Path $changedCase 'installation'
Invoke-SetupInstall $old $changedInstall
$changedSource = $script.Replace($boundaries[1], ('            [IO.File]::WriteAllText((Join-Path $stage ''content.txt''), ''foreign bytes'')' + "`r`n" + $boundaries[1]))
Assert ((Invoke-SetupVariant $changedSource $changedCase $new $changedInstall) -ne 0) 'Changed payload was published.'
Assert ((Active-Version $changedInstall) -ceq '1.2.3') 'Changed payload switched the active launcher.'
Assert ([IO.File]::ReadAllText((Join-Path $changedInstall 'versions\1.2.4\content.txt')) -ceq 'foreign bytes') 'Rollback erased a changed payload.'
$blockedJournal = Join-Path $changedInstall 'versions\setup-transaction.jsonl'
Assert ([SetupFileSystem]::Exists($blockedJournal)) 'Persistent obstruction discarded its recovery journal.'
$journalBytes = [IO.File]::ReadAllBytes($blockedJournal)
$stagingPath = Join-Path $changedInstall 'versions\.staging'
$preparationBefore = @([IO.Directory]::EnumerateFileSystemEntries($stagingPath) | Sort-Object) -join "`n"
# With no later invocation the first child has exited with an explicit user action, not an automatic wait.
Assert ([IO.File]::ReadAllText((Join-Path $changedCase 'stderr.log')).Contains('run setup again after correcting the reported obstruction')) 'Missing retry trigger left an unreported automatic wait.'
foreach ($attempt in @('first-retry', 'repeated-retry')) {
    $retryCase = Join-Path $changedCase $attempt
    Assert ((Invoke-SetupVariant $script $retryCase $new $changedInstall) -ne 0) 'Persistent obstruction was silently accepted.'
    Assert ([IO.File]::ReadAllText((Join-Path $retryCase 'stderr.log')).Contains('run setup again after correcting the reported obstruction')) 'Persistent obstruction did not report a terminal user action.'
    Assert ([SetupFileSystem]::Equal([IO.File]::ReadAllBytes($blockedJournal), $journalBytes)) 'Persistent obstruction changed its pending recovery record.'
    Assert ((@([IO.Directory]::EnumerateFileSystemEntries($stagingPath) | Sort-Object) -join "`n") -ceq $preparationBefore) 'Persistent obstruction accumulated preparation.'
    Assert ((Active-Version $changedInstall) -ceq '1.2.3') 'Persistent obstruction changed the active predecessor.'
    Assert ([IO.File]::ReadAllText((Join-Path $changedInstall 'versions\1.2.4\content.txt')) -ceq 'foreign bytes') 'Persistent obstruction erased changed content.'
}
# Restore the fixture's original bytes in place, retaining the recorded file identity.
[IO.File]::WriteAllText((Join-Path $changedInstall 'versions\1.2.4\content.txt'), '1.2.4')
Invoke-SetupInstall $new $changedInstall
Assert ((Active-Version $changedInstall) -ceq '1.2.4') 'Corrected obstruction did not permit install retry.'
Assert (-not [SetupFileSystem]::Exists($blockedJournal)) 'Corrected obstruction retained its recovery journal.'
Assert (@([IO.Directory]::EnumerateFileSystemEntries($stagingPath)).Count -eq 0) 'Corrected obstruction retained preparation.'

# The detached janitor must fail visibly and retain a log, rather than swallowing locked-file errors.
$download = Join-Path $Root 'download'; [IO.Directory]::CreateDirectory($download) | Out-Null
$locked = [IO.File]::Open((Join-Path $download 'locked.exe'), 'CreateNew', 'ReadWrite', 'None')
try {
    $env:SYNCSHARK_SETUP_DIR = $download
    $env:SYNCSHARK_SETUP_PID = [int]::MaxValue
    $env:SYNCSHARK_SETUP_CLEANUP_LOG = Join-Path $Root 'cleanup.log'
    $env:SYNCSHARK_SETUP_RETAINED_MESSAGE = [Massdex.SyncShark.SetupMessages]::Text('cleanup.retained', [object[]]@('{0}','{1}'))
    $env:SYNCSHARK_SETUP_LOG_MESSAGE = [Massdex.SyncShark.SetupMessages]::Text('cleanup.log', [object[]]@('{0}','{1}'))
    $cleanup = Start-Process powershell.exe -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File', ('"' + (Join-Path $repository 'Installer/cleanup.ps1') + '"')) -Wait -PassThru -RedirectStandardError (Join-Path $Root 'cleanup.stderr')
    Assert ($cleanup.ExitCode -ne 0) 'Detached cleanup hid a retained download.'
    Assert ([IO.File]::ReadAllText($env:SYNCSHARK_SETUP_CLEANUP_LOG).Contains($download)) 'Detached cleanup log omitted the retained path.'
} finally { $locked.Dispose() }
$manifest = [xml][IO.File]::ReadAllText((Join-Path $repository 'Installer/setup.manifest'))
Assert ($manifest.assembly.trustInfo.security.requestedPrivileges.requestedExecutionLevel.level -ceq 'asInvoker') 'Setup elevation contract changed.'
Assert ([IO.File]::ReadAllText((Join-Path $repository 'Installer/SyncSharkSetup.cs')).Contains('-ExecutionPolicy Bypass')) 'Setup lost process-scoped policy.'
$published = Join-Path $Root 'payload-1.2.2'
foreach ($member in @('SyncShark.dll','SyncShark.Core.dll','SyncShark.Engine.dll','SyncShark.deps.json','SyncShark.runtimeconfig.json')) {
    [IO.File]::WriteAllText((Join-Path $published $member), 'synthetic build input')
}
$built = Join-Path $Root 'built-setup'
& (Join-Path $repository 'tools/Build-SyncSharkSetup.ps1') -PublishedApp $published -OutputDirectory $built
$shell = Start-Process (Join-Path $built 'SyncShark-Setup-v1.2.2.exe') -ArgumentList '--fixture-invalid-argument' -PassThru -Wait -RedirectStandardError (Join-Path $Root 'shell.stderr')
try {
    Assert ($shell.ExitCode -eq 1) 'Standalone setup shell accepted unknown arguments.'
    Assert ([IO.File]::ReadAllText((Join-Path $Root 'shell.stderr')).Contains([Massdex.SyncShark.SetupMessages]::Text('shell.arguments'))) 'Standalone setup shell lost its embedded message resource.'
} finally { $shell.Dispose() }
Write-Host "Setup checks passed; unelevated=$RequireNonAdmin; retained synthetic evidence: $Root"
& cmd /c exit 0
