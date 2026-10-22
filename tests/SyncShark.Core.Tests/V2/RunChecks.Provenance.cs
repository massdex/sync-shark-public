namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task ProvenanceAsync()
    {
        var root = LocalChecks.FreshRoot("provenance");
        using var store = SqliteStateStore.Open(Path.Combine(root, "store"), StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("same.txt", "same");
        fx.A.Seed("held.txt", "held");
        await FirstSyncAsync(fx.Run(), fx);
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        foreach (var side in new[] { Side.A, Side.B })
        {
            var endpoint = side == Side.A ? fx.A : fx.B;
            var pairRoot = side == Side.A ? fx.Pair.A : fx.Pair.B;
            var gen = store.BeginGeneration(pairRoot.RootId, "fixture");
            var snapshot = await endpoint.FullAsync(gen, _ => true, null!, default);
            store.WriteSnapshot(snapshot);
            store.CompleteGeneration(gen, snapshot.CursorAfter, true);
        }
        fx.A.Seed("incoming.txt", "incoming");
        using var csv = new StringWriter();
        using var provenance = new PlanProvenance(Path.Combine(root, "observations.db"), csv);
        using var overlay = new RehearsalOverlay(Path.Combine(root, "overlay.db"));
        PlanProvenance.Current.Value = provenance;
        try
        {
            provenance.BeginRun(1);
            var scanner = new RunScope(store, _ => true, _ => true, LinkExclusions.None.WithPending(["held.txt"]));
            scanner.LoadFull(fx.Pair);
            var counts = new List<string>();
            provenance.Summary(counts.Add);
            foreach (var count in counts) Console.WriteLine(count);
            Require(counts.Any(line => line.Contains("raw-same-path-pairs=2", StringComparison.Ordinal)) &&
                counts.Any(line => line.Contains("both-hidden=1", StringComparison.Ordinal)),
                "provenance counts actual frozen production Hide decisions");
            provenance.BeginRun(2);
            var endpoints = new RehearsalEndpoints(fx.Provider, overlay, _ => { });
            await fx.Run().Rehearsal(endpoints).IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            var text = csv.ToString();
            Require(text.Contains("CopyNew", StringComparison.Ordinal) && text.Contains("incoming.txt", StringComparison.Ordinal) &&
                text.Contains("Touched", StringComparison.Ordinal), "provenance streams final executed production action with scope");
            Require(text.Contains(",n,incoming.txt,incoming.txt,Touched,", StringComparison.Ordinal),
                "final action carries the production row's absent baseline");
            counts.Clear();
            provenance.Summary(counts.Add);
            Require(counts.Any(line => line.Contains("one-visible:A:opposite-absent-in-raw-scope=1", StringComparison.Ordinal)),
                "one-sided visibility distinguishes actual absence from hidden counterparts");
            Require(text.Split('\n').Count(line => line.Contains(",CopyNew,", StringComparison.Ordinal)) == 1,
                "provenance emits one row per final action, not hash or occupancy planning passes");
            provenance.BeginRun(3);
            var sample = (await fx.A.ObserveAsync("same.txt", default))!;
            for (var index = 0; index < 6000; index++)
            {
                var item = sample with { Path = "batch/" + index };
                provenance.Visibility(Side.A, item, false, "visible");
                provenance.Visibility(Side.B, item, true, "other-protection");
            }
            counts.Clear();
            provenance.Summary(counts.Add);
            Require(counts.Any(line => line.Contains("raw-same-path-pairs=6000", StringComparison.Ordinal)) &&
                counts.Any(line => line.Contains("one-visible-total=6000", StringComparison.Ordinal)),
                "provenance preserves counts across bounded transaction flushes and a partial final batch");
        }
        finally { PlanProvenance.Current.Value = null; }
        Console.WriteLine("PASS opt-in production provenance fixture");
    }
}
