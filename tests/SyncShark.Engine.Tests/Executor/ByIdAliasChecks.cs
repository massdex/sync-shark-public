using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using WorkQueue = global::Massdex.SyncShark.Engine.Run.WorkQueue;
namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>A PC file with two or more bound names moves its Dropbox copy only when its own key proves the rename; otherwise the input waits.</summary>
public static class ByIdAliasChecks
{
    private const string Id = "L";
    private sealed record Fixture(PairConfig Pair,PathScope Paths,FakeSide Pc,FakeSide Db,InMemoryStateStore Store);

    private static Fixture New()
    {
        var pair=ItemChecks.Pair();
        return new(pair,new PathScope(pair,[pair],DriveType.Fixed),new FakeSide(Side.Local),new FakeSide(Side.Dropbox),new InMemoryStateStore());
    }
    private static Entry Pc(string path,byte value=1) => ItemChecks.File(Side.Local,path,value,Id);
    private static Entry Cloud(string path,string id,byte value=1) => ItemChecks.File(Side.Dropbox,path,value,id);
    private static async Task<SyncedEntry> BindAsync(Fixture f,Entry pc,Entry cloud)
    {
        var binding=SyncedEntries.From(pc,cloud);
        await f.Store.CommitAsync(f.Pair.PairId,null,binding,null,null,default);
        return binding;
    }
    private static Task<SyncedEntry?> BoundAsync(Fixture f,string localPath) => f.Store.FindAsync(f.Pair.PairId,Side.Local,PathNames.Key(localPath),default);
    private static Change Changed(Entry observed,ulong usn,bool dirty=false) => new(Side.Local,ChangeKind.Changed,observed.Path,null,Id,observed,dirty,false,usn,JournalId:1);
    private static Task PageAsync(Fixture f,WorkQueue queue,Action<RunEvent> events,Change change,string label) =>
        Run.AutoSyncChecks.WithinDeadline(ct=>global::Massdex.SyncShark.Engine.Run.AutoSync.ApplyPageAsync(f.Pair,ItemChecks.Settings,events,f.Paths,queue,Side.Local,
            new([change],null,new(1,change.Usn!.Value+1)),f.Pc,f.Db,f.Store,ct),label);
    private static bool RetryWait(RunEvent e) => e.Kind==RunEventKind.Wait && e.Cause==TextKey.WaitItemRetry;
    private static void Settled(Fixture f,string what) =>
        Check.Require(f.Pc.Requests[nameof(ISide.NamesByIdAsync)]==0 && !f.Store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure),$"{what}: a names-by-id read or an open failure notice");

    /// <summary>T1: two other bound names and no own-key binding keep the input retained, with no upload and no move.</summary>
    public static async Task RetainedAliasesAsync()
    {
        var f=New();
        var a=Pc("A"); var c=Pc("C"); var b=Pc("B"); var dbA=Cloud("A","dA"); var dbC=Cloud("C","dC");
        f.Pc.Seed(a,[1]); f.Pc.Seed(c,[1]); f.Pc.Seed(b,[1]); f.Db.Seed(dbA,[1]); f.Db.Seed(dbC,[1]);
        var bindA=await BindAsync(f,a,dbA); var bindC=await BindAsync(f,c,dbC);
        var queue=await ItemChecks.StartQueueAsync(f.Pair,f.Paths,f.Pc,f.Db,f.Store);
        var (moves,publishes)=(f.Db.Requests[nameof(ISide.MoveAsync)],f.Db.Requests[nameof(ISide.PublishAsync)]);
        var failed=new List<(string Key,bool Reproducible)>();
        try
        {
            await ItemSync.ApplyAsync(f.Pair,ItemChecks.Settings,_=>{},f.Paths,new Change(Side.Local,ChangeKind.Changed,b.Path,null,Id,b,false,false),f.Pc,f.Db,f.Store,
                ItemChecks.Population(f.Pair,f.Paths,f.Pc,f.Db,f.Store),ItemChecks.Hop(f.Pair,f.Paths,queue,f.Pc,f.Db,f.Store),default,queue.Held,failed:(key,reproducible)=>failed.Add((key,reproducible)));
            throw new InvalidDataException("A name of a file with two other bound names was not retained");
        }
        catch(InventoryIncompleteException) { }
        Check.Require(failed.SequenceEqual([(PathNames.KeyFor(Side.Local,b.Path),false)]),"The retained input did not report its own key once as not reproducible");
        Check.Require(f.Db.Requests[nameof(ISide.MoveAsync)]==moves && f.Db.Requests[nameof(ISide.PublishAsync)]==publishes && f.Pc.Requests[nameof(ISide.NamesByIdAsync)]==0,"A retained alias moved, uploaded or read names by id");
        Check.Require(await BoundAsync(f,"A")==bindA && await BoundAsync(f,"C")==bindC && await BoundAsync(f,"B")==null,"A retained alias changed a binding");
        Check.Require(f.Db.Items["A"].Entry==dbA && f.Db.Items["A"].Bytes.SequenceEqual(new byte[]{1}) && f.Db.Items["C"].Entry==dbC && f.Db.Items["C"].Bytes.SequenceEqual(new byte[]{1}) && f.Db.Items.Count==2,"A retained alias changed Dropbox");
    }

    /// <summary>T2: a capitals-only rename at the file's own key moves its Dropbox copy in two hops, though another name is bound.</summary>
    public static Task CaseRenameWithAliasAsync() => CaseRenameAsync(edited:false);

    /// <summary>T3: the same rename with both names edited moves first, then publishes the new bytes to the same Dropbox id.</summary>
    public static Task CaseRenameEditedAsync() => CaseRenameAsync(edited:true);

    private static async Task CaseRenameAsync(bool edited)
    {
        var f=New(); byte value=edited?(byte)9:(byte)1;
        var dbFile=Cloud("file","D"); var dbC=Cloud("C","dC");
        await BindAsync(f,Pc("file"),dbFile); var bindC=await BindAsync(f,Pc("C"),dbC);
        var renamed=Pc("File",value);
        f.Pc.Seed(Pc("C",value),[value]); f.Pc.Seed(renamed,[value]); f.Db.Seed(dbFile,[1]); f.Db.Seed(dbC,[1]);
        var queue=await ItemChecks.StartQueueAsync(f.Pair,f.Paths,f.Pc,f.Db,f.Store);
        var (moves,publishes)=(f.Db.Requests[nameof(ISide.MoveAsync)],f.Db.Requests[nameof(ISide.PublishAsync)]);
        var hops=new List<(string Id,string Path)>(); f.Db.AfterMove=(entry,path)=>hops.Add((entry.Id,path));
        var events=new List<RunEvent>();
        await PageAsync(f,queue,e=>{ lock(events) events.Add(e); },Changed(renamed,1,edited),edited?"Edited case rename with an alias":"Case rename with an alias");
        var what=edited?"Edited case rename":"Case rename";
        var temporary=PathNames.CaseRenamePath("File",ItemChecks.Settings.CaseRenameSuffix);
        Check.Require(f.Db.Requests[nameof(ISide.MoveAsync)]-moves==2 && hops.SequenceEqual([("D",temporary),("D","File")]),$"{what}: the Dropbox copy did not take both case hops on its own id");
        Check.Require(f.Db.Requests[nameof(ISide.PublishAsync)]-publishes==(edited?1:0),$"{what}: wrong number of publications");
        Check.Require(f.Db.Items.TryGetValue("File",out var now) && now.Entry.Path=="File" && now.Entry.Id=="D" && now.Bytes.SequenceEqual(new[]{value}) && !f.Db.Items.ContainsKey(temporary),$"{what}: Dropbox does not hold the file's bytes at the new spelling on its own id");
        Check.Require(await BoundAsync(f,"File") is {} bound && bound with {LocalPublished=null}==SyncedEntries.From(f.Pc.Items["File"].Entry,now.Entry),$"{what}: the binding is not at the new spelling with the current stamps");
        Check.Require(await BoundAsync(f,"C")==bindC && f.Db.Items["C"].Entry==dbC,$"{what}: the other bound name changed");
        if(!edited) Check.Require(events.Count(RetryWait)==1,$"{what}: expected exactly one retry wait for the other name read first");
        Settled(f,what);
    }

    /// <summary>T4: both sides respelled differently keep their own spelling with one report, with or without another bound name.</summary>
    public static async Task DivergentCapitalsAsync()
    {
        foreach(var (alias,respelled) in new[]{(true,true),(false,true),(true,false)})
        {
            var f=New(); var what=$"Divergent capitals (alias {alias}, Dropbox respelled {respelled})";
            var dbFile=Cloud("file","D"); await BindAsync(f,Pc("file"),dbFile);
            var renamed=Pc("File"); f.Pc.Seed(renamed,[1]);
            if(alias) { var c=Pc("C"); var dbC=Cloud("C","dC"); await BindAsync(f,c,dbC); f.Pc.Seed(c,[1]); f.Db.Seed(dbC,[1]); }
            f.Db.Seed(respelled?dbFile with {Path="FILE"}:dbFile,[1]);
            var queue=await ItemChecks.StartQueueAsync(f.Pair,f.Paths,f.Pc,f.Db,f.Store);
            var (moves,publishes)=(f.Db.Requests[nameof(ISide.MoveAsync)],f.Db.Requests[nameof(ISide.PublishAsync)]);
            await PageAsync(f,queue,_=>{},Changed(renamed,1),what);
            if(!respelled)
            {
                Check.Require(f.Db.Requests[nameof(ISide.MoveAsync)]-moves==2 && f.Db.Items.TryGetValue("File",out var moved) && moved.Entry is {Path:"File",Id:"D"},$"{what}: the control did not take the case rename");
                continue;
            }
            Check.Require(f.Db.Requests[nameof(ISide.MoveAsync)]==moves && f.Db.Requests[nameof(ISide.PublishAsync)]==publishes,$"{what}: a side was overwritten or moved");
            Check.Require(await BoundAsync(f,"File") is {LocalPath:"File",DropboxPath:"FILE",LocalId:Id,DropboxId:"D"},$"{what}: the binding does not keep each side's spelling");
            Check.Require(f.Db.Items["FILE"].Entry==dbFile with {Path="FILE"} && f.Pc.Items["File"].Entry==renamed,$"{what}: an endpoint changed");
            Check.Require(f.Store.OpenNotices.Count(n=>n.Cause==TextKey.InfoCapitalsDiffer)==1,$"{what}: not exactly one capitals report");
            Settled(f,what);
            var again=new List<RunEvent>();
            await PageAsync(f,queue,e=>{ lock(again) again.Add(e); },Changed(renamed,2),what+" again");
            Check.Require(!again.Any(e=>e.Kind is RunEventKind.Effect or RunEventKind.Issue or RunEventKind.Wait or RunEventKind.Failed) && f.Store.OpenNotices.Count(n=>n.Cause==TextKey.InfoCapitalsDiffer)==1 && f.Db.Requests[nameof(ISide.MoveAsync)]==moves,$"{what}: the identical page acted again");
        }
    }

    /// <summary>T5: one bound name off the file's place is still its ordinary unique move.</summary>
    public static async Task UniqueMoveControlAsync()
    {
        var f=New(); var dbA=Cloud("A","D"); await BindAsync(f,Pc("A"),dbA);
        var b=Pc("B"); f.Pc.Seed(b,[1]); f.Db.Seed(dbA,[1]);
        var queue=await ItemChecks.StartQueueAsync(f.Pair,f.Paths,f.Pc,f.Db,f.Store);
        var (moves,publishes)=(f.Db.Requests[nameof(ISide.MoveAsync)],f.Db.Requests[nameof(ISide.PublishAsync)]);
        await PageAsync(f,queue,_=>{},Changed(b,1),"Unique by-id move");
        Check.Require(f.Db.Requests[nameof(ISide.MoveAsync)]-moves==1 && f.Db.Requests[nameof(ISide.PublishAsync)]==publishes && f.Db.Items.TryGetValue("B",out var moved) && moved.Entry.Id=="D" && !f.Db.Items.ContainsKey("A"),"A unique by-id move did not move the Dropbox copy on its id");
        Check.Require(await BoundAsync(f,"B") is {DropboxPath:"B",DropboxId:"D"} && await BoundAsync(f,"A")==null,"A unique by-id move did not rebind the file");
        Settled(f,"Unique by-id move");
    }

    /// <summary>T6: a named rename moves its Dropbox copy whichever bound name is read first.</summary>
    public static async Task NamedRenameControlAsync()
    {
        foreach(var bFirst in new[]{true,false})
        {
            var f=New(); var what=$"Named rename ({(bFirst?"new name":"other name")} read first)";
            var dbA=Cloud("A","D"); var dbC=Cloud("C","dC"); await BindAsync(f,Pc("A"),dbA); var bindC=await BindAsync(f,Pc("C"),dbC);
            var b=Pc("B"); var c=Pc("C");
            if(bFirst) { f.Pc.Seed(b,[1]); f.Pc.Seed(c,[1]); } else { f.Pc.Seed(c,[1]); f.Pc.Seed(b,[1]); }
            f.Db.Seed(dbA,[1]); f.Db.Seed(dbC,[1]);
            var queue=await ItemChecks.StartQueueAsync(f.Pair,f.Paths,f.Pc,f.Db,f.Store);
            var moves=f.Db.Requests[nameof(ISide.MoveAsync)];
            var hops=new List<(string Id,string Path)>(); f.Db.AfterMove=(entry,path)=>hops.Add((entry.Id,path));
            var events=new List<RunEvent>();
            await PageAsync(f,queue,e=>{ lock(events) events.Add(e); },new Change(Side.Local,ChangeKind.Renamed,"B","A",Id,b,false,false,1,JournalId:1),what);
            Check.Require(f.Db.Requests[nameof(ISide.MoveAsync)]-moves==1 && hops.SequenceEqual([("D","B")]) && !f.Db.Items.ContainsKey("A"),$"{what}: the Dropbox copy did not move once from the old name");
            Check.Require(await BoundAsync(f,"B") is {DropboxPath:"B",DropboxId:"D"} && await BoundAsync(f,"C")==bindC && f.Db.Items["C"].Entry==dbC,$"{what}: wrong bindings or the other name changed");
            Check.Require(events.Count(RetryWait)<=(bFirst?0:1),$"{what}: more retry waits than the other-name-first replay allows");
            Settled(f,what);
        }
    }

    /// <summary>All six by-id alias cases.</summary>
    public static async Task RunAsync()
    {
        await RetainedAliasesAsync(); await CaseRenameWithAliasAsync(); await CaseRenameEditedAsync();
        await DivergentCapitalsAsync(); await UniqueMoveControlAsync(); await NamedRenameControlAsync();
    }
}
