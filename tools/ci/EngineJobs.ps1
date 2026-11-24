function Get-EngineJobs([string[]]$Groups, [string]$SecondsPath, [int]$TargetSeconds) {
    $seconds = @{}
    foreach ($line in (Get-Content -LiteralPath $SecondsPath)) {
        if (-not $line.Trim() -or $line.TrimStart().StartsWith('#')) { continue }
        if ($line -cnotmatch '^([a-z0-9-]+)\s+([0-9]+)$') { throw "Invalid engine seconds row: $line" }
        $seconds[$Matches[1]] = [int]$Matches[2]
    }
    if ($seconds.Count -eq 0) { throw 'Engine seconds file is empty.' }
    if ($TargetSeconds -le 0) { throw 'Engine job target must be positive.' }
    $weights = @($seconds.Values | Sort-Object)
    $median = ($weights[[int][Math]::Floor(($weights.Count - 1) / 2)] + $weights[[int][Math]::Floor($weights.Count / 2)]) / 2
    $items = @($Groups | Select-Object -Unique | ForEach-Object {
        [pscustomobject]@{ Group = $_; Weight = $(if ($seconds.ContainsKey($_)) { $seconds[$_] } else { $median }) }
    } | Sort-Object -CaseSensitive @{ Expression = 'Weight'; Descending = $true }, Group)
    $count = [Math]::Max(1, [int][Math]::Ceiling(($items | Measure-Object Weight -Sum).Sum / $TargetSeconds))
    $jobs = @(for ($i = 0; $i -lt $count; $i++) {
        [pscustomobject]@{ Groups = [Collections.Generic.List[string]]::new(); Weight = 0; Index = $i }
    })
    foreach ($item in $items) {
        $job = $jobs | Sort-Object Weight, Index | Select-Object -First 1
        $job.Groups.Add($item.Group)
        $job.Weight += $item.Weight
    }
    $jobs
}
