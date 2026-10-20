using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed record PairRemovalArchive(string PairId, string ArchivePath, string ManifestPath,
    IReadOnlyDictionary<string, long> RowCounts, IReadOnlyList<string> OrphanedRoots, long UnfinishedEffects);

public sealed partial class SqliteStateStore
{
    /// <summary>Archives a disabled pair before atomically removing its catalog and pair-owned rows.
    /// Root inventories and recovery payloads are never removed. No filesystem/provider effects are issued.</summary>
    public PairRemovalArchive ArchiveAndRemovePair(string pairId, string archiveDirectory, bool dropUnfinished = false) =>
        ArchiveAndRemovePair(pairId, archiveDirectory, dropUnfinished, beforeRemoval: null);

    // The runtime invalidates retained prepares only once the archive is durable, but before catalog removal.
    // If invalidation fails, SQL rolls back; no committed removal can leave an old enclosing-pair prepare usable.
    internal PairRemovalArchive ArchiveAndRemovePair(string pairId, string archiveDirectory, bool dropUnfinished,
        Action? beforeRemoval, Action<string>? beforeArchiveVerification = null,
        IReadOnlyList<string>? invalidatePrepares = null)
    {
        var archiveResult = InStore(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(archiveDirectory);
            // Reserve the writer for the entire snapshot/archive/delete operation, including other connections.
            using var transaction = _connection.BeginTransaction(deferred: false);
            var pairJson = StoreSchema.Scalar(_connection, transaction,
                "SELECT def_json FROM pair WHERE pair_id=$id;", ("$id", pairId)) as string
                ?? throw new ArgumentException($"Unknown pair '{pairId}'. Use an exact pair id.");
            var pair = Deserialize(pairJson);
            if (pair.Enabled)
                throw new InvalidOperationException($"Pair '{pairId}' is enabled. Disable it first with pairs disable --pair {pairId} --yes, then retry pairs remove; or cancel and keep the pair.");
            if (pairId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("The pair id cannot be used as an archive filename.", nameof(pairId));
            var pairNo = (long)StoreSchema.Scalar(_connection, transaction,
                "SELECT pair_no FROM pair WHERE pair_id=$id;", ("$id", pairId))!;
            var unfinished = (long)StoreSchema.Scalar(_connection, transaction,
                "SELECT COUNT(*) FROM pending_effect WHERE pair_no=$p AND state IN ('started','uncertain','failed');",
                ("$p", pairNo))!;
            if (unfinished > 0 && !dropUnfinished)
                throw new InvalidOperationException($"Pair '{pairId}' has {unfinished} unfinished effects (including any folder-delete groups). " +
                    $"Removing it stops their reconciliation. Options: (1) enable with pairs enable --pair {pairId} --yes " +
                    $"(this permits normal syncing), then run --pair {pairId} --yes to reconcile, " +
                    $"then pairs disable --pair {pairId} --yes and retry removal; " +
                    "(2) re-run pairs remove with --drop-unfinished to archive and drop them explicitly; (3) cancel. Nothing was removed.");
            var a = (long)StoreSchema.Scalar(_connection, transaction,
                "SELECT root_no FROM root WHERE root_id=$id;", ("$id", pair.A.RootId))!;
            var b = (long)StoreSchema.Scalar(_connection, transaction,
                "SELECT root_no FROM root WHERE root_id=$id;", ("$id", pair.B.RootId))!;
            var pairTables = new[] { "sync_point", "pending_effect", "notice", "recovery", "deletion_evidence" };
            var selections = new Dictionary<string, string>
            {
                ["pair"] = "pair_no=$p",
                ["prepare_header"] = "pair_id=(SELECT pair_id FROM pair WHERE pair_no=$p)",
                ["prepare_row"] = "generation IN (SELECT generation FROM prepare_header " +
                    "WHERE pair_id=(SELECT pair_id FROM pair WHERE pair_no=$p))",
                ["prepare_cursor"] = "generation IN (SELECT generation FROM prepare_header " +
                    "WHERE pair_id=(SELECT pair_id FROM pair WHERE pair_no=$p))",
                ["root"] = "root_no IN ($a,$b)",
                ["file_state"] = "root_no IN ($a,$b)",
                ["generation"] = "root_no IN ($a,$b)",
                ["tombstone"] = "root_no IN ($a,$b)",
                // dir is globally interned, not root-owned. Include baseline-only and tombstone-only paths too.
                ["dir"] = "dir_no IN (SELECT dir_no FROM file_state WHERE root_no IN ($a,$b) " +
                    "UNION SELECT dir_no FROM tombstone WHERE root_no IN ($a,$b) " +
                    "UNION SELECT a_dir FROM sync_point WHERE pair_no=$p " +
                    "UNION SELECT b_dir FROM sync_point WHERE pair_no=$p)",
            };
            foreach (var table in pairTables) selections[table] = "pair_no=$p";
            var others = new List<PairDef>();
            using (var command = StoreSchema.Command(_connection, transaction, "SELECT def_json FROM pair WHERE pair_no <> $p;"))
            {
                command.Parameters.AddWithValue("$p", pairNo);
                using var reader = command.ExecuteReader();
                while (reader.Read()) others.Add(Deserialize(reader.GetString(0)));
            }
            var orphaned = new[] { pair.A.RootId, pair.B.RootId }.Distinct(StringComparer.Ordinal)
                .Where(id => !others.Any(p => p.A.RootId == id || p.B.RootId == id)).ToArray();
            var directory = Path.GetFullPath(archiveDirectory);
            Directory.CreateDirectory(directory);
            var manifestPath = Path.Combine(directory, "manifest.json");
            // Never overwrite a previous archive's manifest. A fresh archive directory is an inexpensive retry.
            if (File.Exists(manifestPath))
                throw new IOException($"Archive manifest already exists: {manifestPath}. Choose a new archive directory.");
            var archivePath = Path.Combine(directory, $"pair-{pairId}-{_clock.GetUtcNow():yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.db");
            using (new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            var counts = new Dictionary<string, long>();
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = archivePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
            };
            using (var archive = new SqliteConnection(connectionString.ToString()))
            {
                archive.Open();
                StoreSchema.Execute(archive, null, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;");
                StoreSchema.Create(archive);
                using var archiveTransaction = archive.BeginTransaction();
                foreach (var (table, where) in selections)
                {
                    counts[table] = (long)StoreSchema.Scalar(_connection, transaction,
                        $"SELECT COUNT(*) FROM {table} WHERE {where};", ("$p", pairNo), ("$a", a), ("$b", b))!;
                    using var select = StoreSchema.Command(_connection, transaction, $"SELECT * FROM {table} WHERE {where};");
                    select.Parameters.AddWithValue("$p", pairNo);
                    select.Parameters.AddWithValue("$a", a);
                    select.Parameters.AddWithValue("$b", b);
                    using var rows = select.ExecuteReader();
                    using var insert = StoreSchema.Command(archive, archiveTransaction,
                        $"INSERT INTO {table} ({string.Join(",", Enumerable.Range(0, rows.FieldCount).Select(i => rows.GetName(i)))}) " +
                        $"VALUES ({string.Join(",", Enumerable.Range(0, rows.FieldCount).Select(i => "$v" + i))});");
                    for (var i = 0; i < rows.FieldCount; i++) insert.Parameters.AddWithValue("$v" + i, DBNull.Value);
                    while (rows.Read())
                    {
                        for (var i = 0; i < rows.FieldCount; i++) insert.Parameters[i].Value = rows.GetValue(i);
                        insert.ExecuteNonQuery();
                    }
                }
                archiveTransaction.Commit();
            }
            // Offline fault-injection seam: production callers never supply this callback.
            beforeArchiveVerification?.Invoke(archivePath);
            // Reopen the standalone file, not a connection's uncommitted view or WAL sidecar.
            connectionString.Mode = SqliteOpenMode.ReadOnly;
            using (var archive = new SqliteConnection(connectionString.ToString()))
            {
                archive.Open();
                if (!Equals(StoreSchema.Scalar(archive, null, "PRAGMA integrity_check;"), "ok"))
                    throw new IOException("Pair archive failed SQLite integrity verification; nothing was removed.");
                foreach (var (table, count) in counts)
                    if ((long)StoreSchema.Scalar(archive, null, $"SELECT COUNT(*) FROM {table};")! != count)
                        throw new IOException($"Pair archive count mismatch for {table}; nothing was removed.");
            }
            var result = new PairRemovalArchive(pairId, archivePath, manifestPath, counts, orphaned, unfinished);
            using (var manifest = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(manifest, new { Archive = result, RootDataRetained = true,
                    RemovalStatus = "Archive verified before removal; live transaction may not yet have committed." },
                    new JsonSerializerOptions { WriteIndented = true });
                manifest.Flush(flushToDisk: true);
            }
            beforeRemoval?.Invoke();
            // Prepare cleanup shares this transaction; ClearPrepare would start a nested transaction.
            DeletePrepare(transaction, pairId);
            foreach (var affectedPair in invalidatePrepares ?? []) DeletePrepare(transaction, affectedPair);
            foreach (var table in pairTables)
                StoreSchema.Execute(_connection, transaction, $"DELETE FROM {table} WHERE pair_no=$p;", ("$p", pairNo));
            StoreSchema.Execute(_connection, transaction, "DELETE FROM pair WHERE pair_no=$p;", ("$p", pairNo));
            // Enclosing roots must perform a full scan of the newly admitted subtree.
            foreach (var rootId in others.SelectMany(p => new[] { p.A, p.B })
                .Where(root => PairAdmission.NestedPath(root, pair.A) is not null || PairAdmission.NestedPath(root, pair.B) is not null)
                .Select(root => root.RootId).Distinct(StringComparer.Ordinal))
                StoreSchema.Execute(_connection, transaction, "UPDATE root SET cursor=NULL WHERE root_id=$id;", ("$id", rootId));
            transaction.Commit();
            _pairNo.Remove(pairId);
            _rootSides.Clear();
            return result;
        });
        // The archive snapshot and catalog removal are finished before any batched row cleanup.
        TrySweepPrepareGenerations();
        return archiveResult;
    }
}
