using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.Engine.Run;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>A settled unchanged Repair must not re-read or transfer individual unchanged files at either regression size.</summary>
public static class CallBudgetChecks
{
    public static async Task UnchangedAsync(int size)
    {
        var pair = ItemChecks.Pair(); var pc = new FakeSide(Side.Local); var cloud = new FakeSide(Side.Dropbox);
        using var store = new InMemoryStateStore();
        await store.SavePairAsync(pair, default); await store.FinishRepairAsync(pair.PairId,(0,await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default)),"{}",default);
        for (int i=0;i<size;i++)
        {
            string path = $"file-{i}"; var local = ItemChecks.File(Side.Local,path,1,$"local-{i}"); var remote = ItemChecks.File(Side.Dropbox,path,1,$"cloud-{i}");
            pc.Seed(local,[1]); cloud.Seed(remote,[1]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(local,remote),null,null,default);
        }
        long before = store.Requests[nameof(IStore.CommitAsync)];
        await Repair.RunAsync(pair,new PathScope(pair,[pair],DriveType.Fixed),pc,cloud,store,null,null,default);
        foreach (var side in new[]{pc,cloud})
        {
            Check.Require(side.Items.Count == size && side.Items.Values.All(v => v.Bytes.SequenceEqual(new byte[]{1})), "Unchanged file modified");
            foreach (var op in new[]{nameof(ISide.OpenReadAsync),nameof(ISide.ContentHashAsync),nameof(ISide.ReadOneAsync),nameof(ISide.PublishAsync),nameof(ISide.DeleteAsync),nameof(ISide.MoveAsync)})
                Check.Require(side.Requests[op] == 0, $"{side.Side} {op} per unchanged item");
        }
        Check.Require(store.Requests[nameof(IStore.CommitAsync)] == before && store.Records.Count == size && store.OpenNotices.Count == 0, "Unchanged settlement wrote state");
    }

    public static Task UnchangedAutosyncAsync(int size) => throw new NotImplementedException();
    public static Task ChangedUploadDirectAsync(int size) => throw new NotImplementedException();
    public static Task ChangedUploadSessionAsync(int size) => throw new NotImplementedException();
    public static Task UploadFolderReplacementAsync(int size) => throw new NotImplementedException();
    public static Task ChangedDownloadAsync(int size) => throw new NotImplementedException();
    public static Task SameSizeHashDecisionAsync(int size) => throw new NotImplementedException();
    public static Task MoveWithoutTransferAsync(int size) => throw new NotImplementedException();
    public static Task FirstSyncStampMatchAsync(int size) => throw new NotImplementedException();
    public static Task FirstSyncDeletedNameProbeAsync(int size) => throw new NotImplementedException();
    public static Task CloudEntryEchoAsync(int size) => throw new NotImplementedException();
    public static Task CloudTombstoneEchoAsync(int size) => throw new NotImplementedException();
    public static Task PcPublicationEchoAsync(int size) => throw new NotImplementedException();
    public static Task PcUnprovedContentChangeAsync(int size) => throw new NotImplementedException();
    public static Task RepairAfterGapAsync(int size) => throw new NotImplementedException();
    public static Task WholeFolderDeleteAsync(int size) => throw new NotImplementedException();
    public static Task PartialFolderDeleteAsync(int size) => throw new NotImplementedException();
    public static Task RefusedFolderSplitAsync(int size) => throw new NotImplementedException();
    public static Task PcRecycleNoteAsync(int size) => throw new NotImplementedException();
    public static Task MoveOrMergeDestinationAsync(int size) => throw new NotImplementedException();
    public static Task MoveRefusalSubdivideAsync(int size) => throw new NotImplementedException();
    public static Task MappedRangeBeforeRepathAsync(int size) => throw new NotImplementedException();
    public static Task SourceCleanupAsync(int size) => throw new NotImplementedException();
    public static Task FolderJobBootAsync(int size) => throw new NotImplementedException();
    public static Task FolderJobRepairAsync(int size) => throw new NotImplementedException();
    public static Task FolderJobOverlapAsync(int size) => throw new NotImplementedException();
    public static Task FolderJobCoalescedWakeAsync(int size) => throw new NotImplementedException();
    public static Task DeletedNameBlockAndDeltaAsync(int size) => throw new NotImplementedException();
    public static Task ScratchScanWritesAndClearAsync(int size) => throw new NotImplementedException();
    public static Task ScopeConfigWriteAsync(int size) => throw new NotImplementedException();
    public static Task ScopeCompareAsync(int size) => throw new NotImplementedException();
    public static Task ScopePartialRepeatAsync(int size) => throw new NotImplementedException();
    public static Task FailureNewAndRefreshedAsync(int size) => throw new NotImplementedException();
    public static Task NoticeCloseAsync(int size) => throw new NotImplementedException();
    public static Task UnknownEffectReadbacksAsync(int size) => throw new NotImplementedException();
    public static Task HeldVersionRetryAsync(int size) => throw new NotImplementedException();
    public static Task StartupOpenObligationsAsync(int size) => throw new NotImplementedException();
    public static Task ResumeReplayAsync(int size) => throw new NotImplementedException();
    public static Task PausedVolumeHistoryAsync(int size) => throw new NotImplementedException();
    public static Task CleanBootNoBindingWalkAsync(int size) => throw new NotImplementedException();
    public static Task PerPhaseSqlAndRowCountsAsync(int size) => throw new NotImplementedException();
    public static Task DurableBytesPerItemAsync(int size) => throw new NotImplementedException();
    public static Task ScratchBytesPerItemAsync(int size) => throw new NotImplementedException();
    public static Task WindowAndWaiterMemoryAsync(int size) => throw new NotImplementedException();
}
