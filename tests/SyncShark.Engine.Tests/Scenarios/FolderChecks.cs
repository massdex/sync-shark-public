using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>Models a deletion on one side with an independent surviving, changed, hidden or unreadable child on the other.</summary>
public static class FolderChecks
{
    internal static Func<(JournalPosition? Local,string? Dropbox),bool> RemovalCoverage(FakeSide pc,FakeSide cloud,Side victimSide,Entry victim,CancellationToken ct)
    {
        var echo=new Change(victimSide,ChangeKind.Deleted,victim.Path,null,victim.Id,null,false,victimSide==Side.Local);
        var journal=new ChangePage(victimSide==Side.Local?[echo with {Usn=2,JournalId=1}]:[],null,new(1,3));
        var feed=new ChangePage(victimSide==Side.Dropbox?[echo]:[],"after-removal",null);
        pc.Pages.Enqueue(journal);cloud.Pages.Enqueue(feed);
        return fence=>
        {
            if(fence.Local!=journal.Position || fence.Dropbox!=feed.Cursor ||
               !(victimSide==Side.Local?pc:cloud).History.Any(v=>v.Entry==victim)) return false;
            var stop=(fence.Dropbox,fence.Local);
            var localRead=pc.ReadChangesAsync(null,null,stop,ct).GetAwaiter().GetResult();
            var cloudRead=cloud.ReadChangesAsync(null,null,stop,ct).GetAwaiter().GetResult();
            return !localRead.ContinuityLost && !cloudRead.ContinuityLost && localRead.CaughtUp && cloudRead.CaughtUp &&
                localRead.Position==fence.Local && cloudRead.Cursor==fence.Dropbox &&
                localRead.Changes.SequenceEqual(journal.Changes) && cloudRead.Changes.SequenceEqual(feed.Changes);
        };
    }

    public static async Task DeleteAsync(bool excluded=false, bool ignored=false, bool unreadable=false, bool edited=false, bool nested=false)
    {
        const string root="folder"; const string child="folder/child";
        var pair=ItemChecks.Pair() with { IgnoreRules=ignored?["FileName:child"]:[], Exclusions=excluded?[new(Side.Local,child,true)]:[] };
        var pc=new FakeSide(Side.Local);var cloud=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default)),"{}",default);
        var inner=pair with { PairId="inner",LocalRoot=Path.Combine(pair.LocalRoot,"folder","child"),DropboxRoot=pair.DropboxRoot+"/folder/child" };
        if(nested) await store.SavePairAsync(inner,default);
        var paths=new PathScope(pair,nested?[pair,inner]:[pair],DriveType.Fixed);var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,cloud,store);
        var a=ItemChecks.File(Side.Local,root,0,"parent-a",true);var b=ItemChecks.File(Side.Dropbox,root,0,"parent-b",true);
        var parent=SyncedEntries.From(a,b);await store.CommitAsync(pair.PairId,null,parent,null,null,default); pc.Seed(a,[]);
        var childA=ItemChecks.File(Side.Local,child,1,"child-a",nested);var childB=ItemChecks.File(Side.Dropbox,child,1,"child-b",nested);
        if(!ignored && !excluded && !nested) await store.CommitAsync(pair.PairId,null,SyncedEntries.From(childA,childB),null,null,default);
        var live=edited?ItemChecks.File(Side.Local,child,3,childA.Id):childA;pc.Seed(live,nested?[]:new[]{edited?(byte)3:(byte)1});
        if(excluded || nested)
        {
            var sibling=ItemChecks.File(Side.Local,"folder/free",2,"free-a");pc.Seed(sibling,[2]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(sibling,ItemChecks.File(Side.Dropbox,"folder/free",2,"free-b")),null,null,default);
        }
        if(unreadable) pc.ReadFailures[child]=new ItemFailedException(child,TextKey.ReadLocalFailed,null);
        if(edited)
        {
            // The parent owns the footprint before the local edit is read; recovery must consume the held child.
            var edit = new Change(Side.Local,ChangeKind.Changed,child,null,live.Id,live,true,false,10,JournalId:1);
            var gone = new Change(Side.Dropbox,ChangeKind.Deleted,root,null,b.Id,null,false,false);
            cloud.Pages.Enqueue(new([gone],"removed",null));
            pc.Pages.Enqueue(new([edit],null,new(1,11)));
            var deletion = await cloud.ReadChangesAsync(null,null,default);
            Task? held = null;
            await queue.AdmitAsync(pair,deletion,async (_,ct)=>
            {
                var editedPage = await pc.ReadChangesAsync(null,null,ct);
                held = queue.AdmitAsync(pair,editedPage,(_,_)=>throw new InvalidOperationException("Recovered edit ran independently"),pc.SettleJournalAsync,ct);
                // The next local record is the folder recycle; the feed has no newer change.
                var echo = new Change(Side.Local,ChangeKind.Deleted,root,null,a.Id,null,false,true,11,JournalId:1);
                pc.Pages.Enqueue(new([echo],null,new(1,12)));
                cloud.Pages.Enqueue(new([],deletion.Cursor,null));
                bool Covered((JournalPosition? Local,string? Dropbox) fence)
                {
                    // FakeSide reads complete synchronously. Do not certify the fence until the actual victim and child are in its bin.
                    var victim = pc.ReadByIdAsync(a.Id,ct).GetAwaiter().GetResult();
                    var retained = pc.ReadByIdAsync(live.Id,ct).GetAwaiter().GetResult();
                    if(victim is not {InScope:false} parentReceipt || parentReceipt.Location.Entry!=a ||
                       retained is not {InScope:false} childReceipt || childReceipt.Location.Entry!=live) return false;
                    var localEnd = pc.ReadChangesAsync(null,editedPage.Position,ct).GetAwaiter().GetResult();
                    var cloudEnd = cloud.ReadChangesAsync(deletion.Cursor,null,ct).GetAwaiter().GetResult();
                    return localEnd.Changes.SequenceEqual(new[]{echo}) && localEnd.Position==fence.Local && cloudEnd.Cursor==fence.Dropbox;
                }
                await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,parent,Side.Dropbox,pc,cloud,store,queue.Held,ItemChecks.Population(pair,paths,pc,cloud,store),ItemChecks.Hop(pair,paths,queue,pc,cloud,store),ct,coversThrough:Covered);
            },null,default);
            if(held!=null) await held;
        }
        else
        {
            var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,parent,Side.Dropbox,pc,cloud,store,queue.Held,ItemChecks.Population(pair,paths,pc,cloud,store),ItemChecks.Hop(pair,paths,queue,pc,cloud,store),default,
                coversThrough:ignored?RemovalCoverage(pc,cloud,Side.Local,a,default):null);
            if(ignored) Check.Require(result.Outcome==RemovalOutcome.Removed,"Ignored folder removal was not settled");
        }
        if(ignored)
        {
            Check.Require(pc.Items.Count==0 && cloud.Items.Count==0 && pc.History.Any(x=>x.Entry.Path==child && x.Bytes.SequenceEqual(new byte[]{1})),"Ignored child did not accompany recycled parent");
            Check.Require(store.Records.Count==0 && store.OpenNotices.Count==0,"Spurious kept-folder notice or binding");
        }
        else if(edited)
        {
            Check.Require(pc.Items[child].Bytes.SequenceEqual(new byte[]{3}) && cloud.Items[child].Bytes.SequenceEqual(new byte[]{3}),"Latest attributable edit lost");
            Check.Require(store.Records.Values.Any(row=>row.LocalPath==child && row.DropboxPath==child) && store.OpenNotices.Count(n=>n.Cause==TextKey.InfoEditBeatsDelete)==1,"Lost linkage or edit notice");
        }
        else
        {
            Check.Require(pc.Items.Count==2 && pc.Items.ContainsKey(root) && pc.Items[child].Entry==live && cloud.Items.Count==0 && pc.History.All(v=>v.Entry.Path=="folder/free"),"Protected or unreadable child was removed");
            if(excluded || nested) Check.Require(pc.History.Single().Bytes.SequenceEqual(new byte[]{2}) && store.Records.Values.All(v=>v.LocalPath!="folder/free"),"Eligible sibling not recycled and retired");
            if(unreadable) Check.Require(store.OpenNotices.Count(n=>n.Kind==NoticeKind.Failure)==1 && store.Records.Count==2,"Unreadable source incorrectly settled");
            else Check.Require(store.OpenNotices.All(n=>n.Kind!=NoticeKind.Failure),"Ordinary protected survivor became a failure");
        }
    }
    public static async Task LinksAsync()
    {
        var pair=ItemChecks.Pair();var pc=new FakeSide(Side.Local);var cloud=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default)),"{}",default);
        var paths=new PathScope(pair,[pair],DriveType.Fixed);var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,cloud,store);
        foreach(var path in new[]{"top-a/link","top-b/link"}) pc.Seed(ItemChecks.File(Side.Local,path,0,path,true) with { Skip=nameof(TextKey.SkipLink) },[]);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,cloud,store,null,null,default);
        var notice=store.OpenNotices.Single();Check.Require(notice.Kind==NoticeKind.Info && notice.Cause==TextKey.InfoLinks && notice.Subject.Items.Count==2,"Link summary must contain both members");
        Check.Require(pc.Items.Count==2 && cloud.Items.Count==0 && store.Records.Count==0,"Link followed or copied");
        pc.Items.Clear();await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,cloud,store,null,null,default);Check.Require(store.OpenNotices.Count==0 && cloud.Items.Count==0 && store.Records.Count==0,"Stale link membership");
    }
    /// <summary>A detached read creates no image; the first explicit add publishes the pair.</summary>
    public static Task FreshStartAsync() => FirstAddAsync(false);

    private static async Task FirstAddAsync(bool interrupt)
    {
        var scratch=Directory.CreateTempSubdirectory().FullName;
        var folder=Path.Combine(scratch,ResourceNames.EngineFolder);var path=Path.Combine(folder,ResourceNames.DatabaseFile);
        var stage=path+".20000101T000000Z.new";var boundary=new IOException("Fixture: closed stage, before publication");
        int creates=0;byte[]? abandoned=null;
        try
        {
            var pair=ItemChecks.Pair();var local=new FakeSide(Side.Local);var cloud=new FakeSide(Side.Dropbox);
            async Task<IStore?> Open(bool create,CancellationToken ct)
            {
                if(!File.Exists(path) && !create) return null;
                if(create && !File.Exists(path))
                {
                    creates++;
                    if(interrupt && creates==1)
                    {
                        // Stop the first host at the create factory, after writing a closed stage but before the rename.
                        Directory.CreateDirectory(folder);
                        using(var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder {DataSource=stage,Pooling=false}.ToString()))
                        {
                            await db.OpenAsync(ct);db.CreateFunction<string,string>("pathkey",PathNames.Key,isDeterministic:true);
                            using var cmd=db.CreateCommand();cmd.CommandText=global::Massdex.SyncShark.Engine.Storage.Schema.Create;
                            await cmd.ExecuteNonQueryAsync(ct);
                            cmd.CommandText=$"PRAGMA user_version={(global::Massdex.SyncShark.Engine.Storage.Schema.Version)}; INSERT INTO journal_position VALUES('orphan',1,2)";await cmd.ExecuteNonQueryAsync(ct);
                        }
                        abandoned=File.ReadAllBytes(stage);throw boundary;
                    }
                    await new global::Massdex.SyncShark.Engine.Storage.StoreMove().CreateAsync(scratch,ct);
                }
                return new global::Massdex.SyncShark.Engine.Storage.Store(path,false,ItemChecks.Settings);
            }
            Host Start()=>new(Open,(_,_,_,_)=>(local,cloud),ItemChecks.Settings,null!,TimeProvider.System,_=>throw new InvalidOperationException(),(_,_)=>throw new InvalidOperationException());
            var host=Start();
            await foreach(var unused in host.PairsAsync(default)) throw new InvalidDataException("Fresh read reported a pair");
            Check.Require(creates==0 && !File.Exists(path),"Read created a database");
            if(interrupt)
            {
                try { await host.AddPairAsync(pair,default);throw new InvalidDataException("First add did not report interruption"); }
                catch(IOException ex) when(ReferenceEquals(ex,boundary)) { }
                Check.Require(creates==1 && abandoned is {Length:>0} && !File.Exists(path),"No observable closed-stage/pre-publication boundary");
                host=Start();await foreach(var unused in host.PairsAsync(default)) throw new InvalidDataException("Restart adopted an abandoned stage");
                Check.Require(creates==1 && !File.Exists(path) && File.ReadAllBytes(stage).SequenceEqual(abandoned!),"Restart published or disturbed the earlier attempt");
            }
            await host.AddPairAsync(pair,default);
            Check.Require(creates==(interrupt?2:1) && File.Exists(path),"Explicit add did not publish exactly one fresh store");
            using var reopened=new global::Massdex.SyncShark.Engine.Storage.Store(path,true,ItemChecks.Settings);
            Check.Require((await reopened.PairAsync(pair.PairId,default))?.PairId==pair.PairId,"Published image lacks the new pair");
            if(interrupt) Check.Require(await reopened.PositionAsync("orphan",default)==null && File.ReadAllBytes(stage).SequenceEqual(abandoned!),"Retry reused or removed an earlier attempt’s stage");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(scratch,true); }
    }

    public static Task A04FileOnPcFolderOnCloudAsync() => throw new NotImplementedException();
    public static Task A04FolderOnPcFileOnCloudAsync() => throw new NotImplementedException();
    public static Task A04ChildBoundAtAbandonedLabelAsync() => throw new NotImplementedException();
    public static Task A04FreshLabelCompletionRebindsChildAsync() => throw new NotImplementedException();
    public static Task A08FileToFolderOnCloudAsync() => throw new NotImplementedException();
    public static Task A08FolderToFileOnPcAsync() => throw new NotImplementedException();
    public static Task A08FolderToFileOnCloudAsync() => throw new NotImplementedException();
    public static Task A08ChildDeletesBeforeParentReplacementAsync() => throw new NotImplementedException();
    public static Task A08StageFailureBeforeRemovalAsync() => throw new NotImplementedException();
    public static async Task A08SoleSmallUploadSourceUnreadableAsync()
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);using var store=new InMemoryStateStore();
        var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var file=ItemChecks.File(Side.Local,"item",2,"pc");var folder=ItemChecks.File(Side.Dropbox,"item",0,"db",true);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,db,store);
        pc.Seed(file,[2]);db.Seed(folder,[]);pc.ContentFailures[file.Path]=new ItemFailedException(file.Path,TextKey.ReadLocalFailed,null);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,file.Path,null,file.Id,file,true,false),pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default);
        Check.Require(pc.Requests[nameof(ISide.OpenReadAsync)]+pc.Requests[nameof(ISide.ContentHashAsync)]>0 && db.Items[folder.Path].Entry==folder && db.Requests[nameof(ISide.DeleteAsync)]==0 && store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure),"Failed content read removed the target or went unreported");
    }
    public static Task A08ProtectedExclusionPcAsync() => throw new NotImplementedException();
    public static Task A08ProtectedExclusionCloudAsync() => throw new NotImplementedException();
    public static Task A08ProtectedLinkPcAsync() => throw new NotImplementedException();
    public static Task A08ProtectedLinkCloudAsync() => throw new NotImplementedException();
    public static Task A08UnreadablePcChildAsync() => throw new NotImplementedException();
    public static Task A08CloudReplacementKnownUnreadableAsync() => throw new NotImplementedException();
    public static Task A08FinishRefusedAfterFolderRemovalAsync() => throw new NotImplementedException();
    public static Task A08RetryCreateOnlyAsync() => throw new NotImplementedException();
    public static Task A08CompetingRecoveredFolderAsync() => throw new NotImplementedException();
    public static Task A14NestedRootSurvivorAsync() => throw new NotImplementedException();
    public static Task A14LinkOnDeletingSideAsync() => throw new NotImplementedException();
    public static Task A14EditedLooseFileBesideExclusionAsync() => throw new NotImplementedException();
    public static Task A14MoveOutBeforeDeleteJournalAsync() => throw new NotImplementedException();
    public static Task A14MoveOutFirstSeenByRepairAsync() => throw new NotImplementedException();
    public static Task A14OversizeSelectedFolderSplitAsync() => throw new NotImplementedException();
    public static Task A14FailedChildKeepsCustodyAsync() => throw new NotImplementedException();
    public static Task A14OverlappingEarlierEditBeforeDeleteAsync() => throw new NotImplementedException();
    public static Task A14EarlierAncestorMoveBeforeDeleteAsync() => throw new NotImplementedException();
    public static Task A16IndependentFolderContinuesAsync() => throw new NotImplementedException();
    public static Task A18OneSidedFolderMoveAsync() => throw new NotImplementedException();
    public static Task A18TombstoneQueuedAtFirstHopAsync() => throw new NotImplementedException();
    public static Task A18TombstoneUnreadAtFirstHopAsync() => throw new NotImplementedException();
    public static Task A18TombstoneQueuedAtCaseHopAsync() => throw new NotImplementedException();
    public static Task A18TombstoneUnreadAtCaseHopAsync() => throw new NotImplementedException();
    public static Task A18PcChildAlsoEditedAsync() => throw new NotImplementedException();
    public static Task A18RestartAfterParentBindingBeforeChildSettlementAsync() => throw new NotImplementedException();
    public static Task A18SecondMoveBeforeFirstCloudReportAsync() => throw new NotImplementedException();
    public static Task A18HardLinkedChildInPairAsync() => throw new NotImplementedException();
    public static Task A18HardLinkedChildOutsidePairAsync() => throw new NotImplementedException();
    public static Task A18OldNameReusedAsync() => throw new NotImplementedException();
    public static Task A23NestedClashingChildFoldersAsync() => throw new NotImplementedException();
    public static Task A23DifferentChildFilesKeepBothAsync() => throw new NotImplementedException();
    public static Task A23MoveOverRelocationCeilingAsync() => throw new NotImplementedException();
    public static Task A23NewChildBeforeLastListingAsync() => throw new NotImplementedException();
    public static Task A23NewChildAfterLastListingAsync() => throw new NotImplementedException();
    public static Task A23RemoveOnlyOriginalSourceAsync() => throw new NotImplementedException();
    public static Task A30JunctionAsync() => throw new NotImplementedException();
    public static Task A30SymbolicLinkAsync() => throw new NotImplementedException();
    public static Task A30LastMemberDisappearsAsync() => throw new NotImplementedException();
    public static Task A30MappedLinkProtectsRemoteMoveAsync() => throw new NotImplementedException();
    public static Task A36IgnoredFileOnlyAsync() => throw new NotImplementedException();
    public static Task A36IgnoredFolderWithContentAsync() => throw new NotImplementedException();
    public static Task A36OrdinaryFileInFolderAsync() => throw new NotImplementedException();
    public static Task A36ExcludedChildAsync() => throw new NotImplementedException();
    public static Task A36NestedRootChildAsync() => throw new NotImplementedException();
    public static Task A36LinkedChildAsync() => throw new NotImplementedException();
    public static Task A36NestedEmptyFoldersAsync() => throw new NotImplementedException();
    public static Task A36EmptyChildWithoutDeletionRecordAsync() => throw new NotImplementedException();
    public static Task A36InterleavedOrdinalSubtreesAsync() => throw new NotImplementedException();
    public static Task A46CrashStageWrittenAsync() => throw new NotImplementedException();
    public static Task A46CrashV16PutAsideAsync() => throw new NotImplementedException();
    public static Task A46CrashAfterSwitchAsync() => throw new NotImplementedException();
    public static Task A46SwitchFailedAsync() => throw new NotImplementedException();
    public static Task A46PointerRemovalFailedAsync() => throw new NotImplementedException();
    public static Task A46PreviousAttemptStageUntouchedAsync() => throw new NotImplementedException();
    public static Task A46PointerToFixedRootAsync() => throw new NotImplementedException();
    public static Task A46FixedPointerWithOnlyV16BackupAsync() => throw new NotImplementedException();
    public static Task A46TwoLinkNoticesFoldedAsync() => throw new NotImplementedException();
    public static Task A46MixedLinkAndNonlinkMembersAsync() => throw new NotImplementedException();
    public static Task A46UnchangedNoticesPreservedAsync() => throw new NotImplementedException();
    public static Task A46CrashDuringConnectionListCopyAsync() => throw new NotImplementedException();
    /// <summary>Restart after the first add stops before publication; neither restart nor retry may adopt its stage.</summary>
    public static Task A46CrashDuringFirstPairAddAsync() => FirstAddAsync(true);
    public static Task A46FreshReadCreatesNoStoreAsync() => FreshStartAsync();
    public static Task A46ClosedImageNoWalAsync() => throw new NotImplementedException();
    public static Task A46AmbiguousIndependentStoresAsync() => throw new NotImplementedException();
    public static Task A46InvalidSourceAsync() => throw new NotImplementedException();
    public static Task A46ExternalDanglingPointerAsync() => throw new NotImplementedException();
    public static Task A46BothListsAbsentNoRequiredAccountAsync() => throw new NotImplementedException();
    public static Task A46MissingRequiredConnectionAsync() => throw new NotImplementedException();
    public static Task A46DistinctConnectionListsAsync() => throw new NotImplementedException();
    public static Task A48SameNameRemovedTwiceAsync() => throw new NotImplementedException();
    public static Task A48TwoPairsOneVolumeAsync() => throw new NotImplementedException();
    public static Task A48DriveRootScopeAsync() => throw new NotImplementedException();
    public static Task A48RootBinJunctionRemoveRefusedAsync() => throw new NotImplementedException();
    public static Task A48RootBinJunctionTrimRefusedAsync() => throw new NotImplementedException();
    public static Task A48StartupTrimsOldTopLevelAsync() => throw new NotImplementedException();
    public static Task A48ExactRetentionBoundaryAsync() => throw new NotImplementedException();
    public static Task A48QuietJobBeforeTrimAsync() => throw new NotImplementedException();
    public static Task A48FailedPutBackBeforeTrimAsync() => throw new NotImplementedException();
    public static Task A48BinOnNativeBinVolumeNotTrimmedAsync() => throw new NotImplementedException();
    public static Task A48UnrelatedVolumeNotTrimmedAsync() => throw new NotImplementedException();
    public static Task A48LongDowntimeRecoverBeforeTrimAsync() => throw new NotImplementedException();
    public static Task A48UnavailableHistoryAsync() => throw new NotImplementedException();
    public static Task A48UnknownDriveUsesShellOrAbortAsync() => throw new NotImplementedException();
    public static Task A48StampBeforeNonreplacingMoveAsync() => throw new NotImplementedException();
    public static Task A50NewInnerRootEffectiveBeforeNextEntryAsync() => throw new NotImplementedException();
    public static Task A50ExclusionEffectiveBeforeNextEntryAsync() => throw new NotImplementedException();
    public static Task A50InnerPairRemovedQuietRangeAsync() => throw new NotImplementedException();
    public static Task A50SideAccountSpecificRootsAsync() => throw new NotImplementedException();
    public static Task A50MappedOtherEndpointProtectedAsync() => throw new NotImplementedException();
    public static Task A50WholeMoveProtectedAsync() => throw new NotImplementedException();
    public static Task A50PartialMoveProtectedAsync() => throw new NotImplementedException();
    public static Task A50ReplacementRetainsFolderAsync() => throw new NotImplementedException();
}
