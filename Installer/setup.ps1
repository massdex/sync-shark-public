[CmdletBinding()]
param([string]$Payload, [switch]$FunctionsOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The transition is shared with fixture checks; after start, recovery is roll-forward only.
function Invoke-SetupTransition($Stop, $Backup, $Copy, $Switch, $Start, $Health, $RestartOld, $StopNew) {
    $starting = $false
    try {
        & $Stop
        & $Backup
        & $Copy
        & $Switch
        $starting = $true
        & $Start
        & $Health
    } catch {
        $failure = $_
        if ($starting) { & $StopNew } else { & $RestartOld }
        throw $failure
    }
}
function Invoke-SetupCli([string]$Exe, [string[]]$Arguments) {
    $output = & $Exe --cli @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Sync Shark command failed ($LASTEXITCODE): $($Arguments -join ' ')" }
    if ($Arguments -contains '--json') { return ($output -join "`n" | ConvertFrom-Json) }
}
function Get-SetupProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name='SyncShark.exe'")
}
function Stop-SetupHost([string]$Exe) {
    $processes = @(Get-SetupProcesses)
    if (-not $processes.Count) { return }
    foreach ($item in $processes) {
        if ($item.ExecutablePath -ine $Exe -or $item.CommandLine -notmatch ' --core v2 --engine-(host|supervisor)$') {
            throw 'Another Sync Shark command or generation is running. Close it and retry; nothing was killed.'
        }
    }
    $handles = @($processes | ForEach-Object {
        try { [Diagnostics.Process]::GetProcessById($_.ProcessId) } catch [ArgumentException] { }
    })
    try {
        Invoke-SetupCli $Exe @('engine','host','stop','--yes') | Out-Null
        foreach ($process in $handles) { $process.WaitForExit() }
    } finally { foreach ($process in $handles) { $process.Dispose() } }
    if (@(Get-SetupProcesses).Count) { throw 'Graceful stop was not confirmed. Nothing was replaced; retry after the host and supervisor exit.' }
}
function Get-SetupPairs([string]$Exe) {
    $reply = Invoke-SetupCli $Exe @('engine','pairs','list','--json')
    if (-not $reply.ok) { throw 'Cannot snapshot the existing pairs.' }
    (@($reply.data.pairs) | ForEach-Object { "$($_.id)|$($_.enabled)" } | Sort-Object -CaseSensitive) -join ';'
}
function Test-SetupHealthState([string]$State) {
    # The only states that confirm a running host; held, paused, off and unknown states are not healthy.
    $State -in @('StatusStillWorking','StatusStillWaiting','StatusWatching')
}
function Get-SetupStartupPrevious([string]$Startup) {
    $registered = [regex]::Match($Startup, '^"([^"]+)" --core v2 --engine-supervisor$')
    if (-not $registered.Success -or [IO.Path]::GetFileName($registered.Groups[1].Value) -ine 'SyncShark.exe' -or
        -not (Test-Path -LiteralPath $registered.Groups[1].Value -PathType Leaf)) { throw 'The startup executable is unavailable or invalid.' }
    $registered.Groups[1].Value
}
function Assert-SetupCapacity([long]$Bytes, [long]$Free) {
    # Approved installation headroom: ten percent, with a 256 MiB floor.
    $margin = [long][Math]::Ceiling([Math]::Max($Bytes * 0.1, 256MB))
    if ($Free -lt ($Bytes + $margin)) { throw "Insufficient backup space: need $($Bytes + $margin) bytes, available $Free. Free space and retry." }
}
function Test-SyncSharkHealth([string]$Exe, [string]$Version, $Pairs, [datetime]$Since, [string]$DataRoot) {
    $status = Invoke-SetupCli $Exe @('engine','host','status','--json')
    if (-not $status.ok -or $status.error -or -not (Test-SetupHealthState $status.data.state)) { throw 'The new host is not healthy.' }
    $hosts = @(Get-SetupProcesses | Where-Object { $_.ExecutablePath -ieq $Exe -and $_.CommandLine -match ' --core v2 --engine-host$' })
    if ($hosts.Count -ne 1 -or [Diagnostics.FileVersionInfo]::GetVersionInfo($Exe).ProductVersion -cne $Version) { throw 'The expected new host identity was not confirmed.' }
    if ($null -ne $Pairs -and (Get-SetupPairs $Exe) -cne $Pairs) { throw 'The pairs differ from the pre-upgrade snapshot.' }
    foreach ($name in @('autosync.log','autosync.log.previous')) {
        $path = Join-Path $DataRoot "v2/logs/autosync/$name"
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $reader = [IO.StreamReader]::new([IO.File]::Open($path, 'Open', 'Read', 'ReadWrite, Delete'))
        try {
            while ($null -ne ($line = $reader.ReadLine())) {
                if ($line -match '^(\S+) .*lifecycle failure' -and [datetime]::Parse($Matches[1]).ToUniversalTime() -ge $Since) { throw "New host lifecycle failure: $line" }
            }
        } finally { $reader.Dispose() }
    }
}
function Backup-SetupStore([string]$DataRoot, [bool]$Initialize, [string]$BackupRoot) {
    if (@(Get-SetupProcesses).Count) { throw 'A process appeared before backup; no store was copied.' }
    if ($Initialize) { return }
    [IO.Directory]::CreateDirectory($BackupRoot) | Out-Null
    $directory = Join-Path $DataRoot 'engine'
    $names = @('state.db','state.db-wal','state.db-shm')
    $present = @($names | Where-Object { Test-Path -LiteralPath (Join-Path $directory $_) })
    $bytes = 0L
    foreach ($name in $present) { $bytes += (Get-Item -LiteralPath (Join-Path $directory $name)).Length }
    $free = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($BackupRoot)).AvailableFreeSpace
    Assert-SetupCapacity $bytes $free
    foreach ($name in $present) {
        $source = Join-Path $directory $name
        $destination = Join-Path $BackupRoot $name
        $before = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        Copy-Item -LiteralPath $source -Destination $destination
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -cne $before -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $before) { throw "Backup verification failed: $name. Preserve the incomplete backup." }
    }
    $after = @($names | Where-Object { Test-Path -LiteralPath (Join-Path $directory $_) })
    if (@(Get-SetupProcesses).Count -or ($after -join ';') -cne ($present -join ';')) { throw 'The store changed during backup. Retry after stopping all writers.' }
    Write-Host "Verified backup retained at $BackupRoot"
}
function Get-SetupDataState([string]$Product, [string]$Path) {
    # One product-owned answer: the canonical root and whether it already holds a store.
    $reply = @(& $Product --core v2 --setup-validate-data $Path)
    if ($LASTEXITCODE -ne 0 -or $reply.Count -ne 2 -or ([string]$reply[1]).Trim() -notmatch '^store: (absent|valid|invalid)$') {
        throw 'Product setup validation failed; no startup or data override was applied.'
    }
    [pscustomobject]@{ Root = ([string]$reply[0]).Trim(); Store = $Matches[1] }
}
function Resolve-SetupData($Registered, [string]$Pointer, [string]$Default, [scriptblock]$Probe) {
    # Adopt only a valid store; create a store only at the default on a first install; never replace one.
    if ($null -ne $Registered) {
        $current = & $Probe $Registered
        if ($current.Store -ne 'valid') {
            throw "The registered store is $($current.Store). Restore the folder and retry; setup will not create or replace a store."
        }
        return [pscustomobject]@{ Root = $current.Root; Initialize = $false }
    }
    if ($Pointer) {
        if (-not [IO.Path]::IsPathRooted($Pointer)) {
            throw "The data pointer '$Pointer' is not an absolute path. Resolve it before setup; setup will not create or replace a store."
        }
        $legacy = & $Probe $Pointer
        if ($legacy.Store -eq 'invalid') {
            throw "The store named by $Pointer is unusable. Preserve it and resolve it before setup; setup will not create or replace a store."
        }
        if ($legacy.Store -eq 'valid') { return [pscustomobject]@{ Root = $legacy.Root; Initialize = $false } }
    }
    $fallback = & $Probe $Default
    if ($fallback.Store -eq 'invalid') {
        throw "The store at $($fallback.Root) is unusable. Preserve it and resolve it before setup; setup will not create or replace a store."
    }
    [pscustomobject]@{ Root = $fallback.Root; Initialize = ($fallback.Store -eq 'absent') }
}
function Write-SetupLauncher([string]$Path, [string]$Exe) {
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temporary, "@echo off`r`n`"$Exe`" --cli %*`r`n")
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary, $Path, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $Path) }
}
if ($FunctionsOnly) { return }
try {
    $root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/SyncShark'
    [IO.Directory]::CreateDirectory($root) | Out-Null
    # Exclusive setup handle, not a persistent transaction journal.
    $gate = [IO.File]::Open((Join-Path $root 'setup.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $staging = $null
    try {
        $staging = Join-Path $root ('staging-' + [guid]::NewGuid().ToString('N'))
        Expand-Archive -LiteralPath $Payload -DestinationPath $staging
        $candidate = Join-Path $staging 'SyncShark.exe'
        function Get-ProductSetupValue([string]$Mode, [string]$Value) {
            $result = & $candidate --core v2 $Mode $Value
            if ($LASTEXITCODE -ne 0) { throw 'Product setup validation failed; no startup or data override was applied.' }
            return ($result -join "`n").Trim()
        }
        $pointer = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'massdex/SyncShark/product-root.txt'
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Massdex\SyncShark')
        try { $registered = $key.GetValue('DataLocation', $null, 'DoNotExpandEnvironmentNames') } finally { $key.Dispose() }
        $pointerText = if ($null -eq $registered -and (Test-Path -LiteralPath $pointer)) { [IO.File]::ReadAllText($pointer).Trim() } else { '' }
        $decision = Resolve-SetupData $registered $pointerText 'C:\SyncShark\Data' { param($path) Get-SetupDataState $candidate $path }
        $data = $decision.Root
        $initialize = $decision.Initialize
        if ($null -eq $registered -and -not $initialize) {
            $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Massdex\SyncShark')
            try { $key.SetValue('DataLocation', $data, [Microsoft.Win32.RegistryValueKind]::String) } finally { $key.Dispose() }
        }
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($candidate).ProductVersion
        if ($version -notmatch '^\d+\.\d+\.\d+(\+[0-9a-f]{40})?$') { throw 'Invalid application version in the embedded payload.' }
        $destination = Join-Path $root "versions/$version"
        if (Test-Path -LiteralPath $destination) { throw 'This version folder already exists. Preserve it and use the installed version or a newer setup.' }
        $exe = Join-Path $destination 'SyncShark.exe'
        $launcher = Join-Path $root 'SyncShark.cmd'
        $previous = $null
        if (Test-Path -LiteralPath $launcher) {
            $match = [regex]::Match([IO.File]::ReadAllText($launcher), '(?m)^"([^"]+)" --cli %\*\r?$')
            if (-not $match.Success) { throw 'The existing launcher was edited. Preserve it and resolve its target before installing.' }
            $previous = $match.Groups[1].Value
        }
        $running = @(Get-SetupProcesses)
        if ($running.Count) {
            $paths = @($running | ForEach-Object ExecutablePath | Select-Object -Unique)
            if ($paths.Count -ne 1 -or ($previous -and $previous -ine $paths[0])) { throw 'More than one active version was found. Close extra generations first.' }
            $previous = $paths[0]
        }
        $runKey = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Run'
        $startup = Get-ItemPropertyValue -LiteralPath $runKey -Name 'Sync Shark AutoSync' -ErrorAction SilentlyContinue
        if (-not $previous -and $startup) {
            $previous = Get-SetupStartupPrevious $startup
        }
        $wasRunning = $running.Count -gt 0
        $pairs = if ($previous) { Get-SetupPairs $previous } else { $null }
        $backup = Join-Path $root ('backups/' + [guid]::NewGuid().ToString('N'))
        $since = [datetime]::UtcNow
        $oldLauncher = if (Test-Path -LiteralPath $launcher) { [IO.File]::ReadAllText($launcher) } else { $null }
        $runKey = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Run'
        $startup = Get-ItemPropertyValue -LiteralPath $runKey -Name 'Sync Shark AutoSync' -ErrorAction SilentlyContinue
        if ($startup -and $startup -cne (Get-ProductSetupValue '--setup-startup-command' $previous)) { throw 'The startup value is not the previous Engine supervisor. Preserve it and resolve it before setup.' }
        $newStartup = if ($startup) { Get-ProductSetupValue '--setup-startup-command' $exe } else { $null }
        Invoke-SetupTransition -Stop { if ($previous) { Stop-SetupHost $previous } } -Backup {
            Backup-SetupStore $data $initialize $backup
        } -Copy {
            if ($initialize) {
                & $candidate --core v2 --initialize-store $data
                if ($LASTEXITCODE -ne 0) { throw 'First-install store initialization failed; retry setup after correcting the error.' }
            }
            [IO.Directory]::CreateDirectory((Split-Path $destination)) | Out-Null
            [IO.Directory]::Move($staging, $destination)
        } -Switch {
            Write-SetupLauncher $launcher $exe
            if ($startup) { Set-ItemProperty -LiteralPath $runKey -Name 'Sync Shark AutoSync' -Value $newStartup }
        } -Start {
            # Host start uses the product's supervisor/readiness handshake and performs its own migration.
            Invoke-SetupCli $exe @('engine','host','start','--yes') | Out-Null
        } -Health {
            Test-SyncSharkHealth $exe $version $pairs $since $data
        } -RestartOld {
            if ($startup) { Set-ItemProperty -LiteralPath $runKey -Name 'Sync Shark AutoSync' -Value $startup }
            if ($null -ne $oldLauncher) { [IO.File]::WriteAllText($launcher, $oldLauncher) }
            if ($wasRunning -and -not @(Get-SetupProcesses).Count) { Invoke-SetupCli $previous @('engine','host','start','--yes') | Out-Null }
        } -StopNew {
            try { Stop-SetupHost $exe } finally {
                Write-Error -ErrorAction Continue "Upgrade needs roll-forward recovery. Launcher remains $exe. Preserve data and backup $backup; do not restore automatically or run an older version. Correct the reported error, then run the new executable with --cli engine host start --yes and --cli engine host status --json, or install a fixed newer setup."
            }
        }
        Write-Host "Installed $version. CLI launcher: $launcher. Start With Windows was not enabled by default."
        Get-ChildItem -LiteralPath (Join-Path $root 'versions') -Directory | ForEach-Object { Write-Host "Retained version (removable when no launcher/process uses it): $($_.FullName)" }
    } finally {
        if ($staging -and (Test-Path -LiteralPath $staging)) { Remove-Item -LiteralPath $staging -Recurse -Force }
        $backupParent = Join-Path $root 'backups'
        if (Test-Path -LiteralPath $backupParent) {
            Get-ChildItem -LiteralPath $backupParent -Directory | ForEach-Object { Write-Host "Retained backup (removable after recovery is no longer needed): $($_.FullName)" }
        }
        $gate.Dispose()
    }
} catch {
    Write-Error -ErrorAction Continue $_
    exit 1
}
