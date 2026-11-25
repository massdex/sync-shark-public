#Requires -Version 7.4
param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath($Repository)
$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $repository '.git'))) { throw 'Repository identity is missing.' }
$resolvedCommit = (& git -C $repository rev-parse "$Commit^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or $resolvedCommit -cne $Commit) { throw 'The requested source commit did not resolve exactly.' }
$tree = (& git -C $repository rev-parse "$Commit^{tree}").Trim()
if ($LASTEXITCODE -ne 0 -or $tree -notmatch '^[0-9a-f]{40}$') { throw 'The requested source tree did not resolve.' }
$commitSeconds = [int64]((& git -C $repository show -s --format=%ct $Commit).Trim())
if ($LASTEXITCODE -ne 0) { throw 'The source commit timestamp did not resolve.' }
$commitTime = [DateTimeOffset]::FromUnixTimeSeconds($commitSeconds)
if (-not (Test-Path -LiteralPath $outputDirectory)) { New-Item -ItemType Directory -Path $outputDirectory | Out-Null }
$archive = Join-Path $outputDirectory "sync-shark-source-$Commit.tar"
$manifest = Join-Path $outputDirectory "sync-shark-source-$Commit.members.json"

function Write-GitBlob($Writer, $Member, $Entry) {
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    foreach ($argument in '-C', $repository, 'cat-file', 'blob', $Member.git_object_id) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true
    $process = [Diagnostics.Process]::Start($start)
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $Entry.DataStream = [Security.Cryptography.CryptoStream]::new($process.StandardOutput.BaseStream, $hash, 'Read')
        try { $Writer.WriteEntry($Entry) } finally { $Entry.DataStream.Dispose() }
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "git cat-file failed for $($Member.path)." }
        [Convert]::ToHexString($hash.Hash).ToLowerInvariant()
    } finally { $hash.Dispose(); $process.Dispose() }
}

$gitMembers = [Collections.Generic.List[object]]::new()
$directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in & git -C $repository ls-tree -rl --full-tree $Commit) {
    if ($line -notmatch '^(?<mode>[0-9]{6}) blob (?<object>[0-9a-f]{40}) +(?<length>[0-9]+)\t(?<path>.+)$') { throw "Unsupported Git tree member: $line" }
    $path = $Matches.path; $mode = $Matches.mode; $object = $Matches.object
    if ($mode -notin '100644', '100755') { throw "Unsupported Git mode $mode for $path" }
    $parts = $path.Split('/')
    for ($index = 1; $index -lt $parts.Count; $index++) { $null = $directories.Add((@($parts[0..($index - 1)]) -join '/') + '/') }
    $gitMembers.Add([pscustomobject]@{ path = $path; git_mode = $mode; git_object_id = $object; length = [long]$Matches.length; sha256 = $null })
}
if ($LASTEXITCODE -ne 0) { throw 'Git tree enumeration failed.' }

$stream = [IO.File]::Create($archive)
$writer = [System.Formats.Tar.TarWriter]::new($stream, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
try {
    foreach ($directory in @($directories | Sort-Object)) {
        $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::Directory, $directory)
        $entry.Mode = 493; $entry.Uid = 0; $entry.Gid = 0; $entry.UserName = ''; $entry.GroupName = ''; $entry.ModificationTime = $commitTime
        $writer.WriteEntry($entry)
    }
    foreach ($member in $gitMembers) {
        $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $member.path)
        $entry.Mode = if ($member.git_mode -ceq '100755') { 493 } else { 420 }
        $entry.Uid = 0; $entry.Gid = 0; $entry.UserName = ''; $entry.GroupName = ''; $entry.ModificationTime = $commitTime
        $member.sha256 = Write-GitBlob $writer $member $entry
    }
}
finally { $writer.Dispose(); $stream.Dispose() }

$archiveMembers = @{}; $archiveDirectories = @{}
$stream = [IO.File]::OpenRead($archive); $reader = [System.Formats.Tar.TarReader]::new($stream)
try {
    while ($entry = $reader.GetNextEntry()) {
        if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) {
            if ($archiveDirectories.ContainsKey($entry.Name)) { throw "Duplicate TAR directory: $($entry.Name)" }
            $archiveDirectories[$entry.Name] = [int]$entry.Mode; continue
        }
        if ($entry.EntryType -ne [System.Formats.Tar.TarEntryType]::RegularFile -or $archiveMembers.ContainsKey($entry.Name)) { throw "Invalid TAR source member: $($entry.Name)" }
        $data = if ($null -eq $entry.DataStream) { [IO.Stream]::Null } else { $entry.DataStream }
        $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($data)).ToLowerInvariant()
        $archiveMembers[$entry.Name] = [pscustomobject]@{ length = $entry.Length; sha256 = $sha; mode = [int]$entry.Mode }
    }
}
finally { $reader.Dispose(); $stream.Dispose() }

if ($archiveDirectories.Count -ne $directories.Count -or $archiveMembers.Count -ne $gitMembers.Count) { throw 'The generated archive member set is incomplete.' }
foreach ($directory in $directories) {
    if (-not $archiveDirectories.ContainsKey($directory) -or $archiveDirectories[$directory] -ne 493) { throw "Generated archive directory mismatch: $directory" }
}
$members = [Collections.Generic.List[object]]::new()
foreach ($git in $gitMembers) {
    if (-not $archiveMembers.ContainsKey($git.path)) { throw "Git member missing from archive: $($git.path)" }
    $tar = $archiveMembers[$git.path]; $sha = $git.sha256; $mode = if ($git.git_mode -ceq '100755') { 493 } else { 420 }
    if ($tar.mode -ne $mode -or $tar.length -ne $git.length -or $tar.sha256 -cne $sha) { throw "Archive member does not match its raw Git object: $($git.path)" }
    $members.Add([pscustomobject]@{
        path = $git.path; git_object_id = $git.git_object_id; git_mode = $git.git_mode
        git_length = $git.length; git_sha256 = $sha
        archive_length = $tar.length; archive_sha256 = $tar.sha256; archive_mode = $tar.mode
        transformation = 'none'
    })
}
$document = [ordered]@{
    schema = 1; product = 'Sync Shark'; commit = $Commit; tree = $tree; archive_format = 'git-object-tar'
    transformation = 'none'; member_count = $members.Count; members = $members
}
[IO.File]::WriteAllText($manifest, ($document | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{
    ok = $true; commit = $Commit; tree = $tree; archive = $archive
    archive_sha256 = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    manifest = $manifest; manifest_sha256 = (Get-FileHash $manifest -Algorithm SHA256).Hash.ToLowerInvariant()
    member_count = $members.Count
} | ConvertTo-Json -Depth 5
