param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
function Invoke-HelpCheck([string]$Arguments, [string]$Core, [bool]$ExpectLegacy) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $executablePath
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($name in @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'SYNCSHARK_CORE')) {
        $start.EnvironmentVariables.Remove($name)
    }
    if ($Core) { $start.EnvironmentVariables['SYNCSHARK_CORE'] = $Core }
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'CLI process did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { throw 'State-free CLI help did not finish within 30 seconds.' }
        $output = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "CLI help failed: $($process.ExitCode): $errorText" }
        $envelope = $output | ConvertFrom-Json
        if (-not $envelope.ok -or $envelope.command -ne 'help') { throw 'CLI help envelope was invalid.' }
        $hasNotice = $errorText.Contains('legacy engine selected explicitly')
        if ($hasNotice -ne $ExpectLegacy) { throw "Unexpected legacy notice for arguments '$Arguments' and core '$Core'." }
    }
    finally { $process.Dispose() }
}
# Real application entry point, but help never opens state or starts a sync.
Invoke-HelpCheck '--cli --core v1 help --json' '' $true
Invoke-HelpCheck '--cli help --json' '1' $true
Invoke-HelpCheck '--cli help --json' '' $false
Write-Output 'PASS real CliEntry explicit-v1, environment-v1 and default-v2 legacy notice checks'
