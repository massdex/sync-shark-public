using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>Models a deletion on one side with an independent surviving, changed, hidden or unreadable child on the other.</summary>
public static class FolderChecks
{
    public static async Task DeleteAsync(bool excluded=false, bool ignored=false, bool unreadable=false, bool edited=false, bool nested=false)
    {
        const string root="folder"; const string child="folder/child";
        var pair=ItemChecks.Pair() with { IgnoreRules=ignored?["FileName:child"]:[], Exclusions=excluded?[new(Side.Local,child,true)]:[] };
        var pc=new FakeSide(Side.Local);var cloud=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default)),"{}",default);
        if(nested) await store.SavePairAsync(pair with { PairId="inner",LocalRoot=Path.Combine(pair.LocalRoot,"folder","child"),DropboxRoot=pair.DropboxRoot+"/folder/child" },default);
        var a=ItemChecks.File(Side.Local,root,0,"parent-a",true);var b=ItemChecks.File(Side.Dropbox,root,0,"parent-b",true);
        var parent=SyncedEntries.From(a,b);await store.CommitAsync(pair.PairId,null,parent,null,null,default); pc.Seed(a,[]);
        var childA=ItemChecks.File(Side.Local,child,1,"child-a",nested);var childB=ItemChecks.File(Side.Dropbox,child,1,"child-b",nested);
        if(!ignored && !excluded && !nested) await store.CommitAsync(pair.PairId,null,SyncedEntries.From(childA,childB),null,null,default);
        var live=edited?ItemChecks.File(Side.Local,child,3,childA.Id):childA;pc.Seed(live,nested?[]:new[]{edited?(byte)3:(byte)1});
        if(unreadable) pc.ReadFailures[child]=new ItemFailedException(child,TextKey.ReadLocalFailed,null);
        await new FolderDelete().ApplyAsync(pair,parent,Side.Dropbox,pc,cloud,store,ItemChecks.Population(pair,pc,cloud,store),default);
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
            Check.Require(pc.Items.Count==2 && pc.Items.ContainsKey(root) && pc.Items[child].Entry==live && cloud.Items.Count==0 && pc.History.Count==0,"Protected or unreadable child was removed");
            if(unreadable) Check.Require(store.OpenNotices.Count(n=>n.Kind==NoticeKind.Failure)==1 && store.Records.Count==2,"Unreadable source incorrectly settled");
            else Check.Require(store.OpenNotices.All(n=>n.Kind!=NoticeKind.Failure),"Ordinary protected survivor became a failure");
        }
    }
    public static async Task LinksAsync()
    {
        var pair=ItemChecks.Pair();var pc=new FakeSide(Side.Local);var cloud=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default)),"{}",default);
        foreach(var path in new[]{"top-a/link","top-b/link"}) pc.Seed(ItemChecks.File(Side.Local,path,0,path,true) with { Skip=nameof(TextKey.SkipLink) },[]);
        await Repair.RunAsync(pair,pc,cloud,store,null,default);
        var notice=store.OpenNotices.Single();Check.Require(notice.Kind==NoticeKind.Info && notice.Cause==TextKey.InfoLinks && notice.Subject.Items.Count==2,"Link summary must contain both members");
        Check.Require(pc.Items.Count==2 && cloud.Items.Count==0 && store.Records.Count==0,"Link followed or copied");
        pc.Items.Clear();await Repair.RunAsync(pair,pc,cloud,store,null,default);Check.Require(store.OpenNotices.Count==0 && cloud.Items.Count==0 && store.Records.Count==0,"Stale link membership");
    }
    public static async Task FreshStartAsync()
    {
        // The empty disposable container is not a database; startup must not create one on a read.
        var scratch=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var source=Path.Combine(scratch,"predecessor");var dest=Path.Combine(scratch,"current");
            await new global::Massdex.SyncShark.Engine.Storage.StoreMove().MoveAsync(source,dest,default);
            Check.Require(!File.Exists(Path.Combine(dest,ResourceNames.EngineFolder,ResourceNames.DatabaseFile)),"Fresh startup created a store");
        }
        finally { Directory.Delete(scratch,true); }
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
    public static Task A08SoleSmallUploadSourceUnreadableAsync() => throw new NotImplementedException();
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
    public static Task A46CrashDuringFirstPairAddAsync() => throw new NotImplementedException();
    public static Task A46FreshReadCreatesNoStoreAsync() => throw new NotImplementedException();
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
    public static Task A48NoPositiveNoBinDiscriminatorAsync() => throw new NotImplementedException();
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
