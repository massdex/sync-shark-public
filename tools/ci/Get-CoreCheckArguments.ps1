# The one owner of a Core.Tests invocation's arguments: the switch, the fixture
# root for suites that take one, and any trailing arguments. The shard matrix,
# the quick job and the lane selection all call this, so a suite is never
# invoked differently on another path.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Flag,
    [Parameter(Mandatory)][string]$Root
)

$ErrorActionPreference = 'Stop'
if (-not $env:SUITE_ROOT_ARGS) { throw 'SUITE_ROOT_ARGS is missing from the workflow environment.' }
$rootSuites = @($env:SUITE_ROOT_ARGS -split '\s+' | Where-Object { $_ })
$arguments = @($Flag)
# A suite takes the fixture root as its next argument only when it is listed.
if ($rootSuites -ccontains $Flag) { $arguments += $Root }
# A few suites take a case selector after the root (e.g. slowdelete).
if ($env:SUITE_EXTRA_ARGS) {
    $extraArgs = $env:SUITE_EXTRA_ARGS | ConvertFrom-Json
    $extra = $extraArgs.PSObject.Properties[$Flag]
    if ($null -ne $extra) { $arguments += @($extra.Value) }
}
$arguments
