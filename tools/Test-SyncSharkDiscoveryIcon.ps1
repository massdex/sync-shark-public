param([Parameter(Mandatory=$true)][string]$Path,
    [Parameter(Mandatory=$true)][string]$ExpectedProductVersion)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$file=Get-Item -LiteralPath $Path
if($file.Name -cne 'SyncSharkDiscovery.exe' -or $file.Length -gt 4MB -or
    ($file.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline -bor [IO.FileAttributes]::Directory))){throw 'Expected one bounded ordinary Discovery executable.'}
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DiscoveryIconProof {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]public static extern IntPtr LoadLibraryEx(string file,IntPtr reserved,uint flags);
 [DllImport("kernel32.dll",SetLastError=true)]public static extern IntPtr FindResource(IntPtr module,IntPtr name,IntPtr type);
 [DllImport("kernel32.dll")]public static extern uint SizeofResource(IntPtr module,IntPtr resource);
 [DllImport("kernel32.dll")]public static extern IntPtr LoadResource(IntPtr module,IntPtr resource);
 [DllImport("kernel32.dll")]public static extern IntPtr LockResource(IntPtr resource);
 [DllImport("kernel32.dll")]public static extern bool FreeLibrary(IntPtr module);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]public static extern IntPtr LoadImage(IntPtr module,IntPtr name,uint type,int width,int height,uint flags);
 [DllImport("user32.dll")]public static extern bool DestroyIcon(IntPtr icon);
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]public static extern uint ExtractIconEx(string file,int index,IntPtr[] large,IntPtr[] small,uint count);
}
'@
$module=[DiscoveryIconProof]::LoadLibraryEx($file.FullName,[IntPtr]::Zero,0x22)
if($module -eq [IntPtr]::Zero){throw 'Read-only PE resource load failed.'}
$passed=0
function Resource([int]$Type,[int]$Id) {
 $resource=[DiscoveryIconProof]::FindResource($module,[IntPtr]$Id,[IntPtr]$Type)
 if($resource -eq [IntPtr]::Zero){throw ('Missing PE resource '+$Type+'/'+$Id)}
 $length=[DiscoveryIconProof]::SizeofResource($module,$resource)
 if($length -lt 1 -or $length -gt 1MB){throw 'Resource exceeds bounded size'}
 $pointer=[DiscoveryIconProof]::LockResource([DiscoveryIconProof]::LoadResource($module,$resource))
 if($pointer -eq [IntPtr]::Zero){throw 'Resource unavailable'}
 $bytes=[byte[]]::new($length);[Runtime.InteropServices.Marshal]::Copy($pointer,$bytes,0,$bytes.Length)
 return ,$bytes
}
function VisibleShark([IntPtr]$Handle,[int]$Size) {
 if($Handle -eq [IntPtr]::Zero){throw 'Windows returned no icon'}
 $icon=[Drawing.Icon]::FromHandle($Handle);$bitmap=$icon.ToBitmap()
 try {
  if($bitmap.Width -ne $Size -or $bitmap.Height -ne $Size){throw 'Windows chose an unexpected icon size'}
  $blue=0
  for($y=0;$y -lt $Size;$y++){for($x=0;$x -lt $Size;$x++){$p=$bitmap.GetPixel($x,$y);if($p.A -gt 0 -and $p.B -gt $p.R+30){$blue++}}}
  if($blue -lt $Size){throw 'Extracted icon is empty or not the blue product mark'}
 }finally{$bitmap.Dispose();$icon.Dispose()}
}
try {
 $group=Resource 14 1
 if($group.Length -ne 118 -or [BitConverter]::ToUInt16($group,4) -ne 8){throw 'Expected complete eight-size native icon group'}
 $sizes=@(16,20,24,32,48,64,128,256)
 for($index=0;$index -lt $sizes.Count;$index++) {
  $size=$sizes[$index];$entry=6+14*$index;$id=[BitConverter]::ToUInt16($group,$entry+12)
  $image=Resource 3 $id
  if($size -lt 256) {
   if([BitConverter]::ToUInt32($image,0) -ne 40 -or [BitConverter]::ToInt32($image,4) -ne $size -or
      [BitConverter]::ToInt32($image,8) -ne 2*$size -or [BitConverter]::ToUInt16($image,14) -ne 32){throw 'Small native icon is not a standard32-bit DIB'}
  }elseif([BitConverter]::ToString($image,0,8) -cne '89-50-4E-47-0D-0A-1A-0A'){throw 'High-DPI icon is not PNG'}
  $handle=[DiscoveryIconProof]::LoadImage($module,[IntPtr]1,1,$size,$size,0)
  try {VisibleShark $handle $size}finally{if($handle -ne [IntPtr]::Zero){[void][DiscoveryIconProof]::DestroyIcon($handle)}}
  $passed++;'PASS real Windows resource icon '+$size+'px'
 }
 $manifest=[Text.Encoding]::UTF8.GetString((Resource 24 1)).Trim([char]0)
 [xml]$xml=$manifest
 $node=$xml.SelectSingleNode('//*[local-name()="requestedExecutionLevel"]')
 if($null -eq $node -or $node.level -cne 'asInvoker' -or $node.uiAccess -cne 'false'){throw 'Helper manifest changes elevation/UI-access policy'}
 $passed++;'PASS native helper remains asInvoker; elevation occurs only through explicit runas'
 $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName)
 if($version.FileDescription -cne 'Sync Shark' -or $version.CompanyName -cne 'Massdex' -or
    $version.OriginalFilename -cne 'SyncSharkDiscovery.exe' -or $version.ProductVersion -cne $ExpectedProductVersion){throw 'Native helper metadata is missing or not bound to this candidate'}
 $passed++;'PASS friendly product metadata and exact version/source binding'
 $large=[IntPtr[]]::new(1);$small=[IntPtr[]]::new(1)
 try {
  if([DiscoveryIconProof]::ExtractIconEx($file.FullName,0,$large,$small,1) -lt 1){throw 'Windows shell cannot extract the product icon'}
  VisibleShark $large[0] 32;VisibleShark $small[0] 16
  $passed++;'PASS actual shell extracts both large and small shark icons'
 }finally{foreach($handle in @($large[0],$small[0])){if($handle -ne [IntPtr]::Zero){[void][DiscoveryIconProof]::DestroyIcon($handle)}}}
}finally{[void][DiscoveryIconProof]::FreeLibrary($module)}
[ordered]@{checks=$passed;path=$file.FullName;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant();native_execution=$false;uac_prompt_verified=$false}|ConvertTo-Json -Compress
