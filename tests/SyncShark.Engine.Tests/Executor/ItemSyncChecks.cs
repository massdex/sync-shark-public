using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>An exact Dropbox echo consumes no provider reads and no record writes.</summary>
public static class ItemSyncChecks
{
    public static async Task SupersededInputAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        using var store=new InMemoryStateStore(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,db,store);
        var replacement=ItemChecks.File(Side.Local,"doc",2,"replacement"); pc.Seed(replacement,[2]);
        var stale=new Change(Side.Local,ChangeKind.Changed,"doc",null,"temporary",null,true,false,1,JournalId:1);
        Task Page(string label,Side side,ChangePage page)=>Run.AutoSyncChecks.WithinDeadline(ct=>global::Massdex.SyncShark.Engine.Run.AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,side,page,pc,db,store,ct),label);
        await Page("Superseded local edit",Side.Local,new([stale,stale with {Id=replacement.Id,Observed=replacement,Usn=2}],null,new(1,3)));
        Check.Require(pc.Requests[nameof(ISide.SettleJournalAsync)]==2 && db.Items["doc"].Bytes.SequenceEqual(new byte[]{2}),"The successor did not settle both journal inputs");
        var saved=store.Records.Values.Single(); var later=ItemChecks.File(Side.Dropbox,"doc",3,"later"); db.Items["doc"]=(later,[3]);
        var obsolete=new Change(Side.Dropbox,ChangeKind.Changed,"doc",null,saved.DropboxId,null,false,false);
        await Run.AutoSyncChecks.WithinDeadline(async ct=>
        {
            // Overwriting the local file needs the projected journal, including the publication echo.
            var journal=await ItemChecks.ReadPrefixAsync(pc,ItemChecks.Journal([],3),ct);
            var feed=await ItemChecks.ReadPrefixAsync(db,new([obsolete,obsolete with {Id=later.Id,Observed=later}],"after",null),ct);
            ChangePage? echo=null;
            pc.AfterPublish=entry=>ItemChecks.Script(pc,echo=ItemChecks.Journal([new(Side.Local,ChangeKind.Changed,entry.Path,null,entry.Id,entry,true,false,3,JournalId:1)],4));
            bool Covered((JournalPosition? Local,string? Dropbox) fence)
            {
                if(echo==null) return fence==(journal.ProjectedThrough,feed.Cursor);
                var read=pc.ReadChangesAsync(null,journal.Position,(fence.Dropbox,fence.Local),ct).GetAwaiter().GetResult();
                return read.CaughtUp && !read.ContinuityLost && read.ProjectedThrough==read.Position && fence==(read.Position,feed.Cursor) && read.Changes.SequenceEqual(echo.Changes);
            }
            await queue.AdmitAsync(pair,journal,(_,_)=>throw new InvalidDataException("Unexpected journal input"),pc.SettleJournalAsync,ct);
            await queue.AdmitAsync(pair,feed,(input,token)=>ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,input,pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),token,queue.Held,(_,_)=>throw new global::Massdex.SyncShark.Engine.Run.WorkQueue.Unsettled(),coversThrough:Covered),null,(cursor,token)=>store.SaveCursorAsync(pair.PairId,cursor,token),null,ct);
            Check.Require(echo!=null && pc.Pages.Count==0,"Publication echo was not projected");
            pc.AfterPublish=null;
        },"Superseded Dropbox edit");
        Check.Require(await store.CursorAsync(pair.PairId,default)=="after" && store.Records.Values.Single().DropboxId==later.Id && pc.Items["doc"].Bytes.SequenceEqual(new byte[]{3}),"Obsolete edit blocked the successor or its cursor");
        var current=store.Records.Values.Single(); pc.Items.Clear();
        await Page("Superseded edit before bound deletion",Side.Local,new([stale with {Usn=4},new(Side.Local,ChangeKind.Deleted,"doc",null,current.LocalId,null,false,true,5,JournalId:1)],null,new(1,6)));
        Check.Require(store.Records.Count==0 && db.History.Last().Entry.Id==later.Id && store.OpenNotices.Count==0,"Stale identity at an empty name blocked the bound deletion");
        await Run.AutoSyncChecks.WithinDeadline(async ct=>
        {
            var delayed=ItemChecks.File(Side.Dropbox,"delayed",4,"delayed-id");
            var input=obsolete with {Path=delayed.Path,Id=delayed.Id,Observed=delayed};
            var pending=global::Massdex.SyncShark.Engine.Run.AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,Side.Dropbox,new([input],"visible",null),pc,db,store,ct);
            Check.Require(!pending.IsCompleted && await store.CursorAsync(pair.PairId,ct)=="after","Missing identity advanced the cursor");
            db.Seed(delayed,[4]);
            Check.Require(await queue.RetryHeldAsync(new(delayed.Path,delayed.Path),ct),"Missing identity was not retained");
            await pending;
            Check.Require(pc.Items[delayed.Path].Bytes.SequenceEqual(new byte[]{4}) && await store.CursorAsync(pair.PairId,ct)=="visible","Visible identity did not settle the retained input");
        },"Empty unbound Dropbox name");
        await Run.AutoSyncChecks.WithinDeadline(async ct=>
        {
            // A new Dropbox item that left before its first read: its own later tombstone, or a later ancestor tombstone in the next page, settles it.
            var arrival=new Change(Side.Dropbox,ChangeKind.Changed,"gone",null,"gone-id",ItemChecks.File(Side.Dropbox,"gone",5,"gone-id"),false,false);
            Task Page(ChangePage page)=>global::Massdex.SyncShark.Engine.Run.AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,Side.Dropbox,page,pc,db,store,ct);
            await Page(new([arrival,new(Side.Dropbox,ChangeKind.Deleted,"gone",null,null,null,false,false)],"departed",null));
            Check.Require(await store.CursorAsync(pair.PairId,ct)=="departed" && !pc.Items.ContainsKey("gone"),"A departed unbound arrival held the cursor");
            var child=arrival with {Path="moved/child",Id="child-id",Observed=ItemChecks.File(Side.Dropbox,"moved/child",6,"child-id")};
            var first=Page(new([child],"child",null));
            Check.Require(!first.IsCompleted,"An unbound arrival without departure evidence was not retained");
            var second=Page(new([new(Side.Dropbox,ChangeKind.Deleted,"moved",null,null,null,false,false)],"parent-gone",null));
            await queue.RetryHeldAsync(new("moved/child","moved/child"),ct);
            await first; await second;
            Check.Require(await store.CursorAsync(pair.PairId,ct)=="parent-gone" && !pc.Items.ContainsKey("moved/child"),"An ancestor tombstone did not settle the departed child");
        },"Departed unbound Dropbox arrival");
    }

    public static Task ReverseFolderKeepsChildAsync() => Run.AutoSyncChecks.WithinDeadline(async ct=>
    {
        foreach(bool childFirst in new[]{false,true})
        {
            var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed); using var store=new InMemoryStateStore();
            var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var queue=new global::Massdex.SyncShark.Engine.Run.WorkQueue();
            foreach(string path in new[]{"A","A/child"})
            {
                var left=ItemChecks.File(Side.Local,path,1,"local-"+path,path=="A"); var right=left with {Id="cloud-"+path,Revision="revision"};
                pc.Seed(left,left.IsFolder?[]:[1]); db.Seed(right,right.IsFolder?[]:[1]);
                await store.CommitAsync(pair.PairId,null,SyncedEntries.From(left,right),null,null,ct);
            }
            Task Page(ChangePage page)=>global::Massdex.SyncShark.Engine.Run.AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,Side.Local,page,pc,db,store,ct);
            await pc.MoveAsync(pc.Items["A"].Entry,"B",ct);
            var forward=new Change(Side.Local,ChangeKind.Renamed,"B","A","local-A",pc.Items["B"].Entry,false,false,1,JournalId:3);
            await Page(new([forward],null,new(3,2)));
            await pc.MoveAsync(pc.Items["B"].Entry,"A",ct);
            var back=forward with {OldPath="B",Path="A",Observed=pc.Items["A"].Entry,Usn=childFirst?3UL:2UL};
            var child=new Change(Side.Local,ChangeKind.Changed,"A/child",null,"local-A/child",pc.Items["A/child"].Entry,false,false,childFirst?2UL:3UL,JournalId:3);
            await Page(new(childFirst?[child,back]:[back,child],null,new(3,4)));
            Check.Require(!db.Items.ContainsKey("B") && db.Items["A/child"].Entry.Id=="cloud-A/child" && db.Requests[nameof(ISide.PublishAsync)]==0 && !db.History.Any(v=>v.Entry.Id=="cloud-A/child") && store.Records.Count==2 && store.OpenNotices.Count==0,"Reverse move replaced the child identity or its history");
        }
    });

    public static async Task UnboundTombstoneReadsAsync()
    {
        await FileAncestorAsync();
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        using var store=new InMemoryStateStore(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        Task Remove(string path)=>ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Deleted,path,null,null,null,false,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        var root=SyncedEntries.From(ItemChecks.File(Side.Local,"Old",0,"left",true),ItemChecks.File(Side.Dropbox,"Old",0,"right",true));
        await store.CommitAsync(pair.PairId,null,root,null,null,default);
        var names=Enumerable.Range(0,20).Select(i=>"Old/child"+i).ToArray();
        foreach(var name in names)
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(ItemChecks.File(Side.Local,name,1,"left-"+name),ItemChecks.File(Side.Dropbox,name,1,"right-"+name)),null,null,default);
        await store.ProjectSubtreeAsync(pair.PairId,new("Old","Old"),new("New","New"),default);
        await store.CommitAsync(pair.PairId,root,root with {LocalKey=PathNames.Key("New"),DropboxKey=PathNames.DropboxKey("New"),LocalPath="New",DropboxPath="New"},null,null,default);
        var bindings=store.Records.Values.ToHashSet();
        foreach(var path in names.Append("Old").Concat(Enumerable.Range(0,5).Select(i=>".git/removed"+i))) await Remove(path);
        Check.Require(pc.Requests[nameof(ISide.ReadOneAsync)]==0 && db.Requests[nameof(ISide.ReadOneAsync)]==0 && store.OpenNotices.Count==0 && bindings.SetEquals(store.Records.Values),"Unbound tombstones read providers or changed bindings or notices");
    }

    private static async Task FileAncestorAsync()
    {
        var home=Directory.CreateTempSubdirectory();
        try
        {
            var root=home.CreateSubdirectory("root"); using var store=new InMemoryStateStore();
            var side=Local.LocalSideChecks.BoundSide(root.FullName,store);
            File.WriteAllText(Path.Combine(root.FullName,"ordinary"),"keep");
            Check.Require(await side.ReadOneAsync("ordinary/c",default)==null,"An ordinary file ancestor must prove its child absent");
            var other=home.CreateSubdirectory("other"); Directory.CreateSymbolicLink(Path.Combine(root.FullName,"link"),other.FullName);
            try {await side.ReadOneAsync("link/c",default);throw new InvalidDataException("A link supplied absence");} catch(ChangedUnderneathException) { }
            Directory.Move(root.FullName,Path.Combine(home.FullName,"previous")); Directory.CreateDirectory(root.FullName);
            try {await side.ReadOneAsync("ordinary/c",default);throw new InvalidDataException("A replacement root supplied absence");} catch(ChangedUnderneathException) { }
        }
        finally {home.Delete(true);}
    }

    public static async Task UnboundParentSettlesChildAsync()
    {
        await ParentTombstoneChecks.RunAsync();
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        using var store=new InMemoryStateStore(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        var queue=new global::Massdex.SyncShark.Engine.Run.WorkQueue();
        var child=SyncedEntries.From(ItemChecks.File(Side.Local,"U/c",1,"local"),ItemChecks.File(Side.Dropbox,"U/c",1,"cloud"));
        await store.CommitAsync(pair.PairId,null,child,null,null,default);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Deleted,"U",null,null,null,false,false),pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,queue.Held);
        Check.Require(store.Records.Count==0 && pc.Mutations.Count==0 && db.Mutations.Count==0,"An unbound parent hid its bound descendant");
    }

    public static async Task MovedRootPreservesFileAsync()
    {
        var pair=ItemChecks.Pair(); pair=pair with {Roots=new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("account","root","home","original"))};
        var paths=new PathScope(pair,[pair],DriveType.Fixed); using var store=new InMemoryStateStore();
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox){Root=(null,new("account","root","home","replacement"),null,null)};
        var file=ItemChecks.File(Side.Local,"doc",1,"local"); pc.Seed(file,[1]);
        var peer=ItemChecks.File(Side.Dropbox,"doc",1,"cloud");
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(file,peer),null,null,default);
        var outside=Run.AutoSyncChecks.Observation.Wrap<ISide>((method,args)=>method.Name==nameof(ISide.ReadByIdAsync) && Equals(args[0],peer.Id)
            ? Task.FromResult<(IdentityLocation,bool)?>((new("root","elsewhere/doc",peer),false)) : Run.AutoSyncChecks.Observation.Forward(db,method,args));
        bool refused=false;
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,"doc",null,file.Id,null,false,false),pc,outside,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default); }
        catch(ChangedUnderneathException error) when(error.Path==pair.DropboxRoot) { refused=true; }
        Check.Require(refused && pc.Items["doc"].Entry==file && pc.History.Count==0,"A replacement cloud root authorized local deletion");
    }

    public static async Task FolderFailureReplaysComparisonAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed); using var store=new InMemoryStateStore();
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var queue=new global::Massdex.SyncShark.Engine.Run.WorkQueue();
        var left=ItemChecks.File(Side.Local,"target",0,"left",true); var right=ItemChecks.File(Side.Dropbox,"target",0,"right",true);
        pc.Seed(left,[]); db.Seed(right,[]); pc.Seed(ItemChecks.File(Side.Local,"target/child",2,"child"),[2]);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(left,right),null,null,default);
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,right.Path,new([new(Side.Dropbox,right.Path,right.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false),default);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Changed,right.Path,null,right.Id,right,false,false),pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default);
        Check.Require(db.Items.TryGetValue("target/child",out var child) && child.Bytes.SequenceEqual(new byte[]{2}) && store.Records.Values.Any(s=>s.LocalId=="child") && store.OpenNotices.Count==0,"Replaying the failed folder comparison did not sync its destination child");
    }

    public static Task BothGoneFolderForgetsChildrenAsync() => Run.AutoSyncChecks.WithinDeadline(async token=>
    {
        foreach(bool retry in new[]{false,true}) await CheckRemoval(retry,token);
    });

    private static async Task CheckRemoval(bool retry,CancellationToken token)
    {
        var pair=FolderDeleteChecks.BoundPair(); var paths=new PathScope(pair,[pair],DriveType.Fixed); using var store=new InMemoryStateStore();
        var pc=FolderDeleteChecks.Rooted(new FakeSide(Side.Local),pair); var db=FolderDeleteChecks.Rooted(new FakeSide(Side.Dropbox),pair); var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,db,store);
        foreach(var path in new[]{"folder","folder/child"})
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(ItemChecks.File(Side.Local,path,1,"pc-"+path,path=="folder"),ItemChecks.File(Side.Dropbox,path,1,"db-"+path,path=="folder")),null,null,default);
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailLocalInUse,"folder/child",new([new(Side.Local,"folder/child","pc-folder/child")],Side.Local),DateTimeOffset.UnixEpoch,false),default);
        int calls=0; var range=new Scope("folder","folder");
        var deletion=retry ? new Change(Side.Local,ChangeKind.Deleted,"folder",null,"pc-folder",null,false,true,1,JournalId:1) : new Change(Side.Dropbox,ChangeKind.Deleted,"folder",null,null,null,false,false);
        Task Apply(Change input,CancellationToken cancel)=>ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,input,pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),async (binding,before,after,ct)=>
        {
            Check.Require(before==range && after==range,"Removal comparison escaped the retired subtree");
            calls++; await ItemChecks.Hop(pair,paths,queue,pc,db,store)(binding,before,after,ct);
        },cancel,queue.Held);
        if(retry)
        {
            bool ready=false;
            var pending=queue.AdmitAsync(pair,new([deletion],null,new(1,2)),(input,ct)=>ready?Apply(input,ct):throw new global::Massdex.SyncShark.Engine.Run.WorkQueue.Unsettled(),null,token);
            ready=true;
            Check.Require(await queue.RetryHeldAsync(range,token),"The folder input was not retained for retry");
            await pending.WaitAsync(token);
        }
        else await Apply(deletion,token);
        Check.Require(calls==1 && store.Records.Count==0 && store.OpenNotices.Count==0 && pc.Mutations.Count==0 && db.Mutations.Count==0,"Retired folder left a child binding or failure behind");
    }

    public static async Task HopOneSidedChildDecidesAtFinalNames()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var old=ItemChecks.File(Side.Local,"Old/c",1,"local"); var peer=ItemChecks.File(Side.Dropbox,"Old/c",1,"peer");
        var saved=SyncedEntries.From(old,peer); await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var edited=old with {Path="New/c",Size=2}; pc.Seed(edited,[7,7]);
        var row=new Row(saved.LocalKey,edited,null,saved,[],null,RowContext.Neutral with {Destination="New/c",CompleteInventory=true});
        await RowExecution.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,row,[],pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(db.Items.ContainsKey("New/c") && !db.Items.ContainsKey("Old/c") && store.Records.Values.Single().DropboxPath=="New/c","A one-sided hop child was decided at its old name");
    }

    public static async Task GoneTargetFolderRemovalSettles()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var old=ItemChecks.File(Side.Local,"parent",0,"local",true); var peer=ItemChecks.File(Side.Dropbox,"parent",0,"peer",true);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,default);
        var deleted=new Change(Side.Local,ChangeKind.Deleted,old.Path,null,old.Id,null,false,true);
        Check.Require(await (Task<bool>)ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,deleted,pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[])==true && store.Records.Count==0 && db.Requests[nameof(ISide.DeleteAsync)]==0,"An already-removed peer folder kept its binding or was deleted again");
    }

    public static async Task DeliberateRefusalIsNotRetained()
    {
        var pair=ItemChecks.Pair(); var inner=pair with {PairId="inner",LocalRoot=Path.Combine(pair.LocalRoot,"top/nested"),DropboxRoot=pair.DropboxRoot+"/top/nested",Roots=null};
        var paths=new PathScope(pair,[pair,inner],DriveType.Fixed); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var old=ItemChecks.File(Side.Local,"top",0,"local",true); var peer=ItemChecks.File(Side.Dropbox,"top",0,"peer",true);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,default); var moved=old with {Path="moved"}; pc.Seed(moved,[]); db.Seed(peer,[]);
        var change=new Change(Side.Local,ChangeKind.Renamed,moved.Path,old.Path,old.Id,moved,false,false); int calls=0;
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,change,pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,failed:(_,_)=>calls++);
        Check.Require(calls==0 && store.OpenNotices.Any(n=>n.Cause==TextKey.FailCarriesOtherPair),"A deliberate refusal was retained as a race");
    }

    // A bound folder deletion the current scope excludes settles with no effect: it saves its journal boundary and the binding stays.
    public static Task ExcludedFolderDeleteSettlesAsync() => Run.AutoSyncChecks.WithinDeadline(async ct=>
    {
        foreach(var keep in new[]{false,true})
        {
            // The app's outside/bin echo (IdentityDeleted:false) and a selected identity tombstone both hold nothing: E6 settles each page.
            foreach(var tombstone in new[]{false,true})
            {
                var pair=ItemChecks.Pair() with {Exclusions=[new(Side.Local,"dir",keep)]};
                var paths=new PathScope(pair,[pair],DriveType.Fixed);
                var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
                using var store=new InMemoryStateStore(); await store.SavePairAsync(pair,ct);
                var dir=ItemChecks.File(Side.Local,"dir",0,"pc-dir",true); var child=ItemChecks.File(Side.Local,"dir/a",1,"pc-a");
                var cloudDir=ItemChecks.File(Side.Dropbox,"dir",0,"db-dir",true); var cloudChild=ItemChecks.File(Side.Dropbox,"dir/a",1,"db-a");
                var root=SyncedEntries.From(dir,cloudDir); var nested=SyncedEntries.From(child,cloudChild);
                await store.CommitAsync(pair.PairId,null,root,null,null,ct);
                await store.CommitAsync(pair.PairId,null,nested,null,null,ct);
                pc.History.Add((dir,[])); pc.History.Add((child,[1])); db.Seed(cloudDir,[]); db.Seed(cloudChild,[1]);
                var peerRoot=db.Items["dir"]; var peerChild=db.Items["dir/a"];
                var change=new Change(Side.Local,ChangeKind.Deleted,"dir",null,dir.Id,null,false,tombstone);
                var end=new JournalPosition(1,10);
                var queue=new global::Massdex.SyncShark.Engine.Run.WorkQueue();
                var settled=new List<JournalPosition>();
                var local=FolderDeleteChecks.Observe.Wrap<ISide>(pc,(name,args)=>{ if(name==nameof(ISide.SettleJournalAsync)) settled.Add((JournalPosition)args[0]!); return null; });
                void Events(RunEvent e){ if(e.Kind==RunEventKind.Wait && e.Cause==TextKey.WaitItemRetry) Check.Require(false,"The excluded bound folder page was retained instead of settling"); }
                var page=global::Massdex.SyncShark.Engine.Run.AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,Events,paths,queue,Side.Local,new ChangePage([change],null,end),local,db,store,ct);
                await page;
                Check.Require(settled.Count==1 && settled[0]==end,"The excluded bound folder page did not save its exact boundary");
                Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.KeyFor(Side.Local,"dir"),ct)==root && await store.FindAsync(pair.PairId,Side.Local,PathNames.KeyFor(Side.Local,"dir/a"),ct)==nested,"The excluded bound folder settlement changed or dropped a root or child binding");
                Check.Require(db.Items["dir"]==peerRoot && db.Items["dir/a"]==peerChild && !pc.Items.ContainsKey("dir") && !pc.Items.ContainsKey("dir/a"),"An excluded bound folder deletion changed a peer or created a local item");
            }
            // A Dropbox path-only tombstone under the rule settles too, and a live excluded PC counterpart must stay.
            {
                var pair=ItemChecks.Pair() with {Exclusions=[new(Side.Dropbox,"dir",keep)]};
                var paths=new PathScope(pair,[pair],DriveType.Fixed);
                var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
                using var store=new InMemoryStateStore();
                var dir=ItemChecks.File(Side.Local,"dir",0,"pc-dir",true); var cloudDir=ItemChecks.File(Side.Dropbox,"dir",0,"db-dir",true);
                var saved=SyncedEntries.From(dir,cloudDir);
                await store.CommitAsync(pair.PairId,null,saved,null,null,ct);
                pc.Seed(dir,[]); // the live excluded PC copy; Dropbox has only a tombstone
                var peer=pc.Items["dir"];
                await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Deleted,"dir",null,null,null,false,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,ct,_=>[]);
                Check.Require(await store.FindAsync(pair.PairId,Side.Dropbox,PathNames.KeyFor(Side.Dropbox,"dir"),ct)==saved && pc.Items["dir"]==peer && !db.Items.ContainsKey("dir"),"A Dropbox excluded tombstone settled by changing the binding or a live copy");
            }
        }
    },"Excluded folder deletion");

    // A removed local name that is a hard link of another bound name deletes only its own Dropbox peer. An alias that
    // is in scope but unbound, or bound to a different file identity, keeps the ordinary refusal.
    public static async Task HardLinkUnlinkAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var live=ItemChecks.File(Side.Local,"A/f",1,"shared"); var gone=live with {Path="Old/f"};
        var peerLive=ItemChecks.File(Side.Dropbox,"A/f",1,"peer-live"); var peerGone=ItemChecks.File(Side.Dropbox,"Old/f",1,"peer-gone");
        pc.Seed(live,[1]); db.Seed(peerLive,[1]); db.Seed(peerGone,[1]); pc.LinkNames["shared"]=[live.Path];
        var liveBinding=SyncedEntries.From(live,peerLive); var goneBinding=SyncedEntries.From(gone,peerGone);
        await store.CommitAsync(pair.PairId,null,liveBinding,null,null,default);
        await store.CommitAsync(pair.PairId,null,goneBinding,null,null,default);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,db,store);
        var outcome=await RowExecution.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Row(PathNames.Key("Old/f"),null,peerGone,goneBinding,[],null,RowContext.Neutral),[],pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default);
        Check.Require(outcome==EffectOutcome.Applied && !db.Items.ContainsKey("Old/f") && db.History.Any(v=>v.Entry==peerGone),"Removing one bound hard-link name did not delete only its own Dropbox peer");
        Check.Require(db.Items.TryGetValue("A/f",out var keptPeer) && keptPeer.Entry==peerLive && await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("A/f"),default)==liveBinding,"The surviving name's binding or Dropbox peer changed");

        await HardLinkUnlinkInputAsync();
        await RequireHardLinkRefusalAsync("An in-scope unbound alias",["A/f","Extra/f"],differentId:false);
        await RequireHardLinkRefusalAsync("An alias bound to a different file",["A/f"],differentId:true);
    }

    // The journal's Deleted for one name of a known hard-linked file, whose other bound name lives on, is that name's delete.
    private static async Task HardLinkUnlinkInputAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var live=ItemChecks.File(Side.Local,"A/f",1,"shared"); var gone=live with {Path="Old/f"};
        var peerLive=ItemChecks.File(Side.Dropbox,"A/f",1,"peer-live"); var peerGone=ItemChecks.File(Side.Dropbox,"Old/f",1,"peer-gone");
        pc.Seed(live,[1]); db.Seed(peerLive,[1]); db.Seed(peerGone,[1]); pc.LinkNames["shared"]=[live.Path];
        var liveBinding=SyncedEntries.From(live,peerLive);
        await store.CommitAsync(pair.PairId,null,liveBinding,null,null,default);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(gone,peerGone),null,null,default);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,db,store);
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Deleted,gone.Path,null,gone.Id,null,false,false),pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default); }
        catch(InventoryIncompleteException) { }
        Check.Require(!db.Items.ContainsKey("Old/f") && db.History.Any(v=>v.Entry==peerGone) && await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("Old/f"),default)==null,"A removed hard-link name's Deleted input was held instead of deleting its Dropbox peer");
        Check.Require(db.Items.TryGetValue("A/f",out var keptPeer) && keptPeer.Entry==peerLive && await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("A/f"),default)==liveBinding,"The surviving name's binding or Dropbox peer changed on a Deleted input");
    }

    private static async Task RequireHardLinkRefusalAsync(string what,string[] seeded,bool differentId)
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var live=ItemChecks.File(Side.Local,"A/f",1,"shared"); var gone=live with {Path="Old/f"};
        var peerLive=ItemChecks.File(Side.Dropbox,"A/f",1,"peer-live"); var peerGone=ItemChecks.File(Side.Dropbox,"Old/f",1,"peer-gone");
        pc.Seed(live,[1]); db.Seed(peerLive,[1]); db.Seed(peerGone,[1]); pc.LinkNames["shared"]=seeded;
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(live with {Id=differentId?"other-id":live.Id},peerLive),null,null,default);
        var goneBinding=SyncedEntries.From(gone,peerGone);
        await store.CommitAsync(pair.PairId,null,goneBinding,null,null,default);
        var refused=false;
        try { await RowExecution.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Row(PathNames.Key("Old/f"),null,peerGone,goneBinding,[],null,RowContext.Neutral),[],pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default); }
        catch(ChangedUnderneathException) { refused=true; }
        Check.Require(refused && db.Items.ContainsKey("Old/f") && db.Items.ContainsKey("A/f"),$"{what} was treated as another name of the removed file");
    }

    public static async Task RunAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var a=ItemChecks.File(Side.Local,"file",1,"pc"); var b=ItemChecks.File(Side.Dropbox,"file",1,"db");
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,b),null,null,default);
        await store.CommitAsync(pair.PairId,SyncedEntries.From(a,b),SyncedEntries.From(a,b),new(3,10),null,default);
        var commits=store.Requests[nameof(IStore.CommitAsync)];
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Changed,b.Path,null,b.Id,b,false,false),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,a.Path,null,a.Id,a,true,false,10,JournalId:3),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(pc.Requests[nameof(ISide.ReadOneAsync)]==0 && db.Requests[nameof(ISide.ReadOneAsync)]==0 && store.Requests[nameof(IStore.CommitAsync)]==commits,"Matching feed entry was re-read or re-committed");
        await HopOneSidedChildDecidesAtFinalNames();
        await GoneTargetFolderRemovalSettles();
        await DeliberateRefusalIsNotRetained();
        await JournalExitAsync();
        await MoveBeforeRecheckAsync();
        await HardLinkUnlinkAsync();
        await AutoSyncCloseTableAsync();
        await OneDigestPerAttemptAsync();
        await IsolatedNoticeReadFaultOnRecordStillProgressesAsync();
        await HardLinkAliasAsync();
        await ItemSyncChecks.ParentCaseReplacementAsync();
        await KindChangedRemovalRetainsAsync();
        await DirectTombstoneProvesContentAsync();
        await DirectTombstoneFailedUploadAsync();
        var defects=new List<string>();
        await RemoteReplacementAsync(defects);
        await UncertainDeleteAsync(defects);
        await ComparedNoticesAsync(defects);
        await RetriesChecks.MoveObservationAsync(defects);
        if(typeof(ItemSync).GetMethod("CounterpartAsync")==null || typeof(ItemSync).GetMethods(System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public).Single(m=>m.Name=="ReportFailureAsync").IsPrivate) defects.Add("R3-8 shared callers cannot bind the mapper and exact failure owner");
        try { await ItemSync.RequireRemovalAsync(Task.FromResult(new RemovalResult(RemovalOutcome.Pending,null,null))); defects.Add("MF2 pending removal settled"); }
        catch(InventoryIncompleteException) { }
        pc.Seed(a,[1]); db.Seed(b,[1]);
        long reads=pc.Requests[nameof(ISide.ReadOneAsync)];
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,a.Path,null,a.Id,a,true,false,10,JournalId:4),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default); } catch(NotImplementedException) { }
        Check.Require(pc.Requests[nameof(ISide.ReadOneAsync)]>reads,"Wrong journal was accepted as an echo");
        var waitingRow=new Row("held",a,b,null,[],null,RowContext.Neutral);
        bool retained=false;
        try { ItemSync.FinishDecision(new(ActionKind.None,waitingRow,global::Massdex.SyncShark.Engine.Planner.NoneReason.Unfinished),(key,reproducible)=>retained=key==waitingRow.Key && !reproducible); } catch(InventoryIncompleteException) { }
        if(!retained) defects.Add("R2-4 unfinished planner input did not notify the retained-work route");
        var original=ItemChecks.File(Side.Local,"dir",0,"old-folder",true); var peer=ItemChecks.File(Side.Dropbox,"dir",0,"peer-folder",true);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(original,peer),null,null,default);
        pc.Seed(original with {Id="replacement"},[]); db.Seed(peer,[]);
        db.Items[peer.Path]=(peer with {Id="new-cloud-folder"},[]);
        var cloudProbes=db.Requests[nameof(ISide.ReadByIdAsync)];
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Deleted,peer.Path,null,null,null,false,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]); } catch(NotImplementedException) { } catch(InventoryIncompleteException) { }
        if(db.Requests[nameof(ISide.ReadByIdAsync)]==cloudProbes || db.Items[peer.Path].Entry.Id!="new-cloud-folder") defects.Add("R3-1 path tombstone did not select the original Dropbox folder by id");
        db.Items.Remove(b.Path);db.Seed(b with {Path="cloud-new"},[1]);
        cloudProbes=db.Requests[nameof(ISide.ReadByIdAsync)];
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Deleted,b.Path,null,null,null,false,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);
        if(db.Requests[nameof(ISide.ReadByIdAsync)]==cloudProbes || pc.Requests[nameof(ISide.DeleteAsync)]!=0 || pc.Items.ContainsKey(a.Path) || !pc.Items.TryGetValue("cloud-new",out var landed) || landed.Entry.Id!=a.Id || !landed.Bytes.SequenceEqual(new byte[]{1})) defects.Add("R3-3 old-name feed tombstone bypassed native move identity");
        Check.Require(defects.Count==0,string.Join("; ",defects));

    }
    // Each side advertises its current finite page. The next fence becomes visible only after projection.
    private static Func<(JournalPosition? Local,string? Dropbox),bool> RemovalCoverage(FakeSide pc,FakeSide db,Side victimSide,Entry victim,List<Change>? projected=null,Change[]? held=null,bool postProof=true)
    {
        var beforeLocal=new ChangePage([],null,new(3,10));var beforeCloud=new ChangePage([],"before-removal",null);
        var afterLocal=new ChangePage(victimSide==Side.Local?[..(held??[]),new Change(Side.Local,ChangeKind.Deleted,victim.Path,null,victim.Id,null,false,true,12,JournalId:3)]:[],null,new(3,13));
        var afterCloud=new ChangePage(victimSide==Side.Dropbox?[new Change(Side.Dropbox,ChangeKind.Deleted,victim.Path,null,victim.Id,null,false,false)]:[],"after-removal",null);
        pc.Pages.Enqueue(beforeLocal);db.Pages.Enqueue(beforeCloud);
        return fence=>
        {
            bool removed=(victimSide==Side.Local?pc:db).History.Any(v=>v.Entry==victim);
            var journal=pc.ReadChangesAsync(null,null,default).GetAwaiter().GetResult();
            var cloud=db.ReadChangesAsync(null,null,default).GetAwaiter().GetResult();
            if(journal!=(removed?afterLocal:beforeLocal) || cloud!=(removed?afterCloud:beforeCloud) || fence.Local!=journal.Position || fence.Dropbox!=cloud.Cursor) return false;
            if(!removed) { pc.Pages.Enqueue(afterLocal);db.Pages.Enqueue(afterCloud); }
            else if(!postProof) return false;
            projected?.AddRange(journal.Changes);projected?.AddRange(cloud.Changes);
            return true;
        };
    }

    /// <summary>Real classifier, effects and removal owners. A tombstone belongs to the old identity, not to its successor.</summary>
    public static async Task TombstoneRecoveryAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var prior=ItemChecks.File(Side.Local,"local-parent",0,"prior",true);var oldPeer=ItemChecks.File(Side.Dropbox,"local-parent",0,"old-peer",true);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(prior,oldPeer),null,null,default);
        pc.Seed(prior with {Id="successor"},[]);db.Seed(oldPeer,[]);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Deleted,prior.Path,null,prior.Id,null,false,true),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],coversThrough:RemovalCoverage(pc,db,Side.Dropbox,oldPeer));
        Check.Require(pc.Items[prior.Path].Entry.Id=="successor" && !db.Items.ContainsKey(oldPeer.Path) && db.Mutations.Contains(oldPeer.Id),"Local replacement hid the original selected folder removal");
        var originalFile=ItemChecks.File(Side.Local,"reused",1,"12");var peerFile=ItemChecks.File(Side.Dropbox,"reused",1,"old-cloud");var replacement=ItemChecks.File(Side.Local,originalFile.Path,2,"13");
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(originalFile,peerFile),null,null,default);pc.Seed(replacement,[2]);db.Seed(peerFile,[1]);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Deleted,originalFile.Path,null,originalFile.Id,null,false,false,11,JournalId:3),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);
        Check.Require(!db.Items.ContainsKey(peerFile.Path) && db.Mutations.Contains(peerFile.Id) && pc.Items[replacement.Path].Entry.Id==replacement.Id,"Recovered delete did not settle only the originally linked file");
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,replacement.Path,null,replacement.Id,replacement,true,false,12,JournalId:3),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);
        Check.Require(db.Items[replacement.Path].Bytes.SequenceEqual(new byte[]{2}) && store.Records.Values.Any(r=>r.LocalId==replacement.Id && r.DropboxId!=peerFile.Id),"Replacement did not sync as its own ordered change");
        var remoteDeletes=db.Requests[nameof(ISide.DeleteAsync)];
        var directory=ItemChecks.File(Side.Local,"dir",0,"local-folder",true); var cloudDir=ItemChecks.File(Side.Dropbox,"dir",0,"old-folder",true);
        pc.Seed(directory,[]);db.Seed(cloudDir with {Id="new-folder"},[]);
        var saved=SyncedEntries.From(directory,cloudDir); await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var child=ItemChecks.File(Side.Local,"dir/item",2,"child");pc.Seed(child,[2]);
        var held=new Change(Side.Local,ChangeKind.Changed,child.Path,null,child.Id,child,true,false);
        var ack=false; held=held with {ConsumeHeld=()=>{Check.Require(pc.Items[child.Path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[child.Path].Bytes.SequenceEqual(new byte[]{2}),"Held child acknowledged before recovery");ack=true;}};
        var queue=new global::Massdex.SyncShark.Engine.Run.WorkQueue();var projected=new List<Change>();
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Deleted,cloudDir.Path,null,null,null,false,false),pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,_=>projected,coversThrough:RemovalCoverage(pc,db,Side.Local,directory,projected,[held]));
        Check.Require(ack && pc.History.Any(h=>h.Entry.Id==directory.Id) && db.Items[cloudDir.Path].Entry.Id=="new-folder" && db.Requests[nameof(ISide.DeleteAsync)]==remoteDeletes,"Old parent or its successor was mistaken for the other identity");
    }

    public static async Task EmptyFolderHistoryAsync()
    {
        foreach(var (mark,cause) in new (bool?,TextKey?)[]{(false,null),(true,null),(null,null),(null,TextKey.ReadLocalFailed)})
        {
            var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
            var folder=ItemChecks.File(Side.Local,"empty",0,"folder",true);pc.Seed(folder,[]);db.DeletedHistory[folder.Path]=new DeletionEvidence.DeletedPath(folder.Path,folder.Path);
            var row=new Row(PathNames.Key(folder.Path),folder,null,null,[],null,RowContext.Neutral with {FirstRun=true,CompleteInventory=true,DeletionEligible=true,ProtectedContents=mark,UnreadableCause=cause,Destination=folder.Path});
            bool retained=false;
            try { await RowExecution.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,row,[],pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],(_,_)=>retained=true,coversThrough:RemovalCoverage(pc,db,Side.Local,folder)); }
            catch(InventoryIncompleteException) when(retained && mark==null) { }
            catch(ChangedUnderneathException) when(retained && cause!=null) { }
            if(mark==false) Check.Require(db.Requests[nameof(ISide.DeletedAsync)]==1 && pc.History.Count(h=>h.Entry==folder)==1 && !pc.Items.ContainsKey(folder.Path) && !db.Items.ContainsKey(folder.Path) && pc.Requests[nameof(ISide.ReadChangesAsync)]==2 && db.Requests[nameof(ISide.ReadChangesAsync)]==2,"Empty folder removal needs both exact fences and its history receipt");
            else Check.Require(pc.Items[folder.Path].Entry==folder && pc.History.Count==0 && db.Requests[nameof(ISide.DeletedAsync)]==0 && (mark==true?db.Items[folder.Path].Entry.IsFolder:cause!=null?store.OpenNotices.Any(n=>n.Cause==cause):retained),"Protected, missing or unreadable evidence must not select folder removal");
        }
    }

    public static async Task FeedMoveAsync()
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var a=ItemChecks.File(Side.Local,"old",1,"pc"); var b=ItemChecks.File(Side.Dropbox,"old",1,"db");var moved=b with {Path="new"};
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,b),null,null,default);pc.Seed(a,[1]);db.Seed(moved,[1]);
        var queue=new global::Massdex.SyncShark.Engine.Run.WorkQueue();
        foreach(var change in new[]{new Change(Side.Dropbox,ChangeKind.Deleted,b.Path,null,null,null,false,false),new Change(Side.Dropbox,ChangeKind.Changed,moved.Path,null,moved.Id,moved,false,false)})
            await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,change,pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,queue.Held);
        Check.Require(pc.Requests[nameof(ISide.DeleteAsync)]==0 && pc.Requests[nameof(ISide.MoveAsync)]==1 && pc.Items["new"].Entry.Id==a.Id && !pc.Items.ContainsKey("old") && store.Records.Values.Single().DropboxPath=="new","Feed relocation became delete/copy or lost the original binding");
        var original=ItemChecks.File(Side.Local,"A",1,"old-pc");var oldCloud=ItemChecks.File(Side.Dropbox,"A",1,"old-db");var actual=original with {Path="B"};var replaced=oldCloud with {Id="successor-db"};
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(original,oldCloud),null,null,default);pc.Seed(actual,[1]);db.Seed(replaced,[1]);
        foreach(var eventInput in new[]{new Change(Side.Dropbox,ChangeKind.Changed,"A",null,replaced.Id,replaced,false,false),new Change(Side.Local,ChangeKind.Renamed,"B","A",actual.Id,actual,false,false)})
            await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,eventInput,pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,queue.Held);
        Check.Require(db.Items.TryGetValue("A",out var replacementAtA) && replacementAtA.Entry.Id==replaced.Id && pc.Items["B"].Entry.Id==original.Id && store.Records.Values.Any(x=>x.LocalPath=="B" && x.LocalId==original.Id && x.DropboxPath=="B") && store.Records.Values.Any(x=>x.DropboxPath=="A" && x.DropboxId==replaced.Id && x.LocalPath=="A"),"Dropbox replacement and relocated Local original must each reach their own settled binding");
        var moving=ItemChecks.File(Side.Local,"move-source",1,"moving");var mate=ItemChecks.File(Side.Dropbox,moving.Path,1,"mate");
        var arrival=moving with {Path="occupied"};var incumbent=ItemChecks.File(Side.Dropbox,arrival.Path,2,"incumbent");var partner=ItemChecks.File(Side.Local,"partner",2,"partner");
        pc.Seed(arrival,[1]);db.Seed(mate,[1]);db.Seed(incumbent,[2]);pc.Seed(partner,[2]);var owner=SyncedEntries.From(partner,incumbent);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(moving,mate),null,null,default);await store.CommitAsync(pair.PairId,null,owner,null,null,default);
        var rename=new Change(Side.Local,ChangeKind.Renamed,arrival.Path,moving.Path,arrival.Id,arrival,false,false);
        Task Move(CancellationToken ct)=>ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,rename,pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),ct,queue.Held);
        using(var stop=new CancellationTokenSource())
        using(await new ItemLocks().AcquireAsync([(Side.Local,ItemLocks.Physical(pair,Side.Local,partner.Path),false)],default))
        {
            var attempt=Move(stop.Token);bool blocked=!attempt.IsCompleted;stop.Cancel();
            try {await attempt;} catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
            Check.Require(blocked && db.Items.ContainsKey(mate.Path) && pc.Items.ContainsKey(arrival.Path),"Composed move ignored contention on incumbent partner");
        }
        bool refused=false;
        try {await Move(default);} catch(ChangedUnderneathException e) when(PathNames.Same(e.Path,arrival.Path)) {refused=true;}
        Check.Require(!refused && store.Records.Values.Contains(owner) && store.Records.Values.Any(x=>x.LocalId==moving.Id && x.DropboxId==mate.Id && x.LocalPath!=arrival.Path),"Released partner did not permit labelled arrival");
        var departing=ItemChecks.File(Side.Local,"from",1,"departing");var absent=ItemChecks.File(Side.Dropbox,"from",1,"absent");
        var split=SyncedEntries.From(departing,absent);await store.CommitAsync(pair.PairId,null,split,null,null,default);
        pc.Seed(departing with {Path="to"},[1]);bool splitHeld=false;
        try {await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Renamed,"to","from",departing.Id,null,false,false),pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,queue.Held,(_,_)=>splitHeld=true);} catch(InventoryIncompleteException) when(splitHeld) { }
        Check.Require(!splitHeld && !store.Records.Values.Contains(split) && store.OpenNotices.Any(n=>n.Cause==TextKey.InfoMovedDeleted),"Completed move/delete split was retained as a relink");
        var start=ItemChecks.File(Side.Local,"origin",1,"arriving");var sourcePeer=ItemChecks.File(Side.Dropbox,start.Path,1,"arriving-peer");var landing=start with {Path="vacant"};
        var oldPair=SyncedEntries.From(start,sourcePeer);var gone=SyncedEntries.From(landing with {Id="gone-local"},sourcePeer with {Path=landing.Path,Id="gone-remote"});
        pc.Seed(landing,[1]);db.Seed(sourcePeer,[1]);await store.CommitAsync(pair.PairId,null,oldPair,null,null,default);await store.CommitAsync(pair.PairId,null,gone,null,null,default);
        var arrivalInput=new Change(Side.Local,ChangeKind.Renamed,landing.Path,start.Path,start.Id,landing,false,false);bool retained=false;
        Task Arrive()=>ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,arrivalInput,pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,queue.Held,(_,_)=>retained=true);
        try {await Arrive();} catch(InventoryIncompleteException) when(retained) { }
        Check.Require(retained && !store.Records.Values.Contains(gone) && store.Records.Values.Contains(oldPair) && db.Items.ContainsKey(start.Path),"Vacating the gone incumbent acknowledged an unfinished arrival");
        await Arrive();Check.Require(!db.Items.ContainsKey(start.Path) && db.Items.TryGetValue(landing.Path,out var arrived) && arrived.Entry.Id==sourcePeer.Id && store.Records.Values.Any(x=>x.LocalId==start.Id && x.DropboxId==sourcePeer.Id && x.LocalPath==landing.Path && x.DropboxPath==landing.Path),"Reobserved arrival did not move the original peer");
        // Until the move owner settles, even exact identities at physical paths are not a logical binding.
        var priorPc=ItemChecks.File(Side.Local,"old-parent/child",1,"mapped-pc");var priorDb=ItemChecks.File(Side.Dropbox,"old-parent/child",1,"mapped-db");
        var link=SyncedEntries.From(priorPc,priorDb);var finalPc=ItemChecks.File(Side.Local,"new-parent/child",2,priorPc.Id);var finalDb=priorDb with {Path="new-parent/child"};
        var oldPcOccupant=ItemChecks.File(Side.Local,priorPc.Path,3,"other-pc");var oldDbOccupant=ItemChecks.File(Side.Dropbox,priorDb.Path,4,"other-db");
        var waypoint=finalDb with {Path=PathNames.Child(PathNames.CaseRenamePath("new-parent",ItemChecks.Settings.CaseRenameSuffix),"child")};
        pc.Seed(finalPc,[2]);db.Seed(waypoint,[1]);pc.Seed(oldPcOccupant,[3]);db.Seed(oldDbOccupant,[4]);await store.CommitAsync(pair.PairId,null,link,null,null,default);
        bool held=false;
        try {await RowExecution.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Row(link.LocalKey,finalPc,waypoint,link,[],null,RowContext.Neutral with {CompleteInventory=true}),[],pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,queue.Held,(_,_)=>held=true);} catch(InventoryIncompleteException) when(held) { }
        Check.Require(held && store.Records.Values.Contains(link) && db.Items[waypoint.Path].Bytes.SequenceEqual(new byte[]{1}),"An uncovered hop must not write or record the waypoint");
        await db.MoveAsync(waypoint,finalDb.Path,default);
        Task<EffectOutcome?> ApplyPrepared(bool addOnly)=>RowExecution.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Row(link.LocalKey,finalPc,finalDb,link,[],null,RowContext.Neutral),[],pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,queue.Held,addOnly:addOnly);
        var filtered=await ApplyPrepared(true);var applied=await ApplyPrepared(false);
        Check.Require(filtered is null && applied==EffectOutcome.Applied,"Prepared update must return null when filtered and Applied when executed");
        Check.Require(db.Items[finalDb.Path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[oldDbOccupant.Path].Entry==oldDbOccupant && pc.Items[oldPcOccupant.Path].Entry==oldPcOccupant && store.Records.Values.Any(x=>x.LocalId==finalPc.Id && x.LocalPath==finalPc.Path && x.DropboxPath==finalDb.Path) && !store.Records.Values.Contains(link),"Mapped edit must publish at the verified receiving path, retire the original binding and preserve both old occupants");
        await global::Massdex.SyncShark.Engine.Tests.Run.RepairChecks.WithRepairStore(async (scanStore,scanPair,ct)=>
        {
            var scope=new PathScope(scanPair,[scanPair],DriveType.Fixed);var local=new FakeSide(Side.Local);var remote=new FakeSide(Side.Dropbox);var cloud=DeleteReceipt.On(remote);
            var localDebt=await scanStore.ReadRepairAsync(scanPair.PairId,Side.Local,ct);var cloudDebt=await scanStore.ReadRepairAsync(scanPair.PairId,Side.Dropbox,ct);
            Check.Require(await scanStore.FinishRepairAsync(scanPair.PairId,(localDebt.Revision,cloudDebt.Revision),"{}",ct) is (true,true),"Folder removal fixture did not finish first Repair");
            var scanQueue=await ItemChecks.StartQueueAsync(scanPair,scope,local,cloud,scanStore);
            var folder=ItemChecks.File(Side.Dropbox,"removed-folder",0,"peer-folder",true);
            var saved=SyncedEntries.From(ItemChecks.File(Side.Local,folder.Path,0,"missing-folder",true),folder);
            remote.Seed(folder,[]);await scanStore.CommitAsync(scanPair.PairId,null,saved,null,null,ct);
            var bypass=await RowExecution.ApplyAsync(scanPair,ItemChecks.Settings,_=>{},scope,new Row(saved.LocalKey,null,folder,saved,[],null,RowContext.Neutral),[],local,cloud,scanStore,ItemChecks.Population(scanPair,scope,local,cloud,scanStore),ItemChecks.Hop(scanPair,scope,scanQueue,local,cloud,scanStore),ct,scanQueue.Held,coversThrough:RemovalCoverage(local,remote,Side.Dropbox,folder));
            Check.Require(bypass is null && remote.History.Any(v=>v.Entry==folder),"Settled folder removal must not return a prepared row effect");
        });
    }

    public static async Task ReplacementHeldChildAsync()
    {
        var pair=ItemChecks.Pair() with {FirstRun=FirstRunPolicy.LocalWins};var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var file=ItemChecks.File(Side.Local,"item",1,"pc");var dir=ItemChecks.File(Side.Dropbox,"item",0,"folder",true);var child=ItemChecks.File(Side.Dropbox,"item/child",2,"edit");
        pc.Seed(file,[1]);db.Seed(dir,[]);db.Seed(child,[2]);var claim=new Change(Side.Dropbox,ChangeKind.Changed,child.Path,null,child.Id,child,false,false);var ack=false;claim=claim with {ConsumeHeld=()=>ack=true};
        var row=new Row(PathNames.Key(file.Path),file,dir,null,[],null,RowContext.Neutral with {FirstRun=true,CompleteInventory=true,ProtectedContents=false});
        try {await RowExecution.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,row,[],pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,s=>{if(!PathNames.Same(s.DropboxPath,dir.Path))return [];return [claim];});} catch(ItemFailedException) { } catch(InventoryIncompleteException) { } catch(ChangedUnderneathException) { }
        var exactFailure=store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack && n.Path==child.Path && n.Subject.Items.Any(m=>m.Side==Side.Dropbox && m.Id==child.Id && m.Path==child.Path));
        var settledConflict=store.Records.Values.Any(r=>r.DropboxId==child.Id && r.DropboxContentHash==child.ContentHash);
        Check.Require((ack?settledConflict:exactFailure) && db.Items.TryGetValue(child.Path,out var kept) && kept.Bytes.SequenceEqual(new byte[]{2}) && pc.Items[file.Path].Bytes.SequenceEqual(new byte[]{1}),"Replacement neither settled the held version nor retained its exact recovery notice");
    }

    private static async Task RemoteReplacementAsync(List<string> defects)
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var a=ItemChecks.File(Side.Local,"A",1,"original");var old=ItemChecks.File(Side.Dropbox,"A",1,"bound");var b=a with {Path="B"};var replacement=old with {Id="replacement"};
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,old),null,null,default);pc.Seed(b,[1]);db.Seed(replacement,[1]);
        try {await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Changed,"A",null,replacement.Id,replacement,false,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);} catch(NotImplementedException) { } catch(InventoryIncompleteException) { }
        if(pc.Requests[nameof(ISide.ReadByIdAsync)]==0 || !pc.Items.TryGetValue("B",out var source) || source.Entry.Id!=a.Id || !db.Items.TryGetValue("A",out var kept) || kept.Entry.Id!=replacement.Id || db.Mutations.Contains(replacement.Id)) defects.Add("Dropbox replacement at A was treated as the old victim without locating Local original B");
    }

    private static async Task JournalExitAsync()
    {
        foreach(var destination in new[]{"bin/file","outside/file","inside/file"})
        {
            var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
            var original=ItemChecks.File(Side.Local,"file",1,"12");var peer=ItemChecks.File(Side.Dropbox,"file",1,"peer");var moved=original with {Path=destination};
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(original,peer),null,null,default);db.Seed(peer,[1]);
            bool inside=destination=="inside/file";if(inside) pc.Seed(moved,[1]);else pc.History.Add((moved,[1]));
            var deleted=new Change(Side.Local,ChangeKind.Deleted,original.Path,null,original.Id,null,false,false,11,JournalId:3);
            var rename=deleted with {Kind=ChangeKind.Renamed,OldPath=original.Path,Path=destination,Usn=12};
            bool held=false;
            try {await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,deleted,pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>inside?[rename]:[],(_,_)=>held=true);}
            catch(InventoryIncompleteException) when(held) { }
            if(inside) Check.Require(!held && db.History.Count==0 && !db.Items.ContainsKey(peer.Path) && db.Items[destination].Entry.Id==peer.Id && store.Records.Values.Single().LocalPath==destination && !store.OpenNotices.Any(),"An in-scope journal relocation must move the linked peer without deleting it");
            else Check.Require(!held && !db.Items.ContainsKey(peer.Path) && db.History.Count==1 && db.History[0].Entry==peer && db.History[0].Bytes.SequenceEqual(new byte[]{1}) && !store.Records.Any() && !store.OpenNotices.Any(),"An out-of-scope journal delete must settle, archive the peer and forget the binding without a notice");
        }
    }

    private static async Task UncertainDeleteAsync(List<string> defects)
    {
        foreach(bool postProof in new[]{true,false})
        {
            var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
            var old=ItemChecks.File(Side.Local,"dir",0,"12",true);var peer=ItemChecks.File(Side.Dropbox,"dir",0,"peer",true);var replacement=old with {Id="13"};
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,default);pc.Seed(replacement,[]);db.Seed(peer,[]);
            var input=new Change(Side.Local,ChangeKind.Deleted,old.Path,null,old.Id,null,false,false,11,JournalId:3);
            bool retained=false;
            var coverage=RemovalCoverage(pc,db,Side.Dropbox,peer,postProof:postProof);
            async Task Apply(Change change)
            {
                retained=false;
                try {await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,change,pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],(_,_)=>retained=true,coversThrough:coverage);}
                catch(InventoryIncompleteException) when(retained) { }
            }
            pc.Seed(old with {Path="elsewhere"},[]);await Apply(input);
            if(!retained || db.Mutations.Contains(peer.Id)) defects.Add("Uncertain delete of an identity still present elsewhere must not remove its peer");
            pc.Items.Remove("elsewhere");await Apply(input with {Kind=ChangeKind.Changed});
            if(!retained || db.Mutations.Contains(peer.Id)) defects.Add("Missing identity after Changed must not become a deletion");
            await Apply(input);
            if(db.Items.ContainsKey(peer.Path) || db.History.Count(v=>v.Entry==peer)!=1) defects.Add("Confirmed absence did not remove the selected old peer exactly once");
            if(retained==postProof || pc.Requests[nameof(ISide.ReadChangesAsync)]!=2 || db.Requests[nameof(ISide.ReadChangesAsync)]!=2) defects.Add("Selected removal must read each pre/post fence and retain on insufficient post-proof");
            if(pc.Items[old.Path].Entry!=replacement || pc.Mutations.Contains(replacement.Id)) defects.Add("Selected removal touched the local successor");
        }
    }

    private static async Task ComparedNoticesAsync(List<string> defects)
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();
        var pc=ItemChecks.File(Side.Local,"item",1,"pc");var db=ItemChecks.File(Side.Dropbox,"item",1,"db");
        var replay=new Change(Side.Dropbox,ChangeKind.Changed,db.Path,null,db.Id,db,false,false);
        var warning=new Notice(0,NoticeKind.PutBack,TextKey.FailLocalInUse,db.Path,new([new(Side.Dropbox,db.Path,db.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false,true);
        await store.SaveNoticeAsync(pair.PairId,warning,default);
        await store.SaveNoticeAsync(pair.PairId,warning with {Kind=NoticeKind.Failure,Subject=new([new(Side.Local,pc.Path,pc.Id)],Side.Local)},default);
        var before=store.OpenNotices.ToArray();var exact=before.Single(n=>n.Kind==NoticeKind.PutBack);
        await ItemSync.CloseComparedNoticesAsync(pair.PairId,replay,db,pc,before,store,default);
        if(store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure) || store.OpenNotices.SingleOrDefault(n=>n.Kind==NoticeKind.PutBack)?.Matches(exact)!=true)
            defects.Add("Same-ID replay must preserve the exact PutBack and close the matched ordinary Failure");
    }

    public static async Task ReplayPutBackAsync()
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var a=ItemChecks.File(Side.Local,"item",1,"pc");var b=ItemChecks.File(Side.Dropbox,"item",1,"db");pc.Seed(a,[1]);db.Seed(b,[1]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,b),null,null,default);
        var replay=new Change(Side.Dropbox,ChangeKind.Changed,b.Path,null,b.Id,b,false,false);
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailLocalInUse,b.Path,new([new(Side.Dropbox,b.Path,b.Id)],Side.Dropbox),DateTimeOffset.UtcNow,false,true),default);
        var exact=store.OpenNotices.Single();await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,replay,pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(store.OpenNotices.SingleOrDefault()?.Matches(exact)==true,"R4 replay cleared an exact PutBack warning on identity alone");
        var old=ItemChecks.File(Side.Local,"edit",1,"pc-edit");var peer=ItemChecks.File(Side.Dropbox,"edit",1,"db-edit");var edit=ItemChecks.File(Side.Local,old.Path,2,old.Id);
        pc.Seed(edit,[2]);db.Seed(peer,[1]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,default);
        var localWarning=exact with {Id=0,Path=old.Path,Subject=new([new(Side.Local,old.Path,old.Id)],Side.Local)};
        await store.SaveNoticeAsync(pair.PairId,localWarning,default);var selected=store.OpenNotices.Single(n=>n.Path==old.Path);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,edit.Path,null,edit.Id,edit,true,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(store.OpenNotices.All(n=>n.Id!=selected.Id) && store.OpenNotices.Any(n=>n.Matches(exact)) && db.Items[peer.Path].Bytes.SequenceEqual(new byte[]{2}) && store.Records.Values.Any(x=>x.LocalId==edit.Id && x.DropboxContentHash==edit.ContentHash),"Changed bound version must settle its exact PutBack and sync the new bytes");
        foreach(var side in new[]{Side.Local,Side.Dropbox})
        {
            var origin="before-"+side;var destination="after-"+side;
            var left=ItemChecks.File(Side.Local,origin,1,"left-"+side);var right=ItemChecks.File(Side.Dropbox,origin,1,"right-"+side);
            var moved=ItemChecks.File(side,destination,2,side==Side.Local?left.Id:right.Id);
            (side==Side.Local?pc:db).Seed(moved,[2]);(side==Side.Local?db:pc).Seed(side==Side.Local?right:left,[1]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(left,right),null,null,default);
            await store.SaveNoticeAsync(pair.PairId,localWarning with {Path=origin,Subject=new([new(side,origin,moved.Id)],side)},default);
            var priorWarning=store.OpenNotices.Single(n=>n.Path==origin);var queue=new global::Massdex.SyncShark.Engine.Run.WorkQueue();
            var fence=new ChangePage([],null,new(3,10));var cursor=new ChangePage([],"moved",null);
            pc.Pages.Enqueue(fence);db.Pages.Enqueue(cursor);
            var change=new Change(side,side==Side.Local?ChangeKind.Renamed:ChangeKind.Changed,destination,side==Side.Local?origin:null,moved.Id,moved,true,false);
            await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,change,pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,queue.Held,coversThrough:at=>pc.ReadChangesAsync(null,null,default).GetAwaiter().GetResult().Position==at.Local && db.ReadChangesAsync(null,null,default).GetAwaiter().GetResult().Cursor==at.Dropbox && at==(fence.Position,cursor.Cursor));
            Check.Require(store.OpenNotices.All(n=>n.Id!=priorWarning.Id) && store.OpenNotices.Any(n=>n.Matches(exact)) && pc.Items[destination].Bytes.SequenceEqual(new byte[]{2}) && db.Items[destination].Bytes.SequenceEqual(new byte[]{2}),"Moved edit must sync at its destination and close only its old-name PutBack: "+side);
        }
        var fresh=ItemChecks.File(Side.Local,"unbound",3,"unbound-pc");pc.Seed(fresh,[3]);
        await store.SaveNoticeAsync(pair.PairId,localWarning with {Path=fresh.Path,Subject=new([new(Side.Local,fresh.Path,fresh.Id)],Side.Local)},default);var unbound=store.OpenNotices.Single(n=>n.Path==fresh.Path);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,fresh.Path,null,fresh.Id,fresh,true,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(store.OpenNotices.Any(n=>n.Matches(unbound)) && db.Items[fresh.Path].Bytes.SequenceEqual(new byte[]{3}),"An unbound same-ID input may sync but must keep the exact PutBack");
    }

    public static async Task PlannedFailureAsync()
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var folder=ItemChecks.File(Side.Local,"parent",0,"pc-parent",true);pc.Seed(folder,[]);
        var context=RowContext.Neutral with {FirstRun=true,CompleteInventory=true,DeletionEligible=true,ProtectedContents=true,UnreadableCause=TextKey.ReadLocalFailed,Destination=folder.Path};
        var row=new Row(PathNames.Key(folder.Path),folder,null,null,[],null,context);
        var envelopes=new List<Notice>();int retained=0;
        // A planned refusal is the decision: one saved envelope, no retained input, and the source untouched.
        var outcome=await RowExecution.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,row,[],pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],(_,_)=>retained++,failedNotice:envelopes.Add);
        var stored=store.OpenNotices.Single();
        Check.Require(outcome==EffectOutcome.Refused && retained==0 && envelopes.Count==1 && stored.Matches(envelopes[0] with {Id=stored.Id}) && pc.Items.ContainsKey(folder.Path) && pc.History.Count==0,"R4 planned failure did not hand Repair the exact saved envelope without retaining input or touching the source");
    }

    private static async Task MoveBeforeRecheckAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var cloud=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var a=ItemChecks.File(Side.Local,"A",1,"pc-id"); var remote=ItemChecks.File(Side.Dropbox,"A",1,"cloud-id");
        pc.Seed(a,[1]); cloud.Seed(remote,[1]); var binding=SyncedEntries.From(a,remote);
        await store.CommitAsync(pair.PairId,null,binding,null,null,default);
        var observed=await pc.ReadOneAsync("A",default) ?? throw new InvalidDataException("Expected A at acquisition");
        var input=new Change(Side.Local,ChangeKind.Changed,"A",null,observed.Id,observed,true,false,11,JournalId:3);
        await pc.MoveAsync(observed,"B",default);
        bool retained=false;
        Task Apply(Func<Scope,IEnumerable<Change>> held)=>ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,input,pc,cloud,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,held,(key,settled)=>retained=key==binding.LocalKey && !settled);
        try {await Apply(_=>[]);} catch(InventoryIncompleteException) when(retained) { }
        Check.Require(retained && !cloud.Mutations.Any() && store.Records.Values.Single()==binding,"Missing old name was settled without a matching rename");
        // Successful grouped application is asserted unconditionally with the real owners in RetainedMovePrefixAsync.

    }

    /// <summary>Both original inputs finish, and only the original cloud identity moves before the prefix advances.</summary>
    public static async Task RetainedMovePrefixAsync()
    {
        foreach(var reuse in new[]{false,true}) await CheckMove(reuse);
        static async Task CheckMove(bool reuse)
        {
            var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
            var a=ItemChecks.File(Side.Local,"A",1,"local");var peer=ItemChecks.File(Side.Dropbox,"A",1,"remote");var b=a with {Path="B"};
            pc.Seed(b,[1]);db.Seed(peer,[1]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,peer),null,null,default);
            var fresh=ItemChecks.File(Side.Local,"A",2,"successor");if(reuse) pc.Seed(fresh,[2]);
            var write=new Change(Side.Local,ChangeKind.Changed,"A",null,a.Id,a,true,false,11,JournalId:3);var rename=new Change(Side.Local,ChangeKind.Renamed,"B","A",a.Id,b,false,false,12,JournalId:3);
            var queue=new global::Massdex.SyncShark.Engine.Run.WorkQueue();var page=new ChangePage([write,rename],null,new(3,13));
            if(reuse)
            {
                var before=pc.Requests[nameof(ISide.SettleJournalAsync)];
                await global::Massdex.SyncShark.Engine.Run.AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,Side.Local,page,pc,db,store,default);
                Check.Require(pc.Requests[nameof(ISide.SettleJournalAsync)]==before+2,"AutoSync did not settle both original journal inputs at reused A");
            }
            else
            {
                List<ulong> completed=[];List<JournalPosition> prefix=[];
                await queue.AdmitAsync(pair,page,async (change,ct)=>
                {
                    await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,change,pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),ct,queue.Held,(_,reproduced)=>{if(!reproduced)throw new global::Massdex.SyncShark.Engine.Run.WorkQueue.Unsettled();});
                    Check.Require(db.Items.TryGetValue("B",out var moved) && moved.Entry.Id==peer.Id && store.Records.Values.SingleOrDefault() is {LocalPath:"B",DropboxPath:"B"},"Retained write and rename must settle both prefixes with exactly one peer move and the original binding at B");
                    completed.Add(change.Usn!.Value);
                },(position,_)=>{prefix.Add(position);return Task.CompletedTask;},default);
                Check.Require(completed.SequenceEqual(new ulong[]{11,12}) && prefix.SequenceEqual(new[]{new JournalPosition(3,12),new JournalPosition(3,13)}),"Retained write and rename must settle both prefixes with exactly one peer move and the original binding at B");
            }
            Check.Require(db.Requests[nameof(ISide.MoveAsync)]==1 && db.Requests[nameof(ISide.DeleteAsync)]==0 && db.Requests[nameof(ISide.PublishAsync)]==0 && !db.Items.ContainsKey("A") && db.Items.TryGetValue("B",out var result) && result.Entry.Id==peer.Id && store.Records.Values.SingleOrDefault() is {LocalPath:"B",DropboxPath:"B"} link && link.LocalId==a.Id && link.DropboxId==peer.Id,"Retained write and rename must settle both prefixes with exactly one peer move and the original binding at B");
            var settledBefore=pc.Requests[nameof(ISide.SettleJournalAsync)];
            if(reuse)
            {
                await global::Massdex.SyncShark.Engine.Run.AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,Side.Local,new([new(Side.Local,ChangeKind.Changed,"A",null,fresh.Id,fresh,true,false,13,JournalId:3)],null,new(3,14)),pc,db,store,default);
                Check.Require(pc.Items["A"].Entry==fresh && db.Items.TryGetValue("A",out var copy) && copy.Bytes.SequenceEqual(new byte[]{2}) && store.Records.Values.Any(x=>x.LocalId==fresh.Id && x.DropboxId==copy.Entry.Id),"Reused A did not settle as its own item after the peer moved to B");
            }
            pc.Items.Remove("B");
            await global::Massdex.SyncShark.Engine.Run.AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,Side.Local,new([new(Side.Local,ChangeKind.Deleted,"B",null,a.Id,null,false,true,14,JournalId:3)],null,new(3,15)),pc,db,store,default);
            Check.Require(!db.Items.ContainsKey("B") && db.History.Count(x=>x.Entry.Id==peer.Id)==1 && store.Records.Values.All(x=>x.LocalId!=a.Id) && pc.Requests[nameof(ISide.SettleJournalAsync)]==settledBefore+(reuse?2:1),"Genuine deletion after the rename must retire B and settle its journal prefix");
        }
    }

    public static async Task EditedPeerAsync()
    {
        {
            var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
            var old=ItemChecks.File(Side.Local,"item",1,"old");var peer=ItemChecks.File(Side.Dropbox,"item",1,"peer");var next=ItemChecks.File(Side.Local,"item",2,"replacement");
            pc.Seed(next,[2]);db.Seed(peer,[1]);var saved=SyncedEntries.From(old,peer);await store.CommitAsync(pair.PairId,null,saved,null,null,default);
            var removal=new Change(Side.Local,ChangeKind.Deleted,old.Path,null,old.Id,null,false,true,1,JournalId:1);
            Task Apply(Change change)=>ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,change,pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);
            db.PublishFailures[peer.Path]=new ItemFailedException(peer.Path,TextKey.FailChanged,null);
            try {await Apply(removal);} catch(ItemFailedException) { }
            Check.Require(db.Items.TryGetValue(peer.Path,out var original) && original.Entry==peer && original.Bytes.SequenceEqual(new byte[]{1}) && store.Records.Values.Contains(saved) && db.Requests[nameof(ISide.DeleteAsync)]==0,"Split safe save deleted the peer before replacement publication");
            db.PublishFailures.Clear();await Apply(new(Side.Local,ChangeKind.Renamed,next.Path,"temp",next.Id,next,true,false,2,JournalId:1));
            Check.Require(db.Items[peer.Path].Bytes.SequenceEqual(new byte[]{2}) && store.Records.Values.Single().LocalId==next.Id && db.Requests[nameof(ISide.DeleteAsync)]==0,"Split safe save failed to publish the successor");
        }
        foreach(byte version in new byte[]{2,3})
        {
            var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
            var old=ItemChecks.File(Side.Local,"item",1,"old");var peer=ItemChecks.File(Side.Dropbox,"item",1,"peer");var successor=ItemChecks.File(Side.Local,"item",2,"new");var edit=ItemChecks.File(Side.Dropbox,"item",version,peer.Id);
            pc.Seed(successor,[2]);db.Seed(edit,[version]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,default);
            try {await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Deleted,old.Path,null,old.Id,null,false,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);} catch(ChangedUnderneathException) { }
            var links=store.Records.Values.ToArray();
            if(version==2)
                Check.Require(!store.OpenNotices.Any(n=>n.Cause==TextKey.InfoKeptBoth) && links.Length==1 && links[0].LocalId==successor.Id && links[0].DropboxId==peer.Id && pc.Items.Count==1 && db.Items.Count==1 && pc.Items[old.Path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[peer.Path].Bytes.SequenceEqual(new byte[]{2}),"Equal successor and edited peer must adopt one binding without conflict copies");
            else Check.Require(links.Length==2 && links.Any(x=>x.LocalId==successor.Id && db.Items[x.DropboxPath].Bytes.SequenceEqual(new byte[]{2})) && links.Any(x=>x.DropboxId==peer.Id && pc.Items[x.LocalPath].Bytes.SequenceEqual(new byte[]{3})) && pc.Items[old.Path].Entry==successor && db.Items[peer.Path].Entry==edit,"Retry must link both edited peer and successor versions, not return after the by-ID read");
        }
        await global::Massdex.SyncShark.Engine.Tests.Run.RepairChecks.WithRepairStore(async (store,pair,ct)=>
        {
            pair=pair with {Roots=new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("a","r","h","d"))};
            await store.SavePairAsync(pair,ct);
            var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=FolderDeleteChecks.Rooted(new FakeSide(Side.Local),pair);var db=FolderDeleteChecks.Rooted(new FakeSide(Side.Dropbox),pair);
            var localDebt=await store.ReadRepairAsync(pair.PairId,Side.Local,ct);var cloudDebt=await store.ReadRepairAsync(pair.PairId,Side.Dropbox,ct);
            Check.Require(await store.FinishRepairAsync(pair.PairId,(localDebt.Revision,cloudDebt.Revision),"{}",ct) is (true,true),"Meeting folders fixture did not finish first Repair");
            pc.Pages.Enqueue(new([],null,new(7,31)));db.Pages.Enqueue(new([],"opaque",null));
            var queue=new global::Massdex.SyncShark.Engine.Run.WorkQueue(token=>store.PositionAsync("1",token));
            await queue.AdmitAsync(pair,new([],null,new(7,32)),(_,_)=>Task.CompletedTask,(position,token)=>store.SavePositionAsync("1",position,token),null,null,ct);
            await queue.AdmitAsync(pair,new([],"opaque",null),(_,_)=>Task.CompletedTask,null,(cursor,token)=>store.SaveCursorAsync(pair.PairId,cursor,token),null,ct);
            Check.Require(queue.CoversHop(((await pc.BoundaryAsync(ct)).Position,(await db.BoundaryAsync(ct)).Cursor)),"Meeting fixture did not cover both callback boundaries");
            var old=ItemChecks.File(Side.Local,"item",1,"old");var peer=ItemChecks.File(Side.Dropbox,"item",1,"peer");
            pc.Seed(ItemChecks.File(Side.Local,"item",0,"new",true),[]);db.Seed(ItemChecks.File(Side.Dropbox,"item",0,peer.Id,true),[]);
            pc.Seed(ItemChecks.File(Side.Local,"item/both",4,"left-version"),[4]);db.Seed(ItemChecks.File(Side.Dropbox,"item/both",5,"right-version"),[5]);
            pc.Seed(ItemChecks.File(Side.Local,"item/left",2,"left"),[2]);db.Seed(ItemChecks.File(Side.Dropbox,"item/right",3,"right"),[3]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,ct);
            await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Deleted,old.Path,null,old.Id,null,false,false),pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),ct,_=>[]);
            bool Linked(SyncedEntry x,byte value)=>pc.Items.TryGetValue(x.LocalPath,out var a) && a.Entry.Id==x.LocalId && a.Bytes.SequenceEqual(new[]{value}) && db.Items.TryGetValue(x.DropboxPath,out var b) && b.Entry.Id==x.DropboxId && b.Bytes.SequenceEqual(new[]{value});
            var left=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("item/both"),ct);var right=await store.FindAsync(pair.PairId,Side.Dropbox,PathNames.Key("item/both"),ct);
            Check.Require(left is {LocalPath:"item/both",LocalId:"left-version"} && Linked(left,4) && right is {DropboxPath:"item/both",DropboxId:"right-version"} && Linked(right,5),"Meeting folders did not preserve and link both original child versions");
            Check.Require(pc.Items.TryGetValue("item/right",out var r) && r.Bytes.SequenceEqual(new byte[]{3}) && db.Items.TryGetValue("item/left",out var l) && l.Bytes.SequenceEqual(new byte[]{2}) && await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(old.Path),ct) is {IsFolder:true,LocalId:"new"} root && root.DropboxId==peer.Id,"Meeting successor folders must merge both disjoint children before replacing the file binding");
        });
    }

    /// <summary>Real planner and notice owner required at integration; never count their stubs as success.</summary>
    public static async Task UnreadableAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var old=ItemChecks.File(Side.Local,"old",1,"identity"); var moved=old with {Path="new"};
        var cloud=ItemChecks.File(Side.Dropbox,"old",1,"cloud"); var binding=SyncedEntries.From(old,cloud);
        await store.CommitAsync(pair.PairId,null,binding,null,null,default); pc.Seed(moved,[1]);
        pc.ReadFailures[old.Path]=new ItemFailedException(old.Path,TextKey.ReadLocalFailed,null);
        bool? settled=null;
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Renamed,moved.Path,old.Path,moved.Id,moved,false,false),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,failed:(_,ok)=>settled=ok); } catch(ItemFailedException) { }
        Check.Require(settled==false && store.OpenNotices.Count==1 && store.OpenNotices.Single().Cause==TextKey.ReadLocalFailed && store.Records.Values.Contains(binding),"MF1 unreadable rename probe bypassed notice or lost binding");
        Check.Require(store.OpenNotices.Single().Path==old.Path,"Move did not acquire the old endpoint");
        await store.CloseNoticeAsync(pair.PairId,store.OpenNotices.Single(),default);
        pc.ReadFailures.Clear(); pc.Seed(old with {Skip=nameof(TextKey.SkipUnreadable)},[1]);
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Renamed,moved.Path,old.Path,moved.Id,moved,false,false),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default); } catch(ItemFailedException) { }
        Check.Require(store.OpenNotices.Count==1 && store.OpenNotices.Single().Path==old.Path && store.OpenNotices.Single().Cause==TextKey.ReadLocalFailed,"Unreadable old rename member was used as identity evidence");
        await store.CloseNoticeAsync(pair.PairId,store.OpenNotices.Single(),default); pc.Items.Remove(old.Path);
        pc.ReadFailures.Clear(); pc.Items.Remove(moved.Path); var changed=old with {Modified=old.Modified.AddSeconds(1)};
        pc.Seed(changed,[2]); db.Seed(cloud,[1]); pc.ContentFailures[old.Path]=new ItemFailedException(old.Path,TextKey.ReadLocalFailed,null);
        settled=null;
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,old.Path,null,old.Id,changed,true,false),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,failed:(_,ok)=>settled=ok); } catch(ItemFailedException) { }
        Check.Require(pc.Requests[nameof(ISide.ContentHashAsync)]==1 && settled!=null && store.OpenNotices.Count==1 && store.OpenNotices.Single().Cause==TextKey.ReadLocalFailed && store.Records.Values.Contains(binding) && pc.Items[old.Path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[old.Path].Bytes.SequenceEqual(new byte[]{1}),"MF1 unreadable digest lost binding, changed content, or did not record one failure");
    }


    /// <summary>AutoSync keeps 0.3.104's any-member close after a successful change: an in-sync bound file closes its exact old Failure with no
    /// extra read; a Failure shown only at the binding's other key stays; a re-raise after the input's notice load keeps the newer generation.</summary>
    public static async Task AutoSyncCloseTableAsync()
    {
        foreach(var arm in new[]{"in-sync","other-key","re-raised"})
        {
            var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
            var dbPath=arm=="other-key"?"cloud-name":"item";
            var a=ItemChecks.File(Side.Local,"item",1,"pc");var b=ItemChecks.File(Side.Dropbox,dbPath,1,"db");pc.Seed(a,[1]);db.Seed(b,[1]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,b),null,null,default);
            var token=(await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailLocalInUse,"item",new([new(Side.Local,"item",a.Id),new(Side.Dropbox,dbPath,b.Id)],Side.Local),DateTimeOffset.UnixEpoch,false),default))!;
            bool raised=false,started=false;
            var cloud=FolderDeleteChecks.Observe.On(db,(op,args)=>
            {
                if(arm=="re-raised" && started && !raised && op==nameof(ISide.ReadOneAsync)) { raised=true; store.SaveNoticeAsync(pair.PairId,token with {Id=0,At=DateTimeOffset.UtcNow},default).GetAwaiter().GetResult(); }
                return null;
            });
            var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,cloud,store);started=true;
            long reads=db.Requests[nameof(ISide.ReadOneAsync)],byId=db.Requests[nameof(ISide.ReadByIdAsync)];
            await Run.AutoSyncChecks.WithinDeadline(ct=>global::Massdex.SyncShark.Engine.Run.AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,Side.Dropbox,new([new Change(Side.Dropbox,ChangeKind.Changed,dbPath,null,b.Id,b,false,false)],"next",null),pc,cloud,store,ct),"AutoSync close "+arm);
            var open=store.OpenNotices.SingleOrDefault(n=>n.Id==token.Id);
            Check.Require(await store.CursorAsync(pair.PairId,default)=="next" && store.Records.Values.Single()==SyncedEntries.From(a,b),"An in-sync input did not settle its page: "+arm);
            if(arm=="in-sync") Check.Require(open==null && db.Requests[nameof(ISide.ReadOneAsync)]==reads+1 && db.Requests[nameof(ISide.ReadByIdAsync)]==byId,"An in-sync bound file did not close its exact old Failure, or read more than today");
            else if(arm=="other-key") Check.Require(open!=null && open.Matches(token),"A Failure shown only at the binding's other key was closed by the input at this key");
            else Check.Require(raised && open!=null && open.At>token.At,"The input's close removed a Failure re-raised after its notice load");
        }
    }

    /// <summary>A same-stamp dirty PC input decided Record reads one digest per attempt, with or without an exact old Failure at its name.</summary>
    public static async Task OneDigestPerAttemptAsync()
    {
        foreach(bool notice in new[]{true,false})
        {
            var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
            var a=ItemChecks.File(Side.Local,"item",1,"pc");var b=ItemChecks.File(Side.Dropbox,"item",1,"db");pc.Seed(a,[1]);db.Seed(b,[1]);
            var binding=SyncedEntries.From(a,b);await store.CommitAsync(pair.PairId,null,binding,null,null,default);
            if(notice) await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailLocalInUse,"item",new([new(Side.Local,"item",a.Id),new(Side.Dropbox,"item",b.Id)],Side.Local),DateTimeOffset.UnixEpoch,false),default);
            await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,"item",null,a.Id,a,true,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);
            Check.Require(pc.Requests[nameof(ISide.ContentHashAsync)]==1 && pc.Requests[nameof(ISide.OpenReadAsync)]==0 && store.Records.Values.Single()==binding && store.OpenNotices.Count==0 && db.Mutations.Count==0,
                $"A same-stamp dirty input did not take exactly one digest (exact old Failure: {notice})");
        }
    }

    /// <summary>A repeatably unreadable notice at any other key never stops a Record input: only the input's own notice read runs, and the next input settles.</summary>
    public static Task IsolatedNoticeReadFaultOnRecordStillProgressesAsync()=>Run.RepairChecks.WithRepairStore(async (store,pair,ct)=>
    {
        var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
        var inputs=new List<(Change Change,SyncedEntry Binding,Entry Current)>();
        foreach(var name in new[]{"a","z"})
        {
            var left=ItemChecks.File(Side.Local,name,1,"pc-"+name);var right=ItemChecks.File(Side.Dropbox,name,1,"db-"+name);var current=left with {Modified=left.Modified.AddSeconds(5)};
            pc.Seed(current,[1]);db.Seed(right,[1]);var binding=SyncedEntries.From(left,right);await store.CommitAsync(pair.PairId,null,binding,null,null,ct);
            inputs.Add((new Change(Side.Local,ChangeKind.Changed,name,null,left.Id,current,true,false),binding,current));
        }
        var old=(await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailLocalInUse,"a",new([new(Side.Local,"a","pc-a"),new(Side.Dropbox,"a","db-a")],Side.Local),DateTimeOffset.UtcNow,false),ct))!;
        int faults=0;string own="";
        var proxy=FolderDeleteChecks.Observe.Wrap<IStore>(store,(op,args)=>
        {
            if(op==nameof(IStore.NoticesUnderAsync) || op==nameof(IStore.NoticesAtAsync) && (string?)args[1]!=own) { faults++; return Unreadable(); }
            return null;
        });
        foreach(var (change,binding,current) in inputs)
        {
            own=PathNames.Key(change.Path!);bool? reported=null;
            await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,change,pc,db,proxy,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,ct,_=>[],(_,ok)=>reported=ok);
            Check.Require(reported==null && await store.FindAsync(pair.PairId,Side.Local,binding.LocalKey,ct) is {} now && now.LocalModifiedTicks==current.Modified.UtcTicks && db.Mutations.Count==0,"A Record input did not commit its new stamp: "+change.Path);
        }
        Check.Require(faults==0 && await store.NoticeAsync(pair.PairId,old.Id,ct)==null,"A Record read a notice at another key, or did not close its own old Failure");
        static async IAsyncEnumerable<Notice> Unreadable(){await Task.CompletedTask;foreach(var n in Array.Empty<Notice>()) yield return n;throw new InvalidDataException("A repeatably unreadable notice");}
    });

    /// <summary>A new hard-link name the move owner rules out of scope is an ordinary new item; a genuine move still moves, and a mixed group waits.</summary>
    public static async Task HardLinkAliasAsync()
    {
        var failures=new List<string>();
        foreach(var arm in new[]{"live-alias","renamed-unbound","true-move","mixed-group","excluded-name","own-key-rename","held-decision"})
        {
            var pair=ItemChecks.Pair() with {Exclusions=arm=="excluded-name"?[new Exclusion(Side.Local,"B",true)]:[]};var paths=new PathScope(pair,[pair],DriveType.Fixed);
            var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
            var bound=ItemChecks.File(Side.Local,"A/f",1,"shared");var peer=ItemChecks.File(Side.Dropbox,"A/f",1,"peer");
            db.Seed(ItemChecks.File(Side.Dropbox,"A",0,"db-A",true),[]);pc.Seed(ItemChecks.File(Side.Local,"A",0,"pc-A",true),[]);db.Seed(peer,[1]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(bound,peer),null,null,default);
            var name=arm switch {"renamed-unbound"=>"C/f","true-move"=>"D/f",_=>"B/f"};
            var alias=bound with {Path=name};pc.Seed(alias,[1]);
            foreach(var folder in new[]{"B","C","D"}) pc.Seed(ItemChecks.File(Side.Local,folder,0,"pc-"+folder,true),[]);
            if(arm=="true-move") pc.Items.Remove("A/f"); else pc.Seed(bound,[1]);
            if(arm=="mixed-group")
            {
                var second=bound with {Path="E/f"};var secondPeer=ItemChecks.File(Side.Dropbox,"E/f",1,"peer-e");pc.Seed(second,[1]);db.Seed(secondPeer,[1]);
                await store.CommitAsync(pair.PairId,null,SyncedEntries.From(second,secondPeer),null,null,default);
            }
            pc.LinkNames["shared"]=pc.Items.Values.Where(v=>v.Entry.Id=="shared").Select(v=>v.Entry.Path).ToArray();
            // "a decision other than None/OutOfScope (None/Held)": a tie at the bound name holds the move decision, so no fallback execution and no effect.
            if(arm=="held-decision") await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Tie,TextKey.TieEqualTimes,bound.Path,new([new(Side.Local,bound.Path,bound.Id),new(Side.Dropbox,peer.Path,peer.Id)],null),DateTimeOffset.UnixEpoch,false),default);
            var change=arm switch
            {
                "renamed-unbound"=>new Change(Side.Local,ChangeKind.Renamed,name,"Old/f","shared",alias,false,false),
                "own-key-rename"=>new Change(Side.Local,ChangeKind.Renamed,name,"A/f","shared",alias,false,false),
                _=>new Change(Side.Local,ChangeKind.Changed,name,null,"shared",alias,true,false)
            };
            bool retained=false;
            try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,change,pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],(_,ok)=>retained|=!ok); }
            catch(InventoryIncompleteException) when(retained) { }
            bool uploaded=db.Items.TryGetValue(name,out var copy) && copy.Bytes.SequenceEqual(new byte[]{1}) && copy.Entry.Id!=peer.Id;
            bool originalKept=db.Items.TryGetValue("A/f",out var kept) && kept.Entry==peer && store.Records.Values.Any(r=>r.LocalPath=="A/f" && r.DropboxId==peer.Id);
            bool ok=arm switch
            {
                "live-alias" or "renamed-unbound" => uploaded && originalKept && db.Requests[nameof(ISide.MoveAsync)]==0 && db.Requests[nameof(ISide.DeleteAsync)]==0,
                "true-move" => db.Items.TryGetValue(name,out var moved) && moved.Entry.Id==peer.Id && !db.Items.ContainsKey("A/f") && db.Requests[nameof(ISide.PublishAsync)]==0,
                "mixed-group" => retained && db.Mutations.Count==0 && db.Requests[nameof(ISide.PublishAsync)]==0,
                "held-decision" => db.Mutations.Count==0 && pc.Mutations.Count==0 && db.Requests[nameof(ISide.PublishAsync)]==0 && originalKept && pc.Items[name].Entry==alias && pc.Items[bound.Path].Entry==bound && store.OpenNotices.Any(n=>n.Kind==NoticeKind.Tie),
                _ => db.Mutations.Count==0 && db.Requests[nameof(ISide.PublishAsync)]==0 && originalKept
            };
            if(!ok) failures.Add($"{arm}: uploaded={uploaded} kept={originalKept} retained={retained} publish={db.Requests[nameof(ISide.PublishAsync)]} move={db.Requests[nameof(ISide.MoveAsync)]}");
        }
        Check.Require(failures.Count==0,"Hard-link alias: "+string.Join("; ",failures));
    }

    /// <summary>A saved PC path whose parent differs from the disk only in case is the same place: a proved Dropbox edit replaces the PC file.</summary>
    public static async Task ParentCaseReplacementAsync()
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var saved=ItemChecks.File(Side.Local,"parent/Child",1,"pc");var oldPeer=ItemChecks.File(Side.Dropbox,"Parent/Child",1,"db");var edited=ItemChecks.File(Side.Dropbox,"Parent/Child",2,"db");
        pc.Seed(ItemChecks.File(Side.Local,"Parent",0,"pc-P",true),[]);pc.Seed(saved,[1]);pc.LinkNames["pc"]=["Parent/Child"];
        db.Seed(ItemChecks.File(Side.Dropbox,"Parent",0,"db-P",true),[]);db.Seed(edited,[2]);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(saved,oldPeer),null,null,default);
        // A by-name read keeps the requested parent spelling; a by-ID read reports the on-disk spelling, as NTFS does.
        var local=FolderDeleteChecks.Observe.On(pc,(op,args)=>op==nameof(ISide.ReadByIdAsync) && Equals(args[0],"pc") && pc.Items.Values.Any(v=>v.Entry.Id=="pc")
            ? Task.FromResult<(IdentityLocation Location,bool InScope)?>((new(FakeSide.Volume,"Parent/Child",saved with {Path="Parent/Child"}),true)) : null);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Changed,edited.Path,null,edited.Id,edited,false,false),local,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],coversThrough:FolderDeleteChecks.Admitted(pc,db));
        Check.Require(pc.Items["parent/Child"].Bytes.SequenceEqual(new byte[]{2}) && pc.History.Any(h=>h.Entry.Id=="pc" && h.Bytes.SequenceEqual(new byte[]{1})) && store.Records.Values.Single().DropboxId==edited.Id && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure),
            "A parent-only case difference blocked a proved Dropbox edit from replacing the PC file");
    }

    /// <summary>A path-only Dropbox tombstone whose by-ID read finds the bound identity as the other kind is retained, never decided.</summary>
    public static async Task KindChangedRemovalRetainsAsync()
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var a=ItemChecks.File(Side.Local,"k",1,"pc-k");var b=ItemChecks.File(Side.Dropbox,"k",1,"db-k");pc.Seed(a,[1]);
        db.Seed(ItemChecks.File(Side.Dropbox,"elsewhere",0,b.Id,true),[]);
        var binding=SyncedEntries.From(a,b);await store.CommitAsync(pair.PairId,null,binding,null,null,default);
        string? key=null;bool? reproducible=null;
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Deleted,"k",null,null,null,false,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],(k,ok)=>{key=k;reproducible=ok;}); }
        catch(InventoryIncompleteException) { }
        Check.Require(key==binding.DropboxKey && reproducible==false && store.Records.Values.Single()==binding && pc.Mutations.Count==0 && db.Mutations.Count==0 && store.OpenNotices.Count==0,
            "A tombstone whose bound identity changed kind was decided instead of retained");
    }

    // An id-less Dropbox tombstone over a bound PC file reads the PC bytes once: a stamp-restored edit uploads with one edit notice, unchanged bytes recycle.
    private static async Task DirectTombstoneProvesContentAsync()
    {
        foreach(byte bytes in new byte[]{2,1})
        {
            var pair=FolderDeleteChecks.BoundPair();var paths=new PathScope(pair,[pair],DriveType.Fixed);using var store=new InMemoryStateStore();
            var pc=FolderDeleteChecks.Rooted(new FakeSide(Side.Local),pair);var db=FolderDeleteChecks.Rooted(new FakeSide(Side.Dropbox),pair);
            var left=ItemChecks.File(Side.Local,"c",1,"pc-c");var right=ItemChecks.File(Side.Dropbox,"c",1,"db-c");
            pc.Seed(left,[bytes]); // the stamps still match the baseline whatever the bytes are
            pc.IdleBoundary=new([],null,new(3,10));db.IdleBoundary=new([],"seen",null); // finite fences for the removal proof
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(left,right),null,null,default);
            var volume=pair.Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var local=FolderDeleteChecks.Observe.On(pc,(op,args)=>op==nameof(ISide.ReadByIdAsync) && pc.Items.Values.FirstOrDefault(v=>v.Entry.Id==(string?)args[0]).Entry is {} live
                ?Task.FromResult<(IdentityLocation,bool)?>((new(volume,live.Path,live),true)):null);
            await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Deleted,"c",null,null,null,false,false),local,db,store,
                (_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],coversThrough:_=>true);
            if(bytes==2)
                Check.Require(db.Items.TryGetValue("c",out var up) && up.Bytes.SequenceEqual(new byte[]{2}) && pc.History.Count==0 && store.OpenNotices.Count(n=>n.Cause==TextKey.InfoEditBeatsDelete)==1,
                    "A stamp-restored PC edit was deleted by an id-less Dropbox tombstone");
            else
                Check.Require(!pc.Items.ContainsKey("c") && pc.History.Count==1 && store.Records.Count==0 && store.OpenNotices.Count==0 && pc.Requests[nameof(ISide.ContentHashAsync)]==1,
                    "An unchanged PC file under an id-less Dropbox tombstone was not recycled after one content read");
        }
    }

    // The corrective upload of that edit fails for good or with an unknown outcome: the PC copy stays untouched and the
    // Dropbox delete is kept (retained, not released), as for any failed delete; it is decided again on its next attempt.
    private static Task DirectTombstoneFailedUploadAsync() => Run.AutoSyncChecks.WithinDeadline(async ct=>
    {
        foreach(var disposition in new[]{FailureDisposition.Permanent,FailureDisposition.UnknownEffect})
        {
            var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);using var store=new InMemoryStateStore();await store.SavePairAsync(pair,ct);
            var pc=FolderDeleteChecks.Rooted(new FakeSide(Side.Local),pair);var db=FolderDeleteChecks.Rooted(new FakeSide(Side.Dropbox),pair);
            var left=ItemChecks.File(Side.Local,"c",1,"pc-c");pc.Seed(left,[2]); // same stamps as the baseline, different bytes
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(left,ItemChecks.File(Side.Dropbox,"c",1,"db-c")),null,null,ct);
            var local=FolderDeleteChecks.Observe.On(pc,(op,args)=>op==nameof(ISide.ReadByIdAsync) && pc.Items.Values.FirstOrDefault(v=>v.Entry.Id==(string?)args[0]).Entry is {} live
                ?Task.FromResult<(IdentityLocation,bool)?>((new("1",live.Path,live),true)):null);
            db.PublishFailures["c"]=new ItemFailedException("c",TextKey.DropboxRefused,null){Disposition=disposition};
            bool? reproducible=null;
            try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Deleted,"c",null,null,null,false,false),local,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,ct,_=>[],(_,ok)=>reproducible=ok,coversThrough:_=>true); }
            catch(ItemFailedException) { }
            Check.Require(db.Requests[nameof(ISide.PublishAsync)]==1 && reproducible==false && pc.History.Count==0 && pc.Items["c"].Bytes.SequenceEqual(new byte[]{2}) && !db.Items.ContainsKey("c") && store.Records.Count==1,
                $"A failed corrective upload ({disposition}) released the Dropbox delete or changed the PC edit");
        }
    });

    /// <summary>Adds the exact terminal selected-folder receipt to the fake Dropbox delete; this fixture has no dispatcher.</summary>
    public class DeleteReceipt : System.Reflection.DispatchProxy
    {
        private ISide target = null!;
        internal static ISide On(ISide target)
        {
            var side = Create<ISide, DeleteReceipt>();
            ((DeleteReceipt)(object)side).target = target;
            return side;
        }
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ISide.DeleteAsync) && args is { Length: 4 })
                return Delete((Entry)args[0]!, (RemovalSelection?)args[1], (ActionKind)args[2]!, (CancellationToken)args[3]!);
            try { return method.Invoke(target, args); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }
        private async Task<RemovalResult> Delete(Entry expected, RemovalSelection? selection, ActionKind origin, CancellationToken ct)
        {
            var sent = await target.DeleteAsync(expected, selection, origin, ct);
            return sent with { Reply = new JobReply.Terminal([(expected, null, null)]) };
        }
    }
}
