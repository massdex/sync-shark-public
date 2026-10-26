param([Parameter(Mandatory=$true)][string]$BuildRoot,[string]$Allocation='')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath($BuildRoot).TrimEnd('\')
$approved=if([string]::IsNullOrWhiteSpace($Allocation)){[IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'),'SyncShark','build','home-activity-tray')}else{[IO.Path]::GetFullPath($Allocation).TrimEnd('\')}
if($root -ine $approved){throw 'Only the registered active build allocation is permitted.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Native rejection checks must remain unelevated.'}
function FileOffset([byte[]]$Bytes,[int]$Sections,[int]$Count,[uint32]$Rva) {
    for($i=0;$i -lt $Count;$i++){
        $at=$Sections+40*$i;$virtual=[BitConverter]::ToUInt32($Bytes,$at+12)
        $size=[Math]::Max([BitConverter]::ToUInt32($Bytes,$at+8),[BitConverter]::ToUInt32($Bytes,$at+16))
        if($Rva -ge $virtual -and [uint64]$Rva -lt ([uint64]$virtual+$size)){return [int]([BitConverter]::ToUInt32($Bytes,$at+20)+$Rva-$virtual)}
    }
    throw 'The load configuration has no backing section.'
}
$directory=Join-Path $root 'obj\SyncShark\native'
for($parent=[IO.DirectoryInfo]$directory;$parent;$parent=$parent.Parent){
    if(($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Reparse native build path refused.'}
}
foreach($name in @('SyncShark.Explorer.dll','SyncSharkDiscovery.exe')){
    $path=Join-Path $directory $name
    if(([IO.File]::GetAttributes($path) -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline)) -ne 0){throw 'Native member is not an ordinary local file.'}
    $bytes=[IO.File]::ReadAllBytes($path); if($bytes.Length -lt 512 -or $bytes.Length -gt 4194304){throw 'Native member is not bounded.'}
    $pe=[BitConverter]::ToInt32($bytes,0x3c)
    if($pe -lt 64 -or $pe+264 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes,$pe) -ne 0x4550 -or [BitConverter]::ToUInt16($bytes,$pe+4) -ne 0x8664){throw 'Expected x64 PE.'}
    $optional=$pe+24;if([BitConverter]::ToUInt16($bytes,$optional) -ne 0x20b){throw 'Expected PE32+.'}
    $rva=[BitConverter]::ToUInt32($bytes,$optional+112+80)
    $size=[BitConverter]::ToUInt32($bytes,$optional+112+84)
    $sections=$optional+[BitConverter]::ToUInt16($bytes,$pe+20)
    $offset=FileOffset $bytes $sections ([BitConverter]::ToUInt16($bytes,$pe+6)) $rva
    if($size -lt 80 -or $offset+80 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes,$offset) -lt 80 -or [BitConverter]::ToUInt16($bytes,$offset+78) -ne 0x800){throw 'Dependent DLL loading is not restricted to System32.'}
    [pscustomobject]@{check='PE dependent loading';member=$name;bytes=$bytes.Length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant();flags='0x800';result='PASS'} | ConvertTo-Json -Compress
}
$worker=Join-Path $directory 'SyncSharkDiscovery.exe'
$cases=@(
    @{arguments='Unknown 1';expected=87},
    @{arguments='Configure 0';expected=87},
    @{arguments='Configure -1';expected=87},
    @{arguments='Configure 2147483648';expected=87},
    @{arguments='Configure 1 extra';expected=87},
    @{arguments=('Configure '+$PID);expected=740},
    @{arguments=('Restore '+$PID);expected=740},
    @{arguments=('Remove '+$PID);expected=740},
    @{arguments='';expected=740}
)
foreach($case in $cases){
    $start=[Diagnostics.ProcessStartInfo]::new($worker,$case.arguments)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden'
    $process=[Diagnostics.Process]::Start($start)
    try {
        if(-not $process.WaitForExit(5000)){$process.Kill();[void]$process.WaitForExit(5000);throw 'Native rejection exceeded its bound.'}
        if($process.ExitCode -ne $case.expected){throw ('Unexpected native exit for '+$case.arguments+': '+$process.ExitCode)}
        [pscustomobject]@{check='Actual native entry rejection';arguments=$case.arguments;expected=$case.expected;actual=$process.ExitCode;result='PASS'} | ConvertTo-Json -Compress
    } finally {$process.Dispose()}
}
'PASS: exact native binaries; no ShellExecute/runas, UAC, task, provider or protected-directory effect.'
