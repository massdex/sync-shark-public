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
    const uint Extend=2, Create=0x100, Delete=0x200, Old=0x1000, New=0x2000, Close=0x80000000;
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
    // The scope Repair the upload owes (a's folder, the root here) leaves only b, one binding, and a in Dropbox history.
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
            using var store=new InMemoryStateStore();
            await store.SavePairAsync(pair,default);
            await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
            await store.SavePositionAsync(volume.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),new JournalPosition(1,1),default);
            var local=LocalSide.Factory()(pair,()=>paths,ItemChecks.Settings,store);
            var dropbox=new FakeSide(Side.Dropbox);
            var pcB=(await local.ReadOneAsync("b",default))!;
            var bytes=File.ReadAllBytes(Path.Combine(root,"b"));
            using var hash=new ContentHash(); hash.Append(bytes); var digest=hash.Finish();
            var peerA=pcB with {Path="a",Id="db-a",Revision="r1",ContentHash=digest}; var peerB=pcB with {Id="db-b",Revision="r1",ContentHash=digest};
            dropbox.Seed(peerA,bytes); dropbox.Seed(peerB,bytes);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(pcB with {Path="a"},peerA),null,null,default);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(pcB,peerB),null,null,default);
            var queue=new WorkQueue();
            await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,local,dropbox,store,static()=>{},default);
            await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,dropbox,store,new Scope(string.Empty,string.Empty),null,default);
            Check.Require(!dropbox.Items.ContainsKey("a") && dropbox.Items.ContainsKey("b") && dropbox.History.Any(v=>v.Entry.Id=="db-a"),"the stray Dropbox copy at the old name survived the owed Repair or lost its version");
            Check.Require(store.Records.Values.Count(r=>r.LocalId==pcB.Id)==1 && File.Exists(Path.Combine(root,"b")) && store.OpenNotices.Count==0,"the owed Repair left two bindings for one PC file, touched the PC file or raised a notice");
        }
        finally { home.Delete(true); }
    }

    private static async Task<(List<Change> Changes,long Revision,ulong? Settled,int Uncertain)> Read(UsnRecord[] records,bool moved,bool tmpGone=false)
    {
        var f=await OpenAsync(records,moved,tmpGone); using var store=f.Store;
        return await ReadAsync(f,records.Max(r=>r.Usn)+1);
    }

    private static async Task<(List<Change> Changes,long Revision,ulong? Settled,int Uncertain)> ReadAsync((InMemoryStateStore Store,JournalReader Reader,PairConfig Pair,EngineSettings Settings) f,ulong end)
    {
        var page=await f.Reader.ReadPageAsync(f.Pair,new(1,10),new(1,end),f.Settings,default);
        if(page.Position!=null) await f.Reader.SettleAsync(f.Pair,page.Position,f.Store,default);
        var debt=await f.Store.ReadRepairAsync("a",Side.Local,default);
        return (page.Changes.ToList(),debt.Revision,(await f.Reader.SettledJournalAsync(f.Pair,default))?.NextUsn,page.Uncertain.Count);
    }

    private static async Task<(InMemoryStateStore Store,JournalReader Reader,PairConfig Pair,EngineSettings Settings)> OpenAsync(UsnRecord[] records,bool moved,bool tmpGone)
    {
        var store=new InMemoryStateStore(); var src=new Source{Records=records}; var reader=new JournalReader(src);
        var root=Path.Join(Path.GetTempPath(),"Pair A"); var identity=new RootIdentity(root,"/a","test",new(1,Root,null),new("a","b","c","d"));
        var a=new PairConfig("a","Pair A",root,"/a","test",[],[],FirstRunPolicy.NewestWins,true,null,identity);
        var scope=new PathScope(a,[a],DriveType.Fixed); var settings=SettingsParser.Load();
        var start=new JournalPosition(1,10); await store.SavePositionAsync("1",start,default);
        await store.CommitAsync("a",null,new(PathNames.Key("dir"),"dir","dir","dir",true,Dir.ToString(),0,0,"d",""),null,null,default);
        await store.CommitAsync("a",null,new(PathNames.Key("dir/name"),"dir/name","dir/name","dir/name",false,Target.ToString(),0,0,"c",""),null,null,default);
        await store.SavePairAsync(a,default);
        reader.Attach(a,store,()=>scope,(id,_,_)=>id switch {
                Tmp=>tmpGone ? (null,null) : (Path.Join(root,"dir","name"),null),
                Target=>moved ? (Path.Join(root,"dir","renamed"),null) : (null,null),
                Fresh=>(Path.Join(root,"dir","new"),null),
                Dir=>(Path.Join(root,"dir"),null), _=>(null,null)},
            p=>p switch { "dir/name"=>File(p,Tmp), "dir/renamed"=>File(p,Target), "dir/new"=>File(p,Fresh), "dir"=>new(p,true,0,DateTimeOffset.UnixEpoch,Dir.ToString(),"","",null), _=>null });
        return (store,reader,a,settings);
        static Entry File(string path,ulong id)=>new(path,false,5,DateTimeOffset.UnixEpoch,id.ToString(),"","",null);
    }
}
