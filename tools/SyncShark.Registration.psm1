# Requires Windows PowerShell 5.1 or later on Windows.
function Invoke-SyncSharkAppRegistration {
    param([string]$Version, [ValidateSet('Register','Remove','Verify','VerifyAbsent')][string]$Operation)
    throw [System.NotImplementedException]::new()
}
Export-ModuleMember -Function Invoke-SyncSharkAppRegistration
