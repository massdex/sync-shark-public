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

    Run this gate on a checkout of the CI run's exact public head. Without its private
    OWNERSHIP table, the gate skips derived coverage comparison; the helper finalized .ci-lane.json.
    The mirror changes source hashes; its temporary export is removed after publication.
      gh run download <run> -R <ci-repository> -n literal-sites --dir <artifact-dir>
      $env:RATCHET_LITERAL_SITES = '<artifact-dir>/literal-sites.jsonl'
    CI is authoritative. Use its same-run artifact; missing/mismatched evidence is INCOMPLETE. Do not build locally.

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

.PARAMETER OwnershipSelfTest
    Run the real ownership-table controls privately before export and exit.

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
    [switch]$SelfTest,
    [switch]$OwnershipSelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ci/LaneSelection.ps1')
. (Join-Path $PSScriptRoot 'ci/PreReviewCitations.ps1')
. (Join-Path $PSScriptRoot 'ci/PreReviewFixture.ps1')
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
    ConvertFrom-NameStatus -Text $text
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
    if ($Ratchet.ExitCode -eq 2) { return 'spec ratchet incomplete: check or evidence unavailable' }
    if ($Ratchet.ExitCode -ne 0) { return 'spec ratchet failed' }
    $null
}

function Test-RatchetCompleted {
    param([object]$Ratchet)
    -not $Ratchet.TimedOut -and $Ratchet.ExitCode -in @(0, 1)
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

# Match the current runner's named output to the registry; no output for a name is not a pass.
function Get-RunnerEvidence([string]$Registry, [string]$Output) {
    $expected = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $seen = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $group = $null
    foreach ($line in $Registry -split "`n") {
        if ($line -cmatch '^\s*\["([^"]+)"\]\s*=') { $group=$Matches[1] }
        elseif ($line -cmatch '^\s*new\("([^"]+)",') { $expected.Add($Matches[1],$group) }
    }
    if (-not $expected.Count) { throw 'Empty engine-check registry.' }
    foreach ($line in $Output -split '\r?\n') {
        if ($line -cnotmatch '^(PASS|NOT IMPLEMENTED|FAIL) (.+)$') { continue }
        $status=$Matches[1]; $name=$Matches[2]
        if ($status -ceq 'FAIL') { $name=($name -split ': ',2)[0] }
        if (-not $expected.ContainsKey($name)) { throw "Unexpected engine-check result: $name" }
        $seen.Add($name,$status)
    }
    $evidence=@{}
    $priority=@{ success=0; 'not-implemented'=1; missing=2; failure=3 }
    foreach ($entry in $expected.GetEnumerator()) {
        $name=$entry.Key; $key=$entry.Value
        $value=if (-not $seen.ContainsKey($name)) { 'missing' } elseif ($seen[$name] -ceq 'PASS') { 'success' } elseif ($seen[$name] -ceq 'FAIL') { 'failure' } else { 'not-implemented' }
        if (-not $evidence.ContainsKey($key) -or $priority[$value] -gt $priority[$evidence[$key]]) { $evidence[$key]=$value }
        if ($name -ceq 'Layout/Dependencies') { $evidence['layout']=$value }
        if ($value -ne 'success') { Write-Host "  $value $name" }
    }
    $evidence
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

function Invoke-OwnershipSelfTest {
        $sampleRegistry = @'
["dependencies"] = [
    new("Layout/Dependencies", Dependency.Run),
["in-progress"] = [
    new("Waiting check", Waiting.Run),
["absent"] = [
    new("Missing check", Missing.Run),
["broken"] = [
    new("Failed check", Failed.Run),
'@
        $sample = Get-RunnerEvidence -Registry $sampleRegistry -Output "PASS Layout/Dependencies`nNOT IMPLEMENTED Waiting check`nFAIL Failed check: assertion"
        Assert-True ($sample['layout'] -ceq 'success' -and $sample['dependencies'] -ceq 'success' -and $sample['in-progress'] -ceq 'not-implemented' -and $sample['absent'] -ceq 'missing' -and $sample['broken'] -ceq 'failure') 'Named runner outcomes are not interchangeable'
        $tree = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
        $realGroups = (Get-CheckRegistryIndex -Text ([IO.File]::ReadAllText("$tree/tests/SyncShark.Engine.Tests/CheckRegistry.cs"))).Groups
        $live = @(Get-GroupList -Path "$tree/tools/ci/live-groups.txt")
        $hosted = @(Get-HostedGroups -Groups $realGroups -Live $live)
        $rows = @(Get-OwnershipRows -Text ([IO.File]::ReadAllText("$tree/docs/OWNERSHIP.md")))
        foreach ($row in $rows) {
            Assert-True ($row.Groups -cin @('all', 'none') -or @($row.Groups -split ' ' | Where-Object { @($realGroups.Keys) -cnotcontains $_ }).Count -eq 0) 'C5 every cell has valid grammar'
        }
        $cases = @(
            @{ Id = 'C1'; Path = 'src/SyncShark.Engine/Storage/Store.cs'; Want = @('storage','acceptance','call-budget') },
            @{ Id = 'C2'; Path = 'src/SyncShark.Engine/Dropbox/DropboxReplyShapes.cs'; Want = $hosted },
            @{ Id = 'C3'; Path = 'src/SyncShark.Engine/engine-settings.json'; Status = 'D'; Want = $hosted },
            @{ Id = 'C4'; Path = 'src/SyncShark.Engine/PathScope.cs'; Want = $hosted },
            @{ Id = 'C11'; Path = 'tests/SyncShark.Engine.Tests/Layout/CliParityChecks.cs'; Want = @('cli') },
            @{ Id = 'C13'; Path = 'src/SyncShark.Engine/Edge/Render.cs'; Want = @('edge','executor','run','strings') },
            @{ Id = 'C14'; Path = 'tests/SyncShark.Engine.Tests/Layout/LiteralsChecks.cs'; Want = @('literals') }
        )
        $paths = @((Invoke-Git -Root $tree -ArgumentList @('ls-files')) -split "`n" | Where-Object { $_ })
        $inputs = @(Get-ProjectInputs -Paths $paths -Read { param($path) [IO.File]::ReadAllText("$tree/$path") })
        foreach ($required in @('src/SyncShark.Engine/Edge/engine-cli.json','src/SyncShark.Engine/Dropbox/dropbox-routes.json','src/SyncShark.Engine/engine-settings.json')) {
            Assert-True ($inputs.Path -ccontains $required) "C10 nonvacuity: $required"
        }
        foreach ($input in $inputs) {
            foreach ($status in @('M','D')) { $cases += @{ Id = 'C10'; Path = $input.Path; Status = $status; Want = $hosted } }
        }
        foreach ($path in $paths | Where-Object { $_ -like 'tests/SyncShark.Engine.Tests/TestDoubles/*Side.cs' }) {
            $cases += @{ Id = 'C12'; Path = $path; Want = $hosted }
        }
        foreach ($case in $cases) {
            $status = if ($case.ContainsKey('Status')) { $case.Status } else { 'M' }
            $mapped = Get-GroupsForChangedFiles -ChangedFiles @(@{ Path = $case.Path; Status = $status }) -Groups $realGroups -OwnershipRows $rows -HostedGroups $hosted
            $expanded = @(Expand-EngineGroups -Selected $mapped.Groups -Groups @($realGroups.Keys))
            foreach ($group in $case.Want) { Assert-True ($expanded -ccontains $group) "$($case.Id) $($case.Path) $status covers $group" }
        }
        foreach ($path in @('tools/Deploy-SyncShark.ps1')) {
            $mapped = Get-GroupsForChangedFiles -ChangedFiles @(@{ Path = $path; Status = 'M' }) -Groups $realGroups -OwnershipRows $rows -HostedGroups $hosted
            Assert-True (($mapped.Groups -join ',') -ceq 'dependencies,literals,strings') "C15 non-input $path is mandatory-only"
        }
        $synthetic = @([pscustomobject]@{ Regex = Convert-GlobToRegex 'docs/**'; Groups = 'none' },
            [pscustomobject]@{ Regex = Convert-GlobToRegex 'src/{a.cs,b.cs}'; Groups = 'store' })
        foreach ($case in @(@{ Id='C6'; Path='src/new.cs'; Rows=$synthetic; Count=$hosted.Count; Uncertain=1 },
            @{ Id='C8'; Path='docs/readme.md'; Rows=$synthetic; Count=3; Uncertain=0 },
            @{ Id='C9'; Path='docs/readme.md'; Rows=@(); Count=$hosted.Count; Uncertain=1 })) {
            $mapped = Get-GroupsForChangedFiles -ChangedFiles @(@{Path=$case.Path;Status='M'}) -Groups $realGroups -OwnershipRows $case.Rows -HostedGroups $hosted
            Assert-True ($mapped.Groups.Count -eq $case.Count -and $mapped.Uncertain.Count -eq $case.Uncertain) "$($case.Id) conservative fallback"
        }
        $misCased = @([pscustomobject]@{ Regex = Convert-GlobToRegex 'docs/**'; Groups = 'Store' })
        $mapped = Get-GroupsForChangedFiles -ChangedFiles @(@{Path='docs/a';Status='M'}) -Groups $realGroups -OwnershipRows $misCased -HostedGroups $hosted
        Assert-True ($mapped.Uncertain.Count -eq 1 -and $mapped.Uncertain[0] -ceq 'UNCERTAIN docs/a: unknown CI group token') 'C5 mis-cased token is uncertain'
        Assert-True (@(Compare-Object $mapped.Groups $hosted -CaseSensitive).Count -eq 0) 'C5 mis-cased token selects every hosted group'
        $rejected = $false
        try { Get-GroupsForChangedFiles -ChangedFiles @(@{Path='docs/a';Status='M'}) -Groups $realGroups -OwnershipRows @($synthetic[0],$synthetic[0]) -HostedGroups $hosted | Out-Null }
        catch { $rejected = $_.Exception.Message -like '*matches several OWNERSHIP rows' }
        Assert-True $rejected 'C7 overlapping rows throw'
    Write-Host 'OWNERSHIP SELF-TEST PASS'
    Write-Host "OWNERSHIP SELF-TEST assertions: $script:Assertions"
}

if ($SelfTest -or $OwnershipSelfTest) {
    try {
        if ($OwnershipSelfTest) { Invoke-OwnershipSelfTest } else { Invoke-SelfTest -ToolsRoot $PSScriptRoot }
        exit 0
    }
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
$registryIndex = Get-CheckRegistryIndex -Text (Get-HeadFileText -Root $Repo -Commit $headFull -Path 'tests/SyncShark.Engine.Tests/CheckRegistry.cs')
$groups = $registryIndex.Groups
$liveGroups = @(Get-GroupList -Text (Get-HeadFileText -Root $Repo -Commit $headFull -Path 'tools/ci/live-groups.txt'))
$hostedGroups = @(Get-HostedGroups -Groups $groups -Live $liveGroups)
$ownershipText = Get-HeadFileText -Root $Repo -Commit $headFull -Path 'docs/OWNERSHIP.md'
$ownership = @(Get-OwnershipRows -Text $ownershipText)
$selection = Get-GroupsForChangedFiles -ChangedFiles $changed -Groups $groups -OwnershipRows $ownership -HostedGroups $hostedGroups
$hostedSelected = @(Expand-EngineGroups -Selected $selection.Groups -Groups @($groups.Keys))
$liveSelected = @($selection.DroppedLive)
$laneText = Get-HeadFileText -Root $Repo -Commit $headFull -Path '.ci-lane.json'
if ($null -eq $ownershipText) { Write-Host "derived selection is private; the committed .ci-lane.json is the helper's finalized selection" }
elseif ($null -eq $laneText) { Write-Host 'LANE no .ci-lane.json: the helper writes it; select refuses a lane without it' }
else {
    $coverage = Test-SelectionCovers -Selection ($laneText | ConvertFrom-Json) -Required @{ engine = $hostedSelected } -Groups @($groups.Keys)
    foreach ($item in $coverage.Missing) { $script:Attention.Add("the helper will add $item; a mutant must carry $item") }
}
foreach ($path in $selection.Unmapped) { Write-Host "UNMAPPED  $path" }
foreach ($path in $selection.SharedHelpers) { Write-Host "SHARED HELPER  $path" }
foreach ($path in $selection.OutsideRegistry) { Write-Host "OUTSIDE REGISTRY  $path (select core/ui/build explicitly)" }
foreach ($line in $selection.Uncertain) { Write-Host $line }
Write-Host ''
Write-Host ('CI CHECK GROUPS  ' + ($hostedSelected -join ', '))
if ($liveSelected.Count -gt 0) {
    Write-Host ('  live route only (do not put in .ci-lane.json): ' + ($liveSelected -join ', '))
}
if (-not ($RunTests -or $StaticFence)) {
    Write-Host '  (not executed: an approved run is required)'
} else {
    $exe = Join-Path $Repo 'tests/SyncShark.Engine.Tests/bin/Debug/net8.0-windows/SyncShark.Engine.Tests.exe'
    $fresh = Get-StaticFenceState -Root $Repo -ExePath $exe
    if (-not $buildOk -or -not $fresh.Exists -or $fresh.Stale) {
        $script:Attention.Add('engine test executable missing, stale or not built')
    } else {
        $runRoot = Join-Path $ScratchRoot ([guid]::NewGuid().ToString('N'))
        try {
            New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
            $inventory = Join-Path $Repo 'CLI-CAPABILITIES.md'
            $run = Invoke-Native -FileName $exe -Arguments ('"{0}" "{1}"' -f $Repo,$inventory) -WorkingDirectory $Repo -Environment @{
                TEMP = $runRoot; TMP = $runRoot; SYNCSHARK_TEST_ROOT = $runRoot
            }
            $registryText = Get-HeadFileText -Root $Repo -Commit $headFull -Path 'tests/SyncShark.Engine.Tests/CheckRegistry.cs'
            if ($RunTests) { $script:RequiredChecks = @(($script:RequiredChecks + $hostedSelected) | Sort-Object -Unique) }
            $byGroup = Get-RunnerEvidence -Registry $registryText -Output $run.Output
            foreach ($key in $byGroup.Keys) {
                $script:CheckResults[$key] = $byGroup[$key]
                Write-Host "  $($byGroup[$key]) $key"
            }
            if ($run.ExitCode -ne 0) { $script:Attention.Add("engine checks exited $($run.ExitCode)") }
            foreach ($key in $byGroup.Keys) { if ($byGroup[$key] -eq 'failure') { $script:Attention.Add("engine group '$key' failed") } }
        } finally { Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# 3. Spec ratchet and structure register.
$ratchetCompleted = $false
if (-not $SkipRatchet) {
    Write-Host ''
    $ratchet = Get-RatchetReport -Root $Repo -HeadCommit $headFull -TouchedPaths $touchedSrc -TimeoutSeconds $RatchetTimeoutSeconds
    if (-not $ratchet.Present) {
        Write-Host 'RATCHET skipped: tools/ci/check-spec-ratchet.py not present'
    } else {
        $ratchetCompleted = Test-RatchetCompleted -Ratchet $ratchet
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
        if ($item.Name -in @('receipt', 'grown') -and $ratchetCompleted) {
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
