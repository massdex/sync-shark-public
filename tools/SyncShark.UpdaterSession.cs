#if NET8_0_OR_GREATER
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;

// Public only for the signed embedded bootstrap. There is no command-line path,
// operation, hash, code, destination or identity parameter. The one integer is the
// fixed native bootstrap's own PID, supplied inside its authenticated resource pipe.
public sealed class SyncSharkUpdaterSession : IDisposable
{
    private readonly IntPtr medium;
    private readonly SyncSharkUpdaterFiles held;
    private readonly List<IDisposable> additional=new List<IDisposable>();
    private SyncSharkUpdaterFiles discoveryHeld;
    private SyncSharkUpdaterAdmission admission;
    private string source,product,publicationReceipt;
    private Process parent;
    private SyncSharkDiscoveryTransaction claim;
    private byte[] discoveryCandidate;
    private bool published,discoveryStarted,discoveryVerified,disposed;
    private SyncSharkUpdaterSession()
    {medium=SyncSharkDiscoverySetupNative.MediumToken();held=new SyncSharkUpdaterFiles(medium);discoveryHeld=new SyncSharkUpdaterFiles(medium);}

    public string Destination {get{return admission.Contract.Destination;}}
    public bool NeedsDiscoveryRebind {get{return admission.RebindDiscovery;}}
    public string ExpectedDiscoveryManifestSha256 {get{return admission.Contract.Discovery.AfterManifest;}}
    public string PreviousProductVersion {get{return admission.Contract.PreviousVersion;}}

    public static SyncSharkUpdaterSession Prepare(int bootstrapProcessId)
    {
        var identity=WindowsIdentity.GetCurrent();
        SyncSharkUpdaterContract.Require(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),"Updater needs its accepted HighestAvailable split token.");
        var session=new SyncSharkUpdaterSession();
        try {session.Inspect(bootstrapProcessId,identity.User.Value);return session;}
        catch {session.Dispose();throw;}
    }
    private void Inspect(int bootstrapProcessId,string user)
    {
        parent=Process.GetProcessById(bootstrapProcessId);additional.Add(parent);
        SyncSharkUpdaterContract.Require(parent.SessionId==Process.GetCurrentProcess().SessionId && parent.SessionId!=0 &&
            String.Equals(parent.MainModule.FileName,SyncSharkUpdaterContract.WorkerPath,StringComparison.OrdinalIgnoreCase),"Updater bootstrap identity/session changed.");
        held.Ancestors(SyncSharkUpdaterContract.ProtectedRoot,true);held.Hold(SyncSharkUpdaterContract.WorkerPath,false,true);
        SyncSharkUpdaterSignature.Verify(SyncSharkUpdaterContract.WorkerPath,false);
        ValidateTask(user);
        product=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Massdex","SyncShark");
        held.Ancestors(Path.Combine(product,"replacement-staging"),false);
        var pending=SyncSharkUpdaterData.ReadPendingPlan(held.SmallText(Path.Combine(product,SyncSharkUpdaterContract.PendingPlanRelativePath),1024,false));
        string transaction=Path.Combine(product,"replacement-staging","transactions",pending.TransactionName);
        held.Ancestors(transaction,false);
        string installer=Path.Combine(transaction,"Install-SyncShark.ps1");
        string text=held.SmallText(installer,1048576,false);
        SyncSharkUpdaterContract.Require(held.Hash(installer,false)==pending.InstallerSha256,"Retained signed installer changed.");
        SyncSharkUpdaterSignature.Verify(installer,false);
        string header;
        using(var reader=new StringReader(text))header=reader.ReadLine();
        var contract=SyncSharkUpdaterData.ReadSignedHeader(header,pending);
        SyncSharkUpdaterContract.Require(contract.UserSid==user,"Signed update belongs to another interactive account.");
        SyncSharkUpdaterContract.Require(new FileInfo(SyncSharkUpdaterContract.WorkerPath).Length==contract.UpdaterWorker.Bytes &&
            held.Hash(SyncSharkUpdaterContract.WorkerPath,true)==contract.UpdaterWorker.Sha256,"Fixed updater differs from the signed release binding.");
        if(contract.Discovery.Enabled)
        {
            discoveryCandidate=SyncSharkUpdaterManifest.Discovery(contract);
            using(var sha=System.Security.Cryptography.SHA256.Create())
                SyncSharkUpdaterContract.Require(SyncSharkUpdaterFiles.Hex(sha.ComputeHash(discoveryCandidate))==contract.Discovery.AfterManifest,
                    "The signed Discovery postimage cannot be reconstructed from the authenticated runtime.");
        }
        source=Path.Combine(product,"versions",contract.Version);held.Ancestors(source,false);held.Runtime(source,contract.Members,false);
        foreach(string member in new[]{"SyncShark.exe","SyncShark.dll","SyncShark.Core.dll","SyncSharkDiscovery.exe","SyncShark.Explorer.dll","SyncShark.msix"})
        {
            string path=Path.Combine(source,member);SyncSharkUpdaterSignature.Verify(path,member=="SyncShark.msix");
            if(member=="SyncShark.exe" || member=="SyncShark.dll" || member=="SyncShark.Core.dll" || member=="SyncSharkDiscovery.exe")
                SyncSharkUpdaterContract.Require(FileVersionInfo.GetVersionInfo(path).ProductVersion==contract.ProductVersion,"Runtime source revisions differ.");
        }
        string versions=Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"versions"),previous=Path.Combine(versions,contract.PreviousVersion.Split('+')[0]);
        held.Ancestors(previous,true);
        string previousTree=held.TreeIdentity(previous,true),previousVersion=FileVersionInfo.GetVersionInfo(Path.Combine(previous,"SyncShark.exe")).ProductVersion;
        SyncSharkUpdaterSignature.Verify(Path.Combine(previous,"SyncShark.exe"),false);
        bool publication=SyncSharkUpdaterData.PublicationStepPending(held.SmallText(Path.Combine(transaction,"install-journal.json"),65536,false),contract,product);
        additional.Add(SyncSharkUpdaterWindows.HoldJunction(Path.Combine(product,"active"),publication?previous:contract.Destination));
        bool targetExists=Directory.Exists(contract.Destination);
        if(targetExists)held.Runtime(contract.Destination,contract.Members,true);
        string highest=held.HighestVersion(contract);
        string worker=Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"SyncSharkDiscovery.exe"),manifest=Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"discovery-manifest.json");
        bool enabled=DiscoveryEnabled(user);
        bool recovery=SyncSharkUpdaterDiscovery.Inspect(contract,medium);
        string beforeWorker=null,beforeManifest=null;
        if(enabled)
        {
            beforeWorker=discoveryHeld.Hash(worker,true);beforeManifest=discoveryHeld.Hash(manifest,true);
            SyncSharkUpdaterSignature.Verify(worker,false);
        }
        var observed=new SyncSharkUpdaterObservation {
            ElevatedSplitToken=true,FixedWorkerIdentity=true,ProtectedAclAndPaths=true,ExactTaskIdentity=true,
            AuthenticatedInstaller=true,AuthenticatedRuntime=true,SourceShapeAndHashes=true,PredecessorShapeAndHashes=true,
            JournalIdentity=true,JournalCompatible=true,PublicationStepPending=publication,RollbackAvailable=true,
            ProductStopped=!ProductRunning(),UserSid=user,PreviousProductVersion=previousVersion,PreviousTree=previousTree,HighestPublishedVersion=highest,
            TargetExists=targetExists,ExactTarget=targetExists,DiscoveryEnabled=enabled,
            DiscoveryPending=recovery,
            DiscoveryRecoveryBound=recovery,
            DiscoveryWorker=beforeWorker,DiscoveryManifest=beforeManifest};
        admission=SyncSharkUpdaterAdmission.Evaluate(contract,observed);
        publicationReceipt=SyncSharkUpdaterFiles.ReceiptContent(contract,held.TreeIdentity(source,false));
        SyncSharkUpdaterFiles.ValidateReceiptDestination(SyncSharkUpdaterContract.ProtectedRoot,publicationReceipt,medium,true);
        parent.Refresh();SyncSharkUpdaterContract.Require(!parent.HasExited,"Updater bootstrap ended during admission.");
        // Only now may the existing protected claim be opened. Exact unpublished
        // journal reconciliation is repeated there, never during read-only admission.
    }
    private static bool ProductRunning()
    {var processes=Process.GetProcessesByName("SyncShark");try{return processes.Length!=0;}finally{foreach(var process in processes)process.Dispose();}}
    private static void ValidateTask(string user)
    {
        object service=null,folder=null,task=null;
        try
        {
            service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));((dynamic)service).Connect();
            folder=((dynamic)service).GetFolder(@"\");task=((dynamic)folder).GetTask("SyncSharkUpdater");
            SyncSharkUpdaterTask.Validate((string)((dynamic)task).Xml,(string)((dynamic)task).GetSecurityDescriptor(7),user);
        }
        finally {Release(task);Release(folder);Release(service);}
    }
    private bool DiscoveryEnabled(string user)
    {
        object service=null,folder=null,task=null;
        try
        {
            service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));((dynamic)service).Connect();folder=((dynamic)service).GetFolder(@"\");
            try {task=((dynamic)folder).GetTask("SyncSharkDiscovery");}
            catch(COMException error){if(error.ErrorCode==unchecked((int)0x80070002))return false;throw;}
            string worker=Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"SyncSharkDiscovery.exe");
            discoveryHeld.Hold(worker,false,true);SyncSharkUpdaterSignature.Verify(worker,false);
            return SyncSharkUpdaterTask.ValidateDiscovery((string)((dynamic)task).Xml,(string)((dynamic)task).GetSecurityDescriptor(7),
                user,FileVersionInfo.GetVersionInfo(worker).ProductVersion);
        }
        finally {Release(task);Release(folder);Release(service);}
    }
    private static void Release(object value) {if(value!=null && Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}

    public void PublishRuntime()
    {
        SyncSharkUpdaterContract.Require(admission!=null && !published,"Updater publication called out of order.");
        ValidateTask(admission.Contract.UserSid);SyncSharkUpdaterContract.Require(!ProductRunning(),"Product reopened before publication.");
        claim=new SyncSharkDiscoveryTransaction(SyncSharkUpdaterContract.ProtectedRoot,admission.Contract.UserSid,true);
        if(claim.HasPending)SyncSharkUpdaterDiscovery.Validate(claim,admission.Contract);
        // Recheck under the existing exclusive claim before the first runtime
        // mutation. Receipt bytes are never evidence of publication authority.
        SyncSharkUpdaterFiles.ValidateReceiptDestination(SyncSharkUpdaterContract.ProtectedRoot,publicationReceipt,medium,true);
        var members=admission.Contract.Members;
        var transaction=new SyncSharkProtectedRuntimeTransaction(admission.Contract.Version,
            SyncSharkProtectedRuntimeTransaction.Members.Select(name=>members.Single(m=>m.Path==name).Sha256).ToArray(),
            SyncSharkProtectedRuntimeTransaction.Members.Select(name=>members.Single(m=>m.Path==name).Bytes).ToArray());
        SyncSharkUpdaterContract.Require(!claim.HasPending || !admission.PublishRuntime,"Discovery recovery requires its already-published exact runtime.");
        if(admission.PublishRuntime)transaction.Install(source);else transaction.Verify(Destination);
        published=true;
        claim.Dispose();claim=null;
    }
    public void BeginDiscoveryRebind()
    {
        SyncSharkUpdaterContract.Require(published && NeedsDiscoveryRebind && !discoveryStarted,"Discovery rebind called out of order.");
        discoveryHeld.Dispose();discoveryHeld=null;discoveryStarted=true;
    }
    public byte[] DiscoveryCandidate()
    {
        SyncSharkUpdaterContract.Require(discoveryStarted && discoveryCandidate!=null,"Discovery candidate requested out of order.");
        return (byte[])discoveryCandidate.Clone();
    }
    public void RecoverDiscovery(SyncSharkDiscoveryTransaction transaction)
    {SyncSharkUpdaterContract.Require(discoveryStarted,"Recovery called out of order.");SyncSharkUpdaterDiscovery.Recover(transaction,admission.Contract,medium);}
    public bool IsDiscoveryPostimage()
    {
        using(var current=new SyncSharkUpdaterFiles(medium))
            return current.Hash(Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"SyncSharkDiscovery.exe"),true)==admission.Contract.Discovery.AfterWorker &&
                current.Hash(Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"discovery-manifest.json"),true)==admission.Contract.Discovery.AfterManifest;
    }
    public void VerifyDiscoveryPreimage()
    {
        SyncSharkUpdaterContract.Require(discoveryStarted && !discoveryVerified,"Discovery preimage checked out of order.");
        using(var current=new SyncSharkUpdaterFiles(medium))
        {
            SyncSharkUpdaterContract.Require(current.Hash(Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"SyncSharkDiscovery.exe"),true)==admission.Contract.Discovery.BeforeWorker &&
                current.Hash(Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"discovery-manifest.json"),true)==admission.Contract.Discovery.BeforeManifest,
                "Discovery preimage changed after admission.");
        }
    }
    public void VerifyDiscoveryPostimage()
    {
        SyncSharkUpdaterContract.Require(published && (!NeedsDiscoveryRebind || discoveryStarted),"Discovery postimage checked out of order.");
        if(admission.Contract.Discovery.Enabled)
        {
            using(var state=new SyncSharkUpdaterFiles(medium))
                SyncSharkUpdaterContract.Require(SyncSharkUpdaterDiscovery.Enabled(admission.Contract.UserSid,state),"Discovery task was not restored to its signed enabled state.");
            using(var current=new SyncSharkUpdaterFiles(medium))
                SyncSharkUpdaterContract.Require(current.Hash(Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"SyncSharkDiscovery.exe"),true)==admission.Contract.Discovery.AfterWorker &&
                    current.Hash(Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"discovery-manifest.json"),true)==admission.Contract.Discovery.AfterManifest,
                    "Discovery postimage does not match the signed plan.");
        }
        discoveryVerified=true;
    }
    public void WritePublicationReceipt()
    {
        SyncSharkUpdaterContract.Require(published && discoveryVerified,"Publication evidence requested before complete verification.");
        using(var final=new SyncSharkUpdaterFiles(medium))
        using(var publicationClaim=new SyncSharkDiscoveryTransaction(SyncSharkUpdaterContract.ProtectedRoot,admission.Contract.UserSid,true))
        {
            SyncSharkUpdaterContract.Require(!publicationClaim.HasPending,"Discovery recovery appeared before publication readback.");
            ValidateTask(admission.Contract.UserSid);final.Runtime(Destination,admission.Contract.Members,true);
            string content=SyncSharkUpdaterFiles.ReceiptContent(admission.Contract,final.TreeIdentity(Destination,true));
            SyncSharkUpdaterContract.Require(content==publicationReceipt,"Published tree differs from its authenticated pre-effect identity.");
            SyncSharkUpdaterFiles.PublishReceipt(SyncSharkUpdaterContract.ProtectedRoot,content,medium,true);
        }
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        if(claim!=null){claim.Dispose();claim=null;}
        if(discoveryHeld!=null){discoveryHeld.Dispose();discoveryHeld=null;}
        held.Dispose();for(int i=additional.Count-1;i>=0;i--)additional[i].Dispose();additional.Clear();
        SyncSharkDiscoverySetupNative.CloseToken(medium);
    }
}
