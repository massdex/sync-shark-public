$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$path=Join-Path $PSScriptRoot 'Test-SyncSharkProtectedLoad.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Protected-load proof parsing failed'}
$definition=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-SyncSharkColdExplorerLoad'},$true)
if($null -eq $definition){throw 'Actual cold-load classifier is absent'}
. ([scriptblock]::Create($definition.Extent.Text))
$expected='C:\Fixture\Protected\SyncShark.Explorer.dll'
$good=[pscustomobject]@{pid=123;module_path=$expected}
$foreign=[pscustomobject]@{pid=456;module_path='C:\Fixture\Other\SyncShark.Explorer.dll'}
$accepted=Assert-SyncSharkColdExplorerLoad @() @($good) $expected
if($accepted.pid -ne 123){throw 'The cold exact observation was not accepted'}
$checks=1
foreach($case in @(@{before=@($good);after=@($good)},@{before=@($foreign);after=@($good)},
    @{before=@();after=@($foreign)},@{before=@();after=@($good,$foreign)},@{before=@();after=@()})){
    $rejected=$false
    try{Assert-SyncSharkColdExplorerLoad $case.before $case.after $expected|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'A stale, wrong, ambiguous or missing module satisfied the cold-load classifier'}
    $checks++
}
"PASS $checks actual cold-load classifier controls; harmless observed-data inputs only, no COM or registration effect."
