param(
 [Parameter(Mandatory=$true)][ValidatePattern('^0\.3\.\d+$')][string]$Version,
 [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExplorerSha256,
 [Parameter(Mandatory=$true)][string]$SignedInstaller
)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run the protection proof as the ordinary user.'}
$root='C:\Program Files\Massdex\SyncShark'
$runtime=Join-Path $root ('versions\'+$Version)
$dll=Join-Path $runtime 'SyncShark.Explorer.dll'
if((Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant() -cne $ExplorerSha256){throw 'Pinned installed DLL changed.'}
$signature=Get-AuthenticodeSignature -LiteralPath $dll
$publisherSigner=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SyncShark.SignerIdentity.json') -Raw|ConvertFrom-Json).roles.publisher
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -cnotin @($publisherSigner.thumbprints)){throw 'Installed signature differs from the approved product.'}
$installerSignature=Get-AuthenticodeSignature -LiteralPath $SignedInstaller
if($installerSignature.Status -ne 'Valid' -or $installerSignature.SignerCertificate.Thumbprint -cnotin @($publisherSigner.thumbprints) -or $null -eq $installerSignature.TimeStamperCertificate){throw 'The runtime contract is not the exact timestamped product signer.'}
$reader=[IO.File]::OpenText($SignedInstaller)
try{$header=$reader.ReadLine()}finally{$reader.Dispose()}
$prefix='# SyncSharkUpdateContract: '
if(-not $header.StartsWith($prefix,[StringComparison]::Ordinal) -or $header.Length -gt 16384){throw 'The signed runtime contract is missing.'}
$contract=$header.Substring($prefix.Length)|ConvertFrom-Json
if($contract.version -cne $Version -or $contract.protectedRuntimeRoot -cne $runtime -or $contract.product -cne 'Massdex.SyncShark'){throw 'The signed contract identifies another runtime.'}
$expectedMembers=@('Assets/SyncShark-64.png','Microsoft.Data.Sqlite.dll','Microsoft.Windows.SDK.NET.dll','SQLitePCLRaw.batteries_v2.dll','SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.e_sqlite3.dll','SyncShark.Core.dll','SyncShark.Explorer.dll','SyncShark.deps.json','SyncShark.dll','SyncShark.exe','SyncShark.msix','SyncShark.runtimeconfig.json','SyncSharkDiscovery.exe','WinRT.Runtime.dll','e_sqlite3.dll')
$members=@{}
foreach($member in $contract.runtimeMembers){
 if($member.path -cnotin $expectedMembers -or $members.ContainsKey($member.path)){throw 'The signed contract has a foreign or duplicate member.'}
 $path=Join-Path $runtime $member.path
 if((Get-Item -LiteralPath $path).Length -ne $member.bytes -or (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $member.sha256){throw 'An installed member differs from its signed contract.'}
 $members[$member.path]=$member
}
if($members.Count -ne $expectedMembers.Count){throw 'The signed runtime member set is incomplete.'}
$active=Get-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'Massdex\SyncShark\active') -Force
if($active.LinkType -cne 'Junction' -or [string]$active.Target -ine $runtime){throw 'The active installed root differs from the signed runtime.'}
$package=@(Get-AppxPackage -Name Massdex.SyncShark)
if($package.Count -ne 1 -or [string]$package[0].Version -cne ($Version+'.0') -or $package[0].PackageFamilyName -cne 'Massdex.SyncShark_gsfr2ypy2ejyg' -or [int]$package[0].Status -ne 0){throw 'The exact current sparse package is not healthy.'}
function Assert-SyncSharkColdExplorerLoad([object[]]$Before,[object[]]$After,[string]$ExpectedPath) {
 if($Before.Count){throw 'Cold activation requires no pre-existing Sync Shark Explorer module.'}
 if($After.Count -ne 1 -or $After[0].module_path -ine $ExpectedPath){throw 'Cold activation did not produce exactly the expected Explorer module.'}
 return $After[0]
}
function Get-SyncSharkExplorerHosts {
 foreach($process in @(Get-Process -Name dllhost -ErrorAction SilentlyContinue)) {
  $modules=@($process.Modules)
  foreach($module in @($modules|Where-Object {$_.ModuleName -ieq 'SyncShark.Explorer.dll'})) {
   [pscustomobject]@{pid=$process.Id;module_path=$module.FileName;modules=$modules}
  }
 }
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class SyncSharkProtectedLoadProbe {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
 [DllImport("ole32.dll")] public static extern int CoCreateInstance(ref Guid clsid,IntPtr outer,uint context,ref Guid iid,out IntPtr result);
}
'@
$checks=[Collections.Generic.List[object]]::new()
function Denied([string]$Path,[uint32]$Access,[bool]$Directory,[string]$Label) {
 $handle=[SyncSharkProtectedLoadProbe]::CreateFileW($Path,$Access,7,[IntPtr]::Zero,3,$(if($Directory){0x02200000}else{0x00200000}),[IntPtr]::Zero)
 $errorCode=[Runtime.InteropServices.Marshal]::GetLastWin32Error()
 try {if(-not $handle.IsInvalid -or $errorCode -ne 5){throw ('Expected access denied: '+$Label+'; error '+$errorCode)}} finally {$handle.Dispose()}
 $checks.Add([ordered]@{path=$Path;operation=$Label;desired_access=$Access;windows_error=$errorCode})
}
$directories=@('C:\Program Files\Massdex',$root,(Join-Path $root 'versions'),$runtime,(Join-Path $runtime 'Assets'))
foreach($path in $directories) {
 if((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Protected directory is a reparse point.'}
 foreach($right in @(@(2,'Create or Replace Child'),@(4,'Create Subdirectory'),@(64,'Rename or Delete Child'),@(65536,'Rename or Delete Directory'),@(262144,'Change DACL'),@(524288,'Take Ownership'))) {
  Denied $path $right[0] $true $right[1]
 }
}
$files=@(Get-ChildItem -LiteralPath $runtime -File -Recurse)
if($files.Count -ne 16){throw 'The protected runtime member set changed.'}
foreach($file in $files) {
 if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Protected member is a reparse point.'}
 foreach($right in @(@(2,'Replace Bytes'),@(4,'Append Bytes'),@(65536,'Rename or Delete File'),@(262144,'Change DACL'),@(524288,'Take Ownership'))) {
  Denied $file.FullName $right[0] $false $right[1]
 }
}
$before=@(Get-SyncSharkExplorerHosts)
if($before.Count){throw 'Cold activation is not available while a Sync Shark Explorer module is already loaded; no process was stopped.'}
$clsid=[guid]'B2251D9A-7803-4F1F-8FE8-CBDA284BD5E9';$iid=[guid]'A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9';$pointer=[IntPtr]::Zero
$activationStarted=[DateTime]::UtcNow.ToString('o')
$result=[SyncSharkProtectedLoadProbe]::CoCreateInstance([ref]$clsid,[IntPtr]::Zero,4,[ref]$iid,[ref]$pointer)
try {
 if($result -ne 0 -or $pointer -eq [IntPtr]::Zero){throw ('Actual Explorer COM load failed: '+$result)}
 $loaded=Assert-SyncSharkColdExplorerLoad $before @(Get-SyncSharkExplorerHosts) $dll
 $private=@($loaded.modules|Where-Object {-not $_.FileName.StartsWith($env:WINDIR+'\',[StringComparison]::OrdinalIgnoreCase)})
 $privateEvidence=@(foreach($module in $private){
  if(-not $module.FileName.StartsWith($runtime+'\',[StringComparison]::OrdinalIgnoreCase)){throw ('Unexpected non-Windows surrogate dependency: '+$module.ModuleName)}
  $relative=$module.FileName.Substring($runtime.Length+1).Replace('\','/')
  $hash=(Get-FileHash -LiteralPath $module.FileName).Hash.ToLowerInvariant()
  if(-not $members.ContainsKey($relative) -or $members[$relative].sha256 -cne $hash){throw 'A loaded private module is absent from the exact signed contract.'}
  [ordered]@{path=$module.FileName;sha256=$hash}
 })
 if((Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant() -cne $ExplorerSha256){throw 'Loaded DLL changed during proof.'}
 [ordered]@{observed_at_utc=[DateTime]::UtcNow.ToString('o');version=$Version;ordinary_token=$true;denied_checks=$checks.ToArray();signed_contract_sha256=(Get-FileHash -LiteralPath $SignedInstaller).Hash.ToLowerInvariant();runtime_members=$contract.runtimeMembers;package=$package[0].PackageFullName;active_root=[string]$active.Target;actual_com_load=@{pid=$loaded.pid;module_path=$loaded.module_path;sha256=$ExplorerSha256;private_modules=$privateEvidence;windows_module_count=$loaded.modules.Count-$private.Count;activation_started_utc=$activationStarted;pre_existing_modules=0;causality='cold named-module baseline, one new exact module after successful activation'};files_mutated=$false;commands_invoked=$false;contains_secrets=$false}|ConvertTo-Json -Depth 6
} finally {if($pointer -ne [IntPtr]::Zero){[void][Runtime.InteropServices.Marshal]::Release($pointer)}}
