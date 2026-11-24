# Bug #135: the release journal publishes by same-directory move, and a brief antivirus or indexer hold of
# the destination must not fail it. Runs on any Windows pwsh with no release inputs; the held destinations
# are real Windows handles.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
public static class JournalHold {
    private static Timer timer;
    public static FileStream Open(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
    // Releases the hold once, from a timer thread, so the write observes the retry without a second runspace.
    public static void ReleaseAfter(FileStream stream, int milliseconds) =>
        timer = new Timer(_ => { try { stream.Dispose(); } catch (Exception) { } }, null, milliseconds,
            Timeout.Infinite);
}
'@

$script:failures = 0
function Assert-True([bool]$Condition, [string]$Label) {
    if ($Condition) { Write-Host "PASS $Label" }
    else { $script:failures++; Write-Host "FAIL $Label" }
}

# One source of truth: the module's carried policy and the engine's settings defaults agree.
$defaultsPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\SyncShark.Engine\engine-settings.json'
if (-not (Test-Path -LiteralPath $defaultsPath -PathType Leaf)) { throw "Missing defaults file: $defaultsPath" }
$defaults = Get-Content -Raw -LiteralPath $defaultsPath | ConvertFrom-Json
$policy = Get-SyncSharkAtomicReplacePolicy
Assert-True ($policy.Attempts -eq [int]$defaults.AtomicReplaceAttempts -and
    $policy.InitialDelayMilliseconds -eq [int]$defaults.AtomicReplaceInitialDelayMilliseconds) `
    'the module retry policy matches engine-settings.json'

$root = Join-Path 'C:\Temp' ('ss135-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$journal = [pscustomobject]@{ status = 'active'; completed = @(); pending = $null }
try {
    # A plain single move is what the writer used before the fix; it fails while the destination is held.
    $plain = Join-Path $root 'plain.json'
    $probe = Join-Path $root 'plain.json.probe'
    [IO.File]::WriteAllText($plain, '{"status":"original"}')
    [IO.File]::WriteAllText($probe, '{"status":"active"}')
    $held = [JournalHold]::Open($plain)
    try {
        $refused = $false
        try { [IO.File]::Move($probe, $plain, $true) }
        catch {
            $failure = $_.Exception
            while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
            # The two failures a brief external hold produces: access denied, or a sharing violation.
            $refused = $failure -is [UnauthorizedAccessException] -or
                ($failure -is [IO.IOException] -and ($failure.HResult -eq -2147024864 -or $failure.HResult -eq -2147024891))
        }
        Assert-True $refused 'a plain single move fails while the journal is held'
    } finally { $held.Dispose() }
    Assert-True ([IO.File]::ReadAllText($plain) -ceq '{"status":"original"}') `
        'the refused move keeps the original journal'

    # The product's short exponential backoff publishes once the hold clears inside the first wait.
    $heldPath = Join-Path $root 'held.json'
    [IO.File]::WriteAllText($heldPath, '{"status":"original"}')
    $held = [JournalHold]::Open($heldPath)
    [JournalHold]::ReleaseAfter($held, 20)
    Write-SyncSharkJournal $heldPath $journal
    $written = Get-Content -Raw -LiteralPath $heldPath | ConvertFrom-Json
    Assert-True ($written.status -ceq 'active') 'a journal write survives a hold released inside the policy'
    Assert-True (-not (Test-Path -LiteralPath ($heldPath + '.new'))) 'a published write leaves no temporary behind'

    # A hold that never clears is bounded, and the original failure still reaches the caller.
    $deniedPath = Join-Path $root 'denied.json'
    [IO.File]::WriteAllText($deniedPath, '{"status":"original"}')
    $held = [JournalHold]::Open($deniedPath)
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $refused = $false
        try { Write-SyncSharkJournal $deniedPath $journal } catch { $refused = $true }
        $watch.Stop()
        Assert-True $refused 'a permanent hold is bounded and the failure is rethrown'
        Assert-True ($watch.ElapsedMilliseconds -lt 10000) 'the bounded wait is short'
    } finally { $held.Dispose() }
    Assert-True ([IO.File]::ReadAllText($deniedPath) -ceq '{"status":"original"}') `
        'a refused publication keeps the original journal'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
if ($script:failures -gt 0) { throw "$($script:failures) journal retry check(s) failed." }
Write-Host 'All journal retry checks passed.'
