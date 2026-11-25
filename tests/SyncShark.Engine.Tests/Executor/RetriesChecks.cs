using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Retry takes the last held revision; an occupied destination retains the notice and input.</summary>
public static class RetriesChecks
{
    public static async Task RetryCurrentSpellingAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed); using var store=new InMemoryStateStore();
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        var old=ItemChecks.File(Side.Local,"Doc.txt",1,"file"); var peer=ItemChecks.File(Side.Dropbox,old.Path,1,"peer");
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,default);
        pc.Seed(ItemChecks.File(Side.Local,"doc.txt",2,old.Id),[2]); db.Seed(peer,[1]);
        await Retry(pair,paths,pc,db,store,old,TextKey.FailLocalInUse,[new(Side.Local,old.Path,old.Id)]);
        Check.Require(store.OpenNotices.Count==0 && db.Items["doc.txt"].Bytes.SequenceEqual(new byte[]{2}),"Case-only rename turned the retry stale");
    }

    public static async Task RetryCarryingFolderAsync()
    {
        var pair=ItemChecks.Pair(); var inner=pair with {PairId="inner",LocalRoot=Path.Combine(pair.LocalRoot,"G/inner"),DropboxRoot=pair.DropboxRoot+"/G/inner"};
        var paths=new PathScope(pair,[pair,inner],DriveType.Fixed); using var store=new InMemoryStateStore(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        var old=ItemChecks.File(Side.Local,"F",0,"left",true); var peer=ItemChecks.File(Side.Dropbox,"F",0,"right",true); var moved=old with {Path="G"};
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,default); pc.Seed(moved,[]); db.Seed(peer,[]);
        NoticeItem[] members=[new(Side.Local,moved.Path,moved.Id),new(Side.Dropbox,peer.Path,peer.Id)]; var reads=new List<(string,string)>();
        await Retry(pair,paths,pc,db,store,moved,TextKey.FailCarriesOtherPair,members,Recording(pc,reads));
        Check.Require(NameFirst(reads,members[0]),"Retry looked up a present member by ID before its reviewed name");
        Check.Require(store.OpenNotices.Count(n=>n.Cause==TextKey.FailCarriesOtherPair && n.Subject.HasSameItems(new(members,Side.Local)))==1,"The refused Retry did not keep the exact reviewed subject");
        Check.Require(db.Items.TryGetValue("F",out var still) && still.Entry==peer && !db.Items.ContainsKey("G") && db.Requests[nameof(ISide.CreateFolderExclusiveAsync)]==0 && db.Requests[nameof(ISide.EnsureFolderAsync)]==0 && store.OpenNotices.Any(n=>n.Cause==TextKey.FailCarriesOtherPair && n.At!=DateTimeOffset.UnixEpoch),"Retry copied a folder carrying another pair");
    }

    // The ordered member reads one side receives: ReadOneAsync by path, ReadByIdAsync by id.
    private static ISide Recording(FakeSide side,List<(string Method,string Arg)> reads)=>Run.AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>
    {
        if(m.Name is nameof(ISide.ReadOneAsync) or nameof(ISide.ReadByIdAsync)) lock(reads) reads.Add((m.Name,(string)args[0]!));
        return Run.AutoSyncChecks.Observation.Forward(side,m,args);
    });
    // A present member is selected at its exact reviewed name; an ID lookup may only follow that name read.
    private static bool NameFirst(List<(string Method,string Arg)> reads,NoticeItem member)
    {
        lock(reads) return reads.FirstOrDefault(r=>r==(nameof(ISide.ReadOneAsync),member.Path) || r==(nameof(ISide.ReadByIdAsync),member.Id))==(nameof(ISide.ReadOneAsync),member.Path);
    }
    private static async Task Retry(PairConfig pair,PathScope paths,FakeSide pc,FakeSide db,InMemoryStateStore store,Entry subject,TextKey cause,NoticeItem[] items,ISide? local=null)
    {
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,cause,subject.Path,new(items,Side.Local),DateTimeOffset.UnixEpoch,false),default);
        var failure=store.OpenNotices.Single(); var owner=new Unfinished();
        await owner.CheckAsync(pair,ItemChecks.Settings,_=>{},paths,null,local??pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        await new Retries(owner).RetryAsync(pair,ItemChecks.Settings,_=>{},paths,failure,local??pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
    }

    /// <summary>An unchecked owner is never evidence that a manual Retry can resend a cloud effect.</summary>
    public static async Task OpenJobAsync()
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local);var cloud=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var remote=ItemChecks.File(Side.Dropbox,"folder/child",1,"cloud-child");cloud.Seed(remote,[1]);
        var notice=new Notice(0,NoticeKind.Failure,TextKey.FailChanged,remote.Path,new([new(Side.Dropbox,remote.Path,remote.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false);
        await store.SaveNoticeAsync(pair.PairId,notice,default);notice=store.OpenNotices.Single();
        var job=new AcceptedJob(pair.PairId,"delete","pending",[new(null,"folder",null)]);
        await store.SaveJobAsync(job,default);
        try { await new Retries().RetryAsync(pair,ItemChecks.Settings,_=>{},paths,notice,pc,cloud,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default); }
        catch(InvalidOperationException) { } catch(NotImplementedException) { }
        Check.Require(cloud.Requests[nameof(ISide.ReadByIdAsync)]==0 && store.OpenNotices.Single().Matches(notice),"R3-6 ordinary retry entered item execution without its pair job owner");
        var owner=new Unfinished();cloud.JobReplies[job.JobId]=new Queue<JobReply>([new JobReply.Running()]);
        await owner.CheckAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Changed,remote.Path,null,remote.Id,null,false,false),pc,cloud,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        await new Retries(owner).RetryAsync(pair,ItemChecks.Settings,_=>{},paths,notice,pc,cloud,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(cloud.Requests[nameof(ISide.ReadJobAsync)]==1 && cloud.Requests[nameof(ISide.ReadByIdAsync)]==0 && cloud.Mutations.Count==0 && pc.Mutations.Count==0 && store.OpenNotices.Single().Matches(notice),"Pending accepted folder job fell through to ordinary child retry");
        var free=ItemChecks.File(Side.Dropbox,"separate",2,"other-id");cloud.Seed(free,[2]);
        await store.SaveNoticeAsync(pair.PairId,notice with {Path=free.Path,Subject=new([new(Side.Dropbox,free.Path,free.Id)],Side.Dropbox)},default);
        var other=store.OpenNotices.Single(n=>n.Path==free.Path);
        await new Retries(owner).RetryAsync(pair,ItemChecks.Settings,_=>{},paths,other,pc,cloud,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(pc.Items.TryGetValue(free.Path,out var copied) && copied.Bytes.SequenceEqual(new byte[]{2}) && store.OpenNotices.All(n=>n.Id!=other.Id) && store.OpenNotices.Any(n=>n.Matches(notice)),"Running job suppressed disjoint ordinary Retry");
    }

    internal static async Task MoveObservationAsync(List<string> defects)
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var a=ItemChecks.File(Side.Local,"old",1,"pc");var b=ItemChecks.File(Side.Dropbox,"old",1,"db");var binding=SyncedEntries.From(a,b);var moved=b with {Path="new"};
        await store.CommitAsync(pair.PairId,null,binding,null,null,default);db.Seed(moved,[1]);var move=new IssueSubject.UnknownMove(b.Id,b.Path,moved.Path,null,false);
        var observation=await Retries.ObserveMoveAsync(pair,paths,move,pc,db,store,default);
        if(observation is not {} input || input.Row.Synced!=binding || input.Row.Local!=null || input.Row.Dropbox!=moved || !input.Group.SequenceEqual(new[]{new RenameCandidate(Side.Dropbox,moved,binding)}))
            defects.Add("Definitive missing peer must give the planner the original binding, absent Local and surviving Dropbox move");
        db.Items.Clear();var absent=await Retries.ObserveMoveAsync(pair,paths,move,pc,db,store,default);
        if(absent is not {} ordinary || ordinary.Row.Synced!=binding || ordinary.Row.Local!=null || ordinary.Row.Dropbox!=null || ordinary.Group.Count!=0)
            defects.Add("Definitive absence on both sides must give ordinary Decide the bound row and no move candidates");
    }

    public static async Task MovedDeletedRetryAsync()
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var a=ItemChecks.File(Side.Local,"old",1,"pc");var b=ItemChecks.File(Side.Dropbox,"old",1,"db");var moved=b with {Path="new"};db.Seed(moved,[1]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,b),null,null,default);
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailDropboxOutcomeUnknown,"new",new([new(Side.Dropbox,"old",b.Id)],Side.Dropbox){Move=new(b.Id,"old","new",null,false)},DateTimeOffset.UtcNow,false),default);var notice=store.OpenNotices.Single();var owner=new Unfinished();
        await owner.CheckAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Changed,"new",null,b.Id,moved,false,false),pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        await new Retries(owner).RetryAsync(pair,ItemChecks.Settings,_=>{},paths,notice,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(db.Items["new"].Bytes.SequenceEqual(new byte[]{1}) && db.Mutations.Count==0 && store.OpenNotices.Any(n=>n.Cause==TextKey.InfoMovedDeleted) && store.OpenNotices.All(n=>n.Kind!=NoticeKind.Failure),"R4 definitive move with deleted PC peer was rewritten as stale instead of decided");
        // An unresolved intermediate retains the old binding by identity, not by its transient path.
        var aa=ItemChecks.File(Side.Local,"reuse",1,"gone");var bb=ItemChecks.File(Side.Dropbox,"reuse",1,"bound");var successor=ItemChecks.File(Side.Local,"reuse",2,"replacement");pc.Seed(successor,[2]);db.Seed(bb,[1]);
        var binding=SyncedEntries.From(aa,bb);await store.CommitAsync(pair.PairId,null,binding,null,null,default);
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailDropboxOutcomeUnknown,"goal",new([new(Side.Dropbox,"reuse",bb.Id)],Side.Dropbox){Move=new(bb.Id,"hop","goal","hop",false)},DateTimeOffset.UtcNow,false),default);
        var original=store.OpenNotices.Single(n=>n.Path=="goal");
        try {await new Retries(owner).RetryAsync(pair,ItemChecks.Settings,_=>{},paths,original,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);} catch(ChangedUnderneathException) { }
        Check.Require(!store.OpenNotices.Any(n=>n.Id==original.Id) && !db.Items.ContainsKey(bb.Path) && !store.Records.Values.Contains(binding) && pc.Items[aa.Path].Entry.Id==successor.Id,"Unknown-move Retry must retire the selected old binding without deleting the occupant of its reused name");
        foreach(var physical in new[]{"source","waypoint","landed","outside","absent","unreadable"}) await CheckPosition(physical);
        async Task CheckPosition(string physical)
        {
            using var ledger=new InMemoryStateStore();var local=new FakeSide(Side.Local);var cloud=new FakeSide(Side.Dropbox);
            var original=ItemChecks.File(Side.Local,"origin",1,"p");var old=ItemChecks.File(Side.Dropbox,"origin",1,"d");var saved=SyncedEntries.From(original,old);
            var goal="ORIGIN";var hop=PathNames.CaseRenamePath(goal,ItemChecks.Settings.CaseRenameSuffix);var current=original with {Path=goal};
            if(physical=="outside") local.History.Add((original,[1]));else if(physical!="absent") local.Seed(current,[1]);
            var at=old with {Path=physical=="waypoint"?hop:physical=="landed"?goal:old.Path};
            if(physical=="unreadable") at=at with {Skip=nameof(TextKey.SkipUnreadable)};
            cloud.Seed(at,[1]);await ledger.CommitAsync(pair.PairId,null,saved,null,null,default);
            await ledger.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailDropboxOutcomeUnknown,goal,new([new(Side.Dropbox,old.Path,old.Id)],Side.Dropbox){Move=new(old.Id,old.Path,goal,hop,false)},DateTimeOffset.UnixEpoch,false),default);
            var notice=ledger.OpenNotices.Single();var q=await ItemChecks.StartQueueAsync(pair,paths,local,cloud,ledger);
            try {await new Retries(q.Unfinished).RetryAsync(pair,ItemChecks.Settings,_=>{},paths,notice,local,cloud,ledger,q.Held,ItemChecks.Population(pair,paths,local,cloud,ledger),ItemChecks.Hop(pair,paths,q,local,cloud,ledger),default,q.CoversHop);} catch(ItemFailedException) when(physical=="unreadable") { }
            if(physical=="unreadable") Check.Require(ledger.OpenNotices.SingleOrDefault()?.Matches(notice)==true && ledger.Records.Values.Contains(saved) && cloud.Mutations.Count==0,"Unreadable move evidence must leave the exact failure and binding available for Retry");
            else if(physical is "outside" or "absent") Check.Require(ledger.OpenNotices.All(n=>n.Id!=notice.Id) && ledger.Records.Count==0 && !cloud.Items.ContainsKey(old.Path) && cloud.History.Count(x=>x.Entry.Id==old.Id)==1,"An absent or outside selected peer must settle the original deletion");
            else Check.Require(ledger.OpenNotices.All(n=>n.Id!=notice.Id) && ledger.Records.Values.SingleOrDefault() is {} link && link.LocalPath==goal && link.DropboxPath==goal && link.LocalId==original.Id && link.DropboxId==old.Id && cloud.Items.TryGetValue(goal,out var result) && result.Entry.Path==goal && result.Entry.Id==old.Id && cloud.Requests[nameof(ISide.MoveAsync)]==(physical=="source"?2:physical=="waypoint"?1:0),"Retry must finish only the remaining physical move legs and bind the two exact identities at the goal");
        }
    }

    /// <summary>A retained first-run deletion debt re-runs the ordinary decision; the deletion already consumed at that
    /// name is never applied again, so the restored copy is kept and uploaded.</summary>
    public static async Task RetainedDeletedNameDebtAsync()
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local);var cloud=new FakeSide(Side.Dropbox){ClockOffset=(TimeSpan.Zero,TimeSpan.Zero)};
        using var store=new InMemoryStateStore();const string path="debt";
        var entry=ItemChecks.File(Side.Local,path,1,"pc-debt");
        pc.Seed(entry,[1]);
        var deletion=new DeletionEvidence.DeletedFile(path,"cloud","prior-id","r1",entry.Modified.AddDays(1),null,entry.ContentHash,null);
        cloud.DeletedHistory[path]=deletion;
        await store.CommitAsync(pair.PairId,null,null,null,PathNames.DropboxKey(path),default,deletion.DeletedAt);
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.ReadLocalFailed,path,
            new([new(Side.Local,path,entry.Id)],Side.Local,IssueSubject.FirstRunDeletionKind.Item),DateTimeOffset.UnixEpoch,false),default);
        var debt=store.OpenNotices.Single(n=>n.Path==path && n.Kind==NoticeKind.Failure);
        var owner=new Unfinished();
        await owner.CheckAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Changed,path,null,null,null,false,false),pc,cloud,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        var before=cloud.Requests[nameof(ISide.DeletedAsync)];
        await new Retries(owner).RetryAsync(pair,ItemChecks.Settings,_=>{},paths,debt,pc,cloud,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:_=>true);
        Check.Require(cloud.Requests[nameof(ISide.DeletedAsync)]>before,"The retained deletion debt was settled without probing the live deletion evidence");
        Check.Require(pc.Items.TryGetValue(path,out var kept) && kept.Bytes.SequenceEqual(new byte[]{1}) &&
            cloud.Items.TryGetValue(path,out var synced) && synced.Bytes.SequenceEqual(new byte[]{1}),
            "The retained deletion debt recycled the restored copy or never uploaded it");
    }

    public static async Task RunAsync()
    {
        var pair = ItemChecks.Pair(); var paths = new PathScope(pair, [pair], DriveType.Fixed);
        var pc = new FakeSide(Side.Local); var cloud = new FakeSide(Side.Dropbox);
        using var store = new InMemoryStateStore(); const string path = "parent/item";
        var first = ItemChecks.File(Side.Dropbox, path, 1, "child");
        var last = ItemChecks.File(Side.Dropbox, path, 2, first.Id); cloud.Seed(last, [2]);
        cloud.Recoverable[(last.Id,last.Revision,last.ContentHash)]=new Recovery.File(last,new MemoryStream([2]));
        var notice = new Notice(1, NoticeKind.PutBack, TextKey.FailLocalInUse, path,
            new([new(Side.Dropbox, path, last.Id)], Side.Dropbox), DateTimeOffset.UnixEpoch, false);
        await store.SaveNoticeAsync(pair.PairId, notice, default);
        var consumed = new List<string>();
        bool Published()=>store.Records.Values.Any(link=>link.DropboxId==last.Id && link.DropboxContentHash==last.ContentHash && pc.Items.TryGetValue(link.LocalPath,out var copy) && copy.Entry.Id==link.LocalId && copy.Bytes.SequenceEqual(new byte[]{2}));
        Change Held(Entry e) => new Change(Side.Dropbox, ChangeKind.Changed,path,null,e.Id,e,false,false)
            { ConsumeHeld = () => { Check.Require(Published(),"MF6 settled held version before publishing final bytes"); consumed.Add(e.Revision); } };
        Change[] input = [Held(first), Held(last)];
        var defects=new List<string>();
        try { input[0].ConsumeHeld!(); defects.Add("MF6 ordering assertion accepted acknowledgment before publication"); } catch(InvalidDataException) { }
        consumed.Clear();
        var debt=new Notice(2,NoticeKind.Failure,TextKey.ReadLocalFailed,"debt",new([new(Side.Local,"debt","pc-debt")],Side.Local,IssueSubject.FirstRunDeletionKind.Item),DateTimeOffset.UnixEpoch,false);
        await store.SaveNoticeAsync(pair.PairId,debt,default); pc.Seed(ItemChecks.File(Side.Local,"debt",1,"pc-debt"),[1]);
        var pairOwner=new Unfinished();
        await pairOwner.CheckAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Changed,"debt",null,null,null,false,false),pc,cloud,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        var reads=new List<(string,string)>(); var recorded=Recording(pc,reads);
        Task Ordinary(Retries retries,Notice n)=>retries.RetryAsync(pair,ItemChecks.Settings,_=>{},paths,n,recorded,cloud,store,_=>[],(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        bool plannerStub=false;
        try { await Ordinary(new Retries(pairOwner),debt); } catch(NotImplementedException) { plannerStub=true; } catch(ItemFailedException) { }
        if((!plannerStub && (!cloud.Items.TryGetValue("debt",out var syncedDebt) || !syncedDebt.Bytes.SequenceEqual(new byte[]{1}) || store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure && n.Path==debt.Path))) || cloud.Requests[nameof(ISide.DeletedAsync)]==0) defects.Add("MF5 reviewed historical retry neither probed history nor settled debt");
        var renamed=debt with {Path="caption",Subject=new([new(Side.Local,"renamed","moved-id")],Side.Local)};
        await store.SaveNoticeAsync(pair.PairId,renamed,default);renamed=store.OpenNotices.Single(n=>n.Path=="caption");
        pc.ReadFailures["caption"]=new InvalidDataException("unrelated replacement"); pc.Seed(ItemChecks.File(Side.Local,"renamed",1,"moved-id"),[1]);
        lock(reads) reads.Clear();
        try { await Ordinary(new Retries(pairOwner),renamed); } catch(InvalidDataException){defects.Add("R2-8 ordinary Retry followed caption instead of reviewed subject");} catch(NotImplementedException) { }
        if(!NameFirst(reads,renamed.Subject.Items[0])) defects.Add("R2-8 did not select the reviewed member at its own name before any ID lookup");
        pc.ReadFailures.Remove("caption"); await store.CloseNoticeAsync(pair.PairId,renamed,default);
        var unknown=renamed with {Path="source",Subject=new([new(Side.Dropbox,"source","cloud-move")],Side.Dropbox){Move=new("cloud-move","source","destination",null,false)}};
        await store.SaveNoticeAsync(pair.PairId,unknown,default);unknown=store.OpenNotices.Single(n=>n.Path=="source");
        var movePc=ItemChecks.File(Side.Local,"source",1,"pc-move");var moveDb=ItemChecks.File(Side.Dropbox,"source",1,"cloud-move");
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(movePc,moveDb),null,null,default);pc.Seed(movePc,[1]);cloud.Seed(moveDb,[1]);
        long beforeId=cloud.Requests[nameof(ISide.ReadByIdAsync)];
        try { await Ordinary(new Retries(pairOwner),unknown); } catch(NotImplementedException) { }
        if(cloud.Requests[nameof(ISide.ReadByIdAsync)]==beforeId) defects.Add("R3-6 unaccepted unknown move was not observed by id");
        Check.Require(defects.Count==0,string.Join("; ",defects));
        // Both checks use other groups. Run the custody check even when the notice owner is still a stub.
        NotImplementedException? missing=null;
        try { await ItemSyncChecks.UnreadableAsync(); } catch(NotImplementedException e) { missing=e; }
        Task Retry() => new Retries().RetryAsync(pair,ItemChecks.Settings,_=>{},paths,notice,pc,cloud,store,
            scope => scope == new Scope(path,path) ? input : [], (_,_,_,_)=>throw new InvalidOperationException(),
            (_,_,_,_)=>throw new InvalidOperationException(),default);
        pc.Seed(ItemChecks.File(Side.Local,path,3,"occupant"),[3]);
        // Refuse the conflict copy too: a successful keep-both legitimately publishes at a label.
        var label=global::Massdex.SyncShark.Engine.Planner.ConflictName.For(path,Side.Dropbox,last.Modified,ItemChecks.Settings.ComputerName,ItemChecks.Settings,1);
        pc.PublishFailures[label]=new ItemFailedException(label,TextKey.FailChanged,null);
        try { await Retry(); } catch (ItemFailedException) { } catch (PathOccupiedException) { }
        Check.Require(pc.Items[path].Bytes.SequenceEqual(new byte[]{3}) && consumed.Count==0 && store.OpenNotices.Count(n=>n.Kind==NoticeKind.PutBack)==1,"Occupied put-back consumed its evidence or replaced the occupant");
        notice=store.OpenNotices.Single(n=>n.Kind==NoticeKind.PutBack);
        pc.PublishFailures.Clear();
        await Retry();
        Check.Require(pc.Items[path].Bytes.SequenceEqual(new byte[]{3}) && Published() && consumed.SequenceEqual(new[]{first.Revision,last.Revision}) && store.OpenNotices.All(n=>n.Kind!=NoticeKind.PutBack),"Retry did not publish the last held revision before settling ordered input");
        if(missing!=null) throw missing;
        if(plannerStub) throw new NotImplementedException(nameof(ItemSync));
    }
}
