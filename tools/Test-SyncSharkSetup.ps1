$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
. (Join-Path $repository 'Installer/setup.ps1') -FunctionsOnly
$shell = Get-Content -LiteralPath (Join-Path $repository 'Installer/SyncSharkSetup.cs') -Raw
if ($shell -notmatch '-ExecutionPolicy Bypass -File') { throw 'Setup does not pass the process-scoped execution-policy bypass.' }
$setup = Get-Content -LiteralPath (Join-Path $repository 'Installer/setup.ps1') -Raw
if ($setup -match 'engine/state\.db') { throw 'Setup still decides store presence from the engine database file.' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('setup-fixture-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
foreach ($failure in @('stop','backup','copy','switch','start','health','none')) {
    $events = [Collections.Generic.List[string]]::new()
    $launcher = Join-Path $root $failure
    [IO.File]::WriteAllText($launcher, 'old')
    function Step($name) { $events.Add($name); if ($name -eq $failure) { throw "fixture $name" } }
    try {
        Invoke-SetupTransition -Stop { Step 'stop' } -Backup { Step 'backup' } -Copy { Step 'copy' } -Switch {
            Step 'switch'; [IO.File]::WriteAllText($launcher, 'new')
        } -Start { Step 'start' } -Health { Step 'health' } -RestartOld {
            $events.Add('old'); [IO.File]::WriteAllText($launcher, 'old')
        } -StopNew { $events.Add('stop-new') }
        if ($failure -ne 'none') { throw 'Expected injected failure.' }
    } catch {
        if ($_.Exception.Message -notlike 'fixture *') { throw }
    }
    $forward = $failure -in @('start','health','none')
    $expected = if ($forward) { 'new' } else { 'old' }
    if ([IO.File]::ReadAllText($launcher) -cne $expected) { throw "Incorrect launcher at $failure" }
    if ($failure -in @('start','health') -and (-not $events.Contains('stop-new') -or $events.Contains('old'))) { throw 'Unsafe post-start rollback.' }
    if ($failure -eq 'none' -and ($events -join ',') -cne 'stop,backup,copy,switch,start,health') { throw 'Incorrect success order.' }
}
Write-Host "Setup transition fixtures passed. Fixture files retained: $root"

foreach ($state in @('StatusPaused','StatusNeedsAttention','StatusDisabled','StatusUnavailable','StatusSomethingElse')) {
    if (Test-SetupHealthState $state) { throw "Unhealthy state accepted: $state" }
}
foreach ($state in @('StatusStillWorking','StatusStillWaiting','StatusWatching')) {
    if (-not (Test-SetupHealthState $state)) { throw "Allowed state rejected: $state" }
}
function New-SetupProbe([string]$Store) { { param($path) [pscustomobject]@{ Root = $path; Store = $Store } }.GetNewClosure() }
# B2/R6: with nothing configured, an existing valid store at the default location is adopted, not initialised.
$adopted = Resolve-SetupData $null '' 'C:\SyncShark\Data' (New-SetupProbe 'valid')
if ($adopted.Initialize -or $adopted.Root -cne 'C:\SyncShark\Data') { throw 'A valid default store was not adopted.' }
$first = Resolve-SetupData $null '' 'C:\SyncShark\Data' (New-SetupProbe 'absent')
if (-not $first.Initialize -or $first.Root -cne 'C:\SyncShark\Data') { throw 'A first install was not initialised at the default.' }
$registered = Resolve-SetupData 'D:\Data' '' 'C:\SyncShark\Data' (New-SetupProbe 'valid')
if ($registered.Initialize -or $registered.Root -cne 'D:\Data') { throw 'A valid registered store was not used.' }
foreach ($case in @(
    @($null, 'C:\SyncShark\Data', 'invalid'),
    @('D:\Data', 'C:\SyncShark\Data', 'absent'),
    @('D:\Data', 'C:\SyncShark\Data', 'invalid')
)) {
    try { Resolve-SetupData $case[0] '' 'C:\SyncShark\Data' (New-SetupProbe $case[2]); throw "Unsafe data decision accepted: $case" }
    catch { if ($_.Exception.Message -like 'Unsafe data decision accepted*') { throw } }
}
try { Resolve-SetupData $null 'D:\Legacy' 'C:\SyncShark\Data' (New-SetupProbe 'invalid'); throw 'Unsafe pointed data decision accepted.' }
catch { if ($_.Exception.Message -like 'Unsafe pointed data decision*') { throw } }
try { Resolve-SetupData $null 'relative' 'C:\SyncShark\Data' (New-SetupProbe 'valid'); throw 'A relative data pointer was accepted.' }
catch { if ($_.Exception.Message -like 'A relative data pointer*') { throw } }
$pointer = Resolve-SetupData $null 'D:\Legacy' 'C:\SyncShark\Data' (New-SetupProbe 'valid')
if ($pointer.Initialize -or $pointer.Root -cne 'D:\Legacy') { throw 'A valid pointed store was not adopted.' }
$absentPointer = Resolve-SetupData $null 'D:\Legacy' 'C:\SyncShark\Data' (New-SetupProbe 'absent')
if (-not $absentPointer.Initialize -or $absentPointer.Root -cne 'C:\SyncShark\Data') { throw 'An absent pointed store did not fall back to the default.' }
$fake = Join-Path $root 'product.cmd'
[IO.File]::WriteAllText($fake, "@echo off`r`necho C:\Default`r`necho store: valid`r`n")
$reply = Get-SetupDataState $fake 'C:\Requested'
if ($reply.Root -cne 'C:\Default' -or $reply.Store -cne 'valid') { throw 'The product store-state reply was not parsed.' }
[IO.File]::WriteAllText($fake, "@echo off`r`nexit /b 1`r`n")
try { Get-SetupDataState $fake 'C:\Requested'; throw 'A failed product validation was accepted.' }
catch { if ($_.Exception.Message -notlike 'Product setup validation failed*') { throw } }
$exe = Join-Path $root 'SyncShark.exe'
[IO.File]::WriteAllText($exe, 'fixture')
if ((Get-SetupStartupPrevious "`"$exe`" --core v2 --engine-supervisor") -cne $exe) { throw 'Stopped startup predecessor lost.' }
try { Assert-SetupCapacity 1024 1024; throw 'Capacity accepted without headroom.' }
catch { if ($_.Exception.Message -notlike 'Insufficient backup space:*') { throw } }
Assert-SetupCapacity 1024 (1024 + 256MB)
Remove-Item -LiteralPath $root -Recurse -Force
# Stock PowerShell returns the last native command's exit code as the script's, so end on a successful one.
& cmd /c exit 0
