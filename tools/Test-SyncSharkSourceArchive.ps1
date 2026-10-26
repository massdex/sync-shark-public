param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$Archive,
    [Parameter(Mandatory = $true)][string]$Manifest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath($Repository); $archive = [IO.Path]::GetFullPath($Archive); $manifest = [IO.Path]::GetFullPath($Manifest)
$document = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json -Depth 20
if ($document.schema -ne 1 -or $document.transformation -cne 'none' -or $document.archive_format -cne 'git-object-tar') {
    throw 'Unknown source-manifest schema or transformation.'
}
$commit = (& git -C $repository rev-parse "$($document.commit)^{commit}").Trim()
$tree = (& git -C $repository rev-parse "$($document.commit)^{tree}").Trim()
if ($LASTEXITCODE -ne 0 -or $commit -cne $document.commit -or $tree -cne $document.tree) { throw 'Manifest commit/tree identity mismatch.' }

function Read-GitBlob([string]$ObjectId) {
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.ArgumentList.Add('-C'); $start.ArgumentList.Add($repository); $start.ArgumentList.Add('cat-file'); $start.ArgumentList.Add('blob'); $start.ArgumentList.Add($ObjectId)
    $start.UseShellExecute=$false; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start); $memory=[IO.MemoryStream]::new()
    try { $process.StandardOutput.BaseStream.CopyTo($memory); $process.WaitForExit(); if ($process.ExitCode -ne 0) { throw 'git cat-file failed.' }; $memory.ToArray() }
    finally { $memory.Dispose(); $process.Dispose() }
}
function Hash-Bytes([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }

$treeMembers = [ordered]@{}
$expectedDirectories = [ordered]@{}
foreach ($line in & git -C $repository ls-tree -r --full-tree $commit) {
    if ($line -notmatch '^(?<mode>[0-9]{6}) blob (?<object>[0-9a-f]{40})\t(?<path>.+)$') { throw "Unsupported Git tree member: $line" }
    $treeMembers[$Matches.path] = [pscustomobject]@{ mode=$Matches.mode; object=$Matches.object }
    $parts = $Matches.path.Split('/')
    for ($index = 1; $index -lt $parts.Count; $index++) { $expectedDirectories[(@($parts[0..($index - 1)]) -join '/') + '/'] = 493 }
}
$declared = [ordered]@{}
foreach ($member in $document.members) {
    if ($declared.Contains($member.path)) { throw "Duplicate manifest member: $($member.path)" }
    $declared[$member.path] = $member
}
if ($document.member_count -ne $treeMembers.Count -or $declared.Count -ne $treeMembers.Count) { throw 'Manifest member set is incomplete.' }

$archiveMembers = [ordered]@{}
$archiveDirectories = [ordered]@{}
$stream=[IO.File]::OpenRead($archive); $reader=[System.Formats.Tar.TarReader]::new($stream)
try {
    while ($entry=$reader.GetNextEntry()) {
        if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) {
            if ($archiveDirectories.Contains($entry.Name)) { throw "Duplicate TAR directory: $($entry.Name)" }
            $archiveDirectories[$entry.Name] = [int]$entry.Mode; continue
        }
        if ($entry.EntryType -ne [System.Formats.Tar.TarEntryType]::RegularFile -or $archiveMembers.Contains($entry.Name)) { throw "Invalid TAR member: $($entry.Name)" }
        $memory=[IO.MemoryStream]::new(); try { $entry.DataStream.CopyTo($memory); $bytes=$memory.ToArray() } finally { $memory.Dispose() }
        $archiveMembers[$entry.Name]=[pscustomobject]@{ bytes=$bytes; mode=[int]$entry.Mode }
    }
} finally { $reader.Dispose(); $stream.Dispose() }
if ($archiveMembers.Count -ne $treeMembers.Count -or $archiveDirectories.Count -ne $expectedDirectories.Count) { throw 'Archive member set differs from Git.' }
foreach ($directory in $expectedDirectories.Keys) {
    if (-not $archiveDirectories.Contains($directory) -or $archiveDirectories[$directory] -ne $expectedDirectories[$directory]) { throw "Archive directory failed Git authentication: $directory" }
}
foreach ($path in $treeMembers.Keys) {
    if (-not $declared.Contains($path) -or -not $archiveMembers.Contains($path)) { throw "Missing source member: $path" }
    $git=$treeMembers[$path]; $claim=$declared[$path]; $tar=$archiveMembers[$path]; $bytes=Read-GitBlob $git.object
    $sha=Hash-Bytes $bytes; $mode=if ($git.mode -ceq '100755') {493} else {420}
    if ($claim.transformation -cne 'none' -or $claim.git_object_id -cne $git.object -or $claim.git_mode -cne $git.mode -or
        $claim.git_length -ne $bytes.Length -or $claim.git_sha256 -cne $sha -or
        $claim.archive_length -ne $bytes.Length -or $claim.archive_sha256 -cne $sha -or $claim.archive_mode -ne $mode -or
        $tar.mode -ne $mode -or $tar.bytes.Length -ne $bytes.Length -or (Hash-Bytes $tar.bytes) -cne $sha) {
        throw "Source member failed independent Git authentication: $path"
    }
}
[pscustomobject]@{ ok=$true; commit=$commit; tree=$tree; member_count=$treeMembers.Count; transformation='none' } | ConvertTo-Json -Compress
