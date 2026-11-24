# Resolve citations against the tree being reviewed.
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
