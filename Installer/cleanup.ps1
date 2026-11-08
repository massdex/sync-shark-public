$ErrorActionPreference = 'Stop'
try {
    try { $process = [Diagnostics.Process]::GetProcessById([int]$env:SYNCSHARK_SETUP_PID) }
    catch [ArgumentException] { $process = $null }
    if ($process) { try { $process.WaitForExit() } finally { $process.Dispose() } }
    [IO.Directory]::Delete($env:SYNCSHARK_SETUP_DIR, $true)
} catch {
    $message = $env:SYNCSHARK_SETUP_RETAINED_MESSAGE -f $env:SYNCSHARK_SETUP_DIR, $_
    [Console]::Error.WriteLine($message)
    try { [IO.File]::AppendAllText($env:SYNCSHARK_SETUP_CLEANUP_LOG, $message + [Environment]::NewLine) }
    catch { [Console]::Error.WriteLine(($env:SYNCSHARK_SETUP_LOG_MESSAGE -f $env:SYNCSHARK_SETUP_CLEANUP_LOG, $_)) }
    exit 1
}
