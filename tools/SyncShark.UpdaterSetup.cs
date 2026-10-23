#if NET8_0_OR_GREATER
#nullable disable
#endif
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

// The attended bootstrap holds an effect-free admission object across runtime
// publication and Discovery rebind. No staged script is executed while elevated.
public sealed class SyncSharkUpdaterSetup : IDisposable
{
    private readonly IntPtr medium;
    private readonly SyncSharkUpdaterFiles files;
    private SyncSharkUpdaterFiles discoveryFiles;
    private SyncSharkUpdaterContract contract;
    private IDisposable active;
    private object service,folder,task;
    private string source;
    private byte[] candidate;
    private bool needsRebind,started,disposed,restoring;
    private SyncSharkUpdaterSetup()
    {medium=SyncSharkDiscoverySetupNative.MediumToken();files=new SyncSharkUpdaterFiles(medium);discoveryFiles=new SyncSharkUpdaterFiles(medium);}
    public bool NeedsDiscoveryRebind {get{return needsRebind;}}
    public static SyncSharkUpdaterSetup Prepare(string productVersion)
    {
        var identity=WindowsIdentity.GetCurrent();
        SyncSharkUpdaterContract.Require(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
            "Initial updater setup requires the existing attended administrator boundary.");
        SyncSharkUpdaterContract.Require(SyncSharkUpdaterContract.IsProductVersion(productVersion),"Frozen runtime identity required.");
        var setup=new SyncSharkUpdaterSetup();
        try{setup.Inspect(productVersion,identity.User.Value);return setup;}catch{setup.Dispose();throw;}
    }
    private void Inspect(string productVersion,string user)
    {
        string[] version=productVersion.Split('+');
        string product=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Massdex","SyncShark");
        string transaction=Path.Combine(product,"replacement-staging","transactions",version[0]+"-"+version[1].Substring(0,7));
        files.Ancestors(transaction,false);
        string installer=Path.Combine(transaction,"Install-SyncShark.ps1");
        string text=files.SmallText(installer,1048576,false);SyncSharkUpdaterSignature.Verify(installer,false);
        string header;using(var reader=new StringReader(text))header=reader.ReadLine();
        contract=SyncSharkUpdaterData.ReadSignedHeader(header,new SyncSharkUpdaterData.PendingPlan(version[0],version[1],files.Hash(installer,false)));
        SyncSharkUpdaterContract.Require(contract.UserSid==user,"Updater setup account changed.");
        string runtime=Path.Combine(product,"versions",contract.Version);files.Ancestors(runtime,false);files.Runtime(runtime,contract.Members,false);
        foreach(string member in new[]{"SyncShark.exe","SyncShark.dll","SyncShark.Core.dll","SyncSharkDiscovery.exe","SyncShark.Explorer.dll","SyncShark.msix"})
        {
            string path=Path.Combine(runtime,member);SyncSharkUpdaterSignature.Verify(path,member=="SyncShark.msix");
            if(member!="SyncShark.Explorer.dll" && member!="SyncShark.msix")
                SyncSharkUpdaterContract.Require(FileVersionInfo.GetVersionInfo(path).ProductVersion==productVersion,"Updater setup runtime revision changed.");
        }
        files.Ancestors(SyncSharkUpdaterContract.ProtectedRoot,true);
        string predecessor=Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"versions",contract.PreviousVersion.Split('+')[0]);
        SyncSharkUpdaterContract.Require(files.TreeIdentity(predecessor,true)==contract.PreviousTree &&
            FileVersionInfo.GetVersionInfo(Path.Combine(predecessor,"SyncShark.exe")).ProductVersion==contract.PreviousVersion,"Updater setup predecessor changed.");
        SyncSharkUpdaterSignature.Verify(Path.Combine(predecessor,"SyncShark.exe"),false);
        bool targetExists=Directory.Exists(contract.Destination);
        if(targetExists)files.Runtime(contract.Destination,contract.Members,true);
        Version highest;using(var versions=new SyncSharkUpdaterFiles(medium))highest=Version.Parse(versions.HighestVersion(contract));
        var successor=Version.Parse(contract.Version);
        bool publication=SyncSharkUpdaterData.PublicationStepPending(files.SmallText(Path.Combine(transaction,"install-journal.json"),65536,false),contract,product);
        if(!publication)
        {
            // Undo-uninstall is an attended restoration of exactly retained bytes,
            // never ordinary publication, downgrade or authority to enable Discovery.
            string retained=targetExists?contract.Destination:contract.Destination+".uninstalled";
            using(var previousRuntime=new SyncSharkUpdaterFiles(medium))
            {
                previousRuntime.Runtime(retained,contract.Members,true);
                SyncSharkUpdaterData.UninstallRestoration(files.SmallText(Path.Combine(transaction,"uninstall-journal.json"),65536,false),
                    contract,product,previousRuntime.TreeIdentity(retained,true));
            }
            SyncSharkUpdaterContract.Require(SyncSharkUpdaterWindows.Absent(Path.Combine(product,"active")),"An active installation prevents uninstall restoration.");
            active=SyncSharkUpdaterWindows.HoldJunction(Path.Combine(transaction,"active.uninstalled"),contract.Destination);restoring=true;
        }
        else active=SyncSharkUpdaterWindows.HoldJunction(Path.Combine(product,"active"),predecessor);
        SyncSharkUpdaterContract.Require(highest<=successor && (targetExists || highest<successor || restoring),"Initial setup would replay a retired, stale or conflicting release.");
        source=Path.Combine(transaction,"SyncSharkUpdater.exe");VerifyWorker(source,false);
        SyncSharkUpdaterContract.Require(SyncSharkUpdaterContract.IsProductVersion(FileVersionInfo.GetVersionInfo(source).ProductVersion),"Initial updater is not frozen.");
        service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));((dynamic)service).Connect();folder=((dynamic)service).GetFolder(@"\");
        task=FindTask("SyncSharkUpdater");
        if(task!=null)ValidateTask();
        if(File.Exists(SyncSharkUpdaterContract.WorkerPath))VerifyWorker(SyncSharkUpdaterContract.WorkerPath,true);
        bool enrollment=SyncSharkDiscoveryTransaction.InspectUpdater(SyncSharkUpdaterContract.ProtectedRoot,user,contract.UpdaterWorker.Sha256,false,true);
        bool recovery=!enrollment && SyncSharkUpdaterDiscovery.Inspect(contract,medium);
        SyncSharkUpdaterContract.Require(!restoring || !recovery,"Uninstall restoration must not widen Discovery recovery authority.");
        SyncSharkUpdaterContract.Require(!recovery || targetExists,"Discovery recovery needs the exact published successor.");
        object discovery=FindTask("SyncSharkDiscovery");
        try
        {
            if(restoring)
            {
                // Uninstall has retired Discovery separately. Undo restores the
                // app/updater but leaves that explicitly removed helper off.
                SyncSharkUpdaterContract.Require(discovery==null && !File.Exists(Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"SyncSharkDiscovery.exe")) &&
                    !File.Exists(Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"discovery-manifest.json")),"Unexpected Discovery state during uninstall restoration.");
                return;
            }
            bool enabled=false;
            string worker=Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"SyncSharkDiscovery.exe"),manifest=Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"discovery-manifest.json");
            if(discovery!=null)
            {
                discoveryFiles.Hold(worker,false,true);SyncSharkUpdaterSignature.Verify(worker,false);
                enabled=SyncSharkUpdaterTask.ValidateDiscovery((string)((dynamic)discovery).Xml,(string)((dynamic)discovery).GetSecurityDescriptor(7),
                    user,FileVersionInfo.GetVersionInfo(worker).ProductVersion);
            }
            SyncSharkUpdaterContract.Require(enabled==contract.Discovery.Enabled || recovery,"Discovery enablement differs from the signed plan.");
            if(enabled || recovery)
            {
                string workerHash=discoveryFiles.Hash(worker,true),manifestHash=discoveryFiles.Hash(manifest,true);
                bool before=workerHash==contract.Discovery.BeforeWorker && manifestHash==contract.Discovery.BeforeManifest;
                bool after=workerHash==contract.Discovery.AfterWorker && manifestHash==contract.Discovery.AfterManifest;
                SyncSharkUpdaterContract.Require(before || after || recovery,"Discovery differs from the signed preimage/postimage.");
                if(after)files.Runtime(contract.Destination,contract.Members,true);
                needsRebind=recovery || (before && !after);candidate=SyncSharkUpdaterManifest.Discovery(contract);
                using(var sha=System.Security.Cryptography.SHA256.Create())
                    SyncSharkUpdaterContract.Require(SyncSharkUpdaterFiles.Hex(sha.ComputeHash(candidate))==contract.Discovery.AfterManifest,"Discovery postimage is not reconstructible.");
            }
        }
        finally{Release(discovery);}
    }
    private object FindTask(string name)
    {try{return ((dynamic)folder).GetTask(name);}catch(COMException error){if(error.ErrorCode==unchecked((int)0x80070002))return null;throw;}}
    private void ValidateTask()
    {SyncSharkUpdaterTask.Validate((string)((dynamic)task).Xml,(string)((dynamic)task).GetSecurityDescriptor(7),contract.UserSid);}
    public void BeginDiscoveryRebind()
    {SyncSharkUpdaterContract.Require(needsRebind && !started,"Unexpected initial Discovery transition.");discoveryFiles.Dispose();discoveryFiles=null;started=true;}
    public void VerifyDiscoveryPreimage()
    {
        SyncSharkUpdaterContract.Require(started,"Discovery preimage checked out of order.");
        using(var current=new SyncSharkUpdaterFiles(medium))
            SyncSharkUpdaterContract.Require(current.Hash(Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"SyncSharkDiscovery.exe"),true)==contract.Discovery.BeforeWorker &&
                current.Hash(Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"discovery-manifest.json"),true)==contract.Discovery.BeforeManifest,"Discovery preimage changed.");
    }
    public void RecoverDiscovery(SyncSharkDiscoveryTransaction transaction)
    {SyncSharkUpdaterContract.Require(started,"Recovery called out of order.");SyncSharkUpdaterDiscovery.Recover(transaction,contract,medium);}
    public bool IsDiscoveryPostimage()
    {
        using(var current=new SyncSharkUpdaterFiles(medium))
            return current.Hash(Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"SyncSharkDiscovery.exe"),true)==contract.Discovery.AfterWorker &&
                current.Hash(Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"discovery-manifest.json"),true)==contract.Discovery.AfterManifest;
    }
    public byte[] DiscoveryCandidate()
    {SyncSharkUpdaterContract.Require(started && candidate!=null,"Discovery candidate requested out of order.");return (byte[])candidate.Clone();}
    public void Publish()
    {
        files.Runtime(contract.Destination,contract.Members,true);
        if(contract.Discovery.Enabled && !restoring)
        {
            using(var current=new SyncSharkUpdaterFiles(medium))
                SyncSharkUpdaterContract.Require(current.Hash(Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"SyncSharkDiscovery.exe"),true)==contract.Discovery.AfterWorker &&
                    current.Hash(Path.Combine(SyncSharkUpdaterContract.ProtectedRoot,"discovery-manifest.json"),true)==contract.Discovery.AfterManifest,"Initial Discovery postimage differs from signed plan.");
            using(var current=new SyncSharkUpdaterFiles(medium))
                SyncSharkUpdaterContract.Require(SyncSharkUpdaterDiscovery.Enabled(contract.UserSid,current),"Initial Discovery task is not enabled.");
        }
        using(var claim=new SyncSharkDiscoveryTransaction(SyncSharkUpdaterContract.ProtectedRoot,contract.UserSid,true))
        {
            if(claim.HasPending)claim.ValidateUpdater(contract.UpdaterWorker.Sha256,false);
            if(task!=null)ValidateTask();
            claim.ResumeUpdater(source,contract.UpdaterWorker.Sha256,false);
            VerifyWorker(SyncSharkUpdaterContract.WorkerPath,true);
            // CREATE only, suppress Task Scheduler's implicit broader principal ACE.
            if(task==null)task=((dynamic)folder).RegisterTask("SyncSharkUpdater",SyncSharkUpdaterTask.Xml(contract.UserSid),2|0x10,
                contract.UserSid,null,3,SyncSharkUpdaterTask.Security(contract.UserSid));
            ValidateTask();VerifyWorker(SyncSharkUpdaterContract.WorkerPath,true);
            claim.CompleteUpdater();
        }
    }
    // Called only by the existing attended RuntimeRemove/RuntimeRollback
    // bootstrap after its own signed runtime admission. No arbitrary task/path.
    public static void Retire(string productVersion)
    {
        var identity=WindowsIdentity.GetCurrent();
        SyncSharkUpdaterContract.Require(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) &&
            SyncSharkUpdaterContract.IsProductVersion(productVersion),"Updater retirement requires attended frozen product authority.");
        IntPtr token=SyncSharkDiscoverySetupNative.MediumToken();object service=null,folder=null,task=null;
        try
        {
            SyncSharkUpdaterContract binding;
            string[] version=productVersion.Split('+');
            string transaction=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Massdex","SyncShark","replacement-staging","transactions",version[0]+"-"+version[1].Substring(0,7));
            using(var authority=new SyncSharkUpdaterFiles(token))
            {
                authority.Ancestors(transaction,false);string installer=Path.Combine(transaction,"Install-SyncShark.ps1");
                string text=authority.SmallText(installer,1048576,false);SyncSharkUpdaterSignature.Verify(installer,false);
                string header;using(var reader=new StringReader(text))header=reader.ReadLine();
                binding=SyncSharkUpdaterData.ReadSignedHeader(header,new SyncSharkUpdaterData.PendingPlan(version[0],version[1],authority.Hash(installer,false)));
                SyncSharkUpdaterContract.Require(binding.UserSid==identity.User.Value,"Updater retirement account differs from the signed plan.");
                authority.Ancestors(SyncSharkUpdaterContract.ProtectedRoot,true);
                service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));((dynamic)service).Connect();folder=((dynamic)service).GetFolder(@"\");
                try{task=((dynamic)folder).GetTask("SyncSharkUpdater");}catch(COMException error){if(error.ErrorCode!=unchecked((int)0x80070002))throw;}
                if(task!=null)
                {
                    // Disabled is allowed only for the exact in-progress removal,
                    // not as an excuse to accept any changed task policy.
                    bool pending=SyncSharkDiscoveryTransaction.InspectUpdater(SyncSharkUpdaterContract.ProtectedRoot,binding.UserSid,binding.UpdaterWorker.Sha256,true,true);
                    SyncSharkUpdaterTask.ValidateRemoval((string)((dynamic)task).Xml,(string)((dynamic)task).GetSecurityDescriptor(7),binding.UserSid,pending);
                    SyncSharkUpdaterContract.Require(((dynamic)task).GetInstances(0).Count==0,"An updater is running; retirement must not interrupt it.");
                }
                if(File.Exists(SyncSharkUpdaterContract.WorkerPath))
                {
                    SyncSharkUpdaterContract.Require(authority.Hash(SyncSharkUpdaterContract.WorkerPath,true)==binding.UpdaterWorker.Sha256 &&
                        new FileInfo(SyncSharkUpdaterContract.WorkerPath).Length==binding.UpdaterWorker.Bytes,"Foreign updater bytes cannot be retired.");
                    SyncSharkUpdaterSignature.Verify(SyncSharkUpdaterContract.WorkerPath,false);
                }
            }
            using(var claim=new SyncSharkDiscoveryTransaction(SyncSharkUpdaterContract.ProtectedRoot,binding.UserSid,true))
            {
                if(claim.HasPending)claim.ValidateUpdater(binding.UpdaterWorker.Sha256,true);
                else claim.PrepareUpdater(null,binding.UpdaterWorker.Sha256,true);
                if(task!=null)
                {
                    ((dynamic)task).Enabled=false;
                    SyncSharkUpdaterContract.Require(((dynamic)task).GetInstances(0).Count==0,"Updater started during retirement; retain its recovery fence.");
                    ((dynamic)folder).DeleteTask("SyncSharkUpdater",0);
                }
                claim.ResumeUpdater(null,binding.UpdaterWorker.Sha256,true);
                object remaining=null;
                try{remaining=((dynamic)folder).GetTask("SyncSharkUpdater");throw new IOException("Updater task removal did not persist.");}
                catch(COMException error){if(error.ErrorCode!=unchecked((int)0x80070002))throw;}
                finally{Release(remaining);}
                SyncSharkUpdaterContract.Require(!File.Exists(SyncSharkUpdaterContract.WorkerPath),"Updater worker removal did not persist.");
                claim.CompleteUpdater();
            }
        }
        finally{Release(task);Release(folder);Release(service);SyncSharkDiscoverySetupNative.CloseToken(token);}
    }
    private void VerifyWorker(string path,bool protect)
    {
        files.Hold(path,false,protect);
        SyncSharkUpdaterContract.Require(new FileInfo(path).Length==contract.UpdaterWorker.Bytes && files.Hash(path,protect)==contract.UpdaterWorker.Sha256,
            "Updater differs from signed fixed identity; replacement is not authorised.");
        SyncSharkUpdaterSignature.Verify(path,false);
    }
    private static void Release(object value){if(value!=null && Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        if(active!=null)active.Dispose();if(discoveryFiles!=null)discoveryFiles.Dispose();files.Dispose();
        Release(task);Release(folder);Release(service);SyncSharkDiscoverySetupNative.CloseToken(medium);
    }
}
