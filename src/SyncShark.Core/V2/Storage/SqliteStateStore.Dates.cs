namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore : IDateRepairStore
{
    void IDateRepairStore.PrepareDateRepairExclusions(IEnumerable<string> normalizedPaths) => PrepareDateRepairExclusions(normalizedPaths);
    void IDateRepairStore.PrepareDateWriteEvidence(string pairId,
        IEnumerable<(string Path, DateTimeOffset Occurred)> rows, bool requireLogs) => PrepareDateWriteEvidence(pairId, rows, requireLogs);
    IReadOnlyList<Baseline> IDateRepairStore.DateRepairPage(string pairId, string after, int limit, Side side,
        long startUtcTicks, long endUtcTicks, bool mismatch) => DateRepairPage(pairId, after, limit, side, startUtcTicks, endUtcTicks, mismatch);
    void IDateRepairStore.PrepareDateRepair(string pairId) => PrepareDateRepair(pairId);
    bool IDateRepairStore.DateRepairExcluded(string path) => DateRepairExcluded(path);
    bool IDateRepairStore.DateRepairHeld(PairDef pair, Baseline baseline) => DateRepairHeld(pair, baseline);
    string? IDateRepairStore.DateWriteSkipReason(Side side, ItemState local, string? hash) => DateWriteSkipReason(side, local, hash);
    bool IDateRepairStore.DateLogCorrelated(ItemState local) => DateLogCorrelated(local);
    DateTimeOffset IDateRepairStore.RepairDate(Baseline baseline, Side side, DateTimeOffset time,
        Func<DateTimeOffset, DateTimeOffset> setTime, DateTimeOffset? originalTime, ItemState? live, string? verifiedHash) =>
        RepairDate(baseline, side, time, setTime, originalTime, live, verifiedHash);

    internal Action<Baseline>? BeforeDateRepairCommitForTest { get; set; }
    internal IReadOnlyList<Baseline> DateRepairPage(string pairId, string after, int limit = 128,
        Side side = Side.A, long startUtcTicks = 0, long endUtcTicks = 3155378975999999999L, bool mismatch = false) =>
        InStore(() =>
        {
            var pair = Pair(pairId)!;
            return _points.DateRepairPage(pairId, RequirePair(pairId),
                RootNo((side == Side.A ? pair.A : pair.B).RootId)!.Value,
                RootNo((side == Side.A ? pair.B : pair.A).RootId)!.Value, after, limit, side, startUtcTicks, endUtcTicks, mismatch);
        });

    internal void PrepareDateRepairExclusions(IEnumerable<string> normalizedPaths) => InStore(() =>
    {
        StoreSchema.Execute(_connection, null,
            "CREATE TEMP TABLE IF NOT EXISTS date_repair_exclude(path TEXT PRIMARY KEY) WITHOUT ROWID; DELETE FROM date_repair_exclude;");
        using var transaction = _connection.BeginTransaction();
        foreach (var path in normalizedPaths)
            StoreSchema.Execute(_connection, transaction, "INSERT OR IGNORE INTO date_repair_exclude VALUES($path);",
                ("$path", RowCodec.Key(path)));
        transaction.Commit();
    });

    internal bool DateRepairExcluded(string path) => InStore(() => StoreSchema.Scalar(_connection, null,
        "SELECT 1 FROM date_repair_exclude WHERE path=$path;", ("$path", RowCodec.Key(path))) is not null);

    // Snapshot protection under the run gate; the index lives on disk rather than a million-item CLR set.
    internal void PrepareDateRepair(string pairId) => InStore(() =>
    {
        StoreSchema.Execute(_connection, null,
            "CREATE TEMP TABLE IF NOT EXISTS date_repair_hold(path TEXT PRIMARY KEY) WITHOUT ROWID; DELETE FROM date_repair_hold;");
        // Empty display notices do not protect a root; explicit durable scope failures/decisions do.
        if (DecisionHolds.HasRootDecisionHold(this, pairId) || SyncRetryAt(pairId, string.Empty) is { Kind: "scan" })
            StoreSchema.Execute(_connection, null, "INSERT OR IGNORE INTO date_repair_hold VALUES('');");
        using var command = StoreSchema.Command(_connection, null,
            "SELECT path FROM notice WHERE pair_no=$p AND " + NoticeTable.ActionableSql("state") + " AND path<>'' AND reason_key<>'local-download-date' UNION ALL " +
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
        DateTimeOffset? originalTime = null, ItemState? live = null, string? verifiedHash = null) =>
        InStore(() =>
        {
            var current = _points.Read(baseline.PairId, RequirePair(baseline.PairId), [baseline.ObjectKey]).SingleOrDefault();
            if (current != baseline) throw new IOException("The baseline changed; date was not repaired.");
            var prefix = side == Side.A ? "a" : "b";
            var item = live ?? (side == Side.A ? baseline.A : baseline.B);
            var hash = verifiedHash ?? item.Hash ?? throw new IOException("No verified content hash.");
            var old = originalTime ?? item.Mtime;
            var pair = Pair(baseline.PairId)!;
            var root = side == Side.A ? pair.A : pair.B;
            var rootNo = RootNo(root.RootId)!.Value;
            if (live is not null && Live(root.RootId, [live.Path]).SingleOrDefault() != live)
                throw new IOException("The local inventory changed; date was not repaired.");
            BeforeDateRepairCommitForTest?.Invoke(baseline);
            using var transaction = _connection.BeginTransaction();
            try
            {
                var observed = setTime(time);
                StoreSchema.Execute(_connection, transaction,
                    $"UPDATE sync_point SET {prefix}_mtime=$time, {prefix}_object_id=$id, {prefix}_version_id=$version WHERE pair_no=$p AND object_key=$key;",
                    ("$time", RowCodec.MtimeTicks(observed)), ("$id", item.ObjectId), ("$version", (object?)item.VersionId ?? DBNull.Value), ("$p", RequirePair(baseline.PairId)), ("$key", baseline.ObjectKey));
                StoreSchema.Execute(_connection, transaction,
                    "UPDATE file_state SET mtime=$time,hash=$hash WHERE root_no=$root AND object_id=$id AND size=$size AND (hash=$hash OR hash IS NULL);",
                    ("$time", RowCodec.MtimeTicks(observed)), ("$root", rootNo),
                    ("$id", item.ObjectId), ("$size", item.Size), ("$hash", Convert.FromHexString(hash)));
                _notices.CloseWhere(transaction, "resolved",
                    "pair_no=$p AND reason_key='local-download-date' AND path=$path;",
                    ("$p", RequirePair(baseline.PairId)), ("$path", item.Path));
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
