using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks that a collision retains its source while free siblings move.</summary>
public static class FolderMergeChecks
{
    public static async Task RunAsync()
    {
        await MergeReplayChecks.RunAsync();
        await NestedCoverageAsync();
        await RetainedKeptFollowAsync();
        await UnreadableVetoAsync();
        var failures=new List<string>();foreach(var mode in new[]{0,2,3,5,7,8})try{await Verify(mode);}catch(Exception ex){failures.Add(ex.Message);}Check.Require(failures.Count==0,string.Join("; ",failures));
    }
    public static async Task MergeChildRepathsDescendantsAsync()
    {
        var pair=ItemChecks.Pair();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
        using var store=new InMemoryStateStore();
        var source=ItemChecks.File(Side.Local,"m",0,"source",true);var dest=source with {Path="d",Id="dest"};pc.Seed(source,[]);pc.Seed(dest,[]);
        pc.Seed(source with {Path="m/s",Id="link",Skip=nameof(TextKey.SkipLink)},[]);
        foreach(var child in new[]{source with {Path="m/c",Id="c"},ItemChecks.File(Side.Local,"m/c/g",1,"g")})
        {
            var peer=child with {Id="peer-"+child.Id};pc.Seed(child,[1]);db.Seed(peer,[1]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(child,peer),null,null,default);
        }
        var result=await new FolderMerge().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),source,dest,Side.Local,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Task<SyncedEntry?> Find(string path)=>store.FindAsync(pair.PairId,Side.Local,PathNames.Key(path),default);
        Check.Require(result.Outcome==RemovalOutcome.Retained && await Find("d/c")!=null && await Find("d/c/g") is {LocalPath:"d/c/g",DropboxPath:"m/c/g"} && await Find("m/c/g")==null,"Retained sibling left moved descendants at their old path");
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
    // A retained kept child whose user-side peer already moved with the rename is settled on the moved side only; the
    // receiving endpoint stays, an ordinary sibling relinks, and the moved name stays covered by the kept row's leg (K01).
    public static Task RetainedKeptFollowAsync() => Storage.StoreChecks.WithImage(async (store,_) =>
    {
        var pair=ItemChecks.Pair() with { Exclusions=[new Exclusion(Side.Dropbox,"P/sub/k",true)] };
        var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);await store.SavePairAsync(pair,default);
        var pFolder=ItemChecks.File(Side.Dropbox,"P",0,"P",true);var qFolder=ItemChecks.File(Side.Dropbox,"Q",0,"Q",true);
        db.Seed(pFolder,[]);db.Seed(qFolder,[]);db.Seed(pFolder with {Path="P/sub",Id="sub"},[]);
        // Saved at P on both sides; the user has since renamed PC P->Q, so the actual peer items are at Q with the same local ids.
        var keptSaved=SyncedEntries.From(ItemChecks.File(Side.Local,"P/sub/k",1,"Lk"),ItemChecks.File(Side.Dropbox,"P/sub/k",1,"Dk"));
        var freeSaved=SyncedEntries.From(ItemChecks.File(Side.Local,"P/sub/free",2,"Lf"),ItemChecks.File(Side.Dropbox,"P/sub/free",2,"Df"));
        await store.CommitAsync(pair.PairId,null,keptSaved,null,null,default);
        await store.CommitAsync(pair.PairId,null,freeSaved,null,null,default);
        pair=await store.EnrollKeepsAsync(pair,default);paths=new(pair,[pair],DriveType.Fixed);
        store.PairChanged=view=>paths=new(view,[view],DriveType.Fixed);
        db.Seed(ItemChecks.File(Side.Dropbox,"P/sub/k",1,"Dk"),[1]);db.Seed(ItemChecks.File(Side.Dropbox,"P/sub/free",2,"Df"),[2]);
        pc.Seed(ItemChecks.File(Side.Local,"Q/sub/k",1,"Lk"),[1]);pc.Seed(ItemChecks.File(Side.Local,"Q/sub/free",2,"Lf"),[2]);
        var result=await new FolderMerge().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,pFolder,qFolder,Side.Dropbox,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Task<SyncedEntry?> Find(string path)=>store.FindAsync(pair.PairId,Side.Local,PathNames.Key(path),default);
        Check.Require(await Find("Q/sub/k") is {} kb && kb.DropboxPath=="P/sub/k" && kb.LocalId=="Lk" && kb.DropboxId=="Dk",
            "A retained kept child's moved peer was not settled to its new name (K01)");
        Check.Require(await Find("P/sub/k")==null,"The retained kept child was not moved off its old local name");
        Check.Require(await Find("Q/sub/free") is {} fb && fb.DropboxPath=="Q/sub/free","An ordinary sibling did not relink");
        Check.Require(db.Items.ContainsKey("P/sub/k"),"The retained receiving kept endpoint was removed");
        Check.Require(result.Outcome==RemovalOutcome.Retained,"A retained merge did not report retention");
        Check.Require(paths.Excludes("Q/sub/k",null,false) && !paths.Excludes("Q/sub/free","Q/sub/free",false),"Retained merge did not publish the kept peer independently of the sibling");
        var edit=ItemChecks.File(Side.Local,"Q/sub/k",3,"Lk");pc.Items[edit.Path]=(edit,[3]);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new(Side.Local,ChangeKind.Changed,edit.Path,null,edit.Id,edit,true,false),pc,db,store,null!,null!,default);
        Check.Require(db.Items["P/sub/k"].Bytes.SequenceEqual(new byte[]{1}) && !db.Items.ContainsKey("Q/sub/k"),"Retained merge uploaded a later edit");
    });
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
    // A PC read notice vetoes a merge child only while its failed PC name, under the child's PC scope, may still be unreadable.
    // A notice found through a member under the source or destination scope whose failed name lies outside vetoes without any PC read.
    private static async Task UnreadableVetoAsync()
    {
        foreach(var arm in new[]{"stale","persistent","source-member","destination-member"})
        {
            var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
            var old=ItemChecks.File(Side.Dropbox,"old",0,"src",true);var dest=old with {Path="new",Id="dest"};var child=ItemChecks.File(Side.Dropbox,"old/c",1,"child");
            db.Seed(old,[]);db.Seed(dest,[]);db.Seed(child,[1]);
            var member=arm switch {"source-member"=>"old/c","destination-member"=>"new/c",_=>null};
            var notice=member==null
                ? new Notice(0,NoticeKind.Failure,TextKey.ReadLocalFailed,"old/c",new([new(Side.Local,"old/c","pc-c")],Side.Local),DateTimeOffset.UnixEpoch,false)
                : new Notice(0,NoticeKind.Failure,TextKey.ReadLocalFailed,"Z/d",new([new(Side.Dropbox,member,child.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false);
            await store.SaveNoticeAsync(pair.PairId,notice,default);
            if(arm=="persistent"){pc.Seed(ItemChecks.File(Side.Local,"old/c",1,"pc-c"),[1]);pc.ContentFailures["old/c"]=new ItemFailedException("old/c",TextKey.ReadLocalFailed,null);}
            if(member!=null) pc.Seed(ItemChecks.File(Side.Local,"Z/d",0,"z",true),[]); // present and listable, yet never read
            var result=await new FolderMerge().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,old,dest,Side.Dropbox,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
            bool moved=db.Items.TryGetValue("new/c",out var landed) && landed.Entry.Id==child.Id;
            if(arm=="stale") Check.Require(moved && pc.Requests[nameof(ISide.ContentHashAsync)]==0,"An old read notice over an absent PC name still vetoed the merge child");
            else Check.Require(!moved && db.Items.ContainsKey("old/c") && result.Outcome==RemovalOutcome.Retained && (arm=="persistent" || pc.Requests[nameof(ISide.ReadOneAsync)]==0 && pc.Requests[nameof(ISide.ContentHashAsync)]==0),
                $"A merge child moved over a still-unreadable or outside PC name ({arm})");
        }
    }
}
