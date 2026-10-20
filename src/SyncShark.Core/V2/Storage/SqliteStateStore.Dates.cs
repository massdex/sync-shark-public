namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal IReadOnlyList<Baseline> DateRepairPage(string pairId, string after, int limit = 128,
        Side side = Side.A, DateOnly? localDate = null, DateOnly? from = null, DateOnly? to = null) =>
        InStore(() =>
        {
            var pair = Pair(pairId)!;
            return _points.DateRepairPage(pairId, RequirePair(pairId),
                RootNo((side == Side.A ? pair.A : pair.B).RootId)!.Value,
                RootNo((side == Side.A ? pair.B : pair.A).RootId)!.Value, after, limit, side, localDate, from, to);
        });

    // Snapshot protection under the run gate; the index lives on disk rather than a million-item CLR set.
    internal void PrepareDateRepair(string pairId) => InStore(() =>
    {
        StoreSchema.Execute(_connection, null,
            "CREATE TEMP TABLE IF NOT EXISTS date_repair_hold(path TEXT PRIMARY KEY) WITHOUT ROWID; DELETE FROM date_repair_hold;");
        using var command = StoreSchema.Command(_connection, null,
            "SELECT path FROM notice WHERE pair_no=$p AND state='open' AND path<>'' UNION ALL " +
            "SELECT json_extract(action_json,'$.Path') FROM pending_effect WHERE pair_no=$p UNION ALL " +
            "SELECT json_extract(action_json,'$.FromPath') FROM pending_effect WHERE pair_no=$p;");
        command.Parameters.AddWithValue("$p", RequirePair(pairId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(0))
                StoreSchema.Execute(_connection, null, "INSERT OR IGNORE INTO date_repair_hold VALUES($path);",
                    ("$path", RowCodec.Key(reader.GetString(0))));
    });

    internal bool DateRepairHeld(PairDef pair, Baseline baseline) => InStore(() =>
    {
        foreach (var (root, item) in new[] { (pair.A, baseline.A), (pair.B, baseline.B) })
        {
            var path = item.Path;
            while (true)
            {
                if (StoreSchema.Scalar(_connection, null,
                    "SELECT 1 FROM date_repair_hold WHERE path=$path;", ("$path", RowCodec.Key(path))) is not null ||
                    Live(root.RootId, [path]).Any(row => row.Flags != ItemFlags.None)) return true;
                if (path.Length == 0) break;
                var slash = path.LastIndexOf('/');
                path = slash < 0 ? "" : path[..slash];
            }
        }
        return false;
    });

    // The exclusive file handle remains held across this transaction. Filesystem and SQLite cannot share an atomic
    // commit: restore the original timestamp on a database failure; the caller's flushed intent log covers crashes.
    internal DateTimeOffset RepairDate(Baseline baseline, Side side, DateTimeOffset time, Func<DateTimeOffset, DateTimeOffset> setTime,
        DateTimeOffset? originalTime = null, ItemState? live = null) =>
        InStore(() =>
        {
            var current = _points.Read(baseline.PairId, RequirePair(baseline.PairId), [baseline.ObjectKey]).SingleOrDefault();
            if (current != baseline) throw new IOException("The baseline changed; date was not repaired.");
            var prefix = side == Side.A ? "a" : "b";
            var item = live ?? (side == Side.A ? baseline.A : baseline.B);
            var old = originalTime ?? item.Mtime;
            var pair = Pair(baseline.PairId)!;
            var root = side == Side.A ? pair.A : pair.B;
            var rootNo = RootNo(root.RootId)!.Value;
            if (live is not null && Live(root.RootId, [live.Path]).SingleOrDefault() != live)
                throw new IOException("The local inventory changed; date was not repaired.");
            using var transaction = _connection.BeginTransaction();
            try
            {
                var observed = setTime(time);
                StoreSchema.Execute(_connection, transaction,
                    $"UPDATE sync_point SET {prefix}_mtime=$time, {prefix}_object_id=$id, {prefix}_version_id=$version WHERE pair_no=$p AND object_key=$key;",
                    ("$time", RowCodec.MtimeTicks(observed)), ("$id", item.ObjectId), ("$version", (object?)item.VersionId ?? DBNull.Value), ("$p", RequirePair(baseline.PairId)), ("$key", baseline.ObjectKey));
                StoreSchema.Execute(_connection, transaction,
                    "UPDATE file_state SET mtime=$time WHERE root_no=$root AND object_id=$id AND size=$size AND hash=$hash;",
                    ("$time", RowCodec.MtimeTicks(observed)), ("$root", rootNo),
                    ("$id", item.ObjectId), ("$size", item.Size), ("$hash", Convert.FromHexString(item.Hash!)));
                transaction.Commit();
                return observed;
            }
            catch (Exception error)
            {
                try { setTime(old); }
                catch (Exception rollback) { error.Data["DateRepairRollbackError"] = rollback.ToString(); }
                throw;
            }
        });
}
