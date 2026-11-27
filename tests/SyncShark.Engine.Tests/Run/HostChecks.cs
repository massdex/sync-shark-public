using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Host startup and read-only commands never create an absent store.</summary>
public static class HostChecks
{
    public static async Task RunAsync()
    {
        var opens = new List<bool>();
        var host = new Host((create,_) => {opens.Add(create);return Task.FromResult<IStore?>(null);},
            (_,_,_,_)=>throw new InvalidOperationException(),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,
            _=>Task.FromResult("connection"),(_,_)=>Task.CompletedTask);
        await host.StartAsync(default);
        await foreach(var _ in host.PairsAsync(default)) throw new InvalidDataException("Invented pair");
        Check.Require(opens.Count>0 && !opens.Contains(true),"Read-only command requested store creation");
        await host.PauseAsync(null,default); await host.ResumeAsync("pair",default);
        Check.Require(host.IsPaused("pair"),"Pair resume undid global pause");
        await host.PauseAsync("other",default); await host.ResumeAsync(null,default);
        Check.Require(!host.IsPaused("pair") && host.IsPaused("other"),"Global resume lost a pair pause");
        var before=opens.Count;
        try { await host.SetExclusionAsync("pair",new(Side.Local,"../outside",false),false,true,default); throw new InvalidDataException("Accepted an escaping exclusion"); }
        catch(EngineRequestException e) when(e.Key==TextKey.ExclusionPathInvalid) { }
        Check.Require(before==opens.Count,"Invalid exclusion reached storage");
        host.SetStartupMessage("switch refused",true);
        await host.StartAsync(default); Check.Require(await host.StatusAsync(null,default)=="switch refused" && before==opens.Count,"Switch refusal opened the store");
        var stop=host.Stopping; await host.StopAsync(default); Check.Require(stop.IsCancellationRequested,"Host did not notify the pipe owner of stop");
        var errors=new List<string>();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? missing=null;
        foreach(var check in new Func<Task>[]{IndependentBind,MemberRewind,ForeignCancellation,DisconnectedAdapter,ExplicitBind,PendingOuterScope,RemovingMember,ReleaseFailure,FailedFirstRead,OldPublicationFence,FailedExplicitRepairAsync,FailedNoticeWriteAsync,UnexpectedFailureLogged,StopAfterDrainFailure,NoticeStreamReleasesCommands,MalformedPairRemovable,OwedScopeIsolationAsync,OwedScopeHandOffAsync,OwedScopeReadFailureAsync,ExplicitRepairPartialInventoryWakesAsync,DirectSettlementFailureWakesAsync,FullRepairOwedScopeCompletesAsync,RetryStallPauseOtherPairAsync,RetryStallHostStopAsync})
            try { await check(); }
            catch(NotImplementedException e) {missing??=System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);}
            catch(InvalidDataException e) {errors.Add($"{check.Method.Name}: {e}");}
        Check.Require(errors.Count==0,string.Join("; ",errors));
        missing?.Throw();
    }

    // Run at integration with the real planner, job checker and ordinary retry. No emulated callback qualifies as a pass.
    public static async Task RetryAtHostAsync()
    {
        using var store=new InMemoryStateStore();var p=Bound("selected",@"T:\Selected",1);await store.SavePairAsync(p,default);
        var pc=new FakeSide(Side.Local){Root=(p.Roots!.Local,null,null,DriveType.Fixed)};
        var cloud=new FakeSide(Side.Dropbox){Root=(null,p.Roots.Dropbox,null,null)};
        var entered=Signal();var release=Signal();
        var proxy=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var probe=(JournalProbe)(object)proxy;
        probe.Local=cloud;probe.Job=async ct=>{entered.TrySetResult();await release.Task.WaitAsync(ct);return new JobReply.Running();};
        var parent=ItemChecks.File(Side.Local,"local-folder",0,"local-folder-id",true);
        var remote=ItemChecks.File(Side.Dropbox,"cloud-folder",0,"cloud-folder-id",true);
        await store.CommitAsync(p.PairId,null,SyncedEntries.From(parent,remote),null,null,default);
        var notice=new Notice(1,NoticeKind.Failure,TextKey.FailChanged,"local-folder/item",new([new(Side.Dropbox,"other-leg","other-id"),new(Side.Local,"local-folder/item","item-id")],Side.Local),DateTimeOffset.UnixEpoch,false);
        await store.SaveNoticeAsync(p.PairId,notice,default);await store.SaveJobAsync(new(p.PairId,"files/delete_batch","job",[new("cloud-folder-id","cloud-folder",null)]),default);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_)=>(pc,proxy),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try
        {
            Notice? selected=null;await foreach(var n in host.NoticesAsync(p.PairId,default)) selected=n;
            var retry=host.ResolveAsync(p.PairId,selected!.Id,selected.Reviewed!,ResolutionChoice.Retry,null,default);
            if(await Task.WhenAny(entered.Task,retry).WaitAsync(TimeSpan.FromSeconds(5))==retry) await retry;
            Check.Require(entered.Task.IsCompleted && !retry.IsCompleted,"Host skipped or failed to await the accepted job check");
            release.TrySetResult();await retry;Check.Require(probe.JobReads==1,"Manual Retry did not check the accepted job exactly once");
            Check.Require(pc.Requests[nameof(ISide.ReadByIdAsync)]==0 && store.OpenNotices.Any(n=>n.Id==selected.Id),"Ordinary Retry ran for a child of a still-running accepted folder job");
            var free=notice with {Path="disjoint",Subject=new([new(Side.Local,"disjoint","disjoint-id")],Side.Local)};
            await store.SaveNoticeAsync(p.PairId,free,default);
            await foreach(var n in host.NoticesAsync(p.PairId,default)) if(n.Path==free.Path) selected=n;
            var reads=pc.Requests[nameof(ISide.ReadByIdAsync)];
            await host.ResolveAsync(p.PairId,selected!.Id,selected.Reviewed!,ResolutionChoice.Retry,null,default);
            Check.Require(pc.Requests[nameof(ISide.ReadByIdAsync)]>reads && probe.JobReads==1,"Disjoint ordinary Retry never reobserved its selected identity");
        }
        finally{release.TrySetResult();await host.StopAsync(default);}
    }
    // A Retry whose job read retries in place holds the command gate; Pause of another pair and Stop must still end it and reply.
    private static Task RetryStallPauseOtherPairAsync() => RetryStall(host=>host.PauseAsync("other",default),false);
    private static Task RetryStallHostStopAsync() => RetryStall(host=>host.StopAsync(default),true);
    private static async Task RetryStall(Func<Host,Task> decisive,bool stops)
    {
        using var store=new InMemoryStateStore();var p=Bound("stalled",@"T:\Stalled",1);await store.SavePairAsync(p,default);
        var pc=new FakeSide(Side.Local){Root=(p.Roots!.Local,null,null,DriveType.Fixed)};
        var cloud=new FakeSide(Side.Dropbox){Root=(null,p.Roots.Dropbox,null,null)};
        var entered=Signal();
        var proxy=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var probe=(JournalProbe)(object)proxy;
        probe.Local=cloud;probe.Job=async ct=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);return new JobReply.Running();}; // ends only by cancellation
        var parent=ItemChecks.File(Side.Local,"local-folder",0,"local-folder-id",true);
        var remote=ItemChecks.File(Side.Dropbox,"cloud-folder",0,"cloud-folder-id",true);
        await store.CommitAsync(p.PairId,null,SyncedEntries.From(parent,remote),null,null,default);
        var notice=new Notice(1,NoticeKind.Failure,TextKey.FailChanged,"local-folder/item",new([new(Side.Dropbox,"other-leg","other-id"),new(Side.Local,"local-folder/item","item-id")],Side.Local),DateTimeOffset.UnixEpoch,false);
        await store.SaveNoticeAsync(p.PairId,notice,default);await store.SaveJobAsync(new(p.PairId,"files/delete_batch","job",[new("cloud-folder-id","cloud-folder",null)]),default);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_)=>(pc,proxy),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        Task? retry=null;
        try
        {
            Notice? selected=null;await foreach(var n in host.NoticesAsync(p.PairId,default)) selected=n;
            retry=host.ResolveAsync(p.PairId,selected!.Id,selected.Reviewed!,ResolutionChoice.Retry,null,default);
            if(await Task.WhenAny(entered.Task,retry).WaitAsync(TimeSpan.FromSeconds(5))==retry) await retry;
            var command=decisive(host);
            if(await Task.WhenAny(command,Task.Delay(TimeSpan.FromSeconds(10)))!=command) throw new InvalidDataException("A command waited forever behind a stalled gated Retry");
            await command;
            if(await Task.WhenAny(retry,Task.Delay(TimeSpan.FromSeconds(10)))!=retry) throw new InvalidDataException("The stalled Retry was not cancelled");
            Check.Require(retry.IsCanceled || retry.IsFaulted,"An interrupted Retry reported success");
            Check.Require(store.OpenNotices.Any(n=>n.Id==selected.Id),"An interrupted Retry closed its notice");
            Check.Require(stops || !host.IsPaused(p.PairId),"Pausing another pair paused the stalled pair");
        }
        finally { if(!stops && retry?.IsCompleted!=false) await host.StopAsync(default); }
    }
    // The retained feed callback still cannot observe its ID; only exact-version PutBack can restore these bytes.
    public static async Task PutBackAtHostAsync()
    {
        var pair=Bound("putback",@"T:\PutBack",1);using var store=new InMemoryStateStore();
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        var logs=new MemoryLogs();using(var preflight=new RunLog(logs,"putback",ItemChecks.Settings)){}
        var local=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var pc=(JournalProbe)(object)local;
        pc.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};pc.Quiet=true;
        var remote=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var db=(JournalProbe)(object)remote;
        db.Local=new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};db.Quiet=true;
        var v=ItemChecks.File(Side.Dropbox,"item",3,"original");var admitted=Signal();
        // A second put-back subject under the bound Dropbox folder Photos, held the same way as the first.
        var x=ItemChecks.File(Side.Dropbox,"Photos/x",4,"x-id");
        db.Feed=async (cursor,token)=>{if(cursor==null)return new([new(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false),new(Side.Dropbox,ChangeKind.Changed,x.Path,null,x.Id,x,false,false)],"one",null);
            admitted.TrySetResult();await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();};
        // A held PC input at the bound folder Pics (Dropbox Photos) overlaps a put-back at Dropbox Photos/x only through that binding.
        var pics=ItemChecks.File(Side.Local,"Pics",0,"pics-id",true);var photos=ItemChecks.File(Side.Dropbox,"Photos",0,"photos-id",true);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(pics,photos),null,null,default);pc.Local.Seed(pics,[]);db.Local.Seed(photos,[]);
        int pcReads=0;var removalAdmitted=Signal();
        pc.Feed=async (_,token)=>{if(Interlocked.Increment(ref pcReads)==1)return new([new(Side.Local,ChangeKind.Deleted,pics.Path,null,pics.Id,null,false,true)],null,new(1,5));
            removalAdmitted.TrySetResult();await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();};
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));var ct=stop.Token;
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_)=>(local,remote),ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try{
            await host.StartAsync(ct);await admitted.Task.WaitAsync(ct);
            Check.Require(await store.CursorAsync(pair.PairId,ct)==null && db.Local.Requests[nameof(ISide.ReadOneAsync)]>0,"Missing feed identity did not leave the page unsettled");
            // Public status counts both held Dropbox changes toward this PC and the held PC change toward Dropbox.
            await removalAdmitted.Task.WaitAsync(ct);var waiting=Text.Get(TextKey.StatusPendingIncoming,(TextArg.Pair,pair.PairId),(TextArg.Count,"2"),(TextArg.Total,"1"));
            Check.Require((await host.StatusAsync(pair.PairId,ct)).Contains(waiting,StringComparison.Ordinal),"Status did not show the held changes in both directions");
            db.Local.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.File(v,new MemoryStream([3]));
            await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,v.Path,new([new(Side.Dropbox,v.Path,v.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false),ct);
            Notice? selected=null;await foreach(var n in host.NoticesAsync(pair.PairId,ct))if(n.Kind==NoticeKind.PutBack)selected=n;
            await host.ResolveAsync(pair.PairId,selected!.Id,selected.Reviewed!,ResolutionChoice.Retry,null,ct);
            Check.Require(!store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack) && pc.Local.Items.TryGetValue(v.Path,out var a) && a.Bytes.SequenceEqual(new byte[]{3}) && db.Local.Items.TryGetValue(v.Path,out var b) && b.Bytes.SequenceEqual(new byte[]{3}),"Matching held callback swallowed the exact PutBack restore");
            var partial=Text.Get(TextKey.StatusPendingIncoming,(TextArg.Pair,pair.PairId),(TextArg.Count,"1"),(TextArg.Total,"1"));
            Check.Require((await host.StatusAsync(pair.PairId,ct)).Contains(partial,StringComparison.Ordinal),"Settling the first Dropbox change decremented the wrong incoming count");
            // Closing the mapped put-back releases the held PC removal at its PC-side ancestor.
            db.Local.Recoverable[(x.Id,x.Revision,x.ContentHash)]=new Recovery.File(x,new MemoryStream([4]));
            await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,x.Path,new([new(Side.Dropbox,x.Path,x.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false),ct);
            selected=null;await foreach(var n in host.NoticesAsync(pair.PairId,ct))if(n.Kind==NoticeKind.PutBack)selected=n;
            // Both original folder identities have now gone; replacements own the same names. Only the mapped wake can retire the old binding.
            pc.Local.Items[pics.Path]=(pics with {Id="new-pics"},[]);db.Local.Items[photos.Path]=(photos with {Id="new-photos"},[]);
            Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(pics.Path),ct)!=null,"The removal settled before PutBack Retry");
            await host.ResolveAsync(pair.PairId,selected!.Id,selected.Reviewed!,ResolutionChoice.Retry,null,ct);
            Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(pics.Path),ct)==null,"PutBack Retry left the mapped folder removal unsettled");
            Check.Require(pc.Local.Items.TryGetValue("Pics/x",out var left) && left.Bytes.SequenceEqual(new byte[]{4}) && db.Local.Items.TryGetValue(x.Path,out var right) && right.Bytes.SequenceEqual(new byte[]{4}),"Settling the old removal lost the restored bytes");
            var empty=Text.Get(TextKey.StatusPendingIncoming,(TextArg.Pair,pair.PairId),(TextArg.Count,"0"),(TextArg.Total,"0"));
            Check.Require((await host.StatusAsync(pair.PairId,ct)).Contains(empty,StringComparison.Ordinal),"Settled changes stayed pending");
        }finally{stop.Cancel();await host.StopAsync(default);}
    }
    // Two failed reads for one account, then the next successful page without restarting the Host.
    public static async Task FailedMemberReadAsync()
    {
        using var store=new InMemoryStateStore();var pair=Bound("retry",@"T:\Retry",1);
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        var logs=new MemoryLogs();using(var preflight=new RunLog(logs,"retry",ItemChecks.Settings)){}
        var ended=Signal();logs.Closed=()=>ended.TrySetResult();var seen=Signal();int tries=0;
        var initial=Signal();var boundary=Signal();var release=Signal();var requested=Signal();var finished=Signal();int boundaries=0;
        var pc=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var lp=(JournalProbe)(object)pc;lp.Quiet=true;lp.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};
        var cloud=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var rp=(JournalProbe)(object)cloud;rp.Quiet=true;rp.Local=new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        lp.Boundary=async token=>{if(Interlocked.Increment(ref boundaries)==2){boundary.TrySetResult();await release.Task.WaitAsync(token);}return (null,null);};
        var item=ItemChecks.File(Side.Dropbox,"resumed",3,"item-id");
        rp.Feed=async (_,token)=>{
            switch(Interlocked.Increment(ref tries)){
            case 1:await initial.Task.WaitAsync(token);goto case 2;
            case 2:throw new ItemFailedException(string.Empty,TextKey.SignInReturnRetry,null);
            case 3:rp.Local.Seed(item,[3]);return new([new(Side.Dropbox,ChangeKind.Changed,item.Path,null,item.Id,item,true,false)],"resumed",null);
            default:await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();}};
        var ledger=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var tap=(PublicationStore)(object)ledger;tap.Store=store;tap.CursorSaved=()=>seen.TrySetResult();tap.Requested=()=>requested.TrySetResult();tap.Finished=()=>finished.TrySetResult();
        await store.SavePositionAsync(pair.Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),new(1,11),default);await store.SaveCursorAsync(pair.PairId,"start",default);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(ledger),(_,_,_,_,_)=>Task.FromResult<(ISide Local,ISide Dropbox)>((pc,cloud)),ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask,(_,_,_)=>{initial.TrySetResult();return Task.CompletedTask;});
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try{
            await host.StartAsync(stop.Token);await boundary.Task.WaitAsync(stop.Token);
            var oneSignIn=store.OpenNotices.Count(n=>n.Kind==NoticeKind.Failure)==1 && store.OpenNotices.Any(n=>n.Cause==TextKey.SignInReturnRetry);
            var repair=host.RepairAsync(pair.PairId,stop.Token);await requested.Task.WaitAsync(stop.Token);
            // The startup retry is still blocked and its boot result unknown: the Repair does not wait for it, it lists both sides and finishes.
            await logs.RepairOpened.Task.WaitAsync(stop.Token);
            try{await finished.Task.WaitAsync(stop.Token);}catch(OperationCanceledException){}
            Check.Require(finished.Task.IsCompleted && !release.Task.IsCompleted,"Repair waited for the blocked AutoSync startup retry");
            release.TrySetResult();await repair;
            await Task.WhenAny(seen.Task,ended.Task).WaitAsync(stop.Token);
            Check.Require(seen.Task.IsCompleted && !ended.Task.IsCompleted && lp.Local.Items.TryGetValue(item.Path,out var copy) && copy.Bytes.SequenceEqual(new byte[]{3}) && oneSignIn,"Failed member read ended live sync instead of retrying with one sign-in notice");
        }finally{stop.Cancel();await host.StopAsync(default);}
    }
    public static Task FailedExplicitRepairAsync()=>FailureRecovery(true);
    // An AutoSync failure and the rewind after it leave a running explicit Repair to finish; a root that is gone at that rewind stops it.
    public static async Task RepairOutlivesLiveFailureAsync() { await RepairAtRewind(false); await RepairAtRewind(true); }
    private static async Task RepairAtRewind(bool rootGone)
    {
        using var raw=new InMemoryStateStore();var pair=Bound("survive",@"T:\Survive",1);
        await raw.SavePairAsync(pair,default);await raw.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        var boot=Signal();var failRead=Signal();var rewound=Signal();var entered=Signal();var release=Signal();var finished=Signal();int reads=0;bool gone=false;
        // The full Repair waits at its finish on its own token, so a cancelled Repair surfaces instead of hanging.
        var store=AutoSyncChecks.Observation.Wrap<IStore>((m,args)=>m.Name==nameof(IDisposable.Dispose) ? null
            : m.Name==nameof(IStore.FinishRepairAsync) && args.Length==4 && args[1] is not Scope ? Gate(raw,m,args) : AutoSyncChecks.Observation.Forward(raw,m,args));
        async Task<(bool Local,bool Dropbox)> Gate(IStore inner,System.Reflection.MethodInfo m,object?[] args)
        {
            entered.TrySetResult();await release.Task.WaitAsync((CancellationToken)args[3]!);
            var ok=await (Task<(bool Local,bool Dropbox)>)AutoSyncChecks.Observation.Forward(inner,m,args)!;if(ok is (true,true))finished.TrySetResult();return ok;
        }
        var local=QuietSide(new FakeSide(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)});var pc=(JournalProbe)(object)local;
        pc.ReadRoot=()=>{if(Volatile.Read(ref gone))throw new ItemFailedException(pair.Name,TextKey.LocalRootMissing,new IOException("gone",unchecked((int)0x80070003)));};
        var remote=QuietSide(new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)});var cloud=(JournalProbe)(object)remote;
        cloud.Feed=async (_,ct)=>{if(Interlocked.Increment(ref reads)==1){await failRead.Task.WaitAsync(ct);throw new ItemFailedException(string.Empty,TextKey.SignInReturnRetry,null);}
            rewound.TrySetResult();await Task.Delay(Timeout.Infinite,ct);throw new InvalidOperationException();};
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_,_)=>Task.FromResult((local,remote)),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask,(_,_,_)=>{boot.TrySetResult();return Task.CompletedTask;});
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));var token=stop.Token;
        try{
            await host.StartAsync(token);await boot.Task.WaitAsync(token);
            var repair=host.RepairAsync(pair.PairId,token);await entered.Task.WaitAsync(token);
            Volatile.Write(ref gone,rootGone);failRead.SetResult();
            if(rootGone)
            {
                bool stopped=false;
                try{await repair.WaitAsync(TimeSpan.FromSeconds(10),token);}catch(OperationCanceledException) when(!token.IsCancellationRequested){stopped=true;}
                catch(TimeoutException e){throw new InvalidDataException("A root that was gone at the rewind left the Repair running",e);}
                Check.Require(stopped && !finished.Task.IsCompleted,"A Repair finished after its root was gone");
                Check.Require(raw.OpenNotices.Any(n=>n.Kind==NoticeKind.RootIdentity && n.Cause==TextKey.RootUnavailable),"A root that was gone raised no root notice");
                return;
            }
            try{await rewound.Task.WaitAsync(TimeSpan.FromSeconds(10),token);}
            catch(TimeoutException e){throw new InvalidDataException("AutoSync waited behind the running Repair instead of rewinding",e);}
            Check.Require(!repair.IsCompleted,"An AutoSync failure ended the running Repair");
            release.TrySetResult();
            try{await repair.WaitAsync(token);}catch(OperationCanceledException e) when(!token.IsCancellationRequested){throw new InvalidDataException("An AutoSync failure cancelled the running Repair",e);}
            Check.Require(finished.Task.IsCompleted,"The Repair did not run to completion after an AutoSync failure");
        }finally{release.TrySetResult();stop.Cancel();await host.StopAsync(default);}
    }
    // A temporary Dropbox refusal on the Repair's boundary read waits the provider time and asks again; the explicit Repair completes.
    public static async Task RepairBoundaryRefusalAsync()
    {
        using var store=new InMemoryStateStore();var pair=Bound("refused",@"T:\Refused",1);
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        var db=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var tap=(PublicationStore)(object)db;tap.Store=store;
        var finished=Signal();tap.Finished=()=>finished.TrySetResult();
        var local=QuietSide(new FakeSide(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)});
        var remote=QuietSide(new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)});var cloud=(JournalProbe)(object)remote;
        var boot=Signal();int armed=0,refused=0,asked=0;
        cloud.Boundary=_=>{
            if(Volatile.Read(ref armed)!=0) Interlocked.Increment(ref asked);
            if(Volatile.Read(ref armed)!=0 && Interlocked.Exchange(ref refused,1)==0)
                throw new ItemFailedException(string.Empty,TextKey.DropboxRefused,null){Disposition=FailureDisposition.TemporaryNoEffect,Provider="files/list_folder/get_latest_cursor 429 -",ProviderWait=TimeSpan.FromMilliseconds(1)};
            return Task.FromResult<(string?,JournalPosition?)>((null,null));};
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(_,_,_,_,_)=>Task.FromResult((local,remote)),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask,(_,_,_)=>{boot.TrySetResult();return Task.CompletedTask;});
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));var token=stop.Token;
        try{
            await host.StartAsync(token);await boot.Task.WaitAsync(token);
            Volatile.Write(ref armed,1);
            try{await host.RepairAsync(pair.PairId,token);}
            catch(ItemFailedException e){throw new InvalidDataException("A temporary refusal on the Repair's boundary read failed the Repair",e);}
            Check.Require(refused==1 && asked>=2 && finished.Task.IsCompleted,"The Repair did not ask its boundary again and complete after one temporary refusal");
        }finally{stop.Cancel();await host.StopAsync(default);}
    }
    // A waiting Repair's output reaches it before the command returns; a reader that stops reading never holds Host Stop,
    // and Stop's own pipe cancellation, not the reader, ends that held write.
    public static async Task StalledRepairReaderStopsAsync()
    {
        using var store=new InMemoryStateStore();var pair=Bound("stalled",@"T:\Stalled",1);
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        var db=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var tap=(PublicationStore)(object)db;tap.Store=store;
        var finished=Signal();tap.Finished=()=>finished.TrySetResult();
        var local=QuietSide(new FakeSide(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)});
        var remote=QuietSide(new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)});
        var boot=Signal();
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(_,_,_,_,_)=>Task.FromResult((local,remote)),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask,(_,_,_)=>{boot.TrySetResult();return Task.CompletedTask;});
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));var token=stop.Token;
        var held=new HeldReader(host.Stopping);
        try{
            await host.StartAsync(token);await boot.Task.WaitAsync(token);
            var open=new StringWriter();
            await host.RepairAsync(pair.PairId,token,open);
            Check.Require(open.ToString().Contains(Text.Get(TextKey.HumanFinished,(TextArg.Name,Text.Get(TextKey.HumanTitleRepair)),(TextArg.Status,string.Empty),(TextArg.Suffix,string.Empty)),StringComparison.Ordinal),
                "the Repair's output did not reach its command before it returned");
            finished=Signal();
            var repair=host.RepairAsync(pair.PairId,token,held);
            await finished.Task.WaitAsync(token);await held.Entered.Task.WaitAsync(token);
            try{await host.StopAsync(token).WaitAsync(token);}
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw new InvalidDataException("A stalled Repair reader held Host Stop");}
            Check.Require(host.Stopping.IsCancellationRequested && !held.Gate.Task.IsCompleted,"Stop did not cancel the pipe while the reader stayed stalled");
            await repair.WaitAsync(token);
        }finally{held.Gate.TrySetResult();stop.Cancel();await host.StopAsync(default);}
    }
    // Stands in for the pipe writer: every write waits for the reader (the gate) or for the pipe's own cancellation.
    private sealed class HeldReader(CancellationToken pipe):TextWriter
    {
        internal readonly TaskCompletionSource Gate=Signal(),Entered=Signal();
        public override System.Text.Encoding Encoding=>System.Text.Encoding.UTF8;
        public override void Write(char value)=>Write(value.ToString());
        public override void Write(string? value){Entered.TrySetResult();Gate.Task.Wait(pipe);}
    }
    public static Task FailedNoticeWriteAsync()=>FailureRecovery(false);
    private static Task UnexpectedFailureLogged()=>FailureRecovery(false,true);
    // An unexpected pair-loop failure (neither an item failure nor a request refusal) is logged with its type; its notice write succeeds.
    // The reader retries an IOException itself, so the unexpected failure is another exception type.
    private static async Task FailureRecovery(bool manual,bool unexpected=false)
    {
        using var store=new InMemoryStateStore();var pair=Bound("recover",@"T:\Recover",1);
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        var db=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var tap=(PublicationStore)(object)db;tap.Store=store;
        var pc=new FakeSide(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};var local=QuietSide(pc);
        var remote=QuietSide(new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)});var cloud=(JournalProbe)(object)remote;
        var boot=Signal();var failRead=Signal();var finished=Signal();var copied=Signal();int reads=0,rootNotices=-1;
        var injected=new IOException("injected store failure");var item=ItemChecks.File(Side.Dropbox,"later",3,"item-id");
        tap.Finished=()=>finished.TrySetResult();tap.CursorSaved=()=>{if(pc.Items.TryGetValue(item.Path,out var file) && file.Bytes.SequenceEqual(new byte[]{3}))copied.TrySetResult();};
        cloud.Feed=async (_,ct)=>{switch(Interlocked.Increment(ref reads)){
            case 1:await failRead.Task.WaitAsync(ct);if(unexpected)throw new InvalidOperationException("injected read failure");throw new ItemFailedException(string.Empty,TextKey.SignInReturnRetry,null);
            case 2:rootNotices=store.OpenNotices.Count(n=>n.Cause==TextKey.RootUnavailable);if(manual)await finished.Task.WaitAsync(ct);cloud.Local.Seed(item,[3]);return new([new(Side.Dropbox,ChangeKind.Changed,item.Path,null,item.Id,item,true,false)],"later",null);
            default:await Task.Delay(Timeout.Infinite,ct);throw new InvalidOperationException();}};
        var logs=new MemoryLogs();var output=new StringWriter();logs.Capture=output;
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(_,_,_,_,_)=>Task.FromResult((local,remote)),ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask,(_,_,_)=>{boot.TrySetResult();return Task.CompletedTask;});
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));var token=stop.Token;bool recovered=false;
        try{
            await host.StartAsync(token);await boot.Task.WaitAsync(token);
            if(manual){tap.BeforeFinish=()=>throw injected;
                try{await host.RepairAsync(pair.PairId,token);throw new InvalidDataException("Explicit Repair hid the store failure");}catch(IOException e) when(e==injected){}
                tap.BeforeFinish=null;
            }else if(!unexpected) tap.NoticeFailure=injected;
            failRead.SetResult();
            try{await (manual ? Task.WhenAll(copied.Task,finished.Task) : copied.Task).WaitAsync(token);recovered=true;}catch(OperationCanceledException) when(token.IsCancellationRequested){}
        }finally{stop.Cancel();try{await host.StopAsync(default);}catch(IOException e) when(e==injected){recovered=false;}}
        if(manual)Check.Require(recovered && host.Stopping.IsCancellationRequested,"Failed explicit Repair blocked owed Repair or clean shutdown");
        else if(unexpected) Check.Require(rootNotices==1,$"Unexpected pair-loop failure raised {rootNotices} RootUnavailable notices before the retry, not one");
        if(!manual) Check.Require(recovered && output.ToString().Contains(unexpected ? nameof(InvalidOperationException) : nameof(IOException),StringComparison.Ordinal),unexpected ? "Unexpected pair-loop failure stopped later sync or was logged without its type" : "Failed notice write stopped later sync or was not logged");
    }
    private sealed class RetryClock : TimeProvider
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<TimeSpan> Waits=new();
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan due,TimeSpan period)
        {Waits.Enqueue(due);return TimeProvider.System.CreateTimer(callback,state,TimeSpan.Zero,period);}
    }
    private static async Task FailedFirstRead()
    {
        using var store=new InMemoryStateStore();var pair=Bound("first",@"T:\First",1);await store.SavePairAsync(pair,default);
        var published=new TaskCompletionSource<(bool Admitted,int Reads,int SignInNotices)>(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;
        var pc=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var probe=(JournalProbe)(object)pc;probe.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};
        // Observe the admission decision before the live runner can write another Failure in the same notice slot.
        probe.Published=()=>published.TrySetResult((!probe.Paused,Volatile.Read(ref calls),store.OpenNotices.Count(n=>n.Kind==NoticeKind.Failure && n.Cause==TextKey.SignInReturnRetry)));
        var cloud=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var remote=(JournalProbe)(object)cloud;remote.Local=new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        probe.Quiet=remote.Quiet=true;
        var clock=new RetryClock();var settings=ItemChecks.Settings with {Backoff=ItemChecks.Settings.Backoff with {BackoffJitter=0}};
        remote.ReadRoot=()=>{if(Interlocked.Increment(ref calls)<3)throw new ItemFailedException(string.Empty,TextKey.SignInReturnRetry,null){ProviderWait=TimeSpan.Zero};};
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_)=>(pc,cloud),settings,new MemoryLogs(),clock,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        await WithCleanup(async ()=>{await host.StartAsync(default);var at=await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Require(at.Admitted,"First-read retry: member was not admitted");
            Check.Require(clock.Waits.Take(2).SequenceEqual(new[]{settings.Backoff.RetryDelay(0),settings.Backoff.RetryDelay(1)}),"Root retry ignored the shared consecutive-failure schedule");
            Check.Require(at.Reads==3,$"First-read retry: expected three root reads, observed {at.Reads}");
            Check.Require(at.SignInNotices==1,$"First-read retry: expected one sign-in notice, observed {at.SignInNotices}");
        },()=>host.StopAsync(default));
    }
    private static async Task WithCleanup(Func<Task> body,Func<Task> cleanup)
    {
        bool assertion=false;
        try {await body();}
        catch(InvalidDataException){assertion=true;throw;}
        finally {try{await cleanup();}catch(NotImplementedException) when(assertion){ /* Keep the original assertion. */ }}
    }
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static PairConfig Bound(string id,string path,ulong serial)
    {
        var p=ItemChecks.Pair() with {PairId=id,LocalRoot=path,DropboxRoot="/"+id};
        return p with {Roots=new(p.LocalRoot,p.DropboxRoot,p.ConnectionId,new(serial,2,null),new("account","root","home",id))};
    }
    internal sealed class MemoryLogs : ILogFiles
    {
        internal Action? Closed;
        internal StringWriter? Capture;
        internal TaskCompletionSource RepairOpened=Signal(); private int opens;
        private sealed class Writer(Action closed):StringWriter {protected override void Dispose(bool disposing){if(disposing)closed();base.Dispose(disposing);}}
        public TextWriter Open(string path,int bufferCharacters){if(Capture!=null)return Capture;var n=Interlocked.Increment(ref opens);if(n==3)RepairOpened.TrySetResult();return n==2 && Closed is {} closed ? new Writer(closed) : new StringWriter();}
        public string? Resolve(string folder,string pairId,string runId)=>null;
        public ILogFiles.ReadStatus Read(string path,Action<string,bool> record,int bufferBytes,CancellationToken ct,out bool trailing){trailing=false;return ILogFiles.ReadStatus.NotFound;}
        internal TimeSpan? PrunedOlderThan;
        public void Prune(string folder,TimeSpan olderThan,DateTimeOffset now) => PrunedOlderThan=olderThan;
    }
    public static async Task LogsPrunedAtStartAsync()
    {
        var logs=new MemoryLogs();
        var host=new Host((_,_)=>Task.FromResult<IStore?>(null),(_,_,_,_)=>throw new InvalidOperationException(),ItemChecks.Settings,logs,TimeProvider.System,
            _=>Task.FromResult("connection"),(_,_)=>Task.CompletedTask);
        await host.StartAsync(default);
        Check.Require(logs.PrunedOlderThan==TimeSpan.FromDays(ItemChecks.Settings.LogRetentionDays),"Host start did not prune run logs older than the retention");
    }
    public static async Task TrimAtHostAsync()
    {
        var logs=new MemoryLogs();using(var check=new RunLog(logs,"probe",ItemChecks.Settings)){} // W22 stub => NOT IMPLEMENTED, not an artificial successful startup.
        using var db=new InMemoryStateStore();var pending=Bound("pending",@"T:\Pending",1);var ready=Bound("ready",@"T:\Ready",1);
        var jobPair=Bound("job",@"U:\Job",2);var free=Bound("free",@"V:\Free",3);
        foreach(var p in new[]{pending,ready,jobPair,free}) {await db.SavePairAsync(p,default);await db.FinishRepairAsync(p.PairId,(0,0),"{}",default);}
        await db.SaveJobAsync(new(jobPair.PairId,"files/delete_batch","job",[new("id","item",null)]),default);
        var endpoints=new System.Collections.Concurrent.ConcurrentDictionary<string,(JournalProbe Pc,JournalProbe Cloud)>();var readAtTrim=new System.Collections.Concurrent.ConcurrentDictionary<string,bool>();
        var reservations=new HashSet<string>();var bind=Signal();var started=Signal();var checking=Signal();var observed=Signal();var freeTrim=Signal();var readyTrim=Signal();var jobTrim=Signal();var finishTrim=Signal();CancellationToken trimToken=default;
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),async (pair,_,_,_,ct)=>{
            if(pair.PairId==pending.PairId){started.TrySetResult();await bind.Task.WaitAsync(ct);}
            var pc=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var lp=(JournalProbe)(object)pc;lp.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Removable)};lp.Quiet=true;
            var remote=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var rp=(JournalProbe)(object)remote;rp.Local=new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};rp.Quiet=true;
            var pcItem=ItemChecks.File(Side.Local,"local-change",1,"pc-id");var dbItem=ItemChecks.File(Side.Dropbox,"remote-change",1,"db-id");
            lp.Local.Seed(pcItem,[1]);rp.Local.Seed(dbItem,[1]);
            lp.Local.Pages.Enqueue(new([new(Side.Local,ChangeKind.Changed,pcItem.Path,null,pcItem.Id,pcItem,false,false)],null,new(1,4)));
            rp.Local.Pages.Enqueue(new([new(Side.Dropbox,ChangeKind.Changed,dbItem.Path,null,dbItem.Id,dbItem,false,false)],"through",null));
            endpoints[Path.GetPathRoot(pair.LocalRoot)!]=(lp,rp);
            if(pair.PairId==jobPair.PairId)rp.Job=async token=>{checking.TrySetResult();await observed.Task.WaitAsync(token);return new JobReply.Running();};
            return(pc,remote);
        },ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask,async (root,_,ct)=>{
            readAtTrim[root]=endpoints.TryGetValue(root,out var ends) && ends.Pc.Local.Requests[nameof(ISide.ReadChangesAsync)]>0 && ends.Cloud.Local.Requests[nameof(ISide.ReadChangesAsync)]>0;
            if(root==Path.GetPathRoot(free.LocalRoot))freeTrim.TrySetResult();
            if(root==Path.GetPathRoot(ready.LocalRoot)){trimToken=ct;readyTrim.TrySetResult();await finishTrim.Task.WaitAsync(ct);}
            if(root==Path.GetPathRoot(jobPair.LocalRoot))jobTrim.TrySetResult();
        },p=>reservations.Add(p.PairId),(p,ct)=>{ct.ThrowIfCancellationRequested();reservations.Remove(p.PairId);return Task.CompletedTask;});
        try{
            await host.StartAsync(default);await Task.WhenAll(started.Task,checking.Task,freeTrim.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Check.Require(!jobTrim.Task.IsCompleted && !readyTrim.Task.IsCompleted,"Trim preceded recovery or a pending first bind");
            Check.Require(await host.RemovePairAsync(pending.PairId,default)==null,"Pending member not removed");
            Check.Require(!reservations.Contains(pending.PairId) && reservations.Contains(ready.PairId),"Removing an unopened member did not retire just its reservation");
            await readyTrim.Task.WaitAsync(TimeSpan.FromSeconds(5));await host.PauseAsync(ready.PairId,default).WaitAsync(TimeSpan.FromSeconds(5));
            Check.Require(!trimToken.IsCancellationRequested,"Pair pause canceled volume trim");finishTrim.TrySetResult();
            observed.TrySetResult();await jobTrim.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Require(new[]{free,ready,jobPair}.All(p=>readAtTrim.GetValueOrDefault(Path.GetPathRoot(p.LocalRoot)!)),"Trim ran without actually reading both finite sources");
        }finally{bind.TrySetResult();observed.TrySetResult();finishTrim.TrySetResult();await host.StopAsync(default);}
    }
    // Hold the store lookup for the parent's publication, after the child has drained.
    public class PublicationStore : System.Reflection.DispatchProxy
    {
        internal InMemoryStateStore Store=null!;
        internal Func<Task>? OnFind,OnSave;
        internal Action<string>? Lookup,NoticeSaved;
        internal Func<string,Task>? FindAt;
        internal Action? Finished,CursorSaved,Requested;
        internal IOException? NoticeFailure;
        internal Func<Task>? BeforeFinish;
        internal Action<string?>? ReadDropboxDebt;
        internal Exception? PairFailure;
        internal bool Disposed;
        protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? args)
        {
            if(method!.Name==nameof(IStore.PairAsync) && PairFailure!=null && (string)args![0]! =="bad") return Task.FromException<PairConfig?>(PairFailure);
            if(method.Name==nameof(IDisposable.Dispose)) Disposed=true;
            if(method.Name==nameof(IStore.FindAsync)){Lookup?.Invoke((string)args![2]!);if(OnFind!=null || FindAt!=null)return Find();}
            if(method.Name==nameof(IStore.SaveNoticeAsync) && NoticeFailure!=null)return Task.FromException<Notice?>(NoticeFailure);
            if(method.Name==nameof(IStore.SaveNoticeAsync) && NoticeSaved!=null)return SaveNotice();
            if(ReadDropboxDebt!=null && (method.Name==nameof(IStore.ReadFullRepairAsync) || method.Name==nameof(IStore.ReadRepairAsync) && (Side)args![1]! ==Side.Dropbox)) return Debt();
            if(method.Name==nameof(IStore.OweRepairAsync) && Requested!=null){var result=Call();Requested();return result;}
            if(method.Name==nameof(IStore.SaveCursorAsync) && CursorSaved!=null) return Cursor();
            if(method.Name==nameof(IStore.SavePairAsync) && OnSave!=null) return Save();
            if(method.Name==nameof(IStore.FinishRepairAsync) && Finished!=null) return args![1] is Scope ? Finish<bool>(ok=>ok) : Finish<(bool Local,bool Dropbox)>(ok=>ok is (true,true));
            return Call();
            // A store call that throws synchronously must surface its real exception type, not reflection's wrapper.
            object? Call()
            {
                try {return method!.Invoke(Store,args);}
                catch(System.Reflection.TargetInvocationException error) when(error.InnerException!=null)
                {System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();throw;}
            }
            async Task<(long Revision,string? Reason)> Debt(){var debt=await (Task<(long Revision,string? Reason)>)Call()!;ReadDropboxDebt!(debt.Reason);return debt;}
            async Task<T> Finish<T>(Func<T,bool> done){if(BeforeFinish!=null)await BeforeFinish();var ok=await (Task<T>)Call()!;if(done(ok))Finished!();return ok;}
            async Task<Notice?> SaveNotice(){var saved=await (Task<Notice?>)Call()!;NoticeSaved!((string)args![0]!);return saved;}
            async Task Cursor(){await (Task)Call()!;CursorSaved!();}
            async Task Save(){await OnSave!();await (Task)Call()!;}
            async Task<SyncedEntry?> Find(){if(FindAt!=null)await FindAt((string)args![2]!);else await OnFind!();return await Store.FindAsync((string)args![0]!, (Side)args[1]!, (string)args[2]!, (CancellationToken)args[3]!);}
        }
    }
    // Exercise both completion orders; an ordinary later record is not a reset or an acknowledgement.
    public static async Task RepairReplaysJournalAsync()
    {
        await QuietRepairReplay();
        foreach(var late in new[]{false,true}) await Run(late);
        static async Task Run(bool late)
        {
            using var store=new InMemoryStateStore();var pair=Bound("journal",@"T:\Journal",1);
            await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
            var ledger=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var tap=(PublicationStore)(object)ledger;tap.Store=store;
            using var stop=new CancellationTokenSource();var ct=stop.Token; // Cleanup only; the CI job watchdog guards a hang.
            var waiting=Signal();var finished=Signal();if(late)tap.BeforeFinish=()=>waiting.Task.WaitAsync(ct);tap.Finished=()=>finished.TrySetResult();
            var local=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var pc=(JournalProbe)(object)local;pc.Quiet=true;pc.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};
            var remote=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var db=(JournalProbe)(object)remote;db.Quiet=true;db.Local=new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
            var cloudReading=Signal();var acknowledged=Signal();var version=ItemChecks.File(Side.Local,"item",3,"original");int reads=0;
            db.Feed=async (_,token)=>{cloudReading.TrySetResult();try{await Task.Delay(Timeout.Infinite,token);}finally{if(!late)await finished.Task.WaitAsync(ct);}throw new InvalidOperationException();};
            pc.OnSettle=pos=>{
                if(pos.NextUsn==32){if(db.Local.Items.TryGetValue(version.Path,out var file) && file.Bytes.SequenceEqual(new byte[]{3}))acknowledged.TrySetResult();
                    else acknowledged.TrySetException(new InvalidDataException("Journal acknowledgement preceded the replayed effect"));}
                return pc.ReadAhead==30 && pos.NextUsn>11 ? new(pos.JournalId,11) : pos;};
            pc.Feed=async (_,token)=>{
                switch(Interlocked.Increment(ref reads)){
                case 1: await cloudReading.Task.WaitAsync(token);return new([],null,new(1,11));
                case 2: pc.Settled=11;pc.ReadAhead=30;await store.OweRepairAsync(pair.PairId,Side.Local,"journal",token);return new([],null,new(1,30)){ContinuityLost=true};
                case 3 when late && pc.ReadAhead!=11:
                    waiting.TrySetResult();await finished.Task.WaitAsync(token);
                    var other=ItemChecks.File(Side.Local,"other",4,"other-id");pc.Local.Seed(other,[4]);
                    return new([new(Side.Local,ChangeKind.Changed,other.Path,null,other.Id,other,true,false){Usn=30,JournalId=1}],null,new(1,31));
                case 3: case 4 when late: if(!finished.Task.IsCompleted || pc.ReadAhead!=11 || pc.Paused){var error=new InvalidDataException("Repair returned to read-ahead instead of the held journal record");acknowledged.TrySetException(error);throw error;}
                    pc.Local.Seed(version,[3]);return new([new(Side.Local,ChangeKind.Changed,version.Path,null,version.Id,version,true,false){Usn=11,JournalId=1}],null,new(1,32));
                default: await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();}};
            var logs=new MemoryLogs();using(var preflight=new RunLog(logs,"journal",ItemChecks.Settings)){}
            var host=new Host((_,_)=>Task.FromResult<IStore?>(ledger),(_,_,_,_)=>(local,remote),ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
            try{
                await host.StartAsync(ct);
                await acknowledged.Task.WaitAsync(ct);
            }finally{stop.Cancel();await host.StopAsync(default);}
        }
    }
    // The LoseCoverage source-reset fence (resetAt bumped by WorkQueue.LoseCoverage) invalidates admissions taken before it:
    // an older page must not acknowledge the replayed record. This drives only the source-reset fence, not the Repair rewind
    // prefix reset (WorkQueue.RewindJournalAsync); RewindDropsOlderAdmissionAsync is the separate F1 case for that.
    private static async Task OldPublicationFence()
    {
        using var store=new InMemoryStateStore();var pair=Bound("fence",@"T:\Fence",1);
        var queue=new WorkQueue();
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));var ct=stop.Token;
        bool oldSettled=false,replaySettled=false;var releaseOld=Signal();
        var oldChange=new Change(Side.Local,ChangeKind.Changed,"older",null,null,null,false,false);
        var older=queue.AdmitAsync(pair,new([oldChange],null,new(1,31)),
            async (_,token)=>{await releaseOld.Task.WaitAsync(token);},
            (pos,token)=>{if(pos.NextUsn==31)oldSettled=true;return Task.CompletedTask;},ct);
        await queue.LoseCoverage(pair,Side.Local,new ChangePage([],null,new(1,30)){ContinuityLost=true},store,ct);
        var version=ItemChecks.File(Side.Local,"item",3,"original");
        var replay=queue.AdmitAsync(pair,new([new(Side.Local,ChangeKind.Changed,version.Path,null,version.Id,version,true,false)],null,new(1,32)),
            (_,_)=>Task.CompletedTask,(pos,token)=>{if(pos.NextUsn==32)replaySettled=true;return Task.CompletedTask;},ct);
        releaseOld.TrySetResult();
        await older.WaitAsync(ct); // await the older page's real admission completion, not a timed window
        await replay.WaitAsync(ct);
        Check.Require(replaySettled,"Replayed record was not acknowledged");
        Check.Require(!oldSettled,"An older journal acknowledgement passed the replayed record");
    }
    // A reset received during finite catch-up leaves the same readers live. No later journal event wakes them.
    private static async Task QuietRepairReplay()
    {
        using var store=new InMemoryStateStore();var pair=Bound("journal",@"T:\Journal",1);
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        using var stop=new CancellationTokenSource();var ct=stop.Token; // Cleanup only; the CI job watchdog guards a hang.
        var ledger=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var tap=(PublicationStore)(object)ledger;tap.Store=store;
        var waiting=Signal();var acknowledged=Signal();var peer=Signal();var peerRead=Signal();var peerContinued=Signal();int peerStarts=0;
        var local=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var pc=(JournalProbe)(object)local;pc.Quiet=true;pc.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};
        var remote=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var db=(JournalProbe)(object)remote;db.Quiet=true;db.Local=new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        var version=ItemChecks.File(Side.Local,"item",3,"original");int reads=0;
        pc.Settled=11;pc.ReadAhead=30;await local.SettleJournalAsync(new(1,11),ct);
        await store.OweRepairAsync(pair.PairId,Side.Local,"journal",ct);pc.Local.Pages.Enqueue(new([],null,new(1,30)){ContinuityLost=true});
        tap.Finished=()=>{};tap.BeforeFinish=()=>waiting.Task.WaitAsync(ct);
        db.Feed=async (_,token)=>{Interlocked.Increment(ref peerStarts);peerRead.TrySetResult();await peer.Task.WaitAsync(token);token.ThrowIfCancellationRequested();peerContinued.TrySetResult();await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();};
        pc.OnSettle=pos=>{if(pos.NextUsn==32)acknowledged.TrySetResult();return pos;};
        pc.Feed=async (_,token)=>{
            switch(Interlocked.Increment(ref reads)){
            case 1: await peerRead.Task.WaitAsync(token);return new([],null,null);
            case 2: waiting.TrySetResult();await Task.Delay(Timeout.Infinite,token);break;
            case 3: Check.Require(pc.ReadAhead==11 && !pc.Paused,"Repair returned to read-ahead instead of the held journal record");
                peer.TrySetResult();
                pc.Local.Seed(version,[3]);return new([new(Side.Local,ChangeKind.Changed,version.Path,null,version.Id,version,true,false)],null,new(1,32));
            default:await Task.Delay(Timeout.Infinite,token);break;}throw new InvalidOperationException();};
        var logs=new MemoryLogs();using(var preflight=new RunLog(logs,"journal",ItemChecks.Settings)){}
        var host=new Host((_,_)=>Task.FromResult<IStore?>(ledger),(_,_,_,_)=>(local,remote),ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try{
            await host.StartAsync(ct);
            await acknowledged.Task.WaitAsync(ct);
            Check.Require(db.Local.Items.TryGetValue(version.Path,out var file) && file.Bytes.SequenceEqual(new byte[]{3}),"Journal acknowledgement preceded the replayed effect");
            await peerContinued.Task.WaitAsync(ct);Check.Require(peerStarts==1,"Repair restarted the peer reader");
        }finally{stop.Cancel();await host.StopAsync(default);}
    }
    public static async Task RewindDropsOlderAdmissionAsync()
    {
        using var store=new InMemoryStateStore();var pair=Bound("rewind",@"T:\Rewind",1);
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        using var stop=new CancellationTokenSource();var ct=stop.Token; // Cleanup only; the CI job watchdog guards a hang.
        var ledger=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var tap=(PublicationStore)(object)ledger;tap.Store=store;
        var entered=Signal();var waiting=Signal();var acknowledged=Signal();var releaseOld=Signal();var drained=Signal();var stale=Signal();
        var local=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var pc=(JournalProbe)(object)local;pc.Quiet=true;pc.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};
        var remote=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var db=(JournalProbe)(object)remote;db.Quiet=true;db.Local=new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        var version=ItemChecks.File(Side.Local,"item",3,"original");int reads=0;
        pc.Settled=11;pc.ReadAhead=30;await local.SettleJournalAsync(new(1,11),ct);
        await store.OweRepairAsync(pair.PairId,Side.Local,"journal",ct);pc.Local.Pages.Enqueue(new([],null,new(1,30)){ContinuityLost=true});
        tap.Finished=()=>{};tap.BeforeFinish=()=>Task.WhenAll(entered.Task,waiting.Task).WaitAsync(ct);
        // Hold the older page's store lookup so it only reaches publication after the rewind has bumped resetAt.
        tap.FindAt=async key=>{if(key==PathNames.Key("older")){entered.TrySetResult();await releaseOld.Task.WaitAsync(ct);}};
        CancellationToken? firstReader=null;
        db.Feed=async (_,token)=>{
            firstReader??=token;
            if(firstReader!=token){drained.TrySetResult();await Task.Delay(Timeout.Infinite,token);}
            await releaseOld.Task.WaitAsync(token);
            // Only a drained live reader yields on reset. A peer reset cannot fence the old Local page.
            await Task.Yield();return new([],"peer-base",null){ContinuityLost=true};};
        pc.OnSettle=pos=>{if(pos.NextUsn==31)stale.TrySetResult();if(pos.NextUsn==32)acknowledged.TrySetResult();return pos;};
        pc.Feed=async (_,token)=>{
            switch(Interlocked.Increment(ref reads)){
            case 1: return new([new(Side.Local,ChangeKind.Changed,"older",null,null,null,false,false)],null,new(1,31));
            case 2: waiting.TrySetResult();await Task.Delay(Timeout.Infinite,token);break;
            case 3: Check.Require(pc.ReadAhead==11 && !pc.Paused,"Repair returned to read-ahead instead of the held journal record");
                pc.Local.Seed(version,[3]);return new([new(Side.Local,ChangeKind.Changed,version.Path,null,version.Id,version,true,false)],null,new(1,32));
            default:await Task.Delay(Timeout.Infinite,token);break;}throw new InvalidOperationException();};
        var logs=new MemoryLogs();using(var preflight=new RunLog(logs,"rewind",ItemChecks.Settings)){}
        var host=new Host((_,_)=>Task.FromResult<IStore?>(ledger),(_,_,_,_)=>(local,remote),ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try{
            await host.StartAsync(ct);
            await Task.WhenAll(entered.Task,acknowledged.Task).WaitAsync(ct);
            var rewinds=pc.Rewinds;
            releaseOld.SetResult();await drained.Task.WaitAsync(ct);
            Check.Require(firstReader is {IsCancellationRequested:true} && pc.Rewinds==rewinds,"Reader restarted after a failure instead of draining the older page");
            Check.Require(!stale.Task.IsCompleted,"An older journal acknowledgement passed the replayed record");
        }finally{releaseOld.TrySetResult();stop.Cancel();await host.StopAsync(default);}
    }
    /// <summary>Confirmed exclusion reports the actual apply diagnosis even when its preview was healthy.</summary>
    public static async Task ExclusionCauseAsync()
    {
        using var store=new InMemoryStateStore();var pair=Bound("exclude",@"T:\Exclude",1);await store.SavePairAsync(pair,default);
        var local=new FakeSide(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};var cloud=new FakeSide(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        var selection=new Exclusion(Side.Local,"item",false);local.Seed(ItemChecks.File(Side.Local,selection.Path,1,"pc-file"),[1]);
        var db=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var gate=(PublicationStore)(object)db;gate.Store=store;
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(_,_,_,_)=>(local,cloud),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try {
            var preview=await host.SetExclusionAsync(pair.PairId,selection,false,false,default);
            Check.Require(preview.Outcome==null && preview.Cause==null && (await store.PairAsync(pair.PairId,default))!.Exclusions.Count==0,"Preview changed the exclusion or reported an apply outcome");
            gate.OnSave=()=>{local.ReadFailures[selection.Path]=new ItemFailedException(selection.Path,TextKey.ReadLocalFailed,null);return Task.CompletedTask;};
            var applied=await host.SetExclusionAsync(pair.PairId,selection,false,true,default);
            Check.Require(applied.Outcome==ExclusionOutcome.Pending && applied.Cause==TextKey.ReadLocalFailed && local.Items.ContainsKey(selection.Path),"Host dropped the observed apply cause or inferred it from the healthy preview");
        } finally {await host.StopAsync(default);}
        await IncludeAfterInterruptedExclusion("ex");
        await IncludeAfterInterruptedExclusion("kept");
        await RestartKeptAsync();
    }
    private static Task RestartKeptAsync() => Storage.StoreChecks.WithImage(async (store,sql)=>
    {
        var pair=Bound("restart",@"T:\Restart",1) with {Enabled=false,Drive=new(1,2,30),Exclusions=[new(Side.Dropbox,"cloud",true)]};
        await store.SavePairAsync(pair,default);
        var pc=ItemChecks.File(Side.Local,"local",1,"local-id");var db=ItemChecks.File(Side.Dropbox,"cloud",1,"cloud-id");
        var binding=SyncedEntries.From(pc,db);await store.CommitAsync(pair.PairId,null,binding,null,null,default);
        var local=new FakeSide(Side.Local){Root=(pair.Roots!.Local,null,pair.Drive,DriveType.Fixed)};var remote=new FakeSide(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        var moved=pc with {Path="moved"};local.Seed(moved,[1]);remote.Seed(db,[1]);
        Func<PathScope>? live=null;
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,scope,_)=>{live=scope;return(local,remote);},ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try
        {
            await host.PauseAsync(null,default);await host.StartAsync(default);
            var enrolled=(await store.PairAsync(pair.PairId,default))!;
            Check.Require(enrolled.Exclusions.All(e=>e.KeptBy!=null) && enrolled.Exclusions.Any(e=>e.Side==Side.Local && e.Path==pc.Path),"Host restart did not enroll the split Dropbox binding");
            await host.SetExclusionAsync(pair.PairId,new(Side.Local,"other",true),false,true,default);
            await Engine.Executor.RowExecution.ApplyAsync(enrolled,ItemChecks.Settings,_=>{},live!(),null,[new(Side.Local,moved,binding)],local,remote,store,null!,null!,default);
            Check.Require(live!().Excludes("moved",null,false) && !live!().Excludes("cloud","moved",false),"Host failed to install the moved physical KEEP leg");
            await host.SetExclusionAsync(pair.PairId,new(Side.Local,"moved",false),true,true,default);
            Check.Require(!live!().Excludes("moved","cloud",false) && live!().Excludes("other",null,false),"Include of the peer alias cleared the wrong rule");
            await host.SetExclusionAsync(pair.PairId,new(Side.Dropbox,"cloud",true),false,true,default);
            await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.DriveChange,TextKey.DriveChanged,"",new([],null),DateTimeOffset.UnixEpoch,false),default);
            Notice? drive=null;await foreach(var notice in host.NoticesAsync(pair.PairId,default)) if(notice.Kind==NoticeKind.DriveChange) drive=notice;
            await host.ResolveAsync(pair.PairId,drive!.Id,drive!.Reviewed!,ResolutionChoice.FirstSync,null,default);
            var reset=(await store.PairAsync(pair.PairId,default))!;
            Check.Require(reset.Exclusions.All(e=>e.KeptBy==null) && live!().Excludes("moved","cloud",false) && await store.FindAsync(pair.PairId,Side.Local,"MOVED",default)==null,"FirstSync re-admitted an obsolete binding");
        }
        finally {await host.StopAsync(default);}
    });
    // A no-keep exclusion removed the Dropbox copy but stopped before forgetting its bindings; including again keeps the PC copy and copies it back.
    private static async Task IncludeAfterInterruptedExclusion(string kept)
    {
        using var store=new InMemoryStateStore();var pair=Bound("include",@"T:\Include",1) with {Exclusions=[new(Side.Dropbox,"ex",false)]};
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        var folder=ItemChecks.File(Side.Local,kept,0,"pc-ex",true);var file=ItemChecks.File(Side.Local,kept+"/f",1,"pc-f");
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(folder,ItemChecks.File(Side.Dropbox,"ex",0,"db-ex",true)),null,null,default);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(file,ItemChecks.File(Side.Dropbox,"ex/f",1,"db-f")),null,null,default);
        var pc=new FakeSide(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};pc.Seed(folder,[]);pc.Seed(file,[1]);
        // Covered-folder population skips links; only the outer child comparison can record this member.
        var link=file with {Path=kept+"/link",Id="pc-link",Skip=nameof(TextKey.SkipLink)};pc.Seed(link,[]);
        var cloud=new FakeSide(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        var db=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var tap=(PublicationStore)(object)db;tap.Store=store;
        var boot=Signal();var finished=Signal();tap.Finished=()=>finished.TrySetResult();var recycled=Signal();pc.AfterDelete=_=>recycled.TrySetResult();
        var noticed=Signal();tap.NoticeSaved=id=>{if(id==pair.PairId)noticed.TrySetResult();};
        var logs=new MemoryLogs();using(var preflight=new RunLog(logs,"include",ItemChecks.Settings)){}
        var compared=Signal();var remote=QuietSide(cloud);
        var delayed=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.EnsureFolderAsync) && args[0] is "ex" ? Ensure((CancellationToken)args[1]!) : AutoSyncChecks.Observation.Forward(remote,m,args));
        async Task<RemovalResult> Ensure(CancellationToken token){await compared.Task.WaitAsync(token);return await remote.EnsureFolderAsync("ex",token);}
        var observed=AutoSyncChecks.Observation.Wrap<IStore>((m,args)=>m.Name==nameof(IStore.CompareAsync) && args[2] is Scope range && range==new Scope(kept,"ex") ?
            AfterRows(store.CompareAsync((string)args[0]!, (Guid)args[1]!,range,(Func<Side,string,(bool,bool)>)args[3]!, (CancellationToken)args[4]!)) : AutoSyncChecks.Observation.Forward(db,m,args));
        async IAsyncEnumerable<T> AfterRows<T>(IAsyncEnumerable<T> rows){await foreach(var row in rows) yield return row;compared.TrySetResult();}
        var host=new Host((_,_)=>Task.FromResult<IStore?>(observed),(_,_,_,_,_)=>Task.FromResult((QuietSide(pc),delayed)),ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask,(_,_,_)=>{boot.TrySetResult();return Task.CompletedTask;});
        using var stop=new CancellationTokenSource();var ct=stop.Token; // Cleanup only; the CI job watchdog guards a hang.
        try{
            await host.StartAsync(ct);await boot.Task.WaitAsync(ct);
            var included=await host.SetExclusionAsync(pair.PairId,new(Side.Dropbox,"ex",false),true,false,ct);
            // A failed scoped Repair can save a notice without finishing or recycling anything.
            await Task.WhenAny(finished.Task,recycled.Task,noticed.Task).WaitAsync(ct);
            Check.Require(included.Outcome==ExclusionOutcome.Applied && pc.History.Count==0 && pc.Items.TryGetValue(file.Path,out var keptFile) && keptFile.Bytes.SequenceEqual(new byte[]{1}),$"Include after an interrupted no-keep exclusion recycled the kept PC copy");
            Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.KeyFor(Side.Local,file.Path),ct) is {} binding && binding.IdOn(Side.Dropbox)!="db-f" &&
                binding.DropboxPath=="ex/f" && cloud.Items.TryGetValue(binding.DropboxPath,out var back) && back.Bytes.SequenceEqual(new byte[]{1}),$"Include did not bring the kept PC copy back to Dropbox under a new binding");
            Check.Require(store.OpenNotices.Any(n=>n.Cause==TextKey.InfoLinks && n.Subject.Items.Contains(new NoticeItem(Side.Local,link.Path,link.Id))),"Mapped child was outside the outer comparison before the root binding existed");
            Check.Require((await store.PairAsync(pair.PairId,ct))!.OwedScopes is not {Count:>0},$"Include left its owed Repair scope unsettled");
        }finally{stop.Cancel();await host.StopAsync(default);}
    }
    /// <summary>Later feed input cannot retry a retained item through a configuration publication in progress.</summary>
    public static async Task PublicationRetryAsync()
    {
        using var store=new InMemoryStateStore();var pair=Bound("scope",@"T:\Scope",1);await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        var logs=new MemoryLogs();using(var preflight=new RunLog(logs,"scope",ItemChecks.Settings)){}
        var db=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var gate=(PublicationStore)(object)db;gate.Store=store;
        var local=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var lp=(JournalProbe)(object)local;lp.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};lp.Quiet=true;
        var cloud=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var cp=(JournalProbe)(object)cloud;cp.Local=new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};cp.Quiet=true;
        var second=Signal();var admitted=Signal();var saving=Signal();var commit=Signal();var resumed=Signal();var firstAdmitted=Signal();
        var path="held";var input=new Change(Side.Dropbox,ChangeKind.Changed,path,null,"missing",null,false,false);
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(10));var ct=cancel.Token;int lookups=0;
        gate.Lookup=key=>{if(key==PathNames.DropboxKey(path) && Interlocked.Increment(ref lookups)>2 && commit.Task.IsCompleted)resumed.TrySetResult();};
        cp.Local.Pages.Enqueue(new([input],"one",null)); // The first held input is finite; its successor must arrive live.
        cp.Feed=async (cursor,token)=>{
            if(cursor==null)throw new InvalidDataException("Live reader restarted from settlement instead of the acquired finite cursor");
            if(cursor=="one"){firstAdmitted.TrySetResult();await second.Task.WaitAsync(token);return new([input],"two",null);}
            admitted.TrySetResult();await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();};
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(_,_,_,_)=>(local,cloud),ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try{
            await host.StartAsync(ct);await firstAdmitted.Task.WaitAsync(ct);
            Check.Require(Volatile.Read(ref lookups)>=2,"Feed failed to retain and retry the original missing identity");
            gate.OnSave=()=>{saving.TrySetResult();return commit.Task.WaitAsync(ct);};
            var publication=host.SetIgnoresAsync(pair.PairId,[],ct);await saving.Task.WaitAsync(ct);int before=Volatile.Read(ref lookups);
            second.SetResult();await admitted.Task.WaitAsync(ct);
            Check.Require(Volatile.Read(ref lookups)==before,"Retained input re-entered before the scope publication finished");
            commit.SetResult();await publication;await resumed.Task.WaitAsync(ct);
        }finally{second.TrySetResult();commit.TrySetResult();cancel.Cancel();await host.StopAsync(default);}
    }
    // A client that stops reading the issues stream must not hold the host command lock.
    private static async Task NoticeStreamReleasesCommands()
    {
        using var store=new InMemoryStateStore();var pair=Bound("issues",@"T:\Issues",1);await store.SavePairAsync(pair,default);
        foreach(var path in new[]{"first","second"})
            await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,path,new([new(Side.Local,path,path+"-id")],Side.Local),DateTimeOffset.UnixEpoch,false),default);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_)=>throw new InvalidOperationException(),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        var rows=host.NoticesAsync(pair.PairId,default).GetAsyncEnumerator();
        try{
            Check.Require(await rows.MoveNextAsync(),"Issues stream lost its first notice");
            // With no member, Pause completes synchronously whenever the command lock is free.
            var pause=host.PauseAsync(pair.PairId,default);
            Check.Require(pause.IsCompleted,"A paused issues reader holds the host command lock");
            Check.Require(await rows.MoveNextAsync(),"Issues stream lost its second notice");
            await pause;await host.ResumeAsync(pair.PairId,default);
        }finally{await rows.DisposeAsync();await host.StopAsync(default);}
    }
    // A stored definition that cannot be decoded never launched, so its row can still be removed; the store's own refusals still apply.
    private static async Task MalformedPairRemovable()
    {
        foreach(var (failure,putBack) in new (Exception,bool)[]{(new System.Text.Json.JsonException("undecodable"),false),(new InvalidDataException("undecodable"),false),(new InvalidDataException("undecodable"),true)})
        {
            using var store=new InMemoryStateStore();var bad=Bound("bad",@"T:\Bad",1);await store.SavePairAsync(bad,default);
            if(putBack) await store.SaveNoticeAsync(bad.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,"item",new([new(Side.Dropbox,"item","item-id")],Side.Dropbox),DateTimeOffset.UnixEpoch,false),default);
            var db=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var tap=(PublicationStore)(object)db;tap.Store=store;tap.PairFailure=failure;
            var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(_,_,_,_)=>throw new InvalidOperationException(),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
            try{
                TextKey? cause;
                try{cause=await host.RemovePairAsync(bad.PairId,default);}
                catch(Exception e) when(e is System.Text.Json.JsonException or InvalidDataException){throw new InvalidDataException($"Undecodable pair could not be removed ({e.GetType().Name})",e);}
                var stored=false;await foreach(var p in store.PairsAsync(default)) stored|=p.PairId==bad.PairId;
                Check.Require(putBack ? cause==TextKey.PairPutBackOutstanding && stored : cause==null && !stored,"Undecodable pair removal ignored the store's result");
            }finally{await host.StopAsync(default);}
        }
    }
    // A drain that throws still disposes the store and signals the pipe owner; the caller sees the drain's own exception.
    private static async Task StopAfterDrainFailure()
    {
        using var store=new InMemoryStateStore();var pair=Bound("drain",@"T:\Drain",1);
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        var db=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var tap=(PublicationStore)(object)db;tap.Store=store;
        var injected=new IOException("injected drain failure");var admitted=Signal();var failing=false;
        var local=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var probe=(JournalProbe)(object)local;probe.Quiet=true;
        probe.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};
        // Drain's last call pauses the journal member; fail only that one, after admission.
        probe.Published=()=>{if(!probe.Paused)admitted.TrySetResult();else if(failing)throw injected;};
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(_,_,_,_)=>(local,QuietSide(new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)})),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        await host.StartAsync(default);await admitted.Task; // The CI job watchdog guards a hang.
        failing=true;
        try{await host.StopAsync(default);throw new InvalidDataException("Stop hid the drain failure");}catch(IOException e) when(e==injected){}
        Check.Require(host.Stopping.IsCancellationRequested && tap.Disposed,"A failed drain skipped the stop signal or store disposal");
    }
    private static async Task RemovingMember()
    {
        using var db=new InMemoryStateStore();var parent=Bound("parent",@"T:\Parent",1) with {Enabled=false};
        var child=Bound("child",Path.Combine(parent.LocalRoot,"child"),1);
        await db.SavePairAsync(parent,default);await db.SavePairAsync(child,default);
        await db.CommitAsync(parent.PairId,null,SyncedEntries.From(ItemChecks.File(Side.Local,"child",0,"pc-child",true),ItemChecks.File(Side.Dropbox,"child",0,"db-child",true)),null,null,default);
        var wrapped=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var gate=(PublicationStore)(object)wrapped;gate.Store=db;
        int opens=0;var opened=Signal();var entered=Signal();var release=Signal();
        var host=new Host((_,_)=>Task.FromResult<IStore?>(wrapped),(p,_,_,_)=>{
            if(p.PairId==child.PairId){Interlocked.Increment(ref opens);opened.TrySetResult();}
            return(QuietSide(new(Side.Local){Root=(p.Roots!.Local,null,null,DriveType.Fixed)}),QuietSide(new(Side.Dropbox){Root=(null,p.Roots.Dropbox,null,null)}));
        },ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try {
            await host.StartAsync(default);await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            gate.OnFind=()=>{entered.TrySetResult();return release.Task;};var remove=host.RemovePairAsync(child.PairId,default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var bind=host.BindAsync(child.PairId,default);var resume=host.ResumeAsync(child.PairId,default);
            release.SetResult();await remove;try{await bind;}catch(EngineRequestException){} await resume;
            Check.Require(opens==1 && await db.PairAsync(child.PairId,default)==null && await db.PairAsync(parent.PairId,default)!=null,"Removal admitted a child adapter while publishing the parent scope");
        } finally {release.TrySetResult();await host.StopAsync(default);}
    }
    private static async Task ReleaseFailure()
    {
        using var db=new InMemoryStateStore();var pair=Bound("pending",@"T:\Pending",1);await db.SavePairAsync(pair,default);var entered=Signal();
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),async (_,_,_,_,ct)=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);throw new InvalidOperationException();},ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask,reserve:_=>{},releaseReservation:(_,_)=>throw new IOException("journal release"));
        try {await host.StartAsync(default);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));await host.PauseAsync(pair.PairId,default);
            try{await host.RemovePairAsync(pair.PairId,default);throw new InvalidDataException("Journal release failure was hidden");}catch(IOException){}
            Check.Require(!host.IsPaused(pair.PairId) && await db.PairAsync(pair.PairId,default)==null,"Committed removal left the failed journal member in host state");
        } finally {await host.StopAsync(default);}
    }
    private static async Task ExplicitBind()
    {
        using var db=new InMemoryStateStore();var pair=ItemChecks.Pair();await db.SavePairAsync(pair,default);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),async (_,_,_,_,ct)=>{entered.TrySetResult();await release.Task.WaitAsync(ct);return(new FakeSide(Side.Local),new FakeSide(Side.Dropbox));},ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        var bind=host.BindAsync(pair.PairId,default);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var all=host.PairsAsync(default).GetAsyncEnumerator(); var next=all.MoveNextAsync().AsTask();
        try { Check.Require(await next.WaitAsync(TimeSpan.FromSeconds(2)),"Missing pair"); }
        catch(TimeoutException e){throw new InvalidDataException("Explicit Bind blocked the CLI",e);}
        finally {release.TrySetResult();try{await bind;}catch(EngineRequestException){} await next; await all.DisposeAsync(); await host.StopAsync(default);}
    }
    private static async Task PendingOuterScope()
    {
        using var db=new InMemoryStateStore(); var p=ItemChecks.Pair();
        var roots=new RootIdentity(p.LocalRoot,p.DropboxRoot,p.ConnectionId,new(5,6,null),new("account","root","home","folder"));
        var outer=p with {Roots=roots,Enabled=false};await db.SavePairAsync(outer,default);
        await db.CommitAsync(outer.PairId,null,SyncedEntries.From(ItemChecks.File(Side.Local,"child",0,"pc-child",true),ItemChecks.File(Side.Dropbox,"child",0,"db-child",true)),null,null,default);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<PathScope>? live=null;
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),async (pair,_,scope,_,ct)=>{
            var side=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var probe=(JournalProbe)(object)side;
            probe.Local=new(Side.Local){Root=(roots.Local,null,null,DriveType.Fixed)};probe.Quiet=true;
            if(pair.PairId==outer.PairId) {live=scope; entered.TrySetResult();await release.Task.WaitAsync(ct);probe.Published=()=>published.TrySetResult();}
            return(side,QuietSide(new(Side.Dropbox){Root=(null,roots.Dropbox,null,null)}));
        },ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try
        {
            await host.StartAsync(default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.AddPairAsync(p with {PairId="inner",LocalRoot=Path.Combine(p.LocalRoot,"child"),DropboxRoot=p.DropboxRoot+"/child"},default);
            release.TrySetResult(); await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Require(live!().Excludes("child/file","child/file",false),"Late first bind overwrote the published nested scope");
        }
        finally {release.TrySetResult(); await host.StopAsync(default);}
    }
    private static ISide QuietSide(FakeSide endpoint)
    {
        var side=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();
        var probe=(JournalProbe)(object)side; probe.Local=endpoint; probe.Quiet=true; return side;
    }
    // Forwards to the real store; optionally commits the `owe` scope just before `first`'s scoped Finish (an E2 owe made
    // during the pass), and signals once `watch` retires. Letting Dispose through keeps the fixture's own lifetime.
    private static IStore ScopedStore(IStore inner,string pairId,Scope? first,Scope? owe,Scope? watch,TaskCompletionSource retired)
    {
        var owed=new bool[1];
        return AutoSyncChecks.Observation.Wrap<IStore>((m,args)=>{
            if(m.Name==nameof(IDisposable.Dispose)) return null;
            if(m.Name==nameof(IStore.FinishRepairAsync) && args.Length==4 && args[1] is Scope s)
            {
                if(owe!=null && s==first && !owed[0]) { owed[0]=true; return OweThenFinish(inner,pairId,owe,m,args); }
                var done=AutoSyncChecks.Observation.Forward(inner,m,args);
                if(watch!=null && s==watch && done is Task<bool> fin) return Retire(fin,retired);
                return done;
            }
            return AutoSyncChecks.Observation.Forward(inner,m,args);
        });
    }
    private static async Task<bool> OweThenFinish(IStore inner,string pairId,Scope owe,System.Reflection.MethodInfo m,object?[] args)
    {
        await inner.OweScopeAsync(pairId,owe,default);
        return await (Task<bool>)AutoSyncChecks.Observation.Forward(inner,m,args)!;
    }
    private static async Task<bool> Retire(Task<bool> fin,TaskCompletionSource retired)
    {
        var ok=await fin; if(ok) retired.TrySetResult(); return ok;
    }
    // An unreadable folder is skipped: its scope settles, the folder itself stays owed, an unrelated owed scope settles and a notice is raised.
    private static async Task OwedScopeIsolationAsync()
    {
        using var raw=new InMemoryStateStore();
        var pair=Bound("iso",@"T:\Iso",1);
        await raw.SavePairAsync(pair,default); await raw.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        await raw.OweScopeAsync(pair.PairId,new Scope("A","A"),default);
        await raw.OweScopeAsync(pair.PairId,new Scope("B","B"),default);
        var pc=new FakeSide(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};
        var db=new FakeSide(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        pc.Seed(ItemChecks.File(Side.Local,"A",0,"pc-a",true),[]);
        // The unreadable folder alone stays owed (an unreadable file is only its own item's failure).
        pc.Seed(ItemChecks.File(Side.Local,"A/bad",0,"pc-bad",true),[]); pc.ReadFailures["A/bad"]=new ItemFailedException("A/bad",TextKey.ReadLocalFailed,null);
        pc.Seed(ItemChecks.File(Side.Local,"B",0,"pc-b",true),[]); pc.Seed(ItemChecks.File(Side.Local,"B/ok",1,"pc-bok"),[1]);
        var retired=Signal();
        var store=ScopedStore(raw,pair.PairId,null,null,new Scope("B","B"),retired);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_,_)=>Task.FromResult((QuietSide(pc),QuietSide(db))),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        using var stop=new CancellationTokenSource(); var ct=stop.Token;
        try
        {
            await host.StartAsync(ct);
            try { await retired.Task.WaitAsync(TimeSpan.FromSeconds(10),ct); }
            catch(TimeoutException e) { throw new InvalidDataException("An unreadable folder stranded an unrelated owed scope",e); }
            var owed=(await raw.PairAsync(pair.PairId,ct))?.OwedScopes ?? [];
            Check.Require(owed.Contains(new Scope("A/bad","A/bad")) && !owed.Contains(new Scope("B","B")),"An unrelated owed scope did not settle beside an unreadable one, or the unreadable folder was not owed again");
            Check.Require(db.Items.ContainsKey("B/ok"),"The unrelated owed scope did not sync its file");
            Check.Require(raw.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure),"The unreadable folder raised no failure notice");
        }
        finally {stop.Cancel();try{await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));}catch(TimeoutException){}}
    }
    // A scope a pass commits after the dispatch snapshot is runnable through the existing wake; no feed page follows.
    private static async Task OwedScopeHandOffAsync()
    {
        using var raw=new InMemoryStateStore();
        var pair=Bound("hand",@"T:\Hand",1);
        await raw.SavePairAsync(pair,default); await raw.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        await raw.OweScopeAsync(pair.PairId,new Scope("A","A"),default);
        var pc=new FakeSide(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};
        var db=new FakeSide(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        foreach(var (folder,file) in new[]{("A","A/ok"),("B","B/ok")})
        {
            pc.Seed(ItemChecks.File(Side.Local,folder,0,"pc-"+folder,true),[]);
            pc.Seed(ItemChecks.File(Side.Local,file,1,"pc-"+file),[1]);
        }
        var retired=Signal();
        var store=ScopedStore(raw,pair.PairId,new Scope("A","A"),new Scope("B","B"),new Scope("B","B"),retired);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_,_)=>Task.FromResult((QuietSide(pc),QuietSide(db))),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        using var stop=new CancellationTokenSource(); var ct=stop.Token;
        try
        {
            await host.StartAsync(ct);
            try { await retired.Task.WaitAsync(TimeSpan.FromSeconds(10),ct); }
            catch(TimeoutException e) { throw new InvalidDataException("A scope committed during a scoped pass was not scheduled again",e); }
            Check.Require((await raw.PairAsync(pair.PairId,ct))?.OwedScopes is not {Count:>0},"The hand-off left owed scopes behind");
            Check.Require(db.Items.ContainsKey("A/ok") && db.Items.ContainsKey("B/ok"),"The newly owed scope did not sync");
            Check.Require(raw.OpenNotices.All(n=>n.Kind!=NoticeKind.Failure),"A hand-off raised a failure");
        }
        finally {stop.Cancel();try{await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));}catch(TimeoutException){}}
    }
    // A scoped endpoint read failing with the production acquisition exception keeps its debt; an unrelated scope still settles.
    private static async Task OwedScopeReadFailureAsync()
    {
        using var raw=new InMemoryStateStore();
        var pair=Bound("read",@"T:\Read",1);
        await raw.SavePairAsync(pair,default); await raw.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        await raw.OweScopeAsync(pair.PairId,new Scope("A","A"),default);
        await raw.OweScopeAsync(pair.PairId,new Scope("B","B"),default);
        var pc=new FakeSide(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};
        var db=new FakeSide(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        pc.Seed(ItemChecks.File(Side.Local,"A",0,"pc-a",true),[]);
        pc.Seed(ItemChecks.File(Side.Local,"B",0,"pc-b",true),[]); pc.Seed(ItemChecks.File(Side.Local,"B/ok",1,"pc-bok"),[1]);
        var quiet=QuietSide(pc);
        var local=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>{
            if(m.Name==nameof(ISide.ReadOneAsync) && (string?)args[0]=="A") return Task.FromException<Entry?>(new ItemFailedException("A",TextKey.ReadLocalFailed,null));
            return AutoSyncChecks.Observation.Forward(quiet,m,args);
        });
        var retired=Signal();
        var store=ScopedStore(raw,pair.PairId,null,null,new Scope("B","B"),retired);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_,_)=>Task.FromResult((local,QuietSide(db))),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        using var stop=new CancellationTokenSource(); var ct=stop.Token;
        try
        {
            await host.StartAsync(ct);
            try { await retired.Task.WaitAsync(TimeSpan.FromSeconds(10),ct); }
            catch(TimeoutException e) { throw new InvalidDataException("A scoped endpoint-read failure stranded an unrelated owed scope",e); }
            var owed=(await raw.PairAsync(pair.PairId,ct))?.OwedScopes ?? [];
            Check.Require(owed.Contains(new Scope("A","A")) && !owed.Contains(new Scope("B","B")),"An unrelated owed scope did not settle beside an exact read failure");
            Check.Require(db.Items.ContainsKey("B/ok"),"The unrelated owed scope did not sync its file");
            Check.Require(raw.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure),"The exact read failure raised no failure notice");
        }
        finally {stop.Cancel();try{await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));}catch(TimeoutException){}}
    }
    // An explicit Repair that commits a scope owe and skips an unreadable folder completes and still wakes the newly owed scope.
    private static async Task ExplicitRepairPartialInventoryWakesAsync()
    {
        using var raw=new InMemoryStateStore();
        var pair=Bound("explicit",@"T:\Explicit",1);
        await raw.SavePairAsync(pair,default); await raw.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        var pc=new FakeSide(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};
        var db=new FakeSide(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        pc.Seed(ItemChecks.File(Side.Local,"B",0,"pc-b",true),[]); pc.Seed(ItemChecks.File(Side.Local,"B/ok",1,"pc-bok"),[1]);
        // An unrelated unreadable folder is skipped: the explicit full Repair completes for the rest.
        pc.Seed(ItemChecks.File(Side.Local,"bad",0,"pc-bad",true),[]); pc.Seed(ItemChecks.File(Side.Local,"bad/x",0,"pc-badx",true),[]); pc.ReadFailures["bad/x"]=new ItemFailedException("bad/x",TextKey.ReadLocalFailed,null);
        var owedOnce=new int[1]; var bootedOnce=new int[1]; var booted=Signal(); var retired=Signal();
        var store=AutoSyncChecks.Observation.Wrap<IStore>((m,args)=>{
            if(m.Name==nameof(IDisposable.Dispose)) return null;
            if(m.Name==nameof(IStore.PairAsync) && Interlocked.Exchange(ref bootedOnce[0],1)==0)
            {
                var pairRead=AutoSyncChecks.Observation.Forward(raw,m,args);
                if(pairRead is Task<PairConfig?> read) return SignalPair(read,booted);
                return pairRead;
            }
            if(m.Name==nameof(IStore.AddScanAsync) && Interlocked.Exchange(ref owedOnce[0],1)==0) return OweThenAddScan(raw,pair.PairId,new Scope("B","B"),m,args);
            if(m.Name==nameof(IStore.FinishRepairAsync) && args.Length==4 && args[1] is Scope s && s==new Scope("B","B"))
            {
                var done=AutoSyncChecks.Observation.Forward(raw,m,args);
                if(done is Task<bool> fin) return Retire(fin,retired);
                return done;
            }
            return AutoSyncChecks.Observation.Forward(raw,m,args);
        });
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_,_)=>Task.FromResult((QuietSide(pc),QuietSide(db))),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        using var stop=new CancellationTokenSource(); var ct=stop.Token;
        try
        {
            await host.StartAsync(ct);
            // The boot debt dispatch must take its no-debt snapshot before the explicit request, or the boot wake would retire B without the explicit hand-off.
            try { await booted.Task.WaitAsync(TimeSpan.FromSeconds(10),ct); }
            catch(TimeoutException e) { throw new InvalidDataException("Boot debt dispatch did not take its first snapshot",e); }
            bool threw=false;
            try { await host.RepairAsync(pair.PairId,ct); } catch(InventoryIncompleteException) { threw=true; }
            Check.Require(!threw,"One unreadable folder failed the whole explicit Repair");
            try { await retired.Task.WaitAsync(TimeSpan.FromSeconds(10),ct); }
            catch(TimeoutException e) { throw new InvalidDataException("A scope owed by an explicit partial-inventory Repair was not scheduled",e); }
            Check.Require(!((await raw.PairAsync(pair.PairId,ct))?.OwedScopes ?? []).Contains(new Scope("B","B")),"The newly owed scope was left behind");
            Check.Require(db.Items.ContainsKey("B/ok"),"The newly owed scope did not sync");
        }
        finally {stop.Cancel();try{await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));}catch(TimeoutException){}}
    }
    // A scope owed while a full Repair walks does not supersede it: the full run completes and the scope is still served after it.
    private static async Task FullRepairOwedScopeCompletesAsync()
    {
        using var raw=new InMemoryStateStore();
        var pair=Bound("full",@"T:\Full",1);
        await raw.SavePairAsync(pair,default); await raw.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        await raw.OweRepairAsync(pair.PairId,Side.Local,"gap",default);
        var pc=new FakeSide(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};
        var db=new FakeSide(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        pc.Seed(ItemChecks.File(Side.Local,"B",0,"pc-b",true),[]); pc.Seed(ItemChecks.File(Side.Local,"B/ok",1,"pc-bok"),[1]);
        var owedOnce=new int[1]; var retired=Signal();
        var store=AutoSyncChecks.Observation.Wrap<IStore>((m,args)=>{
            if(m.Name==nameof(IDisposable.Dispose)) return null;
            if(m.Name==nameof(IStore.AddScanAsync) && Interlocked.Exchange(ref owedOnce[0],1)==0) return OweThenAddScan(raw,pair.PairId,new Scope("B","B"),m,args);
            if(m.Name==nameof(IStore.FinishRepairAsync) && args.Length==4 && args[1] is Scope s && s==new Scope("B","B") && AutoSyncChecks.Observation.Forward(raw,m,args) is Task<bool> fin) return Retire(fin,retired);
            return AutoSyncChecks.Observation.Forward(raw,m,args);
        });
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_,_)=>Task.FromResult((QuietSide(pc),QuietSide(db))),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        using var stop=new CancellationTokenSource(); var ct=stop.Token;
        try
        {
            await host.StartAsync(ct);
            try { await retired.Task.WaitAsync(TimeSpan.FromSeconds(10),ct); }
            catch(TimeoutException e) { throw new InvalidDataException("A scope owed during a full Repair was not served after it",e); }
            Check.Require(Volatile.Read(ref owedOnce[0])==1 && (await raw.ReadRepairAsync(pair.PairId,Side.Local,ct)).Reason==null,"A scope owed mid-run superseded the full Repair");
            Check.Require(db.Items.ContainsKey("B/ok"),"The full Repair did not sync its file");
        }
        finally {stop.Cancel();try{await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));}catch(TimeoutException){}}
    }
    private static async Task<PairConfig?> SignalPair(Task<PairConfig?> read,TaskCompletionSource signal)
    {
        var value=await read; signal.TrySetResult(); return value;
    }
    private static async Task OweThenAddScan(IStore inner,string pairId,Scope owe,System.Reflection.MethodInfo m,object?[] args)
    {
        await inner.OweScopeAsync(pairId,owe,default);
        await (Task)AutoSyncChecks.Observation.Forward(inner,m,args)!;
    }
    // A reviewed Retry that commits/keeps an owed scope and then leaves on an independent job failure still wakes that scope (no page/boot wake).
    private static async Task DirectSettlementFailureWakesAsync()
    {
        using var raw=new InMemoryStateStore();
        var pair=Bound("direct",@"T:\Direct",1);
        await raw.SavePairAsync(pair,default); await raw.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        var pc=new FakeSide(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};
        var db=new FakeSide(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        pc.Seed(ItemChecks.File(Side.Local,"B",0,"pc-b",true),[]); pc.Seed(ItemChecks.File(Side.Local,"B/ok",1,"pc-bok"),[1]);
        // A reviewable Failure notice and a matching accepted job: running at boot, failing independently on the retry.
        await raw.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,"x",new([new(Side.Dropbox,"x","x-id")],Side.Dropbox),DateTimeOffset.UnixEpoch,false),default);
        var job=new AcceptedJob(pair.PairId,"files/delete_batch","jobX",[new("x-id","x",null)]);
        await raw.SaveJobAsync(job,default); db.JobReplies[job.JobId]=new([new JobReply.Running()]);
        var armed=new int[1]; var bootedOnce=new int[1]; var booted=Signal(); var retired=Signal();
        var quietRemote=QuietSide(db);
        var remote=AutoSyncChecks.Observation.Wrap<ISide>((m,args)=>{
            if(Volatile.Read(ref armed[0])!=0 && m.Name==nameof(ISide.ReadJobAsync) && args[0] is AcceptedJob j && j.JobId==job.JobId)
                return Task.FromException<JobReply>(new ItemFailedException("x",TextKey.ReadDropboxFailed,null));
            return AutoSyncChecks.Observation.Forward(quietRemote,m,args);
        });
        var store=AutoSyncChecks.Observation.Wrap<IStore>((m,args)=>{
            if(m.Name==nameof(IDisposable.Dispose)) return null;
            if(m.Name==nameof(IStore.PairAsync) && Interlocked.Exchange(ref bootedOnce[0],1)==0)
            {
                var pairRead=AutoSyncChecks.Observation.Forward(raw,m,args);
                if(pairRead is Task<PairConfig?> read) return SignalPair(read,booted);
                return pairRead;
            }
            if(m.Name==nameof(IStore.FinishRepairAsync) && args.Length==4 && args[1] is Scope s && s==new Scope("B","B"))
            {
                var done=AutoSyncChecks.Observation.Forward(raw,m,args);
                if(done is Task<bool> fin) return Retire(fin,retired);
                return done;
            }
            return AutoSyncChecks.Observation.Forward(raw,m,args);
        });
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_,_)=>Task.FromResult((QuietSide(pc),remote)),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        using var stop=new CancellationTokenSource(); var ct=stop.Token;
        try
        {
            await host.StartAsync(ct);
            try { await booted.Task.WaitAsync(TimeSpan.FromSeconds(10),ct); }
            catch(TimeoutException e) { throw new InvalidDataException("Boot debt dispatch did not take its first snapshot",e); }
            // The scope the successful side of the settlement owns; nothing but the settlement's hand-off may wake it.
            await raw.OweScopeAsync(pair.PairId,new Scope("B","B"),default);
            Volatile.Write(ref armed[0],1);
            Notice? notice=null;
            await foreach(var n in host.NoticesAsync(pair.PairId,ct)) { notice=n; break; }
            Check.Require(notice is {Reviewed:not null},"The failure notice was not exposed for review");
            bool threw=false;
            try { await host.ResolveAsync(pair.PairId,notice!.Id,notice.Reviewed!,ResolutionChoice.Retry,null,ct); }
            catch(ItemFailedException) { threw=true; }
            Check.Require(threw,"The Retry hid its independent job failure");
            try { await retired.Task.WaitAsync(TimeSpan.FromSeconds(10),ct); }
            catch(TimeoutException e) { throw new InvalidDataException("A scope owed by a direct settlement that failed independently was not scheduled",e); }
            Check.Require(!((await raw.PairAsync(pair.PairId,ct))?.OwedScopes ?? []).Contains(new Scope("B","B")),"The direct-settlement failure left the owed scope behind");
            Check.Require(db.Items.ContainsKey("B/ok"),"The newly owed scope did not sync");
        }
        finally {stop.Cancel();try{await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));}catch(TimeoutException){}}
    }
    private static async Task IndependentBind()
    {
        using var db=new InMemoryStateStore();
        var slow=ItemChecks.Pair() with {PairId="slow"}; var other=slow with {PairId="other"};
        var roots=new RootIdentity(other.LocalRoot,other.DropboxRoot,other.ConnectionId,new(1,2,null),new("acct","root","home","folder"));
        other=other with {Roots=roots};
        await db.SavePairAsync(slow,default); await db.SavePairAsync(other,default);
        using var lifetime=new CancellationTokenSource();
        var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pairToken=default;
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),async (p,_,_,_,ct)=>{
            if(p.PairId==slow.PairId) {pairToken=ct; entered.SetResult(); await held.Task.WaitAsync(ct);}
            var local=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>(); var probe=(JournalProbe)(object)local;
            probe.Local=new(Side.Local){Root=(roots.Local,null,null,DriveType.Fixed)};probe.Quiet=true;
            if(p.PairId==other.PairId) probe.Published=()=>{if(!probe.Paused) observed.TrySetResult();};
            return (local,QuietSide(new(Side.Dropbox){Root=(null,roots.Dropbox,null,null)}));
        },ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        var startup=host.StartAsync(lifetime.Token);
        await WithCleanup(async ()=>{try
        {
            await Task.WhenAll(entered.Task,observed.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
            await using var pairs=host.PairsAsync(default).GetAsyncEnumerator();
            Check.Require(await pairs.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),"Pending first bind monopolized the CLI");
            await pairs.DisposeAsync();
            await host.PauseAsync(slow.PairId,default).WaitAsync(TimeSpan.FromSeconds(5));
            Check.Require(pairToken.IsCancellationRequested && !lifetime.IsCancellationRequested,"Pause did not cancel just the pending member");
        }
        catch(TimeoutException e) {throw new InvalidDataException("One pending factory delayed host readiness, the other pair, or the CLI",e);}
        },async ()=>{ held.TrySetResult(); lifetime.Cancel(); try { await startup; } catch(OperationCanceledException) { } await host.StopAsync(default); });

    }
    private static async Task ForeignCancellation()
    {
        using var db=new InMemoryStateStore(); var p=ItemChecks.Pair(); var bad=p with {PairId="broken"};
        await db.SavePairAsync(bad,default);
        var attempted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(_,_,_,_,_)=>{
            attempted.TrySetResult(); throw new OperationCanceledException();
        },ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try {await host.StartAsync(default);} catch(OperationCanceledException e){throw new InvalidDataException("Provider cancellation escaped pair admission",e);}
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5)); await host.PauseAsync(bad.PairId,default);
        Check.Require(db.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure),"A failed first bind has no notice");
        await host.ResumeAsync(bad.PairId,default); await host.StopAsync(default);
    }
    private static async Task DisconnectedAdapter()
    {
        using var db=new InMemoryStateStore(); var p=ItemChecks.Pair();
        var roots=new RootIdentity(p.LocalRoot,p.DropboxRoot,p.ConnectionId,new(1,3,null),new("account","root","home","folder"));
        p=p with {Roots=roots,Enabled=false};await db.SavePairAsync(p,default);
        bool connected=true;int staleReads=0,denials=0;
        var opened=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(pair,_,_,_)=>{
            if(!connected){Interlocked.Increment(ref denials);observed.TrySetResult();throw new EngineRequestException(TextKey.AccountMissing);}
            var local=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>(); var pc=(JournalProbe)(object)local;
            pc.Local=new(Side.Local){Root=(roots.Local,null,null,DriveType.Fixed)};pc.Published=()=>opened.TrySetResult();
            var cloud=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var remote=(JournalProbe)(object)cloud;
            remote.Local=new(Side.Dropbox){Root=(null,roots.Dropbox,null,null)};
            pc.Quiet=remote.Quiet=true;
            remote.ReadRoot=()=>{if(!connected){Interlocked.Increment(ref staleReads);observed.TrySetResult();}};
            return(local,cloud);
        },ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>{connected=false;return Task.CompletedTask;});
        try
        {
            await host.StartAsync(default);await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.DisconnectAsync(p.ConnectionId,default);await host.ResumeAsync(p.PairId,default);
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try {await host.BindAsync(p.PairId,default);}catch(EngineRequestException){}
            Check.Require(staleReads==0 && denials>=2,"Disconnected slot reached its cached Dropbox adapter instead of checking membership on Resume and Bind");
        } finally {await host.StopAsync(default);}
    }
    public class JournalProbe : System.Reflection.DispatchProxy
    {
        internal FakeSide Local=null!;
        internal bool Paused;
        internal int ReadAhead,Settled,Rewinds;
        internal Action? Published;
        internal bool FailRoot;
        internal Action? ReadRoot;
        internal Func<CancellationToken,Task<JobReply>>? Job;
        internal int JobReads;
        internal Func<string?,CancellationToken,Task<ChangePage>>? Feed;
        internal bool Quiet;
        internal Func<CancellationToken,Task<(string? Cursor,JournalPosition? Position)>>? Boundary;
        internal Func<JournalPosition,JournalPosition>? OnSettle;
        private JournalPosition? committed;
        private static async Task<ChangePage> WaitForStop(CancellationToken ct){await Task.Delay(Timeout.Infinite,ct);return new([],null,null);}
        // The endpoint takes the settlement first; only then is it committed and acknowledged, so a waiter cannot stop the host before it ran.
        private async Task Settle(System.Reflection.MethodInfo method,object?[] args)
        {
            try {await (Task)method.Invoke(Local,args)!;}
            catch(System.Reflection.TargetInvocationException error) when(error.InnerException!=null)
            {System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();}
            var offered=(JournalPosition)args[0]!;committed=OnSettle?.Invoke(offered)??offered;
        }
        protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? args)
        {
            if(method!.Name==nameof(ISide.BoundaryAsync) && Boundary!=null) return Boundary((CancellationToken)args![0]!);
            if(method.Name==nameof(ISide.ReadJobAsync) && Job!=null){Interlocked.Increment(ref JobReads);return Job((CancellationToken)args![1]!);}
            if(Quiet && method.Name==nameof(ISide.SettleJournalAsync)) return Settle(method,args!);
            if(Quiet && method.Name==nameof(ISide.SettledJournalAsync)) return Task.FromResult(committed);
            if(Quiet && method.Name==nameof(ISide.ReadChangesAsync) && args?.Length==3) return Feed?.Invoke((string?)args[0],(CancellationToken)args[2]!) ?? WaitForStop((CancellationToken)args[2]!);
            if(method.Name==nameof(ISide.RootAsync)) ReadRoot?.Invoke();
            if(method.Name==nameof(ISide.RootAsync) && FailRoot) throw new IOException("unavailable root");
            if(method.Name==nameof(ISide.SetMembershipAsync))
            {
                bool paused=(bool)args![1]!; if(paused!=Paused){ReadAhead=Settled;Rewinds++;}
                Paused=paused; Published?.Invoke(); return Task.CompletedTask;
            }
            // The endpoint's own exception, not reflection's wrapper (FakeSide throws some faults synchronously).
            try {return method.Invoke(Local,args);}
            catch(System.Reflection.TargetInvocationException error) when(error.InnerException!=null)
            {System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();throw;}
        }
    }
    private static async Task MemberRewind()
    {
        using var db=new InMemoryStateStore(); var p=ItemChecks.Pair();
        var root=new RootIdentity(p.LocalRoot,p.DropboxRoot,p.ConnectionId,new(5,6,null),new("account","root","home","folder"));
        p=p with {Roots=root}; var second=p with {PairId="sibling"};
        await db.SavePairAsync(p,default); await db.SavePairAsync(second,default);
        var ready=new CountdownEvent(2);
        var probes=new System.Collections.Concurrent.ConcurrentDictionary<string,ISide>();
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(pair,_,_,_)=>{
            // Reopening an adapter still addresses the same journal member and its settled position.
            var local=probes.GetOrAdd(pair.PairId,id=>{
                var side=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>(); var probe=(JournalProbe)(object)side;
                probe.Local=new(Side.Local){Root=(root.Local,null,null,DriveType.Fixed)};probe.Quiet=true;
                int once=0;probe.Published=()=>{if(Interlocked.Exchange(ref once,1)==0)ready.Signal();};
                return side;
            });
            return(local,QuietSide(new(Side.Dropbox){Root=(null,root.Dropbox,null,null)}));
        },ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        await host.StartAsync(default); Check.Require(ready.Wait(TimeSpan.FromSeconds(5)),"Members not published");
        var a=(JournalProbe)(object)probes[p.PairId]; var b=(JournalProbe)(object)probes[second.PairId]; a.Settled=4;a.ReadAhead=7;b.Settled=3;b.ReadAhead=9;
        await host.BindAsync(p.PairId,default);
        Check.Require(a.ReadAhead==4 && !a.Paused && a.Rewinds>=2 && b.ReadAhead==9,"Bind lost read-ahead or rewound an unrelated journal member");
        await host.StopAsync(default); ready.Dispose();
    }

}
