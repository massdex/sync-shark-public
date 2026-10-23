param([Parameter(Mandatory=$true)][string]$BuildRoot,[string]$Allocation='')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$allocation=if([string]::IsNullOrWhiteSpace($Allocation)){[IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'),'SyncShark','build','home-activity-tray')}else{[IO.Path]::GetFullPath($Allocation).TrimEnd('\')}
if([IO.Path]::GetFullPath($BuildRoot).TrimEnd('\') -ine $allocation){throw 'Use the registered active build allocation.'}
for($parent=[IO.DirectoryInfo]$allocation;$parent;$parent=$parent.Parent){if(($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Reparse allocation refused.'}}
$root=Join-Path $allocation ('identity-package-checks-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$compiler=[Microsoft.CSharp.CSharpCodeProvider]::new()
$parameters=[CodeDom.Compiler.CompilerParameters]::new();$parameters.GenerateInMemory=$true
$parameters.TempFiles=[CodeDom.Compiler.TempFileCollection]::new($root,$false)
foreach($reference in @('System.dll','System.Core.dll','System.Xml.dll')){[void]$parameters.ReferencedAssemblies.Add($reference)}
$explorer=Join-Path $allocation 'obj\SyncShark\native\SyncShark.Explorer.dll'
$logo=Join-Path (Split-Path -Parent $PSScriptRoot) 'apps\SyncShark\Assets\SyncShark-64.png'
try{
    $built=$compiler.CompileAssemblyFromSource($parameters,[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SyncShark.IdentityPackage.cs')))
    if($built.Errors.HasErrors){throw (($built.Errors | ForEach-Object {$_.ToString()}) -join '; ')}
    $packageType=$built.CompiledAssembly.GetType('SyncSharkIdentityPackage',$true)
    $shellNames=@($packageType::ShellAssetNames())
    if($shellNames.Count -ne 21 -or @($shellNames | Select-Object -Unique).Count -ne 21){throw 'Shell artwork size/theme matrix is incomplete or duplicated.'}
    foreach($size in @(16,20,24,32,48,64,256)) {
        foreach($form in @('','_altform-unplated','_altform-lightunplated')) {
            if($shellNames -cnotcontains ('Assets\SyncShark-64.targetsize-'+$size+$form+'.png')){throw 'A required shell artwork variant is missing.'}
        }
        if(-not (Test-Path -LiteralPath (Join-Path (Split-Path $logo -Parent) ('SyncShark-'+$size+'.png')) -PathType Leaf)){throw 'Shell artwork references a nonexistent source asset.'}
    }
    $missingResourcePackage=Join-Path $root 'missing-resources.msix'
    $resourceRefused=$false
    try {$packageType::Create($missingResourcePackage,$logo,$explorer,'0.3.35.0',$root,$BuildRoot)}
    catch {if($_.Exception.ToString() -notlike '*Missing or oversized shell resource*'){throw};$resourceRefused=$true}
    if(-not $resourceRefused -or (Test-Path -LiteralPath $missingResourcePackage)){throw 'Absent shell index/artwork did not fail before creating package bytes.'}
    Write-Output 'PASS all 21 shell artwork names bind existing size assets; missing resource bundle is refused before package creation. Actual PRI resolution remains a separate required proof.'
    $package=Join-Path $root 'SyncShark.msix'
    $packageType::Create($package,$logo,$explorer,'0.3.19.0',$null,$BuildRoot)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($package)
    try{
        $entry=$zip.GetEntry('SyncShark.Explorer.dll')
        if($null -eq $entry -or $entry.Length -ne (Get-Item -LiteralPath $explorer).Length){throw 'The actual package omitted or truncated its COM payload.'}
        $stream=$entry.Open();$hash=[Security.Cryptography.SHA256]::Create()
        try{$embedded=([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}finally{$hash.Dispose();$stream.Dispose()}
        if($embedded -cne (Get-FileHash -LiteralPath $explorer).Hash.ToLowerInvariant()){throw 'Package-contained command bytes differ.'}
        $reader=[IO.StreamReader]::new($zip.GetEntry('AppxManifest.xml').Open())
        try{[xml]$manifest=$reader.ReadToEnd()}finally{$reader.Dispose()}
        $ns=[Xml.XmlNamespaceManager]::new($manifest.NameTable);$ns.AddNamespace('com','http://schemas.microsoft.com/appx/manifest/com/windows10')
        $classes=$manifest.SelectNodes('//com:Class',$ns)
        if($classes.Count -ne 1 -or $classes[0].Path -cne 'SyncShark.Explorer.dll' -or $classes[0].Id -cne 'B2251D9A-7803-4F1F-8FE8-CBDA284BD5E9'){throw 'Package COM manifest binding changed.'}
        $extension=$manifest.SelectSingleNode('//com:Extension',$ns)
        $uap10='http://schemas.microsoft.com/appx/manifest/uap/windows10/10'
        if($extension.GetAttribute('RuntimeBehavior',$uap10) -cne 'packagedClassicApp' -or
            $extension.GetAttribute('TrustLevel',$uap10) -cne 'mediumIL') {throw 'The COM server must explicitly use the contained package runtime, not inherit the external app runtime.'}
        [pscustomobject]@{result='PASS';package_bytes=(Get-Item -LiteralPath $package).Length;package_sha256=(Get-FileHash -LiteralPath $package).Hash.ToLowerInvariant();embedded_com_sha256=$embedded;payload_entries=@($zip.Entries|ForEach-Object FullName);scope='Actual Windows packaging factory create/read succeeds with exact package-contained COM. Unsigned development proof only: no trust, registration, installation or Explorer action.'} | ConvertTo-Json -Depth 3 -Compress
    }finally{$zip.Dispose()}
}finally{
    $compiler.Dispose();$parameters.TempFiles.Delete()
    if(-not [IO.Path]::GetFullPath($root).StartsWith($allocation+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup containment changed.'}
    foreach($entry in [IO.Directory]::EnumerateFileSystemEntries($root)){
        if(([IO.File]::GetAttributes($entry) -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint)) -ne 0){throw 'Unclaimed package fixture content preserved.'}
    }
    foreach($entry in [IO.Directory]::EnumerateFiles($root)){[IO.File]::Delete($entry)}
    [IO.Directory]::Delete($root,$false)
}
