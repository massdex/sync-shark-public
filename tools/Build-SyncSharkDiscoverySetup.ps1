param([Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+\+(?:[0-9a-f]{40}|UNBOUND)$')][string]$ProductVersion,
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+\+[0-9a-f]{40}$')][string]$ExpectedPreviousVersion,
    [ValidateSet('Discovery','Updater')][string]$Kind='Discovery',
    [string]$Allocation='')
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
# Migration-owned reference repair: the owner build allocation is the CONFIGURED SyncSharkBuildRoot passed by the
# actual invocation (the MSBuild output allocation), never a path inferred from the source location. When the
# invocation does not configure one, the legacy LocalAppData allocation is preserved as the default - so the
# legacy preimage is intact and the guard simply follows the configured owner allocation across the migration.
$allocation=if([string]::IsNullOrWhiteSpace($Allocation)){[IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'),'SyncShark','build')}else{[IO.Path]::GetFullPath($Allocation).TrimEnd('\')}
if(-not $output.StartsWith($allocation+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Discovery build resources must stay in the owner build allocation.'}
for($parent=[IO.DirectoryInfo]$output;$parent;$parent=$parent.Parent){if(($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)-ne 0){throw 'Reparse build output refused.'}}
$component=$Kind+'Setup';$nativeName='SyncShark'+$Kind
$assembly=Join-Path $output ($component+'.dll')
$parameters=[CodeDom.Compiler.CompilerParameters]::new()
$parameters.GenerateInMemory=$false;$parameters.GenerateExecutable=$false;$parameters.IncludeDebugInformation=$false
$parameters.OutputAssembly=$assembly;$parameters.CompilerOptions='/optimize+ /platform:x64'
$parameters.TempFiles=[CodeDom.Compiler.TempFileCollection]::new($output,$false)
foreach($reference in @('System.dll','System.Core.dll','System.Xml.dll','System.Runtime.Serialization.dll')){[void]$parameters.ReferencedAssemblies.Add($reference)}
foreach($reference in @('System.Xml.Linq.dll','System.Security.dll','System.IO.Compression.dll','Microsoft.CSharp.dll')){[void]$parameters.ReferencedAssemblies.Add($reference)}
$compiler=[Microsoft.CSharp.CSharpCodeProvider]::new()
try {
    # The framework compiler predates nullable annotations, including in excluded
    # preprocessor branches. Remove only that non-semantic directive; both callers
    # otherwise compile the same transaction and native source.
    $sourceNames=@('SyncShark.JournalNative.cs','SyncShark.JournalSetup.cs','SyncShark.DiscoverySetupNative.cs','SyncShark.DiscoveryTransaction.cs','SyncShark.SignerIdentity.cs')
    $sourceNames+=@('SyncShark.UpdaterContract.cs','SyncShark.UpdaterData.cs','SyncShark.UpdaterSignature.cs','SyncShark.UpdaterTask.cs','SyncShark.UpdaterFiles.cs','SyncShark.UpdaterWindows.cs','SyncShark.UpdaterManifest.cs')
    $sourceNames+='SyncShark.UpdaterDiscovery.cs'
    if($Kind -eq 'Updater'){$sourceNames+='SyncShark.UpdaterSession.cs'}else{$sourceNames+='SyncShark.UpdaterSetup.cs'}
    $sources=$sourceNames | ForEach-Object {
        [IO.File]::ReadAllText((Join-Path $PSScriptRoot $_)).Replace('#nullable disable','')
    }
    $result=$compiler.CompileAssemblyFromSource($parameters,[string[]]$sources)
    if($result.Errors.HasErrors){throw (($result.Errors | ForEach-Object {$_.ToString()}) -join "`n")}
    $script=Join-Path $output ($component+'.ps1')
    $embedded=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SyncShark.DiscoverySetup.ps1'))
    if([regex]::Matches($embedded,'__EXPECTED_PREVIOUS_VERSION__').Count -ne 1){throw 'Expected one embedded predecessor binding.'}
    $embedded=$embedded.Replace('__EXPECTED_PREVIOUS_VERSION__',$ExpectedPreviousVersion)
    if($Kind -eq 'Updater') {
        $embedded+="`r`n"+[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SyncShark.Updater.ps1'))
    }
    [IO.File]::WriteAllText($script,$embedded,[Text.UTF8Encoding]::new($false))
    $icon=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\apps\SyncShark\Assets\SyncShark.ico'))
    $manifestPath=Join-Path $output ($component+'.manifest')
    $manifest=@'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
 <assemblyIdentity name="Massdex.SyncShark" version="1.0.0.0" />
 <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false" /></requestedPrivileges></security></trustInfo>
</assembly>
'@
    [IO.File]::WriteAllText($manifestPath,$manifest,[Text.UTF8Encoding]::new($false))
    $fileVersion=$Version.Replace('.',',')+',0'
    $metadata=@"
1 VERSIONINFO
FILEVERSION $fileVersion
PRODUCTVERSION $fileVersion
FILEFLAGSMASK 0x3fL
FILEFLAGS 0x0L
FILEOS 0x40004L
FILETYPE 0x1L
FILESUBTYPE 0x0L
BEGIN
 BLOCK "StringFileInfo"
 BEGIN
  BLOCK "040904b0"
  BEGIN
   VALUE "CompanyName", "Massdex\0"
   VALUE "FileDescription", "Sync Shark\0"
   VALUE "FileVersion", "$Version\0"
   VALUE "InternalName", "$nativeName\0"
   VALUE "OriginalFilename", "$nativeName.exe\0"
   VALUE "ProductName", "Sync Shark\0"
   VALUE "ProductVersion", "$ProductVersion\0"
  END
 END
 BLOCK "VarFileInfo"
 BEGIN
  VALUE "Translation", 0x409, 1200
 END
END
"@
    $rc='1 ICON "'+$icon.Replace('\','\\')+'"'+"`r`n"+
        '1 24 "'+$manifestPath.Replace('\','\\')+'"'+"`r`n"+
        '201 RCDATA "'+$assembly.Replace('\','\\')+'"'+"`r`n"+
        '202 RCDATA "'+$script.Replace('\','\\')+'"'+"`r`n"+$metadata
    [IO.File]::WriteAllText((Join-Path $output ($component+'.rc')),$rc,[Text.UTF8Encoding]::new($false))
} finally {$compiler.Dispose();$parameters.TempFiles.Delete()}
