using Microsoft.Data.Sqlite;

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
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(pair);
            var initialIds = new List<string>();
            var allChanged = false;
            var gen = store.BeginGeneration(pair.A.RootId, "fixture");
            store.WriteSnapshot(new(pair.A.RootId, Side.A, gen, "cursor", [local], [])
            { ChangedObject = initialIds.Add, AllItemsChanged = () => allChanged = true });
            store.CompleteGeneration(gen, "cursor", true);
            LocalChecks.Require(allChanged && initialIds.Count == 0, "initial root snapshot reports all-changed without collecting identities");
            Snapshot(store, pair.B, Side.B, [cloud, added]);
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(pair.A.RootId, "cursor", 1, false, "synthetic gap");
            var links = LinkExclusions.Of(store, pair, false);
            var scope = new RunScope(store, _ => true, _ => true, links, persistDeferred: true).LoadTouched(pair,
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
            LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == 1,
                "only the changed hidden cloud entity is durable; the unchanged local entity has no row");
            var advanced = store.BeginGeneration(pair.B.RootId, "delta");
            store.WriteDelta(new(pair.B.RootId, Side.B, advanced, "cursor", "advanced", [], false, []));
            store.CompleteGeneration(advanced, "advanced", true);
        }
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var deferred = store.AdmitDeferredPlanning(pair.PairId, DateTime.UtcNow.Ticks, rotate: false);
            LocalChecks.Require(store.Cursor(pair.B.RootId) == "advanced" && deferred.Count == 1,
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
            var unsettled = store.AdmitDeferredPlanning(pair.PairId, DateTime.UtcNow.Ticks, rotate: false);
            LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == 2 &&
                unsettled.Any(row => row.RootId == pair.A.RootId && row.ObjectId == local.ObjectId) &&
                unsettled.Any(row => row.RootId == pair.B.RootId && row.ObjectId == cloud.ObjectId),
                "unsettled cloud work and the atomic local promotion handoff both stay dirty");
            store.DeferPlanning(pair.PairId, new[] { (pair.B.RootId, cloud) });
            store.AcknowledgeDeferredPlanning(pair.PairId, deferred, _ => true);
            var newer = store.AdmitDeferredPlanning(pair.PairId, DateTime.UtcNow.Ticks, rotate: false);
            LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == 2 &&
                newer.Any(row => row.RootId == pair.A.RootId && row.ObjectId == local.ObjectId) &&
                newer.Any(row => row.RootId == pair.B.RootId && row.ObjectId == cloud.ObjectId),
                "a new hold and the local handoff cannot be cleared by an older acknowledgement page");
            store.AcknowledgeDeferredPlanning(pair.PairId, store.AdmitDeferredPlanning(pair.PairId, DateTime.UtcNow.Ticks, rotate: false), _ => true);
            LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == 0, "settled current entity work retires");
            var heldLinks = LinkExclusions.Of(store, pair, false).WithPending([cloud.Path]);
            new RunScope(store, _ => true, _ => true, heldLinks, persistDeferred: true)
                .LoadTouched(pair, [cloud.Path], [], [cloud.ObjectId]);
            LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == 0,
                "ongoing decision holds never create planning deferrals");
            var retryLinks = LinkExclusions.Of(store, pair, false).WithRetryProtection(
                (path, descendants) => PairPath.IsSelfOrUnder(path, "Held"));
            var retryScope = new RunScope(store, _ => true, _ => true, retryLinks,
                restoreProtection: RecoveryRestoreProtection.None, persistDeferred: true)
                .LoadTouched(pair, [cloud.Path], [], [cloud.ObjectId]);
            LocalChecks.Require(retryScope.A.Count == 0 && retryScope.B.Count == 0 && store.CountDeferredPlanning(pair.PairId) == 0,
                "a retry-only hold without a gap does not create a misleading verification row");
            store.RequireRehash(pair.A.RootId, local, "preview hold");
            new RunScope(store, _ => true, _ => true, LinkExclusions.Of(store, pair, false))
                .LoadTouched(pair, [cloud.Path], [], [cloud.ObjectId]);
            LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == 0,
                "preview preparation is read-only even for a changed hidden item");
            store.SaveSyncRetry(pair.PairId, new(cloud.Path, "sync", 0, "existing retry", DateTimeOffset.MinValue));
            new RunScope(store, _ => true, _ => true, LinkExclusions.Of(store, pair, false), persistDeferred: true)
                .LoadTouched(pair, [cloud.Path], [], [cloud.ObjectId]);
            LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == 0,
                "an existing retry is not duplicated as planning debt");
            store.ClearSyncRetry(pair.PairId, cloud.Path);
            var many = Enumerable.Range(0, 1025).Select(n => (pair.B.RootId,
                cloud with { ObjectId = $"deferred-{n:D6}", Path = $"Pending/{n}" }));
            store.DeferPlanning(pair.PairId, many);
            var drained = 0;
            while (store.CountDeferredPlanning(pair.PairId) > 0)
            {
                var page = store.AdmitDeferredPlanning(pair.PairId, DateTime.UtcNow.Ticks);
                drained += page.Count;
                LocalChecks.Require(page.Count <= 256, "drain memory stays page bounded");
                store.AcknowledgeDeferredPlanning(pair.PairId, page, _ => true);
            }
            LocalChecks.Require(drained == 1025 && store.CountDeferredPlanning(pair.PairId) == 0,
                "bounded repeated run intakes visit all due work, not just the first 256 (RQ-032/140)");
            using var connection = new SqliteConnection($"Data Source={SqliteStateStore.DatabasePath(folder)};Pooling=False");
            connection.Open();
            AssertSeek(connection);
            StoreSchema.Execute(connection, null, "CREATE TRIGGER fail_defer BEFORE INSERT ON planning_deferred BEGIN SELECT RAISE(FAIL,'synthetic deferral failure'); END;");
            var retained = new RunScope(store, _ => true, _ => true, LinkExclusions.Of(store, pair, false), persistDeferred: true)
                .LoadTouched(pair, [cloud.Path], [], [cloud.ObjectId]);
            LocalChecks.Require(retained.B.Count == 0 && store.SyncRetryAt(pair.PairId, cloud.Path) is not null,
                "deferral failure retains the hold and saves existing durable retry work without throwing");
            store.ClearSyncRetry(pair.PairId, cloud.Path);
            StoreSchema.Execute(connection, null, "CREATE TRIGGER fail_retry BEFORE INSERT ON sync_retry BEGIN SELECT RAISE(FAIL,'synthetic retry failure'); END;");
            // C06 / run.md: a failed durable handoff must leave preparation through
            // the run failure owner before cursor advancement; RunScope cannot swallow it.
            var cursorBefore = store.Cursor(pair.B.RootId);
            SqliteException? persistenceFailure = null;
            try
            {
                new RunScope(store, _ => true, _ => true, LinkExclusions.Of(store, pair, false), persistDeferred: true)
                    .LoadTouched(pair, [cloud.Path], [], [cloud.ObjectId]);
            }
            catch (SqliteException error) { persistenceFailure = error; }
            LocalChecks.Require(persistenceFailure?.Message.Contains("synthetic retry failure") == true &&
                store.Cursor(pair.B.RootId) == cursorBefore && store.SyncRetryAt(pair.PairId, cloud.Path) is null,
                "double persistence failure propagates without advancing cursor or claiming durable retry");
        }

        // Review 4 finding 3: retirement through the empty-path branch is structural, not an alias guess.
        using (var store = SqliteStateStore.Open(Path.Combine(folder, "moved-empty-path"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(pair);
            Snapshot(store, pair.A, Side.A, [local]);
            Snapshot(store, pair.B, Side.B, [cloud]);
            var moved = local with { Path = "Hidden/Moved", ObjectId = "moved-object" };
            store.DeferPlanning(pair.PairId, new[] { (pair.A.RootId, moved) });
            var stale = store.AdmitDeferredPlanning(pair.PairId, DateTime.UtcNow.Ticks, rotate: false).Single(row => row.ObjectId == "moved-object");
            LocalChecks.Require(stale.Paths.SequenceEqual(new[] { "Hidden/Moved" }),
                "the queued row captures its alias-free path before a later move");
            // The entity appears on its root at a path this run never evaluated, after the aliases were read.
            Snapshot(store, pair.A, Side.A, [local, moved]);
            store.AcknowledgeDeferredPlanning(pair.PairId, [stale], _ => false, _ => true);
            LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == 1,
                "an empty-path sighting never retires work while the entity still has a row on its root");
            Snapshot(store, pair.A, Side.A, [local]);
            store.AcknowledgeDeferredPlanning(pair.PairId, [stale], _ => false, _ => true);
            LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == 0,
                "a genuinely absent entity retires its empty-path work");
        }

        var migratedFolder = Path.Combine(folder, "migrated-v21");
        using (var old = SqliteStateStore.Open(migratedFolder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            old.SavePair(pair);
            Snapshot(old, pair.A, Side.A, [local]);
        }
        using (var connection = new SqliteConnection($"Data Source={SqliteStateStore.DatabasePath(migratedFolder)};Pooling=False"))
        {
            connection.Open();
            StoreSchema.Execute(connection, null, "DROP TABLE planning_deferred; PRAGMA user_version=21;");
        }
        using (var migrated = SqliteStateStore.Open(migratedFolder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            migrated.DeferPlanning(pair.PairId, new[] { (pair.A.RootId, local) });
            LocalChecks.Require(migrated.AdmitDeferredPlanning(pair.PairId, DateTime.UtcNow.Ticks, rotate: false).Single().Reason.Length > 0,
                "v21 migration creates usable reason-bearing deferrals");
            using var connection = new SqliteConnection($"Data Source={SqliteStateStore.DatabasePath(migratedFolder)};Pooling=False");
            connection.Open();
            AssertSeek(connection);
        }

        // A store created by the earlier unshipped v22 must preserve queued work and
        // heal idempotently, without rebuilding domain tables or changing schema version.
        using (var connection = new SqliteConnection($"Data Source={SqliteStateStore.DatabasePath(migratedFolder)};Pooling=False"))
        {
            connection.Open();
            StoreSchema.Execute(connection, null, "DROP INDEX planning_deferred_pair;" +
                "ALTER TABLE planning_deferred ADD COLUMN last_attempt INTEGER NOT NULL DEFAULT 0;" +
                "CREATE INDEX planning_deferred_due ON planning_deferred(pair_no,last_attempt,root_no,object_id);");
        }
        for (var open = 0; open < 2; open++)
        {
            using var migrated = SqliteStateStore.Open(migratedFolder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            LocalChecks.Require(migrated.CountDeferredPlanning(pair.PairId) == 1, "v22 index migration preserves queued entities on repeated opens");
            migrated.DeferPlanning(pair.PairId, new[] { (pair.A.RootId, local) });
            using var connection = new SqliteConnection($"Data Source={SqliteStateStore.DatabasePath(migratedFolder)};Pooling=False");
            connection.Open();
            AssertSeek(connection);
            LocalChecks.Require(Convert.ToInt64(StoreSchema.Scalar(connection, null,
                "SELECT COUNT(*) FROM pragma_table_info('planning_deferred') WHERE name='last_attempt';")) == 0,
                "unshipped deferral scheduling column is removed");
        }

        using (var store = SqliteStateStore.Open(Path.Combine(folder, "hidden-delete"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(pair);
            var baselineCloud = cloud with { Hash = local.Hash };
            Snapshot(store, pair.A, Side.A, [local]);
            Snapshot(store, pair.B, Side.B, [baselineCloud]);
            var adopt = new SyncAction("adopt", 3, ActionKind.Adopt, Side.B, cloud.Path, null, local, baselineCloud, false, "linked");
            store.RecordIntent(pair.PairId, adopt);
            store.CommitResult(pair.PairId, 1, adopt, new(adopt.ActionId, Outcome.Done, local, baselineCloud, null, null));
            var edited = local with { Hash = new string('c', 64), Size = 2 };
            Snapshot(store, pair.A, Side.A, [edited]);
            Snapshot(store, pair.B, Side.B, []);
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(pair.A.RootId, "cursor", 1, false, "hidden delete");
            new RunScope(store, _ => true, _ => true, LinkExclusions.Of(store, pair, false), persistDeferred: true)
                .LoadTouched(pair, [cloud.Path], [], [cloud.ObjectId]);
            LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == 1,
                "a deleted hidden entity remains dirty through its baseline identity");
            var epoch = gaps.GapEpoch(pair.A.RootId)!;
            gaps.GapDiscoveredBatch(pair.A.RootId, epoch, new[] { (edited, false) });
            gaps.GapDiscoveryDone(pair.A.RootId, epoch);
            gaps.PromoteGapDiscovery(pair.A.RootId);
            var deferred = store.AdmitDeferredPlanning(pair.PairId, DateTime.UtcNow.Ticks, rotate: false);
            var scope = new RunScope(store, _ => true, _ => true, LinkExclusions.Of(store, pair, false))
                .LoadTouched(pair, deferred.SelectMany(item => item.Paths).ToArray(), [], [cloud.ObjectId]);
            var rows = Planner.Join(scope.Baselines, scope.A, scope.B,
                new Dictionary<string, DateTimeOffset?>(), new Dictionary<string, DateTimeOffset?>(),
                new(true, true, new HashSet<string>(), new HashSet<string>()), false);
            var plan = Planner.Decide(pair.PairId, 2, rows, new(TimeSpan.Zero, false,
                new("{name} [{side}]{ext}", "s", TimeSpan.Zero, "Local", "Dropbox"), 10));
            LocalChecks.Require(scope.A.Any(item => item.ObjectId == local.ObjectId) &&
                plan.Actions.All(action => action.Kind != ActionKind.Delete),
                "hidden delete replay cannot delete the edited survivor without fresh deletion evidence");
        }

        static void AssertSeek(SqliteConnection connection)
        {
            using (var explain = StoreSchema.Command(connection, null, "EXPLAIN QUERY PLAN " + SqliteStateStore.DeferredPlanningIntakeSql))
            {
                explain.Parameters.AddWithValue("$p", 1);
                explain.Parameters.AddWithValue("$before", long.MaxValue);
                explain.Parameters.AddWithValue("$limit", 256);
                using var reader = explain.ExecuteReader();
                var details = new List<string>();
                while (reader.Read()) details.Add(reader.GetString(3));
                LocalChecks.Require(details.Any(detail => detail.Contains("SEARCH d USING INDEX planning_deferred_pair", StringComparison.Ordinal)) &&
                    details.All(detail => !detail.Contains("TEMP B-TREE", StringComparison.Ordinal)),
                    "deferred paging seeks the entity key without sorting the pair on every page");
            }
        }

        static void Snapshot(SqliteStateStore store, RootDef root, Side side, IReadOnlyList<ItemState> items)
        {
            var gen = store.BeginGeneration(root.RootId, "fixture");
            store.WriteSnapshot(new(root.RootId, side, gen, "cursor", items, []));
            store.CompleteGeneration(gen, "cursor", true);
        }
    }
}
