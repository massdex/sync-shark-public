using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Check bytes, binding and the distinction between a pending, refused and completed action.</summary>
public static class EffectsChecks
{
    public static Task MoveWithEditAsync() => RepairMove("renamed",2,"item",1,null);
    public static Task MoveOppositeEditAsync() => RepairMove("renamed",1,"item",2,TextKey.InfoMovedEdited);
    public static Task MoveTwiceAsync() => RepairMove("pc-place",1,"cloud-place",1,TextKey.InfoMovedTwice);
    public static Task MoveOppositeDeleteAsync() => RepairMove("renamed",1,null,0,TextKey.InfoMovedDeleted);
    public static Task DifferentRenamesAsync() => RepairMove("first",1,"second",1,TextKey.InfoMovedTwice);

    private static Task RepairMove(string pcPath,byte pcValue,string? dbPath,byte dbValue,TextKey? cause) =>
        global::Massdex.SyncShark.Engine.Tests.Run.RepairChecks.WithRepairStore(async (store,pair,ct)=>
        {
            var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
            var oldPc=ItemChecks.File(Side.Local,"item",1,"pc"); var oldDb=ItemChecks.File(Side.Dropbox,"item",1,"cloud");
            pc.Seed(ItemChecks.File(Side.Local,pcPath,pcValue,oldPc.Id),[pcValue]);
            if(dbPath!=null) db.Seed(ItemChecks.File(Side.Dropbox,dbPath,dbValue,oldDb.Id),[dbValue]);
            await store.FinishRepairAsync(pair.PairId,(0,await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",ct)),"{}",ct);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(oldPc,oldDb),null,null,ct);
            var paths=new PathScope(pair,[pair],DriveType.Fixed);
            var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,db,store).WaitAsync(ct);
            await global::Massdex.SyncShark.Engine.Run.Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct).WaitAsync(ct);
            bool Has(FakeSide leg,string path,byte value) => leg.Items.TryGetValue(path,out var item) && item.Entry.Path==path && item.Bytes.SequenceEqual(new[]{value});
            if(cause==null) Check.Require(Has(db,pcPath,pcValue) && db.Items[pcPath].Entry.Id==oldDb.Id && db.History.Any(h=>h.Entry.Revision==oldDb.Revision && h.Bytes.SequenceEqual(new byte[]{1})),"Moved edit did not replace the old Dropbox revision at its new path");
            var expected=new Dictionary<string,byte>{{pcPath,pcValue}};
            if(cause!=null && dbPath!=null) expected.Add(dbPath,dbValue);
            foreach(var (path,value) in expected)
            {
                Check.Require(Has(pc,path,value) && Has(db,path,value),"A surviving move version is missing from its peer");
                Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(path),ct)==SyncedEntries.From(pc.Items[path].Entry,db.Items[path].Entry),"Move did not bind the final peers");
            }
            Check.Require(pc.Items.Count==expected.Count && db.Items.Count==expected.Count && pc.Items[pcPath].Entry.Id==oldPc.Id && (cause==null || dbPath==null || db.Items[dbPath].Entry.Id==oldDb.Id),"Move changed an original identity or left an extra path");
            var notices=new List<TextKey>(); await foreach(var n in store.NoticesAsync(pair.PairId,ct)) if(!n.Dismissed) notices.Add(n.Cause);
            Check.Require(cause==null ? notices.Count==0 : notices.SequenceEqual(new[]{cause.Value}),"Move did not settle the expected notice");
        });

    public static async Task FolderLeavesScopeAsync()
    {
        var pair=ItemChecks.Pair() with {Exclusions=[new(Side.Local,"ex",true)]}; var paths=new PathScope(pair,[pair],DriveType.Fixed);
        using var store=new InMemoryStateStore(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        var left=ItemChecks.File(Side.Local,"folder",0,"left",true); var right=ItemChecks.File(Side.Dropbox,left.Path,0,"right",true);
        var moved=left with {Path="ex/folder"}; var saved=SyncedEntries.From(left,right); pc.Seed(moved,[]); db.Seed(right,[]);
        pc.Seed(ItemChecks.File(Side.Local,"ex/folder/child",1,"child"),[1]); db.Seed(ItemChecks.File(Side.Dropbox,"folder/child",1,"peer-child"),[1]);
        await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var action=await MoveDecision.DecideMoveAsync(pair,ItemChecks.Settings,paths,[new(Side.Local,moved,saved)],pc,db,store,false,default);
        Check.Require(action.Kind==ActionKind.DeferFolder && action.Row.Local==null && action.Row.Dropbox==right,"Folder leaving scope did not select the peer for folder removal");
    }

    public static async Task ExcludedCloudMoveAsync()
    {
        await CheckMove("ex");
        await CheckMove("other-parent");
    }

    private static async Task CheckMove(string cloudParent)
    {
        var pair=ItemChecks.Pair() with {Exclusions=[new(Side.Local,"ex",true)]}; var paths=new PathScope(pair,[pair],DriveType.Fixed);
        using var store=new InMemoryStateStore(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        var left=ItemChecks.File(Side.Local,"file",1,"left"); var right=ItemChecks.File(Side.Dropbox,"file",1,"right");
        var saved=SyncedEntries.From(left,right); var a=left with {Path="in/file"}; var b=right with {Path=cloudParent+"/file"}; pc.Seed(a,[1]); db.Seed(b,[1]);
        var parent=SyncedEntries.From(ItemChecks.File(Side.Local,"ex",0,"parent-local",true),ItemChecks.File(Side.Dropbox,cloudParent,0,"parent-cloud",true));
        await store.CommitAsync(pair.PairId,null,parent,null,null,default);
        await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        await RowExecution.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,null,[new(Side.Local,a,saved),new(Side.Dropbox,b,saved)],pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(db.Items[a.Path].Bytes.SequenceEqual(new byte[]{1}) && db.Items[b.Path].Entry==b && db.Items[b.Path].Bytes.SequenceEqual(new byte[]{1}) && !pc.Items.ContainsKey(b.Path) &&
            store.Records.Count==2 && store.Records.Values.Contains(parent) && store.Records.Values.Contains(SyncedEntries.From(a,db.Items[a.Path].Entry)) && store.OpenNotices.Single().Cause==TextKey.InfoMovedDeleted,"Excluded cloud leg was copied or the surviving local move did not settle");
    }

    public static async Task IgnoredBackupAsync()
    {
        var pair=ItemChecks.Pair() with {IgnoreRules=["FileExtension:.tmp"]}; var paths=new PathScope(pair,[pair],DriveType.Fixed);
        using var store=new InMemoryStateStore(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        var left=ItemChecks.File(Side.Local,"file",1,"left"); var right=ItemChecks.File(Side.Dropbox,left.Path,1,"right"); var saved=SyncedEntries.From(left,right);
        var backup=left with {Path="file.tmp"}; var replacement=ItemChecks.File(Side.Local,left.Path,2,"replacement"); pc.Seed(backup,[1]); pc.Seed(replacement,[2]); db.Seed(right,[1]);
        await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        Task Apply()=>RowExecution.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,null,[new(Side.Local,backup,saved)],pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Tie,TextKey.TieEqualTimes,left.Path,new([new(Side.Local,left.Path,left.Id),new(Side.Dropbox,right.Path,right.Id)],null),DateTimeOffset.UnixEpoch,false),default);
        var tie=store.OpenNotices.Single();
        await Apply();
        Check.Require(db.Mutations.Count==0 && db.Items[right.Path].Entry==right && db.Items[right.Path].Bytes.SequenceEqual(new byte[]{1}) && pc.Items[left.Path].Entry==replacement && pc.Items[left.Path].Bytes.SequenceEqual(new byte[]{2}) && pc.Items[backup.Path].Entry==backup && store.Records.Values.Single()==saved && store.OpenNotices.Single()==tie,"Held safe-save changed a version, binding or notice");
        await store.CloseNoticeAsync(pair.PairId,tie,default);
        await Apply();
        Check.Require(db.Requests[nameof(ISide.DeleteAsync)]==0 && db.Items[right.Path].Entry.Id==right.Id && db.Items[right.Path].Bytes.SequenceEqual(new byte[]{2}) &&
            !db.Items.ContainsKey(backup.Path) && pc.Items[backup.Path].Entry==backup && store.Records.Values.Single()==SyncedEntries.From(replacement,db.Items[right.Path].Entry),"Safe-save replaced the cloud identity or synced the backup");
    }

    public static async Task SkippedMoveCollisionAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed); using var store=new InMemoryStateStore();
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var left=ItemChecks.File(Side.Local,"old",1,"left"); var right=ItemChecks.File(Side.Dropbox,left.Path,1,"right");
        var moved=left with {Path="new"}; var occupant=ItemChecks.File(Side.Dropbox,moved.Path,2,"occupant") with {Skip=nameof(TextKey.SkipReservedName)};
        pc.Seed(moved,[1]); db.Seed(right,[1]); var saved=SyncedEntries.From(left,right); await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var racing=FolderDeleteChecks.Observe.On(db,(operation,args)=>
        {
            if(operation==nameof(ISide.MoveAsync) && (string?)args[1]==moved.Path) db.Seed(occupant,[2]);
            return null;
        });
        try { await RowExecution.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,null,[new(Side.Local,moved,saved)],pc,racing,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default); }
        catch(ItemFailedException e) when(e.Cause==TextKey.ClashNameTaken && e.Path==moved.Path) { }
        Check.Require(store.OpenNotices.Single() is {Kind:NoticeKind.Failure,Cause:TextKey.ClashNameTaken} n && n.Path==moved.Path && db.Items[right.Path].Entry==right && db.Items[moved.Path].Entry==occupant && store.Records.Values.Single()==saved,"Late skipped occupant did not preserve both files and record the collision");
    }

    public static async Task MergeRootFailureAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed); using var store=new InMemoryStateStore();
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var left=ItemChecks.File(Side.Local,"old",0,"left",true); var right=ItemChecks.File(Side.Dropbox,left.Path,0,"right",true);
        var moved=left with {Path="new"}; var target=right with {Path=moved.Path,Id="incumbent"}; pc.Seed(moved,[]); db.Seed(right,[]); db.Seed(target,[]);
        var saved=SyncedEntries.From(left,right); await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        pc.Pages.Enqueue(new([],null,new(1,2))); db.Pages.Enqueue(new([],"after",null));
        int compared=0;
        Task Compare(SyncedEntry? binding, Scope scope, Scope? next, CancellationToken ct)
        {
            if(next!=null) return Task.CompletedTask;
            Check.Require(binding==saved && scope==new Scope(moved.Path,target.Path) && store.Records.Values.Single()==saved && db.Items.ContainsKey(right.Path),"Root compare lost the original source or binding");
            compared++; throw new ItemFailedException(scope.DropboxPath+"/child",TextKey.ReadDropboxFailed,null);
        }
        try { await RowExecution.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,null,[new(Side.Local,moved,saved)],pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),Compare,default,coversThrough:f=>f.Local==new JournalPosition(1,2) && f.Dropbox=="after"); }
        catch(ItemFailedException e) when(e.Cause==TextKey.ReadDropboxFailed) { }
        Check.Require(compared==1 && store.OpenNotices.Single() is {Kind:NoticeKind.Failure,Cause:TextKey.ReadDropboxFailed} n && n.Path==target.Path,"Failed comparison of meeting folders did not leave a retryable root failure");
    }

    public static async Task RunAsync()
    {
        await MoveWithEditAsync(); await MoveOppositeEditAsync(); await MoveTwiceAsync(); await MoveOppositeDeleteAsync(); await DifferentRenamesAsync();
        var pc = new FakeSide(Side.Local); var db = new FakeSide(Side.Dropbox);
        using var store = new InMemoryStateStore(); var pair = ItemChecks.Pair();
        var a = ItemChecks.File(Side.Local,"item",7,"pc"); pc.Seed(a,[7]);
        var row = new Row(a.Path,a,null,null,[],a.ContentHash,RowContext.Neutral);
        var effects = new Effects();
        Task<EffectOutcome> Apply(SyncAction action) => effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),action,pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(await Apply(new(ActionKind.None,row,NoneReason.Unfinished)) == EffectOutcome.Pending && store.Records.Count == 0,"Unfinished input must not settle");
        // Directory observations contain no digest; the bytes changed without changing the observed stamp.
        var metadata=a with {Path="upload",ContentHash=string.Empty}; pc.Seed(metadata,[10]);
        var receiver=new CollisionEndpoint(db); var before=ItemChecks.File(Side.Dropbox,metadata.Path,1,"remote"); db.Seed(before,[1]);
        var selectedUpload=new SyncAction(ActionKind.Upload,row with {Key=metadata.Path,Local=metadata,Dropbox=before});
        bool rejected=false;
        try { await effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),selectedUpload,pc,receiver,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default); }
        catch(ChangedUnderneathException e) when(e.Path==metadata.Path) {rejected=true;}
        Check.Require(rejected && receiver.StagedDigest==a.ContentHash && db.Items[before.Path].Entry==before,"Upload staged bytes without the selected PC digest");
        db.Items.Remove(before.Path);
        Notice? delivered=null; int calls=0; bool alreadySaved=false;
        var debt=IssueSubject.FirstRunDeletionKind.UnscannedSubtree;
        var planned=new SyncAction(ActionKind.Notice,row with {Context=row.Context with {FirstRunDeletion=debt}},Side:Side.Local,NoticeKind:NoticeKind.Failure,Cause:TextKey.FailChanged);
        var refused=await effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),planned,pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,failedNotice:n=>{calls++;delivered=n;alreadySaved=store.OpenNotices.Count==1;});
        var savedNotice=store.OpenNotices.Single();
        Check.Require(refused==EffectOutcome.Refused && calls==1 && alreadySaved && delivered!=null && savedNotice.Matches(delivered with {Id=savedNotice.Id}) && delivered.Subject.FirstRunDeletion==debt && delivered.Subject.Items.Single()==new NoticeItem(Side.Local,a.Path,a.Id) && store.Records.Count==0,"Planned failure did not deliver the saved envelope before refusal");
        Check.Require(await Apply(new(ActionKind.Upload,row)) == EffectOutcome.Applied && db.Items[a.Path].Bytes.SequenceEqual(new byte[]{7}) && store.Records.Values.Single() == SyncedEntries.From(a,db.Items[a.Path].Entry),"Upload must publish before settlement");
        Check.Require(await Apply(new(ActionKind.Upload,row))==EffectOutcome.Applied && db.Items.Count==1 && db.History.Count==0,"Create-only collision with identical bytes must adopt, not overwrite");
        // Only the source metadata re-read fails; opening its bytes and staging them on Dropbox must succeed.
        var source=ItemChecks.File(Side.Local,"p",5,"pc-source"); var victim=ItemChecks.File(Side.Dropbox,"p",0,"db-folder",true);
        pc.Seed(source,[5]); db.Seed(victim,[]);
        var bare=new ChangedUnderneathException("p"); pc.ReadFailures.Add("p",bare);
        var replace=new SyncAction(ActionKind.ReplaceKind,new("p",source,victim,null,[],source.ContentHash,RowContext.Neutral),Side:Side.Dropbox);
        var stages=db.Requests[nameof(ISide.StageAsync)];
        try { await Apply(replace); throw new InvalidDataException("Source re-read failure was ignored"); }
        catch(ChangedUnderneathException ex)
        {
            Check.Require(ex.Path==source.Path && ex.FailedSide==Side.Local,"PC source re-read during Dropbox folder replacement lost its endpoint");
        }
        Check.Require(db.Requests[nameof(ISide.StageAsync)]==stages+1 && db.Items[victim.Path].Entry==victim && db.Requests[nameof(ISide.DeleteAsync)]==0,"Source re-read refusal changed the selected folder or did not reach staging");
        pc.ReadFailures.Remove("p");
        int recoveries=0;
        db.AfterDelete=entry=>{if(entry==victim){recoveries++;db.Seed(victim with {Id="recovered-folder"},[]);}};
        await effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),replace,pc,FolderDeleteChecks.DeleteSide(db),store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],FolderDeleteChecks.Admitted(pc,db));
        Check.Require(recoveries==1,"The recovered-folder window was never reached");
        Check.Require(db.Items["p"].Entry.Id=="recovered-folder" && pc.Items["p"].Bytes.SequenceEqual(new byte[]{5}) && store.OpenNotices.Any(n=>n.Cause==TextKey.InfoKeptBoth),"The competing folder did not take keep-both");
        db.AfterDelete=null;
        // Identical relative path, opposite selection: the actual reader, not a fixed PC tag, wins.
        var remoteSource=source with {Id="db-source",Revision="r5"}; var pcFolder=victim with {Id="pc-folder"};
        var otherPc=new FakeSide(Side.Local); var otherDb=new FakeSide(Side.Dropbox);
        otherPc.Seed(pcFolder,[]); otherDb.Seed(remoteSource,[5]);
        otherDb.ReadFailures["p"]=new ChangedUnderneathException("p");
        try
        {
            await effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),new(ActionKind.ReplaceKind,new("p",pcFolder,remoteSource,null,[],null,RowContext.Neutral),Side:Side.Local),otherPc,otherDb,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
            throw new InvalidDataException("Dropbox source re-read failure was ignored");
        }
        catch(ChangedUnderneathException ex) { Check.Require(ex.Path==remoteSource.Path && ex.FailedSide==Side.Dropbox,"Dropbox source re-read was attributed to the PC"); }
        Check.Require(otherPc.Requests[nameof(ISide.StageAsync)]==1 && otherPc.Items["p"].Entry==pcFolder && otherPc.Requests[nameof(ISide.DeleteAsync)]==0,"Dropbox failure removed the selected PC folder");
        var folder = ItemChecks.File(Side.Dropbox,"other",0,"cloud-folder",true); db.Seed(folder,[]);
        var peer = ItemChecks.File(Side.Local,"OTHER",0,"pc-folder",true); pc.Seed(peer,[]);
        var old = SyncedEntries.From(peer with {Path="other"},folder);
        await store.CommitAsync(pair.PairId,null,old,null,null,default);
        db.MoveOutcomes.Enqueue(new(RemovalOutcome.Unknown,null,TextKey.FailChanged));
        var move = new SyncAction(ActionKind.MoveDropbox,new(old.LocalKey,peer,folder,old,[],null,RowContext.Neutral),Target:peer.Path);
        try { await Apply(move); throw new InvalidDataException("Unknown move lost the selected identity and goal"); }
        catch (ItemFailedException ex) when (ex.Disposition==FailureDisposition.UnknownEffect && ex.Move?.ExpectedId==folder.Id && ex.Move.Destination==peer.Path && ex.Cause==TextKey.FailChanged) { }
        Check.Require(store.Records[(pair.PairId,old.LocalKey)]==old && db.Items[folder.Path].Entry==folder,"Unconfirmed move projected the old binding");
        db.MoveOutcomes.Enqueue(new(RemovalOutcome.NoEffect,null,TextKey.FailChanged));
        try { await Apply(move); throw new InvalidDataException("Definite move refusal was accepted"); }
        catch (ItemFailedException ex) { Check.Require(ex.Cause==TextKey.FailChanged && ex.Disposition==FailureDisposition.Permanent && ex.Move==null,"Definite Dropbox refusal was stored as an uncertain move"); }
        var unreadable = peer with {Skip=nameof(TextKey.SkipUnreadable)};
        pc.Items[peer.Path] = (unreadable,[]);
        try
        {
            await MoveDecision.DecideMoveAsync(pair,ItemChecks.Settings,new(pair,[pair],DriveType.Fixed),[new(Side.Local,peer,old)],pc,db,store,false,default);
            throw new InvalidDataException("Unreadable move observation became an ordinary hold");
        }
        catch (ItemFailedException ex) when (ex.Path==peer.Path && ex.Cause==TextKey.ReadLocalFailed) { }
        // A bin-only file is not a live conflict row. BC5 must restore/checkpoint it or retain its PutBack notice.
        var held=ItemChecks.File(Side.Local,"lost",11,"binned"); pc.History.Add((held,[11]));
        var recovered=await effects.RecoverConflictAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),new("lost","lost"),Side.Local,held,pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(recovered==EffectOutcome.Pending && !pc.Items.ContainsKey("lost") && !db.Items.ContainsKey("lost"),"Ordinary conflict independently staged a bin-only last copy");
        var noFence=ItemChecks.File(Side.Local,"no-fence",7,"victim"); pc.Seed(noFence,[7]);
        var missing=new SyncAction(ActionKind.DeleteLocal,new(noFence.Path,noFence,null,null,[],noFence.ContentHash,RowContext.Neutral));
        var pending=await Apply(missing);
        Check.Require(pending==EffectOutcome.Pending && pc.Items[noFence.Path].Entry==noFence,"Missing acquisition ownership allowed a PC recycle");
        var click=await effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),missing,pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,userSelected:true);
        Check.Require(click==EffectOutcome.Applied && !pc.Items.ContainsKey(noFence.Path) && pc.History.Any(x=>x.Entry==noFence && x.Bytes.SequenceEqual(new byte[]{7})),"Reviewed file removal waited for a reader or lost its recycled version");
        // The fake checks its full record at delete; its mutation log distinguishes reaching delete from refusing the metadata-only by-ID read.
        pc.Seed(noFence with {ContentHash=string.Empty},[7]); var beforeDelete=pc.Mutations.Count;
        try { await effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),missing with {Row=missing.Row with {LocalHash=noFence.ContentHash}},pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,userSelected:true); }
        catch(ChangedUnderneathException e) when(e.Path==noFence.Path) { }
        Check.Require(pc.Mutations.Count==beforeDelete+1 && pc.Mutations[^1]==noFence.Id,"Metadata-only identity read rejected the selected hashed edition before conditional recycle");
        pc.Items.Remove(noFence.Path);
        pc.Seed(noFence,[7]);
        pc.Pages.Enqueue(new([],null,new(1,2))); db.Pages.Enqueue(new([],"after",null));
        bool queried=false;
        pending=await effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),missing,pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,_=>{queried=true;return [];},_=>false);
        Check.Require(pending==EffectOutcome.Pending && !queried && !pc.Items.ContainsKey(noFence.Path),"An uncovered post-recycle snapshot counted as acquired input");
    }

    /// <summary>Replay a changed file under relocated parents, preserving the independent occupant of the old name.</summary>
    public static async Task ChangedChildTargetAsync()
    {
        var pair=ItemChecks.Pair(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var oldPc=ItemChecks.File(Side.Local,"before/child",1,"pc"); var oldDb=ItemChecks.File(Side.Dropbox,"prior/child",1,"cloud");
        var saved=SyncedEntries.From(oldPc,oldDb); await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var edited=ItemChecks.File(Side.Local,"after/child",9,oldPc.Id); var receiving=oldDb with {Path="final/child"};
        var successor=ItemChecks.File(Side.Dropbox,oldDb.Path,5,"successor"); pc.Seed(edited,[9]); db.Seed(receiving,[1]); db.Seed(successor,[5]);
        var row=new Row(saved.LocalKey,edited,receiving,saved,[],edited.ContentHash,RowContext.Neutral with {CompleteInventory=true});
        const string wrong="Changed-child replay used the old saved path instead of the verified receiving endpoint";
        var paths=new PathScope(pair,[pair],DriveType.Fixed); bool held=false;
        try { await RowExecution.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,row with {Context=RowContext.Neutral},[],pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,failed:(_,_)=>held=true); }
        catch(InventoryIncompleteException) when(held) { }
        Check.Require(held && store.Records.Values.Single()==saved && db.Items[receiving.Path].Bytes.SequenceEqual(new byte[]{1}),"Unsettled child was published before its parent projection");
        var parent=SyncedEntries.From(ItemChecks.File(Side.Local,"after",0,"pc-parent",true),ItemChecks.File(Side.Dropbox,"final",0,"db-parent",true));
        await store.CommitAsync(pair.PairId,null,parent,null,null,default);
        await RowExecution.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,row,[],pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(db.Items.TryGetValue(receiving.Path,out var current) && current.Entry.Id==receiving.Id && current.Bytes.SequenceEqual(new byte[]{9}) &&
            db.Items.TryGetValue(oldDb.Path,out var kept) && kept.Entry==successor && kept.Bytes.SequenceEqual(new byte[]{5}) &&
            !db.Mutations.Contains(successor.Id) && store.Records.Count==2 && store.Records.Values.Contains(parent) && store.Records.Values.Contains(SyncedEntries.From(edited,current.Entry)),wrong);
    }

    public static async Task ForeignDestinationAsync()
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore(); var pair=ItemChecks.Pair();
        var folder=ItemChecks.File(Side.Dropbox,"source",0,"moving",true); db.Seed(folder,[]);
        var peer=ItemChecks.File(Side.Local,"vacant",0,"peer",true);pc.Seed(peer,[]);
        var old=SyncedEntries.From(peer with {Path=folder.Path},folder);await store.CommitAsync(pair.PairId,null,old,null,null,default);
        var move=new SyncAction(ActionKind.MoveDropbox,new(old.LocalKey,peer,folder,old,[],null,RowContext.Neutral),Target:peer.Path);
        Task<EffectOutcome> Apply(SyncAction action)=>new Effects().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),action,pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        var child=ItemChecks.File(Side.Dropbox,"source/child",3,"child"); db.Seed(child,[3]);
        var partner=ItemChecks.File(Side.Local,"elsewhere",0,"partner",true); pc.Seed(partner,[]);
        var late=ItemChecks.File(Side.Dropbox,peer.Path,0,"late",true); var bound=SyncedEntries.From(partner,late);
        bool childMoved=false; bool refused=false;
        var arriving=new CollisionEndpoint(db){AtRead=path=>
        {
            if(path==late.Path && !db.Items.ContainsKey(path)) {db.Seed(late,[]);store.Records[(pair.PairId,bound.LocalKey)]=bound;}
        },AtMove=(expected,_)=>{if(expected.Path==child.Path) childMoved=true;}};
        try {await new Effects().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),move,pc,arriving,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],_=>true);}
        catch(ChangedUnderneathException e) when(e.Path==late.Path) {refused=true;}
        catch(Exception) when(childMoved) { }
        Check.Require(!childMoved && refused && db.Mutations.Count==0 && pc.Mutations.Count==0 && db.Items[child.Path].Entry==child && store.Records.Values.Contains(bound),"Late bound folder entered child merge without its partner grant");
        db.Items.Remove(child.Path);db.Items.Remove(late.Path);pc.Items.Remove(partner.Path);store.Records.Remove((pair.PairId,bound.LocalKey));
        var stale=ItemChecks.File(Side.Dropbox,"vacant",0,"absent-folder",true);
        var former=SyncedEntries.From(stale with {Path="another",Id="owner"},stale);
        await store.CommitAsync(pair.PairId,null,former,null,null,default);
        var mutations=db.Mutations.Count;
        try { Check.Require(await Apply(move with {Target=stale.Path})==EffectOutcome.Pending,"Vacating a foreign key must require fresh comparison"); }
        finally { Check.Require(!db.Items.ContainsKey(stale.Path) && db.Mutations.Count==mutations,"Folder move wrote the vacant physical target before its saved owner was decided"); }
        Check.Require(!store.Records.Values.Contains(former),"Planner did not vacate the proven-gone owner before retry");
        foreach(var written in new[]{Side.Dropbox,Side.Local})
        foreach(var scenario in new[]{"refused","edited","receiver-edit","edited-equal","equal","different","uncovered","cancelled","late","rebound","new-owner"})
        {
            var dir=Directory.CreateTempSubdirectory();
            try
            {
                using var links=await Massdex.SyncShark.Engine.Storage.Store.OpenAsync(dir.FullName,true,ItemChecks.Settings,default) ?? throw new InvalidDataException();
                await links.SavePairAsync(pair,default);
                var left=new FakeSide(Side.Local);var right=new FakeSide(Side.Dropbox);
                left.Pages.Enqueue(new([],null,new(1,2)));right.Pages.Enqueue(new([],"after",null));
                byte occupantByte=scenario is "different" or "edited" or "receiver-edit"?(byte)8:scenario=="edited-equal"?(byte)9:(byte)7;
                var incoming=PathNames.Other(written); var to=written==Side.Local?left:right; var from=written==Side.Local?right:left;
                var a=ItemChecks.File(written,"old",7,"arriving-selected");var d=ItemChecks.File(incoming,"final",7,"arriving-moved");
                var b=ItemChecks.File(written,d.Path,occupantByte,"incumbent");var c=ItemChecks.File(incoming,"separate",occupantByte,"partner");
                var arrival=SyncedEntries.From(written,a,d with {Path=a.Path});var owner=SyncedEntries.From(written,b,c);
                byte content=scenario is "edited" or "receiver-edit" or "edited-equal"?(byte)9:(byte)7;
                if(content!=7) d=ItemChecks.File(incoming,d.Path,content,d.Id);
                to.Seed(a,[7]);from.Seed(d,[content]);to.Seed(b,[occupantByte]);from.Seed(c,[occupantByte]);
                await links.CommitAsync(pair.PairId,null,arrival,null,null,default);await links.CommitAsync(pair.PairId,null,owner,null,null,default);
                var pcArrival=written==Side.Local?a:d;var dbArrival=written==Side.Dropbox?a:d;
                var action=new SyncAction(written==Side.Local?ActionKind.MoveLocal:ActionKind.MoveDropbox,new(arrival.LocalKey,pcArrival,dbArrival,arrival,[],null,RowContext.Neutral),Target:b.Path);
                if(scenario is "rebound" or "new-owner")
                {
                    var snapshot=scenario=="rebound"?owner:null;
                    if(snapshot==null) await links.CommitAsync(pair.PairId,owner,null,null,null,default);
                    var release=new TaskCompletionSource(); var entered=new TaskCompletionSource();
                    var locks=new ItemLocks();
                    var hold=locks.RunAsync(pair,action,[],async ()=>{entered.SetResult();await release.Task;return true;},default);
                    await Task.WhenAny(hold,entered.Task); if(hold.IsCompleted) await hold;
                    var waiting=locks.RunAsync(pair,action,[],()=>new Effects().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),action,left,right,links,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],_=>true,grantedIncumbent:snapshot),default);
                    try
                    {
                        Check.Require(!waiting.IsCompleted,"Grant race did not reach the lock wait");
                        var replacement=c with {Id="replacement-partner",Path="replacement"}; from.Seed(replacement,[occupantByte]);
                        await links.CommitAsync(pair.PairId,snapshot,SyncedEntries.From(written,b,replacement),null,null,default);
                    }
                    finally {release.SetResult();await hold;}
                    bool rejected=false;try {await waiting;} catch(ChangedUnderneathException e) when(e.Path==b.Path) {rejected=true;}
                    Check.Require(rejected && left.Mutations.Count==0 && right.Mutations.Count==0,"Bound effect admitted an owner absent from its grant");
                    continue;
                }
                using var cancel=new CancellationTokenSource();
                if(scenario=="refused") right.RecycleRefusals[dbArrival.Path]=new ItemFailedException(dbArrival.Path,TextKey.FailChanged,null);
                if(scenario=="edited") to.AfterStage=()=>Check.Require(!to.Items.ContainsKey(a.Path) && !from.Items.ContainsKey(d.Path),"Edited arrival staged before both label moves");
                bool Covers((JournalPosition? Local,string? Dropbox) fence)
                {
                    if(scenario=="cancelled") cancel.Cancel();
                    if(scenario=="late") right.Items[dbArrival.Path]=(ItemChecks.File(Side.Dropbox,dbArrival.Path,9,dbArrival.Id),[9]);
                    return scenario!="uncovered" && fence.Local==new JournalPosition(1,2) && fence.Dropbox=="after";
                }
                const string wrong="Bound collision did not settle the arrival independently of the incumbent pair";
                EffectOutcome outcome;
                void OnEvent(RunEvent e)
                {
                    if(scenario=="receiver-edit" && e.Side==written && e.What==nameof(ISide.MoveAsync))
                        to.Items[e.Path]=(ItemChecks.File(written,e.Path,10,a.Id),[10]);
                }
                try { outcome=await new Effects().ApplyAsync(pair,ItemChecks.Settings,OnEvent,new(pair,[pair],DriveType.Fixed),action,left,right,links,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),cancel.Token,_=>[],Covers,grantedIncumbent:owner); }
                catch(ChangedUnderneathException) when(scenario=="receiver-edit") {outcome=EffectOutcome.Pending;}
                catch(OperationCanceledException) when(cancel.IsCancellationRequested) {outcome=EffectOutcome.Pending;}
                catch(ItemFailedException e) when(scenario=="refused" && e.Path==dbArrival.Path && e.Cause==TextKey.FailChanged) {outcome=EffectOutcome.Refused;}
                catch(ItemFailedException e) when(e.Cause==TextKey.ClashNameTaken && e.Path==b.Path) {throw new InvalidDataException(wrong,e);}
                var records=new List<SyncedEntry>(); await foreach(var link in links.DescendantsAsync(pair.PairId,Side.Local,"",default)) records.Add(link);
                var notices=new List<Notice>(); await foreach(var notice in links.NoticesAsync(pair.PairId,default)) notices.Add(notice);
                if(scenario=="receiver-edit")
                {
                    Check.Require(outcome==EffectOutcome.Pending && to.Items.Values.Any(v=>v.Entry.Id==a.Id && v.Bytes.SequenceEqual(new byte[]{10})) && to.Requests[nameof(ISide.StageAsync)]==0,"Changed label edition authorized stale transfer staging");
                    continue;
                }
                if(scenario=="refused")
                {
                    Check.Require(records.Contains(arrival) && left.History.Any(h=>h.Entry==pcArrival) && right.Items[dbArrival.Path].Entry==dbArrival,"Arrival binding retired after PC recycle before cloud removal");

                }
                if(scenario=="edited")
                    Check.Require(outcome==EffectOutcome.Applied && !from.Items.ContainsKey(d.Path) && !to.Items.ContainsKey(a.Path) && records.Contains(owner),"Edited arrival published without settling its move");
                if (scenario is not ("equal" or "different" or "edited" or "edited-equal"))
                {
                    Check.Require(outcome==(scenario=="refused"?EffectOutcome.Refused:EffectOutcome.Pending) && records.Contains(arrival) && records.Contains(owner) && right.Items[dbArrival.Path].Bytes.SequenceEqual(new byte[]{scenario=="late"?(byte)9:(byte)7}),"Unsettled recycle lost the arriving binding or deleted its cloud peer");
                    continue;
                }
                Check.Require(outcome==EffectOutcome.Applied && records.Contains(owner) && !records.Contains(arrival) &&
                    from.Items[c.Path].Entry==c && to.Items[b.Path].Entry==b && !from.Mutations.Contains(c.Id) && !to.Mutations.Contains(b.Id) && !from.Items.ContainsKey(d.Path) && !to.Items.ContainsKey(a.Path),wrong);
                if(occupantByte==content) Check.Require(records.Count==1 && left.History.Any(h=>h.Entry==pcArrival && h.Bytes.SequenceEqual(new byte[]{written==Side.Local?(byte)7:content})) && right.History.Any(h=>h.Entry==dbArrival && h.Bytes.SequenceEqual(new byte[]{written==Side.Dropbox?(byte)7:content})) && notices.Count==0 && left.Requests[nameof(ISide.PublishAsync)]==0 && right.Requests[nameof(ISide.PublishAsync)]==0,"Equal winner was published over the selected arrival before retirement");
                else
                {
                    var label=ConflictName.For(b.Path,incoming,d.Modified,ItemChecks.Settings.ComputerName,ItemChecks.Settings,1);
                    Check.Require(left.Items.TryGetValue(label,out var l) && right.Items.TryGetValue(label,out var r) && (scenario=="edited" && written==Side.Local || l.Entry.Id==pcArrival.Id) && r.Entry.Id==dbArrival.Id && l.Bytes.SequenceEqual(new byte[]{content}) && r.Bytes.SequenceEqual(new byte[]{content}) && records.Count==2 && records.Contains(SyncedEntries.From(l.Entry,r.Entry)) && notices.SingleOrDefault() is {} notice && notice.Cause==TextKey.InfoKeptBothRetained && !Resolutions.Choices(notice).Any(choice=>choice is ResolutionChoice.UseLocal or ResolutionChoice.UseDropbox or ResolutionChoice.RenameLocal or ResolutionChoice.RenameDropbox),wrong);
                    var settled=records.Single(v=>v!=owner);
                    NoticeItem[] members=[new(Side.Local,owner.LocalPath,left.Items[owner.LocalPath].Entry.Id),new(Side.Dropbox,owner.DropboxPath,right.Items[owner.DropboxPath].Entry.Id),new(Side.Local,settled.LocalPath,left.Items[settled.LocalPath].Entry.Id),new(Side.Dropbox,settled.DropboxPath,right.Items[settled.DropboxPath].Entry.Id)];
                    Check.Require(notices.Single().Subject.Items.Count==4 && members.ToHashSet().SetEquals(notices.Single().Subject.Items),"Retained notice does not name the four live binding members");
                }
            }
            finally {dir.Delete(recursive:true);}
        }
    }

    public static async Task RecycleEvidenceAsync()
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore(); var pair=ItemChecks.Pair(); var effects=new Effects();
        pc.Pages.Enqueue(new([],null,new(1,2))); db.Pages.Enqueue(new([],"after",null));
        var obsolete=ItemChecks.File(Side.Local,"dir/old",7,"obsolete"); var incumbent=ItemChecks.File(Side.Local,"dir/new",7,"incumbent");
        pc.Seed(obsolete,[7]); pc.Seed(incumbent,[7]); var peerFile=ItemChecks.File(Side.Dropbox,"dir/new",7,"keeper"); db.Seed(peerFile,[7]);
        var oldPeer=peerFile with {Path="dir/former"}; var oldLink=SyncedEntries.From(obsolete,oldPeer);
        await store.CommitAsync(pair.PairId,null,oldLink,null,null,default);
        var successor=ItemChecks.File(Side.Dropbox,oldPeer.Path,5,"successor"); db.Seed(successor,[5]);
        var selected=new RemovalSelection(new(obsolete,Side.Local.ToString(),obsolete.ContentHash),oldLink);
        await store.ArmAsync(pair.PairId,selected,null,default);
        Scope? observed=null;
        var settled=await Collisions.CollisionAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),Side.Local,obsolete,incumbent,peerFile,oldLink,pc,db,store,scope=>{observed=scope;return [];},(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,f=>f.Local==new JournalPosition(1,2) && f.Dropbox=="after");
        Check.Require(observed==new Scope(obsolete.Path,peerFile.Path) && settled==EffectOutcome.Applied &&
            store.Records.Values.SingleOrDefault()==SyncedEntries.From(incumbent,peerFile) && await store.ArmedAsync(pair.PairId,default)==null &&
            !pc.Items.ContainsKey(obsolete.Path) && pc.History.Any(h=>h.Entry==obsolete && h.Bytes.SequenceEqual(new byte[]{7})) &&
            pc.Items[incumbent.Path].Entry==incumbent && db.Items[peerFile.Path].Entry==peerFile && db.Items[oldPeer.Path].Entry==successor &&
            db.Items[oldPeer.Path].Bytes.SequenceEqual(new byte[]{5}) && !db.Mutations.Contains(successor.Id),"Mapped cleanup did not settle the live link or touched the obsolete-name occupant");
        await store.ArmAsync(pair.PairId,new(new(incumbent,Side.Local.ToString(),incumbent.ContentHash),null),null,default);
        Check.Require(await store.ArmedAsync(pair.PairId,default) is {} next && next.Victim.Proof.Expected.Id==incumbent.Id,"Next independent removal could not arm");
        await store.DisarmAsync(pair.PairId,new(new(incumbent,Side.Local.ToString(),incumbent.ContentHash),null),null,default);
        var victim=ItemChecks.File(Side.Local,"late",7,"late-id"); pc.Seed(victim,[7]);
        var prior=SyncedEntries.From(victim,victim with {Id="cloud-late"}); await store.CommitAsync(pair.PairId,null,prior,null,null,default);
        var expectedNote=new RemovalSelection(new(victim,Side.Local.ToString(),victim.ContentHash),prior);
        await store.ArmAsync(pair.PairId,expectedNote,null,default);
        var newer=ItemChecks.File(Side.Local,victim.Path,8,victim.Id);bool consumed=false;bool read=false;
        var actualReport=new Change(Side.Local,ChangeKind.Changed,victim.Path,null,victim.Id,newer,true,false){ConsumeHeld=()=>consumed=true};
        var outcome=await effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),new(ActionKind.DeleteLocal,new(prior.LocalKey,victim,null,prior,[],null,RowContext.Neutral)),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,scope=>
        {
            read=scope==new Scope(victim.Path,prior.DropboxPath) && !pc.Items.ContainsKey(victim.Path);
            Check.Require(store.Records.GetValueOrDefault((pair.PairId,prior.LocalKey))==prior &&
                store.ArmedAsync(pair.PairId,default).GetAwaiter().GetResult() is {} note && note.Victim==expectedNote && note.Stage==null,"Recovery received the edit without the selected old link and its armed note");
            pc.History.RemoveAll(h=>h.Entry.Id==victim.Id); pc.History.Add((newer,[8]));
            return [actualReport];
        },f=>f.Local==new JournalPosition(1,2) && f.Dropbox=="after");
        Check.Require(read && consumed && outcome==EffectOutcome.Pending && pc.Items.TryGetValue(victim.Path,out var restored) && restored.Bytes.SequenceEqual(new byte[]{8}) && db.Items.TryGetValue(victim.Path,out var other) && other.Bytes.SequenceEqual(new byte[]{8}) &&
            store.Records.GetValueOrDefault((pair.PairId,prior.LocalKey))==SyncedEntries.From(restored.Entry,other.Entry) && await store.ArmedAsync(pair.PairId,default)==null,"Identified late edit was not delivered to recovery before retiring the binding");
    }
    // Observe the stage contract and inject a provider arrival at the actual target read.
    private sealed class CollisionEndpoint(FakeSide inner) : ISide
    {
        public Side Side=>inner.Side;
        public string? StagedDigest {get;private set;}
        public Action<string>? AtRead {get;init;}
        public Action<Entry,string>? AtMove {get;init;}
        public Task<Entry?> ReadOneAsync(string path,CancellationToken ct) {AtRead?.Invoke(path); return inner.ReadOneAsync(path,ct);}
        public async Task<IStaged> StageAsync(string path,Stream content,Entry source,Entry? selectedTarget,CancellationToken ct)
        {
            StagedDigest=source.ContentHash;
            using var hash=new ContentHash(); int value;
            while((value=content.ReadByte())!=-1) hash.Append([(byte)value]);
            if(source.ContentHash.Length!=0 && hash.Finish()!=source.ContentHash) throw new ChangedUnderneathException(path);
            content.Position=0; return await inner.StageAsync(path,content,source,selectedTarget,ct);
        }
        public Task<(DirectoryIdentity? Local, DropboxRootIdentity? Dropbox, DriveEvidence? Drive, DriveType? LocalDriveType)> RootAsync(CancellationToken ct) => inner.RootAsync(ct);
        public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct) => inner.ReadAllAsync(under, options, ct);
        public Task<Stream> OpenReadAsync(Entry entry, CancellationToken ct) => inner.OpenReadAsync(entry, ct);
        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => inner.ContentHashAsync(entry, ct);
        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection, CancellationToken ct) => inner.PublishAsync(staged, expected, selection, ct);
        public Task<RemovalResult> EnsureFolderAsync(string path, CancellationToken ct) => inner.EnsureFolderAsync(path, ct);
        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct) => inner.DeleteAsync(expected, selection, ct);
        public async Task<RemovalResult> MoveAsync(Entry expected, string newPath, CancellationToken ct) {var moved=await inner.MoveAsync(expected,newPath,ct); AtMove?.Invoke(expected,newPath); return moved;}
        public Task<(IdentityLocation Location, bool InScope)?> ReadByIdAsync(string id, CancellationToken ct) => inner.ReadByIdAsync(id, ct);
        public Task<RemovalResult> CreateFolderExclusiveAsync(string path, CancellationToken ct) => inner.CreateFolderExclusiveAsync(path, ct);
        public Task<ChangePage> ReadChangesAsync(string? cursor, JournalPosition? position, CancellationToken ct) => inner.ReadChangesAsync(cursor, position, ct);
        public Task<(string? Cursor,JournalPosition? Position)> BoundaryAsync(CancellationToken ct) => inner.BoundaryAsync(ct);
        public Task SettleJournalAsync(JournalPosition position, CancellationToken ct) => inner.SettleJournalAsync(position, ct);
        public (TimeSpan Offset, TimeSpan Uncertainty)? ClockOffset => inner.ClockOffset;
        public Task<DeletionEvidence> DeletedAsync(string path, CancellationToken ct) => inner.DeletedAsync(path, ct);
        public Task<DeletionEvidence> DeletedAsync(string path, bool folder, CancellationToken ct) => inner.DeletedAsync(path, folder, ct);
        public Task<Recovery?> RecoverAsync(IdentityLocation actual, Entry version, CancellationToken ct) => inner.RecoverAsync(actual, version, ct);
        public Task<IStaged> ReopenStageAsync(RemovalSelection armedVictim, StageProof expected, CancellationToken ct) => inner.ReopenStageAsync(armedVictim, expected, ct);
        public Task<JobReply> ReadJobAsync(AcceptedJob job, CancellationToken ct) => inner.ReadJobAsync(job, ct);
    }
}
