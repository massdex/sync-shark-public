# Pure dry-run assertions only. Never call Set-SyncSharkInstalledJournalCapacity, fsutil, or elevation.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'SyncShark.DiscoverySetup.ps1')
$plan=Get-SyncSharkJournalSizing -ExistingMaximum 32MB -ExistingDelta 8MB
if($plan.Maximum -ne 1GB -or $plan.AllocationDelta -ne 512MB -or -not $plan.Change){throw 'Default journal sizing differs.'}
if(($plan.Arguments -join ' ') -cne 'usn createjournal m=1073741824 a=536870912'){throw 'Dry-run command differs.'}
$large=Get-SyncSharkJournalSizing -ExistingMaximum 4GB -ExistingDelta 2GB
if($large.Maximum -ne 4GB -or $large.AllocationDelta -ne 2GB -or $large.Change){throw 'Larger existing values would be reduced.'}
$mixed=Get-SyncSharkJournalSizing -ExistingMaximum 4GB -ExistingDelta 8MB
if($mixed.Maximum -ne 4GB -or $mixed.AllocationDelta -ne 512MB -or -not $mixed.Change){throw 'Independent larger value not preserved.'}
$custom=Get-SyncSharkJournalSizing -ExistingMaximum 32MB -ExistingDelta 8MB -JournalMaximumBytes 2GB -JournalAllocationBytes 1GB
if($custom.Maximum -ne 2GB -or $custom.AllocationDelta -ne 1GB){throw 'Installer override ignored.'}
$tokens=$null;$errors=$null
[void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'SyncShark.DiscoverySetup.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw ($errors|Out-String)}
'PASS installer journal defaults, preserve-larger values, overrides and dry-run command; no journal queried or changed'
