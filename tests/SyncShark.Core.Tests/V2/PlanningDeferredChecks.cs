namespace Massdex.SyncShark.V2;

internal static class PlanningDeferredChecks
{
    internal static void Run(string folder)
    {
        var pair = new PairDef("pair", "Pair A", new("local", SyncProviders.Local, null, "/local", "local"),
            new("cloud", SyncProviders.Dropbox, null, "/cloud", "cloud"), [], [], true);
        var local = new ItemState("Held/File", ItemKind.File, "local-file", null, new string('a', 64),
            1, DateTimeOffset.UnixEpoch, ItemFlags.None);
        var cloud = local with { ObjectId = "cloud-file", Hash = new string('b', 64) };
        var added = cloud with { Path = "New/File", ObjectId = "cloud-new" };
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
        {
            store.SavePair(pair);
            Snapshot(store, pair.A, Side.A, [local]);
            Snapshot(store, pair.B, Side.B, [cloud, added]);
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(pair.A.RootId, "cursor", 1, false, "synthetic gap");
            var links = LinkExclusions.Of(store, pair, false);
            var scope = new RunScope(store, _ => true, _ => true, links).LoadTouched(pair,
                [cloud.Path, added.Path], [], [cloud.ObjectId, added.ObjectId]);
            LocalChecks.Require(scope.B.Any(item => item.ObjectId == added.ObjectId) &&
                !scope.B.Any(item => item.ObjectId == cloud.ObjectId),
                "unpublished local gap leaves cloud-only addition visible, but hides colliding change");
            var rows = Planner.Join(scope.Baselines, scope.A, scope.B,
                new Dictionary<string, DateTimeOffset?>(), new Dictionary<string, DateTimeOffset?>(),
                new(true, true, new HashSet<string>(), new HashSet<string>()), false);
            var plan = Planner.Decide(pair.PairId, 1, rows, new(TimeSpan.Zero, false,
                new("{name} [{side}]{ext}", "s", TimeSpan.Zero, "Local", "Dropbox"), 10));
            LocalChecks.Require(plan.Actions.Any(action => action.Path == added.Path && action.Kind == ActionKind.CopyNew),
                "incremental cloud-only file is planned during an unpublished local gap");
            LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == 2,
                "both hidden entities have durable item-owned work before cursor advancement");
            var advanced = store.BeginGeneration(pair.B.RootId, "delta");
            store.WriteDelta(new(pair.B.RootId, Side.B, advanced, "cursor", "advanced", [], false, []));
            store.CompleteGeneration(advanced, "advanced", true);
        }
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
        {
            var deferred = store.DeferredPlanningPage(pair.PairId);
            LocalChecks.Require(store.Cursor(pair.B.RootId) == "advanced" && deferred.Count == 2,
                "cursor advance and restart retain hidden entities for the next run");
            var gaps = (ILocalGapStore)store;
            var epoch = gaps.GapEpoch(pair.A.RootId)!;
            gaps.GapDiscoveredBatch(pair.A.RootId, epoch, new[] { (local, false) });
            gaps.GapDiscoveryDone(pair.A.RootId, epoch);
            gaps.PromoteGapDiscovery(pair.A.RootId);
            var paths = deferred.SelectMany(item => item.Paths).ToArray();
            var ids = deferred.Select(item => item.ObjectId).ToArray();
            var scope = new RunScope(store, _ => true, _ => true, LinkExclusions.Of(store, pair, false))
                .LoadTouched(pair, paths, ids, ids);
            LocalChecks.Require(scope.A.Any(item => item.ObjectId == local.ObjectId) &&
                scope.B.Any(item => item.ObjectId == cloud.ObjectId),
                "with no new provider delta, publication re-admits the hidden change from durable work");
            store.AcknowledgeDeferredPlanning(pair.PairId, deferred, _ => false);
            LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == 2, "unsettled work stays dirty");
            store.DeferPlanning(pair.PairId, new[] { (pair.B.RootId, cloud) });
            store.AcknowledgeDeferredPlanning(pair.PairId, deferred, _ => true);
            LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == 1,
                "a new hold cannot be cleared by an older acknowledgement page");
            store.AcknowledgeDeferredPlanning(pair.PairId, store.DeferredPlanningPage(pair.PairId), _ => true);
            LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == 0, "settled current entity work retires");
        }

        static void Snapshot(SqliteStateStore store, RootDef root, Side side, IReadOnlyList<ItemState> items)
        {
            var gen = store.BeginGeneration(root.RootId, "fixture");
            store.WriteSnapshot(new(root.RootId, side, gen, "cursor", items, []));
            store.CompleteGeneration(gen, "cursor", true);
        }
    }
}
