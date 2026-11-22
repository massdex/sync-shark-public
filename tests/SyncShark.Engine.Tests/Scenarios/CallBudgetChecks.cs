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
        await store.SavePairAsync(pair, default); store.MarkFirstRepairDone(pair.PairId);
        for (int i=0;i<size;i++)
        {
            string path = $"file-{i}"; var local = ItemChecks.File(Side.Local,path,1,$"local-{i}"); var remote = ItemChecks.File(Side.Dropbox,path,1,$"cloud-{i}");
            pc.Seed(local,[1]); cloud.Seed(remote,[1]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(local,remote),null,default);
        }
        long before = store.Requests[nameof(IStore.CommitAsync)];
        await Repair.RunAsync(pair,pc,cloud,store,null,default);
        foreach (var side in new[]{pc,cloud})
        {
            Check.Require(side.Items.Count == size && side.Items.Values.All(v => v.Bytes.SequenceEqual(new byte[]{1})), "Unchanged file modified");
            foreach (var op in new[]{nameof(ISide.OpenReadAsync),nameof(ISide.ContentHashAsync),nameof(ISide.ReadOneAsync),nameof(ISide.PublishAsync),nameof(ISide.DeleteAsync),nameof(ISide.MoveAsync)})
                Check.Require(side.Requests[op] == 0, $"{side.Side} {op} per unchanged item");
        }
        Check.Require(store.Requests[nameof(IStore.CommitAsync)] == before && store.Records.Count == size && store.OpenNotices.Count == 0, "Unchanged settlement wrote state");
    }
}
