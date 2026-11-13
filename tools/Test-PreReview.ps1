#requires -Version 7.0
<#
.SYNOPSIS
    Pre-review gate for a Sync Shark writer branch. Runs the mechanical checks that
    reviewers keep finding by hand and prints PASS, FAIL or INCOMPLETE. It never edits a
    tracked file.

.DESCRIPTION
    Given a base commit and the head commit checked out in -Repo, the gate:
      1. with -RunTests, builds the Engine tests in Debug once (the Engine is a
         project reference);
      2. with -RunTests, runs the existing `dependencies` group and every group
         CheckRegistry.cs maps to a changed test or src file;
      3. runs tools/ci/check-spec-ratchet.py as CI does and reports the size rows
         and structure-debt.md sections that need a refresh for changed src files;
      3b. runs the deterministic checks in tools/ci/prereview_checks.py (receipt
         freshness, grown units, test registration), each with a file:line;
      3c. with -Owners, fails when the diff touches a path owned by another lane;
      3d. reports repeated long expressions as an advisory;
      4. lists design-doc lines to check for added/removed src names;
      5. with -Report or -Reports, flags file:line citations whose line no longer
         holds the cited symbol (optionally against -CitationsSha);
      6. requires ratchet, Core layout and Engine dependencies/literals/strings
         evidence; missing or skipped checks print INCOMPLETE and exit 2.

    This is a helper, not a replacement for review: it reports lines to look at.

.PARAMETER Base
    The commit the writer branched from (e.g. the lane's base).

.PARAMETER Head
    The writer's commit. The checkout in -Repo must be at this commit.

.PARAMETER Report
    Optional writer report. File:line citations are checked against the checkout
    unless -CitationsSha is also given.

.PARAMETER Reports
    Optional list of Markdown reports. Their citations are checked against
    -CitationsSha, which defaults to -Head.

.PARAMETER CitationsSha
    The commit to resolve citations against. When omitted, -Report uses the
    checkout and -Reports use -Head.

.PARAMETER Owners
    Optional JSON map of { path glob: owning lane }. With -Lane, a changed path
    owned by another lane fails the gate.

.PARAMETER Lane
    This lane's name, matched against -Owners.

.PARAMETER Controls
    Optional JSON map of { control: { witness: Class.Method, groups: [...] } }.
    Each control's witness check must be registered in one of the groups the
    control is submitted to (its witness group).

.PARAMETER Repo
    The checkout to inspect. Defaults to the repository that contains this script.

.PARAMETER ScratchRoot
    TEMP/TMP/SYNCSHARK_TEST_ROOT for the check processes. Defaults to a folder under
    LOCALAPPDATA so nothing is created at a drive root.

.PARAMETER RunTests
    Build and run the selected check groups locally. The default gate runs no
    tests; tests run in CI. The default run prints the CI group selection and
    returns INCOMPLETE because mandatory fence execution is missing.

.PARAMETER StaticFence
    Explicitly run mandatory fences against existing fresh Engine and Core test
    binaries, without building. Missing or stale binaries yield INCOMPLETE.

.PARAMETER IncludeLive
    With -RunTests, also run the live-route groups (tools/ci/live-groups.txt).
    They call the real account, so they are skipped unless this is passed.

.PARAMETER SkipRatchet
    Diagnostic-only skip; always yields INCOMPLETE and exit 2, never clearance.

.PARAMETER RatchetTimeoutSeconds
    Give up on the ratchet after this many seconds. Default 900.

.PARAMETER SelfTest
    Run the script's own fixtures and exit.

.EXAMPLE
    ./tools/Test-PreReview.ps1 -Base 83440cb0f -Head 45ef755f7 -Report run/report.md

.EXAMPLE
    ./tools/Test-PreReview.ps1 -Base 83440cb0f -Head 45ef755f7 -RunTests
#>
[CmdletBinding()]
param(
    [string]$Base,
    [string]$Head = 'HEAD',
    [string]$Report,
    [string[]]$Reports,
    [string]$CitationsSha,
    [string]$Owners,
    [string]$Lane,
    [string]$Controls,
    [string]$Repo = (Join-Path $PSScriptRoot '..'),
    [string]$ScratchRoot = (Join-Path $env:LOCALAPPDATA 'SyncShark\prereview'),
    [switch]$RunTests,
    [switch]$IncludeLive,
    [switch]$SkipRatchet,
    [switch]$StaticFence,
    [int]$RatchetTimeoutSeconds = 900,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ci/MandatoryChecks.ps1')
$script:RequiredChecks = @('ratchet', 'layout') + @(Get-MandatoryEngineGroups)
$script:CheckResults = @{}
$script:Attention = [System.Collections.Generic.List[string]]::new()
$script:Assertions = 0
$script:Utf8 = [System.Text.UTF8Encoding]::new($false)

# One owner for the top-level type name pattern used by the doc-drift scan.
$script:TypePattern = '(?m)^(?:public |internal |abstract |sealed |static |partial |readonly )*(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(@?[A-Z]\w*)'

# ---------------------------------------------------------------- helpers

function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [hashtable]$Environment,
        [int]$TimeoutSeconds = 0
    )
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FileName
    $psi.Arguments = $Arguments
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = $script:Utf8
    $psi.StandardErrorEncoding = $script:Utf8
    if ($Environment) { foreach ($key in $Environment.Keys) { $psi.Environment[$key] = $Environment[$key] } }
    $process = [System.Diagnostics.Process]::Start($psi)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $timedOut = $false
    if ($TimeoutSeconds -gt 0) {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            try { $process.Kill($true) } catch { }
        }
    }
    $process.WaitForExit()
    [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output   = ($stdout.Result + $stderr.Result)
        TimedOut = $timedOut
    }
}

function Invoke-Git {
    param([string]$Root, [string[]]$ArgumentList)
    $result = Invoke-Native -FileName 'git' -Arguments (($ArgumentList | ForEach-Object { '"' + $_ + '"' }) -join ' ') -WorkingDirectory $Root
    if ($result.ExitCode -ne 0) { throw "git $($ArgumentList -join ' ') failed: $($result.Output.Trim())" }
    $result.Output
}

function Get-ChangedFiles {
    param([string]$Root, [string]$BaseCommit, [string]$HeadCommit)
    # --no-renames keeps a rename as its delete and add, which is what the
    # consumers below need; it also avoids parsing status scores.
    $text = Invoke-Git -Root $Root -ArgumentList @('diff', '--name-status', '--no-renames', "$BaseCommit..$HeadCommit")
    foreach ($line in $text -split "`n") {
        if (-not $line.Trim()) { continue }
        $parts = $line -split "`t"
        [pscustomobject]@{ Status = $parts[0]; Path = ($parts[1..($parts.Count - 1)] -join "`t") }
    }
}

function Get-RegistryEntries {
    param([string]$Text)
    # One owner of the CheckRegistry text format. Blank // and /* */ comments but
    # keep "..." string literals, so a URL or "/*" inside a string is not a comment.
    # shortcut: text parse; upgrade if CheckRegistry stops using one ["name"] = [...] per group.
    $clean = [regex]::Replace($Text, '"(?:[^"\\\n]|\\.)*"|//[^\n]*|/\*[\s\S]*?\*/', {
        param($match)
        if ($match.Value[0] -eq '"') { $match.Value } else { ' ' }
    })
    $entries = [System.Collections.Generic.List[object]]::new()
    $starts = [regex]::Matches($clean, '(?m)^\s*\["(?<group>[^"]+)"\]\s*=')
    for ($i = 0; $i -lt $starts.Count; $i++) {
        $start = $starts[$i].Index
        $end = if ($i + 1 -lt $starts.Count) { $starts[$i + 1].Index } else { $clean.Length }
        $chunk = $clean.Substring($start, $end - $start)
        $group = $starts[$i].Groups['group'].Value
        foreach ($entry in [regex]::Matches($chunk, '(?<![A-Za-z0-9_.])([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+(?:\(\))?)')) {
            $token = $entry.Groups[1].Value -replace '\(\)$', ''
            $entries.Add([pscustomobject]@{ Group = $group; Token = $token })
        }
    }
    $entries
}

function Get-CheckRegistryIndex {
    param([string]$Text)
    # Groups for CI selection and the method tokens the Python registration check
    # consumes, both from one parse of the one parser.
    $groups = @{}
    $methods = @{}
    foreach ($entry in (Get-RegistryEntries -Text $Text)) {
        $cut = $entry.Token.LastIndexOf('.')
        if ($cut -gt 0) {
            if (-not $groups.ContainsKey($entry.Group)) { $groups[$entry.Group] = [System.Collections.Generic.List[string]]::new() }
            $groups[$entry.Group].Add($entry.Token.Substring(0, $cut))
        }
        if (-not $methods.ContainsKey($entry.Token)) { $methods[$entry.Token] = [System.Collections.Generic.List[string]]::new() }
        if (-not $methods[$entry.Token].Contains($entry.Group)) { $methods[$entry.Token].Add($entry.Group) }
    }
    foreach ($key in @($groups.Keys)) { $groups[$key] = @($groups[$key] | Sort-Object -Unique) }
    [pscustomobject]@{ Groups = $groups; Methods = $methods }
}

function Test-IsSharedTestHelper {
    param([string]$Path)
    # Files shared by many groups: their change has no single group to select.
    if ($Path -match '^tests/SyncShark\.Engine\.Tests/TestDoubles/') { return $true }
    if ($Path -match '^tests/SyncShark\.Engine\.Tests/Scenarios/') { return $true }
    $name = [IO.Path]::GetFileName($Path)
    if ($name -eq 'Program.cs' -or $name -eq 'CheckRegistry.cs' -or $name -eq 'ScanTestSettings.cs') { return $true }
    $name -like 'Check*.cs'
}

function Get-GroupsForChangedFiles {
    param([object[]]$ChangedFiles, [hashtable]$Groups)
    $selected = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($group in (Get-MandatoryEngineGroups)) { [void]$selected.Add($group) }
    $unmapped = [System.Collections.Generic.List[string]]::new()
    $shared = [System.Collections.Generic.List[string]]::new()
    $outside = [System.Collections.Generic.List[string]]::new()
    foreach ($file in $ChangedFiles) {
        if ($file.Status -eq 'D') { continue }
        $isTest = $file.Path -match '^tests/SyncShark\.Engine\.Tests/(?<rel>.+)\.cs$'
        if ($isTest) {
            $classKey = $Matches['rel'].Replace('/', '.')
        } elseif ($file.Path -match '^src/SyncShark\.Engine/(?<rel>.+)\.cs$') {
            # The project convention: src/<rel>.cs is covered by <rel>Checks.
            $classKey = $Matches['rel'].Replace('/', '.') + 'Checks'
        } elseif ($file.Path -match '^(?:src|apps)/') {
            # Not in the Engine CheckRegistry; core/ui/build selection is manual.
            $outside.Add($file.Path)
            continue
        } else {
            continue
        }
        $found = $false
        foreach ($group in $Groups.Keys) {
            if ($Groups[$group] -contains $classKey) { [void]$selected.Add($group); $found = $true }
        }
        if (-not $found -and $isTest) {
            # A partial file carries its class name as a prefix: CallBudgetChecks.Speed.
            $baseName = [IO.Path]::GetFileNameWithoutExtension($file.Path).Split('.')[0]
            foreach ($group in $Groups.Keys) {
                foreach ($class in $Groups[$group]) {
                    if ($class.Split('.')[-1] -eq $baseName) { [void]$selected.Add($group); $found = $true }
                }
            }
        }
        if (-not $found) {
            if (-not $isTest) { $unmapped.Add($file.Path) }
            elseif (Test-IsSharedTestHelper -Path $file.Path) { $shared.Add($file.Path) }
            else { $unmapped.Add($file.Path) }
        }
    }
    [pscustomobject]@{ Groups = @($selected); Unmapped = @($unmapped); SharedHelpers = @($shared); OutsideRegistry = @($outside) }
}

function Get-DeclarationNamesFromLines {
    param([string[]]$Lines)
    $names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $methodPattern = '^\s*(?:public|private|protected|internal)\b[^=(]*?(?<name>@?[A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\('
    $skip = @('if', 'for', 'foreach', 'while', 'switch', 'catch', 'using', 'lock', 'return', 'new', 'typeof', 'nameof', 'get', 'set', 'await', 'throw')
    foreach ($line in $Lines) {
        foreach ($match in [regex]::Matches($line, $script:TypePattern)) { [void]$names.Add($match.Groups[1].Value.TrimStart('@')) }
        $method = [regex]::Match($line, $methodPattern)
        if ($method.Success) {
            $name = $method.Groups['name'].Value.TrimStart('@')
            if ($name -cmatch '^[A-Z]' -and $skip -notcontains $name) { [void]$names.Add($name) }
        }
    }
    @($names)
}

function Get-TypeNames {
    param([string]$Text)
    if (-not $Text) { return @() }
    @([regex]::Matches($Text, $script:TypePattern) |
        ForEach-Object { $_.Groups[1].Value.TrimStart('@') } | Sort-Object -Unique)
}

function Get-HeadFileText {
    param([string]$Root, [string]$Commit, [string]$Path)
    $result = Invoke-Native -FileName 'git' -Arguments ('show "' + $Commit + ':' + $Path + '"') -WorkingDirectory $Root
    if ($result.ExitCode -ne 0) { return $null }
    $result.Output
}

function Add-DocNameFindings {
    param([object[]]$DocContent, [string[]]$Names, [string]$Kind, [System.Collections.Generic.List[object]]$Findings)
    if ($Names.Count -eq 0) { return }
    $pattern = '\b(?:' + (($Names | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\b'
    foreach ($doc in $DocContent) {
        $lineNumber = 0
        foreach ($line in $doc.Lines) {
            $lineNumber++
            $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
            foreach ($match in [regex]::Matches($line, $pattern)) {
                if (-not $seen.Add($match.Value)) { continue }
                $Findings.Add([pscustomobject]@{ Kind = $Kind; Name = $match.Value; File = $doc.File; Line = $lineNumber; Text = $line.Trim() })
            }
        }
    }
}

function Get-DocDrift {
    param([string]$Root, [string]$BaseCommit, [string]$HeadCommit, [object[]]$ChangedFiles)
    # shortcut: top-level types and access-modified methods only; upgrade if a design doc starts naming a nested type.
    $removed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $touched = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $srcFiles = @($ChangedFiles | Where-Object { $_.Path -like 'src/*.cs' -and $_.Status -ne 'D' })
    foreach ($file in $srcFiles) {
        $headText = Get-HeadFileText -Root $Root -Commit $HeadCommit -Path $file.Path
        $baseText = Get-HeadFileText -Root $Root -Commit $BaseCommit -Path $file.Path
        $headTypes = @(Get-TypeNames -Text $headText)
        $baseTypes = @(Get-TypeNames -Text $baseText)
        foreach ($name in $headTypes) { [void]$touched.Add($name) }
        foreach ($name in $baseTypes) { if ($headTypes -notcontains $name) { [void]$removed.Add($name) } }
    }
    $deleted = @($ChangedFiles | Where-Object { $_.Path -like 'src/*.cs' -and $_.Status -eq 'D' })
    foreach ($file in $deleted) {
        foreach ($name in (Get-TypeNames -Text (Get-HeadFileText -Root $Root -Commit $BaseCommit -Path $file.Path))) { [void]$removed.Add($name) }
    }
    # Method names missing from the changed src files: an edit keeps its name on a
    # `+` line, so subtract the names the change still declares.
    $diff = Invoke-Git -Root $Root -ArgumentList @('diff', '--no-renames', '-U0', "$BaseCommit..$HeadCommit", '--', 'src')
    $removedLines = [System.Collections.Generic.List[string]]::new()
    $addedLines = [System.Collections.Generic.List[string]]::new()
    $current = ''
    foreach ($line in ($diff -split "`n")) {
        if ($line -like '+++ b/*') { $current = $line.Substring(6); continue }
        if ($current -notlike '*.cs') { continue }
        if ($line -like '-*' -and $line -notlike '---*') { $removedLines.Add($line.Substring(1)) }
        elseif ($line -like '+*' -and $line -notlike '+++*') { $addedLines.Add($line.Substring(1)) }
    }
    foreach ($name in (Get-DeclarationNamesFromLines -Lines $removedLines)) { [void]$removed.Add($name) }
    foreach ($name in (Get-DeclarationNamesFromLines -Lines $addedLines)) { [void]$removed.Remove($name) }
    # A name still declared in head src only moved; it is not a removal.
    foreach ($name in @($removed | Sort-Object)) {
        $grep = Invoke-Native -FileName 'git' -WorkingDirectory $Root -Arguments ('grep -q -w -e "' + $name + '" "' + $HeadCommit + '" -- src')
        if ($grep.ExitCode -eq 0) { [void]$removed.Remove($name) }
    }

    # Read every design doc once, then match all names per line in one alternation.
    $docContent = [System.Collections.Generic.List[object]]::new()
    foreach ($doc in (Get-ChildItem -LiteralPath (Join-Path $Root 'docs/design') -Filter '*.md' -File -ErrorAction SilentlyContinue)) {
        $docContent.Add([pscustomobject]@{ File = $doc.FullName; Lines = [IO.File]::ReadAllLines($doc.FullName) })
    }
    $findings = [System.Collections.Generic.List[object]]::new()
    Add-DocNameFindings -DocContent $docContent -Names @($removed | Sort-Object) -Kind 'removed' -Findings $findings
    Add-DocNameFindings -DocContent $docContent -Names @($touched | Sort-Object) -Kind 'touched' -Findings $findings
    $findings
}

function New-RatchetRow {
    param([string]$Path, [string]$Category, [string]$Verdict, [string]$Line)
    $range = [regex]::Match($Line, '(?<old>\d+)\s*->\s*(?<new>\d+)')
    [pscustomobject]@{
        Path     = $Path
        Category = $Category
        Verdict  = $Verdict
        Old      = $(if ($range.Success) { [int]$range.Groups['old'].Value } else { $null })
        New      = $(if ($range.Success) { [int]$range.Groups['new'].Value } else { $null })
    }
}

function Convert-RatchetOutput {
    param([string]$Text, [string[]]$TouchedPaths)
    $rows = [System.Collections.Generic.List[object]]::new()
    $totals = [System.Collections.Generic.List[object]]::new()
    $newRows = [System.Collections.Generic.List[object]]::new()
    $reviewed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $register = [System.Collections.Generic.List[object]]::new()
    $touched = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($path in $TouchedPaths) { [void]$touched.Add($path) }
    foreach ($line in ($Text -split "`n")) {
        $line = $line.TrimEnd()
        $size = [regex]::Match($line, '^(?:REVIEW\s+(?<verdict>REQUIRED|[A-Z-]+)|(?<status>NEW|GROWN))\s*\|?\s*(?<path>[^|]+?)\s*\|\s*(?<category>[^|]+?)\s*\|')
        $sizePath = if ($size.Success) { $size.Groups['path'].Value.Trim() } else { '' }
        # @total rows are repository-wide; keep them apart from the lane's own rows.
        if ($size.Success -and ($touched.Contains($sizePath) -or $sizePath -eq '@total')) {
            $path = $sizePath
            $category = $size.Groups['category'].Value.Trim()
            if ($size.Groups['verdict'].Success) {
                # Any current verdict settles the row; only REQUIRED needs a refresh.
                [void]$reviewed.Add("$path|$category")
                if ($size.Groups['verdict'].Value.Trim() -eq 'REQUIRED') {
                    $row = New-RatchetRow -Path $path -Category $category -Verdict 'REQUIRED' -Line $line
                    if ($path -eq '@total') { $totals.Add($row) } else { $rows.Add($row) }
                }
            } else {
                $newRows.Add((New-RatchetRow -Path $path -Category $category -Verdict $size.Groups['status'].Value.Trim() -Line $line))
            }
        }
        $reg = [regex]::Match($line, '^REGISTER review required:\s*(?<path>[^\s(]+)\s*(?<reason>.*)$')
        if ($reg.Success -and $touched.Contains($reg.Groups['path'].Value.Trim())) {
            $register.Add([pscustomobject]@{ Path = $reg.Groups['path'].Value.Trim(); Reason = $reg.Groups['reason'].Value.Trim() })
        }
    }
    foreach ($row in $newRows) {
        if ($reviewed.Contains("$($row.Path)|$($row.Category)")) { continue }
        if ($row.Path -eq '@total') { $totals.Add($row) } else { $rows.Add($row) }
    }
    [pscustomobject]@{ Rows = @($rows); Totals = @($totals); Register = @($register) }
}

function Get-RatchetAttention {
    param([object]$Ratchet, [int]$TimeoutSeconds)
    # Missing release evidence is incomplete; neither non-zero outcome grants clearance.
    if ($Ratchet.TimedOut) { return "spec ratchet timed out after $TimeoutSeconds s" }
    if ($Ratchet.ExitCode -eq 2) { return 'spec ratchet incomplete: trusted release evidence unavailable' }
    if ($Ratchet.ExitCode -ne 0) { return 'spec ratchet failed' }
    $null
}

function Get-RatchetReport {
    param([string]$Root, [string]$HeadCommit, [string[]]$TouchedPaths, [int]$TimeoutSeconds)
    $script = Join-Path $Root 'tools/ci/check-spec-ratchet.py'
    if (-not (Test-Path -LiteralPath $script -PathType Leaf)) {
        return [pscustomobject]@{ Present = $false; Text = ''; ExitCode = $null; TimedOut = $false; Rows = @(); Totals = @(); Register = @(); Summary = '' }
    }
    $run = Invoke-Native -FileName 'python' -Arguments ('-I "' + $script + '"') -WorkingDirectory $Root -TimeoutSeconds $TimeoutSeconds
    $parsed = Convert-RatchetOutput -Text $run.Output -TouchedPaths $TouchedPaths
    $summary = ($run.Output -split "`n" | Where-Object { $_ -match '^(PASS|FAIL) regressions=' } | Select-Object -Last 1)
    [pscustomobject]@{
        Present   = $true
        Text      = $run.Output
        ExitCode  = $run.ExitCode
        TimedOut  = $run.TimedOut
        Rows      = $parsed.Rows
        Totals    = $parsed.Totals
        Register  = $parsed.Register
        Summary   = "$summary".Trim()
    }
}

function Get-LiveGroups {
    param([string]$Root)
    # One owner for the live-route group list: tools/ci/live-groups.txt.
    $path = Join-Path $Root 'tools/ci/live-groups.txt'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return @() }
    @(Get-Content -LiteralPath $path | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
}

function Get-LaneEngineGroups {
    param([string]$Root, [string]$Commit)
    # $null means no .ci-lane.json is committed, so CI runs every group; an empty
    # array means a lane file exists but selects no engine group.
    $text = Get-HeadFileText -Root $Root -Commit $Commit -Path '.ci-lane.json'
    if ($null -eq $text) { return $null }
    try {
        $lane = $text | ConvertFrom-Json
        if ($null -eq $lane -or $null -eq $lane.PSObject.Properties['engine']) { return ,@() }
        ,@($lane.engine)
    } catch { ,@() }
}

function Get-SelectionAttention {
    param([object[]]$Unmapped, [object[]]$SharedHelpers, [object[]]$OutsideRegistry, [string[]]$HostedGroups, [object]$LaneEngine)
    # No .ci-lane.json means CI runs every group, so nothing here can hide work.
    $attention = [System.Collections.Generic.List[string]]::new()
    if ($null -eq $LaneEngine) { return @($attention) }
    foreach ($path in $Unmapped) {
        $attention.Add("cannot verify the .ci-lane.json selection for $path; drop .ci-lane.json or confirm the groups in the report")
    }
    foreach ($path in $SharedHelpers) {
        $attention.Add("shared helper $path affects many groups; drop .ci-lane.json or confirm the groups in the report")
    }
    foreach ($path in $OutsideRegistry) {
        $attention.Add("$path is outside the Engine registry: select core/ui/build by hand or run the full matrix")
    }
    $effectiveEngine = @($LaneEngine) + @(Get-MandatoryEngineGroups)
    foreach ($group in $HostedGroups) {
        if ($effectiveEngine -cnotcontains $group) {
            $attention.Add("'.ci-lane.json' engine omits '$group' selected by the diff; add it or drop .ci-lane.json")
        }
    }
    @($attention)
}

function Get-BasenameMap {
    param([string]$Root, [string]$Commit)
    $map = @{}
    $listed = if ($Commit) {
        Invoke-Git -Root $Root -ArgumentList @('ls-tree', '-r', '--name-only', $Commit)
    } else {
        Invoke-Git -Root $Root -ArgumentList @('ls-files')
    }
    foreach ($path in ($listed -split "`n")) {
        if (-not $path.Trim()) { continue }
        $path = $path.Trim()
        $base = [IO.Path]::GetFileName($path).ToLowerInvariant()
        if (-not $map.ContainsKey($base)) { $map[$base] = [System.Collections.Generic.List[string]]::new() }
        $map[$base].Add($path)
    }
    $map
}

function Get-CommitFileSet {
    param([string]$Root, [string]$Commit)
    # lower-case path -> actual tracked spelling, so a mis-cased citation still
    # resolves (git show is case-sensitive). shortcut: flat file set; upgrade if
    # citations ever need the tree's modes or objects.
    $set = @{}
    foreach ($path in ((Invoke-Git -Root $Root -ArgumentList @('ls-tree', '-r', '--name-only', $Commit)) -split "`n")) {
        if ($path.Trim()) { $set[$path.Trim().ToLowerInvariant()] = $path.Trim() }
    }
    $set
}

function Get-CitationLines {
    param([string]$Root, [string]$Path, [string]$Commit)
    if (-not $Commit) { return @(Get-Content -LiteralPath $Path) }
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $relative = [IO.Path]::GetFullPath($Path).Substring($fullRoot.Length).TrimStart('\', '/') -replace '\\', '/'
    $text = Invoke-Git -Root $Root -ArgumentList @('show', ($Commit + ':' + $relative))
    $lines = @($text -split "`n" | ForEach-Object { $_.TrimEnd("`r") })
    if ($lines.Count -gt 0 -and $lines[-1] -eq '') { $lines = @($lines[0..($lines.Count - 2)]) }
    $lines
}

function Resolve-CitationCandidates {
    param([string]$Root, [string]$File, [hashtable]$BasenameMap, $FileSet)
    # Accept only paths inside the checkout; anything else is reported not found.
    $normalized = $File.Replace('\', '/')
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $candidate = if ([IO.Path]::IsPathRooted($normalized)) {
        [IO.Path]::GetFullPath($normalized)
    } else {
        [IO.Path]::GetFullPath((Join-Path $fullRoot $normalized))
    }
    $inside = $candidate.StartsWith($fullRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    if ($FileSet) {
        if ($inside) {
            $relative = $candidate.Substring($fullRoot.Length).TrimStart('\', '/') -replace '\\', '/'
            $actual = $FileSet[$relative.ToLowerInvariant()]
            if ($actual) { return @(Join-Path $Root $actual) }
        }
    } elseif ($inside -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        return @($candidate)
    }
    if ([IO.Path]::IsPathRooted($normalized)) { return @() }
    $base = [IO.Path]::GetFileName($normalized).ToLowerInvariant()
    if ($BasenameMap.ContainsKey($base)) { return @($BasenameMap[$base] | ForEach-Object { Join-Path $Root $_ }) }
    @()
}

function Get-SymbolCandidates {
    param([string]$Line, [int]$BeforeIndex)
    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($token in [regex]::Matches($Line, '`([^`]+)`')) {
        if ($token.Index -ge $BeforeIndex) { continue }
        $value = $token.Groups[1].Value.Trim()
        if ($value -match '[:\\/]' -or $value -match '\s' -or $value -match '\.(cs|md|ps1|py|json|jsonl|yml|yaml|csproj|sln)$') { continue }
        $value = $value -replace '\(.*$', ''
        $value = $value -replace '<.*$', ''
        $last = ($value -split '[.:]')[-1].Trim()
        if ($last -match '^[A-Za-z_]\w{1,}$') { $candidates.Add($last) }
    }
    @($candidates | Sort-Object -Unique)
}

function Get-StaleCitations {
    param([string]$Root, [string]$ReportPath, [string]$Commit)
    # shortcut: explicit file.ext:line citations only; upgrade if reports start using bare :line after a path.
    $report = Get-Content -LiteralPath $ReportPath
    $basenameMap = Get-BasenameMap -Root $Root -Commit $Commit
    $fileSet = if ($Commit) { Get-CommitFileSet -Root $Root -Commit $Commit } else { $null }
    $fileCache = @{}
    $pattern = '(?<file>(?:[A-Za-z]:)?[^\s`()\[\]*''"<>|,;]+?\.(?:cs|md|ps1|py|json|jsonl|yml|yaml|csproj|sln)):(?<lines>\d+(?:\s*,\s*\d+)*)(?:-(?<end>\d+))?'
    $findings = [System.Collections.Generic.List[object]]::new()
    $lineNumber = 0
    foreach ($text in $report) {
        $lineNumber++
        foreach ($match in [regex]::Matches($text, $pattern)) {
            $file = $match.Groups['file'].Value
            if ($file -match '^[0-9a-f]{7,40}:') { continue }  # base-side citation carries its commit; skipped
            $numbers = [System.Collections.Generic.List[int]]::new()
            foreach ($part in ($match.Groups['lines'].Value -split ',')) { $numbers.Add([int]$part.Trim()) }
            if ($match.Groups['end'].Success) {
                $last = [int]$match.Groups['end'].Value
                for ($n = $numbers[0] + 1; $n -le $last; $n++) { $numbers.Add($n) }
            }
            $resolvedList = @(Resolve-CitationCandidates -Root $Root -File $file -BasenameMap $basenameMap -FileSet $fileSet)
            if ($resolvedList.Count -eq 0) {
                $where = if ($Commit) { "at $Commit" } else { 'in the checkout' }
                $findings.Add([pscustomobject]@{ ReportLine = $lineNumber; Citation = $match.Value; Reason = "file not found $where" })
                continue
            }
            $symbols = @(Get-SymbolCandidates -Line $text -BeforeIndex $match.Index)
            $accepted = [System.Collections.Generic.List[string]]::new()
            $outOfRange = 0
            $firstLine = ''
            foreach ($resolved in $resolvedList) {
                $cacheKey = if ($Commit) { $Commit + ':' + $resolved } else { $resolved }
                if (-not $fileCache.ContainsKey($cacheKey)) { $fileCache[$cacheKey] = @(Get-CitationLines -Root $Root -Path $resolved -Commit $Commit) }
                $content = $fileCache[$cacheKey]
                if (@($numbers | Where-Object { $_ -lt 1 -or $_ -gt $content.Count }).Count -gt 0) { $outOfRange++; continue }
                if ($symbols.Count -eq 0) { $accepted.Add($resolved); continue }
                if (-not $firstLine) { $firstLine = $content[$numbers[0] - 1].Trim() }
                $hit = $false
                foreach ($number in $numbers) {
                    foreach ($symbol in $symbols) {
                        if ($content[$number - 1].Contains($symbol)) { $hit = $true; break }
                    }
                    if ($hit) { break }
                }
                if ($hit) { $accepted.Add($resolved) }
            }
            if ($accepted.Count -eq 1) { continue }
            if ($accepted.Count -gt 1) {
                if ($symbols.Count -gt 0) {
                    $findings.Add([pscustomobject]@{ ReportLine = $lineNumber; Citation = $match.Value; Reason = "ambiguous file: the cited symbol is in $($accepted.Count) files; use the full path" })
                }
                continue  # prose citation with several same-named files: nothing to verify
            }
            if ($outOfRange -eq $resolvedList.Count) {
                $findings.Add([pscustomobject]@{ ReportLine = $lineNumber; Citation = $match.Value; Reason = 'line out of range in every candidate file' })
            } else {
                $findings.Add([pscustomobject]@{
                    ReportLine = $lineNumber
                    Citation = $match.Value
                    Reason = "none of [$($symbols -join ', ')] is on the cited line: '$firstLine'"
                })
            }
        }
    }
    $findings
}

function Convert-GlobToRegex {
    param([string]$Glob)
    # ** crosses directories; * and ? stay within one path segment.
    $builder = [System.Text.StringBuilder]::new('^')
    $i = 0
    while ($i -lt $Glob.Length) {
        $char = $Glob[$i]
        if ($char -eq '*') {
            if ($i + 1 -lt $Glob.Length -and $Glob[$i + 1] -eq '*') { [void]$builder.Append('.*'); $i += 2; continue }
            [void]$builder.Append('[^/]*'); $i++; continue
        }
        if ($char -eq '?') { [void]$builder.Append('[^/]'); $i++; continue }
        [void]$builder.Append([regex]::Escape([string]$char)); $i++
    }
    [void]$builder.Append('$')
    $builder.ToString()
}

function Assert-OwnerArguments {
    param([string]$Owners, [string]$Lane)
    if ($Owners -and -not $Lane) { throw '-Lane is required when -Owners is given.' }
}

function Test-ShouldRunTests {
    param([bool]$TestChanged, [string]$ControlsPath)
    # Registration is relevant only when a test file changed, but an explicit
    # -Controls must still run its witness check on a src-only lane.
    $TestChanged -or [bool]$ControlsPath
}

function Get-OwnershipFindings {
    param([object[]]$ChangedFiles, [string]$OwnersPath, [string]$Lane)
    if (-not $OwnersPath) { return @() }
    # A JSON map of { path glob: owning lane }. The path may be deleted; touching it
    # still crosses the owner. A path matched by two different owners is ambiguous.
    $map = (Get-Content -LiteralPath $OwnersPath -Raw | ConvertFrom-Json).PSObject.Properties
    $findings = [System.Collections.Generic.List[string]]::new()
    foreach ($file in $ChangedFiles) {
        $owners = [System.Collections.Generic.List[string]]::new()
        foreach ($property in $map) {
            if ($file.Path -match (Convert-GlobToRegex -Glob $property.Name)) {
                if (-not $owners.Contains([string]$property.Value)) { $owners.Add([string]$property.Value) }
            }
        }
        if ($owners.Count -gt 1) {
            $findings.Add("$($file.Path) matches several owner globs ($($owners -join ', ')); make the map unambiguous")
        } elseif ($owners.Count -eq 1 -and $owners[0] -ne $Lane) {
            $findings.Add("$($file.Path) is owned by $($owners[0]); this lane is '$Lane' (move the change or get the owner's sign-off)")
        }
    }
    @($findings)
}

function Get-RepeatedExpressionFindings {
    param([string]$Root, [string]$HeadCommit, [object[]]$ChangedFiles, [int]$MinLength = 40, [int]$MinCount = 5)
    # Advisory: a long expression repeated many times across the changed src files.
    $counts = @{}
    $first = @{}
    foreach ($file in ($ChangedFiles | Where-Object { $_.Path -like 'src/*.cs' -and $_.Status -ne 'D' })) {
        $text = Get-HeadFileText -Root $Root -Commit $HeadCommit -Path $file.Path
        if ($null -eq $text) { continue }
        $lineNumber = 0
        foreach ($line in ($text -split "`n")) {
            $lineNumber++
            $trim = $line.Trim()
            if ($trim.Length -lt $MinLength) { continue }
            if ($trim -match '^[{}()\[\];,]+$') { continue }
            if (-not $counts.ContainsKey($trim)) { $counts[$trim] = 0; $first[$trim] = "$($file.Path):$lineNumber" }
            $counts[$trim]++
        }
    }
    $findings = [System.Collections.Generic.List[string]]::new()
    foreach ($key in $counts.Keys) {
        if ($counts[$key] -ge $MinCount) { $findings.Add("$($first[$key])  x$($counts[$key])  $key") }
    }
    @($findings | Sort-Object)
}

function Invoke-PreReviewCheck {
    param([string]$Root, [string]$Check, [string]$BaseCommit, [string]$HeadCommit, [string]$Registry, [string]$Controls)
    $gateScript = Join-Path $PSScriptRoot 'ci/prereview_checks.py'
    $laneScript = Join-Path $Root 'tools/ci/prereview_checks.py'
    # CI runs the lane's ratchet. receipt/grown import the ratchet, so prefer the
    # lane's helper there; registration reads only lane files, so the gate's copy
    # is fine. Fall back either way when one is missing.
    if ($Check -in @('receipt', 'grown') -and (Test-Path -LiteralPath $laneScript -PathType Leaf)) {
        $script = $laneScript
    } elseif (Test-Path -LiteralPath $gateScript -PathType Leaf) {
        $script = $gateScript
    } elseif (Test-Path -LiteralPath $laneScript -PathType Leaf) {
        $script = $laneScript
    } else {
        return $null
    }
    $arguments = '-I "' + $script + '" --check ' + $Check + ' --repo "' + $Root + '"'
    if ($BaseCommit) { $arguments += ' --base ' + $BaseCommit }
    if ($HeadCommit) { $arguments += ' --head ' + $HeadCommit }
    if ($Registry) { $arguments += ' --registry "' + $Registry + '"' }
    if ($Controls) { $arguments += ' --controls "' + $Controls + '"' }
    Invoke-Native -FileName 'python' -Arguments $arguments -WorkingDirectory $Root
}

function Get-StaticFenceState {
    param([string]$Root, [string]$ExePath)
    # Locate an existing build output and say whether it is older than the sources.
    $exists = Test-Path -LiteralPath $ExePath -PathType Leaf
    $newest = [datetime]::MinValue
    # Inputs to the Engine test build: all src and test .cs and .csproj (the
    # project also compiles Core.Tests wire files), plus Directory.Build.props.
    foreach ($base in @((Join-Path $Root 'src'), (Join-Path $Root 'tests'))) {
        if (-not (Test-Path -LiteralPath $base)) { continue }
        foreach ($file in (Get-ChildItem -LiteralPath $base -Recurse -File -ErrorAction SilentlyContinue)) {
            if ($file.FullName -match '[\\/](bin|obj)[\\/]') { continue }
            if ($file.Extension -notin @('.cs', '.csproj')) { continue }
            if ($file.LastWriteTimeUtc -gt $newest) { $newest = $file.LastWriteTimeUtc }
        }
    }
    $props = Join-Path $Root 'Directory.Build.props'
    if ((Test-Path -LiteralPath $props) -and (Get-Item -LiteralPath $props).LastWriteTimeUtc -gt $newest) {
        $newest = (Get-Item -LiteralPath $props).LastWriteTimeUtc
    }
    $exeTime = if ($exists) { (Get-Item -LiteralPath $ExePath).LastWriteTimeUtc } else { [datetime]::MinValue }
    [pscustomobject]@{
        Exists       = $exists
        Stale        = $exists -and ($newest -ne [datetime]::MinValue) -and ($exeTime -lt $newest)
        NewestSource = $newest
        ExeTime      = $exeTime
    }
}

# ---------------------------------------------------------------- self-test

function Assert-True {
    param([bool]$Condition, [string]$Message)
    $script:Assertions++
    if (-not $Condition) { throw "SELF-TEST FAILED: $Message" }
}

function Invoke-SelfTest {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ('prereview-selftest-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $registry = @'
namespace X;
public static class CheckRegistry
{
    public static IReadOnlyDictionary<string, IReadOnlyList<Action>> All { get; } = new Dictionary<string, IReadOnlyList<Action>>
    {
        ["literals"] = [Layout.LiteralsChecks.Run],
        ["store"] = [Storage.StateStoreChecks.Run, Storage.StateStoreChecks.ClaimedRepair],
        ["scenarios"] = Scenarios.ScenarioChecks.OfflineCases(),
        ["speed"] = [Run.CallBudgetChecks.RunSpeed],
    };
}
'@
        $groups = (Get-CheckRegistryIndex -Text $registry).Groups
        Assert-True ($groups['literals'] -contains 'Layout.LiteralsChecks') 'literal group class'
        Assert-True ($groups['store'] -contains 'Storage.StateStoreChecks') 'store group class'
        Assert-True ($groups['scenarios'] -contains 'Scenarios.ScenarioChecks') 'scenarios group class'

        # P1-1: a src change selects the group that owns <rel>Checks.
        $srcOnly = @([pscustomobject]@{ Status = 'M'; Path = 'src/SyncShark.Engine/Storage/StateStore.cs' })
        $srcMap = Get-GroupsForChangedFiles -ChangedFiles $srcOnly -Groups $groups
        Assert-True ($srcMap.Groups -contains 'store') 'P1-1 src change maps to its Checks group'
        Assert-True ($srcMap.Groups.Count -gt 1) 'P1-1 red proof: src-only change selects more than dependencies'
        Assert-True ($srcMap.Unmapped.Count -eq 0) 'P1-1 mapped src change is not unmapped'
        # P1-1 red proof: a src file with no Checks class raises ATTENTION, so the run cannot print SUMMARY PASS.
        $srcUnknown = @([pscustomobject]@{ Status = 'M'; Path = 'src/SyncShark.Engine/Cloud/Unknown.cs' })
        $srcUnknownMap = Get-GroupsForChangedFiles -ChangedFiles $srcUnknown -Groups $groups
        Assert-True ($srcUnknownMap.Unmapped.Count -eq 1) 'P1-1 unmapped src change is listed'

        # P2-3: a partial file maps by its class prefix; shared helpers are not unmapped.
        $partial = @([pscustomobject]@{ Status = 'M'; Path = 'tests/SyncShark.Engine.Tests/Run/CallBudgetChecks.Speed.cs' })
        $partialMap = Get-GroupsForChangedFiles -ChangedFiles $partial -Groups $groups
        Assert-True ($partialMap.Groups -contains 'speed') 'P2-3 partial file maps to its group'
        Assert-True ($partialMap.Unmapped.Count -eq 0) 'P2-3 partial file is not unmapped'
        $helper = @(
            [pscustomobject]@{ Status = 'M'; Path = 'tests/SyncShark.Engine.Tests/TestDoubles/FakeSide.cs' },
            [pscustomobject]@{ Status = 'M'; Path = 'tests/SyncShark.Engine.Tests/Check.cs' }
        )
        $helperMap = Get-GroupsForChangedFiles -ChangedFiles $helper -Groups $groups
        Assert-True ($helperMap.SharedHelpers.Count -eq 2) 'P2-3 shared helpers reported'
        Assert-True ($helperMap.Unmapped.Count -eq 0) 'P2-3 shared helpers are not unmapped'

        # N3: files outside the Engine registry are listed, not silently ignored.
        $coreOnly = @([pscustomobject]@{ Status = 'M'; Path = 'src/SyncShark.Core/PairSettings.cs' })
        $coreMap = Get-GroupsForChangedFiles -ChangedFiles $coreOnly -Groups $groups
        Assert-True ($coreMap.OutsideRegistry.Count -eq 1) 'N3 Core src file is outside the Engine registry'
        $appOnly = @([pscustomobject]@{ Status = 'M'; Path = 'apps/SyncShark.App/Main.cs' })
        $appMap = Get-GroupsForChangedFiles -ChangedFiles $appOnly -Groups $groups
        Assert-True ($appMap.OutsideRegistry.Count -eq 1) 'N3 app file is outside the Engine registry'

        # N2: unmapped, shared and outside-registry files raise ATTENTION only
        # when a .ci-lane.json is committed; with none, CI runs every group.
        $noLaneAttention = Get-SelectionAttention -Unmapped @('src/SyncShark.Engine/Cloud/Unknown.cs') -SharedHelpers @() -OutsideRegistry @('src/SyncShark.Core/PairSettings.cs') -HostedGroups @('dependencies') -LaneEngine $null
        Assert-True (@($noLaneAttention).Count -eq 0) 'N2 no lane file: unmapped and outside-registry are information only'
        $laneAttention = Get-SelectionAttention -Unmapped @('src/SyncShark.Engine/Cloud/Unknown.cs') -SharedHelpers @() -OutsideRegistry @('src/SyncShark.Core/PairSettings.cs') -HostedGroups @('dependencies', 'speed') -LaneEngine @('dependencies')
        Assert-True (@($laneAttention).Count -eq 3) 'N2 lane file: unmapped, outside-registry and a missing group raise ATTENTION'

        # P2-6: type names and declaration parsing.
        Assert-True (@(Get-TypeNames -Text 'public class Alpha') -contains 'Alpha') 'P2-6 type names extracted'
        $removedNames = Get-DeclarationNamesFromLines -Lines @('public void OldMethod(int value)', 'public class OldType', 'if (value)')
        Assert-True ($removedNames -contains 'OldMethod') 'removed method'
        Assert-True ($removedNames -contains 'OldType') 'removed type'
        Assert-True ($removedNames -notcontains 'if') 'control keyword excluded'

        # P2-1 and N1: the real ratchet format has no pipe after NEW/GROWN; any
        # current verdict settles its NEW/GROWN row.
        $ratchetText = @'
NEW src/A.cs | file-size |  | 400
REVIEW REQUIRED | src/A.cs | file-size |  |  | ~hash
REVIEW SPLIT-PENDING | src/B.cs | file-size |  |  | ~hash
NEW src/C.cs | class-size | C | declaration-000001 | 500
GROWN @total | magic-tunables | 1325 -> 1394
REGISTER review required: src/A.cs (update its section or record a content-bound review reason)
REGISTER review required: src/B.cs (update its section or record a content-bound review reason)
'@
        $parsed = Convert-RatchetOutput -Text $ratchetText -TouchedPaths @('src/A.cs', 'src/C.cs')
        Assert-True ($parsed.Rows.Count -eq 2) 'ratchet rows: A deduped, C new'
        Assert-True (@($parsed.Rows | Where-Object { $_.Path -eq 'src/A.cs' -and $_.Category -eq 'file-size' }).Count -eq 1) 'required row deduped'
        Assert-True (@($parsed.Rows | Where-Object { $_.Path -eq 'src/B.cs' }).Count -eq 0) 'current verdict and untouched path ignored'
        Assert-True ($parsed.Register.Count -eq 1) 'register row only for the touched path'
        Assert-True (@($parsed.Rows | Where-Object { $_.Path -eq '@total' }).Count -eq 0) 'P2-2 repository total is not a lane row'
        Assert-True (@($parsed.Totals | Where-Object { $_.Path -eq '@total' -and $_.Category -eq 'magic-tunables' }).Count -eq 1) 'P2-2 repository total row is parsed'
        $nonSize = @'
NEW src/C.cs | top-level-types |  | 2
'@
        $nonSizeParsed = Convert-RatchetOutput -Text $nonSize -TouchedPaths @('src/C.cs')
        Assert-True ($nonSizeParsed.Rows.Count -eq 1) 'N1 a real non-size NEW row is parsed'
        Assert-True ($nonSizeParsed.Rows[0].Category -eq 'top-level-types') 'N1 non-size row keeps its category'
        $grown = @'
GROWN src/C.cs | file-size |  | 100 -> 120
REVIEW SPLIT-PENDING | src/C.cs | file-size |  |  | ~hash
'@
        $grownParsed = Convert-RatchetOutput -Text $grown -TouchedPaths @('src/C.cs')
        Assert-True ($grownParsed.Rows.Count -eq 0) 'P2-1 current verdict suppresses its GROWN row'

        # P3-1: a non-zero ratchet exit raises 'spec ratchet failed'; the gate
        # reads only the exit code, so total growth is not waived.
        $failedRatchet = [pscustomobject]@{ TimedOut = $false; ExitCode = 1 }
        Assert-True ((Get-RatchetAttention -Ratchet $failedRatchet -TimeoutSeconds 900) -eq 'spec ratchet failed') 'P3-1 a non-zero ratchet exit raises spec ratchet failed'

        $incompleteRatchet = [pscustomobject]@{ TimedOut = $false; ExitCode = 2 }
        Assert-True ((Get-RatchetAttention -Ratchet $incompleteRatchet -TimeoutSeconds 900) -match 'incomplete') 'unavailable trusted release evidence is incomplete'
        Assert-True ((Get-MandatoryCheckOutcome -Results @{ ratchet = 'skipped' } -Required @('ratchet')).State -eq 'INCOMPLETE') 'a skipped ratchet is incomplete'

        # A throwaway two-commit repo for the git-backed fixtures (P2-2, P2-6).
        $repo = Join-Path $temp 'repo'
        New-Item -ItemType Directory -Path (Join-Path $repo 'src') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $repo 'docs/design') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $repo 'a') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $repo 'b') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $repo 'src/Old.cs') -Value @(
            'namespace N;', '', 'public class Vanishing', '{', '    public void Foo(int a) { }', '    public void Moved() { }', '}') -NoNewline:$false
        # Unchanged across both commits: keeps Moved in head src for the N4 check.
        Set-Content -LiteralPath (Join-Path $repo 'src/Source.cs') -Value @(
            'namespace N;', '', 'public class Anchor', '{', '    public void Moved() { }', '}') -NoNewline:$false
        Set-Content -LiteralPath (Join-Path $repo 'docs/design/engine.md') -Value @(
            '# Design', 'The Vanishing type and the Foo and Moved methods are described here.') -NoNewline:$false
        $sampleLines = @('namespace N;', 'public class Sample', '{', '    public void Target() { }')
        $sampleLines += @('', '', '', '', '', '    // Alpha', '    public void Alpha() { }', '', '', '    public void Beta() { }', '', '}')
        Set-Content -LiteralPath (Join-Path $repo 'Sample.cs') -Value $sampleLines -NoNewline:$false
        Set-Content -LiteralPath (Join-Path $repo 'a/Dup.cs') -Value @('public void Target() { }') -NoNewline:$false
        Set-Content -LiteralPath (Join-Path $repo 'b/Dup.cs') -Value @('public void Target() { }') -NoNewline:$false
        Invoke-Git -Root $repo -ArgumentList @('init') | Out-Null
        Invoke-Git -Root $repo -ArgumentList @('config', 'user.email', 'selftest@example.invalid') | Out-Null
        Invoke-Git -Root $repo -ArgumentList @('config', 'user.name', 'Self Test') | Out-Null
        Invoke-Git -Root $repo -ArgumentList @('add', '-A') | Out-Null
        Invoke-Git -Root $repo -ArgumentList @('commit', '-m', 'base', '--no-gpg-sign') | Out-Null
        $baseCommit = (Invoke-Git -Root $repo -ArgumentList @('rev-parse', 'HEAD')).Trim()
        Set-Content -LiteralPath (Join-Path $repo 'src/Old.cs') -Value @(
            'namespace N;', '', 'public class Keep', '{', '    public void Foo(int a, int b) { }', '}') -NoNewline:$false
        Set-Content -LiteralPath (Join-Path $repo 'src/New.cs') -Value @('namespace N;', '', 'public class Fresh { }') -NoNewline:$false
        # P3-B: a longer name containing a removed name must not answer the -w grep.
        Set-Content -LiteralPath (Join-Path $repo 'src/Substring.cs') -Value @('namespace N;', '', 'public class VanishingExtra { }') -NoNewline:$false
        Invoke-Git -Root $repo -ArgumentList @('add', '-A') | Out-Null
        Invoke-Git -Root $repo -ArgumentList @('commit', '-m', 'head', '--no-gpg-sign') | Out-Null
        $headCommit = (Invoke-Git -Root $repo -ArgumentList @('rev-parse', 'HEAD')).Trim()

        # P2-6: --name-status parsing.
        $changedFiles = @(Get-ChangedFiles -Root $repo -BaseCommit $baseCommit -HeadCommit $headCommit)
        Assert-True (@($changedFiles | Where-Object { $_.Path -eq 'src/Old.cs' -and $_.Status -eq 'M' }).Count -eq 1) 'P2-6 name-status modified'
        Assert-True (@($changedFiles | Where-Object { $_.Path -eq 'src/New.cs' -and $_.Status -eq 'A' }).Count -eq 1) 'P2-6 name-status added'

        # P2-2 and P2-6: doc drift finds a removed type, not an edited method.
        $drift = @(Get-DocDrift -Root $repo -BaseCommit $baseCommit -HeadCommit $headCommit -ChangedFiles $changedFiles)
        Assert-True (@($drift | Where-Object { $_.Kind -eq 'removed' -and $_.Name -eq 'Vanishing' }).Count -ge 1) 'P3-B -w: a removed name inside a longer name is still reported'
        Assert-True (@($drift | Where-Object { $_.Kind -eq 'removed' -and $_.Name -eq 'Vanishing' }).Count -ge 1) 'P2-6 doc drift finds the removed type'
        Assert-True (@($drift | Where-Object { $_.Name -eq 'Foo' }).Count -eq 0) 'P2-2 edited method is not reported removed'
        Assert-True (@($drift | Where-Object { $_.Kind -eq 'removed' -and $_.Name -eq 'Moved' }).Count -eq 0) 'N4 a name still declared in head src is not reported removed'

        # N2: the committed .ci-lane.json engine list is read from the head commit.
        Assert-True ($null -eq (Get-LaneEngineGroups -Root $repo -Commit $headCommit)) 'N2 no lane file reads as null'
        Set-Content -LiteralPath (Join-Path $repo '.ci-lane.json') -Value '{ "engine": ["dependencies", "store"] }' -NoNewline:$false
        Invoke-Git -Root $repo -ArgumentList @('add', '-A') | Out-Null
        Invoke-Git -Root $repo -ArgumentList @('commit', '-m', 'lane', '--no-gpg-sign') | Out-Null
        $laneCommit = (Invoke-Git -Root $repo -ArgumentList @('rev-parse', 'HEAD')).Trim()
        $laneRead = Get-LaneEngineGroups -Root $repo -Commit $laneCommit
        Assert-True ($laneRead -contains 'store') 'N2 lane file engine list is read'

        # P2-1: a lane file that selects no engine group (no engine key, or an
        # empty engine list) must not read as "no lane file" (which means CI runs
        # every group). Mandatory groups are added by CI; other groups still need selection.
        foreach ($laneCase in @(
            @{ Label = 'no engine key'; Json = '{ "core": ["--v2-x"] }' },
            @{ Label = 'empty engine list'; Json = '{ "engine": [] }' },
            @{ Label = 'invalid JSON'; Json = '{' }
        )) {
            Set-Content -LiteralPath (Join-Path $repo '.ci-lane.json') -Value $laneCase.Json -NoNewline:$false
            Invoke-Git -Root $repo -ArgumentList @('add', '-A') | Out-Null
            Invoke-Git -Root $repo -ArgumentList @('commit', '-m', 'lane', '--no-gpg-sign') | Out-Null
            $emptyLaneCommit = (Invoke-Git -Root $repo -ArgumentList @('rev-parse', 'HEAD')).Trim()
            $emptyLane = Get-LaneEngineGroups -Root $repo -Commit $emptyLaneCommit
            Assert-True ($null -ne $emptyLane) "P2-1 $($laneCase.Label): an existing lane file is not 'no lane file'"
            Assert-True (@($emptyLane).Count -eq 0) "P2-1 $($laneCase.Label): lane requests no engine group"
            $emptyLaneAttention = Get-SelectionAttention -Unmapped @() -SharedHelpers @() -OutsideRegistry @() -HostedGroups @('store') -LaneEngine $emptyLane
            Assert-True (@($emptyLaneAttention).Count -ge 1) "P2-1 $($laneCase.Label): a selected hosted group is flagged, not hidden"
            $mandatoryAttention = Get-SelectionAttention -Unmapped @() -SharedHelpers @() -OutsideRegistry @() -HostedGroups @(Get-MandatoryEngineGroups) -LaneEngine $emptyLane
            Assert-True (@($mandatoryAttention).Count -eq 0) 'Mandatory groups are selected by CI, not omitted'
        }

        $report = Join-Path $temp 'report.md'
        Set-Content -LiteralPath $report -Value @(
            '`Target` at `Sample.cs:4` is right.',
            '`Missing` at `Sample.cs:2` is stale.'
        ) -NoNewline:$false
        $citations = @(Get-StaleCitations -Root $repo -ReportPath $report)
        Assert-True ($citations.Count -eq 1) 'one stale citation'
        Assert-True ($citations[0].Citation -eq 'Sample.cs:2') 'stale citation is the second one'

        # P2-5: markdown emphasis around the citation is not part of the file name.
        $bold = Join-Path $temp 'report-bold.md'
        Set-Content -LiteralPath $bold -Value '`Target` see **Sample.cs:4**' -NoNewline:$false
        Assert-True (@(Get-StaleCitations -Root $repo -ReportPath $bold).Count -eq 0) 'P2-5 bold citation is not a false failure'

        # N5: the range is satisfied only by its last line (12-14, Beta on 14).
        $ranges = Join-Path $temp 'report-ranges.md'
        Set-Content -LiteralPath $ranges -Value @('`Beta` at `Sample.cs:12-14`', '`Beta` at `Sample.cs:10,14`') -NoNewline:$false
        Assert-True (@(Get-StaleCitations -Root $repo -ReportPath $ranges).Count -eq 0) 'N5 range expansion and list citations resolve'

        # P2-6: out-of-range line.
        $outside = Join-Path $temp 'report-outside.md'
        Set-Content -LiteralPath $outside -Value '`Alpha` at `Sample.cs:99`' -NoNewline:$false
        $outsideFindings = @(Get-StaleCitations -Root $repo -ReportPath $outside)
        Assert-True ($outsideFindings.Count -eq 1) 'P2-6 out-of-range citation reported'
        Assert-True ($outsideFindings[0].Reason -like '*out of range*') 'out-of-range reason'

        # P2-6: ambiguous file name.
        $ambiguous = Join-Path $temp 'report-ambiguous.md'
        Set-Content -LiteralPath $ambiguous -Value '`Target` at `Dup.cs:1`' -NoNewline:$false
        $ambiguousFindings = @(Get-StaleCitations -Root $repo -ReportPath $ambiguous)
        Assert-True ($ambiguousFindings.Count -eq 1) 'P2-6 ambiguous file reported'
        Assert-True ($ambiguousFindings[0].Reason -like '*ambiguous*') 'ambiguous reason'

        # N5: an existing file outside the checkout is not read. Its basename
        # Sample.cs is tracked, so without confinement the fallback would resolve it.
        $outsideRoot = Join-Path $temp 'Sample.cs'
        Set-Content -LiteralPath $outsideRoot -Value $sampleLines -NoNewline:$false
        $data = Join-Path $temp 'report-data.md'
        Set-Content -LiteralPath $data -Value ('`Beta` at `' + $outsideRoot + ':14`') -NoNewline:$false
        $dataFindings = @(Get-StaleCitations -Root $repo -ReportPath $data)
        Assert-True ($dataFindings.Count -eq 1) 'N5 a file outside the checkout is not read'
        Assert-True ($dataFindings[0].Reason -like '*not found*') 'outside path reported not found'

        # P3-5 and N5: a base-side citation out of range without the skip is still skipped.
        $baseSide = Join-Path $temp 'report-base.md'
        Set-Content -LiteralPath $baseSide -Value '18bf61719:src/Old.cs:99' -NoNewline:$false
        Assert-True (@(Get-StaleCitations -Root $repo -ReportPath $baseSide).Count -eq 0) 'P3-5 base-side citation skipped'

        # ---- new checks: ownership, repeated expressions, sha citations, and the Python checks ----

        function New-FixtureRepo {
            param([string]$Path)
            New-Item -ItemType Directory -Path $Path -Force | Out-Null
            Invoke-Git -Root $Path -ArgumentList @('init') | Out-Null
            Invoke-Git -Root $Path -ArgumentList @('config', 'user.email', 'selftest@example.invalid') | Out-Null
            Invoke-Git -Root $Path -ArgumentList @('config', 'user.name', 'Self Test') | Out-Null
        }
        function Commit-Fixture {
            param([string]$Path, [string]$Message)
            Invoke-Git -Root $Path -ArgumentList @('add', '-A') | Out-Null
            Invoke-Git -Root $Path -ArgumentList @('commit', '-m', $Message, '--no-gpg-sign') | Out-Null
            (Invoke-Git -Root $Path -ArgumentList @('rev-parse', 'HEAD')).Trim()
        }
        function Invoke-Python {
            param([string]$Root, [string[]]$Arguments)
            Invoke-Native -FileName 'python' -Arguments ('-I ' + (($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' ')) -WorkingDirectory $Root
        }
        function New-RegistryJson {
            param([string]$RegistryText, [string]$Path)
            $index = Get-CheckRegistryIndex -Text $RegistryText
            $methods = @{}
            foreach ($key in $index.Methods.Keys) { $methods[$key] = @($index.Methods[$key]) }
            ($methods | ConvertTo-Json -Compress) | Set-Content -LiteralPath $Path -Encoding utf8
            $Path
        }
        function New-ExemptFile {
            param([string]$Repo, [string[]]$Methods)
            $dir = Join-Path $Repo 'tests/SyncShark.Engine.Tests/Run'
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            $body = @('namespace Massdex.SyncShark.Engine.Tests.Run;', 'public static class ExcludedSavedRowChecks', '{')
            foreach ($name in $Methods) { $body += "    public static void $name() { }" }
            $body += '}'
            Set-Content -LiteralPath (Join-Path $dir 'ExcludedSavedRowChecks.cs') -Value $body -NoNewline:$false
            # HarnessSafety is exempt because a registered action list returns it.
            $scen = Join-Path $Repo 'tests/SyncShark.Engine.Tests/Scenarios'
            New-Item -ItemType Directory -Path $scen -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $scen 'ScenarioChecks.cs') -Value @(
                'namespace Massdex.SyncShark.Engine.Tests.Scenarios;',
                'public static class ScenarioChecks',
                '{',
                '    public static System.Collections.Generic.IReadOnlyList<System.Action> OfflineCases()',
                '    {',
                '        return new System.Action[] { HarnessSafety };',
                '    }',
                '    private static void Other()',
                '    {',
                '        HarnessSafety();',
                '    }',
                '    public static void HarnessSafety() { }',
                '}') -NoNewline:$false
        }
        $checkScript = Join-Path $PSScriptRoot 'ci/prereview_checks.py'
        $ratchetScript = Join-Path $PSScriptRoot 'ci/check-spec-ratchet.py'
        $exemptNames = @('PrefixFirstRun', 'PrefixUnchanged', 'PrefixChangedNeighbor', 'PrefixSavedOnly', 'PrefixBothAbsent', 'PrefixExactName', 'PrefixPerPair')

        # Check 3: ownership map (and the -Owners without -Lane guard).
        $ownersFile = Join-Path $temp 'owners.json'
        Set-Content -LiteralPath $ownersFile -Value '{ "src/Alpha/*.cs": "Lane-A", "src/Beta/**/*.cs": "Lane-B" }' -NoNewline:$false
        $ownPass = @(Get-OwnershipFindings -ChangedFiles @([pscustomobject]@{ Status = 'M'; Path = 'src/Alpha/File.cs' }) -OwnersPath $ownersFile -Lane 'Lane-A')
        Assert-True ($ownPass.Count -eq 0) 'ownership: a path in the lane passes'
        $ownFail = @(Get-OwnershipFindings -ChangedFiles @([pscustomobject]@{ Status = 'M'; Path = 'src/Beta/Deep/File.cs' }) -OwnersPath $ownersFile -Lane 'Lane-A')
        Assert-True ($ownFail.Count -eq 1) 'ownership: another lane path fails (glob ** crosses directories)'
        $ambiguousOwners = Join-Path $temp 'owners-ambiguous.json'
        Set-Content -LiteralPath $ambiguousOwners -Value '{ "src/**/*.cs": "Lane-A", "src/Shared/*.cs": "Lane-B" }' -NoNewline:$false
        $ownAmbiguous = @(Get-OwnershipFindings -ChangedFiles @([pscustomobject]@{ Status = 'M'; Path = 'src/Shared/File.cs' }) -OwnersPath $ambiguousOwners -Lane 'Lane-A')
        Assert-True ($ownAmbiguous.Count -eq 1 -and $ownAmbiguous[0] -like '*several owner globs*') 'ownership: an ambiguous path fails'
        $threw = $false
        try { Assert-OwnerArguments -Owners $ownersFile -Lane '' } catch { $threw = $true }
        Assert-True $threw 'ownership: -Owners without -Lane throws'

        # P1-1: a registry entry inside a comment is not registered.
        $commentRegistry = @'
namespace T;
public static class CheckRegistry
{
    public static readonly System.Collections.Generic.Dictionary<string, System.Action[]> All = new()
    {
        ["syncer"] = [Foo.Run, /* Bar.Run, */ Baz.Run],
        // ["disabled"] = [Quux.Run],
    };
}
'@
        $commentIndex = Get-CheckRegistryIndex -Text $commentRegistry
        Assert-True (-not $commentIndex.Methods.ContainsKey('Bar.Run')) 'registry: a /* */ commented entry is not registered'
        Assert-True (-not $commentIndex.Methods.ContainsKey('Quux.Run')) 'registry: a // commented entry is not registered'
        Assert-True ($commentIndex.Methods.ContainsKey('Foo.Run') -and $commentIndex.Methods.ContainsKey('Baz.Run')) 'registry: live entries beside a comment are registered'
        # P3-1: comment stripping keeps string literals.
        $stringRegistry = @'
namespace T;
public static class CheckRegistry
{
    public static readonly System.Collections.Generic.Dictionary<string, System.Action[]> All = new()
    {
        ["a"] = [X.Go("http://h/p"), Bar.Run],
        ["s"] = [X.Str("src/*.cs"), Baz.Run],
    };
}
'@
        $stringIndex = Get-CheckRegistryIndex -Text $stringRegistry
        Assert-True ($stringIndex.Methods.ContainsKey('Bar.Run')) 'registry: // inside a string does not start a comment'
        Assert-True ($stringIndex.Methods.ContainsKey('Baz.Run')) 'registry: /* inside a string does not start a comment'

        # P1-2: an explicit -Controls runs the witness check even with no test change.
        Assert-True (-not (Test-ShouldRunTests -TestChanged $false -ControlsPath '')) 'gate: tests skipped when no test changed and no controls'
        Assert-True (Test-ShouldRunTests -TestChanged $false -ControlsPath 'controls.json') 'gate: -Controls runs tests on a src-only lane'
        Assert-True (Test-ShouldRunTests -TestChanged $true -ControlsPath '') 'gate: tests run when a test file changed'

        # Check 6: repeated expressions (advisory).
        $r6 = Join-Path $temp 'repeats'
        New-FixtureRepo -Path $r6
        New-Item -ItemType Directory -Path (Join-Path $r6 'src') -Force | Out-Null
        $long = '        var repeated = ComputeRepeatedLongExpression(firstValue, secondValue);'
        Set-Content -LiteralPath (Join-Path $r6 'src/Hot.cs') -Value (@($long) * 5) -NoNewline:$false
        Set-Content -LiteralPath (Join-Path $r6 'src/Cold.cs') -Value (@($long) * 4) -NoNewline:$false
        $r6head = Commit-Fixture -Path $r6 -Message 'base'
        $hot = @(Get-RepeatedExpressionFindings -Root $r6 -HeadCommit $r6head -ChangedFiles @([pscustomobject]@{ Status = 'M'; Path = 'src/Hot.cs' }))
        Assert-True ($hot.Count -eq 1) 'repeats: five identical 40+ character lines are reported'
        $cold = @(Get-RepeatedExpressionFindings -Root $r6 -HeadCommit $r6head -ChangedFiles @([pscustomobject]@{ Status = 'M'; Path = 'src/Cold.cs' }))
        Assert-True ($cold.Count -eq 0) 'repeats: four repeats stay quiet'

        # Check 4: citations verified against a given sha, including a mis-cased path.
        $r4 = Join-Path $temp 'sha'
        New-FixtureRepo -Path $r4
        New-Item -ItemType Directory -Path (Join-Path $r4 'src') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $r4 'src/Cited.cs') -Value @('// header', 'public void Alpha()', 'public void Beta()') -NoNewline:$false
        $r4base = Commit-Fixture -Path $r4 -Message 'base'
        Set-Content -LiteralPath (Join-Path $r4 'src/Cited.cs') -Value @('// header', 'public void Alpha()') -NoNewline:$false
        $r4head = Commit-Fixture -Path $r4 -Message 'head'
        $reportAtBase = Join-Path $temp 'report-at-base.md'
        Set-Content -LiteralPath $reportAtBase -Value '`Beta` at `src/Cited.cs:3`' -NoNewline:$false
        Assert-True (@(Get-StaleCitations -Root $r4 -ReportPath $reportAtBase -Commit $r4base).Count -eq 0) 'citations: resolves against the given base sha'
        Assert-True (@(Get-StaleCitations -Root $r4 -ReportPath $reportAtBase -Commit $r4head).Count -eq 1) 'citations: stale against a later sha where the line is gone'
        $reportMisCased = Join-Path $temp 'report-miscased.md'
        Set-Content -LiteralPath $reportMisCased -Value '`Alpha` at `src/cited.cs:2`' -NoNewline:$false
        Assert-True (@(Get-StaleCitations -Root $r4 -ReportPath $reportMisCased -Commit $r4base).Count -eq 0) 'citations: a mis-cased path resolves in commit mode'

        # Check 1: structure-debt receipt freshness, via the Python helper.
        $r1 = Join-Path $temp 'receipt'
        New-FixtureRepo -Path $r1
        New-Item -ItemType Directory -Path (Join-Path $r1 'src') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $r1 'tools/ci') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $r1 'src/Rep.cs') -Value @('namespace N;', '', 'public class Rep { }') -NoNewline:$false
        $r1base = Commit-Fixture -Path $r1 -Message 'base'
        Set-Content -LiteralPath (Join-Path $r1 'src/Rep.cs') -Value @('namespace N;', '', 'public class Rep { public int Value; }') -NoNewline:$false
        $debtPlaceholder = @('# Structure debt register', '', '## src/Rep.cs', '', '<!-- spec-source: 000000000000~000000000000~000000000000~000000000000~000000000000~0000 -->', '', 'pending')
        Set-Content -LiteralPath (Join-Path $r1 'tools/ci/structure-debt.md') -Value $debtPlaceholder -NoNewline:$false
        $r1head = Commit-Fixture -Path $r1 -Message 'head'
        $refresh = Invoke-Python -Root $r1 -Arguments @($ratchetScript, '--root', $r1, '--refresh-register', 'src/Rep.cs')
        Assert-True ($refresh.ExitCode -eq 0) 'receipt fixture: refresh-register succeeds'
        $receiptArgs = @($checkScript, '--check', 'receipt', '--repo', $r1, '--base', $r1base, '--head', $r1head)
        Assert-True ((Invoke-Python -Root $r1 -Arguments $receiptArgs).ExitCode -eq 0) 'receipt: a fresh receipt passes'
        Set-Content -LiteralPath (Join-Path $r1 'tools/ci/structure-debt.md') -Value $debtPlaceholder -NoNewline:$false
        $receiptStale = Invoke-Python -Root $r1 -Arguments $receiptArgs
        Assert-True ($receiptStale.ExitCode -eq 1) 'receipt: a stale receipt fails'
        Assert-True ($receiptStale.Output -like '*--refresh-register*') 'receipt: the failure prints the refresh command'

        # Check 2: a grown unit needs its own verdict; a register receipt does not settle it.
        function New-GrowSource {
            param([int]$Interior, [string]$Comment)
            $lines = @('namespace N;', '', 'public class Grow', '{', "    // $Comment", '    public static void Big()', '    {')
            for ($i = 0; $i -lt $Interior; $i++) { $lines += "        var value$i = $i;" }
            $lines += @('    }', '}')
            $lines
        }
        $r2 = Join-Path $temp 'grown'
        New-FixtureRepo -Path $r2
        New-Item -ItemType Directory -Path (Join-Path $r2 'src') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $r2 'tools/ci') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $r2 'src/Grow.cs') -Value (New-GrowSource -Interior 42 -Comment 'old') -NoNewline:$false
        $r2base = Commit-Fixture -Path $r2 -Message 'base'
        Set-Content -LiteralPath (Join-Path $r2 'src/Grow.cs') -Value (New-GrowSource -Interior 48 -Comment 'old') -NoNewline:$false
        $r2head = Commit-Fixture -Path $r2 -Message 'head'
        $grownArgs = @($checkScript, '--check', 'grown', '--repo', $r2, '--base', $r2base, '--head', $r2head)
        Assert-True ((Invoke-Python -Root $r2 -Arguments $grownArgs).ExitCode -eq 1) 'grown: a longer unit without a verdict fails'
        $growDebt = @('# Structure debt register', '', '## src/Grow.cs', '', '<!-- spec-source: 000000000000~000000000000~000000000000~000000000000~000000000000~0000 -->', '', 'pending')
        Set-Content -LiteralPath (Join-Path $r2 'tools/ci/structure-debt.md') -Value $growDebt -NoNewline:$false
        $refreshGrow = Invoke-Python -Root $r2 -Arguments @($ratchetScript, '--root', $r2, '--refresh-register', 'src/Grow.cs')
        Assert-True ($refreshGrow.ExitCode -eq 0) 'grown fixture: refresh-register succeeds'
        Assert-True ((Invoke-Python -Root $r2 -Arguments $grownArgs).ExitCode -eq 1) 'grown: a current register receipt still does not settle a grown unit'
        # PASS: a changed file whose debt units did not grow.
        $r2b = Join-Path $temp 'grown-flat'
        New-FixtureRepo -Path $r2b
        New-Item -ItemType Directory -Path (Join-Path $r2b 'src') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $r2b 'src/Grow.cs') -Value (New-GrowSource -Interior 48 -Comment 'old') -NoNewline:$false
        $flatBase = Commit-Fixture -Path $r2b -Message 'base'
        Set-Content -LiteralPath (Join-Path $r2b 'src/Grow.cs') -Value (New-GrowSource -Interior 48 -Comment 'new') -NoNewline:$false
        $flatHead = Commit-Fixture -Path $r2b -Message 'head'
        Assert-True ((Invoke-Python -Root $r2b -Arguments @($checkScript, '--check', 'grown', '--repo', $r2b, '--base', $flatBase, '--head', $flatHead)).ExitCode -eq 0) 'grown: a same-size change passes'

        # Check 5: registration, exemptions and control witness, via the Python helper.
        $r5 = Join-Path $temp 'registration'
        New-FixtureRepo -Path $r5
        New-Item -ItemType Directory -Path (Join-Path $r5 'tests/SyncShark.Engine.Tests') -Force | Out-Null
        Invoke-Git -Root $r5 -ArgumentList @('commit', '--allow-empty', '-m', 'base', '--no-gpg-sign') | Out-Null
        $r5base = (Invoke-Git -Root $r5 -ArgumentList @('rev-parse', 'HEAD')).Trim()
        New-ExemptFile -Repo $r5 -Methods $exemptNames
        Set-Content -LiteralPath (Join-Path $r5 'tests/SyncShark.Engine.Tests/FooChecks.cs') -Value @('namespace Massdex.SyncShark.Engine.Tests;', 'public static class FooChecks { public static void Run() { } }') -NoNewline:$false
        Set-Content -LiteralPath (Join-Path $r5 'tests/SyncShark.Engine.Tests/Run/NewThingChecks.cs') -Value @('namespace Massdex.SyncShark.Engine.Tests.Run;', 'public static class NewThingChecks { public static void Run() { var m = typeof(Massdex.SyncShark.Engine.Run.Monitor); } }') -NoNewline:$false
        Set-Content -LiteralPath (Join-Path $r5 'tests/SyncShark.Engine.Tests/InlineChecks.cs') -Value @('namespace Massdex.SyncShark.Engine.Tests;', 'public static class InlineChecks { public static void Run() { Other.Run(); } }') -NoNewline:$false
        Set-Content -LiteralPath (Join-Path $r5 'tests/SyncShark.Engine.Tests/CheckRegistry.cs') -Value 'namespace T; public static class CheckRegistry { }' -NoNewline:$false
        $r5head = Commit-Fixture -Path $r5 -Message 'head'
        $registryAll = @'
namespace T;
public static class CheckRegistry
{
    public static readonly System.Collections.Generic.Dictionary<string, System.Action[]> All = new()
    {
        ["syncer"] = [FooChecks.Run, InlineChecks.Run],
        ["run"] = [NewThingChecks.Run],
    };
}
'@
        $regAll = New-RegistryJson -RegistryText $registryAll -Path (Join-Path $temp 'registry-all.json')
        $testsBase = @($checkScript, '--check', 'tests', '--repo', $r5, '--base', $r5base, '--head', $r5head)
        Assert-True ((Invoke-Python -Root $r5 -Arguments ($testsBase + @('--registry', $regAll))).ExitCode -eq 0) 'tests: every check registered under its namespace, exemptions valid'
        # P1-1: a deleted registry entry must fail.
        $regNoFooText = @'
namespace T;
public static class CheckRegistry
{
    public static readonly System.Collections.Generic.Dictionary<string, System.Action[]> All = new()
    {
        ["syncer"] = [InlineChecks.Run],
        ["run"] = [NewThingChecks.Run],
    };
}
'@
        $regNoFoo = New-RegistryJson -RegistryText $regNoFooText -Path (Join-Path $temp 'registry-no-foo.json')
        $noFoo = Invoke-Python -Root $r5 -Arguments ($testsBase + @('--registry', $regNoFoo))
        Assert-True ($noFoo.ExitCode -eq 1 -and $noFoo.Output -like '*FooChecks.Run*') 'tests: a deleted registry entry fails'
        # P1-1: a new Run() in the Run namespace must fail.
        $regNoNewText = @'
namespace T;
public static class CheckRegistry
{
    public static readonly System.Collections.Generic.Dictionary<string, System.Action[]> All = new()
    {
        ["syncer"] = [FooChecks.Run, InlineChecks.Run],
    };
}
'@
        $regNoNew = New-RegistryJson -RegistryText $regNoNewText -Path (Join-Path $temp 'registry-no-new.json')
        $noNew = Invoke-Python -Root $r5 -Arguments ($testsBase + @('--registry', $regNoNew))
        Assert-True ($noNew.ExitCode -eq 1 -and $noNew.Output -like '*NewThingChecks.Run*') 'tests: a new Run() in the Run namespace fails'
        # P1-1: an unrelated x.Run() call in the file must not wire the method.
        $regNoInlineText = @'
namespace T;
public static class CheckRegistry
{
    public static readonly System.Collections.Generic.Dictionary<string, System.Action[]> All = new()
    {
        ["syncer"] = [FooChecks.Run],
        ["run"] = [NewThingChecks.Run],
    };
}
'@
        $regNoInline = New-RegistryJson -RegistryText $regNoInlineText -Path (Join-Path $temp 'registry-no-inline.json')
        $noInline = Invoke-Python -Root $r5 -Arguments ($testsBase + @('--registry', $regNoInline))
        Assert-True ($noInline.ExitCode -eq 1 -and $noInline.Output -like '*InlineChecks.Run*') 'tests: an in-file x.Run() call does not count as registered'
        # P2-4: a stale reviewed exemption fails.
        $regStaleText = @'
namespace T;
public static class CheckRegistry
{
    public static readonly System.Collections.Generic.Dictionary<string, System.Action[]> All = new()
    {
        ["syncer"] = [FooChecks.Run, InlineChecks.Run],
        ["run"] = [NewThingChecks.Run],
        ["extra"] = [ExcludedSavedRowChecks.PrefixFirstRun],
    };
}
'@
        $regStale = New-RegistryJson -RegistryText $regStaleText -Path (Join-Path $temp 'registry-stale.json')
        $stale = Invoke-Python -Root $r5 -Arguments ($testsBase + @('--registry', $regStale))
        Assert-True ($stale.ExitCode -eq 1 -and $stale.Output -like '*exemption stale*') 'tests: a registered reviewed exemption is stale and fails'
        # P2-2: the HarnessSafety exemption fails when it leaves its action list.
        $harness = Join-Path $temp 'harness-stale'
        New-FixtureRepo -Path $harness
        New-Item -ItemType Directory -Path (Join-Path $harness 'tests/SyncShark.Engine.Tests') -Force | Out-Null
        Invoke-Git -Root $harness -ArgumentList @('commit', '--allow-empty', '-m', 'base', '--no-gpg-sign') | Out-Null
        $harnessBase = (Invoke-Git -Root $harness -ArgumentList @('rev-parse', 'HEAD')).Trim()
        New-ExemptFile -Repo $harness -Methods $exemptNames
        Set-Content -LiteralPath (Join-Path $harness 'tests/SyncShark.Engine.Tests/Scenarios/ScenarioChecks.cs') -Value @(
            'namespace Massdex.SyncShark.Engine.Tests.Scenarios;',
            'public static class ScenarioChecks',
            '{',
            '    public static System.Collections.Generic.IReadOnlyList<System.Action> OfflineCases()',
            '    {',
            '        return new System.Action[] { };',
            '    }',
            '    private static void Other()',
            '    {',
            '        HarnessSafety();',
            '    }',
            '    public static void HarnessSafety() { }',
            '}') -NoNewline:$false
        $harnessHead = Commit-Fixture -Path $harness -Message 'head'
        $emptyReg = New-RegistryJson -RegistryText 'namespace T; public static class CheckRegistry { }' -Path (Join-Path $temp 'registry-empty.json')
        $harnessRun = Invoke-Python -Root $harness -Arguments @($checkScript, '--check', 'tests', '--repo', $harness, '--base', $harnessBase, '--head', $harnessHead, '--registry', $emptyReg)
        # A direct call elsewhere must not keep it: only the OfflineCases body counts.
        Assert-True ($harnessRun.ExitCode -eq 1 -and $harnessRun.Output -like '*HarnessSafety*' -and $harnessRun.Output -like '*OfflineCases*') 'tests: HarnessSafety removed from OfflineCases is stale even with a direct call'
        # Control witness: in its group, mismatch, and not registered.
        $controlsOk = Join-Path $temp 'controls-ok.json'
        Set-Content -LiteralPath $controlsOk -Value '{ "control1": { "witness": "FooChecks.Run", "groups": ["syncer"] } }' -NoNewline:$false
        Assert-True ((Invoke-Python -Root $r5 -Arguments ($testsBase + @('--registry', $regAll, '--controls', $controlsOk))).ExitCode -eq 0) 'tests: a control whose witness is in its submitted group passes'
        $controlsBad = Join-Path $temp 'controls-bad.json'
        Set-Content -LiteralPath $controlsBad -Value '{ "control1": { "witness": "FooChecks.Run", "groups": ["stale-freshness"] } }' -NoNewline:$false
        Assert-True ((Invoke-Python -Root $r5 -Arguments ($testsBase + @('--registry', $regAll, '--controls', $controlsBad))).ExitCode -eq 1) 'tests: a control whose witness is in another group fails'
        $controlsMissing = Join-Path $temp 'controls-missing.json'
        Set-Content -LiteralPath $controlsMissing -Value '{ "control1": { "witness": "Missing.Thing", "groups": ["syncer"] } }' -NoNewline:$false
        Assert-True ((Invoke-Python -Root $r5 -Arguments ($testsBase + @('--registry', $regAll, '--controls', $controlsMissing))).ExitCode -eq 1) 'tests: an unregistered witness fails'

        # Static fence: a missing assembly and an assembly older than the sources.
        $sf = Join-Path $temp 'staticfence'
        New-Item -ItemType Directory -Path (Join-Path $sf 'src') -Force | Out-Null
        $sfExe = Join-Path $sf 'bin/Engine.Tests.exe'
        $missingState = Get-StaticFenceState -Root $sf -ExePath $sfExe
        Assert-True (-not $missingState.Exists) 'static fence: a missing assembly is reported missing'
        Set-Content -LiteralPath (Join-Path $sf 'src/Engine.cs') -Value 'class C { }' -NoNewline:$false
        (Get-Item -LiteralPath (Join-Path $sf 'src/Engine.cs')).LastWriteTimeUtc = [datetime]::UtcNow.AddMinutes(-10)
        New-Item -ItemType Directory -Path (Split-Path -Parent $sfExe) -Force | Out-Null
        Set-Content -LiteralPath $sfExe -Value 'x' -NoNewline:$false
        (Get-Item -LiteralPath $sfExe).LastWriteTimeUtc = [datetime]::UtcNow.AddMinutes(-5)
        $freshState = Get-StaticFenceState -Root $sf -ExePath $sfExe
        Assert-True ($freshState.Exists -and -not $freshState.Stale) 'static fence: a fresh assembly is not stale'
        (Get-Item -LiteralPath (Join-Path $sf 'src/Engine.cs')).LastWriteTimeUtc = [datetime]::UtcNow
        $staleState = Get-StaticFenceState -Root $sf -ExePath $sfExe
        Assert-True ($staleState.Stale) 'static fence: an assembly older than the sources is stale'

        Write-Host 'SELF-TEST PASS'
        Write-Host "SELF-TEST assertions: $script:Assertions"
    }
    finally {
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($SelfTest) {
    try { Invoke-SelfTest; exit 0 }
    catch { Write-Host $_.Exception.Message; Write-Host $_.ScriptStackTrace; exit 1 }
}

# ---------------------------------------------------------------- main

if (-not $Base) { throw '-Base is required.' }
$Repo = (Resolve-Path -LiteralPath $Repo).Path
Assert-OwnerArguments -Owners $Owners -Lane $Lane
$ControlsPath = if ($Controls) { (Resolve-Path -LiteralPath $Controls).Path } else { $null }
try { $headFull = (Invoke-Git -Root $Repo -ArgumentList @('rev-parse', $Head)).Trim() }
catch { throw "Cannot resolve head commit '$Head' in $Repo." }
$headAtWorktree = (Invoke-Git -Root $Repo -ArgumentList @('rev-parse', 'HEAD')).Trim()
if ($headFull -ne $headAtWorktree) {
    throw "The checkout is at $headAtWorktree but -Head is $headFull. Run the gate in the head's worktree (or check out that commit)."
}
try { $baseFull = (Invoke-Git -Root $Repo -ArgumentList @('rev-parse', $Base)).Trim() }
catch { throw "Cannot resolve base commit '$Base' in $Repo." }

Write-Host "Pre-review gate"
Write-Host "  repo  : $Repo"
Write-Host "  base  : $baseFull"
Write-Host "  head  : $headFull"
Write-Host ''

$changed = @(Get-ChangedFiles -Root $Repo -BaseCommit $baseFull -HeadCommit $headFull)
$touchedSrc = @($changed | Where-Object { $_.Path -like 'src/*' -and $_.Status -ne 'D' } | ForEach-Object { $_.Path })

# 1. Build: only with -RunTests. The Engine is a project reference of the tests.
$buildOk = $true
if ($RunTests) {
    $build = Invoke-Native -FileName 'dotnet' -Arguments 'build "tests/SyncShark.Engine.Tests/SyncShark.Engine.Tests.csproj" -c Debug --nologo -p:NoWarn=CA1416' -WorkingDirectory $Repo
    $buildOk = $build.ExitCode -eq 0
    if ($buildOk) { Write-Host 'BUILD PASS  Engine + Engine.Tests (Debug)' }
    else {
        Write-Host 'BUILD FAIL'
        ($build.Output -split "`n" | Select-Object -Last 25) | ForEach-Object { Write-Host ('  ' + $_) }
        $script:Attention.Add('build failed')
    }
} else {
    Write-Host 'BUILD skipped (the default gate runs no tests; tests run in CI. Pass -RunTests for an approved run)'
}

# 2. Registry mapping: always printed; the groups run only with -RunTests.
$registryPath = Join-Path $Repo 'tests/SyncShark.Engine.Tests/CheckRegistry.cs'
$registryIndex = Get-CheckRegistryIndex -Text (Get-Content -LiteralPath $registryPath -Raw)
$groups = $registryIndex.Groups
$selection = Get-GroupsForChangedFiles -ChangedFiles $changed -Groups $groups
$liveGroups = @(Get-LiveGroups -Root $Repo)
$hostedSelected = @($selection.Groups | Where-Object { $liveGroups -cnotcontains $_ })
$liveSelected = @($selection.Groups | Where-Object { $liveGroups -ccontains $_ })
# Unmapped files, shared helpers and files outside the Engine registry raise
# ATTENTION only when a committed .ci-lane.json can under-select; with none, CI
# runs every group (sync-shark-ci.yml select step).
$laneEngine = Get-LaneEngineGroups -Root $Repo -Commit $headFull
if ($null -eq $laneEngine) { Write-Host 'LANE  no .ci-lane.json: CI runs every group' }
foreach ($path in $selection.Unmapped) { Write-Host "UNMAPPED  $path" }
foreach ($path in $selection.SharedHelpers) { Write-Host "SHARED HELPER  $path" }
foreach ($path in $selection.OutsideRegistry) { Write-Host "OUTSIDE REGISTRY  $path (select core/ui/build by hand or run the full matrix)" }
foreach ($item in (Get-SelectionAttention -Unmapped $selection.Unmapped -SharedHelpers $selection.SharedHelpers -OutsideRegistry $selection.OutsideRegistry -HostedGroups $hostedSelected -LaneEngine $laneEngine)) {
    $script:Attention.Add($item)
}
Write-Host ''
Write-Host ('CI CHECK GROUPS  ' + ($hostedSelected -join ', '))
if ($liveSelected.Count -gt 0) {
    Write-Host ('  live route only (do not put in .ci-lane.json): ' + ($liveSelected -join ', '))
}
if (-not $RunTests) {
    Write-Host '  (not executed: the default gate runs no tests; pass -RunTests only for an approved run)'
} else {
    # One fixed path for the build's configuration, matching CI's Debug build.
    $exe = Join-Path $Repo 'tests/SyncShark.Engine.Tests/bin/Debug/net8.0-windows/SyncShark.Engine.Tests.exe'
    $runGroups = $hostedSelected
    if ($IncludeLive) { $runGroups = @($selection.Groups) }
    if ($buildOk -and (Test-Path -LiteralPath $exe -PathType Leaf)) {
        $runRoot = Join-Path $ScratchRoot ([guid]::NewGuid().ToString('N'))
        try {
            New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
            foreach ($group in $runGroups) {
                $run = Invoke-Native -FileName $exe -Arguments ('--engine-checks ' + $group) -WorkingDirectory $Repo -Environment @{
                    TEMP = $runRoot; TMP = $runRoot; SYNCSHARK_TEST_ROOT = $runRoot
                }
                $script:CheckResults[$group] = if ($run.ExitCode -eq 0) { 'success' } else { 'failure' }
                if ($run.ExitCode -eq 0) { Write-Host "  PASS $group" }
                else {
                    Write-Host "  FAIL $group"
                    ($run.Output -split "`n" | Where-Object { $_ -match '^(FAIL|SKIP|PASS) ' } | Select-Object -Last 30) | ForEach-Object { Write-Host ('    ' + $_) }
                    $script:Attention.Add("check group '$group' failed")
                }
            }
        } finally {
            Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    } else {
        Write-Host '  CHECK GROUPS skipped: test executable not found'
        $script:Attention.Add('test executable not found')
    }
}

# 3. Spec ratchet and structure register.
$ratchetSkipped = $true
if (-not $SkipRatchet) {
    Write-Host ''
    $ratchet = Get-RatchetReport -Root $Repo -HeadCommit $headFull -TouchedPaths $touchedSrc -TimeoutSeconds $RatchetTimeoutSeconds
    if (-not $ratchet.Present) {
        Write-Host 'RATCHET skipped: tools/ci/check-spec-ratchet.py not present'
    } else {
        $ratchetSkipped = $false
        $script:CheckResults['ratchet'] = if ($ratchet.TimedOut) { 'cancelled' } elseif ($ratchet.ExitCode -eq 2) { 'incomplete' } elseif ($ratchet.ExitCode -eq 0) { 'success' } else { 'failure' }
        $state = if ($ratchet.TimedOut) { 'TIMEOUT' } elseif ($ratchet.ExitCode -eq 2) { 'INCOMPLETE' } elseif ($ratchet.ExitCode -eq 0) { 'PASS' } else { 'FAIL' }
        Write-Host "RATCHET $state  $($ratchet.Summary)"
        $ratchet.Text -split "`n" | Where-Object { $_ -match '^(TRUSTED RELEASE|INHERITED DEBT|POLICY REVIEW|NEW FINDING|INCOMPLETE:|FAIL )' } | ForEach-Object { Write-Host $_ }
        $ratchetAttention = Get-RatchetAttention -Ratchet $ratchet -TimeoutSeconds $RatchetTimeoutSeconds
        if ($ratchetAttention) { $script:Attention.Add($ratchetAttention) }

        $sections = @{}
        $debtFile = Join-Path $Repo 'tools/ci/structure-debt.md'
        $debtPresent = Test-Path -LiteralPath $debtFile -PathType Leaf
        if ($debtPresent) {
            $lineNumber = 0
            foreach ($line in Get-Content -LiteralPath $debtFile) {
                $lineNumber++
                $heading = [regex]::Match($line, '^##\s+(?<path>src/\S+)\s*$')
                if ($heading.Success) { $sections[$heading.Groups['path'].Value] = $lineNumber }
            }
        }
        if ($ratchet.Rows.Count -eq 0) { Write-Host '  size rows        : none for changed src files' }
        else {
            Write-Host '  size rows        : refresh the review ledger for'
            foreach ($row in $ratchet.Rows) { Write-Host "    $($row.Category)  $($row.Path)  ($($row.Verdict))" }
            $script:Attention.Add('spec ratchet has size rows needing review')
        }
        # Repository totals are printed for context; a non-zero ratchet exit is the failure.
        if ($ratchet.Totals.Count -gt 0) {
            Write-Host '  repository totals'
            foreach ($row in $ratchet.Totals) {
                $range = if ($null -ne $row.Old) { "  $($row.Old) -> $($row.New)" } else { '' }
                Write-Host "    $($row.Category)  $($row.Path)$range"
            }
        }
        if (-not $debtPresent) {
            Write-Host '  register sections: not checked (no tools/ci/structure-debt.md)'
        } elseif ($ratchet.Register.Count -eq 0) { Write-Host '  register sections: current for changed src files' }
        else {
            Write-Host '  register sections: refresh these structure-debt.md sections'
            foreach ($row in $ratchet.Register) {
                $where = if ($sections.ContainsKey($row.Path)) { "tools/ci/structure-debt.md:$($sections[$row.Path])" } else { 'tools/ci/structure-debt.md (section missing)' }
                Write-Host "    $($row.Path)  $($row.Reason)  -> $where"
            }
            $script:Attention.Add('structure-debt.md receipts are stale for changed src files')
        }
    }
} else {
    Write-Host ''
    Write-Host 'RATCHET skipped (-SkipRatchet); state this in the attached output'
}

# 3b. Deterministic checks from tools/ci/prereview_checks.py.
Write-Host ''
$helperPresent = (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'ci/prereview_checks.py') -PathType Leaf) -or
    (Test-Path -LiteralPath (Join-Path $Repo 'tools/ci/prereview_checks.py') -PathType Leaf)
$testChanged = @($changed | Where-Object { $_.Status -ne 'D' -and $_.Path -like 'tests/SyncShark.Engine.Tests/*.cs' }).Count -gt 0
$registryJson = $null
if ($helperPresent) {
    New-Item -ItemType Directory -Force -Path $ScratchRoot | Out-Null
    $registryJson = Join-Path $ScratchRoot ('prereview-registry-' + [guid]::NewGuid().ToString('N') + '.json')
    $methods = @{}
    foreach ($key in $registryIndex.Methods.Keys) { $methods[$key] = @($registryIndex.Methods[$key]) }
    ($methods | ConvertTo-Json -Compress) | Set-Content -LiteralPath $registryJson -Encoding utf8
}
try {
    $mechanical = @(
        @{ Name = 'receipt'; Label = 'structure-debt receipts match the changed src bytes'; NeedsTests = $false },
        @{ Name = 'grown'; Label = 'grown units have a current spec-verdicts.json verdict'; NeedsTests = $false },
        @{ Name = 'tests'; Label = 'test check methods are registered'; NeedsTests = $true }
    )
    foreach ($item in $mechanical) {
        # A missing helper must never let the summary read PASS.
        if (-not $helperPresent) {
            Write-Host ("CHECK {0}  skipped: tools/ci/prereview_checks.py not present beside the gate or in this lane" -f $item.Name)
            $script:Attention.Add("$($item.Name) check skipped (helper not present)")
            continue
        }
        # The ratchet already reports receipt staleness and grown units; running
        # our copy as well is redundant work (P2-1/P2-3).
        if ($item.Name -in @('receipt', 'grown') -and -not $ratchetSkipped) {
            Write-Host ("CHECK {0}  skipped: the spec ratchet already reports this" -f $item.Name)
            continue
        }
        # P2 cost: registration is relevant only when a test file changed, but an
        # explicit -Controls must still run its witness check on a src-only lane.
        if ($item.NeedsTests -and -not (Test-ShouldRunTests -TestChanged $testChanged -ControlsPath $ControlsPath)) {
            Write-Host 'CHECK tests  skipped: no tests/SyncShark.Engine.Tests file changed'
            continue
        }
        $checkRegistry = if ($item.Name -eq 'tests') { $registryJson } else { $null }
        $checkControls = if ($item.Name -eq 'tests') { $ControlsPath } else { $null }
        $run = Invoke-PreReviewCheck -Root $Repo -Check $item.Name -BaseCommit $baseFull -HeadCommit $headFull -Registry $checkRegistry -Controls $checkControls
        if ($run.ExitCode -eq 0) { Write-Host ("CHECK {0}  PASS  {1}" -f $item.Name, $item.Label); continue }
        if ($run.ExitCode -eq 2) {
            Write-Host ("CHECK {0}  ERROR  {1}" -f $item.Name, $item.Label)
            ($run.Output -split "`n" | Select-Object -Last 5) | ForEach-Object { Write-Host ('  ' + $_) }
            $script:Attention.Add("$($item.Name) check could not run")
            continue
        }
        Write-Host ("CHECK {0}  FAIL  {1}" -f $item.Name, $item.Label)
        foreach ($line in ($run.Output -split "`n")) { if ($line.Trim()) { Write-Host ('  ' + $line.TrimEnd()) } }
        $script:Attention.Add($item.Label)
    }
} finally {
    if ($registryJson -and (Test-Path -LiteralPath $registryJson)) { Remove-Item -LiteralPath $registryJson -Force -ErrorAction SilentlyContinue }
}

# 3c. Ownership of the diff, when a path-glob-to-lane map is supplied.
if ($Owners) {
    Write-Host ''
    $ownership = @(Get-OwnershipFindings -ChangedFiles $changed -OwnersPath $Owners -Lane $Lane)
    if ($ownership.Count -eq 0) { Write-Host "OWNERSHIP  PASS  no changed path is owned by another lane (lane '$Lane')" }
    else {
        Write-Host 'OWNERSHIP  FAIL  the diff touches paths owned by another lane:'
        foreach ($finding in $ownership) { Write-Host "  $finding" }
        $script:Attention.Add('diff touches a path owned by another lane')
    }
}

# 3d. Repeated-expression detector (advisory only; never fails the gate).
Write-Host ''
$repeats = @(Get-RepeatedExpressionFindings -Root $Repo -HeadCommit $headFull -ChangedFiles $changed)
if ($repeats.Count -eq 0) { Write-Host 'REPEATS  none: no 40+ character expression repeats 5+ times in changed src' }
else {
    Write-Host 'REPEATS  ATTENTION (advisory, not a failure): a long expression repeats 5+ times'
    foreach ($finding in $repeats) { Write-Host "  $finding" }
}

# 3e. Reuse existing binaries only when local execution was explicitly requested.
# Otherwise the mandatory summary is INCOMPLETE, never a waiver of the fences.
if ($StaticFence -or $RunTests) {
    $fenceSets = @(
        @{ Exe = 'tests/SyncShark.Engine.Tests/bin/Debug/net8.0-windows/SyncShark.Engine.Tests.exe'; Groups = @(Get-MandatoryEngineGroups); Arguments = '--engine-checks ' },
        @{ Exe = 'tests/SyncShark.Core.Tests/bin/Debug/net8.0/SyncShark.Core.Tests.exe'; Groups = @('layout'); Arguments = '--engine-layout-checks' }
    )
    foreach ($set in $fenceSets) {
        $pending = @($set.Groups | Where-Object { -not $script:CheckResults.ContainsKey($_) })
        if (-not $pending.Count) { continue }
        $fenceExe = Join-Path $Repo $set.Exe
        $fence = Get-StaticFenceState -Root $Repo -ExePath $fenceExe
        if (-not $fence.Exists -or $fence.Stale) {
            Write-Host "STATIC FENCE  INCOMPLETE  missing or stale assembly: $fenceExe (build it first)"
            continue
        }
        $fenceRoot = Join-Path $ScratchRoot ([guid]::NewGuid().ToString('N'))
        try {
            New-Item -ItemType Directory -Force -Path $fenceRoot | Out-Null
            foreach ($group in $pending) {
                $arguments = $set.Arguments
                if ($group -ne 'layout') { $arguments += $group }
                $run = Invoke-Native -FileName $fenceExe -Arguments $arguments -WorkingDirectory $Repo -Environment @{
                    TEMP = $fenceRoot; TMP = $fenceRoot; SYNCSHARK_TEST_ROOT = $fenceRoot
                }
                $script:CheckResults[$group] = if ($run.ExitCode -eq 0) { 'success' } else { 'failure' }
                if ($run.ExitCode -eq 0) { Write-Host "STATIC FENCE  PASS  $group" }
                else {
                    Write-Host "STATIC FENCE  FAIL  $group"
                    ($run.Output -split "`n" | Select-Object -Last 30) | ForEach-Object { Write-Host ('    ' + $_) }
                    $script:Attention.Add("static fence group '$group' failed")
                }
            }
        } finally {
            Remove-Item -LiteralPath $fenceRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# 4. Design-doc drift.
Write-Host ''
$drift = @(Get-DocDrift -Root $Repo -BaseCommit $baseFull -HeadCommit $headFull -ChangedFiles $changed)
$removedDrift = @($drift | Where-Object { $_.Kind -eq 'removed' })
$touchedDrift = @($drift | Where-Object { $_.Kind -eq 'touched' })
if ($removedDrift.Count -eq 0) { Write-Host 'DOC DRIFT  no removed src name is still referenced in docs/design' }
else {
    Write-Host 'DOC DRIFT  removed src names still referenced (check these lines):'
    foreach ($hit in $removedDrift) { Write-Host "  $($hit.File):$($hit.Line)  $($hit.Name)" }
    $script:Attention.Add('removed src names still referenced in docs/design')
}
if ($touchedDrift.Count -eq 0) { Write-Host '           no touched class is named in docs/design' }
else {
    Write-Host '           touched classes named in docs/design (check these lines):'
    foreach ($hit in $touchedDrift) { Write-Host "  $($hit.File):$($hit.Line)  $($hit.Name)" }
}

# 5. Stale line references in the writer's report(s).
$citationReports = @()
if ($Report) { $citationReports += [pscustomobject]@{ Path = $Report; Commit = $CitationsSha } }
foreach ($path in $Reports) { $citationReports += [pscustomobject]@{ Path = $path; Commit = $(if ($CitationsSha) { $CitationsSha } else { $headFull }) } }
foreach ($citationReport in $citationReports) {
    Write-Host ''
    if (-not (Test-Path -LiteralPath $citationReport.Path -PathType Leaf)) { throw "Report not found: $($citationReport.Path)" }
    $citations = @(Get-StaleCitations -Root $Repo -ReportPath $citationReport.Path -Commit $citationReport.Commit)
    $where = if ($citationReport.Commit) { " at $($citationReport.Commit)" } else { '' }
    if ($citations.Count -eq 0) { Write-Host "REPORT  $($citationReport.Path): every file:line citation resolves$where" }
    else {
        Write-Host "REPORT  $($citationReport.Path): stale file:line citations$where (check these):"
        foreach ($finding in $citations) { Write-Host "  report line $($finding.ReportLine): $($finding.Citation)  $($finding.Reason)" }
        $script:Attention.Add('report has stale file:line citations')
    }
}

# 6. Missing mandatory evidence takes precedence; retain all observed failures.
Write-Host ''
$outcome = Get-MandatoryCheckOutcome -Results $script:CheckResults -Required $script:RequiredChecks
if ($outcome.State -eq 'PASS' -and $script:Attention.Count) {
    $outcome.State = 'FAIL'
    $outcome.ExitCode = 1
}
Write-Host "SUMMARY  $($outcome.State)"
foreach ($item in $outcome.Incomplete) { Write-Host "  - mandatory check incomplete: $item" }
foreach ($item in $script:Attention) { Write-Host "  - $item" }
exit $outcome.ExitCode
