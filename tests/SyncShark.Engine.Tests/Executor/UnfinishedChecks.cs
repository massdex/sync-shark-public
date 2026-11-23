using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks cold-cache and pre-install path-only wakes.</summary>
public static class UnfinishedChecks
{
    public static async Task RunAsync()
    {
        var failures=new List<string>();
        foreach(var test in new Func<Task>[]{PublishedCommitAsync,UnboundMoveAsync,DeletedStageAsync,TerminalAsync,MixedAsync,PendingParentAsync,UnknownAsync,ThirdPathAsync,AbsentMoveAsync,MissingOriginAsync,PolicyAsync,PlainRecycleAsync,UnboundStageAsync,ComparedFolderAsync})try{await test();}catch(Exception ex){failures.Add(test.Method.Name+": "+ex.Message);}
        Check.Require(failures.Count==0,string.Join("; ",failures));
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        var owner=new Unfinished();var change=new Change(Side.Dropbox,ChangeKind.Deleted,"else",null,null,null,false,false){AffectedPath="new/child"};
        Task Visit(Change? c)=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),c,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        await Visit(null); // Boot can finish before either token exists.
        var job=new AcceptedJob(pair.PairId,"move","token",[new("id","old","new")]);var other=new AcceptedJob(pair.PairId,"move","other",[new("other-id","unrelated","elsewhere")]);
        foreach(var accepted in new[]{other,job}){await store.SaveJobAsync(accepted,default);db.JobReplies[accepted.JobId]=new([new JobReply.Running()]);}
        int wakes=0;Task Wake(Change c){Check.Require(c==change,"Changed held report");wakes++;return Visit(c);}
        await owner.InstalledAsync(other,_=>[change],Wake);
        await owner.InstalledAsync(job,_=>[change,change],Wake);
        await owner.InstalledAsync(job,_=>[change with {AffectedPath="newer/child"}],Wake);
        int pending=0;await foreach(var _ in store.JobsAsync(pair.PairId,default))pending++;
        Check.Require(wakes==1 && pending==2 && db.Requests[nameof(ISide.ReadJobAsync)]==1 && db.JobReplies[other.JobId].Count==1 && db.JobReplies[job.JobId].Count==0 && db.Mutations.Count==0,"Installed path-only wake did not check just its running token once");
    }

    public static async Task DeferredEnsureAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        var job=new AcceptedJob(pair.PairId,"files/create_folder_batch/check","create",[new(null,"wait",null),new(null,"good",null)]);await store.SaveJobAsync(job,default);var good=ItemChecks.File(Side.Dropbox,"good",0,"good-id",true);var occupant=good with {Path="wait",Id="held-id"};db.JobReplies[job.JobId]=new([new JobReply.Terminal([(null,TextKey.ClashNameTaken,FailureDisposition.Permanent),(null,TextKey.ClashNameTaken,FailureDisposition.Permanent)])]);
        int lookups=0,goodLookups=0;var clock=new TestDoubles.FakeTimeProvider(DateTimeOffset.UnixEpoch);var start=clock.GetTimestamp();var delay=TimeSpan.FromSeconds(7);
        var wait=new ItemFailedException("wait",TextKey.FailChanged,null){Disposition=FailureDisposition.TemporaryNoEffect,ProviderWait=delay,ProviderWaitStarted=start};
        var held=new TaskCompletionSource<JobReply.Terminal>(TaskCreationOptions.RunContinuationsAsynchronously);
        var side=FolderDeleteChecks.Observe.On(db,(op,args)=>{
            if(op!=nameof(ISide.ResolveCreateClashAsync))return null;
            var selected=(JobMember)args[0]!;
            if(selected==job.Launch[1]){goodLookups++;return Task.FromResult(new JobReply.Terminal([(good,null,null)]));}
            Check.Require(selected==job.Launch[0],"Deferred lookup changed ordinal");
            return ++lookups==1?held.Task:Task.FromResult(new JobReply.Terminal([(occupant,null,null)]));});
        var owner=new Unfinished();Task Visit()=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,side,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        var first=Visit();bool bootReturned=first.IsCompletedSuccessfully,peerStarted=goodLookups==1;
        if(!bootReturned){held.SetException(wait);try{await first;}catch(ItemFailedException error) when(ReferenceEquals(error,wait)){} }
        Check.Require(peerStarted,"Pending ensure blocked its independent neighbour");
        Check.Require(bootReturned,"Finite boot check waited for an outstanding ensure lookup");
        await Visit();Check.Require(!held.Task.IsCompleted && clock.GetTimestamp()==start && goodLookups==1 && lookups==1 && owner.CoversOpenJob(new("wait","wait")),"Another wake resent an in-flight ensure lookup or lost job custody");
        held.SetException(wait);bool original=false;try{await Visit();}catch(ItemFailedException error){original=ReferenceEquals(error,wait);}
        Check.Require(original && lookups==1,"Deferred ensure did not preserve the selected lookup wait");Check.Require(goodLookups==1,"Deferred ensure blocked a good neighbour before its wait expired");clock.Advance(delay);await Visit();
        Check.Require(goodLookups==1 && lookups==2 && db.Requests[nameof(ISide.ReadJobAsync)]==1 && db.Mutations.Count==0 && db.Requests[nameof(ISide.EnsureFolderAsync)]==0 && db.Requests[nameof(ISide.CreateFolderExclusiveAsync)]==0,"A settled ensure neighbour, mutation or job poll was replayed");await foreach(var _ in store.JobsAsync(pair.PairId,default))throw new InvalidDataException("Resolved ensure still owns its job");
    }
    private static async Task PublishedCommitAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);var old=ItemChecks.File(Side.Local,"p",1,"victim");var remote=ItemChecks.File(Side.Dropbox,"p",2,"peer");var saved=SyncedEntries.From(old,remote);await store.CommitAsync(pair.PairId,null,saved,null,null,default);pc.History.Add((old,[1]));db.Seed(remote,[2]);
        await using var stage=await pc.StageAsync("p",new MemoryStream([2]),remote,old,default);var note=new RemovalSelection(new(old,"volume",old.ContentHash),saved);await store.ArmAsync(pair.PairId,note,stage.Proof,default);var published=(await pc.PublishAsync(stage,null,null,default)).Final!;var final=SyncedEntries.From(published,remote);await store.CommitAsync(pair.PairId,saved,final,null,null,default);
        await new Unfinished().CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(await store.ArmedAsync(pair.PairId,default)==null && store.Records.Values.Single()==final && pc.Requests[nameof(ISide.PublishAsync)]==1,"Committed stage replay did not release its exact note without republishing");
    }
    private static async Task UnboundMoveAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);var arrive=ItemChecks.File(Side.Dropbox,"new",3,"id");db.Seed(arrive,[3]);var job=new AcceptedJob(pair.PairId,"files/move_batch","unbound",[new(arrive.Id,"old","new")]);await store.SaveJobAsync(job,default);db.JobReplies[job.JobId]=new([new JobReply.Terminal([(arrive,null,null)])]);
        await new Unfinished().CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        bool pending=false;await foreach(var _ in store.JobsAsync(pair.PairId,default))pending=true;Check.Require(pending || store.Records.Values.Any(x=>x.DropboxId==arrive.Id),"Unbound arrival retired without settlement");
    }
    private static async Task TerminalAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var db=new TestDoubles.FakeSide(Side.Dropbox);var pc=new TestDoubles.FakeSide(Side.Local);
        var root=ItemChecks.File(Side.Dropbox,"p",0,"selected",true);var job=new AcceptedJob(pair.PairId,"files/delete_batch/check","token",[new JobMember(root.Id,root.Path,null){Origin=ActionKind.DeleteDropbox}]);
        await store.SaveJobAsync(job,default);db.JobReplies[job.JobId]=new([new JobReply.Terminal([(root,null,null)])]);
        var cloud=FolderDeleteChecks.Observe.On(db,(op,_)=>op==nameof(ISide.BoundaryAsync)?Task.FromResult<(string?,JournalPosition?)>(("final",null)):null);
        var mark=new JournalPosition(1,42);pc.Pages.Enqueue(new([],null,mark));bool acquired=false,first=false,last=false;var owner=new Unfinished {AcquiredThrough=f=>acquired && f.Local==mark && f.Dropbox=="final"};
        var v=ItemChecks.File(Side.Dropbox,"p/late",2,"child");db.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.File(v,new MemoryStream([2]));
        var edit=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false){ConsumeHeld=()=>first=true};var tomb=new Change(Side.Dropbox,ChangeKind.Deleted,v.Path,null,null,null,false,false){ConsumeHeld=()=>last=true};
        Task Visit(Change c,Change[] held)=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),c,pc,cloud,store,_=>held,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        await Visit(edit,[edit]);Check.Require(!first && pc.Items.Count==0 && db.Items.Count==0,"Terminal published earlier edit before reading pending tombstone");
        acquired=true;await Visit(tomb,[edit,tomb]);await foreach(var _ in store.JobsAsync(pair.PairId,default))throw new InvalidOperationException("Completed terminal job remained persisted");Check.Require(first && last && pc.Items.Count==0 && db.Items.Count==0 && db.Requests[nameof(ISide.ReadJobAsync)]==1,"Last acquired tombstone failed to settle without publishing old bytes");
    }

    private static async Task MixedAsync()
    {
        var pair=FolderDeleteChecks.BoundPair(); using var store=new TestDoubles.InMemoryStateStore();var db=new TestDoubles.FakeSide(Side.Dropbox);var pc=new TestDoubles.FakeSide(Side.Local);
        var folder=ItemChecks.File(Side.Dropbox,"split",0,"selected",true);var child=ItemChecks.File(Side.Dropbox,"split/c",1,"child");db.Seed(folder,[]);db.Seed(child,[1]);
        var job=new AcceptedJob(pair.PairId,"files/delete_batch/check","mixed",[new JobMember("gone","absent",null){Origin=ActionKind.DeleteDropbox},new JobMember(folder.Id,folder.Path,null){Origin=ActionKind.DeleteDropbox},new JobMember("unknown","untouched",null){Origin=ActionKind.DeleteDropbox}]);
        await store.SaveJobAsync(job,default);db.JobReplies[job.JobId]=new([new JobReply.Terminal([(null,TextKey.FailChanged,FailureDisposition.Permanent),(null,TextKey.FailDropboxTooManyFiles,FailureDisposition.Permanent),(null,null,FailureDisposition.UnknownEffect)]){AbsentOrdinals=new HashSet<int>{0}}]);
        db.Seed(ItemChecks.File(Side.Dropbox,"untouched",8,"unknown"),[8]);var seen=new HashSet<string>();var mark=new JournalPosition(1,42);pc.Pages.Enqueue(new([],null,mark));var owner=new Unfinished{AcquiredThrough=f=>f.Local==mark && f.Dropbox!=null && seen.Contains(f.Dropbox)};
        var side=FolderDeleteChecks.Observe.On(FolderDeleteChecks.DeleteSide(db),(op,_)=>op==nameof(ISide.BoundaryAsync)?Task.FromResult<(string?,JournalPosition?)>((db.Mutations.Count==0?"initial":"split",null)):null);
        Task Visit()=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,side,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        await Visit();Check.Require(db.Mutations.Count==0,"Terminal size refusal split before acquisition");seen.Add("initial");await Visit();seen.Add("split");await Visit();await Visit();
        Check.Require(db.Mutations.SequenceEqual(new[]{child.Id,folder.Id}) && db.Items["untouched"].Bytes.SequenceEqual(new byte[]{8}) && db.Requests[nameof(ISide.ReadJobAsync)]==1,"Typed refusal not split exactly once or wrong ordinal mutated");
        int pending=0;await foreach(var _ in store.JobsAsync(pair.PairId,default))pending++;Check.Require(pending==1,"Uncertain live neighbour was retired");db.Items.Remove("untouched");await Visit();await foreach(var _ in store.JobsAsync(pair.PairId,default))throw new InvalidDataException("Settled neighbour ordinals remained in launch");
    }

    private static async Task PendingParentAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        var root=ItemChecks.File(Side.Dropbox,"p",0,"root",true);db.Seed(root,[]);var saved=SyncedEntries.From(root with {Id="local"},root);await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var member=new JobMember(root.Id,root.Path,null){Origin=ActionKind.DeleteDropbox};var first=new AcceptedJob(pair.PairId,"files/delete_batch/check","first",[member]);var last=first with {JobId="last"};
        await store.SaveJobAsync(first,default);db.JobReplies[first.JobId]=new([new JobReply.Terminal([(null,TextKey.FailDropboxTooManyFiles,FailureDisposition.Permanent)])]);
        db.JobReplies[last.JobId]=new([new JobReply.Running(),new JobReply.Terminal([(root,null,null)])]);int sends=0,compares=0;bool acquired=false,ack=false,effect=false;
        var mark=new JournalPosition(1,42);pc.Pages.Enqueue(new([],null,mark));var owner=new Unfinished{AcquiredThrough=f=>f.Local==mark && (f.Dropbox=="before" || acquired && f.Dropbox=="after")};
        async Task<RemovalResult> Send(){sends++;await store.SaveJobAsync(last,default);await owner.InstalledAsync(last,_=>[],_=>Task.CompletedTask);return new(RemovalOutcome.Pending,null,null){Accepted=last};}
        var cloud=FolderDeleteChecks.Observe.On(db,(op,_)=>op==nameof(ISide.DeleteAsync)?Send():op==nameof(ISide.BoundaryAsync)?Task.FromResult<(string?,JournalPosition?)>((effect?"after":"before",null)):null);
        var tomb=new Change(Side.Dropbox,ChangeKind.Deleted,"p/child",null,null,null,false,false){ConsumeHeld=()=>ack=true};
        Task Visit()=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,cloud,store,_=>[tomb],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>{compares++;return Task.CompletedTask;},default);
        await Visit();await Visit();effect=true;db.Items.Remove(root.Path);await Visit();
        Check.Require(sends==1 && !ack && compares==0,"Pending split settled against its pre-effect capture");acquired=true;await Visit();
        Check.Require(ack && compares==1 && sends==1,"Final accepted parent did not own settlement exactly once");
    }

    public static async Task LiveThirdPathAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        var a=ItemChecks.File(Side.Local,"old",3,"local");var b=ItemChecks.File(Side.Dropbox,"old",3,"remote");pc.Seed(a,[3]);db.Seed(b with {Path="third"},[3]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,b),null,null,default);
        var job=new AcceptedJob(pair.PairId,"files/move_batch_v2","token",[new(b.Id,b.Path,"obsolete")]);await store.SaveJobAsync(job,default);db.JobReplies[job.JobId]=new([new JobReply.Terminal([(null,null,FailureDisposition.UnknownEffect)])]);
        await new Unfinished{CoversThrough=FolderDeleteChecks.Admitted(pc,db)}.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(pc.Items.TryGetValue("third",out var arrived) && arrived.Entry.Id==a.Id && arrived.Bytes.SequenceEqual(new byte[]{3}) && !pc.Items.ContainsKey("old") && !db.Items.ContainsKey("obsolete") && store.Records.Values.Single().LocalPath=="third" && store.Records.Values.Single().DropboxPath=="third" && db.Mutations.Count==0 && db.Requests[nameof(ISide.ReadJobAsync)]==1,"Unknown third-path move did not settle through the current live counterpart");
    }
    private static Task ThirdPathAsync()=>MoveObservation("third");
    private static Task AbsentMoveAsync()=>MoveObservation(null);
    private static Task UnknownAsync()=>MoveObservation("new");
    private static async Task MoveObservation(string? destination)
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);var selected=ItemChecks.File(Side.Dropbox,"old",1,"id");var saved=SyncedEntries.From(selected with {Id="pc"},selected);await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var job=new AcceptedJob(pair.PairId,"files/move_batch","token",[new(selected.Id,"old","new")]);await store.SaveJobAsync(job,default);db.JobReplies[job.JobId]=new([new JobReply.Terminal([(null,null,FailureDisposition.UnknownEffect)])]);db.Seed(selected, [1]);var owner=new Unfinished();int compares=0,oldCompares=0;
        Task Visit()=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,after,ct)=>{if(after.DropboxPath==destination)compares++;else oldCompares++;return Task.CompletedTask;},default);
        await Visit();db.Items.Clear();if(destination!=null)db.Seed(selected with {Path=destination},[1]);await Visit();int pending=0;await foreach(var j in store.JobsAsync(pair.PairId,default)) pending++;
        var exact=store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure && n.Subject.Move is {ExpectedId:"id",Source:"old",Destination:"new"});
        if(destination==null){Check.Require(oldCompares==0 && pending==0 && exact && db.Mutations.Count==0,"Unknown absence retired as an arrival");return;}
        if(destination=="third"){Check.Require(pending==0 && (compares!=0 || exact) && db.Mutations.Count==0,"Third-path move ignored by ordinary decision");return;}
        Check.Require(compares==1 && pending==0 && db.Requests[nameof(ISide.ReadJobAsync)]==1 && db.Requests[nameof(ISide.ReadByIdAsync)]>=2 && db.Mutations.Count==0,"Unknown move was not reobserved to its exact destination");
    }
    private static Task DeletedStageAsync()=>StaleStage(true);
    public static Task StaleStageAsync()=>StaleStage(false);
    private static async Task StaleStage(bool deleted)
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);var old=ItemChecks.File(Side.Local,"f",1,"old");var remote=ItemChecks.File(Side.Dropbox,"f",2,"remote");var saved=SyncedEntries.From(old,remote with {ContentHash=old.ContentHash});await store.CommitAsync(pair.PairId,null,saved,null,null,default);pc.History.Add((old,[1]));if(!deleted) db.Seed(remote with {ContentHash=old.ContentHash},[1]);
        await using var staged=await pc.StageAsync(old.Path,new MemoryStream([2]),remote,old,default);await store.ArmAsync(pair.PairId,new(new(old,"volume",old.ContentHash),saved),staged.Proof,default);
        await new Unfinished().CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require((deleted?!pc.Items.ContainsKey("f"):pc.Items.TryGetValue("f",out var final) && final.Bytes.SequenceEqual(new byte[]{1}) && store.Records.Values.SingleOrDefault()?.LocalId==final.Entry.Id && store.Records.Values.Single().DropboxContentHash==old.ContentHash) && await store.ArmedAsync(pair.PairId,default)==null,deleted?"Deleted source resurrected from obsolete stage or note left armed":"Changed cloud source did not retire obsolete stage for redecision");
        bool discarded=false;try{await using var residual=await pc.ReopenStageAsync(new(new(old,"volume",old.ContentHash),saved),staged.Proof!,default);}catch(ChangedUnderneathException){discarded=true;}
        Check.Require(discarded,"Obsolete stage remained after note settlement");
    }

    private static async Task ComparedFolderAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var local=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        var folder=ItemChecks.File(Side.Local,"p",0,"old",true);var saved=SyncedEntries.From(folder,folder with {Id="peer"});var selection=new RemovalSelection(new(folder,"volume",null),saved);
        local.History.Add((folder,[]));await store.CommitAsync(pair.PairId,null,saved,null,null,default);await store.ArmAsync(pair.PairId,selection,null,default);await store.CommitAsync(pair.PairId,saved,null,null,null,default);
        var full=FolderDeleteChecks.Admitted(local,db);bool acquired=false;var owner=new Unfinished{CoversThrough=f=>acquired && full(f)};Task Visit()=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,local,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>throw new InvalidDataException("Recompared an already settled folder"),default);
        await Visit();Check.Require(await store.ArmedAsync(pair.PairId,default)!=null,"Restart discharged unread child input");acquired=true;await Visit();Check.Require(await store.ArmedAsync(pair.PairId,default)==null && local.History.Count==1 && store.Records.Count==0,"Restart stranded the note after the selected folder comparison committed");
    }
    private static Task PlainRecycleAsync()=>ReplayNote(false);
    private static Task UnboundStageAsync()=>ReplayNote(true);
    private static async Task ReplayNote(bool staged)
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);var old=ItemChecks.File(Side.Local,"f",1,"old");var remote=ItemChecks.File(Side.Dropbox,"f",2,"remote");var saved=staged?null:SyncedEntries.From(old,remote);if(saved!=null) await store.CommitAsync(pair.PairId,null,saved,null,null,default);pc.History.Add((old,[1]));
        var victim=new RemovalSelection(new(old,"volume",old.ContentHash),saved);await using var stage=staged?await pc.StageAsync(old.Path,new MemoryStream([2]),remote,old,default):null;
        if(staged)db.Seed(remote,[2]);await store.ArmAsync(pair.PairId,victim,stage?.Proof,default);
        await new Unfinished().CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(await store.ArmedAsync(pair.PairId,default)==null && (staged?store.Records.Values.Single().LocalId==pc.Items[old.Path].Entry.Id:store.Records.Count==0 && pc.Items.Count==0),"Plain recycle or unbound publication remained armed without its exact outcome");
    }

    private static async Task MissingOriginAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var local=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        var old=ItemChecks.File(Side.Dropbox,"p",0,"selected",true);var child=ItemChecks.File(Side.Dropbox,"p/new",3,"child");db.Seed(old,[]);db.Seed(child,[3]);local.Seed(ItemChecks.File(Side.Local,"p",4,"replacement"),[4]);
        var job=new AcceptedJob(pair.PairId,"files/delete_batch","before-restart",[new(old.Id,old.Path,null)]);await store.SaveJobAsync(job,default);db.JobReplies[job.JobId]=new([new JobReply.Terminal([(null,TextKey.FailDropboxTooManyFiles,FailureDisposition.Permanent)])]);
        local.Pages.Enqueue(new([],null,new(1,42)));db.Pages.Enqueue(new([],"end",null));await new Unfinished{AcquiredThrough=f=>f.Local!=null && f.Dropbox=="end"}.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,local,FolderDeleteChecks.DeleteSide(db),store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        int remaining=0;await foreach(var _ in store.JobsAsync(pair.PairId,default))remaining++;
        Check.Require(remaining==1 && db.Mutations.Count==0 && db.Items[child.Path].Bytes.SequenceEqual(new byte[]{3}) && local.Items[old.Path].Bytes.SequenceEqual(new byte[]{4}),"Delete wire route authorized splitting a replacement after restart");
    }

    private static async Task PolicyAsync()
    {
        foreach(var origin in new[]{ActionKind.ReplaceKind,ActionKind.MoveDropbox})
        {
            var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var local=new TestDoubles.FakeSide(Side.Local);var cloud=new TestDoubles.FakeSide(Side.Dropbox);
            var old=ItemChecks.File(Side.Dropbox,"parent",0,"original",true);cloud.Seed(old,[]);var child=ItemChecks.File(Side.Dropbox,"parent/child",3,"child");cloud.Seed(child,[3]);
            var job=new AcceptedJob(pair.PairId,"files/delete_batch","accepted",[new JobMember(old.Id,old.Path,null){Origin=origin}]);await store.SaveJobAsync(job,default);
            cloud.JobReplies[job.JobId]=new([new JobReply.Terminal([(null,TextKey.FailDropboxTooManyFiles,FailureDisposition.Permanent)])]);
            local.Pages.Enqueue(new([],null,new(1,42)));cloud.Pages.Enqueue(new([],"end",null));await new Unfinished{AcquiredThrough=f=>f.Local!=null && f.Dropbox=="end"}.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,local,FolderDeleteChecks.DeleteSide(cloud),store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
            Check.Require(cloud.Mutations.Count==0 && cloud.Items[child.Path].Bytes.SequenceEqual(new byte[]{3}),"Restored policy was replaced with wire-delete splitting");
        }
    }
}
