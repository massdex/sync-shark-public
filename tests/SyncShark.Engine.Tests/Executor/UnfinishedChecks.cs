using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks cold-cache and pre-install path-only wakes.</summary>
public static class UnfinishedChecks
{
    public static async Task RunAsync()
    {
        await RecoveryChecks.A45PathOnlyWakeUnboundMemberAsync();
        var owner=new Unfinished(); var change=new Change(Side.Dropbox,ChangeKind.Deleted,"new/child",null,null,null,false,false);
        var job=new AcceptedJob("pair","move","token",[new("id","old","new")]); int wakes=0;
        await owner.InstalledAsync(job,_=>[change,change],seen=>{Check.Require(seen==change,"Changed held report");wakes++;return Task.CompletedTask;});
        await owner.InstalledAsync(job,_=>[change with {Path="newer/child"}],_=>{wakes++;return Task.CompletedTask;});
        Check.Require(wakes==1,"Installation failed to coalesce a matching held report or matched an unrelated prefix");
    }
}
