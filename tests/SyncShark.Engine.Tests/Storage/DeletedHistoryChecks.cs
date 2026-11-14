using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Retained discovery, bounded block decoding, upgrade obligations and transactional delta folding.</summary>
public static class DeletedHistoryChecks
{
    private const string PairId = "a";
    private const string Cursor = "history-cursor";

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var count in new[] { 0, 1, 4999, 5000, 5001, 10001, 10000, 20000 })
                RoundTrip(Path.Combine(folder, "keys-" + count + ".db"), count);
            Corruption(Path.Combine(folder, "corrupt.db"));
            foreach (var version in new[] { 7, 8 }) Migration(Path.Combine(folder, "v" + version + ".db"), version);
            DeltaFold(Path.Combine(folder, "delta.db"));
            Incremental(Path.Combine(folder, "incremental.db"));
            DeltaSize(Path.Combine(folder, "delta-size.db"));
            CountWork(Path.Combine(folder, "counts.db"));
            LongKeys(Path.Combine(folder, "long-keys.db"));
            SpentOverlay(Path.Combine(folder, "spent.db"));
            IdentityLifetime(Path.Combine(folder, "identity.db"));
            SpentMigration(Path.Combine(folder, "v9.db"));
            Retirement(Path.Combine(folder, "retirement.db"));
            FakeAtomicity();
            SpentCosts();
            Plans();
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static PairConfig Pair(string id = PairId) => new(id, "Pair A", "X:/Parent", "/Parent",
        "account", [], [], FirstRunPolicy.NewestWins, true, null);

    private static SqliteConnection Connection(StateStore store) => (SqliteConnection)typeof(StateStore)
        .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    internal static void CorruptSavedHistory(StateStore store) =>
        Execute(Connection(store), "UPDATE discovery_deleted_block SET names=x'010203';");

    private static long Generation(IStateStore store) => store.ReadPairExecution(PairId)!.Value.Generation;

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        using var command = StorageCommand.Create(connection, null, sql, args);
        command.ExecuteNonQuery();
    }

    private static long Number(SqliteConnection connection, string sql)
    {
        using var command = StorageCommand.Create(connection, null, sql);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string[] Keys(SqliteConnection connection, string sql)
    {
        using var command = StorageCommand.Create(connection, null, sql);
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys.ToArray();
    }

    private static void Seed(SqliteConnection connection, IEnumerable<string> keys)
    {
        using var transaction = connection.BeginTransaction();
        using var command = StorageCommand.Create(connection, transaction, StateSchema.DeletedNameInsert,
            (StateSchema.ParamPairRow, 1L), (StateSchema.ParamPath, string.Empty));
        foreach (var key in keys)
        {
            command.Parameters[StateSchema.ParamPath].Value = key;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void RoundTrip(string path, int count)
    {
        string[] expected;
        using (var store = StateStore.Open(path))
        {
            store.SavePair(Pair());
            var token = store.BeginScan(PairId, Generation(store), true);
            var connection = Connection(store);
            Seed(connection, Enumerable.Range(0, count).Select(index => "Parent/" + new string('x', 120) +
                "/Name" + index.ToString("D8", CultureInfo.InvariantCulture)));
            if (count == 1)
                Seed(connection, ["A", "A.B", "A/B", "A0", "\u00e9/\u96ea", "e\u0301", "\ue000", "\U00010000", "broken\ud800",
                    new string('\u754c', SyncEndpointContext.MaximumPathLength)]);
            expected = Keys(connection, "SELECT hex(path_key) FROM scan_state.deleted_name ORDER BY path_key;");
            store.SaveDeletedHistory(PairId, token, Cursor);
            Check.Require(!store.DeletedHistoryOwed(PairId), "Publication clears the durable listing obligation.");
            var blocks = Number(connection, "SELECT count(*) FROM discovery_deleted_block;");
            Check.Require(blocks == (expected.Length + StateSchema.DeletedHistoryBatchSize - 1) / StateSchema.DeletedHistoryBatchSize,
                "The exact boundary sizes produce the expected number of blocks.");
            if (count is 10000 or 20000)
                Console.WriteLine("HISTORY-SIZE keys=" + count + " blocks=" + blocks + " blobBytes=" +
                    Number(connection, "SELECT coalesce(sum(length(names)),0) FROM discovery_deleted_block;") +
                    " databaseBytes=" + new FileInfo(path).Length);
        }
        using (var store = StateStore.Open(path))
        {
            foreach (var key in Keys(Connection(store), "SELECT last_key FROM discovery_deleted_block ORDER BY last_key;"))
                Check.Require(store.SavedDeletedNameExact(PairId, key), "Completed history membership survives reopen without an active scan.");
            Check.Require(!store.SavedDeletedNameExact(PairId, "Missing"), "Completed empty/nonmatching history is not a match.");
            var token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.RestoreDeletedHistory(PairId, token) == Cursor &&
                expected.SequenceEqual(Keys(Connection(store), "SELECT hex(path_key) FROM scan_state.deleted_name ORDER BY path_key;")),
                "Roundtrip preserves SQLite UTF-8 bytes, BINARY order, long keys and surrogate binding normalization.");
        }
    }

    private static byte[] Payload(Action<BinaryWriter> write)
    {
        using var output = new MemoryStream();
        using (var stream = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8)) write(writer);
        return output.ToArray();
    }

    private static void Corruption(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var connection = Connection(store);
        var cases = new (byte[] Bytes, int Count, string Last)[]
        {
            (Payload(writer => { writer.Write("A"); writer.Write("A"); writer.Write("C"); }), 3, "C"),
            (Payload(writer => { writer.Write("C"); writer.Write("A"); }), 2, "A"),
            (Payload(writer => writer.Write("A")), 2, "A"),
            (Payload(writer => { writer.Write("A"); writer.Write("B"); }), 1, "A"),
            (Payload(writer => writer.Write("A")), 1, "B"),
            (Payload(writer => writer.Write(new byte[] { 255, 255, 255, 255, 255 })), 1, "Z"),
            (Payload(writer => { writer.Write7BitEncodedInt(10); writer.Write((byte)'A'); }), 1, "A"),
            (Payload(writer => { writer.Write7BitEncodedInt(StateEncoding.MaximumDeletedKeyBytes + 1);
                writer.Write(new byte[StateEncoding.MaximumDeletedKeyBytes + 1]); }), 1, "Z"),
            (Payload(writer => writer.Write("A"))[..^1], 1, "A"),
            (Payload(writer => writer.Write("A"))[..^4], 1, "A"),
            (new byte[] { 1, 2, 3 }, 1, "A")
        };
        foreach (var item in cases)
        {
            Execute(connection, "DELETE FROM discovery_deleted_block;UPDATE pair SET deleted_history_cursor=$cursor,deleted_history_owed=0;",
                ("$cursor", Cursor));
            Execute(connection, StateSchema.SaveDeletedBlock, ("$pair", 1L), ("$path", item.Last),
                ("$count", item.Count), ("$names", item.Bytes));
            var token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.RestoreDeletedHistory(PairId, token) is null && store.DeletedHistoryOwed(PairId) &&
                !store.HasDeletedNames(PairId) && store.Issues(PairId).Any(issue => issue.Cause == TextKey.DeletedHistoryCorrupt),
                "Codec corruption rolls back scratch, invalidates the acquisition and publishes a visible diagnostic.");
        }
        Execute(connection, "DELETE FROM discovery_deleted_block;UPDATE pair SET deleted_history_cursor=$cursor,deleted_history_owed=0;",
            ("$cursor", Cursor));
        foreach (var keys in new[] { new[] { "A", "C" }, new[] { "B", "D" } })
            Execute(connection, StateSchema.SaveDeletedBlock, ("$pair", 1L), ("$path", keys[^1]),
                ("$count", keys.Length), ("$names", StateEncoding.EncodeDeletedNames(keys)));
        var overlapping = store.BeginScan(PairId, Generation(store), true);
        Check.Require(store.RestoreDeletedHistory(PairId, overlapping) is null && !store.HasDeletedNames(PairId),
            "Cross-block overlap is invalid even when each block is individually sorted and unique.");
    }

    private static void Migration(string path, int version)
    {
        using (var connection = StateStore.Connect(path, SqliteOpenMode.ReadWriteCreate))
        {
            Execute(connection, StateSchema.Pragmas + StateSchema.Create + StateSchema.SchemaAdditions + StateSchema.DeletedHistorySchema +
                (version == 8 ? StateSchema.RebuildIssues : string.Empty));
            foreach (var id in new[] { "a", "b" })
                Execute(connection, StateSchema.UpsertPair, ("$p", id), ("$def", JsonSerializer.Serialize(Pair(id))));
            Execute(connection, StateSchema.SeedSources, (StateSchema.ParamBootstrap, "Bootstrap"), (StateSchema.ParamNormal, "NormalRepair"));
            Execute(connection, "UPDATE pair SET first_repair_done=1;UPDATE pair SET deleted_history_cursor=$cursor;UPDATE source_state SET repair_reason=NULL;",
                ("$cursor", Cursor));
            using (var transaction = connection.BeginTransaction())
            using (var insert = StorageCommand.Create(connection, transaction,
                "INSERT INTO discovery_deleted_name VALUES($pair,$path);", ("$pair", 1), ("$path", string.Empty)))
            {
                foreach (var pair in new[] { 1, 2 })
                for (var index = 0; index <= StateSchema.DeletedHistoryBatchSize; index++)
                {
                    insert.Parameters["$pair"].Value = pair;
                    insert.Parameters["$path"].Value = "Name" + index.ToString("D8", CultureInfo.InvariantCulture);
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            Execute(connection, "PRAGMA user_version=" + version + ";");
        }
        using (var store = StateStore.Open(path))
        {
            var connection = Connection(store);
            Check.Require(Number(connection, "PRAGMA user_version;") == StateSchema.Version &&
                Number(connection, "SELECT sum(name_count) FROM discovery_deleted_block;") == 10002 &&
                Number(connection, "SELECT count(*) FROM sqlite_master WHERE name='discovery_deleted_name';") == 0 &&
                Number(connection, "PRAGMA freelist_count;") == 0 &&
                Keys(connection, "PRAGMA foreign_key_check;").Length == 0,
                "Both populated legacy routes atomically transform every name and reclaim the old pages.");
            foreach (var pairRow in new[] { 1, 2 })
            {
                var decoded = new List<string>();
                using var blocks = StorageCommand.Create(connection, null,
                    "SELECT names,name_count,last_key FROM discovery_deleted_block WHERE pair_id=$pair ORDER BY last_key;", ("$pair", pairRow));
                using var reader = blocks.ExecuteReader();
                string? after = null;
                while (reader.Read())
                {
                    using var payload = new MemoryStream((byte[])reader.GetValue(0));
                    decoded.AddRange(StateEncoding.DecodeDeletedNames(payload, reader.GetInt32(1), reader.GetString(2), after));
                    after = reader.GetString(2);
                }
                Check.Require(decoded.SequenceEqual(Enumerable.Range(0, 5001).Select(index => "Name" + index.ToString("D8", CultureInfo.InvariantCulture))),
                    "Migration preserves every exact key in each pair, not just total counts.");
            }
            Check.Require(store.Pairs().All(pair => store.DeletedHistoryOwed(pair.PairId) &&
                store.ReadSource(pair.PairId, Side.Dropbox)!.Value.Obligation == SourceObligation.NormalRepair),
                "Every migrated pair owes a full listing through the existing independent Repair scheduler.");
            Check.Require(Number(connection, "SELECT count(*) FROM pair WHERE deleted_history_cursor='history-cursor';") == 2,
                "Migration preserves saved cursor bytes but never reuses them instead of the mandatory full listing.");
            var token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.RestoreDeletedHistory(PairId, token) is null,
                "An owed migration listing must be full even when the old acquisition was published.");
        }
        using (var store = StateStore.Open(path))
        {
            Check.Require(store.DeletedHistoryOwed(PairId), "Crash before acquisition completion retains the owed flag.");
            var token = store.BeginScan(PairId, Generation(store), true);
            Seed(Connection(store), ["Fresh"]);
            store.SaveDeletedHistory(PairId, token, "fresh-cursor");
        }
        using (var store = StateStore.Open(path))
        {
            Check.Require(!store.DeletedHistoryOwed(PairId) && store.DeletedHistoryOwed("b"),
                "Completed acquisition survives restart without clearing another pair's independent obligation.");
            var token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.RestoreDeletedHistory(PairId, token) == "fresh-cursor" && store.DeletedNameExact(PairId, "Fresh"),
                "A completed migrated pair resumes its retained snapshot even though first Repair was already done.");
        }
    }

    private static void DeltaFold(string path)
    {
        using (var store = StateStore.Open(path))
        {
            store.SavePair(Pair());
            var generation = Generation(store);
            var source = store.ReadSource(PairId, Side.Dropbox)!.Value;
            var provenance = new SourceProvenance("account", "namespace", "root", "/Parent", true, true, true, true, generation, source.Epoch);
            store.AdvanceSource(PairId, Side.Dropbox, generation, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, source.Epoch, source.Revision, provenance, "cursor-a", source.Obligation);
            source = store.ReadSource(PairId, Side.Dropbox)!.Value;
            try
            {
                store.AcquireDropboxPage(PairId, generation, source.Epoch, source.Revision, provenance, source.Cursor,
                    [new ChangeNotice(Side.Dropbox, ChangeKind.Deleted, "RolledBack", null, null, null, false, false),
                     new ChangeNotice(Side.Local, ChangeKind.Deleted, "InvalidSide", null, null, null, false, false)], "not-committed");
                throw new InvalidOperationException("An invalid provider page must fail.");
            }
            catch (ArgumentException) { }
            Check.Require(Number(Connection(store), "SELECT count(*) FROM discovery_deleted_delta;") == 0 &&
                store.ReadSource(PairId, Side.Dropbox)!.Value.Cursor == source.Cursor,
                "A failed AutoSync page rolls back deleted names together with work and cursor.");
            store.AcquireDropboxPage(PairId, generation, source.Epoch, source.Revision, provenance, source.Cursor,
                [new ChangeNotice(Side.Dropbox, ChangeKind.Deleted, "Deleted", null, null, null, false, false)], "cursor-b");
        }
        using (var store = StateStore.Open(path))
        {
            var token = store.BeginScan(PairId, Generation(store), true);
            var connection = Connection(store);
            Check.Require(store.DeletedNameExact(PairId, PathNames.Key("Deleted")), "AutoSync history persists independently of any scan or published blocks.");
            Seed(connection, ["A", "Z"]);
            Execute(connection, "CREATE TEMP TRIGGER concurrent_delta AFTER INSERT ON discovery_deleted_block BEGIN INSERT OR IGNORE INTO discovery_deleted_delta(pair_id,path_key) VALUES(new.pair_id,'DURING');END;");
            store.SaveDeletedHistory(PairId, token, Cursor);
            Execute(connection, "DROP TRIGGER concurrent_delta;");
            Check.Require(Number(connection, "SELECT count(*) FROM discovery_deleted_delta;") == 1,
                "Publication retires folded rows but preserves concurrent appends absent from its immutable snapshot.");
            token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.RestoreDeletedHistory(PairId, token) == Cursor && store.DeletedNameExact(PairId, "DURING") &&
                store.DeletedNameExact(PairId, "DELETED"), "Both initial and concurrent deletes survive the fold.");
            store.SaveDeletedHistory(PairId, token, Cursor);
            Check.Require(Number(connection, "SELECT count(*) FROM discovery_deleted_delta;") == 0,
                "The next full acquisition folds the concurrent append without ever updating a published block.");
            Execute(connection, "INSERT INTO discovery_deleted_delta(pair_id,path_key) VALUES(1,'AFTER');");
        }
        using (var store = StateStore.Open(path))
        {
            foreach (var key in Keys(Connection(store), "SELECT last_key FROM discovery_deleted_block ORDER BY last_key;"))
                Check.Require(store.SavedDeletedNameExact(PairId, key), "Completed history membership survives reopen without an active scan.");
            Check.Require(!store.SavedDeletedNameExact(PairId, "Missing"), "Completed empty/nonmatching history is not a match.");
            var token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.RestoreDeletedHistory(PairId, token) == Cursor && store.DeletedNameExact(PairId, "AFTER"),
                "An append after publication remains readable through the same membership owner after restart.");
        }
    }

    private static void Incremental(string path)
    {
        using (var store = StateStore.Open(path))
        {
            store.SavePair(Pair());
            var token = store.BeginScan(PairId, Generation(store), true);
            var connection = Connection(store);
            Seed(connection, Enumerable.Range(0, 10001).Select(index => "Old" + index.ToString("D8", CultureInfo.InvariantCulture)));
            store.SaveDeletedHistory(PairId, token, Cursor);
            var blocks = Keys(connection, "SELECT hex(names) FROM discovery_deleted_block ORDER BY last_key;");
            token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.ContinueDeletedHistory(PairId, token) == Cursor &&
                Number(connection, "SELECT count(*) FROM scan_state.deleted_name;") == 0,
                "Ordinary continuation reads only its cursor, never restoring H names.");
            Execute(connection, "CREATE TEMP TRIGGER no_restore BEFORE INSERT ON scan_state.deleted_name BEGIN SELECT RAISE(ABORT,'unexpected restore');END;");
            Execute(connection, "CREATE TEMP TRIGGER no_republish BEFORE INSERT ON discovery_deleted_block BEGIN SELECT RAISE(ABORT,'unexpected republish');END;");
            var sink = store.OpenScan(PairId, Side.Dropbox, 100);
            sink.AddAsync(new Listed("New", null), CancellationToken.None).GetAwaiter().GetResult();
            sink.CompleteAsync().GetAwaiter().GetResult();
            store.SaveDeletedHistory(PairId, token, "continued");
            Check.Require(blocks.SequenceEqual(Keys(connection, "SELECT hex(names) FROM discovery_deleted_block ORDER BY last_key;")) &&
                store.DeletedNameExact(PairId, "NEW") && Number(connection, "SELECT count(*) FROM discovery_deleted_delta;") == 1,
                "One continued delete is durable with no scratch restoration, block replacement, or fold.");
        }
        using (var store = StateStore.Open(path))
        {
            var token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.ContinueDeletedHistory(PairId, token) == "continued" && store.DeletedNameExact(PairId, "NEW"),
                "Cursor advancement never outruns durable incremental names across restart.");
            var sink = store.OpenScan(PairId, Side.Dropbox, 100);
            sink.AddAsync(new Listed("Interrupted", null), CancellationToken.None).GetAwaiter().GetResult();
            sink.CompleteAsync().GetAwaiter().GetResult();
            // Deliberately leave the terminal cursor unpublished, as on an interrupted Repair.
        }
        using (var store = StateStore.Open(path))
        {
            var token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.ContinueDeletedHistory(PairId, token) == "continued" && store.DeletedNameExact(PairId, "INTERRUPTED"),
                "An interrupted continuation retains its names and replays from the prior cursor idempotently.");
            store.ResetDeletedHistory(PairId, token, discardNames: true);
            Seed(Connection(store), ["Fresh"]);
            store.SaveDeletedHistory(PairId, token, "reset");
            Check.Require(!store.DeletedHistoryOwed(PairId) && Number(Connection(store), "SELECT count(*) FROM discovery_deleted_delta;") == 0,
                "Cursor-reset fallback returns to full acquisition and folds the retained incremental names.");
        }
    }

    private static void DeltaSize(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var connection = Connection(store);
        long previous = Number(connection, "PRAGMA page_count;");
        var pageSize = Number(connection, "PRAGMA page_size;");
        foreach (var total in new[] { 10000, 20000 })
        {
            using (var transaction = connection.BeginTransaction())
            using (var insert = StorageCommand.Create(connection, transaction, StateSchema.AppendDeletedHistory,
                (StateSchema.ParamPairRow, 1L), (StateSchema.ParamPath, string.Empty)))
            {
                for (var index = total - 10000; index < total; index++)
                {
                    insert.Parameters[StateSchema.ParamPath].Value = "Parent/" + new string('x', 120) + "/Name" + index.ToString("D8", CultureInfo.InvariantCulture);
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            var pages = Number(connection, "PRAGMA page_count;");
            Console.WriteLine("HISTORY-DELTA keys=" + total + " addedBytesPerName=" + ((pages - previous) * pageSize / 10000.0).ToString("F2", CultureInfo.InvariantCulture));
            previous = pages;
        }
    }

    private static void CountWork(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var connection = Connection(store);
        long previousBytes = 0;
        foreach (var count in new[] { 10000, 20000 })
        {
            var token = store.BeginScan(PairId, Generation(store), true);
            Seed(connection, Enumerable.Range(0, count).Select(index => "Parent/Name" + index.ToString("D8", CultureInfo.InvariantCulture)));
            var blocks = count / StateSchema.DeletedHistoryBatchSize;
            var save = Trace(() => store.SaveDeletedHistory(PairId, token, Cursor));
            Check.Require(save.Count(sql => sql.StartsWith("INSERT INTO main.discovery_deleted_block", StringComparison.Ordinal)) == blocks &&
                save.Count(sql => sql.StartsWith("SELECT path_key FROM scan_state.deleted_name", StringComparison.Ordinal)) == blocks + 1,
                "Full save writes exactly B blocks and reads B plus one terminal scratch pages.");
            token = store.BeginScan(PairId, Generation(store), true);
            var progress = new List<long>();
            var lockFree = true;
            var restore = Trace(() => store.RestoreDeletedHistory(PairId, token, restored =>
            {
                progress.Add(restored);
                // Another thread must get the store between blocks, or the quiet progress observer starves (B-21).
                lockFree &= Task.Run(() => store.DeletedHistoryOwed(PairId)).Wait(TimeSpan.FromSeconds(30));
            }));
            // Each insert's JSON array grows by one quoted key and comma until it reaches the byte bound.
            var keysPerInsert = (StateEncoding.MaximumDeletedKeyBytes + "\"Parent/Name00000000\",".Length - 1) / "\"Parent/Name00000000\",".Length;
            var insertsPerBlock = (StateSchema.DeletedHistoryBatchSize + keysPerInsert - 1) / keysPerInsert;
            Check.Require(restore.Count(sql => sql.StartsWith("SELECT last_key,name_count,rowid", StringComparison.Ordinal)) == blocks + 1 &&
                restore.Count(sql => sql.StartsWith("INSERT INTO scan_state.deleted_name", StringComparison.Ordinal)) == blocks * insertsPerBlock &&
                restore.Count(sql => sql == "COMMIT;") == blocks,
                "Restore performs B plus one terminal block reads, byte-bounded inserts per block, and one commit per block.");
            Check.Require(progress.SequenceEqual(Enumerable.Range(1, blocks).Select(block => (long)block * StateSchema.DeletedHistoryBatchSize)) &&
                lockFree, "Restore reports its running count after each block with the store lock released.");
            var bytes = Number(connection, "SELECT sum(length(names)) FROM discovery_deleted_block;");
            Check.Require(previousBytes == 0 || bytes <= previousBytes * 2 + bytes / blocks,
                "Compressed bytes at 2H grow within twice H plus one block.");
            previousBytes = bytes;
            Console.WriteLine($"HISTORY-COUNTS keys={count} blocks={blocks} saveStatements={save.Count} restoreStatements={restore.Count} blobBytes={bytes}");
        }
        List<string> Trace(Action action)
        {
            var statements = new List<string>();
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, sql) => statements.Add(sql.utf8_to_string())), null);
            try { action(); }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
            return statements;
        }
    }

    // B21-R1: one block of maximum-length CJK keys streams in byte-bounded statements, never one whole-block text.
    private static void LongKeys(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var connection = Connection(store);
        var token = store.BeginScan(PairId, Generation(store), true);
        var keys = Enumerable.Range(0, 64).Select(index => new string((char)0x754C, SyncEndpointContext.MaximumPathLength - 4) +
            index.ToString("D4", CultureInfo.InvariantCulture)).ToArray();
        Seed(connection, keys);
        var expected = Keys(connection, "SELECT hex(path_key) FROM scan_state.deleted_name ORDER BY path_key;");
        store.SaveDeletedHistory(PairId, token, Cursor);
        Check.Require(Number(connection, "SELECT count(*) FROM discovery_deleted_block;") == 1, "All long keys share one block.");
        token = store.BeginScan(PairId, Generation(store), true);
        var inserts = new List<int>();
        SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, sql) =>
        {
            var text = sql.utf8_to_string();
            if (text.StartsWith("INSERT INTO scan_state.deleted_name", StringComparison.Ordinal)) inserts.Add(Encoding.UTF8.GetByteCount(text));
        }), null);
        string? restored;
        try { restored = store.RestoreDeletedHistory(PairId, token); }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
        // Traced text expands the bound JSON; one statement holds under one maximum key plus one escaped key (6 bytes a unit).
        var bound = 3 * StateEncoding.MaximumDeletedKeyBytes + 1024;
        var total = keys.Sum(key => (long)Encoding.UTF8.GetByteCount(key));
        Check.Require(restored == Cursor &&
            expected.SequenceEqual(Keys(connection, "SELECT hex(path_key) FROM scan_state.deleted_name ORDER BY path_key;")),
            "Long-key restore keeps the exact UTF-8 key set.");
        Check.Require(inserts.Count >= (total + bound - 1) / bound && inserts.All(length => length <= bound),
            "One long-key block streams in byte-bounded statements, not one whole-block text.");
    }

    private static void Spend(IStateStore store, string path) => store.Commit(PairId,
        StateChange.Empty with { ForgetSelf = [(Side.Local, PathNames.Key(path))] });

    private static void SpentOverlay(string path)
    {
        using (var store = StateStore.Open(path))
        {
            store.SavePair(Pair());
            var token = store.BeginScan(PairId, Generation(store), true);
            var connection = Connection(store);
            Seed(connection, ["PARENT", "PARENT/CHILD", "PARENT/SIBLING"]);
            store.SaveDeletedHistory(PairId, token, Cursor);
            Spend(store, "Parent/Child");
            Check.Require(store.DeletedNameSpent(PairId, "PARENT/CHILD") && !store.DeletedNameExact(PairId, "PARENT/CHILD") &&
                store.DeletedNameExact(PairId, "PARENT"), "Exact spending masks an already encoded block without spending its ancestor.");
            token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.RestoreDeletedHistory(PairId, token) == Cursor && !store.DeletedHistoryOwed(PairId),
                "Filtering a spent decoded key does not corrupt the raw block count/order validation.");
            Spend(store, "Parent");
            Check.Require(store.DeletedNameExact(PairId, "PARENT/SIBLING"),
                "A spent ancestor does not exempt an independently positive child.");
            Spend(store, "Parent/Sibling");
            store.SaveDeletedHistory(PairId, token, Cursor);
            token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.RestoreDeletedHistory(PairId, token) == Cursor && !store.HasDeletedNames(PairId) &&
                !store.DeletedHistoryOwed(PairId), "An all-spent snapshot remains Complete-empty, not Owed.");
            store.ResetDeletedHistory(PairId, token, discardNames: true);
            Spend(store, "Late");
            var sink = store.OpenScan(PairId, Side.Dropbox, 1);
            sink.AddAsync(new Listed("Late", null), default).GetAwaiter().GetResult();
            sink.AddAsync(new Listed("Unseen", null), default).GetAwaiter().GetResult();
            sink.CompleteAsync().GetAwaiter().GetResult();
            Check.Require(store.DeletedHistoryOwed(PairId) && !store.DeletedNameExact(PairId, "LATE") &&
                store.DeletedNameExact(PairId, "UNSEEN"), "An owed late acquisition cannot re-add spent eligibility.");
            Seed(connection, ["COPY", "PARENT/CHILD"]);
            Execute(connection, "CREATE TEMP TRIGGER spend_during_copy AFTER INSERT ON discovery_deleted_block BEGIN INSERT INTO discovery_deleted_delta(pair_id,path_key,spent) VALUES(new.pair_id,'COPY',1) ON CONFLICT(pair_id,path_key) DO UPDATE SET spent=1;END;");
            store.SaveDeletedHistory(PairId, token, Cursor);
            Execute(connection, "DROP TRIGGER spend_during_copy;");
            Check.Require(store.DeletedNameSpent(PairId, "COPY") && !store.DeletedNameExact(PairId, "COPY"),
                "Spending after block encoding survives final positive-delta retirement.");
        }
        using (var store = StateStore.Open(path))
        {
            Check.Require(store.DeletedNameSpent(PairId, "LATE") && !store.SavedDeletedNameExact(PairId, "COPY") &&
                store.SavedDeletedNameExact(PairId, "UNSEEN"), "Restart and outside-scan membership preserve exact precedence.");
            var token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.RestoreDeletedHistory(PairId, token) == Cursor, "Filtered snapshot restores after restart.");
            Execute(Connection(store), "UPDATE discovery_deleted_block SET names=x'010203';");
        }
        using (var store = StateStore.Open(path))
        {
            var unavailable = false;
            try { store.SavedDeletedNameExact(PairId, "UNSEEN"); }
            catch (InvalidDataException) { unavailable = true; }
            Check.Require(unavailable && store.DeletedHistoryOwed(PairId) &&
                store.DeletedNameSpent(PairId, "COPY"), "A corrupt outside-scan probe diagnoses reacquisition and preserves spent facts.");
            var token = store.BeginScan(PairId, Generation(store), true);
            Seed(Connection(store), ["COPY", "FRESH"]);
            store.SaveDeletedHistory(PairId, token, Cursor);
            Execute(Connection(store), "UPDATE discovery_deleted_block SET names=x'010203';");
            token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.RestoreDeletedHistory(PairId, token) is null && store.DeletedHistoryOwed(PairId) &&
                store.DeletedNameSpent(PairId, "COPY"), "Corrupt-history restore also preserves spent facts.");
        }
    }

    private static void IdentityLifetime(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        Spend(store, "Handled");
        var current = store.Pair(PairId)!;
        store.SavePairDecision(current, Generation(store), current, null, false, null);
        Check.Require(store.DeletedNameSpent(PairId, "HANDLED"), "P11 generation-only SavePairDecision preserves spent keys.");
        store.SavePair(current with { Name = "Renamed pair" });
        Check.Require(store.DeletedNameSpent(PairId, "HANDLED"), "P11 name-only SavePair preserves spent keys.");
        store.SavePair(current with { FirstRun = FirstRunPolicy.NewestWins, IgnoreRules = ["Ignored"] });
        Check.Require(store.DeletedNameSpent(PairId, "HANDLED"), "P11 policy/ignore changes preserve spent keys.");
        foreach (var replace in new Func<PairConfig, PairConfig>[]
        {
            pair => pair with { LocalRoot = "X:/Other" },
            pair => pair with { DropboxRoot = "/Other" },
            pair => pair with { ConnectionId = "other-account" },
            pair => pair with { Roots = new RootBinding(pair.LocalRoot, pair.DropboxRoot, pair.ConnectionId,
                new DirectoryIdentity(1, 2, null), new DropboxRootIdentity("account", "root", "home", "folder")) }
        })
        {
            Spend(store, "Handled");
            var token = store.BeginScan(PairId, Generation(store), true);
            Seed(Connection(store), ["OLD"]);
            store.SaveDeletedHistory(PairId, token, Cursor);
            store.SavePair(replace(store.Pair(PairId)!));
            Check.Require(!store.DeletedNameSpent(PairId, "HANDLED") && !store.SavedDeletedNameExact(PairId, "OLD") &&
                store.DeletedHistoryOwed(PairId), "P11 each saved identity component clears old positive and negative namespace facts.");
        }
        Spend(store, "Handled");
        current = store.Pair(PairId)!;
        store.SavePairDecision(current, Generation(store), current with
            { Roots = current.Roots! with { Dropbox = current.Roots.Dropbox with { FolderId = "rebound" } } }, null, true, null);
        Check.Require(!store.DeletedNameSpent(PairId, "HANDLED"), "P11 RootBinding rebind through SavePairDecision clears spent keys atomically.");
        Spend(store, "Handled");
        store.RemovePair(PairId);
        store.SavePair(Pair());
        Check.Require(!store.DeletedNameSpent(PairId, "HANDLED"), "Pair removal cascades spent facts; recreated identifiers inherit none.");
    }

    private static void SpentMigration(string path)
    {
        using (var connection = StateStore.Connect(path, SqliteOpenMode.ReadWriteCreate))
        {
            Execute(connection, StateSchema.Create + StateSchema.SchemaAdditions + StateSchema.DeletedHistorySchema +
                StateSchema.RebuildIssues + StateSchema.DeletedHistoryBlocks + StateSchema.DropDeletedHistoryNames);
            Execute(connection, StateSchema.UpsertPair, ("$p", PairId), ("$def", JsonSerializer.Serialize(Pair())));
            Execute(connection, "INSERT INTO discovery_deleted_delta VALUES(1,'POSITIVE');PRAGMA user_version=9;");
            var changes = new ChangeTable(connection, writable: true);
            using var transaction = connection.BeginTransaction();
            changes.SeedPairSources(transaction, PairId);
            transaction.Commit();
            Execute(connection, "PRAGMA foreign_keys=OFF;INSERT INTO discovery_deleted_delta VALUES(999,'INVALID');PRAGMA foreign_keys=ON;");
        }
        try { using var refused = StateStore.Open(path); throw new Exception("invalid foreign key accepted"); }
        catch (InvalidDataException) { }
        using (var connection = StateStore.Connect(path, SqliteOpenMode.ReadWrite))
        {
            Check.Require(Number(connection, "PRAGMA user_version;") == 9 &&
                Number(connection, "SELECT count(*) FROM pragma_table_info('discovery_deleted_delta') WHERE name='spent';") == 0 &&
                Number(connection, "SELECT count(*) FROM discovery_deleted_delta;") == 2,
                "a refused v9 upgrade rolls back its added column and rows before publishing the version");
            Execute(connection, "DELETE FROM discovery_deleted_delta WHERE pair_id=999;");
        }
        using var store = StateStore.Open(path);
        store.BeginScan(PairId, Generation(store), true);
        Check.Require(store.DeletedNameExact(PairId, "POSITIVE") && !store.DeletedNameSpent(PairId, "POSITIVE") &&
            Number(Connection(store), "PRAGMA user_version;") == StateSchema.Version &&
            Keys(Connection(store), "PRAGMA foreign_key_check;").Length == 0,
            "Populated v9 upgrade retains positive defaults and validates foreign keys before publishing v10.");
        Spend(store, "Positive");
        Check.Require(!store.DeletedNameExact(PairId, "POSITIVE"), "Upgraded delta accepts durable negative overrides.");
    }

    private static void Retirement(string path)
    {
        using var store = StateStore.Open(path);
        var pair = SourceFixture.Bound(Pair());
        store.SavePair(pair);
        SourceFixture.AcquireJournal(store, pair);
        SyncedEntry Binding(string local, string remote) => new(PathNames.Key(local), local, PathNames.DropboxKey(remote), remote,
            false, "local-id", 1, 0, "remote-id", "hash");
        var before = Binding("Parent/\u00e9:Child", "Original");
        store.Commit(PairId, StateChange.Empty with { Record = [before] });
        store.Commit(PairId, StateChange.Empty with { Record = [before] });
        Check.Require(!store.DeletedNameSpent(PairId, PathNames.DropboxKey(before.LocalPath)), "Same-key Record never spends eligibility.");
        var after = Binding("Original", "Original");
        var localCollision = Binding("Original", "Elsewhere");
        store.Commit(PairId, StateChange.Empty with { Record = [localCollision] });
        var watermark = store.ReadWorkWatermark();
        using (var transaction = Connection(store).BeginTransaction())
        {
            StateCommitter.Commit(Connection(store), transaction, new ChangeTable(Connection(store), writable: false), PairId, StateChange.Empty with { Record = [after] },
                DateTimeOffset.UtcNow, false, null, []);
            transaction.Rollback();
        }
        Check.Require(store.ReadWorkWatermark() == watermark &&
            !store.DeletedNameSpent(PairId, PathNames.DropboxKey(before.LocalPath)) &&
            store.SyncedByKey(PairId, Side.Local, before.LocalKey) == before &&
            store.SyncedByKey(PairId, Side.Local, localCollision.LocalKey) == localCollision,
            "Both RETURNING result sets, binding retirement and spend roll back together.");
        store.Commit(PairId, StateChange.Empty with { Record = [after] });
        Check.Require(store.DeletedNameSpent(PairId, PathNames.DropboxKey(before.LocalPath)) &&
            !store.DeletedNameSpent(PairId, after.LocalKey), "Crossed Local retirement spends its own mapped/case key, not the retained endpoint.");
        var child = Binding("Parent/Saved", "Remote/Saved");
        store.Commit(PairId, StateChange.Empty with { Record = [child] });
        store.Commit(PairId, StateChange.Empty with { ForgetSubtrees = [(Side.Dropbox, "REMOTE")], Record = [child] });
        Check.Require(!store.DeletedNameSpent(PairId, child.LocalKey), "A subtree rewrite retains resulting Local endpoints without spending them.");
        store.Commit(PairId, StateChange.Empty with { ForgetSubtrees = [(Side.Dropbox, "REMOTE")] });
        Check.Require(store.DeletedNameSpent(PairId, child.LocalKey) && !store.DeletedNameSpent(PairId, "PARENT/UNSEEN"),
            "Set-based subtree retirement spends actual Local endpoints only, never every possible descendant.");
    }

    private static void FakeAtomicity()
    {
        using var store = new InMemoryStateStore();
        store.SavePair(Pair());
        var saved = new SyncedEntry("CHILD", "Child", "CHILD", "Child", false, "local", 1, 0, "remote", "hash");
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var refused = false;
        try
        {
            store.Commit(PairId, StateChange.Empty with
                { ForgetSelf = [(Side.Local, "CHILD")], StaleBindings = [saved] });
        }
        catch (ArgumentException) { refused = true; }
        Check.Require(refused && !store.DeletedNameSpent(PairId, "CHILD") &&
            store.SyncedByKey(PairId, Side.Local, "CHILD") == saved, "The staged fake rolls spending back with refused binding work.");
    }

    private static void SpentCosts()
    {
        StatementCost? previous = null;
        foreach (var background in new[] { 10000, 20000 })
        {
            using var probe = new CostProbe(":memory:");
            probe.CreateSchema();
            probe.Execute(StateSchema.UpsertPair, ("$p", PairId), ("$def", JsonSerializer.Serialize(Pair())));
            probe.Execute("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<$count) " +
                "INSERT INTO discovery_deleted_delta(pair_id,path_key) SELECT 1,'BACKGROUND/'||i FROM n;", ("$count", background));
            probe.Begin();
            var cost = probe.Execute(StateSchema.SpendDeletedKey, ("$p", PairId), ("$path", "TARGET"));
            cost += probe.Execute(StateSchema.AppendDeletedHistory, ("$pair", 1), ("$path", "TARGET"));
            cost += probe.ExecuteFirst(StateSchema.SpentDeletedKey, ("$p", PairId), ("$path", "TARGET"));
            probe.Commit();
            Check.Require(cost.ProbePrepares == 3 && cost.ChangedRows == 1 &&
                (previous is null || cost.VmSteps == previous.Value.VmSteps),
                "One spent key, late append and exact negative lookup have fixed VM work as unrelated history doubles.");
            Console.WriteLine($"SPENT-COST background={background}; {cost}");
            previous = cost;
        }
    }

    private static void Plans()
    {
        using var connection = StateStore.Connect(":memory:", SqliteOpenMode.ReadWriteCreate);
        Execute(connection, StateSchema.Create + StateSchema.SchemaAdditions + StateSchema.DeletedHistorySchema +
            StateSchema.RebuildIssues + StateSchema.DeletedHistoryBlocks + StateSchema.DropDeletedHistoryNames + StateSchema.SpentDeletedHistory + StateSchema.LocalPublishedColumns + StateSchema.DiscoveryRequiredColumn + StateSchema.IssuePathKeyIndex + StateSchema.RenameContinuationColumns);
        foreach (var sql in new[] { StateSchema.ReadDeletedBlock, StateSchema.SavedDeletedHistoryKey, StateSchema.ReclaimDeletedHistory,
            StateSchema.DeletedDeltaPage, StateSchema.DeletedKeyDelta, StateSchema.DeletedKeyBlock, StateSchema.SpentDeletedKey })
        {
            using var command = StorageCommand.Create(connection, null, "EXPLAIN QUERY PLAN " + sql,
                ("$pair", 1), ("$p", PairId), ("$after", "A"), ("$take", 5000), ("$path", "A"));
            using var reader = command.ExecuteReader();
            var details = new List<string>();
            while (reader.Read()) details.Add(reader.GetString(3));
            // Scalar EXISTS emits one constant result row, not a stored-table scan.
            Check.Require(details.Any(detail => detail.Contains("SEARCH", StringComparison.Ordinal)) &&
                details.All(detail => detail == "SCAN CONSTANT ROW" || !detail.Contains("SCAN ", StringComparison.Ordinal)),
                $"History page operations use indexed seeks without statistics: {string.Join("; ", details)}");
        }
    }

    /// <summary>B-21 measurement: restore cost, allocation and longest sampled store-lock acquisition wait at half and all of the known-facts R02 history size.</summary>
    public static void RunRestoreSpeed()
    {
        // R02: 2,643,679 deleted-history rows, path-key length 2-382, mean 132.2; the second size is half of it (§6.6 two sizes).
        var sizes = Environment.GetEnvironmentVariable("HISTORY_RESTORE_NAMES") is { } names
            ? new[] { int.Parse(names, CultureInfo.InvariantCulture) } : new[] { 1321840, 2643679 };
        foreach (var size in sizes) RestoreSpeedAt(size);
    }

    /// <summary>B-232 storage cost: write-log footprint and logical bytes after a deleted-history restore at two sizes.</summary>
    internal static void RunRestoreFootprint()
    {
        // Four versus sixteen blocks separates a retained whole-history WAL from the default checkpoint high-water mark.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var small = RestoreSpeedAt(4 * StateSchema.DeletedHistoryBatchSize);
        var large = RestoreSpeedAt(16 * StateSchema.DeletedHistoryBatchSize);
        var walBounded = large.WalFileBytes <= 2 * small.WalFileBytes;
        var linear = large.LogicalBytes * small.Names <= 2 * small.LogicalBytes * large.Names;
        // Diagnostic until measured fill supports an absolute guard: two copies need twice the key bytes.
        var oneCopy = large.LogicalBytes < 2 * large.Names * large.MeanLength;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"RESTORE-FOOTPRINT wal_file_bytes={small.WalFileBytes}->{large.WalFileBytes} logical_bytes={small.LogicalBytes}->{large.LogicalBytes} " +
            $"names={small.Names}->{large.Names} mean_key={large.MeanLength} wal_bounded={walBounded} linear={linear} one_copy={oneCopy} " +
            $"elapsed_total_ms={timer.ElapsedMilliseconds}"));
        Check.Require(walBounded && linear, $"restore footprint: wal_bounded={walBounded} linear={linear}");
    }

    // Each size uses a fresh store and the same deterministic name generator, so the smaller set is a prefix of the larger.
    private static (long Names, long MeanLength, long LogicalBytes, long DbFileBytes, long WalFileBytes) RestoreSpeedAt(int names)
    {
        var folder = Path.Combine(Path.GetTempPath(), "history-speed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var store = StateStore.Open(Path.Combine(folder, "state.db"));
            store.SavePair(Pair());
            var connection = Connection(store);
            var token = store.BeginScan(PairId, Generation(store), true);
            for (var start = 0; start < names; start += StateSchema.DeletedHistoryBatchSize)
            {
                var keys = Enumerable.Range(start, Math.Min(StateSchema.DeletedHistoryBatchSize, names - start)).Select(index =>
                {
                    var key = "d" + (index % 97).ToString(CultureInfo.InvariantCulture) + "/s" + (index % 9973).ToString(CultureInfo.InvariantCulture) +
                        "/n" + index.ToString(CultureInfo.InvariantCulture);
                    var length = 2 + index * 7919L % 261;
                    return length <= key.Length ? key : key + "/" + new string('x', (int)length - key.Length - 1);
                }).ToArray();
                Execute(connection, "INSERT INTO scan_state.deleted_name(pair_id,path_key) SELECT 1,value FROM json_each($keys) WHERE true ON CONFLICT DO NOTHING;",
                    ("$keys", JsonSerializer.Serialize(keys)));
            }
            var seeded = Number(connection, "SELECT count(*) FROM scan_state.deleted_name;");
            var meanLength = Number(connection, "SELECT CAST(round(avg(length(path_key))) AS INTEGER) FROM scan_state.deleted_name;");
            store.SaveDeletedHistory(PairId, token, Cursor);
            token = store.BeginScan(PairId, Generation(store), true);
            using var done = new ManualResetEventSlim();
            long longestWait = 0, largestHeap = 0, probes = 0;
            Exception? probeFailure = null;
            var probe = new Thread(() =>
            {
                try
                {
                    while (!done.IsSet)
                    {
                        var started = System.Diagnostics.Stopwatch.GetTimestamp();
                        store.DeletedHistoryOwed(PairId);
                        longestWait = Math.Max(longestWait, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                        largestHeap = Math.Max(largestHeap, GC.GetTotalMemory(false));
                        probes++;
                        done.Wait(1); // probe cadence only; it never orders the measured work
                    }
                }
                catch (Exception error) { probeFailure = error; } // rethrown on the test thread after Join
            });
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            process.Refresh();
            var peakBefore = process.PeakWorkingSet64;
            var allocated = GC.GetTotalAllocatedBytes(true);
            var heapBefore = GC.GetTotalMemory(false);
            probe.Start();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            string? cursor;
            try { cursor = store.RestoreDeletedHistory(PairId, token); }
            finally
            {
                timer.Stop();
                done.Set();
                probe.Join(); // the probe must stop before done and store are disposed, even when restore throws
            }
            if (probeFailure is not null) throw new InvalidOperationException("The lock probe failed.", probeFailure);
            allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            process.Refresh();
            // Read retained WAL bytes first; page_count includes logical pages still in the WAL.
            var scanFile = Directory.EnumerateFiles(folder, "state.db" + StateSchema.ScanFileSuffix + "*" + StateSchema.DatabaseExtension).Single();
            var walFileBytes = new FileInfo(scanFile + StateSchema.WalSuffix).Length;
            var dbFileBytes = new FileInfo(scanFile).Length;
            var logicalBytes = Number(connection, "PRAGMA scan_state.page_count;") * Number(connection, "PRAGMA scan_state.page_size;");
            var autocheckpoint = Number(connection, "PRAGMA wal_autocheckpoint;");
            var sqlite = Keys(connection, "SELECT sqlite_version();").Single();
            var restored = Number(connection, "SELECT count(*) FROM scan_state.deleted_name;");
            Check.Require(cursor == Cursor && restored == seeded, "The measured restore restores every seeded name.");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"HISTORY-RESTORE names={seeded} mean_length={meanLength} elapsed_ms={timer.Elapsed.TotalMilliseconds:F0} allocated_bytes={allocated} " +
                $"heap_before={heapBefore} largest_heap={largestHeap} peak_working_set_before={peakBefore} peak_working_set_after={process.PeakWorkingSet64} " +
                $"longest_lock_wait_ms={longestWait * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} probes={probes} " +
                $"scan_wal_file_bytes={walFileBytes} scan_db_file_bytes={dbFileBytes} footprint=physical " +
                $"scan_logical_bytes={logicalBytes} logical_bytes_per_name={(double)logicalBytes / seeded:F2} " +
                $"autocheckpoint={autocheckpoint} sqlite={sqlite}"));
            return (seeded, meanLength, logicalBytes, dbFileBytes, walFileBytes);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
