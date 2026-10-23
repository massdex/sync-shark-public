using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    // One durable root-indexing notice: physical on the lowest pair, projected to every other pair that shares
    // the local root. Ignore suppresses only the current episode, a new cause reopens that same row, and Retry
    // makes the durable deadlines due without clearing attempt evidence. Only accepted success clears it.
    private static async Task IndexFailureNoticeChecksAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var product = Path.Combine(folder, "store");
        var local = new RootDef("index-fail-root", SyncProviders.Local, null, Path.Combine(folder, "root"),
            "id:index-fail-root");
        var cloudA = new RootDef("index-fail-cloud-a", SyncProviders.Dropbox, "acc-a", "/a", "id:index-fail-cloud-a");
        var cloudB = new RootDef("index-fail-cloud-b", SyncProviders.Dropbox, "acc-b", "/b", "id:index-fail-cloud-b");
        var pairA = new PairDef("index-fail-a", "index-fail-a", local, cloudA, [], [], true);
        var pairB = new PairDef("index-fail-b", "index-fail-b", local, cloudB, [], [], true);
        var reasonKey = SqliteStateStore.IndexFailurePrefix + local.RootId;
        var item = new ItemState("folder/file.txt", ItemKind.File, SyncLocalEndpointStore.ObjectId(1, 20), null,
            new string('a', 64), 3, DateTimeOffset.UtcNow, ItemFlags.None);
        const string cursor = "0000000000000001:200";
        using var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        // pairA is saved first, so it owns the lowest pair number and the one physical notice.
        store.SavePair(pairA);
        store.SavePair(pairB);
        var generation = store.BeginGeneration(local.RootId, "full");
        store.WriteSnapshot(new Snapshot(local.RootId, Side.A, generation, cursor, [item], []));
        store.CompleteGeneration(generation, cursor, true);
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(local.RootId, cursor, 100, true);
        var epoch = gaps.GapEpoch(local.RootId)!;
        gaps.GapDiscovered(local.RootId, epoch, item, true);
        gaps.GapDiscoveryDone(local.RootId, epoch);
        // Publish the worker frontier; HasGapProtection reads the published discovery flag, not the worker's.
        gaps.PromoteGapDiscovery(local.RootId);
        // One due item failure and one due rehash failure make the deadline assertions observable.
        gaps.FailGapItem(local.RootId, epoch, item, "hash failed");
        store.RequireRehash(local.RootId, item, "background verification pending");
        var revision = store.RehashRevision(local.RootId, item.ObjectId);
        LocalChecks.Require(revision != 0 &&
            store.FailIndexingRehash(local.RootId, item, revision, "hash failed"),
            "the fixture records one durable rehash failure");
        store.RecordIndexFailure(local.RootId, "journal access denied");

        // Raw reads are the fixture's durable oracle: no notice or attempt state is inferred from a projection.
        SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(product, "v2", "state.db"), Pooling = false }.ToString());
            connection.Open();
            return connection;
        }
        (long Count, long Due) RootState()
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT index_failure_count,index_retry_after FROM root WHERE root_id=$r;";
            cmd.Parameters.AddWithValue("$r", local.RootId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
        }
        (long Count, long Due) GapState()
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT failure_count,retry_after FROM local_gap_item WHERE root_no=" +
                "(SELECT root_no FROM root WHERE root_id=$r) AND object_id=$o;";
            cmd.Parameters.AddWithValue("$r", local.RootId);
            cmd.Parameters.AddWithValue("$o", item.ObjectId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
        }
        (long Count, long Due) RehashState()
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT failure_count,retry_after FROM local_rehash WHERE root_no=" +
                "(SELECT root_no FROM root WHERE root_id=$r) AND object_id=$o;";
            cmd.Parameters.AddWithValue("$r", local.RootId);
            cmd.Parameters.AddWithValue("$o", item.ObjectId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
        }
        (int Rows, string Id, string State, string? Choice) NoticeRow()
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT notice_id,state,choice FROM notice WHERE reason_key=$k;";
            cmd.Parameters.AddWithValue("$k", reasonKey);
            using var reader = cmd.ExecuteReader();
            var rows = new List<(string Id, string State, string? Choice)>();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            return rows.Count == 1
                ? (1, rows[0].Id, rows[0].State, rows[0].Choice)
                : (rows.Count, string.Empty, string.Empty, null);
        }

        var physical = store.OpenNotices(pairA.PairId).Where(notice => notice.ReasonKey == reasonKey).ToArray();
        var projected = store.OpenNotices(pairB.PairId).Where(notice => notice.ReasonKey == reasonKey).ToArray();
        LocalChecks.Require(physical.Length == 1 && physical[0].PairId == pairA.PairId &&
            projected.Length == 1 && projected[0].PairId == pairB.PairId &&
            projected[0].NoticeId == physical[0].NoticeId && NoticeRow().Rows == 1,
            "one physical indexing notice on the lowest pair is projected to every pair sharing the root");
        LocalChecks.Require(RootState().Count == 1 && RootState().Due > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() &&
            !store.IndexRetryDue(local.RootId),
            "a recorded root failure holds the root behind its future durable deadline");

        var settings = StorageChecks.Fast with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException),
            new FakeEndpointProvider(), settings, TimeProvider.System);
        var beforeIgnore = (RootState(), GapState(), RehashState());
        var ignoreResult = await run.ApplyRetryNoticeChoiceAsync(pairB.PairId, projected[0].NoticeId!,
            NoticeChoices.Ignore, default);
        // Latest notice semantics: Ignore keeps the episode actionable as state open + choice ignore.
        // The inbox projection hides it, while unresolved-notice consumers still see it.
        bool Suppressed(string pairId, string noticeId) =>
            store.OpenNotices(pairId).All(notice => notice.NoticeId != noticeId) &&
            ((IStateStore)store).UnresolvedNotices(pairId).Any(notice => notice.NoticeId == noticeId);
        var ignored = NoticeRow();
        LocalChecks.Require(ignoreResult == NoticeChoices.IgnoreResult &&
            ignored.State == "open" && ignored.Choice == NoticeChoices.Ignore &&
            Suppressed(pairB.PairId, physical[0].NoticeId!) &&
            RootState() == beforeIgnore.Item1 && GapState() == beforeIgnore.Item2 &&
            RehashState() == beforeIgnore.Item3 &&
            store.Pair(pairA.PairId)!.Excluded.Count == 0 && store.Pair(pairB.PairId)!.Excluded.Count == 0,
            "Ignore keeps the episode actionable but hidden without changing a deadline, count or exclusion");

        // The ignored episode stays suppressed for the same cause and reopens for a genuinely different one.
        store.RecordIndexFailure(local.RootId, "journal access denied");
        var suppressed = NoticeRow();
        LocalChecks.Require(suppressed.Rows == 1 && suppressed.State == "open" &&
            suppressed.Choice == NoticeChoices.Ignore && Suppressed(pairB.PairId, physical[0].NoticeId!),
            "the same cause stays suppressed and actionable for the ignored episode");
        store.RecordIndexFailure(local.RootId, "journal is unreadable");
        LocalChecks.Require(NoticeRow().State == "open" && NoticeRow().Choice == NoticeChoices.Ignore &&
            Suppressed(pairB.PairId, physical[0].NoticeId!),
            "different diagnostic wording within the root failure class stays suppressed");
        gaps.FailGapItem(local.RootId, epoch, item, "content proof unavailable");
        var reopened = NoticeRow();
        LocalChecks.Require(reopened.Rows == 1 && reopened.State == "open" && reopened.Choice is null &&
            reopened.Id == physical[0].NoticeId &&
            store.OpenNotices(pairB.PairId).Any(notice => notice.NoticeId == reopened.Id) &&
            ((IStateStore)store).UnresolvedNotices(pairB.PairId).Any(notice => notice.NoticeId == reopened.Id),
            "a different cause reopens and resurfaces the same physical notice");

        var beforeRetry = (RootState(), GapState(), RehashState());
        var retryResult = await run.ApplyRetryNoticeChoiceAsync(pairB.PairId, reopened.Id,
            NoticeChoices.RetryNow, default);
        var afterRetry = (RootState(), GapState(), RehashState());
        LocalChecks.Require(retryResult == NoticeChoices.IndexRetryResult && store.IndexRetryDue(local.RootId) &&
            afterRetry.Item1.Count == beforeRetry.Item1.Count && afterRetry.Item1.Due == 0 &&
            afterRetry.Item2.Count == beforeRetry.Item2.Count && afterRetry.Item2.Due == 0 &&
            afterRetry.Item3.Count == beforeRetry.Item3.Count && afterRetry.Item3.Due == 0,
            "Retry makes every durable deadline due without clearing attempt evidence");
        LocalChecks.Require(store.OpenNotices(pairA.PairId).Any(notice => notice.ReasonKey == reasonKey),
            "a retry request keeps the indexing notice visible until verified success");

        LocalChecks.Require(store.CompleteRehash(local.RootId, item, new string('b', 64)),
            "accepted verification publishes the recovered content");
        store.CompleteIndexFailure(local.RootId);
        var cleared = RootState();
        LocalChecks.Require(cleared.Count == 0 && cleared.Due == 0 && GapState() == (0L, 0L) &&
            NoticeRow().State == "closed" &&
            store.OpenNotices(pairA.PairId).All(notice => notice.ReasonKey != reasonKey) &&
            ((IStateStore)store).UnresolvedNotices(pairB.PairId).All(notice => notice.ReasonKey != reasonKey),
            "only accepted success clears the root evidence and closes the notice");
        IndexFailureClassSuppressionChecks(Path.Combine(folder, "class-suppression"));
        IndexFailureSchemaUpgradeChecks(Path.Combine(folder, "schema-upgrade"));
        await GapUnchangedRequiresContentProofChecksAsync(Path.Combine(folder, "gap-proof"));
        Console.WriteLine("PASS root indexing failure notice: one physical row, projection, Ignore episode, cause reopen, Retry deadline");
    }

    // Owner decision: a summary episode is root + failure class, not the latest failed item.
    private static void IndexFailureClassSuppressionChecks(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var root = new RootDef("class-root", SyncProviders.Local, null, "/local", "id:class-root");
        var remote = new RootDef("class-remote", SyncProviders.Dropbox, "account", "/remote", "id:remote");
        var pair = new PairDef("class-pair", "Pair A", root, remote, [], [], true);
        store.SavePair(pair);
        var first = new ItemState("first.txt", ItemKind.File, "id:first", null, new string('a', 64),
            1, DateTimeOffset.UtcNow, ItemFlags.None);
        var second = first with { Path = "second.txt", ObjectId = "id:second" };
        var gen = store.BeginGeneration(root.RootId, "full");
        const string cursor = "0000000000000001:200";
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, gen, cursor, [first, second], []));
        store.CompleteGeneration(gen, cursor, true);
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, cursor, 100, true);
        var epoch = gaps.GapEpoch(root.RootId)!;
        gaps.GapDiscovered(root.RootId, epoch, first, true);
        gaps.GapDiscovered(root.RootId, epoch, second, true);
        gaps.FailGapItem(root.RootId, epoch, first, "first.txt could not be verified");
        var notice = store.OpenNotices(pair.PairId).Single(n => n.ReasonKey == SqliteStateStore.IndexFailurePrefix + root.RootId);
        store.CloseNotice(notice.NoticeId!, NoticeChoices.Ignore);
        foreach (var item in new[] { second, first, second })
            gaps.FailGapItem(root.RootId, epoch, item, item.Path + " could not be verified");
        LocalChecks.Require(store.OpenNotices(pair.PairId).All(n => n.ReasonKey != notice.ReasonKey) &&
            gaps.HasGapProtection(root.RootId),
            "alternating failed items of one class do not reopen an ignored root summary or release protection");
        store.RecordIndexFailure(root.RootId, "root journal unavailable");
        var changed = store.OpenNotices(pair.PairId).Single(n => n.ReasonKey == notice.ReasonKey);
        LocalChecks.Require(changed.NoticeId == notice.NoticeId && changed.CauseKey != notice.CauseKey,
            "a new root failure class resurfaces the same physical summary");
    }

    // A real v22 store (no closure timestamp), a v23 store (closure timestamp) and a v24 store (the
    // planner lane's reserved step) all predate the INDEX-25 deadline columns and root/version index.
    // They upgrade forward-only and idempotently. The v22 step stamps every already-closed notice at
    // migration time; it never reopens one, so closure lifetime and data survive. Every additive object
    // is recreated and a second open changes nothing. The fixture is disposable; no live product state
    // is touched.
    private static void IndexFailureSchemaUpgradeChecks(string folder)
    {
        static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        static long Query(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        static bool Column(SqliteConnection connection, string table, string column)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('" + table + "') WHERE name=$c;";
            command.Parameters.AddWithValue("$c", column);
            return Convert.ToInt64(command.ExecuteScalar()) == 1;
        }
        static (string State, string? Choice, long? ClosedAt) NoticeState(SqliteConnection connection, string id)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT state,choice,closed_at FROM notice WHERE notice_id=$id;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2))
                : (string.Empty, null, null);
        }
        SqliteConnection Fixture(string product) => new(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(product, "v2", "state.db"), Pooling = false }.ToString());

        foreach (var fromVersion in new[] { 22, 23, 24 })
        {
            var product = Path.Combine(folder, "v" + fromVersion);
            Directory.CreateDirectory(product);
            string pairId, rootId, closedId;
            long closedBefore;
            using (var created = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
            {
                var (root, pair) = SaveIndexRoot(created, folder, "upgrade-root-" + fromVersion,
                    "upgrade-pair-" + fromVersion, ["upgrade/file.txt"]);
                rootId = root.RootId;
                pairId = pair.PairId;
                // SaveIndexRoot deliberately seeds unverified backlog for lifecycle tests.
                // This migration fixture needs an accepted hash before testing its preservation.
                var seeded = created.Live(rootId, ["upgrade/file.txt"]).Single();
                LocalChecks.Require(created.CompleteRehash(rootId, seeded, new string('a', 64)) &&
                    created.Live(rootId, [seeded.Path]).Single().Hash == new string('a', 64),
                    "migration fixture starts with verified content and a retained planning obligation");
                created.AddNotices([new Notice(pairId, 40, NoticeKind.Info, string.Empty,
                    "preserved across the schema upgrade", "upgrade-preservation")]);
                created.AddNotices([new Notice(pairId, 40, NoticeKind.Info, string.Empty,
                    "closed before the schema upgrade", "upgrade-closed")]);
                closedId = created.OpenNotices(pairId).Single(notice => notice.ReasonKey == "upgrade-closed").NoticeId!;
                created.CloseNotice(closedId, NoticeChoices.Dismiss);
            }
            // Simulate the exact older shape. v22 predates the closure timestamp; every fixture version
            // predates the INDEX-25 deadline columns and root/version index. v23 adds closed_at, and v24 is
            // the planner lane's reserved step, so neither owns the INDEX-25 objects.
            using (var connection = Fixture(product))
            {
                connection.Open();
                closedBefore = Query(connection,
                    "SELECT closed_at FROM notice WHERE notice_id='" + closedId + "';");
                Execute(connection, "DROP INDEX IF EXISTS planning_deferred_root_version;");
                // INDEX-25 owns all of these; none of the older shapes has them.
                Execute(connection,
                    "ALTER TABLE local_gap_item DROP COLUMN retry_after;" +
                    "ALTER TABLE local_rehash DROP COLUMN failure_count;" +
                    "ALTER TABLE local_rehash DROP COLUMN retry_after;" +
                    "ALTER TABLE root DROP COLUMN index_failure_count;" +
                    "ALTER TABLE root DROP COLUMN index_retry_after;" +
                    "ALTER TABLE root DROP COLUMN index_failure_reason;");
                if (fromVersion == 22)
                    Execute(connection,
                        // The imported notice lane may add a closure-age partial index; a real v22 shape has neither.
                        "DROP INDEX IF EXISTS nt_terminal;" +
                        "ALTER TABLE notice DROP COLUMN closed_at;");
                Execute(connection, "PRAGMA user_version=" + fromVersion + ";");
            }
            var migrationStarted = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var pass in new[] { "first open", "idempotent reopen" })
            {
                using var upgraded = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
                LocalChecks.Require(upgraded.Pair(pairId) is not null &&
                    upgraded.Live(rootId, ["upgrade/file.txt"]).Single().Hash == new string('a', 64) &&
                    upgraded.OpenNotices(pairId).Any(notice => notice.ReasonKey == "upgrade-preservation") &&
                    upgraded.OpenNotices(pairId).All(notice => notice.ReasonKey != "upgrade-closed"),
                    "v" + fromVersion + " upgrade (" + pass + ") preserves pair, verified content and open notice data");
            }
            using (var check = Fixture(product))
            {
                check.Open();
                var closed = NoticeState(check, closedId);
                LocalChecks.Require(closed.State == "closed" && closed.Choice == NoticeChoices.Dismiss &&
                    (fromVersion == 22 ? closed.ClosedAt >= migrationStarted : closed.ClosedAt == closedBefore),
                    "v" + fromVersion + " upgrade keeps the closed notice closed with its choice and " +
                    (fromVersion == 22 ? "stamps closure at migration" : "preserves its original closure time"));
                LocalChecks.Require(Query(check, "PRAGMA user_version;") == StoreSchema.Version &&
                    Query(check, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' " +
                        "AND name='planning_deferred_root_version';") == 1 &&
                    Column(check, "local_gap_item", "retry_after") &&
                    Column(check, "local_rehash", "failure_count") &&
                    Column(check, "local_rehash", "retry_after") &&
                    Column(check, "root", "index_failure_count") &&
                    Column(check, "root", "index_retry_after") &&
                    Column(check, "root", "index_failure_reason") &&
                    Column(check, "notice", "closed_at"),
                    "v" + fromVersion + " upgrade recreates the v" + StoreSchema.Version +
                    " index, closure timestamp and every additive deadline column");
            }
        }
    }
}