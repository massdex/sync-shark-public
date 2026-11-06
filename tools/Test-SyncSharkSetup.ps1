$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path $PSScriptRoot) 'Installer/setup.ps1') -FunctionsOnly
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

foreach ($state in @('StatusDisabled','StatusUnavailable')) {
    if (Test-SetupHealthState $state) { throw "Unhealthy state accepted: $state" }
}
foreach ($state in @('StatusPaused','StatusNeedsAttention','StatusWatching')) {
    if (-not (Test-SetupHealthState $state)) { throw "Allowed state rejected: $state" }
}
$exe = Join-Path $root 'SyncShark.exe'
[IO.File]::WriteAllText($exe, 'fixture')
if ((Get-SetupStartupPrevious "`"$exe`" --core v2 --engine-supervisor") -cne $exe) { throw 'Stopped startup predecessor lost.' }
try { Assert-SetupCapacity 1024 1024; throw 'Capacity accepted without headroom.' }
catch { if ($_.Exception.Message -notlike 'Insufficient backup space:*') { throw } }
Assert-SetupCapacity 1024 (1024 + 256MB)
Remove-Item -LiteralPath $root -Recurse -Force
