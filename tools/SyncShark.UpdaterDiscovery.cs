using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

// Shared by initial attended enrollment and the routine fixed updater. This is
// narrowly bound to the existing Discovery journal and a signed enabled preimage.
internal static class SyncSharkUpdaterDiscovery
{
    internal static bool Inspect(SyncSharkUpdaterContract contract,IntPtr medium)
    {
        string journal=Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"discovery-setup.json");
        if(!File.Exists(journal) && !File.Exists(journal+".new"))return false;
        SyncSharkUpdaterContract.Require(contract.Discovery.Enabled,"Disabled Discovery cannot grant recovery.");
        using(var files=new SyncSharkUpdaterFiles(medium))
        {if(File.Exists(journal))files.SmallText(journal,65536,true);if(File.Exists(journal+".new"))files.SmallText(journal+".new",65536,true);
         return SyncSharkDiscoveryTransaction.InspectConfigure(SyncSharkUpdaterContract.ProtectedRoot,contract.UserSid,
            contract.Discovery.BeforeWorker,contract.Discovery.BeforeManifest,contract.Discovery.AfterWorker,contract.Discovery.AfterManifest,true);}
    }
    internal static void Validate(SyncSharkDiscoveryTransaction transaction,SyncSharkUpdaterContract contract)
    {
        SyncSharkUpdaterContract.Require(contract.Discovery.Enabled,"Only previously enabled Discovery may recover.");
        transaction.ValidateConfigure(contract.Discovery.BeforeWorker,contract.Discovery.BeforeManifest,contract.Discovery.AfterWorker,contract.Discovery.AfterManifest);
    }
    internal static bool Enabled(string user,SyncSharkUpdaterFiles files)
    {
        object service=null,folder=null,task=null;
        try
        {
            service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));((dynamic)service).Connect();folder=((dynamic)service).GetFolder(@"\");
            task=((dynamic)folder).GetTask("SyncSharkDiscovery");
            string worker=Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"SyncSharkDiscovery.exe");
            files.Hold(worker,false,true);SyncSharkUpdaterSignature.Verify(worker,false);
            return SyncSharkUpdaterTask.ValidateDiscovery((string)((dynamic)task).Xml,(string)((dynamic)task).GetSecurityDescriptor(7),user,FileVersionInfo.GetVersionInfo(worker).ProductVersion);
        }
        finally{Release(task);Release(folder);Release(service);}
    }
    internal static void Recover(SyncSharkDiscoveryTransaction transaction,SyncSharkUpdaterContract contract,IntPtr medium)
    {
        Validate(transaction,contract);
        RecoverBound(transaction,contract.UserSid,medium);
    }
    // The caller has already authenticated the exact journal transition. Both
    // update and original-pair restoration keep its fence through task readback.
    internal static void RecoverBound(SyncSharkDiscoveryTransaction transaction,string user,IntPtr medium)
    {
        object service=null,folder=null,task=null,registered=null;
        try
        {
            service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));((dynamic)service).Connect();folder=((dynamic)service).GetFolder(@"\");
            task=((dynamic)folder).GetTask("SyncSharkDiscovery");
            using(var files=new SyncSharkUpdaterFiles(medium))Enabled(user,files);
            ((dynamic)task).Enabled=false;((dynamic)task).Stop(0);
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while(((dynamic)task).GetInstances(0).Count!=0)
            {SyncSharkUpdaterContract.Require(DateTime.UtcNow<deadline,"Discovery worker did not stop for recovery.");System.Threading.Thread.Sleep(100);}
            // The signed disposition restores the exact preimage unless the
            // durable commit already proves the complete postimage.
            transaction.RecoverRetainingFence();
            string worker=Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"SyncSharkDiscovery.exe");
            string version=FileVersionInfo.GetVersionInfo(worker).ProductVersion;
            SyncSharkUpdaterSignature.Verify(worker,false);
            var xml=System.Xml.Linq.XElement.Parse((string)((dynamic)task).Xml);
            System.Xml.Linq.XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
            xml.Element(ns+"Settings").Element(ns+"ExecutionTimeLimit").Value=
                Version.Parse(version.Split('+')[0])>=new Version(0,3,32)?"PT31M":"PT2M";
            xml.Element(ns+"Settings").Element(ns+"Enabled").Value="true";
            registered=((dynamic)folder).RegisterTask("SyncSharkDiscovery",xml.ToString(),4|0x10,user,null,3,SyncSharkUpdaterTask.Security(user));
            SyncSharkUpdaterContract.Require(SyncSharkUpdaterTask.ValidateDiscovery((string)((dynamic)registered).Xml,
                (string)((dynamic)registered).GetSecurityDescriptor(7),user,version),"Recovered Discovery is not enabled.");
            transaction.FinishRecovery();
        }
        finally{Release(registered);Release(task);Release(folder);Release(service);}
    }
    private static void Release(object value){if(value!=null && Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
}

// A proof-only, signed extension of the existing RuntimeRollback. It reads data
// from the retained rollback script, never executes it, and publishes only the
// existing four Discovery members through the existing Restore transaction.
public sealed class SyncSharkOriginalDiscovery : IDisposable
{
    internal const string Prefix="# SyncSharkProofRestoration: ";
    private readonly IntPtr medium;
    private readonly SyncSharkUpdaterFiles files;
    private SyncSharkUpdaterContract owner;
    private Pair current,previous;
    private string successor;
    private string[] before,after;
    internal sealed class Pair
    {
        internal byte[] Manifest;
        internal string Version,WorkerHash,ManifestHash,TreeHash,Root,Worker;
        internal SyncSharkUpdaterContract.Member[] Members;
    }
    private SyncSharkOriginalDiscovery(IntPtr token)
    {medium=token;files=new SyncSharkUpdaterFiles(token);}
    public string CurrentWorker {get{return current.Worker;}}
    public string PreviousWorker {get{return previous.Worker;}}
    public byte[] CurrentManifest {get{return (byte[])current.Manifest.Clone();}}
    public byte[] PreviousManifest {get{return (byte[])previous.Manifest.Clone();}}
    public string[] ExpectedBefore {get{return (string[])before.Clone();}}
    public static SyncSharkOriginalDiscovery Prepare(string productVersion)
    {
        var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
        SyncSharkUpdaterContract.Require(new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) &&
            SyncSharkUpdaterContract.IsProductVersion(productVersion),"Original-pair restoration requires the existing attended signed rollback boundary.");
        var value=new SyncSharkOriginalDiscovery(SyncSharkDiscoverySetupNative.MediumToken());
        try
        {
            value.owner=value.ReadContract(productVersion);
            string rollback=Path.Combine(value.Transaction(productVersion),"Rollback-SyncShark.ps1");
            string text=value.files.SmallText(rollback,1048576,false);SyncSharkUpdaterSignature.Verify(rollback,false);
            string header,binding;using(var reader=new StringReader(text)){header=reader.ReadLine();binding=reader.ReadLine();}
            string installer=value.files.SmallText(Path.Combine(value.Transaction(productVersion),"Install-SyncShark.ps1"),1048576,false);
            using(var reader=new StringReader(installer))SyncSharkUpdaterContract.Require(header==reader.ReadLine(),"Rollback and install contracts differ.");
            if(binding==null || !binding.StartsWith(Prefix,StringComparison.Ordinal)){value.Dispose();return null;}
            value.ReadBinding(binding.Substring(Prefix.Length));
            SyncSharkUpdaterContract.Require(value.owner.UserSid==identity.User.Value && value.owner.Discovery.Enabled,"Original-pair restoration needs its signed enabled Discovery preimage.");
            value.VerifyPair(value.current);value.VerifyPair(value.previous);
            value.after=new[]{value.current.WorkerHash,value.current.ManifestHash,value.previous.WorkerHash,value.previous.ManifestHash};
            SyncSharkUpdaterContract.Require(value.current.Version==value.owner.PreviousVersion &&
                value.current.WorkerHash==value.owner.Discovery.BeforeWorker && value.current.ManifestHash==value.owner.Discovery.BeforeManifest &&
                Version.Parse(value.previous.Version.Split('+')[0])<Version.Parse(value.current.Version.Split('+')[0]),"Original P/Q does not match the signed enrollment predecessor.");
            return value;
        }
        catch{value.Dispose();throw;}
    }
    private string Transaction(string productVersion)
    {var parts=productVersion.Split('+');return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Massdex","SyncShark","replacement-staging","transactions",parts[0]+"-"+parts[1].Substring(0,7));}
    private SyncSharkUpdaterContract ReadContract(string productVersion)
    {
        string directory=Transaction(productVersion);files.Ancestors(directory,false);
        string path=Path.Combine(directory,"Install-SyncShark.ps1"),text=files.SmallText(path,1048576,false);
        SyncSharkUpdaterSignature.Verify(path,false);string header;using(var reader=new StringReader(text))header=reader.ReadLine();
        var parts=productVersion.Split('+');return SyncSharkUpdaterData.ReadSignedHeader(header,new SyncSharkUpdaterData.PendingPlan(parts[0],parts[1],files.Hash(path,false)));
    }
    private void ReadBinding(string json)
    {
        var data=SyncSharkUpdaterData.Object(SyncSharkUpdaterData.Read(json,16384),"schemaVersion","successorVersion","current","previous");
        successor=SyncSharkUpdaterData.Text(data["successorVersion"]);
        SyncSharkUpdaterContract.Require(SyncSharkUpdaterData.Integer(data["schemaVersion"])==1 && SyncSharkUpdaterContract.IsVersion(successor) &&
            Version.Parse(successor)>Version.Parse(owner.Version),"Invalid fixed proof successor.");
        current=ReadPair(data["current"],owner.UserSid);previous=ReadPair(data["previous"],owner.UserSid);
    }
    internal static Pair ReadPair(System.Xml.Linq.XElement element,string user)
    {
        var data=SyncSharkUpdaterData.Object(element,"manifestBase64","manifestSha256","treeSha256");
        var pair=new Pair {ManifestHash=SyncSharkUpdaterData.Text(data["manifestSha256"]),TreeHash=SyncSharkUpdaterData.Text(data["treeSha256"])};
        SyncSharkUpdaterContract.Require(SyncSharkUpdaterContract.IsHex(pair.ManifestHash,64) && SyncSharkUpdaterContract.IsHex(pair.TreeHash,64),"Missing original-pair hashes.");
        string base64=SyncSharkUpdaterData.Text(data["manifestBase64"]);
        try{pair.Manifest=Convert.FromBase64String(base64);}catch(FormatException error){throw new InvalidDataException("Invalid original manifest bytes.",error);}
        SyncSharkUpdaterContract.Require(pair.Manifest.Length>0 && pair.Manifest.Length<=8192 && Convert.ToBase64String(pair.Manifest)==base64,"Noncanonical or oversized original manifest.");
        using(var sha=System.Security.Cryptography.SHA256.Create())SyncSharkUpdaterContract.Require(SyncSharkUpdaterFiles.Hex(sha.ComputeHash(pair.Manifest))==pair.ManifestHash,"Original manifest hash changed.");
        var manifest=SyncSharkUpdaterData.Object(SyncSharkUpdaterData.Read(new System.Text.UTF8Encoding(false,true).GetString(pair.Manifest),8192),
            "schemaVersion","version","userSid","appDirectory","workerSha256","members");
        pair.Version=SyncSharkUpdaterData.Text(manifest["version"]);pair.WorkerHash=SyncSharkUpdaterData.Text(manifest["workerSha256"]);
        SyncSharkUpdaterContract.Require(SyncSharkUpdaterContract.IsProductVersion(pair.Version) && SyncSharkUpdaterContract.IsHex(pair.WorkerHash,64) &&
            SyncSharkUpdaterData.Integer(manifest["schemaVersion"])==1 && SyncSharkUpdaterData.Text(manifest["userSid"])==user,"Original manifest identity changed.");
        pair.Root=Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"versions",pair.Version.Split('+')[0]);pair.Worker=Path.Combine(pair.Root,"SyncSharkDiscovery.exe");
        SyncSharkUpdaterContract.Require(SyncSharkUpdaterData.Text(manifest["appDirectory"])==pair.Root,"Original manifest cannot select a restore path.");
        pair.Members=SyncSharkUpdaterData.Array(manifest["members"],16).Select(row=>
        {var member=SyncSharkUpdaterData.Object(row,"path","sha256");return new SyncSharkUpdaterContract.Member(SyncSharkUpdaterData.Text(member["path"]).Replace('\\','/'),SyncSharkUpdaterData.Text(member["sha256"]),0);}).ToArray();
        SyncSharkUpdaterContract.Require(pair.Members.Length==16 && pair.Members.Select(m=>m.Path).Distinct(StringComparer.Ordinal).Count()==16 &&
            pair.Members.All(m=>SyncSharkProtectedRuntimeTransaction.Members.Contains(m.Path,StringComparer.Ordinal) && SyncSharkUpdaterContract.IsHex(m.Sha256,64)) &&
            pair.Members.Single(m=>m.Path=="SyncSharkDiscovery.exe").Sha256==pair.WorkerHash,"Original manifest member set changed.");
        return pair;
    }
    private void VerifyPair(Pair pair)
    {
        files.Ancestors(pair.Root,true);
        SyncSharkUpdaterContract.Require(files.TreeIdentity(pair.Root,true)==pair.TreeHash,"Retained original runtime changed.");
        foreach(var member in pair.Members)SyncSharkUpdaterContract.Require(files.Hash(Path.Combine(pair.Root,member.Path),true)==member.Sha256,"Original manifest differs from retained runtime.");
        foreach(string name in new[]{"SyncShark.exe","SyncShark.dll","SyncShark.Core.dll","SyncSharkDiscovery.exe","SyncShark.Explorer.dll","SyncShark.msix"})
        {
            string path=Path.Combine(pair.Root,name);SyncSharkUpdaterSignature.Verify(path,name=="SyncShark.msix");
            if(name!="SyncShark.Explorer.dll" && name!="SyncShark.msix")SyncSharkUpdaterContract.Require(FileVersionInfo.GetVersionInfo(path).ProductVersion==pair.Version,"Original runtime revisions differ.");
        }
    }
    private string[] CurrentHashes()
    {using(var held=new SyncSharkUpdaterFiles(medium))return new[]{"SyncSharkDiscovery.exe","discovery-manifest.json","SyncSharkDiscovery.exe.previous","discovery-manifest.json.previous"}.Select(n=>held.Hash(Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,n),true)).ToArray();}
    public bool ValidateBefore(SyncSharkDiscoveryTransaction transaction)
    {
        bool enabled;
        using(var taskFiles=new SyncSharkUpdaterFiles(medium))enabled=SyncSharkUpdaterDiscovery.Enabled(owner.UserSid,taskFiles);
        SyncSharkUpdaterContract.Require(enabled || transaction.HasPending,"Original restoration cannot enable an independently disabled task.");
        // Already-restored bytes are a readback, not permission to repeat effects.
        if((!transaction.HasPending || transaction.UpdaterPending) && CurrentHashes().SequenceEqual(after))
        {SyncSharkUpdaterContract.Require(enabled,"Original pair task readback is not enabled.");return true;}
        before=new[]{owner.Discovery.AfterWorker,owner.Discovery.AfterManifest,current.WorkerHash,current.ManifestHash};
        if(transaction.HasPending)
        {try{transaction.ValidateRestore(before,after);return false;}catch(InvalidDataException){throw;}catch(IOException){}}
        else if(CurrentHashes().SequenceEqual(before))return false;
        var second=ReadContract(successor+"+"+owner.SourceRevision);
        SyncSharkUpdaterContract.Require(second.PreviousVersion==owner.ProductVersion && second.Discovery.Enabled && second.UserSid==owner.UserSid &&
            second.Discovery.BeforeWorker==owner.Discovery.AfterWorker && second.Discovery.BeforeManifest==owner.Discovery.AfterManifest,"Proof successor is not the same-source exact transition.");
        files.Ancestors(second.Destination,true);files.Runtime(second.Destination,second.Members,true);
        foreach(string name in new[]{"SyncShark.exe","SyncShark.dll","SyncShark.Core.dll","SyncSharkDiscovery.exe","SyncShark.Explorer.dll","SyncShark.msix"})SyncSharkUpdaterSignature.Verify(Path.Combine(second.Destination,name),name=="SyncShark.msix");
        before=new[]{owner.Discovery.AfterWorker,owner.Discovery.AfterManifest,second.Discovery.AfterWorker,second.Discovery.AfterManifest};
        if(transaction.HasPending)transaction.ValidateRestore(before,after);
        else SyncSharkUpdaterContract.Require(CurrentHashes().SequenceEqual(before),"Discovery is not the exact original or proof rollback state.");
        return false;
    }
    public void Recover(SyncSharkDiscoveryTransaction transaction)
    {transaction.ValidateRestore(before,after);SyncSharkUpdaterDiscovery.RecoverBound(transaction,owner.UserSid,medium);}
    public void Dispose(){files.Dispose();SyncSharkDiscoverySetupNative.CloseToken(medium);}
}
