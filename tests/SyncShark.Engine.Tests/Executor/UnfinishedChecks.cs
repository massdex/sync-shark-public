using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks cold-cache and pre-install path-only wakes.</summary>
public static class UnfinishedChecks
{
    public static async Task RunAsync()
    {
        var failures=new List<string>();
        foreach(var test in new Func<Task>[]{TerminalAsync,MixedAsync,UnknownAsync,StaleStageAsync,MissingOriginAsync,PlainRecycleAsync,UnboundStageAsync})try{await test();}catch(Exception ex){failures.Add(test.Method.Name+": "+ex.Message);}
        Check.Require(failures.Count==0,string.Join("; ",failures));
        await RecoveryChecks.A45PathOnlyWakeUnboundMemberAsync();
        var owner=new Unfinished(); var change=new Change(Side.Dropbox,ChangeKind.Deleted,"new/child",null,null,null,false,false);
        var job=new AcceptedJob("pair","move","token",[new("id","old","new")]); int wakes=0;
        await owner.InstalledAsync(job,_=>[change,change],seen=>{Check.Require(seen==change,"Changed held report");wakes++;return Task.CompletedTask;});
        await owner.InstalledAsync(job,_=>[change with {Path="newer/child"}],_=>{wakes++;return Task.CompletedTask;});
        Check.Require(wakes==1,"Installation failed to coalesce a matching held report or matched an unrelated prefix");
    }

    private static async Task TerminalAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var db=new TestDoubles.FakeSide(Side.Dropbox);var pc=new TestDoubles.FakeSide(Side.Local);
        var root=ItemChecks.File(Side.Dropbox,"p",0,"selected",true);var job=new AcceptedJob(pair.PairId,"files/delete_batch/check","token",[new(root.Id,root.Path,null)]);
        await store.SaveJobAsync(job,default);db.JobReplies[job.JobId]=new([new JobReply.Terminal([(root,null,null)])]);
        var cloud=FolderDeleteChecks.Observe.On(db,(op,_)=>op==nameof(ISide.BoundaryAsync)?Task.FromResult<(string?,JournalPosition?)>(("final",null)):null);
        bool acquired=false,first=false,last=false;var owner=new Unfinished {AcquiredThrough=cursor=>acquired && cursor=="final"};
        var v=ItemChecks.File(Side.Dropbox,"p/late",2,"child");db.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.File(v,new MemoryStream([2]));
        var edit=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false){ConsumeHeld=()=>first=true};var tomb=new Change(Side.Dropbox,ChangeKind.Deleted,v.Path,null,null,null,false,false){ConsumeHeld=()=>last=true};
        Task Visit(Change c,Change[] held)=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),c,pc,cloud,store,_=>held,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        await Visit(edit,[edit]);Check.Require(!first && pc.Items.Count==0 && db.Items.Count==0,"Terminal published earlier edit before reading pending tombstone");
        acquired=true;await Visit(tomb,[edit,tomb]);Check.Require(first && last && pc.Items.Count==0 && db.Items.Count==0 && db.Requests[nameof(ISide.ReadJobAsync)]==1,"Last acquired tombstone failed to settle without publishing old bytes");
    }

    private static async Task MixedAsync()
    {
        var pair=FolderDeleteChecks.BoundPair(); using var store=new TestDoubles.InMemoryStateStore();var db=new TestDoubles.FakeSide(Side.Dropbox);var pc=new TestDoubles.FakeSide(Side.Local);
        var folder=ItemChecks.File(Side.Dropbox,"split",0,"selected",true);var child=ItemChecks.File(Side.Dropbox,"split/c",1,"child");db.Seed(folder,[]);db.Seed(child,[1]);
        var job=new AcceptedJob(pair.PairId,"files/delete_batch/check","mixed",[new("gone","absent",null),new(folder.Id,folder.Path,null),new("unknown","untouched",null)]);
        await store.SaveJobAsync(job,default);db.JobReplies[job.JobId]=new([new JobReply.Terminal([(null,TextKey.FailChanged,FailureDisposition.Permanent),(null,TextKey.FailDropboxTooManyFiles,FailureDisposition.Permanent),(null,null,FailureDisposition.UnknownEffect)]){AbsentOrdinals=new HashSet<int>{0}}]);
        await new Unfinished().CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(db.Mutations.SequenceEqual(new[]{child.Id,folder.Id}) && db.Requests[nameof(ISide.ReadJobAsync)]==1,"Typed refusal not split exactly once or wrong ordinal mutated");
    }

    private static async Task UnknownAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);var selected=ItemChecks.File(Side.Dropbox,"old",1,"id");var saved=SyncedEntries.From(selected with {Id="pc"},selected);await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var job=new AcceptedJob(pair.PairId,"files/move_batch","token",[new(selected.Id,"old","new")]);await store.SaveJobAsync(job,default);db.JobReplies[job.JobId]=new([new JobReply.Terminal([(null,null,FailureDisposition.UnknownEffect)])]);db.Seed(selected, [1]);var owner=new Unfinished();int compares=0;
        Task Visit()=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,after,ct)=>{if(after.DropboxPath=="new")compares++;return Task.CompletedTask;},default);
        await Visit();db.Items.Clear();db.Seed(selected with {Path="new"},[1]);await Visit();int pending=0;await foreach(var j in store.JobsAsync(pair.PairId,default)) pending++;
        Check.Require(compares==1 && pending==0 && db.Requests[nameof(ISide.ReadJobAsync)]==1 && db.Requests[nameof(ISide.ReadByIdAsync)]>=2 && db.Mutations.Count==0,"Unknown move was not reobserved to its exact destination");
    }
    private static async Task StaleStageAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);var old=ItemChecks.File(Side.Local,"f",1,"old");var remote=ItemChecks.File(Side.Dropbox,"f",2,"remote");var saved=SyncedEntries.From(old,remote);await store.CommitAsync(pair.PairId,null,saved,null,null,default);pc.History.Add((old,[1]));db.Seed(remote with {ContentHash=old.ContentHash},[1]);
        await using var staged=await pc.StageAsync(old.Path,new MemoryStream([2]),remote,old,default);await store.ArmAsync(pair.PairId,new(new(old,"volume",old.ContentHash),saved),staged.Proof,default);
        await new Unfinished().CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(!pc.Items.ContainsKey("f") && await store.ArmedAsync(pair.PairId,default)!=null,"Stale stage published before examining changed counterpart");
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
        await new Unfinished().CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,local,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        int remaining=0;await foreach(var _ in store.JobsAsync(pair.PairId,default))remaining++;
        Check.Require(remaining==1 && db.Mutations.Count==0 && db.Items[child.Path].Bytes.SequenceEqual(new byte[]{3}) && local.Items[old.Path].Bytes.SequenceEqual(new byte[]{4}),"Delete wire route authorized splitting a replacement after restart");
    }
}
