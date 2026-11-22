using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static class RepairChecks
{
    public static async Task RunAsync()
    {
        await InventoryPruningAsync(); await OwnAttemptAsync();
        Check.Require(await HealthyDirtySetAsync(4)==await HealthyDirtySetAsync(512),"Per-item observation work scaled with unchanged population");
        await CoveredHopAsync();
        await BilateralNoticeAsync(); await RecoveryChecks.ClosedNoticeAsync(true); await RecoveryChecks.PutBackSurvivesRepairAsync(true);
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
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,default);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,default);
        foreach(var side in new[]{pc,db}) Check.Require(side.Requests[nameof(ISide.ReadAllAsync)]==0 && side.Items.Count==size && side.Items[edit.Path].Bytes.SequenceEqual(new byte[]{2}),"Healthy Repair skipped the edit or rescanned the pair");
        Check.Require(await store.CursorAsync(pair.PairId,default)=="after-edit","Repair did not consume its dirty input");
        return pc.Requests[nameof(ISide.ReadOneAsync)]+db.Requests[nameof(ISide.ReadOneAsync)];
    }

    public static async Task BilateralNoticeAsync()
    {
        var pair=ItemChecks.Pair(); using var store=new InMemoryStateStore(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        await store.SavePairAsync(pair,default);
        foreach(string path in new[]{"live","unread","missing","empty-subject"})
        {
            var members=path=="empty-subject" ? Array.Empty<NoticeItem>() : new[]{new NoticeItem(Side.Local,path,"previous")};
            await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,path,new(members,Side.Local),DateTimeOffset.UnixEpoch,false),default);
        }
        db.Seed(ItemChecks.File(Side.Dropbox,"live",2,"other"),[2]); db.Seed(ItemChecks.File(Side.Dropbox,"unread",2,"other2"),[2]);
        db.ReadFailures["unread"]=new IOException("unreadable");
        var queue=new WorkQueue(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,default);
        try {await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,default);} catch(InventoryIncompleteException) { }
        Check.Require(store.OpenNotices.Any(n=>n.Path=="live" && n.Kind==NoticeKind.Failure) && store.OpenNotices.Any(n=>n.Path=="unread" && n.Kind==NoticeKind.Failure),"Retired a notice whose counterpart was live or unread");
        Check.Require(!store.OpenNotices.Any(n=>n.Path=="missing" || n.Path=="empty-subject"),"Bilateral absence did not retire an ordinary notice, including its path fallback");
    }

    public static async Task CoveredFolderAsync(bool keep)
    {
        var pair=ItemChecks.Pair(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        using var store=new InMemoryStateStore(); await store.SavePairAsync(pair,default);
        pc.Seed(ItemChecks.File(Side.Local,"a",0,"parent",true),[]); pc.Seed(ItemChecks.File(Side.Local,"a/empty",0,"empty",true),[]);
        db.DeletedHistory["a"]=new DeletionEvidence.DeletedPath("a","a");
        if(keep) pc.Seed(ItemChecks.File(Side.Local,"a/content",3,"file"),[3]);
        var queue=new WorkQueue(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,default);
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

    private static async Task CoveredHopAsync()
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
        var child=ItemChecks.File(Side.Local,"old/item",1,"carried"); var prior=ItemChecks.File(Side.Dropbox,"old/item",1,"remote-child");
        var priorBinding=SyncedEntries.From(child,prior); await store.CommitAsync(pair.PairId,null,priorBinding,null,null,default);
        pc.Seed(child with {Path="new/item"},[1]);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30)); var token=timeout.Token;
        var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int attempts=0;
        var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var move=new Change(Side.Dropbox,ChangeKind.Renamed,"new","old",binding.DropboxId,null,false,false);
        var page=queue.AdmitAsync(pair,new([move],"after-hop",null),async (_,t)=>{
            Interlocked.Increment(ref attempts);
            try {await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,before,(binding,before,after),t);}
            catch(WorkQueue.Unsettled) {held.TrySetResult(); throw;}
        },null,(cursor,t)=>store.SaveCursorAsync(pair.PairId,cursor,t),null,token);
        await held.Task.WaitAsync(token);
        Check.Require(await store.CursorAsync(pair.PairId,token)=="opaque" && !page.IsCompleted,"Uncovered changed row released its input");
        // Simulate the counterpart catching up while the old binding is still held. The queue must rerun the original callback.
        db.Seed(prior with {Path="new/item"},[1]);
        while(!page.IsCompleted) {await queue.RetryHeldAsync(before,token); await Task.Yield();}
        await page;
        Check.Require(attempts>=2 && await store.CursorAsync(pair.PairId,token)=="after-hop" && await store.FindAsync(pair.PairId,Side.Local,priorBinding.LocalKey,token)==priorBinding,"Held hop did not settle on actual replay against its logical binding");
        Check.Require(pc.Items[occupant.Path].Entry==occupant && pc.Items[occupant.Path].Bytes.SequenceEqual(new byte[]{9}),"Physical difference was applied to a reused old path");
        Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]!=0 && db.Requests[nameof(ISide.ReadAllAsync)]!=0,"Unread opaque cursor treated as covered");
    }
}
