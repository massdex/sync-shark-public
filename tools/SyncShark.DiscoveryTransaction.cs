#if NET8_0_OR_GREATER
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

// Fixed-member filesystem transaction. The setup bootstrap validates the protected
// root, caller and source before constructing this class; it never selects a task,
// provider, arbitrary command or user-data target. Also exercised directly on fixtures.
public sealed class SyncSharkDiscoveryTransaction : IDisposable
{
    private static readonly string[] DiscoveryNames = { "SyncSharkDiscovery.exe", "discovery-manifest.json", "SyncSharkDiscovery.exe.previous", "discovery-manifest.json.previous" };
    private static readonly string[] UpdaterNames = { "SyncSharkUpdater.exe" };
    private string[] Names {get{return UpdaterPending?UpdaterNames:DiscoveryNames;}}
    private readonly string root, user, journalPath, pendingPath;
    private readonly FileStream claim;
    private readonly Action<string> checkpoint;
    private readonly bool protectedWrites, preserveUnpublishedJournal;
    private Journal journal;
    private Journal unpublished;
    private string unpublishedHash;
    private bool signedJournalValidated;
    [DataContract] private sealed class Journal
    {
        [DataMember(Order=0)] public int Schema;
        [DataMember(Order=1)] public string Id;
        [DataMember(Order=2)] public string User;
        [DataMember(Order=3)] public string Mode;
        [DataMember(Order=4)] public string State;
        [DataMember(Order=5)] public string[] Before;
        [DataMember(Order=6)] public string[] After;
        [DataMember(Order=7)] public string[] Sources;
    }
    public SyncSharkDiscoveryTransaction(string directory, string userSid) : this(directory,userSid,null,true,false) { }
    // Updater admission must never opportunistically clean a foreign or torn
    // setup journal. Existing attended setup retains its established recovery.
    public SyncSharkDiscoveryTransaction(string directory,string userSid,bool preserveUnpublished) : this(directory,userSid,null,true,preserveUnpublished) { }
    // Tests exercise the identical transaction on an isolated ordinary directory;
    // that does not stand in for the separately required protected-ACL proof.
    internal SyncSharkDiscoveryTransaction(string directory, string userSid, Action<string> observe) : this(directory,userSid,observe,false) { }
    internal SyncSharkDiscoveryTransaction(string directory,string userSid,Action<string> observe,bool strict) : this(directory,userSid,observe,false,strict) { }
    private SyncSharkDiscoveryTransaction(string directory, string userSid, Action<string> observe,bool protect,bool preserveUnpublished,bool inspectOnly=false)
    {
        root=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);user=userSid;checkpoint=observe;protectedWrites=protect;preserveUnpublishedJournal=preserveUnpublished;
        if(!user.StartsWith("S-1-5-21-",StringComparison.Ordinal))throw new IOException("The setup transaction needs its exact Windows user.");
        using(SyncSharkDiscoverySetupNative.Hold(root,true)) { }
        journalPath=Path.Combine(root,"discovery-setup.json");pendingPath=journalPath+".new";
        string lockPath=Path.Combine(root,"discovery-setup.lock");
        if(File.Exists(lockPath))using(SyncSharkDiscoverySetupNative.Hold(lockPath,false)) { }
        if(!inspectOnly)claim=preserveUnpublished?
            (protect?SyncSharkDiscoverySetupNative.OpenProtectedFile(lockPath):new FileStream(lockPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None)):
            NewFile(lockPath,false);
        try { Load(); } catch {if(claim!=null)claim.Dispose();throw;}
    }
    public bool HasPending { get {return journal!=null;} }
    public bool UpdaterPending {get{return journal!=null && (journal.Mode=="EnrollUpdater" || journal.Mode=="RemoveUpdater");}}
    internal static bool InspectUpdater(string directory,string userSid,string expected,bool removing,bool protect)
    {
        using(var value=new SyncSharkDiscoveryTransaction(directory,userSid,null,protect,true,true))
        {if(!value.UpdaterPending)return false;value.ValidateUpdater(expected,removing);return true;}
    }
    internal void ValidateUpdater(string expected,bool removing)
    {
        if(!UpdaterPending || !IsHash(expected) || expected==null || journal.Mode!=(removing?"RemoveUpdater":"EnrollUpdater") ||
            (journal.Before[0]!=null && journal.Before[0]!=expected) || journal.After[0]!=(removing?null:expected) ||
            journal.Sources[0]!=(removing?null:"candidate-0") || journal.Before.Skip(1).Any(x=>x!=null) ||
            journal.After.Skip(1).Any(x=>x!=null) || journal.Sources.Skip(1).Any(x=>x!=null))
            throw new IOException("Updater lifecycle journal differs from the signed fixed worker.");
        string path=Path.Combine(root,UpdaterNames[0]),current=File.Exists(path)?Hash(path):null;
        if(current!=journal.Before[0] && current!=journal.After[0])throw new IOException("Foreign updater content blocks lifecycle recovery.");
        if(journal.State=="Ready")
        {
            if(journal.Before[0]!=null && Hash(Path.Combine(Stage,"old-0"))!=expected)throw new IOException("Updater recovery preimage changed.");
            if(!removing && Hash(Path.Combine(Stage,"candidate-0"))!=expected)throw new IOException("Updater recovery candidate changed.");
        }
        else Verify(journal.State=="Committed"?journal.After:journal.Before);
        signedJournalValidated=true;
    }
    internal void PrepareUpdater(string source,string expected,bool removing)
    {
        if(journal!=null || expected==null || !IsHash(expected))throw new IOException("Updater lifecycle is not ready.");
        string path=Path.Combine(root,UpdaterNames[0]);
        if(Directory.Exists(path))throw new IOException("Updater target is not an ordinary file.");
        string before=File.Exists(path)?Hash(path):null;
        if(before!=null && before!=expected)throw new IOException("Foreign updater bytes are preserved.");
        if(!removing && Hash(source)!=expected)throw new IOException("Updater source changed.");
        journal=new Journal{Schema=1,Id=Guid.NewGuid().ToString("N"),User=user,Mode=removing?"RemoveUpdater":"EnrollUpdater",State="Preparing",
            Before=new[]{before,null,null,null},After=new[]{removing?null:expected,null,null,null},Sources=new[]{removing?null:"candidate-0",null,null,null}};
        Save();if(protectedWrites)SyncSharkDiscoverySetupNative.CreateProtectedDirectory(Stage);else Directory.CreateDirectory(Stage);Step("stage-created");
        if(before!=null){Copy(path,Path.Combine(Stage,"old-0"),before);Step("snapshot-0");}
        if(!removing){Copy(source,Path.Combine(Stage,"candidate-0"),expected);Step("candidate-worker");}
        Verify(journal.Before);journal.State="Ready";Save();
    }
    // Reopen a signed lifecycle without undoing a completed worker publication.
    // Preparing has no live effects; Ready is idempotently applied; Committed
    // retains its cleanup fence until the caller checks task presence/absence.
    internal void ResumeUpdater(string source,string expected,bool removing)
    {
        if(journal!=null)
        {
            ValidateUpdater(expected,removing);
            ResolveUnpublished();
            if(journal.State=="Preparing" || journal.State=="RolledBack"){RecoverCore();FinishRecovery();}
        }
        if(journal==null)PrepareUpdater(source,expected,removing);
        if(journal.State=="Ready")Apply();else Verify(journal.After);
    }
    internal void CompleteUpdater()
    {if(!UpdaterPending)throw new IOException("No updater lifecycle is claimed.");if(journal.State=="Ready")Complete();else if(journal.State=="Committed")FinishRecovery();else throw new IOException("Updater lifecycle is incomplete.");}
    // Read-only admission inspects the real journal, not a success-shaped flag.
    // A second check under the existing exclusive claim precedes any recovery.
    internal static bool InspectConfigure(string directory,string userSid,string beforeWorker,string beforeManifest,string afterWorker,string afterManifest,bool protect)
    {
        using(var value=new SyncSharkDiscoveryTransaction(directory,userSid,null,protect,true,true))
        {if(!value.HasPending)return false;value.ValidateConfigure(beforeWorker,beforeManifest,afterWorker,afterManifest);return true;}
    }
    internal void ValidateConfigure(string beforeWorker,string beforeManifest,string afterWorker,string afterManifest)
    {
        if(journal==null || journal.Mode!="Configure" || journal.Before[0]!=beforeWorker || journal.Before[1]!=beforeManifest ||
            journal.After[0]!=afterWorker || journal.After[1]!=afterManifest || journal.After[2]!=beforeWorker || journal.After[3]!=beforeManifest ||
            !journal.Sources.SequenceEqual(new[]{"candidate-0","candidate-1","old-0","old-1"}))
            throw new IOException("Discovery recovery is not the exact signed transition; preserve it.");
        if(journal.State=="Ready")
        {
            // Validate every recovery input before the first live replacement.
            for(int i=0;i<4;i++)
            {
                string path=Path.Combine(root,Names[i]),current=File.Exists(path)?Hash(path):null;
                if(current!=journal.Before[i] && current!=journal.After[i])throw new IOException("Unrelated Discovery bytes block recovery.");
                if(journal.Before[i]!=null && Hash(Path.Combine(Stage,"old-"+i))!=journal.Before[i])throw new IOException("Discovery recovery snapshot changed.");
            }
        }
        else Verify(journal.State=="Committed"?journal.After:journal.Before);
        signedJournalValidated=true;
    }
    public static bool IsControlName(string name)
    { return name=="discovery-setup.lock" || name=="discovery-setup.json" || name=="discovery-setup.json.new"; }
    private void Step(string name) {if(checkpoint!=null)checkpoint(name);}
    private FileStream NewFile(string path,bool fresh)
    {return protectedWrites?SyncSharkDiscoverySetupNative.CreateProtectedFile(path,fresh):new FileStream(path,fresh?FileMode.CreateNew:FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
    private static string Hash(string path)
    {
        using(SyncSharkDiscoverySetupNative.Hold(path,false))
        using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var hash=SHA256.Create())
        {
            if(stream.Length<1 || stream.Length>16*1024*1024)throw new IOException("A setup transaction member is empty or oversized.");
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
        }
    }
    private static bool IsHash(string value)
    { return value==null || (value.Length==64 && value.All(c=>(c>='0'&&c<='9')||(c>='a'&&c<='f'))); }
    private string Stage {get{return Path.Combine(root,".setup-"+journal.Id);}}
    private Journal ReadJournal(string path)
    {
        Journal value;
        using(SyncSharkDiscoverySetupNative.Hold(path,false))
        using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            if(stream.Length<1 || stream.Length>65536)throw new IOException("The setup journal is invalid; preserve it.");
            try{value=(Journal)new DataContractJsonSerializer(typeof(Journal)).ReadObject(stream);}
            catch(SerializationException error){throw new IOException("The setup journal is incomplete; preserve it.",error);}
        }
        return value;
    }
    private void ValidateJournal()
    {
        if(journal!=null)
        {
            if(journal.Schema!=1 || journal.User!=user || journal.Id==null || journal.Id.Length!=32 ||
                !journal.Id.All(c=>(c>='0'&&c<='9')||(c>='a'&&c<='f')) ||
                !new[]{"Configure","Restore","Remove","EnrollUpdater","RemoveUpdater"}.Contains(journal.Mode) ||
                !new[]{"Preparing","Ready","Committed","RolledBack"}.Contains(journal.State) ||
                journal.Before==null || journal.After==null || journal.Sources==null ||
                journal.Before.Length!=4 || journal.After.Length!=4 || journal.Sources.Length!=4 ||
                !journal.Before.All(IsHash) || !journal.After.All(IsHash))throw new IOException("Unexpected setup journal identity.");
            for(int i=0;i<4;i++)
                if((journal.After[i]==null)!=(journal.Sources[i]==null) || (journal.Sources[i]!=null &&
                    !new[]{"old-0","old-1","old-2","old-3","candidate-0","candidate-1","candidate-2","candidate-3"}.Contains(journal.Sources[i])))
                    throw new IOException("Unexpected setup journal source.");
        }
    }
    private void Load()
    {
        journal=File.Exists(journalPath)?ReadJournal(journalPath):null;unpublished=null;unpublishedHash=null;
        ValidateJournal();
        if(preserveUnpublishedJournal && File.Exists(pendingPath))
        {
            var durable=journal;unpublished=ReadJournal(pendingPath);unpublishedHash=Hash(pendingPath);
            journal=unpublished;ValidateJournal();
            if(durable==null)
            {
                if(journal.State!="Preparing")throw new IOException("Unpublished initial journal has no valid pre-effect boundary.");
            }
            else
            {
                bool next=(durable.State=="Preparing" && unpublished.State=="Ready") ||
                    (durable.State=="Ready" && (unpublished.State=="Committed" || unpublished.State=="RolledBack"));
                if(!next || durable.Id!=unpublished.Id || durable.User!=unpublished.User || durable.Mode!=unpublished.Mode ||
                    !durable.Before.SequenceEqual(unpublished.Before) || !durable.After.SequenceEqual(unpublished.After) || !durable.Sources.SequenceEqual(unpublished.Sources))
                    throw new IOException("Foreign unpublished setup journal is preserved.");
                journal=durable;
            }
        }
        foreach(string entry in Directory.EnumerateFileSystemEntries(root))
        {
            string name=Path.GetFileName(entry);
            if(DiscoveryNames.Contains(name) || IsControlName(name))continue;
            if(name=="SyncSharkUpdater.exe" || name=="updater-publication.json" || name=="updater-publication.json.new") {
                using(var handle=SyncSharkDiscoverySetupNative.Hold(entry,false)) {
                    if(new FileInfo(entry).Length>(name=="SyncSharkUpdater.exe"?16777216:4096))throw new IOException("Unbounded updater control member.");
                    if(protectedWrites) {
                        IntPtr medium=SyncSharkDiscoverySetupNative.MediumToken();
                        try {SyncSharkDiscoverySetupNative.RequireProtected(handle,medium,false);}
                        finally {SyncSharkDiscoverySetupNative.CloseToken(medium);}
                    }
                }
                continue;
            }
            if(name=="versions") {
                using(var handle=SyncSharkDiscoverySetupNative.Hold(entry,true)) {
                    if(protectedWrites) {
                        IntPtr medium=SyncSharkDiscoverySetupNative.MediumToken();
                        try {SyncSharkDiscoverySetupNative.RequireProtected(handle,medium,true);}
                        finally {SyncSharkDiscoverySetupNative.CloseToken(medium);}
                    }
                }
                continue;
            }
            if(journal!=null && name==".setup-"+journal.Id) {using(SyncSharkDiscoverySetupNative.Hold(entry,true)){};continue;}
            throw new IOException("Unclaimed protected setup content; preserve it for recovery.");
        }
        // Atomic journal publication precedes all stage/current writes. A torn
        // unpublished next record cannot own live effects; the previous record wins.
        if(File.Exists(pendingPath))
        {
            if(preserveUnpublishedJournal)return; // Validated as data only; no effect until exact signed admission under the claim.
            using(SyncSharkDiscoverySetupNative.Hold(pendingPath,false))
                if(new FileInfo(pendingPath).Length>65536)throw new IOException("Unbounded setup journal staging file.");
            File.Delete(pendingPath);
        }
    }
    private void ResolveUnpublished()
    {
        if(unpublished==null)return;
        if(claim==null || !signedJournalValidated || Hash(pendingPath)!=unpublishedHash)
            throw new IOException("Unpublished journal has no exact signed claimed recovery authority.");
        if(File.Exists(journalPath))File.Delete(pendingPath);else File.Move(pendingPath,journalPath);
        unpublished=null;unpublishedHash=null;
    }
    private void Save()
    {
        using(var stream=NewFile(pendingPath,true))
        {new DataContractJsonSerializer(typeof(Journal)).WriteObject(stream,journal);stream.Flush(true);}
        Step("journal-written-"+journal.State);
        if(File.Exists(journalPath))File.Replace(pendingPath,journalPath,null);else File.Move(pendingPath,journalPath);
        Step("journal-published-"+journal.State);
    }
    private void Verify(string[] expected)
    {
        for(int i=0;i<Names.Length;i++)
        {
            string path=Path.Combine(root,Names[i]);
            if(expected[i]==null) {if(File.Exists(path)||Directory.Exists(path))throw new IOException("Unexpected setup member.");}
            else if(!File.Exists(path)||Hash(path)!=expected[i])throw new IOException("Setup member does not match its transaction.");
        }
    }
    private void Copy(string source,string target,string expected)
    {
        using(SyncSharkDiscoverySetupNative.Hold(source,false))
        using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var output=NewFile(target,true))
        {if(input.Length>16*1024*1024)throw new IOException("Oversized setup member.");input.CopyTo(output);output.Flush(true);}
        if(Hash(target)!=expected)throw new IOException("Setup snapshot changed.");
    }
    public void Prepare(string mode,string candidateWorker,byte[] candidateManifest)
    {Prepare(mode,candidateWorker,candidateManifest,null,null,null);}
    // Only the signed RuntimeRollback caller supplies an exact original pair.
    // This reuses Restore and its four fixed journal slots; it never accepts a
    // live destination, rollback depth or additional protected operation.
    public void Prepare(string mode,string candidateWorker,byte[] candidateManifest,
        string previousWorker,byte[] previousManifest,string[] expectedBefore)
    {
        if(journal!=null)throw new IOException("Recover the previous setup transaction first.");
        if(!new[]{"Configure","Restore","Remove"}.Contains(mode))throw new ArgumentException("Unknown setup operation.");
        var before=Names.Select(name=>File.Exists(Path.Combine(root,name))?Hash(Path.Combine(root,name)):null).ToArray();
        if((before[0]==null)!=(before[1]==null) || (before[2]==null)!=(before[3]==null))throw new IOException("An unclaimed setup pair is incomplete.");
        bool exact=expectedBefore!=null;
        if(exact && (mode!="Restore" || expectedBefore.Length!=4 || expectedBefore.Any(x=>x==null || !IsHash(x)) ||
            !before.SequenceEqual(expectedBefore)))throw new IOException("The signed original-pair restoration preimage changed.");
        if(!exact && (previousWorker!=null || previousManifest!=null))throw new IOException("An original pair needs an exact signed preimage.");
        var after=new string[4];var sources=new string[4];
        if(exact)
        {
            if(candidateManifest==null || previousManifest==null || candidateManifest.Length<1 || previousManifest.Length<1 ||
                candidateManifest.Length>524288 || previousManifest.Length>524288)throw new IOException("Invalid original-pair manifests.");
            after[0]=Hash(candidateWorker);after[2]=Hash(previousWorker);
            using(var hash=SHA256.Create())
            {after[1]=BitConverter.ToString(hash.ComputeHash(candidateManifest)).Replace("-","").ToLowerInvariant();
             after[3]=BitConverter.ToString(hash.ComputeHash(previousManifest)).Replace("-","").ToLowerInvariant();}
            for(int i=0;i<4;i++)sources[i]="candidate-"+i;
        }
        else if(mode=="Configure")
        {
            if(candidateManifest==null || candidateManifest.Length<1 || candidateManifest.Length>524288)throw new IOException("Invalid candidate manifest.");
            after[0]=Hash(candidateWorker);using(var hash=SHA256.Create())after[1]=BitConverter.ToString(hash.ComputeHash(candidateManifest)).Replace("-","").ToLowerInvariant();
            sources[0]="candidate-0";sources[1]="candidate-1";
            for(int i=0;i<2;i++){int original=before[0]!=null?i:i+2;after[i+2]=before[original];if(after[i+2]!=null)sources[i+2]="old-"+original;}
        }
        else if(mode=="Restore")
        {
            if(before[2]==null)throw new IOException("No previous setup is available.");
            for(int i=0;i<4;i++){int original=(i+2)%4;after[i]=before[original];if(after[i]!=null)sources[i]="old-"+original;}
        }
        else if(before[0]==null)throw new IOException("There is no bound setup to remove.");
        journal=new Journal{Schema=1,Id=Guid.NewGuid().ToString("N"),User=user,Mode=mode,State="Preparing",Before=before,After=after,Sources=sources};
        Save();if(protectedWrites)SyncSharkDiscoverySetupNative.CreateProtectedDirectory(Stage);else Directory.CreateDirectory(Stage);Step("stage-created");
        for(int i=0;i<4;i++)if(before[i]!=null){Copy(Path.Combine(root,Names[i]),Path.Combine(Stage,"old-"+i),before[i]);Step("snapshot-"+i);}
        if(mode=="Configure" || exact)
        {
            Copy(candidateWorker,Path.Combine(Stage,"candidate-0"),after[0]);Step("candidate-worker");
            using(var stream=NewFile(Path.Combine(Stage,"candidate-1"),true))
            {stream.Write(candidateManifest,0,candidateManifest.Length);stream.Flush(true);}
            Step("candidate-manifest");
        }
        if(exact)
        {
            Copy(previousWorker,Path.Combine(Stage,"candidate-2"),after[2]);Step("candidate-previous-worker");
            using(var stream=NewFile(Path.Combine(Stage,"candidate-3"),true))
            {stream.Write(previousManifest,0,previousManifest.Length);stream.Flush(true);}
            Step("candidate-previous-manifest");
        }
        Verify(before);journal.State="Ready";Save();
    }
    public void ValidateRestore(string[] before,string[] after)
    {
        if(journal==null || journal.Mode!="Restore" || before==null || after==null || before.Length!=4 || after.Length!=4 ||
            before.Any(x=>x==null || !IsHash(x)) || after.Any(x=>x==null || !IsHash(x)) ||
            !journal.Before.SequenceEqual(before) || !journal.After.SequenceEqual(after) ||
            !journal.Sources.SequenceEqual(new[]{"candidate-0","candidate-1","candidate-2","candidate-3"}))
            throw new IOException("Restoration journal is not the exact signed original pair.");
        if(journal.State=="Ready")
        {
            for(int i=0;i<4;i++)
            {
                string path=Path.Combine(root,Names[i]),current=File.Exists(path)?Hash(path):null;
                if(current!=before[i] && current!=after[i])throw new IOException("Foreign bytes block original-pair restoration.");
                if(Hash(Path.Combine(Stage,"old-"+i))!=before[i] || Hash(Path.Combine(Stage,"candidate-"+i))!=after[i])
                    throw new IOException("The original-pair restoration snapshot changed.");
            }
        }
        else Verify(journal.State=="Committed"?after:before);
        signedJournalValidated=true;
    }
    private void Put(int index,string expected,string source)
    {
        string path=Path.Combine(root,Names[index]);string current=File.Exists(path)?Hash(path):null;
        if(current!=journal.Before[index] && current!=journal.After[index])throw new IOException("An unrelated setup change blocks recovery.");
        if(current==expected)return;
        if(expected==null){File.Delete(path);Step("member-deleted-"+index);return;}
        string staged=Path.Combine(Stage,source),temporary=Path.Combine(Stage,"promote-"+index);
        if(Hash(staged)!=expected)throw new IOException("The complete recovery snapshot is unavailable.");
        if(File.Exists(temporary)){using(SyncSharkDiscoverySetupNative.Hold(temporary,false)){}File.Delete(temporary);}
        Copy(staged,temporary,expected);Step("member-staged-"+index);
        if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
        Step("member-published-"+index);
    }
    public void Apply()
    {
        if(journal==null || journal.State!="Ready")throw new IOException("Setup is not prepared.");
        for(int i=0;i<Names.Length;i++)Put(i,journal.After[i],journal.Sources[i]);Verify(journal.After);
    }
    public void Complete()
    {
        if(journal==null || journal.State!="Ready")throw new IOException("Setup is not ready to commit.");
        Verify(journal.After);journal.State="Committed";Save();Cleanup();
    }
    public void Recover()
    {RecoverRetainingFence();FinishRecovery();}
    public void RecoverRetainingFence()
    {
        if(UpdaterPending)throw new IOException("Updater lifecycle requires its task-aware signed caller.");
        RecoverCore();
    }
    private void RecoverCore()
    {
        // A failed atomic journal replacement can leave an uncommitted in-memory
        // state. Always recover the durable record, never that proposed state.
        Load();
        if(journal==null)return;
        ResolveUnpublished();
        if(journal.State=="Ready")
        {
            for(int i=0;i<Names.Length;i++)Put(i,journal.Before[i],"old-"+i);
            Verify(journal.Before);journal.State="RolledBack";Save();
        }
        else Verify(journal.State=="Committed"?journal.After:journal.Before);
        // The task-aware caller retains the journal until task identity, worker
        // and enabled state have all been restored and independently read back.
    }
    public void FinishRecovery()
    {
        if(journal==null)return;
        if(journal.State=="Ready")throw new IOException("Recovery has not restored its durable state.");
        Verify(journal.State=="Committed"?journal.After:journal.Before);Cleanup();
    }
    private void Cleanup()
    {
        if(Directory.Exists(Stage))
        {
            using(SyncSharkDiscoverySetupNative.Hold(Stage,true)){}
            var allowed=new HashSet<string>{"old-0","old-1","old-2","old-3","candidate-0","candidate-1","candidate-2","candidate-3","promote-0","promote-1","promote-2","promote-3"};
            string[] files=Directory.GetFileSystemEntries(Stage);
            foreach(string file in files)
            {
                if(!allowed.Contains(Path.GetFileName(file)))throw new IOException("Unowned setup stage content; nothing removed.");
                using(SyncSharkDiscoverySetupNative.Hold(file,false)){}
            }
            foreach(string file in files){File.Delete(file);Step("cleanup-"+Path.GetFileName(file));}
            Directory.Delete(Stage,false);Step("stage-removed");
        }
        File.Delete(journalPath);journal=null;Step("transaction-closed");
    }
    public void Dispose(){if(claim!=null)claim.Dispose();}
}

// Complete code-tree publication under the same setup claim. The caller first
// authenticates the signed installer contract and holds every source member.
// No user state, provider path, command, registry or task is accepted here.
public sealed class SyncSharkProtectedRuntimeTransaction
{
    public static readonly string[] Members = {
        "Assets/SyncShark-64.png", "Microsoft.Data.Sqlite.dll", "Microsoft.Windows.SDK.NET.dll",
        "SQLitePCLRaw.batteries_v2.dll", "SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.e_sqlite3.dll",
        "SyncShark.Core.dll", "SyncShark.Explorer.dll", "SyncShark.deps.json", "SyncShark.dll",
        "SyncShark.exe", "SyncShark.msix", "SyncShark.runtimeconfig.json", "SyncSharkDiscovery.exe",
        "WinRT.Runtime.dll", "e_sqlite3.dll"
    };
    private readonly string root, target, stage, retired;
    private readonly string[] hashes;
    private readonly long[] sizes;
    private readonly bool protect;
    private readonly Action<string> checkpoint;
    public SyncSharkProtectedRuntimeTransaction(string version, string[] expectedHashes, long[] expectedSizes)
        : this(@"C:\Program Files\Massdex\SyncShark\versions",version,expectedHashes,expectedSizes,null,true) { }
    internal SyncSharkProtectedRuntimeTransaction(string directory,string version,string[] expectedHashes,long[] expectedSizes,Action<string> observe)
        : this(directory,version,expectedHashes,expectedSizes,observe,false) { }
    private SyncSharkProtectedRuntimeTransaction(string directory,string version,string[] expectedHashes,long[] expectedSizes,Action<string> observe,bool protectedWrites)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(version,@"^[0-9]+\.[0-9]+\.[0-9]+$"))throw new IOException("Invalid release version.");
        if(expectedHashes==null || expectedSizes==null || expectedHashes.Length!=Members.Length || expectedSizes.Length!=Members.Length ||
            expectedHashes.Any(h=>h==null || h.Length!=64 || !h.All(c=>(c>='0'&&c<='9')||(c>='a'&&c<='f'))) ||
            expectedSizes.Any(s=>s<1 || s>536870912))throw new IOException("Incomplete signed runtime member contract.");
        root=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);target=Path.Combine(root,version);
        stage=Path.Combine(root,version+".installing");retired=Path.Combine(root,version+".uninstalled");
        hashes=(string[])expectedHashes.Clone();sizes=(long[])expectedSizes.Clone();protect=protectedWrites;checkpoint=observe;
        Hold(root,true);
    }
    private void Step(string name){if(checkpoint!=null)checkpoint(name);}
    private void Hold(string path,bool directory)
    {
        using(var handle=SyncSharkDiscoverySetupNative.Hold(path,directory)) {
            if(protect) {
                IntPtr medium=SyncSharkDiscoverySetupNative.MediumToken();
                try {SyncSharkDiscoverySetupNative.RequireProtected(handle,medium,directory);}
                finally {SyncSharkDiscoverySetupNative.CloseToken(medium);}
            }
        }
    }
    private void CreateDirectory(string path)
    {if(protect)SyncSharkDiscoverySetupNative.CreateProtectedDirectory(path);else Directory.CreateDirectory(path);}
    private string Member(string directory,int index){return Path.Combine(directory,Members[index].Replace('/',Path.DirectorySeparatorChar));}
    private bool Matches(string path,int index)
    {
        Hold(path,false);
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var hash=SHA256.Create())
            return file.Length==sizes[index] && BitConverter.ToString(hash.ComputeHash(file)).Replace("-","").ToLowerInvariant()==hashes[index];
    }
    private void Shape(string directory)
    {
        Hold(directory,true);
        foreach(string entry in Directory.EnumerateFileSystemEntries(directory)) {
            string name=Path.GetFileName(entry);
            if(name=="Assets") {
                Hold(entry,true);
                foreach(string asset in Directory.EnumerateFileSystemEntries(entry)) {
                    if(Path.GetFileName(asset)!="SyncShark-64.png")throw new IOException("Unexpected protected runtime asset.");
                    Hold(asset,false);
                }
            } else {
                if(!Members.Contains(name))throw new IOException("Unexpected protected runtime member.");
                Hold(entry,false);
            }
        }
    }
    public void Verify(string directory)
    {
        if(directory!=target && directory!=stage && directory!=retired)throw new IOException("Runtime verification target changed.");
        Shape(directory);
        for(int i=0;i<Members.Length;i++)if(!Matches(Member(directory,i),i))throw new IOException("Protected runtime bytes changed.");
    }
    public void Install(string source)
    {
        if(Directory.Exists(target)) {
            if(Directory.Exists(stage)||Directory.Exists(retired))throw new IOException("Conflicting protected runtime states.");
            Verify(target);return;
        }
        if(Directory.Exists(retired)) {
            if(Directory.Exists(stage))throw new IOException("Conflicting protected runtime recovery states.");
            Verify(retired);Directory.Move(retired,target);Step("runtime-restored");Verify(target);return;
        }
        if(!Directory.Exists(stage))CreateDirectory(stage);
        Shape(stage);Step("runtime-stage-created");
        string assets=Path.Combine(stage,"Assets");if(!Directory.Exists(assets))CreateDirectory(assets);
        for(int i=0;i<Members.Length;i++) {
            string destination=Member(stage,i);
            if(File.Exists(destination)) {
                if(Matches(destination,i))continue;
                // Only this unpublished, protected, fixed contract member can be
                // replaced after an interrupted copy; never a live/unknown tree.
                File.Delete(destination);
            }
            string inputPath=Member(source,i);
            using(SyncSharkDiscoverySetupNative.Hold(inputPath,false))
            using(var input=new FileStream(inputPath,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                if(input.Length!=sizes[i])throw new IOException("Runtime source length changed.");
                using(var output=protect?SyncSharkDiscoverySetupNative.CreateProtectedFile(destination,true):
                    new FileStream(destination,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None)) {
                    input.CopyTo(output);output.Flush(true);
                }
            }
            if(!Matches(destination,i))throw new IOException("Runtime source hash changed.");
            Step("runtime-copied-"+i);
        }
        Verify(stage);Step("runtime-stage-verified");
        Directory.Move(stage,target);Step("runtime-published");Verify(target);
    }
    public void Retire()
    {
        if(Directory.Exists(stage))throw new IOException("Finish the protected installation before removing it.");
        if(Directory.Exists(retired)) {
            if(Directory.Exists(target))throw new IOException("Conflicting protected uninstall states.");
            Verify(retired);return;
        }
        Verify(target);Directory.Move(target,retired);Step("runtime-retired");Verify(retired);
    }
}
