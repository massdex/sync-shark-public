param(
    [Parameter(Mandatory=$true)][string]$FixtureParent,
    [string]$RuntimeFixture = ''
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'SyncShark.Release.psm1') -Force
. (Join-Path $PSScriptRoot 'SyncShark.Update.ps1')

# The existing isolated release-test seam is the only root exempt from the
# installed-state lease; the whole run stays inside it.
$env:SYNC_SHARK_RELEASE_TEST_MODE='1'
# Optional operator snapshot fallback; explicit fixtures need no diagnostic root.
$diagRoot=$env:SYNCSHARK_DIAG_ROOT

# Isolated, source-level proof for SS-A08 and SS-A09. No installed product root,
# no privileged task, no Appx registration, no signer and no live launch is used.
$fixtureParent=[IO.Path]::GetFullPath($FixtureParent)
if(-not (Test-Path -LiteralPath $fixtureParent -PathType Container)){throw 'The fixture parent allocation must already exist.'}
$modulePath=Join-Path $PSScriptRoot 'SyncShark.Release.psm1'
$updatePath=Join-Path $PSScriptRoot 'SyncShark.Update.ps1'
$installPath=Join-Path $PSScriptRoot 'Install-SyncShark.ps1.template'
$rootBase=Join-Path ([IO.Path]::GetTempPath()) 'sync-shark-release-tests'
if(-not (Test-Path -LiteralPath $rootBase -PathType Container)){[void][IO.Directory]::CreateDirectory($rootBase)}
$checks=[Collections.Generic.List[string]]::new()
$unverified=[Collections.Generic.List[string]]::new()
$stateRoot=$null;$missingRoot=$null;$productRoot=$null;$packageRoot=$null;$sqlite=$null;$executableBackup=$null
function Check([bool]$Value,[string]$Label){
    if(-not $Value){throw ('FAILED: '+$Label)}
    $checks.Add($Label);[Console]::Out.WriteLine('PASS '+$Label)
}
function CheckRefused([scriptblock]$Action,[string]$Label){
    $refused=$false
    try { & $Action } catch { $refused=$true }
    if(-not $refused){throw ('Expected refusal: '+$Label)}
    Check $true $Label
}
function Get-ProductionFunction([object]$Ast,[string]$Name){
    $node=$Ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $Name},$true)
    if($null -eq $node){throw ('Production function not found: '+$Name)}
    $node.Extent.Text
}
function Get-SqliteFixtureProvider {
    # A real Microsoft.Data.Sqlite WAL fixture when the pinned packages are
    # already on this machine; otherwise the caller records the exact reason.
    $packages=Join-Path $env:USERPROFILE '.nuget\packages'
    if(-not (Test-Path -LiteralPath $packages -PathType Container)){return $null}
    $wanted=[ordered]@{
        'SQLitePCLRaw.core.dll'='sqlitepclraw.core'
        'SQLitePCLRaw.provider.e_sqlite3.dll'='sqlitepclraw.provider.e_sqlite3'
        'Microsoft.Data.Sqlite.dll'='microsoft.data.sqlite.core'
        'SQLitePCLRaw.batteries_v2.dll'='sqlitepclraw.bundle_e_sqlite3'
    }
    $resolved=@{}
    foreach($assembly in $wanted.Keys){
        $root=Join-Path $packages $wanted[$assembly]
        if(-not (Test-Path -LiteralPath $root -PathType Container)){return $null}
        $candidate=Get-ChildItem -LiteralPath $root -Recurse -Filter $assembly -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like '*net8.0*' } | Select-Object -First 1
        if($null -eq $candidate){return $null}
        $resolved[$assembly]=$candidate.FullName
    }
    $nativeRoot=Join-Path $packages 'sqlitepclraw.lib.e_sqlite3'
    if(Test-Path -LiteralPath $nativeRoot -PathType Container){
        $native=Get-ChildItem -LiteralPath $nativeRoot -Recurse -Filter 'e_sqlite3.dll' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like '*win-x64*' } | Select-Object -First 1
        if($null -ne $native){[void][Runtime.InteropServices.NativeLibrary]::Load($native.FullName)}
    }
    foreach($assembly in $wanted.Keys){Add-Type -Path $resolved[$assembly]}
    [SQLitePCL.Batteries_V2]::Init()
    'Microsoft.Data.Sqlite'
}
function New-SqliteWalFixture([string]$Path) {
    $connection=[Microsoft.Data.Sqlite.SqliteConnection]::new('Data Source='+$Path)
    $connection.Open()
    $command=$connection.CreateCommand()
    $command.CommandText="PRAGMA journal_mode=WAL; CREATE TABLE consume(id INTEGER PRIMARY KEY, note TEXT NOT NULL); INSERT INTO consume(note) VALUES ('first'),('second'),('third');"
    [void]$command.ExecuteNonQuery()
    # Kept open so the committed WAL frames remain on disk for the backup proof.
    $connection
}
function Get-StateFileIdentities([string]$Directory) {
    @(Get-ChildItem -LiteralPath $Directory -File -Force | Sort-Object Name | ForEach-Object {
        [ordered]@{ name=$_.Name; length=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
}
function Resolve-RuntimeFixtureFile([string]$Requested) {
    $candidates=[Collections.Generic.List[string]]::new()
    if($Requested){$candidates.Add($Requested)}
    $candidates.Add((Join-Path $fixtureParent 'SyncShark.exe'))
    foreach($candidate in $candidates){
        if(Test-Path -LiteralPath $candidate -PathType Leaf){return [IO.Path]::GetFullPath($candidate)}
    }
    if(-not [string]::IsNullOrWhiteSpace($diagRoot)){
        $snapshotRoot=[IO.Path]::GetFullPath($diagRoot)
        foreach($configuration in @('Release','Debug')){
            $candidate=Join-Path $snapshotRoot ('2026-09-04--damaged-dropbox-checkout\snapshot\repository\apps\SyncShark\bin\'+$configuration+'\net8.0-windows\SyncShark.exe')
            if(Test-Path -LiteralPath $candidate -PathType Leaf){return [IO.Path]::GetFullPath($candidate)}
        }
    }
    $null
}

try {
    # ---------------------------------------------------------------- SS-A08
    # The release transaction's exclusion object is the installed application's
    # own state lock. The wrapper, its live-lease lookup and the registration
    # exclusion are the shipped module definitions, extracted verbatim.
    $parseErrors=$null
    $moduleAst=[Management.Automation.Language.Parser]::ParseFile($modulePath,[ref]$null,[ref]$parseErrors)
    if($parseErrors.Count){throw 'Production module parse failed'}
    foreach($name in @('Resolve-SyncSharkMaintenanceLeaseRoot','Get-SyncSharkLiveMaintenanceLease',
            'Open-SyncSharkInstanceExclusion','Invoke-SyncSharkWithInstanceMaintenanceLease')){
        . ([scriptblock]::Create((Get-ProductionFunction $moduleAst $name)))
    }
    $script:SyncSharkMaintenanceLease=$null

    $stateRoot=Join-Path $rootBase ([guid]::NewGuid().ToString('N'))
    $stateDir=Join-Path $stateRoot 'state'
    [void][IO.Directory]::CreateDirectory($stateDir)
    $lockPath=Join-Path $stateDir 'instance.lock'
    [IO.File]::WriteAllText($lockPath,'4242')
    $sqliteReason='not attempted'
    if($null -eq $sqlite){
        try {
            if(Get-SqliteFixtureProvider){ $sqlite=New-SqliteWalFixture (Join-Path $stateDir 'sync-authority.db'); $sqliteReason='' }
            else { $sqliteReason='the pinned Microsoft.Data.Sqlite packages are not resolvable on this host' }
        } catch { $sqliteReason=$_.Exception.Message; $sqlite=$null }
    }
    if($null -eq $sqlite){$unverified.Add('SS-A08 real SQLite WAL/main fixture: '+$sqliteReason)}
    $stateBefore=Get-StateFileIdentities $stateDir
    $beforeIdentity=Get-SyncSharkTreeIdentity $stateDir

    $backup=Join-Path $stateRoot 'state.predecessor'
    $script:secondLaunchRefused=$false
    $script:nestedRefused=$false
    $script:registrationOwned=$null
    $script:registrationSame=$false
    $script:registrationReadable=$false
    $script:registrationIndependentRefused=$false
    $body={
        try { ([IO.File]::Open($lockPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)).Dispose() }
        catch { $script:secondLaunchRefused=$true }
        try { Invoke-SyncSharkWithInstanceMaintenanceLease $stateRoot { } } catch { $script:nestedRefused=$true }
        Copy-SyncSharkTreeToStaging $stateDir $backup
        $live=$script:SyncSharkMaintenanceLease
        $exclusion=Open-SyncSharkInstanceExclusion $stateRoot
        $script:registrationOwned=$exclusion.Owned
        $script:registrationSame=[object]::ReferenceEquals($exclusion.Stream,$live.Stream)
        $script:registrationReadable=($exclusion.Stream.CanRead -and -not $exclusion.Stream.SafeFileHandle.IsClosed)
        try { ([IO.File]::Open($lockPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)).Dispose() }
        catch { $script:registrationIndependentRefused=$true }
    }
    Invoke-SyncSharkWithInstanceMaintenanceLease $stateRoot $body
    Check $script:secondLaunchRefused 'A second application launch is refused while the maintenance lease is held'
    Check $script:nestedRefused 'A nested maintenance lease is refused instead of self-conflicting'
    Check ($script:registrationOwned -eq $false -and $script:registrationSame -and $script:registrationReadable) 'Explorer registration reuses the exact live maintenance lease without reopening it'
    Check $script:registrationIndependentRefused 'An independent exclusive state open is refused while the lease is held'
    $stateAfter=Get-StateFileIdentities $stateDir
    Check (($stateAfter|ConvertTo-Json -Depth 5 -Compress) -ceq ($stateBefore|ConvertTo-Json -Depth 5 -Compress)) 'The live state store main/WAL bytes are unchanged across the maintenance phase'
    Check ((Get-SyncSharkTreeIdentity $backup) -eq $beforeIdentity) 'The retained rollback state copy matches the source byte-for-byte'
    if($null -ne $sqlite){
        Check ((Get-FileHash -LiteralPath (Join-Path $backup 'sync-authority.db') -Algorithm SHA256).Hash -ceq
            (Get-FileHash -LiteralPath (Join-Path $stateDir 'sync-authority.db') -Algorithm SHA256).Hash) 'The backed-up SQLite main file is byte-identical to the live main file'
    }
    Check ($null -eq (Get-SyncSharkLiveMaintenanceLease $stateRoot)) 'The module-owned maintenance lease is released after success'
    $released=$false
    try { ([IO.File]::Open($lockPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)).Dispose(); $released=$true } catch { }
    Check $released 'The state lock is released after a successful transaction'
    CheckRefused { Invoke-SyncSharkWithInstanceMaintenanceLease $stateRoot {throw 'injected transaction failure'} } 'A failing transaction body propagates its failure'
    $releasedAfterError=$false
    try { ([IO.File]::Open($lockPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)).Dispose(); $releasedAfterError=$true } catch { }
    Check $releasedAfterError 'The state lock is released after a failed transaction'
    Check ($null -eq (Get-SyncSharkLiveMaintenanceLease $stateRoot)) 'No stale maintenance lease remains after a failed transaction'
    $missingRoot=Join-Path $rootBase ([guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory((Join-Path $missingRoot 'state'))
    $missingMessage=$null
    try { Invoke-SyncSharkWithInstanceMaintenanceLease $missingRoot { } } catch { $missingMessage=$_.Exception.Message }
    Check ($null -ne $missingMessage -and $missingMessage -like '*state lock is missing*') 'A missing application state lock refuses maintenance before any effect'
    $independent=Open-SyncSharkInstanceExclusion $stateRoot
    Check ($independent.Owned -eq $true) 'Outside the wrapper the exclusion still acquires the independent exclusive lock'
    CheckRefused { [void](Open-SyncSharkInstanceExclusion $stateRoot) } 'The independent exclusive lock still refuses a second holder'
    $independent.Stream.Dispose()

    # ---------------------------------------------------------------- SS-A09
    # The completion receipt, the fixed target, the installed executable identity
    # and the active-pointer binding are the production ones; Start-Process is the
    # test's launch seam, and the production bridge is the only caller of it.
    $fixtureRuntime=Resolve-RuntimeFixtureFile $RuntimeFixture
    $version='0.3.99';$revision=('a'*40)
    $productRoot=Join-Path $rootBase ([guid]::NewGuid().ToString('N'))
    if($null -ne $fixtureRuntime){
        $installedVersion=[string][Diagnostics.FileVersionInfo]::GetVersionInfo($fixtureRuntime).ProductVersion
        $parts=$installedVersion.Split('+')
        if($parts.Count -eq 2 -and $parts[0]){ $version=$parts[0];$revision=$parts[1] }
        else { $unverified.Add('SS-A09 real ProductVersion comparison: the fixture executable reports no version identity') }
    } else {
        $unverified.Add('SS-A09 real ProductVersion comparison: no runtime fixture executable resolved')
    }
    $target=Join-Path $productRoot ('versions\'+$version)
    [void][IO.Directory]::CreateDirectory($target)
    $executable=Join-Path $target 'SyncShark.exe'
    if($null -ne $fixtureRuntime){ Copy-Item -LiteralPath $fixtureRuntime -Destination $executable }
    else { [IO.File]::WriteAllBytes($executable,[Text.Encoding]::UTF8.GetBytes('inert runtime executable fixture')) }
    $exeHash=(Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    $executableBackup=Join-Path $fixtureParent ([guid]::NewGuid().ToString('N')+'.exe')
    Copy-Item -LiteralPath $executable -Destination $executableBackup
    $contract=[pscustomobject]@{version=$version;sourceRevision=$revision;signedExeSha256=$exeHash}
    $expectedRuntime=Join-Path $productRoot ('versions\'+$version)
    function Resolve-SyncSharkRuntimeRoot([string]$Version,[string]$TestProductRoot=''){ Join-Path $productRoot ('versions\'+$Version) }
    function Get-SyncSharkSignatureObservation([string]$Path){
        $publisher=(Get-SyncSharkSignerIdentity).roles.publisher
        [pscustomobject]@{Status='Valid';RawStatus='Valid';Thumbprint=@($publisher.thumbprints)[0];
            Subject=$publisher.subject;Issuer=$publisher.subject;
            SpkiSha256=@($publisher.spki)[0];
            SignerChainStatuses=@();Rfc3161Present=$true;Rfc3161CryptographicallyValid=$true;Rfc3161=$true;
            TimestampSignatureBound=$true;TimestampChainEvaluatedAtTokenTime=$true;TimestampChainValid=$true;
            TimestampUtc=$null;TimestampChainStatuses=@()}
    }
    $launches=[Collections.Generic.List[string]]::new()
    function Start-Process { param([Parameter(Mandatory=$true)][string]$FilePath,[string]$WindowStyle,[switch]$PassThru)
        $launches.Add([IO.Path]::GetFullPath($FilePath)) }
    $activePath=Join-Path $productRoot 'active'
    New-Item -ItemType Junction -Path $activePath -Target $target | Out-Null
    $packageRoot=Join-Path $fixtureParent ([guid]::NewGuid().ToString('N'))
    $packageScripts=Join-Path $packageRoot 'scripts'
    [void][IO.Directory]::CreateDirectory((Join-Path $packageRoot 'package\signed-candidate'))
    [void][IO.Directory]::CreateDirectory($packageScripts)
    $installerFixture=Join-Path $packageScripts 'Install-SyncShark.ps1'
    [IO.File]::WriteAllText($installerFixture,"throw 'Fixture installer MUST NEVER execute.'")
    [IO.File]::WriteAllText((Join-Path $packageScripts 'SyncShark.Release.psm1'),'# Inert module input; never imported.')
    [IO.File]::WriteAllText((Join-Path $packageRoot 'package\unsigned-candidate-framework-manifest.json'),'{}')
    [IO.File]::WriteAllText((Join-Path $packageRoot 'package\signed-candidate\SyncShark.Core.dll'),'inert package bytes')
    $receipt=[pscustomobject]@{ok=$true;operation='install';product_version=($version+'+'+$revision);active_target=$target}
    $lease=Open-SyncSharkUpdateInputLease $installerFixture
    try {
        $resolved=Assert-SyncSharkUpdateReceipt $receipt $contract $productRoot
        Check ((Get-SyncSharkNormalizedPath $resolved) -ceq (Get-SyncSharkNormalizedPath $expectedRuntime)) 'The exact completion receipt resolves the signed runtime target'
        $versionMatches=($null -ne $fixtureRuntime) -and
            ([string][Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion -ceq ($version+'+'+$revision))
        if($versionMatches){
            Start-SyncSharkVerifiedRuntime $productRoot $resolved $contract $lease
            Check ($launches.Count -eq 1 -and $launches[0] -ceq ($expectedRuntime+'\SyncShark.exe') -and $launches[0] -cne (Join-Path $productRoot 'active\SyncShark.exe')) 'Only the exact verified fixed-version target reaches the launch seam'
        } else {
            $refused=$false
            try { Start-SyncSharkVerifiedRuntime $productRoot $resolved $contract $lease } catch { $refused=$true }
            if(-not $refused -or $launches.Count -ne 0){throw 'A runtime executable without the signed version identity reached the launch seam.'}
            Check $true 'A runtime executable whose version does not match the signed release is refused before launch'
            $unverified.Add('SS-A09 correct-success launch identity: no fixture runtime executable carried the signed version')
        }
    } finally { $lease.Dispose() }
    function RefuseLaunch([string]$Label,[scriptblock]$Action){
        $before=$launches.Count
        $refused=$false
        try { & $Action } catch { $refused=$true }
        if(-not $refused){throw ('The bridge launch accepted '+$Label)}
        if($launches.Count -ne $before){throw ('A refused case launched: '+$Label)}
        Check $true $Label
    }
    $failLaunch={
        $candidate=Open-SyncSharkUpdateInputLease $installerFixture
        try { Start-SyncSharkVerifiedRuntime $productRoot $expectedRuntime $script:badContract $candidate }
        finally { $candidate.Dispose() }
    }
    $script:badContract=$contract
    RefuseLaunch 'A receipt for another product version is refused before launch' {
        Assert-SyncSharkUpdateReceipt ([pscustomobject]@{ok=$true;operation='install';product_version=($version+'9+'+$revision);active_target=$target}) $contract $productRoot }
    RefuseLaunch 'A receipt for another source commit is refused before launch' {
        Assert-SyncSharkUpdateReceipt ([pscustomobject]@{ok=$true;operation='install';product_version=($version+'+'+('b'*40));active_target=$target}) $contract $productRoot }
    RefuseLaunch 'A receipt for another active target is refused before launch' {
        Assert-SyncSharkUpdateReceipt ([pscustomobject]@{ok=$true;operation='install';product_version=($version+'+'+$revision);active_target=(Join-Path $productRoot 'versions\0.0.0')}) $contract $productRoot }
    RefuseLaunch 'A receipt that is not a completed install is refused before launch' {
        Assert-SyncSharkUpdateReceipt ([pscustomobject]@{ok=$false;operation='install';product_version=($version+'+'+$revision);active_target=$target}) $contract $productRoot }
    RefuseLaunch 'A receipt for another operation is refused before launch' {
        Assert-SyncSharkUpdateReceipt ([pscustomobject]@{ok=$true;operation='rollback';product_version=($version+'+'+$revision);active_target=$target}) $contract $productRoot }
    RefuseLaunch 'A receipt without its identity fields is refused before launch' {
        Assert-SyncSharkUpdateReceipt ([pscustomobject]@{ok=$true;operation='install'}) $contract $productRoot }
    RefuseLaunch 'No installer receipt is refused' { $null=Get-SyncSharkUpdateInstallerReceipt @() }
    RefuseLaunch 'Two installer receipts are refused' { $null=Get-SyncSharkUpdateInstallerReceipt @('{"ok":true}','{"ok":true}') }
    RefuseLaunch 'An unparsable installer receipt is refused' { $null=Get-SyncSharkUpdateInstallerReceipt @('not json') }
    RefuseLaunch 'A receipt array is refused' { $null=Get-SyncSharkUpdateInstallerReceipt @('[{"ok":true}]') }
    RefuseLaunch 'A signed release hash mismatch is refused before launch' {
        $script:badContract=[pscustomobject]@{version=$version;sourceRevision=$revision;signedExeSha256=('0'*64)}
        & $failLaunch }
    $script:badContract=$contract
    RefuseLaunch 'A substituted installed executable is refused before launch' {
        [IO.File]::AppendAllText($executable,'substituted')
        try { & $failLaunch } finally { Copy-Item -LiteralPath $executableBackup -Destination $executable -Force }
    }
    RefuseLaunch 'A substituted active pointer is refused before launch' {
        $other=Join-Path $productRoot 'versions\0.0.0'
        [void][IO.Directory]::CreateDirectory($other)
        [IO.Directory]::Delete($activePath)
        New-Item -ItemType Junction -Path $activePath -Target $other | Out-Null
        try { & $failLaunch }
        finally {
            [IO.Directory]::Delete($activePath)
            New-Item -ItemType Junction -Path $activePath -Target $target | Out-Null
        }
    }
    RefuseLaunch 'An old signed target is refused before launch' {
        $old=Join-Path $productRoot 'versions\0.0.1'
        [void][IO.Directory]::CreateDirectory($old)
        Copy-Item -LiteralPath $executableBackup -Destination (Join-Path $old 'SyncShark.exe')
        Assert-SyncSharkUpdateReceipt ([pscustomobject]@{ok=$true;operation='install';product_version=($version+'+'+$revision);active_target=$old}) $contract $productRoot }

    # ------------------------------------------------------- production wiring
    $installerText=[IO.File]::ReadAllText($installPath)
    $moduleText=[IO.File]::ReadAllText($modulePath)
    $updateText=[IO.File]::ReadAllText($updatePath)
    Check (([regex]::Matches($installerText,'Invoke-SyncSharkWithInstanceMaintenanceLease')).Count -eq 1) 'The installer source acquires exactly one instance-maintenance lease'
    Check ($installerText.IndexOf('Invoke-SyncSharkWithInstanceMaintenanceLease $productRoot $transactionBody') -gt 0) 'The installer source holds the lease across its transaction body'
    Check ($moduleText.IndexOf('Export-ModuleMember -Function Invoke-SyncSharkWithInstanceMaintenanceLease') -gt 0) 'The maintenance wrapper is exported for the installer'
    Check ($moduleText.IndexOf('Open-SyncSharkInstanceExclusion $root') -gt 0) 'Explorer registration acquires its exclusion through the lease-reuse helper'
    Check ($installerText.IndexOf('if ($testMode) { & $transactionBody }') -lt 0) 'The installer no longer exempts the isolated test-mode path from the maintenance lease'
    $guardsText=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Test-SyncSharkReleaseGuards.ps1'))
    $transactionsText=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Test-SyncSharkReleaseTransactions.ps1'))
    Check ($guardsText.IndexOf("state\instance.lock") -gt 0 -and $transactionsText.IndexOf("state\instance.lock") -gt 0) 'Both isolated installer fixtures supply the application state lock'
    $companionIndex=$updateText.IndexOf('Assert-SyncSharkUpdateProtectedCompanion $inputs')
    $readyIndex=$updateText.IndexOf('{"ready":true}')
    $runtimeCallIndex=$updateText.IndexOf('Start-SyncSharkVerifiedRuntime $root $target $contract $inputs')
    Check ($companionIndex -gt 0 -and $companionIndex -lt $readyIndex -and $readyIndex -lt $runtimeCallIndex) 'The companion hold precedes the ready handoff and the launch call in the bridge'
    Check (([regex]::Matches($updateText,'Start-Process -FilePath \$executable -WindowStyle Hidden')).Count -eq 1) 'The bridge creates the process at exactly one place'
    Check ($updateText.IndexOf("'active\SyncShark.exe'") -lt 0) 'The bridge no longer launches through the mutable active link'

    [ordered]@{
        ok=$true
        checks=@($checks)
        scope=[ordered]@{
            module_source_sha256=(Get-FileHash -LiteralPath $modulePath -Algorithm SHA256).Hash.ToLowerInvariant()
            update_source_sha256=(Get-FileHash -LiteralPath $updatePath -Algorithm SHA256).Hash.ToLowerInvariant()
            installer_source_sha256=(Get-FileHash -LiteralPath $installPath -Algorithm SHA256).Hash.ToLowerInvariant()
            isolated_state_root=$stateRoot
            launch_fixture_root=$productRoot
            runtime_fixture=if($null -ne $fixtureRuntime){$fixtureRuntime}else{'absent'}
        }
        sqlite_fixture=if($null -ne $sqlite){'Microsoft.Data.Sqlite WAL (main + -wal bytes)'}else{'unavailable'}
        installed_effects=$false
        privileged_worker_executed=$false
        unverified=@($unverified)
    } | ConvertTo-Json -Depth 6
}
finally {
    if($null -ne $sqlite){$sqlite.Dispose()}
    if($executableBackup -and (Test-Path -LiteralPath $executableBackup -PathType Leaf)){Remove-Item -LiteralPath $executableBackup -Force}
    Remove-Item Env:SYNC_SHARK_RELEASE_TEST_MODE -ErrorAction SilentlyContinue
    foreach($root in @($stateRoot,$missingRoot,$productRoot,$packageRoot)) {
        if(-not $root -or -not (Test-Path -LiteralPath $root)){continue}
        $junction=Join-Path $root 'active'
        if(Test-Path -LiteralPath $junction){[IO.Directory]::Delete($junction)}
        Remove-Item -LiteralPath $root -Recurse -Force
        if(Test-Path -LiteralPath $root){throw ('Disposable release-recovery fixture remains: '+$root)}
    }
}
