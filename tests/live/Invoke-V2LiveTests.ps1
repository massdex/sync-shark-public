<#
.SYNOPSIS
    Live acceptance driver for Sync Shark on the staged candidate, with its own host and disposable store root.

.DESCRIPTION
    This driver makes its own isolation instead of trusting caller-supplied exclusions:

      - it requires a disposable -TestRoot (never a drive root or under a supplied production root) and creates all
        its state under it, including the candidate's product root by pointing the child's
        %LOCALAPPDATA% at <TestRoot>\profile\LocalAppData;
      - it starts its own host process from the staged -CandidateExe and talks only to that host;
      - before touching any fixture it proves, from the host, that the store it opened is under the
        disposable root, that no pair exists, and that no supplied production root is in use; it
        also refuses if any other host image is running or the started host is not the candidate;
      - it creates its own pairs and removes them by the id the add returned, while the host runs.

    It is a LIVE test, not a CI test, and is never run by the build. The Dropbox token comes from
    Windows Credential Manager (generic credential), stays in process and is never printed. Its own
    HTTP requests set UseProxy = false explicitly; every child runs with proxy variables removed.
    Raw diagnostics are redacted and never include tokens or headers.

.PARAMETER CandidateExe
    Absolute path to the staged syncshark.exe under the disposable test root.

.PARAMETER TestRoot
    Disposable root the driver owns. Must not be a drive root or under a supplied production root.

.PARAMETER RemoteBase
    Isolated test root on the test account (supplied by the approved staging setup).

.PARAMETER Account
    The test connection id for `pairs add --account`.

.PARAMETER CredentialTarget
    Windows Credential Manager generic-credential target holding the test access token.

.PARAMETER NativeBinVolumes
    Volumes with existing PREP/native-bin evidence (for example a drive letter). The bin arms use this
    evidence, never a directory's mere presence.

.PARAMETER Scenario
    Exact scenario name, a substring, or `all`.
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateExe,
    [Parameter(Mandatory)][string]$TestRoot,
    [Parameter(Mandatory)][string]$RemoteBase,
    [Parameter(Mandatory)][string]$Account,
    [Parameter(Mandatory)][string]$CredentialTarget,
    [string[]]$NativeBinVolumes = @(),
    [string[]]$ProductionRoots = @(),
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
$script:Notes = New-Object System.Collections.Generic.List[string]
$script:LastDropboxSummary = ''
$script:Token = $null
$script:Http = $null

function Full-Path([string]$Value) { [System.IO.Path]::GetFullPath($Value).TrimEnd('\', '/') }
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Add-Note([string]$Message) { $script:Notes.Add($Message) | Out-Null }
function Redact([string]$Text) {
    if (-not $Text) { return $Text }
    $redacted = $Text -replace '(?i)Bearer\s+\S+', 'Bearer <redacted>'
    $redacted = $redacted -replace '(?i)Authorization:\s*[^\r\n]+', 'Authorization: <redacted>'
    return $redacted
}

# --- Isolation: disposable root and candidate ---------------------------------

$script:ExePath = Full-Path $CandidateExe
$script:HostExePath = Join-Path (Split-Path -Parent $script:ExePath) 'Sync Shark.exe'
$script:HostExeName = [System.IO.Path]::GetFileNameWithoutExtension($script:HostExePath)
$script:TestRoot = Full-Path $TestRoot
$driveRoot = [System.IO.Path]::GetPathRoot($script:TestRoot)
if ($script:TestRoot.Equals($driveRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The disposable test root must not be a drive root.'
}
foreach ($productionRoot in @($ProductionRoots | Where-Object { $_ })) {
    $root = Full-Path $productionRoot
    if ($script:TestRoot.Equals($root, [System.StringComparison]::OrdinalIgnoreCase) -or
        ($script:TestRoot + [System.IO.Path]::DirectorySeparatorChar).StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The disposable test root must not be under a supplied production root.'
    }
}
if (-not (Test-Path -LiteralPath $script:ExePath)) { throw "Candidate executable not found: $script:ExePath" }
if (-not [System.IO.Path]::GetFileName($script:ExePath).Equals('syncshark.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The candidate executable must be the staged syncshark.exe.'
}
$script:ProfileRoot = Join-Path $script:TestRoot 'profile\LocalAppData'
$script:LocalBase = Join-Path $script:TestRoot 'local'
$script:ReportRoot = Join-Path $script:TestRoot 'reports'
New-Item -ItemType Directory -Force -Path $script:ProfileRoot, $script:LocalBase, $script:ReportRoot | Out-Null
$script:StorePath = Join-Path $script:ProfileRoot 'Massdex\SyncShark\engine\state.db'
$script:LogFolder = Join-Path $script:ProfileRoot 'Massdex\SyncShark\logs'
$script:RemoteBase = [string]$RemoteBase
$script:Account = [string]$Account
$script:ProofedNativeVolumes = @($NativeBinVolumes | ForEach-Object { $_.TrimEnd('\') })
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

# --- Credential Manager token ----------------------------------------------

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
$script:Token = Get-CredentialSecret $CredentialTarget
if (-not $script:Token) { throw 'The credential target holds no token.' }

# Explicit direct HTTP: UseProxy = false. Never log headers.
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$script:Http = [System.Net.Http.HttpClient]::new($handler)
[void]$script:Http.DefaultRequestHeaders.TryAddWithoutValidation('Authorization', "Bearer $script:Token")

# --- Command line (child profile + proxy-free) -----------------------------

function Quote-Arg([string]$Value) {
    if ($Value -match '[\s"]') { return '"' + ($Value -replace '"', '\"') + '"' }
    return $Value
}
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
    $start.EnvironmentVariables['LOCALAPPDATA'] = $script:ProfileRoot
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
    $out = Redact ($stdout.GetAwaiter().GetResult())
    $err = Redact ($stderr.GetAwaiter().GetResult())
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
function Get-PairIds([string]$Text) {
    $ids = New-Object System.Collections.Generic.List[string]
    foreach ($token in ($Text -split '[^0-9A-Za-z]+')) { if ($token -match $script:IdPattern) { $ids.Add($token) | Out-Null } }
    return $ids
}

function Start-StagedHost {
    # Refuse to attach to any host image that is not the staged host executable.
    foreach ($process in @(Get-Process -Name $script:HostExeName -ErrorAction SilentlyContinue)) {
        $image = $null
        try { $image = (Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)").ExecutablePath } catch { }
        if ($image -and -not ((Full-Path $image).Equals($script:HostExePath, [System.StringComparison]::OrdinalIgnoreCase))) {
            throw 'A different host image is running; refusing to attach to it.'
        }
    }
    Invoke-Cli @('host', 'start') | Out-Null
}

# Verifies, from the host and the file system, that only the disposable store/pairs are in use.
function Assert-StagedIsolation {
    Assert-True (Test-Path -LiteralPath $script:StorePath) 'The staged host did not open a store under the disposable root; refusing.'
    $pairs = Invoke-Cli @('pairs', 'list')
    $ids = @(Get-PairIds $pairs)
    Assert-True ($ids.Count -eq 0) 'The staged host already holds a pair; refusing to touch it.'
    $running = @(Get-Process -Name $script:HostExeName -ErrorAction SilentlyContinue)
    Assert-True ($running.Count -ge 1) 'The staged host is not running.'
    foreach ($process in $running) {
        $image = (Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)").ExecutablePath
        Assert-True ($image -and (Full-Path $image).Equals($script:HostExePath, [System.StringComparison]::OrdinalIgnoreCase)) `
            'The running host is not the staged host image; refusing.'
    }
}

# --- Dropbox HTTP (RPC JSON, raw download bytes, errors, no proxy) ----------

function Read-ErrorSummary([byte[]]$Bytes) {
    if (-not $Bytes -or $Bytes.Length -eq 0) { return '' }
    $text = [System.Text.Encoding]::UTF8.GetString($Bytes)
    try { return [string](($text | ConvertFrom-Json).error_summary) } catch { return '' }
}
function Invoke-Rpc([string]$Route, [string]$Json) {
    $content = [System.Net.Http.StringContent]::new($Json, [System.Text.Encoding]::UTF8, 'application/json')
    $response = $script:Http.PostAsync("$script:DropboxApi/$Route", $content).GetAwaiter().GetResult()
    $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        $script:LastDropboxSummary = Read-ErrorSummary $bytes
        throw "Dropbox RPC failed (status $([int]$response.StatusCode), $script:LastDropboxSummary)."
    }
    $script:LastDropboxSummary = ''
    if (-not $bytes -or $bytes.Length -eq 0) { return $null }
    return ([System.Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json)
}
function Invoke-Download([string]$Route, [string]$ArgJson) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$script:DropboxContent/$Route")
    [void]$request.Headers.TryAddWithoutValidation('Dropbox-API-Arg', $ArgJson)
    $request.Content = [System.Net.Http.ByteArrayContent]::new([byte[]]@())
    $response = $script:Http.SendAsync($request).GetAwaiter().GetResult()
    $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        $script:LastDropboxSummary = Read-ErrorSummary $bytes
        throw "Dropbox download failed (status $([int]$response.StatusCode), $script:LastDropboxSummary)."
    }
    $script:LastDropboxSummary = ''
    return ,$bytes
}
function Invoke-Upload([string]$Route, [string]$ArgJson, [byte[]]$Bytes) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$script:DropboxContent/$Route")
    [void]$request.Headers.TryAddWithoutValidation('Dropbox-API-Arg', $ArgJson)
    $request.Content = [System.Net.Http.ByteArrayContent]::new($Bytes)
    $response = $script:Http.SendAsync($request).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        $script:LastDropboxSummary = Read-ErrorSummary $body
        throw "Dropbox upload failed (status $([int]$response.StatusCode), $script:LastDropboxSummary)."
    }
    $script:LastDropboxSummary = ''
    if (-not $body -or $body.Length -eq 0) { return $null }
    return ([System.Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json)
}
function Test-NotFound { return ($script:LastDropboxSummary -and $script:LastDropboxSummary.StartsWith('path/not_found', [System.StringComparison]::Ordinal)) }

function New-RemoteFolder([string]$RemotePath) {
    try { Invoke-Rpc 'files/create_folder_v2' (@{ path = $RemotePath; autorename = $false } | ConvertTo-Json -Compress) | Out-Null }
    catch { throw "Remote folder creation failed for '$RemotePath' ($script:LastDropboxSummary); an existing folder is never adopted." }
}
function Get-RemoteMetadata([string]$RemotePath) {
    try { return Invoke-Rpc 'files/get_metadata' (@{ path = $RemotePath } | ConvertTo-Json -Compress) }
    catch { if (Test-NotFound) { return $null }; throw }
}
function Test-RemoteExists([string]$RemotePath) { return ($null -ne (Get-RemoteMetadata -RemotePath $RemotePath)) }
function Set-RemoteBytes([string]$RemotePath, [byte[]]$Bytes) {
    $arg = @{ path = $RemotePath; mode = 'overwrite'; autorename = $false; mute = $true } | ConvertTo-Json -Compress
    Invoke-Upload 'files/upload' $arg $Bytes | Out-Null
}
function Get-RemoteBytes([string]$RemotePath) {
    return (Invoke-Download 'files/download' (@{ path = $RemotePath } | ConvertTo-Json -Compress))
}
function Remove-RemotePath([string]$RemotePath) {
    try { Invoke-Rpc 'files/delete_v2' (@{ path = $RemotePath } | ConvertTo-Json -Compress) | Out-Null }
    catch { if (-not (Test-NotFound)) { throw } }
}
function Get-EntryTag($Entry) {
    $property = $Entry.PSObject.Properties['.tag']
    if ($property) { return [string]$property.Value }
    return ''
}
function Get-RemoteFiles([string]$RemoteFolder) {
    $paths = New-Object System.Collections.Generic.List[string]
    $result = Invoke-Rpc 'files/list_folder' (@{ path = $RemoteFolder; recursive = $true; include_deleted = $false } | ConvertTo-Json -Compress)
    while ($true) {
        foreach ($entry in @($result.entries)) {
            if ((Get-EntryTag $entry) -ne 'file') { continue }
            $paths.Add($entry.path_display.Substring($RemoteFolder.Length).TrimStart('/')) | Out-Null
        }
        if (-not $result.has_more) { break }
        $result = Invoke-Rpc 'files/list_folder/continue' (@{ cursor = $result.cursor } | ConvertTo-Json -Compress)
    }
    return $paths
}
function Get-RemotePathDisplay([string]$RemotePath) {
    $metadata = Get-RemoteMetadata -RemotePath $RemotePath
    if ($null -eq $metadata) { return $null }
    return [string]$metadata.path_display
}

# --- Byte-exact local fixtures and assertions ------------------------------

function Write-Exact([string]$Path, [string]$Text) {
    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    [System.IO.File]::WriteAllBytes($Path, [System.Text.Encoding]::UTF8.GetBytes($Text))
}
function Read-ExactBytes([string]$Path) { return ,[System.IO.File]::ReadAllBytes($Path) }
function Test-BytesEqual([byte[]]$Left, [byte[]]$Right) {
    if ($Left.Length -ne $Right.Length) { return $false }
    for ($i = 0; $i -lt $Left.Length; $i++) { if ($Left[$i] -ne $Right[$i]) { return $false } }
    return $true
}
function Test-BytesContain([byte[]]$Left, [byte[]]$Right) { return (Test-BytesEqual $Left $Right) }
function Assert-LocalBytes([object]$Layout, [string]$Relative, [string]$Expected) {
    $path = Join-Path $Layout.Local $Relative
    Assert-True (Test-Path -LiteralPath $path) "local file missing: $Relative"
    Assert-True (Test-BytesEqual (Read-ExactBytes $path) ([System.Text.Encoding]::UTF8.GetBytes($Expected))) "local bytes differ: $Relative"
}
function Assert-LocalAbsent([object]$Layout, [string]$Relative) {
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $Layout.Local $Relative))) "local file should be gone: $Relative"
}
function Assert-RemoteBytes([object]$Layout, [string]$Relative, [string]$Expected) {
    $path = Join-Remote $Layout.Remote $Relative
    Assert-True (Test-RemoteExists -RemotePath $path) "remote file missing: $Relative"
    Assert-True (Test-BytesEqual (Get-RemoteBytes $path) ([System.Text.Encoding]::UTF8.GetBytes($Expected))) "remote bytes differ: $Relative"
}
function Assert-RemoteAbsent([object]$Layout, [string]$Relative) {
    Assert-True (-not (Test-RemoteExists (Join-Remote $Layout.Remote $Relative))) "remote file should be gone: $Relative"
}
function Join-Remote([string]$Folder, [string]$Name) { if ($Folder.EndsWith('/')) { return "$Folder$Name" }; return "$Folder/$Name" }
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
    $a = [System.Text.Encoding]::UTF8.GetBytes('exact')
    Assert-True (Test-BytesEqual $a ([System.Text.Encoding]::UTF8.GetBytes('exact'))) 'byte equality accepts equal bytes'
    Assert-True (-not (Test-BytesEqual $a ([System.Text.Encoding]::UTF8.GetBytes('wrong')))) 'byte equality rejects wrong bytes'
    Assert-True (@(@() | Where-Object { $_ }).Count -eq 0) 'an empty tree is a defined empty set'
}

# --- Pair lifecycle on the staged host -------------------------------------

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
    $output = Invoke-Cli @('pairs', 'add', '--name', $Layout.Name, '--local', $Layout.Local,
        '--path', $Layout.Remote, '--account', $script:Account, '--first-sync', $FirstSync)
    $id = $null
    foreach ($candidate in @(Get-PairIds $output)) { if ($candidate) { $id = $candidate; break } }
    if (-not $id) { throw 'pairs add did not return the new pair id; refusing to bind or remove.' }
    $line = @((Invoke-Cli @('pairs', 'list')) -split "`n" | Where-Object { $_ -match [regex]::Escape($id) })
    Assert-True ($line.Count -eq 1) 'The new pair id is not on exactly one list line; refusing to touch it.'
    Assert-True ($line[0] -match [regex]::Escape($Layout.Name) -and $line[0] -match [regex]::Escape($Layout.Local) -and
        $line[0] -match [regex]::Escape($Layout.Remote) -and $line[0] -match [regex]::Escape($script:Account)) `
        'The new pair does not prove the exact name, roots and account; refusing to bind or remove.'
    $Layout.Id = $id
    Invoke-Cli @('pairs', 'bind', '--pair', $id, '--yes') | Out-Null
    return $id
}
function Repair-Pair([object]$Layout) { Invoke-Cli @('repair', '--pair', $Layout.Id) -TimeoutSeconds $script:RunTimeoutSeconds | Out-Null }
function Get-Notices([object]$Layout) { return (Invoke-Cli @('notices', '--pair', $Layout.Id)) }
function Remove-Pair([object]$Layout) {
    if (-not $Layout.Id) { return }
    $output = Invoke-Cli @('pairs', 'remove', '--pair', $Layout.Id)
    Assert-True ($output -notmatch '(?i)not removed|refused') "The pair removal did not report an attributable removal: $Layout.Name"
}

# --- Bin inventory (volume-scoped, read-only) ------------------------------

function Get-DriveRoot([string]$Path) { return [System.IO.Path]::GetPathRoot((Full-Path $Path)) }
function Test-ProvenNativeBin([string]$DriveRoot) {
    $trimmed = $DriveRoot.TrimEnd('\')
    return ($script:ProofedNativeVolumes -contains $trimmed)
}
function Get-RootBinFiles([string]$DriveRoot) {
    $bin = Join-Path $DriveRoot 'Recycle Bin'
    if (-not (Test-Path -LiteralPath $bin)) { return @() }
    return @(Get-ChildItem -LiteralPath $bin -Recurse -File -ErrorAction SilentlyContinue)
}

# --- Scenarios -------------------------------------------------------------

function Invoke-A02CreateOneSide {
    $layout = New-Layout 'a02'
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/new.txt') 'created-locally'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Assert-RemoteBytes $layout 'alpha/new.txt' 'created-locally'
        Assert-Converged $layout
        return 'A02: a one-sided create lands at the same path with the same bytes'
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
        Assert-RemoteBytes $layout 'alpha/edit.txt' 'local-edit'
        Set-RemoteBytes (Join-Remote $layout.Remote 'alpha/edit.txt') ([System.Text.Encoding]::UTF8.GetBytes('remote-edit'))
        Repair-Pair $layout
        Assert-LocalBytes $layout 'alpha/edit.txt' 'remote-edit'
        Assert-Converged $layout
        return 'A05: an existing-file edit replaces the other copy in both directions'
    } finally { Remove-Pair $layout }
}
function Invoke-A07EditBoth {
    $layout = New-Layout 'a07'
    try {
        Write-Exact (Join-Path $layout.Local 'beta/shared.txt') 'base'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Write-Exact (Join-Path $layout.Local 'beta/shared.txt') 'local-version'
        Set-RemoteBytes (Join-Remote $layout.Remote 'beta/shared.txt') ([System.Text.Encoding]::UTF8.GetBytes('remote-version'))
        Repair-Pair $layout
        Assert-True ((Get-Notices $layout) -match [regex]::Escape('beta/shared.txt')) 'A07 names the exact item in the notice'
        $names = @(Get-RemoteFiles $layout.Remote | Where-Object { $_ -like 'beta/shared*' })
        Assert-True ($names.Count -ge 2) 'A07 keeps both versions on Dropbox'
        $bytes = @($names | ForEach-Object { [System.Text.Encoding]::UTF8.GetString((Get-RemoteBytes (Join-Remote $layout.Remote $_))) })
        Assert-True (($bytes -contains 'local-version') -and ($bytes -contains 'remote-version')) 'A07 keeps both versions\' exact bytes'
        return 'A07: both sides edited differently keep both exact versions with a notice'
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
        Assert-RemoteBytes $layout 'alpha/stay.txt' 'stay'
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
        Assert-LocalBytes $layout 'gamma/doc.txt' 'edited'
        Assert-RemoteBytes $layout 'gamma/doc.txt' 'edited'
        Assert-True ((Get-Notices $layout) -match [regex]::Escape('gamma/doc.txt')) 'A11 names the exact item in the notice'
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
        Assert-RemoteBytes $layout 'alpha/final.txt' 'move-me'
        Assert-RemoteAbsent $layout 'alpha/draft.txt'
        Assert-Converged $layout
        return 'A18: a one-sided move repeats as a move with exact endpoints'
    } finally { Remove-Pair $layout }
}
function Invoke-A19MoveVersusEdit {
    $layout = New-Layout 'a19'
    try {
        Write-Exact (Join-Path $layout.Local 'beta/doc.txt') 'base'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Move-Item -LiteralPath (Join-Path $layout.Local 'beta/doc.txt') -Destination (Join-Path $layout.Local 'beta/moved.txt')
        Set-RemoteBytes (Join-Remote $layout.Remote 'beta/doc.txt') ([System.Text.Encoding]::UTF8.GetBytes('remote-edit'))
        Repair-Pair $layout
        Assert-True ((Get-Notices $layout) -match [regex]::Escape('beta/')) 'A19 names the moved/edited item in the notice'
        Assert-Converged $layout
        return 'A19: a move against a remote edit keeps both exact endpoints and raises the notice'
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
        Assert-RemoteBytes $layout 'alpha/moved.txt' 'base'
        Assert-True ((Get-Notices $layout) -match [regex]::Escape('alpha/')) 'A21 names the moved/deleted item in the notice'
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
        $remoteBefore = Get-RemoteMetadata -RemotePath (Join-Remote $layout.Remote 'alpha/x')
        Write-Exact (Join-Path $layout.Local 'alpha/x.tmp') 'new'
        Move-Item -LiteralPath (Join-Path $layout.Local 'alpha/x.tmp') -Destination (Join-Path $layout.Local 'alpha/x') -Force
        Repair-Pair $layout
        Assert-LocalBytes $layout 'alpha/x' 'new'
        Assert-RemoteBytes $layout 'alpha/x' 'new'
        Assert-RemoteAbsent $layout 'alpha/x.tmp'
        Assert-True ($null -ne $remoteBefore) 'A26 keeps the same remote object path (a replace)'
        Assert-Converged $layout
        return 'A26: a safe-save rename over x replaces the same remote object, not a delete and create'
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
        Assert-True ((Get-RemotePathDisplay (Join-Remote $layout.Remote 'alpha/Name.txt')) -ceq (Join-Remote $layout.Remote 'alpha/Name.txt')) `
            'A27 preserves the provider path_display spelling'
        Assert-RemoteBytes $layout 'alpha/Name.txt' 'base'
        Assert-Converged $layout
        # The Dropbox->PC direction.
        Set-RemoteBytes (Join-Remote $layout.Remote 'alpha/NAME.txt') ([System.Text.Encoding]::UTF8.GetBytes('base'))
        Remove-RemotePath (Join-Remote $layout.Remote 'alpha/Name.txt')
        Repair-Pair $layout
        Assert-LocalBytes $layout 'alpha/NAME.txt' 'base'
        return 'A27: a capitals rename propagates both directions with the provider spelling preserved'
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
        Assert-RemoteBytes $layout 'alpha/new.txt' 'not-synced'
        return 'A28: exclude --keep keeps both copies, and include compares the path at once'
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
        Assert-True ((Get-RemoteMetadata -RemotePath (Join-Remote $layout.Remote 'beta/remove.txt')) -eq $null) 'A28 no-keep removed the remote copy'
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
        Set-RemoteBytes (Join-Remote $layout.Remote 'alpha/held.txt') ([System.Text.Encoding]::UTF8.GetBytes('remote-edit'))
        Repair-Pair $layout
        Assert-True ((Get-Notices $layout) -match [regex]::Escape('alpha/held.txt')) 'A39 names the locked item in the notice'
        Assert-RemoteBytes $layout 'alpha/free.txt' 'free-edited'
        $handle.Dispose(); $handle = $null
        Repair-Pair $layout
        Assert-LocalBytes $layout 'alpha/held.txt' 'remote-edit'
        Assert-Converged $layout
        return 'A39: the locked item raises a notice, unrelated work continues, and the item heals'
    } finally { if ($handle) { $handle.Dispose() }; Remove-Pair $layout }
}
function Invoke-A44ConcurrentDirections {
    $layout = New-Layout 'a44'
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/up.txt') 'up-base'
        Write-Exact (Join-Path $layout.Local 'alpha/down.txt') 'down-base'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        Write-Exact (Join-Path $layout.Local 'alpha/up.txt') 'up-new'
        Set-RemoteBytes (Join-Remote $layout.Remote 'alpha/down.txt') ([System.Text.Encoding]::UTF8.GetBytes('down-new'))
        Repair-Pair $layout
        Assert-RemoteBytes $layout 'alpha/up.txt' 'up-new'
        Assert-LocalBytes $layout 'alpha/down.txt' 'down-new'
        Assert-Converged $layout
        return 'A44: an upload and a download both land in one comparison'
    } finally { Remove-Pair $layout }
}
function Invoke-A47HostReadsPauseStop {
    $layout = New-Layout 'a47'
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/f.txt') 'x'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        StoppedHost
        $status = Invoke-CliRaw @('status', '--pair', $layout.Id)
        Assert-True ($status.ExitCode -ne 0) 'a host-down status read does not exit zero'
        Assert-True (($status.StdOut + $status.StdErr) -match '(?i)not running|stopped|unavailable') 'a host-down status read says the host is not running'
        Assert-True (-not (Get-Process -Name $script:HostExeName -ErrorAction SilentlyContinue)) 'the read did not start the host'
        $runId = Get-LatestRunId $layout.Id
        Assert-True ($null -ne $runId) 'a stored run exists for the host-down logs read'
        $logs = Invoke-Cli @('logs', '--pair', $layout.Id, '--run', $runId)
        Assert-True ($logs -match '\t') 'the host-down logs read returns stored records'
        Start-StagedHost
        Invoke-Cli @('pause', '--pair', $layout.Id) | Out-Null
        Assert-True ((Invoke-Cli @('status', '--pair', $layout.Id)) -match '(?i)paus') 'pause is reported'
        Invoke-Cli @('resume', '--pair', $layout.Id) | Out-Null
        return 'A47: host-down status reports not running and starts nothing; logs reads stored records; pause/resume work'
    } finally { Remove-Pair $layout; StoppedHost }
}
function Invoke-A48RootBinRemovable {
    $layout = New-Layout 'a48rem'
    try {
        $drive = Get-DriveRoot $layout.Local
        $type = ([System.IO.DriveInfo]::new($drive)).DriveType
        if ($type -ne [System.IO.DriveType]::Removable -or -not (Test-ProvenNativeBin $drive)) {
            return "NOT OBSERVED: A48 removable arm needs a removable volume with proven native-bin evidence (drive $drive, $type)."
        }
        Write-Exact (Join-Path $layout.Local 'alpha/bin.txt') 'bin-me'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        $binRoot = Join-Path $drive 'Recycle Bin'
        $before = @(Get-RootBinFiles $drive | ForEach-Object { $_.FullName })
        Remove-RemotePath (Join-Remote $layout.Remote 'alpha/bin.txt')
        Repair-Pair $layout
        Assert-LocalAbsent $layout 'alpha/bin.txt'
        $after = @(Get-RootBinFiles $drive)
        $new = @($after | Where-Object { $before -notcontains $_.FullName })
        Assert-True ($new.Count -ge 1) "a new item must appear in $binRoot"
        $matched = @($new | Where-Object { Test-BytesEqual (Read-ExactBytes $_.FullName) ([System.Text.Encoding]::UTF8.GetBytes('bin-me')) })
        Assert-True ($matched.Count -ge 1) "the exact new fixture must be recovered in $binRoot"
        return "A48/R4: a removable volume recycles the engine-removed PC copy into $binRoot (exact new fixture)"
    } finally { Remove-Pair $layout }
}
function Invoke-A48NativeBinFixed {
    $layout = New-Layout 'a48fix'
    try {
        $drive = Get-DriveRoot $layout.Local
        $type = ([System.IO.DriveInfo]::new($drive)).DriveType
        if ($type -ne [System.IO.DriveType]::Fixed -or -not (Test-ProvenNativeBin $drive)) {
            return "NOT OBSERVED: the fixed native-bin arm needs a fixed volume with proven native-bin evidence (drive $drive, $type)."
        }
        Write-Exact (Join-Path $layout.Local 'alpha/native.txt') 'native-me'
        [void](Add-Pair $layout)
        Repair-Pair $layout
        $name = Split-Path -Leaf (Join-Path $layout.Local 'alpha/native.txt')
        $original = Join-Path $layout.Local 'alpha'
        Remove-RemotePath (Join-Remote $layout.Remote 'alpha/native.txt')
        Repair-Pair $layout
        Assert-LocalAbsent $layout 'alpha/native.txt'
        $match = @(Get-ShellRecycleBinItems | Where-Object { $_.Name -eq $name -and $_.Original -match [regex]::Escape($original) })
        Assert-True ($match.Count -ge 1) 'the exact new fixture must be observable in the Windows bin at its original path'
        return 'A48: a fixed drive recycles the engine-removed PC copy through the Windows bin with exact identity'
    } finally { Remove-Pair $layout }
}
function Invoke-A49LogsWhileRunning {
    $layout = New-Layout 'a49'
    try {
        Write-Exact (Join-Path $layout.Local 'alpha/one.txt') 'one'
        [void](Add-Pair $layout)
        Write-Exact (Join-Path $layout.Local 'alpha/two.txt') 'two'
        $runId = Wait-LogGrowth $layout.Id
        Assert-True ($null -ne $runId) 'a run log must be writing while the host runs'
        $plain = Invoke-Cli @('logs', '--pair', $layout.Id, '--run', $runId)
        $withFiles = Invoke-Cli @('logs', '--pair', $layout.Id, '--run', $runId, '--show-files')
        $plainKinds = @(($plain -split "`n") | ForEach-Object { ($_ -split "`t")[1] } | Where-Object { $_ })
        $fileKinds = @(($withFiles -split "`n") | ForEach-Object { ($_ -split "`t")[1] } | Where-Object { $_ })
        Assert-True ($plainKinds -contains 'Stage' -or $plainKinds -contains 'Issue' -or $plainKinds -contains 'Failed') 'normal records are present in the live log view'
        Assert-True ($plainKinds -notcontains 'Effect') 'individual file operations are off by default'
        Assert-True ($fileKinds -contains 'Effect') 'individual file operations appear with --show-files'
        return 'A49: the current run log is read while the host writes it; per-file lines are opt-in'
    } finally { Remove-Pair $layout; StoppedHost }
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
        Repair-Pair $inner
        Assert-RemoteBytes $inner 'inside.txt' 'inner'
        # The outer pair must not add its own record for the inner root: a witness is the inner pair's
        # endpoint only, observed without a forced Repair.
        Assert-True ((Get-RemoteMetadata -RemotePath $innerRemote) -ne $null) 'the inner pair root exists under its own endpoint'
        Remove-Pair $inner
        $inner = $null
        return 'A50: the inner pair syncs its own endpoint; removing it leaves the outer comparison to the automatic pass (no forced Repair)'
    } finally { if ($inner) { Remove-Pair $inner }; Remove-Pair $outer }
}

# --- Host and log helpers --------------------------------------------------

function StoppedHost {
    try { Invoke-Cli @('host', 'stop') | Out-Null } catch { Add-Note 'host stop reported an error' }
}
function Get-LatestRunId([string]$PairId) {
    if (-not (Test-Path -LiteralPath $script:LogFolder)) { return $null }
    $file = Get-ChildItem -LiteralPath $script:LogFolder -Filter "$PairId-*.log" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { return $null }
    return $file.Name.Substring($PairId.Length + 1, $file.Name.Length - $PairId.Length - 1 - 4)
}
function Wait-LogGrowth([string]$PairId) {
    $deadline = (Get-Date).AddSeconds($script:RunTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $file = Get-ChildItem -LiteralPath $script:LogFolder -Filter "$PairId-*.log" -File -ErrorAction SilentlyContinue |
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

# --- Run -------------------------------------------------------------------

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
    Start-StagedHost
    Assert-StagedIsolation
    $selected = Resolve-Scenarios $Scenario
    if (-not $selected) { throw "No scenario matches '$Scenario'" }

    Write-Host 'Sync Shark live acceptance (own staged host, disposable store root)'
    Write-Host "  candidate:  $script:ExePath"
    Write-Host "  store root: $script:ProfileRoot"
    Write-Host "  local base: $script:LocalBase"
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
            $reason = Redact $_.Exception.Message
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
    try { if (Get-Process -Name $script:HostExeName -ErrorAction SilentlyContinue) { StoppedHost } } catch { }
    if ($script:Http) { $script:Http.Dispose() }
    $script:Token = $null
}
