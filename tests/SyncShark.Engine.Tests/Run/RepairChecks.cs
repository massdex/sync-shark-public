using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static class RepairChecks
{
    public static async Task RunAsync()
    {
        await InventoryPruningAsync(); await OwnAttemptAsync();
        await HealthyDirtySetAsync();
        await CoveredHopAsync();
        await BilateralNoticeAsync(); await RecoveryChecks.ClosedNoticeAsync(true); await RecoveryChecks.PutBackSurvivesRepairAsync(true);
        await RetainedAttemptAsync();
        await CoveredFolderAsync(true); await CoveredFolderAsync(false);
        var pair=ItemChecks.Pair(); var local=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        using var store=new InMemoryStateStore(); await store.SavePairAsync(pair,default);
        await store.OweRepairAsync(pair.PairId,Side.Local,"requested",default);
        var paths=new PathScope(pair,[pair],DriveType.Fixed); var queue=new WorkQueue();
        db.ReadFailures[""]=new IOException("listing interrupted");
        try { await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,db,store,null,null,default); throw new InvalidDataException("Unread scan accepted"); }
        catch(IOException e) when (e.Message=="listing interrupted") { }
        Check.Require(!(await store.RepairStateAsync(pair.PairId,default)).FirstRepairDone && (await store.ReadRepairAsync(pair.PairId,Side.Local,default)).Reason=="requested","Incomplete scan consumed the obligation");
        db.ReadFailures.Clear(); await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,db,store,null,null,default);
        Check.Require((await store.RepairStateAsync(pair.PairId,default)).FirstRepairDone && (await store.ReadRepairAsync(pair.PairId,Side.Local,default)).Reason==null,"Complete scan did not publish");
    }
    public static async Task HealthyDirtySetAsync()
    {
        Check.Require(await HealthyDirtySetAsync(4)==await HealthyDirtySetAsync(512),"Per-item observation work scaled with unchanged population");
    }

    private static async Task<long> HealthyDirtySetAsync(int size)
    {
        var pair=ItemChecks.Pair(); pair=pair with {Roots=new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("a","r","h","d"))};
        using var store=new InMemoryStateStore(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        await store.SavePairAsync(pair,default); await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        for(var i=0;i<size;i++)
        {
            var path=$"unchanged-{i}"; var a=ItemChecks.File(Side.Local,path,1,$"a{i}"); var b=ItemChecks.File(Side.Dropbox,path,1,$"b{i}");
            pc.Seed(a,[1]); db.Seed(b,[1]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,b),null,null,default);
        }
        var since=new JournalPosition(7,24); await store.SavePositionAsync("1",since,default); await store.SaveCursorAsync(pair.PairId,"current",default);
        var edit=ItemChecks.File(Side.Dropbox,"unchanged-0",2,"b0"); db.Items[edit.Path]=(edit,[2]);
        pc.Pages.Enqueue(new([],null,since)); db.Pages.Enqueue(new([new(Side.Dropbox,ChangeKind.Changed,edit.Path,null,edit.Id,edit,false,false)],"after-edit",null));
        var queue=new WorkQueue(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,static()=>{},default);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,default);
        foreach(var side in new[]{pc,db}) Check.Require(side.Requests[nameof(ISide.ReadAllAsync)]==0 && side.Items.Count==size && side.Items[edit.Path].Bytes.SequenceEqual(new byte[]{2}),"Healthy Repair skipped the edit or rescanned the pair");
        Check.Require(await store.CursorAsync(pair.PairId,default)=="after-edit","Repair did not consume its dirty input");
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
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,Count(pc),Count(db),memory,null,null,default,caughtUp:true);
        Check.Require(duplicateReads==0,"Boot Repair reacquired a finite source or refreshed jobs already visited by the runner");
        return pc.Requests[nameof(ISide.ReadOneAsync)]+db.Requests[nameof(ISide.ReadOneAsync)];
    }

    public static async Task BilateralNoticeAsync()
    {
        foreach(bool healthy in new[]{false,true})
        {
            var pair=ItemChecks.Pair(); pair=pair with {Roots=new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("a","r","h","d"))};
            using var store=new InMemoryStateStore(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
            await store.SavePairAsync(pair,default);
            var queue=new WorkQueue(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
            if(healthy)
            {
                var steady=new JournalPosition(1,2); await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
                await store.SavePositionAsync("1",steady,default); await store.SaveCursorAsync(pair.PairId,"steady",default);
                pc.Pages.Enqueue(new([],null,steady)); db.Pages.Enqueue(new([],"steady",null));
            }
            var parent=ItemChecks.File(Side.Local,"local-parent",0,"pc-parent",true); var peer=ItemChecks.File(Side.Dropbox,"cloud-parent",0,"cloud-parent",true);
            pc.Seed(parent,[]); db.Seed(peer,[]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(parent,peer),null,null,default);
            foreach(string path in new[]{"unread","missing","empty-subject","in-job"})
            {
                var members=path=="empty-subject" ? Array.Empty<NoticeItem>() : new[]{new NoticeItem(Side.Local,path,"previous")};
                await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,path,new(members,Side.Local),DateTimeOffset.UnixEpoch,false),default);
            }
            var tie=new Notice(0,NoticeKind.Tie,TextKey.TieEqualTimes,"local-parent/live",new([new(Side.Local,"local-parent/live","previous"),new(Side.Dropbox,"gone","gone-id")],null),DateTimeOffset.UnixEpoch,false);
            await store.SaveNoticeAsync(pair.PairId,tie,default);
            var open=new AcceptedJob(pair.PairId,"files/delete_batch/check","open",[new("accepted","in-job",null)]);
            await store.SaveJobAsync(open,default); db.JobReplies[open.JobId]=new([new JobReply.Running(),new JobReply.Running()]);
            await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,static()=>{},default);
            db.Seed(ItemChecks.File(Side.Dropbox,"cloud-parent/live",2,"current"),[2]); db.Seed(ItemChecks.File(Side.Dropbox,"unread",2,"other"),[2]);
            db.ReadFailures["unread"]=new IOException("unreadable");
            try {await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,default);} catch(InventoryIncompleteException) when(!healthy) { }
            Check.Require(store.OpenNotices.Any(n=>n.Kind==NoticeKind.Tie && n.Path==tie.Path) && store.OpenNotices.Any(n=>n.Path=="unread") && store.OpenNotices.Any(n=>n.Path=="in-job"),"Retired a mapped legacy tie, unread counterpart or accepted job");
            Check.Require(!store.OpenNotices.Any(n=>n.Path=="missing" || n.Path=="empty-subject"),"One unread notice prevented another bilaterally absent notice from closing");
            if(healthy) Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]+db.Requests[nameof(ISide.ReadAllAsync)]==0,"Notice-only repair listed the pair");
        }
    }

    public static async Task CoveredFolderAsync(bool keep)
    {
        var pair=ItemChecks.Pair(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        using var store=new InMemoryStateStore(); await store.SavePairAsync(pair,default);
        pc.Seed(ItemChecks.File(Side.Local,"a",0,"parent",true),[]); pc.Seed(ItemChecks.File(Side.Local,"a/empty",0,"empty",true),[]);
        db.DeletedHistory["a"]=new DeletionEvidence.DeletedPath("a","a");
        if(keep) pc.Seed(ItemChecks.File(Side.Local,"a/content",3,"file"),[3]);
        var queue=new WorkQueue(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,static()=>{},default);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,default);
        Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]==1 && db.Requests[nameof(ISide.ReadAllAsync)]==1,"Covered child population listed its range again");
        Check.Require(db.Items.ContainsKey("a/empty")==keep && pc.Items.ContainsKey("a")==keep,"Suppressed child created under recycled parent or lost under kept parent");
        if(keep) Check.Require(db.Items["a/content"].Bytes.SequenceEqual(new byte[]{3}),"Kept parent did not consume its acquired content");
        else Check.Require(db.Requests[nameof(ISide.EnsureFolderAsync)]==0,"Created a child before recycling the whole parent");
    }

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
            var pc=new FakeSide(Side.Local);var dbSide=new FakeSide(Side.Dropbox);var queue=new WorkQueue();var paths=new PathScope(pair,[pair],DriveType.Fixed);
            pc.Seed(ItemChecks.File(Side.Local,"range/a",1,"11"),[1]);pc.Seed(ItemChecks.File(Side.Local,"range/b",2,"12"),[2]);
            int tries=0;
            var remote=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.PublishAsync) && args[0] is IStaged {Path:"range/a"} && Interlocked.Increment(ref tries)==1 ?
                Task.FromException<RemovalResult>(new ItemFailedException("range/a",TextKey.FailLocalInUse,null)) : AutoSyncChecks.Observation.Forward(dbSide,m,args));
            await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,remote,store,static()=>{},ct);
            await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,remote,store,new("range","range"),null,ct);
            Check.Require(tries==2 && dbSide.Items.TryGetValue("range/a",out var a) && a.Bytes.SequenceEqual(new byte[]{1}) && dbSide.Items.TryGetValue("range/b",out var b) && b.Bytes.SequenceEqual(new byte[]{2}),"Repair treated its own failure-notice touch as settlement or blocked the independent sibling");
        }
        finally {directory.Delete(true);}
    }

    /// <summary>A damaged saved history must be replaced by the first Repair, before it publishes completion.</summary>
    public static async Task CorruptHistoryAsync()
    {
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct=budget.Token;
        var directory=Directory.CreateTempSubdirectory();
        try
        {
            var image=Path.Combine(directory.FullName,"fixture.sqlite");
            using var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder {DataSource=image,Pooling=false}.ToString());
            await db.OpenAsync(ct);db.CreateFunction<string,string>("pathkey",PathNames.Key,isDeterministic:true);
            using var seed=db.CreateCommand();seed.CommandText=global::Massdex.SyncShark.Engine.Storage.Schema.Create+$"PRAGMA user_version={(global::Massdex.SyncShark.Engine.Storage.Schema.Version)};";await seed.ExecuteNonQueryAsync(ct);
            using var store=new global::Massdex.SyncShark.Engine.Storage.Store(image,false,ItemChecks.Settings); var pair=ItemChecks.Pair();await store.SavePairAsync(pair,ct);
            string old=PathNames.Key("old");var bytes=global::Massdex.SyncShark.Engine.Storage.Encoding.EncodeDeletedNames([old]);bytes[^1]^=1;
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
    }

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

    public static async Task CoveredHopAsync()
    {
        var pair=ItemChecks.Pair(); pair=pair with {Roots=new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("a","r","h","d"))};
        using var store=new InMemoryStateStore(); await store.SavePairAsync(pair,default);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        pc.Pages.Enqueue(new([],null,new(7,31))); db.Pages.Enqueue(new([],"opaque",null));
        var queue=new WorkQueue(t=>store.PositionAsync("1",t));
        await queue.AdmitAsync(pair,new([],null,new(7,32)),(_,_)=>Task.CompletedTask,(position,ct)=>store.SavePositionAsync("1",position,ct),null,null,default);
        await queue.AdmitAsync(pair,new([],"opaque",null),(_,_)=>Task.CompletedTask,null,(cursor,ct)=>store.SaveCursorAsync(pair.PairId,cursor,ct),null,default);
        var before=new Scope("old","old"); var after=new Scope("new","new");
        var binding=SyncedEntries.From(ItemChecks.File(Side.Local,"old",0,"pc",true),ItemChecks.File(Side.Dropbox,"old",0,"db",true));
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},new PathScope(pair,[pair],DriveType.Fixed),queue,pc,db,store,before,(binding,before,after),default);
        Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]==0 && db.Requests[nameof(ISide.ReadAllAsync)]==0,"Already settled physical hop listed again");
        Check.Require(!(await store.RepairStateAsync(pair.PairId,default)).FirstRepairDone,"Physical hop published a full Repair");
        await AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},new PathScope(pair,[pair],DriveType.Fixed),queue,Side.Local,new([],null,new(7,32)){ContinuityLost=true},pc,db,store,default);
        Check.Require(!queue.CoversHop((new(7,31),"opaque")),"A same-journal reset kept the old bilateral coverage proof");
        db.Pages.Enqueue(new([],"later",null));
        var occupant=ItemChecks.File(Side.Local,"old/item",9,"new-occupant"); pc.Seed(occupant,[9]);
        var oldDestination=ItemChecks.File(Side.Dropbox,"old/item",8,"replacement-at-destination"); db.Seed(oldDestination,[8]);
        var child=ItemChecks.File(Side.Local,"old/item",1,"carried"); var prior=ItemChecks.File(Side.Dropbox,"old/item",1,"remote-child");
        var priorBinding=SyncedEntries.From(child,prior); await store.CommitAsync(pair.PairId,null,priorBinding,null,null,default);
        var edited=ItemChecks.File(Side.Local,"new/item",2,child.Id); pc.Seed(edited,[2]);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30)); var token=timeout.Token;
        var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int attempts=0;
        var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var move=new Change(Side.Dropbox,ChangeKind.Renamed,"new","old",binding.DropboxId,null,false,false);
        var page=queue.AdmitAsync(pair,new([move],"after-hop",null),async (_,t)=>{
            Interlocked.Increment(ref attempts);
            try {await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,before,(binding,before,after),t);}
            catch(WorkQueue.Unsettled) {held.TrySetResult(); throw;}
        },null,(cursor,t)=>store.SaveCursorAsync(pair.PairId,cursor,t),null,token);
        if(await Task.WhenAny(page,held.Task).WaitAsync(token)==page) {await page; throw new InvalidDataException("Changed child settled without holding its source page");}
        Check.Require(await store.CursorAsync(pair.PairId,token)=="opaque" && !page.IsCompleted,"Uncovered changed row released its input");
        // The remote name arrives with its old bytes. Replaying the original input must apply the local edit.
        db.Seed(prior with {Path="new/item"},[1]);
        try {while(!page.IsCompleted) {token.ThrowIfCancellationRequested(); await queue.RetryHeldAsync(before,token); await Task.Yield();} await page;}
        catch(OperationCanceledException) when(timeout.IsCancellationRequested) {throw new InvalidDataException("Changed child stayed held instead of propagating on replay");}
        Check.Require(attempts>=2 && await store.CursorAsync(pair.PairId,token)=="after-hop" && db.Items["new/item"].Bytes.SequenceEqual(new byte[]{2}),"Held hop failed to propagate the edit before cursor publication");
        var final=store.Records.Values.SingleOrDefault(row=>row.LocalId==edited.Id);
        Check.Require(final!=null && final.DropboxContentHash==edited.ContentHash,"Held hop failed to record the propagated version");
        Check.Require(pc.Items[occupant.Path].Entry==occupant && pc.Items[occupant.Path].Bytes.SequenceEqual(new byte[]{9}),"Physical difference was applied to a reused old path");
        Check.Require(db.Items[oldDestination.Path].Entry==oldDestination && db.Items[oldDestination.Path].Bytes.SequenceEqual(new byte[]{8}) && !db.Mutations.Contains(oldDestination.Id),"Replay wrote to the old destination occupant");
        Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]!=0 && db.Requests[nameof(ISide.ReadAllAsync)]!=0,"Unread opaque cursor treated as covered");
    }
}
