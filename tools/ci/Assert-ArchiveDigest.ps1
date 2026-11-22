# Fail closed when an archive pin is unset or the downloaded bytes do not match it.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][AllowEmptyString()][string]$Expected,
    [Parameter(Mandatory)][string]$Name
)
$actual = (Get-FileHash -Algorithm SHA256 -Path $Path).Hash
if (-not $Expected -or -not [string]::Equals($actual, $Expected, [StringComparison]::OrdinalIgnoreCase)) {
    throw "$Name archive checksum mismatch or missing pin: $actual"
}
