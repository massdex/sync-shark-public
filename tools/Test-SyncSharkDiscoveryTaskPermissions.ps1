param()
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -ne 5){throw 'Use the actual Windows PowerShell setup host.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'This check must remain unelevated.'}
. (Join-Path $PSScriptRoot 'SyncShark.DiscoverySetup.ps1')
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DiscoveryTaskPermissionProof {
 [StructLayout(LayoutKind.Sequential)] public struct Mapping {public uint Read,Write,Execute,All;}
 [DllImport("kernel32.dll")]public static extern IntPtr GetCurrentProcess();
 [DllImport("kernel32.dll")]public static extern bool CloseHandle(IntPtr handle);
 [DllImport("kernel32.dll")]public static extern IntPtr LocalFree(IntPtr handle);
 [DllImport("advapi32.dll",SetLastError=true)]public static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
 [DllImport("advapi32.dll",SetLastError=true)]public static extern bool GetTokenInformation(IntPtr token,int kind,out IntPtr linked,int size,out int returned);
 [DllImport("advapi32.dll",SetLastError=true)]public static extern bool DuplicateToken(IntPtr token,int level,out IntPtr duplicate);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl,uint revision,out IntPtr descriptor,out uint size);
 [DllImport("advapi32.dll",SetLastError=true)]public static extern bool CreatePrivateObjectSecurityEx(IntPtr parent,IntPtr creator,out IntPtr descriptor,IntPtr type,bool container,uint flags,IntPtr token,ref Mapping mapping);
 [DllImport("advapi32.dll",SetLastError=true)]public static extern bool DestroyPrivateObjectSecurity(ref IntPtr descriptor);
 [DllImport("advapi32.dll",SetLastError=true)]public static extern bool AccessCheck(IntPtr descriptor,IntPtr token,uint requested,ref Mapping mapping,byte[] privileges,ref uint length,out uint granted,out bool allowed);
}
'@
$token=[IntPtr]::Zero;$linked=[IntPtr]::Zero;$medium=[IntPtr]::Zero;$returned=0;$passed=0
if(-not [DiscoveryTaskPermissionProof]::OpenProcessToken([DiscoveryTaskPermissionProof]::GetCurrentProcess(),10,[ref]$token)){throw 'Read-only token open failed'}
try {
 if(-not [DiscoveryTaskPermissionProof]::GetTokenInformation($token,19,[ref]$linked,[IntPtr]::Size,[ref]$returned)){throw 'Read-only linked-token query failed'}
 if(-not [DiscoveryTaskPermissionProof]::DuplicateToken($token,2,[ref]$medium)){throw 'Medium token query copy failed'}
 $production=Get-SyncSharkDiscoveryTaskSddl $identity.User.Value
 foreach($case in @(@{sddl=$production.Replace('O:BA','O:SY');success=$false;name='Unassignable SYSTEM owner'},@{sddl=$production;success=$true;name='Production Administrators owner'})) {
  $creator=[IntPtr]::Zero;$derived=[IntPtr]::Zero;$size=[uint32]0
  try {
   if(-not [DiscoveryTaskPermissionProof]::ConvertStringSecurityDescriptorToSecurityDescriptor($case.sddl,1,[ref]$creator,[ref]$size)){throw 'In-memory descriptor parsing failed'}
   $mapping=[DiscoveryTaskPermissionProof+Mapping]::new();$mapping.Read=0x120089;$mapping.Write=0x120116;$mapping.Execute=0x1200a0;$mapping.All=0x1f01ff
   $ok=[DiscoveryTaskPermissionProof]::CreatePrivateObjectSecurityEx([IntPtr]::Zero,$creator,[ref]$derived,[IntPtr]::Zero,$false,0,$linked,[ref]$mapping)
   $nativeError=[Runtime.InteropServices.Marshal]::GetLastWin32Error()
   if($ok -ne $case.success -or (-not $ok -and $nativeError -ne 1307)){throw ('Unexpected real owner-assignment result: '+$case.name)}
   $passed++;'PASS '+$case.name+' uses real Windows in-memory owner assignment'
   if($ok) {
    foreach($access in @(@{mask=0x1200a9;allowed=$true;name='Read and run'},@{mask=0x40000;allowed=$false;name='Change DACL'},@{mask=0x80000;allowed=$false;name='Take ownership'},@{mask=0x10000;allowed=$false;name='Delete'},@{mask=0x120116;allowed=$false;name='Write'})) {
     $privileges=[byte[]]::new(1024);$length=[uint32]$privileges.Length;$granted=[uint32]0;$allowed=$false
     if(-not [DiscoveryTaskPermissionProof]::AccessCheck($derived,$medium,[uint32]$access.mask,[ref]$mapping,$privileges,[ref]$length,[ref]$granted,[ref]$allowed)){throw 'Actual medium-token AccessCheck failed'}
     if($allowed -ne $access.allowed){throw ('Unexpected medium-token access: '+$access.name)}
     $passed++;'PASS '+$access.name+' matches the actual unelevated token boundary'
    }
   }
  }finally{if($derived -ne [IntPtr]::Zero){[void][DiscoveryTaskPermissionProof]::DestroyPrivateObjectSecurity([ref]$derived)};if($creator -ne [IntPtr]::Zero){[void][DiscoveryTaskPermissionProof]::LocalFree($creator)}}
 }
}finally{foreach($handle in @($medium,$linked,$token)){if($handle -ne [IntPtr]::Zero){[void][DiscoveryTaskPermissionProof]::CloseHandle($handle)}}}
"$passed real Windows task-permission checks passed. In-memory security descriptors only; no task, protected file, privilege enablement or elevation."
