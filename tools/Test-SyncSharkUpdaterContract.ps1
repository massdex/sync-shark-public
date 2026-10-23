param([Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$Allocation='')
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$allocation=if([string]::IsNullOrWhiteSpace($Allocation)){[IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'),'SyncShark','build')}else{[IO.Path]::GetFullPath($Allocation).TrimEnd('\')}
if(-not $output.StartsWith($allocation+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Updater fixture output must stay in the registered build allocation.'}
for($parent=[IO.DirectoryInfo]$output;$parent;$parent=$parent.Parent){if($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)-ne 0){throw 'Reparse build output refused.'}}
[void][IO.Directory]::CreateDirectory($output)
$parameters=[CodeDom.Compiler.CompilerParameters]::new()
$parameters.GenerateInMemory=$false;$parameters.GenerateExecutable=$true;$parameters.IncludeDebugInformation=$false
$parameters.OutputAssembly=Join-Path $output 'UpdaterContractChecks.exe'
$parameters.CompilerOptions='/optimize+ /platform:x64 /define:UPDATER_CONTRACT_STANDALONE'
$parameters.TempFiles=[CodeDom.Compiler.TempFileCollection]::new($output,$false)
foreach($reference in @('System.dll','System.Core.dll','System.Xml.dll','System.Xml.Linq.dll','System.Runtime.Serialization.dll','System.Security.dll','System.IO.Compression.dll','Microsoft.CSharp.dll')){[void]$parameters.ReferencedAssemblies.Add($reference)}
$compiler=[Microsoft.CSharp.CSharpCodeProvider]::new()
try {
    $paths=@('SyncShark.DiscoverySetupNative.cs','SyncShark.DiscoveryTransaction.cs','SyncShark.UpdaterContract.cs','SyncShark.UpdaterData.cs','SyncShark.UpdaterSignature.cs','SyncShark.UpdaterTask.cs','SyncShark.UpdaterFiles.cs','SyncShark.UpdaterWindows.cs','SyncShark.UpdaterManifest.cs','SyncShark.UpdaterSession.cs','SyncShark.UpdaterSetup.cs','..\tests\SyncShark.UiFixture\UpdaterContractChecks.cs')
    $paths+='SyncShark.UpdaterDiscovery.cs'
    $sources=@($paths | ForEach-Object {[IO.File]::ReadAllText((Join-Path $PSScriptRoot $_)).Replace('#nullable disable','')})
    $result=$compiler.CompileAssemblyFromSource($parameters,[string[]]$sources)
    if($result.Errors.HasErrors){throw (($result.Errors | ForEach-Object {$_.ToString()}) -join "`n")}
    $start=[Diagnostics.ProcessStartInfo]::new($parameters.OutputAssembly)
    $start.Arguments='--receipt-checks "'+$output+'"'
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(30000)){throw 'The bounded no-effect admission fixture did not finish; preserve its state.'}
        $stdout.GetAwaiter().GetResult();$errorOutput=$stderr.GetAwaiter().GetResult()
        if($process.ExitCode -ne 0){throw $errorOutput}
    } finally {$process.Dispose()}
} finally {$compiler.Dispose();$parameters.TempFiles.Delete()}
