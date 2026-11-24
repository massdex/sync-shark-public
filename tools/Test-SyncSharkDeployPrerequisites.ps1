[CmdletBinding()]
param([Parameter(Mandatory)][string]$TestProductRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:SYNC_SHARK_RELEASE_TEST_MODE -cne '1') { throw 'Test mode is required.' }
if ((Get-Location).Drive.Name -ne 'F' -or [IO.Path]::GetPathRoot([IO.Path]::GetTempPath()) -ine 'F:\') {
    throw 'Run fixtures with cwd and TEMP on F:.'
}
Remove-Item Env:HTTPS_PROXY, Env:HTTP_PROXY -ErrorAction SilentlyContinue
$module = Join-Path $PSScriptRoot 'SyncShark.Release.psm1'
Import-Module $module -Force
$root = Resolve-SyncSharkProductRoot $TestProductRoot
if (Test-Path -LiteralPath $root) { throw 'Use a new fixture root.' }
[void][IO.Directory]::CreateDirectory($root)
$target = Join-Path $root 'Data'
$pointer = Join-Path $root 'product-root.txt'
[IO.File]::WriteAllText($pointer, $target)
function Check([string]$Name, [scriptblock]$Body) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $Body
    '{0} PASS {1}ms' -f $Name, $watch.ElapsedMilliseconds
}
function Refused([scriptblock]$Body) {
    $refused = $false
    try { & $Body | Out-Null } catch { $refused = $true }
    if (-not $refused) { throw 'Expected refusal.' }
}
Check 'ordinary admission rejects missing leaf' { Refused { Resolve-SyncSharkDataRoot $root } }
Check 'recovery resolver accepts only missing leaf' {
    if ((Resolve-SyncSharkDataRoot $root -AllowMissingLeaf) -ine $target) { throw 'Wrong target.' }
}
Check 'missing ancestor refused' {
    [IO.File]::WriteAllText($pointer, (Join-Path $root 'missing\Data'))
    Refused { Resolve-SyncSharkDataRoot $root -AllowMissingLeaf }
}
Check 'reparse ancestor refused' {
    $ordinary = [IO.Directory]::CreateDirectory((Join-Path $root 'ordinary'))
    $junction = Join-Path $root 'junction'
    New-Item -ItemType Junction -Path $junction -Target $ordinary.FullName | Out-Null
    [IO.File]::WriteAllText($pointer, (Join-Path $junction 'Data'))
    Refused { Resolve-SyncSharkDataRoot $root -AllowMissingLeaf }
}
[IO.File]::WriteAllText($pointer, $target)
[void][IO.Directory]::CreateDirectory((Join-Path $target 'state'))
[IO.File]::WriteAllText((Join-Path $target 'state\instance.lock'), '')
Check 'present target ordinary admission unchanged' {
    if ((Resolve-SyncSharkDataRoot $root) -ine $target) { throw 'Wrong target.' }
}
Check 'competing thread refused by same mutex' {
    $mutex = Enter-SyncSharkMaintenanceMutex $root
    $worker = [PowerShell]::Create()
    try {
        [void]$worker.AddScript({ param($Module, $Root)
            Import-Module $Module
            try {
                $lock = Enter-SyncSharkMaintenanceMutex $Root
                $lock.ReleaseMutex(); $lock.Dispose()
                return $false
            } catch { return $true }
        }).AddArgument($module).AddArgument($root)
        $pending = $worker.BeginInvoke()
        $result = $worker.EndInvoke($pending)
        if ($result.Count -ne 1 -or $result[0] -ne $true) { throw 'Contender was not refused.' }
    } finally { $worker.Dispose(); $mutex.ReleaseMutex(); $mutex.Dispose() }
}
Check 'lease releases extracted mutex after body failure' {
    Refused { Invoke-SyncSharkWithInstanceMaintenanceLease $root { throw 'Injected failure.' } }
    Invoke-SyncSharkWithInstanceMaintenanceLease $root { }
}
Write-Host "Fixtures retained under $root; no production process or registry operation performed."
