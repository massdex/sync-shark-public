using Massdex.SyncShark.Engine.Executor;
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
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,ct);
        var before=ItemChecks.File(Side.Local,"held",1,"local"); var old=ItemChecks.File(Side.Dropbox,"held",1,"remote");
        var edit=ItemChecks.File(Side.Dropbox,"held",2,old.Id); pc.Seed(before,[1]); db.Seed(edit,[2]);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(before,old),null,null,ct); await store.SaveCursorAsync(pair.PairId,"initial",ct);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var change=new Change(Side.Dropbox,ChangeKind.Changed,"held",null,edit.Id,edit,false,false);
        var blocker=queue.AdmitAsync(pair,new([change],null,null),(_,t)=>release.Task.WaitAsync(t),null,ct);
        db.Pages.Enqueue(new([change],"obsolete",null)); db.Pages.Enqueue(new([],"fresh",null){ContinuityLost=true});
        var intake=AutoSync.ReadThroughAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,(null,"end"),()=>{},ct);
        try
        {
            while((await store.ReadRepairAsync(pair.PairId,Side.Dropbox,ct)).Reason==null)
            {
                if(intake.IsCompleted) {await intake; throw new InvalidDataException("Feed returned without recording verified reset");}
                await Task.Delay(10,ct);
            }
            Check.Require(await store.CursorAsync(pair.PairId,ct)=="fresh" && queue.Held(new("held","held")).Any(c=>c.Observed==edit),"Reset lost the retained edit or failed to publish its fresh cursor");
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw new InvalidDataException("Held page prevented publication of verified reset and Repair debt");}
        finally {release.TrySetResult();}
        await Task.WhenAll(blocker,intake).WaitAsync(ct);
        Check.Require(pc.Items["held"].Bytes.SequenceEqual(new byte[]{2}) && await store.CursorAsync(pair.PairId,ct)=="fresh","The prior page lost its edit or rewound the reset when it settled");
    }

    public static async Task RunAsync()
    {
        await FiniteAttemptAsync(); await SourceRetryAsync(); await BlockedFinitePageAsync(); await ResetBehindHeldPageAsync();
        const string root="parent", path="parent/item";
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct=timeout.Token;
        var pair=ItemChecks.Pair(); pair=pair with {Roots=new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("a","r","h","d"))};
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var paths=new PathScope(pair,[pair],DriveType.Fixed); var queue=new WorkQueue();
        await store.SavePairAsync(pair,ct); await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,ct);
        var old=ItemChecks.File(Side.Local,root,0,"pc-parent",true); var peer=ItemChecks.File(Side.Dropbox,root,0,"remote-parent",true);
        db.Seed(peer,[]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,ct);
        var first=ItemChecks.File(Side.Dropbox,path,1,"child"); var last=ItemChecks.File(Side.Dropbox,path,2,"child"); db.Seed(last,[2]);
        foreach(var item in new[]{first,last}) db.Recoverable[(item.Id,item.Revision,item.ContentHash)]=new Recovery.File(item,new MemoryStream(new byte[]{item==first?(byte)1:(byte)2}));
        pc.PublishFailures[path]=new ItemFailedException(path,TextKey.FailLocalInUse,null);
        Change Report(Entry v)=>new(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prerequisite=queue.AdmitAsync(pair,new([new(Side.Local,ChangeKind.Changed,root,null,old.Id,old,false,false)],null,null),(_,token)=>release.Task.WaitAsync(token),null,ct);
        var removal=Apply(Side.Local,new([new(Side.Local,ChangeKind.Deleted,root,null,old.Id,null,false,true,1,JournalId:1)],null,new(1,2)));
        var children=Apply(Side.Dropbox,new([Report(first),new(Side.Dropbox,ChangeKind.Deleted,path,null,null,null,false,true),Report(last)],"after",null));
        release.SetResult(); await prerequisite;
        Notice? pending=null;
        while(pending==null)
        {
            if(removal.IsFaulted) await removal;
            if(children.IsCompleted) {await children;throw new InvalidDataException("Children escaped the removal before its put-back retry");}
            await foreach(var notice in store.NoticesAtAsync(pair.PairId,PathNames.Key(path),ct)) if(notice.Kind==NoticeKind.PutBack) pending=notice;
            if(pending==null) await Task.Delay(10,ct);
        }
        Check.Require(await store.CursorAsync(pair.PairId,ct)==null && queue.Held(new(root,root)).Any(c=>c.Observed==last),"Failed recovery lost the exact final version or advanced the cursor");
        pc.PublishFailures.Remove(path);
        await new Retries().RetryAsync(pair,ItemChecks.Settings,_=>{},paths,pending,pc,db,store,queue.Held,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),ct);
        await Task.WhenAll(removal,children).WaitAsync(ct);
        Check.Require(await store.CursorAsync(pair.PairId,ct)=="after" && !queue.Held(new(root,root)).Any() && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack),"Recovery left held work or failed to settle its cursor");
        Check.Require(pc.Items[path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[path].Bytes.SequenceEqual(new byte[]{2}),"Recovery lost the revision after the path-only tombstone");
        Task Apply(Side side,ChangePage page)=>AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,side,page,pc,db,store,ct);
    }
}
