<#
.SYNOPSIS
    Live acceptance tests for Sync Shark core v2 (selected with SYNCSHARK_CORE=2).

.DESCRIPTION
    Drives the real SyncShark.exe CLI against a real Dropbox account using the core v2 flow:
        pairs create  ->  first-sync prepare (preview)  ->  pairs enable  ->  first-sync apply
    then mutates the local side with PowerShell and the remote side with Sync Shark's own
    Dropbox CLI (`--cli dropbox <verb> ... --json`), runs an incremental sync, and asserts the
    outcome on both sides: the local folder listing, `pairs show`, `first-sync prepare`, `run`,
    the Dropbox CLI's `ls`/`stat`/`get`/`revisions`, and the pair-specific `conflicts list`/
    `recovery list`.

    The v2 core is selected for every child process with the SYNCSHARK_CORE=2 setting, set only on
    each launch and never in this shell (see docs/design/layout-and-wiring-plan.md "Live-run
    prerequisites"); the explicit `--cli --core v2` switch selects the same core. A startup probe
    runs `status` and refuses to continue unless it reports the v2 mode, so a wrong setting cannot
    silently exercise the wrong core.

    Two shared helpers run one CLI command and parse the one-JSON-object envelope: `Invoke-Cli`
    for the core commands and `Invoke-DropboxCli` for the `dropbox` group. Both go through
    `Invoke-CliRaw`, the single place that enforces success: a valid envelope (`schemaVersion`,
    expected `command`, `ok` a real JSON boolean) and `ok:true`, or else an error code the caller
    names explicitly (`stat` -> missing_target; `run` -> review_required only for a call that
    passes -Expect <name> from $script:RunExpectedOutcomes; a changed-choices first-sync apply ->
    review_required, named at its one call site). Anything else fails the scenario.

    Every scenario folder (local and remote) carries a random suffix and must not already
    exist; the remote one is created with an exclusive `mkdir` (an existing folder fails, it is
    never adopted). -RemoteBase is never created or modified here; it must already exist.

    Every value (paths, counts, timeouts) is a parameter or a named variable at the top, and
    every command verb and option string lives in the one `$script:Cli` block (engineering
    standards section 2.2); the scenario bodies contain no magic numbers or command literals.

    NEVER deletes anything outside the scenario's own timestamped local and remote folders.

.NOTES
    Windows PowerShell 5.1 compatible. Writes only files; run it yourself when you mean to
    exercise the live account.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Exe,

    [string]$LocalBase = (Join-Path $env:LOCALAPPDATA 'SyncSharkTests\v2-live'),

    [string]$RemoteBase = '/Apps/Sync Shark/Testing/v2-live',

    [Parameter(Mandatory = $true)]
    [string]$Account,

    [string]$Scenario = 'all',

    [int]$TinyFileCount = 2000,
    [int]$TinyFileBytes = 16,
    [int]$TieWindowSeconds = 2,
    [int]$FirstSyncOlderSeconds = 120,

    [int]$CommandTimeoutSeconds = 120,
    [int]$LongTimeoutSeconds = 1800,

    [string]$ReportRoot = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
# Agent shells inherit HTTPS_PROXY pointing at a local LLM proxy; Sync Shark must reach Dropbox directly.
foreach ($proxyVar in 'HTTPS_PROXY','HTTP_PROXY','ALL_PROXY','https_proxy','http_proxy','all_proxy') {
    Remove-Item "env:$proxyVar" -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# Named values (no magic numbers in the scenario bodies)
# ---------------------------------------------------------------------------
$script:CoreEnvironmentVar    = 'SYNCSHARK_CORE'  # selects the core for every child process (CoreSwitch)
$script:CoreSetting           = '2'               # 2 selects the v2 core
$script:CoreModeV2            = 'direct-v2'       # `status` reports this mode only under the v2 core
$script:SchemaVersionWanted   = 1            # SyncCli.SchemaVersion in the envelope
$script:LocalBase             = $LocalBase
$script:RemoteBase            = $RemoteBase
$script:Account               = $Account
$script:ExePath               = $null
$script:ReportDir             = $null
$script:DefaultTimeoutMs      = $CommandTimeoutSeconds * 1000
$script:ApplyTimeoutMs        = $LongTimeoutSeconds * 1000
$script:RunTimeoutMs          = $LongTimeoutSeconds * 1000
$script:TinyFileCount         = $TinyFileCount
$script:TinyFileBytes         = $TinyFileBytes
$script:TieWindowSeconds      = $TieWindowSeconds
$script:FirstSyncOlderSeconds = $FirstSyncOlderSeconds
$script:FolderSuffixHex       = 8            # random hex characters added to each scenario folder name
$script:KeepBothMarker        = ' version '  # planner section 6 default label contains " version "
$script:KeepBothNoticeGroup   = 'KeepBoth'   # conflicts list reports a keep-both notice under this group
$script:RunEffectFields       = @('uploaded', 'downloaded', 'deleted', 'moved', 'conflicts', 'blocked')
$script:FailPrefix            = '__FAIL__ '
$script:MissingTargetCode     = 'missing_target'   # `dropbox stat` error code for an absent path
$script:ReviewRequiredCode    = 'review_required'  # review error code (a run with items to review; a bound-choices apply)
$script:ReviewExitCode        = 5                  # SyncCliExitCode.NeedsReview: the process exit code of a review error
$script:DigestHexLength       = 64                 # the choices digest is a SHA-256 hex string
$script:ChoicePrefixSeparator = '/'                # a --choices prefix names a folder with it; a reported effect trims it
# The two refusals a bound first-sync apply can give are BOTH review_required, so the code alone
# cannot tell them apart; these are the distinctive phrases of each message (V2CliRuntime digest
# mismatch; FirstSyncChoicesState.RefuseRemoveGrowth) that the check matches.
$script:ChoicesDifferReason   = 'differ from the last prepare'
$script:RemoveGrowthReason    = 'more than the last prepare reported'
# RQ-034: a missing local root is refused up front with review_required and this phrase of its
# message ("The local folder '<path>' is missing."); the phrase names the missing folder, not the path.
$script:LocalRootMissingReason = 'is missing.'

# ---------------------------------------------------------------------------
# CLI vocabulary (engineering standards section 2.2): every command word and
# option string lives here once; the helpers and scenarios refer to it by key,
# so a changed command string is a change in this one block.
# ---------------------------------------------------------------------------
$script:Cli = [ordered]@{
    # Switches and option names
    CliSwitch        = '--cli'
    Json             = '--json'
    Yes              = '--yes'
    Pair             = '--pair'
    Account          = '--account'
    Path             = '--path'
    Name             = '--name'
    Local            = '--local'
    Choices          = '--choices'
    AllowRecommended = '--allow-recommended'
    Mode             = '--mode'
    Overwrite        = 'overwrite'
    In               = '--in'
    Out              = '--out'
    To               = '--to'
    Cursor           = '--cursor'
    Recursive        = '--recursive'
    ClientModified   = '--client-modified'
    Side             = '--side'
    SideBoth         = 'both'
    # Core command words
    Status           = @('status')
    PairsCreate      = @('pairs', 'create')
    PairsEnable      = @('pairs', 'enable')
    PairsShow        = @('pairs', 'show')
    PairsRescan      = @('pairs', 'rescan')
    FirstSyncPrepare = @('first-sync', 'prepare')
    FirstSyncApply   = @('first-sync', 'apply')
    Run              = @('run')
    ConflictsList    = @('conflicts', 'list')
    RecoveryList     = @('recovery', 'list')
    # The shared Dropbox group (dispatched before the core operations surface)
    DropboxGroup     = 'dropbox'
    DropboxMkdir     = @('mkdir')
    DropboxPut       = @('put')
    DropboxRm        = @('rm')
    DropboxMv        = @('mv')
    DropboxStat      = @('stat')
    DropboxLs        = @('ls')
    DropboxGet       = @('get')
    DropboxRevisions = @('revisions')
}

# The ONLY expected non-success `run` outcomes, by name. A run call opts in with
# `Invoke-Incremental -Expect <name>`; the helper accepts it only inside the listed scenarios
# and only with the listed error code. Every other run, and every first sync, must succeed.
# `run` reports anything kept-both, held, failed or uncertain as review_required with the run
# counts as data (SyncCliRegistry `run`).
#   KeepBothReview   - a keep-both was staged on purpose (conflicts >= 1, nothing blocked/failed).
#   LocalRootMissing - RQ-034: a missing local root is refused up front (review_required, no data,
#                      "The local folder '<path>' is missing."). The helper accepts that refusal by
#                      its code and that message phrase; nothing runs, so no effect count is read.
$script:RunExpectedOutcomes  = @{
    'KeepBothReview'   = @{ Code = $script:ReviewRequiredCode
                            Scenarios = @('Rule27_EditBothSidesKeepBoth', 'Rule10_MoveVsEditKeepBoth') }
    'LocalRootMissing' = @{ Code = $script:ReviewRequiredCode
                            Scenarios = @('RQ034_LocalRootMissingNoDeletes') }
}
$script:CurrentScenario      = ''                 # set by the driver before each scenario

$script:ScenarioNotes = @()
$script:Results       = @()

# ---------------------------------------------------------------------------
# Result signalling (scenario bodies throw; the driver turns a throw into PASS/FAIL)
# ---------------------------------------------------------------------------
function Fail([string]$Message) {
    throw (New-Object System.Exception ($script:FailPrefix + $Message))
}

function Add-ScenarioNote([string]$Message) {
    $script:ScenarioNotes += $Message
}

# ---------------------------------------------------------------------------
# Assertions
# ---------------------------------------------------------------------------
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { Fail $Message }
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) {
        Fail ("$Message (expected '$Expected', got '$Actual')")
    }
}

# Assert a refused call failed for the EXACT named reason: the review error code AND a distinctive
# phrase of the message. Several refusals share the same code, so the code alone cannot prove which
# one fired; the phrase can.
function Assert-Refusal([object]$Result, [string]$Code, [string]$ReasonContains, [string]$What) {
    Assert-True (-not $Result.Ok) "$What should be refused"
    Assert-Equal $Code ([string]$Result.ErrorCode) "$What should be refused with '$Code'"
    Assert-True (([string]$Result.ErrorMessage).Contains($ReasonContains)) `
        "$What refusal should name the expected reason '$ReasonContains' (got: $($Result.ErrorMessage))"
}

# ---------------------------------------------------------------------------
# Local filesystem helpers (all mutations stay inside a scenario folder)
# ---------------------------------------------------------------------------
function Get-RelativeFiles([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root)) { return @() }
    $rootFull = (Resolve-Path -LiteralPath $Root).Path
    $items = Get-ChildItem -LiteralPath $Root -Recurse -File -Force
    $rel = foreach ($item in $items) {
        $item.FullName.Substring($rootFull.Length).TrimStart('\').Replace('\', '/')
    }
    return @($rel | Sort-Object)
}

function New-TextFile([string]$Path, [string]$Text) {
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Text)
}

# Write a --choices document to a unique file under the report folder and return its absolute path.
# `--choices` accepts an absolute JSON file, which avoids the Windows command-line quoting of an
# inline JSON argument that carries embedded double quotes; the file is UTF-8 without a BOM, a
# regular locally available file, exactly as the shared reader requires.
function New-ChoicesFile([string]$Json) {
    $path = Join-Path $script:ReportDir ('choices-' + [guid]::NewGuid().ToString('N') + '.json')
    New-TextFile $path $Json
    return $path
}

# A --choices prefix names a folder with its trailing separator ('gamma/'); a reported choice effect
# normalizes it away ('gamma'). Compare the two by trimming at most one trailing separator from each
# side, so the document's spelling and the effect's spelling are compared on equal terms.
function Get-ComparableChoicePrefix([string]$Prefix) {
    if ($Prefix.EndsWith($script:ChoicePrefixSeparator)) {
        return $Prefix.Substring(0, $Prefix.Length - $script:ChoicePrefixSeparator.Length)
    }
    return $Prefix
}

# ---------------------------------------------------------------------------
# The shared CLI/JSON helpers: one raw runner, two front doors
# ---------------------------------------------------------------------------
# A double quote cannot be escaped safely onto the Windows command line, so the startup
# guard rejects any parameter that contains one (see the startup section).
function ConvertTo-ArgString([string[]]$Values) {
    $parts = foreach ($value in $Values) {
        $s = if ($null -eq $value) { '' } else { [string]$value }
        if ($s -eq '') {
            '""'
        }
        elseif ($s -match '[\s"]') {
            $escaped = $s -replace '(\\+)"', '$1$1\"'
            $escaped = $escaped -replace '(\\+)$', '$1$1'
            '"' + $escaped + '"'
        }
        else {
            $s
        }
    }
    return ($parts -join ' ')
}

# The registry command string is the leading one or two words that are not options.
function Get-ExpectedCommand([string[]]$Words) {
    $leading = @()
    foreach ($word in $Words) {
        if ($word.StartsWith('-')) { break }
        $leading += $word
        if ($leading.Count -eq 2) { break }
    }
    return ($leading -join ' ')
}

# Runs one CLI invocation, parses the one-JSON-object envelope
# ({schemaVersion, command, ok, data, error{code,message}}) and ENFORCES it. This is the single
# place that decides success, so no caller can forget a check. It fails the scenario when:
#   - the command timed out;
#   - the envelope is missing or malformed (EnvelopeFault): wrong schemaVersion, wrong command,
#     `ok` not a real JSON boolean, or `ok:false` without an error code;
#   - the command did not succeed (exit 0 and `ok` exactly JSON true), unless its error code is
#     one the caller names in -AllowedErrorCodes (an explicitly expected, named outcome).
# The returned result therefore always has a valid envelope; `.Ok` says which of the two
# accepted outcomes (success, or a named allowed error) it is.
function Invoke-CliRaw {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string[]]$FullArgs,
        [Parameter(Mandatory = $true)][string]$ExpectedCommand,
        [int]$TimeoutMs = 0,
        [string[]]$AllowedErrorCodes = @()
    )
    $timeout = if ($TimeoutMs -gt 0) { $TimeoutMs } else { $script:DefaultTimeoutMs }
    $tag = 'cmd-' + (Get-Date -Format 'HHmmssfff') + '-' + (Get-Random -Maximum 999999)
    $outFile = Join-Path $script:ReportDir ($tag + '.out.json')
    $errFile = Join-Path $script:ReportDir ($tag + '.err.txt')

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # Select the core only for this one child. The setting is placed in this process's environment
    # just for the launch (a child copies the environment block at creation) and restored in a
    # finally, so a startup failure cannot leave it set. It is set only while launching this child.
    $previousCore = [System.Environment]::GetEnvironmentVariable($script:CoreEnvironmentVar)
    [System.Environment]::SetEnvironmentVariable($script:CoreEnvironmentVar, $script:CoreSetting)
    try {
        $process = Start-Process -FilePath $script:ExePath -ArgumentList (ConvertTo-ArgString $FullArgs) `
            -PassThru -NoNewWindow -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        # Retain the native handle before a quick child exits (Windows PowerShell 5.1).
        # Otherwise Process.ExitCode can be null even after WaitForExit reports success.
        $null = $process.Handle
    }
    finally {
        [System.Environment]::SetEnvironmentVariable($script:CoreEnvironmentVar, $previousCore)
    }
    $exited = $process.WaitForExit($timeout)
    $timedOut = -not $exited
    if ($timedOut) {
        try { $process.Kill() } catch { }
        $process.WaitForExit()
    }
    # Complete asynchronous redirected-output handling before reading the exit code/files.
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $sw.Stop()

    $stdout = ''
    if (Test-Path -LiteralPath $outFile) { $stdout = Get-Content -LiteralPath $outFile -Raw }
    $stderr = ''
    if (Test-Path -LiteralPath $errFile) { $stderr = Get-Content -LiteralPath $errFile -Raw }

    $json = $null
    if ($stdout -and $stdout.Trim().Length -gt 0) {
        try { $json = $stdout | ConvertFrom-Json } catch { $json = $null }
    }

    $errorObject = $null
    if ($json -and $json.PSObject.Properties['error'] -and $null -ne $json.error) { $errorObject = $json.error }
    $errorCode = if ($errorObject) { $errorObject.code } else { $null }
    $errorMessage = if ($errorObject) { $errorObject.message } else { $null }

    $command = $null
    $schemaVersion = $null
    $okValue = $null
    $envelopeFault = $null
    if ($json) {
        if ($json.PSObject.Properties['command']) { $command = [string]$json.command }
        if ($json.PSObject.Properties['schemaVersion']) { $schemaVersion = $json.schemaVersion }
        if ($json.PSObject.Properties['ok']) { $okValue = $json.ok }
        # ConvertFrom-Json maps JSON true/false to [bool] and a JSON number to [int]/[long];
        # anything else (a string "true", "1", null) is not accepted, never coerced.
        if (-not (($schemaVersion -is [int]) -or ($schemaVersion -is [long])) -or
            $schemaVersion -ne $script:SchemaVersionWanted) {
            $envelopeFault = "the envelope schemaVersion '$schemaVersion' is not the JSON number $($script:SchemaVersionWanted)"
        }
        elseif ($command -ne $ExpectedCommand) {
            $envelopeFault = "the envelope command '$command' is not '$ExpectedCommand'"
        }
        elseif (-not ($okValue -is [bool])) {
            $envelopeFault = "the envelope 'ok' is not a JSON boolean (got '$okValue')"
        }
        elseif (-not $okValue -and -not $errorCode) {
            $envelopeFault = "the envelope says ok:false but carries no error code"
        }
    }
    else {
        $envelopeFault = 'no JSON envelope was written to stdout'
    }

    $succeeded = ($null -eq $envelopeFault) -and ($exitCode -eq 0) -and ($okValue -eq $true)
    $argsText = ($FullArgs -join ' ')
    $seconds = [math]::Round($sw.Elapsed.TotalSeconds, 3)

    if ($timedOut) { Fail "'$argsText' timed out after ${seconds}s (report: $outFile)" }
    if ($envelopeFault) { Fail "'$argsText': $envelopeFault (exit $exitCode; report: $outFile)" }
    if (-not $succeeded -and -not ($AllowedErrorCodes -contains $errorCode)) {
        $why = if ($errorCode) { "${errorCode}: $errorMessage" } elseif ($stderr) { $stderr.Trim() } else { 'no error text' }
        Fail "'$argsText' failed (exit $exitCode, ok $okValue): $why"
    }

    return [pscustomobject]@{
        Args          = $FullArgs
        ArgsText      = $argsText
        ExitCode      = $exitCode
        TimedOut      = $timedOut
        Ok            = $succeeded
        Json          = $json
        Data          = if ($json -and $json.PSObject.Properties['data']) { $json.data } else { $null }
        Command       = $command
        SchemaVersion = $schemaVersion
        Error         = $errorObject
        ErrorCode     = $errorCode
        ErrorMessage  = $errorMessage
        StdOut        = $stdout
        StdErr        = $stderr
        Seconds       = $seconds
        OutFile       = $outFile
        ErrFile       = $errFile
    }
}

# The core surface: --cli <command> ... --json. The v2 core is selected by SYNCSHARK_CORE in the
# child's environment, set for the child's launch only (see Invoke-CliRaw); the explicit `--core v2`
# switch is equivalent and is not also passed here.
function Invoke-Cli {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string[]]$CliArgs,
        [int]$TimeoutMs = 0,
        [string[]]$AllowedErrorCodes = @()
    )
    $full = @($script:Cli.CliSwitch) + $CliArgs + @($script:Cli.Json)
    return Invoke-CliRaw -FullArgs $full -TimeoutMs $TimeoutMs -AllowedErrorCodes $AllowedErrorCodes `
        -ExpectedCommand (Get-ExpectedCommand $CliArgs)
}

# Sync Shark's own Dropbox CLI: --cli dropbox <verb> ... --json
# The `dropbox` group lives in the shared registry (it is not core-specific) and is dispatched
# before the core surface, so it is invoked without the --core switch; the envelope and parsing
# are identical to Invoke-Cli.
function Invoke-DropboxCli {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string[]]$DropboxArgs,
        [int]$TimeoutMs = 0,
        [string[]]$AllowedErrorCodes = @()
    )
    $full = @($script:Cli.CliSwitch, $script:Cli.DropboxGroup) + $DropboxArgs + @($script:Cli.Json)
    return Invoke-CliRaw -FullArgs $full -TimeoutMs $TimeoutMs -AllowedErrorCodes $AllowedErrorCodes `
        -ExpectedCommand ($script:Cli.DropboxGroup + ' ' + (Get-ExpectedCommand $DropboxArgs))
}

# A Dropbox result that does not fit the reply envelope is spilled to a file and `data`
# becomes {spilled, resultFile, bytes}; read the real data back when that happens.
function Resolve-DropboxData([object]$Result) {
    $data = $Result.Data
    if ($null -eq $data) { return $null }
    if ($data.PSObject.Properties['spilled'] -and [bool]$data.spilled) {
        $file = [string]$data.resultFile
        if (-not (Test-Path -LiteralPath $file)) {
            Fail "a Dropbox result was spilled to '$file' but that file is missing"
        }
        return (Get-Content -LiteralPath $file -Raw | ConvertFrom-Json)
    }
    return $data
}

# ---------------------------------------------------------------------------
# Remote helpers: every call is Sync Shark's Dropbox CLI and stays inside the
# scenario's own timestamped remote folder under -RemoteBase.
# ---------------------------------------------------------------------------
function Join-RemotePath([string]$Folder, [string]$Name) {
    return $Folder.TrimEnd('/') + '/' + $Name
}

# `mkdir` for one scenario folder, exclusively: it must create a NEW folder. An existing folder
# (error `conflict`) or any other error fails the scenario; an existing folder is never adopted.
# Only scenario folders are created here; -RemoteBase must already exist and is never created.
function New-RemoteFolder([string]$RemotePath) {
    $mkdirArgs = @($script:Cli.DropboxMkdir) +
        @($script:Cli.Account, $script:Account, $script:Cli.Path, $RemotePath, $script:Cli.Yes)
    Invoke-DropboxCli -DropboxArgs $mkdirArgs | Out-Null
}

# `put` a small text file; overwrite so it works for both creating and editing. An optional
# --client-modified pins the Dropbox-side modified time (used to force the rule-4 branch).
function New-RemoteFile([string]$RemoteFolder, [string]$Name, [string]$Text, [string]$ClientModified = '') {
    $safeName = $Name -replace '[\\/:*?"<>|]', '_'
    $tmp = Join-Path $script:ReportDir ('remote-' + [guid]::NewGuid().ToString('N') + '-' + $safeName)
    New-TextFile $tmp $Text
    $dest = Join-RemotePath $RemoteFolder $Name
    $putArgs = @($script:Cli.DropboxPut) +
        @($script:Cli.Account, $script:Account, $script:Cli.In, $tmp, $script:Cli.Path, $dest,
          $script:Cli.Mode, $script:Cli.Overwrite)
    if ($ClientModified) { $putArgs += @($script:Cli.ClientModified, $ClientModified) }
    $putArgs += $script:Cli.Yes
    Invoke-DropboxCli -DropboxArgs $putArgs -TimeoutMs $script:ApplyTimeoutMs | Out-Null
}

function Remove-RemotePath([string]$RemotePath) {
    $rmArgs = @($script:Cli.DropboxRm) +
        @($script:Cli.Account, $script:Account, $script:Cli.Path, $RemotePath, $script:Cli.Yes)
    Invoke-DropboxCli -DropboxArgs $rmArgs -TimeoutMs $script:ApplyTimeoutMs | Out-Null
}

function Move-RemotePath([string]$From, [string]$To) {
    $mvArgs = @($script:Cli.DropboxMv) +
        @($script:Cli.Account, $script:Account, $script:Cli.Path, $From, $script:Cli.To, $To, $script:Cli.Yes)
    Invoke-DropboxCli -DropboxArgs $mvArgs -TimeoutMs $script:ApplyTimeoutMs | Out-Null
}

# `stat` returns error code missing_target for an absent path; anything else is a failure.
function Test-RemotePathExists([string]$RemotePath) {
    $statArgs = @($script:Cli.DropboxStat) +
        @($script:Cli.Account, $script:Account, $script:Cli.Path, $RemotePath)
    $result = Invoke-DropboxCli -DropboxArgs $statArgs -AllowedErrorCodes @($script:MissingTargetCode)
    return [bool]$result.Ok
}

# The metadata of one existing remote path (its `rev` is the revision proof).
function Get-RemoteEntry([string]$RemotePath) {
    $statArgs = @($script:Cli.DropboxStat) +
        @($script:Cli.Account, $script:Account, $script:Cli.Path, $RemotePath)
    $result = Invoke-DropboxCli -DropboxArgs $statArgs
    return $result.Data
}

# One page of a file's revision history (newest first), read through the spill resolver.
function Get-RemoteRevisions([string]$RemotePath) {
    $revArgs = @($script:Cli.DropboxRevisions) +
        @($script:Cli.Account, $script:Account, $script:Cli.Path, $RemotePath)
    $result = Invoke-DropboxCli -DropboxArgs $revArgs
    $data = Resolve-DropboxData $result
    return @($data.entries)
}

# One or more `ls` pages (following the cursor), each read through the spill resolver.
function Get-RemoteEntries {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RemotePath,
        [switch]$Recursive
    )
    $lsArgs = @($script:Cli.DropboxLs) +
        @($script:Cli.Account, $script:Account, $script:Cli.Path, $RemotePath)
    if ($Recursive) { $lsArgs += $script:Cli.Recursive }

    $result = Invoke-DropboxCli -DropboxArgs $lsArgs
    $data = Resolve-DropboxData $result
    $all = @($data.entries)
    $cursor = $data.cursor
    $hasMore = [bool]$data.hasMore
    while ($hasMore) {
        $pageArgs = @($script:Cli.DropboxLs) +
            @($script:Cli.Account, $script:Account, $script:Cli.Cursor, $cursor)
        $page = Invoke-DropboxCli -DropboxArgs $pageArgs
        $data = Resolve-DropboxData $page
        $all += @($data.entries)
        $cursor = $data.cursor
        $hasMore = [bool]$data.hasMore
    }
    return @($all)
}

# Every FILE under a folder, as paths relative to it (folder separators are '/'). A recursive
# listing also returns the folder itself and any subfolders; those are not files, so they are
# dropped and the result is exactly the files the folder contains.
function Get-RemoteRelativeNames([string]$RemoteFolder) {
    $entries = Get-RemoteEntries -RemotePath $RemoteFolder -Recursive
    $prefix = $RemoteFolder.TrimEnd('/') + '/'
    $names = foreach ($entry in $entries) {
        if ([string]$entry.tag -ne 'file') { continue }
        $path = [string]$entry.pathDisplay
        if ($path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            $path.Substring($prefix.Length)
        }
    }
    return @($names | Sort-Object)
}

# Download a remote file into the report folder and return its text (content proof).
function Get-RemoteFileText([string]$RemotePath) {
    $tmp = Join-Path $script:ReportDir ('get-' + [guid]::NewGuid().ToString('N') + '.bin')
    $getArgs = @($script:Cli.DropboxGet) +
        @($script:Cli.Account, $script:Account, $script:Cli.Path, $RemotePath, $script:Cli.Out, $tmp)
    Invoke-DropboxCli -DropboxArgs $getArgs -TimeoutMs $script:ApplyTimeoutMs | Out-Null
    return (Get-Content -LiteralPath $tmp -Raw)
}

# ---------------------------------------------------------------------------
# Pair-specific catalog helpers (core v2 reads the pair's own rows)
# ---------------------------------------------------------------------------
function Get-PairConflicts([string]$PairId) {
    $result = Invoke-Cli -CliArgs (@($script:Cli.ConflictsList) + @($script:Cli.Pair, $PairId))
    return @($result.Data.items)
}

function Get-PairRecoveries([string]$PairId) {
    $result = Invoke-Cli -CliArgs (@($script:Cli.RecoveryList) + @($script:Cli.Pair, $PairId))
    return @($result.Data.items)
}

function Get-PairRecoveryPaths([string]$PairId) {
    return @(Get-PairRecoveries $PairId | ForEach-Object { [string]$_.relativePath })
}

# ---------------------------------------------------------------------------
# Pair lifecycle helpers
# ---------------------------------------------------------------------------
function New-Layout([string]$Name) {
    $suffix = [guid]::NewGuid().ToString('N').Substring(0, $script:FolderSuffixHex)
    $stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $suffix + '-' + $Name
    $local = Join-Path $script:LocalBase $stamp
    $remote = $script:RemoteBase.TrimEnd('/') + '/' + $stamp

    if (Test-Path -LiteralPath $local) { Fail "the local scenario folder '$local' already exists; refusing to reuse it" }
    if (-not (Test-RemotePathExists $script:RemoteBase)) {
        Fail "the remote base '$($script:RemoteBase)' does not exist on Dropbox for account '$($script:Account)'. These tests never create it; create it once before running."
    }
    if (Test-RemotePathExists $remote) { Fail "the remote scenario folder '$remote' already exists; refusing to reuse it" }

    # No -Force: refuse to reuse an existing folder rather than adopting its contents.
    New-Item -ItemType Directory -Path $local | Out-Null
    New-RemoteFolder $remote
    return [pscustomobject]@{
        Name         = $Name
        Stamp        = $stamp
        Local        = $local
        Remote       = $remote
        PairId       = $null
        PlannedItems = 0
        SetupSeconds = 0.0
        ApplySeconds = 0.0
        ApplyData    = $null
        RunSeconds   = 0.0
    }
}

# `first-sync prepare` is the reviewed first-sync preview (first-sync-choices.md, decision 54): it
# refreshes both inventories, honours optional per-folder --choices, and records the digest the
# apply is bound to. It does not need the pair enabled and it makes no file effect.
function Invoke-FirstSyncPrepare([object]$Layout, [string]$Choices = '') {
    $prepareArgs = @($script:Cli.FirstSyncPrepare) +
        @($script:Cli.Pair, $Layout.PairId, $script:Cli.Yes)
    if ($Choices) { $prepareArgs += @($script:Cli.Choices, $Choices) }
    return (Invoke-Cli -CliArgs $prepareArgs -TimeoutMs $script:ApplyTimeoutMs).Data
}

function Get-PairState([string]$PairId) {
    return (Invoke-Cli -CliArgs (@($script:Cli.PairsShow) + @($script:Cli.Pair, $PairId))).Data
}

# Fail closed unless the created pair's retained roots are exactly the layout's: the account, the
# local root and the remote root must all match before the pair is enabled or applied. `pairs show`
# returns the account as connectionId and the two roots as localRoot and dropboxRoot.
function Assert-PairRoots([object]$Layout) {
    $state = Get-PairState -PairId $Layout.PairId
    Assert-Equal $script:Account ([string]$state.connectionId) 'pair roots: the pair account should be the requested one'
    Assert-Equal ([string]$Layout.Local) ([string]$state.localRoot) 'pair roots: the pair local root should be the layout local root'
    Assert-Equal ([string]$Layout.Remote) ([string]$state.dropboxRoot) 'pair roots: the pair remote root should be the layout remote root'
}

# `pairs create` (disabled); records the new pair id on the layout and checks its retained roots.
function New-Pair([object]$Layout) {
    $createArgs = @($script:Cli.PairsCreate) +
        @($script:Cli.Account, $script:Account, $script:Cli.Path, $Layout.Remote,
          $script:Cli.Local, $Layout.Local, $script:Cli.Name, $Layout.Name, $script:Cli.Yes)
    $create = Invoke-Cli -CliArgs $createArgs
    $Layout.PairId = $create.Data.id
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$Layout.PairId)) 'pairs create returned no pair id'
    Assert-PairRoots $Layout
}

function Enable-Pair([object]$Layout) {
    Invoke-Cli -CliArgs (@($script:Cli.PairsEnable) + @($script:Cli.Pair, $Layout.PairId, $script:Cli.Yes)) | Out-Null
}

# The single first-sync apply path. With -Choices it passes exactly the reviewed --choices; without
# it, --allow-recommended approves the safe transfers (an empty choices document). The envelope must
# succeed (Invoke-CliRaw allows no error code here) and the first sync must report complete with
# nothing failed, held or uncertain.
function Invoke-FirstSyncApply([object]$Layout, [string]$Choices = '') {
    $applyArgs = @($script:Cli.FirstSyncApply) + @($script:Cli.Pair, $Layout.PairId)
    if ($Choices) { $applyArgs += @($script:Cli.Choices, $Choices) } else { $applyArgs += $script:Cli.AllowRecommended }
    $applyArgs += $script:Cli.Yes

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $apply = Invoke-Cli -CliArgs $applyArgs -TimeoutMs $script:ApplyTimeoutMs
    $sw.Stop()
    $Layout.ApplySeconds = [math]::Round($sw.Elapsed.TotalSeconds, 3)
    $Layout.ApplyData = $apply.Data

    $data = $apply.Data
    Assert-True ($null -ne $data) 'first-sync apply succeeded but returned no data'
    $counts = "applied=$($data.appliedItems) blocked=$($data.blocked) failed=$($data.failed)"
    Assert-True ($data.firstSyncComplete -eq $true) "first sync did not complete: $counts"
    Assert-True (-not ($data.stopped -eq $true)) "first-sync apply was stopped: $counts"
    Assert-Equal 0 ([int]$data.failed) 'first-sync apply reported failed items'
    Assert-Equal 0 ([int]$data.needsVerification) 'first-sync apply reported items needing verification'
    return $data
}

# Apply a choices document that is expected to be refused with a review error (exit 5); returns the
# result so the caller asserts the exact code and reason with Assert-Refusal. Invoke-CliRaw accepts
# only the one named review code here; every other outcome still fails the scenario.
function Invoke-FirstSyncApplyRefusal([object]$Layout, [string]$Choices) {
    $applyArgs = @($script:Cli.FirstSyncApply) +
        @($script:Cli.Pair, $Layout.PairId, $script:Cli.Choices, $Choices, $script:Cli.Yes)
    return Invoke-Cli -CliArgs $applyArgs -TimeoutMs $script:ApplyTimeoutMs `
        -AllowedErrorCodes @($script:ReviewRequiredCode)
}

# `pairs rescan` forgets the pair's stored scan cursors so its next run re-reads the chosen roots in full;
# `--side both` (the default) targets both roots. It changes no file and no baseline. The result names the
# roots the next run will rescan.
function Invoke-Rescan([object]$Layout) {
    $rescanArgs = @($script:Cli.PairsRescan) +
        @($script:Cli.Pair, $Layout.PairId, $script:Cli.Side, $script:Cli.SideBoth, $script:Cli.Yes)
    $result = Invoke-Cli -CliArgs $rescanArgs -TimeoutMs $script:DefaultTimeoutMs
    $roots = @($result.Data.roots)
    Assert-Equal 2 $roots.Count 'pairs rescan should report both pair roots'
    return $result
}

# create (disabled) -> first-sync prepare (preview) -> enable -> first-sync apply, then verify
# review is closed. The prepare records the (empty) reviewed choices the apply is bound to.
function Initialize-Pair([object]$Layout) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    New-Pair $Layout
    $prepare = Invoke-FirstSyncPrepare $Layout
    Assert-True ($prepare.firstSyncApproved -eq $false) 'a fresh pair should still require first-sync review'
    $Layout.PlannedItems = [int]$prepare.totalItems

    Enable-Pair $Layout
    Invoke-FirstSyncApply $Layout | Out-Null

    $state = Get-PairState -PairId $Layout.PairId
    Assert-True ($state.enabled -eq $true) 'pair should be enabled after the first sync'
    Assert-True ($state.firstSyncReviewRequired -eq $false) 'first-sync review should be closed after apply'

    $sw.Stop()
    $Layout.SetupSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 3)
    return $Layout
}

# The single incremental-run path. Invoke-CliRaw enforces the envelope and no timeout. Without
# -Expect a run must succeed (ok:true) with nothing failed or uncertain. With -Expect <name>
# (a key of $script:RunExpectedOutcomes, valid only inside that entry's scenarios) the one
# named error code is also accepted, and only in that outcome's exact shape. Always: not stopped,
# nothing needing verification, no failed items; data present except for the LocalRootMissing
# refusal, which is a whole-call refusal (a missing root is refused before any report exists).
function Invoke-Incremental([object]$Layout, [string]$What, [int]$TimeoutMs = 0, [string]$Expect = '') {
    $timeout = if ($TimeoutMs -gt 0) { $TimeoutMs } else { $script:RunTimeoutMs }
    $allowed = @()
    if ($Expect) {
        if (-not $script:RunExpectedOutcomes.ContainsKey($Expect)) { Fail "$What names an unknown run outcome '$Expect'" }
        $outcome = $script:RunExpectedOutcomes[$Expect]
        if ($outcome.Scenarios -notcontains $script:CurrentScenario) {
            Fail "$What expects '$Expect', which is not allowed in scenario '$($script:CurrentScenario)'"
        }
        $allowed = @($outcome.Code)
    }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $runArgs = @($script:Cli.Run) + @($script:Cli.Pair, $Layout.PairId, $script:Cli.Yes)
    $run = Invoke-Cli -CliArgs $runArgs -TimeoutMs $timeout -AllowedErrorCodes $allowed
    $sw.Stop()
    $Layout.RunSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 3)

    $data = $run.Data

    # RQ-034: a missing local root is refused up front, so this one outcome accepts a whole-call
    # refusal instead of a run report: the named review code plus the stable phrase of its message
    # ("The local folder '<path>' is missing."). Nothing ran, so there is no data to shape-check;
    # the caller proves no remote file was touched (revision and content unchanged).
    if ($Expect -eq 'LocalRootMissing' -and -not $run.Ok) {
        Assert-Equal $script:ReviewRequiredCode ([string]$run.ErrorCode) `
            "$What should be refused with '$($script:ReviewRequiredCode)'"
        Assert-True (([string]$run.ErrorMessage).Contains($script:LocalRootMissingReason)) `
            "$What refusal should name the missing local folder (got: $($run.ErrorMessage))"
        return $run
    }

    Assert-True ($null -ne $data) "$What run returned no data ($($run.ErrorCode): $($run.ErrorMessage))"
    Assert-True (-not ($data.stopped -eq $true)) "$What run was stopped"
    Assert-Equal 0 ([int]$data.needsVerification) "$What run reported items needing verification"
    Assert-Equal 0 ([int]$data.failed) "$What run reported failed items"
    if (-not $run.Ok) {
        # Reached only for the named code of -Expect (Invoke-CliRaw fails every other non-success).
        switch ($Expect) {
            'KeepBothReview' {
                Assert-True ([int]$data.conflicts -ge 1) "$What run ended $($run.ErrorCode) without a keep-both: $($run.ErrorMessage)"
                Assert-Equal 0 ([int]$data.blocked) "$What run ended $($run.ErrorCode) with blocked items"
            }
            default { Fail "$What has no shape check for run outcome '$Expect'" }
        }
    }
    return $run
}

# No effect of any kind: uploaded, downloaded, deleted, moved, kept-both (conflicts) and blocked all zero.
function Assert-NoEffects([object]$Run, [string]$What) {
    foreach ($field in $script:RunEffectFields) {
        Assert-Equal 0 ([int]$Run.Data.$field) "$What should report no '$field'"
    }
}

# A second pass must find nothing to do at all.
function Assert-Converged([object]$Layout, [string]$What) {
    $run = Invoke-Incremental -Layout $Layout -What "$What (convergence pass)"
    Assert-NoEffects -Run $run -What "$What convergence pass"
}

# ---------------------------------------------------------------------------
# Scenarios (one function per planner rule group)
# ---------------------------------------------------------------------------
function Invoke-ScenarioRule04_05 {
    # First sync, same path, different content, one side clearly older (rule 4): the newer version
    # wins and the older one is retired to Dropbox history and to the pair's Recovery. The remote
    # copy's time is pinned with `dropbox put --client-modified` so the 2 s tie (rule 5) never
    # applies here; the tie itself is proven by the planner unit tests (owner decision 33).
    $layout = New-Layout 'rule04-05-newer-wins'
    New-TextFile (Join-Path $layout.Local 'newer.txt') 'local-newer'
    $olderUtc = (Get-Date).ToUniversalTime().AddSeconds(-$script:FirstSyncOlderSeconds).ToString('yyyy-MM-ddTHH:mm:ssZ')

    New-Pair $layout

    New-RemoteFile $layout.Remote 'newer.txt' 'remote-older' -ClientModified $olderUtc
    Assert-True (Test-RemotePathExists (Join-RemotePath $layout.Remote 'newer.txt')) 'rule04_05: the older remote copy should exist after put'

    $prepare = Invoke-FirstSyncPrepare $layout
    Assert-True ([int]$prepare.conflictItems -ge 1) 'rule04_05: a first-sync clash on the same path should be detected'

    Enable-Pair $layout
    Invoke-FirstSyncApply $layout | Out-Null

    # Newer wins on both sides.
    Assert-Equal 'local-newer' (Get-Content -LiteralPath (Join-Path $layout.Local 'newer.txt') -Raw) 'rule04_05: the newer local content should stand'
    Assert-Equal 'local-newer' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'newer.txt')) 'rule04_05: the newer content should win on Dropbox'
    # The replaced (older) version survives only in Dropbox history and in the pair's Recovery.
    Assert-True (@(Get-RemoteRevisions (Join-RemotePath $layout.Remote 'newer.txt')).Count -ge 2) 'rule04_05: the replaced version should remain in Dropbox history'
    Assert-True ((Get-PairRecoveryPaths $layout.PairId) -contains 'newer.txt') 'rule04_05: the replaced version should be retained in Recovery'
    Add-ScenarioNote 'the 2 s tie (rule 5) is proven by the planner unit tests; here put --client-modified forces the rule-4 newer-wins branch (owner decision 33)'
}

function Invoke-ScenarioRule02 {
    # One-sided edit propagates each direction (rule 2).
    $layout = New-Layout 'rule02-onesided-edit'
    New-TextFile (Join-Path $layout.Local 'edit-local.txt') 'v1'
    New-TextFile (Join-Path $layout.Local 'edit-remote.txt') 'v1'
    Initialize-Pair $layout | Out-Null

    # local -> remote
    New-TextFile (Join-Path $layout.Local 'edit-local.txt') 'v2'
    $run = Invoke-Incremental $layout 'rule02 local edit'
    Assert-True ([int]$run.Data.uploaded -ge 1) 'rule02 local edit should upload the changed file'
    Assert-Equal 'v2' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'edit-local.txt')) 'rule02: the local edit should be on Dropbox'
    Assert-Converged -Layout $layout -What 'rule02 local edit'
    Assert-Equal 'v2' (Get-Content -LiteralPath (Join-Path $layout.Local 'edit-local.txt') -Raw) 'rule02 local edit should stay on disk'

    # remote -> local
    New-RemoteFile $layout.Remote 'edit-remote.txt' 'v2'
    $run2 = Invoke-Incremental $layout 'rule02 remote edit'
    Assert-Equal 'v2' (Get-Content -LiteralPath (Join-Path $layout.Local 'edit-remote.txt') -Raw) 'rule02 remote edit should download to local'
    Assert-Converged -Layout $layout -What 'rule02 remote edit'
}

function Invoke-ScenarioRule27 {
    # Same path edited differently on both sides, ongoing: keep both with a labelled copy (rule 27).
    # Each side keeps its own version at the path and parks the other side's version beside it.
    $layout = New-Layout 'rule27-edit-both'
    New-TextFile (Join-Path $layout.Local 'report.txt') 'base'
    Initialize-Pair $layout | Out-Null

    New-TextFile (Join-Path $layout.Local 'report.txt') 'local version'
    New-RemoteFile $layout.Remote 'report.txt' 'remote version'

    $run = Invoke-Incremental $layout 'rule27 keep both' -Expect 'KeepBothReview'

    # Original + labelled copy on BOTH sides, each with the right content.
    $localNames = Get-RelativeFiles $layout.Local
    $localLabelled = @($localNames | Where-Object { $_ -like "*$($script:KeepBothMarker)*" })
    Assert-Equal 1 $localLabelled.Count 'rule27: exactly one labelled copy should exist locally'
    Assert-Equal 'local version' (Get-Content -LiteralPath (Join-Path $layout.Local 'report.txt') -Raw) 'rule27: the local version should stand locally'
    Assert-Equal 'remote version' (Get-Content -LiteralPath (Join-Path $layout.Local $localLabelled[0]) -Raw) 'rule27: the labelled copy should hold the other side''s version'

    $remoteNames = Get-RemoteRelativeNames $layout.Remote
    $remoteLabelled = @($remoteNames | Where-Object { $_ -like "*$($script:KeepBothMarker)*" })
    Assert-Equal 1 $remoteLabelled.Count 'rule27: exactly one labelled copy should exist on Dropbox'
    Assert-Equal 'remote version' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'report.txt')) 'rule27: the remote version should stand on Dropbox'
    Assert-Equal 'local version' (Get-RemoteFileText (Join-RemotePath $layout.Remote $remoteLabelled[0])) 'rule27: the Dropbox labelled copy should hold the other side''s version'

    # A pair-specific keep-both notice, not a global count.
    $keepBoth = @(Get-PairConflicts $layout.PairId | Where-Object {
        $_.relativePath -eq 'report.txt' -and $_.group -eq $script:KeepBothNoticeGroup })
    Assert-True ($keepBoth.Count -ge 1) 'rule27: conflicts list should report the pair-specific keep-both notice'
}

function Invoke-ScenarioRule07 {
    # Edit on one side, delete on the other: the edit survives (rule 7). The delete is staged on
    # the remote side with `dropbox rm`, the edit on the local side.
    $layout = New-Layout 'rule07-edit-vs-delete'
    New-TextFile (Join-Path $layout.Local 'edel.txt') 'base'
    Initialize-Pair $layout | Out-Null

    New-TextFile (Join-Path $layout.Local 'edel.txt') 'local-edit'
    Remove-RemotePath (Join-RemotePath $layout.Remote 'edel.txt')
    Assert-True (-not (Test-RemotePathExists (Join-RemotePath $layout.Remote 'edel.txt'))) 'rule07: the remote copy should be deleted before the run'

    $run = Invoke-Incremental $layout 'rule07 edit vs delete'

    Assert-True ((Get-RelativeFiles $layout.Local) -contains 'edel.txt') 'rule07: the edited file should survive locally'
    Assert-Equal 'local-edit' (Get-Content -LiteralPath (Join-Path $layout.Local 'edel.txt') -Raw) 'rule07: the local edit should survive'
    Assert-True (Test-RemotePathExists (Join-RemotePath $layout.Remote 'edel.txt')) 'rule07: the edit should be restored on Dropbox'
    Assert-Equal 'local-edit' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'edel.txt')) 'rule07: Dropbox should carry the surviving edit'
    Assert-Converged -Layout $layout -What 'rule07 edit vs delete'
}

function Invoke-ScenarioRule09 {
    # Local move propagates as a move (rule 9) and is visible remotely at the new path.
    $layout = New-Layout 'rule09-local-move'
    New-TextFile (Join-Path $layout.Local 'move-me.txt') 'move-content'
    Initialize-Pair $layout | Out-Null

    Move-Item -LiteralPath (Join-Path $layout.Local 'move-me.txt') -Destination (Join-Path $layout.Local 'moved.txt')
    $run = Invoke-Incremental $layout 'rule09 local move'

    $files = Get-RelativeFiles $layout.Local
    Assert-True ($files -contains 'moved.txt') 'rule09: the moved name should exist locally'
    Assert-True (-not ($files -contains 'move-me.txt')) 'rule09: the old name should be gone locally'

    Assert-True (Test-RemotePathExists (Join-RemotePath $layout.Remote 'moved.txt')) 'rule09: the new name should exist on Dropbox'
    Assert-True (-not (Test-RemotePathExists (Join-RemotePath $layout.Remote 'move-me.txt'))) 'rule09: the old name should be gone from Dropbox'
    Assert-Equal 'move-content' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'moved.txt')) 'rule09: the moved content should be on Dropbox'

    Assert-Converged -Layout $layout -What 'rule09 local move'

    $moveNotices = @(Get-PairConflicts $layout.PairId | Where-Object {
        $_.relativePath -eq 'moved.txt' -or $_.relativePath -eq 'move-me.txt' })
    Assert-Equal 0 $moveNotices.Count 'rule09: a plain move should not raise a pair notice for its paths'
}

function Invoke-ScenarioRule10 {
    # Move on one side, edit on the other, different paths: keep both (rule 10). The move is
    # staged on the remote side with `dropbox mv`, the edit on the local side.
    $layout = New-Layout 'rule10-move-vs-edit'
    New-TextFile (Join-Path $layout.Local 'doc.txt') 'base'
    Initialize-Pair $layout | Out-Null

    Move-RemotePath (Join-RemotePath $layout.Remote 'doc.txt') (Join-RemotePath $layout.Remote 'doc-moved.txt')
    New-TextFile (Join-Path $layout.Local 'doc.txt') 'local edit'

    $run = Invoke-Incremental $layout 'rule10 move vs edit' -Expect 'KeepBothReview'

    $files = Get-RelativeFiles $layout.Local
    Assert-True ($files -contains 'doc.txt') 'rule10: the edited name should stay locally'
    Assert-True ($files -contains 'doc-moved.txt') "rule10: the other side's moved name should arrive locally"
    Assert-Equal 'local edit' (Get-Content -LiteralPath (Join-Path $layout.Local 'doc.txt') -Raw) 'rule10: the local edit should be kept'
    Assert-Equal 'base' (Get-Content -LiteralPath (Join-Path $layout.Local 'doc-moved.txt') -Raw) 'rule10: the moved content should arrive locally'

    $remoteNames = Get-RemoteRelativeNames $layout.Remote
    Assert-True ($remoteNames -contains 'doc.txt') 'rule10: the edited name should arrive on Dropbox'
    Assert-True ($remoteNames -contains 'doc-moved.txt') 'rule10: the moved name should stay on Dropbox'
    Assert-Equal 'local edit' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'doc.txt')) 'rule10: Dropbox should carry the local edit'
    Assert-Equal 'base' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'doc-moved.txt')) 'rule10: Dropbox should keep the moved content'
}

function Invoke-ScenarioRule06 {
    # Local delete propagates (rule 6): the file is gone on Dropbox too and its version is retained.
    $layout = New-Layout 'rule06-delete-propagates'
    New-TextFile (Join-Path $layout.Local 'to-delete.txt') 'delete-me'
    Initialize-Pair $layout | Out-Null
    Assert-True (Test-RemotePathExists (Join-RemotePath $layout.Remote 'to-delete.txt')) 'rule06: the file should first be synced to Dropbox'

    Remove-Item -LiteralPath (Join-Path $layout.Local 'to-delete.txt') -Force
    $run = Invoke-Incremental $layout 'rule06 delete'
    Assert-True (-not ((Get-RelativeFiles $layout.Local) -contains 'to-delete.txt')) 'rule06: deleted file stays deleted locally'
    Assert-True (-not (Test-RemotePathExists (Join-RemotePath $layout.Remote 'to-delete.txt'))) 'rule06: the file should be gone from Dropbox'
    Assert-True ((Get-PairRecoveryPaths $layout.PairId) -contains 'to-delete.txt') 'rule06: the propagated delete should be retained in Recovery'
    Assert-Converged -Layout $layout -What 'rule06 delete'
}

function Invoke-ScenarioRule24 {
    # Folder deleted with one edited child: the child survives (rule 24). The edit is made on the
    # remote side with `dropbox put` before the local folder is deleted.
    $layout = New-Layout 'rule24-folder-delete'
    New-TextFile (Join-Path $layout.Local 'tree\a.txt') 'a'
    New-TextFile (Join-Path $layout.Local 'tree\b.txt') 'b'
    Initialize-Pair $layout | Out-Null

    New-RemoteFile $layout.Remote 'tree/b.txt' 'b-edited'
    Remove-Item -LiteralPath (Join-Path $layout.Local 'tree') -Recurse -Force
    $run = Invoke-Incremental $layout 'rule24 folder delete'

    $files = Get-RelativeFiles $layout.Local
    Assert-True ($files -contains 'tree/b.txt') 'rule24: the edited child should be restored locally'
    Assert-Equal 'b-edited' (Get-Content -LiteralPath (Join-Path $layout.Local 'tree/b.txt') -Raw) 'rule24: the restored child should carry the remote edit'
    Assert-True (-not ($files -contains 'tree/a.txt')) 'rule24: the unchanged child should stay deleted locally'

    Assert-True (Test-RemotePathExists (Join-RemotePath $layout.Remote 'tree/b.txt')) 'rule24: the edited child should remain on Dropbox'
    Assert-Equal 'b-edited' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'tree/b.txt')) 'rule24: Dropbox should carry the edited child'
    Assert-True (-not (Test-RemotePathExists (Join-RemotePath $layout.Remote 'tree/a.txt'))) 'rule24: the unchanged child should be deleted on Dropbox'
}

function Invoke-ScenarioRq034 {
    # RQ-034 (safety, no planner rule number): the local pair root missing on disk must never be
    # read as "everything deleted". The run must refuse before it does anything at all — a review
    # refusal naming the missing folder, with no data — and the remote file's content and revision
    # must be untouched. (The refusal plus the unchanged remote is the proof.)
    $layout = New-Layout 'rq034-local-root-missing'
    New-TextFile (Join-Path $layout.Local 'keep.txt') 'keep'
    Initialize-Pair $layout | Out-Null

    $keep = Join-RemotePath $layout.Remote 'keep.txt'
    $beforeRev = [string](Get-RemoteEntry $keep).rev
    $beforeRevisions = @(Get-RemoteRevisions $keep).Count

    $renamed = $layout.Local + '-renamed'
    if (Test-Path -LiteralPath $renamed) {
        Fail "rq034: the rename destination '$renamed' already exists; refusing to nest the local root into it"
    }
    # A same-volume directory rename. [System.IO.Directory]::Move fails if the destination exists,
    # rather than nesting the source inside it the way Move-Item can.
    [System.IO.Directory]::Move($layout.Local, $renamed)
    try {
        # The one expected-failure probe: Invoke-Incremental enforces no timeout, a valid envelope and
        # not stopped; for this outcome it accepts the up-front review refusal (its code and the phrase
        # of its message naming the missing local folder), which carries no data. A run that instead
        # returned a report would still be checked for no effects. The restore run below stays strict.
        $run = Invoke-Incremental $layout 'rq034 missing local root' -Expect 'LocalRootMissing'
        if ($null -ne $run.Data) { Assert-NoEffects -Run $run -What 'rq034: the missing-root run' }
    }
    finally {
        # Restore only the exact folder this scenario moved: it must still hold the scenario's own
        # marker file, and the original path must be free, so a foreign folder is never adopted and
        # nothing that appeared meanwhile is overwritten.
        if (-not (Test-Path -LiteralPath (Join-Path $renamed 'keep.txt'))) {
            Fail "rq034: the renamed root '$renamed' is not the folder this scenario moved; refusing to restore it"
        }
        if (Test-Path -LiteralPath $layout.Local) {
            Fail "rq034: the original root '$($layout.Local)' is occupied; refusing to overwrite it during restore"
        }
        [System.IO.Directory]::Move($renamed, $layout.Local)
    }

    # The no-delete proof: the remote file still exists with its exact revision and content, and no
    # revision was added (a delete or a re-upload would move both), all read with `stat` and `revisions`.
    Assert-Equal $beforeRev ([string](Get-RemoteEntry $keep).rev) 'rq034: the remote revision should be unchanged'
    Assert-Equal 'keep' (Get-RemoteFileText $keep) 'rq034: the remote content should be unchanged'
    Assert-Equal $beforeRevisions (@(Get-RemoteRevisions $keep).Count) 'rq034: the remote revision count should be unchanged'

    # And after the restore the pair converges.
    $run2 = Invoke-Incremental $layout 'rq034 restore'
    Assert-NoEffects -Run $run2 -What 'rq034 restore'
    Assert-True ((Get-RelativeFiles $layout.Local) -contains 'keep.txt') 'rq034: the local file should still be there'
}

function Invoke-ScenarioChoicesRemoveCopy {
    # Per-folder one-sided choices by prefix (decision 52): a `remove` on one folder deletes the
    # one-sided file on the side that holds it (recovery-first); a `copy` on another folder puts
    # its one-sided file on the other side, and the first sync completes. The prefixes match the
    # folder and everything below it, never a sibling. Neutral folder names only (decision 53).
    $layout = New-Layout 'choices-remove-copy-prefix'
    New-TextFile (Join-Path $layout.Local 'alpha\x.txt') 'one'
    New-TextFile (Join-Path $layout.Local 'beta\y.txt') 'two'
    New-Pair $layout

    $choices = New-ChoicesFile '{"oneSided":[{"prefix":"alpha/","side":"Local","action":"remove"},{"prefix":"beta/","side":"Local","action":"copy"}]}'
    $prepare = Invoke-FirstSyncPrepare $layout -Choices $choices
    Assert-Equal $script:DigestHexLength ([string]$prepare.choicesDigest).Length 'choices: prepare should report a SHA-256 choices digest'
    Assert-Equal 2 (@($prepare.choiceEffects).Count) 'choices: prepare should report both one-sided choices'

    Enable-Pair $layout
    $apply = Invoke-FirstSyncApply $layout -Choices $choices
    Assert-Equal 2 (@($apply.choiceEffects).Count) 'choices: apply should report both choice effects'

    $localNames = Get-RelativeFiles $layout.Local
    Assert-True (-not ($localNames -contains 'alpha/x.txt')) 'choices: the remove choice should delete the file on its own side'
    Assert-True ($localNames -contains 'beta/y.txt') 'choices: the copy choice should leave its source file in place'

    Assert-True (Test-RemotePathExists (Join-RemotePath $layout.Remote 'beta/y.txt')) 'choices: the copy choice should put the file on the other side'
    Assert-Equal 'two' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'beta/y.txt')) 'choices: the copied content should match'
    Assert-True (-not (Test-RemotePathExists (Join-RemotePath $layout.Remote 'alpha/x.txt'))) 'choices: the removed file should not appear on the other side'
    Assert-True ((Get-PairRecoveryPaths $layout.PairId) -contains 'alpha/x.txt') 'choices: the removed file should be retained recovery-first'
}

function Invoke-ScenarioChoicesChangedRefused {
    # Decision 54: an apply is bound to the reviewed prepare. Applying choices whose digest differs
    # from the last prepare is a review error (exit 5) naming that exact reason, and no writer is
    # reached, so nothing changes anywhere. The original prepared choices then still apply cleanly.
    $layout = New-Layout 'choices-changed-refused'
    New-TextFile (Join-Path $layout.Local 'alpha\x.txt') 'one'
    New-Pair $layout

    $prepared = New-ChoicesFile '{"oneSided":[{"prefix":"alpha/","side":"Local","action":"copy"}]}'
    $changed = New-ChoicesFile '{"oneSided":[{"prefix":"alpha/","side":"Local","action":"remove"}]}'
    Invoke-FirstSyncPrepare $layout -Choices $prepared | Out-Null
    Enable-Pair $layout

    $refused = Invoke-FirstSyncApplyRefusal $layout $changed
    Assert-Equal $script:ReviewExitCode ([int]$refused.ExitCode) 'changed choices: the refusal should exit 5'
    Assert-Refusal $refused $script:ReviewRequiredCode $script:ChoicesDifferReason 'changed choices'

    # No effect of any kind: the local file is untouched with its CONTENT intact, nothing reaches the
    # other side, nothing is retained, and review is still required.
    $localFile = Join-Path $layout.Local 'alpha\x.txt'
    Assert-True ((Get-RelativeFiles $layout.Local) -contains 'alpha/x.txt') 'changed choices: the local file should be untouched'
    Assert-Equal 'one' (Get-Content -LiteralPath $localFile -Raw) 'changed choices: the local content should be unchanged'
    Assert-True (-not (Test-RemotePathExists (Join-RemotePath $layout.Remote 'alpha/x.txt'))) 'changed choices: no file should reach the other side'
    Assert-Equal 0 (@(Get-PairRecoveryPaths $layout.PairId).Count) 'changed choices: no remove or copy should be retained'
    Assert-True ((Get-PairState -PairId $layout.PairId).firstSyncReviewRequired -eq $true) 'changed choices: first-sync review should still be required'

    # Positive control: the ORIGINAL prepared choices still apply to the same pair, proving the first
    # refusal was the digest mismatch and not a broken pair.
    Invoke-FirstSyncApply $layout -Choices $prepared | Out-Null
    Assert-True ((Get-RelativeFiles $layout.Local) -contains 'alpha/x.txt') 'positive control: the copy should leave the source file in place'
    Assert-Equal 'one' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'alpha/x.txt')) 'positive control: the prepared copy should reach the other side'
}

function Invoke-ScenarioChoicesHappyPath {
    # The prepare -> apply happy path (decision 54): the same choices on both calls succeed and the
    # reviewed effect the prepare reported is exactly what the apply executed. A one-sided remote
    # file is copied down to the local side, so the local side is genuinely changed by the choice
    # (the file is absent locally before the apply).
    $layout = New-Layout 'choices-happy-path'
    New-Pair $layout
    New-RemoteFile $layout.Remote 'gamma/z.txt' 'three'
    Assert-True (-not ((Get-RelativeFiles $layout.Local) -contains 'gamma/z.txt')) 'happy path: the file should not exist locally before the apply'

    $choices = New-ChoicesFile '{"oneSided":[{"prefix":"gamma/","side":"Dropbox","action":"copy"}]}'
    $prepare = Invoke-FirstSyncPrepare $layout -Choices $choices
    Assert-Equal $script:DigestHexLength ([string]$prepare.choicesDigest).Length 'happy path: prepare should report a SHA-256 choices digest'
    Assert-Equal 1 (@($prepare.choiceEffects).Count) 'happy path: prepare should report the one choice'
    $preparedEffect = @($prepare.choiceEffects)[0]
    Assert-Equal (Get-ComparableChoicePrefix 'gamma/') (Get-ComparableChoicePrefix ([string]$preparedEffect.prefix)) 'happy path: the reported effect should name the choice prefix'
    Assert-Equal 'copy' ([string]$preparedEffect.action) 'happy path: the reported action should be copy'
    Assert-Equal 'Dropbox' ([string]$preparedEffect.side) 'happy path: the reported side should be Dropbox'
    Assert-Equal 1 ([int]$preparedEffect.files) 'happy path: the reported effect should cover one file'

    Enable-Pair $layout
    $apply = Invoke-FirstSyncApply $layout -Choices $choices
    Assert-Equal 1 (@($apply.choiceEffects).Count) 'happy path: apply should report the same one choice effect'

    # The apply's effect must equal the prepare's, field for field, not just in count: that is the
    # proof the reviewed effect is what was executed.
    $appliedEffect = @($apply.choiceEffects)[0]
    foreach ($field in @('prefix', 'side', 'action', 'files', 'folders', 'bytes')) {
        # A prefix is compared without its trailing separator (the effect trims it); every other field
        # compared verbatim.
        $preparedValue = [string]$preparedEffect.$field
        $appliedValue = [string]$appliedEffect.$field
        if ($field -eq 'prefix') {
            $preparedValue = Get-ComparableChoicePrefix $preparedValue
            $appliedValue = Get-ComparableChoicePrefix $appliedValue
        }
        Assert-Equal $preparedValue $appliedValue "happy path: the apply effect '$field' should equal the prepare's"
    }

    Assert-True ((Get-RelativeFiles $layout.Local) -contains 'gamma/z.txt') 'happy path: the copied file should arrive locally'
    Assert-Equal 'three' (Get-Content -LiteralPath (Join-Path $layout.Local 'gamma\z.txt') -Raw) 'happy path: the copied content should match'
    Assert-True (@(Get-PairRecoveries $layout.PairId).Count -eq 0) 'happy path: a copy retains no recovery record'
}

function Invoke-ScenarioChoicesRemoveGrowthRefused {
    # Decision 54 addendum: a `remove` choice whose effect grows after the prepare (a new one-sided
    # file appeared under its prefix) is refused with the same review error, naming the growth, and
    # makes no effect. The digest is unchanged (the same document is applied), so the growth guard is
    # the reason, not the digest guard. Neutral names only (decision 53).
    $layout = New-Layout 'choices-remove-growth'
    New-TextFile (Join-Path $layout.Local 'alpha\x.txt') 'one'
    New-Pair $layout

    $choices = New-ChoicesFile '{"oneSided":[{"prefix":"alpha/","side":"Local","action":"remove"}]}'
    $prepare = Invoke-FirstSyncPrepare $layout -Choices $choices
    Assert-Equal 1 ([int](@($prepare.choiceEffects)[0]).files) 'remove growth: the prepare should report one file'
    Enable-Pair $layout

    # A new one-sided file appears under the same prefix after the prepare, so the unchanged remove
    # choice would now remove more than the prepare reported.
    New-TextFile (Join-Path $layout.Local 'alpha\y.txt') 'two'

    $refused = Invoke-FirstSyncApplyRefusal $layout $choices
    Assert-Equal $script:ReviewExitCode ([int]$refused.ExitCode) 'remove growth: the refusal should exit 5'
    Assert-Refusal $refused $script:ReviewRequiredCode $script:RemoveGrowthReason 'remove growth'

    # No effect: both local files are untouched with their content, nothing is on the other side, and
    # nothing is retained.
    $localFiles = Get-RelativeFiles $layout.Local
    Assert-True ($localFiles -contains 'alpha/x.txt') 'remove growth: the first file should be untouched'
    Assert-True ($localFiles -contains 'alpha/y.txt') 'remove growth: the new file should be untouched'
    Assert-Equal 'one' (Get-Content -LiteralPath (Join-Path $layout.Local 'alpha\x.txt') -Raw) 'remove growth: the first file content should be unchanged'
    Assert-Equal 'two' (Get-Content -LiteralPath (Join-Path $layout.Local 'alpha\y.txt') -Raw) 'remove growth: the new file content should be unchanged'
    Assert-True (-not (Test-RemotePathExists (Join-RemotePath $layout.Remote 'alpha/x.txt'))) 'remove growth: nothing should reach the other side'
    Assert-Equal 0 (@(Get-PairRecoveryPaths $layout.PairId).Count) 'remove growth: nothing should be retained'
}

function Invoke-ScenarioLocalDeleteDelta {
    # A local delete after the first sync propagates on the incremental (delta) run (rule 6): the
    # Dropbox copy is removed recovery-first and the run reports the delete, while the other files
    # are untouched on both sides. The run is a delta because the first sync left each root cursor.
    $layout = New-Layout 'delete-after-first-sync-delta'
    New-TextFile (Join-Path $layout.Local 'keep-one.txt') 'keep-one'
    New-TextFile (Join-Path $layout.Local 'keep-two.txt') 'keep-two'
    New-TextFile (Join-Path $layout.Local 'goes-away.txt') 'gone'
    Initialize-Pair $layout | Out-Null
    Assert-True (Test-RemotePathExists (Join-RemotePath $layout.Remote 'goes-away.txt')) 'local delete delta: the file should first be synced to Dropbox'

    Remove-Item -LiteralPath (Join-Path $layout.Local 'goes-away.txt') -Force
    $run = Invoke-Incremental $layout 'local delete delta'

    # The run reports the delete, and the removal is recovery-first on Dropbox.
    Assert-True ([int]$run.Data.deleted -ge 1) 'local delete delta: the run should report a delete'
    Assert-True (-not (Test-RemotePathExists (Join-RemotePath $layout.Remote 'goes-away.txt'))) 'local delete delta: the Dropbox copy should be removed'
    Assert-True ((Get-PairRecoveryPaths $layout.PairId) -contains 'goes-away.txt') 'local delete delta: the removed copy should be retained in Recovery'

    # The other two files are untouched on both sides.
    $localFiles = Get-RelativeFiles $layout.Local
    Assert-True ($localFiles -contains 'keep-one.txt') 'local delete delta: keep-one should stay locally'
    Assert-True ($localFiles -contains 'keep-two.txt') 'local delete delta: keep-two should stay locally'
    Assert-Equal 'keep-one' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'keep-one.txt')) 'local delete delta: keep-one should be unchanged on Dropbox'
    Assert-Equal 'keep-two' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'keep-two.txt')) 'local delete delta: keep-two should be unchanged on Dropbox'

    Assert-Converged -Layout $layout -What 'local delete delta'
}

function Invoke-ScenarioLocalDeleteDeleteAndMove {
    # The journal (delta) path with BOTH a delete and a rename: the delete is detected from the baseline and
    # the rename propagates as a move, while the untouched file stays. (The forced full-rescan path is its own
    # scenario, LocalDeleteAfterFirstSync_FullRescan.)
    $layout = New-Layout 'delete-after-first-sync-delete-move'
    New-TextFile (Join-Path $layout.Local 'keep.txt') 'keep'
    New-TextFile (Join-Path $layout.Local 'goes-away.txt') 'gone'
    New-TextFile (Join-Path $layout.Local 'before.txt') 'move-me'
    Initialize-Pair $layout | Out-Null
    Assert-True (Test-RemotePathExists (Join-RemotePath $layout.Remote 'goes-away.txt')) 'journal delete+move: goes-away should first be synced to Dropbox'
    Assert-True (Test-RemotePathExists (Join-RemotePath $layout.Remote 'before.txt')) 'journal delete+move: before should first be synced to Dropbox'

    Remove-Item -LiteralPath (Join-Path $layout.Local 'goes-away.txt') -Force
    Move-Item -LiteralPath (Join-Path $layout.Local 'before.txt') -Destination (Join-Path $layout.Local 'after.txt')
    $run = Invoke-Incremental $layout 'journal delete+move'

    # The delete is detected from the baseline, and removal is recovery-first on Dropbox.
    Assert-True ([int]$run.Data.deleted -ge 1) 'journal delete+move: the run should report a delete'
    Assert-True (-not (Test-RemotePathExists (Join-RemotePath $layout.Remote 'goes-away.txt'))) 'journal delete+move: the Dropbox copy should be removed'
    Assert-True ((Get-PairRecoveryPaths $layout.PairId) -contains 'goes-away.txt') 'journal delete+move: the removed copy should be retained in Recovery'

    # The rename propagates: the old name is gone and the new name carries the content on Dropbox.
    Assert-True (Test-RemotePathExists (Join-RemotePath $layout.Remote 'after.txt')) 'journal delete+move: the new name should exist on Dropbox'
    Assert-True (-not (Test-RemotePathExists (Join-RemotePath $layout.Remote 'before.txt'))) 'journal delete+move: the old name should be gone from Dropbox'
    Assert-Equal 'move-me' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'after.txt')) 'journal delete+move: the moved content should be on Dropbox'

    # The untouched file stays on both sides.
    Assert-True ((Get-RelativeFiles $layout.Local) -contains 'keep.txt') 'journal delete+move: keep should stay locally'
    Assert-Equal 'keep' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'keep.txt')) 'journal delete+move: keep should be unchanged on Dropbox'

    Assert-Converged -Layout $layout -What 'journal delete+move'
}

function Invoke-ScenarioLocalDeleteFullRescan {
    # A true full rescan. The first sync's preview commits no root cursor, so one settling run leaves each
    # root a stored cursor; `pairs rescan` then forgets both, so the next run re-reads each root in full
    # instead of from its journal delta. The local delete made before the rescan is still detected as a delete
    # on Dropbox - from the retained baseline, which the rescan never touches.
    $layout = New-Layout 'delete-after-first-sync-full-rescan'
    New-TextFile (Join-Path $layout.Local 'keep-one.txt') 'keep-one'
    New-TextFile (Join-Path $layout.Local 'keep-two.txt') 'keep-two'
    New-TextFile (Join-Path $layout.Local 'goes-away.txt') 'gone'
    Initialize-Pair $layout | Out-Null
    Assert-True (Test-RemotePathExists (Join-RemotePath $layout.Remote 'goes-away.txt')) 'full rescan: the file should first be synced to Dropbox'

    # A settling run leaves each root a stored cursor, so the rescan below actually changes the next scan.
    Invoke-Incremental $layout 'full rescan settling run' | Out-Null

    Remove-Item -LiteralPath (Join-Path $layout.Local 'goes-away.txt') -Force
    Invoke-Rescan $layout | Out-Null
    $run = Invoke-Incremental $layout 'full rescan'

    # The delete is detected from the baseline and removal is recovery-first on Dropbox.
    Assert-True ([int]$run.Data.deleted -ge 1) 'full rescan: the run should report a delete'
    Assert-True (-not (Test-RemotePathExists (Join-RemotePath $layout.Remote 'goes-away.txt'))) 'full rescan: the Dropbox copy should be removed'
    Assert-True ((Get-PairRecoveryPaths $layout.PairId) -contains 'goes-away.txt') 'full rescan: the removed copy should be retained in Recovery'

    # The other two files are untouched on both sides.
    $localFiles = Get-RelativeFiles $layout.Local
    Assert-True ($localFiles -contains 'keep-one.txt') 'full rescan: keep-one should stay locally'
    Assert-True ($localFiles -contains 'keep-two.txt') 'full rescan: keep-two should stay locally'
    Assert-Equal 'keep-one' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'keep-one.txt')) 'full rescan: keep-one should be unchanged on Dropbox'
    Assert-Equal 'keep-two' (Get-RemoteFileText (Join-RemotePath $layout.Remote 'keep-two.txt')) 'full rescan: keep-two should be unchanged on Dropbox'

    Assert-Converged -Layout $layout -What 'full rescan'
}

function Invoke-ScenarioPerf2000 {
    # Throughput: 2,000 tiny files through the first sync; report files per second and the remote count.
    $layout = New-Layout 'perf-2000-tiny'
    for ($i = 0; $i -lt $script:TinyFileCount; $i++) {
        New-TextFile (Join-Path $layout.Local ('c' + $i + '.txt')) ('x' * $script:TinyFileBytes)
    }
    Initialize-Pair $layout | Out-Null

    Assert-Equal $script:TinyFileCount ([int]$layout.PlannedItems) 'perf: the preview should plan all tiny files'
    Assert-Equal $script:TinyFileCount (@(Get-RelativeFiles $layout.Local).Count) 'perf: the local file count should be unchanged'
    Assert-Equal $script:TinyFileCount ([int]$layout.ApplyData.appliedItems) 'perf: the apply should report all tiny files'
    Assert-Equal $script:TinyFileCount ([int]$layout.ApplyData.uploaded) 'perf: the apply should upload all tiny files'
    Assert-Equal $script:TinyFileCount (@(Get-RemoteRelativeNames $layout.Remote).Count) 'perf: every tiny file should exist on Dropbox'

    if ($layout.ApplySeconds -le 0) { Fail 'perf: apply timing was not captured' }
    $rate = [math]::Round($script:TinyFileCount / $layout.ApplySeconds, 1)
    Add-ScenarioNote ("throughput: {0} files in {1}s = {2} files/s" -f $script:TinyFileCount, $layout.ApplySeconds, $rate)
}

# ---------------------------------------------------------------------------
# Driver
# ---------------------------------------------------------------------------
$scenarioTable = [ordered]@{
    'Rule04_05_FirstSyncNewerWinsAndTieHold' = 'Invoke-ScenarioRule04_05'
    'Rule02_OneSidedEditEachDirection'       = 'Invoke-ScenarioRule02'
    'Rule27_EditBothSidesKeepBoth'           = 'Invoke-ScenarioRule27'
    'Rule07_EditVsDelete'                    = 'Invoke-ScenarioRule07'
    'Rule09_LocalMovePropagates'             = 'Invoke-ScenarioRule09'
    'Rule10_MoveVsEditKeepBoth'              = 'Invoke-ScenarioRule10'
    'Rule06_DeletePropagatesToRecovery'      = 'Invoke-ScenarioRule06'
    'Rule24_FolderDeleteEditedChildSurvives' = 'Invoke-ScenarioRule24'
    'RQ034_LocalRootMissingNoDeletes'        = 'Invoke-ScenarioRq034'
    'Choices_RemoveAndCopyByPrefix'          = 'Invoke-ScenarioChoicesRemoveCopy'
    'Choices_ChangedApplyRefused'            = 'Invoke-ScenarioChoicesChangedRefused'
    'Choices_PrepareApplySameChoices'        = 'Invoke-ScenarioChoicesHappyPath'
    'Choices_RemoveGrowthRefused'            = 'Invoke-ScenarioChoicesRemoveGrowthRefused'
    'LocalDeleteAfterFirstSync_Delta'        = 'Invoke-ScenarioLocalDeleteDelta'
    'LocalDeleteAfterFirstSync_DeleteAndMove' = 'Invoke-ScenarioLocalDeleteDeleteAndMove'
    'LocalDeleteAfterFirstSync_FullRescan'   = 'Invoke-ScenarioLocalDeleteFullRescan'
    'Perf_2000TinyFilesThroughput'           = 'Invoke-ScenarioPerf2000'
}

function Resolve-ScenarioNames([string]$Selection) {
    if ($Selection -eq 'all') { return @($scenarioTable.Keys) }
    $match = @($scenarioTable.Keys | Where-Object { $_ -eq $Selection })
    if ($match.Count -eq 0) {
        $match = @($scenarioTable.Keys | Where-Object { $_ -like "*$Selection*" })
    }
    if ($match.Count -eq 0) {
        Fail ("no scenario matches '$Selection'. Known scenarios: " + ($scenarioTable.Keys -join ', '))
    }
    return $match
}

function Write-Summary {
    Write-Host ''
    Write-Host '==================== V2 LIVE TESTS SUMMARY ===================='
    $script:Results |
        Select-Object Scenario, Status, Seconds, Reason |
        Format-Table -AutoSize -Wrap |
        Out-String -Width 400 |
        Write-Host

    $passed = @($script:Results | Where-Object { $_.Status -eq 'PASS' }).Count
    $failed = @($script:Results | Where-Object { $_.Status -eq 'FAIL' }).Count
    Write-Host ("Totals: PASS $passed  FAIL $failed")
    Write-Host ("Exit code = number of failures = $failed")
    return $failed
}

# --- startup -----------------------------------------------------------------
# A double quote in any parameter cannot be escaped safely onto the Windows command line, so
# reject it before anything is created, synced or deleted.
$quotedParams = @()
foreach ($entry in @(
        @{ Name = 'Exe'; Value = $Exe }, @{ Name = 'LocalBase'; Value = $LocalBase },
        @{ Name = 'RemoteBase'; Value = $RemoteBase }, @{ Name = 'Account'; Value = $Account },
        @{ Name = 'Scenario'; Value = $Scenario }, @{ Name = 'ReportRoot'; Value = $ReportRoot })) {
    if ($entry.Value -and ([string]$entry.Value).IndexOf([char]0x22) -ge 0) { $quotedParams += $entry.Name }
}
if ($quotedParams.Count -gt 0) {
    throw ("The parameter(s) {0} contain a double quote, which cannot be passed safely on a command line. Remove it and retry." -f ($quotedParams -join ', '))
}

# Every expected run outcome names real scenarios; a typo would silently allow nothing.
foreach ($outcomeName in $script:RunExpectedOutcomes.Keys) {
    foreach ($allowedName in $script:RunExpectedOutcomes[$outcomeName].Scenarios) {
        if (-not $scenarioTable.Contains($allowedName)) {
            throw "RunExpectedOutcomes '$outcomeName' names '$allowedName', which is not a scenario in the scenario table."
        }
    }
}

if (-not (Test-Path -LiteralPath $Exe)) { throw "SyncShark.exe not found at '$Exe'." }
$script:ExePath = (Resolve-Path -LiteralPath $Exe).Path

New-Item -ItemType Directory -Force -Path $script:LocalBase | Out-Null
$runStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ($ReportRoot) {
    $script:ReportDir = Join-Path $ReportRoot $runStamp
}
else {
    $script:ReportDir = Join-Path $script:LocalBase ('_reports\' + $runStamp)
}
New-Item -ItemType Directory -Force -Path $script:ReportDir | Out-Null
# --in/--out and the pair --local must be absolute; make the report folder absolute here.
$script:ReportDir = (Resolve-Path -LiteralPath $script:ReportDir).Path

# Nothing here changes this shell's environment: each child process selects the core through
# SYNCSHARK_CORE, set only for that one launch inside Invoke-CliRaw and restored immediately.
Write-Host "Sync Shark core live tests (each child selects the core with $($script:CoreEnvironmentVar)=$($script:CoreSetting); the setting is applied only while launching each child, then restored)"
Write-Host "  exe        : $($script:ExePath)"
Write-Host "  local base : $($script:LocalBase)"
Write-Host "  remote base: $($script:RemoteBase)"
Write-Host "  account    : $($script:Account)"
Write-Host "  reports    : $($script:ReportDir)"

# Prove v2 before any scenario runs, so a wrong setting cannot silently exercise the other core.
# `status` also reports the resolved v2 product root, so the auxiliary write locations are printed
# before the first effect.
$probe = Invoke-Cli -CliArgs @($script:Cli.Status)
Assert-True ([string]$probe.Data.mode -eq $script:CoreModeV2) `
    ("$($script:CoreEnvironmentVar)=$($script:CoreSetting) did not select the v2 core: 'status' reported mode '$($probe.Data.mode)'")
Write-Host "  product    : $($probe.Data.productRoot)  (v2 state: <product>\v2\state.db with -wal/-shm sidecars; prepare records: <product>\v2\first-sync-<pairId>.choices.json)"
Write-Host "  recovery   : <recovery root>\.syncshark-recovery-<pairId>\<operationId>.recovery  (the root from the Recovery location setting, decision 48)"
Write-Host "  control    : <parent of local pair root>\.syncshark-control-<namespaceId>\  (a sibling of each local pair root)"
Write-Host ''

$names = Resolve-ScenarioNames $Scenario
foreach ($name in $names) {
    $script:ScenarioNotes = @()
    $script:CurrentScenario = $name
    $status = 'PASS'
    $reason = ''
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $scenarioTable[$name]
    }
    catch {
        $message = $_.Exception.Message
        if ($message.StartsWith($script:FailPrefix)) {
            $status = 'FAIL'
            $reason = $message.Substring($script:FailPrefix.Length)
        }
        else {
            $status = 'FAIL'
            $reason = "unexpected error: $message"
        }
    }
    $sw.Stop()
    if ($script:ScenarioNotes.Count -gt 0) {
        $note = ($script:ScenarioNotes -join ' | ')
        if ($reason) { $reason = "$reason | $note" } else { $reason = $note }
    }
    Write-Host ("[{0}] {1} ({2}s)" -f $status, $name, [math]::Round($sw.Elapsed.TotalSeconds, 1))
    if ($reason) { Write-Host "        $reason" }
    $script:Results += [pscustomobject]@{
        Scenario = $name
        Status   = $status
        Reason   = $reason
        Seconds  = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    }
}

$failures = Write-Summary
exit $failures
