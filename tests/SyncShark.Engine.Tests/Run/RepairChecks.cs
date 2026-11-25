using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static class RepairChecks
{
    public static async Task RunAsync()
    {
        await CheckScanCompletionAsync(); await InventoryPruningAsync(); await OwnAttemptAsync();
        await HealthyDirtySetAsync();
        await CoveredHopAsync();
        await BilateralNoticeAsync(); await RecoveryChecks.ClosedNoticeAsync(true); await RecoveryChecks.PutBackSurvivesRepairAsync(true);
        await RetainedAttemptAsync(); await SelectedStreamRaceAsync();
        await CoveredFolderAsync(true); await CoveredFolderAsync(false);
        await AutoSyncChecks.WithinDeadline(HardLinkEditThroughOutsideNameAsync);
        await AutoSyncChecks.WithinDeadline(UnreadableLinkedFileBehavesAsBaseAsync);
        await AutoSyncChecks.WithinDeadline(RetryHardLinkMemberAsync);
    }

    private static Task CheckScanCompletionAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var local=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        await store.OweRepairAsync(pair.PairId,Side.Local,"requested",ct);
        var paths=new PathScope(pair,[pair],DriveType.Fixed); var queue=new WorkQueue();
        int wakes=0; void Wake()=>++wakes;
        var observed=AutoSyncChecks.Observation.Wrap<IStore>((method,args)=>{
            if(method.Name==nameof(IStore.ClearScanAsync)) Check.Require(args[^1] is CancellationToken cleanup && cleanup==ct,"Scan cleanup did not use the operation token");
            return AutoSyncChecks.Observation.Forward(store,method,args);
        });
        var folder=ItemChecks.File(Side.Local,"folder",0,"folder",true);
        local.Seed(folder,[]);db.Seed(ItemChecks.File(Side.Dropbox,"folder",0,"peer-folder",true),[]);
        await AutoSync.PopulateAsync(pair,ItemChecks.Settings,_=>{},paths,new(folder.Path,folder.Path),Side.Local,new(folder,local.ReadAllAsync(folder.Path,new(true,false),ct)),local,db,observed,ct);
        using(var interrupted=CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            Guid active=default, cleared=default;
            var cancelled=AutoSyncChecks.Observation.Wrap<IStore>((method,args)=>{
                if(method.Name==nameof(IStore.BeginScan)){active=(Guid)args[1]!;interrupted.Cancel();}
                if(method.Name==nameof(IStore.ClearScanAsync)){cleared=(Guid)args[1]!; Check.Require((CancellationToken)args[^1]! ==interrupted.Token,"Cancelled cleanup waited without its operation token");}
                return AutoSyncChecks.Observation.Forward(store,method,args);
            });
            try {await AutoSync.PopulateAsync(pair,ItemChecks.Settings,_=>{},paths,new(folder.Path,folder.Path),Side.Local,new(folder,local.ReadAllAsync(folder.Path,new(true,false),interrupted.Token)),local,db,cancelled,interrupted.Token);}
            catch(OperationCanceledException) when(interrupted.IsCancellationRequested) { }
            Check.Require(active!=default && active==cleared,"Cancelled cleanup did not target its own scan");
        }
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,local,db,store,static()=>{},ct);
        db.ReadFailures[""]=new IOException("listing interrupted");
        try { await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,db,observed,null,null,ct,localRepaired:Wake); throw new InvalidDataException("Unread scan accepted"); }
        catch(IOException e) when (e.Message=="listing interrupted") { }
        Check.Require(!(await store.RepairStateAsync(pair.PairId,ct)).FirstRepairDone && (await store.ReadRepairAsync(pair.PairId,Side.Local,ct)).Reason=="requested","Incomplete scan consumed the obligation");
        Check.Require(wakes==0,"Incomplete Repair woke a journal reader");
        db.ReadFailures.Clear(); await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,db,observed,null,null,ct,localRepaired:Wake);
        Check.Require(wakes==1,"Committed full Repair did not wake its journal reader");
        Check.Require((await store.RepairStateAsync(pair.PairId,ct)).FirstRepairDone && (await store.ReadRepairAsync(pair.PairId,Side.Local,ct)).Reason==null,"Complete scan did not publish");
    });
    public static async Task HealthyDirtySetAsync()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try {Check.Require(await HealthyDirtySetAsync(4,timeout.Token)==await HealthyDirtySetAsync(512,timeout.Token),"Per-item observation work scaled with unchanged population");}
        catch(OperationCanceledException) when(timeout.IsCancellationRequested) {throw new InvalidDataException("Healthy dirty-set Repair did not settle its edit and cursor");}
    }

    private static async Task<long> HealthyDirtySetAsync(int size,CancellationToken ct)
    {
        var pair=ItemChecks.Pair(); pair=pair with {Roots=new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("a","r","h","d"))};
        using var store=new InMemoryStateStore(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        await store.SavePairAsync(pair,ct); await store.FinishRepairAsync(pair.PairId,(0,0),"{}",ct);
        for(var i=0;i<size;i++)
        {
            var path=$"unchanged-{i}"; var a=ItemChecks.File(Side.Local,path,1,$"a{i}"); var b=ItemChecks.File(Side.Dropbox,path,1,$"b{i}");
            pc.Seed(a,[1]); db.Seed(b,[1]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,b),null,null,ct);
        }
        const string volume="1"; var since=new JournalPosition(7,24); await store.SavePositionAsync(volume,since,ct); await store.SaveCursorAsync(pair.PairId,"current",ct);
        var edit=ItemChecks.File(Side.Dropbox,"unchanged-0",2,"b0"); db.Items[edit.Path]=(edit,[2]);
        var end=since with {NextUsn=since.NextUsn+1}; const string cursor="after-edit";
        pc.Pages.Enqueue(new([],null,end){ProjectedThrough=end}); db.Pages.Enqueue(new([new(Side.Dropbox,ChangeKind.Changed,edit.Path,null,edit.Id,edit,false,false)],cursor,null));
        var local=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>m.Name switch {
            nameof(ISide.BoundaryAsync)=>Task.FromResult(((string?)null,(JournalPosition?)end)),
            nameof(ISide.ReadByIdAsync)=>Locate((string)args[0]!, (CancellationToken)args[1]!),
            _=>AutoSyncChecks.Observation.Forward(pc,m,args)});
        async Task<(IdentityLocation Location,bool InScope)?> Locate(string id,CancellationToken token) =>
            await pc.ReadByIdAsync(id,token) is {} hit ? (hit.Location with {Domain=volume},hit.InScope) : null;
        var cloud=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.BoundaryAsync)?Task.FromResult(((string?)cursor,(JournalPosition?)null)):AutoSyncChecks.Observation.Forward(db,m,args));
        var queue=new WorkQueue(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,local,cloud,store,static()=>{},ct);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,cloud,store,null,null,ct);
        foreach(var side in new[]{pc,db}) Check.Require(side.Requests[nameof(ISide.ReadAllAsync)]==0 && side.Items.Count==size && side.Items[edit.Path].Bytes.SequenceEqual(new byte[]{2}),"Healthy Repair skipped the edit or rescanned the pair");
        Check.Require(await store.CursorAsync(pair.PairId,ct)=="after-edit","Repair did not consume its dirty input");
        // PairRunner already covered this interval. Boot Repair may reconcile notices but must not visit jobs or either source again.
        int duplicateReads=0;
        ISide Count(ISide side)=>AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>{
            if(m.Name is nameof(ISide.BoundaryAsync) or nameof(ISide.ReadChangesAsync)) Interlocked.Increment(ref duplicateReads);
            return AutoSyncChecks.Observation.Forward(side,m,args);
        });
        var memory=AutoSyncChecks.Observation.Wrap<IStore>((m,args)=>{
            if(m.Name==nameof(IStore.JobsAsync)) Interlocked.Increment(ref duplicateReads);
            return AutoSyncChecks.Observation.Forward(store,m,args);
        });
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,Count(pc),Count(db),memory,null,null,ct,caughtUp:true);
        Check.Require(duplicateReads==0,"Boot Repair reacquired a finite source or refreshed jobs already visited by the runner");
        return pc.Requests[nameof(ISide.ReadOneAsync)]+db.Requests[nameof(ISide.ReadOneAsync)];
    }

    public static async Task BilateralNoticeAsync()
    {
        await FilteredChildKeepsNoticeAsync();
        foreach(var (healthy,interrupt) in new[]{(false,true),(false,false),(true,false)}) await WithRepairStore(async (store,pair,ct)=>
        {
            pair=pair with {Roots=new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("a","r","h","d"))};
            var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
            await store.SavePairAsync(pair,ct);
            var queue=new WorkQueue(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
            if(healthy)
            {
                var steady=new JournalPosition(1,2); await store.FinishRepairAsync(pair.PairId,(0,0),"{}",ct);
                await store.SavePositionAsync("1",steady,ct); await store.SaveCursorAsync(pair.PairId,"steady",ct);
                pc.Pages.Enqueue(new([],null,steady)); db.Pages.Enqueue(new([],"steady",null));
            }
            var parent=ItemChecks.File(Side.Local,"local-parent",0,"pc-parent",true); var peer=ItemChecks.File(Side.Dropbox,"cloud-parent",0,"cloud-parent",true);
            pc.Seed(parent,[]); db.Seed(peer,[]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(parent,peer),null,null,ct);
            foreach(string path in new[]{"unread","missing","empty-subject","in-job"})
            {
                var members=path=="empty-subject" ? Array.Empty<NoticeItem>() : new[]{new NoticeItem(Side.Local,path,"previous")};
                await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,path,new(members,Side.Local),DateTimeOffset.UnixEpoch,false),ct);
            }
            if(!healthy)
            {
                pc.Seed(ItemChecks.File(Side.Local,"marked",0,"new-folder",true),[]);
                pc.Seed(ItemChecks.File(Side.Local,"marked/child",7,"new-child"),[7]);
                if(interrupt) pc.Seed(ItemChecks.File(Side.Local,"marked/later",8,"later-child"),[8]);
                db.DeletedHistory["marked"]=new DeletionEvidence.DeletedPath("marked","marked");
                foreach(var path in new[]{"marked","marked/child"}) await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,path,new([new(Side.Local,path,"former")],Side.Local),DateTimeOffset.UnixEpoch,false),ct);
                var bound=SyncedEntries.From(ItemChecks.File(Side.Local,"missing",1,"previous"),ItemChecks.File(Side.Dropbox,"missing",1,"peer"));
                await store.CommitAsync(pair.PairId,null,bound,null,null,ct);
            }
            var tie=new Notice(0,NoticeKind.Tie,TextKey.TieEqualTimes,"local-parent/live",new([new(Side.Local,"local-parent/live","previous"),new(Side.Dropbox,"gone","gone-id")],null),DateTimeOffset.UnixEpoch,false);
            await store.SaveNoticeAsync(pair.PairId,tie,ct);
            var open=new AcceptedJob(pair.PairId,"files/delete_batch/check","open",[new("accepted","in-job",null)]);
            await store.SaveJobAsync(open,ct); db.JobReplies[open.JobId]=new([new JobReply.Running(),new JobReply.Running()]);
            await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,static()=>{},ct);
            db.Seed(ItemChecks.File(Side.Dropbox,"cloud-parent/live",2,"current"),[2]);
            if(!interrupt)
            {
                db.Seed(ItemChecks.File(Side.Dropbox,"unread",2,"other"),[2]);
                db.ReadFailures["unread"]=new IOException("unreadable");
            }
            var interruption=new InvalidDataException("Interrupted second child binding");
            int jobReads=0;var observed=AutoSyncChecks.Observation.Wrap<IStore>((m,args)=>{
                if(interrupt && m.Name==nameof(IStore.CommitAsync) && args[2] is SyncedEntry {LocalPath:"marked/later"}) throw interruption;
                if(m.Name==nameof(IStore.JobsAsync)) Interlocked.Increment(ref jobReads);
                return AutoSyncChecks.Observation.Forward(store,m,args);
            });
            if(!healthy && !interrupt) await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",ct);
            try {await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,observed,null,null,ct);Check.Require(healthy || interrupt,"Incomplete inventory reported success");}
            catch(InventoryIncompleteException e) when(!healthy && !interrupt)
            {
                Check.Require(e.Message.Contains("unread",StringComparison.OrdinalIgnoreCase) && (await store.ReadRepairAsync(pair.PairId,Side.Dropbox,ct)).Reason!=null && db.Items["unread"].Bytes.SequenceEqual(new byte[]{2}),"Incomplete inventory lost the unread path, obligation or bytes");
            }
            catch(InvalidDataException e) when(ReferenceEquals(e,interruption))
            {
                Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("marked/child"),ct)!=null &&
                    db.Items.TryGetValue("marked/child",out var committed) && committed.Bytes.SequenceEqual(new byte[]{7}) &&
                    !await HasChildFailure() && await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("marked/later"),ct)==null,"Committed child kept its superseded failure after its sibling failed");
                return;
            }
            Check.Require(!interrupt,"Second child did not reach its binding");
            async Task<bool> HasChildFailure()
            {
                await foreach(var n in store.NoticesAtAsync(pair.PairId,PathNames.Key("marked/child"),ct)) if(n.Kind==NoticeKind.Failure) return true;
                return false;
            }
            Check.Require(jobReads==1,"Repair rescanned accepted jobs while reconciling notices");
            var openNotices=new List<Notice>(); await foreach(var notice in store.NoticesAsync(pair.PairId,ct)) openNotices.Add(notice);
            if(!healthy) Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("missing"),ct)==null && !openNotices.Any(n=>n.Path=="missing"),"Bound absent row kept its notice or binding");
            if(!healthy) Check.Require(db.Items.ContainsKey("marked") && db.Items.TryGetValue("marked/child",out var kept) && kept.Bytes.SequenceEqual(new byte[]{7}) && !openNotices.Any(n=>n.Path=="marked" || n.Path=="marked/child"),"Kept historical folder or covered child lost its ordinary notice disposition");
            Check.Require(openNotices.Any(n=>n.Kind==NoticeKind.Tie && n.Path==tie.Path) && openNotices.Any(n=>n.Path=="unread") && openNotices.Any(n=>n.Path=="in-job"),"Retired a mapped legacy tie, unread counterpart or accepted job");
            Check.Require(!openNotices.Any(n=>n.Path=="missing" || n.Path=="empty-subject"),"One unread notice prevented another bilaterally absent notice from closing");
            if(healthy) Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]+db.Requests[nameof(ISide.ReadAllAsync)]==0,"Notice-only repair listed the pair");
        });
    }

    private static Task FilteredChildKeepsNoticeAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var queue=new WorkQueue();await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,static()=>{},ct);
        // The caller owns both range folders; only the Local range contains the new child.
        db.Seed(ItemChecks.File(Side.Dropbox,"range",0,"range-peer",true),[]);
        var rangeFolder=ItemChecks.File(Side.Local,"range",0,"range-folder",true);pc.Seed(rangeFolder,[]);
        var folder=ItemChecks.File(Side.Local,"range/new",0,"folder",true);pc.Seed(folder,[]);
        var child=ItemChecks.File(Side.Local,"range/new/child",3,"current");pc.Seed(child,[3]);
        var binding=SyncedEntries.From(child with {Modified=DateTimeOffset.UnixEpoch},ItemChecks.File(Side.Dropbox,child.Path,3,"peer"));
        await store.CommitAsync(pair.PairId,null,binding,null,null,ct);
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,child.Path,new([new(Side.Local,child.Path,"former")],Side.Local),DateTimeOffset.UnixEpoch,false),ct);
        async Task<string> Hash(Entry entry)
        {
            Notice? expected=null;await foreach(var n in store.NoticesAtAsync(pair.PairId,binding.LocalKey,ct)) expected=n;
            Check.Require(expected!=null && await store.UpdateNoticeVisibilityAsync(pair.PairId,expected,true,ct),"No former notice to ignore during hash");
            return await pc.ContentHashAsync(entry,ct);
        }
        var local=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.ContentHashAsync) && args[0] is Entry e && e.Path==child.Path ? Hash(e) : AutoSyncChecks.Observation.Forward(pc,m,args));
        // Run inline under an existing comparison grant, so population precedes the outer child row.
        var range=new Scope("range","range");var owner=SyncedEntries.From(rangeFolder,ItemChecks.File(Side.Dropbox,"range",0,"range-peer",true));
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,db,store,range,(owner,range,range),ct);
        Notice? kept=null;await foreach(var n in store.NoticesAtAsync(pair.PairId,binding.LocalKey,ct)) kept=n;
        Check.Require(kept is {Ignored:true} && await store.FindAsync(pair.PairId,Side.Local,binding.LocalKey,ct)==binding && pc.Items.ContainsKey(child.Path) && !db.Items.ContainsKey(child.Path),"Filtered add-only child lost its ignored failure without applying an effect");
    });

    public static Task CoveredFolderAsync(bool keep) => AutoSyncChecks.WithinDeadline(async ct=>
    {
        var pair=ItemChecks.Pair(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        using var store=new InMemoryStateStore(); await store.SavePairAsync(pair,ct);
        pc.Seed(ItemChecks.File(Side.Local,"a",0,"parent",true),[]); pc.Seed(ItemChecks.File(Side.Local,"a/empty",0,"empty",true),[]);
        db.DeletedHistory["a"]=new DeletionEvidence.DeletedPath("a","a");
        if(keep) pc.Seed(ItemChecks.File(Side.Local,"a/content",3,"file"),[3]);
        var queue=new WorkQueue(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        // Count enumerations of the returned stream, not construction of the lazy listing (Effects builds unused ones).
        int localListings=0,remoteListings=0;
        static async IAsyncEnumerable<Listed> Started(IAsyncEnumerable<Listed> source,Action count)
        {
            count(); await foreach(var item in source) yield return item;
        }
        var idle = new TaskCompletionSource<ChangePage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ISide CountStarts(FakeSide side,Action started)=>AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>{
            if(m.Name==nameof(ISide.ReadAllAsync)) return Started((IAsyncEnumerable<Listed>)AutoSyncChecks.Observation.Forward(side,m,args)!,started);
            if(m.Name==nameof(ISide.ReadChangesAsync) && args.Length==3) return idle.Task.WaitAsync((CancellationToken)args[2]!);
            return AutoSyncChecks.Observation.Forward(side,m,args);
        });
        var local=CountStarts(pc,()=>Interlocked.Increment(ref localListings)); var remote=CountStarts(db,()=>Interlocked.Increment(ref remoteListings));
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var readerStop=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var end=new JournalPosition(1,1);
        if(!keep)
        {
            ItemChecks.Script(pc,new([],null,end){ProjectedThrough=end});
            ItemChecks.Script(db,new([],"cursor",null));
        }
        var reader=new PairRunner().CatchUpAsync(pair,keep?(null,null):(end,"cursor"),ItemChecks.Settings,_=>{},paths,queue,local,remote,store,()=>ready.TrySetResult(),readerStop.Token,continueLive:!keep);
        try
        {
            await await Task.WhenAny(ready.Task,reader);
            await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,remote,store,null,null,ct);
        }
        finally
        {
            readerStop.Cancel();
            try {await reader;} catch(OperationCanceledException) when(readerStop.IsCancellationRequested && !ct.IsCancellationRequested) { }
        }
        Check.Require(localListings==1 && remoteListings==1,"Covered child population listed its range again");
        Check.Require(db.Items.ContainsKey("a/empty")==keep,$"keep={keep}: db has a/empty={db.Items.ContainsKey("a/empty")} expected {keep}; db=[{string.Join(",",db.Items.Keys)}] pc=[{string.Join(",",pc.Items.Keys)}]");
        Check.Require(pc.Items.ContainsKey("a")==keep,$"keep={keep}: pc has a={pc.Items.ContainsKey("a")} expected {keep}; db=[{string.Join(",",db.Items.Keys)}] pc=[{string.Join(",",pc.Items.Keys)}]");
        if(keep) Check.Require(db.Items["a/content"].Bytes.SequenceEqual(new byte[]{3}),"Kept parent did not consume its acquired content");
        else Check.Require(db.Requests[nameof(ISide.EnsureFolderAsync)]==0,"Created a child before recycling the whole parent");
    });

    public static async Task OwnAttemptAsync()
    {
        var pair=ItemChecks.Pair(); using var store=new InMemoryStateStore(); await store.SavePairAsync(pair,default);
        var own=new Notice(0,NoticeKind.Failure,TextKey.FailLocalInUse,"item",new([new(Side.Local,"item","a")],Side.Local),DateTimeOffset.UnixEpoch,false);
        await store.SaveNoticeAsync(pair.PairId,own,default);
        Check.Require(await Repair.CurrentRetryAsync(store,pair.PairId,own,default) is {Id:>0},"Repair lost its own failed attempt");
        var other=own with {Subject=new([new(Side.Local,"item","b")],Side.Local),At=own.At.AddSeconds(1)};
        await store.SaveNoticeAsync(pair.PairId,other,default);
        Check.Require(await Repair.CurrentRetryAsync(store,pair.PairId,own,default)==null && (await Repair.CurrentRetryAsync(store,pair.PairId,other,default))?.At==other.At,"Repair retried a different failure at a reused path");
    }

    // A real store marks the active scan when the executor raises the failure. Both the retry and the sibling must publish.
    public static async Task RetainedAttemptAsync()
    {
        await ReaderCompletesRemovalAsync(true);
        await ReaderCompletesRemovalAsync(false);
        await RetainedNoticeAsync();
    }

    private static Task ReaderCompletesRemovalAsync(bool previouslyBound) => WithRepairStore(async (store,pair,ct)=>
    {
        const string path="formerly-deleted";
        var pc=new FakeSide(Side.Local);var cloud=new FakeSide(Side.Dropbox){ClockOffset=(TimeSpan.Zero,TimeSpan.Zero)};
        var paths=new PathScope(pair,[pair],DriveType.Fixed);var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,cloud,store);
        var entry=ItemChecks.File(Side.Local,path,1,"pc");pc.Seed(entry,[1]);
        pc.Seed(ItemChecks.File(Side.Local,"other",1,"pc-other"),[1]);cloud.Seed(ItemChecks.File(Side.Dropbox,"other",1,"cloud-other"),[1]);
        if(previouslyBound) await store.CommitAsync(pair.PairId,null,SyncedEntries.From(entry,ItemChecks.File(Side.Dropbox,path,1,"cloud")),null,null,ct);
        else cloud.DeletedHistory[path]=new DeletionEvidence.DeletedFile(path,"cloud","previous","r1",entry.Modified.AddDays(1),null,entry.ContentHash,null);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,cloud,store,null,null,ct);
        Check.Require(pc.Items.ContainsKey(path) && pc.History.Count==0,"Unproved Repair removal changed the selected file");
        var end=new JournalPosition(1,2);ItemChecks.Script(pc,new([],null,end){ProjectedThrough=end});ItemChecks.Script(cloud,new([],"after",null));
        await AutoSync.ReadThroughAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,cloud,store,(end,"after"),static()=>{},ct);
        Check.Require(!pc.Items.ContainsKey(path) && pc.History.Single().Entry==entry,"Reader proof did not complete the held Repair removal");
        Check.Require(!await queue.RetryHeldAsync(null,ct),"Completed Repair removal remained held");
    });

    // These cases exercise the children, not creation of a new root (which touches its subtree).
    private static async Task SeedRangeAsync(IStore store,PairConfig pair,FakeSide pc,FakeSide db,CancellationToken ct)
    {
        var local=ItemChecks.File(Side.Local,"range",0,"range-local",true);var remote=ItemChecks.File(Side.Dropbox,"range",0,"range-peer",true);
        pc.Seed(local,[]);db.Seed(remote,[]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(local,remote),null,null,ct);
    }

    private static Task RetainedNoticeAsync() => WithRepairStore(async (store,pair,ct)=>
    {
            var pc=new FakeSide(Side.Local);var dbSide=new FakeSide(Side.Dropbox);var queue=new WorkQueue();var paths=new PathScope(pair,[pair],DriveType.Fixed);
            await SeedRangeAsync(store,pair,pc,dbSide,ct);
            pc.Seed(ItemChecks.File(Side.Local,"range/a",1,"11"),[1]);pc.Seed(ItemChecks.File(Side.Local,"range/b",2,"12"),[2]);
            int tries=0;
            var remote=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.PublishAsync) && args[0] is IStaged {Path:"range/a"} && Interlocked.Increment(ref tries)==1 ?
                Task.FromException<RemovalResult>(new ItemFailedException("range/a",TextKey.FailLocalInUse,null)) : AutoSyncChecks.Observation.Forward(dbSide,m,args));
            await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,remote,store,static()=>{},ct);
            await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,remote,store,new("range","range"),null,ct);
            Check.Require(tries==2 && dbSide.Items.TryGetValue("range/a",out var a) && a.Bytes.SequenceEqual(new byte[]{1}) && dbSide.Items.TryGetValue("range/b",out var b) && b.Bytes.SequenceEqual(new byte[]{2}),"Repair treated its own failure-notice touch as settlement or blocked the independent sibling");
    });

    /// <summary>A write may have landed before the source error arrived. Its queued retry must compare, not publish twice.</summary>
    public static Task SelectedStreamRaceAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        const string item="range/a", sibling="range/b";
        var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var queue=new WorkQueue();var paths=new PathScope(pair,[pair],DriveType.Fixed);
        await SeedRangeAsync(store,pair,pc,db,ct);
        pc.Seed(ItemChecks.File(Side.Local,item,1,"11"),[1]);pc.Seed(ItemChecks.File(Side.Local,sibling,2,"12"),[2]);
        var neighborDone=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int published=0,uncertain=0,localReads=0,remoteReads=0;
        var local=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>{
            if(m.Name==nameof(ISide.ReadOneAsync) && (string?)args[0]==item && Volatile.Read(ref uncertain)!=0) Interlocked.Increment(ref localReads);
            return AutoSyncChecks.Observation.Forward(pc,m,args);
        });
        var remote=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>{
            if(m.Name==nameof(ISide.ReadOneAsync) && (string?)args[0]==item && Volatile.Read(ref uncertain)!=0) Interlocked.Increment(ref remoteReads);
            if(m.Name==nameof(ISide.PublishAsync) && args[0] is IStaged stage) return Publish(stage,(Entry?)args[1],(RemovalSelection?)args[2],(CancellationToken)args[3]!);
            return AutoSyncChecks.Observation.Forward(db,m,args);
        });
        async Task<RemovalResult> Publish(IStaged stage,Entry? expected,RemovalSelection? selection,CancellationToken token)
        {
            if(stage.Path==item && Interlocked.Increment(ref published)!=1) throw new InvalidDataException("Repair resent a direct publish after its result became uncertain");
            var result=await db.PublishAsync(stage,expected,selection,token);
            if(stage.Path==sibling) neighborDone.TrySetResult();
            if(stage.Path==item) {Volatile.Write(ref uncertain,1);await neighborDone.Task.WaitAsync(token);throw new ChangedUnderneathException(item){FailedSide=Side.Dropbox};}
            return result;
        }
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,local,remote,store,static()=>{},ct);
        try {await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,remote,store,new("range","range"),null,ct);}
        catch(ChangedUnderneathException e) when(e.Path==item) {throw new InvalidDataException("Relative source race escaped ordinary Repair instead of being retained");}
        var bound=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(item),ct);
        Check.Require(published==1 && localReads>0 && remoteReads>0 && neighborDone.Task.IsCompletedSuccessfully && bound==SyncedEntries.From(pc.Items[item].Entry,db.Items[item].Entry) && db.Items[item].Bytes.SequenceEqual(new byte[]{1}) && db.Items[sibling].Bytes.SequenceEqual(new byte[]{2}),"Repair settled a source race without a fresh bilateral decision or sibling progress");
    });

    public static Task ManualRootRecheckAsync() => RootRecheckAsync(owed:false,childFirst:false);
    public static Task OwnerlessRootAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var range=new Scope("range","range"); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var queue=new WorkQueue();
        bool compared=false;
        var observed=AutoSyncChecks.Observation.Wrap<IStore>((method,args)=>{
            if(method.Name==nameof(IStore.RenamesAsync) || method.Name==nameof(IStore.CompareAsync)) compared=true;
            return AutoSyncChecks.Observation.Forward(store,method,args);
        });
        Task Scan()=>Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,observed,range,(null!,range,null),ct);
        foreach(var side in new[]{pc,db})
        {
            side.Seed(ItemChecks.File(side.Side,"range",0,"folder",true) with {Skip=nameof(TextKey.SkipUnreadable)},[]);
            try {await Scan(); throw new InvalidDataException("Unreadable ownerless root was accepted");}
            catch(ItemFailedException e) when(e.Path=="range" && e.Cause==(side==pc?TextKey.ReadLocalFailed:TextKey.ReadDropboxFailed)) { }
            Check.Require(!compared,"Started comparing children before both roots were acquired");
            side.Items.Clear();
        }
        await Scan();
        Check.Require(compared && pc.Requests[nameof(ISide.ReadAllAsync)]==0 && db.Requests[nameof(ISide.ReadAllAsync)]==0,"Missing ownerless roots were not compared as empty");
    });
    public static Task OwedRootRecheckAsync() => RootRecheckAsync(owed:true,childFirst:false);
    public static Task ChildRootControlAsync() => RootRecheckAsync(owed:false,childFirst:true);

    // Use the real Host/Repair slot. Only the endpoint observations and log writer are synthetic.
    private static Task RootRecheckAsync(bool owed,bool childFirst) => WithRepairStore(async (store,pair,ct)=>
    {
        const string under="range", kept="range/kept", work="range/work";
        var range=new Scope(under,under);var roots=new RootIdentity(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,7,null),new("a","r","h","d"));
        pair=pair with {Roots=roots}; await store.SavePairAsync(pair,ct);
        var l=await store.ReadRepairAsync(pair.PairId,Side.Local,ct);var d=await store.ReadRepairAsync(pair.PairId,Side.Dropbox,ct);
        Check.Require(await store.FinishRepairAsync(pair.PairId,(l.Revision,d.Revision),"{}",ct),"Root fixture did not finish bootstrap");
        if(owed) await store.SavePairAsync(pair,ct,[(pair.PairId,range)]);
        var pc=new FakeSide(Side.Local){Root=(roots.Local,null,null,DriveType.Fixed)};var db=new FakeSide(Side.Dropbox){Root=(null,roots.Dropbox,null,null)};
        var localFile=ItemChecks.File(Side.Local,kept,9,"11");var peer=ItemChecks.File(Side.Dropbox,kept,9,"peer");
        if(owed) foreach(var side in new[]{pc,db}) side.Seed(ItemChecks.File(side.Side,under,0,under,true),[]);
        db.Seed(peer,[9]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(localFile,peer),null,null,ct);
        if(childFirst){pc.Seed(localFile,[9]);pc.Seed(ItemChecks.File(Side.Local,work,2,"12"),[2]);}
        int rootFaults=0,rootReads=0,childFaults=0,childRechecks=0;bool loseRoot=!childFirst;
        var disposition=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ChangePage> Quiet(CancellationToken t){await Task.Delay(Timeout.Infinite,t);throw new InvalidOperationException();}
        ISide Endpoint(FakeSide side)=>AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>{
            if(m.Name==nameof(ISide.ReadChangesAsync) && args.Length==3) return Quiet((CancellationToken)args[2]!);
            if(side==pc && m.Name==nameof(ISide.RootAsync))
            {
                if(Volatile.Read(ref rootFaults)!=0) Interlocked.Increment(ref rootReads);
                else if(Volatile.Read(ref childFaults)!=0 && !loseRoot) Interlocked.Increment(ref childRechecks);
            }
            if(side==pc && m.Name==nameof(ISide.ReadAllAsync) && loseRoot && (string?)args[0]==(owed?under:""))
            {
                Interlocked.Increment(ref rootFaults);pc.Root=(new(1,8,null),null,null,DriveType.Fixed);
                pc.ReadFailures[(string)args[0]!]=new ChangedUnderneathException(pair.LocalRoot);
            }
            if(side==pc && !loseRoot && m.Name==nameof(ISide.ReadOneAsync) && (string?)args[0]==work && Interlocked.CompareExchange(ref childFaults,1,0)==0)
                return Task.FromException<Entry?>(new ChangedUnderneathException(work));
            return AutoSyncChecks.Observation.Forward(side,m,args);
        });
        var observed=AutoSyncChecks.Observation.Wrap<IStore>((m,args)=>{
            if(m.Name==nameof(IDisposable.Dispose)) return null; // WithRepairStore owns this fixture.
            var result=AutoSyncChecks.Observation.Forward(store,m,args);
            if(m.Name==nameof(IStore.SaveNoticeAsync) && args[1] is Notice {Kind:NoticeKind.RootIdentity or NoticeKind.Failure} notice)
                return AfterNotice((Task)result!,notice);
            return result;
        });
        async Task AfterNotice(Task save,Notice notice)
        {
            await save;
            if(Volatile.Read(ref rootFaults)==0) disposition.TrySetException(new InvalidDataException($"Host reported {notice.Cause} before the selected Repair inventory"));
            else disposition.TrySetResult();
        }
        var logs=AutoSyncChecks.Observation.Wrap<ILogFiles>((m,_)=>m.Name==nameof(ILogFiles.Open)?TextWriter.Null:null);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(observed),(_,_,_,_)=>(Endpoint(pc),Endpoint(db)),ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        bool Preserved()=>db.Items.TryGetValue(kept,out var at) && at.Entry==peer && at.Bytes.SequenceEqual(new byte[]{9}) && !db.Mutations.Contains(peer.Id);
        Exception? observedManualError=null;
        try
        {
            if(childFirst)
            {
                bool promoted=false;
                try {await host.RepairAsync(pair.PairId,ct);} catch(ChangedUnderneathException e) when(PathNames.Same(e.Path,pair.LocalRoot)){observedManualError=e;promoted=true;}
                Check.Require(!promoted && childFaults==1 && childRechecks==0 && Preserved() && db.Items.TryGetValue(work,out var copied) && copied.Bytes.SequenceEqual(new byte[]{2}),"Ordinary child failure triggered a root recheck or failed to preserve the counterpart");
                loseRoot=true; // Positive control: this same pair must recheck a subsequent exact-root loss.
            }
            bool refused=false;
            if(owed)
            {
                await host.StartAsync(ct);
                try {await disposition.Task.WaitAsync(ct);}
                catch(OperationCanceledException e) when(ct.IsCancellationRequested) {throw new InvalidDataException("Owed Repair did not report its root disposition",e);}
            }
            else
            {
                try {await host.RepairAsync(pair.PairId,ct);}
                catch(ChangedUnderneathException e) when(PathNames.Same(e.Path,pair.LocalRoot)){observedManualError=e;refused=true;}
                catch(InventoryIncompleteException e) {observedManualError=e;} // A demoted root error is not a successful refusal.
            }
            Check.Require(rootFaults>0,"Repair did not visit the selected full or owed range");
            var issues=new List<Notice>();await foreach(var n in store.NoticesAsync(pair.PairId,ct)) issues.Add(n);
            bool rootNotice=issues.Count(n=>n.Kind==NoticeKind.RootIdentity && n.Cause==TextKey.RootLocalIdentityChanged)==1 && !issues.Any(n=>n.Kind==NoticeKind.Failure);
            bool outstanding=owed ? (await store.PairAsync(pair.PairId,ct))?.OwedScopes?.Contains(range)==true : (await store.ReadRepairAsync(pair.PairId,Side.Dropbox,ct)).Reason!=null;
            Check.Require(rootReads>0 && rootNotice && outstanding && (owed || refused) && Preserved(),owed ? "Owed scoped Repair did not recheck the pair and preserve its counterpart" : "Manual Repair did not recheck the pair and preserve its counterpart");
        }
        finally
        {
            try {await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));}
            catch(TimeoutException e) {throw new InvalidDataException("Root recheck host did not stop",e);}
            catch(Exception e) when(ReferenceEquals(e,observedManualError)) { } // Stop joins the same completed manual task.
        }
    });

    internal static async Task WithRepairStore(Func<IStore,PairConfig,CancellationToken,Task> run,[System.Runtime.CompilerServices.CallerMemberName] string check="")
    {
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct=budget.Token;
        var directory=Directory.CreateTempSubdirectory();
        try
        {
            var image=Path.Combine(directory.FullName,"fixture.sqlite");
            using(var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder {DataSource=image,Pooling=false}.ToString()))
            {
                await db.OpenAsync(ct);db.CreateFunction<string,string>("pathkey",PathNames.Key,isDeterministic:true);
                using var schema=db.CreateCommand();schema.CommandText=global::Massdex.SyncShark.Engine.Storage.Schema.Create+$"PRAGMA user_version={(global::Massdex.SyncShark.Engine.Storage.Schema.Version)};";await schema.ExecuteNonQueryAsync(ct);
            }
            using var store=new global::Massdex.SyncShark.Engine.Storage.Store(image,false,ItemChecks.Settings); var pair=ItemChecks.Pair();await store.SavePairAsync(pair,ct);
            try {await run(store,pair,ct);}
            catch(OperationCanceledException e) when(ct.IsCancellationRequested) {throw new InvalidDataException($"{check} did not finish before its deadline",e);}
        }
        finally {directory.Delete(true);}
    }

    /// <summary>A damaged saved history must be replaced by the first Repair, before it publishes completion.</summary>
    public static Task CorruptHistoryAsync() => AutoSyncChecks.WithinDeadline(async ct=>
    {
        var directory=Directory.CreateTempSubdirectory();
        try
        {
            var image=Path.Combine(directory.FullName,"fixture.sqlite");
            using var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder {DataSource=image,Pooling=false}.ToString());
            await db.OpenAsync(ct);db.CreateFunction<string,string>("pathkey",PathNames.Key,isDeterministic:true);
            using var seed=db.CreateCommand();seed.CommandText=global::Massdex.SyncShark.Engine.Storage.Schema.Create+$"PRAGMA user_version={(global::Massdex.SyncShark.Engine.Storage.Schema.Version)};";await seed.ExecuteNonQueryAsync(ct);
            using var store=new global::Massdex.SyncShark.Engine.Storage.Store(image,false,ItemChecks.Settings); var pair=ItemChecks.Pair();await store.SavePairAsync(pair,ct);
            string old=PathNames.Key("old");var bytes=global::Massdex.SyncShark.Engine.Storage.DeletedNamesCodec.EncodeDeletedNames([old]);bytes[^1]^=1;
            seed.CommandText="UPDATE pair SET deleted_history_cursor='previous',deleted_history_owed=0 WHERE pair_id=$id; INSERT INTO discovery_deleted_block SELECT id,$old,1,$bytes FROM pair WHERE pair_id=$id; INSERT INTO discovery_deleted_delta SELECT id,pathkey('append'),0 FROM pair WHERE pair_id=$id;";
            seed.Parameters.AddWithValue("$id",pair.PairId);seed.Parameters.AddWithValue("$old",old);seed.Parameters.AddWithValue("$bytes",bytes);await seed.ExecuteNonQueryAsync(ct);
            var pc=new FakeSide(Side.Local);var cloud=new FakeSide(Side.Dropbox);var paths=new PathScope(pair,[pair],DriveType.Fixed);var queue=new WorkQueue();
            cloud.DeletedHistory["fresh"]=new DeletionEvidence.DeletedPath("fresh","fresh");
            int listings=0;var remote=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>{
                if(m.Name==nameof(ISide.ReadAllAsync) && args[1] is ListOptions {IncludeDeleted:true}) listings++;
                return AutoSyncChecks.Observation.Forward(cloud,m,args);
            });
            var observed=AutoSyncChecks.Observation.Wrap<IStore>((m,args)=>m.Name==nameof(IStore.FinishRepairAsync) && args[1] is ValueTuple<long,long> revisions ? Finish(revisions,(string)args[2]!) : AutoSyncChecks.Observation.Forward(store,m,args));
            async Task<bool> Finish((long,long) revisions,string summary)
            {
                Check.Require(listings==1 && (await store.DeletedHistoryAsync(pair.PairId,ct))==("terminal",false),"First Repair reached completion without reacquiring corrupt history");
                return await store.FinishRepairAsync(pair.PairId,revisions,summary,ct);
            }
            await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,remote,observed,static()=>{},ct);
            await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,remote,observed,null,null,ct);
            Check.Require((await store.RepairStateAsync(pair.PairId,ct)).FirstRepairDone && await store.DeletedNameAsync(pair.PairId,null,PathNames.Key("fresh"),ct) && await store.DeletedNameAsync(pair.PairId,null,PathNames.Key("append"),ct) && !await store.DeletedNameAsync(pair.PairId,null,old,ct),"Reacquired history omitted the independent delta or retained the corrupt name");
            await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,remote,observed,null,null,ct);
            Check.Require(listings==1,"The next Repair acquired the same history again");
        }
        finally {directory.Delete(true);}
    });

    public static async Task InventoryPruningAsync()
    {
        foreach(bool history in new[]{false,true})
        {
            bool read=false; string? terminal=null;
            var side=new FakeSide(history?Side.Dropbox:Side.Local);
            side.Seed(ItemChecks.File(side.Side,"visible",1,"included"),[1]);
            IAsyncEnumerable<Listed> Read(string root,ListOptions options,CancellationToken ct)
            {
                Check.Require(!options.IncludeOutOfScope,"Ordinary Repair requested traversal into excluded folders");
                Check.Require(options.Recursive && options.IncludeDeleted==history,"Wrong Repair listing mode");
                read=true; return side.ReadAllAsync(root,options,ct);
            }
            var seen=new List<string>();
            await foreach(var item in Repair.ReadInventory(Read,"",_=>{},history?value=>terminal=value:null,default)) seen.Add(item.Path);
            Check.Require(read && seen.SequenceEqual(new[]{"visible"}) && terminal==(history?"terminal":null),"Repair skipped its included sibling or lost its history cursor");
        }
    }

    private static async Task<(PairConfig Pair,FakeSide Pc,FakeSide Db,WorkQueue Queue)> CoveredQueueAsync(IStore store,PairConfig pair,CancellationToken token)
    {
        pair=pair with {Roots=new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("a","r","h","d"))};
        await store.SavePairAsync(pair,token);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        pc.Pages.Enqueue(new([],null,new(7,31))); db.Pages.Enqueue(new([],"opaque",null));
        var queue=new WorkQueue(t=>store.PositionAsync("1",t));
        await queue.AdmitAsync(pair,new([],null,new(7,32)),(_,_)=>Task.CompletedTask,(position,ct)=>store.SavePositionAsync("1",position,ct),null,null,token);
        await queue.AdmitAsync(pair,new([],"opaque",null),(_,_)=>Task.CompletedTask,null,(cursor,ct)=>store.SaveCursorAsync(pair.PairId,cursor,ct),null,token);
        return (pair,pc,db,queue);
    }

    private static Task CoveredMeetingAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var (bound,pc,db,queue)=await CoveredQueueAsync(store,pair,ct);
        Check.Require(queue.CoversHop((new(7,31),"opaque")),"Meeting fixture did not establish bilateral coverage");
        var local=await store.ReadRepairAsync(bound.PairId,Side.Local,ct);var remote=await store.ReadRepairAsync(bound.PairId,Side.Dropbox,ct);
        Check.Require(await store.FinishRepairAsync(bound.PairId,(local.Revision,remote.Revision),"{}",ct),"Meeting fixture did not finish first Repair");
        var saved=SyncedEntries.From(ItemChecks.File(Side.Local,"m",1,"old"),ItemChecks.File(Side.Dropbox,"m",1,"peer"));
        await store.CommitAsync(bound.PairId,null,saved,null,null,ct);
        pc.Seed(ItemChecks.File(Side.Local,"m",0,"new",true),[]);db.Seed(ItemChecks.File(Side.Dropbox,"m",0,"peer",true),[]);
        pc.Seed(ItemChecks.File(Side.Local,"m/both",4,"left-version"),[4]);db.Seed(ItemChecks.File(Side.Dropbox,"m/both",5,"right-version"),[5]);
        pc.Seed(ItemChecks.File(Side.Local,"m/left",2,"left"),[2]);db.Seed(ItemChecks.File(Side.Dropbox,"m/right",3,"right"),[3]);
        var scope=new Scope("m","m");
        try {await Repair.RunAsync(bound,ItemChecks.Settings,_=>{},new PathScope(bound,[bound],DriveType.Fixed),queue,pc,db,store,scope,(saved,scope,null),ct);}
        catch(ChangedUnderneathException e) {throw new InvalidDataException("Meeting comparison processed the caller-owned root",e);}
        Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]!=0 && db.Requests[nameof(ISide.ReadAllAsync)]!=0,"Covered meeting folders skipped the descendant comparison");
        bool Linked(SyncedEntry x,byte value)=>pc.Items.TryGetValue(x.LocalPath,out var a) && a.Entry.Id==x.LocalId && a.Bytes.SequenceEqual(new[]{value}) && db.Items.TryGetValue(x.DropboxPath,out var b) && b.Entry.Id==x.DropboxId && b.Bytes.SequenceEqual(new[]{value});
        var left=await store.FindAsync(bound.PairId,Side.Local,PathNames.Key("m/both"),ct);var right=await store.FindAsync(bound.PairId,Side.Dropbox,PathNames.Key("m/both"),ct);
        Check.Require(left is {LocalId:"left-version"} && Linked(left,4) && right is {DropboxId:"right-version"} && Linked(right,5),"Meeting comparison did not keep and link both versions");
        Check.Require(pc.Items.TryGetValue("m/right",out var r) && r.Bytes.SequenceEqual(new byte[]{3}) && db.Items.TryGetValue("m/left",out var l) && l.Bytes.SequenceEqual(new byte[]{2}),"Meeting comparison lost a disjoint child");
        Check.Require(await store.FindAsync(bound.PairId,Side.Local,PathNames.Key("m"),ct)==saved,"Meeting comparison processed the caller-owned root");
    });

    /// <summary>Moving a folder carries existing identities without point-reading each descendant.</summary>
    public static async Task FolderMoveCostAsync()
    {
        Check.Require(await Measure(4,false)==await Measure(16,false),"Endpoint point reads grew with the number of carried descendants");
        await Measure(4,true);
        Check.Require(await Measure(4,false,Side.Dropbox)==await Measure(16,false,Side.Dropbox),"Endpoint point reads grew with the number of descendants moved on Dropbox");
        await Measure(4,true,Side.Dropbox);

        static async Task<(long Path,long Id)> Measure(int children,bool renameChild,Side source=Side.Local)
        {
            (long,long) reads=default;
            await WithRepairStore(async (store,pair,ct)=>
            {
                var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
                var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,db,store);
                async Task Bind(string path,bool folder)
                {
                    var left=ItemChecks.File(Side.Local,path,1,"pc-"+path,folder); var right=ItemChecks.File(Side.Dropbox,path,1,"db-"+path,folder);
                    pc.Seed(left,[1]); db.Seed(right,[1]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(left,right),null,null,ct);
                }
                await Bind("Old",true);
                for(int i=0;i<children;i++) await Bind($"Old/{i}",false);
                var origin=source==Side.Local?pc:db; var peer=source==Side.Local?db:pc;
                await origin.MoveAsync(origin.Items["Old"].Entry,"New",ct);
                // Dropbox guarantees display casing for the leaf only. Vary ancestor spelling on carried children.
                if(source==Side.Dropbox) for(int i=0;i<children;i++)
                {
                    var path=$"New/{i}"; var child=origin.Items[path];
                    origin.Items[path]=(child.Entry with {Path=$"NEW/{i}"},child.Bytes);
                }
                if(renameChild) await origin.MoveAsync(origin.Items["New/0"].Entry,"New/renamed",ct);
                await store.OweRepairAsync(pair.PairId,source,"continuity lost",ct);
                long Reads(string name) => pc.Requests[name]+db.Requests[name];
                var before=(Reads(nameof(ISide.ReadOneAsync)),Reads(nameof(ISide.ReadByIdAsync)));
                await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct);
                reads=(Reads(nameof(ISide.ReadOneAsync))-before.Item1,Reads(nameof(ISide.ReadByIdAsync))-before.Item2);
                Check.Require(peer.Items.TryGetValue("New",out var moved) && moved.Entry.Id==(source==Side.Local?"db-Old":"pc-Old"),"Repair did not preserve the moved folder's identity");
                Check.Require(!peer.Items.ContainsKey("Old"),"Repair left the folder at its old name");
                Check.Require(peer.Requests[nameof(ISide.MoveAsync)]==(renameChild?2:1),$"Expected {(renameChild?2:1)} moves on {peer.Side}, observed {peer.Requests[nameof(ISide.MoveAsync)]}");
                if(renameChild) Check.Require(!peer.Items.ContainsKey("New/0"),"The independently renamed child remained at its carried name");
                for(int i=0;i<children;i++)
                {
                    var path=renameChild && i==0?"New/renamed":$"New/{i}"; var binding=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(path),ct);
                    Check.Require(pc.Items.TryGetValue(path,out var left) && left.Entry.Id==$"pc-Old/{i}" && binding?.LocalPath==path && binding.LocalId==left.Entry.Id,"A moved child lost its local name, binding or original identity");
                    Check.Require(db.Items.TryGetValue(path,out var right) && right.Entry.Id==$"db-Old/{i}" && binding?.DropboxPath==path && binding.DropboxId==right.Entry.Id,"A moved child lost its Dropbox name, binding or original identity");
                }
            });
            return reads;
        }
    }

    public static Task NewChildOfRenamedFolderAsync() => WithRepairStore(async (s,p,ct)=>
    {
        var local=new FakeSide(Side.Local);var remote=new FakeSide(Side.Dropbox);var scope=new PathScope(p,[p],DriveType.Fixed);
        var work=await ItemChecks.StartQueueAsync(p,scope,local,remote,s);
        var folder=ItemChecks.File(Side.Local,"Old",0,"folder",true);var peer=ItemChecks.File(Side.Dropbox,"Old",0,"peer",true);
        local.Seed(folder,[]);remote.Seed(peer,[]);await s.CommitAsync(p.PairId,null,SyncedEntries.From(folder,peer),null,null,ct);
        await local.MoveAsync(folder,"New",ct);local.Seed(ItemChecks.File(Side.Local,"New/x",1,"child"),[1]);
        await s.OweRepairAsync(p.PairId,Side.Local,"journal lost",ct);
        await Repair.RunAsync(p,ItemChecks.Settings,_=>{},scope,work,local,remote,s,null,null,ct);
        var links=0;await foreach(var b in s.ByIdAsync(p.PairId,Side.Local,"child",ct)) if(b.DropboxPath=="New/x") links++;
        Check.Require(remote.Items.TryGetValue("New/x",out var x) && x.Bytes.SequenceEqual(new byte[]{1}) && links==1,"Full Repair missed the new child of a renamed folder");
    });

    public static Task CoveredHopAsync() => WithRepairStore(async (store,pair,token)=>
    {
        var (bound,pc,db,queue)=await CoveredQueueAsync(store,pair,token);pair=bound;
        var before=new Scope("old","old"); var after=new Scope("new","new");
        var binding=SyncedEntries.From(ItemChecks.File(Side.Local,"old",0,"pc",true),ItemChecks.File(Side.Dropbox,"old",0,"db",true));
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},new PathScope(pair,[pair],DriveType.Fixed),queue,pc,db,store,before,(binding,before,after),token);
        Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]==0 && db.Requests[nameof(ISide.ReadAllAsync)]==0,"Already settled physical hop listed again");
        Check.Require(!(await store.RepairStateAsync(pair.PairId,token)).FirstRepairDone,"Physical hop published a full Repair");
        // A covered no-stage retirement has already verified both endpoints absent. A real missing directory cannot be listed.
        ISide MissingDirectory(ISide side)=>AutoSyncChecks.Observation.Wrap<ISide>((method,args)=>method.Name==nameof(ISide.ReadAllAsync)
            ? throw new InvalidDataException("Settled removal replay listed the absent folder") : method.Name==nameof(ISide.ReadOneAsync)
                ? throw new InvalidDataException("Settled removal replay probed the absent folder") : AutoSyncChecks.Observation.Forward(side,method,args));
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},new PathScope(pair,[pair],DriveType.Fixed),queue,MissingDirectory(pc),MissingDirectory(db),store,before,(binding,before,before),token);
        await CoveredMeetingAsync();
        await AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},new PathScope(pair,[pair],DriveType.Fixed),queue,Side.Local,new([],null,new(7,32)){ContinuityLost=true},pc,db,store,token);
        Check.Require(!queue.CoversHop((new(7,31),"opaque")),"A same-journal reset kept the old bilateral coverage proof");
        var paths=new PathScope(pair,[pair],DriveType.Fixed);
        // A real parent Record through Effects owns the hop: it projects and relinks the saved parent itself.
        await store.CommitAsync(pair.PairId,null,binding,null,null,token);
        pc.Seed(ItemChecks.File(Side.Local,"old",0,"pc",true),[]); db.Seed(ItemChecks.File(Side.Dropbox,"old",0,"db",true),[]);
        var child=ItemChecks.File(Side.Local,"old/item",1,"carried"); var prior=ItemChecks.File(Side.Dropbox,"old/item",1,"remote-child");
        var priorBinding=SyncedEntries.From(child,prior); await store.CommitAsync(pair.PairId,null,priorBinding,null,null,token);
        pc.Seed(child,[1]); db.Seed(prior,[1]);
        await pc.MoveAsync(pc.Items["old"].Entry,"new",token); await db.MoveAsync(db.Items["old"].Entry,"new",token);
        var edited=ItemChecks.File(Side.Local,"new/item",2,child.Id); pc.Items["new/item"]=(edited,new byte[]{2});
        var occupant=ItemChecks.File(Side.Local,"old/item",9,"new-occupant"); pc.Seed(occupant,[9]);
        var oldDestination=ItemChecks.File(Side.Dropbox,"old/item",8,"replacement-at-destination"); db.Seed(oldDestination,[8]);
        var parentRow=new Row(binding.LocalKey,pc.Items["new"].Entry,db.Items["new"].Entry,binding,[],null,RowContext.Neutral with {CompleteInventory=true,Destination="new"});
        var recorded=await new global::Massdex.SyncShark.Engine.Executor.Effects().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,
            new global::Massdex.SyncShark.Engine.Planner.SyncAction(ActionKind.Record,parentRow),
            pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),token);
        Check.Require(recorded==global::Massdex.SyncShark.Engine.Executor.EffectOutcome.Applied && await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("new"),token) is {LocalPath:"new",DropboxPath:"new"},"The parent move did not settle at its final peer endpoints");
        Check.Require(db.Items.TryGetValue("new/item",out var propagated) && propagated.Bytes.SequenceEqual(new byte[]{2}),"The mapped changed child did not settle at its projected path");
        var settled=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(edited.Path),token);
        Check.Require(settled!=null && settled.LocalId==edited.Id && settled.DropboxPath=="new/item","The mapped child binding did not follow the hop");
        Check.Require(pc.Items[occupant.Path].Entry==occupant && !pc.Mutations.Contains(occupant.Id) && db.Items[oldDestination.Path].Entry==oldDestination && !db.Mutations.Contains(oldDestination.Id),"A guessed write touched a reused old name");
        Check.Require(pc.Items.Keys.Concat(db.Items.Keys).All(p=>!p.Contains(ItemChecks.Settings.CaseRenameSuffix,StringComparison.Ordinal)),"A write landed at the case temporary name");
        Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]!=0 && db.Requests[nameof(ISide.ReadAllAsync)]!=0,"An uncovered hop was treated as covered");
        // Unproved child: no live proof covers the hop, so no destructive leg may be guessed at either name.
        await WithRepairStore(async (store2,pair2,ct2)=>
        {
            var pc2=new FakeSide(Side.Local); var db2=new FakeSide(Side.Dropbox); var paths2=new PathScope(pair2,[pair2],DriveType.Fixed);
            var parent2=SyncedEntries.From(ItemChecks.File(Side.Local,"old",0,"pc",true),ItemChecks.File(Side.Dropbox,"old",0,"db",true));
            await store2.CommitAsync(pair2.PairId,null,parent2,null,null,ct2);
            var localChild=ItemChecks.File(Side.Local,"old/item",1,"carried"); var peerChild=ItemChecks.File(Side.Dropbox,"old/item",1,"remote-child");
            await store2.CommitAsync(pair2.PairId,null,SyncedEntries.From(localChild,peerChild),null,null,ct2);
            pc2.Seed(ItemChecks.File(Side.Local,"old",0,"pc",true),[]); db2.Seed(ItemChecks.File(Side.Dropbox,"old",0,"db",true),[]);
            pc2.Seed(localChild,[1]); db2.Seed(peerChild,[1]);
            await pc2.MoveAsync(pc2.Items["old"].Entry,"new",ct2); await db2.MoveAsync(db2.Items["old"].Entry,"new",ct2);
            db2.Items.Remove("new/item"); // the peer child is gone; deleting the local copy needs live proof
            var queue2=await ItemChecks.StartQueueAsync(pair2,paths2,pc2,db2,store2);
            var occupant2=ItemChecks.File(Side.Local,"old/item",9,"new-occupant"); pc2.Seed(occupant2,[9]);
            var destination2=ItemChecks.File(Side.Dropbox,"old/item",8,"replacement-at-destination"); db2.Seed(destination2,[8]);
            var row2=new Row(parent2.LocalKey,pc2.Items["new"].Entry,db2.Items["new"].Entry,parent2,[],null,RowContext.Neutral with {CompleteInventory=true,Destination="new"});
            var outcome2=await new global::Massdex.SyncShark.Engine.Executor.Effects().ApplyAsync(pair2,ItemChecks.Settings,_=>{},paths2,
                new global::Massdex.SyncShark.Engine.Planner.SyncAction(ActionKind.Record,row2),
                pc2,db2,store2,ItemChecks.Population(pair2,paths2,pc2,db2,store2),ItemChecks.Hop(pair2,paths2,queue2,pc2,db2,store2),ct2);
            Check.Require(outcome2==global::Massdex.SyncShark.Engine.Executor.EffectOutcome.Applied && await store2.FindAsync(pair2.PairId,Side.Local,PathNames.Key("new"),ct2) is {LocalPath:"new",DropboxPath:"new"},"The unproved hop did not settle its parent");
            Check.Require(pc2.Items.ContainsKey("new/item") && pc2.Items["new/item"].Bytes.SequenceEqual(new byte[]{1}) && !db2.Items.ContainsKey("new/item"),"An unproved child was guessed at the projected or old name");
            Check.Require(pc2.Items["old/item"].Entry==occupant2 && db2.Items["old/item"].Entry==destination2,"A guessed write touched a reused old name");
            // The ordinary observation then arrives at the projected path with its own live proof and settles there.
            pc2.IdleBoundary=new ChangePage([],null,new(1,42)){ProjectedThrough=new(1,42)};
            db2.IdleBoundary=new ChangePage([],"complete",null);
            var tombstone=new Change(Side.Dropbox,ChangeKind.Deleted,"new/item",null,peerChild.Id,null,false,false);
            await queue2.AdmitAsync(pair2,new ChangePage([tombstone],"after-projection",null),(change,token)=>ItemSync.ApplyAsync(
                pair2,ItemChecks.Settings,_=>{},paths2,change,pc2,db2,store2,ItemChecks.Population(pair2,paths2,pc2,db2,store2),
                ItemChecks.Hop(pair2,paths2,queue2,pc2,db2,store2),token,queue2.Held,(_,_)=>throw new WorkQueue.Unsettled(),coversThrough:_=>true),
                null,(cursor,token)=>store2.SaveCursorAsync(pair2.PairId,cursor,token),null,ct2);
            Check.Require(await store2.CursorAsync(pair2.PairId,ct2)=="after-projection" && !pc2.Items.ContainsKey("new/item") && pc2.History.Any(x=>x.Entry.Id==localChild.Id),"The later child observation did not settle at the projected path");
            Check.Require(await store2.FindAsync(pair2.PairId,Side.Local,PathNames.Key("new/item"),ct2)==null,"The deleted child binding survived its projected observation");
        });
    });

    // A walk reads the file itself, so an edit made through an out-of-pair hard link reaches Dropbox.
    private static async Task HardLinkEditThroughOutsideNameAsync(CancellationToken ct)
    {
        var home=Directory.CreateTempSubdirectory();
        try
        {
            var root=Directory.CreateDirectory(Path.Combine(home.FullName,"root")).FullName;
            var outside=Directory.CreateDirectory(Path.Combine(home.FullName,"outside")).FullName;
            var a=Path.Combine(outside,"a"); var b=Path.Combine(root,"b");
            File.WriteAllText(a,"v1");
            if(!CreateHardLinkW(b,a,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            var (pair,paths,volume)=RealPair(root);
            using var store=new InMemoryStateStore();
            await store.SavePairAsync(pair,ct);
            await store.FinishRepairAsync(pair.PairId,(0,0),"{}",ct);
            await store.SavePositionAsync(volume.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),new JournalPosition(1,1),ct);
            var local=global::Massdex.SyncShark.Engine.Local.LocalSide.Factory()(pair,()=>paths,ItemChecks.Settings,store);
            var dropbox=new FakeSide(Side.Dropbox);
            var localB=(await local.ReadOneAsync("b",ct))!;
            var oldBytes=File.ReadAllBytes(b);
            var peerB=DropboxPeer(localB,"db-b",oldBytes);
            dropbox.Seed(peerB,oldBytes);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(localB,peerB),null,null,ct);
            using(var stream=new FileStream(a,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)) stream.Write("abc"u8);
            var current=new FileInfo(a).Length;
            // Confirm the cached row is still stale by enumeration only; do not open b before the walk observes it.
            if(new DirectoryInfo(root).EnumerateFiles("b").Single().Length==current) throw new NotImplementedException("b's cached row already reflects the edit");
            var queue=await BoundQueueAsync(pair,paths,local,dropbox,store,ct);
            await store.OweRepairAsync(pair.PairId,Side.Local,"journal lost",ct);
            await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,dropbox,store,null,null,ct);
            var expected=File.ReadAllBytes(b);
            Check.Require(dropbox.Items.TryGetValue("b",out var published) && published.Bytes.SequenceEqual(expected),"the out-of-pair edit was not uploaded to Dropbox");
            var binding=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("b"),ct);
            Check.Require(binding is { LocalSize:5 } && store.OpenNotices.Count==0,"the binding was not updated or a notice was raised");
        }
        finally { home.Delete(true); }
    }

    // A denied file keeps exactly its listing values and adds no failure; once readable the edit is seen.
    private static async Task UnreadableLinkedFileBehavesAsBaseAsync(CancellationToken ct)
    {
        var home=Directory.CreateTempSubdirectory();
        try
        {
            var root=Directory.CreateDirectory(Path.Combine(home.FullName,"root")).FullName;
            var outside=Directory.CreateDirectory(Path.Combine(home.FullName,"outside")).FullName;
            var a=Path.Combine(outside,"a"); var b=Path.Combine(root,"b"); var c=Path.Combine(root,"c"); var d=Path.Combine(root,"d");
            File.WriteAllText(a,"v1");
            if(!CreateHardLinkW(b,a,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            File.WriteAllText(c,"c1"); File.WriteAllText(d,"d1");
            var (pair,paths,volume)=RealPair(root);
            using var store=new InMemoryStateStore();
            await store.SavePairAsync(pair,ct);
            await store.FinishRepairAsync(pair.PairId,(0,0),"{}",ct);
            await store.SavePositionAsync(volume.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),new JournalPosition(1,1),ct);
            var local=global::Massdex.SyncShark.Engine.Local.LocalSide.Factory()(pair,()=>paths,ItemChecks.Settings,store);
            var dropbox=new FakeSide(Side.Dropbox);
            var seeded=new Dictionary<string,byte[]>();
            foreach(var name in new[]{"b","c","d"})
            {
                var le=(await local.ReadOneAsync(name,ct))!;
                var bytes=File.ReadAllBytes(Path.Combine(root,name)); seeded[name]=bytes;
                var peer=DropboxPeer(le,"db-"+name,bytes); dropbox.Seed(peer,bytes);
                await store.CommitAsync(pair.PairId,null,SyncedEntries.From(le,peer),null,null,ct);
            }
            var originalB=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("b"),ct);
            var oldSize=originalB!.LocalSize;
            using(var sb=new FileStream(a,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)) sb.Write("abc"u8);
            using(var sc=new FileStream(c,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)) sc.Write("xy"u8);
            var current=new FileInfo(a).Length;
            // The deny ACE lives on the shared file; apply and restore it through the outside link so the listed
            // name's cached row is never opened and stays stale.
            var originalBAcl=AclFixture.Deny(a,AclFixture.MetadataAccess);
            var originalDAcl=AclFixture.Deny(d,AclFixture.MetadataAccess);
            int rows=0;
            var counted=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=> m.Name==nameof(ISide.ReadAllAsync)
                ? Counted((IAsyncEnumerable<Listed>)AutoSyncChecks.Observation.Forward(local,m,args)!,()=>Interlocked.Increment(ref rows))
                : AutoSyncChecks.Observation.Forward(local,m,args));
            var stale=false;
            try
            {
                // The deny fixture must block the same metadata open the product makes, backup intent included.
                if(!AclFixture.Denied(b,backup:true) || !AclFixture.Denied(d,backup:true)) throw new NotImplementedException("the deny fixture did not block the product's metadata open");
                // Confirm the stale pre-edit size is still present under the denial, by enumeration only.
                var rawB=new DirectoryInfo(root).EnumerateFiles("b").Single().Length;
                stale = rawB==oldSize && rawB!=current;
                var oneB=global::Massdex.SyncShark.Engine.Local.DirectoryListing.One(root,"b");
                Check.Require(oneB is {Skip:null} && oneB.Size==rawB,"a denied file did not fall back to its listing values");
                var queue=await BoundQueueAsync(pair,paths,local,dropbox,store,ct);
                await store.OweRepairAsync(pair.PairId,Side.Local,"journal lost",ct);
                await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,counted,dropbox,store,null,null,ct);
                Check.Require(rows>0,"the forced Repair did not enumerate its walk");
                Check.Require(dropbox.Items.TryGetValue("c",out var publishedC) && publishedC.Bytes.SequenceEqual(File.ReadAllBytes(c)),"the normally edited sibling was not uploaded");
                Check.Require(dropbox.Items["b"].Bytes.SequenceEqual(seeded["b"]),"a denied unreadable file changed its Dropbox bytes");
                var stillB=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("b"),ct);
                Check.Require(stillB!=null && originalB!=null && stillB.LocalId==originalB.LocalId && stillB.DropboxId==originalB.DropboxId && stillB.LocalSize==originalB.LocalSize,"the denied file's binding changed");
                if(stale) Check.Require(store.OpenNotices.Count==0,"a denied unchanged file raised a notice");
                Check.Require((await store.ReadRepairAsync(pair.PairId,Side.Local,ct)).Reason==null,"the Repair debt was not cleared");
            }
            finally { AclFixture.Restore(a,originalBAcl); AclFixture.Restore(d,originalDAcl); }
            // With the DACL restored the same file is readable; a fresh full walk must now see the edit. The walk is the
            // positive control, so nothing opens b between the restore and the observation.
            var beforeRows=rows;
            var recovery=await BoundQueueAsync(pair,paths,local,dropbox,store,ct);
            await store.OweRepairAsync(pair.PairId,Side.Local,"journal lost",ct);
            await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,recovery,counted,dropbox,store,null,null,ct);
            Check.Require(rows>beforeRows,"the recovery Repair did not run another full walk");
            Check.Require(dropbox.Items.TryGetValue("b",out var publishedB) && publishedB.Bytes.SequenceEqual(File.ReadAllBytes(b)),"the edit was not seen once the file became readable");
            if(!stale) throw new NotImplementedException("b's cached row was not the stale pre-edit size under the denial");
        }
        finally { home.Delete(true); }
    }

    // The sole in-scope alias of a hard-linked file retried after a failure uploads the current edit, not a stale revision.
    private static async Task RetryHardLinkMemberAsync(CancellationToken ct)
    {
        var home=Directory.CreateTempSubdirectory();
        try
        {
            var root=Directory.CreateDirectory(Path.Combine(home.FullName,"root")).FullName;
            var outside=Directory.CreateDirectory(Path.Combine(home.FullName,"outside")).FullName;
            var a=Path.Combine(outside,"a"); var b=Path.Combine(root,"b");
            File.WriteAllText(a,"v1");
            if(!CreateHardLinkW(b,a,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            var (pair,paths,volume)=RealPair(root);
            using var store=new InMemoryStateStore();
            await store.SavePairAsync(pair,ct);
            await store.FinishRepairAsync(pair.PairId,(0,0),"{}",ct);
            await store.SavePositionAsync(volume.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),new JournalPosition(1,1),ct);
            var local=global::Massdex.SyncShark.Engine.Local.LocalSide.Factory()(pair,()=>paths,ItemChecks.Settings,store);
            var dropbox=new FakeSide(Side.Dropbox);
            var localB=(await local.ReadOneAsync("b",ct))!;
            var oldBytes=File.ReadAllBytes(b);
            var peerB=DropboxPeer(localB,"db-b",oldBytes);
            dropbox.Seed(peerB,oldBytes);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(localB,peerB),null,null,ct);
            using(var stream=new FileStream(a,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)) stream.Write("abc"u8);
            await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.ReadLocalFailed,"b",
                new(new NoticeItem[]{new(Side.Local,"b",localB.Id),new(Side.Dropbox,"b",peerB.Id)},Side.Local),DateTimeOffset.UtcNow,false),ct);
            var failure=store.OpenNotices.Single();
            var queue=new WorkQueue();
            var owner=new Unfinished();
            await owner.CheckAsync(pair,ItemChecks.Settings,_=>{},paths,null,local,dropbox,store,_=>[],
                ItemChecks.Population(pair,paths,local,dropbox,store),ItemChecks.Hop(pair,paths,queue,local,dropbox,store),ct);
            await new Retries(owner).RetryAsync(pair,ItemChecks.Settings,_=>{},paths,failure,local,dropbox,store,_=>[],
                ItemChecks.Population(pair,paths,local,dropbox,store),ItemChecks.Hop(pair,paths,queue,local,dropbox,store),ct);
            Check.Require(dropbox.Items.TryGetValue("b",out var published) && published.Bytes.SequenceEqual(File.ReadAllBytes(b)),"Retry did not upload the edit through the sole in-scope alias");
            var binding=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("b"),ct);
            Check.Require(binding is { LocalSize:5 } && store.OpenNotices.Count==0,"Retry did not update the binding or close the notice");
        }
        finally { home.Delete(true); }
    }

    private static (PairConfig Pair, PathScope Paths, DirectoryIdentity Volume) RealPair(string root)
    {
        var (volume,drive)=global::Massdex.SyncShark.Engine.Local.DriveIdentity.Read(root);
        var pair=ItemChecks.Pair() with { LocalRoot=root, Roots=new(root,"/Pair A","test",volume,new("account","root","home","folder")) };
        return (pair,new PathScope(pair,[pair],drive),volume);
    }
    private static Entry DropboxPeer(Entry local,string id,byte[] bytes)
    {
        using var hash=new ContentHash(); hash.Append(bytes);
        return local with { Id=id, Revision="r1", ContentHash=hash.Finish() };
    }
    private static async IAsyncEnumerable<Listed> Counted(IAsyncEnumerable<Listed> source,Action seen)
    {
        await foreach(var item in source) { seen(); yield return item; }
    }
    // Bind a Repair queue to its job owner the way the runner does, so a full Repair does not run with a null owner.
    private static async Task<WorkQueue> BoundQueueAsync(PairConfig pair,PathScope paths,ISide local,ISide dropbox,IStore store,CancellationToken ct)
    {
        var queue=new WorkQueue();
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,local,dropbox,store,static()=>{},ct);
        Check.Require(queue.Unfinished!=null,"Repair queue was not bound to a job owner");
        return queue;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
    private static extern bool CreateHardLinkW(string newName,string existing,IntPtr security);
}
