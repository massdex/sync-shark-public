# Fault fixtures 1-38 for tools/Deploy-SyncShark.ps1 (DESIGN-ONE-DEPLOY-11A and addenda). Test mode only.
# Every root lives under %TEMP%\sync-shark-release-tests; stub builds stand in for Sync Shark; the registry is only read.
# The harness ends only stub processes whose image lies under its own fixture root, by PID.
[CmdletBinding()]
param([string]$Fixture = '') # comma-separated subset, e.g. 2,9
# Check the shipped sources before creating any fixtures or launching a process.
foreach ($entry in @{
    'Deploy-SyncShark.ps1' = 'Restore-Launchers|Write-LauncherBackup|Invoke-StagedRestore|Restore-RunValues'
    'Install-SyncShark.ps1.template' = 'install-journal|updater-publication|Assert-SyncSharkProtectedRuntime'
    'Prepare-SyncSharkRelease.ps1' = 'Rollback-SyncShark|Assert-SyncSharkProtectedRuntime'
}.GetEnumerator()) {
    if ([IO.File]::ReadAllText((Join-Path $PSScriptRoot $entry.Key)) -match $entry.Value) {
        throw "Retired release path in $($entry.Key): $($Matches[0])"
    }
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:SYNC_SHARK_RELEASE_TEST_MODE -cne '1') { throw 'Test mode is required.' }
# The caller chooses its workspace and TEMP; fixture writes remain under the fixed test root.
Remove-Item Env:HTTPS_PROXY, Env:HTTP_PROXY -ErrorAction SilentlyContinue
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force
$base = Join-Path ([IO.Path]::GetTempPath()) 'sync-shark-release-tests'
$pwsh = (Get-Process -Id $PID).Path
$script = Join-Path $PSScriptRoot 'Deploy-SyncShark.ps1'
$signer = (Get-SyncSharkSignerIdentity).roles.publisher
$commits = @{ '0.3.95' = 'a' * 40; '0.3.96' = 'b' * 40; '0.3.97' = 'c' * 40 }

# ---- Stub Sync Shark (CLI shapes of engine-cli.json, host/supervisor argument shapes of AutoSyncProcess.cs) ----
$stubSource = @'
using System; using System.Diagnostics; using System.IO; using System.Reflection; using System.Text; using System.Text.Json; using System.Threading;
static class Stub {
    static readonly string State = Environment.GetEnvironmentVariable("SYNCSHARK_STUB_STATE");
    static readonly string Data = Environment.GetEnvironmentVariable("SYNCSHARK_STUB_DATA");
    static readonly string Exe = Environment.ProcessPath;
    static readonly string Version = typeof(Stub).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion.Split('+')[0];
    static readonly string Key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetDirectoryName(Exe).ToUpperInvariant()))).Substring(0, 16);
    static string StopFile => Path.Combine(State, "stop-" + Key);
    static JsonElement B(string name) {
        var file = Path.Combine(State, "behavior.json");
        if (!File.Exists(file)) return default;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.TryGetProperty(Version, out var v) && v.TryGetProperty(name, out var x) ? x.Clone() : default;
    }
    static bool Flag(string n) => B(n).ValueKind == JsonValueKind.True;
    static string[] Hosts() => Directory.GetFiles(State, "host-" + Key + "-*.running");
    static void Json(object data) => Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, command = "stub", ok = true, data, error = (object)null }));
    static int Main(string[] a) {
        var args = string.Join(" ", a);
        bool legacy=Version=="0.3.95";
        if (!legacy && Path.GetFileName(Exe)=="Sync Shark.exe") return args=="--notify-ready" ? Host() : 2;
        if (legacy != args.StartsWith("--", StringComparison.Ordinal)) return 2;
        switch (args) {
            case "--core v2 --engine-host": return Host();
            case "--core v2 --engine-supervisor": return Wait(Flag("supervisorIgnoresStop"));
            case "--cli engine host start --yes": return Start();
            case "--cli engine host stop --yes": return Stop();
            case "--cli engine host status --json":
                if (Flag("statusFail")) { Console.Error.WriteLine("stub: status refused"); return 1; }
                Json(new { state = Flag("busy") ? "StatusStillWorking" : Hosts().Length > 0 ? "StatusWatching" : "StatusUnavailable", message = "stub" }); return 0;
            case "--cli engine status --json": Json(new { items = Flag("empty") ? Array.Empty<object>() : new object[] { new { pair = new { pairId="pair-a", enabled=true }, pendingPresent=Flag("pending") } } }); return 0;
            case "--cli engine pairs list --json": Json(new { pairs = Flag("empty") ? Array.Empty<object>() : new object[] { new { id = "pair-a", enabled = true } } }); return 0;
            case "--cli engine settings list --json":
                var root = B("recoveryRoot");
                Json(new { settings = root.ValueKind == JsonValueKind.String ? new object[] { new { name = "RecoveryRoot", value = root.GetString(), overridden = true } } : new object[0] });
                return 0;
            case "host start": return Start();
            case "host stop": return Stop();
            case "status":
                if (Hosts().Length==0) { Console.WriteLine("The Sync Shark host is not running."); return 1; }
                if (Flag("statusFail")) { Console.Error.WriteLine("stub: status refused"); return 1; }
                Console.WriteLine(Flag("refused") ? "Startup refused." : Flag("empty") ? "There are no pairs yet." : "pair-a  Pair A  no Repair yet  open notices: 0"); return 0;
            case "pairs list": Console.WriteLine(Flag("empty") ? "There are no pairs yet." : "pair-a  Pair A  /Local <-> /Cloud  account acc  first run: NewestWins"); return 0;
            default: return 2;
        }
    }
    static int Host() {
        FileStream held = Flag("noLock") ? null : new FileStream(Path.Combine(Data, "state", "instance.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try {
            if (Flag("hostWrites")) File.AppendAllText(Path.Combine(Data, "host-" + Version + ".txt"), DateTime.UtcNow.ToString("O") + Environment.NewLine);
            var marker = Path.Combine(State, "host-" + Key + "-" + Environment.ProcessId + ".running");
            File.WriteAllText(marker, Exe);
            try {
                int exit = Wait(false);
                if (Flag("commitAtDrain")) {
                    var psi = new ProcessStartInfo("python") { UseShellExecute=false };
                    foreach (var arg in new[]{"-I",Path.Combine(State,"drain.py"),Path.Combine(Data,"engine","state.db"),Path.Combine(State,"wal-proven")}) psi.ArgumentList.Add(arg);
                    using var writer=Process.Start(psi); writer.WaitForExit(); if(writer.ExitCode!=0) return 1;
                }
                return exit;
            } finally { File.Delete(marker); }
        } finally { if (held != null) held.Dispose(); }
    }
    static int Wait(bool ignoreStop) {
        var end = DateTime.UtcNow.AddMinutes(10); // leak guard for fixtures
        while (DateTime.UtcNow < end) { if (!ignoreStop && File.Exists(StopFile)) return 0; Thread.Sleep(50); }
        return 0;
    }
    static Process Spawn(string args) => Process.Start(new ProcessStartInfo(args=="--notify-ready" ? Path.Combine(Path.GetDirectoryName(Exe),"Sync Shark.exe") : Exe, args) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
    static int Start() {
        if (Flag("startFail")) { if (Flag("startLeavesSupervisor")) Spawn("--core v2 --engine-supervisor"); Console.Error.WriteLine("stub: start failed"); return 1; }
        if (Hosts().Length > 0) return 0;
        File.Delete(StopFile);
        var p = Spawn(Version=="0.3.95" ? "--core v2 --engine-host" : "--notify-ready");
        var end = DateTime.UtcNow.AddSeconds(30);
        while (Hosts().Length == 0) { if (p.HasExited || DateTime.UtcNow > end) return 1; Thread.Sleep(20); }
        return 0;
    }
    static int Stop() {
        if (Flag("stopFail") || Hosts().Length == 0) { Console.Error.WriteLine("stub: no host to stop"); return 1; }
        var delay = B("stopDelayMs"); if (delay.ValueKind == JsonValueKind.Number) Thread.Sleep(delay.GetInt32());
        File.WriteAllText(StopFile, "stop");
        var end = DateTime.UtcNow.AddSeconds(30);
        while (Hosts().Length > 0) { if (DateTime.UtcNow > end) return 1; Thread.Sleep(20); }
        return 0;
    }
}
'@
function Get-StubCache([string]$Version) {
    $cache = Join-Path $base "deploy-stub-cache\$Version-$([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($stubSource))))"
    if (Test-Path -LiteralPath (Join-Path $cache 'SyncShark.exe')) { return $cache }
    $project = Join-Path $base "deploy-stub-src\$Version"
    [void][IO.Directory]::CreateDirectory($project)
    [IO.File]::WriteAllText((Join-Path $project 'Stub.cs'), $stubSource)
    [IO.File]::WriteAllText((Join-Path $project 'SyncShark.csproj'), @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
<AssemblyName>SyncShark</AssemblyName><Version>$Version</Version><InformationalVersion>$Version+$($commits[$Version])</InformationalVersion>
<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion></PropertyGroup></Project>
"@)
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'; $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    # Immediately before the build: a module auto-load in between restores the inherited pwsh module paths
    # that the Windows PowerShell 5.1 signing child cannot read (B-31).
    Remove-Item env:PSModulePath -ErrorAction SilentlyContinue
    & dotnet build (Join-Path $project 'SyncShark.csproj') -c Release -o $cache -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -nologo -v:q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Stub build failed for $Version." }
    $cache
}

# ---- Fixture roots ----
function Write-Manifest([string]$BuildRoot, [string]$Name, [string]$Version, [string]$Previous, [string]$Out, [switch]$Legacy, [string[]]$Unlisted) {
    $files = @(Get-ChildItem -LiteralPath $Out -File -Recurse -Force | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Out, $_.FullName).Replace('\', '/')
        if ((-not $Legacy -or $relative -match '\.(exe|dll)$') -and $relative -notin $Unlisted) {
            [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; length = $_.Length }
        } })
    $manifest = [ordered]@{ package = $Name; commit = $commits[$Version]; version = $Version; expectedPreviousVersion = $Previous
        productVersion = "$Version+$($commits[$Version])"; outputDirectory = $Out; signed = $true; signerThumbprint = $signer.thumbprints[1]; files = $files }
    $path = Join-Path $BuildRoot "$Name-manifest.json"
    [IO.File]::WriteAllText($path, ($manifest | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $BuildRoot "$Name-manifest.sha256"), "$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)  $Name-manifest.json`n")
}
function New-StubBuild($Ctx, [string]$Name, [string]$Version, [string]$Previous, [switch]$Legacy, [scriptblock]$Mutate, [string[]]$Unlisted) {
    $root = Join-Path $Ctx.root "builds\$Name"; $out = Join-Path $root 'bin\out'
    [void][IO.Directory]::CreateDirectory($out)
    Copy-Item -Path (Join-Path (Get-StubCache $Version) '*') -Destination $out -Recurse
    if ($Legacy) {
    foreach ($dll in 'SyncShark.Core.dll', 'SyncShark.Engine.dll', 'SyncShark.Explorer.dll') { Copy-Item -LiteralPath (Join-Path $out 'SyncShark.dll') -Destination (Join-Path $out $dll) }
    Copy-Item -LiteralPath (Join-Path $out 'SyncShark.exe') -Destination (Join-Path $out 'SyncSharkDiscovery.exe')
    } else {
        Copy-Item -LiteralPath (Join-Path $out 'syncshark.exe') -Destination (Join-Path $out 'Sync Shark.exe')
        $installerDir=Join-Path $root "release-$Version/deploy-scripts"
        [void][IO.Directory]::CreateDirectory($installerDir)
        $contract=@{previousTreeSha256=(Get-SyncSharkTreeIdentity $Ctx.builds['old-0375'].out);product='Massdex.SyncShark';version=$Version;sourceRevision=$commits[$Version];previousVersion=$Previous;hostExecutable='Sync Shark.exe';cliExecutable='syncshark.exe';signedMembers=@{'Sync Shark.exe'=(Get-SyncSharkSha256 (Join-Path $out 'Sync Shark.exe'));'syncshark.exe'=(Get-SyncSharkSha256 (Join-Path $out 'syncshark.exe'))}} | ConvertTo-Json -Compress
        $body=@'
param($PredecessorRuntime,$TestProductRoot)
$root=Resolve-SyncSharkProductRoot $TestProductRoot
foreach ($name in 'cli-common.ps1','run-apply.ps1') {
    if (Test-Path -LiteralPath (Join-Path $j.launcherDir $name)) { throw "Setup entered before predecessor launcher retirement: $name" }
}
$target=Join-Path $root ('versions/'+$j.candidate.version)
[void][IO.Directory]::CreateDirectory($target)
Copy-Item -Path (Join-Path $j.candidate.outputDirectory '*') -Destination $target -Recurse
New-Item -ItemType Junction -Path (Join-Path $root 'active') -Target $target | Out-Null
'@
        [IO.File]::WriteAllText((Join-Path $installerDir 'Install-SyncShark.ps1'),"# SyncSharkUpdateContract: $contract`n$body")
        [void][IO.Directory]::CreateDirectory((Join-Path $out 'tools'))
        foreach ($tool in 'SyncShark.Release.psm1','SyncShark.Registration.psm1', 'Deploy-SyncShark.ps1') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $tool) -Destination (Join-Path $out "tools\$tool") }
    }
    if (!$Legacy) {
        $assets=Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/SyncShark.Engine.Tests/bin/Release/net8.0-windows'
        foreach ($file in 'Microsoft.Data.Sqlite.dll','SQLitePCLRaw.core.dll','SQLitePCLRaw.batteries_v2.dll','SQLitePCLRaw.provider.e_sqlite3.dll','runtimes/win-x64/native/e_sqlite3.dll') {
            Copy-Item -LiteralPath (Join-Path $assets $file) -Destination $out
        }
    }
    if ($Mutate) { & $Mutate $out }
    Write-Manifest $root $Name $Version $Previous $out -Legacy:$Legacy -Unlisted $Unlisted
    $build = [ordered]@{ root = $root; out = $out; exe = (Join-Path $out $(if ($Legacy) { 'SyncShark.exe' } else { 'Sync Shark.exe' })); script = (Join-Path $out 'tools\Deploy-SyncShark.ps1'); pv = "$Version+$($commits[$Version])" }
    $Ctx.builds[$Name] = $build
    Write-Observations $Ctx
    $build
}
function New-Observation { [ordered]@{ Status = 'Valid'; RawStatus = 'Valid'; Thumbprint = $signer.thumbprints[0]; Subject = $signer.subject; Issuer = $signer.subject
    SpkiSha256 = $signer.spki[0]; SignerChainStatuses = @(); Rfc3161Present = $true; Rfc3161CryptographicallyValid = $true; Rfc3161 = $true
    TimestampSignatureBound = $true; TimestampChainEvaluatedAtTokenTime = $true; TimestampChainValid = $true; TimestampUtc = '2026-11-05T00:00:00Z'; TimestampChainStatuses = @() } }
function Write-Observations($Ctx) {
    $all = [ordered]@{}
    foreach ($build in $Ctx.builds.Values) {
        $installer=Join-Path $build.root ('release-'+$build.pv.Split('+')[0]+'/deploy-scripts/Install-SyncShark.ps1')
        $all[(Get-SyncSharkNormalizedPath $installer)] = New-Observation
        foreach ($name in $(if ($build.pv.StartsWith('0.3.95+')) { @('SyncShark.exe','SyncShark.dll','SyncShark.Core.dll','SyncShark.Engine.dll','SyncSharkDiscovery.exe','SyncShark.Explorer.dll') } else { @('Sync Shark.exe','syncshark.exe') })) {
            $path = Get-SyncSharkNormalizedPath (Join-Path $build.out $name)
            $all[$path] = if ($Ctx.overrides.Contains($path)) { $Ctx.overrides[$path] } else { New-Observation }
        }
    }
    [IO.File]::WriteAllText($Ctx.obs, ($all | ConvertTo-Json -Depth 10))
}
function New-Context([int]$Number) {
    $root = Resolve-SyncSharkProductRoot (Join-Path $base ("deploy-{0:d2}-{1}" -f $Number, [Guid]::NewGuid().ToString('N').Substring(0, 6)))
    $ctx = [ordered]@{ root = $root; data = (Join-Path $root 'vol\Data'); state = (Join-Path $root 'stub-state'); launchers = (Join-Path $root 'launchers')
        obs = (Join-Path $root 'observations.json'); builds = [ordered]@{}; overrides = [ordered]@{}; runs = 0 }
    foreach ($dir in 'state', 'engine', 'v2\logs\autosync') { [void][IO.Directory]::CreateDirectory((Join-Path $ctx.data $dir)) }
    [void][IO.Directory]::CreateDirectory($ctx.state); [void][IO.Directory]::CreateDirectory($ctx.launchers)
    [IO.File]::WriteAllText((Join-Path $ctx.data 'state\instance.lock'), 'lock')
    @'
import sqlite3, sys
with sqlite3.connect(sys.argv[1]) as db:
    db.executescript("""PRAGMA user_version=16;
        CREATE TABLE pair(id INTEGER PRIMARY KEY,pair_id TEXT,pending_json TEXT);
        CREATE TABLE folder_removal_submission(pair_id INTEGER,submission_id TEXT,phase TEXT);
        CREATE TABLE folder_removal(pair_id INTEGER,operation_id TEXT,active INTEGER);
        INSERT INTO pair VALUES(1,'pair-a',NULL);
        INSERT INTO folder_removal_submission VALUES(1,'settled','Terminal');
        INSERT INTO folder_removal VALUES(1,'completed',0);""")
'@ | & python -I - (Join-Path $ctx.data 'engine/state.db')
    if ($LASTEXITCODE -ne 0) { throw 'Fixture database creation failed.' }
    [IO.File]::WriteAllText((Join-Path $ctx.data 'v2\logs\autosync\autosync.log'), "2026-01-01T00:00:00.0000000+00:00 host start`n")
    [IO.File]::WriteAllText((Join-Path $root 'product-root.txt'), $ctx.data)
    # Launchers are written first: the builds below give any on-write scanner time to release them.
    $oldExe = Join-Path $root 'builds\old-0375\bin\out\SyncShark.exe'
    $cli = Join-Path $ctx.launchers 'cli-common.ps1'; $run = Join-Path $ctx.launchers 'run-apply.ps1'
    [IO.File]::WriteAllText($cli, "`$Exe = '$oldExe'`n& `$Exe --cli @args`n")
    [IO.File]::WriteAllText($run, "param([string]`$Exe = '$oldExe')`n& `$Exe --cli engine repair --yes`n")
    $old = New-StubBuild $ctx 'old-0375' '0.3.95' '0.3.74+0000000000000000000000000000000000000000' -Legacy
    $null = New-StubBuild $ctx 'new-0376' '0.3.96' $old.pv
    $ctx.old = $ctx.builds['old-0375']; $ctx.new = $ctx.builds['new-0376']
    $ctx
}
function Invoke-Tool($Ctx, [string[]]$Arguments, [string]$Crash = '', [string]$Script = '', [switch]$NoObservations) {
    if (-not $Script) { $Script = $Ctx.new.script }
    $all = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $Script) + $Arguments + @('-TestProductRoot', $Ctx.root)
    if (-not $NoObservations) { $all += @('-VerificationObservationPath', $Ctx.obs) }
    if ($Crash) { $all += @('-CrashAfter', $Crash) }
    $Ctx.runs++
    $log = Join-Path $Ctx.root ('run-{0:d2}.log' -f $Ctx.runs)
    $env:SYNCSHARK_STUB_STATE = $Ctx.state; $env:SYNCSHARK_STUB_DATA = $Ctx.data
    # Wait on the process only: children of a crashed run may still hold an inherited pipe.
    $process = Start-Process -FilePath $pwsh -ArgumentList $all -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $process.WaitForExit()
    [pscustomobject]@{ Exit = $process.ExitCode; Log = (Read-Shared $log) + (Read-Shared "$log.err") }
}
function Read-Shared([string]$Path) { $reader = [IO.StreamReader]::new([IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite, Delete')); try { $reader.ReadToEnd() } finally { $reader.Dispose() } }
function Deploy($Ctx, [string]$Crash = '', $Build = $null, [switch]$NoObservations) {
    if (-not $Build) { $Build = $Ctx.new }
    Invoke-Tool $Ctx @('-Build', $Build.root, '-LauncherDir', $Ctx.launchers) $Crash $Build.script -NoObservations:$NoObservations
}
function Recover($Ctx, [string]$Crash = '') { Invoke-Tool $Ctx @('-Recover') $Crash }
function CloseTx($Ctx, [string]$Disposition) { Invoke-Tool $Ctx @('-Close','-Disposition',$Disposition) }
function Get-DeployRoot($Ctx) { Join-Path $Ctx.root 'replacement-staging\deploys' }
function Get-Pointer($Ctx) { @(Get-ChildItem -LiteralPath (Get-DeployRoot $Ctx) -Filter 'active-*.json' -File -ErrorAction SilentlyContinue) }
function Get-Journal($Ctx) {
    $file = @(Get-ChildItem -LiteralPath (Get-DeployRoot $Ctx) -Directory -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { Join-Path $_.FullName 'journal.json' } | Where-Object { Test-Path -LiteralPath $_ }) | Select-Object -Last 1
    if ($file) { Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable -Depth 30 }
}
function Get-JournalBytes($Ctx) { $j = Get-Journal $Ctx; Get-FileHash -LiteralPath (Join-Path (Get-DeployRoot $Ctx) "$($j.T)\journal.json") | ForEach-Object Hash }
function Set-Behavior($Ctx, [hashtable]$Behavior) { [IO.File]::WriteAllText((Join-Path $Ctx.state 'behavior.json'), ($Behavior | ConvertTo-Json -Depth 5)) }
function Start-Host($Ctx, $Build) {
    $env:SYNCSHARK_STUB_STATE = $Ctx.state; $env:SYNCSHARK_STUB_DATA = $Ctx.data
    if ((Split-Path -Leaf $Build.exe) -ceq 'Sync Shark.exe') { & (Join-Path $Build.out 'syncshark.exe') host start | Out-Null }
    else { & $Build.exe --cli engine host start --yes | Out-Null }
    if ($LASTEXITCODE -ne 0) { throw "Stub host start failed: $($Build.exe)" }
}
function Start-Stub($Ctx, $Build, [string]$Arguments) {
    $env:SYNCSHARK_STUB_STATE = $Ctx.state; $env:SYNCSHARK_STUB_DATA = $Ctx.data
    Start-Process -FilePath $Build.exe -ArgumentList $Arguments -WindowStyle Hidden -PassThru
}
function Get-Stubs($Ctx, [string]$Arguments = '') {
    @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($Ctx.root + '\', [StringComparison]::OrdinalIgnoreCase) -and
        (-not $Arguments -or $_.CommandLine.EndsWith(" $Arguments")) })
}
function Stop-Stubs($Ctx) { foreach ($p in Get-Stubs $Ctx) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue } }
function Wait-Until([scriptblock]$Condition, [string]$What) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $Condition)) { if ($watch.Elapsed.TotalSeconds -gt 60) { throw "Timed out waiting for $what." }; Start-Sleep -Milliseconds 50 }
}
function Get-Reason($J) { if ($J -and $J.needsOwner) { $J.needsOwner.reason } else { '' } }
function Expect([bool]$Condition, [string]$Message) { if (-not $Condition) { throw "Expectation failed: $Message" } }
function Get-DataId($Ctx) { Get-SyncSharkTreeIdentity $Ctx.data }
function Get-LauncherHashes($Ctx) { (@('cli-common.ps1', 'run-apply.ps1') | ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $Ctx.launchers $_)).Hash }) -join ',' }
function Edit-Data($Ctx) { [IO.File]::AppendAllText((Join-Path $Ctx.data 'engine\state.db'), 'newer write') }
function Flip-Byte([string]$Path) { $b = [IO.File]::ReadAllBytes($Path); $b[0] = $b[0] -bxor 0xFF; [IO.File]::WriteAllBytes($Path, $b) }
function Expect-Open($Ctx, [string]$Phase) { $j = Get-Journal $Ctx; Expect ($j.state -ceq 'open') 'journal open'; if ($Phase) { Expect (@($j.phases | ForEach-Object name) -contains $Phase) "phase $Phase reached" }; $j }
function Expect-Closed($Ctx, [string]$Terminal) {
    $j = Get-Journal $Ctx
    Expect ($j.state -ceq 'closed' -and $j.terminal -ceq $Terminal) "closed $Terminal (got $($j.state) $($j.terminal); errors: $(@($j.errors) -join ' | '); needsOwner: $(Get-Reason $j))"
    Expect (@(Get-Pointer $Ctx).Count -eq 0) 'pointer removed'
    $j
}
function Expect-NeedsOwner($Ctx, [string]$Like) {
    $j = Get-Journal $Ctx
    Expect ($j.state -ceq 'open' -and (Get-Reason $j) -like $Like) "needs-owner like '$Like' (got: $(Get-Reason $j); terminal $($j.terminal); errors $(@($j.errors) -join ' | '))"
    Expect (@(Get-Pointer $Ctx).Count -eq 1) 'pointer kept'
    $j
}
function Expect-NoChain($Build) { Expect (-not (Test-Path -LiteralPath (Join-Path $Build.root 'deploy-log.json'))) 'no chain record' }
function Import-DeployFunctions {
    $ast = [Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$null)
    $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false) | ForEach-Object { $_.Extent.Text }
}

function Same-Path([string]$A, [string]$B) { (Get-SyncSharkNormalizedPath $A) -ieq (Get-SyncSharkNormalizedPath $B) }
$results = [Collections.Generic.List[object]]::new()
function F([int]$Number, [string]$Name, [scriptblock]$Body) {
    if ($Fixture -and "$Number" -notin ($Fixture -split ',')) { return }
    $watch = [Diagnostics.Stopwatch]::StartNew(); $status = 'PASS'; $detail = ''
    $script:contexts = [Collections.Generic.List[object]]::new()
    try { & $Body } catch { $status = 'FAIL'; $detail = $_.Exception.Message + ' @' + $_.InvocationInfo.ScriptLineNumber }
    finally { foreach ($c in $script:contexts) { Stop-Stubs $c } }
    $results.Add([pscustomobject]@{ Fixture = $Number; Name = $Name; Result = $status; Ms = $watch.ElapsedMilliseconds; Detail = $detail })
    Write-Host ('{0,2} {1} {2,6}ms {3} {4}' -f $Number, $status, $watch.ElapsedMilliseconds, $Name, $detail)
}
function Use($Ctx) { $script:contexts.Add($Ctx); $Ctx }

F 37 'legacy predecessor + candidate shape accepted; unlisted module, missing metadata, extra file refused' {
    $ctx = Use (New-Context 37)
    Start-Host $ctx $ctx.old
    $r = Deploy $ctx
    $j = Expect-Closed $ctx 'healthy'
    $record = Get-Content -LiteralPath (Join-Path $ctx.new.root 'deploy-log.json') -Raw | ConvertFrom-Json -AsHashtable
    Expect ($record.terminal -ceq 'healthy' -and $record.hostWasRunning -and $record.started -and -not $record.Contains('runStarted')) 'deploy-log from the journal only'
    Expect (@(Get-Stubs $ctx '--notify-ready' | Where-Object ExecutablePath -ieq (Join-Path $ctx.root 'versions/0.3.96/Sync Shark.exe')).Count -eq 1) 'new host running'
    Expect (Test-Path -LiteralPath (Join-Path (Get-DeployRoot $ctx) 'DELETION-CANDIDATES.md')) 'candidates listed'
    Expect ($null -eq $j.snapshot.recoveryRoot) 'Recovery root absent when none is set'
    $variants = @(
        @{ name = 'nomod'; unlisted = @('tools/SyncShark.Release.psm1'); mutate = $null; like = '*does not list tools/SyncShark.Release.psm1*' },
        @{ name = 'nodeps'; unlisted = @(); mutate = { param($out) Remove-Item -LiteralPath (Join-Path $out 'SyncShark.deps.json') }; like = '*Missing runtime metadata: syncshark.deps.json*' },
        @{ name = 'noconfig'; unlisted = @(); mutate = { param($out) Remove-Item -LiteralPath (Join-Path $out 'SyncShark.runtimeconfig.json') }; like = '*Missing runtime metadata: syncshark.runtimeconfig.json*' },
        @{ name = 'extra'; unlisted = @('extra.txt'); mutate = { param($out) [IO.File]::WriteAllText((Join-Path $out 'extra.txt'), 'x') }; like = '*Unlisted file in build tree: extra.txt*' })
    $fresh = Use (New-Context 37)
    foreach ($v in $variants) {
        $build = New-StubBuild $fresh "cand-$($v.name)" '0.3.96' $fresh.old.pv -Mutate $v.mutate -Unlisted $v.unlisted
        $r = Deploy $fresh '' $build
        Expect ($r.Exit -ne 0 -and $r.Log -like $v.like) "$($v.name) refused ($($r.Log.Trim()))"
        Expect (@(Get-Pointer $fresh).Count -eq 0) "$($v.name) left no pointer"
    }
}
F 24 'Run values read back equal to the snapshot; test mode never writes them (production write-back not exercised)' {
    $ctx = Use (New-Context 24)
    $r = Deploy $ctx
    $j = Expect-Closed $ctx 'staged'
    $now = @(foreach ($name in 'Sync Shark AutoSync', 'Sync Shark') {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run', $false)
        try { $v = if ($key) { $key.GetValue($name, $null, 'DoNotExpandEnvironmentNames') } else { $null } } finally { if ($key) { $key.Dispose() } }
        "$name|$($null -ne $v)|$v" })
    $snap = @($j.snapshot.runValues | ForEach-Object { "$($_.name)|$($_.present)|$($_.data)" })
    Expect (($now -join ';') -ceq ($snap -join ';')) 'Run values equal the snapshot after the deploy'
}
F 1 'backup byte flipped: no data effect, refused' {
    $ctx = Use (New-Context 1)
    [IO.File]::WriteAllBytes((Join-Path $ctx.data 'aa-small.bin'), [byte[]]::new(4096))
    $stream = [IO.File]::Create((Join-Path $ctx.data 'zz-large.bin')); $stream.SetLength(200MB); $stream.Dispose()
    $before = Get-DataId $ctx; $launchers = Get-LauncherHashes $ctx
    $watcher = [PowerShell]::Create()
    [void]$watcher.AddScript({ param($DeployRoot)
        $end = [DateTime]::UtcNow.AddSeconds(90)
        while ([DateTime]::UtcNow -lt $end) {
            $hit = Get-ChildItem -Path (Join-Path $DeployRoot '*\Data\aa-small.bin') -ErrorAction SilentlyContinue | Where-Object Length -eq 4096 | Select-Object -First 1
            if ($hit) { $s = [IO.File]::Open($hit.FullName, 'Open', 'ReadWrite', 'ReadWrite'); $s.WriteByte(1); $s.Dispose(); return 'flipped' }
            Start-Sleep -Milliseconds 2
        }
        'missed' }).AddArgument((Get-DeployRoot $ctx))
    $pending = $watcher.BeginInvoke()
    $r = Deploy $ctx
    $flipped = $watcher.EndInvoke($pending); $watcher.Dispose()
    Expect ($flipped -contains 'flipped') 'watcher flipped the backup byte before verification'
    $j = Expect-Closed $ctx 'refused'
    Expect (@(@($j.errors) -like '*does not verify*').Count -gt 0) 'backup verification refused'
    Expect ((Get-DataId $ctx) -ceq $before -and (Get-LauncherHashes $ctx) -ceq $launchers) 'no data or launcher effect'
}
F 48 'busy predecessor leaves the host and active pointer untouched' {
    $ctx=Use (New-Context 48)
    Start-Host $ctx $ctx.old
    Set-Behavior $ctx @{'0.3.95'=@{busy=$true}}
    $r=Deploy $ctx
    Expect ($r.Exit -ne 0 -and $r.Log -like '*not idle*') 'busy host reported'
    Expect (@(Get-Stubs $ctx '--core v2 --engine-host').Count -eq 1 -and !(Test-Path -LiteralPath (Join-Path $ctx.root 'active'))) 'no stop or switch'
}
F 49 'post-drain inventory reports every outstanding record before switching' {
    $ctx=Use (New-Context 49)
    $db=Join-Path $ctx.data 'engine/state.db'
    $seed=@'
import hashlib, os, pathlib, sqlite3, sys
path=pathlib.Path(sys.argv[1]); db=sqlite3.connect(path)
assert db.execute('pragma journal_mode=wal').fetchone()[0]=='wal'
db.execute('pragma wal_autocheckpoint=0')
db.executescript("""BEGIN; UPDATE pair SET pending_json='{}';
    INSERT INTO folder_removal_submission VALUES(1,'job-one','Accepted'),(1,'job-two','Accepted');
    INSERT INTO folder_removal VALUES(1,'folder-one',1); COMMIT;""")
wal=pathlib.Path(str(path)+'-wal'); assert wal.stat().st_size > 32
main=sqlite3.connect(path.as_uri()+'?immutable=1&mode=ro',uri=True)
assert main.execute("select count(*) from pair where pending_json is not null").fetchone()==(0,)
pathlib.Path(sys.argv[2]).write_text(hashlib.sha256(path.read_bytes()).hexdigest()+' '+hashlib.sha256(wal.read_bytes()).hexdigest())
os._exit(0) # Preserve committed frames on last-close; no checkpoint.
'@
    [IO.File]::WriteAllText((Join-Path $ctx.state 'drain.py'),$seed)
    Set-Behavior $ctx @{'0.3.95'=@{commitAtDrain=$true}}
    # Do not let a host inherit the CI runner's output handles while waiting for the test to stop it.
    $env:SYNCSHARK_STUB_STATE=$ctx.state; $env:SYNCSHARK_STUB_DATA=$ctx.data
    $proc=Start-Process -FilePath $ctx.old.exe -ArgumentList '--core v2 --engine-host' -WindowStyle Hidden -PassThru
    Wait-Until { @(Get-ChildItem -LiteralPath $ctx.state -Filter 'host-*.running').Count -eq 1 } 'stub acquired lock'
    $beforeLaunchers=Get-LauncherHashes $ctx
    $r=Deploy $ctx; $j=Get-Journal $ctx
    Expect ((Get-LauncherHashes $ctx) -ceq $beforeLaunchers) 'blocked inventory must preserve both original launchers'
    if (!(Test-Path -LiteralPath (Join-Path $ctx.state 'wal-proven'))) { throw "Host did not leave committed WAL on exit: $($r.Log)" }
    Expect ($proc.HasExited -and $proc.ExitCode -eq 0 -and $j.stopRequested -and $r.Exit -ne 0 -and $r.Log -like '*inventory did not clear*') "refusal after graceful stop: $($r.Log)"
    foreach ($item in 'pending pair','job-one','job-two','folder-one') { Expect ($r.Log.Contains($item)) "missing inventory finding: $item" }
    $after=(Get-SyncSharkSha256 $db)+' '+(Get-SyncSharkSha256 ($db+'-wal'))
    Expect ($after -ceq [IO.File]::ReadAllText((Join-Path $ctx.state 'wal-proven'))) 'main and WAL bytes unchanged'
    $blocked=Recover $ctx
    Expect ($blocked.Exit -eq 2 -and $blocked.Log -like '*inventory did not clear*' -and (Get-LauncherHashes $ctx) -ceq $beforeLaunchers) 'blocked Recover must preserve both original launchers'
    Expect (((Get-SyncSharkSha256 $db)+' '+(Get-SyncSharkSha256 ($db+'-wal'))) -ceq $after) 'blocked Recover must preserve the stopped main and WAL bytes'
    Expect (!(Test-Path -LiteralPath (Join-Path $ctx.root 'active'))) 'no switch'
    $clean=Use (New-Context 49)
    $installed=Join-Path $clean.root 'versions/0.3.95'
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $installed))
    Copy-Item -LiteralPath $clean.old.out -Destination $installed -Recurse
    New-Item -ItemType Junction -Path (Join-Path $clean.root 'active') -Target $installed | Out-Null
    $ok=Deploy $clean 'phase-backup-verified'
    if (!(Get-Journal $clean)) { throw "Clean image not admitted: $($ok.Log)" }
    $null=Expect-Open $clean 'backup-verified'

    $errors=@()
    # A predecessor may release the store lock before its process exits. Close must still see the installed image.
    Set-Behavior $clean @{'0.3.95'=@{noLock=$true}}
    $oldProcess=Start-Stub $clean @{exe=(Join-Path $installed 'SyncShark.exe')} '--core v2 --engine-host'
    Wait-Until { @(Get-ChildItem -LiteralPath $clean.state -Filter 'host-*.running').Count -eq 1 } 'installed predecessor running without lock'
    $closed=CloseTx $clean 'stop-refused'
    if ($closed.Exit -eq 0 -or $closed.Log -notlike '*tracked Sync Shark process is running*') { $errors+='Close missed the admitted installed predecessor' }

    $fresh=Use (New-Context 49)
    $env:SYNCSHARK_STUB_STATE=$fresh.state; $env:SYNCSHARK_STUB_DATA=$fresh.data
    $null=Start-Stub $fresh $fresh.old '--core v2 --engine-host'
    Wait-Until { @(Get-ChildItem -LiteralPath $fresh.state -Filter 'host-*.running').Count -eq 1 } 'fresh predecessor running'
    $switched=Deploy $fresh 'phase-started'
    $refusal=(Get-Journal $fresh).needsOwner
    if ($refusal) { throw $refusal.reason }
    $null=Expect-Open $fresh 'launchers-switched'
    Assert-SyncSharkJunction (Join-Path $fresh.root 'active') (Join-Path $fresh.root 'versions/0.3.96')
    foreach ($name in 'cli-common.ps1','run-apply.ps1') {
        if (Test-Path -LiteralPath (Join-Path $fresh.launchers $name)) { $errors+="Predecessor launcher still callable: $name" }
    }
    $null=Expect-Open $fresh 'started'
    $null=Recover $fresh 'outcome'
    $outcome=(Get-Journal $fresh).outcome
    if (!$outcome -or $outcome.terminal -cne 'healthy') { $errors+='Recover must finish a start intent before any child was launched'; Write-Host $errors[-1] }
    else {
        [IO.File]::Delete((Join-Path $fresh.root 'product-root.txt')) # BC8 removes the legacy locator after migration.
        $reopened=Recover $fresh
        Expect ($reopened.Exit -eq 0) 'Recover must close the published transaction after the legacy data locator is removed'
        $null=Expect-Closed $fresh 'healthy'
    }
    $partial=Use (New-Context 49)
    $partialDb=Join-Path $partial.data 'engine/state.db'
    @'
import os, sqlite3, sys
con=sqlite3.connect(sys.argv[1]); assert con.execute('pragma journal_mode=wal').fetchone()[0]=='wal'
con.execute("update folder_removal_submission set phase='Finished'"); con.commit()
os._exit(0)
'@ | & python -I - $partialDb
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath ($partialDb+'-wal'))) { throw 'Cleared inventory must have committed WAL frames.' }
    [IO.File]::Delete($partialDb+'-shm') # A stopped copy may have no transient WAL index.
    $first=Join-Path $partial.launchers 'cli-common.ps1'; $second=Join-Path $partial.launchers 'run-apply.ps1'
    $beforeFirst=Get-SyncSharkSha256 $first; $beforeSecond=Get-SyncSharkSha256 $second
    $hold=[IO.File]::Open($second,'Open','Read','Read')
    try { $attempt=Deploy $partial } finally { $hold.Dispose() }
    $pending=Expect-Open $partial 'switch-requested'
    Expect ($attempt.Exit -ne 0 -and !(Test-Path $first) -and (Get-SyncSharkSha256 ($first+'.retired')) -ceq $beforeFirst -and (Get-SyncSharkSha256 $second) -ceq $beforeSecond -and !(Test-Path (Join-Path $partial.root 'active'))) 'second hold left only the first launcher retired before publication'
    $backup=Get-SyncSharkTreeIdentity $pending.backup.path
    # A same-content substitute is not the file retired by this transaction.
    [IO.File]::Move(($first+'.retired'),($first+'.held')); [IO.File]::Copy(($first+'.held'),($first+'.retired'))
    $foreign=Recover $partial
    Expect ($foreign.Exit -ne 0 -and !(Test-Path (Join-Path $partial.root 'active')) -and (Test-Path $second)) 'foreign retired file must not advance publication'
    [IO.File]::Move(($first+'.retired'),($first+'.foreign')); [IO.File]::Move(($first+'.held'),($first+'.retired'))
    $resumed=Recover $partial
    Expect ($resumed.Exit -eq 0) "Recover did not finish partial launcher retirement and publication: $($resumed.Log)"
    $finished=Expect-Closed $partial 'staged'
    Expect ($finished.T -ceq $pending.T -and (Get-SyncSharkTreeIdentity $finished.backup.path) -ceq $backup) 'same transaction and verified backup preserved'
    Expect (!(Test-Path $first) -and !(Test-Path $second) -and (Get-SyncSharkSha256 ($second+'.retired')) -ceq $beforeSecond) 'no legacy launch path restored'
    Assert-SyncSharkJunction (Join-Path $partial.root 'active') (Join-Path $partial.root 'versions/0.3.96')
    $missing=& $pwsh -NoProfile -NonInteractive -File $PSCommandPath -Fixture 999 2>&1
    if ($LASTEXITCODE -eq 0 -or ($missing -join ' ') -notlike '*Selected fixture did not run: 999*') { $errors+='Unexecuted fixture selected as success' }
    if ($errors.Count) { throw ($errors -join '; ') }

}
F 50 'startup refusal with zero pairs cannot close healthy' {
    $ctx=Use (New-Context 50); Start-Host $ctx $ctx.old
    Set-Behavior $ctx @{'0.3.95'=@{empty=$true}; '0.3.96'=@{empty=$true;refused=$true}}
    $r=Deploy $ctx; $j=Expect-NeedsOwner $ctx '*did not report an open store*'
    Expect (!$j.outcome) 'refusal never publishes a healthy result'
}
F 5 'a supervisor with no host: refused' {
    $ctx = Use (New-Context 5)
    $null = Start-Stub $ctx $ctx.old '--core v2 --engine-supervisor'
    Wait-Until { @(Get-Stubs $ctx '--core v2 --engine-supervisor').Count -eq 1 } 'the supervisor'
    $r = Deploy $ctx
    $j = Expect-Closed $ctx 'refused'
    Expect (@(@($j.errors) -like '*supervisor is running with no host*').Count -gt 0 -and -not $j.stopRequested) 'refused before any stop'
}
F 6 'failure after start leaves the candidate running' {
    $ctx = Use (New-Context 6)
    Start-Host $ctx $ctx.old
    Set-Behavior $ctx @{ '0.3.96' = @{ statusFail = $true } }
    $r = Deploy $ctx
    $j = Expect-NeedsOwner $ctx '*failure after start*'
    Expect (@(Get-Stubs $ctx '--notify-ready').Count -eq 1) 'new host left running'
}
F 30 'live-new closure verifies the installed host; a switched transaction cannot close as stop-refused' {
    $ctx = Use (New-Context 30)
    Start-Host $ctx $ctx.old
    Set-Behavior $ctx @{ '0.3.96' = @{ statusFail = $true } }
    $r = Deploy $ctx
    $null = Expect-NeedsOwner $ctx '*failure after start*'
    $r = CloseTx $ctx 'stop-refused'
    $j = Expect-NeedsOwner $ctx '*'
    Expect (@(Get-Pointer $ctx).Count -eq 1) 'open switch retained'
    Set-Behavior $ctx @{}
    $db = [IO.File]::Open((Join-Path $ctx.data 'engine\state.db'), 'Open', 'Read', 'None')
    try { $r = CloseTx $ctx 'live-new' } finally { $db.Dispose() }
    $j = Expect-Closed $ctx 'needs-owner-resolved'
    Expect (@($j.close)[-1].result -ceq 'passed') 'labelled live closure'
}
F 7 'PID reuse: a stale creation time is re-enumerated, never stopped' {
    $ctx = Use (New-Context 7)
    $stub = Start-Stub $ctx $ctx.old '--core v2 --engine-supervisor'
    Wait-Until { @(Get-Stubs $ctx '--core v2 --engine-supervisor').Count -eq 1 } 'the stub'
    foreach ($text in Import-DeployFunctions) { . ([scriptblock]::Create($text)) }
    $script:calls = 0
    function Get-CimInstance { param([string]$ClassName, [string]$Filter)
        $script:calls++
        if ($Filter -like 'ExecutablePath*') {
            if ($script:calls -eq 1) { return [pscustomobject]@{ ProcessId = $stub.Id; ExecutablePath = $ctx.old.exe; CommandLine = 'reused'; CreationDate = [DateTime]'2000-01-01' } }
            return @()
        }
        [pscustomobject]@{ ProcessId = $stub.Id; ExecutablePath = $ctx.old.exe; CommandLine = 'reused'; CreationDate = [DateTime]'2000-01-01' }
    }
    $tracked = @(Get-Tracked @($ctx.old.exe))
    Expect ($tracked.Count -eq 0 -and $script:calls -ge 3) 're-enumerated and dropped the stale record'
    Expect (-not $stub.HasExited) 'the stub was never stopped'
}
F 11 'UntrustedRoot+NotTimeValid, wrong root thumbprint and HashMismatch refused; UntrustedRoot alone accepted (R1)' {
    $ctx = Use (New-Context 11)
    $exe = Get-SyncSharkNormalizedPath $ctx.new.exe
    $cases = @(
        @{ name = 'UntrustedRoot+NotTimeValid'; patch = @{ Status = 'NotTrusted'; SignerChainStatuses = @('UntrustedRoot', 'NotTimeValid') }; like = '*not permitted*' },
        @{ name = 'wrong thumbprint'; patch = @{ Thumbprint = '00' * 20 }; like = '*Pinned signer identity mismatch*' },
        @{ name = 'HashMismatch'; patch = @{ Status = 'HashMismatch' }; like = '*not permitted*' })
    foreach ($case in $cases) {
        $o = New-Observation; foreach ($k in $case.patch.Keys) { $o[$k] = $case.patch[$k] }
        $ctx.overrides[$exe] = $o; Write-Observations $ctx
        $r = Deploy $ctx
        Expect ($r.Exit -ne 0 -and $r.Log -like $case.like -and @(Get-Pointer $ctx).Count -eq 0) "$($case.name) refused before any effect"
    }
    $o = New-Observation; $o.Status = 'NotTrusted'; $o.SignerChainStatuses = @('UntrustedRoot')
    $ctx.overrides[$exe] = $o; Write-Observations $ctx
    $r = Deploy $ctx
    $null = Expect-Closed $ctx 'staged'
}
F 12 'capacity check refuses insufficient backup space (unit, mocked free space; no end-to-end drop)' {
    foreach ($text in Import-DeployFunctions) { . ([scriptblock]::Create($text)) }
    $script:deployRoot = 'F:\deploys'; $script:dataTarget = 'F:\Data'
    function Get-FreeBytes { param([string]$Path) 1000 }
    $refused = $false; try { Assert-Capacity 1001 } catch { $refused = $_.Exception.Message -like 'Not enough free space*' }
    Expect $refused 'short backup capacity refused'
    Assert-Capacity 1000
}
F 13 'two generations running: refused' {
    $ctx = Use (New-Context 13)
    Set-Behavior $ctx @{ '0.3.96' = @{ noLock = $true } }
    Start-Host $ctx $ctx.old; Start-Host $ctx $ctx.new
    $r = Deploy $ctx
    $j = Expect-Closed $ctx 'refused'
    Expect (@(@($j.errors) -like '*More than one Sync Shark generation*').Count -gt 0) 'two generations refused'
}
F 16 'a second deploy while the pointer exists: refused' {
    $ctx = Use (New-Context 16)
    $r = Deploy $ctx 'phase-snapshot'
    $r = Deploy $ctx
    Expect ($r.Exit -ne 0 -and $r.Log -like '*is open: run -Recover*') 'second deploy refused'
    Expect (@(Get-ChildItem -LiteralPath (Get-DeployRoot $ctx) -Directory).Count -eq 1) 'no second transaction'
}
F 18 'crash after a child launched, before its PID was recorded: recover classifies by exe, args and creation time' {
    $ctx = Use (New-Context 18)
    Start-Host $ctx $ctx.old
    Set-Behavior $ctx @{ '0.3.95' = @{ stopDelayMs = 40000 } }
    $r = Deploy $ctx 'child-stop-launched'
    $r = Recover $ctx
    $j = Expect-NeedsOwner $ctx '*fix forward*'
    Expect (@($j.children | Where-Object { $_.purpose -ceq 'stop' })[0].exit -ceq 'joined') 'the unrecorded stop child was joined'
}
F 19 'killed run with a live, recorded stop child: -Recover joins it (the Ctrl+C journal state; a real Ctrl+C is not exercised)' {
    $ctx = Use (New-Context 19)
    Start-Host $ctx $ctx.old
    Set-Behavior $ctx @{ '0.3.95' = @{ stopDelayMs = 15000 } }
    $r = Deploy $ctx 'child-stop-pid'
    $r = Recover $ctx
    $j = Expect-NeedsOwner $ctx '*fix forward*'
    Expect (@($j.children | Where-Object { $_.purpose -ceq 'stop' })[0].exit -ceq 'joined') 'the recorded stop child was joined'
}
F 22 'WinVerifyTrust failure (unsigned stub) and a missing timestamp: refused' {
    $ctx = Use (New-Context 22)
    $r = Deploy $ctx '' $null -NoObservations
    Expect ($r.Exit -ne 0 -and $r.Log -like '*signature integrity verification failed*' -and @(Get-Pointer $ctx).Count -eq 0) 'WinVerifyTrust result refused'
    $o = New-Observation; $o.Rfc3161Present = $false
    $ctx.overrides[(Get-SyncSharkNormalizedPath $ctx.new.exe)] = $o; Write-Observations $ctx
    $r = Deploy $ctx
    Expect ($r.Exit -ne 0 -and $r.Log -like '*RFC3161 timestamp*') 'missing timestamp refused'
}
F 23 'a host that never answers trusted status (crash loop): refused before any effect' {
    $ctx = Use (New-Context 23)
    Start-Host $ctx $ctx.old
    $null = Start-Stub $ctx $ctx.old '--core v2 --engine-supervisor'
    Set-Behavior $ctx @{ '0.3.95' = @{ statusFail = $true } }
    $r = Deploy $ctx
    $j = Expect-Closed $ctx 'refused'
    Expect (@(@($j.errors) -like '*not idle*').Count -gt 0 -and -not $j.stopRequested) 'refused before any stop'
}
F 27 'no pointer while a journal is open: needs-owner, not recreated' {
    $ctx = Use (New-Context 27)
    $r = Deploy $ctx 'phase-snapshot'
    Remove-Item -LiteralPath @(Get-Pointer $ctx)[0].FullName
    $r = Deploy $ctx
    Expect ($r.Exit -eq 2 -and $r.Log -like '*no pointer while journal*') 'deploy refused needs-owner'
    $r = Recover $ctx
    Expect ($r.Exit -eq 2 -and @(Get-Pointer $ctx).Count -eq 0) 'recover refused, pointer not recreated'
}
F 28 'malformed or journal-less pointer: needs-owner, untouched' {
    $ctx = Use (New-Context 28)
    $rootId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Get-SyncSharkNormalizedPath $ctx.root).ToUpperInvariant())))
    $deploys = [IO.Directory]::CreateDirectory((Get-DeployRoot $ctx)).FullName
    $pointer = Join-Path $deploys "active-$rootId.json"
    $t = '20260101-000000-000-deadbeef'
    foreach ($content in '{ not json', ([ordered]@{ rootId = $rootId; T = $t; journalPath = (Join-Path $deploys "$t\journal.json"); installationRoot = $ctx.root; dataTarget = $ctx.data } | ConvertTo-Json)) {
        [IO.File]::WriteAllText($pointer, $content)
        $hash = (Get-FileHash -LiteralPath $pointer).Hash
        foreach ($run in @({ Deploy $ctx }, { Recover $ctx }, { CloseTx $ctx 'pointer-unproven' })) {
            $r = & $run
            Expect ($r.Exit -ne 0 -and ($r.Log -like '*pointer is malformed*' -or $r.Log -like '*no journal exists*') -and (Get-FileHash -LiteralPath $pointer).Hash -ceq $hash) 'refused, pointer untouched'
        }
    }
}
F 29 'closed journal whose pointer removal was interrupted: pointer removed by replay' {
    $ctx = Use (New-Context 29)
    $r = Deploy $ctx 'pointer-remove'
    $j = Get-Journal $ctx
    Expect ($j.state -ceq 'closed' -and @(Get-Pointer $ctx).Count -eq 1) 'closed with the pointer left'
    $r = Recover $ctx
    Expect ($r.Exit -eq 0 -and @(Get-Pointer $ctx).Count -eq 0) 'pointer removed'
}
F 31 'two processes match an unrecorded child intent: needs-owner, none adopted' {
    $ctx = Use (New-Context 31)
    Start-Host $ctx $ctx.old
    Set-Behavior $ctx @{ '0.3.95' = @{ stopDelayMs = 20000 } }
    $r = Deploy $ctx 'child-stop-launched'
    $null = Start-Stub $ctx $ctx.old '--cli engine host stop --yes'
    Wait-Until { @(Get-Stubs $ctx '--cli engine host stop --yes').Count -eq 2 } 'two stop processes'
    $r = Recover $ctx
    $null = Expect-NeedsOwner $ctx '*processes match the unrecorded stop child*'
}
F 32 'two deploys at once: the second is refused at the mutex' {
    $ctx = Use (New-Context 32)
    $mutex = Enter-SyncSharkMaintenanceMutex $ctx.root
    try { $r = Deploy $ctx } finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
    Expect ($r.Exit -ne 0 -and $r.Log -like '*could not be held*' -and -not (Test-Path -LiteralPath (Get-DeployRoot $ctx))) 'refused, nothing done'
}
F 33 'two -Recover at once: the second is refused; only the first advances the journal' {
    $ctx = Use (New-Context 33)
    $r = Deploy $ctx 'phase-snapshot'
    $before = Get-JournalBytes $ctx
    $mutex = Enter-SyncSharkMaintenanceMutex $ctx.root
    try { $r = Recover $ctx } finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
    Expect ($r.Exit -ne 0 -and $r.Log -like '*could not be held*' -and (Get-JournalBytes $ctx) -ceq $before) 'second refused, journal unchanged'
    $r = Recover $ctx
    $null = Expect-Closed $ctx 'refused'
}
F 34 'abandoned mutex after a script crash: the next -Recover owns it' {
    $ctx = Use (New-Context 34)
    $r = Deploy $ctx 'phase-stopped'
    Expect ($r.Exit -ne 0) 'crashed while holding the mutex'
    $r = Recover $ctx
    Expect ($r.Log -notlike '*could not be held*') 'abandoned mutex owned'
    $null = Expect-Closed $ctx 'refused'
}
F 35 'pointer naming another installation root or data target: refused on deploy, recover and close; journal-less close refused' {
    $ctx = Use (New-Context 35)
    $r = Deploy $ctx 'phase-snapshot'
    $pointer = @(Get-Pointer $ctx)[0].FullName
    $good = [IO.File]::ReadAllText($pointer)
    $runs = @({ Deploy $ctx }, { Recover $ctx }, { CloseTx $ctx 'pointer-unproven' })
    $p = $good | ConvertFrom-Json -AsHashtable; $p.installationRoot = Join-Path $ctx.root 'other'
    [IO.File]::WriteAllText($pointer, ($p | ConvertTo-Json)); $hash = (Get-FileHash -LiteralPath $pointer).Hash
    foreach ($run in $runs) { $r = & $run; Expect ($r.Exit -ne 0 -and $r.Log -like '*different installation root or data target*' -and (Get-FileHash -LiteralPath $pointer).Hash -ceq $hash) 'installationRoot mismatch refused' }
    [IO.File]::WriteAllText($pointer, $good)
    $other = Join-Path $ctx.root 'vol\Other'; [void][IO.Directory]::CreateDirectory("$other\state"); [IO.File]::WriteAllText("$other\state\instance.lock", '')
    # A store, so the bug #110 check passes and the pointer check decides.
    [void][IO.Directory]::CreateDirectory("$other\engine"); [IO.File]::WriteAllText("$other\engine\state.db", 'db')
    [IO.File]::WriteAllText((Join-Path $ctx.root 'product-root.txt'), $other)
    foreach ($run in $runs) { $r = & $run; Expect ($r.Exit -ne 0 -and $r.Log -like '*different installation root or data target*') 'dataTarget mismatch refused' }
    [IO.File]::WriteAllText((Join-Path $ctx.root 'product-root.txt'), $ctx.data)
    $j = Get-Journal $ctx; $journal = Join-Path (Get-DeployRoot $ctx) "$($j.T)\journal.json"
    Rename-Item -LiteralPath $journal -NewName 'journal.hidden'
    $r = CloseTx $ctx 'pointer-unproven'
    Expect ($r.Exit -ne 0 -and $r.Log -like '*no journal exists*') 'journal-less close refused'
    Rename-Item -LiteralPath (Join-Path (Get-DeployRoot $ctx) "$($j.T)\journal.hidden") -NewName 'journal.json'
}
F 36 'store already at fixed root: verified backup is disjoint' {
    $ctx=Use (New-Context 36)
    Remove-Item -LiteralPath (Join-Path $ctx.root 'product-root.txt')
    Copy-Item -LiteralPath (Join-Path $ctx.data 'engine'),(Join-Path $ctx.data 'state') -Destination $ctx.root -Recurse
    $r=Deploy $ctx 'phase-backup-verified'
    $j=Expect-Open $ctx 'backup-verified'
    Expect ((Get-SyncSharkTreeIdentity (Join-Path $ctx.root 'engine')) -ceq $j.backup.manifestSha256) 'fixed store was copied exactly before switch'
}
function New-Outside([string]$Name) { [IO.Directory]::CreateDirectory((Join-Path $base ("escape-$Name-" + [Guid]::NewGuid().ToString('N').Substring(0, 6)))).FullName }
function Get-StampDiff([string]$Before, [string]$Dir) { (Compare-Object ($Before -split "`n") ((Get-TreeStamp $Dir) -split "`n") | ForEach-Object { "$($_.SideIndicator)$($_.InputObject)" }) -join ' ; ' }
# Files by content and write time, directories by name only (NTFS settles directory times lazily).
function Get-TreeStamp([string]$Dir) { (@(Get-ChildItem -LiteralPath $Dir -Recurse -Force | Sort-Object FullName | ForEach-Object { if ($_.PSIsContainer) { "$($_.FullName)|dir" } else { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)|$((Get-FileHash -LiteralPath $_.FullName).Hash)" } }) -join "`n") }
function Write-ProductState($Ctx, [string]$RecoveryRoot, [switch]$Corrupt) {
    $value = '{"TransferSettings":{"LocalRecoveryRoot":' + (ConvertTo-Json $RecoveryRoot) + '}}'
    $sha = if ($Corrupt) { '0' * 64 } else { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($value))).ToLowerInvariant() }
    [IO.File]::WriteAllText((Join-Path $Ctx.data 'state\sync-product-state-a.json'), '{"SchemaVersion":1,"Generation":3,"Value":' + $value + ',"PayloadSha256":"' + $sha + '"}')
}
F 39 'escape: product-root.txt outside the test root is refused before any child or write' {
    $ctx = Use (New-Context 39)
    $outside = New-Outside 'data'
    [void][IO.Directory]::CreateDirectory("$outside\Data\state"); [IO.File]::WriteAllText("$outside\Data\state\instance.lock", 'lock')
    [IO.File]::WriteAllText((Join-Path $ctx.root 'product-root.txt'), "$outside\Data")
    $before = Get-TreeStamp $outside
    foreach ($run in @({ Deploy $ctx }, { Recover $ctx })) {
        $r = & $run
        Expect ($r.Exit -ne 0 -and $r.Log -like '*outside the test root*') 'refused'
    }
    Expect (-not (Test-Path -LiteralPath (Join-Path $ctx.root 'replacement-staging')) -and (Get-TreeStamp $outside) -ceq $before) "nothing written inside or outside: $(Get-StampDiff $before $outside)"
}
F 40 'escape: launchers that start an exe outside the test root are refused before any child or write' {
    $ctx = Use (New-Context 40)
    $other = Use (New-Context 40)
    $paths = @('cli-common.ps1', 'run-apply.ps1' | ForEach-Object { Join-Path $ctx.launchers $_ })
    foreach ($path in $paths) { [IO.File]::WriteAllText($path, ([IO.File]::ReadAllText($path)).Replace($ctx.old.exe, $other.old.exe)) }
    $before = Get-TreeStamp $other.root
    $r = Deploy $ctx
    Expect ($r.Exit -ne 0 -and $r.Log -like '*outside the test root*') 'refused'
    Expect (-not (Test-Path -LiteralPath (Join-Path $ctx.root 'replacement-staging')) -and (Get-TreeStamp $other.root) -ceq $before -and @(Get-Stubs $other).Count -eq 0) "no write, no outside process: $(Get-StampDiff $before $other.root)"
}
F 41 'escape: a -Build reached through a junction to outside the test root is refused before any child or write' {
    $ctx = Use (New-Context 41)
    $outside = New-Outside 'build'
    [void][IO.Directory]::CreateDirectory((Join-Path $ctx.root 'builds'))
    New-Item -ItemType Junction -Path (Join-Path $ctx.root 'builds\jcand') -Target $outside | Out-Null
    $build = New-StubBuild $ctx 'jcand' '0.3.96' $ctx.old.pv
    $before = Get-TreeStamp $outside
    $r = Deploy $ctx '' $build
    Expect ($r.Exit -ne 0 -and $r.Log -like '*passes through the reparse point*') 'refused'
    Expect (-not (Test-Path -LiteralPath (Join-Path $ctx.root 'replacement-staging')) -and (Get-TreeStamp $outside) -ceq $before) "nothing written inside or outside: $(Get-StampDiff $before $outside)"
}
F 43 'Recovery root from LocalRecoveryRoot: inside the data folder refused (R4); unavailable store refused; elsewhere recorded' {
    $ctx = Use (New-Context 43); Write-ProductState $ctx (Join-Path $ctx.data 'recovery')
    $r = Deploy $ctx
    $j = Expect-Closed $ctx 'refused'
    Expect (@(@($j.errors) -like '*inside the data folder*').Count -gt 0) 'R4 refusal'
    $ctx = Use (New-Context 43); Write-ProductState $ctx 'F:\x' -Corrupt
    $r = Deploy $ctx
    $j = Expect-Closed $ctx 'refused'
    Expect (@(@($j.errors) -like '*store is unavailable*').Count -gt 0) 'unavailable refused'
    $ctx = Use (New-Context 43); $recovery = Join-Path $ctx.root 'recovery'; Write-ProductState $ctx $recovery
    $r = Deploy $ctx
    $j = Expect-Closed $ctx 'staged'
    Expect (Same-Path $j.snapshot.recoveryRoot $recovery) 'Recovery root recorded'
}
F 44 'chain publication fails after a durable success: nothing compensated; -Recover publishes and closes' {
    $ctx = Use (New-Context 44)
    Start-Host $ctx $ctx.old
    $directory = [IO.DirectoryInfo]::new($ctx.new.root)
    $rule = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.WindowsIdentity]::GetCurrent().User, 'CreateFiles, CreateDirectories', 'None', 'None', 'Deny')
    $acl = [IO.FileSystemAclExtensions]::GetAccessControl($directory, 'Access'); $acl.AddAccessRule($rule); [IO.FileSystemAclExtensions]::SetAccessControl($directory, $acl)
    try { $r = Deploy $ctx }
    finally { $acl = [IO.FileSystemAclExtensions]::GetAccessControl($directory, 'Access'); [void]$acl.RemoveAccessRule($rule); [IO.FileSystemAclExtensions]::SetAccessControl($directory, $acl) }
    $j = Get-Journal $ctx
    Expect ($r.Exit -eq 2 -and $j.state -ceq 'open' -and $j.outcome -and $j.outcome.terminal -ceq 'healthy') 'success kept open for publication'
    Expect (@(Get-Stubs $ctx '--notify-ready' | Where-Object ExecutablePath -ieq (Join-Path $ctx.root 'versions/0.3.96/Sync Shark.exe')).Count -eq 1) 'new host still running'
    $r = Recover $ctx
    $null = Expect-Closed $ctx 'healthy'
    Expect (Test-Path -LiteralPath (Join-Path $ctx.new.root 'deploy-log.json')) 'published by -Recover'
}
F 45 'a tampered module, or another build''s tools copy, is refused before any module code runs' {
    $ctx = Use (New-Context 45)
    $marker = Join-Path $ctx.root 'module-ran.txt'
    $build = New-StubBuild $ctx 'cand-tampered' '0.3.96' $ctx.old.pv
    [IO.File]::AppendAllText((Join-Path $build.out 'tools\SyncShark.Release.psm1'), "`n[IO.File]::WriteAllText('$marker', 'ran')`n")
    $r = Deploy $ctx '' $build
    Expect ($r.Exit -ne 0 -and $r.Log -like '*SyncShark.Release.psm1 differs*' -and -not (Test-Path -LiteralPath $marker)) 'tampered module refused before import'
    $r = Invoke-Tool $ctx @('-Build', $build.root, '-LauncherDir', $ctx.launchers) '' $ctx.new.script
    Expect ($r.Exit -ne 0 -and $r.Log -like "*own outputDirectory\tools*" -and -not (Test-Path -LiteralPath $marker)) 'another build''s tools copy refused'
}
F 46 'backup keeps file and directory attributes and creation and last-write times' {
    $ctx = Use (New-Context 46)
    $dir = Join-Path $ctx.data 'meta'; [void][IO.Directory]::CreateDirectory($dir)
    $file = Join-Path $dir 'keep.txt'; [IO.File]::WriteAllText($file, 'keep')
    $f = [IO.FileInfo]::new($file); $f.CreationTimeUtc = [DateTime]::new(2001, 2, 3, 4, 5, 6, [DateTimeKind]::Utc); $f.LastWriteTimeUtc = [DateTime]::new(2002, 3, 4, 5, 6, 7, [DateTimeKind]::Utc); $f.Attributes = 'Hidden, ReadOnly, Archive'
    $d = [IO.DirectoryInfo]::new($dir); $d.CreationTimeUtc = [DateTime]::new(2003, 4, 5, 6, 7, 8, [DateTimeKind]::Utc); $d.LastWriteTimeUtc = [DateTime]::new(2004, 5, 6, 7, 8, 9, [DateTimeKind]::Utc); $d.Attributes = 'Hidden, Directory'
    $root = [IO.DirectoryInfo]::new($ctx.data); $root.CreationTimeUtc = [DateTime]::new(2005, 6, 7, 8, 9, 10, [DateTimeKind]::Utc)
    function Stamp([string]$Path) { $i = Get-Item -LiteralPath $Path -Force; "$($i.Attributes)|$($i.CreationTimeUtc.Ticks)|$($i.LastWriteTimeUtc.Ticks)" }
    $expected = @{ file = (Stamp $file); dir = (Stamp $dir); root = (Stamp $ctx.data) }
    $r = Deploy $ctx 'phase-launchers-switched'
    $j = Get-Journal $ctx
    Expect ((Stamp (Join-Path $j.backup.path 'meta\keep.txt')) -ceq $expected.file -and (Stamp (Join-Path $j.backup.path 'meta')) -ceq $expected.dir -and (Stamp $j.backup.path) -ceq $expected.root) 'backup keeps metadata'
}

F 47 'bug #110: product-root.txt missing while the store exists elsewhere: refused before any effect, the recorded root named, the pointer not written' {
    $ctx = Use (New-Context 47)
    $r = Deploy $ctx 'phase-snapshot'
    Remove-Item -LiteralPath (Join-Path $ctx.root 'product-root.txt')
    $data = Get-TreeStamp $ctx.data; $deploys = Get-TreeStamp (Get-DeployRoot $ctx)
    $r = Deploy $ctx
    Expect ($r.Exit -ne 0 -and $r.Log -like '*holds no engine\state.db*' -and $r.Log -like "*recorded $($ctx.data)*") 'refused, naming the recorded data root'
    Expect (-not (Test-Path -LiteralPath (Join-Path $ctx.root 'product-root.txt')) -and -not (Test-Path -LiteralPath (Join-Path $ctx.root 'engine'))) 'pointer not written, no store created at the default root'
    Expect ((Get-TreeStamp $ctx.data) -ceq $data -and (Get-TreeStamp (Get-DeployRoot $ctx)) -ceq $deploys) 'data and deploy records unchanged'
    # A corrupt journal: the primary diagnosis is still reported and no expected root is invented.
    $journal = Join-Path (Get-DeployRoot $ctx) "$((Get-Journal $ctx).T)\journal.json"
    [IO.File]::WriteAllText($journal, '{ not json')
    $deploys = Get-TreeStamp (Get-DeployRoot $ctx)
    $r = Deploy $ctx
    Expect ($r.Exit -ne 0 -and $r.Log -like '*holds no engine\state.db*' -and $r.Log -like '*expected root: unavailable*') 'corrupt journal: diagnosis reported, expected root unavailable'
    Expect ((Get-TreeStamp $ctx.data) -ceq $data -and (Get-TreeStamp (Get-DeployRoot $ctx)) -ceq $deploys -and -not (Test-Path -LiteralPath (Join-Path $ctx.root 'product-root.txt'))) 'corrupt journal: nothing changed'
}
$results | Sort-Object Fixture | Format-Table Fixture, Result, Ms, Name -AutoSize | Out-String -Width 220 | Write-Host
if ($Fixture) {
    foreach ($number in $Fixture.Split(',')) {
        if ($number -notin @($results | ForEach-Object { [string]$_.Fixture })) { throw "Selected fixture did not run: $number" }
    }
}
$failed = @($results | Where-Object Result -ne 'PASS')
foreach ($f in $failed) { Write-Host "FAIL $($f.Fixture): $($f.Detail)" }
Write-Host "Fixture roots are under $base; no production process or registry write was performed."
if ($failed.Count) { exit 1 }
exit 0
