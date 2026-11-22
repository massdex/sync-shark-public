using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks that a collision retains its source while free siblings move.</summary>
public static class FolderMergeChecks
{
    public static async Task RunAsync()
    {
        var failures=new List<string>();foreach(var mode in new[]{0,2,3})try{await Verify(mode);}catch(Exception ex){failures.Add(ex.Message);}Check.Require(failures.Count==0,string.Join("; ",failures));
    }
    public static async Task NativeCollisionAsync(){await Verify(1);await Verify(4);}
    private static async Task Verify(int mode)
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var db=new FakeSide(mode is 0 or 4?Side.Dropbox:Side.Local); var pc=new FakeSide(mode is 0 or 4?Side.Local:Side.Dropbox); using var store=new InMemoryStateStore();
        var old=ItemChecks.File(Side.Dropbox,"old",0,"src",true); var dest=old with {Path="new",Id="dest"}; db.Seed(old,[]); db.Seed(dest,[]);
        foreach(var p in new[]{"old/a-taken","new/a-taken","old/z-free"}) db.Seed(ItemChecks.File(Side.Dropbox,p,1,p),[1]);
        var seenRace=false;
        var racing=FolderDeleteChecks.Observe.On(db,(op,args)=>{
            if(mode==1 && op==nameof(ISide.MoveAsync) && (string?)args[1]=="new/a-taken")throw new PathOccupiedException("a-taken");
            if(mode is 1 or 4 && op==nameof(ISide.ReadOneAsync) && (string?)args[0]=="new/a-taken" && !seenRace){seenRace=true;return Task.FromResult<Entry?>(null);}return null;});
        int collisions=0;
        RemovalResult? result=null;try{result=await new FolderMerge().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,old,dest,db.Side,mode is 0 or 4?pc:racing,mode is 0 or 4?racing:pc,store,_=>[],(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,collide:(selected,incumbent,peer,saved,ct)=>{Check.Require(selected.Id=="old/a-taken" && incumbent.Id=="new/a-taken","Wrong collision subjects");collisions++;if(mode==3)throw new ItemFailedException(selected.Path,TextKey.FailLocalInUse,null);return Task.FromResult(mode==2?EffectOutcome.Pending:EffectOutcome.Refused);});}catch(Exception e) when (e is PathOccupiedException or ItemFailedException){}
        Check.Require(collisions==1 && (result?.Outcome is RemovalOutcome.Retained or RemovalOutcome.Pending) && db.Items.ContainsKey("new/z-free") && db.Items["new/z-free"].Entry.Id=="old/z-free" && db.Items["old/a-taken"].Entry.Id=="old/a-taken" && db.Items["new/a-taken"].Entry.Id=="new/a-taken" && pc.Items.Count==0,mode switch {1=>"Native PC leaf collision bypassed collision owner",2=>"Pending child starved free sibling",3=>"Classified child failure starved free sibling",_=>"Merge lost a collision or free child"});
    }
}
