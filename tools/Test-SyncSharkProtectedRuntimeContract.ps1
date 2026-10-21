param([Parameter(Mandatory=$true)][string]$BuildRoot,
    [Parameter(Mandatory=$true)][string]$ReleaseVersion,
    [Parameter(Mandatory=$true)][string]$PreviousProductVersion,
    [string]$Allocation='')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -ne 5){throw 'Use the real Windows PowerShell setup host.'}
$build=[IO.Path]::GetFullPath($BuildRoot)
$allocation=if([string]::IsNullOrWhiteSpace($Allocation)){[IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'),'SyncShark','build')}else{[IO.Path]::GetFullPath($Allocation).TrimEnd('\')}
if(-not $build.StartsWith($allocation+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use the existing owner build allocation.'}
# Check the actual published executable, not a freshly regenerated sibling
# script/assembly that may differ from the resources linked into the package.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class SyncSharkPublishedSetupResources {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    public static byte[] Read(string path, int id) {
        // DATAFILE only: never execute the helper or request elevation.
        IntPtr module=LoadLibraryEx(path,IntPtr.Zero,2);
        if(module==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            IntPtr resource=FindResource(module,(IntPtr)id,(IntPtr)10);
            if(resource==IntPtr.Zero)throw new InvalidOperationException("Published setup resource is missing.");
            uint size=SizeofResource(module,resource);
            if(size==0 || size>524288)throw new InvalidOperationException("Published setup resource size is invalid.");
            IntPtr bytes=LockResource(LoadResource(module,resource));
            if(bytes==IntPtr.Zero)throw new InvalidOperationException("Published setup resource is unreadable.");
            byte[] result=new byte[size];Marshal.Copy(bytes,result,0,result.Length);return result;
        } finally {FreeLibrary(module);}
    }
}
'@
$publishedHelper=[IO.Path]::Combine($build,'publish','SyncSharkDiscovery.exe')
$embeddedAssembly=[SyncSharkPublishedSetupResources]::Read($publishedHelper,201)
$embeddedScript=[SyncSharkPublishedSetupResources]::Read($publishedHelper,202)
function ResourceHash([byte[]]$Bytes) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try {return [BitConverter]::ToString($sha.ComputeHash($Bytes))} finally {$sha.Dispose()}
}
foreach($resource in @(@('DiscoverySetup.dll',$embeddedAssembly),@('DiscoverySetup.ps1',$embeddedScript))) {
    $generated=[IO.File]::ReadAllBytes([IO.Path]::Combine($build,'obj','SyncShark','native',[string]$resource[0]))
    if((ResourceHash $generated) -cne (ResourceHash ([byte[]]$resource[1]))) {
        throw ('Published helper embeds stale '+$resource[0]+'. Rebuild and republish with the exact predecessor; regenerating adjacent files does not repair the executable.')
    }
}
[void][Reflection.Assembly]::Load($embeddedAssembly)
. ([scriptblock]::Create([Text.Encoding]::UTF8.GetString($embeddedScript)))
. (Join-Path $PSScriptRoot 'SyncShark.ReleaseMetadata.ps1')
$script:passed=0
function Check([bool]$Value,[string]$Label){if(-not $Value){throw $Label};$script:passed++;'PASS '+$Label}
function Reject([scriptblock]$Action,[string]$Label){try{& $Action|Out-Null}catch{Check $true $Label;return};throw ('Expected rejection: '+$Label)}
$revision='1234567890123456789012345678901234567890'
$previousVersion=$PreviousProductVersion
$metadata=New-SyncSharkReleaseMetadata $ReleaseVersion $revision $previousVersion ('a'*64)
function Contract {
    $rows=@([SyncSharkProtectedRuntimeTransaction]::Members|ForEach-Object {[pscustomobject]@{path=$_;bytes=12;sha256=('a'*64)}})
    [pscustomobject]@{product='Massdex.SyncShark';version=$releaseVersion;sourceRevision=$revision;
        previousVersion=$previousVersion;
        protectedRuntimeRoot=('C:\Program Files\Massdex\SyncShark\versions\'+$releaseVersion);runtimeMembers=$rows;
        signedExeSha256=('a'*64);signedUiSha256=('a'*64);signedCoreSha256=('a'*64);
        signedDiscoverySha256=('a'*64);signedExplorerSha256=('a'*64);signedIdentityPackageSha256=('a'*64)}
}
$version=$releaseVersion+'+'+$revision
$valid=Contract;$members=Assert-SyncSharkProtectedRuntimeContract $valid $version
Check ($members.Count -eq 16 -and $members.ContainsKey('Assets/SyncShark-64.png')) 'Exact complete code/dependency/logo contract admitted by production predicate'
foreach($template in @('Install-SyncShark.ps1.template','Rollback-SyncShark.ps1.template')) {
    $tokens=$null;$errors=$null
    $rendered=Expand-SyncSharkReleaseTemplate ([IO.File]::ReadAllText((Join-Path $PSScriptRoot $template))) $metadata ((Contract)|ConvertTo-Json -Depth 6 -Compress)
    $ast=[Management.Automation.Language.Parser]::ParseInput($rendered,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'Release template parsing failed.'}
    $pins=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and $node.Left.VariablePath.UserPath -ceq 'expectedPreviousVersion'},$true))
    if($pins.Count -ne 1 -or $pins[0].Right -isnot [Management.Automation.Language.CommandExpressionAst] -or $pins[0].Right.Expression -isnot [Management.Automation.Language.StringConstantExpressionAst]){throw 'Expected one literal predecessor pin.'}
    $candidate=Contract;$candidate.previousVersion=$pins[0].Right.Expression.Value
    $null=Assert-SyncSharkProtectedRuntimeContract $candidate $version
    Check ($candidate.previousVersion -ceq $previousVersion) ($template+' predecessor passes the actual protected helper predicate')
}
$candidate=Contract;$candidate.previousVersion='0.3.33+94a083a4800d47d4615096c9c533306c00d9de31'
Reject {Assert-SyncSharkProtectedRuntimeContract $candidate $version} 'Obsolete 0.3.33 predecessor remains rejected'
foreach($field in @('product','version','sourceRevision','previousVersion','protectedRuntimeRoot')) {
    $candidate=Contract;$candidate.$field='wrong'
    Reject {Assert-SyncSharkProtectedRuntimeContract $candidate $version} ('Changed '+$field+' cannot select an elevated effect')
}
$candidate=Contract;$candidate.runtimeMembers=$candidate.runtimeMembers[0..14]
Reject {Assert-SyncSharkProtectedRuntimeContract $candidate $version} 'Omitted dependency rejected'
$candidate=Contract;$candidate.runtimeMembers[1].path=$candidate.runtimeMembers[0].path
Reject {Assert-SyncSharkProtectedRuntimeContract $candidate $version} 'Duplicate member rejected'
foreach($path in @('../outside.dll','C:\outside.dll','Assets/../SyncShark.exe','syncshark.exe','unowned.dll')) {
    $candidate=Contract;$candidate.runtimeMembers[0].path=$path
    Reject {Assert-SyncSharkProtectedRuntimeContract $candidate $version} ('Unexpected or case-aliased path rejected: '+$path)
}
foreach($size in @(0,-1,536870913,12.5,'12')) {
    $candidate=Contract;$candidate.runtimeMembers[0].bytes=$size
    Reject {Assert-SyncSharkProtectedRuntimeContract $candidate $version} 'Invalid member length rejected'
}
$candidate=Contract;$candidate.signedDiscoverySha256='b'*64
Reject {Assert-SyncSharkProtectedRuntimeContract $candidate $version} 'Helper hash differs between authenticated inventories'
Reject {Assert-SyncSharkProtectedRuntimeContract $valid '0.3.32+UNBOUND'} 'Unfrozen executable cannot authorize setup'
"$script:passed actual contract predicate checks passed. Inputs are synthetic after-authentication contract data; signature trust and protected effects remain separate required proof."
