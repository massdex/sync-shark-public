using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Owns atomic updates to synced bindings and their publication evidence.</summary>
public sealed class Committer
{
    private const string PairRow = "(SELECT id FROM pair WHERE pair_id=$p)";
    private const string KeyOf = "pathkey(";

    /// <summary>Settle a binding and the exact spent historical name in one transaction.</summary>
    public async Task CommitAsync(SqliteConnection db, string pairId, SyncedEntry? before, SyncedEntry? after,
        JournalPosition? publication, string? spentDeletedKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var tx = db.BeginTransaction(deferred: false);
        await ApplyAsync(tx, pairId, before, after, publication, spentDeletedKey, ct);
        tx.Commit();
    }

    private readonly Queue<(Func<SqliteTransaction,Task> Apply, Action Published, TaskCompletionSource Done)> pending = new();
    private bool draining;

    internal Task QueueAsync(SqliteConnection db, SemaphoreSlim gate, int batchSize, string pairId, SyncedEntry? before,
        SyncedEntry? after, JournalPosition? publication, string? spentDeletedKey, Action published, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pending)
        {
            pending.Enqueue((tx => ApplyAsync(tx, pairId, before, after, publication, spentDeletedKey, ct), published, done));
            if (draining) return done.Task;
            draining = true;
        }
        _ = Task.Run(DrainAsync);
        return done.Task;

        async Task DrainAsync()
        {
            while (true)
            {
                List<(Func<SqliteTransaction,Task> Apply, Action Published, TaskCompletionSource Done)> batch = [];
                lock (pending)
                {
                    while (batch.Count < batchSize && pending.TryDequeue(out var item)) batch.Add(item);
                    if (batch.Count == 0) { draining = false; return; }
                }
                await gate.WaitAsync();
                try
                {
                    try { await FlushAsync(batch); }
                    catch (Exception e) when (batch.Count > 1 && e is OperationCanceledException or ChangedUnderneathException or InvalidDataException or SqliteException { SqliteErrorCode: 19 })
                    {
                        // One rejected item must not repeatedly roll back its independent neighbors.
                        foreach (var item in batch)
                            try { await FlushAsync([item]); } catch (Exception error) { item.Done.TrySetException(SqliteFull.Normalize(error)); }
                    }
                }
                catch (Exception e) { foreach (var item in batch) item.Done.TrySetException(SqliteFull.Normalize(e)); }
                finally { gate.Release(); }
            }
        }

        async Task FlushAsync(List<(Func<SqliteTransaction,Task> Apply, Action Published, TaskCompletionSource Done)> batch)
        {
            using var tx = db.BeginTransaction(deferred:false);
            foreach (var item in batch) await item.Apply(tx);
            tx.Commit();
            foreach (var item in batch) item.Published();
            foreach (var item in batch) item.Done.TrySetResult();
        }
    }

    private static async Task ApplyAsync(SqliteTransaction tx, string pairId, SyncedEntry? before, SyncedEntry? after,
        JournalPosition? publication, string? spentDeletedKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (after is null) await RemoveAsync(tx, pairId, before, ct);
        else await WriteAsync(tx, pairId, before, after, publication, ct, replacing:before != null);
        if (spentDeletedKey != null)
        {
            using var spend = StorageCommand.Create(tx.Connection!, tx, "INSERT INTO discovery_deleted_delta(pair_id,path_key,spent) VALUES(" + PairRow +
                ",$key,1) ON CONFLICT(pair_id,path_key) DO UPDATE SET spent=1", ("$p",pairId), ("$key",spentDeletedKey));
            await spend.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Replace the old binding, insert both surviving links, and publish their notice together.</summary>
    public async Task CommitKeepBothAsync(SqliteConnection db, string pairId, SyncedEntry? before, SyncedEntry first,
        SyncedEntry? second, Notice notice, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var tx = db.BeginTransaction(deferred: false);
        await RemoveAsync(tx, pairId, before, ct);
        await WriteAsync(tx, pairId, before, first, null, ct, replacing:false);
        if (second != null) await WriteAsync(tx, pairId, before, second, null, ct, replacing:false);
        await new NoticeTable().SaveNoticeAsync(tx, pairId, notice, ct);
        tx.Commit();
    }

    private static async Task RemoveAsync(SqliteTransaction tx, string pairId, SyncedEntry? before, CancellationToken ct)
    {
        if (before == null) return;
        using var cmd = StorageCommand.Create(tx.Connection!, tx,
            "DELETE FROM synced_entry WHERE pair_id=" + PairRow + " AND pathkey(local_path)=$key AND local_id=$id AND dropbox_id=$remote",
            ("$p",pairId), ("$key",before.LocalKey), ("$id",Encoding.LocalId(before.LocalId)), ("$remote",before.DropboxId));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new ChangedUnderneathException(before.LocalPath);
    }

    private static async Task WriteAsync(SqliteTransaction tx, string pairId, SyncedEntry? before, SyncedEntry entry,
        JournalPosition? publication, CancellationToken ct, bool replacing)
    {
        if (PathNames.Key(entry.LocalPath) != entry.LocalKey) throw new InvalidDataException(Text.Get(TextKey.StateLocalKeyInvalid, (TextArg.Path,entry.LocalPath)));
        var boundary = entry.IsFolder ? null : publication ?? (before?.LocalId == entry.LocalId ? entry.LocalPublished : null);
        // An update is one row write even for a move; an explicit key and an inherited key share a logical namespace.
        const string noConflict = """
             NOT EXISTS(SELECT 1 FROM synced_entry WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND dropbox_key_delta=$dk AND ($old IS NULL OR pathkey(local_path)!=$old)
                UNION ALL SELECT 1 FROM synced_entry WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND pathkey(local_path)=$dk AND dropbox_key_delta IS NULL AND ($old IS NULL OR pathkey(local_path)!=$old))
            """;
        var sql = replacing ? """
            UPDATE synced_entry SET local_path=$path,dropbox_key_delta=nullif($dk,$lk),dropbox_path_delta=nullif($dp,$path),
                is_folder=$folder,local_id=$lid,local_size=$size,local_mtime=$mtime,dropbox_id=$did,dropbox_hash=$hash,
                local_published_journal=$journal,local_published_usn=$usn
            WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND pathkey(local_path)=$old AND local_id=$oldId AND dropbox_id=$oldRemote AND
            """ : """
            INSERT INTO synced_entry(pair_id,local_path,dropbox_key_delta,dropbox_path_delta,is_folder,local_id,local_size,
                local_mtime,dropbox_id,dropbox_hash,local_published_journal,local_published_usn)
            SELECT (SELECT id FROM pair WHERE pair_id=$p),$path,nullif($dk,$lk),nullif($dp,$path),$folder,$lid,$size,$mtime,$did,$hash,$journal,$usn WHERE
            """;
        using var insert = StorageCommand.Create(tx.Connection!, tx, sql + " " + noConflict, ("$old",replacing ? before!.LocalKey : null), ("$p",pairId), ("$path",entry.LocalPath), ("$dk",entry.DropboxKey), ("$lk",entry.LocalKey), ("$dp",entry.DropboxPath),
            ("$folder",entry.IsFolder), ("$lid",Encoding.LocalId(entry.LocalId)), ("$size",entry.LocalSize), ("$mtime",entry.LocalModifiedTicks),
            ("$did",entry.DropboxId), ("$hash",Encoding.Hash(entry.DropboxContentHash)), ("$journal",unchecked((long?)boundary?.JournalId)),
            ("$usn",unchecked((long?)boundary?.NextUsn)));
        if (replacing)
        {
            insert.Parameters.AddWithValue("$oldId", Encoding.LocalId(before!.LocalId)!);
            insert.Parameters.AddWithValue("$oldRemote",before.DropboxId);
        }
        if (await insert.ExecuteNonQueryAsync(ct) != 1) throw new ChangedUnderneathException(entry.DropboxPath);
    }

    /// <summary>Rebase both mapped descendant ranges from the same frozen old rows.</summary>
    public async Task ProjectSubtreeAsync(SqliteConnection db, string pairId, Scope from, Scope to, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var tx = db.BeginTransaction(deferred: false);
        using var cmd = StorageCommand.Create(db, tx, RebaseDescendantsBoth, ("$p",pairId),
            ("$oldLocalPath",from.LocalPath), ("$oldLocalKey",PathNames.Key(from.LocalPath)),
            ("$newLocalPath",to.LocalPath), ("$newLocalKey",PathNames.Key(to.LocalPath)),
            ("$oldDropboxPath",from.DropboxPath), ("$oldDropboxKey",PathNames.DropboxKey(from.DropboxPath)),
            ("$newDropboxPath",to.DropboxPath), ("$newDropboxKey",PathNames.DropboxKey(to.DropboxPath)));
        await cmd.ExecuteNonQueryAsync(ct);
        using var collision = StorageCommand.Create(db, tx, ClashInDropboxSubtree, ("$p",pairId), ("$k",PathNames.DropboxKey(to.DropboxPath)));
        if (Convert.ToInt64(await collision.ExecuteScalarAsync(ct)) != 0) throw new ChangedUnderneathException(to.DropboxPath);
        tx.Commit();
    }
    private const string ClashInDropboxSubtree = "SELECT EXISTS(\r\n  SELECT 1 FROM synced_entry a INDEXED BY synced_dropbox_key\r\n  CROSS JOIN synced_entry b INDEXED BY synced_local_key\r\n  WHERE a.pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND a.dropbox_key_delta >= $k || '/' AND a.dropbox_key_delta < $k || '0'\r\n    AND b.pair_id = a.pair_id AND " + KeyOf + "b.local_path) = +a.dropbox_key_delta AND b.dropbox_key_delta IS NULL\r\n  UNION ALL\r\n  SELECT 1 FROM synced_entry a INDEXED BY synced_local_key\r\n  CROSS JOIN synced_entry b INDEXED BY synced_dropbox_key\r\n  WHERE a.pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND " + KeyOf + "a.local_path) >= $k || '/' AND " + KeyOf + "a.local_path) < $k || '0'\r\n    AND a.dropbox_key_delta IS NULL AND b.pair_id = a.pair_id AND b.dropbox_key_delta = " + KeyOf + "a.local_path));";
    private const string RebaseDescendantsBoth = """
        WITH old_rows AS MATERIALIZED (
          SELECT rowid AS id, local_path, dropbox_path, dropbox_key_delta,
        """ + KeyOf + "local_path) AS local_key FROM synced_entry WHERE rowid IN (" +
        "SELECT rowid FROM synced_entry INDEXED BY synced_local_key WHERE pair_id = " + PairRow +
        " AND " + KeyOf + "local_path) >= $oldLocalKey || '/' AND " + KeyOf + "local_path) < $oldLocalKey || '0' " +
        "UNION ALL SELECT rowid FROM synced_entry INDEXED BY synced_dropbox_key WHERE pair_id = " + PairRow +
        " AND dropbox_key_delta >= $oldDropboxKey || '/' AND dropbox_key_delta < $oldDropboxKey || '0' " +
        "UNION ALL SELECT rowid FROM synced_entry INDEXED BY synced_local_key WHERE pair_id = " + PairRow +
        " AND " + KeyOf + "local_path) >= $oldDropboxKey || '/' AND " + KeyOf + "local_path) < $oldDropboxKey || '0' AND dropbox_key_delta IS NULL)), " + """
        final_rows AS MATERIALIZED (
          SELECT id,
            CASE WHEN local_key >= $oldLocalKey || '/' AND local_key < $oldLocalKey || '0'
              THEN $newLocalPath || substr(local_path, length($oldLocalPath) + 1)
              ELSE local_path END AS local_path,
            CASE WHEN local_key >= $oldLocalKey || '/' AND local_key < $oldLocalKey || '0'
              THEN $newLocalKey || substr(local_key, length($oldLocalKey) + 1)
              ELSE local_key END AS local_key,
            CASE WHEN coalesce(dropbox_key_delta, local_key) >= $oldDropboxKey || '/'
                AND coalesce(dropbox_key_delta, local_key) < $oldDropboxKey || '0'
              THEN $newDropboxPath || substr(dropbox_path, length($oldDropboxPath) + 1)
              ELSE dropbox_path END AS dropbox_path,
            CASE WHEN coalesce(dropbox_key_delta, local_key) >= $oldDropboxKey || '/'
                AND coalesce(dropbox_key_delta, local_key) < $oldDropboxKey || '0'
              THEN $newDropboxKey || substr(coalesce(dropbox_key_delta, local_key), length($oldDropboxKey) + 1)
              ELSE coalesce(dropbox_key_delta, local_key) END AS dropbox_key
          FROM old_rows)
        UPDATE synced_entry SET local_path = f.local_path,
          dropbox_key_delta = nullif(f.dropbox_key, f.local_key),
          dropbox_path_delta = nullif(f.dropbox_path, f.local_path)
        FROM final_rows f WHERE synced_entry.rowid = f.id;
        """;
}
