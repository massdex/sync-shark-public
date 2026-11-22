Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [version]'7.4') {
    throw 'Release preparation, installation and updates require PowerShell 7.4 or newer. Use system pwsh.exe or set SYNCSHARK_PWSH_PATH to the bundled pwsh.exe; Windows PowerShell 5.1 is not supported.'
}

# The signer identity is the single source of truth for release trust and accepts
# a LIST per role so an old and a replacement key verify during the switch. The
# exact canonical JSON is carried verbatim here so the vendored/embedded copies of
# this module (the in-memory module in the app, the signed deploy scripts and the
# packaged scripts) resolve the same identity for either signer role without
# depending on a sibling file. tools/New-SyncSharkSignerIdentity.ps1 regenerates
# this block and the native/managed artefacts from the canonical JSON.
# BEGIN GENERATED SyncShark signer identity (tools/SyncShark.SignerIdentity.json)
$script:SyncSharkSignerIdentityJson = @'
{
  "roles": {
    "publisher": {
      "subject": "CN=Massdex, O=Massdex",
      "thumbprints": ["CB6F453160451567EC3902F2006FF43616F799CA", "0788C3CB08917F003ECE5E0CF8FCF7288249D83E"],
      "spki": ["1706cb48355e1441d7e6b389e5606ece2d84416984afee77c29eaa0b28bcc211", "4d74ee24923f5453f76aa885cacb936005f133ea5016dd1ae733c4130d0817bb"]
    },
    "package": {
      "subject": "CN=Massdex Sync Shark Code Signing v1, O=Massdex",
      "thumbprints": ["2274E4508318BECBDBDC1CC3473CD63729117BB5", "2F4762A30CFD85AEB0F3EFC253325208140DBA03"],
      "spki": ["1706cb48355e1441d7e6b389e5606ece2d84416984afee77c29eaa0b28bcc211", "e56a9bba2a0857850f0c427d2a1923aeb62b6f4339be5d54fc51fcb9c1cee6df"]
    }
  }
}
'@
# END GENERATED SyncShark signer identity

$script:SyncSharkSignerIdentity = $null
function Get-SyncSharkSignerIdentity {
    if ($null -ne $script:SyncSharkSignerIdentity) { return $script:SyncSharkSignerIdentity }
    $script:SyncSharkSignerIdentity = ($script:SyncSharkSignerIdentityJson | ConvertFrom-Json)
    $script:SyncSharkSignerIdentity
}

function Get-SyncSharkSha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-SyncSharkNormalizedPath([string]$Path) {
    [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
}

function Assert-SyncSharkWithin([string]$Root, [string]$Path) {
    $rootPath = (Get-SyncSharkNormalizedPath $Root) + [IO.Path]::DirectorySeparatorChar
    $candidate = Get-SyncSharkNormalizedPath $Path
    if (-not $candidate.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes the authorised Sync Shark root: $Path"
    }
}

function Assert-SyncSharkOrdinaryTree([string]$Root, [switch]$AllowMissing, [switch]$RootOnly) {
    if (-not (Test-Path -LiteralPath $Root)) {
        if ($AllowMissing) { return }
        throw "Missing tree: $Root"
    }
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (-not $rootItem.PSIsContainer) { throw "Expected a directory: $Root" }
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Mutable tree root is a reparse point: $Root"
    }
    if ($RootOnly) { return }
    $reparse = Get-ChildItem -LiteralPath $Root -Force -Recurse |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
        Select-Object -First 1
    if ($reparse) { throw "Mutable tree contains a reparse point: $($reparse.FullName)" }
}

function Assert-SyncSharkTransactionTree([string]$Root, [string[]]$AllowedReparsePaths) {
    if (-not (Test-Path -LiteralPath $Root)) { return }
    Assert-SyncSharkOrdinaryTree $Root -RootOnly
    $allowed = @($AllowedReparsePaths | ForEach-Object { Get-SyncSharkNormalizedPath $_ })
    $unexpected = Get-ChildItem -LiteralPath $Root -Force -Recurse |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -and
            (Get-SyncSharkNormalizedPath $_.FullName) -notin $allowed } | Select-Object -First 1
    if ($unexpected) { throw "Transaction tree contains an unexpected reparse point: $($unexpected.FullName)" }
}

function Get-SyncSharkTreeManifest([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return @() }
    @(Get-ChildItem -LiteralPath $Root -Force -File -Recurse | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            path = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
            length = $_.Length
            sha256 = Get-SyncSharkSha256 $_.FullName
        }
    })
}

function Assert-SyncSharkTreeMatch([string]$ExpectedRoot, [string]$ActualRoot) {
    Assert-SyncSharkOrdinaryTree $ExpectedRoot
    Assert-SyncSharkOrdinaryTree $ActualRoot
    $expected = @(Get-SyncSharkTreeManifest $ExpectedRoot | ConvertTo-Json -Depth 5 -Compress)
    $actual = @(Get-SyncSharkTreeManifest $ActualRoot | ConvertTo-Json -Depth 5 -Compress)
    if (($expected -join '') -cne ($actual -join '')) { throw "Tree identity mismatch: $ActualRoot" }
}

function Get-SyncSharkTreeIdentity([string]$Root) {
    Assert-SyncSharkOrdinaryTree $Root
    $json = ConvertTo-Json -InputObject @(Get-SyncSharkTreeManifest $Root) -Depth 5 -Compress
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.UTF8Encoding]::new($false).GetBytes($json))).ToLowerInvariant()
}

function Get-SyncSharkJunctionTarget([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.LinkType -cne 'Junction') { throw "Expected a junction: $Path" }
    Get-SyncSharkNormalizedPath ([string]$item.Target)
}

function Assert-SyncSharkJunction([string]$Path, [string]$Target) {
    $actual = Get-SyncSharkJunctionTarget $Path
    if ($null -eq $actual -or $actual -cne (Get-SyncSharkNormalizedPath $Target)) {
        throw "Junction target mismatch: $Path"
    }
}

function Resolve-SyncSharkProductRoot([string]$TestProductRoot) {
    if ([string]::IsNullOrWhiteSpace($TestProductRoot)) {
        $localApplicationData = [Environment]::GetFolderPath('LocalApplicationData')
        if ([string]::IsNullOrWhiteSpace($localApplicationData) -or
            -not [IO.Path]::IsPathFullyQualified($localApplicationData)) {
            throw 'The current Windows user has no absolute LocalApplicationData folder.'
        }
        return Join-Path $localApplicationData 'Massdex\SyncShark'
    }
    if ($env:SYNC_SHARK_RELEASE_TEST_MODE -cne '1') {
        throw 'The test product-root seam is disabled.'
    }
    $testBase = Join-Path ([IO.Path]::GetTempPath()) 'sync-shark-release-tests'
    $base = (Get-SyncSharkNormalizedPath $testBase) + [IO.Path]::DirectorySeparatorChar
    $resolved = Get-SyncSharkNormalizedPath $TestProductRoot
    if (-not $resolved.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The test product root must be a child of the fixed Sync Shark release-test root.'
    }
    $resolved
}

function Resolve-SyncSharkDataRoot([string]$InstallationRoot, [switch]$AllowMissingLeaf) {
    $pointer = Join-Path $InstallationRoot 'product-root.txt'
    if (-not (Test-Path -LiteralPath $pointer)) { return $InstallationRoot }
    $item = Get-Item -LiteralPath $pointer -Force
    if ($item.PSIsContainer -or ($item.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline))) {
        throw 'The data-location pointer must be an ordinary local file.'
    }
    $lines = [IO.File]::ReadAllLines($pointer)
    if ($lines.Count -ne 1 -or $lines[0] -notmatch '^[A-Za-z]:[\\/]' -or
        -not [IO.Path]::IsPathFullyQualified($lines[0])) {
        throw 'Invalid product-root.txt: expected one absolute local data folder; no fallback was used.'
    }
    $root = Get-SyncSharkNormalizedPath $lines[0]
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($root))
    if ($drive.DriveType -notin @([IO.DriveType]::Fixed,[IO.DriveType]::Removable)) {
        throw 'The data location must be on a local drive.'
    }
    for ($directory = [IO.DirectoryInfo]::new($root); $null -ne $directory; $directory = $directory.Parent) {
        Assert-SyncSharkOrdinaryTree $directory.FullName -RootOnly -AllowMissing:($AllowMissingLeaf -and $directory.FullName -ieq $root)
    }
    $root
}

function Resolve-SyncSharkRuntimeRoot([string]$Version,[string]$TestProductRoot='') {
    if($Version -cnotmatch '^\d+\.\d+\.\d+$'){throw 'An exact runtime version is required.'}
    Join-Path (Resolve-SyncSharkProductRoot $TestProductRoot) ('versions/'+$Version)
}

function Read-SyncSharkJournal([string]$Path, [string]$Operation, [string]$Version,
    [string]$Commit, [string]$ProductRoot, [string[]]$AllowedSteps) {
    $temporary = $Path + '.new'
    if (Test-Path -LiteralPath $temporary) {
        $temporaryItem = Get-Item -LiteralPath $temporary -Force
        if ($temporaryItem.PSIsContainer -or
            ($temporaryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The interrupted journal write is not an ordinary temporary file.'
        }
        # The committed journal remains the sole durable boundary. A surviving
        # .new file is either partial or an uncommitted complete successor and
        # can always be discarded before replaying the idempotent pending step.
        Remove-Item -LiteralPath $temporary -Force
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { $journal = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 20 }
    catch { throw 'The deployment journal is truncated or corrupt.' }
    if ($journal.schema -ne 1 -or $journal.operation -cne $Operation -or
        $journal.version -cne $Version -or $journal.commit -cne $Commit -or
        (Get-SyncSharkNormalizedPath ([string]$journal.product_root)) -cne
            (Get-SyncSharkNormalizedPath $ProductRoot)) {
        throw 'The deployment journal identity or schema does not match this transaction.'
    }
    $completed = @($journal.completed)
    if (@($completed | Where-Object { $_ -notin $AllowedSteps }).Count -ne 0 -or
        ($journal.pending -and $journal.pending -notin $AllowedSteps) -or
        ($journal.status -notin @('active', 'complete'))) {
        throw 'The deployment journal contains an unknown phase or status.'
    }
    if ($completed.Count -ne @($completed | Select-Object -Unique).Count) {
        throw 'The deployment journal repeats a completed phase.'
    }
    $highest = -1
    foreach ($step in $completed) {
        $index = [Array]::IndexOf($AllowedSteps, [string]$step)
        if ($index -lt $highest) { throw 'The deployment journal phase order is contradictory.' }
        $highest = $index
    }
    if ($journal.pending) {
        $pendingIndex = [Array]::IndexOf($AllowedSteps, [string]$journal.pending)
        if ($pendingIndex -ne ($highest + 1)) { throw 'The pending journal phase contradicts completed history.' }
    }
    elseif ($journal.status -ceq 'active' -and $highest -ge 0 -and $highest -lt ($AllowedSteps.Count - 1)) {
        # A clean boundary between phases is valid; the next invocation continues at highest + 1.
    }
    if ($journal.status -ceq 'complete' -and $highest -ne ($AllowedSteps.Count - 1)) {
        throw 'A complete journal is missing one or more required phases.'
    }
    $journal
}

function Get-SyncSharkAtomicReplacePolicy {
    # The bounded atomic-replace wait (#135) mirrors src/SyncShark.Engine/engine-settings.json
    # (AtomicReplaceAttempts and AtomicReplaceInitialDelayMilliseconds). An install can run before any
    # product file exists, so the numbers are carried here like the generated signer identity above;
    # Test-SyncSharkJournalRetry.ps1 fails if they drift from that defaults file.
    [pscustomobject]@{ Attempts = 5; InitialDelayMilliseconds = 50 }
}

function Write-SyncSharkJournal([string]$Path, [object]$Journal,
    [string]$CrashAfter = '', [string]$CrashPrefix = 'journal') {
    $parent = [IO.Path]::GetDirectoryName($Path)
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent | Out-Null
    }
    $temporary = $Path + '.new'
    if (Test-Path -LiteralPath $temporary) { throw 'A prior atomic journal write was not reconciled.' }
    $json = $Journal | ConvertTo-Json -Depth 20
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try {
        $firstLength = [Math]::Max(1, [int][Math]::Floor($bytes.Length / 2))
        $stream.Write($bytes, 0, $firstLength)
        $stream.Flush($true)
        Invoke-SyncSharkCrashPoint "$CrashPrefix-mid-write" $CrashAfter
        if ($firstLength -lt $bytes.Length) {
            $stream.Write($bytes, $firstLength, $bytes.Length - $firstLength)
        }
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    Invoke-SyncSharkCrashPoint "$CrashPrefix-after-temp-write" $CrashAfter
    # A brief antivirus/indexer hold must not fail the journal: the product's short exponential backoff,
    # then the original failure is rethrown to the existing notice path (#135).
    $policy = Get-SyncSharkAtomicReplacePolicy
    $delay = $policy.InitialDelayMilliseconds
    for ($attempt = 1; ; $attempt++) {
        try { [IO.File]::Move($temporary, $Path, $true); break }
        catch {
            $failure = $_.Exception
            while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
            $transient = $failure -is [UnauthorizedAccessException] -or
                ($failure -is [IO.IOException] -and ($failure.HResult -eq -2147024864 -or $failure.HResult -eq -2147024891))
            if (-not $transient -or $attempt -ge $policy.Attempts) { throw }
            Start-Sleep -Milliseconds $delay
            $delay *= 2
        }
    }
}

function Copy-SyncSharkTreeToStaging([string]$Source, [string]$Destination,
    [string]$CrashAfter = '', [string]$CrashPoint = '') {
    Assert-SyncSharkOrdinaryTree $Source
    if (Test-Path -LiteralPath $Destination) { throw "Staging destination already exists: $Destination" }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($directory in @(Get-ChildItem -LiteralPath $Source -Force -Directory -Recurse | Sort-Object FullName)) {
        $relative = [IO.Path]::GetRelativePath($Source, $directory.FullName)
        New-Item -ItemType Directory -Path (Join-Path $Destination $relative) -Force | Out-Null
    }
    $crashReached = $false
    foreach ($file in @(Get-ChildItem -LiteralPath $Source -Force -File -Recurse | Sort-Object FullName)) {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        $input = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $output = [IO.FileStream]::new($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
            [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
        try {
            $buffer = [byte[]]::new(65536)
            $read = $input.Read($buffer, 0, $buffer.Length)
            if ($read -gt 0) {
                $firstLength = [Math]::Max(1, [int][Math]::Floor($read / 2))
                $output.Write($buffer, 0, $firstLength)
                $output.Flush($true)
                if (-not $crashReached -and $CrashPoint) {
                    $crashReached = $true
                    Invoke-SyncSharkCrashPoint $CrashPoint $CrashAfter
                }
                if ($firstLength -lt $read) { $output.Write($buffer, $firstLength, $read - $firstLength) }
            }
            while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) { $output.Write($buffer, 0, $read) }
            $output.Flush($true)
        } finally { $output.Dispose(); $input.Dispose() }
    }
    if (-not $crashReached -and $CrashPoint) { Invoke-SyncSharkCrashPoint $CrashPoint $CrashAfter }
    Assert-SyncSharkTreeMatch $Source $Destination
}

function Assert-SyncSharkRuntimeSystemReadAccess([string]$Root) {
    Assert-SyncSharkOrdinaryTree $Root
    if (-not (Test-Path -LiteralPath (Join-Path $Root 'SyncShark.exe') -PathType Leaf)) {
        throw 'Windows icon access applies only to a verified app runtime, never product state.'
    }
    $required = [int][Security.AccessControl.FileSystemRights]::ReadAndExecute
    foreach ($item in @((Get-Item -LiteralPath $Root)) + @(Get-ChildItem -LiteralPath $Root -Recurse -Force)) {
        $allowed = 0
        foreach ($rule in (Get-Acl -LiteralPath $item.FullName).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.IdentityReference.Value -cne 'S-1-5-18') { continue }
            if ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) { continue }
            $rights = [int]$rule.FileSystemRights
            if ($rule.AccessControlType -eq 'Deny' -and ($rights -band $required)) {
                throw 'An existing SYSTEM deny prevents Windows from reading the runtime icon.'
            }
            if ($rule.AccessControlType -eq 'Allow') { $allowed = $allowed -bor $rights }
        }
        if (($allowed -band $required) -ne $required) { throw 'Windows SYSTEM cannot read the installed runtime resources.' }
    }
}

function Set-SyncSharkRuntimeSystemReadAccess([string]$Root) {
    Assert-SyncSharkOrdinaryTree $Root
    if (-not (Test-Path -LiteralPath (Join-Path $Root 'SyncShark.exe') -PathType Leaf)) {
        throw 'Windows icon access applies only to a verified app runtime, never product state.'
    }
    # A same-volume move preserves private staging ACLs. Windows consent runs
    # outside the user's token and must read the *published public binaries*.
    # Add read/execute for SYSTEM only. Do not reset ACLs, grant other users or
    # give SYSTEM new write rights; state/recovery/credential directories stay private.
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $directory = [IO.DirectoryInfo]::new($Root)
    $security = [IO.FileSystemAclExtensions]::GetAccessControl($directory,
        [Security.AccessControl.AccessControlSections]::Access)
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($system,
        [Security.AccessControl.FileSystemRights]::ReadAndExecute,
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    [IO.FileSystemAclExtensions]::SetAccessControl($directory, $security)
    Assert-SyncSharkRuntimeSystemReadAccess $Root
}

function Invoke-SyncSharkCrashPoint([string]$Name, [string]$CrashAfter) {
    if ($CrashAfter -ceq $Name) {
        [Diagnostics.Process]::GetCurrentProcess().Kill()
        Start-Sleep -Seconds 60
    }
}

function Invoke-SyncSharkJournalStep([object]$Journal, [string]$JournalPath, [string]$Step,
    [string]$CrashAfter, [scriptblock]$Effect, [scriptblock]$Verify) {
    if (@($Journal.completed) -contains $Step) {
        # A later phase is allowed to consume or replace an earlier phase's
        # intermediate artifact. Re-verify only the latest durable boundary;
        # when a later phase is pending, that phase's idempotent effect and
        # verifier perform reconciliation. A complete transaction reaches its
        # aggregate final verifier through the last completed step.
        $stepIndex = [Array]::IndexOf([string[]]$Journal.allowed_steps, $Step)
        $highestCompleted = -1
        foreach ($completedStep in @($Journal.completed)) {
            $completedIndex = [Array]::IndexOf([string[]]$Journal.allowed_steps, [string]$completedStep)
            if ($completedIndex -gt $highestCompleted) { $highestCompleted = $completedIndex }
        }
        if (-not $Journal.pending -and $stepIndex -eq $highestCompleted) { & $Verify }
        return
    }
    if ($Journal.pending -and $Journal.pending -cne $Step) {
        throw "Another deployment phase is pending: $($Journal.pending)"
    }
    $Journal.pending = $Step
    Write-SyncSharkJournal $JournalPath $Journal $CrashAfter "$Step-pending-journal"
    Invoke-SyncSharkCrashPoint "$Step-after-pending" $CrashAfter
    & $Effect
    Invoke-SyncSharkCrashPoint "$Step-after-effect" $CrashAfter
    & $Verify
    $Journal.completed = @($Journal.completed) + $Step
    $Journal.pending = $null
    if ($Step -ceq $Journal.allowed_steps[-1]) { $Journal.status = 'complete' }
    Write-SyncSharkJournal $JournalPath $Journal $CrashAfter "$Step-complete-journal"
    Invoke-SyncSharkCrashPoint "$Step-after-journal" $CrashAfter
}

# native exclusion object. The application holds it ReadWrite/ShareNone while it
# runs, so a maintenance read lease both proves that no application instance is
# live and refuses every later application start for the whole transaction. Read
# sharing keeps byte-exact backup and hashing of the non-authorizing process-id
# file possible, and does not infer quiescence from process enumeration.
$script:SyncSharkMaintenanceLease = $null
$script:SyncSharkMaintenanceAdmissionFailure = 'The installed application state lock could not be held for maintenance. Nothing was changed.'

function Resolve-SyncSharkMaintenanceLeaseRoot([string]$ProductRoot) {
    if ([string]::IsNullOrWhiteSpace($ProductRoot)) { throw 'An exact product root is required for release maintenance.' }
    $resolved = Get-SyncSharkNormalizedPath $ProductRoot
    if ($resolved -ieq (Get-SyncSharkNormalizedPath (Resolve-SyncSharkProductRoot ''))) { return Resolve-SyncSharkDataRoot $resolved }
    # Any other root must be the exact isolated release-test root, which the
    # existing seam resolves or refuses; no caller-supplied substitute root.
    Resolve-SyncSharkDataRoot (Resolve-SyncSharkProductRoot $resolved)
}

function Get-SyncSharkLiveMaintenanceLease([string]$ProductRoot) {
    # The exact live module-owned lease for this same resolved product root, or
    # $null. Only the maintenance wrapper creates it, in module-private scope.
    $lease = $script:SyncSharkMaintenanceLease
    if ($null -eq $lease) { return $null }
    $resolved = Resolve-SyncSharkMaintenanceLeaseRoot $ProductRoot
    if ($lease.Root -ine $resolved) { return $null }
    if ($null -eq $lease.Mutex -or $lease.Mutex.SafeWaitHandle.IsClosed -or
        $null -eq $lease.Stream -or $lease.Stream.SafeFileHandle.IsClosed -or -not $lease.Stream.CanRead) {
        throw 'The held maintenance lease is no longer readable. Nothing was changed.'
    }
    if ((Get-SyncSharkNormalizedPath ([string]$lease.LockPath)) -cne
        (Get-SyncSharkNormalizedPath (Join-Path $resolved 'state\instance.lock'))) {
        throw 'The held maintenance lease is bound to a different application state lock.'
    }
    $lease
}

function Open-SyncSharkInstanceExclusion([string]$ProductRoot) {
    # Reuse ONLY the exact live maintenance lease for this same resolved product
    # root. Otherwise take the current independent exclusive lock. A reused lease
    # stays owned by its wrapper scope and is never disposed here.
    $resolved = Resolve-SyncSharkMaintenanceLeaseRoot $ProductRoot
    $lease = Get-SyncSharkLiveMaintenanceLease $ProductRoot
    if ($null -ne $lease) { return [pscustomobject]@{ Stream = $lease.Stream; Owned = $false } }
    [pscustomobject]@{ Owned = $true; Stream = [IO.File]::Open((Join-Path $resolved 'state\instance.lock'),
        [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
}

function Enter-SyncSharkMaintenanceMutex([string]$ProductRoot) {
    $installation = Get-SyncSharkNormalizedPath $ProductRoot
    $normal = Get-SyncSharkNormalizedPath (Resolve-SyncSharkProductRoot '')
    if ($installation -ine $normal) { $installation = Resolve-SyncSharkProductRoot $ProductRoot }
    $identity = [Text.Encoding]::UTF8.GetBytes($installation.ToUpperInvariant())
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $name = 'Global\SyncShark.Maintenance.' + ([BitConverter]::ToString($hash.ComputeHash($identity))).Replace('-', '') }
    finally { $hash.Dispose() }
    $mutex = [Threading.Mutex]::new($false, $name)
    $owned = $false
    try {
        try { $owned = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $owned = $true }
        if (-not $owned) { throw $script:SyncSharkMaintenanceAdmissionFailure }
        return $mutex
    }
    catch { $mutex.Dispose(); throw }
}

function Invoke-SyncSharkWithInstanceMaintenanceLease {
    param([string]$ProductRoot,[scriptblock]$Body)
    if ($null -eq $Body) { throw 'A release transaction body is required.' }
    if ($null -ne $script:SyncSharkMaintenanceLease) {
        throw 'A release maintenance lease is already held by this session; nested acquisition is refused.'
    }
    $mutex = Enter-SyncSharkMaintenanceMutex $ProductRoot
    try {
    $root = Resolve-SyncSharkMaintenanceLeaseRoot $ProductRoot
    Assert-SyncSharkOrdinaryTree $root -RootOnly
    $state = Join-Path $root 'state'
    Assert-SyncSharkOrdinaryTree $state -RootOnly
    $lock = Join-Path $state 'instance.lock'
    $item = Get-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) { throw 'The installed application state lock is missing. Nothing was changed.' }
    if ($item.PSIsContainer -or ($item.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline))) {
        throw 'The installed application state lock is not an ordinary local file. Nothing was changed.'
    }
    $stream = [IO.File]::Open($lock, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    if (-not $stream.CanRead -or $stream.SafeFileHandle.IsClosed) {
        $stream.Dispose()
        throw $script:SyncSharkMaintenanceAdmissionFailure
    }
    $previous = $script:SyncSharkMaintenanceLease
    $script:SyncSharkMaintenanceLease = [pscustomobject]@{ Root = $root; LockPath = (Get-SyncSharkNormalizedPath $lock); Stream = $stream; Mutex = $mutex }
    try {
        if ($root -ine (Resolve-SyncSharkMaintenanceLeaseRoot $ProductRoot)) {
            throw 'The data location changed while acquiring maintenance. Nothing was changed.'
        }
        & $Body
    }
    finally {
        $script:SyncSharkMaintenanceLease = $previous
        $stream.Dispose()
    }
    }
    finally {
        $mutex.ReleaseMutex()
        $mutex.Dispose()
    }
}

# Windows ARP values: https://learn.microsoft.com/windows/win32/msi/uninstall-registry-key
# Per-user Programs folder / shell links: https://learn.microsoft.com/windows/win32/shell/links
function Invoke-SyncSharkAppRegistration {
    param([string]$Version,[ValidateSet('Register','Remove','Verify','VerifyAbsent')][string]$Operation)
    if ($Version -cnotmatch '^\d+\.\d+\.\d+$') { throw 'An exact registration version is required.' }
    $root = Resolve-SyncSharkProductRoot ''
    $runtime = Join-Path $root 'active'
    $executable = Join-Path $runtime 'SyncShark.exe'
    $uninstall = '"' + (Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe') + '" -NoProfile -ExecutionPolicy Bypass -File "' +
        (Join-Path $runtime 'tools\SyncShark.Uninstall.ps1') + '"'
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Massdex.SyncShark'
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Massdex\Sync Shark.lnk'
    $owner = 'Massdex.SyncShark:' + $root
    $values = @{ DisplayName='Sync Shark'; DisplayVersion=$Version; Publisher='Massdex';
        InstallLocation=(Resolve-SyncSharkRuntimeRoot $Version); UninstallString=$uninstall; SyncSharkOwner=$owner }
    $existing = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
    # An empty key can survive creation before the first value; provider metadata is not a value.
    $keyExists = Test-Path -LiteralPath $key
    $emptyKey = $false
    if ($keyExists) {
        $registryKey = Get-Item -LiteralPath $key -ErrorAction Stop
        try { $emptyKey = $registryKey.ValueCount -eq 0 -and $registryKey.SubKeyCount -eq 0 }
        finally { if ($registryKey -is [IDisposable]) { $registryKey.Dispose() } }
    }
    if ($keyExists -and -not $emptyKey -and ($null -eq $existing -or $null -eq $existing.PSObject.Properties['SyncSharkOwner'] -or
            $existing.SyncSharkOwner -cne $owner)) {
        throw 'Another installation owns the uninstall registration. Nothing was changed.'
    }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $null
        if (Test-Path -LiteralPath $shortcut) {
            $item = Get-Item -LiteralPath $shortcut -Force
            if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'The Start-menu shortcut must be an ordinary file.'
            }
            $link = $shell.CreateShortcut($shortcut)
            if ($link.TargetPath -ine $executable -or $link.Arguments -notin @('', '--cli engine host start --yes')) {
                throw 'Another application owns the Start-menu shortcut. Nothing was changed.'
            }
        }
        if ($Operation -eq 'Register') {
            Assert-SyncSharkJunction $runtime $values.InstallLocation
            if (-not (Test-Path -LiteralPath (Join-Path $runtime 'tools\SyncShark.Uninstall.ps1') -PathType Leaf)) {
                throw 'The installed uninstall entry point is missing.'
            }
            if (-not $keyExists) { New-Item -Path $key | Out-Null }
            # Ownership precedes descriptive fields so an interrupted owned write can be replayed.
            New-ItemProperty -LiteralPath $key -Name 'SyncSharkOwner' -Value $owner -PropertyType String -Force | Out-Null
            foreach ($name in $values.Keys | Where-Object { $_ -cne 'SyncSharkOwner' }) {
                New-ItemProperty -LiteralPath $key -Name $name -Value $values[$name] -PropertyType String -Force | Out-Null
            }
            if ($null -eq $link) {
                $folder = Split-Path -Parent $shortcut
                Assert-SyncSharkOrdinaryTree $folder -RootOnly -AllowMissing
                [void][IO.Directory]::CreateDirectory($folder)
                $link = $shell.CreateShortcut($shortcut)
            }
            $link.TargetPath=$executable; $link.Arguments='--cli engine host start --yes'; $link.WorkingDirectory=$runtime; $link.Save()
        } elseif ($Operation -eq 'Remove') {
            if ($null -ne $link) { Remove-Item -LiteralPath $shortcut }
            if ($keyExists) { Remove-Item -LiteralPath $key }
        } elseif ($Operation -eq 'VerifyAbsent') {
            if ($null -ne $link -or $keyExists) { throw 'Application registration remains.' }
        } else {
            if ($null -eq $link -or $null -eq $existing) { throw 'Application registration is missing.' }
            if ($link.Arguments -cne '--cli engine host start --yes') { throw 'Start menu must launch the engine host.' }
            foreach ($name in $values.Keys) {
                if ($existing.$name -cne $values[$name]) { throw "Application registration differs: $name" }
            }
        }
    } finally {
        if ($null -ne $link -and [Runtime.InteropServices.Marshal]::IsComObject($link)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
        if ([Runtime.InteropServices.Marshal]::IsComObject($shell)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    }
}

function Initialize-SyncSharkSignatureInspector {
    if ('SyncShark.Release.SignatureInspector' -as [type]) { return }
    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    Add-Type -AssemblyName System.Formats.Asn1
    $references = @(
        [System.Security.Cryptography.Pkcs.SignedCms].Assembly.Location,
        [System.Formats.Asn1.AsnReader].Assembly.Location,
        [System.Security.Cryptography.X509Certificates.X509Certificate2].Assembly.Location,
        [System.Collections.CollectionBase].Assembly.Location,
        [System.IO.Compression.ZipArchive].Assembly.Location,
        [System.Numerics.BigInteger].Assembly.Location
    )
    Add-Type -ReferencedAssemblies $references -TypeDefinition @'
using System;
using System.Formats.Asn1;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace SyncShark.Release {
  public sealed class TimestampResult {
    public bool Present { get; set; }
    public bool CryptographicallyValid { get; set; }
    public bool IsRfc3161 { get; set; }
    public bool SignatureBound { get; set; }
    public bool ChainEvaluatedAtTokenTime { get; set; }
    public bool ChainValid { get; set; }
    public string TokenTimestampUtc { get; set; } = "";
    public string[] ChainStatuses { get; set; } = Array.Empty<string>();
  }
  public static class SignatureInspector {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct TrustFile {
      public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path;
      public IntPtr File, Subject;
    }
    [StructLayout(LayoutKind.Sequential)] struct TrustData {
      public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Choice;
      public IntPtr File; public uint Action; public IntPtr State, Url; public uint Flags, Context; public IntPtr Settings;
    }
    [DllImport("wintrust.dll", ExactSpelling=true)] static extern uint WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    public static uint VerifyFileIntegrity(string path) {
      var file = new TrustFile { Size=(uint)Marshal.SizeOf<TrustFile>(), Path=path };
      IntPtr pointer=Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
      Marshal.StructureToPtr(file,pointer,false);
      var data=new TrustData { Size=(uint)Marshal.SizeOf<TrustData>(), Ui=2, Choice=1, File=pointer, Action=1, Flags=0x1000 };
      var action=new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
      try { return WinVerifyTrust(new IntPtr(-1),ref action,ref data); }
      finally { data.Action=2; WinVerifyTrust(new IntPtr(-1),ref action,ref data); Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer); }
    }
    const string Rfc3161Attribute = "1.3.6.1.4.1.311.3.3.1";
    const string TstInfoContent = "1.2.840.113549.1.9.16.1.4";
    const string TimestampingEku = "1.3.6.1.5.5.7.3.8";
    public static TimestampResult InspectTimestamp(string path) => InspectTimestamp(path, null);
    public static TimestampResult InspectTimestamp(string path, X509Certificate2 customTrustRoot) {
      var result = new TimestampResult();
      try {
        byte[] cmsBytes = ReadAuthenticode(path);
        var cms = new SignedCms(); cms.Decode(cmsBytes); cms.CheckSignature(true);
        if (cms.SignerInfos.Count != 1) return result;
        var primary = cms.SignerInfos[0];
        CryptographicAttributeObject attr = null;
        foreach (CryptographicAttributeObject candidate in primary.UnsignedAttributes)
          if (candidate.Oid?.Value == Rfc3161Attribute) { if (attr != null) return result; attr = candidate; }
        if (attr == null || attr.Values.Count != 1) return result;
        return InspectTimestampToken(attr.Values[0].RawData, primary, customTrustRoot);
      } catch { return result; }
    }
    public static TimestampResult InspectTimestampTokenForFile(string path, byte[] encodedToken,
        X509Certificate2 customTrustRoot) {
      var result = new TimestampResult();
      try {
        byte[] cmsBytes = ReadAuthenticode(path);
        var cms = new SignedCms(); cms.Decode(cmsBytes); cms.CheckSignature(true);
        if (cms.SignerInfos.Count != 1) return result;
        return InspectTimestampToken(encodedToken, cms.SignerInfos[0], customTrustRoot);
      } catch { return result; }
    }
    static TimestampResult InspectTimestampToken(byte[] encodedToken, SignerInfo primary,
        X509Certificate2 customTrustRoot) {
      var result = new TimestampResult { Present = true };
      try {
        if (!Rfc3161TimestampToken.TryDecode(encodedToken, out Rfc3161TimestampToken token, out int consumed) ||
            token == null || consumed != encodedToken.Length) return result;
        var stamp = token.AsSignedCms(); stamp.CheckSignature(true);
        result.CryptographicallyValid = true;
        if (stamp.ContentInfo.ContentType?.Value != TstInfoContent || stamp.SignerInfos.Count != 1) return result;
        var tokenInfo = token.TokenInfo;
        if (tokenInfo.Version != 1 || tokenInfo.GetSerialNumber().Length == 0 ||
            !CryptographicOperations.FixedTimeEquals(tokenInfo.Encode(), stamp.ContentInfo.Content)) return result;
        result.IsRfc3161 = true;
        result.TokenTimestampUtc = tokenInfo.Timestamp.UtcDateTime.ToString("O");
        if (!token.VerifySignatureForSignerInfo(primary, out X509Certificate2 cert, stamp.Certificates) || cert == null)
          return result;
        result.SignatureBound = true;
        if (!HasCriticalExclusiveTimestampingEku(cert)) return result;
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = customTrustRoot == null ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(TimestampingEku));
        chain.ChainPolicy.VerificationTime = tokenInfo.Timestamp.UtcDateTime;
        if (chain.ChainPolicy.VerificationTimeIgnored) return result;
        result.ChainEvaluatedAtTokenTime = true;
        if (customTrustRoot != null) {
          chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
          chain.ChainPolicy.CustomTrustStore.Add(customTrustRoot);
        }
        foreach (X509Certificate2 embedded in stamp.Certificates)
          chain.ChainPolicy.ExtraStore.Add(embedded);
        result.ChainValid = chain.Build(cert);
        var statuses = new string[chain.ChainStatus.Length];
        for (int i = 0; i < chain.ChainStatus.Length; i++) statuses[i] = chain.ChainStatus[i].Status.ToString();
        result.ChainStatuses = statuses;
        return result;
      } catch { return result; }
    }
    static bool HasCriticalExclusiveTimestampingEku(X509Certificate2 cert) {
      X509EnhancedKeyUsageExtension timestampEku = null;
      foreach (X509Extension extension in cert.Extensions) {
        if (extension.Oid?.Value != "2.5.29.37") continue;
        if (timestampEku != null || !extension.Critical) return false;
        timestampEku = extension as X509EnhancedKeyUsageExtension ??
          new X509EnhancedKeyUsageExtension(extension, extension.Critical);
      }
      return timestampEku != null && timestampEku.EnhancedKeyUsages.Count == 1 &&
        timestampEku.EnhancedKeyUsages[0].Value == TimestampingEku;
    }
    static byte[] ReadAuthenticode(string path) {
      var extension = Path.GetExtension(path);
      if (string.Equals(extension, ".msix", StringComparison.OrdinalIgnoreCase)) {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException();
        using var file=File.OpenRead(path); using var archive=new ZipArchive(file,ZipArchiveMode.Read);
        ZipArchiveEntry signature=null;
        foreach(var entry in archive.Entries) if (string.Equals(entry.FullName,"AppxSignature.p7x",StringComparison.OrdinalIgnoreCase)) {
          if(signature!=null || entry.FullName!="AppxSignature.p7x" || entry.Length<5 || entry.Length>1024*1024) throw new InvalidDataException();
          signature=entry;
        }
        if(signature==null) throw new InvalidDataException();
        using var input=signature.Open(); var bytes=new byte[checked((int)signature.Length)]; input.ReadExactly(bytes);
        if(bytes[0]!='P' || bytes[1]!='K' || bytes[2]!='C' || bytes[3]!='X') throw new InvalidDataException();
        return bytes[4..];
      }
      if (string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase) ||
          string.Equals(extension, ".psm1", StringComparison.OrdinalIgnoreCase)) {
        var text = File.ReadAllText(path);
        const string begin = "# SIG # Begin signature block";
        const string end = "# SIG # End signature block";
        int start = text.LastIndexOf(begin, StringComparison.Ordinal), finish = text.LastIndexOf(end, StringComparison.Ordinal);
        if (start < 0 || finish <= start || text.Substring(finish + end.Length).Trim().Length != 0) throw new InvalidDataException();
        var encoded = new System.Text.StringBuilder();
        foreach (var line in text.Substring(start + begin.Length, finish - start - begin.Length).Split('\n')) {
          var encodedLine = line.Trim(); if (encodedLine.Length == 0) continue;
          if (!encodedLine.StartsWith("# ", StringComparison.Ordinal)) throw new InvalidDataException();
          encoded.Append(encodedLine.Substring(2));
        }
        return Convert.FromBase64String(encoded.ToString());
      }
      byte[] pe = File.ReadAllBytes(path);
      if (pe.Length < 0x100 || BitConverter.ToUInt16(pe, 0) != 0x5a4d) throw new InvalidDataException();
      int header = BitConverter.ToInt32(pe, 0x3c);
      if (header < 0 || header + 24 >= pe.Length || BitConverter.ToUInt32(pe, header) != 0x00004550) throw new InvalidDataException();
      int optional = header + 24; ushort magic = BitConverter.ToUInt16(pe, optional);
      int directory = optional + (magic == 0x20b ? 112 : magic == 0x10b ? 96 : throw new InvalidDataException());
      int security = directory + 8 * 4;
      int offset = checked((int)BitConverter.ToUInt32(pe, security));
      int size = checked((int)BitConverter.ToUInt32(pe, security + 4));
      if (offset <= 0 || size < 8 || offset + size > pe.Length) throw new InvalidDataException();
      int length = checked((int)BitConverter.ToUInt32(pe, offset));
      if (length < 8 || length > size || offset + length > pe.Length) throw new InvalidDataException();
      byte[] value = new byte[length - 8]; Buffer.BlockCopy(pe, offset + 8, value, 0, value.Length); return value;
    }
  }
}
'@
}

function Get-SyncSharkSignatureObservationCore([string]$Path,
    [Security.Cryptography.X509Certificates.X509Certificate2]$TimestampCustomTrustRoot) {
    Initialize-SyncSharkSignatureInspector
    # Check the file/package digest through the Windows SIP, separately from the
    # certificate chain and timestamp CMS. An unrelated UnknownError is never
    # accepted merely because this product's self-issued root is untrusted.
    $integrity = [SyncShark.Release.SignatureInspector]::VerifyFileIntegrity($Path)
    if ($integrity -ne 0 -and $integrity -ne [Convert]::ToUInt32('800b0109',16)) { throw ('Windows signature integrity verification failed: 0x{0:x8}' -f $integrity) }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $cert = $signature.SignerCertificate
    $signerStatuses = @()
    if ($cert) {
        $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
        try {
            $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
            $null = $chain.Build($cert)
            $signerStatuses = @($chain.ChainStatus | ForEach-Object { $_.Status.ToString() })
        } finally { $chain.Dispose() }
    }
    $timestamp = [SyncShark.Release.SignatureInspector]::InspectTimestamp($Path, $TimestampCustomTrustRoot)
    $rawStatus = $signature.Status.ToString()
    # PowerShell reports this self-issued package as UnknownError on this host
    # even though its independently built signer chain differs only by the
    # expected UntrustedRoot. Normalize that exact observed condition to the
    # explicit NotTrusted decision class; every injected/raw UnknownError is
    # otherwise rejected by the production policy below.
    $status = if ($rawStatus -ceq 'UnknownError' -and $signerStatuses.Count -eq 1 -and
        $signerStatuses[0] -ceq 'UntrustedRoot') { 'NotTrusted' } else { $rawStatus }
    [pscustomobject]@{
        Status = $status
        RawStatus = $rawStatus
        Thumbprint = if ($cert) { $cert.Thumbprint } else { $null }
        Subject = if ($cert) { $cert.Subject } else { $null }
        Issuer = if ($cert) { $cert.Issuer } else { $null }
        SpkiSha256 = if ($cert) {
            [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                $cert.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
        } else { $null }
        SignerChainStatuses = $signerStatuses
        Rfc3161Present = $timestamp.Present
        Rfc3161CryptographicallyValid = $timestamp.CryptographicallyValid
        Rfc3161 = $timestamp.IsRfc3161
        TimestampSignatureBound = $timestamp.SignatureBound
        TimestampChainEvaluatedAtTokenTime = $timestamp.ChainEvaluatedAtTokenTime
        TimestampChainValid = $timestamp.ChainValid
        TimestampUtc = $timestamp.TokenTimestampUtc
        TimestampChainStatuses = @($timestamp.ChainStatuses)
    }
}

function Get-SyncSharkSignatureObservation([string]$Path) {
    Get-SyncSharkSignatureObservationCore $Path $null
}

function Assert-SyncSharkSignatureObservation([object]$Observation, [string[]]$ExpectedThumbprint,
    [string]$ExpectedSubject, [string]$ExpectedIssuer, [string[]]$ExpectedSpkiSha256) {
    if ($Observation.Thumbprint -cnotin $ExpectedThumbprint -or $Observation.Subject -cne $ExpectedSubject -or
        $Observation.Issuer -cne $ExpectedIssuer -or $Observation.SpkiSha256 -cnotin $ExpectedSpkiSha256) {
        throw 'Pinned signer identity mismatch.'
    }
    $signerStatuses = @($Observation.SignerChainStatuses)
    $allowed = $Observation.Status -ceq 'Valid' -and $signerStatuses.Count -eq 0
    if ($Observation.Status -ceq 'NotTrusted' -and $signerStatuses.Count -eq 1 -and
        $signerStatuses[0] -ceq 'UntrustedRoot') { $allowed = $true }
    if (-not $allowed) { throw "Authenticode status is not permitted: $($Observation.Status)" }
    if (-not $Observation.Rfc3161Present -or -not $Observation.Rfc3161CryptographicallyValid -or
        -not $Observation.Rfc3161 -or -not $Observation.TimestampSignatureBound -or
        -not $Observation.TimestampChainEvaluatedAtTokenTime -or
        -not $Observation.TimestampChainValid -or @($Observation.TimestampChainStatuses).Count -ne 0) {
        throw 'A cryptographically valid, signature-bound RFC3161 timestamp with a chain valid at the token time is required.'
    }
}

function Get-SyncSharkSignedMemberHashes([object]$Contract) {
    $result = @{}
    foreach ($property in $Contract.signedMembers.PSObject.Properties) {
        if ($property.Name -notmatch '^[^/\\:]+\.exe$' -or $property.Value -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid signed executable identity.' }
        $result.Add($property.Name,[string]$property.Value)
    }
    if ($result.Count -ne 2) { throw 'Expected a signed host and CLI.' }
    return $result
}

function Assert-SyncSharkExactPackage([string]$Root, [string]$ManifestPath, [string]$ExpectedManifestSha256,
    [string]$ExpectedVersion, [hashtable]$SignedHashes, [string[]]$ExpectedThumbprint,
    [string]$ExpectedSubject, [string]$ExpectedIssuer, [string[]]$ExpectedSpkiSha256,
    [string]$VerificationObservationPath, [bool]$AllowObservationInjection) {
    if ((Get-SyncSharkSha256 $ManifestPath) -cne $ExpectedManifestSha256) { throw 'Package manifest hash mismatch.' }
    Assert-SyncSharkOrdinaryTree $Root
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json -Depth 20
    if ($SignedHashes.Count -ne 2) { throw 'Expected two executable pins.' }
    foreach ($name in $SignedHashes.Keys) {
        if ($name -notmatch '^[^/\\:]+\.exe$' -or $SignedHashes[$name] -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid signed executable pin.' }
    }
    $expected = [ordered]@{}
    foreach ($member in $manifest.members) {
        $path = [string]$member.path
        if (-not $path -or [IO.Path]::IsPathRooted($path) -or $path.Contains('\') -or
            @($path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -or $expected.Contains($path)) {
            throw 'The package manifest contains an invalid or duplicate member path.'
        }
        $expected[$path] = if ($SignedHashes.ContainsKey($path)) { $SignedHashes[$path] } else { [string]$member.sha256 }
    }
    foreach ($name in $SignedHashes.Keys) {
        if (-not $expected.Contains($name)) { throw "Signed component is absent from the manifest: $name" }
    }
    $actual = @(Get-ChildItem -LiteralPath $Root -File -Force -Recurse)
    if ($actual.Count -ne $expected.Count) { throw 'Package membership mismatch.' }
    foreach ($file in $actual) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if (-not $expected.Contains($relative) -or (Get-SyncSharkSha256 $file.FullName) -cne $expected[$relative]) {
            throw "Package identity mismatch: $relative"
        }
    }
    $injected = $null
    if ($VerificationObservationPath) {
        if (-not $AllowObservationInjection) { throw 'Signature observation injection is limited to the isolated release-test seam.' }
        $injected = Get-Content -LiteralPath $VerificationObservationPath -Raw | ConvertFrom-Json -Depth 20
    }
    foreach ($name in $SignedHashes.Keys) {
        $path = Join-Path $Root $name
        if (
            [Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion -cne $ExpectedVersion) {
            throw "Product version mismatch: $name"
        }
        $observation = if ($injected) { $injected.$name } else { Get-SyncSharkSignatureObservation $path }
        Assert-SyncSharkSignatureObservation $observation $ExpectedThumbprint $ExpectedSubject $ExpectedIssuer $ExpectedSpkiSha256
    }
}

Export-ModuleMember -Function Get-SyncSharkSha256, Get-SyncSharkNormalizedPath,
    Get-SyncSharkSignerIdentity,
    Assert-SyncSharkWithin, Assert-SyncSharkOrdinaryTree, Assert-SyncSharkTransactionTree, Get-SyncSharkTreeManifest,
    Assert-SyncSharkTreeMatch, Get-SyncSharkTreeIdentity, Get-SyncSharkJunctionTarget, Assert-SyncSharkJunction,
    Resolve-SyncSharkProductRoot, Read-SyncSharkJournal, Write-SyncSharkJournal,
    Get-SyncSharkAtomicReplacePolicy, Copy-SyncSharkTreeToStaging,
    Invoke-SyncSharkCrashPoint, Invoke-SyncSharkJournalStep, Get-SyncSharkSignatureObservation,
    Assert-SyncSharkSignatureObservation, Get-SyncSharkSignedMemberHashes, Assert-SyncSharkExactPackage,
    Resolve-SyncSharkDataRoot, Resolve-SyncSharkRuntimeRoot, Invoke-SyncSharkAppRegistration,
    Invoke-SyncSharkWithInstanceMaintenanceLease, Enter-SyncSharkMaintenanceMutex
