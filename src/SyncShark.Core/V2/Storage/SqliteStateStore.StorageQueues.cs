using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    bool IStateStore.HasOwnedPointers(string rootId) => HasOwnedPointers(rootId);

    void IStateStore.ClearStorageRootBackoff(string rootId) => ClearStorageRootBackoff(rootId);

    // The caller supplies roots after global/account pause projection. With no projection,
    // derive active roots from the small pair catalog, never from the item inventory.
    private const string StorageRootsSql = "json_each($roots) roots";
    private const string StorageUnblockedSql = "WHERE NOT EXISTS(SELECT 1 FROM storage_root_backoff b WHERE b.root_id=roots.value AND b.due>$now)";

    private void StorageRootParameters(SqliteCommand command, IReadOnlyCollection<string>? activeRoots)
    {
        // Caller-supplied roots are an upper bound, never an override: intersect with the actual enabled,
        // not-paused catalog so a stale caller cannot admit work for a suspended pair.
        var roots = Pairs().Where(p => p.Enabled && !PairPaused(p.PairId))
            .SelectMany(p => new[] { p.A.RootId, p.B.RootId })
            .Where(root => activeRoots is null || activeRoots.Contains(root))
            .Distinct().ToArray();
        command.Parameters.AddWithValue("$roots", JsonSerializer.Serialize(roots));
        command.Parameters.AddWithValue("$now", NowMs());
        command.Parameters.AddWithValue("$requested", StorageDue.UserRequested);
    }

    public void SetStorageRootBackoff(string rootId, TimeSpan delay) => InStore(() =>
    {
        if (!_storageEnabled) return;
        StoreSchema.Execute(_connection, null, "INSERT INTO storage_root_backoff(root_id,due) VALUES($r,$due) ON CONFLICT(root_id) DO UPDATE SET due=excluded.due;",
            ("$r", rootId), ("$due", NowMs() + Math.Max(0, (long)delay.TotalMilliseconds)));
    });

    public void FailStorageRoot(string rootId) => InStore(() =>
    {
        if (!_storageEnabled) return;
        using var tx = _connection.BeginTransaction();
        var attempts = Convert.ToInt32(StoreSchema.Scalar(_connection, tx,
            "SELECT failure_attempts FROM storage_root_backoff WHERE root_id=$r;", ("$r", rootId)) ?? 0);
        var state = FailureRetryOwner.Failed(attempts, DateTimeOffset.FromUnixTimeMilliseconds(NowMs()));
        StoreSchema.Execute(_connection, tx,
            "INSERT INTO storage_root_backoff(root_id,due,failure_attempts) VALUES($r,$due,$attempts) " +
            "ON CONFLICT(root_id) DO UPDATE SET due=excluded.due,failure_attempts=excluded.failure_attempts;",
            ("$r", rootId), ("$due", state.RetryAfter.ToUnixTimeMilliseconds()), ("$attempts", state.Attempts));
        tx.Commit();
    });

    public bool StorageRootBackedOff(string rootId) => InStore(() => _storageEnabled &&
        StoreSchema.Scalar(_connection, null, "SELECT 1 FROM storage_root_backoff WHERE root_id=$r AND due>$now;", ("$r", rootId), ("$now", NowMs())) is not null);

    public void ClearStorageRootBackoff(string rootId) => InStore(() =>
    {
        if (!_storageEnabled) return;
        StoreSchema.Execute(_connection, null, "DELETE FROM storage_root_backoff WHERE root_id=$r;", ("$r", rootId));
    });

    public bool HasOwnedPointers(string rootId) => InStore(() => _storageEnabled &&
        StoreSchema.Scalar(_connection, null, "SELECT 1 FROM file_state INDEXED BY fs_owned WHERE root_no=$r AND (flags & 1024)!=0 LIMIT 1;", ("$r", RootNo(rootId) ?? -1)) is not null);

    public bool HasStorageRootWork(string rootId) => InStore(() => _storageEnabled && Convert.ToInt64(StoreSchema.Scalar(_connection, null,
        "SELECT EXISTS(SELECT 1 FROM storage_settlement WHERE root_id=$r AND state='pending') OR " +
        "EXISTS(SELECT 1 FROM storage_refresh WHERE root_id=$r) OR EXISTS(SELECT 1 FROM storage_hydration WHERE root_id=$r) OR " +
        "EXISTS(SELECT 1 FROM storage_subtree_sweep WHERE root_id=$r) OR EXISTS(SELECT 1 FROM storage_path_intent WHERE root_id=$r);", ("$r", rootId))) != 0);

    public IReadOnlyList<StoragePathIntent> UnresolvedStorageIntents(RootDef root, int limit = 50, int offset = 0) => InStore(() =>
    {
        if (!_storageEnabled) return (IReadOnlyList<StoragePathIntent>)[];
        using var cmd = StoreSchema.Command(_connection, null, "SELECT path,mode FROM storage_path_intent WHERE root_id=$r ORDER BY path_key LIMIT $limit OFFSET $offset;");
        cmd.Parameters.AddWithValue("$r", root.RootId); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200)); cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        var result = new List<StoragePathIntent>(); using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1)));
        return (IReadOnlyList<StoragePathIntent>)result;
    });

    internal const string StorageHydrationRequestedSql =
        "SELECT w.job_id,w.root_id,w.object_id FROM " + StorageRootsSql +
        " CROSS JOIN storage_hydration w ON w.job_id IN (SELECT h.job_id FROM storage_hydration h INDEXED BY storage_hydration_requested " +
        "WHERE h.root_id=roots.value AND h.due=-1 ORDER BY h.job_id LIMIT $limit) " +
        "ORDER BY w.job_id LIMIT $limit;";

    internal const string StorageHydrationRangeSql =
        "SELECT w.job_id,w.root_id,w.object_id FROM " + StorageRootsSql +
        " CROSS JOIN storage_hydration w ON w.job_id IN (SELECT h.job_id FROM storage_hydration h INDEXED BY storage_hydration_root " +
        "WHERE h.root_id=roots.value AND h.due<=$now AND h.job_id>$after ORDER BY h.job_id LIMIT $limit) " +
        StorageUnblockedSql + " ORDER BY w.job_id LIMIT $limit;";

    public IReadOnlyList<StorageHydrationJob> StorageHydrationPage(long afterJobId = 0, int limit = 128, IReadOnlyCollection<string>? activeRoots = null) => InStore(() =>
    {
        if (!_storageEnabled) return (IReadOnlyList<StorageHydrationJob>)[];
        var pageSize = Math.Clamp(limit, 1, 512);
        using var cmd = StoreSchema.Command(_connection, null, StorageHydrationRequestedSql);
        StorageRootParameters(cmd, activeRoots);
        cmd.Parameters.AddWithValue("$after", afterJobId); cmd.Parameters.AddWithValue("$limit", Math.Min(pageSize, 8));
        var result = new List<StorageHydrationJob>();
        var seen = new HashSet<long>();
        foreach (var sql in new[] { StorageHydrationRequestedSql, StorageHydrationRangeSql })
        {
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (result.Count < pageSize && reader.Read())
                if (seen.Add(reader.GetInt64(0)))
                    result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            if (result.Count == pageSize) break;
            cmd.Parameters["$limit"].Value = pageSize;
        }
        return (IReadOnlyList<StorageHydrationJob>)result;
    });

    public StorageHydrationJob? StorageHydrationFor(string rootId, string objectId) => InStore(() =>
    {
        if (!_storageEnabled) return null;
        using var cmd = StoreSchema.Command(_connection, null, "SELECT job_id FROM storage_hydration WHERE root_id=$r AND object_id=$o;");
        cmd.Parameters.AddWithValue("$r", rootId); cmd.Parameters.AddWithValue("$o", objectId);
        return cmd.ExecuteScalar() is long id ? new StorageHydrationJob(id, rootId, objectId) : null;
    });

    public bool IsCurrentStorageHydration(StorageHydrationJob job) => InStore(() => _storageEnabled &&
        StoreSchema.Scalar(_connection, null, "SELECT 1 FROM storage_hydration WHERE job_id=$id AND root_id=$r AND object_id=$o;",
            ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId)) is not null);

    public bool CompleteStorageHydration(StorageHydrationJob job, StorageObserved? observed = null) => InStore(() =>
    {
        if (!_storageEnabled) return false;
        using var tx = _connection.BeginTransaction();
        if (StoreSchema.Scalar(_connection, tx, "SELECT 1 FROM storage_hydration WHERE job_id=$id AND root_id=$r AND object_id=$o;", ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId)) is null) return false;
        if (observed is not null)
            StoreSchema.Execute(_connection, tx, "UPDATE storage_item SET availability=$a,pinned=$p,in_sync=$s WHERE root_id=$r AND object_id=$o;",
                ("$a", observed.Availability), ("$p", observed.Pinned ? 1 : 0), ("$s", observed.InSync ? 1 : 0), ("$r", job.RootId), ("$o", job.ObjectId));
        StoreSchema.Execute(_connection, tx, "DELETE FROM storage_hydration WHERE job_id=$id AND root_id=$r AND object_id=$o;",
            ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId));
        tx.Commit();
        return true;
    });

    // Failure adapters load/store only; timing and counting belong to FailureRetryOwner.
    private void FailStorageJob(string table, long jobId, string rootId, string objectId, string? binding)
    {
        if (!_storageEnabled) return;
        var where = "job_id=$id AND root_id=$r AND object_id=$o" + (binding is null ? "" : " AND remote_binding=$b");
        using var tx = _connection.BeginTransaction();
        using var read = StoreSchema.Command(_connection, tx, $"SELECT failure_attempts FROM {table} WHERE {where};");
        read.Parameters.AddWithValue("$id", jobId); read.Parameters.AddWithValue("$r", rootId); read.Parameters.AddWithValue("$o", objectId);
        if (binding is not null) read.Parameters.AddWithValue("$b", binding);
        if (read.ExecuteScalar() is not long attempts) return;
        var state = FailureRetryOwner.Failed((int)Math.Min(int.MaxValue, attempts), DateTimeOffset.FromUnixTimeMilliseconds(NowMs()));
        using var update = StoreSchema.Command(_connection, tx, $"UPDATE {table} SET due=$due,failure_attempts=$attempts WHERE {where};");
        update.Parameters.AddWithValue("$id", jobId); update.Parameters.AddWithValue("$r", rootId); update.Parameters.AddWithValue("$o", objectId);
        if (binding is not null) update.Parameters.AddWithValue("$b", binding);
        update.Parameters.AddWithValue("$due", state.RetryAfter.ToUnixTimeMilliseconds()); update.Parameters.AddWithValue("$attempts", state.Attempts);
        update.ExecuteNonQuery();
        tx.Commit();
    }

    public void FailStorageHydration(StorageHydrationJob job) => InStore(() => FailStorageJob("storage_hydration", job.JobId, job.RootId, job.ObjectId, null));
    public void FailStoragePointer(StorageSettlement job) => InStore(() => FailStorageJob("storage_refresh", job.JobId, job.RootId, job.ObjectId, job.RemoteBinding));
    public void FailStorageSettlement(StorageSettlement job) => InStore(() => FailStorageJob("storage_settlement", job.JobId, job.RootId, job.ObjectId, job.RemoteBinding));

    public void DeferStorageHydration(StorageHydrationJob job, TimeSpan delay) => InStore(() =>
    {
        if (!_storageEnabled) return;
        StoreSchema.Execute(_connection, null, "UPDATE storage_hydration SET due=$due WHERE job_id=$id AND root_id=$r AND object_id=$o;",
            ("$due", NowMs() + Math.Max(0, (long)delay.TotalMilliseconds)), ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId));
    });

    public void DeferStoragePointer(StorageSettlement job, TimeSpan delay) => InStore(() =>
    {
        if (!_storageEnabled) return;
        StoreSchema.Execute(_connection, null, "UPDATE storage_refresh SET due=$due WHERE job_id=$id AND root_id=$r AND object_id=$o AND remote_binding=$b;",
            ("$due", NowMs() + Math.Max(0, (long)delay.TotalMilliseconds)), ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId), ("$b", job.RemoteBinding));
    });

    /// <summary>Item-scoped retry scheduling. A pair-level cause additionally backs off the whole root, so this row
    /// only decides when a single item is worth another rehash after its exact cause is fixed.</summary>
    public void DeferStorageSettlement(StorageSettlement job, TimeSpan delay) => InStore(() =>
    {
        if (!_storageEnabled) return;
        StoreSchema.Execute(_connection, null, "UPDATE storage_settlement SET due=$due WHERE job_id=$id AND root_id=$r AND object_id=$o AND remote_binding=$b;",
            ("$due", NowMs() + Math.Max(0, (long)delay.TotalMilliseconds)), ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId), ("$b", job.RemoteBinding));
    });

    internal bool IsStorageHydrationRequested(StorageHydrationJob job) => InStore(() => _storageEnabled &&
        StoreSchema.Scalar(_connection, null, "SELECT 1 FROM storage_hydration WHERE job_id=$id AND root_id=$r AND object_id=$o AND due=$requested;",
            ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId), ("$requested", StorageDue.UserRequested)) is not null);

    internal bool RequestStorageHydration(StorageHydrationJob job) => InStore(() =>
    {
        if (!_storageEnabled) return false;
        StoreSchema.Execute(_connection, null, "UPDATE storage_hydration SET due=$requested WHERE job_id=$id AND root_id=$r AND object_id=$o;",
            ("$requested", StorageDue.UserRequested), ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId));
        return StoreSchema.Changes(_connection, null) != 0;
    });

    internal StorageClaim TryClaimStorageHydration(StorageHydrationJob job) => InStore(() =>
    {
        if (!_storageEnabled) return StorageClaim.None;
        var now = NowMs();
        StoreSchema.Execute(_connection, null, "UPDATE storage_hydration SET due=$lease WHERE job_id=$id AND root_id=$r AND object_id=$o AND due=$requested;",
            ("$lease", now + StorageDue.ClaimLeaseMs), ("$requested", StorageDue.UserRequested),
            ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId));
        if (StoreSchema.Changes(_connection, null) != 0) return StorageClaim.Requested;
        StoreSchema.Execute(_connection, null, "UPDATE storage_hydration SET due=$lease WHERE job_id=$id AND root_id=$r AND object_id=$o AND due<=$now " +
            "AND NOT EXISTS(SELECT 1 FROM storage_root_backoff b WHERE b.root_id=$r AND b.due>$now);",
            ("$lease", now + StorageDue.ClaimLeaseMs), ("$now", now), ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId));
        return StoreSchema.Changes(_connection, null) != 0 ? StorageClaim.Normal : StorageClaim.None;
    });

    internal bool IsStoragePointerRequested(StorageSettlement job) => InStore(() => _storageEnabled &&
        StoreSchema.Scalar(_connection, null, "SELECT 1 FROM storage_refresh WHERE job_id=$id AND root_id=$r AND object_id=$o AND remote_binding=$b AND due=$requested;",
            ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId), ("$b", job.RemoteBinding), ("$requested", StorageDue.UserRequested)) is not null);

    internal bool RequestStoragePointer(StorageSettlement job) => InStore(() =>
    {
        if (!_storageEnabled) return false;
        StoreSchema.Execute(_connection, null, "UPDATE storage_refresh SET due=$requested WHERE job_id=$id AND root_id=$r AND object_id=$o AND remote_binding=$b;",
            ("$requested", StorageDue.UserRequested), ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId), ("$b", job.RemoteBinding));
        return StoreSchema.Changes(_connection, null) != 0;
    });

    internal StorageClaim TryClaimStoragePointer(StorageSettlement job) => InStore(() =>
    {
        if (!_storageEnabled) return StorageClaim.None;
        var now = NowMs();
        StoreSchema.Execute(_connection, null, "UPDATE storage_refresh SET due=$lease WHERE job_id=$id AND root_id=$r AND object_id=$o AND remote_binding=$b AND due=$requested;",
            ("$lease", now + StorageDue.ClaimLeaseMs), ("$requested", StorageDue.UserRequested),
            ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId), ("$b", job.RemoteBinding));
        if (StoreSchema.Changes(_connection, null) != 0) return StorageClaim.Requested;
        StoreSchema.Execute(_connection, null, "UPDATE storage_refresh SET due=$lease WHERE job_id=$id AND root_id=$r AND object_id=$o AND remote_binding=$b AND due<=$now " +
            "AND NOT EXISTS(SELECT 1 FROM storage_root_backoff b WHERE b.root_id=$r AND b.due>$now);",
            ("$lease", now + StorageDue.ClaimLeaseMs), ("$now", now), ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId), ("$b", job.RemoteBinding));
        return StoreSchema.Changes(_connection, null) != 0 ? StorageClaim.Normal : StorageClaim.None;
    });
}
