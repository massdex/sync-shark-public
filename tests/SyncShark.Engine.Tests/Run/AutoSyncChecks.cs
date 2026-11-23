using System.Reflection;
using System.Runtime.ExceptionServices;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static class AutoSyncChecks
{
    internal static async Task WithinDeadline(Func<CancellationToken,Task> run,[System.Runtime.CompilerServices.CallerMemberName] string check="")
    {
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try {await run(budget.Token).WaitAsync(budget.Token);}
        catch(OperationCanceledException e) when(budget.IsCancellationRequested) {throw new InvalidDataException($"{check} did not finish before its deadline",e);}
    }

    public static Task SourceRetryAsync() => WithinDeadline(async ct=>
    {
        // No jitter in this fixture: each observed duration identifies its attempt in the configured schedule.
        var settings=ItemChecks.Settings with {Backoff=new(0.01,2,0,0.025)};
        var initial=settings.Backoff.InitialBackoffSeconds;var second=initial*settings.Backoff.BackoffMultiplier;
        var positive=TimeSpan.FromSeconds(initial/2);var expected=new[]{initial,second,settings.Backoff.MaximumBackoffSeconds,positive.TotalSeconds,0d};
        var first=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts=0,waits=0;var next=new ChangePage([],"next",null);
        async Task<ChangePage> Read(CancellationToken t)
        {
            switch(attempts++)
            {
                case 0: throw Failure(TimeSpan.Zero);
                case 1: await release.Task.WaitAsync(t);throw Failure(null);
                case 2: throw new IOException("offline");
                case 3: throw Failure(positive);
                case 4: throw Failure(positive,TimeProvider.System.GetTimestamp()-TimeProvider.System.TimestampFrequency);
                default: return next;
            }
        }
        static ItemFailedException Failure(TimeSpan? floor,long? received=null)=>new("item",TextKey.ReadDropboxFailed,null){Disposition=FailureDisposition.TemporaryNoEffect,ProviderWait=floor,ProviderWaitStarted=received};
        void Observe(RunEvent e)
        {
            var seconds=expected[waits++].ToString(System.Globalization.CultureInfo.InvariantCulture);
            Check.Require(e.Cause==TextKey.WaitSourceRetry && e.Side==Side.Dropbox && e.Detail==Text.Get(TextKey.WaitSourceRetry,(TextArg.Seconds,seconds)),"Source retry did not use the attempt schedule or the remaining positive provider wait");
            first.TrySetResult();
        }
        var retry=AutoSync.ReadPageAsync(Read,Side.Dropbox,settings,Observe,ct);
        if(await Task.WhenAny(first.Task,retry).WaitAsync(ct)==retry) await retry;
        var other=await AutoSync.ReadPageAsync(_=>Task.FromResult(new ChangePage([],null,new(1,7))),Side.Local,settings,_=>{},ct);
        Check.Require(other.Position==new JournalPosition(1,7) && !retry.IsCompleted,"Failed source cancelled independent acquisition");
        release.TrySetResult();Check.Require(await retry==next && waits==expected.Length && attempts==waits+1,"Failed source did not retry its original read");
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            int reads=0, announced=0;
            var oversized=AutoSync.ReadPageAsync(_=>++reads==1 ? throw Failure(TimeSpan.FromMilliseconds(uint.MaxValue)) : Task.FromResult(next),Side.Dropbox,settings,_=>++announced,cancel.Token);
            Check.Require(announced==1 && reads==1 && !oversized.IsCompleted,"Oversized retry re-entered acquisition before cancellation");
            cancel.Cancel();await oversized;
            throw new InvalidDataException("Oversized provider wait was shortened instead of cancelled");
        }
        catch(OperationCanceledException) when(cancel.IsCancellationRequested && !ct.IsCancellationRequested) { }
        catch(ArgumentOutOfRangeException e) {throw new InvalidDataException("Oversized provider wait exceeded a single timer",e);}

    });

    public static Task ChangedItemReplayAsync() => WithinDeadline(async ct=>
    {
        using var store=new InMemoryStateStore();var pair=ItemChecks.Pair();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
        var queue=new WorkQueue();var paths=new PathScope(pair,[pair],DriveType.Fixed);await store.SavePairAsync(pair,ct);
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,static()=>{},ct);
        var edits=new List<Change>();
        foreach(var path in new[]{"racing","sibling"})
        {
            var file=ItemChecks.File(Side.Dropbox,path,2,path);db.Seed(file,[2]);edits.Add(new(Side.Dropbox,ChangeKind.Changed,path,null,file.Id,file,false,false));
        }
        int attempts=0;var local=Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.ReadOneAsync) && (string?)args[0]=="racing" && Interlocked.Increment(ref attempts)==1 ?
            Task.FromException<Entry?>(new ChangedUnderneathException("racing")) : Observation.Forward(pc,m,args));
        try {await AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,Side.Dropbox,new(edits,"done",null),local,db,store,ct);}
        catch(ChangedUnderneathException e) when(e.Path=="racing") {throw new InvalidDataException("Child precondition failure escaped the pair instead of re-observing its input");}
        Check.Require(attempts>=2 && pc.Items.TryGetValue("racing",out var fileA) && fileA.Bytes.SequenceEqual(new byte[]{2}) && pc.Items.TryGetValue("sibling",out var fileB) && fileB.Bytes.SequenceEqual(new byte[]{2}) && await store.CursorAsync(pair.PairId,ct)=="done","Child precondition failure escaped the pair instead of re-observing its input");
    });

    // RAW 30 is available, but Local can prove projection only through 11. The selected peer must not be sent to trash.
    public static Task UnprovedFolderStaysAsync() => FolderProofAsync(null);
    public static Task ProofDuringCallbackAsync() => FolderProofAsync(true);
    public static Task ProofAfterIndexAsync() => FolderProofAsync(false);

    private static Task FolderProofAsync(bool? duringOriginal) => RepairChecks.WithRepairStore(async (store,pair,deadline)=>
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(deadline);var ct=budget.Token;
        var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var paths=new PathScope(pair,[pair],DriveType.Fixed);var queue=new WorkQueue();
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,static()=>{},ct);
        var folder=duringOriginal switch {true=>"during", false=>"after", null=>"denied"};
        var removed=ItemChecks.File(Side.Local,folder,0,"11",true);var target=ItemChecks.File(Side.Dropbox,folder,0,"remote",true);
        var child=ItemChecks.File(Side.Dropbox,folder+"/child",7,"child");db.Seed(target,[]);db.Seed(child,[7]);
        var binding=SyncedEntries.From(removed,target);await store.CommitAsync(pair.PairId,null,binding,null,null,ct);
        var projected=new JournalPosition(1,11);var raw=new JournalPosition(1,30);const string cloud="acquired";
        var cloudRegistered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var tried=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int visits=0;
        int cloudReads=0;
        Task<ChangePage> Cloud(){bool second=++cloudReads==2;if(second) cloudRegistered.TrySetResult();return Task.FromResult(new ChangePage([],cloud,null){CaughtUp=second});}
        var admitted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int journalReads=0;
        async Task<ChangePage> Journal(CancellationToken t)
        {
            if(++journalReads==1)
            {
                await cloudRegistered.Task.WaitAsync(t);
                return new([new(Side.Local,ChangeKind.Deleted,removed.Path,null,removed.Id,null,false,true)],null,raw){CaughtUp=duringOriginal==null,ProjectedThrough=projected};
            }
            await tried.Task.WaitAsync(t);
            if(duringOriginal==false) while(!queue.Held(new(removed.Path,target.Path)).Any(c=>c.Id==removed.Id)){t.ThrowIfCancellationRequested();await Task.Yield();}
            return new([],null,raw){CaughtUp=true,ProjectedThrough=raw};
        }
        var local=Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.SettleJournalAsync)?store.SavePositionAsync("1",(JournalPosition)args[0]!, (CancellationToken)args[1]!):m.Name==nameof(ISide.ReadChangesAsync) && args.Length==4?Journal((CancellationToken)args[3]!):m.Name==nameof(ISide.BoundaryAsync)?Task.FromResult(((string?)null,(JournalPosition?)raw)):Observation.Forward(pc,m,args));
        // ChangeAsync reads the exact member's notices once per callback, after AutoSync sampled its proof.
        int callbacks=0;var observed=Observation.Wrap<IStore>((m,args)=>{
            if(m.Name==nameof(IStore.NoticesAtAsync) && Equals(args[1],binding.LocalKey)) Interlocked.Increment(ref callbacks);
            return Observation.Forward(store,m,args);
        });
        var remote=Observation.Wrap<ISide>((m,args)=>{
            if(m.Name==nameof(ISide.ReadChangesAsync) && args.Length==4) return Cloud();
            if(m.Name==nameof(ISide.BoundaryAsync)) return Task.FromResult(((string?)cloud,(JournalPosition?)null));
            if(m.Name==nameof(ISide.ReadByIdAsync) && (string?)args[0]==target.Id && Volatile.Read(ref callbacks)==(duringOriginal==false?2:1) && Interlocked.Increment(ref visits)==1){tried.TrySetResult();if(duringOriginal==true) return AfterAdmission((CancellationToken)args[^1]!);}
            if(m.Name==nameof(ISide.DeleteAsync)) return Delete((Entry)args[0]!, (Task<RemovalResult>)Observation.Forward(db,m,args)!);
            return Observation.Forward(db,m,args);
        });
        async Task<(IdentityLocation Location,bool InScope)?> AfterAdmission(CancellationToken t){await admitted.Task.WaitAsync(t);return await db.ReadByIdAsync(target.Id,t);}
        async Task<RemovalResult> Delete(Entry selected,Task<RemovalResult> request)
        {
            var result=await request;
            if(duringOriginal==null) throw new InvalidDataException("Folder removal without projected read-through did not leave the folder in place");
            return result.Outcome==RemovalOutcome.Removed ? result with {Reply=new JobReply.Terminal([(selected,null,null)])} : result;
        }
        var run=AutoSync.ReadThroughAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,remote,observed,(raw,cloud),()=>{
            if(duringOriginal==true) Check.Require(callbacks==1 && visits==1 && tried.Task.IsCompletedSuccessfully && journalReads==2 && cloudReads==2 && !db.Mutations.Contains(target.Id),"Proof did not arrive while the original removal callback was held");
            admitted.TrySetResult();
        },ct);
        string stalled=duringOriginal==false ? "Proof admitted after indexing did not wake the held removal" : "Proof admitted during the callback did not settle the held removal";
        try
        {
            if(duringOriginal!=null)
            {
                await run.WaitAsync(ct);
                Check.Require(!db.Items.ContainsKey(target.Path) && db.Mutations.Count(id=>id==target.Id)==1 && await store.PositionAsync("1",ct)==raw && tried.Task.IsCompletedSuccessfully && admitted.Task.IsCompletedSuccessfully && journalReads==2 && cloudReads==2 && callbacks>=(duringOriginal==true?2:3),stalled);
                return;
            }
            if(await Task.WhenAny(tried.Task,run).WaitAsync(ct)==run) await run;
            Check.Require(tried.Task.IsCompletedSuccessfully && !run.IsCompleted && db.Items.ContainsKey(target.Path) && db.Items.TryGetValue(child.Path,out var kept) && kept.Bytes.SequenceEqual(new byte[]{7}) && !db.Mutations.Contains(target.Id) && await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(removed.Path),ct)==binding,"Folder removal without projected read-through did not leave the folder in place");
        }
        catch(OperationCanceledException e) when(ct.IsCancellationRequested){throw new InvalidDataException(duringOriginal!=null && admitted.Task.IsCompletedSuccessfully ? stalled : "Folder-removal evidence was not checked before deadline",e);}
        finally{budget.Cancel();try{await run.WaitAsync(TimeSpan.FromSeconds(30));}catch(OperationCanceledException) when(ct.IsCancellationRequested){} catch(TimeoutException e){throw new InvalidDataException("Folder-removal check did not stop",e);}}
    });

    public static Task FiniteAttemptAsync() => WithinDeadline(async ct=>
    {
        var local=new TaskCompletionSource(); var cloud=new TaskCompletionSource(); var held=new TaskCompletionSource();
        int attempts=0; var entered=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish=AutoSync.FinishIntakeAsync(Task.WhenAll(local.Task,cloud.Task),()=>Interlocked.Increment(ref attempts),()=>{entered.SetResult(attempts);return held.Task;});
        local.SetResult(); Check.Require(attempts==0 && !entered.Task.IsCompleted,"Attempt reported before both finite reads");
        cloud.SetResult(); int observed=await entered.Task.WaitAsync(ct); bool waiting=!finish.IsCompleted;
        held.SetResult(); await finish.WaitAsync(ct);
        Check.Require(observed==1 && waiting && attempts==1,"Held input postponed the attempt signal until settlement");
    });

    public static Task BlockedFinitePageAsync() => WithinDeadline(async ct=>
    {
        var pair=ItemChecks.Pair(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var paths=new PathScope(pair,[pair],DriveType.Fixed); var queue=new WorkQueue();
        var observed=ItemChecks.File(Side.Local,"item",1,"file"); var remote=ItemChecks.File(Side.Dropbox,"item",1,"cloud");
        pc.Seed(observed,[1]); db.Seed(remote,[1]); var end=new JournalPosition(1,2);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(observed,remote),null,null,ct);
        var change=new Change(Side.Local,ChangeKind.Changed,"item",null,observed.Id,observed,false,false,1,JournalId:1);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var predecessor=queue.AdmitAsync(pair,new([change],null,null),(_,t)=>release.Task.WaitAsync(t),null,ct);
        pc.Pages.Enqueue(new([change],null,end)); await store.SaveCursorAsync(pair.PairId,"steady",ct);
        var attempted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int count=0;
        var finite=AutoSync.ReadThroughAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,(end,"steady"),()=>{Interlocked.Increment(ref count);attempted.TrySetResult();},ct);
        try
        {
            await Task.WhenAny(finite,attempted.Task).WaitAsync(ct); if(finite.IsFaulted) await finite;
            Check.Require(count==1 && !finite.IsCompleted && pc.Pages.Count==0 && queue.Held(new("item","item")).Any(),"Finite attempt did not include the prerequisite-blocked page before drain");
        }
        finally {release.TrySetResult();}
        await Task.WhenAll(predecessor,finite).WaitAsync(ct);
        Check.Require(count==1 && pc.Requests[nameof(ISide.SettleJournalAsync)]==1,"Attempt signal repeated or page failed to settle");
    });

    public static async Task ResetBehindHeldPageAsync()
    {
        await ResetBehindHeldPage(false);await ResetBehindHeldPage(true);
    }

    private static async Task ResetBehindHeldPage(bool continuous)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct=timeout.Token;
        var pair=ItemChecks.Pair(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        using var store=new InMemoryStateStore(); await store.SavePairAsync(pair,ct);
        var queue=new WorkQueue(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,static()=>{},ct);
        var before=ItemChecks.File(Side.Local,"held",1,"local"); var old=ItemChecks.File(Side.Dropbox,"held",1,"remote");
        var edit=ItemChecks.File(Side.Dropbox,"held",2,old.Id); pc.Seed(before,[1]); db.Seed(edit,[2]);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(before,old),null,null,ct); await store.SaveCursorAsync(pair.PairId,"initial",ct);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var change=new Change(Side.Dropbox,ChangeKind.Changed,"held",null,edit.Id,edit,false,false);
        var blocker=queue.AdmitAsync(pair,new([change],null,null),(_,t)=>release.Task.WaitAsync(t),null,ct);
        db.Pages.Enqueue(new([change],"obsolete",null)); db.Pages.Enqueue(new([],"fresh",null){ContinuityLost=true});
        var end=new JournalPosition(3,7); pc.Pages.Enqueue(new([],null,end){ProjectedThrough=end});
        var allowLocal=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetSaved=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int count=0;
        var localLive=new TaskCompletionSource<JournalPosition?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var live=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed=Observation.Wrap<IStore>((m,args)=>m.Name==nameof(IStore.ResetCursorAsync) ? Reset((string)args[1]!, (string)args[2]!, (CancellationToken)args[3]!) :
            m.Name==nameof(IStore.SaveCursorAsync) && (string?)args[1]=="after" ? SaveAfter((CancellationToken)args[2]!) : Observation.Forward(store,m,args));
        async Task SaveAfter(CancellationToken t){await store.SaveCursorAsync(pair.PairId,"after",t);saved.TrySetResult();}
        async Task Reset(string fresh,string reason,CancellationToken t){await store.ResetCursorAsync(pair.PairId,fresh,reason,t);resetSaved.TrySetResult();}
        async Task<ChangePage> Idle(CancellationToken t){await Task.Delay(Timeout.Infinite,t);throw new InvalidOperationException();}
        var local=Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.BoundaryAsync) ? Task.FromResult(((string?)null,(JournalPosition?)end)) : m.Name==nameof(ISide.ReadChangesAsync) ? (args.Length==4 ? FinishLocal((CancellationToken)args[3]!) : LocalLive((JournalPosition?)args[1],(CancellationToken)args[2]!)) : Observation.Forward(pc,m,args));
        var remote=Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.BoundaryAsync) ? Task.FromResult(((string?)(live.Task.IsCompleted?"after":"fresh"),(JournalPosition?)null)) : m.Name==nameof(ISide.ReadChangesAsync) && args.Length==3 ? Live((string?)args[0],(CancellationToken)args[2]!) : Observation.Forward(db,m,args));
        Task<ChangePage> LocalLive(JournalPosition? from,CancellationToken t){localLive.TrySetResult(from);return Idle(t);}
        async Task<ChangePage> Live(string? cursor,CancellationToken t)
        {
            if(live.TrySetResult(cursor)) return new([],"after",null);
            return await Idle(t);
        }
        async Task<ChangePage> FinishLocal(CancellationToken t){await allowLocal.Task.WaitAsync(t);return await pc.ReadChangesAsync(null,null,(null,end),t);}
        var intake=AutoSync.ReadThroughAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,remote,observed,(end,"end"),()=>{Interlocked.Increment(ref count);signal.TrySetResult();},ct,continuous);
        try
        {
            if(await Task.WhenAny(resetSaved.Task,intake).WaitAsync(ct)==intake) {await intake; throw new InvalidDataException("Feed returned before publishing reset");}
            Check.Require(count==0 && (await store.ReadRepairAsync(pair.PairId,Side.Dropbox,ct)).Reason!=null && await store.CursorAsync(pair.PairId,ct)=="fresh" && queue.Held(new("held","held")).Any(c=>c.Observed==edit),"Reset lost its held edit, omitted its debt, or reported an unfinished source as attempted");
            allowLocal.SetResult();
            if(await Task.WhenAny(signal.Task,intake).WaitAsync(ct)==intake) {await intake;throw new InvalidDataException("Finite reset skipped its actual attempt signal");}
            if(continuous) Check.Require(await live.Task.WaitAsync(ct)=="fresh" && await localLive.Task.WaitAsync(ct)==end && !saved.Task.IsCompleted,"Live reader lost its acquired continuation or settled past a held finite page");
            Check.Require(count==1 && pc.Pages.Count==0 && !intake.IsCompleted,"Finite reset waited for settlement or abandoned the other source");
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw new InvalidDataException("Held page prevented publication of verified reset and Repair debt");}
        finally {allowLocal.TrySetResult(); release.TrySetResult();}
        try
        {
            if(continuous)
            {
                await saved.Task.WaitAsync(ct);timeout.Cancel();
                try{await intake.WaitAsync(TimeSpan.FromSeconds(30));}catch(OperationCanceledException) when(ct.IsCancellationRequested){}
            }
            else await intake.WaitAsync(ct);
            await blocker.WaitAsync(ct);
        }
        catch(TimeoutException e) {throw new InvalidDataException("Continuous reset reader did not stop",e);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw new InvalidDataException("Admitted change did not settle after reset released its predecessor");}
        Check.Require(pc.Items["held"].Bytes.SequenceEqual(new byte[]{2}) && await store.CursorAsync(pair.PairId,default)==(continuous?"after":"fresh"),"The prior page lost its edit or rewound the reset when it settled");
    }

    public static async Task RunAsync()
    {
        await FiniteAttemptAsync(); await SourceRetryAsync(); await SourceFailureWithHeldPageAsync(); await BlockedFinitePageAsync(); await ResetBehindHeldPageAsync();
        await ManualRetryAsync();
    }

    public static async Task SourceFailureWithHeldPageAsync()
    {
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(30));var ct=budget.Token;
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();await store.SavePairAsync(pair,ct);
        var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var waiting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fault=new ChangedUnderneathException(pair.LocalRoot);
        async Task<ChangePage> Fail(CancellationToken token){await waiting.Task.WaitAsync(token);throw fault;}
        var exiting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int exited=0;
        async Task Hold(CancellationToken token)
        {
            waiting.TrySetResult();
            try {await Task.Delay(Timeout.Infinite,token);}
            finally {exiting.TrySetResult();await release.Task.WaitAsync(ct);Volatile.Write(ref exited,1);}
        }
        var local=Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.ReadChangesAsync)?Fail((CancellationToken)args[^1]!):Observation.Forward(pc,m,args));
        var observed=Observation.Wrap<IStore>((m,args)=>m.Name==nameof(IStore.SaveCursorAsync)?Hold((CancellationToken)args[^1]!):Observation.Forward(store,m,args));
        db.Pages.Enqueue(new([],"held",null));
        var intake=AutoSync.ReadThroughAsync(pair,ItemChecks.Settings,_=>{},paths,new WorkQueue(),local,db,observed,(new(1,1),"held"),static()=>{},ct);
        try
        {
            if(await Task.WhenAny(exiting.Task,intake).WaitAsync(ct)==intake && !waiting.Task.IsCompleted) await intake;
            // A missing queue implementation never entered the publication and is not a join failure.
            Check.Require(exiting.Task.IsCompleted && !intake.IsCompleted,"Source failure returned before held publication exited");
        }
        finally {release.TrySetResult();}
        try {await intake.WaitAsync(ct);}
        catch(ChangedUnderneathException e) when(ReferenceEquals(e,fault)) {Check.Require(Volatile.Read(ref exited)==1,"Source failure returned before held publication exited");Check.Require(await store.CursorAsync(pair.PairId,ct)==null,"Failed reader published the held cursor");return;}
        catch(OperationCanceledException) {throw new InvalidDataException("Held page blocked or replaced the typed source failure");}
        throw new InvalidDataException("Held page swallowed the typed source failure");
    }

    public static async Task ManualRetryAsync()
    {
        const string root="parent", path="parent/item";
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct=timeout.Token;
        var pair=ItemChecks.Pair(); pair=pair with {Roots=new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("a","r","h","d"))};
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        pc.Root=(pair.Roots!.Local,null,null,DriveType.Fixed); db.Root=(null,pair.Roots.Dropbox,null,null);
        await store.SavePairAsync(pair,ct); await store.FinishRepairAsync(pair.PairId,(0,0),"{}",ct);
        await store.SavePositionAsync("1",new(1,1),ct); await store.SaveCursorAsync(pair.PairId,"before",ct);
        var old=ItemChecks.File(Side.Local,root,0,"pc-parent",true); var peer=ItemChecks.File(Side.Dropbox,root,0,"remote-parent",true);
        db.Seed(peer,[]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,ct);
        var first=ItemChecks.File(Side.Dropbox,path,1,"child"); var last=ItemChecks.File(Side.Dropbox,path,2,"child"); db.Seed(last,[2]);
        pc.PublishFailures[path]=new ItemFailedException(path,TextKey.FailLocalInUse,null);
        Change Report(Entry v)=>new(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false);
        var end=new JournalPosition(1,2);
        pc.Pages.Enqueue(new([new(Side.Local,ChangeKind.Deleted,root,null,old.Id,null,false,true,1,JournalId:1)],null,end){ProjectedThrough=end});
        db.Pages.Enqueue(new([Report(first),new(Side.Dropbox,ChangeKind.Deleted,path,null,null,null,false,true),Report(last)],"held",null));
        db.Pages.Enqueue(new([],"after",null));
        var admitted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refused=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int afterRecovery=0,independent=0;
        var memory=Observation.Wrap<IStore>((m,args)=>{
            if(m.Name==nameof(IStore.CloseNoticeAsync) && args[1] is Notice {Kind:NoticeKind.PutBack}) Interlocked.Exchange(ref afterRecovery,1);
            if(m.Name==nameof(IStore.NoticesAtAsync) && (string?)args[1]==PathNames.Key(path) && Volatile.Read(ref afterRecovery)!=0) Interlocked.Increment(ref independent);
            if(m.Name==nameof(IStore.SaveCursorAsync) && (string?)args[1]=="after") Interlocked.Exchange(ref afterRecovery,0);
            var result=Observation.Forward(store,m,args);
            if(m.Name==nameof(IStore.SaveNoticeAsync) && args[1] is Notice {Kind:NoticeKind.PutBack,Cause:TextKey.FailLocalInUse}) refused.TrySetResult();
            return result;
        });
        async Task<ChangePage> Idle(CancellationToken token){await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();}
        var local=Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.ReadChangesAsync) && args.Length==3 ? Idle((CancellationToken)args[2]!) : m.Name==nameof(ISide.ReadByIdAsync) && (string?)args[0]==old.Id ? MissingAfterAdmission() :
            m.Name==nameof(ISide.BoundaryAsync) ? Task.FromResult(((string?)null,(JournalPosition?)end)) :
            m.Name==nameof(ISide.SettledJournalAsync) ? store.PositionAsync("1",ct) : Observation.Forward(pc,m,args));
        async Task<(IdentityLocation Location,bool InScope)?> MissingAfterAdmission(){await admitted.Task.WaitAsync(ct); return await pc.ReadByIdAsync(old.Id,ct);}
        var cloud=Observation.Wrap<ISide>((m,args)=>{
            if(m.Name==nameof(ISide.BoundaryAsync)) return Task.FromResult(((string?)"after",(JournalPosition?)null));
            if(m.Name==nameof(ISide.ReadChangesAsync) && args.Length==3) return Idle((CancellationToken)args[2]!);
            if(m.Name==nameof(ISide.ReadChangesAsync) && (string?)args[0]=="held") admitted.TrySetResult();
            if(m.Name==nameof(ISide.RecoverAsync) && args[1] is Entry e && (e==first || e==last)) return Task.FromResult<Recovery?>(new Recovery.File(e,new MemoryStream(e==first ? new byte[]{1} : new byte[]{2},false)));
            return Observation.Forward(db,m,args);
        });
        var logs=Observation.Wrap<ILogFiles>((m,_)=>m.Name==nameof(ILogFiles.Open)?TextWriter.Null:null);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(memory),(_,_,_,_)=>(local,cloud),ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try
        {
            var repair=host.RepairAsync(pair.PairId,ct);
            try
            {
                if(await Task.WhenAny(refused.Task,repair).WaitAsync(ct)==repair) {await repair;throw new InvalidDataException("Removal failed to retain the child for put-back");}
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw new InvalidDataException("Removal failed to retain the child for put-back");}
            Check.Require(await store.CursorAsync(pair.PairId,ct)=="before","Failed put-back released the child page");
            var upload=new AcceptedJob(pair.PairId,"files/upload_session/finish_batch/check","token",[new(last.Id,path,null)]);
            await store.SaveJobAsync(upload,ct); db.JobReplies[upload.JobId]=new([new JobReply.Running(),new JobReply.Terminal([(last,null,null)])]);
            await host.InstalledAsync(upload,ct); long jobReads=db.Requests[nameof(ISide.ReadJobAsync)];
            Notice? choice=null; await foreach(var n in host.NoticesAsync(pair.PairId,ct)) if(n.Kind==NoticeKind.PutBack && n.Cause==TextKey.FailLocalInUse) choice=n;
            Check.Require(jobReads==1 && choice?.Reviewed!=null,"Held receipt did not wake the installed token or expose the reviewed failure");
            pc.PublishFailures.Remove(path);
            await host.ResolveAsync(pair.PairId,choice!.Id,choice.Reviewed!,ResolutionChoice.Retry,null,ct);
            await repair.WaitAsync(ct);
            Check.Require(db.Requests[nameof(ISide.ReadJobAsync)]==jobReads+1 && independent==0,"Manual Retry skipped its token or independently dispatched a consumed child");
            Check.Require(await store.CursorAsync(pair.PairId,ct)=="after" && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack),"Recovery failed to settle its cursor or notice");
            Check.Require(pc.Items[path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[path].Bytes.SequenceEqual(new byte[]{2}),"Recovery lost the revision after the path-only tombstone");
        }
        catch(OperationCanceledException e) when(ct.IsCancellationRequested) {throw new InvalidDataException("Manual retry did not finish before its deadline",e);}
        finally {admitted.TrySetResult();timeout.Cancel();try {await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));} catch(TimeoutException e){throw new InvalidDataException("Manual retry host did not stop",e);}}
    }

    // Observe the synthetic public store/adapter boundaries; the Host creates and owns its real queue and checker.
    public class Observation : DispatchProxy
    {
        private Func<MethodInfo,object?[],object?> call=null!;
        internal static T Wrap<T>(Func<MethodInfo,object?[],object?> invoke) where T:class
        {var obj=Create<T,Observation>(); ((Observation)(object)obj).call=invoke; return obj;}
        internal static object? Forward(object target,MethodInfo m,object?[] args)
        {
            try {return m.Invoke(target,args);}
            catch(TargetInvocationException e) when(e.InnerException!=null) {ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
        }
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>call(method!,args??[]);
    }
}
