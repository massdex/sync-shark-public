using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks the evidence carried across the local publication and listing boundaries.</summary>
public static class LocalSideChecks
{
    public static async Task RunAsync()
    {
        await SecondRecycleWaitsAsync();
        // A selected non-readable entry must be refused before even resolving a data filename.
        var pair = new PairConfig("pair","Pair","invalid\0root","/root","account",[],[],FirstRunPolicy.NewestWins,true,null);
        var side = LocalSide.Factory()(pair,()=>throw new InvalidOperationException(),SettingsParser.Load(),new TestDoubles.InMemoryStateStore());
        var wrong = new List<string>();
        foreach (var reason in new[]{TextKey.SkipOnlineOnly,TextKey.SkipLink,TextKey.SkipSpecial})
        {
            var entry=new Entry("file",false,1,DateTimeOffset.UnixEpoch,"1","","",reason.ToString());
            try { using var stream=await side.OpenReadAsync(entry,default); wrong.Add($"{reason}: returned a content stream"); }
            catch(ChangedUnderneathException) { }
            catch(Exception) { wrong.Add($"{reason}: content open reached the filesystem"); }
            var impossible=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"file");
            try { if(await side.RecoverAsync(new("1",impossible,entry),entry,default) is not null) wrong.Add($"{reason}: offered recovery bytes"); }
            catch(ChangedUnderneathException) { }
            catch(Exception) { wrong.Add($"{reason}: recovery reached the filesystem"); }
        }
        Check.Require(wrong.Count==0,string.Join("; ",wrong));
    }

    [System.Runtime.InteropServices.DllImport("kernel32",EntryPoint="CreateFileW",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectory(string path,uint access,FileShare share,IntPtr security,uint disposition,uint flags,IntPtr template);
    // Registered separately: the cross-group checks must stay NOT IMPLEMENTED until their owners land.
    public static async Task LateReadFailureAsync()
    {
        var home=Directory.CreateTempSubdirectory(); const string path="broken-item";
        try
        {
            var full=Path.Combine(home.FullName,path); File.WriteAllBytes(full,[7]);
            var side=BoundSide(home.FullName); var entry=(await side.ReadOneAsync(path,default))!;
            await using var reader=await side.OpenReadAsync(entry,default);
            using(var locker=new FileStream(full,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete))
            {
                locker.Lock(0,1);
                try { reader.ReadByte(); throw new InvalidDataException("late read did not report its failed item"); }
                catch(ItemFailedException e) { Check.Require(e.Path==path && e.Cause==TextKey.FailLocalInUse,"late read lost its item or lock cause"); }
                catch(IOException e) when((e.HResult&0xffff)==33) { throw new InvalidDataException("late read lost its item or lock cause",e); }
            }
            await using var reopened=await side.OpenReadAsync(entry,default);
            Check.Require(reopened.ReadByte()==7,"failed item did not reopen with its original bytes");
        }
        finally { home.Delete(true); }
    }
    public static async Task ReceiptNoteAsync(int row)
    {
        // These calls reach the actual Windows bin. Only the disposable hosted runner may run them.
        if(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")!="true" || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT")!="github-hosted") throw new NotImplementedException("Requires disposable Windows runner");
        var home=Directory.CreateTempSubdirectory();
        try
        {
            using var state=new TestDoubles.InMemoryStateStore();var first=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;bool early=false;
            async Task<bool> Arm(RemovalSelection v,StageProof? stage,CancellationToken ct)
            {
                if(Interlocked.Increment(ref calls)==1){first.SetResult();early=await Task.WhenAny(second.Task,Task.Delay(TimeSpan.FromSeconds(2),ct))==second.Task;}
                else second.TrySetResult();return await state.ArmAsync("pair",v,stage,ct);
            }
            IStore store=row==4?Executor.FolderDeleteChecks.Observe.Wrap<IStore>(state,(m,args)=>m==nameof(IStore.ArmAsync)?Arm((RemovalSelection)args[1]!, (StageProof?)args[2],(CancellationToken)args[3]!):null):state;
            var factory=LocalSide.Factory();var side=BoundSide(home.FullName,store,factory);var domain=DriveIdentity.Read(home.FullName).Identity.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            async Task<RemovalSelection> Select(string name,bool folder)
            {
                var path=Path.Combine(home.FullName,name);if(folder)Directory.CreateDirectory(path);else File.WriteAllBytes(path,[1]);
                var e=(await side.ReadOneAsync(name,default))!;return new(new(e,domain,folder?null:await side.ContentHashAsync(e,default)),null);
            }
            var a=await Select("a",row==3);
            if(row==2)
            {
                var source=Scenarios.ItemChecks.File(Side.Dropbox,"a",2,"peer");await using var stage=await side.StageAsync("a",new MemoryStream([2]),source,a.Proof.Expected,default);
                var result=await side.PublishAsync(stage,a.Proof.Expected,a,default);Check.Require(result.Outcome==RemovalOutcome.Published && result.Final?.Id==stage.Proof!.FileId && await store.ArmedAsync("pair",default)==null && File.ReadAllBytes(Path.Combine(home.FullName,"a")).SequenceEqual(new byte[]{2}),"Replacement did not settle at receipt");
            }
            else if(row==4)
            {
                var b=await Select("b",false);var other=BoundSide(home.FullName,store,factory);
                var one=side.DeleteAsync(a.Proof.Expected,a,default);await Task.WhenAny(first.Task,one);var results=await Task.WhenAll(one,other.DeleteAsync(b.Proof.Expected,b,default));
                Check.Require(results.All(r=>r.Outcome==RemovalOutcome.Removed) && calls==2 && !early && await store.ArmedAsync("pair",default)==null,"Concurrent recycles did not take turns");
            }
            else
            {
                var result=await side.DeleteAsync(a.Proof.Expected,a,default);var note=await store.ArmedAsync("pair",default);
                Check.Require(result.Outcome==RemovalOutcome.Removed && (row==3?note?.Victim==a:note==null && await side.ReadOneAsync("a",default)==null),"Unexpected note at recycle receipt");
            }
        }
        finally {home.Delete(true);}
    }
    private static async Task SecondRecycleWaitsAsync()
    {
        var home=Directory.CreateTempSubdirectory();
        try
        {
            using var store=new TestDoubles.InMemoryStateStore();var side=BoundSide(home.FullName,store);
            var domain=DriveIdentity.Read(home.FullName).Identity.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            async Task<RemovalSelection> Select(string name,byte value)
            {
                File.WriteAllBytes(Path.Combine(home.FullName,name),[value]);var entry=(await side.ReadOneAsync(name,default))!;
                return new(new(entry,domain,await side.ContentHashAsync(entry,default)),null);
            }
            var a=await Select("a",1);var b=await Select("b",2);
            Check.Require(await store.ArmAsync("pair",a,null,default),"First arm failed");
            var result=await side.DeleteAsync(b.Proof.Expected,b,default);
            Check.Require(result.Outcome==RemovalOutcome.Pending && (await side.ReadOneAsync("b",default))?.Id==b.Proof.Expected.Id && File.ReadAllBytes(Path.Combine(home.FullName,"b")).SequenceEqual(new byte[]{2}) && await store.ArmedAsync("pair",default)==(a,(StageProof?)null),"Local/SecondRecycleWaits: busy recycle moved the second file or lost the original note");
        }
        finally {home.Delete(true);}
    }
    private static LocalSide BoundSide(string root,params Exclusion[] exclusions)=>BoundSide(root,new TestDoubles.InMemoryStateStore(),exclusions);
    private static LocalSide BoundSide(string root,IStore store,params Exclusion[] exclusions)=>BoundSide(root,store,LocalSide.Factory(),exclusions);
    private static LocalSide BoundSide(string root,IStore store,Func<PairConfig,Func<PathScope>,EngineSettings,IStore,LocalSide> factory,params Exclusion[] exclusions)
    {
        var (id,type)=DriveIdentity.Read(root);
        var pair=new PairConfig("pair","Pair",root,"/root","account",[],exclusions,FirstRunPolicy.NewestWins,true,null,
            new(root,"/root","account",id,new("account","root","home","folder")));
        var scope=new PathScope(pair,[pair],type);
        return factory(pair,()=>scope,SettingsParser.Load(),store);
    }

    public static async Task ArmedOfflineStageAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store=new TestDoubles.InMemoryStateStore(); var side=BoundSide(root,store);
            byte[] bytes=[1,2,3]; using var hash=new ContentHash(); hash.Append(bytes);
            var entry=new Entry("target",false,bytes.Length,DateTimeOffset.UnixEpoch,"item","",hash.Finish(),null);
            using var input=new MemoryStream(bytes);
            var stage=await side.StageAsync(entry.Path,input,entry,null,default); var proof=stage.Proof!;
            var selection=new RemovalSelection(new(entry,proof.VolumeId,entry.ContentHash),null);
            await store.ArmAsync("pair",selection,proof,default); await stage.DisposeAsync();
            File.SetAttributes(proof.Path,File.GetAttributes(proof.Path)|FileAttributes.Offline);
            Check.Require(File.GetAttributes(proof.Path).HasFlag(FileAttributes.Offline),"offline stage fixture was not established");
            try { await using var invalid=await side.ReopenStageAsync(selection,proof,default); throw new InvalidDataException("offline armed stage was opened as proven bytes"); }
            catch(ChangedUnderneathException e) when(e.Path==entry.Path) { }
            Check.Require(await store.ArmedAsync("pair",default) is {} note && note.Stage==proof && File.Exists(proof.Path),"offline replay lost stage custody");
            File.SetAttributes(proof.Path,FileAttributes.Normal);
            await using var reopened=await side.ReopenStageAsync(selection,proof,default);
            Check.Require(reopened.Proof==proof,"readable stage did not reopen after its state was restored");
        }
        finally {Directory.Delete(root,true);}
    }
    public static async Task WriterPreconditionsAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var side=BoundSide(root,System.Reflection.DispatchProxy.Create<IStore,ArmHandleSample>()); var full=Path.Combine(root,"file"); byte[] old=[1],fresh=[2];
            File.WriteAllBytes(full,old); File.SetAttributes(full,FileAttributes.Normal);
            var target=(await side.ReadOneAsync("file",default))!;
            var source=target with {ContentHash=Hash(fresh)};
            string domain=DriveIdentity.Read(root).Identity.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var selection=new RemovalSelection(new(target,domain,Hash(old)),null);
            using(var wrong=new MemoryStream(old))
            {
                try { await using var bad=await side.StageAsync("file",wrong,source,target,default); throw new InvalidDataException("stage accepted wrong bytes"); }
                catch(ItemFailedException e) when(e.Cause==TextKey.DownloadHashMismatch) { }
            }
            using var input=new MemoryStream(fresh); await using var staged=await side.StageAsync("file",input,source,target,default);
            try { await side.PublishAsync(staged,null,null,default); throw new InvalidDataException("create-only replaced an occupant"); }
            catch(PathOccupiedException) { }
            Check.Require(File.ReadAllBytes(full).SequenceEqual(old),"create-only changed existing bytes");
            File.WriteAllBytes(full,[3,4]);
            try {await side.PublishAsync(staged,target,selection,default);throw new InvalidDataException("stale update was applied");}
            catch(ChangedUnderneathException) { }
            try {await side.DeleteAsync(target,selection,default);throw new InvalidDataException("stale delete was applied");}
            catch(ChangedUnderneathException) { }
            Check.Require(File.ReadAllBytes(full).SequenceEqual(new byte[]{3,4}) && (await side.ReadOneAsync("file",default))?.Id==target.Id,
                "stale operation changed the later version");
            var parent=Directory.CreateDirectory(Path.Combine(root,"sensitive")).FullName;
            using(var handle=OpenDirectory(parent,0x100,FileShare.ReadWrite|FileShare.Delete,IntPtr.Zero,3,0x02000000,IntPtr.Zero))
            {
                uint enable=1; Check.Require(!handle.IsInvalid && SetCaseSensitive(handle,23,ref enable,4),"case-sensitive fixture unavailable");
            }
            var a=Path.Combine(parent,"Folder"); var b=Path.Combine(parent,"folder");
            Directory.CreateDirectory(a); Directory.CreateDirectory(b); File.WriteAllBytes(Path.Combine(a,"a"),old); File.WriteAllBytes(Path.Combine(b,"b"),fresh);
            var from=(await side.ReadOneAsync("sensitive/Folder",default))!; var to=(await side.ReadOneAsync("sensitive/folder",default))!;
            Check.Require(from.Id!=to.Id,"case-sensitive distinct folder identities not observed");
            bool refused=false;
            try { await side.MoveAsync(from,to.Path,default); }
            catch(Exception e) when(e is PathOccupiedException or IOException) { refused=true; }
            Check.Require(refused,"move accepted a distinct case-equivalent occupant");
            Check.Require((await side.ReadOneAsync(from.Path,default))?.Id==from.Id && (await side.ReadOneAsync(to.Path,default))?.Id==to.Id &&
                File.ReadAllBytes(Path.Combine(a,"a")).SequenceEqual(old) && File.ReadAllBytes(Path.Combine(b,"b")).SequenceEqual(fresh),"folder identity or bytes lost");
        }
        finally {Directory.Delete(root,true);}
        static string Hash(byte[] bytes){using var hash=new ContentHash();hash.Append(bytes);return hash.Finish();}
    }
    [System.Runtime.InteropServices.DllImport("kernel32",EntryPoint="SetFileInformationByHandle",SetLastError=true)]
    private static extern bool SetCaseSensitive(Microsoft.Win32.SafeHandles.SafeFileHandle h,int kind,ref uint info,uint bytes);
    public static async Task LongPathRecoveryAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var side=BoundSide(root); var parent=string.Join("/",Enumerable.Repeat(new string('d',48),6));
            Directory.CreateDirectory(Path.Combine(root,parent));
            var path=PathNames.Child(parent,"receipt"); var native=Path.GetFullPath(Path.Combine(root,path));
            // Raw directory probe: host qualification, not setup. It must fail on length (206) before the
            // caller's deep-directory effect can count as proof of operation without raw long-path support.
            var raw=native+"-probe";
            var rawDirectoryCreated=RawCreate(raw,IntPtr.Zero);
            var rawDirectoryError=System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            var rawDirectoryRejected=!rawDirectoryCreated && rawDirectoryError==206;
            // Converter assertion only: this observes the shared converter's output for the >MAX_PATH path;
            // it does not show the caller passes it, so it is not proof of operation without the opt-in.
            var extended=DriveIdentity.ToExtendedWindowsPath(native);
            Check.Require(native.Length>260 && extended.StartsWith(@"\\?\") && extended!=native,
                "extended spelling converter did not prefix a >MAX_PATH path");
            try
            {
                var result=await side.CreateFolderExclusiveAsync(path,default);
                Check.Require(result is {Outcome:RemovalOutcome.Published,Final.IsFolder:true} && native.Length>260,"deep native directory not created");
            }
            catch(Exception e) when(e is IOException or ItemFailedException or System.ComponentModel.Win32Exception)
            { throw new InvalidDataException("extended native directory creation failed",e); }
            var child=PathNames.Child(path,"child"); var full=Path.GetFullPath(Path.Combine(root,child)); File.WriteAllBytes(full,[1,3]);
            var file=(await side.ReadOneAsync(child,default))!;
            try { await using var read=await side.OpenReadAsync(file,default); Check.Require(read.ReadByte()==1 && read.ReadByte()==3 && read.ReadByte()==-1,"deep native content bytes differ"); }
            catch(Exception e) when(e is IOException or ItemFailedException or System.ComponentModel.Win32Exception)
            { throw new InvalidDataException("extended native data open failed",e); }
            // Raw data probe: host qualification. The caller create/read count as proof of operation without
            // raw long-path support only where both raw probes fail on length (206); otherwise it stays open.
            using var rawData=OpenDirectory(full,0x80000000,FileShare.ReadWrite|FileShare.Delete,IntPtr.Zero,3,0x00300000,IntPtr.Zero);
            var rawDataError=System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            var rawDataRejected=rawData.IsInvalid && rawDataError==206;
            if(!rawDirectoryRejected || !rawDataRejected)
                throw new NotImplementedException("caller proof requires a raw-rejecting host: raw directory and data probes must report 206");
        }
        finally { Directory.Delete(root,true); }
    }
    [System.Runtime.InteropServices.DllImport("kernel32",EntryPoint="CreateDirectoryW",CharSet=System.Runtime.InteropServices.CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    private static extern bool RawCreate(string path,IntPtr security);
    public static async Task ReceiptWalkAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var side=BoundSide(root); var native=Directory.CreateDirectory(Path.Combine(root,"receipt")).FullName;
            File.WriteAllBytes(Path.Combine(native,"chosen"),[1,3]); File.WriteAllBytes(Path.Combine(native,"ignored"),[5,7]);
            var folder=(await side.ReadOneAsync("receipt",default))!; var receipt=new IdentityLocation(DriveIdentity.Read(root).Identity.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),native,folder);
            var tree=(Recovery.Folder)(await side.RecoverAsync(receipt,folder,default))!;
            using var deny=File.OpenHandle(Path.Combine(native,"ignored"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);
            var names=new List<string>(); bool metadataOnly=true;
            try { await foreach(var child in tree.Contents) { metadataOnly &= child.Entry is {IsFolder:false,ContentHash:""}; names.Add(PathNames.Name(child.Path)); } }
            catch(IOException e) when((e.HResult&0xffff)==32) { throw new InvalidDataException("receipt walk attempted a data open on the locked child",e); }
            Check.Require(metadataOnly,"metadata walk returned a descendant digest");
            Check.Require(names.Order().SequenceEqual(["chosen","ignored"]),"selected and untouched receipt members not listed");
        }
        finally { Directory.Delete(root,true); }
    }
    public static async Task SelectedReceiptAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var side=BoundSide(root);var native=Path.Combine(root,"chosen");File.WriteAllBytes(native,[1,3]);var chosen=(await side.ReadOneAsync("chosen",default))!;
            var actual=new IdentityLocation(DriveIdentity.Read(root).Identity.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),native,chosen);
            var selected=await side.RecoverAsync(actual,chosen,default) as Recovery.File ?? throw new InvalidDataException("selected child not proved");
            await using var stream=selected.Bytes; using var copy=new MemoryStream(); await stream.CopyToAsync(copy); using var hash=new ContentHash(); hash.Append(new byte[]{1,3});
            var wrong=new List<string>();
            if(!copy.ToArray().SequenceEqual(new byte[]{1,3}) || selected.Version.ContentHash!=hash.Finish()) wrong.Add("no digest for selected bytes");
            if(await side.RecoverAsync(actual,chosen with {ContentHash=new string('0',64)},default) is Recovery.File unexpected)
            { await unexpected.Bytes.DisposeAsync(); wrong.Add("accepted conflicting known digest"); }
            Check.Require(wrong.Count==0,string.Join("; ",wrong));
        }
        finally {Directory.Delete(root,true);}
    }
    // Native publication produces the actual file's record; a volume tail carries no file identity.
    public static async Task PublishedFileRecordAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var side=BoundSide(root); const string name="published"; byte[] bytes=[7,13];
            using var digest=new ContentHash(); digest.Append(bytes);
            var source=new Entry(name,false,bytes.Length,DateTimeOffset.UnixEpoch,"source","",digest.Finish(),null);
            using var input=new MemoryStream(bytes); await using var stage=await side.StageAsync(name,input,source,null,default);
            var result=await side.PublishAsync(stage,null,null,default);
            Check.Require(result is {Outcome:RemovalOutcome.Published,Final:not null} && result.Final.Id==stage.Proof!.FileId,"stage did not become final");
            using var handle=File.OpenHandle(Path.Combine(root,name),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var record=new byte[4096];
            var readable=DeviceIoControl(handle,0x900eb,IntPtr.Zero,0,record,record.Length,out var count,IntPtr.Zero);
            JournalPosition? volume;
            try { volume=(await side.BoundaryAsync(default)).Position; } catch(JournalSourceException) { volume=null; }
            if(volume is null || !readable)
            {
                Check.Require(result.LocalPublished is null,"invented file proof without a readable journal");
                throw new InvalidDataException("F004 positive arm requires a readable active NTFS journal; null is not positive proof");
            }
            Check.Require(count>=60 && BitConverter.ToUInt16(record,4)==2,"unsupported file-record fixture");
            var actualFile=BitConverter.ToInt64(record,8).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Check.Require(result.Final!.Id==actualFile && result.LocalPublished==new JournalPosition(volume.JournalId,BitConverter.ToUInt64(record,24)),
                "PublishAsync did not return this final file's exact close record");
        }
        finally { Directory.Delete(root,true); }
    }
    [System.Runtime.InteropServices.DllImport("kernel32",SetLastError=true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h,uint control,IntPtr input,int size,
        [System.Runtime.InteropServices.Out] byte[] data,int capacity,out int length,IntPtr overlapped);

    public static async Task BoundInventoryAsync()
    {
        var home=Directory.CreateTempSubdirectory().FullName; var root=Path.Combine(home,"root");
        try
        {
            Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root,"kept"),"original");
            Directory.CreateDirectory(Path.Combine(root,"excluded")); File.WriteAllText(Path.Combine(root,"excluded","child"),"excluded");
            var side=BoundSide(root,new Exclusion(Side.Local,"excluded",true));
            var rows=new List<string>(); await foreach(var row in side.ReadAllAsync("",new(true,false),default)) rows.Add(row.Path);
            Check.Require(rows.SequenceEqual(["kept"]),"ReadAllAsync did not forward the effective scope");
        }
        finally { Directory.Delete(home,true); }
    }
    // Exercise interface dispatch: an advanced volume tail is not this member's contiguous position.
    public static async Task SettledPrefixAsync()
    {
        var home=Directory.CreateTempSubdirectory();
        try
        {
            ISide side=BoundSide(home.FullName);
            var start=(await side.BoundaryAsync(default)).Position ?? throw new InvalidDataException("settlement fixture requires a readable NTFS journal");
            var page=await side.ReadChangesAsync(null,start,(null,start),default);
            Check.Require(page.CaughtUp && page.Position==start,"member did not initialize at the qualified boundary");
            await side.SettleJournalAsync(start,default); File.WriteAllBytes(Path.Combine(home.FullName,"later"),[4]);
            var tail=(await side.BoundaryAsync(default)).Position;
            Check.Require(tail!=null && tail.JournalId==start.JournalId && tail.NextUsn>start.NextUsn,"fixture did not advance the journal beyond the member");
            Check.Require(await side.SettledJournalAsync(default)==start,"Local endpoint did not return the member's contiguous settled prefix");
        }
        finally { home.Delete(true); }
    }
    public static async Task ReplacedInventoryRootAsync()
    {
        var home=Directory.CreateTempSubdirectory().FullName; var root=Directory.CreateDirectory(Path.Combine(home,"root")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(root,"kept"),"original"); var side=BoundSide(root);
            // The iterator must authenticate the original directory when it starts, not just at method entry.
            var item=await side.ReadOneAsync("kept",default) ?? throw new InvalidDataException("bound fixture missing");
            var queued=side.ReadAllAsync("",new(true,false),default);
            Directory.Move(root,Path.Combine(home,"old"));
            try { _=await side.ReadOneAsync("kept",default); throw new InvalidDataException("missing bound root supplied absence"); }
            catch(ChangedUnderneathException e) when(e.Path==root) { }
            try { _=await side.ReadByIdAsync(item.Id,default); throw new InvalidDataException("by-ID lookup accepted absent bound root"); }
            catch(ChangedUnderneathException e) when(e.Path==root) { }
            try { await side.EnsureFolderAsync("new",default); throw new InvalidDataException("effect accepted absent bound root"); }
            catch(ChangedUnderneathException e) when(e.Path==root) { }
            Directory.CreateDirectory(root);
            try { await side.EnsureFolderAsync("new",default); throw new InvalidDataException("effect accepted replacement root"); }
            catch(ChangedUnderneathException e) when(e.Path==root) { }
            Check.Require(!Directory.Exists(Path.Combine(root,"new")),"effect mutated the replacement root");
            File.WriteAllText(Path.Combine(root,"replacement"),"other");
            try { await foreach(var _ in queued) throw new InvalidDataException("replacement root supplied inventory coverage");
                throw new InvalidDataException("replacement root supplied inventory coverage"); }
            catch(ChangedUnderneathException e) when(e.Path==root) { }
            Check.Require(File.ReadAllText(Path.Combine(home,"old","kept"))=="original" && File.ReadAllText(Path.Combine(root,"replacement"))=="other","root rejection did not preserve both directories");
            try { _=await side.ReadOneAsync("kept",default); throw new InvalidDataException("exact lookup accepted replacement root"); }
            catch(ChangedUnderneathException e) when(PathNames.Same(e.Path,root)) { }
        }
        finally { Directory.Delete(home,true); }
    }
    // Stop at the real arm boundary, before the recycle call. Count held native handles, not source tokens.
    public static async Task ParentPinCountAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var (bound,drive)=DriveIdentity.Read(root);
            var pair=new PairConfig("pair","Pair",root,"/root","account",[],[],FirstRunPolicy.NewestWins,true,null,
                new(root,"/root","account",bound,new("account","root","home","folder")));
            var store=System.Reflection.DispatchProxy.Create<IStore,ArmHandleSample>();
            var sample=(ArmHandleSample)(object)store;
            var scope=new PathScope(pair,[pair],drive);
            var side=LocalSide.Factory()(pair,()=>scope,SettingsParser.Load(),store);
            byte[] original=[1], replacement=[2];
            using var oldHash=new ContentHash(); oldHash.Append(original); var oldDigest=oldHash.Finish();
            using var hash=new ContentHash(); hash.Append(replacement); var newDigest=hash.Finish();
            async Task<int> Count(string parent)
            {
                var path=PathNames.Child(parent,"file"); var full=Path.Combine(root,path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllBytes(full,original);
                var victim=await side.ReadOneAsync(path,default) ?? throw new InvalidDataException("missing fixture");
                var selected=new RemovalSelection(new RemovalProof(victim,bound.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),oldDigest),null);
                using var input=new MemoryStream(replacement);
                await using var stage=await side.StageAsync(path,input,victim with {ContentHash=newDigest},victim,default);
                var start=ArmHandleSample.Handles();
                try { await side.PublishAsync(stage,victim,selected,default); throw new InvalidDataException("expected pre-recycle arm"); }
                catch(ArmHandleSample.AtArm) { return sample.Count-start; }
            }
            await Count("warm");
            const int extra=20; var deep="parent/"+string.Join("/",Enumerable.Repeat("p",extra));
            var shallowCount=await Count("parent"); var deepCount=await Count(deep);
            var added=deepCount-shallowCount;
            // A second retained source chain adds another twenty handles; allow only small process background drift.
            Check.Require(Math.Abs(added-extra)<=2,"parent pin count did not grow by one handle per added directory");

        }
        finally { Directory.Delete(root,true); }
    }
    public class ArmHandleSample : System.Reflection.DispatchProxy
    {
        internal sealed class AtArm : Exception { }
        private readonly IStore memory=new TestDoubles.InMemoryStateStore();
        internal int Count { get; private set; }
        internal static int Handles()=>GetProcessHandleCount(new IntPtr(-1),out var n)?n:throw new System.ComponentModel.Win32Exception();
        protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? args)
        {
            if(method?.Name==nameof(IStore.ArmAsync)) { Count=Handles(); throw new AtArm(); }
            return method!.Invoke(memory,args);
        }
        [System.Runtime.InteropServices.DllImport("kernel32",SetLastError=true)]
        private static extern bool GetProcessHandleCount(IntPtr process,out int count);
    }
    public static async Task ExcludedFirstAliasAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        var path=Path.Combine(root,"one"); var alias=Path.Combine(root,"two");
        try
        {
            var (identity,drive)=DriveIdentity.Read(root);
            File.WriteAllText(path,"version");
            if(!CreateHardLinkW(alias,path,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            var observed=DirectoryListing.One(root,"one") ?? throw new InvalidDataException("missing fixture");
            var id=unchecked((ulong)long.Parse(observed.Id,System.Globalization.CultureInfo.InvariantCulture));
            var (_,links)=IdentityPaths.ResolveLinksById(root,identity,id,true,default);
            var names=links?.Select(p=>PathNames.RelativeUnderLocalRoot(root,p)).OfType<string>().Order(StringComparer.Ordinal).ToArray();
            if(names is not {Length:2}) throw new InvalidDataException("incomplete alias fixture");
            var pair=new PairConfig("pair","Pair",root,"/root","account",[],[new Exclusion(Side.Local,names[0],true)],
                FirstRunPolicy.NewestWins,true,null,new(root,"/root","account",identity,new("account","root","home","folder")));
            var scope=new PathScope(pair,[pair],drive);
            var side=LocalSide.Factory()(pair,()=>scope,SettingsParser.Load(),new TestDoubles.InMemoryStateStore());
            var actual=await side.ReadByIdAsync(observed.Id,default);
            Check.Require(actual is { InScope:true } && actual.Value.Location.Entry.Id==observed.Id &&
                PathNames.Same(actual.Value.Location.Entry.Path,names[1]),"excluded first alias concealed eligible second alias");
        }
        finally { File.Delete(alias); File.Delete(path); Directory.Delete(root); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32",CharSet=System.Runtime.InteropServices.CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    private static extern bool CreateHardLinkW(string alias,string existing,IntPtr security);
}
