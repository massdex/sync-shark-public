using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Translate at execution, including deferred MoveNext, without changing non-damage failures.
    internal static T DamageBoundary<T>(Func<T> action)
    {
        try { return action(); }
        catch (SqliteException error) when (StoreSchema.IsDamaged(error))
        { throw new StateDamagedException($"v2 state is damaged: {error.Message}", error); }
    }

    internal static void DamageBoundary(Action action) => DamageBoundary(() => { action(); return true; });

    internal static void DisposePreserving(Exception? primary, params IDisposable?[] resources)
    {
        Exception? failure = primary;
        foreach (var resource in resources)
        {
            try { DamageBoundary(() => resource?.Dispose()); }
            catch (Exception secondary)
            {
                if (failure is null) failure = secondary;
                else
                {
                    // Same diagnostic attachment as FairStoreGate's fatal retention; never replace primary.
                    const string key = "StoreCleanupFailure";
                    failure.Data[key] = failure.Data[key] is Exception prior
                    ? new AggregateException(prior, secondary) : secondary;
                }
            }
        }
        if (primary is null && failure is not null)
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static IEnumerable<T> DamageEnumeration<T>(Func<IEnumerable<T>> source)
    {
        IEnumerator<T>? iterator = null;
        Exception? primary = null;
        try
        {
            try { iterator = DamageBoundary(() => source().GetEnumerator()); }
            catch (Exception error) { primary = error; throw; }
            while (true)
            {
                bool moved;
                T current = default!;
                try
                {
                    moved = DamageBoundary(iterator.MoveNext);
                    if (moved) current = DamageBoundary(() => iterator.Current);
                }
                catch (Exception error) { primary = error; throw; }
                if (!moved) yield break;
                yield return current;
            }
        }
        finally { DisposePreserving(primary, iterator); }
    }

    // Scoped to synchronous RunScope materialization, NEVER retained across hashing,
    // planning awaits or execution. This does not pin the WAL for a long running plan.
    internal PlanningProtection OpenPlanningProtection(PairDef pair)
    {
        var snapshot = DamageBoundary(() => OpenProtectionSnapshot(pair.A.RootId));
        try
        {
            return DamageBoundary(() => new PlanningProtection(snapshot.Connection, snapshot.Transaction, snapshot.Root,
            ProtectionRootNumber(snapshot.Connection, snapshot.Transaction, pair.B.RootId), _storageEnabled));
        }
        catch (Exception error)
        {
            DisposePreserving(error, snapshot.Transaction, snapshot.Connection);
            throw;
        }
    }

    internal sealed class PlanningProtection : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly long? _a;
        private readonly long? _b;
        private readonly bool _storageEnabled;

        internal PlanningProtection(SqliteConnection connection, SqliteTransaction transaction, long? a, long? b,
        bool storageEnabled)
        { _connection = connection; _transaction = transaction; _a = a; _b = b; _storageEnabled = storageEnabled; }

        internal ProtectionStream Stream() =>
        new(_connection, _transaction, _a, _b, ItemFlags.NeedsRehash, _storageEnabled);

        internal IReadOnlyList<ProtectionMatch> Read(IReadOnlyList<string> paths)
        => DamageBoundary(() =>
        {
            if (paths.Count > 1024) throw new ArgumentOutOfRangeException(nameof(paths));
            var a = ReadProtectionBatch(_connection, _transaction, _a, ItemFlags.NeedsRehash, paths, _storageEnabled);
            var b = ReadProtectionBatch(_connection, _transaction, _b, ItemFlags.NeedsRehash, paths, _storageEnabled);
            return a.Zip(b, (left, right) => new ProtectionMatch(left.Covered || right.Covered,
            left.Contains || right.Contains)).ToArray();
        });

        // Read once from the same pinned snapshot as protection. Large backlogs retain
        // the existing bounded fallback; ordinary retries never require per-item SQL.
        internal Func<string, bool, bool> RetryLookup(string pairId, IReadOnlySet<string> active,
        bool restoredOnly, Action? onRead = null) => DamageBoundary(() => RetryPathIndex.Create(RetryPaths(pairId, restoredOnly), active,
        (path, descendants) =>
        {
            onRead?.Invoke();
            return RetryProtected(pairId, path, descendants, active, restoredOnly);
        }));

        private IEnumerable<string> RetryPaths(string pairId, bool restoredOnly) =>
        DamageEnumeration(() => RetryPathsCore(pairId, restoredOnly));

        private IEnumerable<string> RetryPathsCore(string pairId, bool restoredOnly)
        {
            SqliteCommand? command = null;
            SqliteDataReader? reader = null;
            Exception? primary = null;
            try
            {
                try
                {
                    command = StoreSchema.Command(_connection, _transaction,
                    "SELECT path_key FROM sync_retry WHERE pair_no=(SELECT pair_no FROM pair WHERE pair_id=$id)" +
                    RetryProtectionPredicate.Sql(restoredOnly) + ";");
                    command.Parameters.AddWithValue("$id", pairId);
                    reader = command.ExecuteReader();
                }
                catch (Exception error) { primary = error; throw; }
                while (true)
                {
                    string path;
                    try
                    {
                        if (!reader.Read()) break;
                        path = reader.GetString(0);
                    }
                    catch (Exception error) { primary = error; throw; }
                    yield return path;
                }
            }
            finally { DisposePreserving(primary, reader, command); }
        }

        internal bool RetryProtected(string pairId, string path, bool descendants,
        IReadOnlySet<string> active, bool restoredOnly)
        => DamageBoundary(() =>
        {
            var pair = StoreSchema.Scalar(_connection, _transaction,
            "SELECT pair_no FROM pair WHERE pair_id=$id;", ("$id", pairId));
            if (pair is null) return false;
            var filter = RetryProtectionPredicate.Sql(restoredOnly);
            SqliteCommand Command(string sql)
            {
                var command = StoreSchema.Command(_connection, _transaction, sql);
                command.Parameters.AddWithValue("$p", pair);
                return command;
            }
            if (!descendants)
            {
                foreach (var ancestor in new[] { path }.Concat(PairPath.Ancestors(path)))
                {
                    if (active.Contains(ancestor)) continue;
                    var command = Command("SELECT 1 FROM sync_retry WHERE pair_no=$p AND path_key=$k" + filter + ";");
                    Exception? cleanupPrimary0 = null;
                    try
                    {
                        command.Parameters.AddWithValue("$k", RowCodec.Key(ancestor));
                        if (command.ExecuteScalar() is not null) return true;

                    }
                    catch (Exception cleanupError) { cleanupPrimary0 = cleanupError; throw; }
                    finally { DisposePreserving(cleanupPrimary0, command); }
                }
                return false;
            }
            var key = RowCodec.Key(path);
            var range = Command("SELECT path_key FROM sync_retry WHERE pair_no=$p AND path_key >= $lo AND path_key < $hi" +
            filter + " ORDER BY path_key LIMIT $limit;");
            Exception? cleanupPrimary1 = null;
            try
            {
                range.Parameters.AddWithValue("$lo", key.Length == 0 ? "" : key + "/");
                range.Parameters.AddWithValue("$hi", key.Length == 0 ? "￿" : key + "0");
                range.Parameters.AddWithValue("$limit", (long)active.Count + 1);
                var reader = range.ExecuteReader();
                Exception? cleanupPrimary2 = null;
                try
                {
                    while (reader.Read()) if (!active.Contains(reader.GetString(0))) return true;
                    return false;

                }
                catch (Exception cleanupError) { cleanupPrimary2 = cleanupError; throw; }
                finally { DisposePreserving(cleanupPrimary2, reader); }

            }
            catch (Exception cleanupError) { cleanupPrimary1 = cleanupError; throw; }
            finally { DisposePreserving(cleanupPrimary1, range); }
        });

        // Restore protection must share this planning read transaction. Consulting the live writer connection
        // here could expose the two halves of a pending-to-ready transition to opposite sides of the join.
        internal bool RecoveryRestoreProtected(string pairId, string path, bool descendants)
        => DamageBoundary(() =>
        {
            var pair = StoreSchema.Scalar(_connection, _transaction,
            "SELECT pair_no FROM pair WHERE pair_id=$id;", ("$id", pairId));
            if (pair is null) return false;
            var pending = "SELECT 1 FROM notice WHERE pair_no=$p " +
            "AND reason_key='recovery-restore-pending' AND " + NoticeTable.ActionableSql("state") + " AND restore_hold=1 ";
            foreach (var ancestor in new[] { path }.Concat(PairPath.Ancestors(path)))
            if (StoreSchema.Scalar(_connection, _transaction, pending + "AND path_key=$key LIMIT 1;",
            ("$p", pair), ("$key", PathKey.Of(ancestor))) is not null) return true;
            if (!descendants) return false;
            var key = PathKey.Of(path);
            return StoreSchema.Scalar(_connection, _transaction,
            pending + "AND path_key >= $lo AND path_key < $hi LIMIT 1;", ("$p", pair),
            ("$lo", key.Length == 0 ? "" : key + "/"), ("$hi", key.Length == 0 ? "￿" : key + "0")) is not null;
        });

        public void Dispose()
        {
            DisposePreserving(null, _transaction, _connection);
        }
    }
}
