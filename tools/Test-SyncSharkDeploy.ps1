# Fault fixtures 1-38 for tools/Deploy-SyncShark.ps1 (DESIGN-ONE-DEPLOY-11A and addenda). Test mode only.
# Every root lives under %TEMP%\sync-shark-release-tests; stub builds stand in for Sync Shark; the registry is only read.
# The harness ends only stub processes whose image lies under its own fixture root, by PID.
[CmdletBinding()]
param([string]$Fixture = '') # comma-separated subset, e.g. 2,9
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:SYNC_SHARK_RELEASE_TEST_MODE -cne '1') { throw 'Test mode is required.' }
if ((Get-Location).Drive.Name -ne 'F' -or [IO.Path]::GetPathRoot([IO.Path]::GetTempPath()) -ine 'F:\') { throw 'Run fixtures with cwd and TEMP on F:.' }
Remove-Item Env:HTTPS_PROXY, Env:HTTP_PROXY -ErrorAction SilentlyContinue
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force
$base = Join-Path ([IO.Path]::GetTempPath()) 'sync-shark-release-tests'
$pwsh = (Get-Process -Id $PID).Path
$script = Join-Path $PSScriptRoot 'Deploy-SyncShark.ps1'
$signer = (Get-SyncSharkSignerIdentity).roles.publisher
$commits = @{ '0.3.75' = 'a' * 40; '0.3.76' = 'b' * 40; '0.3.77' = 'c' * 40 }

# ---- Stub Sync Shark (CLI shapes of engine-cli.json, host/supervisor argument shapes of AutoSyncProcess.cs) ----
$stubSource = @'
using System; using System.Diagnostics; using System.IO; using System.Reflection; using System.Text; using System.Text.Json; using System.Threading;
static class Stub {
    static readonly string State = Environment.GetEnvironmentVariable("SYNCSHARK_STUB_STATE");
    static readonly string Data = Environment.GetEnvironmentVariable("SYNCSHARK_STUB_DATA");
    static readonly string Exe = Environment.ProcessPath;
    static readonly string Version = typeof(Stub).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion.Split('+')[0];
    static readonly string Key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Exe.ToUpperInvariant()))).Substring(0, 16);
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
        switch (args) {
            case "--core v2 --engine-host": return Host();
            case "--core v2 --engine-supervisor": return Wait(Flag("supervisorIgnoresStop"));
            case "--cli engine host start --yes": return Start();
            case "--cli engine host stop --yes": return Stop();
            case "--cli engine host status --json":
                if (Flag("statusFail")) { Console.Error.WriteLine("stub: status refused"); return 1; }
                Json(new { state = Hosts().Length > 0 ? "Watching" : "Unavailable", message = "stub" }); return 0;
            case "--cli engine status --json": Json(new { pairs = new object[0] }); return 0;
            case "--cli engine pairs list --json": Json(new { pairs = new[] { new { id = "pair-a", enabled = true } } }); return 0;
            case "--cli engine settings list --json":
                var root = B("recoveryRoot");
                Json(new { settings = root.ValueKind == JsonValueKind.String ? new object[] { new { name = "RecoveryRoot", value = root.GetString(), overridden = true } } : new object[0] });
                return 0;
            default: return Wait(false);
        }
    }
    static int Host() {
        FileStream held = Flag("noLock") ? null : new FileStream(Path.Combine(Data, "state", "instance.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try {
            if (Flag("hostWrites")) File.AppendAllText(Path.Combine(Data, "host-" + Version + ".txt"), DateTime.UtcNow.ToString("O") + Environment.NewLine);
            var marker = Path.Combine(State, "host-" + Key + "-" + Environment.ProcessId + ".running");
            File.WriteAllText(marker, Exe);
            try { return Wait(false); } finally { File.Delete(marker); }
        } finally { if (held != null) held.Dispose(); }
    }
    static int Wait(bool ignoreStop) {
        var end = DateTime.UtcNow.AddMinutes(10); // leak guard for fixtures
        while (DateTime.UtcNow < end) { if (!ignoreStop && File.Exists(StopFile)) return 0; Thread.Sleep(50); }
        return 0;
    }
    static Process Spawn(string args) => Process.Start(new ProcessStartInfo(Exe, args) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
    static int Start() {
        if (Flag("startFail")) { if (Flag("startLeavesSupervisor")) Spawn("--core v2 --engine-supervisor"); Console.Error.WriteLine("stub: start failed"); return 1; }
        if (Hosts().Length > 0) return 0;
        File.Delete(StopFile);
        var p = Spawn("--core v2 --engine-host");
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
    $cache = Join-Path $base "deploy-stub-cache\$Version"
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
    & 'C:\Program Files\dotnet\dotnet.exe' build (Join-Path $project 'SyncShark.csproj') -c Release -o $cache -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -nologo -v:q | Out-Host
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
    foreach ($dll in 'SyncShark.Core.dll', 'SyncShark.Engine.dll', 'SyncShark.Explorer.dll') { Copy-Item -LiteralPath (Join-Path $out 'SyncShark.dll') -Destination (Join-Path $out $dll) }
    Copy-Item -LiteralPath (Join-Path $out 'SyncShark.exe') -Destination (Join-Path $out 'SyncSharkDiscovery.exe')
    if (-not $Legacy) {
        [void][IO.Directory]::CreateDirectory((Join-Path $out 'tools'))
        foreach ($tool in 'SyncShark.Release.psm1', 'Deploy-SyncShark.ps1') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $tool) -Destination (Join-Path $out "tools\$tool") }
    }
    if ($Mutate) { & $Mutate $out }
    Write-Manifest $root $Name $Version $Previous $out -Legacy:$Legacy -Unlisted $Unlisted
    $build = [ordered]@{ root = $root; out = $out; exe = (Join-Path $out 'SyncShark.exe'); script = (Join-Path $out 'tools\Deploy-SyncShark.ps1'); pv = "$Version+$($commits[$Version])" }
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
        foreach ($name in 'SyncShark.exe', 'SyncShark.dll', 'SyncShark.Core.dll', 'SyncShark.Engine.dll', 'SyncSharkDiscovery.exe', 'SyncShark.Explorer.dll') {
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
    $bytes = [byte[]]::new(65536); [Security.Cryptography.RandomNumberGenerator]::Fill($bytes); [IO.File]::WriteAllBytes((Join-Path $ctx.data 'engine\state.db'), $bytes)
    [IO.File]::WriteAllText((Join-Path $ctx.data 'v2\logs\autosync\autosync.log'), "2026-01-01T00:00:00.0000000+00:00 host start`n")
    [IO.File]::WriteAllText((Join-Path $root 'product-root.txt'), $ctx.data)
    # Launchers are written first: the builds below give any on-write scanner time to release them.
    $oldExe = Join-Path $root 'builds\old-0375\bin\out\SyncShark.exe'
    $cli = Join-Path $ctx.launchers 'cli-common.ps1'; $run = Join-Path $ctx.launchers 'run-apply.ps1'
    [IO.File]::WriteAllText($cli, "`$Exe = '$oldExe'`n& `$Exe --cli @args`n")
    [IO.File]::WriteAllText($run, "param([string]`$Exe = '$oldExe')`n& `$Exe --cli engine repair --yes`n")
    $old = New-StubBuild $ctx 'old-0375' '0.3.75' '0.3.74+0000000000000000000000000000000000000000' -Legacy
    $null = New-StubBuild $ctx 'new-0376' '0.3.76' $old.pv
    $ctx.old = $ctx.builds['old-0375']; $ctx.new = $ctx.builds['new-0376']
    $attempt = 'deploy-attempt-20261105-000000-000-aaaaaaaa'
    [void][IO.Directory]::CreateDirectory((Join-Path $old.root $attempt))
    $backups = @($cli, $run | ForEach-Object { $bak = "$_.pre-0375-$attempt.bak"; Copy-Item -LiteralPath $_ -Destination $bak; $bak })
    [IO.File]::WriteAllText((Join-Path $old.root "$attempt\launcher-recovery.json"), ([ordered]@{ previousExe = 'C:\previous\SyncShark.exe'; paths = @($cli, $run); backups = $backups } | ConvertTo-Json))
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
function CloseTx($Ctx, [string]$Disposition, [string]$Layout = '') {
    $arguments = @('-Close', '-Disposition', $Disposition); if ($Layout) { $arguments += @('-Layout', $Layout) }
    Invoke-Tool $Ctx $arguments
}
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
    & $Build.exe --cli engine host start --yes | Out-Null
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
function Get-Staging($Ctx, $J) { "$($Ctx.data).restore-staging-$($J.T)" }
function Get-Failed($Ctx, $J) { "$($Ctx.data).failed-$($J.T)" }
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
# Restart-after-crash journal: launchers switched, host not running, data then changed so a staged restore is due.
function New-RestoreDue([int]$Number) {
    $ctx = New-Context $Number
    $r = Deploy $ctx 'phase-launchers-switched'
    Expect ($r.Exit -ne 0) 'deploy crashed'
    $null = Expect-Open $ctx 'launchers-switched'
    Edit-Data $ctx
    $ctx
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
    $recovery = Get-Content -LiteralPath (Join-Path $ctx.new.root "deploy-attempt-$($j.T)\launcher-recovery.json") -Raw | ConvertFrom-Json -AsHashtable
    Expect ($record.terminal -ceq 'healthy' -and $record.hostWasRunning -and $record.started -and -not $record.Contains('runStarted')) 'deploy-log from the journal only'
    Expect (@($recovery.backups).Count -eq 2 -and @($recovery.backups | Where-Object { Test-Path -LiteralPath $_ }).Count -eq 2) '.bak files exist and are recorded'
    Expect (@(Get-Stubs $ctx '--core v2 --engine-host' | Where-Object ExecutablePath -ieq $ctx.new.exe).Count -eq 1) 'new host running'
    Expect (Test-Path -LiteralPath (Join-Path (Get-DeployRoot $ctx) 'DELETION-CANDIDATES.md')) 'candidates listed'
    Expect ($null -eq $j.snapshot.recoveryRoot) 'Recovery root absent when none is set'
    $variants = @(
        @{ name = 'nomod'; unlisted = @('tools/SyncShark.Release.psm1'); mutate = $null; like = '*does not list tools/SyncShark.Release.psm1*' },
        @{ name = 'nodeps'; unlisted = @(); mutate = { param($out) Remove-Item -LiteralPath (Join-Path $out 'SyncShark.deps.json') }; like = '*Missing runtime metadata: syncshark.deps.json*' },
        @{ name = 'noconfig'; unlisted = @(); mutate = { param($out) Remove-Item -LiteralPath (Join-Path $out 'SyncShark.runtimeconfig.json') }; like = '*Missing runtime metadata: syncshark.runtimeconfig.json*' },
        @{ name = 'extra'; unlisted = @('extra.txt'); mutate = { param($out) [IO.File]::WriteAllText((Join-Path $out 'extra.txt'), 'x') }; like = '*Unlisted file in build tree: extra.txt*' })
    $fresh = Use (New-Context 37)
    foreach ($v in $variants) {
        $build = New-StubBuild $fresh "cand-$($v.name)" '0.3.76' $fresh.old.pv -Mutate $v.mutate -Unlisted $v.unlisted
        $r = Deploy $fresh '' $build
        Expect ($r.Exit -ne 0 -and $r.Log -like $v.like) "$($v.name) refused ($($r.Log.Trim()))"
        Expect (@(Get-Pointer $fresh).Count -eq 0) "$($v.name) left no pointer"
    }
}
F 38 'two deploys in a row; .bak crash accepted; tampered .bak refused' {
    $ctx = Use (New-Context 38)
    Start-Host $ctx $ctx.old
    $r = Deploy $ctx
    $null = Expect-Closed $ctx 'healthy'
    $next = New-StubBuild $ctx 'new-0377' '0.3.77' $ctx.new.pv
    $r = Deploy $ctx '' $next
    $j = Expect-Closed $ctx 'healthy'
    Expect (Same-Path $j.predecessor.root $ctx.new.root) 'predecessor derived from the first deploy'
    Expect (Test-Path -LiteralPath (Join-Path $next.root 'deploy-log.json')) 'second chain record'
    $c = Use (New-Context 38)
    $r = Deploy $c 'bak-renamed'
    $j = Expect-Open $c ''
    Expect ($null -ne $j.launchers[0].bak.intentUtc -and $null -eq $j.launchers[0].bak.done -and (Test-Path -LiteralPath $j.launchers[0].bak.path)) 'crash after rename, before done'
    $r = Recover $c
    $j = Expect-Closed $c 'compensated'
    Expect ($null -ne $j.launchers[0].bak.done) 'recover accepted the matching .bak'
    $t = Use (New-Context 38)
    $r = Deploy $t 'bak-renamed'
    $j = Get-Journal $t
    [IO.File]::AppendAllText($j.launchers[0].bak.path, '# tampered')
    $r = Recover $t
    $null = Expect-NeedsOwner $t '*does not hold the journaled original bytes*'
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
F 2 'crash after swap-1 (after done, and between rename and done): recover validates the missing leaf, runs swap-2' {
    foreach ($crash in 'phase-swap-1', 'swap-1-renamed') {
        $ctx = Use (New-RestoreDue 2)
        $r = Recover $ctx $crash
        $j = Expect-Open $ctx ''
        Expect (-not (Test-Path -LiteralPath $ctx.data) -and (Test-Path -LiteralPath (Get-Failed $ctx $j))) "$crash left Data absent"
        $r = Recover $ctx
        $j = Expect-Closed $ctx 'restored'
        Expect ((Get-DataId $ctx) -ceq $j.backup.manifestSha256 -and (Test-Path -LiteralPath (Get-Failed $ctx $j))) 'data restored, failed-T kept'
        Expect (@($j.launchers | Where-Object { (Get-FileHash -LiteralPath $_.path).Hash.ToLowerInvariant() -cne $_.originalSha256 }).Count -eq 0) 'launchers on the old side'
        Expect-NoChain $ctx.new
    }
    $ctx = Use (New-RestoreDue 2)
    $r = Recover $ctx 'phase-swap-1'
    $vol = Join-Path $ctx.root 'vol'; $real = Join-Path $ctx.root 'vol-real'
    Rename-Item -LiteralPath $vol -NewName 'vol-real'
    New-Item -ItemType Junction -Path $vol -Target $real | Out-Null
    $r = Recover $ctx
    $j = Expect-Open $ctx 'swap-1'
    Expect (-not (@($j.phases | ForEach-Object name) -contains 'swap-2') -and @(Get-Pointer $ctx).Count -eq 1) 'reparse ancestor refused'
    [IO.Directory]::Delete($vol)
    Rename-Item -LiteralPath $real -NewName 'vol-moved'
    $r = Recover $ctx
    $j = Expect-Open $ctx 'swap-1'
    Expect (-not (@($j.phases | ForEach-Object name) -contains 'swap-2')) 'missing ancestor refused'
}
F 9 'repeated -Recover on a restored journal after a newer write: no-op, the write survives' {
    $ctx = Use (New-RestoreDue 9)
    $r = Recover $ctx
    $null = Expect-Closed $ctx 'restored'
    [IO.File]::WriteAllText((Join-Path $ctx.data 'newer.txt'), 'newer')
    $before = Get-DataId $ctx
    $r = Recover $ctx
    Expect ($r.Exit -eq 0 -and (Get-DataId $ctx) -ceq $before) 'nothing replayed'
}
F 3 'staging half-copied: recover re-verifies and completes' {
    $ctx = Use (New-RestoreDue 3)
    [IO.File]::WriteAllText((Join-Path $ctx.data 'engine\second.bin'), 'second file')
    $r = Recover $ctx 'staging-file'
    $j = Expect-Open $ctx ''
    Expect ((Test-Path -LiteralPath (Get-Staging $ctx $j)) -and -not (@($j.phases | ForEach-Object name) -contains 'restore-staged')) 'staging partial'
    $r = Recover $ctx
    $j = Expect-Closed $ctx 'restored'
    Expect ((Get-DataId $ctx) -ceq $j.backup.manifestSha256) 'data restored from the backup'
}
F 4 'one launcher switched: per-file restore; a foreign hash is refused' {
    $ctx = Use (New-Context 4); $launchers = Get-LauncherHashes $ctx
    $r = Deploy $ctx 'launcher-switched-0'
    $r = Recover $ctx
    $null = Expect-Closed $ctx 'compensated'
    Expect ((Get-LauncherHashes $ctx) -ceq $launchers) 'launchers restored per file'
    $ctx = Use (New-Context 4)
    $r = Deploy $ctx 'launcher-switched-0'
    [IO.File]::AppendAllText((Join-Path $ctx.launchers 'run-apply.ps1'), '# foreign edit')
    $r = Recover $ctx
    $null = Expect-NeedsOwner $ctx '*foreign launcher content*'
}
F 5 'a supervisor with no host: refused' {
    $ctx = Use (New-Context 5)
    $null = Start-Stub $ctx $ctx.old '--core v2 --engine-supervisor'
    Wait-Until { @(Get-Stubs $ctx '--core v2 --engine-supervisor').Count -eq 1 } 'the supervisor'
    $r = Deploy $ctx
    $j = Expect-Closed $ctx 'refused'
    Expect (@(@($j.errors) -like '*supervisor is running with no host*').Count -gt 0 -and -not $j.stopRequested) 'refused before any stop'
}
F 6 'failure after start: new host stopped, needs-owner, no restore' {
    $ctx = Use (New-Context 6)
    Start-Host $ctx $ctx.old
    Set-Behavior $ctx @{ '0.3.76' = @{ statusFail = $true } }
    $r = Deploy $ctx
    $j = Expect-NeedsOwner $ctx '*failure after start*'
    Expect ($j.needsOwner.reason -like '*stopped gracefully*') 'new host stopped'
    Expect (@($j.launchers | Where-Object { (Get-FileHash -LiteralPath $_.path).Hash.ToLowerInvariant() -cne $_.newSha256 }).Count -eq 0) 'launchers stay on the new build'
    Expect (-not (Test-Path -LiteralPath (Get-Staging $ctx $j)) -and -not (Test-Path -LiteralPath (Get-Failed $ctx $j))) 'no restore'
}
F 30 'live-new close reads no DB bytes; stop-refused without -Layout is refused' {
    $ctx = Use (New-Context 30)
    Start-Host $ctx $ctx.old
    Set-Behavior $ctx @{ '0.3.76' = @{ statusFail = $true } }
    $r = Deploy $ctx
    $null = Expect-NeedsOwner $ctx '*failure after start*'
    $r = CloseTx $ctx 'stop-refused'
    $j = Expect-NeedsOwner $ctx '*'
    Expect (@($j.close)[-1].result -like '*-Layout*') 'missing layout refused, pointer kept'
    Set-Behavior $ctx @{}
    Start-Host $ctx $ctx.new
    $db = [IO.File]::Open((Join-Path $ctx.data 'engine\state.db'), 'Open', 'Read', 'None')
    try { $r = CloseTx $ctx 'live-new' } finally { $db.Dispose() }
    $j = Expect-Closed $ctx 'needs-owner-resolved'
    Expect (@($j.close)[-1].label -ceq 'operational health only; full DB integrity unverified') 'labelled live closure'
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
F 8 'pre-existing Data.failed-T: refused, data intact' {
    $ctx = Use (New-RestoreDue 8); $before = Get-DataId $ctx
    $j = Get-Journal $ctx
    [void][IO.Directory]::CreateDirectory((Get-Failed $ctx $j))
    $r = Recover $ctx
    $j = Expect-NeedsOwner $ctx '*already exists*'
    Expect ((Get-DataId $ctx) -ceq $before -and -not (Test-Path -LiteralPath (Get-Staging $ctx $j))) 'data intact, nothing staged'
}
F 10 'a foreign folder renamed into Data: file ID mismatch refused' {
    $ctx = Use (New-RestoreDue 10)
    $orig = Join-Path $ctx.root 'vol\Data-original'
    Rename-Item -LiteralPath $ctx.data -NewName 'Data-original'
    Copy-Item -LiteralPath $orig -Destination $ctx.data -Recurse
    $before = Get-SyncSharkTreeIdentity $orig
    $r = Recover $ctx
    $null = Expect-NeedsOwner $ctx '*file ID changed*'
    Expect ((Get-SyncSharkTreeIdentity $orig) -ceq $before) 'original untouched'
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
F 12 'capacity check refuses short free space, summing needs on one volume (unit, mocked free space; no end-to-end drop)' {
    foreach ($text in Import-DeployFunctions) { . ([scriptblock]::Create($text)) }
    $script:deployRoot = 'F:\deploys'; $script:dataTarget = 'F:\Data'
    function Get-FreeBytes { param([string]$Path) 1000 }
    $refused = $false; try { Assert-Capacity 0 1001 } catch { $refused = $_.Exception.Message -like 'Not enough free space*' }
    Expect $refused 'staging need above free space refused'
    $refused = $false; try { Assert-Capacity 600 600 } catch { $refused = $true }
    Expect $refused 'needs on one volume are summed'
    Assert-Capacity 400 500
    $script:dataTarget = 'C:\Data'
    Assert-Capacity 900 900
}
F 13 'two generations running: refused' {
    $ctx = Use (New-Context 13)
    Set-Behavior $ctx @{ '0.3.76' = @{ noLock = $true } }
    Start-Host $ctx $ctx.old; Start-Host $ctx $ctx.new
    $r = Deploy $ctx
    $j = Expect-Closed $ctx 'refused'
    Expect (@(@($j.errors) -like '*More than one Sync Shark generation*').Count -gt 0) 'two generations refused'
}
F 14 'crash between creating staging and recording its ID: needs-owner, nothing adopted' {
    $ctx = Use (New-RestoreDue 14); $before = Get-DataId $ctx
    $r = Recover $ctx 'staging-created'
    $r = Recover $ctx
    $j = Expect-NeedsOwner $ctx '*no recorded ID*'
    Expect ((Test-Path -LiteralPath (Get-Staging $ctx $j)) -and (Get-DataId $ctx) -ceq $before) 'staging kept, data intact'
}
F 15 'staging ID matches but its content differs before swap-1: needs-owner' {
    $ctx = Use (New-RestoreDue 15); $before = Get-DataId $ctx
    $r = Recover $ctx 'phase-restore-staged'
    $j = Get-Journal $ctx
    Flip-Byte (Join-Path (Get-Staging $ctx $j) 'engine\state.db')
    $r = Recover $ctx
    $null = Expect-NeedsOwner $ctx '*staging content differs*'
    Expect ((Get-DataId $ctx) -ceq $before) 'data intact'
}
F 16 'a second deploy while the pointer exists: refused' {
    $ctx = Use (New-Context 16)
    $r = Deploy $ctx 'phase-snapshot'
    $r = Deploy $ctx
    Expect ($r.Exit -ne 0 -and $r.Log -like '*is open: run -Recover*') 'second deploy refused'
    Expect (@(Get-ChildItem -LiteralPath (Get-DeployRoot $ctx) -Directory).Count -eq 1) 'no second transaction'
}
F 17 'crash during start-pending: recover only joins and checks health, then closes' {
    $ctx = Use (New-Context 17)
    Start-Host $ctx $ctx.old
    $r = Deploy $ctx 'phase-launchers-switched'
    $r = Recover $ctx 'child-old-start-pid'
    $j = Expect-Open $ctx 'start-pending'
    $r = Recover $ctx
    $null = Expect-Closed $ctx 'compensated'
    Expect (@(Get-Stubs $ctx '--core v2 --engine-host' | Where-Object ExecutablePath -ieq $ctx.old.exe).Count -eq 1) 'old host running'
}
F 18 'crash after a child launched, before its PID was recorded: recover classifies by exe, args and creation time' {
    $ctx = Use (New-Context 18)
    Start-Host $ctx $ctx.old
    Set-Behavior $ctx @{ '0.3.75' = @{ stopDelayMs = 40000 } }
    $r = Deploy $ctx 'child-stop-launched'
    $r = Recover $ctx
    $j = Expect-Closed $ctx 'compensated'
    Expect (@($j.children | Where-Object { $_.purpose -ceq 'stop' })[0].exit -ceq 'joined') 'the unrecorded stop child was joined'
}
F 19 'killed run with a live, recorded stop child: -Recover joins it (the Ctrl+C journal state; a real Ctrl+C is not exercised)' {
    $ctx = Use (New-Context 19)
    Start-Host $ctx $ctx.old
    Set-Behavior $ctx @{ '0.3.75' = @{ stopDelayMs = 15000 } }
    $r = Deploy $ctx 'child-stop-pid'
    $r = Recover $ctx
    $j = Expect-Closed $ctx 'compensated'
    Expect (@($j.children | Where-Object { $_.purpose -ceq 'stop' })[0].exit -ceq 'joined') 'the recorded stop child was joined'
}
F 20 'start leaves only a supervisor: needs-owner; a new deploy refused; recover changes nothing' {
    $ctx = Use (New-Context 20)
    Start-Host $ctx $ctx.old
    Set-Behavior $ctx @{ '0.3.76' = @{ startFail = $true; startLeavesSupervisor = $true; supervisorIgnoresStop = $true } }
    $r = Deploy $ctx
    $null = Expect-NeedsOwner $ctx '*failure after start*'
    Expect (@(Get-Stubs $ctx '--core v2 --engine-supervisor').Count -eq 1) 'only the supervisor is left'
    $r = Deploy $ctx
    Expect ($r.Exit -ne 0 -and $r.Log -like '*is open: run -Recover*') 'new deploy refused'
    $before = Get-JournalBytes $ctx
    $r = Recover $ctx
    Expect ($r.Exit -eq 2 -and (Get-JournalBytes $ctx) -ceq $before) 'recover made no change'
}
F 21 'failure before the backup with the host running: old host restarted, health checked, compensated' {
    $ctx = Use (New-Context 21)
    $elsewhere = [IO.Directory]::CreateDirectory((Join-Path $ctx.root 'elsewhere'))
    New-Item -ItemType Junction -Path (Join-Path $ctx.data 'link') -Target $elsewhere.FullName | Out-Null
    Start-Host $ctx $ctx.old
    $r = Deploy $ctx
    $j = Expect-Closed $ctx 'compensated'
    Expect ($j.stopRequested -and @(Get-Stubs $ctx '--core v2 --engine-host' | Where-Object ExecutablePath -ieq $ctx.old.exe).Count -eq 1) 'old host restarted'
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
    Set-Behavior $ctx @{ '0.3.75' = @{ statusFail = $true } }
    $r = Deploy $ctx
    $j = Expect-Closed $ctx 'refused'
    Expect (@(@($j.errors) -like '*trusted peer*').Count -gt 0 -and -not $j.stopRequested) 'refused before any stop'
}
F 25 'staging changed after swap-1: swap-2 re-check fails, Data absent, failed-T kept' {
    $ctx = Use (New-RestoreDue 25)
    $r = Recover $ctx 'phase-swap-1'
    $j = Get-Journal $ctx
    Flip-Byte (Join-Path (Get-Staging $ctx $j) 'engine\state.db')
    $r = Recover $ctx
    Expect ($r.Exit -ne 0) 'recover refused'
    $j = Get-Journal $ctx
    Expect ($j.state -ceq 'open' -and -not (@($j.phases | ForEach-Object name) -contains 'swap-2')) 'swap-2 never ran'
    Expect (-not (Test-Path -LiteralPath $ctx.data) -and (Test-Path -LiteralPath (Get-Failed $ctx $j))) 'Data absent, failed-T kept'
}
F 26 'crash after old-start-intent while the old host writes: recover never restores again' {
    $ctx = Use (New-Context 26)
    Start-Host $ctx $ctx.old
    Set-Behavior $ctx @{ '0.3.75' = @{ hostWrites = $true } }
    $r = Deploy $ctx 'phase-launchers-switched'
    Edit-Data $ctx
    $r = Recover $ctx 'child-old-start-pid'
    $written = Join-Path $ctx.data 'host-0.3.75.txt'
    Wait-Until { Test-Path -LiteralPath $written } 'the old host write'
    $r = Recover $ctx
    $j = Expect-Closed $ctx 'restored'
    Expect (Test-Path -LiteralPath $written) "the old host's write survived"
    Expect (@($j.phases | Where-Object { $_.name -ceq 'swap-2' }).Count -eq 1) 'data restored exactly once'
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
        foreach ($run in @({ Deploy $ctx }, { Recover $ctx }, { CloseTx $ctx 'pointer-unproven' 'none' })) {
            $r = & $run
            Expect ($r.Exit -ne 0 -and $r.Log -like '*needs-owner*' -and (Get-FileHash -LiteralPath $pointer).Hash -ceq $hash) 'refused, pointer untouched'
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
    Set-Behavior $ctx @{ '0.3.75' = @{ stopDelayMs = 20000 } }
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
    $runs = @({ Deploy $ctx }, { Recover $ctx }, { CloseTx $ctx 'pointer-unproven' 'none' })
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
    $r = CloseTx $ctx 'pointer-unproven' 'none'
    Expect ($r.Exit -ne 0 -and $r.Log -like '*no journal exists*') 'journal-less close refused'
    Rename-Item -LiteralPath (Join-Path (Get-DeployRoot $ctx) "$($j.T)\journal.hidden") -NewName 'journal.json'
}
F 36 'no product-root.txt: the deploy root lies inside the data target, refused before any effect' {
    $ctx = Use (New-Context 36)
    Remove-Item -LiteralPath (Join-Path $ctx.root 'product-root.txt')
    # A store at the default root, so the bug #110 store check passes and ruling 3 is what refuses.
    [void][IO.Directory]::CreateDirectory((Join-Path $ctx.root 'engine')); [IO.File]::WriteAllText((Join-Path $ctx.root 'engine\state.db'), 'db')
    $r = Deploy $ctx
    Expect ($r.Exit -ne 0 -and $r.Log -like '*Overlapping roots*' -and -not (Test-Path -LiteralPath (Join-Path $ctx.root 'replacement-staging'))) 'refused before any effect'
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
    $attempt = 'deploy-attempt-20261105-000000-000-bbbbbbbb'
    $paths = @('cli-common.ps1', 'run-apply.ps1' | ForEach-Object { Join-Path $ctx.launchers $_ })
    foreach ($path in $paths) { [IO.File]::WriteAllText($path, ([IO.File]::ReadAllText($path)).Replace($ctx.old.exe, $other.old.exe)) }
    $backups = @($paths | ForEach-Object { $bak = "$_.pre-0375-$attempt.bak"; Copy-Item -LiteralPath $_ -Destination $bak; $bak })
    [void][IO.Directory]::CreateDirectory((Join-Path $other.old.root $attempt))
    [IO.File]::WriteAllText((Join-Path $other.old.root "$attempt\launcher-recovery.json"), ([ordered]@{ previousExe = 'C:\previous\SyncShark.exe'; paths = $paths; backups = $backups } | ConvertTo-Json))
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
    $build = New-StubBuild $ctx 'jcand' '0.3.76' $ctx.old.pv
    $before = Get-TreeStamp $outside
    $r = Deploy $ctx '' $build
    Expect ($r.Exit -ne 0 -and $r.Log -like '*passes through the reparse point*') 'refused'
    Expect (-not (Test-Path -LiteralPath (Join-Path $ctx.root 'replacement-staging')) -and (Get-TreeStamp $outside) -ceq $before) "nothing written inside or outside: $(Get-StampDiff $before $outside)"
}
F 42 'recover re-proves the stop: a host found running before compensation is needs-owner, nothing restored' {
    $ctx = Use (New-Context 42)
    Start-Host $ctx $ctx.old
    $r = Deploy $ctx 'phase-stopped'
    $launchers = Get-LauncherHashes $ctx
    Start-Host $ctx $ctx.old
    $r = Recover $ctx
    $j = Expect-NeedsOwner $ctx '*stop is not proven*'
    Expect ((Get-LauncherHashes $ctx) -ceq $launchers -and $null -eq $j.restore.decision -and -not (@($j.phases | ForEach-Object name) -contains 'start-pending')) 'no compensation step ran'
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
    Expect (-not (@($j.phases | ForEach-Object name) -contains 'start-pending') -and @($j.launchers | Where-Object { $_.restore }).Count -eq 0) 'nothing compensated'
    Expect (@(Get-Stubs $ctx '--core v2 --engine-host' | Where-Object ExecutablePath -ieq $ctx.new.exe).Count -eq 1) 'new host still running'
    $r = Recover $ctx
    $null = Expect-Closed $ctx 'healthy'
    Expect (Test-Path -LiteralPath (Join-Path $ctx.new.root 'deploy-log.json')) 'published by -Recover'
}
F 45 'a tampered module, or another build''s tools copy, is refused before any module code runs' {
    $ctx = Use (New-Context 45)
    $marker = Join-Path $ctx.root 'module-ran.txt'
    $build = New-StubBuild $ctx 'cand-tampered' '0.3.76' $ctx.old.pv
    [IO.File]::AppendAllText((Join-Path $build.out 'tools\SyncShark.Release.psm1'), "`n[IO.File]::WriteAllText('$marker', 'ran')`n")
    $r = Deploy $ctx '' $build
    Expect ($r.Exit -ne 0 -and $r.Log -like '*SyncShark.Release.psm1 differs*' -and -not (Test-Path -LiteralPath $marker)) 'tampered module refused before import'
    $r = Invoke-Tool $ctx @('-Build', $build.root, '-LauncherDir', $ctx.launchers) '' $ctx.new.script
    Expect ($r.Exit -ne 0 -and $r.Log -like "*own outputDirectory\tools*" -and -not (Test-Path -LiteralPath $marker)) 'another build''s tools copy refused'
}
F 46 'backup and restore keep file and directory attributes and creation and last-write times' {
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
    Edit-Data $ctx
    $r = Recover $ctx
    $null = Expect-Closed $ctx 'restored'
    Expect ((Stamp $file) -ceq $expected.file -and (Stamp $dir) -ceq $expected.dir -and (Stamp $ctx.data) -ceq (Stamp $j.backup.path)) 'restore keeps metadata'
    Expect ((Get-Item -LiteralPath $ctx.data -Force).CreationTimeUtc -eq [DateTime]::new(2005, 6, 7, 8, 9, 10, [DateTimeKind]::Utc)) 'restored data root keeps its creation time'
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
    Expect ($r.Exit -ne 0 -and $r.Log -like '*holds no engine\state.db*' -and $r.Log -like '*expected root: unavailable*' -and $r.Log -like '*Restore the pointer by hand*') 'corrupt journal: diagnosis reported, expected root unavailable'
    Expect ((Get-TreeStamp $ctx.data) -ceq $data -and (Get-TreeStamp (Get-DeployRoot $ctx)) -ceq $deploys -and -not (Test-Path -LiteralPath (Join-Path $ctx.root 'product-root.txt'))) 'corrupt journal: nothing changed'
}
$results | Sort-Object Fixture | Format-Table Fixture, Result, Ms, Name -AutoSize | Out-String -Width 220 | Write-Host
$failed = @($results | Where-Object Result -ne 'PASS')
foreach ($f in $failed) { Write-Host "FAIL $($f.Fixture): $($f.Detail)" }
Write-Host "Fixture roots are under $base; no production process or registry write was performed."
if ($failed.Count) { exit 1 }
