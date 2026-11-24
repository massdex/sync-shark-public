using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

// Measurement branches only. Include the existing ExclusionEffectAllocationProbe for its yielding endpoint.
public static class PreviewResolutionAllocationProbe
{
    public static async Task RunAsync()
    {
        foreach (var suspended in new[] { false, true })
        {
            await PreviewAsync(4, suspended);
            await ResolutionAsync(2, suspended);
            foreach (var size in new[] { 100, 200 })
            {
                await PreviewAsync(size, suspended);
                await ResolutionAsync(size, suspended);
            }
        }
    }

    private static async Task PreviewAsync(int size, bool suspended)
    {
        var pair = ItemChecks.Pair();
        var exclusion = new Exclusion(Side.Local, "dir", false);
        var scope = new PathScope(pair, [pair], DriveType.Fixed);
        using var store = new InMemoryStateStore();
        var local = new FakeSide(Side.Local);
        var remote = new FakeSide(Side.Dropbox);
        local.Seed(ItemChecks.File(Side.Local, "dir", 0, "root", true), []);
        remote.Seed(ItemChecks.File(Side.Dropbox, "dir", 0, "root", true), []);
        for (var i = 0; i < size; i++)
        {
            var path = $"dir/{i}";
            var folder = i % 2 == 0;
            local.Seed(ItemChecks.File(Side.Local, path, 1, path, folder), folder ? [] : [1]);
            // Equal-size files require a hash; unequal-size files with unreliable times select KeepBoth.
            var peer = ItemChecks.File(Side.Dropbox, path, 2, path, folder);
            if (i % 4 == 3) peer = peer with { Size = 2 };
            remote.Seed(peer, folder ? [] : peer.Size == 2 ? [2, 2] : [2]);
        }
        ISide peerSide = suspended ? ExclusionEffectAllocationProbe.YieldingSide.Over(remote) : remote;
        var exclusions = new Exclusions();
        var start = GC.GetTotalAllocatedBytes(true);
        var pending = exclusions.PreviewAsync(pair, exclusion, local, peerSide, store, scope, default);
        var observedSuspend = !pending.IsCompleted;
        var result = await pending;
        var bytes = GC.GetTotalAllocatedBytes(true) - start;
        Check.Require(result.CopyFiles == size / 2 && result.RemoveFiles == size / 2 && !result.BlockedByProtected &&
            local.Requests[nameof(ISide.ContentHashAsync)] == 0 && local.Mutations.Count + remote.Mutations.Count == 0,
            "Preview did not evaluate the mixed tree without hashing or mutation");
        Console.WriteLine($"PROBE preview n={size} yield={suspended} incomplete={observedSuspend} bytes={bytes} copies={result.CopyFiles} reads={remote.Requests[nameof(ISide.ReadOneAsync)]} finds={store.Requests[nameof(IStore.FindAsync)]}");
    }

    private static async Task ResolutionAsync(int clicks, bool suspended)
    {
        long bytes = 0, reads = 0, opens = 0, finds = 0;
        var incomplete = 0;
        for (var i = 0; i < clicks; i++)
        {
            var pair = ItemChecks.Pair();
            var scope = new PathScope(pair, [pair], DriveType.Fixed);
            using var store = new InMemoryStateStore();
            var local = new FakeSide(Side.Local);
            var remote = new FakeSide(Side.Dropbox);
            var pc = ItemChecks.File(Side.Local, "p", 1, "pc");
            var db = ItemChecks.File(Side.Dropbox, "p", 2, "db");
            local.Seed(pc, [1]); remote.Seed(db, [2]);
            await store.SaveNoticeAsync(pair.PairId, new(0, NoticeKind.Tie, TextKey.TieEqualTimes, "p",
                new([new(Side.Local, "p", pc.Id), new(Side.Dropbox, "p", db.Id)], null), DateTimeOffset.UnixEpoch, false), default);
            var reviewed = store.OpenNotices.Single();
            local.ContentFailures["p"] = new ItemFailedException("p", TextKey.ReadLocalFailed, null);
            ISide source = suspended ? ExclusionEffectAllocationProbe.YieldingSide.Over(local) : local;
            var resolver = new Resolve();
            var queue = new Massdex.SyncShark.Engine.Run.WorkQueue();
            var populate = ItemChecks.Population(pair, scope, local, remote, store);
            var hop = ItemChecks.Hop(pair, scope, queue, local, remote, store);
            var start = GC.GetTotalAllocatedBytes(true);
            var pending = resolver.ApplyAsync(pair, ItemChecks.Settings, _ => { }, scope, reviewed,
                ResolutionChoice.UseLocal, null, source, remote, store, populate, hop, default, queue.Held);
            if (!pending.IsCompleted) incomplete++;
            var outcome = await pending;
            bytes += GC.GetTotalAllocatedBytes(true) - start;
            Check.Require(outcome == EffectOutcome.Refused && store.OpenNotices.Contains(reviewed) &&
                store.OpenNotices.Any(n => n.Kind == NoticeKind.Failure && n.Subject.Incoming == Side.Local) &&
                local.Requests[nameof(ISide.OpenReadAsync)] > 0 && remote.Items["p"].Entry == db,
                "Click did not reach the failed effect and retain the reviewed notice and peer");
            reads += local.Requests[nameof(ISide.ReadOneAsync)] + remote.Requests[nameof(ISide.ReadOneAsync)];
            opens += local.Requests[nameof(ISide.OpenReadAsync)];
            finds += store.Requests[nameof(IStore.FindAsync)];
        }
        // Task.Yield forces the read continuation off-stack; completion may still race the caller's observation.
        Console.WriteLine($"PROBE resolve n={clicks} yield={suspended} incomplete={incomplete} bytes={bytes} reads={reads} opens={opens} finds={finds}");
    }
}
