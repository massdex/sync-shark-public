using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks that a collision retains its source while free siblings move.</summary>
public static class FolderMergeChecks
{
    public static async Task RunAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var db=new FakeSide(Side.Dropbox); var pc=new FakeSide(Side.Local); using var store=new InMemoryStateStore();
        var old=ItemChecks.File(Side.Dropbox,"old",0,"src",true); var dest=old with {Path="new",Id="dest"}; db.Seed(old,[]); db.Seed(dest,[]);
        foreach(var p in new[]{"old/taken","new/taken","old/free"}) db.Seed(ItemChecks.File(Side.Dropbox,p,1,p),[1]);
        var seenRace=false;
        var racing=FolderDeleteChecks.Observe.On(db,(op,args)=>{
            if(op==nameof(ISide.ReadOneAsync) && (string?)args[0]=="new/taken" && !seenRace){seenRace=true;return Task.FromResult<Entry?>(null);}return null;});
        var result=await new FolderMerge().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,old,dest,Side.Dropbox,pc,racing,store,_=>[],(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(result.Outcome==RemovalOutcome.Retained && db.Items["new/free"].Entry.Id=="old/free" && db.Items["old/taken"].Entry.Id=="old/taken" && db.Items["new/taken"].Entry.Id=="new/taken" && pc.Items.Count==0,"Merge lost a collision, left a free child behind, or used the wrong side");
    }
}
