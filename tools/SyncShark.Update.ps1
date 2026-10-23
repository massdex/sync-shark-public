function Show-SyncSharkUpdateFailure([string]$Message,[string]$Caption='Sync Shark Update Not Completed') {
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class SyncSharkUpdateMessage { [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int MessageBox(IntPtr owner, string text, string caption, uint kind); }'
    [void][SyncSharkUpdateMessage]::MessageBox([IntPtr]::Zero, $Message + "`n`nDo not remove existing versions or transaction checkpoints. Reopen Sync Shark if available, or resume the existing versioned installer/rollback.", $Caption, 0x10)
}

function Open-SyncSharkUpdateInputLease([string]$Installer) {
    # These are trusted embedded bridge bytes, not code read from the package.
    # Keep the same ordinary local objects held from verification through the
    # installer/module invocation. A final path hash alone is not an execution fence.
    if (-not ('SyncSharkUpdateInputLease' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

public sealed class SyncSharkUpdateInputLease : IDisposable {
    [StructLayout(LayoutKind.Sequential)] struct Information {
        public uint Attributes,CreatedLow,CreatedHigh,AccessedLow,AccessedHigh,WrittenLow,WrittenHigh;
        public uint Volume,SizeHigh,SizeLow,Links,IdHigh,IdLow;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle,out Information information);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder path,uint length,uint flags);
    readonly List<SafeFileHandle> handles=new List<SafeFileHandle>();
    readonly Dictionary<string,SafeFileHandle> heldHandles=new Dictionary<string,SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
    long bytes;
    int entries;
    public SyncSharkUpdateInputLease(string installer) {
        try {
            installer=Path.GetFullPath(installer);
            string volume=Path.GetPathRoot(installer);
            if(volume.Length!=3 || new DriveInfo(volume).DriveType!=DriveType.Fixed ||
                !new DriveInfo(volume).DriveFormat.Equals("NTFS",StringComparison.OrdinalIgnoreCase))
                throw new IOException("Copy the complete update package onto a local NTFS drive before checking it.");
            if(Path.GetFileName(installer)!="Install-SyncShark.ps1")
                throw new IOException("The exact delivered installer is required.");
            string scripts=Path.GetDirectoryName(installer), release=Path.GetDirectoryName(scripts);
            // Parents must remain ordinary and unrenameable too: holding just
            // the installer leaf would still leave a directory substitution gap.
            var parents=new Stack<string>();
            for(string at=scripts; at!=null; at=Path.GetDirectoryName(at)) parents.Push(at);
            while(parents.Count!=0) Hold(parents.Pop(),true);
            Hold(installer,false);
            Hold(Path.Combine(scripts,"SyncShark.Release.psm1"),false);
            Hold(Path.Combine(release,"package"),true);
            Hold(Path.Combine(release,"package","unsigned-candidate-framework-manifest.json"),false);
            Inspect(Path.Combine(release,"package","signed-candidate"),0);
        } catch {Dispose();throw;}
    }
    void Inspect(string directory,int depth) {
        if(depth>8) throw new IOException("The update package directory depth exceeds its bound.");
        Hold(directory,true);
        foreach(string path in Directory.EnumerateFileSystemEntries(directory)) {
            if(++entries>512) throw new IOException("The update package has too many input paths.");
            var attributes=File.GetAttributes(path);
            if((attributes&(FileAttributes.ReparsePoint|FileAttributes.Offline|(FileAttributes)0x440000))!=0)
                throw new IOException("Update inputs must be ordinary on-disk objects; nothing was installed.");
            if((attributes&FileAttributes.Directory)!=0) Inspect(path,depth+1); else Hold(path,false);
        }
    }
    SafeFileHandle Hold(string path,bool directory) {
        path=Path.GetFullPath(path);
        SafeFileHandle existing;
        if(heldHandles.TryGetValue(path,out existing)) return existing;
        // LIST_DIRECTORY is essential: an attribute-only directory handle does
        // not establish the read-sharing reservation that prevents substitution.
        // OPEN_REPARSE_POINT/OPEN_NO_RECALL inspect, never hydrate, unsafe inputs.
        var handle=CreateFile(path,directory?0x81u:0x80000080u,1,IntPtr.Zero,3,
            0x00300000u|(directory?0x02000000u:0u),IntPtr.Zero);
        bool keep=false;
        try {
            if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(),"Update inputs cannot be held unchanged; nothing was installed.");
            Information info;
            if(!GetFileInformationByHandle(handle,out info) || (info.Attributes&0x441400u)!=0 ||
                ((info.Attributes&0x10u)!=0)!=directory || (!directory&&info.Links!=1))
                throw new IOException("The opened update input is redirected, offline, aliased or changed type.");
            var final=new StringBuilder(32768);
            uint length=GetFinalPathNameByHandle(handle,final,(uint)final.Capacity,0);
            if(length==0 || length>=final.Capacity ||
                !final.ToString().Equals("\\\\?\\"+path,StringComparison.OrdinalIgnoreCase))
                throw new IOException("The update input path differs from its held physical object.");
            if(!directory) {
                long size=((long)info.SizeHigh<<32)|info.SizeLow;
                if(size>64L*1024*1024 || (bytes=checked(bytes+size))>256L*1024*1024)
                    throw new IOException("The held update inputs exceed their inspection bound.");
                if(Path.GetFileName(path)=="Install-SyncShark.ps1" && size>1024*1024)
                    throw new IOException("The update installer exceeds its size bound.");
            }
            handles.Add(handle);heldHandles.Add(path,handle);keep=true;return handle;
        } finally {if(!keep) handle.Dispose();}
    }
    static long SizeOf(SafeFileHandle handle) {
        Information info;
        if(!GetFileInformationByHandle(handle,out info)) throw new Win32Exception(Marshal.GetLastWin32Error(),"The held update object could not be re-read.");
        return ((long)info.SizeHigh<<32)|info.SizeLow;
    }
    static string HashOf(SafeFileHandle handle) {
        using(var stream=new FileStream(handle,FileAccess.Read,4096,false)) {
            byte[] value;
            using(var sha=SHA256.Create()) value=sha.ComputeHash(stream);
            var text=new StringBuilder(value.Length*2);
            foreach(byte item in value) text.Append(item.ToString("x2"));
            return text.ToString();
        }
    }
    static void RequireBinding(string expectedSha256,string label) {
        if(expectedSha256==null || expectedSha256.Length!=64)
            throw new IOException("The "+label+" hash binding is invalid.");
    }
    public string HoldProtectedUpdater(string installer,long expectedBytes,string expectedSha256) {
        if(installer==null) throw new ArgumentNullException("installer");
        installer=Path.GetFullPath(installer);
        string scripts=Path.GetDirectoryName(installer);
        if(scripts==null || !heldHandles.ContainsKey(scripts))
            throw new IOException("The signed updater companion is only held under the already held scripts root.");
        if(expectedBytes<1 || expectedBytes>16L*1024*1024)
            throw new IOException("The signed updater companion size binding is invalid.");
        RequireBinding(expectedSha256,"signed updater companion");
        string companion=Path.Combine(scripts,"SyncSharkUpdater.exe");
        if(!File.Exists(companion))
            throw new IOException("The declared signed updater companion is missing from the verified update package; nothing was installed.");
        var handle=Hold(companion,false);
        if(SizeOf(handle)!=expectedBytes || !string.Equals(HashOf(handle),expectedSha256,StringComparison.OrdinalIgnoreCase))
            throw new IOException("The signed updater companion bytes differ from the signed release; nothing was installed.");
        return companion;
    }
    public void HoldVerifiedLaunch(string productRoot,string runtimeTarget,string expectedExeSha256) {
        string root=Path.GetFullPath(productRoot);
        string target=Path.GetFullPath(runtimeTarget);
        string executable=Path.Combine(target,"SyncShark.exe");
        if(!File.Exists(executable))
            throw new IOException("The installed runtime executable is missing; nothing was launched.");
        RequireBinding(expectedExeSha256,"installed runtime executable");
        Hold(target,true);
        var handle=Hold(executable,false);
        if(!string.Equals(HashOf(handle),expectedExeSha256,StringComparison.OrdinalIgnoreCase))
            throw new IOException("The installed runtime executable differs from the signed release; nothing was launched.");
        string active=Path.Combine(root,"active");
        HoldReparsePoint(active);
        VerifyActiveBinding(active,target);
    }
    void HoldReparsePoint(string path) {
        path=Path.GetFullPath(path);
        if(heldHandles.ContainsKey(path)) return;
        // The pointer is opened as its own reparse object with read sharing, so it
        // cannot be retargeted, renamed or deleted while it is held.
        var handle=CreateFile(path,0x81u,1,IntPtr.Zero,3,0x02300000u,IntPtr.Zero);
        bool keep=false;
        try {
            if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(),"The active application pointer cannot be held; nothing was launched.");
            Information info;
            if(!GetFileInformationByHandle(handle,out info) || (info.Attributes&0x10u)==0 || (info.Attributes&0x400u)==0 ||
                (info.Attributes&0x441000u)!=0)
                throw new IOException("The active application pointer is not an ordinary local directory junction.");
            var final=new StringBuilder(32768);
            uint length=GetFinalPathNameByHandle(handle,final,(uint)final.Capacity,0);
            if(length==0 || length>=final.Capacity ||
                !final.ToString().Equals("\\\\?\\"+path,StringComparison.OrdinalIgnoreCase))
                throw new IOException("The active application pointer path differs from its held physical object.");
            handles.Add(handle);heldHandles.Add(path,handle);keep=true;
        } finally {if(!keep) handle.Dispose();}
    }
    void VerifyActiveBinding(string active,string target) {
        // The same pointer followed without OPEN_REPARSE_POINT must resolve to the
        // exact installed runtime, so the held pointer is bound to that target.
        var handle=CreateFile(active,0x81u,1,IntPtr.Zero,3,0x02100000u,IntPtr.Zero);
        try {
            if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(),"The active application pointer could not be resolved; nothing was launched.");
            Information info;
            if(!GetFileInformationByHandle(handle,out info) || (info.Attributes&0x10u)==0 || (info.Attributes&0x441000u)!=0)
                throw new IOException("The active application pointer does not resolve to an ordinary local directory.");
            var final=new StringBuilder(32768);
            uint length=GetFinalPathNameByHandle(handle,final,(uint)final.Capacity,0);
            if(length==0 || length>=final.Capacity ||
                !final.ToString().Equals("\\\\?\\"+target,StringComparison.OrdinalIgnoreCase))
                throw new IOException("The active application pointer does not resolve to the installed runtime; nothing was launched.");
        } finally {if(!handle.IsInvalid) handle.Dispose();}
    }
    public void Dispose() {for(int i=handles.Count-1;i>=0;i--) handles[i].Dispose();handles.Clear();heldHandles.Clear();}
}
'@
    }
    [SyncSharkUpdateInputLease]::new($Installer)
}

function Assert-SyncSharkUpdateProtectedCompanion([object]$Lease,[string]$Installer,[object]$Contract,
    [string[]]$Thumbprint,[string]$Subject,[string[]]$SpkiSha256) {
    # The existing signed-contract reader decides whether this release declares a
    # protected updater companion. A release without protectedUpdater keeps its
    # legacy behaviour and holds no companion.
    $release = Read-SyncSharkUpdaterRelease $Installer ($Contract.version + '+' + $Contract.sourceRevision)
    if ($null -eq $release) { return $null }
    $companion = $Lease.HoldProtectedUpdater($Installer, [long]$release.protectedUpdater.workerBytes,
        [string]$release.protectedUpdater.workerSha256)
    Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $companion) $Thumbprint $Subject $Subject $SpkiSha256
    $companion
}

function Get-SyncSharkUpdateInstallerReceipt([object]$Output) {
    # Exactly one parsed completion receipt. Extra or unparsable output is not a
    # release identity and never reaches a launch.
    $lines = @($Output | Where-Object { $null -ne $_ -and ([string]$_).Trim().Length -ne 0 })
    if ($lines.Count -ne 1) { throw 'The installer did not return exactly one completion receipt. Nothing was launched.' }
    $receipt = $null
    try { $receipt = ([string]$lines[0]) | ConvertFrom-Json -Depth 8 }
    catch { throw 'The installer completion receipt is not valid JSON. Nothing was launched.' }
    if ($null -eq $receipt -or $receipt -is [string] -or $receipt -is [Array]) {
        throw 'The installer did not return exactly one completion receipt object. Nothing was launched.'
    }
    $receipt
}

function Assert-SyncSharkUpdateReceipt([object]$Receipt,[object]$Contract,[string]$ProductRoot) {
    foreach ($name in @('ok','operation','product_version','active_target')) {
        if ($null -eq $Receipt.PSObject.Properties[$name]) {
            throw ('The installer completion receipt is missing its ' + $name + ' identity. Nothing was launched.')
        }
    }
    if ($Receipt.ok -ne $true -or $Receipt.operation -cne 'install' -or
        $Receipt.product_version -cne ($Contract.version + '+' + $Contract.sourceRevision)) {
        throw 'The installer completion receipt does not match the signed release identity. Nothing was launched.'
    }
    $target = Resolve-SyncSharkRuntimeRoot $Contract.version
    if ((Get-SyncSharkNormalizedPath ([string]$Receipt.active_target)) -ine (Get-SyncSharkNormalizedPath $target)) {
        throw 'The installer completion receipt reports a different active runtime. Nothing was launched.'
    }
    # The receipt is evidence, never authority: re-read the real pointer.
    $observed = Get-SyncSharkJunctionTarget (Join-Path $ProductRoot 'active')
    if ($null -eq $observed -or $observed -ine (Get-SyncSharkNormalizedPath $target)) {
        throw 'The active application pointer does not resolve to the installed runtime. Nothing was launched.'
    }
    $target
}

function Start-SyncSharkVerifiedRuntime([string]$ProductRoot,[string]$Target,[object]$Contract,[object]$Lease) {
    $publisher = (Get-SyncSharkSignerIdentity).roles.publisher
    $thumbprint = @($publisher.thumbprints)
    $subject = $publisher.subject
    $spki = @($publisher.spki)
    $expectedVersion = $Contract.version + '+' + $Contract.sourceRevision
    $root = Get-SyncSharkNormalizedPath $ProductRoot
    $executable = Join-Path (Get-SyncSharkNormalizedPath $Target) 'SyncShark.exe'
    # Hold the exact verified runtime/EXE and the active-pointer binding until the
    # process exists, then launch the fixed verified target rather than the mutable
    # active link. The hold re-verifies the executable digest natively.
    $Lease.HoldVerifiedLaunch($root, $Target, [string]$Contract.signedExeSha256)
    Assert-SyncSharkJunction (Join-Path $root 'active') $Target
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion -cne $expectedVersion) {
        throw 'The installed runtime executable version does not match the signed release. Nothing was launched.'
    }
    Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $executable) $thumbprint $subject $subject $spki
    Start-Process -FilePath $executable -WindowStyle Hidden
}

function Invoke-SyncSharkUpdate {
    param([string]$Installer, [string]$ExpectedInstallerHash, [string]$CurrentVersion,
        [int]$ParentProcessId = 0, [string]$PowerShellPath, [switch]$Apply)
    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version Latest
    $publisher = (Get-SyncSharkSignerIdentity).roles.publisher
    $thumbprint = @($publisher.thumbprints)
    $subject = $publisher.subject
    $spki = @($publisher.spki)
    $installerPath = [IO.Path]::GetFullPath($Installer)
    if ([IO.Path]::GetFileName($installerPath) -cne 'Install-SyncShark.ps1') { throw 'Choose the delivered Install-SyncShark.ps1 file.' }
    $inputs = Open-SyncSharkUpdateInputLease $installerPath
    try {
    $scripts = [IO.Path]::GetDirectoryName($installerPath)
    $release = [IO.Path]::GetDirectoryName($scripts)
    Assert-SyncSharkOrdinaryTree $scripts
    if ((Get-SyncSharkSha256 $installerPath) -cne $ExpectedInstallerHash) { throw 'The selected installer changed. Choose the package again.' }
    Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $installerPath) $thumbprint $subject $subject $spki
    $headers = @(Get-Content -LiteralPath $installerPath -TotalCount 8 | Where-Object { $_.StartsWith('# SyncSharkUpdateContract: ') })
    if ($headers.Count -ne 1) { throw 'This release has no signed update contract and cannot be installed from the app.' }
    $contract = $headers[0].Substring('# SyncSharkUpdateContract: '.Length) | ConvertFrom-Json -Depth 8
    if ($contract.product -cne 'Massdex.SyncShark' -or $contract.sourceRevision -cnotmatch '^[0-9a-f]{40}$' -or
        $contract.version -cnotmatch '^\d+\.\d+\.\d+$' -or $contract.previousVersion -cne $CurrentVersion -or
        [version]$contract.version -le [version]($CurrentVersion.Split('+')[0])) { throw 'The package is not a newer compatible update for this installation.' }
    $module = Join-Path $scripts 'SyncShark.Release.psm1'
    if ((Get-SyncSharkSha256 $module) -cne $contract.moduleSha256) { throw 'The installer module does not match the signed contract.' }
    $manifest = Join-Path $release 'package\unsigned-candidate-framework-manifest.json'
    $package = Join-Path $release 'package\signed-candidate'
    $hashes = Get-SyncSharkSignedMemberHashes $contract
    foreach ($hash in @($contract.moduleSha256, $contract.manifestSha256) + @($hashes.Values)) {
        if ($hash -cnotmatch '^[0-9a-f]{64}$') { throw 'The signed package contract contains an invalid hash.' }
    }
    Assert-SyncSharkExactPackage $package $manifest $contract.manifestSha256 ($contract.version + '+' + $contract.sourceRevision) $hashes $thumbprint $subject $subject $spki '' $false
    [void](Assert-SyncSharkUpdateProtectedCompanion $inputs $installerPath $contract $thumbprint $subject $spki)
    if (-not $Apply) {
        [pscustomobject]@{ ok=$true; version=$contract.version; source=$contract.sourceRevision; previous=$contract.previousVersion;
            installer=$installerPath; installerSha256=$ExpectedInstallerHash; status='Verified';
            trust='Signed and locally pinned; not a Windows-trusted publisher'; restartRequired=$true } | ConvertTo-Json -Compress
        return
    }
    if ($ParentProcessId -le 0) { throw 'The installed application must complete its safe exit before updating.' }
    # A verified handoff is emitted before the application closes. Verification/launch failures
    # therefore leave the current UI running and never turn a failed start into a blank desktop.
    [Console]::Out.WriteLine('{"ready":true}')
    [Console]::Out.Flush()
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while (Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Sync Shark did not finish its safe exit. No install was attempted.' }
        Start-Sleep -Milliseconds 250
    }
    # The existing transaction installer rebinds every input, stages the exact package, backs up
    # state, swaps the versioned active pointer and preserves its immediate predecessor.
    if ((Get-SyncSharkSha256 $installerPath) -cne $ExpectedInstallerHash -or (Get-SyncSharkSha256 $module) -cne $contract.moduleSha256) {
        throw 'The update changed while waiting for a safe exit. No install was attempted.'
    }
    $installArguments = @{
        ExpectedManifestSha256 = $contract.manifestSha256
        ExpectedModuleSha256 = $contract.moduleSha256
        ExpectedSignedCoreSha256 = $contract.signedCoreSha256
        ExpectedSignedUiSha256 = $contract.signedUiSha256
        ExpectedSignedExeSha256 = $contract.signedExeSha256
    }
    if ($hashes.ContainsKey('SyncShark.Explorer.dll')) { $installArguments.ExpectedSignedExplorerSha256 = $hashes['SyncShark.Explorer.dll'] }
    if ($hashes.ContainsKey('SyncSharkDiscovery.exe')) { $installArguments.ExpectedSignedDiscoverySha256 = $hashes['SyncSharkDiscovery.exe'] }
    if ($hashes.ContainsKey('SyncShark.msix')) { $installArguments.ExpectedSignedIdentityPackageSha256 = $hashes['SyncShark.msix'] }
    $output = & $installerPath @installArguments
    $receipt = Get-SyncSharkUpdateInstallerReceipt $output
    if ($receipt.ok -ne $true) { throw 'The installer has not confirmed completion. Its retained transaction is available for recovery.' }
    $root = Resolve-SyncSharkProductRoot ''
    $target = Assert-SyncSharkUpdateReceipt $receipt $contract $root
    Start-SyncSharkVerifiedRuntime $root $target $contract $inputs
    } finally { $inputs.Dispose() }
}
