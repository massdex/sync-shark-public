using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static class RepairChecks
{
    public static async Task RunAsync()
    {
        await CheckScanCompletionAsync(); await InventoryPruningAsync(); await OwnAttemptAsync(); await NoKeepRootRediscoveredAsync();
        await HealthyDirtySetAsync();
        await CoveredHopAsync();
        await BilateralNoticeAsync(); await RecoveryChecks.ClosedNoticeAsync(true); await RecoveryChecks.PutBackSurvivesRepairAsync(true);
        await RetainedAttemptAsync(); await SelectedStreamRaceAsync(); await ReplacedDuringRepairAsync();
        await CoveredFolderAsync(true); await CoveredFolderAsync(false);
        await BoundedAdmissionAsync(); await AncestorCaseRecordedOnceAsync();
        await ObservedAncestorCaseAsync(); await BilateralCaseRenameAsync(); await UnreadableFolderSkippedAsync();
        await RetirementGuardAsync(); await BoundaryWaitRetriesInPlaceAsync(); await PageWaitNoticeAsync(); await ReplacedRootStopsRepairAsync();
        await CleanupHopProvesRootAsync(); await IsolatedFileKeepsFolderAsync(); await HopOwesOnceAsync(); await OwedHistoryAfterFirstAsync();
        await StampRestoredEditBeatsDeleteAsync();
    }

    // A Dropbox name Windows cannot use fails at the local side's path check: one notice for that item, and the Repair completes for the rest.
    public static Task InvalidLocalNameAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        db.Seed(ItemChecks.File(Side.Dropbox,"ok",1,"ok"),[1]); db.Seed(ItemChecks.File(Side.Dropbox,"bad.",2,"bad"),[2]);
        // Every local path operation first resolves its name the way the real local side does, through a real side's read.
        var home=Directory.CreateTempSubdirectory(); using var bound=new TestDoubles.InMemoryStateStore(); var real=Local.LocalSideChecks.BoundSide(home.FullName,bound);
        var local=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>
        {
            if(m.Name is nameof(ISide.ReadOneAsync) or nameof(ISide.StageAsync) or nameof(ISide.EnsureFolderAsync) && args[0] is string path) real.ReadOneAsync(path,ct);
            return AutoSyncChecks.Observation.Forward(pc,m,args);
        });
        try { await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,new WorkQueue(),local,db,store,null,null,ct,caughtUp:true); }
        finally { home.Delete(true); }
        var notices=new List<Notice>(); await foreach(var n in store.NoticesAsync(pair.PairId,ct)) notices.Add(n);
        Check.Require((await store.RepairStateAsync(pair.PairId,ct)).FirstRepairDone && pc.Items.ContainsKey("ok") && !pc.Items.ContainsKey("bad."),"One invalid local name stopped the Repair");
        Check.Require(notices.Count(n=>n.Kind==NoticeKind.Failure && n.Path=="bad." && n.Cause==TextKey.LocalNameInvalid)==1,$"The invalid name did not get exactly one notice: {string.Join(",",notices.Select(n=>n.Cause+"@"+n.Path))}");
    });

    // A listed Dropbox name ending in a backslash, with an ignore rule present, matches no rule and does not fail the Repair.
    public static Task MalformedIgnoredNameAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var p=pair with {IgnoreRules=["FileExtension:.tmp"]}; await store.SavePairAsync(p,ct);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        db.Seed(ItemChecks.File(Side.Dropbox,"ok",1,"ok"),[1]); db.Seed(ItemChecks.File(Side.Dropbox,"bad\\",2,"bad"),[2]);
        await Repair.RunAsync(p,ItemChecks.Settings,_=>{},new PathScope(p,[p],DriveType.Fixed),new WorkQueue(),pc,db,store,null,null,ct,caughtUp:true);
        Check.Require((await store.RepairStateAsync(p.PairId,ct)).FirstRepairDone && pc.Items.ContainsKey("ok"),"A malformed name under an ignore rule stopped the Repair");
    });

    // A Repair logs a path-free start and counted end for each phase, in order, then one completion line with the effects applied.
    public static Task StageLinesAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); db.Seed(ItemChecks.File(Side.Dropbox,"new",1,"new"),[1]);
        var seen=new List<RunEvent>();
        await Repair.RunAsync(pair,ItemChecks.Settings,e=>{lock(seen) if(e.Kind==RunEventKind.Stage) seen.Add(e);},new PathScope(pair,[pair],DriveType.Fixed),new WorkQueue(),pc,db,store,null,null,ct,caughtUp:true);
        int At(TextKey key,bool ended)=>seen.FindIndex(e=>e.What==key.ToString() && e.Ended==ended);
        // The 0.3.95 stage-end text: name, count, then the elapsed seconds since the paired start.
        bool EndText(TextKey key)=>seen[At(key,true)] is var end && end.Detail.StartsWith(Text.Get(TextKey.StageEnded,(TextArg.Name,Text.Get(key)),
            (TextArg.Count,end.Count?.ToString(System.Globalization.CultureInfo.InvariantCulture)??Text.Get(TextKey.CountUnavailable)),(TextArg.Seconds,"#")).Split('#')[0],StringComparison.Ordinal);
        foreach(var key in new[]{TextKey.StageLocalWalk,TextKey.StageDropboxListing,TextKey.StageRenames,TextKey.StageRows,TextKey.StageExecution,TextKey.StageFinish})
            Check.Require(At(key,false)>=0 && At(key,true)>At(key,false) && (key==TextKey.StageFinish || seen[At(key,true)].Count is not null) && EndText(key) && seen.All(e=>e.Path.Length==0),"Stage "+key+" lacks an ordered, counted, path-free start and end");
        Check.Require(At(TextKey.StagePending,false)>=0 && At(TextKey.StagePending,true)>At(TextKey.StagePending,false) && At(TextKey.StagePending,true)<At(TextKey.StageRenames,false),"Held retries were not logged before the compare");
        Check.Require(seen[At(TextKey.StageDropboxListing,true)].Count==1 && seen[At(TextKey.StageLocalWalk,true)].Count==0,"Listing ends did not count the rows listed");
        Check.Require(new[]{At(TextKey.StageLocalWalk,true),At(TextKey.StageDropboxListing,true)}.Max()<At(TextKey.StageRenames,false) && At(TextKey.StageRenames,true)<At(TextKey.StageRows,false)
            && At(TextKey.StageRows,true)<At(TextKey.StageExecution,false) && At(TextKey.StageExecution,true)<At(TextKey.StageFinish,false),"Repair phases were logged out of order");
        Check.Require(At(TextKey.ResultRunCompleted,true) is var done && done>At(TextKey.StageFinish,true) && done==seen.Count-1 && seen[done].Count==1 && EndText(TextKey.ResultRunCompleted) && seen[At(TextKey.StageExecution,true)].Count==1,"The completion line did not carry the one applied effect");
    });

    // An unreadable PC folder is skipped with its own notice: the Repair completes for the rest, nothing under it moves on either side, and it stays owed.
    private static Task UnreadableFolderSkippedAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        pc.Seed(ItemChecks.File(Side.Local,"ok",1,"ok"),[1]);
        pc.Seed(ItemChecks.File(Side.Local,"bad",0,"bad",true),[]); pc.Seed(ItemChecks.File(Side.Local,"bad/pc",2,"pc"),[2]);
        pc.ReadFailures["bad"]=new ItemFailedException("bad",TextKey.ReadLocalFailed,null);
        db.Seed(ItemChecks.File(Side.Dropbox,"bad",0,"dbad",true),[]); db.Seed(ItemChecks.File(Side.Dropbox,"bad/db",3,"db"),[3]);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,new WorkQueue(),pc,db,store,null,null,ct,caughtUp:true);
        var notices=new List<Notice>(); await foreach(var n in store.NoticesAsync(pair.PairId,ct)) notices.Add(n);
        Check.Require((await store.RepairStateAsync(pair.PairId,ct)).FirstRepairDone && db.Items.ContainsKey("ok"),"One unreadable folder kept the Repair from completing for the rest");
        Check.Require(!db.Items.ContainsKey("bad/pc") && db.Items.ContainsKey("bad/db") && !pc.Items.ContainsKey("bad/db") && pc.Items.ContainsKey("bad/pc"),"An item under the unreadable folder was copied or deleted");
        Check.Require(notices.Count(n=>n.Kind==NoticeKind.Failure && n.Path=="bad" && n.Cause==TextKey.SkipUnreadable)==1 && notices.All(n=>n.Cause!=TextKey.RootUnavailable),$"The skipped folder did not get exactly its own notice: {string.Join(",",notices.Select(n=>n.Cause+"@"+n.Path))}");
        Check.Require((await store.PairAsync(pair.PairId,ct))?.OwedScopes is {} owed && owed.Contains(new Scope("bad","bad")),"The skipped folder is not retried by the next Repair");
        // The owed retry: the folder's own entry reads, but listing it is denied.
        pc.ReadFailures.Remove("bad");
        var denied=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.ReadAllAsync) && (string?)args[0]=="bad" ? Denied() : AutoSyncChecks.Observation.Forward(pc,m,args));
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,new WorkQueue(),denied,db,store,new Scope("bad","bad"),null,ct,caughtUp:true);
        notices=await OpenAsync();
        Check.Require(notices.Count(n=>n.Path=="bad" && n.Cause==TextKey.SkipUnreadable)==1 && notices.All(n=>n.Cause!=TextKey.RootUnavailable) && !db.Items.ContainsKey("bad/pc") && !pc.Items.ContainsKey("bad/db") &&
            (await store.PairAsync(pair.PairId,ct))?.OwedScopes is {} still && still.Contains(new Scope("bad","bad")),
            $"A denied folder listing was not a skip of that folder: {string.Join(",",notices.Select(n=>n.Cause+"@"+n.Path))}");
        // Readable again with the same identity: its contents sync and its notice closes.
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,new WorkQueue(),pc,db,store,new Scope("bad","bad"),null,ct,caughtUp:true);
        notices=await OpenAsync();
        Check.Require(db.Items.ContainsKey("bad/pc") && pc.Items.ContainsKey("bad/db") && notices.All(n=>n.Path!="bad"),$"The readable folder did not sync or kept its notice: {string.Join(",",notices.Select(n=>n.Cause+"@"+n.Path))}");
        async Task<List<Notice>> OpenAsync() { var open=new List<Notice>(); await foreach(var n in store.NoticesAsync(pair.PairId,ct)) if(!n.Dismissed) open.Add(n); return open; }
        static async IAsyncEnumerable<Listed> Denied() { await Task.FromException(new InventoryIncompleteException("denied",null)); yield break; }
    });

    // A parent's new case is saved with no Dropbox call and no subtree work; the next Repair finds no difference.
    private static Task AncestorCaseRecordedOnceAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed); var queue=new WorkQueue();
        pc.Seed(ItemChecks.File(Side.Local,"Parent",0,"p",true),[]); pc.Seed(ItemChecks.File(Side.Local,"Parent/Child",0,"c",true),[]); pc.Seed(ItemChecks.File(Side.Local,"Parent/Child/f",1,"f"),[1]);
        db.Seed(ItemChecks.File(Side.Dropbox,"Parent",0,"dp",true),[]); db.Seed(ItemChecks.File(Side.Dropbox,"Parent/Child",0,"dc",true),[]); db.Seed(ItemChecks.File(Side.Dropbox,"Parent/Child/f",1,"df"),[1]);
        var first=new List<string>();
        await Repair.RunAsync(pair,ItemChecks.Settings,e=>{lock(first) first.Add($"{e.Kind}:{e.Action}:{e.Path}:{e.Cause}");},paths,queue,pc,db,store,null,null,ct,caughtUp:true);
        var firstNotices=new List<string>(); await foreach(var n in store.NoticesAsync(pair.PairId,ct)) firstNotices.Add(n.Cause+"@"+n.Path);
        Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("Parent/Child/f"),ct) is {DropboxPath:"Parent/Child/f"} &&
            await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("Parent/Child"),ct) is {DropboxPath:"Parent/Child"},
            $"The first Repair did not record the folder and its file (events {string.Join(" | ",first)}; notices {string.Join(",",firstNotices)})");
        // Dropbox's listing keeps the parent's old case for its descendants.
        foreach(var path in new[]{"Parent/Child/f","Parent/Child"}) { var (entry,bytes)=db.Items[path]; db.Items.Remove(path); db.Seed(entry with {Path="parent"+path["Parent".Length..]},bytes); }
        string[] writes=[nameof(ISide.PublishAsync),nameof(ISide.MoveAsync),nameof(ISide.EnsureFolderAsync),nameof(ISide.DeleteAsync),nameof(ISide.CreateFolderExclusiveAsync)];
        long Writes()=>writes.Sum(w=>db.Requests[w]);
        long Reads()=>db.Requests[nameof(ISide.ReadOneAsync)]+db.Requests[nameof(ISide.ReadByIdAsync)]; // get_metadata
        var trace=new List<string>();
        async Task<List<RunEvent>> RepairAsync()
        {
            var effects=new List<RunEvent>(); await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",ct);
            await Repair.RunAsync(pair,ItemChecks.Settings,e=>{lock(trace) trace.Add($"{e.Kind}:{e.Action}:{e.Path}:{e.Cause}"); if(e.Kind==RunEventKind.Effect) lock(effects) effects.Add(e);},paths,queue,pc,db,store,null,null,ct,caughtUp:true);
            return effects;
        }
        long writesBefore=Writes(), readsBefore=Reads(), listings=db.Requests[nameof(ISide.ReadAllAsync)];
        var second=await RepairAsync();
        var saved=await store.FindAsync(pair.PairId,Side.Dropbox,PathNames.DropboxKey("parent/Child/f"),ct);
        Check.Require(second.Count!=0 && second.All(e=>e.Action==ActionKind.Record) && Writes()==writesBefore && Reads()==readsBefore && db.Requests[nameof(ISide.ReadAllAsync)]==listings+1 && saved?.DropboxPath=="parent/Child/f",
            $"A parent's case did not save as a record only ({string.Join(",",second.Select(e=>e.Action))}; writes {Writes()-writesBefore}; reads {Reads()-readsBefore}; listings {db.Requests[nameof(ISide.ReadAllAsync)]-listings}; saved {saved?.DropboxPath}; pc file {pc.Items.ContainsKey("Parent/Child/f")}; records {string.Join(",",(await RecordsAsync()))}; notices {string.Join(",",(await NoticesAsync()))}; events {string.Join(" | ",trace)})");
        async Task<List<string>> RecordsAsync() { var all=new List<string>(); foreach(var k in new[]{"Parent","Parent/Child","Parent/Child/f"}) if(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(k),ct) is {} r) all.Add(r.LocalPath+"|"+r.DropboxPath); return all; }
        async Task<List<string>> NoticesAsync() { var all=new List<string>(); await foreach(var n in store.NoticesAsync(pair.PairId,ct)) all.Add(n.Cause+"@"+n.Path); return all; }
        var third=await RepairAsync();
        Check.Require(third.Count==0 && Writes()==writesBefore,$"The next Repair found the parent's case again: {string.Join(",",third.Select(e=>e.Action))}");
    });

    // Reading a binding by ID returns the corrected ancestor case; only the item's own name can make it a move.
    private static Task ObservedAncestorCaseAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var l=ItemChecks.File(Side.Local,"Parent/Child/f",1,"l1"); var d=ItemChecks.File(Side.Dropbox,"Parent/Child/f",1,"d1"); pc.Seed(l,[1]); db.Seed(d,[1]);
        var observe=typeof(ItemSync).GetMethod("ObserveBindingAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
        async Task<int> Moved(SyncedEntry binding)=>(await (Task<(Row Row,IReadOnlyList<RenameCandidate> Group)>)observe.Invoke(null,[paths,binding,pc,db,ct])!).Group.Count;
        var ancestor=await Moved(SyncedEntries.From(l with {Path="parent/Child/f"},d with {Path="parent/Child/f"}));
        var leaf=await Moved(SyncedEntries.From(l with {Path="Parent/Child/F"},d));
        Check.Require(ancestor==0 && leaf==1,$"By-ID observation: ancestor case gave {ancestor} moves, a leaf case rename gave {leaf}");
    });

    // The same case rename on both sides, with Dropbox keeping the parent's old case, is one name, not two.
    private static Task BilateralCaseRenameAsync()
    {
        var saved=SyncedEntries.From(ItemChecks.File(Side.Local,"Parent/file",1,"l1"),ItemChecks.File(Side.Dropbox,"Parent/file",1,"d1"));
        var action=Engine.Planner.Moves.For([new(Side.Local,ItemChecks.File(Side.Local,"Parent/File",1,"l1"),saved),new(Side.Dropbox,ItemChecks.File(Side.Dropbox,"parent/File",1,"d1"),saved)],
            null,new Engine.Planner.Moves.Observations(null,null,false,false,false,false,null,null,null),false);
        Check.Require(action is {Kind:ActionKind.Record,InfoAfter:null},$"A matching case rename on both sides gave {action.Kind} {action.InfoAfter}");
        return Task.CompletedTask;
    }

    // Discovery waits for the work queue: while uploads stall, at most one storage page of differences is admitted.
    private static Task BoundedAdmissionAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var dbSide=new FakeSide(Side.Dropbox); int page=ItemChecks.Settings.StoragePageSize, count=page+30;
        for(int i=0;i<count;i++) pc.Seed(ItemChecks.File(Side.Local,$"f{i:D4}",1,$"{i}"),[1]);
        int inflight=0, most=0; var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var fake=new object();
        // The fake's dictionaries are not thread-safe and its calls finish synchronously: forward one call at a time.
        object? Serialized(System.Reflection.MethodInfo m,object?[] args) { lock(fake) return AutoSyncChecks.Observation.Forward(dbSide,m,args); }
        async Task<RemovalResult> Stalled(System.Reflection.MethodInfo m,object?[] args)
        {
            // The known boundary: a full page waits here, then the gate opens.
            var now=Interlocked.Increment(ref inflight); lock(gate) most=Math.Max(most,now);
            if(now>=page) gate.TrySetResult();
            try { await gate.Task.WaitAsync(ct); } finally { Interlocked.Decrement(ref inflight); }
            return await (Task<RemovalResult>)Serialized(m,args)!;
        }
        var remote=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.PublishAsync) ? Stalled(m,args) : Serialized(m,args));

        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},new PathScope(pair,[pair],DriveType.Fixed),new WorkQueue(),pc,remote,store,null,null,ct,caughtUp:true);
        gate.TrySetResult();
        Check.Require(most<=page && dbSide.Items.Count==count,$"Repair admitted {most} differences at once (page {page}) or lost uploads ({dbSide.Items.Count}/{count})");
    });

    // After a journal reset a full Repair finds a present no-keep root by its own pruning, completes once, and only then owes the root to the rule's owner.
    private static Task NoKeepRootRediscoveredAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var rule=new Exclusion(Side.Local,"dir",false); var p=pair with {Exclusions=[rule]}; await store.SavePairAsync(p,ct);
        var local=new FakeSide(Side.Local){OutOfScope=path=>PathNames.IsSelfOrUnder(path,"dir")}; var db=new FakeSide(Side.Dropbox);
        local.Seed(ItemChecks.File(Side.Local,"dir",0,"dir",true),[]); local.Seed(ItemChecks.File(Side.Local,"dir/f",1,"f"),[1]);
        await Repair.RunAsync(p,ItemChecks.Settings,_=>{},new PathScope(p,[p],DriveType.Fixed),new WorkQueue(),local,db,store,null,null,ct,caughtUp:true);
        Check.Require((await store.RepairStateAsync(p.PairId,ct)).FirstRepairDone,"A full Repair that found a present no-keep root superseded its own finish");
        Check.Require((await store.PairAsync(p.PairId,ct))?.OwedScopes is [{LocalPath:"dir"}] && db.Items.Count==0,"A full Repair did not owe exactly the present no-keep root to its owner, or synced under it itself");
    });
    // The full run captures its Dropbox debt in one read, on the SQLite finish path. Scopes owed just before and just after
    // that read leave it completed and still owed; a whole-pair debt right after it still supersedes the run.
    public static Task FullCaptureScopeChurnAsync() => FullCaptureBoundaryAsync(false);
    public static Task FullCaptureWholePairDebtAsync() => FullCaptureBoundaryAsync(true);
    private static Task FullCaptureBoundaryAsync(bool wholePairDebt) => WithRepairStore(async (store,pair,ct)=>
    {
        async Task<(long Revision,string? Reason)> Capture(System.Reflection.MethodInfo method,object?[] args)
        {
            await store.OweScopeAsync(pair.PairId,new Scope("before","before"),ct);
            var debt=await (Task<(long Revision,string? Reason)>)AutoSyncChecks.Observation.Forward(store,method,args)!;
            await store.OweScopeAsync(pair.PairId,new Scope("after","after"),ct);
            if(wholePairDebt) await store.OweRepairAsync(pair.PairId,Side.Dropbox,"gap",ct);
            return debt;
        }
        var observed=AutoSyncChecks.Observation.Wrap<IStore>((method,args)=>method.Name==nameof(IStore.ReadFullRepairAsync) ? Capture(method,args) : AutoSyncChecks.Observation.Forward(store,method,args));
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},new PathScope(pair,[pair],DriveType.Fixed),new WorkQueue(),new FakeSide(Side.Local),new FakeSide(Side.Dropbox),observed,null,null,ct,caughtUp:true);
        Check.Require((await store.RepairStateAsync(pair.PairId,ct)).FirstRepairDone!=wholePairDebt,
            wholePairDebt ? "A whole-pair debt after the full capture did not supersede the run" : "Scopes owed at the full capture boundary superseded the full Repair");
        Check.Require((await store.PairAsync(pair.PairId,ct))?.OwedScopes?.Count==2,"The full Repair retired or lost a scope owed at its capture");
    });
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
        try {Check.Require(await HealthyDirtySetAsync(4,timeout.Token)==await HealthyDirtySetAsync(512,timeout.Token),"Per-item observation work scaled with unchanged population");await RequestedComparesAsync(timeout.Token);}
        catch(OperationCanceledException) when(timeout.IsCancellationRequested) {throw new InvalidDataException("The healthy feed path did not settle its edit and cursor");}
    }

    private static async Task<long> HealthyDirtySetAsync(int size,CancellationToken ct)
    {
        var pair=ItemChecks.Pair(); pair=pair with {Roots=ItemChecks.SavedRoots};
        using var store=new InMemoryStateStore(); var pc=Rooted(new FakeSide(Side.Local),pair); var db=Rooted(new FakeSide(Side.Dropbox),pair);
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
        // The AutoSync feed path consumes the dirty edit and publishes the cursor without listing the pair.
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,local,cloud,store,static()=>{},ct);
        foreach(var side in new[]{pc,db}) Check.Require(side.Requests[nameof(ISide.ReadAllAsync)]==0 && side.Items.Count==size && side.Items[edit.Path].Bytes.SequenceEqual(new byte[]{2}),"The feed path skipped the edit or rescanned the pair");
        Check.Require(await store.CursorAsync(pair.PairId,ct)=="after-edit","The feed path did not consume its dirty input");
        return pc.Requests[nameof(ISide.ReadOneAsync)]+db.Requests[nameof(ISide.ReadOneAsync)];
    }

    // A requested Repair must compare the pair, not finish from the reader's replay; a Dropbox-only item must reach both sides.
    private static async Task RequestedComparesAsync(CancellationToken ct)
    {
        var pair=ItemChecks.Pair(); pair=pair with {Roots=ItemChecks.SavedRoots};
        using var store=new InMemoryStateStore(); var pc=Rooted(new FakeSide(Side.Local),pair); var db=Rooted(new FakeSide(Side.Dropbox),pair);
        await store.SavePairAsync(pair,ct); await store.FinishRepairAsync(pair.PairId,(0,0),"{}",ct);
        const string path="cloud-only"; var item=ItemChecks.File(Side.Dropbox,path,1,"c1"); db.Seed(item,[1]);
        await store.SavePositionAsync("1",new JournalPosition(7,24),ct); await store.SaveCursorAsync(pair.PairId,"current",ct);
        var paths=new PathScope(pair,[pair],DriveType.Fixed); var queue=new WorkQueue();
        await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",ct);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct,caughtUp:true);
        Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]>0 && db.Requests[nameof(ISide.ReadAllAsync)]>0,"A requested Repair finished without comparing both inventories");
        foreach(var side in new[]{pc,db}) Check.Require(side.Items.TryGetValue(path,out var at) && at.Bytes.SequenceEqual(new byte[]{1}),"A requested Repair left the Dropbox-only item off one side");
    }

    public static async Task BilateralNoticeAsync()
    {
        await FilteredChildKeepsNoticeAsync();
        foreach(var interrupt in new[]{true,false}) await WithRepairStore(async (store,pair,ct)=>
        {
            pair=pair with {Roots=ItemChecks.SavedRoots};
            var pc=Rooted(new FakeSide(Side.Local),pair); var db=Rooted(new FakeSide(Side.Dropbox),pair);
            await store.SavePairAsync(pair,ct);
            var queue=new WorkQueue(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
            var parent=ItemChecks.File(Side.Local,"local-parent",0,"pc-parent",true); var peer=ItemChecks.File(Side.Dropbox,"cloud-parent",0,"cloud-parent",true);
            pc.Seed(parent,[]); db.Seed(peer,[]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(parent,peer),null,null,ct);
            foreach(string path in new[]{"unread","missing","empty-subject","in-job"})
            {
                var members=path=="empty-subject" ? Array.Empty<NoticeItem>() : new[]{new NoticeItem(Side.Local,path,"previous")};
                await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,path,new(members,Side.Local),DateTimeOffset.UnixEpoch,false),ct);
            }
            pc.Seed(ItemChecks.File(Side.Local,"marked",0,"new-folder",true),[]);
            pc.Seed(ItemChecks.File(Side.Local,"marked/child",7,"new-child"),[7]);
            if(interrupt) pc.Seed(ItemChecks.File(Side.Local,"marked/later",8,"later-child"),[8]);
            db.DeletedHistory["marked"]=new DeletionEvidence.DeletedPath("marked","marked");
            foreach(var path in new[]{"marked","marked/child"}) await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,path,new([new(Side.Local,path,"former")],Side.Local),DateTimeOffset.UnixEpoch,false),ct);
            var bound=SyncedEntries.From(ItemChecks.File(Side.Local,"missing",1,"previous"),ItemChecks.File(Side.Dropbox,"missing",1,"peer"));
            await store.CommitAsync(pair.PairId,null,bound,null,null,ct);
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
            if(!interrupt) await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",ct);
            try {await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,observed,null,null,ct);Check.Require(interrupt,"Incomplete inventory reported success");}
            catch(IOException e) when(!interrupt && ReferenceEquals(e,db.ReadFailures["unread"]))
            {
                // An unreadable file is its own item's failure; an unrelated fault from its re-observation is not swallowed.
                Check.Require((await store.ReadRepairAsync(pair.PairId,Side.Dropbox,ct)).Reason!=null && db.Items["unread"].Bytes.SequenceEqual(new byte[]{2}),"An unreadable re-observation lost the obligation or bytes");
                var observedUnread=new List<Notice>(); await foreach(var n in store.NoticesAsync(pair.PairId,ct)) if(n.Path=="unread") observedUnread.Add(n);
                Check.Require(observedUnread.Any(n=>n.Kind==NoticeKind.Failure && n.Cause==TextKey.ReadDropboxFailed && n.Subject.Items.Any(i=>i==new NoticeItem(Side.Dropbox,"unread","other"))),
                    "The unreadable file's own failure was not saved before the independent error escaped");
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
            Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("missing"),ct)==null && !openNotices.Any(n=>n.Path=="missing"),"Bound absent row kept its notice or binding");
            Check.Require(db.Items.ContainsKey("marked") && db.Items.TryGetValue("marked/child",out var kept) && kept.Bytes.SequenceEqual(new byte[]{7}) && !openNotices.Any(n=>n.Path=="marked" || n.Path=="marked/child"),"Kept historical folder or covered child lost its ordinary notice disposition");
            Check.Require(openNotices.Any(n=>n.Kind==NoticeKind.Tie && n.Path==tie.Path) && openNotices.Any(n=>n.Path=="unread") && openNotices.Any(n=>n.Path=="in-job"),"Retired a mapped legacy tie, unread counterpart or accepted job");
            Check.Require(!openNotices.Any(n=>n.Path=="missing" || n.Path=="empty-subject"),"One unread notice prevented another bilaterally absent notice from closing");
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

    // The end turn owns only the exact generation its save receipt names.
    public static Task OwnAttemptAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var own=(await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailLocalInUse,"item",new([new(Side.Local,"item","a")],Side.Local),DateTimeOffset.UnixEpoch,false),ct))!;
        Check.Require(await Repair.CurrentRetryAsync(store,pair.PairId,own,ct) is {Id:>0},"Repair lost its own failed attempt");
        var other=(await store.SaveNoticeAsync(pair.PairId,own with {Id=0,Subject=new([new(Side.Local,"item","b")],Side.Local),At=own.At.AddSeconds(1)},ct))!;
        Check.Require(await Repair.CurrentRetryAsync(store,pair.PairId,own,ct)==null && (await Repair.CurrentRetryAsync(store,pair.PairId,other,ct))?.At==other.At,"Repair retried a different failure at a reused path");
        // Ignore only hides the notice; the scheduled end attempt still owns it.
        Check.Require(await store.UpdateNoticeVisibilityAsync(pair.PairId,other,true,ct) && await Repair.CurrentRetryAsync(store,pair.PairId,other,ct) is {Ignored:true},"Ignore cancelled the Repair end attempt");
        // An identical re-raise is a newer generation: it belongs to whoever raised it, not to the old receipt.
        var again=(await store.SaveNoticeAsync(pair.PairId,other with {Id=0},ct))!;
        Check.Require(again.At>other.At && await Repair.CurrentRetryAsync(store,pair.PairId,other,ct)==null && await Repair.CurrentRetryAsync(store,pair.PairId,again,ct) is not null,"An end turn took custody of a re-raised notice");
    });

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
        async Task<bool> OpenFailure()
        {
            await foreach(var n in store.NoticesAsync(pair.PairId,ct)) if(n.Kind==NoticeKind.Failure && PathNames.Same(n.Path,path)) return true;
            return false;
        }
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,cloud,store,null,null,ct);
        Check.Require(pc.Items.ContainsKey(path) && pc.History.Count==0,"Unproved Repair removal changed the selected file");
        Check.Require(!await queue.RetryHeldAsync(null,ct),"Repair kept its failed row in the queue");
        Check.Require(await OpenFailure(),"The unproved Repair removal kept no notice");
        // A later reader page settles; it does not replay the released row or mutate the unproved removal.
        var end=new JournalPosition(1,2);ItemChecks.Script(pc,new([],null,end){ProjectedThrough=end});ItemChecks.Script(cloud,new([],"after",null));
        await AutoSync.ReadThroughAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,cloud,store,(end,"after"),static()=>{},ct);
        Check.Require(pc.Items.ContainsKey(path) && pc.History.Count==0,"An empty reader page completed an unproved removal");
        Check.Require(await OpenFailure(),"An empty reader page closed the unproved removal notice");
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

    /// <summary>A throttled write's in-place retry waits the time Dropbox gave, then settles without a notice.</summary>
    public static Task ProviderWaitAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local);var dbSide=new FakeSide(Side.Dropbox);var queue=new WorkQueue();var paths=new PathScope(pair,[pair],DriveType.Fixed);
        await SeedRangeAsync(store,pair,pc,dbSide,ct);
        var wait=TimeSpan.FromMilliseconds(250); long? refused=null; TimeSpan? retried=null; int tries=0;
        var remote=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>
        {
            if(m.Name!=nameof(ISide.PublishAsync) || args[0] is not IStaged {Path:"range/a"}) return AutoSyncChecks.Observation.Forward(dbSide,m,args);
            if(++tries==1)
            {
                refused=TimeProvider.System.GetTimestamp();
                return Task.FromException<RemovalResult>(new ItemFailedException("range/a",TextKey.WaitTooManyWrites,null){Disposition=FailureDisposition.TemporaryNoEffect,ProviderWait=wait,ProviderWaitStarted=refused});
            }
            retried??=TimeProvider.System.GetElapsedTime(refused!.Value);
            return AutoSyncChecks.Observation.Forward(dbSide,m,args);
        });
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,remote,store,static()=>{},ct);
        pc.Seed(ItemChecks.File(Side.Local,"range/a",1,"11"),[1]);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,remote,store,new("range","range"),null,ct);
        // Windows timers can fire up to one default clock tick (1/64 s) before the requested time.
        Check.Require(tries==2 && retried>=wait-TimeSpan.FromSeconds(1d/64) && dbSide.Items.ContainsKey("range/a"),"Repair resent a throttled write before the provider wait ended, or never resent it");
        await foreach(var n in store.NoticesAsync(pair.PairId,ct))
            Check.Require(n.Kind!=NoticeKind.Failure || !PathNames.Same(n.Path,"range/a"),"A throttled write settled by its in-place retry kept a failure notice");
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

    /// <summary>The occupant is replaced during the row's own upload; the stale Repair row must end, leaving nothing held.</summary>
    public static Task ReplacedDuringRepairAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        const string item="range/a";
        var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var queue=new WorkQueue();var paths=new PathScope(pair,[pair],DriveType.Fixed);
        await SeedRangeAsync(store,pair,pc,db,ct);
        pc.Seed(ItemChecks.File(Side.Local,item,1,"11"),[1]);
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,static()=>{},ct);
        // The user saves the item by replace after the stale upload is staged: a new identity and new bytes; its publish then finds the change.
        var replaced=ItemChecks.File(Side.Local,item,3,"replaced");
        db.AfterStage=()=>pc.Items[item]=(replaced,new byte[]{3});
        db.PublishFailures[item]=new ChangedUnderneathException(item);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,new("range","range"),null,ct);
        Check.Require(pc.Items.TryGetValue(item,out var now) && now.Entry.Id=="replaced" && db.Requests[nameof(ISide.PublishAsync)]>0,"The replacement or the intended stale publish never ran");
        Check.Require(!db.Items.ContainsKey(item),"A replaced local copy was published from its stale identity");
        Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(item),ct)==null,"The stale Repair identity kept a binding");
        Check.Require(!await queue.RetryHeldAsync(null,ct),"Repair left the replaced row held");
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
        Check.Require(await store.FinishRepairAsync(pair.PairId,(l.Revision,d.Revision),"{}",ct) is (true,true),"Root fixture did not finish bootstrap");
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
                return AfterNotice((Task<Notice?>)result!,notice);
            return result;
        });
        async Task<Notice?> AfterNotice(Task<Notice?> save,Notice notice)
        {
            var saved=await save;
            if(Volatile.Read(ref rootFaults)==0) disposition.TrySetException(new InvalidDataException($"Host reported {notice.Cause} before the selected Repair inventory"));
            else disposition.TrySetResult();
            return saved;
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

    // A never-synced KEEP rule does not follow its item: a full Repair that finds the path empty raises one Failure, and clears it when the item is back.
    public static Task KeepRuleMissingNoticeAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        pair=pair with {Exclusions=[new(Side.Local,"kept",true)]}; await store.SavePairAsync(pair,ct);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var kept=ItemChecks.File(Side.Local,"kept",1,"kept-id"); pc.Seed(kept,[1]);
        var paths=new PathScope(pair,[pair],DriveType.Fixed); var queue=new WorkQueue();
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,static()=>{},ct); // wires queue.Unfinished as the host does
        async Task<int> Raised(string path="kept")
        {
            var count=0;
            await foreach(var n in store.NoticesAsync(pair.PairId,ct)) if(n is {Kind:NoticeKind.Failure,Cause:TextKey.KeepRuleItemMissing,Subject.Incoming:Side.Local} && n.Path==path) count++;
            return count;
        }
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct);
        Check.Require(await Raised()==0 && !db.Items.ContainsKey("kept"),"A present never-synced kept item raised a notice or synced");
        pc.Items.Remove("kept");
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct);
        Check.Require(await Raised()==1,"A never-synced KEEP rule whose item left its path did not raise exactly one notice");
        pc.Seed(kept,[1]);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct);
        Check.Require(await Raised()==0,"The missing-kept-item notice stayed after the item came back");
        // A never-synced child under a synced folder: its rule rides the folder's row (nonempty tail) but its item never synced.
        var folder=ItemChecks.File(Side.Local,"P",0,"p",true); var remoteFolder=ItemChecks.File(Side.Dropbox,"P",0,"rp",true);
        pc.Seed(folder,[]); db.Seed(remoteFolder,[]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(folder,remoteFolder),null,null,ct);
        var child=ItemChecks.File(Side.Local,"P/private",1,"private-id"); pc.Seed(child,[1]);
        await store.SavePairAsync(pair with {Exclusions=[..pair.Exclusions,new(Side.Local,"P/private",true)]},ct);
        pair=(await store.PairAsync(pair.PairId,ct))!; paths=new PathScope(pair,[pair],DriveType.Fixed);
        Check.Require(pair.Exclusions.Any(e=>e.Path=="P/private" && e.KeptBy is {Tail:"private"}),"The never-synced child rule did not ride its synced folder");
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct);
        Check.Require(await Raised("P/private")==0 && !db.Items.ContainsKey("P/private"),"A present never-synced kept child raised a notice or synced");
        pc.Items.Remove("P/private");
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct);
        Check.Require(await Raised("P/private")==1,"A never-synced KEEP child under a synced folder whose item left its path did not raise exactly one notice");
        pc.Seed(child,[1]);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct);
        Check.Require(await Raised("P/private")==0,"The missing-kept-child notice stayed after the item came back");
    });

    private static PairConfig Bound(PairConfig pair) => pair with {Roots=ItemChecks.SavedRoots};
    // A fake endpoint whose top folder still has the pair's saved identity.
    private static FakeSide Rooted(FakeSide side,PairConfig pair) { side.Root=side.Side==Side.Local ? (pair.Roots!.Local,null,null,DriveType.Fixed) : (null,pair.Roots!.Dropbox,null,null); return side; }

    // Retirement re-decides scope inside the close transaction: a rule published while the close waits keeps the notice.
    public static async Task RetirementGuardAsync() { await RetirementGuardAsync(false); await RetirementGuardAsync(true); }
    private static Task RetirementGuardAsync(bool ruleAtClose) => WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed); await store.FinishRepairAsync(pair.PairId,(0,0),"{}",ct);
        var ruled=pair with {Exclusions=[new(Side.Local,"gone",false)]}; var closing=false;
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,"gone",new([new(Side.Local,"gone","previous")],Side.Local),DateTimeOffset.UnixEpoch,false),ct);
        var queue=await BoundQueueAsync(pair,paths,pc,db,store,ct); var excluded=new PathScope(ruled,[ruled],DriveType.Fixed);
        typeof(WorkQueue).GetProperty("ScopeNow",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(queue,(Func<PathScope>)(()=>Volatile.Read(ref closing)?excluded:paths));
        var observed=AutoSyncChecks.Observation.Wrap<IStore>((m,args)=>{ if(m.Name==nameof(IStore.CloseNoticeAsync)) Volatile.Write(ref closing,ruleAtClose); return AutoSyncChecks.Observation.Forward(store,m,args); });
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,observed,null,null,ct,caughtUp:true);
        var open=false; await foreach(var n in store.NoticesAsync(pair.PairId,ct)) open|=n.Path=="gone";
        Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]==1 && open==ruleAtClose,ruleAtClose?"A rule published while the retirement close waited did not keep the notice":"A bilaterally absent notice was not retired");
    });

    // The listed Repair's Dropbox boundary waits out a provider refusal in place, and that wait observes the caller's cancellation.
    public static async Task BoundaryWaitRetriesInPlaceAsync() { await BoundaryWaitAsync(true); await BoundaryWaitAsync(false); }
    private static Task BoundaryWaitAsync(bool refuse) => WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed); await store.FinishRepairAsync(pair.PairId,(0,0),"{}",ct);
        var queue=await BoundQueueAsync(pair,paths,pc,db,store,ct);
        int boundaries=0; var waiting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.BoundaryAsync) && Interlocked.Increment(ref boundaries)==1 && refuse
            ? Task.FromException<(string?,JournalPosition?)>(new ItemFailedException(pair.DropboxRoot,TextKey.WaitTooManyWrites,new HttpRequestException("busy",null,System.Net.HttpStatusCode.TooManyRequests)){Disposition=FailureDisposition.TemporaryNoEffect,ProviderWait=TimeSpan.FromHours(1)})
            : AutoSyncChecks.Observation.Forward(db,m,args));
        using var cts=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run=Repair.RunAsync(pair,ItemChecks.Settings,e=>{if(e.Kind==RunEventKind.Wait && e.Cause==TextKey.WaitSourceRetry) waiting.TrySetResult();},paths,queue,pc,remote,store,null,null,cts.Token,caughtUp:true);
        if(!refuse) { await run; Check.Require(boundaries==1 && db.Requests[nameof(ISide.ReadAllAsync)]==1,"An unrefused boundary did not finish the listed Repair"); return; }
        Check.Require(await Task.WhenAny(waiting.Task,run)==waiting.Task && !run.IsCompleted && boundaries==1,"A refused Dropbox boundary failed the Repair instead of waiting in place");
        cts.Cancel();
        try { await run; throw new InvalidDataException("A cancelled boundary wait completed the Repair"); } catch(OperationCanceledException) when(!ct.IsCancellationRequested) { }
        var saved=0; await foreach(var _ in store.NoticesAsync(pair.PairId,ct)) saved++;
        Check.Require(boundaries==1 && saved==0,"The boundary wait resent the read or saved a notice");
    });

    // A Repair's own Dropbox listing shows one page-wait notice while it waits and closes it at the accepted page; a stale one closes at the next start.
    public static Task PageWaitNoticeAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var paths=new PathScope(pair,[pair],DriveType.Fixed); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); int shown=-1, localWaits=0;
        async Task<int> Waits() { var count=0; await foreach(var n in store.NoticesAsync(pair.PairId,ct)) if(n.Cause==TextKey.RequiredRepairRetry) count++; return count; }
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.RequiredRepairRetry,string.Empty,new([],null),DateTimeOffset.UnixEpoch,false),ct);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,new WorkQueue(),pc,db,store,null,null,ct,caughtUp:true);
        Check.Require(await Waits()==0,"A previous run's page-wait notice survived the next Repair's start");
        async IAsyncEnumerable<Listed> Waited(FakeSide side,string under,ListOptions options)
        {
            if(options.PageWait is {} wait) { await wait(true); shown=await Waits(); }
            await foreach(var item in side.ReadAllAsync(under,options,ct)) yield return item;
            if(options.PageWait is {} done) await done(false);
        }
        ISide Paged(FakeSide side)=>AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>
        {
            if(m.Name!=nameof(ISide.ReadAllAsync)) return AutoSyncChecks.Observation.Forward(side,m,args);
            var options=(ListOptions)args[1]!; if(side==pc && options.PageWait!=null) localWaits++;
            return Waited(side,(string)args[0]!,options);
        });
        db.Seed(ItemChecks.File(Side.Dropbox,"new",1,"new"),[1]);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,new WorkQueue(),Paged(pc),Paged(db),store,null,null,ct,caughtUp:true);
        Check.Require(shown==1 && await Waits()==0 && localWaits==0 && pc.Items.ContainsKey("new"),"The Dropbox listing's page wait was not shown once and closed, or the PC walk was given one");
    });

    // A replaced pair root stops the Repair before any listing or compare; the root error is the member's, not an item notice.
    public static Task ReplacedRootStopsRepairAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        pair=Bound(pair); await store.SavePairAsync(pair,ct);
        var pc=Rooted(new FakeSide(Side.Local),pair); var db=new FakeSide(Side.Dropbox){Root=(null,new("a","r","h","replaced"),null,null)};
        db.Seed(ItemChecks.File(Side.Dropbox,"new",1,"new"),[1]);
        try { await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},new PathScope(pair,[pair],DriveType.Fixed),new WorkQueue(),pc,db,store,null,null,ct,caughtUp:true); throw new InvalidDataException("A replaced Dropbox root did not stop the Repair"); }
        catch(ChangedUnderneathException e) when(PathNames.Same(e.Path,pair.DropboxRoot)) { }
        Check.Require(!pc.Items.ContainsKey("new") && db.Requests[nameof(ISide.ReadAllAsync)]==0,"A Repair listed or compared under a replaced root");
    });

    // A confirmed-removal cleanup hop proves the saved Dropbox root before its coverage exit.
    public static Task CleanupHopProvesRootAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var (bound,pc,db,queue)=await CoveredQueueAsync(store,pair,ct); var scope=new Scope("old","old");
        var binding=SyncedEntries.From(ItemChecks.File(Side.Local,"old",0,"pc",true),ItemChecks.File(Side.Dropbox,"old",0,"db",true));
        db.Root=(null,new("a","r","h","replaced"),null,null);
        try { await Repair.RunAsync(bound,ItemChecks.Settings,_=>{},new PathScope(bound,[bound],DriveType.Fixed),queue,pc,db,store,scope,(binding,scope,scope),ct); throw new InvalidDataException("A covered cleanup hop exited on a replaced root"); }
        catch(ChangedUnderneathException e) when(PathNames.Same(e.Path,bound.DropboxRoot)) { }
        Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]+db.Requests[nameof(ISide.ReadAllAsync)]==0,"A cleanup hop listed under a replaced root");
    });

    // An undecodable Dropbox file isolated by the listing proves no removal: its folder stays on Dropbox while other work syncs.
    public static async Task IsolatedFileKeepsFolderAsync() { await IsolatedAsync(true); await IsolatedAsync(false); }
    private static Task IsolatedAsync(bool isolated) => WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        foreach(var (path,folder) in new[]{("F",true),("F/m",false)})
        {
            var remote=ItemChecks.File(Side.Dropbox,path,folder?(byte)0:(byte)1,"db-"+path,folder); db.Seed(remote,folder?[]:[1]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(ItemChecks.File(Side.Local,path,folder?(byte)0:(byte)1,"pc-"+path,folder),remote),null,null,ct);
        }
        pc.Seed(ItemChecks.File(Side.Local,"new",2,"pc-new"),[2]); // the PC folder F is gone
        if(isolated) db.ReadFailures["F/m"]=new ItemFailedException("F/m",TextKey.ReadDropboxFailed,null);
        var queue=await BoundQueueAsync(pair,paths,pc,db,store,ct); FolderChecks.AdmitFullRepair(queue,pc,db);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct);
        Check.Require(db.Items.ContainsKey("new") && db.Items.ContainsKey("F")==isolated,isolated?"A folder holding an isolated Dropbox file was removed":"The control did not mirror the PC folder removal");
    });

    // One mapped hop owes its scope once, however many of its children leave it to the owed Repair.
    public static Task HopOwesOnceAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed); int owes=0;
        var counted=AutoSyncChecks.Observation.Wrap<IStore>((m,args)=>{ if(m.Name==nameof(IStore.OweScopeAsync)) Interlocked.Increment(ref owes); return AutoSyncChecks.Observation.Forward(store,m,args); });
        var parent=SyncedEntries.From(ItemChecks.File(Side.Local,"old",0,"pc",true),ItemChecks.File(Side.Dropbox,"old",0,"db",true));
        await store.CommitAsync(pair.PairId,null,parent,null,null,ct);
        pc.Seed(ItemChecks.File(Side.Local,"old",0,"pc",true),[]); db.Seed(ItemChecks.File(Side.Dropbox,"old",0,"db",true),[]);
        foreach(var name in new[]{"bad","worse"})
        {
            var localChild=ItemChecks.File(Side.Local,"old/"+name,1,"pc-"+name); var peerChild=ItemChecks.File(Side.Dropbox,"old/"+name,1,"db-"+name);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(localChild,peerChild),null,null,ct); pc.Seed(localChild,[1]); db.Seed(peerChild,[1]);
        }
        await pc.MoveAsync(pc.Items["old"].Entry,"new",ct); await db.MoveAsync(db.Items["old"].Entry,"new",ct);
        foreach(var name in new[]{"new/bad","new/worse"}) { var (denied,bytes)=pc.Items[name]; pc.Items[name]=(denied with {Skip=nameof(TextKey.SkipUnreadable)},bytes); }
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,db,store);
        var row=new Row(parent.LocalKey,pc.Items["new"].Entry,db.Items["new"].Entry,parent,[],null,RowContext.Neutral with {CompleteInventory=true,Destination="new"});
        await new global::Massdex.SyncShark.Engine.Executor.Effects().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new global::Massdex.SyncShark.Engine.Planner.SyncAction(ActionKind.Record,row),
            pc,db,counted,ItemChecks.Population(pair,paths,pc,db,counted),ItemChecks.Hop(pair,paths,queue,pc,db,counted),ct);
        Check.Require(owes==1 && (await store.PairAsync(pair.PairId,ct))?.OwedScopes?.Contains(new Scope("new","new"))==true,$"A mapped hop owed its scope {owes} times");
    });

    // A listed row whose bound Dropbox file is gone while its PC copy was edited under a restored timestamp is decided on its bytes: the edit uploads, nothing is recycled.
    public static Task StampRestoredEditBeatsDeleteAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var local=ItemChecks.File(Side.Local,"f",1,"pc-f"); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(local,ItemChecks.File(Side.Dropbox,"f",1,"db-f")),null,null,ct);
        pc.Seed(local,[2]); // same stamp, size and recorded hash as the binding; different bytes
        var queue=await BoundQueueAsync(pair,paths,pc,db,store,ct); FolderChecks.AdmitFullRepair(queue,pc,db);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct);
        Check.Require(pc.History.Count==0 && pc.Items.TryGetValue("f",out var kept) && kept.Bytes.SequenceEqual(new byte[]{2}),"A stamp-restored PC edit was recycled for a Dropbox delete");
        Check.Require(db.Items.TryGetValue("f",out var sent) && sent.Bytes.SequenceEqual(new byte[]{2}) && await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("f"),ct) is {LocalId:"pc-f"},"The edit was not uploaded under its own binding");
    });

    // After first sync a full Repair lists with deleted entries only while the deleted-name history is owed.
    public static async Task OwedHistoryAfterFirstAsync() { await OwedHistoryAsync(true); await OwedHistoryAsync(false); }
    private static Task OwedHistoryAsync(bool owed) => WithRepairStore(async (store,pair,ct)=>
    {
        await store.FinishRepairAsync(pair.PairId,(0,0),"{}",ct); var pc=new FakeSide(Side.Local); var cloud=new FakeSide(Side.Dropbox); int listings=0;
        var remote=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>{ if(m.Name==nameof(ISide.ReadAllAsync) && args[1] is ListOptions {IncludeDeleted:true}) listings++; return AutoSyncChecks.Observation.Forward(cloud,m,args); });
        var observed=AutoSyncChecks.Observation.Wrap<IStore>((m,args)=>m.Name==nameof(IStore.DeletedHistoryAsync) && args.Length==2 ? Task.FromResult<(string?,bool)>(("previous",owed)) : AutoSyncChecks.Observation.Forward(store,m,args));
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},new PathScope(pair,[pair],DriveType.Fixed),new WorkQueue(),pc,remote,observed,null,null,ct,caughtUp:true);
        Check.Require(listings==(owed?1:0),owed?"A full Repair after first sync did not re-acquire an owed deleted-name history":"A healthy history was acquired again");
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

    /// <summary>A Dropbox change page's deleted names join the saved deleted-name history together with its cursor.</summary>
    public static Task AutoSyncAppendsDeletedNamesAsync() => AutoSyncChecks.WithinDeadline(async ct=>
    {
        var directory=Directory.CreateTempSubdirectory();
        try
        {
            var image=Path.Combine(directory.FullName,"fixture.sqlite");
            using var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder {DataSource=image,Pooling=false}.ToString());
            await db.OpenAsync(ct);db.CreateFunction<string,string>("pathkey",PathNames.Key,isDeterministic:true);
            using var seed=db.CreateCommand();seed.CommandText=global::Massdex.SyncShark.Engine.Storage.Schema.Create+$"PRAGMA user_version={(global::Massdex.SyncShark.Engine.Storage.Schema.Version)};";await seed.ExecuteNonQueryAsync(ct);
            using var store=new global::Massdex.SyncShark.Engine.Storage.Store(image,false,ItemChecks.Settings); var pair=ItemChecks.Pair();await store.SavePairAsync(pair,ct);
            seed.CommandText="UPDATE pair SET deleted_history_cursor='previous',deleted_history_owed=0 WHERE pair_id=$id;";seed.Parameters.AddWithValue("$id",pair.PairId);await seed.ExecuteNonQueryAsync(ct);
            await store.SaveCursorAsync(pair.PairId,"next",[PathNames.Key("Gone")],ct);
            Check.Require(await store.CursorAsync(pair.PairId,ct)=="next" && await store.DeletedNameAsync(pair.PairId,null,PathNames.Key("Gone"),ct) && !await store.DeletedNameAsync(pair.PairId,null,PathNames.Key("Kept"),ct),
                "A Dropbox deletion seen by AutoSync was not appended to the saved deleted names with its cursor");
        }
        finally {directory.Delete(true);}
    });

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
            seed.CommandText="UPDATE pair SET deleted_history_cursor='previous',deleted_history_owed=0 WHERE pair_id=$id; INSERT INTO discovery_deleted_block SELECT id,$old,1,$bytes FROM pair WHERE pair_id=$id; INSERT INTO discovery_deleted_delta(pair_id,path_key,spent) SELECT id,pathkey('append'),0 FROM pair WHERE pair_id=$id;";
            seed.Parameters.AddWithValue("$id",pair.PairId);seed.Parameters.AddWithValue("$old",old);seed.Parameters.AddWithValue("$bytes",bytes);await seed.ExecuteNonQueryAsync(ct);
            var pc=new FakeSide(Side.Local);var cloud=new FakeSide(Side.Dropbox);var paths=new PathScope(pair,[pair],DriveType.Fixed);var queue=new WorkQueue();
            cloud.DeletedHistory["fresh"]=new DeletionEvidence.DeletedPath("fresh","fresh");
            int listings=0;var remote=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>{
                if(m.Name==nameof(ISide.ReadAllAsync) && args[1] is ListOptions {IncludeDeleted:true}) listings++;
                return AutoSyncChecks.Observation.Forward(cloud,m,args);
            });
            var observed=AutoSyncChecks.Observation.Wrap<IStore>((m,args)=>m.Name==nameof(IStore.FinishRepairAsync) && args[1] is ValueTuple<long,long> revisions ? Finish(revisions,(string)args[2]!) : AutoSyncChecks.Observation.Forward(store,m,args));
            async Task<(bool Local,bool Dropbox)> Finish((long,long) revisions,string summary)
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

    internal static async Task<(PairConfig Pair,FakeSide Pc,FakeSide Db,WorkQueue Queue)> CoveredQueueAsync(IStore store,PairConfig pair,CancellationToken token)
    {
        pair=pair with {Roots=ItemChecks.SavedRoots};
        await store.SavePairAsync(pair,token);
        var pc=Rooted(new FakeSide(Side.Local),pair); var db=Rooted(new FakeSide(Side.Dropbox),pair);
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
        Check.Require(await store.FinishRepairAsync(bound.PairId,(local.Revision,remote.Revision),"{}",ct) is (true,true),"Meeting fixture did not finish first Repair");
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

    // A moved folder's unreadable child takes no effect in the mapping pass and owes the moved folder's scope, so its own Repair reports it.
    public static Task HopUnreadableLeafOwesScopeAsync() => WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var parent=SyncedEntries.From(ItemChecks.File(Side.Local,"old",0,"pc",true),ItemChecks.File(Side.Dropbox,"old",0,"db",true));
        await store.CommitAsync(pair.PairId,null,parent,null,null,ct);
        var localChild=ItemChecks.File(Side.Local,"old/bad",1,"denied"); var peerChild=ItemChecks.File(Side.Dropbox,"old/bad",1,"remote-bad");
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(localChild,peerChild),null,null,ct);
        pc.Seed(ItemChecks.File(Side.Local,"old",0,"pc",true),[]); db.Seed(ItemChecks.File(Side.Dropbox,"old",0,"db",true),[]);
        pc.Seed(localChild,[1]); db.Seed(peerChild,[1]);
        await pc.MoveAsync(pc.Items["old"].Entry,"new",ct); await db.MoveAsync(db.Items["old"].Entry,"new",ct);
        var (denied,bytes)=pc.Items["new/bad"]; pc.Items["new/bad"]=(denied with {Skip=nameof(TextKey.SkipUnreadable)},bytes);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,db,store); int pcWrites=pc.Mutations.Count, dbWrites=db.Mutations.Count;
        var row=new Row(parent.LocalKey,pc.Items["new"].Entry,db.Items["new"].Entry,parent,[],null,RowContext.Neutral with {CompleteInventory=true,Destination="new"});
        await new global::Massdex.SyncShark.Engine.Executor.Effects().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,
            new global::Massdex.SyncShark.Engine.Planner.SyncAction(ActionKind.Record,row),
            pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),ct);
        Check.Require(pc.Mutations.Count==pcWrites && db.Mutations.Count==dbWrites && pc.Items.ContainsKey("new/bad") && db.Items.TryGetValue("new/bad",out var peer) && peer.Entry.Id==peerChild.Id && peer.Bytes.SequenceEqual(new byte[]{1}),
            "The mapping pass took an effect on an unreadable child");
        Check.Require((await store.PairAsync(pair.PairId,ct))?.OwedScopes?.Contains(new Scope("new","new"))==true,"An unreadable mapped child owed no scoped Repair, so no notice can follow");
    });

    // A walk reads the file itself, so an edit made through an out-of-pair hard link reaches Dropbox.
    public static async Task HardLinkEditThroughOutsideNameAsync(CancellationToken ct)
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
            var dropbox=Rooted(new FakeSide(Side.Dropbox),pair);
            var localB=(await local.ReadOneAsync("b",ct))!;
            var oldBytes=File.ReadAllBytes(a);
            var peerB=DropboxPeer(localB,"db-b",oldBytes);
            dropbox.Seed(peerB,oldBytes);
            var seeded=SyncedEntries.From(localB,peerB);
            await store.CommitAsync(pair.PairId,null,seeded,null,null,ct);
            using(var stream=new FileStream(a,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)) stream.Write("abc"u8);
            // Independently known append result read through the outside alias, so b is never opened before the walk observes it.
            var expected=File.ReadAllBytes(a);
            // Qualify the stale premise before the credited walk: b's cached tuple must still hold the seeded binding and differ from the
            // current values read through a, so an ordinary read/verification failure is not credited as the missed edit.
            var rowB=new DirectoryInfo(root).EnumerateFiles("b").Single();
            long rowBTime; try { rowBTime=rowB.LastWriteTimeUtc.Ticks; } catch(ArgumentOutOfRangeException) { throw new NotImplementedException("b's cached row time is unrepresentable; the stale-walk case cannot be armed"); }
            var rowA=new DirectoryInfo(outside).EnumerateFiles("a").Single();
            if(rowB.Length!=seeded.LocalSize || rowBTime!=seeded.LocalModifiedTicks || rowB.Length==rowA.Length && rowBTime==rowA.LastWriteTimeUtc.Ticks)
                throw new NotImplementedException("b's cached row does not hold the unchanged binding tuple; the stale-walk case cannot be armed");
            var queue=await BoundQueueAsync(pair,paths,local,dropbox,store,ct);
            await store.OweRepairAsync(pair.PairId,Side.Local,"journal lost",ct);
            await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,dropbox,store,null,null,ct);
            Check.Require(dropbox.Items.TryGetValue("b",out var published) && published.Bytes.SequenceEqual(expected),"the out-of-pair edit was not uploaded to Dropbox");
            var binding=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("b"),ct);
            Check.Require(binding is { LocalSize:5 } && store.OpenNotices.Count==0,"the binding was not updated or a notice was raised");
        }
        finally { home.Delete(true); }
    }

    // A denied file keeps exactly its listing values and adds no failure; once readable the edit is seen.
    public static async Task UnreadableLinkedFileBehavesAsBaseAsync(CancellationToken ct)
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
            var dropbox=Rooted(new FakeSide(Side.Dropbox),pair);
            var seeded=new Dictionary<string,byte[]>(); var peers=new Dictionary<string,Entry>();
            foreach(var name in new[]{"b","c","d"})
            {
                var le=(await local.ReadOneAsync(name,ct))!;
                var bytes=File.ReadAllBytes(Path.Combine(root,name)); seeded[name]=bytes;
                var peer=DropboxPeer(le,"db-"+name,bytes); dropbox.Seed(peer,bytes); peers[name]=peer;
                await store.CommitAsync(pair.PairId,null,SyncedEntries.From(le,peer),null,null,ct);
            }
            var originalB=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("b"),ct);
            using(var sb=new FileStream(a,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)) sb.Write("abc"u8);
            using(var sc=new FileStream(c,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)) sc.Write("xy"u8);
            // The intended local bytes, captured through the outside alias (b is never opened) and from c, so a repair that
            // restores the old peer over the edit cannot match them.
            var editedB=File.ReadAllBytes(a);
            var editedC=File.ReadAllBytes(c);
            // The deny ACE lives on the shared file; apply and restore each through the outside link so the listed name's cached
            // row is never opened and stays stale. Each ACE change is bound to its own cleanup scope.
            var originalBAcl=AclFixture.Deny(a,AclFixture.MetadataAccess);
            try
            {
                var originalDAcl=AclFixture.Deny(d,AclFixture.MetadataAccess);
                try
                {
                    // The deny fixture must block the same metadata open the product makes, backup intent included.
                    if(!AclFixture.Denied(b,backup:true) || !AclFixture.Denied(d,backup:true)) throw new NotImplementedException("the deny fixture did not block the product's metadata open");
                    // Qualify the stale premise before any product assertion: b's cached tuple must still hold the original binding and
                    // differ from the current values read through a.
                    var rowB=new DirectoryInfo(root).EnumerateFiles("b").Single();
                    long rowBTime; try { rowBTime=rowB.LastWriteTimeUtc.Ticks; } catch(ArgumentOutOfRangeException) { throw new NotImplementedException("b's cached row time is unrepresentable; the stale-denial case cannot be armed"); }
                    var rowA=new DirectoryInfo(outside).EnumerateFiles("a").Single();
                    if(originalB is null || rowB.Length!=originalB.LocalSize || rowBTime!=originalB.LocalModifiedTicks || rowB.Length==rowA.Length && rowBTime==rowA.LastWriteTimeUtc.Ticks)
                        throw new NotImplementedException("b's cached row does not hold the unchanged binding tuple; the stale-denial case cannot be armed");
                    var oneB=global::Massdex.SyncShark.Engine.Local.DirectoryListing.One(root,"b");
                    // A file whose own metadata cannot be read is unreadable; it never falls back to the cached listing values.
                    Check.Require(oneB is {Skip:nameof(TextKey.SkipUnreadable)},"a denied file fell back to its listing values");
                    int rows=0;
                    var counted=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=> m.Name==nameof(ISide.ReadAllAsync)
                        ? Counted((IAsyncEnumerable<Listed>)AutoSyncChecks.Observation.Forward(local,m,args)!,()=>Interlocked.Increment(ref rows))
                        : AutoSyncChecks.Observation.Forward(local,m,args));
                    var queue=await BoundQueueAsync(pair,paths,local,dropbox,store,ct);
                    await store.OweRepairAsync(pair.PairId,Side.Local,"journal lost",ct);
                    await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,counted,dropbox,store,null,null,ct);
                    Check.Require(rows>0,"the forced Repair did not enumerate its walk");
                    Check.Require(dropbox.Items.TryGetValue("c",out var publishedC) && publishedC.Bytes.SequenceEqual(editedC),"the normally edited sibling was not uploaded");
                    Check.Require(dropbox.Items.TryGetValue("b",out var peerOut) && peerOut.Bytes.SequenceEqual(seeded["b"]) && peerOut.Entry==peers["b"],"a denied unreadable file changed its Dropbox peer");
                    var stillB=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("b"),ct);
                    Check.Require(originalB is not null && stillB == originalB,"the denied file's binding changed");
                    // Each denied file is its own item's failure: one ReadLocalFailed notice each, and the Repair still completes.
                    Check.Require(store.OpenNotices.Count==2 && store.OpenNotices.All(n=>n.Kind==NoticeKind.Failure && n.Cause==TextKey.ReadLocalFailed) &&
                        store.OpenNotices.Select(n=>n.Path).OrderBy(p=>p,StringComparer.Ordinal).SequenceEqual(["b","d"]),"a denied file did not get exactly its own failure notice");
                    Check.Require((await store.ReadRepairAsync(pair.PairId,Side.Local,ct)).Reason==null,"the Repair debt was not cleared");
                }
                finally { AclFixture.Restore(d,originalDAcl); }
            }
            finally { AclFixture.Restore(a,originalBAcl); }
            // With the DACL restored the same file is readable; a fresh full walk must now see the edit. The walk is the
            // positive control, so nothing opens b between the restore and the observation.
            int recoveryRows=0;
            var recoveryCounted=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=> m.Name==nameof(ISide.ReadAllAsync)
                ? Counted((IAsyncEnumerable<Listed>)AutoSyncChecks.Observation.Forward(local,m,args)!,()=>Interlocked.Increment(ref recoveryRows))
                : AutoSyncChecks.Observation.Forward(local,m,args));
            var recovery=await BoundQueueAsync(pair,paths,local,dropbox,store,ct);
            await store.OweRepairAsync(pair.PairId,Side.Local,"journal lost",ct);
            await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,recovery,recoveryCounted,dropbox,store,null,null,ct);
            Check.Require(recoveryRows>0,"the recovery Repair did not run another full walk");
            Check.Require(dropbox.Items.TryGetValue("b",out var publishedB) && publishedB.Bytes.SequenceEqual(editedB) && File.ReadAllBytes(b).SequenceEqual(editedB),
                "the edit was not observed on both sides once the file became readable");
        }
        finally { home.Delete(true); }
    }

    // The sole in-scope alias of a hard-linked file retried after a failure uploads the current edit, not a stale revision.
    public static async Task RetryHardLinkMemberAsync(CancellationToken ct)
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
            var dropbox=Rooted(new FakeSide(Side.Dropbox),pair);
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
    // A PC revision that moves during a full Repair keeps the PC owed, while the run still publishes its completion and the Dropbox side clears.
    public static async Task FullRepairFinishesPerSideAsync()
    {
        using var store=new InMemoryStateStore();
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var cloud=new FakeSide(Side.Dropbox);
        await store.SavePairAsync(pair,default);
        await store.OweRepairAsync(pair.PairId,Side.Local,"lost",default); await store.OweRepairAsync(pair.PairId,Side.Dropbox,"reset",default);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,cloud,store);
        var observed=AutoSyncChecks.Observation.Wrap<IStore>((m,args)=>m.Name==nameof(IStore.FinishRepairAsync) && args[1] is ValueTuple<long,long> ? Moved(m,args) : AutoSyncChecks.Observation.Forward(store,m,args));
        async Task<(bool Local,bool Dropbox)> Moved(System.Reflection.MethodInfo m,object?[] args)
        {
            await store.OweRepairAsync(pair.PairId,Side.Local,"lost",default);
            return await (Task<(bool Local,bool Dropbox)>)AutoSyncChecks.Observation.Forward(store,m,args)!;
        }
        var events=new List<RunEvent>(); int repaired=0;
        await Repair.RunAsync(pair,ItemChecks.Settings,e=>{lock(events) events.Add(e);},paths,queue,pc,cloud,observed,null,null,default,localRepaired:()=>repaired++);
        var state=await store.RepairStateAsync(pair.PairId,default);
        Check.Require(state.LastRepairJson!=null && !state.FirstRepairDone,"A full Repair whose PC side moved did not publish its completion, or published the first Repair");
        Check.Require((await store.ReadRepairAsync(pair.PairId,Side.Local,default)).Reason!=null && (await store.ReadRepairAsync(pair.PairId,Side.Dropbox,default)).Reason==null,"The moved PC side was cleared or the matched Dropbox side stayed owed");
        var moved=Text.Get(TextKey.RepairSuperseded,(TextArg.Side,Text.Get(TextKey.HumanSideLocal)));
        Check.Require(repaired==0 && events.Count(e=>e.Detail==moved)==1 && !events.Any(e=>e.Detail==Text.Get(TextKey.RepairSuperseded,(TextArg.Side,Text.Get(TextKey.HumanSideDropbox)))),"The PC-side completion ran, or the log did not name only the PC side as moved");
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
    private static extern bool CreateHardLinkW(string newName,string existing,IntPtr security);
}
