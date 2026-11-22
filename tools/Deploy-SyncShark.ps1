# One-process owner-present deploy of a Sync Shark build (DESIGN-ONE-DEPLOY-11A with its addenda).
# Deploy:  pwsh -File <candidate outputDirectory>\tools\Deploy-SyncShark.ps1 -Build <candidate build root> -LauncherDir <launcher folder>
# Recover: ... -Recover          Close a needs-owner transaction: ... -Close -Disposition <d> [-Layout old|new|none]
# Operating condition: the owner is present and does not use the GUI, CLI, launchers or Repair until the transaction is closed.
# Trust (coordinator ruling, review finding 6): the six product binaries carry the pinned Authenticode signature; every
# other file is covered by the manifest SHA256 tree check. Backups keep file and directory attributes and creation and
# last-write times; ACLs are not copied (the data folder inherits its ACL). Launcher writes are temp then
# File.Move(overwrite) rename-over (accepted amendment): the launchers are small .ps1 files whose metadata doesn't matter.
[CmdletBinding(DefaultParameterSetName = 'Deploy')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Deploy')][string]$Build,
    [Parameter(Mandatory, ParameterSetName = 'Deploy')][string]$LauncherDir,
    [Parameter(Mandatory, ParameterSetName = 'Recover')][switch]$Recover,
    [Parameter(Mandatory, ParameterSetName = 'Close')][switch]$Close,
    [Parameter(Mandatory, ParameterSetName = 'Close')]
    [ValidateSet('restored-not-started', 'live-old', 'live-new', 'stop-refused', 'pointer-unproven')][string]$Disposition,
    [Parameter(ParameterSetName = 'Close')][ValidateSet('old', 'new', 'none')][string]$Layout,
    [string]$TestProductRoot,
    [string]$VerificationObservationPath,
    [string]$CrashAfter = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$testMode = -not [string]::IsNullOrWhiteSpace($TestProductRoot)
if (($VerificationObservationPath -or $CrashAfter) -and -not $testMode) {
    throw 'Signature injection and crash points are limited to the isolated release-test seam.'
}
Remove-Item Env:HTTPS_PROXY, Env:HTTP_PROXY -ErrorAction SilentlyContinue
# Before any module code runs, built-in operations only: this script and the module must be the bytes listed by the
# manifest of the build whose outputDirectory holds this tools folder (consistency with the manifest, not provenance).
function Get-FullPath([string]$Path) { [IO.Path]::GetFullPath($Path).TrimEnd('\', '/') }
$toolsDir = Get-FullPath $PSScriptRoot
$ownOut = Split-Path -Parent $toolsDir
$ownManifest = $null
for ($dir = Split-Path -Parent $ownOut; $dir; $dir = Split-Path -Parent $dir) {
    try {
        $found = @(Get-ChildItem -LiteralPath $dir -Filter '*-manifest.json' -File -Force -ErrorAction SilentlyContinue)
        if ($found.Count -ne 1) { continue }
        $sidecar = $found[0].FullName -replace '\.json$', '.sha256'
        $sha = (Get-FileHash -LiteralPath $found[0].FullName -Algorithm SHA256).Hash
        if ((([IO.File]::ReadAllText($sidecar).Trim()) -split '\s+')[0] -ine $sha) { continue }
        $parsed = Get-Content -LiteralPath $found[0].FullName -Raw | ConvertFrom-Json -AsHashtable -Depth 20
        if ((Get-FullPath ([string]$parsed['outputDirectory'])) -ine $ownOut) { continue }
        $ownManifest = @{ path = $found[0].FullName; root = (Get-FullPath $dir); files = @($parsed['files']) }
        break
    } catch { continue }
}
if ($null -eq $ownManifest) { throw 'This script must run from <outputDirectory>\tools of a build whose manifest lists it.' }
$modulePath = Join-Path $toolsDir 'SyncShark.Release.psm1'
foreach ($pair in @(@('tools/SyncShark.Release.psm1', $modulePath), @('tools/Deploy-SyncShark.ps1', $PSCommandPath))) {
    $entry = @($ownManifest.files | Where-Object { ([string]$_['path']).Replace('\', '/') -ieq $pair[0] })
    if ($entry.Count -ne 1) { throw "The candidate manifest does not list $($pair[0])." }
    if ((Get-FileHash -LiteralPath $pair[1] -Algorithm SHA256).Hash -ine [string]$entry[0]['sha256']) { throw "$($pair[0]) differs from its candidate manifest entry." }
}
if ($Build -and (Get-FullPath $Build) -ine $ownManifest.root) { throw 'This script must run from the -Build candidate''s own outputDirectory\tools.' }
Write-Host '[deploy] Tools: consistency with the build manifest, not provenance.'
Import-Module $modulePath -Force
$pauseLine = 'Pauses are not preserved across a deploy; re-pause pairs after.'
$liveLabel = 'operational health only; full DB integrity unverified'
$runKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$runNames = @('Sync Shark AutoSync', 'Sync Shark')
$productBinaries = @('SyncShark.exe', 'SyncShark.dll', 'SyncShark.Core.dll', 'SyncShark.Engine.dll', 'SyncSharkDiscovery.exe', 'SyncShark.Explorer.dll')
$launcherSpecs = @(
    @{ name = 'cli-common.ps1'; pattern = '(?m)^(\s*\$Exe\s*=\s*'')([^'']*)('')' },
    @{ name = 'run-apply.ps1'; pattern = '(?m)(\[string\]\$Exe\s*=\s*'')([^'']*)('')' })
$unsafeAttributes = 0x441400 # ReparsePoint, Offline, RecallOnOpen, RecallOnDataAccess
$script:injected = $null
if ($VerificationObservationPath) { $script:injected = Get-Content -LiteralPath $VerificationObservationPath -Raw | ConvertFrom-Json -AsHashtable -Depth 20 }

if (-not ('SyncSharkDeployNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
public static class SyncSharkDeployNative {
    public static readonly ManualResetEvent Cancel = new ManualResetEvent(false);
    static int hooked;
    sealed class ProcessWait : WaitHandle { public ProcessWait(Process p) { SafeWaitHandle = new SafeWaitHandle(p.Handle, false); } }
    public static void HookCancel() {
        if (Interlocked.Exchange(ref hooked, 1) == 0) Console.CancelKeyPress += (s, e) => { e.Cancel = true; Cancel.Set(); };
    }
    // Waits until EVERY handle is signalled (WaitAll is unusable on an STA thread); false when Ctrl+C stopped the wait. Kills nothing.
    public static bool WaitAll(Process[] processes) {
        var pending = new List<WaitHandle>();
        foreach (var p in processes) pending.Add(new ProcessWait(p));
        while (pending.Count > 0) {
            var set = new List<WaitHandle>(pending);
            set.Add(Cancel);
            int index = WaitHandle.WaitAny(set.ToArray());
            if (index == pending.Count) return false;
            pending.RemoveAt(index);
        }
        return true;
    }
    public static Process Start(string exe, string arguments, string log, StringBuilder output) {
        var process = new Process { StartInfo = new ProcessStartInfo(exe, arguments) {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        var gate = new object();
        process.OutputDataReceived += (s, e) => Sink(gate, log, output, e.Data, false);
        process.ErrorDataReceived += (s, e) => Sink(gate, log, output, e.Data, true);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }
    static void Sink(object gate, string log, StringBuilder output, string line, bool error) {
        if (line == null) return;
        lock (gate) {
            if (!error) output.AppendLine(line);
            File.AppendAllText(log, (error ? "stderr " : "stdout ") + line + Environment.NewLine);
            (error ? Console.Error : Console.Out).WriteLine(line);
        }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct Info { public uint Attributes; public long Created, Accessed, Written; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
    // NTFS file ID (volume + index), which a rename keeps. Opens with no data access.
    public static string FileId(string path) {
        using (var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
            if (handle.IsInvalid) throw new IOException("Cannot open " + path, Marshal.GetLastWin32Error());
            Info info;
            if (!GetFileInformationByHandle(handle, out info)) throw new IOException("Cannot read the file ID of " + path, Marshal.GetLastWin32Error());
            return string.Format("{0:x8}-{1:x8}{2:x8}", info.Volume, info.IndexHigh, info.IndexLow);
        }
    }
}
'@
}
[SyncSharkDeployNative]::HookCancel()

function Say([string]$Text) { Write-Host "[deploy] $Text" }
function Utc { [DateTime]::UtcNow.ToString('O') }
function Ticks($Value) {
    if ($Value -is [DateTime]) { return $Value.ToUniversalTime().Ticks }
    [DateTime]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture, 'RoundtripKind').ToUniversalTime().Ticks
}
function Crash([string]$Name) { Invoke-SyncSharkCrashPoint $Name $CrashAfter }
function Same([string]$A, [string]$B) { (Get-SyncSharkNormalizedPath $A) -ieq (Get-SyncSharkNormalizedPath $B) }
function Test-Contains([string]$Outer, [string]$Inner) {
    $a = Get-SyncSharkNormalizedPath $Outer; $b = Get-SyncSharkNormalizedPath $Inner
    $b -ieq $a -or $b.StartsWith($a + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -Depth 30 }
function Get-BytesSha([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
function Get-FileId([string]$Path) { if (Test-Path -LiteralPath $Path) { [SyncSharkDeployNative]::FileId($Path) } else { $null } }
function NeedsOwner([string]$Message) { throw "NEEDS-OWNER: $Message" }
function Assert-Isolated([string[]]$Paths) {
    # Test mode: every resolved path lies physically inside the test root (no reparse point anywhere on its chain),
    # checked before any child process or write that uses it.
    if (-not $testMode) { return }
    $root = Get-SyncSharkNormalizedPath $installationRoot
    foreach ($path in @($Paths | Where-Object { $_ })) {
        $full = Get-SyncSharkNormalizedPath $path
        if (-not ($full -ieq $root -or $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase))) { throw "Test mode: $path lies outside the test root." }
        for ($item = $full; $item; $item = Split-Path -Parent $item) {
            if ((Test-Path -LiteralPath $item) -and ([int](Get-Item -LiteralPath $item -Force).Attributes -band 0x400) -ne 0) {
                throw "Test mode: $path passes through the reparse point $item."
            }
        }
    }
}
function Get-SidePaths($Side) { if ($Side) { $Side.root; $Side.manifestPath; $Side.outputDirectory; $Side.exe } }

function Write-Record([string]$Path, [object]$Value, [string]$Prefix) {
    # The module's atomic writer (temp, flush, rename). A leftover temp is this tool's own uncommitted write.
    $temporary = $Path + '.new'
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
    Write-SyncSharkJournal $Path $Value $CrashAfter $Prefix
}
function Read-Journal([string]$Path) {
    # Read-only: an uncommitted .new is ignored here and discarded only by the writer (Write-Record).
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { Read-Json $Path } catch { $null }
}

function Get-LauncherExe([string]$Text, [string]$Pattern, [string]$Path) {
    $found = [regex]::Matches($Text, $Pattern)
    if ($found.Count -ne 1) { throw "Expected exactly one exe reference in $Path (found $($found.Count))." }
    $found[0].Groups[2].Value
}
function Assert-Parse([string]$Text, [string]$Name) {
    $tokens = $null; $errors = $null
    [void][Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw "Parse errors in $Name : $($errors.Message -join '; ')" }
}

# ---- Build records and trust (step 1) ----
function Read-BuildManifest([string]$Root) {
    $found = @(Get-ChildItem -LiteralPath $Root -Filter '*-manifest.json' -File -Force)
    if ($found.Count -ne 1) { throw "Expected exactly one *-manifest.json in $Root (found $($found.Count))." }
    $path = $found[0].FullName
    $sidecar = $path.Substring(0, $path.Length - 5) + '.sha256'
    if (-not (Test-Path -LiteralPath $sidecar -PathType Leaf)) { throw "Missing manifest sidecar: $sidecar" }
    $sha = Get-SyncSharkSha256 $path
    if ((([IO.File]::ReadAllText($sidecar).Trim()) -split '\s+')[0] -ine $sha) { throw "Build manifest hash mismatch: $path" }
    $manifest = Read-Json $path
    $out = Get-SyncSharkNormalizedPath ([string]$manifest.outputDirectory)
    [ordered]@{ root = (Get-SyncSharkNormalizedPath $Root); manifestPath = $path; manifestSha256 = $sha; outputDirectory = $out
        exe = (Join-Path $out 'SyncShark.exe'); productVersion = [string]$manifest.productVersion; version = [string]$manifest.version
        commit = [string]$manifest.commit; expectedPreviousVersion = [string]$manifest.expectedPreviousVersion; files = @($manifest.files) }
}
function Get-Observation([string]$Path) {
    if ($null -eq $script:injected) { return Get-SyncSharkSignatureObservation $Path }
    $observation = $script:injected[(Get-SyncSharkNormalizedPath $Path)]
    if ($null -eq $observation) { throw "No injected signature observation for $Path." }
    [pscustomobject]$observation
}
function Assert-Tree([System.Collections.IDictionary]$Side, [switch]$Legacy) {
    # Candidate: files[] equals the whole ordinary output tree. Legacy predecessor: every exe/dll listed (D:186-198).
    $info = Read-BuildManifest $Side.root
    if ($info.manifestSha256 -cne $Side.manifestSha256 -or -not (Same $info.outputDirectory $Side.outputDirectory)) {
        throw "Build manifest changed since admission: $($info.manifestPath)"
    }
    $out = $info.outputDirectory
    Assert-SyncSharkOrdinaryTree $out
    $listed = @{}
    foreach ($member in $info.files) {
        $relative = ([string]$member.path).Replace('\', '/')
        if (-not $relative -or [IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.\.(/|$)' -or $listed.ContainsKey($relative.ToLowerInvariant())) {
            throw 'Invalid or duplicate build manifest member.'
        }
        $listed[$relative.ToLowerInvariant()] = $member
    }
    $actual = @{}
    foreach ($file in Get-ChildItem -LiteralPath $out -File -Recurse -Force) {
        if (([int]$file.Attributes -band $unsafeAttributes) -ne 0) { throw "Reparse or offline file in build tree: $($file.FullName)" }
        $actual[[IO.Path]::GetRelativePath($out, $file.FullName).Replace('\', '/').ToLowerInvariant()] = $file
    }
    $mustList = if ($Legacy) { @($actual.Keys | Where-Object { $_ -match '\.(exe|dll)$' }) } else { @($actual.Keys) }
    foreach ($key in $mustList) { if (-not $listed.ContainsKey($key)) { throw "Unlisted file in build tree: $key" } }
    foreach ($key in $listed.Keys) {
        $file = $actual[$key]
        if ($null -eq $file) { throw "Listed build file missing: $key" }
        if ($file.Length -ne [long]$listed[$key].length -or (Get-SyncSharkSha256 $file.FullName) -ine [string]$listed[$key].sha256) {
            throw "Build member changed: $key"
        }
    }
    foreach ($name in @('syncshark.deps.json', 'syncshark.runtimeconfig.json')) {
        # D:203-205 runtime metadata: listed for the candidate, present for the legacy predecessor.
        if (-not $(if ($Legacy) { $actual.ContainsKey($name) } else { $listed.ContainsKey($name) })) { throw "Missing runtime metadata: $name" }
    }
    $signer = (Get-SyncSharkSignerIdentity).roles.publisher
    foreach ($name in $productBinaries) {
        if (-not $listed.ContainsKey($name.ToLowerInvariant())) { throw "Missing product binary: $name" }
        Assert-SyncSharkSignatureObservation (Get-Observation (Join-Path $out $name)) @($signer.thumbprints) $signer.subject $signer.subject @($signer.spki)
    }
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($Side.exe).ProductVersion -cne $Side.productVersion) { throw "Product version mismatch: $($Side.exe)" }
}
function Find-Predecessor([string[]]$LauncherPaths, [string]$ExpectedPreviousVersion) {
    # Ruling 1: the build the launchers start, found by walking up from that exe's folder. No search, no "newest".
    $exes = @(for ($i = 0; $i -lt 2; $i++) {
        if (-not (Test-Path -LiteralPath $LauncherPaths[$i] -PathType Leaf)) { throw "Launcher missing: $($LauncherPaths[$i])" }
        Get-LauncherExe ([IO.File]::ReadAllText($LauncherPaths[$i])) $launcherSpecs[$i].pattern $LauncherPaths[$i] })
    if (-not (Same $exes[0] $exes[1])) { throw 'The launchers start different executables.' }
    $outDir = Split-Path -Parent (Get-SyncSharkNormalizedPath $exes[0])
    $side = $null
    for ($dir = [IO.Directory]::GetParent($outDir); $null -ne $dir; $dir = $dir.Parent) {
        if (@(Get-ChildItem -LiteralPath $dir.FullName -Filter '*-manifest.json' -File -Force -ErrorAction SilentlyContinue).Count -ne 1) { continue }
        try { $info = Read-BuildManifest $dir.FullName } catch { continue }
        if (Same $info.outputDirectory $outDir) { $side = $info; break }
    }
    if ($null -eq $side) { throw "No predecessor build record found above $outDir." }
    if ($side.productVersion -cne $ExpectedPreviousVersion) { throw "Predecessor $($side.productVersion) is not the expected $ExpectedPreviousVersion." }
    if (-not (Same $exes[0] $side.exe)) { throw 'The launchers do not start the predecessor outputDirectory\SyncShark.exe.' }
    $digits = $side.version.Replace('.', '')
    $records = @(Get-ChildItem -LiteralPath $side.root -Directory -Filter 'deploy-attempt-*' -Force | Where-Object {
        $file = Join-Path $_.FullName 'launcher-recovery.json'
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $false }
        try { $record = Read-Json $file } catch { return $false }
        $paths = @($record.paths); $backups = @($record.backups)
        if ($paths.Count -ne 2 -or $backups.Count -ne 2) { return $false }
        $ok = $true
        for ($i = 0; $i -lt 2; $i++) {
            $expected = [string]$paths[$i] + ".pre-$digits-$($_.Name).bak"
            if (-not (Same $paths[$i] $LauncherPaths[$i]) -or -not (Same $backups[$i] $expected) -or
                -not (Test-Path -LiteralPath $expected -PathType Leaf)) { $ok = $false }
        }
        $ok })
    if ($records.Count -ne 1) { throw "Expected exactly one matching launcher recovery record in $($side.root) (found $($records.Count))." }
    $side
}

# ---- Processes (step 2) ----
function Get-Arguments([string]$CommandLine) {
    $text = $CommandLine.Trim()
    if ($text.StartsWith('"')) { return $text.Substring($text.IndexOf('"', 1) + 1).Trim() }
    $space = $text.IndexOf(' ')
    if ($space -lt 0) { '' } else { $text.Substring($space + 1).Trim() }
}
function Get-Tracked([string[]]$Exes) {
    # Pin every process started from these exact files: hold its handle, re-read it, and require the same path,
    # command line and creation time; any mismatch (PID reuse) drops it and re-enumerates.
    while ($true) {
        $found = @(); $retry = $false
        foreach ($exe in $Exes) {
            $filter = "ExecutablePath = '" + $exe.Replace('\', '\\').Replace("'", "\'") + "'"
            foreach ($cim in @(Get-CimInstance Win32_Process -Filter $filter)) {
                $process = Get-Process -Id $cim.ProcessId -ErrorAction SilentlyContinue
                if ($null -eq $process) { $retry = $true; continue }
                try { $null = $process.Handle } catch { throw "Cannot open tracked process $($cim.ProcessId)." }
                $again = Get-CimInstance Win32_Process -Filter "ProcessId = $($cim.ProcessId)"
                if ($null -eq $again -or $again.ExecutablePath -ine $exe -or $again.CommandLine -cne $cim.CommandLine -or
                    [Math]::Abs(($again.CreationDate - $process.StartTime).TotalMilliseconds) -ge 1) { $retry = $true; continue }
                $found += [pscustomobject]@{ Process = $process; Pid = $process.Id; Exe = $exe; Args = (Get-Arguments $cim.CommandLine)
                    CreatedTicks = $process.StartTime.ToUniversalTime().Ticks }
            }
        }
        if (-not $retry) { return $found }
    }
}
function Get-Generation([object[]]$Tracked) {
    # AutoSyncProcess.cs:39-45 argument shapes. Anything else (GUI, Repair, CLI run) is refused.
    $roles = @{ '--core v2 --engine-host' = 'engine-host'; '--core v2 --engine-supervisor' = 'engine-supervisor'
        '--core v2 --background-host' = 'legacy-host'; '--background' = 'legacy-host'
        '--core v2 --autosync-supervisor' = 'legacy-supervisor'; '--core v2 --legacy-autosync-supervisor' = 'legacy-supervisor' }
    if (@($Tracked | Select-Object -ExpandProperty Exe -Unique).Count -gt 1) { throw 'More than one Sync Shark generation is running.' }
    $hosts = @(); $supervisors = @()
    foreach ($item in $Tracked) {
        $role = $roles[$item.Args]
        if ($null -eq $role) { throw "A GUI, CLI or Repair process is running (pid $($item.Pid)); close it first." }
        if ($role -like '*-host') { $hosts += [pscustomobject]@{ Item = $item; Role = $role } } else { $supervisors += [pscustomobject]@{ Item = $item; Role = $role } }
    }
    if ($hosts.Count -gt 1) { throw 'More than one host process is running.' }
    if ($hosts.Count -eq 0 -and $supervisors.Count) { throw 'A supervisor is running with no host (backoff or crash loop, R3).' }
    if ($hosts.Count -and @($supervisors | Where-Object { $_.Role.Split('-')[0] -ne $hosts[0].Role.Split('-')[0] }).Count) { throw 'Engine and legacy processes are mixed.' }
    if ($hosts.Count -eq 0) { return $null }
    [pscustomobject]@{ Exe = $hosts[0].Item.Exe; Engine = ($hosts[0].Role -eq 'engine-host'); Tracked = $Tracked }
}
function Invoke-Child([string]$Purpose, [string]$Exe, [string]$Arguments) {
    $entry = [ordered]@{ purpose = $Purpose; exe = $Exe; args = $Arguments; intentUtc = (Utc); pid = $null; createdTicks = $null; exit = $null }
    $script:journal.children = @($script:journal.children) + @($entry)
    Save-Journal
    $output = [Text.StringBuilder]::new()
    $process = [SyncSharkDeployNative]::Start($Exe, $Arguments, $script:childLog, $output)
    Crash "child-$Purpose-launched"
    $entry.pid = $process.Id; $entry.createdTicks = $process.StartTime.ToUniversalTime().Ticks
    Save-Journal
    Crash "child-$Purpose-pid"
    Wait-Processes @($process)
    $process.WaitForExit()
    $entry.exit = $process.ExitCode
    Save-Journal
    [pscustomobject]@{ Exit = $process.ExitCode; Out = $output.ToString() }
}
function Wait-Processes([Diagnostics.Process[]]$Processes) {
    if ($Processes.Count -and -not [SyncSharkDeployNative]::WaitAll($Processes)) {
        throw 'CANCELLED: Ctrl+C stopped the wait; nothing was killed. The transaction stays open; run -Recover.'
    }
}
function Invoke-Cli([string]$Purpose, [string]$Exe, [string]$Arguments) {
    $result = Invoke-Child $Purpose $Exe "--cli $Arguments"
    $json = $null
    try { $json = $result.Out | ConvertFrom-Json -AsHashtable -Depth 30 } catch { }
    [pscustomobject]@{ Exit = $result.Exit; Json = $json }
}
function Get-PairsKey($Json) { if ($null -eq $Json) { return '' }; (@($Json.data.pairs) | ForEach-Object { "$($_.id)|$($_.enabled)" } | Sort-Object -CaseSensitive) -join ';' }
function Join-Children {
    # -Recover joins every journaled child still alive; an intent with no PID is matched by exe, exact args and creation after the intent.
    foreach ($child in @($script:journal.children | Where-Object { $null -eq $_.exit })) {
        if ($child.pid) {
            $live = @(Get-Tracked @($child.exe) | Where-Object { $_.Pid -eq $child.pid -and $_.CreatedTicks -eq [long]$child.createdTicks })
        } else {
            $live = @(Get-Tracked @($child.exe) | Where-Object { $_.Args -ceq $child.args -and $_.CreatedTicks -gt (Ticks $child.intentUtc) })
            if ($live.Count -gt 1) { NeedsOwner "$($live.Count) processes match the unrecorded $($child.purpose) child; none was joined or adopted." }
        }
        if ($live.Count -eq 1) {
            Say "Joining journaled child $($child.purpose) (pid $($live[0].Pid))."
            $child.pid = $live[0].Pid; $child.createdTicks = $live[0].CreatedTicks
            Wait-Processes @($live[0].Process)
            $child.exit = 'joined'
        } else { $child.exit = 'gone' }
        Save-Journal
    }
}
function Stop-Running {
    $generation = Get-Generation @(Get-Tracked $script:exes)
    if ($null -eq $generation) { return }
    $script:journal.stopRequested = Utc
    Save-Journal
    $arguments = if ($generation.Engine) { '--cli engine host stop --yes' } else { '--cli autosync stop' }
    $result = Invoke-Child 'stop' $generation.Exe $arguments
    if ($result.Exit -ne 0) { NeedsOwner "the stop exited $($result.Exit); the host may still be stopping." }
    Wait-Processes @($generation.Tracked | ForEach-Object Process)
    if (@(Get-Tracked $script:exes).Count) { throw 'A tracked Sync Shark process is running after the stop.' }
}
function Assert-StopProof {
    if (@(Get-Tracked $script:exes).Count) { throw 'Stop proof failed: a tracked Sync Shark process is running.' }
    # After swap-1 the data folder is renamed away: no host can hold a lock in it, so the process check is the proof.
    if (-not (Test-Path -LiteralPath $script:dataTarget -PathType Container)) { return }
    $stream = [IO.File]::Open($script:lockPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    $stream.Dispose()
}

# ---- Registry, launchers, health ----
function Get-RunValues {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKeyPath, $false)
    try {
        foreach ($name in $runNames) {
            $value = if ($key) { $key.GetValue($name, $null, 'DoNotExpandEnvironmentNames') } else { $null }
            if ($null -eq $value) { [ordered]@{ name = $name; present = $false; kind = $null; data = $null } }
            else { [ordered]@{ name = $name; present = $true; kind = $key.GetValueKind($name).ToString(); data = [string]$value } }
        }
    } finally { if ($key) { $key.Dispose() } }
}
function Format-RunValues($Values) { (@($Values) | ForEach-Object { "$($_.name)|$($_.present)|$($_.kind)|$($_.data)" }) -join "`n" }
function Restore-RunValues([switch]$CompareOnly) {
    # R5: the Run values end exactly as snapshotted; absent stays absent. Test mode never writes the registry.
    $snapshot = $script:journal.snapshot.runValues
    if ((Format-RunValues @(Get-RunValues)) -ceq (Format-RunValues $snapshot)) { return }
    if ($CompareOnly) { throw 'The Run values differ from the snapshot.' }
    if ($testMode) { NeedsOwner 'the Run values differ from the snapshot; test mode never writes the registry.' }
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKeyPath, $true)
    try {
        foreach ($value in @($snapshot)) {
            if (-not $value.present) { if ($null -ne $key.GetValue($value.name)) { $key.DeleteValue($value.name) } }
            else { $key.SetValue($value.name, [string]$value.data, [Microsoft.Win32.RegistryValueKind]$value.kind) }
        }
    } finally { $key.Dispose() }
    if ((Format-RunValues @(Get-RunValues)) -cne (Format-RunValues $snapshot)) { NeedsOwner 'the Run values could not be restored exactly.' }
}
function Write-Replace([string]$Path, [byte[]]$Bytes) {
    # Temp then atomic rename-over, as the module's Write-SyncSharkJournal does. File.Replace (ReplaceFile) was measured
    # failing 9/60 here on a freshly written temp (on-write scanning); rename-over failed 0/60.
    $temporary = "$Path.deploy-$($script:T).tmp"
    $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::Move($temporary, $Path, $true)
}
function Write-LauncherBackup([System.Collections.IDictionary]$Launcher) {
    # ADDENDUM-4 edit 2: journal the intent, then temp, rename, read back, done. Recovery accepts only the exact journaled bytes.
    $bak = $Launcher.bak
    if ($bak.done) { return }
    if (-not $bak.intentUtc) {
        if (Test-Path -LiteralPath $bak.path) { NeedsOwner "a file with no journaled intent exists at $($bak.path)." }
        $bak.intentUtc = Utc
        Save-Journal
    }
    if (-not (Test-Path -LiteralPath $bak.path)) {
        $temporary = "$($bak.path).$([Guid]::NewGuid().ToString('N').Substring(0, 8)).tmp"
        [IO.File]::WriteAllBytes($temporary, [Convert]::FromBase64String($Launcher.originalBase64))
        Crash 'bak-temp'
        [IO.File]::Move($temporary, $bak.path)
        Crash 'bak-renamed'
    }
    $item = Get-Item -LiteralPath $bak.path -Force
    if ($item.PSIsContainer -or ([int]$item.Attributes -band $unsafeAttributes) -ne 0 -or (Get-SyncSharkSha256 $bak.path) -cne $bak.expectedSha256) {
        NeedsOwner "the launcher backup $($bak.path) does not hold the journaled original bytes."
    }
    $bak.done = Utc
    Save-Journal
}
function Switch-Launcher([System.Collections.IDictionary]$Launcher, [int]$Index) {
    $Launcher.switchState = [ordered]@{ intentUtc = (Utc); done = $null }
    Save-Journal
    $current = Get-SyncSharkSha256 $Launcher.path
    if ($current -ceq $Launcher.originalSha256) { Write-Replace $Launcher.path ([Convert]::FromBase64String($Launcher.newBase64)) }
    elseif ($current -cne $Launcher.newSha256) { NeedsOwner "foreign launcher content in $($Launcher.path)." }
    if ((Get-SyncSharkSha256 $Launcher.path) -cne $Launcher.newSha256) { throw "Launcher switch did not verify: $($Launcher.path)" }
    Crash "launcher-switched-$Index"
    $Launcher.switchState.done = Utc
    Save-Journal
}
function Restore-Launchers {
    # Per file by hash: original = done; new = write the original; anything else = needs-owner.
    foreach ($launcher in @($script:journal.launchers)) {
        if ($launcher.restore -and $launcher.restore.done) { continue }
        $launcher.restore = [ordered]@{ intentUtc = (Utc); done = $null }
        Save-Journal
        $current = Get-SyncSharkSha256 $launcher.path
        if ($current -ceq $launcher.newSha256) { Write-Replace $launcher.path ([Convert]::FromBase64String($launcher.originalBase64)) }
        elseif ($current -cne $launcher.originalSha256) { NeedsOwner "foreign launcher content in $($launcher.path)." }
        if ((Get-SyncSharkSha256 $launcher.path) -cne $launcher.originalSha256) { throw "Launcher restore did not verify: $($launcher.path)" }
        $launcher.restore.done = Utc
        Save-Journal
    }
}
function Assert-Launchers([string]$Side) {
    foreach ($launcher in @($script:journal.launchers)) {
        $expected = if ($Side -eq 'new') { $launcher.newSha256 } else { $launcher.originalSha256 }
        if ((Get-SyncSharkSha256 $launcher.path) -cne $expected) { throw "Launcher $($launcher.path) is not on the $Side side." }
    }
}
function Assert-NoNewAutoSyncFailure {
    $since = Ticks $script:journal.snapshot.utc
    foreach ($name in @('autosync.log', 'autosync.log.previous')) {
        $path = Join-Path $script:dataTarget "v2\logs\autosync\$name"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $reader = [IO.StreamReader]::new([IO.File]::Open($path, 'Open', 'Read', 'ReadWrite, Delete'))
        try {
            while ($null -ne ($line = $reader.ReadLine())) {
                if ($line -match '^(\S+) .*lifecycle failure' -and (Ticks $Matches[1]) -ge $since) { throw "New AutoSync failure: $line" }
            }
        } finally { $reader.Dispose() }
    }
}
function Test-Health([string]$Side, [switch]$CompareOnly) {
    $build = if ($Side -eq 'new') { $script:journal.candidate } else { $script:journal.predecessor }
    $status = Invoke-Cli "$Side-host-status" $build.exe 'engine host status --json'
    if ($status.Exit -ne 0 -or $null -eq $status.Json -or -not $status.Json.ok -or $status.Json.error -or
        [string]$status.Json.data.state -in @('Off', 'Unavailable')) { throw "The $Side host did not report a healthy state." }
    $hosts = @(Get-Tracked @($build.exe) | Where-Object Args -ceq '--core v2 --engine-host')
    if ($hosts.Count -ne 1) { throw "Expected one pinned $Side host process (found $($hosts.Count))." }
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($build.exe).ProductVersion -cne $build.productVersion) { throw "The $Side host runs the wrong version." }
    $pairs = Invoke-Cli "$Side-pairs" $build.exe 'engine pairs list --json'
    if ($pairs.Exit -ne 0 -or (Get-PairsKey $pairs.Json) -cne $script:journal.snapshot.pairs) { throw 'The pairs differ from the snapshot.' }
    Assert-NoNewAutoSyncFailure
    Restore-RunValues -CompareOnly:$CompareOnly
}

function Get-RecoveryRoot {
    # SyncTransferSettings.LocalRecoveryRoot (EnginePaths.cs:5) from <data>\state\sync-product-state-a|b.json, read the way
    # DualSlotJsonStore.cs does: schema 1, payload SHA256 over the Value text, highest valid generation wins.
    # No slot, or no root set = absent ($null). Slots present but none valid = unavailable (refused).
    $best = $null; $exists = $false
    foreach ($slot in 'a', 'b') {
        $path = Join-Path $script:dataTarget "state\sync-product-state-$slot.json"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $exists = $true
        try {
            $reader = [IO.StreamReader]::new([IO.File]::Open($path, 'Open', 'Read', 'ReadWrite'))
            try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $document = [Text.Json.JsonDocument]::Parse([string]$text, [Text.Json.JsonDocumentOptions]::new())
            try { $valueText = $document.RootElement.GetProperty('Value').GetRawText() } finally { $document.Dispose() }
            $record = $text | ConvertFrom-Json -AsHashtable -Depth 64
            if ($record['SchemaVersion'] -ne 1 -or [long]$record['Generation'] -le 0 -or $record['Value'] -isnot [Collections.IDictionary] -or
                (Get-BytesSha ([Text.Encoding]::UTF8.GetBytes($valueText))) -cne ([string]$record['PayloadSha256']).ToLowerInvariant()) { continue }
            $transfer = $record['Value']['TransferSettings']
            $root = if ($transfer -is [Collections.IDictionary]) { [string]$transfer['LocalRecoveryRoot'] } else { '' }
            if ($null -eq $best -or [long]$record['Generation'] -gt $best.generation) { $best = @{ generation = [long]$record['Generation']; root = $root } }
        } catch { continue }
    }
    if ($exists -and $null -eq $best) { throw 'The product settings store is unavailable (no valid slot), so the Recovery root is unknown: stop and ask the owner.' }
    if ($best -and -not [string]::IsNullOrWhiteSpace($best.root)) { return [IO.Path]::GetFullPath($best.root.Trim()) }
    $null
}

# ---- Data: scan, copy, capacity, restore (steps 3, 7) ----
function Measure-Data {
    $bytes = 0L
    foreach ($item in Get-ChildItem -LiteralPath $script:dataTarget -Recurse -Force) {
        if (([int]$item.Attributes -band $unsafeAttributes) -ne 0) { throw "Reparse or offline entry in the data folder: $($item.FullName)" }
        if (-not $item.PSIsContainer) { $bytes += $item.Length }
    }
    $bytes
}
function Get-FreeBytes([string]$Path) { [IO.DriveInfo]::new([IO.Path]::GetPathRoot((Get-SyncSharkNormalizedPath $Path))).AvailableFreeSpace }
function Assert-Capacity([long]$BackupNeed, [long]$StagingNeed) {
    # ADDENDUM-3 edit 3: only bytes not yet written count; the two needs are summed only on one volume.
    $need = @{}
    foreach ($pair in @(@($script:deployRoot, $BackupNeed), @($script:dataTarget, $StagingNeed))) {
        $volume = [IO.Path]::GetPathRoot((Get-SyncSharkNormalizedPath $pair[0])).ToUpperInvariant()
        $need[$volume] = [long]$need[$volume] + [long]$pair[1]
    }
    foreach ($volume in $need.Keys) {
        $free = Get-FreeBytes $volume
        if ($free -lt $need[$volume]) { throw "Not enough free space on $volume (need $($need[$volume]) bytes, have $free)." }
    }
}
function Copy-DataTree([string]$Source, [string]$Destination, [string]$CrashName, [switch]$Resume) {
    # Each file is read once and hashed while written. The held instance lock is read through its own handle.
    $sourceFiles = @(Get-ChildItem -LiteralPath $Source -File -Recurse -Force)
    [void][IO.Directory]::CreateDirectory($Destination)
    foreach ($directory in Get-ChildItem -LiteralPath $Source -Directory -Recurse -Force) {
        [void][IO.Directory]::CreateDirectory((Join-Path $Destination ([IO.Path]::GetRelativePath($Source, $directory.FullName))))
    }
    if ($Resume) {
        $known = @{}; foreach ($file in $sourceFiles) { $known[[IO.Path]::GetRelativePath($Source, $file.FullName).ToLowerInvariant()] = $true }
        foreach ($file in Get-ChildItem -LiteralPath $Destination -File -Recurse -Force) {
            if (-not $known.ContainsKey([IO.Path]::GetRelativePath($Destination, $file.FullName).ToLowerInvariant())) { NeedsOwner "unknown extra file in staging: $($file.FullName)" }
        }
    }
    $buffer = [byte[]]::new(1MB)
    $entries = foreach ($file in $sourceFiles) {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $target = Join-Path $Destination $relative
        $isLock = $null -ne $script:lockStream -and (Same $file.FullName $script:lockPath)
        $reader = if ($isLock) { $script:lockStream.Position = 0; $script:lockStream } else { [IO.File]::Open($file.FullName, 'Open', 'Read', 'Read') }
        if ($Resume -and (Test-Path -LiteralPath $target)) { [IO.File]::SetAttributes($target, [IO.FileAttributes]::Normal) }
        $mode = if ($Resume) { [IO.FileMode]::Create } else { [IO.FileMode]::CreateNew }
        $output = [IO.FileStream]::new($target, $mode, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $hash = [Security.Cryptography.IncrementalHash]::CreateHash('SHA256')
        try {
            while (($read = $reader.Read($buffer, 0, $buffer.Length)) -gt 0) { $hash.AppendData($buffer, 0, $read); $output.Write($buffer, 0, $read) }
            $output.Flush($true)
        } finally { $output.Dispose(); if (-not $isLock) { $reader.Dispose() }; }
        Copy-Metadata $file $target
        [pscustomobject]@{ path = $relative.Replace('\', '/'); length = $file.Length; sha256 = [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant() }
        $hash.Dispose()
        Crash $CrashName
    }
    # Directories last, deepest first, so creating their children no longer changes the copied times.
    foreach ($directory in @(@(Get-ChildItem -LiteralPath $Source -Directory -Recurse -Force) + @(Get-Item -LiteralPath $Source -Force)) | Sort-Object { $_.FullName.Length } -Descending) {
        Copy-Metadata $directory (Join-Path $Destination ([IO.Path]::GetRelativePath($Source, $directory.FullName)))
    }
    @($entries)
}
function Copy-Metadata($Item, [string]$Target) {
    $keep = [int]$Item.Attributes -band 0x2027 # ReadOnly, Hidden, System, Archive, NotContentIndexed
    $info = if ($Item.PSIsContainer) { [IO.DirectoryInfo]::new($Target) } else { [IO.FileInfo]::new($Target) }
    $info.Attributes = [IO.FileAttributes]::Normal
    $info.CreationTimeUtc = $Item.CreationTimeUtc
    $info.LastWriteTimeUtc = $Item.LastWriteTimeUtc
    $info.Attributes = if ($keep) { [IO.FileAttributes]$keep } elseif ($Item.PSIsContainer) { [IO.FileAttributes]::Directory } else { [IO.FileAttributes]::Normal }
}
function Format-Manifest($Entries) { (@($Entries) | ForEach-Object { '{0}|{1}|{2}' -f $_.path, $_.length, ([string]$_.sha256).ToLowerInvariant() } | Sort-Object -CaseSensitive) -join "`n" }
function Move-Folder([string]$From, [string]$To) {
    Assert-Fresh
    try { [IO.Directory]::Move($From, $To) } catch { NeedsOwner "the rename $From -> $To failed (open handle?): $($_.Exception.Message)" }
}
function Assert-StagingReady {
    # 6A 7.4 and 7A E1: the recorded staging ID and a re-streamed manifest equal to the backup.
    $restore = $script:journal.restore
    if ((Get-FileId $restore.staging) -cne $restore.stagingId) { NeedsOwner 'the staging folder is not the recorded one.' }
    if ((Get-SyncSharkTreeIdentity $restore.staging) -cne $script:journal.backup.manifestSha256) { NeedsOwner 'the staging content differs from the backup manifest.' }
}
function Invoke-StagedRestore {
    $restore = $script:journal.restore
    $snapshotId = $script:journal.snapshot.dataFileId
    if (-not $restore.stagingIntentUtc) {
        Assert-Capacity 0 $script:journal.backup.bytes
        if ((Test-Path -LiteralPath $restore.staging) -or (Test-Path -LiteralPath $restore.failed)) { NeedsOwner 'a staging or failed folder for this transaction already exists.' }
        $restore.stagingIntentUtc = Utc
        Save-Journal
    }
    if (-not $restore.stagingId) {
        if (Test-Path -LiteralPath $restore.staging) { NeedsOwner 'the staging folder exists with no recorded ID; nothing unrecorded is adopted.' }
        [void][IO.Directory]::CreateDirectory($restore.staging)
        Crash 'staging-created'
        $restore.stagingId = Get-FileId $restore.staging
        Save-Journal
    }
    if (-not (Has 'restore-staged')) {
        if ((Get-FileId $restore.staging) -cne $restore.stagingId) { NeedsOwner 'the staging folder is not the recorded one.' }
        $present = 0L; foreach ($file in Get-ChildItem -LiteralPath $restore.staging -File -Recurse -Force) { $present += $file.Length }
        Assert-Capacity 0 ([Math]::Max(0L, [long]$script:journal.backup.bytes - $present))
        $null = Copy-DataTree $script:journal.backup.path $restore.staging 'staging-file' -Resume
        if ((Get-SyncSharkTreeIdentity $restore.staging) -cne $script:journal.backup.manifestSha256) { NeedsOwner 'the staged copy does not verify against the backup manifest.' }
        Mark 'restore-staged'
    }
    if (-not (Has 'swap-1')) {
        if ((Test-Path -LiteralPath $script:dataTarget) -and (Get-FileId $script:dataTarget) -ceq $snapshotId -and -not (Test-Path -LiteralPath $restore.failed)) {
            Assert-StagingReady
            $restore.swap1IntentUtc = Utc
            Save-Journal
            Move-Folder $script:dataTarget $restore.failed
            Crash 'swap-1-renamed'
        } elseif (-not ($restore.swap1IntentUtc -and -not (Test-Path -LiteralPath $script:dataTarget) -and (Get-FileId $restore.failed) -ceq $snapshotId)) {
            NeedsOwner 'unknown data state before swap-1.'
        }
        Mark 'swap-1'
    }
    if (-not (Has 'swap-2')) {
        if (-not ((Test-Path -LiteralPath $script:dataTarget) -and (Get-FileId $script:dataTarget) -ceq $restore.stagingId)) {
            if ((Test-Path -LiteralPath $script:dataTarget) -or (Get-FileId $restore.failed) -cne $snapshotId) { NeedsOwner 'unknown data state before swap-2.' }
            Assert-StagingReady
            $restore.swap2IntentUtc = Utc
            Save-Journal
            Move-Folder $restore.staging $script:dataTarget
            Crash 'swap-2-renamed'
        }
        $script:journal.dataFileId = $restore.stagingId
        Mark 'swap-2'
    }
    if ((Get-SyncSharkTreeIdentity $script:dataTarget) -cne $script:journal.backup.manifestSha256) { NeedsOwner 'the restored data does not match the backup manifest.' }
}

# ---- Journal, pointer, records ----
function Has([string]$Phase) { @($script:journal.phases | Where-Object { $_.name -ceq $Phase }).Count -gt 0 }
function Mark([string]$Phase) {
    $script:journal.phases = @($script:journal.phases) + @([ordered]@{ name = $Phase; utc = (Utc); ms = $script:phaseWatch.ElapsedMilliseconds })
    $script:phaseWatch.Restart()
    Save-Journal
    Crash "phase-$Phase"
}
function Test-PointerNamesT {
    if (-not (Test-Path -LiteralPath $script:pointerPath -PathType Leaf)) { return $false }
    try { $pointer = Read-Json $script:pointerPath } catch { return $false }
    $pointer.T -ceq $script:T -and $pointer.rootId -ceq $script:rootId
}
function Assert-Fresh {
    # Under the mutex, re-read the pointer and journal before every change or rename.
    if (-not (Test-PointerNamesT)) { NeedsOwner 'the pointer no longer names this transaction; nothing further was done.' }
    $disk = Read-Journal $script:journalPath
    if ($null -eq $disk -or $disk.T -cne $script:T -or [long]$disk.revision -ne [long]$script:journal.revision) {
        NeedsOwner 'the journal changed on disk outside this run; nothing further was done.'
    }
}
function Save-Journal {
    Assert-Fresh
    $script:journal.revision = [long]$script:journal.revision + 1
    Write-Record $script:journalPath $script:journal 'journal'
}
function Set-Paths([string]$TransactionId) {
    $script:T = $TransactionId
    $script:txRoot = Join-Path $script:deployRoot $TransactionId
    $script:journalPath = Join-Path $script:txRoot 'journal.json'
    $script:childLog = Join-Path $script:txRoot 'children.log'
}
function Get-PointerState {
    # L2: (a) none, (b) open, (c) closed = remove the pointer, (d) anything else = needs-owner, pointer untouched.
    $open = @(Get-ChildItem -LiteralPath $script:deployRoot -Directory -Force -ErrorAction SilentlyContinue | ForEach-Object {
        $journal = Read-Journal (Join-Path $_.FullName 'journal.json')
        if ($journal -and $journal.rootId -ceq $script:rootId -and $journal.state -cne 'closed') { $journal.T } })
    if (-not (Test-Path -LiteralPath $script:pointerPath)) {
        if ($open.Count) { return @{ kind = 'bad'; reason = "no pointer while journal $($open -join ', ') is open." } }
        return @{ kind = 'none' }
    }
    $malformed = @{ kind = 'bad'; reason = 'the pointer is malformed.' }
    try { $pointer = Read-Json $script:pointerPath } catch { return $malformed }
    if ($pointer -isnot [System.Collections.IDictionary]) { return $malformed }
    foreach ($key in 'rootId', 'T', 'journalPath', 'installationRoot', 'dataTarget') { if (-not $pointer.Contains($key)) { return $malformed } }
    if ($pointer.rootId -cne $script:rootId -or [string]$pointer.T -cnotmatch '^\d{8}-\d{6}-\d{3}-[0-9a-f]{8}$' -or
        -not (Same ([string]$pointer.journalPath) (Join-Path $script:deployRoot "$($pointer.T)\journal.json"))) { return $malformed }
    if (-not (Same ([string]$pointer.installationRoot) $installationRoot) -or ($script:dataTarget -and -not (Same ([string]$pointer.dataTarget) $script:dataTarget))) {
        return @{ kind = 'bad'; reason = 'the pointer names a different installation root or data target.' }
    }
    $journal = Read-Journal ([string]$pointer.journalPath)
    if ($null -eq $journal) { return @{ kind = 'bad'; reason = "the pointer names T=$($pointer.T) but no journal exists." } }
    if ($journal.T -cne $pointer.T -or $journal.rootId -cne $script:rootId -or -not (Same $journal.installationRoot $installationRoot) -or
        ($script:dataTarget -and -not (Same $journal.dataTarget $script:dataTarget))) {
        return @{ kind = 'bad'; reason = 'the journal does not match the pointer, installation root or data target.' }
    }
    @{ kind = $(if ($journal.state -ceq 'closed') { 'closed' } else { 'open' }); journal = $journal; T = [string]$pointer.T }
}
function Remove-Pointer {
    # The only automatic pointer change (L2 c): the closed journal's own last step.
    if (-not (Test-PointerNamesT)) { NeedsOwner 'the pointer no longer names this transaction.' }
    $disk = Read-Journal $script:journalPath
    if ($null -eq $disk -or $disk.state -cne 'closed') { NeedsOwner 'the journal is not closed; the pointer is kept.' }
    Remove-Item -LiteralPath $script:pointerPath -Force
}
function Write-Receipt {
    $receipt = [ordered]@{ transaction = $script:T; terminal = $script:journal.terminal; closedUtc = $script:journal.closedUtc; limitation = $pauseLine
        needsOwner = $script:journal.needsOwner; phases = @($script:journal.phases); backup = $script:journal.backup; close = @($script:journal.close)
        children = @($script:journal.children | ForEach-Object { [ordered]@{ purpose = $_.purpose; pid = $_.pid; createdTicks = $_.createdTicks; exit = $_.exit } }) }
    Write-Record (Join-Path $script:txRoot 'receipt.json') $receipt 'receipt'
}
function Get-Size([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return (Get-Item -LiteralPath $Path -Force).Length }
    $sum = 0L; foreach ($file in Get-ChildItem -LiteralPath $Path -File -Recurse -Force -ErrorAction SilentlyContinue) { $sum += $file.Length }; $sum
}
function Write-Candidates {
    # Report only; nothing is deleted.
    $items = @($script:journal.launchers | ForEach-Object { $_.bak.path; Get-ChildItem -Path "$($_.bak.path).*.tmp" -File -Force -ErrorAction SilentlyContinue | ForEach-Object FullName })
    if ($script:journal.terminal -in @('healthy', 'staged')) { $items += $script:journal.predecessor.root }
    $items += @(Get-ChildItem -LiteralPath $script:deployRoot -Directory -Force | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Data') } |
        Sort-Object Name | Select-Object -SkipLast 2 | ForEach-Object { Join-Path $_.FullName 'Data' })
    if ($script:dataTarget) {
        $leaf = Split-Path -Leaf $script:dataTarget
        $items += @(Get-ChildItem -LiteralPath (Split-Path -Parent $script:dataTarget) -Directory -Force |
            Where-Object { $_.Name -like "$leaf.failed-*" -or $_.Name -like "$leaf.restore-staging-*" } | ForEach-Object FullName)
    }
    $lines = @("## Transaction $($script:T) ($($script:journal.terminal), $(Utc))")
    foreach ($item in @($items | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique)) { $lines += "- $item ($(Get-Size $item) bytes)" }
    foreach ($value in @(Get-RunValues | Where-Object present)) {
        $target = if ($value.data -match '^"([^"]+)"') { $Matches[1] } else { ($value.data -split ' ')[0] }
        if (-not (Test-Path -LiteralPath $target)) { $lines += "- Run value '$($value.name)' targets a missing $target" }
    }
    Add-Content -LiteralPath (Join-Path $script:deployRoot 'DELETION-CANDIDATES.md') -Value $lines -Encoding utf8
}
function Publish-Chain {
    # Ruling 5 / ADDENDUM-4 edit 3: only a successful candidate deploy, only journal values, atomic and read back.
    $j = $script:journal
    $attempt = Join-Path $j.candidate.root "deploy-attempt-$($script:T)"
    $backups = @($j.launchers | ForEach-Object { $_.bak.path })
    $recovery = [ordered]@{ previousExe = $j.predecessor.exe; paths = @($j.launchers | ForEach-Object path); backups = $backups }
    $log = [ordered]@{ completedAtUtc = $j.outcome.utc; transaction = $script:T; commit = $j.candidate.commit; productVersion = $j.candidate.productVersion
        previousVersion = $j.predecessor.productVersion; launcherTarget = $j.candidate.exe; launcherBackups = $backups
        backupPath = $j.backup.path; backupManifestSha256 = $j.backup.manifestSha256; manifestPath = $j.candidate.manifestPath
        phases = @($j.phases | ForEach-Object name); hostWasRunning = $j.snapshot.hostWasRunning; started = (Has 'started'); terminal = $j.outcome.terminal }
    [void][IO.Directory]::CreateDirectory($attempt)
    foreach ($pair in @(@((Join-Path $attempt 'launcher-recovery.json'), $recovery), @((Join-Path $j.candidate.root 'deploy-log.json'), $log))) {
        Write-Record $pair[0] $pair[1] 'chain'
        $expected = $pair[1] | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable -Depth 20 | ConvertTo-Json -Depth 20 -Compress
        if ((Read-Json $pair[0] | ConvertTo-Json -Depth 20 -Compress) -cne $expected) {
            throw "Chain record did not read back equal: $($pair[0])"
        }
    }
}
function Close-Journal([string]$Terminal) {
    $script:journal.state = 'closed'; $script:journal.terminal = $Terminal; $script:journal.closedUtc = Utc
    Save-Journal
    Write-Receipt
    Write-Candidates
    Crash 'pointer-remove'
    Remove-Pointer
    Say "Closed: $Terminal. $pauseLine"
    $script:exitCode = if ($Terminal -in @('healthy', 'staged', 'needs-owner-resolved')) { 0 } else { 1 }
}
function Complete-Success([string]$Terminal) {
    if (-not $script:journal.outcome) { $script:journal.outcome = [ordered]@{ terminal = $Terminal; utc = (Utc) }; Save-Journal; Crash 'outcome' }
    Publish-Chain
    Close-Journal $script:journal.outcome.terminal
}
function Set-NeedsOwner([string]$Reason) {
    $script:journal.needsOwner = [ordered]@{ reason = $Reason; utc = (Utc) }
    Save-Journal
    Write-Receipt
    Say "NEEDS-OWNER: $Reason"
    Say "The pointer is kept. Resolve the cause, then run -Close -Disposition <restored-not-started|live-old|live-new|stop-refused|pointer-unproven> [-Layout old|new|none]."
    $script:exitCode = 2
}
function Test-Effect { [bool]$script:journal.stopRequested -or @($script:journal.launchers | Where-Object { $_.bak.intentUtc -or $_.switchState }).Count -gt 0 }

# ---- Failure handling (6A steps 5-7, 7A E2) ----
function Start-Old {
    if (-not $script:journal.oldStart) {
        Assert-Tree $script:journal.predecessor -Legacy
        $script:journal.oldStart = [ordered]@{ exe = $script:journal.predecessor.exe; args = '--cli engine host start --yes'
            oldTreeManifestHash = $script:journal.predecessor.manifestSha256; intentUtc = (Utc) }
        Save-Journal
        Crash 'old-start-intent'
        $result = Invoke-Child 'old-start' $script:journal.predecessor.exe '--cli engine host start --yes'
        if ($result.Exit -ne 0) { throw "The old host start exited $($result.Exit)." }
    }
}
function Complete-Old {
    # From old-start-intent on: only join, prove the old host and check its health. Never restore again.
    $terminal = if ($script:journal.restore.decision -ceq 'restore') { 'restored' } else { 'compensated' }
    if ($script:journal.snapshot.hostWasRunning) { Test-Health 'old' } else { Assert-StopProof }
    Close-Journal $terminal
}
function Invoke-Compensation {
    try { Assert-StopProof } catch { NeedsOwner "the stop is not proven before compensation: $($_.Exception.Message)" }
    if (Has 'backup-verified') {
        if (-not $script:journal.restore.decision) {
            if ((Get-FileId $script:dataTarget) -cne $script:journal.snapshot.dataFileId) { NeedsOwner 'the data folder is not the snapshotted folder (file ID changed).' }
            $script:journal.restore.decision = if ((Get-SyncSharkTreeIdentity $script:dataTarget) -ceq $script:journal.backup.manifestSha256) { 'compensate' } else { 'restore' }
            Save-Journal
        }
        if ($script:journal.restore.decision -ceq 'restore') { Invoke-StagedRestore }
    }
    Restore-Launchers
    Restore-RunValues
    if (-not (Has 'start-pending')) { Mark 'start-pending' }
    if ($script:journal.snapshot.hostWasRunning) { Start-Old }
    Complete-Old
}
function Stop-NewHost {
    $tracked = @(Get-Tracked @($script:journal.candidate.exe))
    if (-not $tracked.Count) { return 'nothing of the new build is running' }
    $result = Invoke-Child 'new-stop' $script:journal.candidate.exe '--cli engine host stop --yes'
    if ($result.Exit -ne 0) { return "the new host stop exited $($result.Exit)" }
    Wait-Processes @($tracked | ForEach-Object Process)
    $left = @(Get-Tracked @($script:journal.candidate.exe))
    if ($left.Count) { return "$($left.Count) new-build process(es) still running" }
    'the new host stopped gracefully'
}
function Resolve-Failure($ErrorRecord) {
    $message = $ErrorRecord.Exception.Message
    if ($script:lockStream) { $script:lockStream.Dispose(); $script:lockStream = $null }
    if ($message -like 'CANCELLED:*') { Say $message; $script:exitCode = 2; return }
    Say "FAILED: $message"
    $script:journal.errors = @($script:journal.errors) + @($message)
    try {
        if ($script:journal.outcome) {
            Save-Journal
            Say "The deploy succeeded ($($script:journal.outcome.terminal)); publishing its chain records failed. Run -Recover to publish them. Nothing was compensated."
            $script:exitCode = 2
            return
        }
        if ($message -like 'NEEDS-OWNER:*') { Set-NeedsOwner $message.Substring(13); return }
        if ($script:journal.oldStart) { Set-NeedsOwner "the old host restart did not prove healthy: $message"; return }
        if (Has 'started') { Set-NeedsOwner "failure after start: $message; $(Stop-NewHost). Data and launchers stay on the new build."; return }
        if (-not (Test-Effect)) { Close-Journal 'refused'; return }
        Invoke-Compensation
    } catch {
        $inner = $_.Exception.Message
        if ($inner -like 'CANCELLED:*') { Say $inner; $script:exitCode = 2; return }
        if ($inner -like 'NEEDS-OWNER:*') { $inner = $inner.Substring(13) }
        Set-NeedsOwner "recovery after '$message' failed: $inner"
    }
}

# ---- Entry points ----
function Invoke-Forward {
    $j = $script:journal
    # Step 0: snapshot (read-only).
    $j.snapshot = [ordered]@{ utc = (Utc); runValues = @(Get-RunValues); pairs = $null; recoveryRoot = (Get-RecoveryRoot); dataFileId = (Get-FileId $script:dataTarget); hostWasRunning = $false }
    $recoveryRoot = $j.snapshot.recoveryRoot
    if ($recoveryRoot) {
        Assert-Isolated @($recoveryRoot)
        if (Test-Contains $script:dataTarget $recoveryRoot) { throw "The Recovery root $recoveryRoot is inside the data folder: stop and ask the owner (R4)." }
        foreach ($own in @($script:deployRoot, $script:txRoot, $j.backup.path)) { if ((Test-Contains $own $recoveryRoot) -or (Test-Contains $recoveryRoot $own)) { throw "The Recovery root $recoveryRoot overlaps $own." } }
    }
    Say $(if ($recoveryRoot) { "Recovery root: $recoveryRoot" } else { 'Recovery root: absent (no local Recovery location is set).' })
    $generation = Get-Generation @(Get-Tracked $script:exes)
    if ($generation -and $generation.Engine) {
        $peer = Invoke-Cli 'trusted-status' $generation.Exe 'engine host status --json'
        if ($peer.Exit -ne 0 -or $null -eq $peer.Json -or -not $peer.Json.ok) { throw 'The running host does not answer engine host status as a trusted peer (R3).' }
    }
    $j.snapshot.hostWasRunning = $null -ne $generation
    $pairs = Invoke-Cli 'snapshot-pairs' $j.predecessor.exe 'engine pairs list --json'
    if ($pairs.Exit -ne 0 -or $null -eq $pairs.Json) { throw 'engine pairs list failed.' }
    $j.snapshot.pairs = Get-PairsKey $pairs.Json

    Mark 'snapshot'
    # Step 2: stop the running generation with its own CLI, then hold the instance lock.
    Stop-Running
    $script:lockStream = [IO.File]::Open($script:lockPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    Mark 'stopped'
    # Step 3: backup.
    $bytes = Measure-Data
    Assert-Capacity $bytes $bytes
    $j.backup.bytes = $bytes; $j.backup.intentUtc = Utc
    Save-Journal
    $entries = Copy-DataTree $script:dataTarget $j.backup.path 'backup-file'
    if ((Format-Manifest $entries) -cne (Format-Manifest (Get-SyncSharkTreeManifest $j.backup.path))) { throw 'The backup copy does not verify against the bytes read.' }
    $j.backup.manifestSha256 = Get-SyncSharkTreeIdentity $j.backup.path
    Mark 'backup-verified'
    $script:lockStream.Dispose(); $script:lockStream = $null
    # Step 4: launchers (.bak first, then the switch).
    foreach ($launcher in @($j.launchers)) { Write-LauncherBackup $launcher }
    for ($i = 0; $i -lt $j.launchers.Count; $i++) { Switch-Launcher $j.launchers[$i] $i }
    Mark 'launchers-switched'
    # Step 5: start and health.
    if (-not $j.snapshot.hostWasRunning) {
        $status = Invoke-Cli 'new-status' $j.candidate.exe 'engine status --json'
        $after = Invoke-Cli 'new-pairs' $j.candidate.exe 'engine pairs list --json'
        if ($status.Exit -ne 0 -or $after.Exit -ne 0 -or (Get-PairsKey $after.Json) -cne $j.snapshot.pairs) { throw 'The new build did not answer status and pairs equal to the snapshot.' }
        Restore-RunValues
        Say 'Staged: not live-verified (the host was not running before).'
        Complete-Success 'staged'
        return
    }
    Mark 'started'
    $start = Invoke-Child 'new-start' $j.candidate.exe '--cli engine host start --yes'
    if ($start.Exit -ne 0) { throw "The new host start exited $($start.Exit)." }
    Test-Health 'new'
    Complete-Success 'healthy'
}
function Invoke-Deploy {
    if ($null -eq $script:dataTarget) { throw $script:dataTargetError }
    # Bug #110: a missing or wrong product-root.txt made the new exe start on an empty store. Refuse; never write the pointer.
    if (-not (Test-Path -LiteralPath (Join-Path $script:dataTarget 'engine\state.db') -PathType Leaf)) {
        $last = @(Get-ChildItem -LiteralPath $script:deployRoot -Filter 'journal.json' -File -Recurse -Depth 1 -ErrorAction SilentlyContinue | Sort-Object { $_.Directory.Name } | Select-Object -Last 1)
        $recorded = if ($last.Count) { Read-Journal $last[0].FullName }
        $expected = if ($recorded -is [Collections.IDictionary] -and $recorded['dataTarget'] -is [string] -and $recorded['dataTarget']) { "transaction $($last[0].Directory.Name) recorded $($recorded['dataTarget'])" }
            elseif ($last.Count) { "unavailable: transaction $($last[0].Directory.Name) has no readable dataTarget" } else { 'unavailable: no earlier deploy recorded one' }
        throw "The data root $($script:dataTarget) holds no engine\state.db: product-root.txt in $installationRoot is missing or names the wrong folder (expected root: $expected). Restore the pointer by hand; nothing was changed."
    }
    $Build = Get-SyncSharkNormalizedPath $Build; $LauncherDir = Get-SyncSharkNormalizedPath $LauncherDir
    if ($testMode) { Assert-SyncSharkWithin $installationRoot $Build; Assert-SyncSharkWithin $installationRoot $LauncherDir }
    $state = Get-PointerState
    if ($state.kind -ceq 'closed') { Set-Paths $state.T; $script:journal = $state.journal; Remove-Pointer; Say "Removed the closed transaction's pointer ($($state.T))." }
    elseif ($state.kind -ceq 'open') { throw "Transaction $($state.T) is open: run -Recover." }
    elseif ($state.kind -cne 'none') { $script:exitCode = 2; throw "needs-owner: $($state.reason) The pointer was not changed." }
    $candidate = Read-BuildManifest $Build
    if (-not (Test-Contains $candidate.root $candidate.outputDirectory)) { throw 'The candidate outputDirectory is outside -Build.' }
    if (Test-Path -LiteralPath (Join-Path $Build 'deploy-log.json')) { throw 'The candidate already has a deploy-log.json; review it before any new deploy.' }
    $launcherPaths = @($launcherSpecs | ForEach-Object { Join-Path $LauncherDir $_.name })
    $predecessor = Find-Predecessor $launcherPaths $candidate.expectedPreviousVersion
    if (Same $predecessor.root $candidate.root) { throw 'The candidate is the build the launchers already start.' }
    Assert-Isolated (@($Build, $LauncherDir) + $launcherPaths + @(Get-SidePaths $candidate) + @(Get-SidePaths $predecessor))
    Assert-Tree $candidate
    Assert-Tree $predecessor -Legacy
    $script:exes = @($predecessor.exe, $candidate.exe)

    Set-Paths ('{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'), [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(4)).ToLowerInvariant())
    $backupPath = Join-Path $script:txRoot 'Data'
    $staging = "$($script:dataTarget).restore-staging-$($script:T)"; $failed = "$($script:dataTarget).failed-$($script:T)"
    foreach ($own in @($script:deployRoot, $backupPath, $script:txRoot)) {
        foreach ($other in @($script:dataTarget, $staging, $failed)) {
            if ((Test-Contains $own $other) -or (Test-Contains $other $own)) { throw "Overlapping roots: $own and $other (ruling 3)." }
        }
    }
    Assert-Isolated @($script:txRoot, $backupPath, $staging, $failed)
    foreach ($path in @($script:txRoot, $staging, $failed)) { if (Test-Path -LiteralPath $path) { throw "Refusing: $path already exists." } }
    $digits = $candidate.version.Replace('.', '')
    $launchers = for ($i = 0; $i -lt 2; $i++) {
        $path = $launcherPaths[$i]; $original = [IO.File]::ReadAllBytes($path)
        $bom = $original.Length -ge 3 -and $original[0] -eq 0xEF -and $original[1] -eq 0xBB -and $original[2] -eq 0xBF
        $encoding = [Text.UTF8Encoding]::new($bom)
        $text = [IO.File]::ReadAllText($path)
        $updated = [regex]::Replace($text, $launcherSpecs[$i].pattern, { param($m) $m.Groups[1].Value + $candidate.exe + $m.Groups[3].Value })
        Assert-Parse $updated $path
        $new = [byte[]]($encoding.GetPreamble() + $encoding.GetBytes($updated))
        $bakPath = "$path.pre-$digits-deploy-attempt-$($script:T).bak"
        if (Test-Path -LiteralPath $bakPath) { throw "A launcher backup already exists: $bakPath" }
        [ordered]@{ path = $path; originalSha256 = (Get-BytesSha $original); originalBase64 = [Convert]::ToBase64String($original)
            newSha256 = (Get-BytesSha $new); newBase64 = [Convert]::ToBase64String($new); switchState = $null; restore = $null
            bak = [ordered]@{ path = $bakPath; expectedSha256 = (Get-BytesSha $original); intentUtc = $null; done = $null } }
    }
    [void][IO.Directory]::CreateDirectory($script:txRoot)
    $pointer = [ordered]@{ rootId = $script:rootId; T = $script:T; journalPath = $script:journalPath; createdUtc = (Utc)
        installationRoot = $installationRoot; dataTarget = $script:dataTarget }
    $stream = [IO.FileStream]::new($script:pointerPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($pointer | ConvertTo-Json)); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    Crash 'pointer-created'
    $script:journal = [ordered]@{ schema = 1; revision = 0; T = $script:T; rootId = $script:rootId; installationRoot = $installationRoot; dataTarget = $script:dataTarget
        testMode = $testMode; state = 'open'; terminal = $null; closedUtc = $null; outcome = $null; needsOwner = $null; errors = @(); phases = @()
        candidate = $candidate; predecessor = $predecessor; launcherDir = $LauncherDir; launchers = @($launchers); snapshot = $null; children = @()
        stopRequested = $null; backup = [ordered]@{ path = $backupPath; bytes = $null; intentUtc = $null; manifestSha256 = $null }
        restore = [ordered]@{ decision = $null; staging = $staging; failed = $failed; stagingIntentUtc = $null; stagingId = $null; swap1IntentUtc = $null; swap2IntentUtc = $null }
        dataFileId = (Get-FileId $script:dataTarget); oldStart = $null; close = @() }
    Write-Record $script:journalPath $script:journal 'journal'
    Say "Transaction $($script:T): $($candidate.productVersion) replaces $($predecessor.productVersion). $pauseLine"
    try { Invoke-Forward } catch { Resolve-Failure $_ }
}
function Resolve-RecoverTarget {
    # FIX 4: only when the swap-1 intent is recorded and the identity proofs show the rename happened.
    if ($script:dataTarget) { return }
    $j = $script:journal
    if (-not $j.restore.swap1IntentUtc -or (Test-Path -LiteralPath $j.dataTarget) -or (Get-FileId $j.restore.failed) -cne $j.snapshot.dataFileId -or
        (Get-FileId $j.restore.staging) -cne $j.restore.stagingId -or (Get-SyncSharkTreeIdentity $j.restore.staging) -cne $j.backup.manifestSha256) {
        $script:exitCode = 2
        throw "needs-owner: the data target is unavailable ($($script:dataTargetError)) and the swap-2 proofs do not hold; nothing was changed."
    }
    $resolved = Resolve-SyncSharkDataRoot $installationRoot -AllowMissingLeaf
    if (-not (Same $resolved $j.dataTarget)) { throw 'The resolved data target differs from the recorded one.' }
    $script:dataTarget = $resolved
}
function Open-Transaction([switch]$AllowMissingData) {
    $state = Get-PointerState
    if ($state.kind -ceq 'none') { Say 'No transaction is open.'; $script:exitCode = 0; return $null }
    if ($state.kind -ceq 'bad') {
        $script:exitCode = 2
        throw "needs-owner: $($state.reason) The pointer was not changed. Repair the pointer and journal by hand (a valid pointer naming T, rootId, installationRoot and dataTarget) first."
    }
    Set-Paths $state.T
    $script:journal = $state.journal
    $j = $script:journal
    Assert-Isolated (@($script:txRoot, $j.dataTarget, $j.backup.path, $j.restore.staging, $j.restore.failed) + @(Get-SidePaths $j.candidate) +
        @(Get-SidePaths $j.predecessor) + @($j.launchers | ForEach-Object { $_.path; $_.bak.path }))
    if ($AllowMissingData) { Resolve-RecoverTarget } elseif ($null -eq $script:dataTarget) { throw $script:dataTargetError }
    $script:lockPath = Join-Path $script:dataTarget 'state\instance.lock'
    $script:exes = @($script:journal.predecessor.exe, $script:journal.candidate.exe)
    if (-not (Same $script:journal.candidate.manifestPath $ownManifest.path)) { throw 'This script is not the journaled candidate''s own tools copy.' }
    if ($state.kind -ceq 'closed') {
        if (-not (Test-Path -LiteralPath (Join-Path $script:txRoot 'receipt.json'))) { Write-Receipt }
        Remove-Pointer
        Say "Transaction $($state.T) was already closed ($($script:journal.terminal)); removed its pointer."
        $script:exitCode = 0
        return $null
    }
    $state
}
function Invoke-Recover {
    $state = Open-Transaction -AllowMissingData
    if ($null -eq $state) { return }
    $j = $script:journal
    if ($j.needsOwner) {
        Say "NEEDS-OWNER: $($j.needsOwner.reason)"
        Say 'Recover changes nothing here. Resolve the cause, then run -Close -Disposition <d> [-Layout old|new|none].'
        $script:exitCode = 2
        return
    }
    try {
        Join-Children
        foreach ($launcher in @($j.launchers)) { if ($launcher.bak.intentUtc -and -not $launcher.bak.done) { Write-LauncherBackup $launcher } }
        if ($j.outcome) { Complete-Success $j.outcome.terminal; return }
        if ($j.oldStart) { Complete-Old; return }
        if (Has 'started') { Test-Health 'new'; Complete-Success 'healthy'; return }
        if (-not (Test-Effect)) { Close-Journal 'refused'; return }
        Invoke-Compensation
    } catch { Resolve-Failure $_ }
}
function Invoke-Close {
    $state = Open-Transaction
    if ($null -eq $state) { return }
    $j = $script:journal
    $entry = [ordered]@{ disposition = $Disposition; layout = $(if ($Layout) { $Layout } else { $null }); intentUtc = (Utc); result = $null }
    $j.close = @($j.close) + @($entry)
    Save-Journal
    try {
        $side = switch ($Disposition) { 'live-old' { 'old' } 'live-new' { 'new' } 'restored-not-started' { 'restored' }
            default { if (-not $Layout) { throw "-Layout old|new|none is required for $Disposition." }; if ($Layout -eq 'none') { 'none' } else { $Layout } } }
        if ($side -eq 'restored') {
            Assert-StopProof
            if (-not $j.backup.manifestSha256 -or (Get-SyncSharkTreeIdentity $script:dataTarget) -cne $j.backup.manifestSha256) { throw 'The data does not equal the backup manifest.' }
            Assert-Launchers 'old'
            Restore-RunValues -CompareOnly
        } elseif ($side -eq 'none') {
            Assert-StopProof
            if (@($j.launchers | Where-Object { $_.restore -and $_.restore.intentUtc -and -not $_.restore.done }).Count) { throw 'A launcher restore has no done stamp.' }
            $committed = if ((Has 'launchers-switched') -and -not @($j.launchers | Where-Object { $_.restore -and $_.restore.done }).Count) { 'new' } else { 'old' }
            Assert-Launchers $committed
            Restore-RunValues -CompareOnly
        } else {
            Say "Live closure: $liveLabel."
            if ((Get-FileId $script:dataTarget) -cne $j.dataFileId) { throw 'The data folder is not the last recorded folder.' }
            Assert-Launchers $side
            Assert-Tree $(if ($side -eq 'new') { $j.candidate } else { $j.predecessor }) -Legacy:($side -eq 'old')
            $build = if ($side -eq 'new') { $j.candidate } else { $j.predecessor }
            $status = Invoke-Cli "$side-close-status" $build.exe 'engine status --json'
            if ($status.Exit -ne 0) { throw 'engine status did not answer.' }
            Test-Health $side -CompareOnly
            $entry.label = $liveLabel
        }
        $entry.result = 'passed'
        Close-Journal 'needs-owner-resolved'
    } catch {
        $message = $_.Exception.Message
        if ($message -like 'CANCELLED:*') { Say $message; $script:exitCode = 2; return }
        $entry.result = $message
        Save-Journal
        Say "Close proof failed: $message"
        Say 'The pointer is kept.'
        $script:exitCode = 2
    }
}

$installationRoot = Resolve-SyncSharkProductRoot $TestProductRoot
$script:exitCode = 1
$script:lockStream = $null
$script:phaseWatch = [Diagnostics.Stopwatch]::StartNew()
$mutex = Enter-SyncSharkMaintenanceMutex $installationRoot
try {
    # rootId = the maintenance mutex identity (M:719-721): SHA256 of the normalised, upper-cased installation root.
    $script:rootId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Get-SyncSharkNormalizedPath $installationRoot).ToUpperInvariant())))
    $script:deployRoot = Join-Path $installationRoot 'replacement-staging\deploys'
    $script:pointerPath = Join-Path $script:deployRoot "active-$($script:rootId).json"
    $script:dataTarget = $null; $script:dataTargetError = $null
    try { $script:dataTarget = Resolve-SyncSharkDataRoot $installationRoot } catch { $script:dataTargetError = $_.Exception.Message }
    Assert-Isolated @($installationRoot, $script:deployRoot, $script:dataTarget)
    if ($script:dataTarget) { $script:lockPath = Join-Path $script:dataTarget 'state\instance.lock' }
    switch ($PSCmdlet.ParameterSetName) {
        'Deploy' {
            $script:exes = @()
            Invoke-Deploy
        }
        'Recover' { Invoke-Recover }
        'Close' { Invoke-Close }
    }
} catch {
    Say "REFUSED: $($_.Exception.Message)"
    if ($script:exitCode -eq 0) { $script:exitCode = 1 }
} finally {
    if ($script:lockStream) { $script:lockStream.Dispose() }
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
exit $script:exitCode
