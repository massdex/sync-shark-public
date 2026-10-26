# Embedded inside the fixed signed updater; never dot-sourced from staging.
function Invoke-SyncSharkProtectedUpdate {
    param([int]$BootstrapProcessId)
    $ErrorActionPreference='Stop'
    $session=$null
    try {
        $session=[SyncSharkUpdaterSession]::Prepare($BootstrapProcessId)
        $session.PublishRuntime()
        if($session.NeedsDiscoveryRebind) {
            $session.BeginDiscoveryRebind()
            Invoke-SyncSharkDiscoverySetup -Mode Configure -ParentProcessId $BootstrapProcessId `
                -AppDirectory $session.Destination -RuntimeReleaseValidated -UpdaterSession $session
        }
        $session.VerifyDiscoveryPostimage()
        $session.WritePublicationReceipt()
    } finally {if($null -ne $session){$session.Dispose()}}
}
