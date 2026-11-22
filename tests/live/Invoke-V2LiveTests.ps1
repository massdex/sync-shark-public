<#
.SYNOPSIS
    Live acceptance driver for Sync Shark on a real test pair.

.DESCRIPTION
    Drives the new sync-shark command line (CLI-CAPABILITIES.md) against a real Dropbox test
    folder and a real local folder. The host keeps running the pairs; the driver creates a pair,
    confirms its roots, forces a comparison with `repair`, mutates the local side with PowerShell
    and the remote side through the Dropbox HTTP API, forces another comparison, and asserts the
    outcome on BOTH sides. It also proves the log checks: `logs` reads the current files while the
    host writes them, and individual file operations appear only with `--show-files`.

    This is a LIVE test, not a CI test. It writes real files, but only inside its own
    random-suffixed local folder and its own random-suffixed remote folder under -RemoteBase. It
    never creates or modifies -RemoteBase itself, and it never touches a real pair.

    The command line prints plain text; it does not emit a JSON envelope. `pairs add` prints the
    new pair id; where an id is not printed the driver reads `pairs list`. Each command's
    stdout/stderr is written under -ReportRoot for inspection.

    Remote writes use the Dropbox HTTP API v2 with a test-account access token read from the
    environment variable named by -TokenVariable (default SYNCSHARK_LIVE_DROPBOX_TOKEN). The token
    is never written to a report file or passed on a command line.

.PARAMETER Exe
    Absolute path to the new syncshark.exe.

.PARAMETER LocalBase
    Local root; each scenario gets a random-suffixed subfolder created without -Force.

.PARAMETER RemoteBase
    Existing Dropbox folder; each scenario gets a random-suffixed subfolder under it. It is never
    created or modified by this script.

.PARAMETER Account
    The retained Dropbox connection id passed to `pairs add --account`.

.PARAMETER Scenario
    Exact scenario name, a substring, or `all`.

.PARAMETER TokenVariable
    Name of the environment variable holding the Dropbox test-account token.

.PARAMETER ReportRoot
    Where per-command stdout/stderr and scratch files are written.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Account,
    [string]$LocalBase = (Join-Path $env:LOCALAPPDATA 'SyncSharkTests\live'),
    [string]$RemoteBase = '/Apps/Sync Shark/Testing/live',
    [string]$Scenario = 'all',
    [string]$TokenVariable = 'SYNCSHARK_LIVE_DROPBOX_TOKEN',
    [int]$CommandTimeoutSeconds = 180,
    [int]$RunTimeoutSeconds = 1800,
    [string]$ReportRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:DropboxApi = 'https://api.dropboxapi.com/2'
$script:DropboxContent = 'https://content.dropboxapi.com/2'
$script:IdPattern = '^[0-9a-f]{32}$'
$script:SuffixLength = 8
$script:Failures = 0
$script:Notes = New-Object System.Collections.Generic.List[string]
$script:LastDropboxSummary = ''
$script:Account = $Account

if (-not $ReportRoot) { $ReportRoot = Join-Path $LocalBase '_reports' }
if (-not (Test-Path -LiteralPath $Exe)) { throw "Exe not found: $Exe" }
if (-not (Test-Path -LiteralPath $LocalBase)) { New-Item -ItemType Directory -Force -Path $LocalBase | Out-Null }
New-Item -ItemType Directory -Force -Path $ReportRoot | Out-Null
$script:ReportRoot = $ReportRoot

$script:Token = [Environment]::GetEnvironmentVariable($TokenVariable)
if (-not $script:Token) { throw "Dropbox access token is missing; set $TokenVariable for the test account." }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$script:ExePath = (Resolve-Path -LiteralPath $Exe).Path
$script:LogFolder = Join-Path $env:LOCALAPPDATA 'Massdex\SyncShark\logs'

function Add-Note([string]$Message) { $script:Notes.Add($Message) | Out-Null }
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function New-Suffix { [guid]::NewGuid().ToString('N').Substring(0, $script:SuffixLength) }
function Join-Remote([string]$Folder, [string]$Name) {
    if ($Folder.EndsWith('/')) { return "$Folder$Name" }
    return "$Folder/$Name"
}

# --- Command line -----------------------------------------------------------

# Quotes one argument for a Windows command line (PS 5.1 has no ProcessStartInfo.ArgumentList).
function Quote-Arg([string]$Value) {
    if ($Value -match '[\s"]') { return '"' + ($Value -replace '"', '\"') + '"' }
    return $Value
}

# Starts the new CLI with a proxy-free child environment (the engine's transports must not inherit
# an agent-injected proxy) and captures stdout, stderr and the exit code.
function Invoke-CliRaw {
    param([string[]]$Arguments, [int]$TimeoutSeconds = 0)
    if ($TimeoutSeconds -le 0) { $TimeoutSeconds = $script:CommandTimeoutSeconds }
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $script:ExePath
    $start.Arguments = (($Arguments | ForEach-Object { Quote-Arg $_ }) -join ' ')
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    foreach ($name in @('HTTPS_PROXY', 'HTTP_PROXY', 'https_proxy', 'http_proxy', 'ALL_PROXY', 'all_proxy')) {
        if ($start.EnvironmentVariables.ContainsKey($name)) { [void]$start.EnvironmentVariables.Remove($name) }
    }
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill() } catch { }
        throw "Command timed out after $TimeoutSeconds s: $($Arguments -join ' ')"
    }
    $out = $stdout.GetAwaiter().GetResult()
    $err = $stderr.GetAwaiter().GetResult()
    $record = [pscustomobject]@{ Args = $Arguments; ExitCode = $process.ExitCode; StdOut = $out; StdErr = $err }
    $safeName = (($Arguments -join '_') -replace '[^A-Za-z0-9._-]', '_')
    $safeName = $safeName.Substring(0, [Math]::Min(60, $safeName.Length))
    $record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $script:ReportRoot ("{0}-{1}.json" -f $stamp, $safeName)) -Encoding UTF8
    return $record
}

function Invoke-Cli {
    param([string[]]$Arguments, [int]$TimeoutSeconds = 0)
    $result = Invoke-CliRaw -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds
    if ($result.ExitCode -ne 0) {
        throw ("Command failed ({0}): {1}{2}{3}" -f $result.ExitCode, ($Arguments -join ' '), [Environment]::NewLine, $result.StdErr)
    }
    return $result.StdOut
}

function Get-PairIdFromText([string]$Text) {
    if (-not $Text) { return $null }
    foreach ($token in ($Text -split '[^0-9A-Za-z]+')) {
        if ($token -match $script:IdPattern) { return $token }
    }
    return $null
}

# Finds the newest stored run for a pair from the documented log folder, so `logs --run` gets a real id.
function Get-LatestRunId([string]$PairId) {
    if (-not (Test-Path -LiteralPath $script:LogFolder)) { return $null }
    $file = Get-ChildItem -LiteralPath $script:LogFolder -Filter "$PairId-*.log" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { return $null }
    return $file.Name.Substring($PairId.Length + 1, $file.Name.Length - $PairId.Length - 1 - 4)
}

# --- Dropbox HTTP API -------------------------------------------------------

function Read-WebResponseBody($WebException) {
    if (-not $WebException.Response) { return '' }
    $reader = New-Object System.IO.StreamReader($WebException.Response.GetResponseStream())
    return $reader.ReadToEnd()
}

function Set-LastDropboxSummary([string]$Body) {
    if (-not $Body) { $script:LastDropboxSummary = ''; return }
    try { $script:LastDropboxSummary = ($Body | ConvertFrom-Json).error_summary } catch { $script:LastDropboxSummary = $Body }
}

function Test-LastDropbox([string]$Prefix) {
    return ($script:LastDropboxSummary -and $script:LastDropboxSummary.StartsWith($Prefix, [System.StringComparison]::Ordinal))
}

function Invoke-DropboxRpc {
    param([string]$Route, [string]$Json)
    $headers = @{ Authorization = "Bearer $script:Token" }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Json)
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$script:DropboxApi/$Route" -Headers $headers `
            -ContentType 'application/json; charset=utf-8' -Body $bytes -TimeoutSec $script:CommandTimeoutSeconds
        $script:LastDropboxSummary = ''
        $content = $response.Content
        if ($content -is [byte[]]) { $content = [System.Text.Encoding]::UTF8.GetString($content) }
        if (-not $content) { return $null }
        return ($content | ConvertFrom-Json)
    } catch [System.Net.WebException] {
        Set-LastDropboxSummary (Read-WebResponseBody $_.Exception)
        throw
    }
}

function Invoke-DropboxContent {
    param([string]$Route, [string]$ArgJson, [byte[]]$Body)
    $headers = @{ Authorization = "Bearer $script:Token"; 'Dropbox-API-Arg' = $ArgJson }
    try {
        if ($null -eq $Body) {
            $response = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$script:DropboxContent/$Route" -Headers $headers -TimeoutSec $script:CommandTimeoutSeconds
        } else {
            $response = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$script:DropboxContent/$Route" -Headers $headers `
                -ContentType 'application/octet-stream' -Body $Body -TimeoutSec $script:CommandTimeoutSeconds
        }
        $script:LastDropboxSummary = ''
        $content = $response.Content
        if ($content -is [byte[]]) { return [System.Text.Encoding]::UTF8.GetString($content) }
        return [string]$content
    } catch [System.Net.WebException] {
        Set-LastDropboxSummary (Read-WebResponseBody $_.Exception)
        throw
    }
}

function New-RemoteFolder([string]$RemotePath) {
    try {
        Invoke-DropboxRpc -Route 'files/create_folder_v2' -Json (@{ path = $RemotePath; autorename = $false } | ConvertTo-Json -Compress) | Out-Null
    } catch {
        if (-not (Test-LastDropbox 'path/conflict/folder')) { throw }
    }
}

function Get-RemoteMetadata([string]$RemotePath) {
    try {
        return Invoke-DropboxRpc -Route 'files/get_metadata' -Json (@{ path = $RemotePath } | ConvertTo-Json -Compress)
    } catch {
        if (Test-LastDropbox 'path/not_found') { return $null }
        throw
    }
}

function Test-RemoteExists([string]$RemotePath) { return ($null -ne (Get-RemoteMetadata -RemotePath $RemotePath)) }

function Set-RemoteFile([string]$RemotePath, [string]$Text) {
    $arg = @{ path = $RemotePath; mode = 'overwrite'; autorename = $false; mute = $true } | ConvertTo-Json -Compress
    return (Invoke-DropboxContent -Route 'files/upload' -ArgJson $arg -Body ([System.Text.Encoding]::UTF8.GetBytes($Text)))
}

function Get-RemoteFileText([string]$RemotePath) {
    $arg = @{ path = $RemotePath } | ConvertTo-Json -Compress
    return (Invoke-DropboxContent -Route 'files/download' -ArgJson $arg -Body $null)
}

function Remove-RemotePath([string]$RemotePath) {
    try {
        Invoke-DropboxRpc -Route 'files/delete_v2' -Json (@{ path = $RemotePath } | ConvertTo-Json -Compress) | Out-Null
    } catch {
        if (-not (Test-LastDropbox 'path/not_found')) { throw }
    }
}

function Move-RemotePath([string]$From, [string]$To) {
    Invoke-DropboxRpc -Route 'files/move_v2' -Json (@{ from_path = $From; to_path = $To; autorename = $false } | ConvertTo-Json -Compress) | Out-Null
}

function Get-RemoteRelativePaths([string]$RemoteFolder) {
    $paths = New-Object System.Collections.Generic.List[string]
    $result = Invoke-DropboxRpc -Route 'files/list_folder' -Json (@{ path = $RemoteFolder; recursive = $true; include_deleted = $false } | ConvertTo-Json -Compress)
    while ($true) {
        foreach ($entry in @($result.entries)) {
            if ($entry.tag -ne 'file') { continue }
            $paths.Add($entry.path_display.Substring($RemoteFolder.Length).TrimStart('/')) | Out-Null
        }
        if (-not $result.has_more) { break }
        $result = Invoke-DropboxRpc -Route 'files/list_folder/continue' -Json (@{ cursor = $result.cursor } | ConvertTo-Json -Compress)
    }
    return $paths
}

function Get-RemoteRevisionCount([string]$RemotePath) {
    $result = Invoke-DropboxRpc -Route 'files/list_revisions' -Json (@{ path = $RemotePath; mode = 'path'; limit = 20 } | ConvertTo-Json -Compress)
    return @($result.entries).Count
}

# --- Local side -------------------------------------------------------------

function New-LocalText([string]$Path, [string]$Text) {
    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    Set-Content -LiteralPath $Path -Value $Text -Encoding UTF8
}

function Get-LocalText([string]$Path) { return (Get-Content -LiteralPath $Path -Raw) }

# --- Pair lifecycle ---------------------------------------------------------

function New-Layout([string]$Name) {
    $suffix = New-Suffix
    $local = Join-Path $LocalBase ("{0}-{1}-{2}" -f $Name, $stamp, $suffix)
    $remote = Join-Remote $RemoteBase ("{0}-{1}-{2}" -f $Name, $stamp, $suffix)
    if (Test-Path -LiteralPath $local) { throw "Local scenario folder already exists: $local" }
    if (Test-RemoteExists -RemotePath $remote) { throw "Remote scenario folder already exists: $remote" }
    New-Item -ItemType Directory -Path $local | Out-Null
    New-RemoteFolder -RemotePath $remote
    return [pscustomobject]@{ Name = $Name; Local = $local; Remote = $remote; Id = $null }
}

function Add-Pair([object]$Layout, [string]$FirstSync = 'newest') {
    $output = Invoke-Cli @('pairs', 'add', '--name', $Layout.Name, '--local', $Layout.Local,
        '--path', $Layout.Remote, '--account', $script:Account, '--first-sync', $FirstSync)
    $id = Get-PairIdFromText $output
    if (-not $id) {
        $listed = Invoke-Cli @('pairs', 'list')
        $line = ($listed -split "`n" | Where-Object { $_ -match [regex]::Escape($Layout.Name) } | Select-Object -First 1)
        if ($line) { $id = Get-PairIdFromText $line }
    }
    if (-not $id) { throw "Could not read the new pair id for $($Layout.Name)" }
    $Layout.Id = $id
    Invoke-Cli @('pairs', 'bind', '--pair', $id, '--yes') | Out-Null
    return $id
}

function Repair-Pair([object]$Layout) {
    Invoke-Cli @('repair', '--pair', $Layout.Id) -TimeoutSeconds $script:RunTimeoutSeconds | Out-Null
}

function Get-Notices([object]$Layout) { return (Invoke-Cli @('notices', '--pair', $Layout.Id)) }

function Remove-Pair([object]$Layout) {
    if ($Layout.Id) {
        try { Invoke-Cli @('pairs', 'remove', '--pair', $Layout.Id) | Out-Null } catch { Add-Note "pair removal: $($_.Exception.Message)" }
    }
}

# --- Convergence assertions -------------------------------------------------

function Assert-LocalFile([object]$Layout, [string]$Relative, [string]$Expected) {
    $path = Join-Path $Layout.Local $Relative
    Assert-True (Test-Path -LiteralPath $path) "local file missing: $Relative"
    Assert-True ((Get-LocalText $path) -eq $Expected) "local content differs: $Relative"
}

function Assert-LocalAbsent([object]$Layout, [string]$Relative) {
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $Layout.Local $Relative))) "local file should be gone: $Relative"
}

function Assert-RemoteFile([object]$Layout, [string]$Relative, [string]$Expected) {
    $path = Join-Remote $Layout.Remote $Relative
    Assert-True (Test-RemoteExists -RemotePath $path) "remote file missing: $Relative"
    Assert-True ((Get-RemoteFileText $path) -eq $Expected) "remote content differs: $Relative"
}

function Assert-RemoteAbsent([object]$Layout, [string]$Relative) {
    Assert-True (-not (Test-RemoteExists -RemotePath (Join-Remote $Layout.Remote $Relative))) "remote file should be gone: $Relative"
}

function Assert-Converged([object]$Layout) {
    $localRoot = [System.IO.Path]::GetFullPath($Layout.Local)
    $local = Get-ChildItem -LiteralPath $localRoot -Recurse -File | ForEach-Object {
        $_.FullName.Substring($localRoot.Length).TrimStart('\', '/') -replace '\\', '/'
    } | Where-Object { $_ -notmatch '^Recycle Bin/' } | Sort-Object
    $remote = @(Get-RemoteRelativePaths -RemoteFolder $Layout.Remote | Where-Object { $_ -notmatch '^Recycle Bin/' } | Sort-Object)
    $diff = Compare-Object -ReferenceObject @($local) -DifferenceObject @($remote)
    Assert-True (-not $diff) ("both sides differ: " + (($diff | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join ', '))
}

# --- Scenarios --------------------------------------------------------------

function Invoke-FirstSyncNewestWins {
    $layout = New-Layout 'newest'
    try {
        Set-RemoteFile (Join-Remote $layout.Remote 'alpha/newer.txt') 'remote-older'
        Start-Sleep -Seconds 3
        New-LocalText (Join-Path $layout.Local 'alpha/newer.txt') 'local-newer'
        [void](Add-Pair -Layout $layout -FirstSync 'newest')
        Repair-Pair $layout
        Assert-LocalFile $layout 'alpha/newer.txt' 'local-newer'
        Assert-RemoteFile $layout 'alpha/newer.txt' 'local-newer'
        Assert-True ((Get-RemoteRevisionCount (Join-Remote $layout.Remote 'alpha/newer.txt')) -ge 1) 'the replaced version stays in Dropbox history'
        Assert-Converged $layout
        return 'first sync newest wins on both sides'
    } finally { Remove-Pair $layout }
}

function Invoke-OneSidedEdits {
    $layout = New-Layout 'edits'
    try {
        New-LocalText (Join-Path $layout.Local 'alpha/seed.txt') 'seed'
        [void](Add-Pair -Layout $layout)
        Repair-Pair $layout
        New-LocalText (Join-Path $layout.Local 'alpha/up.txt') 'local-edit'
        Repair-Pair $layout
        Assert-RemoteFile $layout 'alpha/up.txt' 'local-edit'
        Set-RemoteFile (Join-Remote $layout.Remote 'alpha/down.txt') 'remote-edit'
        Repair-Pair $layout
        Assert-LocalFile $layout 'alpha/down.txt' 'remote-edit'
        Assert-Converged $layout
        return 'one-sided edits flow both directions'
    } finally { Remove-Pair $layout }
}

function Invoke-BothSidesEdited {
    $layout = New-Layout 'keepboth'
    try {
        New-LocalText (Join-Path $layout.Local 'beta/shared.txt') 'base'
        [void](Add-Pair -Layout $layout)
        Repair-Pair $layout
        New-LocalText (Join-Path $layout.Local 'beta/shared.txt') 'local-version'
        Set-RemoteFile (Join-Remote $layout.Remote 'beta/shared.txt') 'remote-version'
        Repair-Pair $layout
        $notices = Get-Notices $layout
        Assert-True ($notices -match '(?i)notice|tie|clash|keep both') 'a keep-both notice is raised'
        $names = @(Get-RemoteRelativePaths -RemoteFolder $layout.Remote | Where-Object { $_ -like 'beta/shared*' })
        Assert-True ($names.Count -ge 2) 'both versions exist on Dropbox'
        return 'edit-both-sides keeps both versions'
    } finally { Remove-Pair $layout }
}

function Invoke-EditVersusDelete {
    $layout = New-Layout 'editdelete'
    try {
        New-LocalText (Join-Path $layout.Local 'gamma/doc.txt') 'base'
        [void](Add-Pair -Layout $layout)
        Repair-Pair $layout
        New-LocalText (Join-Path $layout.Local 'gamma/doc.txt') 'edited'
        Remove-RemotePath (Join-Remote $layout.Remote 'gamma/doc.txt')
        Repair-Pair $layout
        Assert-LocalFile $layout 'gamma/doc.txt' 'edited'
        Assert-RemoteFile $layout 'gamma/doc.txt' 'edited'
        Assert-Converged $layout
        return 'the edit survives a remote delete'
    } finally { Remove-Pair $layout }
}

function Invoke-LocalMove {
    $layout = New-Layout 'move'
    try {
        New-LocalText (Join-Path $layout.Local 'alpha/draft.txt') 'move-me'
        [void](Add-Pair -Layout $layout)
        Repair-Pair $layout
        Move-Item -LiteralPath (Join-Path $layout.Local 'alpha/draft.txt') -Destination (Join-Path $layout.Local 'alpha/final.txt')
        Repair-Pair $layout
        Assert-LocalAbsent $layout 'alpha/draft.txt'
        Assert-RemoteFile $layout 'alpha/final.txt' 'move-me'
        Assert-RemoteAbsent $layout 'alpha/draft.txt'
        Assert-Converged $layout
        return 'a local rename propagates as a move'
    } finally { Remove-Pair $layout }
}

function Invoke-LocalDelete {
    $layout = New-Layout 'delete'
    try {
        New-LocalText (Join-Path $layout.Local 'alpha/gone.txt') 'delete-me'
        New-LocalText (Join-Path $layout.Local 'alpha/stay.txt') 'stay'
        [void](Add-Pair -Layout $layout)
        Repair-Pair $layout
        Remove-Item -LiteralPath (Join-Path $layout.Local 'alpha/gone.txt')
        Repair-Pair $layout
        Assert-RemoteAbsent $layout 'alpha/gone.txt'
        Assert-LocalFile $layout 'alpha/stay.txt' 'stay'
        Assert-RemoteFile $layout 'alpha/stay.txt' 'stay'
        Assert-Converged $layout
        return 'a local delete propagates and untouched files stay'
    } finally { Remove-Pair $layout }
}

function Invoke-LocalRootMissingNoDeletes {
    $layout = New-Layout 'rootmissing'
    $moved = "$($layout.Local)-moved"
    try {
        New-LocalText (Join-Path $layout.Local 'alpha/keep.txt') 'keep'
        [void](Add-Pair -Layout $layout)
        Repair-Pair $layout
        Rename-Item -LiteralPath $layout.Local -NewName (Split-Path -Leaf $moved)
        try {
            Repair-Pair $layout
            Assert-RemoteFile $layout 'alpha/keep.txt' 'keep'
        } finally {
            if (Test-Path -LiteralPath $moved) { Rename-Item -LiteralPath $moved -NewName (Split-Path -Leaf $layout.Local) }
        }
        Assert-LocalFile $layout 'alpha/keep.txt' 'keep'
        Assert-RemoteFile $layout 'alpha/keep.txt' 'keep'
        return 'a missing local root causes no deletes'
    } finally { Remove-Pair $layout }
}

function Invoke-FolderDeleteEditedChild {
    $layout = New-Layout 'folderdelete'
    try {
        New-LocalText (Join-Path $layout.Local 'alpha/tree/plain.txt') 'plain'
        New-LocalText (Join-Path $layout.Local 'alpha/tree/edited.txt') 'before'
        [void](Add-Pair -Layout $layout)
        Repair-Pair $layout
        Set-RemoteFile (Join-Remote $layout.Remote 'alpha/tree/edited.txt') 'edited-remotely'
        Remove-Item -LiteralPath (Join-Path $layout.Local 'alpha/tree') -Recurse -Force
        Repair-Pair $layout
        Assert-LocalFile $layout 'alpha/tree/edited.txt' 'edited-remotely'
        Assert-RemoteAbsent $layout 'alpha/tree/plain.txt'
        Assert-Converged $layout
        return 'an edited child survives a folder delete'
    } finally { Remove-Pair $layout }
}

# A47: reads never start the host; logs read files host-down; pause and resume work.
function Invoke-HostReadsAndPause {
    $layout = New-Layout 'host'
    try {
        New-LocalText (Join-Path $layout.Local 'alpha/f.txt') 'x'
        [void](Add-Pair -Layout $layout)
        Invoke-Cli @('host', 'start') | Out-Null
        Repair-Pair $layout
        Invoke-Cli @('host', 'stop') | Out-Null
        $status = Invoke-Cli @('status', '--pair', $layout.Id)
        Assert-True ($status -match '(?i)not running|stopped|unavailable') 'a host-down read reports the host is not running'
        $runId = Get-LatestRunId $layout.Id
        if ($runId) {
            $logs = Invoke-Cli @('logs', '--pair', $layout.Id, '--run', $runId)
            Assert-True ($null -ne $logs) 'logs still reads the current files host-down'
        } else {
            Add-Note 'no stored run id found for the host-down logs read'
        }
        Invoke-Cli @('pause', '--pair', $layout.Id) | Out-Null
        Invoke-Cli @('host', 'start') | Out-Null
        $paused = Invoke-Cli @('status', '--pair', $layout.Id)
        Assert-True ($paused -match '(?i)paus') 'pause is reported by status'
        Invoke-Cli @('resume', '--pair', $layout.Id) | Out-Null
        Invoke-Cli @('host', 'stop') | Out-Null
        return 'host-down reads never start the host; pause and resume work'
    } finally { Remove-Pair $layout }
}

# A49: the current file is readable while the host writes it; per-file lines need --show-files.
function Invoke-LogsReadableAndPerFileSwitch {
    $layout = New-Layout 'logs'
    try {
        New-LocalText (Join-Path $layout.Local 'alpha/one.txt') 'one'
        New-LocalText (Join-Path $layout.Local 'alpha/two.txt') 'two'
        [void](Add-Pair -Layout $layout)
        Invoke-Cli @('host', 'start') | Out-Null
        Repair-Pair $layout
        $runId = Get-LatestRunId $layout.Id
        Assert-True ($null -ne $runId) 'the run wrote a log file the reader can find while the host runs'
        $plain = Invoke-Cli @('logs', '--pair', $layout.Id, '--run', $runId)
        $withFiles = Invoke-Cli @('logs', '--pair', $layout.Id, '--run', $runId, '--show-files')
        Assert-True ($plain -notmatch '(?m)^Effect\b') 'individual file operations are off by default'
        Assert-True ($withFiles -match '(?m)^Effect\b') 'individual file operations appear with --show-files'
        Invoke-Cli @('host', 'stop') | Out-Null
        return 'logs are readable while running and per-file lines are opt-in'
    } finally { Remove-Pair $layout }
}

# F111: the registered A48 arm must test only non-removable native-bin volumes. A removable volume
# that also has a Windows Recycle Bin must use the engine's own root Recycle Bin (R4).
function Invoke-RemovableBin {
    $layout = New-Layout 'bin'
    try {
        $driveRoot = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($layout.Local))
        $type = (New-Object System.IO.DriveInfo($driveRoot)).DriveType
        $hasWindowsBin = Test-Path -LiteralPath (Join-Path $driveRoot '$Recycle.Bin')
        if ($type -ne [System.IO.DriveType]::Removable) {
            Add-Note "F111 arm not applicable: $driveRoot is $type, not Removable; rerun from a removable volume that also has a Windows bin."
            return 'F111 removable-with-Windows-bin arm not applicable on this volume'
        }
        New-LocalText (Join-Path $layout.Local 'alpha/bin.txt') 'bin-me'
        [void](Add-Pair -Layout $layout)
        Repair-Pair $layout
        Remove-Item -LiteralPath (Join-Path $layout.Local 'alpha/bin.txt')
        Repair-Pair $layout
        Assert-RemoteAbsent $layout 'alpha/bin.txt'
        $rootBin = Join-Path $layout.Local 'Recycle Bin'
        Assert-True (Test-Path -LiteralPath $rootBin) 'a removable volume gets the engine root Recycle Bin'
        $binned = @(Get-ChildItem -LiteralPath $rootBin -Recurse -File -ErrorAction SilentlyContinue)
        Assert-True ($binned.Count -ge 1) 'the removed file is retained in the engine root bin'
        if ($hasWindowsBin) { Add-Note 'the removable volume also has a Windows bin; the engine still used its own root bin (R4).' }
        return 'R4: a removable volume uses the engine root bin, not the Windows bin'
    } finally { Remove-Pair $layout }
}

# --- Selection and reporting -----------------------------------------------

$script:Scenarios = [ordered]@{
    'FirstSyncNewestWins' = 'Invoke-FirstSyncNewestWins'
    'OneSidedEdits' = 'Invoke-OneSidedEdits'
    'BothSidesEdited' = 'Invoke-BothSidesEdited'
    'EditVersusDelete' = 'Invoke-EditVersusDelete'
    'LocalMove' = 'Invoke-LocalMove'
    'LocalDelete' = 'Invoke-LocalDelete'
    'LocalRootMissingNoDeletes' = 'Invoke-LocalRootMissingNoDeletes'
    'FolderDeleteEditedChild' = 'Invoke-FolderDeleteEditedChild'
    'HostReadsAndPause' = 'Invoke-HostReadsAndPause'
    'LogsReadableAndPerFileSwitch' = 'Invoke-LogsReadableAndPerFileSwitch'
    'BinRemovableWithWindowsBin' = 'Invoke-RemovableBin'
}

function Resolve-Scenarios([string]$Selection) {
    if ($Selection -eq 'all') { return @($script:Scenarios.Keys) }
    return @($script:Scenarios.Keys | Where-Object { $_ -eq $Selection -or $_.IndexOf($Selection, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 })
}

$selected = Resolve-Scenarios $Scenario
if (-not $selected) { throw "No scenario matches '$Scenario'" }

Write-Host 'Sync Shark live acceptance (new CLI)'
Write-Host "  exe:        $script:ExePath"
Write-Host "  local base: $LocalBase"
Write-Host "  remote:     $RemoteBase"
Write-Host "  account:    $Account"
Write-Host "  reports:    $script:ReportRoot"
Write-Host ""

$rows = New-Object System.Collections.Generic.List[object]
foreach ($name in $selected) {
    Write-Host "== $name"
    $started = Get-Date
    $status = 'PASS'
    $reason = ''
    try {
        $reason = & $script:Scenarios[$name]
    } catch {
        $status = 'FAIL'
        $reason = $_.Exception.Message
        $script:Failures++
    }
    $elapsed = (Get-Date) - $started
    if ($script:Notes.Count) { $reason = "$reason | " + ($script:Notes -join '; '); $script:Notes.Clear() }
    Write-Host ("   {0}  {1}  ({2:n1}s)" -f $status, $reason, $elapsed.TotalSeconds)
    $rows.Add([pscustomobject]@{ Scenario = $name; Status = $status; Reason = $reason; Seconds = [math]::Round($elapsed.TotalSeconds, 1) }) | Out-Null
}

Write-Host ''
$rows | Format-Table -AutoSize | Out-String | Write-Host
Write-Host ("failures: {0}" -f $script:Failures)
exit $script:Failures
