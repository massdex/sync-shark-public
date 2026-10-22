using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static class PlanningProtectionChecks
{
    internal static void Run(string folder)
    {
        foreach (var scenario in new[] { "gap-release", "gap-retained", "moved-release", "retry-release", "flag-release", "flag-moved-a", "flag-moved-b" })
            Check(Path.Combine(folder, scenario), scenario);
        foreach (var side in new[] { Side.A, Side.B })
            CheckUnbaselined(Path.Combine(folder, "unbaselined-" + side), side);
        CheckRetryGate(Path.Combine(folder, "retry-gate"));
        CheckIncompleteCoverage();
        PlanningDeferredChecks.Run(Path.Combine(folder, "planning-deferred"));
        ProtectionStreamChecks.Run(Path.Combine(folder, "protection-stream"));
        BlockedDeleteNoticeChecks.Run(Path.Combine(folder, "blocked-delete-notices"));
        DeleteReviewNoticeChecks.Run(Path.Combine(folder, "delete-review-notices"));
        Console.WriteLine("PASS immutable scope protection and real scan completeness: no false Delete/Forget");
    }

    private static void CheckUnbaselined(string folder, Side hiddenSide)
    {
        try
        {
            using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
            var pair = new PairDef("pair", "Pair A", new("local", SyncProviders.Local, null, "/local", "local"),
                new("cloud", SyncProviders.Dropbox, null, "/cloud", "cloud"), [], [], true);
            store.SavePair(pair);
            var a = Enumerable.Range(0, 512).Select(n => new ItemState($"file-{n}.txt", ItemKind.File,
                "local-" + n, null, new string('a', 64), 1, DateTimeOffset.UnixEpoch, ItemFlags.None)).ToArray();
            var b = a.Select((item, n) => item with { ObjectId = "cloud-" + n }).ToArray();
            foreach (var side in new[] { Side.A, Side.B })
            {
                var root = side == Side.A ? pair.A : pair.B;
                var gen = store.BeginGeneration(root.RootId, "fixture");
                store.WriteSnapshot(new(root.RootId, side, gen, "cursor", side == Side.A ? a : b, []));
                store.CompleteGeneration(gen, "cursor", true);
            }
            var hiddenRoot = hiddenSide == Side.A ? pair.A : pair.B;
            ((ILocalGapStore)store).BeginGap(hiddenRoot.RootId, "cursor", 1, false, "fixture gap");
            RunScope Scanner() => new(store, _ => true, _ => true, LinkExclusions.Of(store, pair, false));
            var scope = Scanner().LoadFull(pair);
            LocalChecks.Require(scope.A.Count == 0 && scope.B.Count == 0 && Decide(scope).Actions.Count == 0,
                "unbaselined hidden " + hiddenSide + " cannot produce a counterpart CopyNew");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = SqliteStateStore.DatabasePath(folder), Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE local_gap SET discovery_done=1; DELETE FROM local_gap_item;";
                command.ExecuteNonQuery();
            }
            var visible = Decide(Scanner().LoadFull(pair));
            LocalChecks.Require(visible.Actions.Count == 512 && visible.Actions.All(action => action.Kind == ActionKind.Adopt),
                "released unbaselined identical files Adopt on both-visible run, never CopyNew");
            Plan Decide(RunScope.Scope scope) => Planner.Decide(pair.PairId, 1,
                Planner.Join(scope.Baselines, scope.A, scope.B, new Dictionary<string, DateTimeOffset?>(),
                    new Dictionary<string, DateTimeOffset?>(), new(true, true, new HashSet<string>(), new HashSet<string>()), false),
                new(TimeSpan.Zero, false, new("{name} [{side}]{ext}", "s", TimeSpan.Zero, "Local", "Dropbox"), 10));
        }
        finally { LocalJournalChecks.RemoveFixture(folder); }
    }

    private static void CheckRetryGate(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        var pair = new PairDef("pair", "Pair A", new("local", SyncProviders.Local, null, "/local", "local"),
            new("cloud", SyncProviders.Dropbox, null, "/cloud", "cloud"), [], [], true);
        store.SavePair(pair);
        var item = new ItemState("Parent/File", ItemKind.File, "local-file", null, new string('a', 64),
            1, DateTimeOffset.UnixEpoch, ItemFlags.None);
        var gen = store.BeginGeneration(pair.A.RootId, "fixture");
        store.WriteSnapshot(new(pair.A.RootId, Side.A, gen, "cursor", [item], []));
        store.CompleteGeneration(gen, "cursor", true);
        gen = store.BeginGeneration(pair.B.RootId, "fixture");
        store.WriteSnapshot(new(pair.B.RootId, Side.B, gen, "cursor", [item with { ObjectId = "cloud-file" }], []));
        store.CompleteGeneration(gen, "cursor", true);
        var calls = 0;
        RunScope Scanner() => new(store, _ => true, _ => true, LinkExclusions.Of(store, pair, false),
            activeRetries: new HashSet<string>(), restoreProtection: RecoveryRestoreProtection.None)
            { RetryProtectionReadForTest = () => calls++ };
        var scanner = Scanner();
        scanner.AfterHideAForTest = () => store.SaveSyncRetry(pair.PairId,
            new(item.Path, "sync", 0, "created after snapshot", DateTimeOffset.MaxValue));
        var frozen = scanner.LoadFull(pair);
        LocalChecks.Require(frozen.A.Count == 1 && frozen.B.Count == 1 && calls == 0,
            "empty frozen retry snapshot makes zero per-path calls, including a mid-prepare writer");
        var next = Scanner().LoadFull(pair);
        LocalChecks.Require(next.A.Count == 0 && next.B.Count == 0 && calls == 0,
            "next snapshot sees the new retry and protects its exact path without per-path SQL");
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

        foreach (var missingSide in new[] { Side.A, Side.B })
        {
            var heldA = a with { Path = "Held/File", ObjectId = "held-local" };
            var heldB = b with { Path = "Held/File", ObjectId = "held-cloud" };
            var scope = new RunScope.Scope(missingSide == Side.A ? [] : [a, heldA],
                missingSide == Side.B ? [] : [b, heldB],
                [basis, new Baseline("pair", "held-linked", heldA, heldB, 1)]);
            foreach (var scenario in new[] { "scoped-full", "root-gap", "unexplained", "incomplete-delta", "hydrated-delta" })
            {
                var missing = Scan(false, scenario != "incomplete-delta") with
                {
                    ScopedFullCoverage = scenario == "scoped-full",
                    IncompleteSubtrees = scenario == "root-gap" ? [""] :
                        scenario == "unexplained" ? [] : ["Held"]
                };
                var scan = missingSide == Side.A ? new SyncRun.BothScan(missing, Scan(true, false)) :
                    new SyncRun.BothScan(Scan(true, false), missing);
                var plan = Decide(SyncRun.JoinRows(scope, scan, false));
                LocalChecks.Require(!plan.Actions.Any(action => action.Path == "Held/File" &&
                    action.Kind is ActionKind.Delete or ActionKind.Forget),
                    scenario + ": the incomplete subtree never gains absence authority on " + missingSide);
                LocalChecks.Require(scenario == "scoped-full"
                    ? plan.Actions.Any(action => action.Path == a.Path && action.Kind == ActionKind.Delete)
                    : !plan.Actions.Any(action => action.Kind is ActionKind.Delete or ActionKind.Forget),
                    scenario + ": only a real scoped full observation authorizes sibling absence on " + missingSide);
            }
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
        var currentA = scenario is "moved-release" or "flag-moved-a" ? a with { Path = "Moved/File" } : a;
        var currentB = scenario == "flag-moved-b" ? b with { Path = "Moved/File" } : b;
        Snapshot(pair.A, Side.A, currentA);
        Snapshot(pair.B, Side.B, currentB);
        var adopt = new SyncAction("adopt", 3, ActionKind.Adopt, Side.B, b.Path, null, a, b, false, "linked");
        store.RecordIntent(pair.PairId, adopt);
        store.CommitResult(pair.PairId, 1, adopt, new(adopt.ActionId, Outcome.Done, a, b, null, null));
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (scenario == "retry-release")
            store.SaveSyncRetry(pair.PairId, new SyncRetry(a.Path, "retry", 0, "synthetic hold", DateTimeOffset.MaxValue));
        else if (scenario is "flag-release" or "flag-moved-a" or "flag-moved-b")
            store.RequireRehash(scenario == "flag-moved-b" ? pair.B.RootId : pair.A.RootId,
                scenario == "flag-moved-b" ? currentB : currentA, "synthetic hold");
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
