param([Parameter(Mandatory=$true)][string]$Package,[Parameter(Mandatory=$true)][string]$Installer,
    [Parameter(Mandatory=$true)][string]$BuildRoot,
    [string]$Allocation='')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell5, the actual setup host.'}
$allocation=if([string]::IsNullOrWhiteSpace($Allocation)){Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SyncShark\build\home-activity-tray'}else{[IO.Path]::GetFullPath($Allocation).TrimEnd('\')}
if([IO.Path]::GetFullPath($BuildRoot).TrimEnd('\') -ine $allocation){throw 'Wrong owner build allocation'}
$assembly=Join-Path $BuildRoot 'obj\SyncShark\native\DiscoverySetup.dll'
Add-Type -Path $assembly
$source=Join-Path $PSScriptRoot 'SyncShark.DiscoverySetup.ps1'
. $source
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Source parsing failed'}
$entry=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-SyncSharkProtectedRuntimeSetup'},$true)
foreach($name in @('HoldRuntime','HashRuntime','SignedRuntime')){
 $definition=$entry.Body.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true)
 . ([scriptblock]::Create($definition.Extent.Text))
}
$body=$entry.Body.EndBlock.Statements
$appAt=@($body|Where-Object {$_.Extent.Text.StartsWith('$app=')})[0].Extent.StartOffset
$parentAt=@($body|Where-Object {$_.Extent.Text.StartsWith('$parent=')})[0].Extent.StartOffset
$pathAdmission=[scriptblock]::Create((@($body|Where-Object {$_.Extent.StartOffset -ge $appAt -and $_.Extent.StartOffset -lt $parentAt}).Extent.Text -join "`n"))
$sourceTry=$entry.Body.Find({param($n)$n -is [Management.Automation.Language.TryStatementAst] -and $n.Body.Extent.Text.Contains('$claim=[SyncSharkDiscoveryTransaction]::new(')},$true)
$parentRefresh=@($sourceTry.Body.Statements|Where-Object {$_.Extent.Text.StartsWith('$parent.Refresh()')})[0].Extent.StartOffset
$sourceAdmission=[scriptblock]::Create((@($sourceTry.Body.Statements|Where-Object {$_.Extent.StartOffset -lt $parentRefresh}).Extent.Text -join "`n"))
if($sourceAdmission.ToString().Contains('$claim=') -or !$sourceAdmission.ToString().Contains("throw 'A signed runtime source member changed.'")){throw 'Wrong production pre-effect boundary'}
$script:checks=0;$script:effectBoundary=0
function Check([bool]$Good,[string]$Name){if(!$Good){throw $Name};$script:checks++;'PASS '+$Name}
function Admit([string]$Product,[string]$AppDirectory){
 $held=[Collections.Generic.List[IDisposable]]::new();$medium=[IntPtr]::Zero
 try{. $pathAdmission; . $sourceAdmission; $script:effectBoundary++}
 finally{foreach($handle in $held){$handle.Dispose()};if($medium -ne [IntPtr]::Zero){[SyncSharkDiscoverySetupNative]::CloseToken($medium)}}
}
function Rejected([string]$Product,[string]$AppDirectory,[string]$Label){
 $before=$script:effectBoundary;$failed=$false
 try{Admit $Product $AppDirectory}catch{$failed=$true}
 Check ($failed -and $script:effectBoundary -eq $before) ($Label+' rejects before the claim/task/package boundary')
}
$fixture=Join-Path $BuildRoot ('runtime-admission-'+[guid]::NewGuid().ToString('N'))
$product=Join-Path $fixture 'product'
$productVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Package 'SyncSharkDiscovery.exe')).ProductVersion
if($productVersion -cnotmatch '^(\d+\.\d+\.\d+)\+([0-9a-f]{40})$'){throw 'Unbound signed fixture source'}
$version=$Matches[1];$commit=$Matches[2]
$app=Join-Path $product ('versions\'+$version)
$transaction=Join-Path $product ('replacement-staging\transactions\'+$version+'-'+$commit.Substring(0,7))
try{
 [IO.Directory]::CreateDirectory($app)|Out-Null;[IO.Directory]::CreateDirectory($transaction)|Out-Null
 foreach($name in [SyncSharkProtectedRuntimeTransaction]::Members){
  $to=Join-Path $app $name;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))|Out-Null
  [IO.File]::Copy((Join-Path $Package $name),$to)
 }
 [IO.File]::Copy($Installer,(Join-Path $transaction 'Install-SyncShark.ps1'))
 Admit $product $app;Check ($script:effectBoundary -eq 1) 'Exact signed source reaches the post-admission marker through actual held inventory/hash/signature decisions'
 $private=Join-Path $app 'WinRT.Runtime.dll';$bytes=[IO.File]::ReadAllBytes($private);$bytes[0]=$bytes[0] -bxor 1
 [IO.File]::WriteAllBytes($private,$bytes);Rejected $product $app 'Same-length changed private dependency'
 [IO.File]::Copy((Join-Path $Package 'WinRT.Runtime.dll'),$private,$true)
 $extra=Join-Path $app 'unexpected.txt';[IO.File]::WriteAllText($extra,'Inert extra fixture member')
 Rejected $product $app 'Unexpected source member';[IO.File]::Delete($extra)
 $extra=Join-Path $app 'Assets\unexpected.png';[IO.File]::WriteAllText($extra,'Inert extra fixture asset')
 Rejected $product $app 'Unexpected source asset';[IO.File]::Delete($extra)
 Rejected $product ($app+'-other') 'Unbound source directory'
 $aliasProduct=Join-Path $fixture 'alias';$alias=Join-Path $aliasProduct ('versions\'+$version)
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($alias))|Out-Null
 New-Item -ItemType Junction -Path $alias -Target $app|Out-Null
 try{Rejected $aliasProduct $alias 'Reparse-bound source root'}finally{
  if([IO.Path]::GetFullPath($alias).StartsWith($fixture+'\',[StringComparison]::OrdinalIgnoreCase) -and
      (Get-Item -LiteralPath $alias).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){
   [IO.Directory]::Delete($alias) # Delete the known fixture junction itself, never its target recursively.
  }else{throw 'Unexpected fixture junction cleanup target'}
 }
 Admit $product $app;Check ($script:effectBoundary -eq 2) 'Restored exact source remains admissible after every negative case'
 [ordered]@{checks=$checks;source_sha256=(Get-FileHash $source).Hash.ToLowerInvariant();assembly_sha256=(Get-FileHash $assembly).Hash.ToLowerInvariant();installer_sha256=(Get-FileHash $Installer).Hash.ToLowerInvariant();product_version=$productVersion;actual_member_admission=$true;marker_count=$script:effectBoundary;privileged_effects=$false;task_effects=$false;package_effects=$false}|ConvertTo-Json -Compress
}finally{
 if((Test-Path -LiteralPath $fixture) -and [IO.Path]::GetFullPath($fixture).StartsWith($allocation+'\',[StringComparison]::OrdinalIgnoreCase) -and !(Get-Item $fixture).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){
  Remove-Item -LiteralPath $fixture -Recurse -Force
 }
}
