using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static class PlanningProtectionChecks
{
    internal static void Run(string folder)
    {
        foreach (var scenario in new[] { "gap-release", "gap-retained", "moved-release", "retry-release", "flag-release" })
            Check(Path.Combine(folder, scenario), scenario);
        CheckIncompleteCoverage();
        Console.WriteLine("PASS immutable scope protection and real scan completeness: no false Delete/Forget");
    }

    private static void CheckIncompleteCoverage()
    {
        var a = new ItemState("Parent/File", ItemKind.File, "local-object", null, new string('a', 64),
            1, DateTimeOffset.UnixEpoch, ItemFlags.None);
        var b = a with { ObjectId = "cloud-object" };
        var basis = new Baseline("pair", "linked", a, b, 1);
        foreach (var full in new[] { false, true })
        foreach (var missingSide in new[] { Side.A, Side.B })
        foreach (var bothMissing in new[] { false, true })
        {
            var scope = new RunScope.Scope(missingSide == Side.A || bothMissing ? [] : [a],
                missingSide == Side.B || bothMissing ? [] : [b], [basis]);
            var incomplete = new SyncRun.BothScan(Scan(missingSide != Side.A, full), Scan(missingSide != Side.B, full));
            var plan = Decide(SyncRun.JoinRows(scope, incomplete, false));
            LocalChecks.Require(!plan.Actions.Any(action => action.Kind is ActionKind.Delete or ActionKind.Forget),
                $"incomplete {missingSide} full={full} bothMissing={bothMissing}: no false Delete/Forget");
            var complete = new SyncRun.BothScan(Scan(true, full), Scan(true, full));
            var valid = Decide(SyncRun.JoinRows(scope, complete, false));
            LocalChecks.Require(valid.Actions.Any(action => action.Kind == (bothMissing ? ActionKind.Forget : ActionKind.Delete)),
                $"complete {(full ? "full" : "delta")} retains ordinary absence behavior");
        }

        static SyncRun.SideScan Scan(bool complete, bool full) =>
            new(1, "cursor", complete, full, [], [], new Dictionary<string, DateTimeOffset?>(), []);
        static Plan Decide(IReadOnlyList<PlanRow> rows) => Planner.Decide("pair", 1, rows,
            new(TimeSpan.Zero, false, new("{name} [{side}]{ext}", "s", TimeSpan.Zero, "Local", "Dropbox"), 10));
    }

    private static void Check(string folder, string scenario)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        var pair = new PairDef("pair", "Pair A", new("local", SyncProviders.Local, null, "/local", "local"),
            new("cloud", SyncProviders.Dropbox, null, "/cloud", "cloud"), [], [], true);
        store.SavePair(pair);
        var a = new ItemState("Parent/File", ItemKind.File, "local-object", null, new string('a', 64),
            1, DateTimeOffset.UnixEpoch, ItemFlags.None);
        var b = a with { ObjectId = "cloud-object" };
        var currentA = scenario == "moved-release" ? a with { Path = "Moved/File" } : a;
        Snapshot(pair.A, Side.A, currentA);
        Snapshot(pair.B, Side.B, b);
        var adopt = new SyncAction("adopt", 3, ActionKind.Adopt, Side.B, b.Path, null, a, b, false, "linked");
        store.RecordIntent(pair.PairId, adopt);
        store.CommitResult(pair.PairId, 1, adopt, new(adopt.ActionId, Outcome.Done, a, b, null, null));
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (scenario == "retry-release")
            store.SaveSyncRetry(pair.PairId, new SyncRetry(a.Path, "retry", 0, "synthetic hold", DateTimeOffset.MaxValue));
        else if (scenario == "flag-release")
            store.RequireRehash(pair.A.RootId, currentA, "synthetic hold");
        else
            ((ILocalGapStore)store).BeginGap(pair.A.RootId, "cursor", 1, false, "synthetic gap");
        var links = LinkExclusions.Of(store, pair, false);
        if (scenario == "retry-release")
            links = links.WithRetryProtection(RetryProtection.Create(store, pair.PairId, active));
        var scanner = new RunScope(store, _ => true, _ => true, links,
            activeRetries: scenario == "retry-release" ? active : null);
        var calls = 0;
        scanner.AfterHideAForTest = () =>
        {
            calls++;
            if (scenario == "gap-retained") return;
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = SqliteStateStore.DatabasePath(folder), Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            // Deliberately simulate the old worker's live release, bypassing staging. The
            // scope reader must remain correct even if another writer releases every hold.
            command.CommandText = "UPDATE local_gap SET discovery_done=1; DELETE FROM local_gap_item; " +
                "UPDATE file_state SET flags=0; DELETE FROM sync_retry;";
            command.ExecuteNonQuery();
        };
        var scope = scanner.LoadFull(pair);
        LocalChecks.Require(calls == 1, scenario + ": deterministic release seam ran once");
        LocalChecks.Require(scope.A.Count == 0 && scope.B.Count == 0 && scope.Baselines.Count == 0,
            scenario + ": protected established triple is absent from all three planner inputs");
        var rows = Planner.Join(scope.Baselines, scope.A, scope.B, new Dictionary<string, DateTimeOffset?>(),
            new Dictionary<string, DateTimeOffset?>(), new(true, true, new HashSet<string>(), new HashSet<string>()), false);
        var plan = Planner.Decide(pair.PairId, 1, rows, new(TimeSpan.Zero, false,
            new("{name} [{side}]{ext}", "s", TimeSpan.Zero, "Local", "Dropbox"), 10));
        LocalChecks.Require(plan.Actions.Count == 0, scenario + ": zero Delete, Forget or side B effects");
        LocalChecks.Require(store.Baselines(pair.PairId).Count == 1,
            scenario + ": original baseline remains durable, not forgotten");

        void Snapshot(RootDef root, Side side, ItemState item)
        {
            var gen = store.BeginGeneration(root.RootId, "fixture");
            store.WriteSnapshot(new(root.RootId, side, gen, "cursor", [item], []));
            store.CompleteGeneration(gen, "cursor", true);
        }
    }
}
