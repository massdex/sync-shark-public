namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task CheckProvenancePortAsync(string root, IStateStore store)
    {
        using var output = new StringWriter();
        var path = Path.Combine(root, "port-observations.db");
        var provenance = new SqlitePlanProvenance(path, output);
        var previous = PlanProvenance.Current.Value;
        try
        {
            PlanProvenance.Current.Value = provenance;
            await Task.Run(() =>
            {
                Require(ReferenceEquals(PlanProvenance.Current.Value, provenance), "lower provenance slot flows to child");
                PlanProvenance.Current.Value = null;
            });
            Require(ReferenceEquals(PlanProvenance.Current.Value, provenance), "child ambient change does not overwrite caller");
            provenance.BeginRun(7);
            var sample = new ItemState("first", ItemKind.File, "item", null, new string('a', 64), 1,
                DateTimeOffset.UnixEpoch, ItemFlags.None);
            // BeginRun contributes one operation; the next 4999 reach the existing 5000 flush threshold.
            for (var i = 0; i < 4999; i++)
                provenance.Visibility(Side.A, sample with { Path = "row/" + i }, false, "visible");
            using (var reader = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                reader.Open();
                using var command = reader.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM visibility";
                Require(Convert.ToInt64(command.ExecuteScalar()) == 4999, "batch threshold commits without requiring Summary");
            }
            var first = new SyncAction("first-action", 1, ActionKind.CopyNew, Side.B, "first", null, sample, null, false, null);
            var second = first with { ActionId = "second-action", Path = "second", Source = sample with { Path = "second" } };
            provenance.BeginScope("FirstScope");
            provenance.Decided(first, false);
            provenance.BeginScope("SecondScope");
            provenance.Decided(second, true);
            provenance.Final(new Plan("port-pair", 1, [second, first], []), store);
            provenance.Final(new Plan("port-pair", 1, [first, second], []), store);
            var rows = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Require(rows.Length == 3 && rows[1].Contains(",y,second,second,SecondScope,", StringComparison.Ordinal) &&
                rows[2].Contains(",n,first,first,FirstScope,", StringComparison.Ordinal),
                "final row order follows plan, retains decision scope, and deduplicates emissions");
        }
        finally
        {
            PlanProvenance.Current.Value = previous;
            provenance.Dispose();
        }
        Require(ReferenceEquals(PlanProvenance.Current.Value, previous), "caller-owned ambient restoration is preserved");
        output.WriteLine("still-owned-by-caller");
        Require(output.ToString().Contains("still-owned-by-caller", StringComparison.Ordinal), "disposal does not own output writer");
    }

    internal static async Task ProvenanceAsync()
    {
        var root = LocalChecks.FreshRoot("provenance");
        using var store = SqliteStateStore.Open(Path.Combine(root, "store"), StorageChecks.Fast, TimeProvider.System);
        await CheckProvenancePortAsync(root, store);
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
        using var provenance = new SqlitePlanProvenance(Path.Combine(root, "observations.db"), csv);
        using var overlay = new SqliteRehearsalOverlay(Path.Combine(root, "overlay.db"));
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
