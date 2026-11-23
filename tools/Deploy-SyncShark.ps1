# One-process deploy of a Sync Shark build (DESIGN-ONE-DEPLOY-11A with its addenda).
# Requires Windows, PowerShell 7.4+ and the admitted published SQLite runtime.
# Deploy:  pwsh -File <candidate outputDirectory>\tools\Deploy-SyncShark.ps1 -Build <candidate build root> -LauncherDir <launcher folder>
# Recover: ... -Recover          Close a blocked transaction: ... -Close -Disposition <d>
# Operating condition: no GUI, CLI, launcher or Repair use until the transaction is closed; refuse without effects otherwise.
# The signed house setup publishes the complete version and switches active once.
# Build admission verifies pinned product signatures and manifest hashes; data backups retain file attributes and times.
[CmdletBinding(DefaultParameterSetName = 'Deploy')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Deploy')][string]$Build,
    [Parameter(Mandatory, ParameterSetName = 'Deploy')][string]$LauncherDir,
    [Parameter(Mandatory, ParameterSetName = 'Recover')][switch]$Recover,
    [Parameter(Mandatory, ParameterSetName = 'Close')][switch]$Close,
    [Parameter(Mandatory, ParameterSetName = 'Close')]
    [ValidateSet('live-new', 'stop-refused', 'pointer-unproven')][string]$Disposition,
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
foreach ($pair in @(@('tools/SyncShark.Release.psm1', $modulePath), @('tools/SyncShark.Registration.psm1', (Join-Path $toolsDir 'SyncShark.Registration.psm1')), @('tools/Deploy-SyncShark.ps1', $PSCommandPath))) {
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
$legacyBinaries = @('SyncShark.exe', 'SyncShark.dll', 'SyncShark.Core.dll', 'SyncShark.Engine.dll', 'SyncSharkDiscovery.exe', 'SyncShark.Explorer.dll')
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
function NeedsOwner([string]$Message) { throw "BLOCKED: $Message" }
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
# ---- Build records and trust (step 1) ----
function Read-BuildManifest([string]$Root,[switch]$Legacy) {
    $found = @(Get-ChildItem -LiteralPath $Root -Filter '*-manifest.json' -File -Force)
    if ($found.Count -ne 1) { throw "Expected exactly one *-manifest.json in $Root (found $($found.Count))." }
    $path = $found[0].FullName
    $sidecar = $path.Substring(0, $path.Length - 5) + '.sha256'
    if (-not (Test-Path -LiteralPath $sidecar -PathType Leaf)) { throw "Missing manifest sidecar: $sidecar" }
    $sha = Get-SyncSharkSha256 $path
    if ((([IO.File]::ReadAllText($sidecar).Trim()) -split '\s+')[0] -ine $sha) { throw "Build manifest hash mismatch: $path" }
    $manifest = Read-Json $path
    $out = Get-SyncSharkNormalizedPath ([string]$manifest.outputDirectory)
    $hostName='SyncShark.exe'; $cliName=$hostName
    if (!$Legacy) {
        $installer=Join-Path $Root ("release-$($manifest.version)/deploy-scripts/Install-SyncShark.ps1")
        Assert-Isolated @($installer)
        $signer=(Get-SyncSharkSignerIdentity).roles.publisher
        Assert-SyncSharkSignatureObservation (Get-Observation $installer) $signer.thumbprints $signer.subject $signer.subject $signer.spki
        $line=[IO.File]::ReadLines($installer) | Select-Object -First 1
        $prefix='# SyncSharkUpdateContract: '
        if (!$line.StartsWith($prefix,[StringComparison]::Ordinal)) { throw 'Installer contract missing.' }
        $contract=$line.Substring($prefix.Length) | ConvertFrom-Json
        if ($contract.product -cne 'Massdex.SyncShark' -or ($contract.version+'+'+$contract.sourceRevision) -cne $manifest.productVersion -or $contract.previousVersion -cne $manifest.expectedPreviousVersion) { throw 'Installer does not bind the selected builds.' }
        $pins=Get-SyncSharkSignedMemberHashes $contract
        foreach ($name in $pins.Keys) { if ((Get-SyncSharkSha256 (Join-Path $out $name)) -cne $pins[$name]) { throw "Signed build member differs: $name" } }
        $hostName=$contract.hostExecutable; $cliName=$contract.cliExecutable
    }
    [ordered]@{ root = (Get-SyncSharkNormalizedPath $Root); manifestPath = $path; manifestSha256 = $sha; outputDirectory = $out
        exe=(Join-Path $out $hostName); cliExe=(Join-Path $out $cliName); previousTreeSha256=$(if (!$Legacy) { $contract.previousTreeSha256 }); installerSha256=$(if (!$Legacy) { Get-SyncSharkSha256 $installer }); productVersion=[string]$manifest.productVersion; version=[string]$manifest.version
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
    $info = Read-BuildManifest $Side.root -Legacy:$Legacy
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
    $stem=[IO.Path]::GetFileNameWithoutExtension($Side.cliExe).ToLowerInvariant()
    foreach ($name in @(($stem+'.deps.json'),($stem+'.runtimeconfig.json'))) {
        # D:203-205 runtime metadata: listed for the candidate, present for the legacy predecessor.
        if (-not $(if ($Legacy) { $actual.ContainsKey($name) } else { $listed.ContainsKey($name) })) { throw "Missing runtime metadata: $name" }
    }
    $signer = (Get-SyncSharkSignerIdentity).roles.publisher
    $productBinaries = if ($Legacy) { $legacyBinaries } else { @((Split-Path -Leaf $Side.exe),(Split-Path -Leaf $Side.cliExe)) }
    foreach ($name in $productBinaries) {
        if (-not $listed.ContainsKey($name.ToLowerInvariant())) { throw "Missing product binary: $name" }
        Assert-SyncSharkSignatureObservation (Get-Observation (Join-Path $out $name)) @($signer.thumbprints) $signer.subject $signer.subject @($signer.spki)
    }
    foreach ($exe in @($Side.exe,$Side.cliExe) | Select-Object -Unique) {
        if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion -cne $Side.productVersion) { throw "Product version mismatch: $exe" }
    }
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
        try { $info = Read-BuildManifest $dir.FullName -Legacy } catch { continue }
        if (Same $info.outputDirectory $outDir) { $side = $info; break }
    }
    if ($null -eq $side) { throw "No predecessor build record found above $outDir." }
    if ($side.productVersion -cne $ExpectedPreviousVersion) { throw "Predecessor $($side.productVersion) is not the expected $ExpectedPreviousVersion." }
    if (-not (Same $exes[0] $side.exe)) { throw 'The launchers do not start the predecessor outputDirectory\SyncShark.exe.' }
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
        foreach ($exe in ($Exes | Select-Object -Unique)) {
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
        if ($null -eq $role) { throw "A GUI, CLI or Repair process is running (pid $($item.Pid)); refuse without effects." }
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
function Assert-FinishedWork([string]$Database) {
    # Only the verified backup is opened. Let SQLite read its committed WAL; no checkpoint or source-side maintenance.
    $options=[Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
    $options.DataSource=$Database; $options.Mode='ReadOnly'; $options.Pooling=$false
    $db=[Microsoft.Data.Sqlite.SqliteConnection]::new($options.ToString())
    try {
        $db.Open(); $cmd=$db.CreateCommand()
        $cmd.CommandText='PRAGMA user_version'
        if ($cmd.ExecuteScalar() -ne 16) { throw 'Expected predecessor schema 16; not switching.' }
        $cmd.CommandText=@'
SELECT 'pending pair',pair_id,id FROM pair WHERE pending_json IS NOT NULL
UNION ALL SELECT 'accepted submission',coalesce(p.pair_id,s.pair_id),s.submission_id
FROM folder_removal_submission s LEFT JOIN pair p ON p.id=s.pair_id WHERE s.phase='Accepted'
UNION ALL SELECT 'active folder removal',coalesce(p.pair_id,r.pair_id),r.operation_id
FROM folder_removal r LEFT JOIN pair p ON p.id=r.pair_id WHERE r.active=1
'@
        $reader=$cmd.ExecuteReader(); $blocked=$false
        while ($reader.Read()) { Say (ConvertTo-Json -Compress -InputObject @{unfinished=$reader.GetString(0);pair=$reader.GetValue(1);item=$reader.GetValue(2)}); $blocked=$true }
        if ($blocked) { throw 'Stopped predecessor inventory did not clear; not switching.' }
    } finally { $db.Dispose() }
}
function Get-PairsKeyText([string]$Out) {
    if ($Out.Trim() -ceq 'There are no pairs yet.') { return '' }
    (@($Out.TrimEnd() -split "`r?`n" | ForEach-Object {
        if ($_ -notmatch '^(?<id>\S+)  .+  .+ <-> .+  account .+  first run: \S+(?<off>  \(switched off\))?$') { throw 'Unrecognised candidate pair row.' }
        "$($Matches.id)|$(-not $Matches['off'])"
    }) | Sort-Object -CaseSensitive) -join ';'
}
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
function Stop-Running($ExpectedGeneration) {
    $generation = Get-Generation @(Get-Tracked $script:exes)
    if ($generation -and (!$ExpectedGeneration -or !(Same $generation.Exe $ExpectedGeneration.Exe) -or !$generation.Engine -or ($generation.Tracked.CreatedTicks -join ',') -ne ($ExpectedGeneration.Tracked.CreatedTicks -join ','))) { throw 'Running generation changed before stop.' }
    if ($null -eq $generation) { return }
    $script:journal.stopRequested = Utc
    Save-Journal
    $result = Invoke-Child 'stop' $generation.Exe '--cli engine host stop --yes'
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
function Switch-Launcher {
    # The house setup stages the complete version and atomically retargets active.
    $j=$script:journal
    $installer=Join-Path $j.candidate.root "release-$($j.candidate.version)/deploy-scripts/Install-SyncShark.ps1"
    Assert-Isolated @($installer)
    $held=[IO.File]::Open($installer,'Open','Read','Read')
    try {
        $signer=(Get-SyncSharkSignerIdentity).roles.publisher
        Assert-SyncSharkSignatureObservation (Get-Observation $installer) $signer.thumbprints $signer.subject $signer.subject $signer.spki
        if ((Get-SyncSharkSha256 $installer) -cne $j.candidate.installerSha256) { throw 'Installer changed since build admission.' }
        Mark 'switch-requested'
        if ($testMode) { & $installer -PredecessorRuntime $j.predecessor.installedRoot -TestProductRoot $installationRoot }
        else { & $installer -PredecessorRuntime $j.predecessor.installedRoot }
        Assert-SyncSharkJunction (Join-Path $installationRoot 'active') (Join-Path $installationRoot ('versions/'+$j.candidate.version))
    } finally { $held.Dispose() }
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
function Test-Health {
    $build=$script:journal.candidate
    $installed=Join-Path $installationRoot ('versions/'+$build.version)
    Assert-SyncSharkJunction (Join-Path $installationRoot 'active') $installed
    $hostPath=Join-Path $installed (Split-Path -Leaf $build.exe)
    $cli=Join-Path $installed (Split-Path -Leaf $build.cliExe)
    $status=Invoke-Child 'new-status' $cli 'status'
    if ($status.Exit -ne 0 -or !$status.Out.Trim()) { throw 'The installed host did not report status.' }
    $hosts=@(Get-Tracked @($hostPath) | Where-Object { $_.Args -in @('','--notify-ready') })
    if ($hosts.Count -ne 1 -or [Diagnostics.FileVersionInfo]::GetVersionInfo($hostPath).ProductVersion -cne $build.productVersion) { throw 'Expected one candidate host.' }
    # A refused startup returns just its diagnostic. A successful startup also returns the normal status for the store.
    $lines=@($status.Out.TrimEnd() -split "`r?`n")
    if (!$script:journal.snapshot.pairs) {
        if ($lines[-1] -cne 'There are no pairs yet.') { throw 'The installed host did not report an open store.' }
    } else {
        $seen=@($lines | ForEach-Object { if ($_ -match '^(?<id>\S+)  .+  (?:last Repair: .+|no Repair yet)  open notices: [0-9]+$') { $Matches.id } } | Sort-Object -CaseSensitive)
        $expected=@($script:journal.snapshot.pairs.Split(';') | ForEach-Object { $_.Split('|')[0] } | Sort-Object -CaseSensitive)
        if (Compare-Object $expected $seen -CaseSensitive) { throw 'The installed host did not report status for the snapshotted pairs.' }
    }
    $pairs=Invoke-Child 'new-pairs' $cli 'pairs list'
    if ($pairs.Exit -ne 0 -or (Get-PairsKeyText $pairs.Out) -cne $script:journal.snapshot.pairs) { throw 'The pairs differ from the snapshot.' }
    Assert-NoNewAutoSyncFailure
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
    if ($exists -and $null -eq $best) { throw 'The product settings store is unavailable (no valid slot), so the Recovery root is unknown; refuse without effects.' }
    if ($best -and -not [string]::IsNullOrWhiteSpace($best.root)) { return [IO.Path]::GetFullPath($best.root.Trim()) }
    $null
}

# ---- Data: scan, copy, capacity, restore (steps 3, 7) ----
function Measure-Data([string]$Source) {
    $bytes = 0L
    foreach ($item in Get-ChildItem -LiteralPath $Source -Recurse -Force) {
        if (([int]$item.Attributes -band $unsafeAttributes) -ne 0) { throw "Reparse or offline entry in the data folder: $($item.FullName)" }
        if (-not $item.PSIsContainer) { $bytes += $item.Length }
    }
    $bytes
}
function Get-FreeBytes([string]$Path) { [IO.DriveInfo]::new([IO.Path]::GetPathRoot((Get-SyncSharkNormalizedPath $Path))).AvailableFreeSpace }
function Assert-Capacity([long]$BackupNeed) {
    if ((Get-FreeBytes $script:deployRoot) -lt $BackupNeed) { throw "Not enough free space for the backup ($BackupNeed bytes)." }
}
function Copy-DataTree([string]$Source, [string]$Destination, [string]$CrashName) {
    # Each file is read once and hashed while written. The held instance lock is read through its own handle.
    $sourceFiles = @(Get-ChildItem -LiteralPath $Source -File -Recurse -Force)
    [void][IO.Directory]::CreateDirectory($Destination)
    foreach ($directory in Get-ChildItem -LiteralPath $Source -Directory -Recurse -Force) {
        [void][IO.Directory]::CreateDirectory((Join-Path $Destination ([IO.Path]::GetRelativePath($Source, $directory.FullName))))
    }
    $buffer = [byte[]]::new(1MB)
    $entries = foreach ($file in $sourceFiles) {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $target = Join-Path $Destination $relative
        $isLock = $null -ne $script:lockStream -and (Same $file.FullName $script:lockPath)
        $reader = if ($isLock) { $script:lockStream.Position = 0; $script:lockStream } else { [IO.File]::Open($file.FullName, 'Open', 'Read', 'Read') }
        $output = [IO.FileStream]::new($target,'CreateNew','Write','None')
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
    $items=@()
    if ($script:journal.terminal -in @('healthy', 'staged')) { $items += $script:journal.predecessor.root }
    $items += @(Get-ChildItem -LiteralPath $script:deployRoot -Directory -Force | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Data') } |
        Sort-Object Name | Select-Object -SkipLast 2 | ForEach-Object { Join-Path $_.FullName 'Data' })
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
    $log = [ordered]@{ completedAtUtc = $j.outcome.utc; transaction = $script:T; commit = $j.candidate.commit; productVersion = $j.candidate.productVersion
        previousVersion = $j.predecessor.productVersion; launcherTarget = (Join-Path (Join-Path $installationRoot 'active') (Split-Path -Leaf $j.candidate.exe))
        backupPath = $j.backup.path; backupManifestSha256 = $j.backup.manifestSha256; manifestPath = $j.candidate.manifestPath
        phases = @($j.phases | ForEach-Object name); hostWasRunning = $j.snapshot.hostWasRunning; started = (Has 'started'); terminal = $j.outcome.terminal }
    $path=Join-Path $j.candidate.root 'deploy-log.json'
    Write-Record $path $log 'chain'
    $expected=$log | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable -Depth 20 | ConvertTo-Json -Depth 20 -Compress
    if ((Read-Json $path | ConvertTo-Json -Depth 20 -Compress) -cne $expected) { throw 'Deploy log readback mismatch.' }
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
    Say "BLOCKED: $Reason"
    Say "The pointer is kept. The deploy operator resolves the cause and runs -Close -Disposition <live-new|stop-refused|pointer-unproven>."
    $script:exitCode = 2
}
function Test-Effect { [bool]$script:journal.stopRequested -or (Has 'switch-requested') }
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
        if ($message -like 'BLOCKED:*') { Set-NeedsOwner $message; return }
        if (Has 'started') { Set-NeedsOwner "failure after start: $message; the deploy did not stop the candidate. Fix forward."; return }
        if (-not (Test-Effect)) { Close-Journal 'refused'; return }
        Set-NeedsOwner "no rollback: $message. Nothing was compensated; fix forward."
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
        if (Test-Contains $script:dataTarget $recoveryRoot) { throw "The Recovery root $recoveryRoot is inside the data folder; refusing the overlapping backup." }
        foreach ($own in @($script:deployRoot, $script:txRoot, $j.backup.path)) { if ((Test-Contains $own $recoveryRoot) -or (Test-Contains $recoveryRoot $own)) { throw "The Recovery root $recoveryRoot overlaps $own." } }
    }
    Say $(if ($recoveryRoot) { "Recovery root: $recoveryRoot" } else { 'Recovery root: absent (no local Recovery location is set).' })
    $generation = Get-Generation @(Get-Tracked $script:exes)
    if ($generation -and (($generation.Exe -notin @($j.predecessor.exe,$j.predecessor.installedExe)) -or !$generation.Engine)) { throw 'Unexpected running generation; not stopping it.' }
    if ($generation) {
        $peer=Invoke-Cli 'trusted-status' $generation.Exe 'engine host status --json'
        if ($peer.Exit -ne 0 -or !$peer.Json.ok -or $peer.Json.data.state -cne 'StatusWatching') { throw 'Predecessor is not idle; leaving it running.' }
    }
    $j.snapshot.hostWasRunning = $null -ne $generation
    $pairs = Invoke-Cli 'snapshot-pairs' $j.predecessor.exe 'engine pairs list --json'
    if ($pairs.Exit -ne 0 -or $null -eq $pairs.Json) { throw 'engine pairs list failed.' }
    $j.snapshot.pairs = Get-PairsKey $pairs.Json

    Mark 'snapshot'
    # Step 2: stop the running generation with its own CLI, then hold the instance lock.
    Stop-Running $generation
    $script:lockStream = [IO.File]::Open($script:lockPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    Mark 'stopped'
    # Step 3: backup.
    $bytes = Measure-Data $j.backup.source
    Assert-Capacity $bytes
    $j.backup.bytes = $bytes; $j.backup.intentUtc = Utc
    Save-Journal
    $entries = Copy-DataTree $j.backup.source $j.backup.path 'backup-file'
    if ((Format-Manifest $entries) -cne (Format-Manifest (Get-SyncSharkTreeManifest $j.backup.path))) { throw 'The backup copy does not verify against the bytes read.' }
    $j.backup.manifestSha256 = Get-SyncSharkTreeIdentity $j.backup.path
    $relative=[IO.Path]::GetRelativePath($j.backup.source,(Join-Path $script:dataTarget 'engine/state.db'))
    Assert-FinishedWork (Join-Path $j.backup.path $relative)
    Mark 'backup-verified'
    Switch-Launcher
    $script:lockStream.Dispose(); $script:lockStream = $null
    Mark 'launchers-switched'
    $installedCli=Join-Path (Join-Path $installationRoot ('versions/'+$j.candidate.version)) (Split-Path -Leaf $j.candidate.cliExe)
    # Step 5: start and health.
    if (-not $j.snapshot.hostWasRunning) {
        $status=Invoke-Child 'new-status' $installedCli 'status'
        if ($status.Exit -eq 0 -or $status.Out.Trim() -cne 'The Sync Shark host is not running.') { throw 'Candidate did not report host absence.' }
        Say 'Staged: not live-verified (the host was not running before).'
        Complete-Success 'staged'
        return
    }
    Mark 'started'
    $start = Invoke-Child 'new-start' $installedCli 'host start'
    if ($start.Exit -ne 0) { throw "The new host start exited $($start.Exit)." }
    Test-Health
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
        throw "The data root $($script:dataTarget) holds no engine\state.db: product-root.txt in $installationRoot is missing or names the wrong folder (expected root: $expected). Nothing was changed."
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
    if ($predecessor.version -cne '0.3.95' -or $predecessor.productVersion -cnotmatch '^0[.]3[.]95[+][0-9a-f]{40}$') { throw 'Cutover requires the exact 0.3.95 predecessor.' }
    $active=Join-Path $installationRoot 'active'
    $target=Get-SyncSharkJunctionTarget $active
    if (!$target -and [IO.Directory]::Exists($active)) { throw 'Active predecessor target is unproven.' }
    $predecessor.installedRoot=if ($target) { $target } else { $predecessor.outputDirectory }
    Assert-Isolated @($predecessor.installedRoot)
    $predecessor.installedExe=Join-Path $predecessor.installedRoot (Split-Path -Leaf $predecessor.exe)
    if ($candidate.previousTreeSha256 -cnotmatch '^[0-9a-f]{64}$' -or (Get-SyncSharkTreeIdentity $predecessor.installedRoot) -cne $candidate.previousTreeSha256 -or
        [Diagnostics.FileVersionInfo]::GetVersionInfo($predecessor.installedExe).ProductVersion -cne $predecessor.productVersion) { throw 'Installed predecessor differs from its exact signed version/tree.' }
    foreach ($name in 'SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.e_sqlite3.dll','SQLitePCLRaw.batteries_v2.dll','Microsoft.Data.Sqlite.dll') {
        $null=[Reflection.Assembly]::LoadFrom((Join-Path $candidate.outputDirectory $name))
    }
    $null=[Runtime.InteropServices.NativeLibrary]::Load((Join-Path $candidate.outputDirectory 'e_sqlite3.dll'))
    [SQLitePCL.Batteries_V2]::Init()
    $null=[SQLitePCL.raw]::sqlite3_libversion_number()
    $script:exes = @($predecessor.exe,$predecessor.installedExe,$candidate.exe,(Join-Path (Join-Path $installationRoot ('versions/'+$candidate.version)) (Split-Path -Leaf $candidate.exe)))

    Set-Paths ('{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'), [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(4)).ToLowerInvariant())
    $backupPath = Join-Path $script:txRoot 'Data'
    # At the fixed root the store is engine/, not the installation (which also contains active and deployment records).
    $backupSource = if (Same $script:dataTarget $installationRoot) { Join-Path $installationRoot 'engine' } else { $script:dataTarget }
    Assert-SyncSharkOrdinaryTree $backupSource -RootOnly
    foreach ($own in @($script:deployRoot,$backupPath,$script:txRoot)) {
        if ((Test-Contains $own $backupSource) -or (Test-Contains $backupSource $own)) { throw "Overlapping roots: $own and $backupSource." }
    }
    Assert-Isolated @($script:txRoot, $backupPath)
    foreach ($path in @($script:txRoot)) { if (Test-Path -LiteralPath $path) { throw "Refusing: $path already exists." } }
    [void][IO.Directory]::CreateDirectory($script:txRoot)
    $pointer = [ordered]@{ rootId = $script:rootId; T = $script:T; journalPath = $script:journalPath; createdUtc = (Utc)
        installationRoot = $installationRoot; dataTarget = $script:dataTarget }
    $stream = [IO.FileStream]::new($script:pointerPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($pointer | ConvertTo-Json)); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    Crash 'pointer-created'
    $script:journal = [ordered]@{ schema = 1; revision = 0; T = $script:T; rootId = $script:rootId; installationRoot = $installationRoot; dataTarget = $script:dataTarget
        testMode = $testMode; state = 'open'; terminal = $null; closedUtc = $null; outcome = $null; needsOwner = $null; errors = @(); phases = @()
        candidate = $candidate; predecessor = $predecessor; launcherDir = $LauncherDir; snapshot = $null; children = @()
        stopRequested = $null; backup = [ordered]@{ source = $backupSource; path = $backupPath; bytes = $null; intentUtc = $null; manifestSha256 = $null }
        dataFileId = (Get-FileId $script:dataTarget); close = @() }
    Write-Record $script:journalPath $script:journal 'journal'
    Say "Transaction $($script:T): $($candidate.productVersion) replaces $($predecessor.productVersion). $pauseLine"
    try { Invoke-Forward } catch { Resolve-Failure $_ }
}
function Open-Transaction {
    $state = Get-PointerState
    if ($state.kind -ceq 'none') { Say 'No transaction is open.'; $script:exitCode = 0; return $null }
    if ($state.kind -ceq 'bad') {
        $script:exitCode = 2
        throw "blocked: $($state.reason) The pointer was not changed. The deploy operator preserves the invalid pointer and journal and fixes forward."
    }
    Set-Paths $state.T
    $script:journal = $state.journal
    $j = $script:journal
    Assert-Isolated (@($script:txRoot,$j.dataTarget,$j.backup.path) + @(Get-SidePaths $j.candidate) + @(Get-SidePaths $j.predecessor))
    if ($null -eq $script:dataTarget) { throw $script:dataTargetError }
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
    $state = Open-Transaction
    if ($null -eq $state) { return }
    $j = $script:journal
    if ($j.needsOwner) {
        Say "BLOCKED: $($j.needsOwner.reason)"
        Say 'Recover changes nothing here. The deploy operator resolves the cause and runs -Close -Disposition <d>.'
        $script:exitCode = 2
        return
    }
    try {
        Join-Children
        if ($j.outcome) { Complete-Success $j.outcome.terminal; return }
        if (Has 'started') { Test-Health; Complete-Success 'healthy'; return }
        if (-not (Test-Effect)) { Close-Journal 'refused'; return }
        Set-NeedsOwner 'no rollback: recovery left the transaction open with effects recorded; fix forward.'
    } catch { Resolve-Failure $_ }
}
function Invoke-Close {
    $state = Open-Transaction
    if ($null -eq $state) { return }
    $j = $script:journal
    $entry = [ordered]@{ disposition = $Disposition; intentUtc = (Utc); result = $null }
    $j.close = @($j.close) + @($entry)
    Save-Journal
    try {
        if ($Disposition -eq 'live-new') { Test-Health } else {
            Assert-StopProof
            if (Has 'switch-requested') { throw 'Verify the switched candidate before closing.' }
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
