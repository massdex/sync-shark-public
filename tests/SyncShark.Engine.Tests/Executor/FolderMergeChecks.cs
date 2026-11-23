using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks that a collision retains its source while free siblings move.</summary>
public static class FolderMergeChecks
{
    public static async Task RunAsync()
    {
        await NestedCoverageAsync();
        var failures=new List<string>();foreach(var mode in new[]{0,2,3,5,7,8})try{await Verify(mode);}catch(Exception ex){failures.Add(ex.Message);}Check.Require(failures.Count==0,string.Join("; ",failures));
    }
    private static async Task NestedCoverageAsync()
    {
        var pair=ItemChecks.Pair();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var source=ItemChecks.File(Side.Local,"old",0,"source",true);var dest=source with {Path="new",Id="dest"};pc.Seed(source,[]);pc.Seed(dest,[]);
        foreach(var path in new[]{"old/sub","new/sub"}){var v=source with {Path=path,Id=path};pc.Seed(v,[]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(v,v with {Id="remote-"+v.Id}),null,null,default);}
        var child=ItemChecks.File(Side.Local,"old/free",1,"file");var peer=ItemChecks.File(Side.Dropbox,"old/free",1,"peer");
        pc.Seed(child,[1]);db.Seed(peer,[1]);var before=SyncedEntries.From(child,peer);await store.CommitAsync(pair.PairId,null,before,null,null,default);
        var admitted=FolderDeleteChecks.Admitted(pc,db);int fences=0;
        var result=await new FolderMerge().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),source,dest,Side.Local,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:f=>{fences++;return admitted(f);});
        Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("new/free"),default)==SyncedEntries.Relink(before,child with {Path="new/free"},peer) && await store.FindAsync(pair.PairId,Side.Local,before.LocalKey,default)==null,"Moved child binding did not follow its verified identity");
        Check.Require(result.Outcome==RemovalOutcome.Removed && fences==4 && !pc.Items.ContainsKey("old/sub") && !pc.Items.ContainsKey("old") && pc.Items["new/sub"].Entry.Id=="new/sub","Nested merge lost the caller's post-effect coverage for source cleanup");
    }
    public static async Task NativeCollisionAsync(){await Verify(1);await Verify(4);await Verify(6);}
    private static async Task Verify(int mode)
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var db=new FakeSide(mode is 0 or 4 or 6?Side.Dropbox:Side.Local); var pc=new FakeSide(mode is 0 or 4 or 6?Side.Local:Side.Dropbox); using var store=new InMemoryStateStore();
        var old=ItemChecks.File(Side.Dropbox,"old",0,"src",true); var dest=old with {Path="new",Id="dest"}; db.Seed(old,[]); db.Seed(dest,[]);
        foreach(var p in new[]{"old/a-taken","new/a-taken","old/z-free"}) db.Seed(ItemChecks.File(Side.Dropbox,p,1,p,mode==6 && p=="old/a-taken"),[1]);
        var seenRace=false;
        var racing=FolderDeleteChecks.Observe.On(db,(op,args)=>{
            if(mode==6 && op==nameof(ISide.MoveAsync) && (string?)args[1]=="new/a-taken")return Task.FromResult(new RemovalResult(RemovalOutcome.NoEffect,null,TextKey.FailDropboxTooManyFiles));
            if(mode==6 && op==nameof(ISide.EnsureFolderAsync) && (string?)args[0]=="new/a-taken")throw new PathOccupiedException("new/a-taken");
            if(mode==1 && op==nameof(ISide.MoveAsync) && (string?)args[1]=="new/a-taken")throw new PathOccupiedException("a-taken");
            if(mode is 1 or 4 or 6 && op==nameof(ISide.ReadOneAsync) && (string?)args[0]=="new/a-taken" && !seenRace){seenRace=true;return Task.FromResult<Entry?>(null);}return null;});
        var wait=new ItemFailedException("old/a-taken",TextKey.FailChanged,null){Disposition=FailureDisposition.TemporaryNoEffect,ProviderWait=TimeSpan.FromSeconds(7)};
        var peerSide=FolderDeleteChecks.Observe.On(pc,(op,args)=>mode==8 && op==nameof(ISide.ReadOneAsync) && (string?)args[0]=="new/a-taken"?throw new ChangedUnderneathException("new/a-taken"):null);
        int collisions=0;bool waited=false;
        RemovalResult? result=null;try{result=await new FolderMerge().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,old,dest,db.Side,mode is 0 or 4 or 6?peerSide:racing,mode is 0 or 4 or 6?racing:peerSide,store,_=>[],(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,collide:(selected,incumbent,peer,saved,ct)=>{Check.Require(selected.Id=="old/a-taken" && incumbent.Id=="new/a-taken","Wrong collision subjects");collisions++;if(mode==7)throw wait;if(mode==5)throw new ChangedUnderneathException(selected.Path){FailedSide=db.Side};if(mode==3)throw new ItemFailedException(selected.Path,TextKey.FailLocalInUse,null);return Task.FromResult(mode==2?EffectOutcome.Pending:EffectOutcome.Refused);});}catch(Exception e) when (e is PathOccupiedException or ItemFailedException){waited=ReferenceEquals(e,wait); }
        if(mode==7){Check.Require(waited && db.Items.ContainsKey("new/z-free") && db.Items.ContainsKey("old") && !store.OpenNotices.Any(),"Child provider wait lost before independent sibling completed");return;}
        if(mode==8){var notice=store.OpenNotices.Single();Check.Require(notice.Subject.Incoming==pc.Side && notice.Subject.Items.SequenceEqual(new[]{new NoticeItem(db.Side,"old/a-taken","old/a-taken")}) && db.Items.ContainsKey("new/z-free"),"Peer failure lost its actual side or selected child retry identity");return;}
        Check.Require(collisions==1 && (result?.Outcome is RemovalOutcome.Retained or RemovalOutcome.Pending) && db.Items.ContainsKey("new/z-free") && db.Items["new/z-free"].Entry.Id=="old/z-free" && db.Items["old/a-taken"].Entry.Id=="old/a-taken" && db.Items["new/a-taken"].Entry.Id=="new/a-taken" && pc.Items.Count==0,mode switch {1=>"Native PC leaf collision bypassed collision owner",2=>"Pending child starved free sibling",3=>"Classified child failure starved free sibling",5=>"Changed child starved free sibling",6=>"Size fallback lost final-name collision or free sibling",_=>"Merge lost a collision or free child"});
    }
}
