using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks cold-cache and pre-install path-only wakes.</summary>
public static class UnfinishedChecks
{
    public static async Task RunAsync()
    {
        var failures=new List<string>();
        foreach(var test in new Func<Task>[]{TerminalAsync,MixedAsync})try{await test();}catch(Exception ex){failures.Add(test.Method.Name+": "+ex.Message);}
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
        var owner=new Unfinished(); await owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        int remaining=0;await foreach(var _ in store.JobsAsync(pair.PairId,default))remaining++;
        Check.Require(remaining==1,"Terminal delete discarded its launch before the later page was covered");
        var late=ItemChecks.File(Side.Dropbox,"p/late",2,"child");db.Recoverable[(late.Id,late.Revision,late.ContentHash)]=new Recovery.File(late,new MemoryStream([2]));
        var change=new Change(Side.Dropbox,ChangeKind.Changed,late.Path,null,late.Id,late,false,false);
        await owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),change,pc,db,store,_=>[change],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(db.Requests[nameof(ISide.ReadJobAsync)]==1 && pc.Items[late.Path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[late.Path].Bytes.SequenceEqual(new byte[]{2}),"Late edit after terminal was not restored without polling again");
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
}
