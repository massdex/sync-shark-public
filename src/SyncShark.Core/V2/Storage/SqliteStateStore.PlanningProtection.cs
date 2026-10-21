using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Scoped to synchronous RunScope materialization, NEVER retained across hashing,
    // planning awaits or execution. This does not pin the WAL for a long running plan.
    internal PlanningProtection OpenPlanningProtection(PairDef pair)
    {
        var snapshot = OpenProtectionSnapshot(pair.A.RootId);
        try
        {
            return new PlanningProtection(snapshot.Connection, snapshot.Transaction, snapshot.Root,
                ProtectionRootNumber(snapshot.Connection, snapshot.Transaction, pair.B.RootId), _storageEnabled);
        }
        catch
        {
            snapshot.Transaction.Dispose();
            snapshot.Connection.Dispose();
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

        internal IReadOnlyList<ProtectionMatch> Read(IReadOnlyList<string> paths)
        {
            if (paths.Count > 1024) throw new ArgumentOutOfRangeException(nameof(paths));
            var a = ReadProtectionBatch(_connection, _transaction, _a, ItemFlags.NeedsRehash, paths, _storageEnabled);
            var b = ReadProtectionBatch(_connection, _transaction, _b, ItemFlags.NeedsRehash, paths, _storageEnabled);
            return a.Zip(b, (left, right) => new ProtectionMatch(left.Covered || right.Covered,
                left.Contains || right.Contains)).ToArray();
        }

        internal bool RetryProtected(string pairId, string path, bool descendants,
            IReadOnlySet<string> active, bool restoredOnly)
        {
            var pair = StoreSchema.Scalar(_connection, _transaction,
                "SELECT pair_no FROM pair WHERE pair_id=$id;", ("$id", pairId));
            if (pair is null) return false;
            var filter = restoredOnly
                ? " AND json_extract(json,'$.ReprobeEvidence')=1 AND retry_after<$held" : "";
            if (!descendants)
            {
                foreach (var ancestor in new[] { path }.Concat(PairPath.Ancestors(path)))
                {
                    if (active.Contains(ancestor)) continue;
                    using var command = Command("SELECT 1 FROM sync_retry WHERE pair_no=$p AND path_key=$k" + filter + ";");
                    command.Parameters.AddWithValue("$k", RowCodec.Key(ancestor));
                    if (command.ExecuteScalar() is not null) return true;
                }
                return false;
            }
            var key = RowCodec.Key(path);
            using var range = Command("SELECT path_key FROM sync_retry WHERE pair_no=$p AND path_key >= $lo AND path_key < $hi" +
                filter + " ORDER BY path_key LIMIT $limit;");
            range.Parameters.AddWithValue("$lo", key.Length == 0 ? "" : key + "/");
            range.Parameters.AddWithValue("$hi", key.Length == 0 ? "￿" : key + "0");
            range.Parameters.AddWithValue("$limit", (long)active.Count + 1);
            using var reader = range.ExecuteReader();
            while (reader.Read()) if (!active.Contains(reader.GetString(0))) return true;
            return false;

            SqliteCommand Command(string sql)
            {
                var command = StoreSchema.Command(_connection, _transaction, sql);
                command.Parameters.AddWithValue("$p", pair);
                if (restoredOnly) command.Parameters.AddWithValue("$held", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
                return command;
            }
        }

        public void Dispose()
        {
            try { _transaction.Dispose(); }
            finally { _connection.Dispose(); }
        }
    }
}
