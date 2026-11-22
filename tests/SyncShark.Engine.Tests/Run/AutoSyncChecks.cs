using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static class AutoSyncChecks
{
    public static async Task RunAsync()
    {
        var pair = ItemChecks.Pair(); var pc = new FakeSide(Side.Local); var db = new FakeSide(Side.Dropbox);
        using var store = new InMemoryStateStore(); var paths = new PathScope(pair, [pair], DriveType.Fixed);
        var queue = new WorkQueue(); const string root = "parent", path = "parent/item";
        var first = ItemChecks.File(Side.Dropbox, path, 1, "item"); var last = ItemChecks.File(Side.Dropbox, path, 2, "item");
        Change Report(Entry version) => new(Side.Dropbox, ChangeKind.Changed, path, null, version.Id, version, false, false);
        Task? settlement = null;
        var removal = new Change(Side.Local, ChangeKind.Deleted, root, null, "parent-id", null, false, true);
        await queue.AdmitAsync(pair, new([removal],null,new(1,2)), async (_,ct) => {
            settlement = AutoSync.ApplyPageAsync(pair, ItemChecks.Settings, _=>{}, paths, queue, Side.Dropbox,
                new([Report(first),Report(last),new(Side.Dropbox,ChangeKind.Deleted,path,null,null,null,false,true)],"after",null),pc,db,store,ct);
            var held = queue.Held(new(root,root)).Where(c=>c.Side==Side.Dropbox).ToArray();
            Check.Require(held.Select(c=>c.Observed).SequenceEqual(new Entry?[]{first,last,null}) && held.All(c=>c.ConsumeHeld!=null), "Removal lost ordered versions or the path-only tombstone");
            Check.Require(await store.CursorAsync(pair.PairId,ct)==null,"Cursor passed held removal evidence");
            foreach(var change in held) change.ConsumeHeld!();
        },pc.SettleJournalAsync,default);
        await settlement!;
        Check.Require(await store.CursorAsync(pair.PairId,default)=="after" && pc.Items.Count==0 && db.Items.Count==0,"Consumed children ran independently or the page never settled");
    }
}
