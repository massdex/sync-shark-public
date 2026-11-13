. "$PSScriptRoot/MandatoryChecks.ps1"

function Get-RegisteredGroups([string]$Path, [hashtable]$Groups) {
    $isTest = $Path -match '^tests/SyncShark\.Engine\.Tests/(?<rel>.+)\.cs$'
    if ($isTest) { $classKey = $Matches['rel'].Replace('/', '.') }
    elseif ($Path -match '^src/SyncShark\.Engine/(?<rel>.+)\.cs$') { $classKey = $Matches['rel'].Replace('/', '.') + 'Checks' }
    else { return }
    $found = $false
    foreach ($group in $Groups.Keys) {
        if ($Groups[$group] -contains $classKey) { $group; $found = $true }
    }
    if (-not $found -and $isTest) {
        $baseName = [IO.Path]::GetFileNameWithoutExtension($Path).Split('.')[0]
        foreach ($group in $Groups.Keys) {
            foreach ($class in $Groups[$group]) {
                if ($class.Split('.')[-1] -eq $baseName) { $group; break }
            }
        }
    }
}

function Convert-GlobToRegex([string]$Glob) {
    $builder = [System.Text.StringBuilder]::new('^')
    $i = 0
    while ($i -lt $Glob.Length) {
        $char = $Glob[$i]
        if ($char -eq '*') {
            if ($i + 1 -lt $Glob.Length -and $Glob[$i + 1] -eq '*') { [void]$builder.Append('.*'); $i += 2; continue }
            [void]$builder.Append('[^/]*'); $i++; continue
        }
        if ($char -eq '?') { [void]$builder.Append('[^/]'); $i++; continue }
        if ($char -eq '{') {
            $end = $Glob.IndexOf('}', $i)
            if ($end -lt 0) { throw "Unclosed glob alternatives: $Glob" }
            $alternatives = $Glob.Substring($i + 1, $end - $i - 1).Split(',') | ForEach-Object { [regex]::Escape($_) }
            [void]$builder.Append('(?:' + ($alternatives -join '|') + ')'); $i = $end + 1; continue
        }
        [void]$builder.Append([regex]::Escape([string]$char)); $i++
    }
    [void]$builder.Append('$')
    $builder.ToString()
}

function Get-OwnershipRows([string]$Text) {
    if (-not $Text) { return }
    $table = [regex]::Match($Text, '(?ms)^## Table\r?\n(?<rows>.*?)(?=^## |\z)')
    $lines = @($table.Groups['rows'].Value -split '\r?\n' | Where-Object { $_.StartsWith('|') })
    if ($lines.Count -lt 2 -or $lines[0] -notmatch '^\|\s*Path or glob\s*\|\s*Owner\s*\|\s*Files\s*\|\s*CI groups\s*\|$') {
        throw 'OWNERSHIP table header is missing.'
    }
    foreach ($line in ($lines | Select-Object -Skip 2)) {
        $cells = $line.Split('|')
        if ($cells.Count -ne 6) { throw "Malformed OWNERSHIP row: $line" }
        $glob = $cells[1].Trim().Trim('`')
        [pscustomobject]@{ Path = $glob; Regex = Convert-GlobToRegex $glob; Groups = $cells[4].Trim().Trim('`') }
    }
}

function ConvertFrom-NameStatus([string]$Text) {
    foreach ($line in $Text -split "`n") {
        if (-not $line.Trim()) { continue }
        $parts = $line.TrimEnd("`r") -split "`t"
        [pscustomobject]@{ Status = $parts[0]; Path = ($parts[1..($parts.Count - 1)] -join "`t") }
    }
}

function Get-GroupsForChangedFiles {
    param([object[]]$ChangedFiles, [hashtable]$Groups, [object[]]$OwnershipRows, [string[]]$HostedGroups)
    $selected = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($group in (Get-MandatoryEngineGroups)) { [void]$selected.Add($group) }
    $unmapped = [Collections.Generic.List[string]]::new()
    $shared = [Collections.Generic.List[string]]::new()
    $outside = [Collections.Generic.List[string]]::new()
    $uncertain = [Collections.Generic.List[string]]::new()
    foreach ($file in $ChangedFiles) {
        if ($file.Path -in @('.ci-lane.json', 'ci-quick-suites.txt')) { continue }
        if ($file.Status -ne 'D') {
            $exact = @(Get-RegisteredGroups -Path $file.Path -Groups $Groups)
            foreach ($group in $exact) { [void]$selected.Add($group) }
            if ($exact.Count -eq 0 -and $file.Path -match '^(?:tests/SyncShark\.Engine\.Tests|src/SyncShark\.Engine)/.+\.cs$') { $unmapped.Add($file.Path) }
            elseif ($exact.Count -eq 0 -and $file.Path -match '^(?:src|apps)/') { $outside.Add($file.Path) }
        }
        if ($file.Path -match '^tests/SyncShark\.Engine\.Tests/' -and (Test-IsSharedTestHelper $file.Path)) {
            $shared.Add($file.Path)
            foreach ($group in $HostedGroups) { [void]$selected.Add($group) }
        }
        $rows = @($OwnershipRows | Where-Object { $file.Path -cmatch $_.Regex })
        if ($rows.Count -gt 1) { throw "$($file.Path) matches several OWNERSHIP rows" }
        $reason = if ($rows.Count -eq 0) { 'no OWNERSHIP row' } else { '' }
        $tokens = @(if ($rows.Count) { $rows[0].Groups -split '\s+' | Where-Object { $_ } })
        if ($rows.Count -and $tokens.Count -eq 0) { $reason = 'blank CI groups cell' }
        if ($tokens.Count -and $rows[0].Groups -cnotin @('all', 'none')) {
            if (@($tokens | Where-Object { -not $Groups.ContainsKey($_) }).Count) { $reason = 'unknown CI group token' }
        }
        if ($reason) { $uncertain.Add("UNCERTAIN $($file.Path): $reason") }
        if ($reason -or ($rows.Count -and $rows[0].Groups -ceq 'all')) { $tokens = $HostedGroups }
        elseif ($rows[0].Groups -ceq 'none') { $tokens = @() }
        foreach ($group in $tokens) { [void]$selected.Add($group) }
    }
    $dropped = @($selected | Where-Object { $_ -cne 'budget' -and $HostedGroups -cnotcontains $_ })
    foreach ($group in $dropped) { [void]$selected.Remove($group) }
    [pscustomobject]@{ Groups = @($selected); Unmapped = @($unmapped); SharedHelpers = @($shared); OutsideRegistry = @($outside); Uncertain = @($uncertain); DroppedLive = $dropped }
}

function Test-SelectionCovers([object]$Selection, [object]$Required, [string[]]$Groups) {
    $missing = @(foreach ($key in @('engine', 'core', 'ui')) {
        $have = @(if ($Selection -is [Collections.IDictionary]) { $Selection[$key] }
            elseif ($null -ne $Selection -and $Selection.PSObject.Properties[$key]) { $Selection.$key })
        $need = @(if ($Required -is [Collections.IDictionary]) { $Required[$key] }
            elseif ($null -ne $Required -and $Required.PSObject.Properties[$key]) { $Required.$key })
        if ($key -eq 'engine') {
            $have = @(Expand-EngineGroups -Selected @($have + (Get-MandatoryEngineGroups)) -Groups $Groups)
            $need = @(Expand-EngineGroups -Selected $need -Groups $Groups)
        }
        foreach ($item in $need) { if ($item -and $have -cnotcontains $item) { "${key}:$item" } }
    })
    [pscustomobject]@{ Covers = ($null -ne $Selection -and $missing.Count -eq 0); Missing = $missing }
}

function Get-ProjectInputs([string[]]$Paths, [scriptblock]$Read) {
    $entry = 'tests/SyncShark.Engine.Tests/SyncShark.Engine.Tests.csproj'
    foreach ($path in @('Directory.Build.props', 'global.json')) {
        [pscustomobject]@{ Path = $path; Kind = 'implicit'; Project = $entry }
    }
    $pending = [Collections.Generic.Queue[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pending.Enqueue($entry)
    while ($pending.Count) {
        $project = $pending.Dequeue()
        if (-not $seen.Add($project)) { continue }
        $folder = $project.Substring(0, $project.LastIndexOf('/') + 1)
        [pscustomobject]@{ Path = $project; Kind = 'project'; Project = $project }
        [pscustomobject]@{ Path = $folder + 'packages.lock.json'; Kind = 'lock'; Project = $project }
        [xml]$xml = & $Read $project
        foreach ($item in $xml.SelectNodes('//ProjectReference[@Include] | //Compile[@Include] | //EmbeddedResource[@Include]')) {
            $pattern = [Uri]::UnescapeDataString([Uri]::new([Uri]("https://tree.invalid/$folder"), $item.Include.Replace('\', '/')).AbsolutePath.TrimStart('/'))
            if ($item.Name -eq 'ProjectReference') { $pending.Enqueue($pattern); continue }
            if ($item.Name -eq 'Compile' -and $pattern.StartsWith($folder, [StringComparison]::Ordinal)) { continue }
            $kind = if ($item.Name -eq 'Compile') { 'linked-compile' } else { 'resource' }
            $matches = if ($pattern.Contains('*')) { @($Paths | Where-Object { $_ -cmatch (Convert-GlobToRegex $pattern) }) } else { @($pattern) }
            foreach ($path in $matches) { [pscustomobject]@{ Path = $path; Kind = $kind; Project = $project } }
        }
    }
}

function Get-RunKind([string]$Ref) {
    if ($Ref -like 'refs/heads/quick/*') { return 'quick' }
    if ($Ref -eq 'refs/heads/main' -or $Ref -like 'refs/heads/claude-code/v2-*' -or
        $Ref -like 'refs/heads/ci/int-*' -or $Ref -like 'refs/heads/ci/rc-*') { return 'full' }
    return 'lane'
}

function Get-GroupList([string]$Path) {
    Get-Content -LiteralPath $Path |
        Where-Object { $_.Trim() -and -not $_.TrimStart().StartsWith('#') } |
        ForEach-Object { $_.Trim() }
}

function Expand-EngineGroups([string[]]$Selected, [string[]]$Groups) {
    $expanded = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($group in $Selected) {
        $shards = @($Groups | Where-Object { $_ -cmatch ('^' + [regex]::Escape($group) + '-[0-9]+$') })
        if ($group -ceq 'budget' -and $shards.Count -eq 0) { throw 'No numeric budget shards found in CheckRegistry.' }
        if ($shards.Count -eq 0) { [void]$expanded.Add($group) }
        else { foreach ($shard in $shards) { [void]$expanded.Add($shard) } }
    }
    @($expanded | Sort-Object -CaseSensitive { if ($_ -cmatch '-[0-9]+$') { 0 } else { 1 } }, { $_ })
}

function Get-RunnerLabels([string]$RunKind, [string]$EventName, [string]$LaneRunner, [string[]]$Items, [string[]]$HostedOnly) {
    $labels = @('windows-latest')
    if ($LaneRunner) {
        $requested = ConvertFrom-Json -InputObject $LaneRunner -NoEnumerate
        if ($requested -isnot [array] -or $requested.Count -lt 2 -or $requested -cnotcontains 'self-hosted') {
            throw 'SYNC_SHARK_LANE_RUNNER must be a JSON array of at least two labels including self-hosted.'
        }
        foreach ($label in $requested) {
            if ($label -isnot [string] -or $label -cnotmatch '^[A-Za-z0-9._-]+$') {
                throw 'SYNC_SHARK_LANE_RUNNER contains an invalid label.'
            }
        }
        if ($RunKind -in @('lane', 'quick') -and $EventName -in @('push', 'workflow_dispatch') -and
            @($Items | Where-Object { $HostedOnly -ccontains $_ }).Count -eq 0) { $labels = $requested }
    }
    ConvertTo-Json -InputObject @($labels) -Compress
}

function Get-RegistryEntries {
    param([string]$Text)
    # One owner of the CheckRegistry text format. Blank // and /* */ comments but
    # keep "..." string literals, so a URL or "/*" inside a string is not a comment.
    # shortcut: inline groups and the validated BudgetCatalog shape; upgrade for another registry expression.
    $clean = [regex]::Replace($Text, '"(?:[^"\\\n]|\\.)*"|//[^\n]*|/\*[\s\S]*?\*/', {
        param($match)
        if ($match.Value[0] -eq '"') { $match.Value } else { ' ' }
    })
    $entries = [System.Collections.Generic.List[object]]::new()
    $catalog = [regex]::Match($clean, '\bBudgetCatalog\s*=\s*\[(?<entries>[\s\S]*?)\];')
    $budget = [regex]::Matches($catalog.Groups['entries'].Value, '\(\s*(?<token>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+)\s*,\s*(?<shard>\d+)\s*\)')
    $starts = [regex]::Matches($clean, '(?m)^\s*\["(?<group>[^"]+)"\]\s*=')
    for ($i = 0; $i -lt $starts.Count; $i++) {
        $start = $starts[$i].Index
        $end = if ($i + 1 -lt $starts.Count) { $starts[$i + 1].Index } else { $clean.Length }
        $chunk = $clean.Substring($start, $end - $start)
        $group = $starts[$i].Groups['group'].Value
        $catalogUse = [regex]::Match($chunk, '^\s*\["[^"]+"\]\s*=\s*(?:BudgetChecks\s*,|BudgetShard\(\s*(?<shard>\d+)\s*\)\s*,)')
        if ($catalogUse.Success) {
            foreach ($entry in $budget) {
                if (-not $catalogUse.Groups['shard'].Success -or $entry.Groups['shard'].Value -eq $catalogUse.Groups['shard'].Value) {
                    $entries.Add([pscustomobject]@{ Group = $group; Token = $entry.Groups['token'].Value })
                }
            }
        }
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
