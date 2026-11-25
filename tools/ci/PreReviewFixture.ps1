# Exercise the pre-review gate against isolated fixture repositories.
function Invoke-SelfTest {
    param([string]$ToolsRoot)
    $temp = Join-Path ([IO.Path]::GetTempPath()) ('prereview-selftest-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $tree = (Resolve-Path (Join-Path $ToolsRoot '..')).Path
        $projectTree = @{
            'tests/SyncShark.Engine.Tests/SyncShark.Engine.Tests.csproj' = '<Project><ItemGroup><ProjectReference Include="../../src/Engine/Engine.csproj"/><Compile Include="../../shared/*.cs"/><Compile Include="Own.cs"/><EmbeddedResource Include="own.json"/><EmbeddedResource Include="../../shared/data.json"/></ItemGroup></Project>'
            'src/Engine/Engine.csproj' = '<Project/>'; 'shared/A.cs' = ''; 'shared/B.cs' = ''; 'shared/nested/NotLinked.cs' = ''
        }
        $inventory = @(Get-ProjectInputs -Paths @($projectTree.Keys) -Read { param($path) $projectTree[$path] } | ForEach-Object { "$($_.Kind):$($_.Path)" } | Sort-Object)
        $expected = @('implicit:Directory.Build.props','implicit:global.json','project:tests/SyncShark.Engine.Tests/SyncShark.Engine.Tests.csproj','lock:tests/SyncShark.Engine.Tests/packages.lock.json',
            'linked-compile:shared/A.cs','linked-compile:shared/B.cs','resource:tests/SyncShark.Engine.Tests/own.json','resource:shared/data.json','project:src/Engine/Engine.csproj','lock:src/Engine/packages.lock.json') | Sort-Object
        Assert-True (@(Compare-Object $inventory $expected).Count -eq 0) 'Synthetic project inventory is exact'
        $preSelection = (Invoke-Git -Root $tree -ArgumentList @('rev-parse', 'HEAD')).Trim()
        foreach ($path in @('tools/ci/live-groups.txt','tools/ci/hosted-only.txt')) {
            $text = Get-HeadFileText -Root $tree -Commit $preSelection -Path $path
            Assert-True ((@(Get-GroupList -Text $text) -join ',') -ceq (@(Get-GroupList -Path "$tree/$path") -join ',')) 'Tree-text and path list readers agree'
        }
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
        $mappingInputs = @{ OwnershipRows = @([pscustomobject]@{ Regex = '^.*$'; Groups = 'none' }); HostedGroups = @($groups.Keys) + @(Get-MandatoryEngineGroups) }
        Assert-True ($groups['literals'] -contains 'Layout.LiteralsChecks') 'literal group class'
        Assert-True ($groups['store'] -contains 'Storage.StateStoreChecks') 'store group class'
        Assert-True ($groups['scenarios'] -contains 'Scenarios.ScenarioChecks') 'scenarios group class'

        # P1-1: a src change selects the group that owns <rel>Checks.
        $srcOnly = @([pscustomobject]@{ Status = 'M'; Path = 'src/SyncShark.Engine/Storage/StateStore.cs' })
        $srcMap = Get-GroupsForChangedFiles -ChangedFiles $srcOnly -Groups $groups @mappingInputs
        Assert-True ($srcMap.Groups -contains 'store') 'P1-1 src change maps to its Checks group'
        Assert-True ($srcMap.Groups.Count -gt 1) 'P1-1 red proof: src-only change selects more than dependencies'
        Assert-True ($srcMap.Unmapped.Count -eq 0) 'P1-1 mapped src change is not unmapped'
        # P1-1: an unmatched source path remains visible even when ownership supplies its groups.
        $srcUnknown = @([pscustomobject]@{ Status = 'M'; Path = 'src/SyncShark.Engine/Cloud/Unknown.cs' })
        $srcUnknownMap = Get-GroupsForChangedFiles -ChangedFiles $srcUnknown -Groups $groups @mappingInputs
        Assert-True ($srcUnknownMap.Unmapped.Count -eq 1) 'P1-1 unmapped src change is listed'

        # P2-3: a partial file maps by its class prefix; shared helpers are not unmapped.
        $partial = @([pscustomobject]@{ Status = 'M'; Path = 'tests/SyncShark.Engine.Tests/Run/CallBudgetChecks.Speed.cs' })
        $partialMap = Get-GroupsForChangedFiles -ChangedFiles $partial -Groups $groups @mappingInputs
        Assert-True ($partialMap.Groups -contains 'speed') 'P2-3 partial file maps to its group'
        Assert-True ($partialMap.Unmapped.Count -eq 0) 'P2-3 partial file is not unmapped'
        $helper = @(
            [pscustomobject]@{ Status = 'M'; Path = 'tests/SyncShark.Engine.Tests/TestDoubles/FakeSide.cs' },
            [pscustomobject]@{ Status = 'M'; Path = 'tests/SyncShark.Engine.Tests/Check.cs' }
        )
        $helperMap = Get-GroupsForChangedFiles -ChangedFiles $helper -Groups $groups @mappingInputs
        Assert-True ($helperMap.SharedHelpers.Count -eq 2) 'P2-3 shared helpers reported'
        Assert-True ($helperMap.Unmapped.Count -eq 0) 'P2-3 shared helpers are not unmapped'

        # N3: files outside the Engine registry are listed, not silently ignored.
        $coreOnly = @([pscustomobject]@{ Status = 'M'; Path = 'src/SyncShark.Core/PairSettings.cs' })
        $coreMap = Get-GroupsForChangedFiles -ChangedFiles $coreOnly -Groups $groups @mappingInputs
        Assert-True ($coreMap.OutsideRegistry.Count -eq 1) 'N3 Core src file is outside the Engine registry'
        $appOnly = @([pscustomobject]@{ Status = 'M'; Path = 'apps/SyncShark.App/Main.cs' })
        $appMap = Get-GroupsForChangedFiles -ChangedFiles $appOnly -Groups $groups @mappingInputs
        Assert-True ($appMap.OutsideRegistry.Count -eq 1) 'N3 app file is outside the Engine registry'

        # N2: missing selection is distinct from a committed selection that needs expansion.
        $noLane = Test-SelectionCovers -Selection $null -Required @{ engine = @('dependencies') } -Groups @($groups.Keys)
        Assert-True (-not $noLane.Covers) 'N2 no lane file: helper must write selection'
        $laneCoverage = Test-SelectionCovers -Selection @{ engine = @('dependencies') } -Required @{ engine = @('dependencies', 'speed') } -Groups @($groups.Keys)
        Assert-True (($laneCoverage.Missing -join ',') -ceq 'engine:speed') 'N2 lane file: missing derived group is named'
        Assert-True ($srcUnknownMap.Unmapped -ccontains 'src/SyncShark.Engine/Cloud/Unknown.cs') 'N2 unmapped source path is named'
        Assert-True ($coreMap.OutsideRegistry -ccontains 'src/SyncShark.Core/PairSettings.cs') 'N2 outside-registry source path is named'

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
        Assert-True ((Get-RatchetAttention -Ratchet $incompleteRatchet -TimeoutSeconds 900) -match 'incomplete') 'unavailable check or evidence is incomplete'
        Assert-True ((Get-MandatoryCheckOutcome -Results @{ ratchet = 'skipped' } -Required @('ratchet')).State -eq 'INCOMPLETE') 'a skipped ratchet is incomplete'
        foreach ($case in @(
            @{ Code = 0; TimedOut = $false; Completed = $true },
            @{ Code = 1; TimedOut = $false; Completed = $true },
            @{ Code = 2; TimedOut = $false; Completed = $false },
            @{ Code = 3; TimedOut = $false; Completed = $false },
            @{ Code = 0; TimedOut = $true; Completed = $false }
        )) {
            $result = [pscustomobject]@{ ExitCode = $case.Code; TimedOut = $case.TimedOut }
            Assert-True ((Test-RatchetCompleted -Ratchet $result) -eq $case.Completed) 'receipt/grown fallback runs unless the ratchet completed'
        }

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
        Assert-True ($null -eq (Get-HeadFileText -Root $repo -Commit $headCommit -Path '.ci-lane.json')) 'N2 no lane file reads as null'
        Set-Content -LiteralPath (Join-Path $repo '.ci-lane.json') -Value '{ "engine": ["dependencies", "store"] }' -NoNewline:$false
        Invoke-Git -Root $repo -ArgumentList @('add', '-A') | Out-Null
        Invoke-Git -Root $repo -ArgumentList @('commit', '-m', 'lane', '--no-gpg-sign') | Out-Null
        $laneCommit = (Invoke-Git -Root $repo -ArgumentList @('rev-parse', 'HEAD')).Trim()
        $laneRead = (Get-HeadFileText -Root $repo -Commit $laneCommit -Path '.ci-lane.json') | ConvertFrom-Json
        Assert-True ((Test-SelectionCovers -Selection $laneRead -Required @{ engine = @('store') } -Groups @($groups.Keys)).Covers) 'N2 lane file covers its committed group'

        # Run the actual coverage-report branch on public and private head inputs.
        $gateText = [IO.File]::ReadAllText((Join-Path $ToolsRoot 'Test-PreReview.ps1'))
        $coverageStart = $gateText.LastIndexOf('if ($null -eq $ownershipText)')
        $coverageEnd = $gateText.IndexOf('foreach ($path in $selection.Unmapped)', $coverageStart)
        $coverageBlock = [scriptblock]::Create($gateText.Substring($coverageStart, $coverageEnd - $coverageStart))
        $savedAttention = $script:Attention
        try {
            foreach ($hasOwnership in @($false, $true)) {
                $script:Attention = [Collections.Generic.List[string]]::new()
                $ownershipText = if ($hasOwnership) { 'private table' } else { $null }
                $laneText = '{ "engine": [] }'
                $hostedSelected = @('store')
                $output = (& $coverageBlock 6>&1) -join "`n"
                if ($hasOwnership) {
                    Assert-True ($script:Attention.Count -eq 1 -and $script:Attention[0] -ceq 'the helper will add engine:store; a mutant must carry engine:store') 'N4 private coverage still names omissions'
                } else {
                    Assert-True ($script:Attention.Count -eq 0 -and $output -ceq "derived selection is private; the committed .ci-lane.json is the helper's finalized selection") 'N4 public head trusts finalized selection without false omissions'
                }
            }
        } finally { $script:Attention = $savedAttention }

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
            $laneText = Get-HeadFileText -Root $repo -Commit $emptyLaneCommit -Path '.ci-lane.json'
            Assert-True ($null -ne $laneText) "P2-1 $($laneCase.Label): an existing lane file is not 'no lane file'"
            if ($laneCase.Label -eq 'invalid JSON') {
                $rejected = $false
                try { $laneText | ConvertFrom-Json | Out-Null } catch { $rejected = $true }
                Assert-True $rejected 'P2-1 invalid JSON: selection is rejected'
                continue
            }
            $emptyLane = $laneText | ConvertFrom-Json
            $coverage = Test-SelectionCovers -Selection $emptyLane -Required @{ engine = @('store') } -Groups @($groups.Keys)
            Assert-True ($coverage.Missing -contains 'engine:store') "P2-1 $($laneCase.Label): a selected hosted group is flagged, not hidden"
            $mandatoryCoverage = Test-SelectionCovers -Selection $emptyLane -Required @{ engine = @(Get-MandatoryEngineGroups) } -Groups @($groups.Keys)
            Assert-True $mandatoryCoverage.Covers 'Mandatory groups are selected by CI, not omitted'

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
        $checkScript = Join-Path $ToolsRoot 'ci/prereview_checks.py'
        $ratchetScript = Join-Path $ToolsRoot 'ci/check-spec-ratchet.py'

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
        New-Item -ItemType Directory -Force -Path (Join-Path $r5 'tests/SyncShark.Engine.Tests/Run') | Out-Null
        Set-Content -LiteralPath (Join-Path $r5 'tests/SyncShark.Engine.Tests/FooChecks.cs') -Value @('namespace Massdex.SyncShark.Engine.Tests;', 'public static class FooChecks { public static async Task Run() { await Task.CompletedTask; } }') -NoNewline:$false
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
        # C0: catalog-backed groups and a direct call from a registered entry point.
        $catalogRegistry = @'
namespace T;
public static class CheckRegistry
{
    private static readonly (Action Run, int Shard)[] BudgetCatalog =
    [
        (Run.CallBudgetChecks.RunBudgetCommon, 4),
        (Run.CallBudgetChecks.RunBudgetPair, 1),
        (Executor.ActionExecutorChecks.FileSizedForgetCosts, 4),
        // (Run.GhostChecks.Run, 1),
    ];
    private static readonly Action[] BudgetChecks = ValidateBudget();
    public static readonly Dictionary<string, Action[]> All = new()
    {
        ["budget"] = BudgetChecks,
        ["budget-1"] = BudgetShard(1),
        ["budget-4"] = BudgetShard(4),
    };
}
'@
        $catalogIndex = Get-CheckRegistryIndex -Text $catalogRegistry
        Assert-True (($catalogIndex.Methods['Run.CallBudgetChecks.RunBudgetCommon'] -join ',') -eq 'budget,budget-4') 'catalog: common is registered in its aggregate and exact shard'
        Assert-True (($catalogIndex.Methods['Run.CallBudgetChecks.RunBudgetPair'] -join ',') -eq 'budget,budget-1') 'catalog: another shard is not conflated'
        Assert-True ($catalogIndex.Methods.ContainsKey('Executor.ActionExecutorChecks.FileSizedForgetCosts')) 'catalog: explicit non-budget-class entry is registered'
        Assert-True (-not $catalogIndex.Methods.ContainsKey('Run.GhostChecks.Run')) 'catalog: a commented tuple is not registered'
        $unusedCatalog = Get-CheckRegistryIndex -Text ($catalogRegistry -replace 'BudgetChecks,', '[],' -replace 'BudgetShard\(\d+\),', '[],')
        Assert-True ($unusedCatalog.Methods.Count -eq 0) 'catalog: an unused catalog grants no registration'
        $catalogRoot = Join-Path $temp 'catalog-registration'
        New-Item -ItemType Directory -Force -Path (Join-Path $catalogRoot 'tests/SyncShark.Engine.Tests/Run') | Out-Null
        $callerPath = Join-Path $catalogRoot 'tests/SyncShark.Engine.Tests/Run/CallBudgetChecks.cs'
        $callerText = 'namespace T.Run; public static class CallBudgetChecks { public static void RunBudgetCommon() { StorageCostChecks.Run(); } public static void RunBudgetPair() { } }'
        Set-Content -LiteralPath $callerPath -Value $callerText
        Set-Content -LiteralPath (Join-Path $catalogRoot 'tests/SyncShark.Engine.Tests/Run/StorageCostChecks.cs') -Value 'namespace T.Run; public static class StorageCostChecks { public static void Run() { } }'
        Set-Content -LiteralPath (Join-Path $catalogRoot 'tests/SyncShark.Engine.Tests/ActionExecutorChecks.cs') -Value 'namespace T.Executor; public static class ActionExecutorChecks { public static void FileSizedForgetCosts() { } }'
        $catalogJson = New-RegistryJson -RegistryText $catalogRegistry -Path (Join-Path $temp 'catalog-registry.json')
        $catalogArgs = @($checkScript, '--check', 'tests', '--repo', $catalogRoot, '--registry', $catalogJson)
        $catalogRun = Invoke-Python -Root $catalogRoot -Arguments $catalogArgs
        Assert-True ($catalogRun.ExitCode -eq 0) ('catalog: registered entry points and their direct callee pass: ' + $catalogRun.Output)
        $missingCatalogJson = New-RegistryJson -RegistryText ($catalogRegistry -replace '\(Run.CallBudgetChecks.RunBudgetCommon, 4\),', '') -Path (Join-Path $temp 'catalog-missing.json')
        $missingCatalogRun = Invoke-Python -Root $catalogRoot -Arguments @($checkScript, '--check', 'tests', '--repo', $catalogRoot, '--registry', $missingCatalogJson)
        Assert-True ($missingCatalogRun.ExitCode -eq 1 -and $missingCatalogRun.Output -like '*CallBudgetChecks.RunBudgetCommon*' -and $missingCatalogRun.Output -like '*StorageCostChecks.Run*') 'catalog: a removed entry and its now-unreachable callee both fail'
        Set-Content -LiteralPath $callerPath -Value ($callerText.Replace('StorageCostChecks.Run();', '') -replace '\}\s*$', 'private static void Unregistered() { StorageCostChecks.Run(); } }')
        $unregisteredCaller = Invoke-Python -Root $catalogRoot -Arguments $catalogArgs
        Assert-True ($unregisteredCaller.ExitCode -eq 1 -and $unregisteredCaller.Output -like '*StorageCostChecks.Run*') 'catalog: a call only from an unregistered method still fails'
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
