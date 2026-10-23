param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath($Repository)
$evidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if (-not (Test-Path -LiteralPath $evidenceDirectory)) { New-Item -ItemType Directory -Path $evidenceDirectory | Out-Null }
$builder = Join-Path $repository 'tools\New-SyncSharkSourceArchive.ps1'
$verifier = Join-Path $repository 'tools\Test-SyncSharkSourceArchive.ps1'
foreach ($path in @($builder, $verifier)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing source-archive tool: $path" } }
$firstDirectory = Join-Path $evidenceDirectory 'source-build-a'
$secondDirectory = Join-Path $evidenceDirectory 'source-build-b'
New-Item -ItemType Directory -Path $firstDirectory, $secondDirectory -Force | Out-Null
$probe = Join-Path $repository 'source-archive-untracked-probe.tmp'
$originalAutocrlf = (& git -C $repository config --local --get core.autocrlf 2>$null)
$hadAutocrlf = $LASTEXITCODE -eq 0
try {
    [IO.File]::WriteAllText($probe, 'untracked checkout probe', [Text.UTF8Encoding]::new($false))
    & git -C $repository config --local core.autocrlf true
    if ($LASTEXITCODE -ne 0) { throw 'Could not set the first local line-ending test condition.' }
    $first = (& $builder -Repository $repository -Commit $Commit -OutputDirectory $firstDirectory) | ConvertFrom-Json
    & git -C $repository config --local core.autocrlf false
    if ($LASTEXITCODE -ne 0) { throw 'Could not set the second local line-ending test condition.' }
    $second = (& $builder -Repository $repository -Commit $Commit -OutputDirectory $secondDirectory) | ConvertFrom-Json
}
finally {
    if ($hadAutocrlf) { & git -C $repository config --local core.autocrlf $originalAutocrlf | Out-Null }
    else { & git -C $repository config --local --unset core.autocrlf 2>$null | Out-Null }
    if (Test-Path -LiteralPath $probe -PathType Leaf) { Remove-Item -LiteralPath $probe -Force }
}
if ($first.archive_sha256 -cne $second.archive_sha256 -or $first.manifest_sha256 -cne $second.manifest_sha256) {
    throw 'Source archive output changed with checkout dirt or core.autocrlf.'
}
$verified = (& $verifier -Repository $repository -Archive $first.archive -Manifest $first.manifest) | ConvertFrom-Json
if (-not $verified.ok -or $verified.commit -cne $Commit) { throw 'Untouched source archive was not independently verified.' }

function Read-TarMembers([string]$Path) {
    $members = [Collections.Generic.List[object]]::new()
    $stream = [IO.File]::OpenRead($Path); $reader = [System.Formats.Tar.TarReader]::new($stream)
    try {
        while ($entry = $reader.GetNextEntry()) {
            if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::GlobalExtendedAttributes) {
                $attributes = [ordered]@{}
                foreach ($pair in $entry.GlobalExtendedAttributes.GetEnumerator()) { $attributes[$pair.Key] = $pair.Value }
                $members.Add([pscustomobject]@{ Type = 'global'; Name = $entry.Name; Mode = [int]$entry.Mode; Bytes = $null; Attributes = $attributes })
                continue
            }
            if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) {
                $members.Add([pscustomobject]@{ Type = 'directory'; Name = $entry.Name; Mode = [int]$entry.Mode; Bytes = $null; Attributes = $null })
                continue
            }
            if ($entry.EntryType -ne [System.Formats.Tar.TarEntryType]::RegularFile) { throw "Unsupported TAR entry: $($entry.Name)" }
            $memory = [IO.MemoryStream]::new()
            try { $entry.DataStream.CopyTo($memory); $bytes = $memory.ToArray() } finally { $memory.Dispose() }
            $members.Add([pscustomobject]@{ Type = 'file'; Name = $entry.Name; Mode = [int]$entry.Mode; Bytes = $bytes; Attributes = $null })
        }
    }
    finally { $reader.Dispose(); $stream.Dispose() }
    $members
}

function Write-TarMembers([string]$Path, [object[]]$Members) {
    $stream = [IO.File]::Create($Path); $writer = [System.Formats.Tar.TarWriter]::new($stream, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
    try {
        foreach ($member in $Members) {
            if ($member.Type -ceq 'global') {
                $pairs = [Collections.Generic.List[Collections.Generic.KeyValuePair[string,string]]]::new()
                foreach ($key in $member.Attributes.Keys) { $pairs.Add([Collections.Generic.KeyValuePair[string,string]]::new([string]$key, [string]$member.Attributes[$key])) }
                $entry = [System.Formats.Tar.PaxGlobalExtendedAttributesTarEntry]::new($pairs)
            }
            elseif ($member.Type -ceq 'directory') { $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::Directory, [string]$member.Name) }
            else { $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, [string]$member.Name) }
            $entry.Mode = [int]$member.Mode
            if ($member.Type -ceq 'file') {
                $entry.DataStream = [IO.MemoryStream]::new([byte[]]$member.Bytes, $false)
                try { $writer.WriteEntry($entry) } finally { $entry.DataStream.Dispose() }
            }
            else { $writer.WriteEntry($entry) }
        }
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

function Copy-Manifest([string]$Name, [scriptblock]$Mutate) {
    $document = Get-Content -LiteralPath $first.manifest -Raw | ConvertFrom-Json -Depth 30
    if ($Mutate) { & $Mutate $document }
    $path = Join-Path $evidenceDirectory ($Name + '.members.json')
    [IO.File]::WriteAllText($path, ($document | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    $path
}

function Assert-VerifierRejects([string]$Name, [string]$Archive, [string]$Manifest) {
    $accepted = $false; $errorText = $null
    try { $null = & $verifier -Repository $repository -Archive $Archive -Manifest $Manifest; $accepted = $true }
    catch { $errorText = $_.Exception.Message }
    if ($accepted) { throw "Source verifier accepted fault: $Name" }
    [ordered]@{ case = $Name; result = 'rejected'; error = $errorText; archive_sha256 = (Get-FileHash $Archive -Algorithm SHA256).Hash.ToLowerInvariant(); manifest_sha256 = (Get-FileHash $Manifest -Algorithm SHA256).Hash.ToLowerInvariant() }
}

$members = @(Read-TarMembers $first.archive)
$fileIndices = @(for ($index = 0; $index -lt $members.Count; $index++) { if ($members[$index].Type -ceq 'file') { $index } })
if ($fileIndices.Count -eq 0) { throw 'The source archive has no regular source members.' }
$cases = [Collections.Generic.List[object]]::new()

$variant = @($members | ForEach-Object { [pscustomobject]@{ Type = $_.Type; Name = $_.Name; Mode = $_.Mode; Bytes = if ($_.Bytes) { [byte[]]$_.Bytes.Clone() } else { $null }; Attributes = $_.Attributes } })
$variant[$fileIndices[0]].Bytes[0] = $variant[$fileIndices[0]].Bytes[0] -bxor 1
$path = Join-Path $evidenceDirectory 'altered-member.tar'; Write-TarMembers $path $variant
$cases.Add((Assert-VerifierRejects 'altered-member-bytes' $path $first.manifest))

$omitted = @($members | Where-Object { $_ -ne $members[$fileIndices[0]] })
$path = Join-Path $evidenceDirectory 'omitted-member.tar'; Write-TarMembers $path $omitted
$cases.Add((Assert-VerifierRejects 'omitted-member' $path $first.manifest))

$extra = @($members) + [pscustomobject]@{ Type = 'file'; Name = 'source-verifier-extra.txt'; Mode = 420; Bytes = [Text.Encoding]::UTF8.GetBytes('extra'); Attributes = $null }
$path = Join-Path $evidenceDirectory 'extra-member.tar'; Write-TarMembers $path $extra
$cases.Add((Assert-VerifierRejects 'extra-member' $path $first.manifest))

$variant = @($members | ForEach-Object { [pscustomobject]@{ Type = $_.Type; Name = $_.Name; Mode = $_.Mode; Bytes = $_.Bytes; Attributes = $_.Attributes } })
$variant[$fileIndices[0]].Mode = if ($variant[$fileIndices[0]].Mode -eq 420) { 493 } else { 420 }
$path = Join-Path $evidenceDirectory 'mode-only-mismatch.tar'; Write-TarMembers $path $variant
$cases.Add((Assert-VerifierRejects 'mode-only-mismatch' $path $first.manifest))

$manifestPath = Copy-Manifest 'commit-tree-mismatch' { param($document); $document.tree = ('0' * 40) }
$cases.Add((Assert-VerifierRejects 'manifest-commit-tree-mismatch' $first.archive $manifestPath))

$textIndex = -1
foreach ($index in $fileIndices) {
    if ($members[$index].Name -match '\.(md|cs|xaml|json|ps1)$') {
        $text = [Text.Encoding]::UTF8.GetString($members[$index].Bytes)
        if ($text.Contains("`n") -and -not $text.Contains("`r`n")) { $textIndex = $index; break }
    }
}
if ($textIndex -lt 0) { throw 'No LF-only source member was available for line-ending fault injection.' }
$variant = @($members | ForEach-Object { [pscustomobject]@{ Type = $_.Type; Name = $_.Name; Mode = $_.Mode; Bytes = $_.Bytes; Attributes = $_.Attributes } })
$variant[$textIndex].Bytes = [Text.Encoding]::UTF8.GetBytes(([Text.Encoding]::UTF8.GetString($variant[$textIndex].Bytes)).Replace("`n", "`r`n"))
$path = Join-Path $evidenceDirectory 'crlf-converted-member.tar'; Write-TarMembers $path $variant
$cases.Add((Assert-VerifierRejects 'lf-to-crlf-checkout-conversion' $path $first.manifest))

$receipt = [ordered]@{
    result = 'PASS'; commit = $Commit; tree = $first.tree; member_count = $first.member_count
    builder_sha256 = (Get-FileHash $builder -Algorithm SHA256).Hash.ToLowerInvariant()
    verifier_sha256 = (Get-FileHash $verifier -Algorithm SHA256).Hash.ToLowerInvariant()
    archive_sha256 = $first.archive_sha256; manifest_sha256 = $first.manifest_sha256
    independent_builds_identical = $true; dirty_worktree_and_untracked_probe_ignored = $true
    core_autocrlf_variation_ignored = $true; untouched_archive_verified = $true; cases = $cases
}
$receiptPath = Join-Path $evidenceDirectory 'source-archive-fault-matrix.json'
[IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
$receipt | ConvertTo-Json -Depth 6
