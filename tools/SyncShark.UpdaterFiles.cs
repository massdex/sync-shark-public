#if NET8_0_OR_GREATER
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

// Leases survive verification and publication. Paths are derived by the fixed
// entry point; this helper is not exposed as an elevated command or path runner.
internal sealed class SyncSharkUpdaterFiles : IDisposable
{
    private readonly List<IDisposable> leases=new List<IDisposable>();
    private readonly Dictionary<string,Microsoft.Win32.SafeHandles.SafeFileHandle> paths=new Dictionary<string,Microsoft.Win32.SafeHandles.SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> protectedPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly IntPtr medium;
    internal SyncSharkUpdaterFiles(IntPtr ordinaryToken) {medium=ordinaryToken;}
    internal void Hold(string path,bool directory,bool protect)
    {
        path=Path.GetFullPath(path);
        Microsoft.Win32.SafeHandles.SafeFileHandle existing;
        if(paths.TryGetValue(path,out existing))
        {
            if(protect && !protectedPaths.Contains(path)){SyncSharkDiscoverySetupNative.RequireProtected(existing,medium,directory);protectedPaths.Add(path);}
            return;
        }
        var handle=SyncSharkDiscoverySetupNative.Hold(path,directory);
        try {if(protect){SyncSharkDiscoverySetupNative.RequireProtected(handle,medium,directory);protectedPaths.Add(path);}leases.Add(handle);paths.Add(path,handle);}
        catch {handle.Dispose();throw;}
    }
    internal void Ancestors(string directory,bool protect)
    {
        for(string at=Path.GetFullPath(directory);at!=Path.GetPathRoot(at);at=Path.GetDirectoryName(at))Hold(at,true,protect);
    }
    internal string SmallText(string path,int limit,bool protect)
    {
        Hold(path,false,protect);
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            SyncSharkUpdaterContract.Require(file.Length>0 && file.Length<=limit,"Updater data length is outside its bound.");
            using(var reader=new StreamReader(file,new UTF8Encoding(false,true),true,4096))return reader.ReadToEnd();
        }
    }
    internal string Hash(string path,bool protect)
    {
        Hold(path,false,protect);
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var sha=SHA256.Create())return Hex(sha.ComputeHash(file));
    }
    internal static string Hex(byte[] value) {return BitConverter.ToString(value).Replace("-","").ToLowerInvariant();}
    internal void Runtime(string directory,SyncSharkUpdaterContract.Member[] members,bool protect)
    {
        Shape(directory,protect);
        foreach(var member in members)
        {
            string path=Path.Combine(directory,member.Path.Replace('/',Path.DirectorySeparatorChar));
            Hold(path,false,protect);
            SyncSharkUpdaterContract.Require(new FileInfo(path).Length==member.Bytes && Hash(path,protect)==member.Sha256,"Held runtime member changed.");
        }
    }
    private void Shape(string directory,bool protect)
    {
        Hold(directory,true,protect);
        string[] actual=Directory.GetFileSystemEntries(directory),required=SyncSharkProtectedRuntimeTransaction.Members;
        SyncSharkUpdaterContract.Require(actual.Length==required.Length,"Unexpected runtime root inventory.");
        foreach(string path in actual)
        {
            string name=Path.GetFileName(path);
            if(name=="Assets")
            {
                Hold(path,true,protect);string[] assets=Directory.GetFileSystemEntries(path);
                SyncSharkUpdaterContract.Require(assets.Length==1 && Path.GetFileName(assets[0])=="SyncShark-64.png","Unexpected runtime assets.");
                Hold(assets[0],false,protect);
            }
            else
            {
                SyncSharkUpdaterContract.Require(required.Contains(name,StringComparer.Ordinal),"Unexpected runtime member name.");
                Hold(path,false,protect);
            }
        }
    }
    internal void PartialRuntime(string directory,SyncSharkUpdaterContract.Member[] members,bool protect)
    {
        Hold(directory,true,protect);
        string[] entries=Directory.GetFileSystemEntries(directory);
        SyncSharkUpdaterContract.Require(entries.Length<=members.Length,"Unbounded runtime staging inventory.");
        foreach(string entry in entries)
        {
            string name=Path.GetFileName(entry);
            if(name=="Assets")
            {
                Hold(entry,true,protect);string[] assets=Directory.GetFileSystemEntries(entry);
                SyncSharkUpdaterContract.Require(assets.Length<=1,"Unexpected staging assets.");
                foreach(string asset in assets)PartialMember(asset,"Assets/"+Path.GetFileName(asset),members,protect);
            }
            else PartialMember(entry,name,members,protect);
        }
    }
    internal static string VersionEntry(string name,string successor,out bool staging)
    {
        staging=name.EndsWith(".installing",StringComparison.Ordinal);
        if(staging)name=name.Substring(0,name.Length-11);
        else if(name.EndsWith(".uninstalled",StringComparison.Ordinal))name=name.Substring(0,name.Length-12);
        SyncSharkUpdaterContract.Require(SyncSharkUpdaterContract.IsVersion(name),"Unrecognised protected version state.");
        SyncSharkUpdaterContract.Require(!staging || name==successor,"Another protected update is incomplete.");return name;
    }
    internal string HighestVersion(SyncSharkUpdaterContract contract)
    {
        string versions=Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"versions");Hold(versions,true,true);
        string[] entries=Directory.GetFileSystemEntries(versions);
        SyncSharkUpdaterContract.Require(entries.Length>0 && entries.Length<=128,"Protected version inventory is missing or unbounded.");
        Version highest=new Version(0,0,0);
        foreach(string entry in entries)
        {
            bool staging;string name=VersionEntry(Path.GetFileName(entry),contract.Version,out staging);
            // Unpublished exact staging stays renameable; it is not a published
            // high-water mark. Retired releases still prevent stale replay.
            if(staging){using(var stage=new SyncSharkUpdaterFiles(medium))stage.PartialRuntime(entry,contract.Members,true);continue;}
            Hold(entry,true,true);Version version=Version.Parse(name);if(version>highest)highest=version;
        }
        return highest.ToString(3);
    }
    private void PartialMember(string path,string name,SyncSharkUpdaterContract.Member[] members,bool protect)
    {
        var member=members.SingleOrDefault(m=>m.Path==name);
        SyncSharkUpdaterContract.Require(member!=null,"Unexpected runtime staging member.");
        Hold(path,false,protect);
        SyncSharkUpdaterContract.Require(new FileInfo(path).Length==member.Bytes && Hash(path,protect)==member.Sha256,
            "Interrupted staging bytes are not an exact authenticated member; preserve for recovery.");
    }
    [DataContract] private sealed class TreeMember
    {
        [DataMember(Name="path",Order=0)] public string Path;
        [DataMember(Name="length",Order=1)] public long Length;
        [DataMember(Name="sha256",Order=2)] public string Sha256;
    }
    internal string TreeIdentity(string directory,bool protect)
    {
        Shape(directory,protect);
        long total=0;
        foreach(string name in SyncSharkProtectedRuntimeTransaction.Members)
        {
            long size=new FileInfo(Path.Combine(directory,name)).Length;
            SyncSharkUpdaterContract.Require(size>0 && size<=536870912,"Predecessor member exceeds its inspection bound.");
            total=checked(total+size);
        }
        SyncSharkUpdaterContract.Require(total<=1073741824,"Predecessor tree exceeds its inspection bound.");
        var rows=SyncSharkProtectedRuntimeTransaction.Members.OrderBy(name=>Path.Combine(directory,name),StringComparer.InvariantCultureIgnoreCase)
            .Select(name=>new TreeMember {Path=name,Length=new FileInfo(Path.Combine(directory,name)).Length,Sha256=Hash(Path.Combine(directory,name),protect)}).ToArray();
        using(var stream=new MemoryStream())
        using(var sha=SHA256.Create())
        {
            new DataContractJsonSerializer(typeof(TreeMember[])).WriteObject(stream,rows);
            // Match the existing unelevated release manifest's exact JSON bytes.
            string json=Encoding.UTF8.GetString(stream.ToArray()).Replace("\\/","/");
            return Hex(sha.ComputeHash(new UTF8Encoding(false).GetBytes(json)));
        }
    }
    // Read-only admission must reject obstructing evidence before publication,
    // not discover a foreign temporary only after runtime/Discovery effects.
    internal static void ValidateReceiptDestination(string directory,string content,IntPtr medium,bool protect)
    {
        bool staged;
        InspectReceipt(directory,ReceiptBytes(content),medium,protect,out staged);
    }
    internal static string ReceiptContent(SyncSharkUpdaterContract contract,string tree)
    {
        SyncSharkUpdaterContract.Require(SyncSharkUpdaterContract.IsHex(tree,64),"Missing authenticated publication tree.");
        return "{\"schemaVersion\":1,\"product\":\"Massdex.SyncShark\",\"version\":\""+contract.ProductVersion+
            "\",\"installerSha256\":\""+contract.InstallerSha256+"\",\"previousTreeSha256\":\""+contract.PreviousTree+
            "\",\"publishedTreeSha256\":\""+tree+"\"}";
    }
    private static byte[] ReceiptBytes(string content)
    {
        var bytes=new UTF8Encoding(false,true).GetBytes(content);
        SyncSharkUpdaterContract.Require(bytes.Length>0 && bytes.Length<=4096,"Invalid publication evidence length.");
        return bytes;
    }
    private static bool InspectReceipt(string directory,byte[] bytes,IntPtr medium,bool protect,out bool staged)
    {
        string expected;using(var hash=SHA256.Create())expected=Hex(hash.ComputeHash(bytes));
        string receipt=Path.Combine(directory,"updater-publication.json"),temporary=receipt+".new";
        using(var current=new SyncSharkUpdaterFiles(medium))
        {
            current.Ancestors(directory,protect);
            SyncSharkUpdaterContract.Require(!Directory.Exists(receipt) && !Directory.Exists(temporary),"Unexpected publication evidence directory; preserved.");
            staged=File.Exists(temporary);
            if(staged)SyncSharkUpdaterContract.Require(new FileInfo(temporary).Length==bytes.Length && current.Hash(temporary,protect)==expected,
                "Publication evidence staging differs from the verified result; it was preserved.");
            if(!File.Exists(receipt))return false;
            SyncSharkUpdaterContract.Require(new FileInfo(receipt).Length<=4096,"Oversized publication evidence; preserved.");
            return new FileInfo(receipt).Length==bytes.Length && current.Hash(receipt,protect)==expected;
        }
    }
    // Evidence only. The caller has already authenticated the signed plan and
    // actual completed publication under the exclusive protected setup claim.
    // The false protection mode is reachable only from the isolated test fixture.
    internal static void PublishReceipt(string directory,string content,IntPtr medium,bool protect)
    {
        var bytes=ReceiptBytes(content);
        string expected;using(var hash=SHA256.Create())expected=Hex(hash.ComputeHash(bytes));
        string receipt=Path.Combine(directory,"updater-publication.json"),temporary=receipt+".new";
        using(var root=new SyncSharkUpdaterFiles(medium))
        {
            root.Ancestors(directory,protect);
            bool staged;
            bool complete=InspectReceipt(directory,bytes,medium,protect,out staged);
            if(complete)
            {
                if(staged)File.Delete(temporary);
                return; // Do not rewrite or repeat completed publication.
            }
            if(!staged)
                using(var stream=protect?SyncSharkDiscoverySetupNative.CreateProtectedFile(temporary,true):
                    new FileStream(temporary,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
                {stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
            if(File.Exists(receipt))File.Replace(temporary,receipt,null);else File.Move(temporary,receipt);
            using(var check=new SyncSharkUpdaterFiles(medium))
                SyncSharkUpdaterContract.Require(new FileInfo(receipt).Length==bytes.Length && check.Hash(receipt,protect)==expected,"Publication evidence readback failed.");
        }
    }
    public void Dispose()
    {for(int i=leases.Count-1;i>=0;i--)leases[i].Dispose();leases.Clear();paths.Clear();protectedPaths.Clear();}
}
