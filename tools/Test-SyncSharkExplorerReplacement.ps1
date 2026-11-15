$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$path=Join-Path $PSScriptRoot 'SyncShark.Release.psm1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Production module parse failed'}
$definition=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Assert-SyncSharkExplorerReplacement'},$true)
. ([scriptblock]::Create($definition.Extent.Text))
# Input and read-only external verifier adapters only. The unchanged production
# admission function remains the only path to the fake owned-package effect.
function Get-SyncSharkNormalizedPath([string]$Path){[IO.Path]::GetFullPath($Path).TrimEnd('\')}
function Resolve-SyncSharkRuntimeRoot([string]$Version){'C:\Program Files\Massdex\SyncShark\versions\'+$Version}
$script:verified=[Collections.Generic.List[string]]::new();$script:validExternal=$true;$script:effects=0;$checks=0
function Invoke-SyncSharkExplorerPackage([string]$Runtime,[string]$Operation){
 if($Operation -cne 'Verify'){throw 'No live package effects allowed'}
 $script:verified.Add($Runtime)
 if(!$script:validExternal){throw 'The actual external-root readback did not match'}
}
function Check([bool]$Pass,[string]$Label){if(!$Pass){throw $Label};$script:checks++;'PASS '+$Label}
$next=Resolve-SyncSharkRuntimeRoot '0.3.34';$previous=Resolve-SyncSharkRuntimeRoot '0.3.33'
$old=[pscustomobject]@{Version='0.3.33.0';Publisher='CN=Massdex Sync Shark Code Signing v1, O=Massdex'}
function Apply([object[]]$Observed,[string]$Previous){Assert-SyncSharkExplorerReplacement $Observed $next $Previous;$script:effects++}
Apply @() '';Check ($script:effects -eq 1 -and $script:verified.Count -eq 0) 'Absent registration permits a new exact package'
Apply @($old) $previous;Check ($script:effects -eq 2 -and $script:verified[0] -ceq $previous) 'Explicit exact predecessor is externally verified before upgrade'
foreach($case in @(
 @{observed=@($old);previous='';valid=$true},
 @{observed=@($old);previous=$previous;valid=$false},
 @{observed=@($old);previous=($previous+'-wrong');valid=$true},
 @{observed=@([pscustomobject]@{Version='0.3.34.0';Publisher=$old.Publisher});previous=$previous;valid=$true},
 @{observed=@([pscustomobject]@{Version='0.3.33.0';Publisher='CN=Other'});previous=$previous;valid=$true},
 @{observed=@($old,$old);previous=$previous;valid=$true}
)){
 $script:validExternal=$case.valid;$before=$script:effects;$rejected=$false
 try{Apply $case.observed $case.previous}catch{$rejected=$true}
 Check ($rejected -and $script:effects -eq $before) 'Missing predecessor, wrong root/version/publisher or duplicate rejects before Add'
}
[ordered]@{checks=$checks;source_sha256=(Get-FileHash $path).Hash.ToLowerInvariant();production_admission=$true;actual_os_package_effects=$false}|ConvertTo-Json -Compress
