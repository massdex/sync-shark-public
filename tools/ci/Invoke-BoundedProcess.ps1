[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string[]]$Arguments,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][int]$TimeoutSeconds
)
$ErrorActionPreference = 'Stop'
$clock = [Diagnostics.Stopwatch]::StartNew()
# Start-Process joins arguments; preserve whitespace-bearing paths at that boundary.
$quoted = @($Arguments | ForEach-Object {
    if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
})
$process = Start-Process -FilePath $Executable -ArgumentList $quoted -NoNewWindow -PassThru `
    -RedirectStandardOutput $OutputPath -RedirectStandardError "$OutputPath.stderr.log"
$timedOut = $false
try {
    $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) {
        try { $process.Kill($true) } catch { try { $process.Kill() } catch { } }
        try { $process.WaitForExit(10000) | Out-Null } catch { }
    }
    $clock.Stop()
    @{ elapsedSeconds = $clock.Elapsed.TotalSeconds; timeoutSeconds = $TimeoutSeconds; timedOut = $timedOut } |
        ConvertTo-Json | Set-Content "$OutputPath.execution.json"
    if ($timedOut) {
        throw [TimeoutException]::new("$Executable timed out after $($clock.Elapsed.TotalSeconds) seconds; output retained at $OutputPath")
    }
    exit $process.ExitCode
} finally { $process.Dispose() }
