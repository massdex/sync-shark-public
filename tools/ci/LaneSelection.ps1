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
