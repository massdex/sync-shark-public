using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
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
        PageAsync(f,queue,events,new ChangePage([change],null,new(1,change.Usn!.Value+1)),label);
    private static Task PageAsync(Fixture f,WorkQueue queue,Action<RunEvent> events,ChangePage page,string label) =>
        Run.AutoSyncChecks.WithinDeadline(ct=>global::Massdex.SyncShark.Engine.Run.AutoSync.ApplyPageAsync(f.Pair,ItemChecks.Settings,events,f.Paths,queue,Side.Local,
            page,f.Pc,f.Db,f.Store,ct),label);
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
        // SELECTOR T2: "ordered hop destinations/ids plus CaseRename:29's source/destination event"
        var moved=events.Where(e=>e is {Kind:RunEventKind.Effect,What:nameof(ISide.MoveAsync),Side:Side.Dropbox}).Select(e=>(e.Detail,e.Path,e.Outcome)).ToList();
        Check.Require(moved.SequenceEqual([("file",temporary,nameof(RemovalOutcome.Published)),(temporary,"File",nameof(RemovalOutcome.Published))]),$"{what}: the case hops did not report each source and destination in order");
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
            // SELECTOR T6: "run 1 seeds B before C (no post-commit Retain), run 2 seeds C first with exactly one allowed WaitItemRetry"
            Check.Require(events.Count(RetryWait)==(bFirst?0:1),$"{what}: not the exact retry-wait count of its read order");
            Settled(f,what);
        }
    }

    private sealed record SecondName(Entry Draft,Entry Kept,Entry DbEdited,Entry DbCopy,Entry DbTemp,
        SyncedEntry DraftBinding,SyncedEntry LabelBinding,SyncedEntry TempBinding);

    private static Entry PcEntry(string path,byte value,string id) => ItemChecks.File(Side.Local,path,value,id);
    private static Entry DbEntry(string path,byte value,string id) => ItemChecks.File(Side.Dropbox,path,value,id);

    // X is bound at DRAFT and at its former name DRAFT.tmp; D is the uploaded temp at the old name.
    private static async Task<SecondName> SecondNameAsync(Fixture f)
    {
        var draft=PcEntry("DRAFT",1,"X"); var kept=PcEntry("DRAFT (Dropbox copy)",2,"L"); var temp=PcEntry("DRAFT.tmp",1,"X");
        var dbEdited=DbEntry("DRAFT",2,"A"); var dbCopy=DbEntry("DRAFT (PC copy)",1,"BX"); var dbTemp=DbEntry("DRAFT.tmp",1,"D");
        f.Pc.Seed(draft,[1]); f.Pc.Seed(kept,[2]); f.Pc.LinkNames["X"]=["DRAFT"];
        f.Db.Seed(dbEdited,[2]); f.Db.Seed(dbCopy,[1]);
        var draftBinding=await BindAsync(f,draft,dbCopy);
        var labelBinding=await BindAsync(f,kept,dbEdited);
        var tempBinding=await BindAsync(f,temp,dbTemp);
        return new(draft,kept,dbEdited,dbCopy,dbTemp,draftBinding,labelBinding,tempBinding);
    }

    // The two kept bindings and both endpoints are byte-unchanged by a stale-temp decision.
    private static void KeptUnchanged(Fixture f,SecondName s,string what)
    {
        Check.Require(f.Db.Items["DRAFT"].Entry==s.DbEdited && f.Db.Items["DRAFT"].Bytes.SequenceEqual(new byte[]{2}),$"{what}: the edited Dropbox DRAFT changed");
        Check.Require(f.Db.Items["DRAFT (PC copy)"].Entry==s.DbCopy && f.Db.Items["DRAFT (PC copy)"].Bytes.SequenceEqual(new byte[]{1}),$"{what}: the Dropbox PC copy changed");
        Check.Require(f.Pc.Items["DRAFT"].Entry==s.Draft && f.Pc.Items["DRAFT"].Bytes.SequenceEqual(new byte[]{1}),$"{what}: the PC DRAFT changed");
        Check.Require(f.Pc.Items["DRAFT (Dropbox copy)"].Entry==s.Kept && f.Pc.Items["DRAFT (Dropbox copy)"].Bytes.SequenceEqual(new byte[]{2}),$"{what}: the kept Dropbox label changed");
    }

    // C1/C2: one stale-temp page settles on the one delete, the kept bindings and unchanged endpoints.
    private static async Task SecondNamePageAsync(string what,Func<SecondName,ChangePage> page)
    {
        var f=New(); var s=await SecondNameAsync(f);
        f.Db.Seed(s.DbTemp,[1]);
        var queue=await ItemChecks.StartQueueAsync(f.Pair,f.Paths,f.Pc,f.Db,f.Store);
        var moves=f.Db.Requests[nameof(ISide.MoveAsync)];
        await PageAsync(f,queue,_=>{},page(s),what);
        Check.Require(f.Db.Requests[nameof(ISide.MoveAsync)]==moves && f.Db.Requests[nameof(ISide.DeleteAsync)]==1,$"{what}: not exactly the one old-copy delete with no move");
        Check.Require(f.Db.History.Any(h=>h.Entry.Id=="D" && h.Entry.Path=="DRAFT.tmp") && await BoundAsync(f,"DRAFT.tmp")==null,$"{what}: the old Dropbox copy did not go to history or the temp binding stayed");
        Check.Require(await BoundAsync(f,"DRAFT")==s.DraftBinding && await BoundAsync(f,"DRAFT (Dropbox copy)")==s.LabelBinding,$"{what}: a kept binding changed");
        KeptUnchanged(f,s,what);
        Check.Require(f.Store.OpenNotices.Count==0,$"{what}: an open notice was left");
    }

    /// <summary>U1: a kept-both and upload race leave the temp binding naming a PC file now bound at its destination; the stale name settles, deleting the unchanged old Dropbox copy or copying back the edited one.</summary>
    public static async Task SecondNameMoveAsync()
    {
        // C1 direct route: the PC rename of the old temp name is the page input.
        await SecondNamePageAsync("Second-name move (direct)",s=>new ChangePage(
            [new Change(Side.Local,ChangeKind.Renamed,"DRAFT","DRAFT.tmp","X",s.Draft,false,false,2,JournalId:1)],null,new(1,3)));
        // C2 borrowed route: the changed old temp name borrows the rename still held on the same page.
        await SecondNamePageAsync("Second-name move (borrowed)",s=>new ChangePage(
            [new Change(Side.Local,ChangeKind.Changed,"DRAFT.tmp",null,"X",null,false,false,1,JournalId:1),
             new Change(Side.Local,ChangeKind.Renamed,"DRAFT","DRAFT.tmp","X",s.Draft,false,false,2,JournalId:1)],null,new(1,3)));
        // C3 edited temp: the old Dropbox copy changed, so its bytes copy back to the old PC name with the edit notice.
        {
            var f=New(); var s=await SecondNameAsync(f);
            f.Db.Seed(s.DbTemp,[1]);
            var editedTemp=DbEntry("DRAFT.tmp",3,"D"); f.Db.Items["DRAFT.tmp"]=(editedTemp,new byte[]{3});
            var queue=await ItemChecks.StartQueueAsync(f.Pair,f.Paths,f.Pc,f.Db,f.Store);
            var (deletes,moves)=(f.Db.Requests[nameof(ISide.DeleteAsync)],f.Db.Requests[nameof(ISide.MoveAsync)]);
            await PageAsync(f,queue,_=>{},new Change(Side.Local,ChangeKind.Renamed,"DRAFT","DRAFT.tmp","X",s.Draft,false,false,2,JournalId:1),"Edited temp download");
            var bound=await BoundAsync(f,"DRAFT.tmp");
            Check.Require(f.Pc.Items.TryGetValue("DRAFT.tmp",out var got) && got.Bytes.SequenceEqual(new byte[]{3}) && bound is {DropboxId:"D",LocalPath:"DRAFT.tmp"} && bound.LocalId==got.Entry.Id && bound.LocalId!=s.Draft.Id && bound.LocalId!=s.TempBinding.LocalId,"Edited temp download: the old name does not hold the edited bytes on a fresh binding to Dropbox id D");
            Check.Require(f.Db.Items["DRAFT.tmp"].Entry==editedTemp && f.Db.Items["DRAFT.tmp"].Bytes.SequenceEqual(new byte[]{3}),"Edited temp download: the Dropbox temp edition changed");
            Check.Require(f.Db.Requests[nameof(ISide.DeleteAsync)]==deletes && f.Db.Requests[nameof(ISide.MoveAsync)]==moves,"Edited temp download: a delete or move ran");
            Check.Require(f.Store.OpenNotices.Count==1 && f.Store.OpenNotices.Single() is {Kind:NoticeKind.Info,Cause:TextKey.InfoEditBeatsDelete,Path:"DRAFT.tmp"},"Edited temp download: the open notices are not exactly the one expected Info");
        }
        // C4a grouped baseline: a real PC + Dropbox group still splits as moved twice.
        {
            var f=New(); await SecondNameAsync(f);
            var dbC=DbEntry("C",1,"D"); f.Db.Seed(dbC,[1]);
            var group=(await Retries.ObserveMoveAsync(f.Pair,f.Paths,new IssueSubject.UnknownMove("D","DRAFT.tmp","C",null,false),f.Pc,f.Db,f.Store,default))?.Group;
            Check.Require(group is {Count:2} && group![0].Side==Side.Local && group![1].Side==Side.Dropbox,"Grouped move: the public observation did not return both members");
            var plan=await MoveDecision.DecideMoveAsync(f.Pair,ItemChecks.Settings,f.Paths,group!,f.Pc,f.Db,f.Store,false,default);
            Check.Require(plan.Kind==ActionKind.ForgetOnly && plan.InfoAfter==TextKey.InfoMovedTwice,"Grouped move: not the baseline moved-twice split");
            Check.Require(f.Db.Items["C"].Entry==dbC && f.Db.Items["C"].Bytes.SequenceEqual(new byte[]{1}),"Grouped move: the planner wrote to Dropbox");
        }
        // C4b ungrouped baseline: the stale name alone plans moved-deleted.
        {
            var f=New(); var s=await SecondNameAsync(f);
            var dbC=DbEntry("C",1,"D"); f.Db.Seed(dbC,[1]);
            var plan=await MoveDecision.DecideMoveAsync(f.Pair,ItemChecks.Settings,f.Paths,[new RenameCandidate(Side.Local,s.Draft,s.TempBinding)],f.Pc,f.Db,f.Store,false,default);
            Check.Require(plan.Kind==ActionKind.ForgetOnly && plan.InfoAfter==TextKey.InfoMovedDeleted,"Ungrouped move: not the baseline moved-deleted split");
            Check.Require(f.Db.Items["C"].Entry==dbC && f.Db.Items["C"].Bytes.SequenceEqual(new byte[]{1}),"Ungrouped move: the planner wrote to Dropbox");
        }
        // C4c case intermediate baseline: the saved file mid-hop still plans the ordinary move to DRAFT.
        {
            var f=New(); await SecondNameAsync(f);
            var intermediate=PathNames.CaseRenamePath("DRAFT.tmp",ItemChecks.Settings.CaseRenameSuffix);
            var atHop=DbEntry(intermediate,1,"D"); f.Db.Seed(atHop,[1]);
            var group=(await Retries.ObserveMoveAsync(f.Pair,f.Paths,new IssueSubject.UnknownMove("D","DRAFT.tmp","DRAFT.tmp",intermediate,false),f.Pc,f.Db,f.Store,default))?.Group;
            Check.Require(group is {Count:2} && group![0].Side==Side.Local && group![1].Side==Side.Dropbox,"Case intermediate: the public observation did not return both members");
            var plan=await MoveDecision.DecideMoveAsync(f.Pair,ItemChecks.Settings,f.Paths,group!,f.Pc,f.Db,f.Store,false,default);
            Check.Require(plan.Kind==ActionKind.MoveDropbox && plan.Side==Side.Dropbox && plan.Target=="DRAFT","Case intermediate: not the baseline move to DRAFT");
            Check.Require(f.Db.Items.TryGetValue(intermediate,out var keptHop) && keptHop.Entry==atHop && keptHop.Bytes.SequenceEqual(new byte[]{1}),"Case intermediate: the planner wrote to Dropbox");
        }
        // C4d pure planner: the removed null-group conjunct would delete the untouched moved Dropbox copy instead of splitting.
        {
            var f=New(); var s=await SecondNameAsync(f);
            var plan=Moves.For([new RenameCandidate(Side.Local,s.Draft,s.TempBinding),new RenameCandidate(Side.Dropbox,DbEntry("C",1,"D"),s.TempBinding)],null,
                new Moves.Observations(null,s.DbTemp,false,false,false,false,null,"DRAFT","DRAFT",
                    new Moves.DestinationFacts(s.DraftBinding,Side.Local,Moves.DestinationIdentity.Here)),false);
            Check.Require(plan.Kind==ActionKind.ForgetOnly && plan.InfoAfter==TextKey.InfoMovedTwice,"Stale grouped read: not the baseline moved-twice split");
        }
    }

    /// <summary>All seven by-id alias cases.</summary>
    public static async Task RunAsync()
    {
        await RetainedAliasesAsync(); await CaseRenameWithAliasAsync(); await CaseRenameEditedAsync();
        await DivergentCapitalsAsync(); await UniqueMoveControlAsync(); await NamedRenameControlAsync(); await SecondNameMoveAsync();
    }
}
