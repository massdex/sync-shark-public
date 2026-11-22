using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks the evidence carried across the local publication and listing boundaries.</summary>
public static class LocalSideChecks
{
    public static async Task RunAsync()
    {
        // A selected non-readable entry must be refused before even resolving a data filename.
        var pair = new PairConfig("pair","Pair","invalid\0root","/root","account",[],[],FirstRunPolicy.NewestWins,true,null);
        var side = LocalSide.Factory()(pair,()=>throw new InvalidOperationException(),SettingsParser.Load(),new TestDoubles.InMemoryStateStore());
        ExactRootChange();
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

    private static void ExactRootChange()
    {
        var home=Directory.CreateTempSubdirectory().FullName; var root=Path.Combine(home,"root");
        try
        {
            Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root,"kept"),"original");
            DirectoryIdentity bound;
            using(var dir=OpenDirectory(root,0x80,FileShare.ReadWrite|FileShare.Delete,IntPtr.Zero,3,0x02000000,IntPtr.Zero))
            {
                byte[] id=new byte[24]; Check.Require(!dir.IsInvalid && ReadDirectoryId(dir,18,id,24),"fixture root identity unavailable");
                bound=new(BitConverter.ToUInt64(id),BitConverter.ToUInt64(id,8),BitConverter.ToUInt64(id,16));
            }
            Directory.Move(root,Path.Combine(home,"old")); Directory.CreateDirectory(root);
            try { _=LocalSide.BoundEntry(root,bound,"kept"); throw new InvalidDataException("replacement root supplied absence"); }
            catch(ChangedUnderneathException e) when(e.Path==root) { }
            catch(InventoryIncompleteException) { throw new InvalidDataException("exact lookup routed a root replacement as an incomplete inventory"); }
        }
        finally { Directory.Delete(home,true); }
    }
    [System.Runtime.InteropServices.DllImport("kernel32",EntryPoint="CreateFileW",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectory(string path,uint access,FileShare share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)]
    private static extern bool ReadDirectoryId(Microsoft.Win32.SafeHandles.SafeFileHandle h,int kind,[System.Runtime.InteropServices.Out] byte[] data,uint bytes);
    // Registered separately: the cross-group checks must stay NOT IMPLEMENTED until their owners land.
    public static async Task LateReadFailureAsync()
    {
        // A throwing exception filter would otherwise rethrow the underlying IOException and mask a missing classifier.
        Check.Require(LocalErrors.IsFileFailure(new IOException()), "late I/O is a file failure");
        await using var reader = new LocalReadStream(new FailingRead(), "broken-item");
        try { reader.ReadByte(); throw new InvalidDataException("read did not report failure"); }
        catch (ItemFailedException e) { Check.Require(e.Path == "broken-item" && e.Cause == TextKey.ReadLocalFailed, "late read lost its item"); }
        await RecycleBinChecks.TrimContinuesAsync();
    }
    private static LocalSide BoundSide(string root,params Exclusion[] exclusions)
    {
        var (id,type)=DriveIdentity.Read(root);
        var pair=new PairConfig("pair","Pair",root,"/root","account",[],exclusions,FirstRunPolicy.NewestWins,true,null,
            new(root,"/root","account",id,new("account","root","home","folder")));
        var scope=new PathScope(pair,[pair],type);
        return LocalSide.Factory()(pair,()=>scope,SettingsParser.Load(),new TestDoubles.InMemoryStateStore());
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
            // The iterator must authenticate the original directory when it starts, not just at method entry.
            var queued=side.ReadAllAsync("",new(true,false),default);
            Directory.Move(root,Path.Combine(home,"old")); Directory.CreateDirectory(root);
            try { await foreach(var _ in queued) throw new InvalidDataException("listed replacement content");
                throw new InvalidDataException("replacement supplied empty-directory coverage"); }
            catch(InventoryIncompleteException) { }
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
            Check.Require(Math.Abs(added-extra)<=2,$"{extra} extra parents added {added} live handles at removal arm");
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
            var names=links?.Select(p=>PathNames.RelativeUnderLocalRoot(root,p)).OfType<string>().ToArray();
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
    private sealed class FailingRead : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException();
    }
}
