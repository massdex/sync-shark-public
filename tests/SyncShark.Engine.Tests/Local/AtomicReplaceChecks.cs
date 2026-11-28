using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>An unelevated host reads a name-redacted journal: an unbound tmp renamed over a bound file is a change, a gone unbound id settles,
/// a bound rename owes Repair once and its replay after that Repair settles.</summary>
public static class AtomicReplaceChecks
{
    private sealed class Source : UsnJournal
    {
        public UsnRecord[] Records=[];
        public override (ulong First,JournalPosition Tail) Retained(string root)=>(0,new(1,1000));
        public override Task<NtfsUsnPage> ReadAsync(string root,JournalPosition from,JournalPosition? end,int bytes,CancellationToken ct)
        {
            var records=Records.Where(r=>r.Usn>=from.NextUsn && (end==null || r.Usn<end.NextUsn)).ToArray();
            return Task.FromResult(new NtfsUsnPage(NtfsUsnPageStatus.Complete,records.Length==0 ? end?.NextUsn ?? from.NextUsn+10 : records.Max(r=>r.Usn)+1,null,records));
        }
    }
    const uint Extend=2, Content=7, Create=0x100, Delete=0x200, Old=0x1000, New=0x2000, Links=0x10000, Close=0x80000000;
    const ulong Root=11, Dir=40, Target=50, Tmp=60, Fresh=70;

    private static UsnRecord R(ulong usn,uint reason,ulong id)=>new(id,Dir,usn,reason,FileAttributes.Normal,null);

    // Windows order for File.Move(tmp,name,overwrite): the old file's delete comes before the rename.
    public static async Task RedactedAtomicReplaceAsync()
    {
        var (changes,revision,settled,uncertain)=await Read([R(11,Create,Tmp),R(12,Create|Extend,Tmp),R(13,Create|Extend|Close,Tmp),R(14,Delete|Close,Target),R(15,Old,Tmp),R(16,New,Tmp),R(17,New|Close,Tmp)],moved:false);
        Check.Require(changes.Any(c=>c is {Kind:ChangeKind.Changed,Id:"60",Path:"dir/name"}),"atomic replace lost the change at its target path");
        Check.Require(revision==0 && uncertain==0 && settled==18,"redacted atomic replace of an unbound tmp owed Repair or held the journal position");
    }

    public static async Task RedactedBoundRenameAsync()
    {
        var f=await OpenAsync([R(11,Old,Target),R(12,New,Target),R(13,New|Close,Target),R(14,Create|Close,Fresh)],moved:true,tmpGone:false); using var store=f.Store;
        var (_,revision,settled,uncertain)=await ReadAsync(f,14);
        Check.Require(revision==1 && uncertain!=0 && settled==11,"redacted rename of a bound file no longer goes to Repair");
        Check.Require((await f.Reader.ReadPageAsync(f.Pair,null,null,f.Settings,default)).ContinuityLost,"the uncertain page did not wake Repair");
        // The full Repair relinks the binding to the new name and finishes; the host then rewinds the reader to its held position.
        var saved=(await store.FindAsync("a",Side.Local,PathNames.Key("dir/name"),default))!;
        await store.CommitAsync("a",saved,new(PathNames.Key("dir/renamed"),"dir/renamed","dir/renamed","dir/renamed",false,Target.ToString(),0,0,"c",""),null,null,default);
        await store.FinishRepairAsync("a",(revision,(await store.ReadRepairAsync("a",Side.Dropbox,default)).Revision),"{}",default);
        await f.Reader.Membership(f.Pair,true); await f.Reader.Membership(f.Pair,false);
        var (changes,_,replayed,again)=await ReadAsync(f,15);
        var debt=await store.ReadRepairAsync("a",Side.Local,default);
        Check.Require(again==0 && debt.Reason==null && debt.Revision==revision && replayed==15,"replay of a reconciled redacted rename owed Repair again or held the journal position");
        Check.Require(changes.Any(c=>c is {Kind:ChangeKind.Changed,Id:"70",Path:"dir/new"}),"a PC change after the Repair was not delivered by the replay");
    }

    // Two saves before the reader catches up: the first tmp id is already retired when its records are projected.
    public static async Task RedactedGoneTempAsync()
    {
        var (_,revision,settled,uncertain)=await Read([R(11,Create,Tmp),R(12,Create|Extend|Close,Tmp)],moved:false,tmpGone:true);
        Check.Require(revision==0 && uncertain==0 && settled==13,"a redacted record of an unbound id that no longer exists owed Repair or held the journal position");
    }

    // End state of a PC rename a -> b during a's first upload: Dropbox has a and b, the store binds both to one PC file.
    // The second install owes a file scope per name; serving exactly those scopes leaves only b, one binding, and a in Dropbox history.
    public static async Task MovedFirstUploadRepairAsync()
    {
        var home=Directory.CreateTempSubdirectory();
        try
        {
            var root=Directory.CreateDirectory(Path.Combine(home.FullName,"root")).FullName;
            File.WriteAllText(Path.Combine(root,"b"),"content-v1");
            var (volume,drive)=DriveIdentity.Read(root);
            var pair=ItemChecks.Pair() with { LocalRoot=root, Roots=new(root,"/Pair A","test",volume,new("account","root","home","folder")) };
            var paths=new PathScope(pair,[pair],drive);
            using var store=await Massdex.SyncShark.Engine.Storage.Store.OpenAsync(Path.Combine(home.FullName,"product"),true,ItemChecks.Settings,default) ?? throw new InvalidDataException("No store");
            await store.SavePairAsync(pair,default);
            await store.FinishRepairAsync(pair.PairId,((await store.ReadRepairAsync(pair.PairId,Side.Local,default)).Revision,(await store.ReadFullRepairAsync(pair.PairId,default)).Revision),"{}",default);
            Check.Require((await store.RepairStateAsync(pair.PairId,default)).FirstRepairDone,"The pair's first Repair did not complete");
            await store.SavePositionAsync(volume.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),new JournalPosition(1,1),default);
            var local=LocalSide.Factory()(pair,()=>paths,ItemChecks.Settings,store);
            var dropbox=Executor.FolderDeleteChecks.Rooted(new FakeSide(Side.Dropbox),pair);
            var pcB=(await local.ReadOneAsync("b",default))!;
            var bytes=File.ReadAllBytes(Path.Combine(root,"b"));
            using var hash=new ContentHash(); hash.Append(bytes); var digest=hash.Finish();
            var peerA=pcB with {Path="a",Id="db-a",Revision="r1",ContentHash=digest}; var peerB=pcB with {Id="db-b",Revision="r1",ContentHash=digest};
            dropbox.Seed(peerA,bytes); dropbox.Seed(peerB,bytes);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(pcB with {Path="a"},peerA),null,null,default);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(pcB,peerB),null,null,default);
            var owed=(await store.PairAsync(pair.PairId,default))?.OwedScopes ?? [];
            Check.Require(owed.Select(s=>s.LocalPath).Order(StringComparer.Ordinal).SequenceEqual(["a","b"]),"the second install did not owe a file scope for each name: "+string.Join(",",owed));
            var queue=new WorkQueue();
            await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,local,dropbox,store,static()=>{},default);
            foreach(var scope in owed) await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,dropbox,store,scope,null,default);
            Check.Require(dropbox.Mutations.SequenceEqual(["db-a"]) && !dropbox.Items.ContainsKey("a") && dropbox.Items["b"].Entry==peerB && dropbox.History.Any(v=>v.Entry.Id=="db-a" && v.Bytes.SequenceEqual(bytes)),"the owed scopes did not delete exactly the stray Dropbox copy into its history");
            var links=new List<SyncedEntry>();await foreach(var r in store.ByIdAsync(pair.PairId,Side.Local,pcB.Id,default)) links.Add(r);
            var notices=0;await foreach(var _ in store.NoticesAsync(pair.PairId,default)) notices++;
            Check.Require(links is [{LocalPath:"b"}] && File.ReadAllBytes(Path.Combine(root,"b")).SequenceEqual(bytes) && notices==0 && (await store.PairAsync(pair.PairId,default))?.OwedScopes is null or {Count:0},
                "the owed scopes left two bindings for one PC file, touched the PC file, raised a notice or stayed owed");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();home.Delete(true); }
    }

    // A hop already at its saved names settles when the only extra live alias is an unsaved ignored, excluded or stage name.
    public static async Task IgnoredAliasHopAsync()
    {
        var stage=Path.GetFileName(StageNames.For(Path.Join(Path.GetTempPath(),"x")));
        using var digest=new ContentHash();digest.Append([5]);var hash=digest.Finish();
        var wrong=new List<string>();
        foreach(var (name,links,rules,armed,owes) in new (string,string[],Exclusion[],bool,bool)[]{
            ("ignored hard link",["dir/renamed","dir/x"],[new(Side.Local,"dir/x",true)],false,false),
            ("eligible unbound link",["dir/renamed","dir/c"],[],false,true),
            ("rename into an ignored name",["dir/x"],[new(Side.Local,"dir/x",false)],false,true),
            ("KEEP-bound saved name missing",["dir/x"],[new(Side.Local,"dir/renamed",true),new(Side.Local,"dir/x",false)],false,true),
            ("armed victim",["dir/renamed","dir/x"],[new(Side.Local,"dir/x",false)],true,true),
            ("stage alias",["dir/renamed","dir/"+stage],[],false,false)})
        {
            var f=await OpenAsync([R(11,Old,Target),R(12,New,Target),R(13,New|Close,Target)],moved:true,tmpGone:false,"dir/renamed",links,rules);using var store=f.Store;
            if(armed) await store.ArmAsync("a",new(new(new("dir/renamed",false,5,DateTimeOffset.UnixEpoch,Target.ToString(),"","",null),"1",hash),null),null,default);
            var (_,revision,_,uncertain)=await ReadAsync(f,14);
            if((uncertain!=0 && revision==1)!=owes) wrong.Add($"{name}: revision {revision}, uncertain {uncertain}");
        }
        Check.Require(wrong.Count==0,"a redacted hop at its saved names settled or owed wrongly: "+string.Join("; ",wrong));
    }

    // A redacted hop whose only live alias is the saved place up to ancestor case is an ordinary change, not a Repair owe.
    public static async Task AncestorCaseHopAsync()
    {
        var (changes,revision,settled,uncertain)=await Read([R(11,Old,Target),R(12,New,Target),R(13,New|Close,Target)],moved:true,saved:"Dir/renamed");
        Check.Require(changes.Any(c=>c is {Kind:ChangeKind.Changed,Id:"50",Path:"dir/renamed",OldPath:null}),"an ancestor-case hop at the saved place lost its ordinary change");
        Check.Require(!changes.Any(c=>c is {Kind:ChangeKind.Renamed,Id:"50"}),"an ancestor-case hop at the saved place was reported as a move");
        Check.Require(revision==0 && uncertain==0 && settled==14,"a redacted hop at its saved place up to ancestor case owed Repair or held the journal");
    }

    // A leaf-capitals rename is a different place: the redacted hop still owes Repair (preservation control).
    public static async Task LeafCaseHopAsync()
    {
        var (_,revision,_,uncertain)=await Read([R(11,Old,Target),R(12,New,Target),R(13,New|Close,Target)],moved:true,saved:"dir/Renamed");
        Check.Require(revision==1 && uncertain!=0,"a leaf-capitals redacted rename settled as the same place");
    }

    // Two saved links whose live aliases are the saved places up to ancestor case: admitted per alias; one missing saved place still owes Repair.
    public static async Task TwoLinkHopAsync()
    {
        var records=new[]{R(11,Old,Target),R(12,New|Links,Target),R(13,New|Close|Links,Target)};
        var f=await OpenAsync(records,moved:true,tmpGone:false,saved:"Dir/a",savedNames:["Dir/a","Dir/b"],links:["dir/a","dir/b"]); using var store=f.Store;
        var (changes,revision,_,uncertain)=await ReadAsync(f,14);
        Check.Require(uncertain==0 && revision==0,"two saved places, both live up to ancestor case, owed Repair");
        Check.Require(changes.Count(c=>c is {Kind:ChangeKind.Changed,Id:"50",Path:"dir/a",OldPath:null})==2 &&
            changes.Count(c=>c is {Kind:ChangeKind.Changed,Id:"50",Path:"dir/b",OldPath:null})==2,
            "an admitted two-link hop did not emit one ordinary change per alias per record");
        Check.Require(!changes.Any(c=>c is {Kind:ChangeKind.Renamed,Id:"50"}),"a two-link hop at the saved places was reported as a move");
        var partial=await OpenAsync(records,moved:true,tmpGone:false,saved:"Dir/a",savedNames:["Dir/a","Dir/b"],links:["dir/a"]); using var partialStore=partial.Store;
        var (_,partialRevision,_,partialUncertain)=await ReadAsync(partial,14);
        Check.Require(partialRevision==1 && partialUncertain!=0,"a saved place with no live alias was admitted as already at the saved places");
    }

    // T1's unacknowledged page through ordinary intake: unchanged content at the saved place up to ancestor case records the spelling.
    public static Task AncestorCaseRecordAsync() => Run.AutoSyncChecks.WithinDeadline(async ct =>
    {
        var saved=ItemChecks.File(Side.Local,"Dir/renamed",5,"50"); var db=ItemChecks.File(Side.Dropbox,"Dir/renamed",5,"db");
        var current=saved with {Path="dir/renamed"};
        var f=await HopPageAsync([R(11,Old,Target),R(12,New,Target),R(13,New|Close,Target)],SyncedEntries.From(saved,db),current,[5],db,[5]);
        using var store=f.Store;
        await AutoSync.ApplyPageAsync(f.Pair,ItemChecks.Settings,_=>{},f.Paths,f.Queue,Side.Local,f.Page,NameSpelling(f.Pc),f.Db,store,ct);
        var binding=store.Records.Values.Single();
        Check.Require(binding.LocalPath=="dir/renamed" && binding.LocalId=="50" && binding.DropboxId=="db","an unchanged ancestor-case hop did not record at the observed place");
        Check.Require(f.Pc.Mutations.Count==0 && f.Db.Mutations.Count==0 && store.OpenNotices.Count==0,"an unchanged ancestor-case hop wrote an endpoint or raised a notice");
    });

    // A same-size edit with the saved stamp needs the dirty flag and the hash: one upload to the saved Dropbox name, no move.
    public static Task AncestorCaseEditAsync() => Run.AutoSyncChecks.WithinDeadline(async ct =>
    {
        var saved=ItemChecks.File(Side.Local,"Dir/renamed",1,"50"); var db=ItemChecks.File(Side.Dropbox,"Dir/renamed",1,"db");
        var current=ItemChecks.File(Side.Local,"dir/renamed",2,"50") with {Modified=saved.Modified};
        var f=await HopPageAsync([R(11,Old,Target),R(12,New,Target),R(13,New|Close|Content,Target)],SyncedEntries.From(saved,db),current,[2],db,[1]);
        using var store=f.Store;
        await AutoSync.ApplyPageAsync(f.Pair,ItemChecks.Settings,_=>{},f.Paths,f.Queue,Side.Local,f.Page,NameSpelling(f.Pc),f.Db,store,ct);
        var binding=store.Records.Values.Single();
        Check.Require(f.Db.Requests[nameof(ISide.PublishAsync)]==1 && f.Db.Items.TryGetValue("Dir/renamed",out var up) && up.Bytes.SequenceEqual(new byte[]{2}),
            "a same-size ancestor-case edit was not uploaded exactly once to the saved Dropbox name");
        Check.Require(binding.LocalPath=="dir/renamed" && binding.LocalId=="50","a same-size ancestor-case edit did not settle at the observed place");
        Check.Require(f.Db.Requests[nameof(ISide.MoveAsync)]==0 && f.Pc.Mutations.Count==0 && store.OpenNotices.Count==0,"a same-size ancestor-case edit moved a file or raised a notice");
    });

    // T1's records read through the real projection; the caller supplies the saved binding and both endpoint states.
    private static async Task<(InMemoryStateStore Store,PairConfig Pair,PathScope Paths,FakeSide Pc,FakeSide Db,WorkQueue Queue,ChangePage Page)> HopPageAsync(
        UsnRecord[] records,SyncedEntry binding,Entry pcCurrent,byte[] pcBytes,Entry dbEntry,byte[] dbBytes)
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var store=new InMemoryStateStore(); await store.SavePairAsync(pair,default);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        pc.Seed(pcCurrent,pcBytes); db.Seed(dbEntry,dbBytes);
        await store.CommitAsync(pair.PairId,null,binding,null,null,default);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,db,store);
        await store.SavePositionAsync("1",new JournalPosition(1,10),default);
        var root=pair.LocalRoot; var reader=new JournalReader(new Source{Records=records});
        reader.Attach(pair,store,()=>paths,
            (id,_,_)=>id==Target ? (Path.Join(root,"dir","renamed"),(IReadOnlyList<string>?)null) : ((string?)null,(IReadOnlyList<string>?)null),
            path=>path=="dir/renamed"?pcCurrent:null);
        var page=await reader.ReadPageAsync(pair,new(1,10),new(1,14),ItemChecks.Settings,default);
        return (store,pair,paths,pc,db,queue,page);
    }

    // NTFS answers a differently-cased ancestor with the requested spelling; the underlying bytes/hash keep the seeded path.
    private static ISide NameSpelling(FakeSide pc)=>Executor.FolderDeleteChecks.Observe.On(pc,(op,args)=>
        op==nameof(ISide.ReadOneAsync) && args[0] is string path && pc.Items.TryGetValue(path,out var held) ? Task.FromResult<Entry?>(held.Entry with {Path=path}) : null);

    private static async Task<(List<Change> Changes,long Revision,ulong? Settled,int Uncertain)> Read(UsnRecord[] records,bool moved,bool tmpGone=false,string saved="dir/name")
    {
        var f=await OpenAsync(records,moved,tmpGone,saved); using var store=f.Store;
        return await ReadAsync(f,records.Max(r=>r.Usn)+1);
    }

    private static async Task<(List<Change> Changes,long Revision,ulong? Settled,int Uncertain)> ReadAsync((InMemoryStateStore Store,JournalReader Reader,PairConfig Pair,EngineSettings Settings) f,ulong end)
    {
        var page=await f.Reader.ReadPageAsync(f.Pair,new(1,10),new(1,end),f.Settings,default);
        if(page.Position!=null) await f.Reader.SettleAsync(f.Pair,page.Position,f.Store,default);
        var debt=await f.Store.ReadRepairAsync("a",Side.Local,default);
        return (page.Changes.ToList(),debt.Revision,(await f.Reader.SettledJournalAsync(f.Pair,default))?.NextUsn,page.Uncertain.Count);
    }

    private static async Task<(InMemoryStateStore Store,JournalReader Reader,PairConfig Pair,EngineSettings Settings)> OpenAsync(UsnRecord[] records,bool moved,bool tmpGone,string saved="dir/name",string[]? links=null,Exclusion[]? rules=null,string[]? savedNames=null)
    {
        var store=new InMemoryStateStore(); var src=new Source{Records=records}; var reader=new JournalReader(src);
        var root=Path.Join(Path.GetTempPath(),"Pair A"); var identity=new RootIdentity(root,"/a","test",new(1,Root,null),new("a","b","c","d"));
        var a=new PairConfig("a","Pair A",root,"/a","test",[],rules??[],FirstRunPolicy.NewestWins,true,null,identity);
        var scope=new PathScope(a,[a],DriveType.Fixed); var settings=SettingsParser.Load();
        var start=new JournalPosition(1,10); await store.SavePositionAsync("1",start,default);
        await store.CommitAsync("a",null,new(PathNames.Key("dir"),"dir","dir","dir",true,Dir.ToString(),0,0,"d",""),null,null,default);
        foreach(var name in savedNames ?? [saved])
            await store.CommitAsync("a",null,new(PathNames.Key(name),name,name,name,false,Target.ToString(),0,0,"c",""),null,null,default);
        await store.SavePairAsync(a,default);
        reader.Attach(a,store,()=>scope,(id,_,_)=>id switch {
                Tmp=>tmpGone ? (null,null) : (Path.Join(root,"dir","name"),null),
                Target=>moved ? (Path.Join(root,"dir","renamed"),links?.Select(l=>Path.Join([root,..l.Split('/')])).ToArray()) : (null,null),
                Fresh=>(Path.Join(root,"dir","new"),null),
                Dir=>(Path.Join(root,"dir"),null), _=>(null,null)},
            p=>p switch { "dir/name"=>File(p,Tmp), "dir/renamed"=>File(p,Target), "dir/new"=>File(p,Fresh), "dir"=>new(p,true,0,DateTimeOffset.UnixEpoch,Dir.ToString(),"","",null), _=>links?.Contains(p)==true?File(p,Target):null });
        return (store,reader,a,settings);
        static Entry File(string path,ulong id)=>new(path,false,5,DateTimeOffset.UnixEpoch,id.ToString(),"","",null);
    }
}
