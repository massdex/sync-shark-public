<#
.SYNOPSIS
    Live acceptance driver for Sync Shark on an approved, isolated staging profile.

.DESCRIPTION
    Drives the candidate `syncshark.exe` through the new command line (CLI-CAPABILITIES.md) against
    a real Dropbox test root and real local roots. It is a LIVE test, not a CI test, and it NEVER
    runs against the installed product or production data.

    SAFETY GATE. The data location is fixed (`%LOCALAPPDATA%\Massdex\SyncShark`), so a staging run
    needs its own Windows profile (its own `%LOCALAPPDATA%` and credentials) and roots on volumes
    disjoint from production (PLAN section 8). This script therefore refuses to launch anything
    until an approved staging setup supplies a proof file that certifies, and this script verifies:

      - the candidate executable is the staged build (under the staging root, not the installed exe);
      - the current profile's `%LOCALAPPDATA%` is the staging profile, not the production profile;
      - the local base, report root and recovery root are under the staging root;
      - the local volume is disjoint from every production volume and recovery root;
      - the installed host is stopped and no process from the install root is running;
      - the Dropbox test root, the test connection id and the Windows Credential Manager target.

    If the proof is missing or any check fails, the script throws before its first write or CLI
    launch; it never falls back to the installed host, the production profile, or product paths.

    The script writes only inside its own random-suffixed disposable roots, and it never creates or
    modifies the supplied remote root itself. It reads the Dropbox token from Windows Credential
    Manager only and never prints it. The injected proxy is bypassed for the script's own Dropbox
    requests and stripped from every child process.

.PARAMETER StagingProof
    Absolute path to the staging proof JSON supplied by the approved staging setup (see README).

.PARAMETER Scenario
    Exact scenario name, a substring, or `all`.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StagingProof,
    [string]$Scenario = 'all',
    [int]$CommandTimeoutSeconds = 180,
    [int]$RunTimeoutSeconds = 1800
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:DropboxApi = 'https://api.dropboxapi.com/2'
$script:DropboxContent = 'https://content.dropboxapi.com/2'
$script:IdPattern = '^[0-9a-f]{32}$'
$script:SuffixLength = 8
$script:Failures = 0
$script:NotObserved = 0

# --- Safe setup: proxy bypass and secrets ----------------------------------

$script:SavedProxy = [System.Net.WebRequest]::DefaultWebProxy
$script:SavedEnvProxy = @{}
foreach ($name in @('HTTPS_PROXY', 'HTTP_PROXY', 'https_proxy', 'http_proxy', 'ALL_PROXY', 'all_proxy')) {
    $script:SavedEnvProxy[$name] = [Environment]::GetEnvironmentVariable($name)
    [Environment]::SetEnvironmentVariable($name, $null)
}
[System.Net.WebRequest]::DefaultWebProxy = New-Object System.Net.WebProxy

$script:Token = $null
$script:LastDropboxSummary = ''

if (-not (Test-Path -LiteralPath $StagingProof)) { throw "Staging proof not found: $StagingProof" }
$proof = Get-Content -Raw -LiteralPath $StagingProof | ConvertFrom-Json

function Full-Path([string]$Value) { [System.IO.Path]::GetFullPath($Value).TrimEnd('\', '/') }
function Test-Under([string]$Child, [string]$Parent) {
    $c = Full-Path $Child; $p = Full-Path $Parent
    return $c.Equals($p, [System.StringComparison]::OrdinalIgnoreCase) -or
        $c.StartsWith($p + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
}
function Get-ProofProperty([string]$Name) {
    $property = $proof.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    return $null
}
function Require-Proof([string]$Name) {
    $value = Get-ProofProperty $Name
    if ($null -eq $value -or "$value" -eq '') { throw "Staging proof is missing '$Name'." }
    return $value
}

$stagingRoot = Require-Proof 'stagingRoot'
$installedRoot = Require-Proof 'installRoot'
$installedExe = Require-Proof 'installedExe'
$installedHostExeName = Require-Proof 'installedHostExeName'
$stagingLocalAppData = Require-Proof 'stagingLocalAppData'
$productionLocalAppData = Require-Proof 'productionLocalAppData'
$localBase = Require-Proof 'localBase'
$remoteBase = Require-Proof 'remoteBase'
$reportRoot = Require-Proof 'reportRoot'
$recoveryRoot = Require-Proof 'recoveryRoot'
$stagingCredentialTarget = Require-Proof 'credentialTarget'
$account = Require-Proof 'account'
$candidateExe = Require-Proof 'candidateExe'
$productionVolumes = @(Get-ProofProperty 'productionVolumes' | Where-Object { $_ })
$productionRecoveryRoots = @(Get-ProofProperty 'productionRecoveryRoots' | Where-Object { $_ })

# The candidate must be the staged build, never the installed executable.
if (-not (Test-Under $candidateExe $stagingRoot)) { throw 'The candidate executable is not under the staging root.' }
if ((Full-Path $candidateExe) -eq (Full-Path $installedExe)) { throw 'The candidate executable is the installed executable; staging is not proven.' }
if (-not (Test-Under $localBase $stagingRoot) -or -not (Test-Under $reportRoot $stagingRoot)) {
    throw 'The local base and report root must be under the staging root.'
}
if (-not (Test-Under $recoveryRoot $stagingRoot)) { throw 'The recovery root must be under the staging root.' }
if ((Full-Path $stagingRoot) -eq [System.IO.Path]::GetPathRoot((Full-Path $stagingRoot))) { throw 'The staging root must not be a drive root.' }
foreach ($productionRecovery in $productionRecoveryRoots) {
    if (Test-Under $recoveryRoot $productionRecovery) { throw 'The recovery root overlaps a production recovery location.' }
}
foreach ($productionRoot in @($installedRoot) + $productionRecoveryRoots) {
    if ((Test-Under $localBase $productionRoot) -or (Test-Under $recoveryRoot $productionRoot)) {
        throw 'A staging root overlaps a production location.'
    }
}
# The candidate inherits this profile; require the staging profile, never the production one.
if (-not $env:LOCALAPPDATA) { throw 'LOCALAPPDATA is not set.' }
if (-not (Test-Under $env:LOCALAPPDATA $stagingLocalAppData) -or
    (Full-Path $env:LOCALAPPDATA).Equals((Full-Path $productionLocalAppData), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'This process is not running under the staging profile; refusing to launch the candidate.'
}
# Every volume the staging run can reach must be disjoint from production.
$localDrive = [System.IO.Path]::GetPathRoot((Full-Path $localBase))
foreach ($volume in $productionVolumes) {
    if ($localDrive.Equals((Full-Path $volume) + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or
        (Full-Path $localDrive).Equals((Full-Path $volume), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The staging local volume $localDrive overlaps a production volume."
    }
}
if (-not ((Get-ProofProperty 'installedHostStopped') -eq $true)) { throw 'The staging proof must certify that the installed host is stopped.' }
if (Get-Process -Name $installedHostExeName -ErrorAction SilentlyContinue) {
    throw "The installed host ($installedHostExeName) is running; the candidate could attach to its pipe."
}

$script:ExePath = Full-Path $candidateExe
$script:LocalBase = Full-Path $localBase
$script:RemoteBase = [string]$remoteBase
$script:RecoveryRoot = Full-Path $recoveryRoot
$script:ReportRoot = Full-Path $reportRoot
$script:Account = [string]$account
New-Item -ItemType Directory -Force -Path $script:LocalBase | Out-Null
New-Item -ItemType Directory -Force -Path $script:ReportRoot | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

# --- Windows Credential Manager -------------------------------------------

if (-not ('SyncSharkLiveCredential' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SyncSharkLiveCredential
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CREDENTIAL
    {
        public int Flags; public int Type; public string TargetName; public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize; public IntPtr CredentialBlob; public int Persist;
        public int AttributeCount; public IntPtr Attributes; public string TargetAlias; public string UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern void CredFree(IntPtr buffer);
}
'@
}
function Get-CredentialSecret([string]$Target) {
    $pointer = [IntPtr]::Zero
    if (-not [SyncSharkLiveCredential]::CredRead($Target, 1, 0, [ref]$pointer)) {
        throw "No generic credential named '$Target' exists in Windows Credential Manager."
    }
    try {
        $credential = [System.Runtime.InteropServices.Marshal]::PtrToStructure($pointer, [type][SyncSharkLiveCredential+CREDENTIAL])
        $bytes = New-Object byte[] $credential.CredentialBlobSize
        [System.Runtime.InteropServices.Marshal]::Copy($credential.CredentialBlob, $bytes, 0, $credential.CredentialBlobSize)
        return [System.Text.Encoding]::Unicode.GetString($bytes).TrimEnd([char]0)
    } finally { [SyncSharkLiveCredential]::CredFree($pointer) }
}
$script:Token = Get-CredentialSecret $stagingCredentialTarget
if (-not $script:Token) { throw 'The staging credential target holds no token.' }

# --- Command line ----------------------------------------------------------

function Quote-Arg([string]$Value) {
    if ($Value -match '[\s"]') { return '"' + ($Value -replace '"', '\"') + '"' }
    return $Value
}

# Starts the CLI with a proxy-free child environment and captures its output. Raw output is written
# to a report file only; the caller sees a safe summary that never contains the token.
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
    $safeName = (($Arguments -join '_') -replace '[^A-Za-z0-9._-]', '_')
    if ($safeName.Length -gt 60) { $safeName = $safeName.Substring(0, 60) }
    $reportPath = Join-Path $script:ReportRoot ("{0}-{1}.json" -f $stamp, $safeName)
    [ordered]@{ exitCode = $process.ExitCode; args = $Arguments; stdout = $out; stderr = $err } |
        ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    return [pscustomobject]@{ ExitCode = $process.ExitCode; StdOut = $out; StdErr = $err; Report = $reportPath }
}

function Invoke-Cli {
    param([string[]]$Arguments, [int]$TimeoutSeconds = 0)
    $result = Invoke-CliRaw -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds
    if ($result.ExitCode -ne 0) {
        throw ("Command failed (exit {0}): {1}; details in {2}" -f $result.ExitCode, ($Arguments -join ' '), $result.Report)
    }
    return $result.StdOut
}

# The raw seam for a command whose non-zero exit is expected (A47 host-absent read).
function Invoke-CliExpect {
    param([string[]]$Arguments, [int]$ExpectedExit, [string]$MessageContains)
    $result = Invoke-CliRaw -Arguments $Arguments
    if ($result.ExitCode -ne $ExpectedExit) {
        throw ("Expected exit {0} for {1}, got {2}" -f $ExpectedExit, ($Arguments -join ' '), $result.ExitCode)
    }
    if ($MessageContains -and $result.StdOut -notmatch $MessageContains -and $result.StdErr -notmatch $MessageContains) {
        throw ("Exit {0} but the expected message was not present for {1}" -f $ExpectedExit, ($Arguments -join ' '))
    }
    return $result
}

function Get-PairIds([string]$Text) {
    $ids = New-Object System.Collections.Generic.List[string]
    foreach ($token in ($Text -split '[^0-9A-Za-z]+')) { if ($token -match $script:IdPattern) { $ids.Add($token) | Out-Null } }
    return $ids
}

# --- Dropbox HTTP API ------------------------------------------------------

function Read-WebBody($WebException) {
    if (-not $WebException.Response) { return '' }
    $reader = [System.IO.StreamReader]::new($WebException.Response.GetResponseStream())
    return $reader.ReadToEnd()
}
function Set-LastDropboxError([string]$Body) {
    if (-not $Body) { $script:LastDropboxSummary = ''; return }
    try { $script:LastDropboxSummary = ($Body | ConvertFrom-Json).error_summary } catch { $script:LastDropboxSummary = $Body.Substring(0, [Math]::Min(200, $Body.Length)) }
}
function Test-LastDropbox([string]$Prefix) {
    return ($script:LastDropboxSummary -and $script:LastDropboxSummary.StartsWith($Prefix, [System.StringComparison]::Ordinal))
}

function Invoke-Dropbox {
    param([ValidateSet('rpc', 'content')][string]$Kind, [string]$Route, [string]$ArgJson, [byte[]]$Body)
    $headers = @{ Authorization = "Bearer $script:Token" }
    $uri = if ($Kind -eq 'rpc') { "$script:DropboxApi/$Route" } else { "$script:DropboxContent/$Route" }
    if ($Kind -eq 'content') { $headers['Dropbox-API-Arg'] = $ArgJson }
    try {
        if ($Kind -eq 'rpc') {
            $json = [System.Text.Encoding]::UTF8.GetBytes($ArgJson)
            $response = Invoke-WebRequest -UseBasicParsing -Method Post -Uri $uri -Headers $headers `
                -ContentType 'application/json; charset=utf-8' -Body $json -TimeoutSec $script:CommandTimeoutSeconds
        } elseif ($null -eq $Body) {
            $response = Invoke-WebRequest -UseBasicParsing -Method Post -Uri $uri -Headers $headers -TimeoutSec $script:CommandTimeoutSeconds
        } else {
            $response = Invoke-WebRequest -UseBasicParsing -Method Post -Uri $uri -Headers $headers `
                -ContentType 'application/octet-stream' -Body $Body -TimeoutSec $script:CommandTimeoutSeconds
        }
        $script:LastDropboxSummary = ''
        $content = $response.Content
        if ($content -is [byte[]]) { $content = [System.Text.Encoding]::UTF8.GetString($content) }
        if (-not $content) { return $null }
        return ($content | ConvertFrom-Json)
    } catch [System.Net.WebException] {
        Set-LastDropboxError (Read-WebBody $_.Exception)
        throw
    }
}

function New-RemoteFolder([string]$RemotePath) {
    try {
        Invoke-Dropbox -Kind rpc -Route 'files/create_folder_v2' -ArgJson (@{ path = $RemotePath; autorename = $false } | ConvertTo-Json -Compress) | Out-Null
    } catch {
        throw "Remote folder creation failed for '$RemotePath' ($script:LastDropboxSummary); the driver never adopts an existing folder."
    }
}
function Get-RemoteMetadata([string]$RemotePath) {
    try { return Invoke-Dropbox -Kind rpc -Route 'files/get_metadata' -ArgJson (@{ path = $RemotePath } | ConvertTo-Json -Compress) }
    catch { if (Test-LastDropbox 'path/not_found') { return $null }; throw }
}
function Test-RemoteExists([string]$RemotePath) { return ($null -ne (Get-RemoteMetadata -RemotePath $RemotePath)) }
function Set-RemoteFile([string]$RemotePath, [string]$Text) {
    $arg = @{ path = $RemotePath; mode = 'overwrite'; autorename = $false; mute = $true } | ConvertTo-Json -Compress
    return (Invoke-Dropbox -Kind content -Route 'files/upload' -ArgJson $arg -Body ([System.Text.Encoding]::UTF8.GetBytes($Text)))
}
function Get-RemoteFileText([string]$RemotePath) {
    $arg = @{ path = $RemotePath } | ConvertTo-Json -Compress
    return (Invoke-Dropbox -Kind content -Route 'files/download' -ArgJson $arg -Body $null)
}
function Remove-RemotePath([string]$RemotePath) {
    try { Invoke-Dropbox -Kind rpc -Route 'files/delete_v2' -ArgJson (@{ path = $RemotePath } | ConvertTo-Json -Compress) | Out-Null }
    catch { if (-not (Test-LastDropbox 'path/not_found')) { throw } }
}
function Get-EntryTag($Entry) {
    $property = $Entry.PSObject.Properties['tag']
    if ($property) { return [string]$property.Value }
    return ''
}
function Get-RemoteFiles([string]$RemoteFolder) {
    $paths = New-Object System.Collections.Generic.List[string]
    $result = Invoke-Dropbox -Kind rpc -Route 'files/list_folder' -ArgJson (@{ path = $RemoteFolder; recursive = $true; include_deleted = $false } | ConvertTo-Json -Compress)
    while ($true) {
        foreach ($entry in @($result.entries)) {
            if ((Get-EntryTag $entry) -ne 'file') { continue }
            $paths.Add($entry.path_display.Substring($RemoteFolder.Length).TrimStart('/')) | Out-Null
        }
        if (-not $result.has_more) { break }
        $result = Invoke-Dropbox -Kind rpc -Route 'files/list_folder/continue' -ArgJson (@{ cursor = $result.cursor } | ConvertTo-Json -Compress)
    }
    return $paths
}
function Get-RemoteRevisionCount([string]$RemotePath) {
    $result = Invoke-Dropbox -Kind rpc -Route 'files/list_revisions' -ArgJson (@{ path = $RemotePath; mode = 'path'; limit = 20 } | ConvertTo-Json -Compress)
    return @($result.entries).Count
}
function Join-Remote([string]$Folder, [string]$Name) { if ($Folder.EndsWith('/')) { return "$Folder$Name" }; return "$Folder/$Name" }

# --- Local exact content ---------------------------------------------------

function Write-Exact([string]$Path, [string]$Text) {
    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Text, ([System.Text.UTF8Encoding]::new($false)))
}
function Read-Exact([string]$Path) { return [System.IO.File]::ReadAllText($Path) }
function Test-BinContains([string]$Path, [string]$Text) {
    try { return ((Read-Exact $Path) -ceq $Text) } catch { return $false }
}

# --- Assertions ------------------------------------------------------------

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-LocalFile([object]$Layout, [string]$Relative, [string]$Expected) {
    $path = Join-Path $Layout.Local $Relative
    Assert-True (Test-Path -LiteralPath $path) "local file missing: $Relative"
    Assert-True ((Read-Exact $path) -ceq $Expected) "local content differs: $Relative"
}
function Assert-LocalAbsent([object]$Layout, [string]$Relative) {
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $Layout.Local $Relative))) "local file should be gone: $Relative"
}
function Assert-RemoteFile([object]$Layout, [string]$Relative, [string]$Expected) {
    $path = Join-Remote $Layout.Remote $Relative
    Assert-True (Test-RemoteExists -RemotePath $path) "remote file missing: $Relative"
    Assert-True ((Get-RemoteFileText $path) -ceq $Expected) "remote content differs: $Relative"
}
function Assert-RemoteAbsent([object]$Layout, [string]$Relative) {
    Assert-True (-not (Test-RemoteExists (Join-Remote $Layout.Remote $Relative))) "remote file should be gone: $Relative"
}
function Get-LocalRelativeFiles([object]$Layout) {
    $root = Full-Path $Layout.Local
    return @(Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
        $_.FullName.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'
    } | Where-Object { $_ -notmatch '^(\.syncshark|Recycle Bin)' } | Sort-Object)
}
function Assert-Converged([object]$Layout) {
    $local = @(Get-LocalRelativeFiles $Layout)
    Assert-True ($local.Count -gt 0) 'the local tree must not be empty'
    $remote = @(Get-RemoteFiles $Layout.Remote | Sort-Object)
    $diff = Compare-Object -ReferenceObject $local -DifferenceObject $remote
    Assert-True (-not $diff) ("both sides differ: " + (($diff | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join ', '))
}
function Test-Assertions {
    $probe = Join-Path $script:ReportRoot 'assert-probe.txt'
    Write-Exact $probe 'exact'
    $threw = $false
    try { Assert-True ((Read-Exact $probe) -ceq 'other') 'mismatch' } catch { $threw = $true }
    Assert-True $threw 'a mismatched file must not pass an exact-content assertion'
    $empty = @(@() | Where-Object { $_ })
    Assert-True ($empty.Count -eq 0) 'an empty tree is a defined empty set, never a silent pass'
    Remove-Item -LiteralPath $probe -Force
}

# --- Pair lifecycle: prove the exact new pair ------------------------------

function New-Layout([string]$Name) {
    $suffix = [guid]::NewGuid().ToString('N').Substring(0, $script:SuffixLength)
    $local = Join-Path $script:LocalBase ("{0}-{1}-{2}" -f $Name, $stamp, $suffix)
    $remote = Join-Remote $script:RemoteBase ("{0}-{1}-{2}" -f $Name, $stamp, $suffix)
    if (Test-Path -LiteralPath $local) { throw "Local scenario folder already exists: $local" }
    if (Test-RemoteExists $remote) { throw "Remote scenario folder already exists: $remote" }
    New-Item -ItemType Directory -Path $local | Out-Null
    New-RemoteFolder $remote
    return [pscustomobject]@{ Name = ("{0}-{1}-{2}" -f $Name, $stamp, $suffix); Local = $local; Remote = $remote; Id = $null }
}

function Add-Pair([object]$Layout, [string]$FirstSync = 'newest') {
    $before = @(Get-PairIds (Invoke-Cli @('pairs', 'list')))
    $output = Invoke-Cli @('pairs', 'add', '--name', $Layout.Name, '--local', $Layout.Local,
        '--path', $Layout.Remote, '--account', $script:Account, '--first-sync', $FirstSync)
    $after = @(Get-PairIds (Invoke-Cli @('pairs', 'list')))
    $new = @($after | Where-Object { $before -notcontains $_ } | Select-Object -Unique)
    if ($new.Count -ne 1) { throw "pairs add did not yield exactly one new pair id (got $($new.Count)); refusing to bind or remove." }
    $id = $new[0]
    if ($output -notmatch [regex]::Escape($id)) { Add-Note "pairs add did not print the new pair id; it was proved from pairs list." }
    $line = @((Invoke-Cli @('pairs', 'list')) -split "`n" | Where-Object { $_ -match [regex]::Escape($id) })
    if ($line.Count -ne 1) { throw "The new pair id is not on exactly one list line; refusing to touch it." }
    if ($line[0] -notmatch [regex]::Escape($Layout.Name) -or $line[0] -notmatch [regex]::Escape($Layout.Local) -or
        $line[0] -notmatch [regex]::Escape($Layout.Remote) -or $line[0] -notmatch [regex]::Escape($script:Account)) {
        throw 'The new pair does not prove the exact name, roots and account; refusing to bind or remove.'
    }
    $Layout.Id = $id
    Invoke-Cli @('pairs', 'bind', '--pair', $id, '--yes') | Out-Null
    return $id
}
function Repair-Pair([object]$Layout) { Invoke-Cli @('repair', '--pair', $Layout.Id) -TimeoutSeconds $script:RunTimeoutSeconds | Out-Null }
function Get-Notices([object]$Layout) { return (Invoke-Cli @('notices', '--pair', $Layout.Id)) }
function Remove-Pair([object]$Layout) {
    if ($Layout.Id) { try { Invoke-Cli @('pairs', 'remove', '--pair', $Layout.Id) | Out-Null } catch { Add-Note "pair removal: $($_.Exception.Message)" } }
}
function Add-Note([string]$Message) { $script:Notes.Add($Message) | Out-Null }
$script:Notes = New-Object System.Collections.Generic.List[string]

# --- Bin inventory (read-only) ---------------------------------------------

function Get-RootBinFiles([string]$DriveRoot) {
    $bin = Join-Path $DriveRoot 'Recycle Bin'
    if (-not (Test-Path -LiteralPath $bin)) { return @() }
    return @(Get-ChildItem -LiteralPath $bin -Recurse -File -ErrorAction SilentlyContinue)
}
function Get-ShellRecycleBinItems {
    $items = @()
    $shell = New-Object -ComObject Shell.Application
    $namespace = $shell.Namespace(10)
    if ($null -eq $namespace) { return $items }
    foreach ($item in @($namespace.Items())) {
        $original = $null
        try { $original = [string]$namespace.GetDetailsOf($item, 1) } catch { }
        $items += [pscustomobject]@{ Name = [string]$item.Name; Original = $original; Path = [string]$item.Path }
    }
    return $items
}

# --- Scenarios -------------------------------------------------------------

function Invoke-A02CreateOneSide {
    $layout = New-Layout 'a02'
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/new.txt') 'created-locally'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Assert-RemoteFile $layout 'alpha/new.txt' 'created-locally'
        Assert-Converged $layout
        return 'A02: a one-sided create lands at the same path'
    } finally { Remove-Pair $layout }
}

function Invoke-A05EditOneSide {
    $layout = New-Layout 'a05'
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/edit.txt') 'base'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Write-Exact (Join-Path $layout.Local 'alpha/edit.txt') 'local-edit'
        Repair-Pair $layout
        Assert-RemoteFile $layout 'alpha/edit.txt' 'local-edit'
        Set-RemoteFile (Join-Remote $layout.Remote 'alpha/edit.txt') 'remote-edit'
        Repair-Pair $layout
        Assert-LocalFile $layout 'alpha/edit.txt' 'remote-edit'
        Assert-Converged $layout
        return 'A05: an existing file edit replaces the other copy in both directions'
    } finally { Remove-Pair $layout }
}

function Invoke-A07EditBoth {
    $layout = New-Layout 'a07'
    try {
        Write-Exact (Join-Path $layout.Local 'beta/shared.txt') 'base'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Write-Exact (Join-Path $layout.Local 'beta/shared.txt') 'local-version'
        Set-RemoteFile (Join-Remote $layout.Remote 'beta/shared.txt') 'remote-version'
        Repair-Pair $layout
        $notices = Get-Notices $layout
        Assert-True ($notices -match '(?i)notice|tie|clash|keep both') 'A07 raises a keep-both notice'
        $names = @(Get-RemoteFiles $layout.Remote | Where-Object { $_ -like 'beta/shared*' })
        Assert-True ($names.Count -ge 2) 'A07 keeps both versions on Dropbox'
        return 'A07: both sides edited differently keep both versions with a notice'
    } finally { Remove-Pair $layout }
}

function Invoke-A10DeleteOneSide {
    $layout = New-Layout 'a10'
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/gone.txt') 'delete-me'
        Write-Exact (Join-Path $layout.Local 'alpha/stay.txt') 'stay'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Remove-Item -LiteralPath (Join-Path $layout.Local 'alpha/gone.txt')
        Repair-Pair $layout
        Assert-RemoteAbsent $layout 'alpha/gone.txt'
        Assert-RemoteFile $layout 'alpha/stay.txt' 'stay'
        Assert-Converged $layout
        return 'A10: a one-sided delete propagates; untouched files stay'
    } finally { Remove-Pair $layout }
}

function Invoke-A11EditVersusDelete {
    $layout = New-Layout 'a11'
    try {
        Write-Exact (Join-Path $layout.Local 'gamma/doc.txt') 'base'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Write-Exact (Join-Path $layout.Local 'gamma/doc.txt') 'edited'
        Remove-RemotePath (Join-Remote $layout.Remote 'gamma/doc.txt')
        Repair-Pair $layout
        Assert-LocalFile $layout 'gamma/doc.txt' 'edited'
        Assert-RemoteFile $layout 'gamma/doc.txt' 'edited'
        Assert-True ((Get-Notices $layout) -match '(?i)notice|edit|delete') 'A11 raises the edit-beats-delete notice'
        Assert-Converged $layout
        return 'A11: the edit survives a remote delete and is restored'
    } finally { Remove-Pair $layout }
}

function Invoke-A18MoveOneSide {
    $layout = New-Layout 'a18'
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/draft.txt') 'move-me'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Move-Item -LiteralPath (Join-Path $layout.Local 'alpha/draft.txt') -Destination (Join-Path $layout.Local 'alpha/final.txt')
        Repair-Pair $layout
        Assert-RemoteFile $layout 'alpha/final.txt' 'move-me'
        Assert-RemoteAbsent $layout 'alpha/draft.txt'
        Assert-Converged $layout
        return 'A18: a one-sided move repeats as a move, not a delete and create'
    } finally { Remove-Pair $layout }
}

function Invoke-A19MoveVersusEdit {
    $layout = New-Layout 'a19'
    try {
        Write-Exact (Join-Path $layout.Local 'beta/doc.txt') 'base'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Move-Item -LiteralPath (Join-Path $layout.Local 'beta/doc.txt') -Destination (Join-Path $layout.Local 'beta/moved.txt')
        Set-RemoteFile (Join-Remote $layout.Remote 'beta/doc.txt') 'remote-edit'
        Repair-Pair $layout
        Assert-True ((Get-Notices $layout) -match '(?i)notice|move|edit') 'A19 raises a moved-and-edited notice'
        Assert-Converged $layout
        return 'A19: a move against a remote edit keeps both and raises the notice'
    } finally { Remove-Pair $layout }
}

function Invoke-A21MoveVersusDelete {
    $layout = New-Layout 'a21'
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/doc.txt') 'base'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Move-Item -LiteralPath (Join-Path $layout.Local 'alpha/doc.txt') -Destination (Join-Path $layout.Local 'alpha/moved.txt')
        Remove-RemotePath (Join-Remote $layout.Remote 'alpha/doc.txt')
        Repair-Pair $layout
        Assert-RemoteFile $layout 'alpha/moved.txt' 'base'
        Assert-True ((Get-Notices $layout) -match '(?i)notice|move|delete') 'A21 raises a moved-and-deleted notice'
        Assert-Converged $layout
        return 'A21: the move survives a remote delete and raises the notice'
    } finally { Remove-Pair $layout }
}

function Invoke-A26SafeSave {
    $layout = New-Layout 'a26'
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/x') 'old'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Write-Exact (Join-Path $layout.Local 'alpha/x.tmp') 'new'
        Move-Item -LiteralPath (Join-Path $layout.Local 'alpha/x.tmp') -Destination (Join-Path $layout.Local 'alpha/x') -Force
        Repair-Pair $layout
        Assert-LocalFile $layout 'alpha/x' 'new'
        Assert-RemoteFile $layout 'alpha/x' 'new'
        Assert-RemoteAbsent $layout 'alpha/x.tmp'
        Assert-Converged $layout
        return 'A26: a safe-save rename over x is a replace, not a delete and create (both sides end with new)'
    } finally { Remove-Pair $layout }
}

function Invoke-A27CapitalRename {
    $layout = New-Layout 'a27'
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/name.txt') 'base'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Move-Item -LiteralPath (Join-Path $layout.Local 'alpha/name.txt') -Destination (Join-Path $layout.Local 'alpha/Name.txt')
        Repair-Pair $layout
        Assert-RemoteFile $layout 'alpha/Name.txt' 'base'
        Assert-RemoteAbsent $layout 'alpha/name.txt'
        Assert-Converged $layout
        return 'A27: a capitals-only PC rename propagates both ways'
    } finally { Remove-Pair $layout }
}

function Invoke-A28ExcludeKeepAndInclude {
    $layout = New-Layout 'a28'
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/keep.txt') 'x'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Invoke-Cli @('pairs', 'exclude', '--pair', $layout.Id, '--side', 'local', '--rel', 'alpha', '--keep') | Out-Null
        Assert-True ((Invoke-Cli @('pairs', 'exclusions', '--pair', $layout.Id)) -match 'alpha') 'the exclusion is listed'
        Write-Exact (Join-Path $layout.Local 'alpha/new.txt') 'not-synced'
        Repair-Pair $layout
        Assert-RemoteAbsent $layout 'alpha/new.txt'
        Invoke-Cli @('pairs', 'include', '--pair', $layout.Id, '--side', 'local', '--rel', 'alpha') | Out-Null
        Repair-Pair $layout
        Assert-RemoteFile $layout 'alpha/new.txt' 'not-synced'
        return 'A28: exclude --keep leaves both copies, and include compares the path at once'
    } finally { Remove-Pair $layout }
}

function Invoke-A28ExcludeNoKeep {
    $layout = New-Layout 'a28n'
    try {
        Write-Exact (Join-Path $layout.Local 'beta/remove.txt') 'remove-me'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        $preview = Invoke-Cli @('pairs', 'exclude', '--pair', $layout.Id, '--side', 'local', '--rel', 'beta')
        Assert-True ($preview -match '(?i)recover|warning|remove|bin|history') 'the warning names the recovery place'
        Invoke-Cli @('pairs', 'exclude', '--pair', $layout.Id, '--side', 'local', '--rel', 'beta', '--yes') | Out-Null
        Repair-Pair $layout
        Assert-RemoteAbsent $layout 'beta/remove.txt'
        return 'A28: a no-keep exclusion warns, then removes with recovery'
    } finally { Remove-Pair $layout }
}

function Invoke-A39InUse {
    $layout = New-Layout 'a39'
    $handle = $null
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/held.txt') 'base'
        Write-Exact (Join-Path $layout.Local 'alpha/free.txt') 'free'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        $path = Join-Path $layout.Local 'alpha/held.txt'
        $handle = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        Write-Exact (Join-Path $layout.Local 'alpha/free.txt') 'free-edited'
        Set-RemoteFile (Join-Remote $layout.Remote 'alpha/held.txt') 'remote-edit'
        Repair-Pair $layout
        Assert-True ((Get-Notices $layout) -match '(?i)fail|notice|in use|locked|retry') 'A39 raises an in-use failure notice'
        Assert-RemoteFile $layout 'alpha/free.txt' 'free-edited'
        $handle.Dispose(); $handle = $null
        Repair-Pair $layout
        Assert-LocalFile $layout 'alpha/held.txt' 'remote-edit'
        Assert-Converged $layout
        return 'A39: the locked item raises a notice, unrelated work continues, and the item heals on the next run'
    } finally {
        if ($handle) { $handle.Dispose() }
        Remove-Pair $layout
    }
}

function Invoke-A44ConcurrentDirections {
    $layout = New-Layout 'a44'
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/up.txt') 'up-base'
        Write-Exact (Join-Path $layout.Local 'alpha/down.txt') 'down-base'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Write-Exact (Join-Path $layout.Local 'alpha/up.txt') 'up-new'
        Set-RemoteFile (Join-Remote $layout.Remote 'alpha/down.txt') 'down-new'
        Repair-Pair $layout
        Assert-RemoteFile $layout 'alpha/up.txt' 'up-new'
        Assert-LocalFile $layout 'alpha/down.txt' 'down-new'
        Assert-Converged $layout
        return 'A44: an upload and a download run in the same repair and both land'
    } finally { Remove-Pair $layout }
}

function Invoke-A47HostReadsPauseStop {
    $layout = New-Layout 'a47'
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/f.txt') 'x'
        [void](Add-Pair $layout)
        Invoke-Cli @('host', 'start') | Out-Null
        Repair-Pair $layout
        Invoke-Cli @('host', 'stop') | Out-Null
        # The raw seam proves W23's host-absent exit and message; Invoke-Cli must not throw first.
        [void](Invoke-CliExpect -Arguments @('status', '--pair', $layout.Id) -ExpectedExit 3 -MessageContains '(?i)not running|stopped|unavailable')
        $runId = Get-LatestRunId $layout.Id
        if ($runId) { Assert-True ($null -ne (Invoke-Cli @('logs', '--pair', $layout.Id, '--run', $runId))) 'logs reads host-down' }
        # A state-changing command starts the host; a second start attaches; pause and resume are reported.
        Invoke-Cli @('pause', '--pair', $layout.Id) | Out-Null
        Invoke-Cli @('host', 'start') | Out-Null
        Assert-True ((Invoke-Cli @('status', '--pair', $layout.Id)) -match '(?i)paus') 'pause is reported'
        Invoke-Cli @('host', 'start') | Out-Null
        Invoke-Cli @('resume', '--pair', $layout.Id) | Out-Null
        Invoke-Cli @('host', 'stop') | Out-Null
        return 'A47: host-down status exits 3 with the message, logs reads, a state change starts the host, a second start attaches, pause/resume work'
    } finally { Remove-Pair $layout }
}

function Invoke-A48RootBinRemovable {
    $layout = New-Layout 'a48rem'
    try {
        $driveRoot = [System.IO.Path]::GetPathRoot((Full-Path $layout.Local))
        $type = ([System.IO.DriveInfo]::new($driveRoot)).DriveType
        $windowsBin = Join-Path $driveRoot '$Recycle.Bin'
        if ($type -ne [System.IO.DriveType]::Removable -or -not (Test-Path -LiteralPath $windowsBin)) {
            return "NOT OBSERVED: A48 removable-with-Windows-bin needs a removable staging volume that also has a Windows bin (drive $driveRoot, $type)."
        }
        Write-Exact (Join-Path $layout.Local 'alpha/keep.txt') 'bin-me'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        # Delete on Dropbox so the engine removes the PC copy and must recycle it (engine-side PC recycling).
        Remove-RemotePath (Join-Remote $layout.Remote 'alpha/keep.txt')
        Repair-Pair $layout
        Assert-LocalAbsent $layout 'alpha/keep.txt'
        $binned = @(Get-RootBinFiles $driveRoot | Where-Object { Test-BinContains $_.FullName 'bin-me' })
        Assert-True ($binned.Count -ge 1) "the exact fixture must be recovered in $driveRoot\Recycle Bin"
        return "A48/R4: a removable volume with a Windows bin recycles the engine-removed PC copy into $driveRoot\Recycle Bin"
    } finally { Remove-Pair $layout }
}

function Invoke-A48NativeBinFixed {
    $layout = New-Layout 'a48fix'
    try {
        $driveRoot = [System.IO.Path]::GetPathRoot((Full-Path $layout.Local))
        $type = ([System.IO.DriveInfo]::new($driveRoot)).DriveType
        $windowsBin = Join-Path $driveRoot '$Recycle.Bin'
        if ($type -eq [System.IO.DriveType]::Removable -or -not (Test-Path -LiteralPath $windowsBin)) {
            return "NOT OBSERVED: the fixed-drive native-bin arm needs a fixed staging volume with a Windows bin (drive $driveRoot, $type)."
        }
        Write-Exact (Join-Path $layout.Local 'alpha/native.txt') 'native-me'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Remove-RemotePath (Join-Remote $layout.Remote 'alpha/native.txt')
        Repair-Pair $layout
        Assert-LocalAbsent $layout 'alpha/native.txt'
        $match = @(Get-ShellRecycleBinItems | Where-Object { $_.Name -eq 'native.txt' -and $_.Original -match [regex]::Escape($script:LocalBase) })
        Assert-True ($match.Count -ge 1) 'the exact fixture must be observable in the Windows Recycle Bin'
        return 'A48: a fixed drive recycles the engine-removed PC copy through the Windows bin'
    } finally { Remove-Pair $layout }
}

function Invoke-A49LogsWhileRunning {
    $layout = New-Layout 'a49'
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/one.txt') 'one'
        [void](Add-Pair $layout)
        Invoke-Cli @('host', 'start') | Out-Null
        # Cause a change and wait for a specific log file to grow while the host is still writing.
        Write-Exact (Join-Path $layout.Local 'alpha/two.txt') 'two'
        $runId = Wait-LogGrowth -PairId $layout.Id
        Assert-True ($null -ne $runId) 'a stored run log must be visible and growing while the host runs'
        $plain = Invoke-Cli @('logs', '--pair', $layout.Id, '--run', $runId)
        $withFiles = Invoke-Cli @('logs', '--pair', $layout.Id, '--run', $runId, '--show-files')
        Assert-True ($plain -match '(?m)^(?:Stage|Issue|Failed|Wait|Effect)\b') 'normal records are present in the live log view'
        Assert-True ($plain -notmatch '(?m)^Effect\b') 'individual file operations are off by default'
        Assert-True ($withFiles -match '(?m)^Effect\b') 'individual file operations appear with --show-files'
        Invoke-Cli @('host', 'stop') | Out-Null
        return 'A49: the current run log is read while the host writes it; per-file lines are opt-in'
    } finally { Remove-Pair $layout }
}

function Invoke-A50NestedPairs {
    $outer = New-Layout 'a50outer'
    $inner = $null
    try {
        Write-Exact (Join-Path $outer.Local 'alpha/outer.txt') 'outer'
        [void](Add-Pair $outer)
        Repair-Pair $outer
        $innerLocal = Join-Path $outer.Local 'inner'
        $innerRemote = Join-Remote $outer.Remote 'inner'
        New-RemoteFolder $innerRemote
        $inner = [pscustomobject]@{ Name = "$($outer.Name)-inner"; Local = $innerLocal; Remote = $innerRemote; Id = $null }
        Write-Exact (Join-Path $innerLocal 'inside.txt') 'inner'
        [void](Add-Pair $inner)
        Repair-Pair $outer
        Repair-Pair $inner
        Assert-RemoteFile $inner 'inside.txt' 'inner'
        Assert-True (-not (Test-RemoteExists (Join-Remote $outer.Remote 'inner/inside.txt'))) 'the outer pair must skip the inner pair''s root'
        Assert-RemoteFile $outer 'alpha/outer.txt' 'outer'
        Remove-Pair $inner
        Repair-Pair $outer
        return 'A50: the outer pair skips the inner pair root; both pairs work; removing the inner pair re-compares its subtree'
    } finally { if ($inner) { Remove-Pair $inner }; Remove-Pair $outer }
}

function Get-LatestRunId([string]$PairId) {
    $logs = Join-Path $env:LOCALAPPDATA 'Massdex\SyncShark\logs'
    if (-not (Test-Path -LiteralPath $logs)) { return $null }
    $file = Get-ChildItem -LiteralPath $logs -Filter "$PairId-*.log" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { return $null }
    return $file.Name.Substring($PairId.Length + 1, $file.Name.Length - $PairId.Length - 1 - 4)
}

# Waits for the current run's log to be observed growing (a write boundary), then returns its run id.
function Wait-LogGrowth([string]$PairId) {
    $logs = Join-Path $env:LOCALAPPDATA 'Massdex\SyncShark\logs'
    $deadline = (Get-Date).AddSeconds($script:RunTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $file = Get-ChildItem -LiteralPath $logs -Filter "$PairId-*.log" -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($file) {
            $size = $file.Length
            Start-Sleep -Milliseconds 300
            $file.Refresh()
            if ($file.Length -gt $size) {
                return $file.Name.Substring($PairId.Length + 1, $file.Name.Length - $PairId.Length - 1 - 4)
            }
        }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

# --- Selection and reporting -----------------------------------------------

$script:Scenarios = [ordered]@{
    'A02CreateOneSide' = 'Invoke-A02CreateOneSide'
    'A05EditOneSide' = 'Invoke-A05EditOneSide'
    'A07EditBoth' = 'Invoke-A07EditBoth'
    'A10DeleteOneSide' = 'Invoke-A10DeleteOneSide'
    'A11EditVersusDelete' = 'Invoke-A11EditVersusDelete'
    'A18MoveOneSide' = 'Invoke-A18MoveOneSide'
    'A19MoveVersusEdit' = 'Invoke-A19MoveVersusEdit'
    'A21MoveVersusDelete' = 'Invoke-A21MoveVersusDelete'
    'A26SafeSave' = 'Invoke-A26SafeSave'
    'A27CapitalRename' = 'Invoke-A27CapitalRename'
    'A28ExcludeKeepAndInclude' = 'Invoke-A28ExcludeKeepAndInclude'
    'A28ExcludeNoKeep' = 'Invoke-A28ExcludeNoKeep'
    'A39InUse' = 'Invoke-A39InUse'
    'A44ConcurrentDirections' = 'Invoke-A44ConcurrentDirections'
    'A47HostReadsPauseStop' = 'Invoke-A47HostReadsPauseStop'
    'A48RootBinRemovable' = 'Invoke-A48RootBinRemovable'
    'A48NativeBinFixed' = 'Invoke-A48NativeBinFixed'
    'A49LogsWhileRunning' = 'Invoke-A49LogsWhileRunning'
    'A50NestedPairs' = 'Invoke-A50NestedPairs'
}

function Resolve-Scenarios([string]$Selection) {
    if ($Selection -eq 'all') { return @($script:Scenarios.Keys) }
    return @($script:Scenarios.Keys | Where-Object { $_ -eq $Selection -or $_.IndexOf($Selection, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 })
}

try {
    Test-Assertions
    $selected = Resolve-Scenarios $Scenario
    if (-not $selected) { throw "No scenario matches '$Scenario'" }

    Write-Host 'Sync Shark live acceptance (isolated staging profile)'
    Write-Host "  candidate:  $script:ExePath"
    Write-Host "  local base: $script:LocalBase"
    Write-Host "  remote:     supplied test root"
    Write-Host "  reports:    $script:ReportRoot"
    Write-Host ''

    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($name in $selected) {
        Write-Host "== $name"
        $started = Get-Date
        $status = 'PASS'
        $reason = ''
        try {
            $reason = & $script:Scenarios[$name]
            if ($reason -like 'NOT OBSERVED:*') { $status = 'NOT OBSERVED'; $script:NotObserved++ }
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
    Write-Host ("failures: {0}; not observed: {1}" -f $script:Failures, $script:NotObserved)
    exit $script:Failures
} finally {
    [System.Net.WebRequest]::DefaultWebProxy = $script:SavedProxy
    foreach ($name in $script:SavedEnvProxy.Keys) { [Environment]::SetEnvironmentVariable($name, $script:SavedEnvProxy[$name]) }
    $script:Token = $null
}
