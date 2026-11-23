using System.Reflection;
using System.Runtime.ExceptionServices;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static class AutoSyncChecks
{
    public static async Task SourceRetryAsync()
    {
        var waiting=new TaskCompletionSource<RunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct=timeout.Token;
        int attempts=0; var next=new ChangePage([],"next",null);
        async Task<ChangePage> Read(CancellationToken t)
        {
            if(++attempts==1) throw new ItemFailedException("item",TextKey.ReadDropboxFailed,null){Disposition=FailureDisposition.TemporaryNoEffect,ProviderWait=TimeSpan.Zero};
            await resume.Task.WaitAsync(t); return next;
        }
        var retry=AutoSync.ReadPageAsync(Read,Side.Dropbox,ItemChecks.Settings,e=>{if(e.Kind==RunEventKind.Wait)waiting.TrySetResult(e);},ct);
        var wait=await waiting.Task.WaitAsync(ct);
        Check.Require(wait.Cause==TextKey.WaitSourceRetry && wait.Side==Side.Dropbox && wait.Detail==Text.Get(TextKey.WaitSourceRetry,(TextArg.Seconds,"0")),"Source wait lost its typed cause or scheduled duration");
        var other=await AutoSync.ReadPageAsync(_=>Task.FromResult(new ChangePage([],null,new(1,7))),Side.Local,ItemChecks.Settings,_=>{},ct);
        Check.Require(other.Position==new JournalPosition(1,7) && !retry.IsCompleted,"Failed source cancelled independent acquisition");
        resume.SetResult(); Check.Require(await retry==next && attempts==2,"Failed source did not retry its original read");
    }

    public static async Task ChangedItemReplayAsync()
    {
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(30));var ct=budget.Token;
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
    }

    public static async Task FiniteAttemptAsync()
    {
        var local=new TaskCompletionSource(); var cloud=new TaskCompletionSource(); var held=new TaskCompletionSource();
        int attempts=0; var entered=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish=AutoSync.FinishIntakeAsync(Task.WhenAll(local.Task,cloud.Task),()=>Interlocked.Increment(ref attempts),()=>{entered.SetResult(attempts);return held.Task;});
        local.SetResult(); Check.Require(attempts==0 && !entered.Task.IsCompleted,"Attempt reported before both finite reads");
        cloud.SetResult(); int observed=await entered.Task; bool waiting=!finish.IsCompleted;
        held.SetResult(); await finish;
        Check.Require(observed==1 && waiting && attempts==1,"Held input postponed the attempt signal until settlement");
    }

    public static async Task BlockedFinitePageAsync()
    {
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct=budget.Token;
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
    }

    public static async Task ResetBehindHeldPageAsync()
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
        var end=new JournalPosition(3,7); pc.Pages.Enqueue(new([],null,end));
        var allowLocal=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetSaved=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int count=0;
        var observed=Observation.Wrap<IStore>((m,args)=>m.Name==nameof(IStore.ResetCursorAsync) ? Reset((string)args[1]!, (string)args[2]!, (CancellationToken)args[3]!) : Observation.Forward(store,m,args));
        async Task Reset(string fresh,string reason,CancellationToken t){await store.ResetCursorAsync(pair.PairId,fresh,reason,t);resetSaved.TrySetResult();}
        var local=Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.ReadChangesAsync) && args.Length==4 ? FinishLocal((CancellationToken)args[3]!) : Observation.Forward(pc,m,args));
        async Task<ChangePage> FinishLocal(CancellationToken t){await allowLocal.Task.WaitAsync(t);return await pc.ReadChangesAsync(null,null,(null,end),t);}
        var intake=AutoSync.ReadThroughAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,db,observed,(end,"end"),()=>{Interlocked.Increment(ref count);signal.TrySetResult();},ct);
        try
        {
            if(await Task.WhenAny(resetSaved.Task,intake).WaitAsync(ct)==intake) {await intake; throw new InvalidDataException("Feed returned before publishing reset");}
            Check.Require(count==0 && (await store.ReadRepairAsync(pair.PairId,Side.Dropbox,ct)).Reason!=null && await store.CursorAsync(pair.PairId,ct)=="fresh" && queue.Held(new("held","held")).Any(c=>c.Observed==edit),"Reset lost its held edit, omitted its debt, or reported an unfinished source as attempted");
            allowLocal.SetResult();
            if(await Task.WhenAny(signal.Task,intake).WaitAsync(ct)==intake) {await intake;throw new InvalidDataException("Finite reset skipped its actual attempt signal");}
            Check.Require(count==1 && pc.Pages.Count==0 && !intake.IsCompleted,"Finite reset waited for settlement or abandoned the other source");
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw new InvalidDataException("Held page prevented publication of verified reset and Repair debt");}
        finally {allowLocal.TrySetResult(); release.TrySetResult();}
        await Task.WhenAll(blocker,intake).WaitAsync(ct);
        Check.Require(pc.Items["held"].Bytes.SequenceEqual(new byte[]{2}) && await store.CursorAsync(pair.PairId,ct)=="fresh","The prior page lost its edit or rewound the reset when it settled");
    }

    public static async Task RunAsync()
    {
        await FiniteAttemptAsync(); await SourceRetryAsync(); await BlockedFinitePageAsync(); await ResetBehindHeldPageAsync();
        await ManualRetryAsync();
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
        pc.Pages.Enqueue(new([new(Side.Local,ChangeKind.Deleted,root,null,old.Id,null,false,true,1,JournalId:1)],null,new(1,2)));
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
        var local=Observation.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.ReadByIdAsync) && (string?)args[0]==old.Id ? MissingAfterAdmission() :
            m.Name==nameof(ISide.SettledJournalAsync) ? store.PositionAsync("1",ct) : Observation.Forward(pc,m,args));
        async Task<(IdentityLocation Location,bool InScope)?> MissingAfterAdmission(){await admitted.Task.WaitAsync(ct); return await pc.ReadByIdAsync(old.Id,ct);}
        var cloud=Observation.Wrap<ISide>((m,args)=>{
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
        finally {admitted.TrySetResult();timeout.Cancel();await host.StopAsync(CancellationToken.None);}
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
