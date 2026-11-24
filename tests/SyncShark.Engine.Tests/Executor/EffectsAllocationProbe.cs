using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Measurement only (unmerged probe branch): bytes allocated per item by one Effects.ApplyAsync on each common path.</summary>
public static class EffectsAllocationProbe
{
    public static async Task RunAsync()
    {
        var lines = new List<string>();
        foreach (var kind in new[] { "none", "record", "upload", "delete", "move" })
        {
            await MeasureAsync(kind, 200);
            var small = await MeasureAsync(kind, 1000);
            var large = await MeasureAsync(kind, 2000);
            lines.Add($"{kind} thread/item={(large.Thread - small.Thread) / 1000.0:F1} total/item={(large.Total - small.Total) / 1000.0:F1} outcomes={large.Outcome}");
        }
        throw new InvalidOperationException("PROBE " + string.Join(" | ", lines));
    }

    private static async Task<(long Thread, long Total, string Outcome)> MeasureAsync(string kind, int count)
    {
        var pc = new FakeSide(Side.Local); var db = new FakeSide(Side.Dropbox);
        using var store = new InMemoryStateStore(); var pair = ItemChecks.Pair();
        var paths = new PathScope(pair, [pair], DriveType.Fixed);
        var actions = new List<SyncAction>(count);
        for (var i = 0; i < count; i++)
        {
            var a = ItemChecks.File(Side.Local, $"{kind}{i:D5}", 7, $"pc{i}");
            var b = ItemChecks.File(Side.Dropbox, a.Path, 7, $"db{i}");
            switch (kind)
            {
                case "none": pc.Seed(a, [7]); actions.Add(new(ActionKind.None, new(a.Path, a, null, null, [], a.ContentHash, RowContext.Neutral), NoneReason.Unfinished)); break;
                case "record": pc.Seed(a, [7]); db.Seed(b, [7]); actions.Add(new(ActionKind.Record, new(a.Path, a, b, null, [], a.ContentHash, RowContext.Neutral))); break;
                case "upload": pc.Seed(a, [7]); actions.Add(new(ActionKind.Upload, new(a.Path, a, null, null, [], a.ContentHash, RowContext.Neutral))); break;
                case "delete":
                {
                    db.Seed(b, [7]); var bound = SyncedEntries.From(a, b);
                    await store.CommitAsync(pair.PairId, null, bound, null, null, default);
                    actions.Add(new(ActionKind.DeleteDropbox, new(bound.LocalKey, null, b, bound, [], null, RowContext.Neutral)));
                    break;
                }
                default:
                {
                    var moved = b with { Path = $"moved{i:D5}" };
                    pc.Seed(a, [7]); db.Seed(moved, [7]); var bound = SyncedEntries.From(a, b);
                    await store.CommitAsync(pair.PairId, null, bound, null, null, default);
                    actions.Add(new(ActionKind.MoveLocal, new(bound.LocalKey, a, moved, bound, [], null, RowContext.Neutral), Target: moved.Path));
                    break;
                }
            }
        }
        var effects = new Effects();
        var outcomes = new Dictionary<string, int>();
        var thread = GC.GetAllocatedBytesForCurrentThread();
        var total = GC.GetTotalAllocatedBytes(true);
        foreach (var action in actions)
        {
            string outcome;
            try { outcome = (await effects.ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, action, pc, db, store, (_, _, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask, default)).ToString(); }
            catch (Exception error) { outcome = error.GetType().Name; }
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
        }
        thread = GC.GetAllocatedBytesForCurrentThread() - thread;
        total = GC.GetTotalAllocatedBytes(true) - total;
        return (thread, total, string.Join(",", outcomes.Select(o => $"{o.Key}:{o.Value}")));
    }
}
