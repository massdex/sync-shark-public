using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks cold-cache and pre-install path-only wakes.</summary>
public static class UnfinishedChecks
{
    public static async Task RunAsync()
    {
        await TerminalAsync();
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
        await new Unfinished().CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        int remaining=0;await foreach(var _ in store.JobsAsync(pair.PairId,default))remaining++;
        Check.Require(remaining==1,"Terminal delete discarded its launch before the later page was covered");
    }
}
