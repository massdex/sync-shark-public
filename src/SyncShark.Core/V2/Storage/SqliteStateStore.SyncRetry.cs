using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Keyset paging releases the connection between batches. No million-row retry snapshot.
    public IEnumerable<SyncRetry> ReadSyncRetries(string pairId) => ReadSyncRetriesCore(pairId, null);

    public IEnumerable<SyncRetry> ReadRetriesMissingPrepareMatch(string pairId)
    {
        foreach (var kind in new[] { "sync", "first-sync", "post-preview" })
            foreach (var retry in ReadSyncRetriesCore(pairId, kind))
                yield return retry;
    }

    private IEnumerable<SyncRetry> ReadSyncRetriesCore(string pairId, string? missingMatchKind)
    {
        var after = "";
        var first = true;
        while (true)
        {
            var batch = InStore(() =>
            {
                var rows = new List<(string Key, SyncRetry Retry)>();
                using var command = StoreSchema.Command(_connection, null,
                    "SELECT path_key,json FROM sync_retry" + (missingMatchKind is null ? "" : " INDEXED BY sync_retry_kind") +
                    " WHERE pair_no=$p AND path_key" + (first ? ">=" : ">") + "$after" +
                    (missingMatchKind is null ? "" : " AND json_extract(json,'$.Kind')=$kind AND json_extract(json,'$.Match') IS NULL") +
                    " ORDER BY path_key LIMIT 256;");
                if (missingMatchKind is not null) command.Parameters.AddWithValue("$kind", missingMatchKind);
                command.Parameters.AddWithValue("$p", RequirePair(pairId));
                command.Parameters.AddWithValue("$after", after);
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.Add((reader.GetString(0), JsonSerializer.Deserialize<SyncRetry>(reader.GetString(1), RowCodec.Json)!));
                return rows;
            });
            if (batch.Count == 0) yield break;
            foreach (var row in batch) yield return row.Retry;
            after = batch[^1].Key;
            first = false;
        }
    }

    public IEnumerable<string> ReadSyncRetryPaths(string pairId)
    {
        var after = "";
        var first = true;
        while (true)
        {
            var batch = InStore(() =>
            {
                var rows = new List<string>();
                using var command = StoreSchema.Command(_connection, null,
                    "SELECT path_key FROM sync_retry WHERE pair_no=$p AND path_key" + (first ? ">=" : ">") + "$after" +
                    RetryProtectionPredicate.Sql() + " ORDER BY path_key LIMIT 256;");
                command.Parameters.AddWithValue("$p", RequirePair(pairId));
                command.Parameters.AddWithValue("$after", after);
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.Add(reader.GetString(0));
                return rows;
            });
            if (batch.Count == 0) yield break;
            foreach (var path in batch) yield return path;
            after = batch[^1];
            first = false;
        }
    }

    public bool SyncRetryExists(string pairId, string path) => InStore(() =>
        StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM sync_retry WHERE pair_no=$p AND path_key=$k" + RetryProtectionPredicate.Sql() + ";",
            ("$p", RequirePair(pairId)), ("$k", RowCodec.Key(path))) is not null);
    public void RequestSyncRetryNow(string pairId, string path) => InIndexingStore(() =>
    {
        // Read/modify/write under the same store gate: never resurrect a row completed by a worker.
        if (SyncRetryAt(pairId, path) is { } retry)
            SaveSyncRetry(pairId, retry with { RetryAfter = DateTimeOffset.MinValue });
    });

    public void SaveSyncRetry(string pairId, SyncRetry retry) => InIndexingStore(() =>
        StoreSchema.Execute(_connection, null,
            "INSERT INTO sync_retry(pair_no,path_key,json,retry_after) VALUES($p,$key,$json,$due) " +
            "ON CONFLICT(pair_no,path_key) DO UPDATE SET json=excluded.json,retry_after=excluded.retry_after;",
            ("$p", RequirePair(pairId)), ("$key", RowCodec.Key(retry.Path)), ("$due", retry.RetryAfter.ToUnixTimeMilliseconds()),
            ("$json", JsonSerializer.Serialize(retry, RowCodec.Json))));

    // Result commit owns the exact entity diagnosis; display samples must never be the only copy.
    // This adapter records no second attempt: the run failure exit advances the shared counter.
    private void SaveResultFailureCause(SqliteTransaction transaction, long pairNo, SyncAction action, ActionResult result)
    {
        if (action.CallerOwnsNotice || result.Outcome is not (Outcome.Failed or Outcome.Uncertain)) return;
        var key = RowCodec.Key(action.Path);
        var json = StoreSchema.Scalar(_connection, transaction,
            "SELECT json FROM sync_retry WHERE pair_no=$p AND path_key=$k;", ("$p", pairNo), ("$k", key)) as string;
        var old = json is null ? null : JsonSerializer.Deserialize<SyncRetry>(json, RowCodec.Json);
        if (old?.Kind == "decision") return;
        var retry = (old ?? new SyncRetry(action.Path, "sync", 0, "", DateTimeOffset.MinValue)) with
        { LastError = result.Reason ?? "The item could not be confirmed or applied.",
            FailureKind = FailureRetryOwner.ClassifyCause(result.Reason ?? "The item could not be confirmed or applied.") };
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO sync_retry(pair_no,path_key,json,retry_after) VALUES($p,$k,$j,$due) " +
            "ON CONFLICT(pair_no,path_key) DO UPDATE SET json=excluded.json;",
            ("$p", pairNo), ("$k", key), ("$j", JsonSerializer.Serialize(retry, RowCodec.Json)),
            ("$due", retry.RetryAfter.ToUnixTimeMilliseconds()));
    }

    public SyncRetry? SyncRetryAt(string pairId, string path) => InStore(() =>
    {
        var json = StoreSchema.Scalar(_connection, null,
            "SELECT json FROM sync_retry WHERE pair_no=$p AND path_key=$k;",
            ("$p", RequirePair(pairId)), ("$k", RowCodec.Key(path))) as string;
        return json is null ? null : JsonSerializer.Deserialize<SyncRetry>(json, RowCodec.Json);
    });

    public IReadOnlyList<SyncRetry> DueSyncRetries(string pairId, DateTimeOffset now, int limit) => InStore(() =>
    {
        var rows = new List<SyncRetry>();
        using var command = StoreSchema.Command(_connection, null,
            // The millisecond index narrows candidates only; exact authority is checked before LIMIT.
            "SELECT json FROM sync_retry WHERE pair_no=$p AND retry_after<=$now " +
            "AND sync_retry_due(json_extract(json,'$.RetryAfter'),$nowTicks) ORDER BY retry_after,path_key LIMIT $limit;");
        command.Parameters.AddWithValue("$p", RequirePair(pairId));
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$nowTicks", now.UtcTicks);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 256));
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<SyncRetry>(reader.GetString(0), RowCodec.Json)!);
        return (IReadOnlyList<SyncRetry>)rows;
    });

    // An indefinite pair-wide decision hold (Kind decision at the root path) waits for the user; it is not scheduling
    // debt, so it never makes a completed run look deferred. A root decision still carrying PendingNotice failed to
    // write its notice, so it still needs the fast retry and counts. Real failures and deletion evidence still count.
    public int CountSyncRetries(string pairId) => InStore(() => (int)Math.Min(int.MaxValue,
        (long)StoreSchema.Scalar(_connection, null,
            "SELECT COUNT(*) FROM sync_retry WHERE pair_no=$p AND retry_after<$held " +
            "AND NOT (path_key=$root AND json_extract(json,'$.Kind')='decision' " +
            "AND json_extract(json,'$.PendingNotice') IS NULL);",
            ("$p", RequirePair(pairId)), ("$held", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()),
            ("$root", RowCodec.Key(string.Empty)))!));

    public bool RetryProtected(string pairId, string path, IReadOnlySet<string> active, bool descendants, bool restoredOnly = false) => InStore(() =>
    {
        var pairNo = RequirePair(pairId);
        var key = RowCodec.Key(path);
        var filter = RetryProtectionPredicate.Sql(restoredOnly);
        if (!descendants)
        {
            foreach (var parent in new[] { path }.Concat(PairPath.Ancestors(path)))
                if (!active.Contains(parent) && StoreSchema.Scalar(_connection, null,
                    "SELECT 1 FROM sync_retry WHERE pair_no=$p AND path_key=$k" + filter + ";",
                    ("$p", pairNo), ("$k", RowCodec.Key(parent))) is not null) return true;
            return false;
        }
        // At most active.Count rows can be ignored. Read only one more than that, never the whole subtree.
        using var command = StoreSchema.Command(_connection, null,
            "SELECT path_key FROM sync_retry WHERE pair_no=$p AND path_key >= $lo AND path_key < $hi " +
            filter + " ORDER BY path_key LIMIT $limit;");
        command.Parameters.AddWithValue("$p", pairNo);
        command.Parameters.AddWithValue("$lo", key.Length == 0 ? "" : key + "/");
        command.Parameters.AddWithValue("$hi", key.Length == 0 ? "￿" : key + "0");
        command.Parameters.AddWithValue("$limit", active.Count + 1);
        using var reader = command.ExecuteReader();
        while (reader.Read()) if (!active.Contains(reader.GetString(0))) return true;
        return false;
    });

    public bool HasActiveRestoredRetries(string pairId) => InStore(() =>
        StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM sync_retry WHERE pair_no=$p" + RetryProtectionPredicate.Sql(restoredOnly: true) + " LIMIT 1;",
            ("$p", RequirePair(pairId))) is not null);

    public void SuspendHeldRetry(string pairId, SyncRetry retry) => InIndexingStore(() =>
    {
        using var transaction = _connection.BeginTransaction();
        var dormant = new SyncRetry(retry.Path, "deletion-evidence", 0, retry.LastError,
            DateTimeOffset.MaxValue, Held: retry.Held, ReprobeEvidence: true);
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO sync_retry(pair_no,path_key,json,retry_after) VALUES($p,$k,$j,$due) " +
            "ON CONFLICT(pair_no,path_key) DO UPDATE SET json=excluded.json,retry_after=excluded.retry_after;",
            ("$p", RequirePair(pairId)), ("$k", RowCodec.Key(retry.Path)),
            ("$j", JsonSerializer.Serialize(dormant, RowCodec.Json)), ("$due", dormant.RetryAfter.ToUnixTimeMilliseconds()));
        // Close the derived notice ids exactly as ClearSyncRetry does, so the update uses nt_open and
        // never rewrites the choice of an already-closed notice.
        _notices.CloseWhere(transaction, "exclude",
            "notice_id=$id AND " + NoticeTable.ActionableSql("state") + ";",
            ("$id", ShortId.Make("n", pairId, "40", RowCodec.Key(retry.Path), "sync-retry")));
        foreach (var rule in new[] { "4", "19" })
            _notices.CloseWhere(transaction, "exclude",
                "notice_id=$id AND " + NoticeTable.ActionableSql("state") + ";",
                ("$id", ShortId.Make("n", pairId, rule, RowCodec.Key(retry.Path), "deletion-evidence-pending")));
        transaction.Commit();
    });

    public void ClearSyncRetry(string pairId, string path) => InIndexingStore(() =>
    {
        using var transaction = _connection.BeginTransaction();
        var pairNo = RequirePair(pairId);
        StoreSchema.Execute(_connection, transaction, "DELETE FROM sync_retry WHERE pair_no=$p AND path_key=$key;",
            ("$p", pairNo), ("$key", RowCodec.Key(path)));
        CloseRetryNotices(transaction, pairId, path);
        _notices.CloseWhere(transaction, "resolved",
            "notice_id=$id;",
            ("$id", DeleteSourceGuard.NoticeId(pairId, path)));
        _notices.CloseWhere(transaction, "resolved",
            "notice_id=$id;",
            ("$id", ShortId.Make("n", pairId, NoticeChoices.DeletionReviewRule.ToString(System.Globalization.CultureInfo.InvariantCulture), RowCodec.Key(path), "deletion-review")));
        transaction.Commit();
    });

    /// <summary>
    /// The keys in this resolved batch that hold the exact parked shape (Kind sync, no held evidence, deadline at
    /// MaxValue). One indexed query for the whole batch; every other retry owner keeps its row. Runs inside the
    /// caller's commit transaction, so resolution and unparking cannot split.
    /// </summary>
    private HashSet<string> ParkedSyncRetryKeys(SqliteTransaction transaction, long pairNo,
        IReadOnlyList<string> paths)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var wanted = paths.Select(RowCodec.Key).Distinct(StringComparer.Ordinal).ToArray();
        using var command = StoreSchema.Command(_connection, transaction,
            "SELECT path_key,json FROM sync_retry WHERE pair_no=$p AND path_key IN (SELECT value FROM json_each($paths));");
        command.Parameters.AddWithValue("$p", pairNo);
        command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(wanted));
        var found = new List<(string Key, string Json)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) found.Add((reader.GetString(0), reader.GetString(1)));
        foreach (var (key, json) in found)
            if (JsonSerializer.Deserialize<SyncRetry>(json, RowCodec.Json) is { Kind: "sync", Held: null } retry &&
                retry.RetryAfter == DateTimeOffset.MaxValue)
                keys.Add(key);
        return keys;
    }

    private void DeleteParkedSyncRetry(SqliteTransaction transaction, long pairNo, string pairId, string path)
    {
        StoreSchema.Execute(_connection, transaction, "DELETE FROM sync_retry WHERE pair_no=$p AND path_key=$key;",
            ("$p", pairNo), ("$key", RowCodec.Key(path)));
        CloseRetryNotices(transaction, pairId, path);
    }

    private void CloseRetryNotices(SqliteTransaction transaction, string pairId, string path)
    {
        _notices.CloseWhere(transaction, "resolved", "notice_id=$id;",
            ("$id", NoticeTable.Id(new Notice(pairId, 30, NoticeKind.Info, path, "", "unreadable"))));
        _notices.CloseWhere(transaction, "resolved",
            "notice_id=$id;",
            ("$id", ShortId.Make("n", pairId, "40", RowCodec.Key(path), "sync-retry")));
        foreach (var rule in new[] { "4", "19" })
            _notices.CloseWhere(transaction, "resolved",
                "notice_id=$id;",
                ("$id", ShortId.Make("n", pairId, rule, RowCodec.Key(path), "deletion-evidence-pending")));
    }
}
