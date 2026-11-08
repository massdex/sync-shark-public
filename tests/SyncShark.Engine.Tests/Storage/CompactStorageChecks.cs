using System.Globalization;
using Massdex.SyncShark.Engine.Storage;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Compact physical values, complete key pages and completed-run reclamation through the real store.</summary>
public static class CompactStorageChecks
{
    private static readonly RowContext Context = new(false, false, false, null, false, false, false, false);
    private const string LocalRoot = "X:/Parent";
    private const string RemoteRoot = "/Parent";
    private const string Account = "account";
    private const string Namespace = "namespace";
    private const string RemoteId = "remote-id";
    private const ulong Volume = 1;
    private static readonly RootBinding BoundRoots = new(LocalRoot, RemoteRoot, Account,
        new DirectoryIdentity(Volume, 0, null), new DropboxRootIdentity(Account, Namespace, Namespace, RemoteId));

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "compact-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            ValuesAndRebases(Path.Combine(folder, "values.db"));
            SavedRebases(Path.Combine(folder, "rebases.db"));
            Paging(Path.Combine(folder, "pages.db"));
            Reclamation(Path.Combine(folder, "reclaim.db"));
            BusyCheckpoint(Path.Combine(folder, "busy.db"));
            RestartScans(Path.Combine(folder, "restart.db"));
            ScanReadConnections(Path.Combine(folder, "read paths #%é.db"));
            DeferredScanDeletion(Path.Combine(folder, "delete.db"));
            OpaqueHashes(Path.Combine(folder, "hashes.db"));
            OldVersion(Path.Combine(folder, "old.db"));
            PartialKeyCollisions(Path.Combine(folder, "collisions.db"));
            DerivedLocalKeys(Path.Combine(folder, "derived.db"));
            ContractCases(Path.Combine(folder, "contracts.db"));
            Scaling(folder);
        }
        finally { Directory.Delete(folder, true); }
    }

    internal static PairConfig Pair(string id) => new(id, "Pair A", LocalRoot, RemoteRoot, Account, [], [],
        FirstRunPolicy.NewestWins, true, null);

    private static PairConfig BoundPair(string id) => Pair(id) with { Roots = BoundRoots };

    private static long Generation(StateStore store, string pairId) =>
        store.ReadPairExecution(pairId)!.Value.Generation;

    internal static SyncedEntry Saved(string path, string id = "1", string hash = "") =>
        new(PathNames.Key(path), path, PathNames.DropboxKey(path), path, false, id, 12, 34, "id:" + path, hash);

    internal static SqliteConnection Inspect(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        StateStore.OpenConnection(connection);
        return connection;
    }

    private static long Number(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void ValuesAndRebases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair("a"));
        store.BeginScan("a", Generation(store, "a"), false);
        using var inspect = Inspect(path);
        var ids = new[] { long.MinValue, long.MaxValue, 9007199254740993L, 0L };
        for (var index = 0; index < 8; index++)
        {
            var hash = Convert.ToHexString(Enumerable.Range(index * 32, 32).Select(value => (byte)value).ToArray()).ToLowerInvariant();
            var entry = Saved("file" + index, ids[index % ids.Length].ToString(CultureInfo.InvariantCulture), hash);
            store.Commit("a", StateChange.Empty with { Record = [entry] });
            Check.Require(store.SyncedByKey("a", Side.Dropbox, entry.DropboxKey) == entry,
                "every hash byte and signed 64-bit local id round-trips");
            Check.Require(store.SyncedById("a", Side.Local, entry.LocalId) is not null, "integer local identities remain indexed");
            var sink = store.OpenScan("a", Side.Local, ScanTestSettings.BatchRows);
            var local = new Entry(entry.LocalPath, false, 12, DateTimeOffset.UnixEpoch, entry.LocalId, "", "", null);
            sink.AddAsync(new Listed(local.Path, local), default).GetAwaiter().GetResult();
            sink.CompleteAsync().GetAwaiter().GetResult();
            Check.Require(store.ScanRows("a", _ => Context).Single(row => row.Key == entry.LocalKey).Local?.Id == local.Id, "scan ids retain every Int64 bit");
            sink = store.OpenScan("a", Side.Dropbox, ScanTestSettings.BatchRows);
            var dropbox = local with { Id = entry.DropboxId, ContentHash = hash };
            sink.AddAsync(new Listed(dropbox.Path, dropbox), default).GetAwaiter().GetResult();
            sink.CompleteAsync().GetAwaiter().GetResult();
        }
        Check.Require(Number(inspect, "SELECT count(*) FROM synced_entry WHERE typeof(local_id)='integer' AND typeof(dropbox_hash)='blob' AND length(dropbox_hash)=32") == 8,
            "native ids occupy INTEGER values and hashes occupy exactly 32 bytes");
        Check.Require(Number(inspect, "SELECT count(*) FROM synced_entry WHERE dropbox_key_delta IS NOT NULL OR dropbox_path_delta IS NOT NULL") == 0,
            "equal saved spellings are stored once");
        using (var scan = Inspect(ScanPath(path, "a")))
            Check.Require(Number(scan, "SELECT count(*) FROM scan WHERE key_delta IS NOT NULL") == 0,
                "equal scan keys have no stored override");
        Check.Require(store.ScanRows("a", _ => Context).All(row => row.Dropbox!.ContentHash == row.Synced!.DropboxContentHash && row.Local!.ContentHash == ""),
            "row reads decode full hashes and preserve unknown empty hashes");
        var unknown = Saved("unknown");
        store.Commit("a", StateChange.Empty with { Record = [unknown] });
        Check.Require(store.SyncedByKey("a", Side.Local, unknown.LocalKey)!.DropboxContentHash == "" &&
            Number(inspect, "SELECT length(dropbox_hash) FROM synced_entry WHERE pathkey(local_path)='UNKNOWN'") == 0,
            "an unknown hash stays empty, not a zero-filled digest");

        var parent = Saved("Docs") with { IsFolder = true };
        var child = Saved("Docs/File");
        store.Commit("a", StateChange.Empty with { Record = [parent, child] });
        var moved = parent with { LocalKey = "PAPERS", LocalPath = "Papers" };
        store.Commit("a", StateChange.Empty with { Renames = [new RenameApplied(parent, moved, null, null, null)] });
        var rebased = store.SyncedByKey("a", Side.Local, "PAPERS/FILE")!;
        Check.Require(rebased.DropboxPath == child.DropboxPath && rebased.DropboxKey == child.DropboxKey,
            "a local-only rebase freezes the previously inherited Dropbox spelling");
        Check.Require(Number(inspect, "SELECT count(*) FROM synced_entry WHERE pathkey(local_path)='PAPERS/FILE' AND dropbox_key_delta='DOCS/FILE' AND dropbox_path_delta='Docs/File'") == 1,
            "the unmoved Dropbox spelling is an explicit override");
        var both = moved with { DropboxKey = moved.LocalKey, DropboxPath = moved.LocalPath };
        store.Commit("a", StateChange.Empty with { Renames = [new RenameApplied(moved, both, null, null, null)] });
        Check.Require(Number(inspect, "SELECT count(*) FROM synced_entry WHERE pathkey(local_path) LIKE 'PAPERS%' AND (dropbox_key_delta IS NOT NULL OR dropbox_path_delta IS NOT NULL)") == 0,
            "a both-sides rebase recomputes and normalises overrides in the transaction");
        var differing = Saved("different") with { DropboxKey = "ELSEWHERE", DropboxPath = "ElseWhere" };
        store.Commit("a", StateChange.Empty with { Record = [differing] });
        Check.Require(store.SyncedByKey("a", Side.Dropbox, "ELSEWHERE") == differing,
            "differing key and path spellings remain exact");
        Check.Require(Number(inspect, "SELECT count(*) FROM synced_entry WHERE typeof(pair_id)<>'integer'") == 0,
            "child rows use integer pair ids");
    }

    private static void SavedRebases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair("a"));
        using var inspect = Inspect(path);
        foreach (var dropboxRoot in new[] { "Docs", "Remote" })
        foreach (var localAfter in new[] { "Docs", "Papers", "docs", "Remote" })
        foreach (var dropboxAfter in new[] { dropboxRoot, "Papers", dropboxRoot.ToLowerInvariant() })
        {
            store.ForgetAllSynced("a");
            var parent = Saved("Docs") with
            {
                IsFolder = true, DropboxKey = PathNames.DropboxKey(dropboxRoot), DropboxPath = dropboxRoot,
            };
            var after = parent with
            {
                LocalKey = PathNames.Key(localAfter), LocalPath = localAfter,
                DropboxKey = PathNames.DropboxKey(dropboxAfter), DropboxPath = dropboxAfter,
            };
            // Explicit memberships include inherited keys, path-only deltas and independently moved children.
            var children = new (string Local, string Dropbox, string? LocalSuffix, string? DropboxSuffix)[]
            {
                ("docs/File", dropboxRoot.ToLowerInvariant() + "/File", "/File", "/File"),
                ("Docs/Inherited", "Docs/Inherited", "/Inherited", dropboxRoot == "Docs" ? "/Inherited" : null),
                ("Docs/Spelled", dropboxRoot.ToLowerInvariant() + "/sPeLlEd", "/Spelled", "/sPeLlEd"),
                ("Docs/Mapped_", dropboxRoot + "/Mapped?", "/Mapped_", "/Mapped?"),
                ("Docs/LocalOnly", "Elsewhere/LocalOnly", "/LocalOnly", null),
                ("Elsewhere/CloudOnly", dropboxRoot.ToLowerInvariant() + "/CloudOnly", null, "/CloudOnly"),
                ("docs/Nested/Child", dropboxRoot.ToLowerInvariant() + "/Nested/Child", "/Nested/Child", "/Nested/Child"),
                ("Docs2/Outside", dropboxRoot + "2/Outside", null, null),
            };
            var saved = children.Select((child, index) => Saved(child.Local, "child-" + index, "opaque") with
            {
                DropboxKey = PathNames.DropboxKey(child.Dropbox), DropboxPath = child.Dropbox,
            }).ToArray();
            store.Commit("a", StateChange.Empty with { Record = saved.Prepend(parent).ToArray() });
            store.Commit("a", StateChange.Empty with { Renames = [new RenameApplied(parent, after, null, null, null)] });
            Check.Require(store.SyncedByKey("a", Side.Local, after.LocalKey) == after &&
                store.SyncedByKey("a", Side.Dropbox, after.DropboxKey) == after &&
                Number(inspect, "SELECT count(*) FROM synced_entry") == saved.Length + 1,
                "each rebase preserves the root and every child exactly once");
            for (var index = 0; index < children.Length; index++)
            {
                var child = children[index];
                var local = localAfter != parent.LocalPath && child.LocalSuffix is not null
                    ? localAfter + child.LocalSuffix : child.Local;
                var dropbox = dropboxAfter != parent.DropboxPath && child.DropboxSuffix is not null
                    ? dropboxAfter + child.DropboxSuffix : child.Dropbox;
                var expected = saved[index] with
                {
                    LocalKey = PathNames.Key(local), LocalPath = local,
                    DropboxKey = PathNames.DropboxKey(dropbox), DropboxPath = dropbox,
                };
                Check.Require(store.SyncedByKey("a", Side.Local, expected.LocalKey) == expected &&
                    store.SyncedByKey("a", Side.Dropbox, expected.DropboxKey) == expected,
                    "unchanged sides preserve exact child spelling; changed sides rebase only their own old range");
                using var delta = StorageCommand.Create(inspect, null,
                    "SELECT count(*) FROM synced_entry WHERE pathkey(local_path)=$localKey " +
                    "AND dropbox_key_delta IS $dropboxKey AND dropbox_path_delta IS $dropboxPath;",
                    (StateSchema.ParamLocalKey, expected.LocalKey),
                    (StateSchema.ParamDropboxKey, expected.DropboxKey == expected.LocalKey ? null : expected.DropboxKey),
                    (StateSchema.ParamDropboxPath, expected.DropboxPath == expected.LocalPath ? null : expected.DropboxPath));
                Check.Require((long)delta.ExecuteScalar()! == 1,
                    "rebases keep only necessary key and spelling deltas, including mapped names and case-only changes");
            }
        }
    }

    private static void Paging(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair("a"));
        store.BeginScan("a", Generation(store, "a"), false);
        var sink = store.OpenScan("a", Side.Local, ScanTestSettings.BatchRows);
        foreach (var name in new[] { "", "A", "B", "C", "D", "E", "Z" })
            sink.AddAsync(new Listed(name, new Entry(name, true, 0, default, "1", "", "", null)), default).GetAwaiter().GetResult();
        sink.CompleteAsync().GetAwaiter().GetResult();
        sink = store.OpenScan("a", Side.Dropbox, ScanTestSettings.BatchRows);
        foreach (var name in new[] { "B", "D", "M:N", "M?N", "M_N" })
            sink.AddAsync(new Listed(name, new Entry(name, true, 0, default, "id:" + name, "", "", null)), default).GetAwaiter().GetResult();
        sink.CompleteAsync().GetAwaiter().GetResult();
        var linked = Saved("B") with { DropboxKey = "D", DropboxPath = "D", IsFolder = true };
        store.Commit("a", StateChange.Empty with { Record = [linked] });
        using var inspect = Inspect(path);
        var seen = new List<(string Key, bool Saved)>();
        foreach (var row in store.ScanRows("a", _ => Context))
        {
            seen.Add((row.Key, row.Synced is not null));
            Check.Require(Number(inspect, "PRAGMA wal_checkpoint(TRUNCATE)") == 0,
                "yielding a page holds no SQLite read transaction, even between groups at the same key");
            if (row.Key == "B" && row.Synced is not null)
                store.Commit("a", StateChange.Empty with { ForgetSelf = [(Side.Local, "B")] });
            if (row.Key == "C")
                store.Commit("a", StateChange.Empty with { Record = [Saved("C") with { IsFolder = true }] });
            if (row.Key is "M:N" or "M?N" or "M_N")
                Check.Require(!row.Ambiguous && row.Dropbox!.Path == row.Key,
                    "raw Dropbox spellings keep separate keys; name mapping is deferred");
        }
        Check.Require(seen.SequenceEqual(new[] { ("", false), ("A", false), ("B", true), ("B", false), ("C", false),
            ("D", false), ("E", false), ("M:N", false), ("M?N", false), ("M_N", false), ("Z", false) }),
            "keyset pages yield every linked/free group exactly once despite commits between pages");
    }

    internal static RepairSummary Summary(bool completed = true) => new("run", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        completed, false, null, false, new Dictionary<string, int>(), new Dictionary<string, int>(), "", false);

    /// <summary>Completes the captured scan through the same authoritative fences and catch-ups Repair passes.</summary>
    private static void CompleteRepair(StateStore store, string pairId, Guid token, long generation)
    {
        var local = store.ReadSource(pairId, Side.Local)!.Value;
        var dropbox = store.ReadSource(pairId, Side.Dropbox)!.Value;
        var domain = store.Pair(pairId)!.Roots!.Local.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture);
        if (store.ReadJournal(domain) is null)
        {
            var members = new[]
            {
                (pairId, generation, local.Epoch, local.Revision, false),
            };
            store.AcquireJournalPage(domain, null, new JournalPosition(Volume, 0), members, []);
        }
        var position = store.ReadJournal(domain)!;
        var through = store.ReadWorkWatermark();
        store.FinishRepair(pairId, Summary(), generation, token,
            new LocalCatchUp(pairId, generation, local.Epoch,
                new NtfsUsnCheckpoint(position.JournalId, 0, (long)position.NextUsn, 0), through),
            new DropboxCatchUp(pairId, generation, dropbox.Epoch, dropbox.Cursor!, through));
    }

    private static string ScanPath(string path, string pair) => path + StateSchema.ScanFileSuffix +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pair))) +
        StateSchema.DatabaseExtension;

    private static void Reclamation(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(BoundPair("a"));
        store.SavePair(Pair("b"));
        using var inspect = Inspect(path);
        store.BeginScan("b", Generation(store, "b"), false);
        var other = store.OpenScan("b", Side.Local, ScanTestSettings.BatchRows);
        other.AddAsync(new Listed("keep", new Entry("keep", true, 0, default, "1", "", "", null)), default).GetAwaiter().GetResult();
        other.CompleteAsync().GetAwaiter().GetResult();
        long previous = 0;
        for (var run = 0; run < 3; run++)
        {
            var generation = Generation(store, "a");
            var token = store.BeginScan("a", generation, false);
            var sink = store.OpenScan("a", Side.Local, ScanTestSettings.BatchRows);
            for (var index = 0; index < 300; index++)
            {
                var name = new string('x', 150) + index.ToString("D4");
                sink.AddAsync(new Listed(name, new Entry(name, true, 0, default, index.ToString(), "", "", null)), default).GetAwaiter().GetResult();
            }
            sink.CompleteAsync().GetAwaiter().GetResult();
            using (var scan = Inspect(ScanPath(path, "a")))
                Check.Require(Number(scan, "SELECT count(*) FROM scan") == 300 &&
                    Number(scan, "SELECT count(*) FROM sqlite_master WHERE name='scan_id'") == 0,
                    "the disposable file holds the scan without an identity index");
            Check.Require(Number(inspect, "SELECT count(*) FROM sqlite_master WHERE name='scan'") == 0,
                "scan pages never enter the durable database");
            store.FinishRepair("a", Summary(false));
            Check.Require(File.Exists(ScanPath(path, "a")) && store.ScanRows("a", _ => Context).Count() == 300,
                "incomplete runs leave the scan intact");
            using (var reader = store.ScanRows("a", _ => Context).GetEnumerator())
                Check.Require(reader.MoveNext(), "the scan reader is disposed before completion");
            CompleteRepair(store, "a", token, generation);
            store.EndScanAsync("a", token, 1).GetAwaiter().GetResult();
            var after = Number(inspect, "PRAGMA page_count");
            Check.Require(!File.Exists(ScanPath(path, "a")) && store.InScan("b", Side.Local, "KEEP"),
                "completion deletes its file after readers close and preserves the other pair");
            Check.Require(Number(inspect, "PRAGMA auto_vacuum") == 2 && Number(inspect, "PRAGMA freelist_count") == 0,
                "completion consumes incremental reclamation fully");
            Check.Require(run == 0 || after == previous, "repeated completed runs have no growing main file or freelist");
            previous = after;
            Check.Require(new FileInfo(path + "-wal").Length == 0, "the run's WAL is truncated");
        }
        store.BeginScan("b", Generation(store, "b"), false);
        Check.Require(!store.InScan("b", Side.Local, "KEEP"),
            "the next scan discards an incomplete listing without retaining its entries");
        var writer = store.OpenScan("b", Side.Local, ScanTestSettings.BatchRows);
        writer.AddAsync(new Listed("again", new Entry("again", true, 0, default, "2", "", "", null)), default).GetAwaiter().GetResult();
        writer.CompleteAsync().GetAwaiter().GetResult();
        store.Commit("b", StateChange.Empty with { Record = Enumerable.Range(0, 300).Select(index => Saved("Parent/" + new string('x', 150) + index)).ToArray() });
        var beforeRemoval = Number(inspect, "PRAGMA page_count");
        store.RemovePair("b");
        store.EndScanAsync("b", ScanFixture.Token(store, "b"), 1).GetAwaiter().GetResult();
        Check.Require(!File.Exists(ScanPath(path, "b")) && Number(inspect, "PRAGMA freelist_count") == 0 &&
            Number(inspect, "PRAGMA page_count") < beforeRemoval && store.Pair("a") is not null,
            "pair removal deletes only its scan and reclaims its durable pages");
        using var readOnly = StateStore.OpenReadOnly(path);
        Check.Require(readOnly!.Pairs().Count == 1 && !File.Exists(ScanPath(path, "a")),
            "read-only status does not create a scan file");
        RefusesScan(() => readOnly.ReadScan("a", Guid.NewGuid()));
    }

    private static void RestartScans(string path)
    {
        using (var store = StateStore.Open(path))
        {
            store.SavePair(Pair("a"));
            store.SavePair(Pair("b"));
            store.BeginScan("a", Generation(store, "a"), false);
            var first = store.OpenScan("a", Side.Local, ScanTestSettings.BatchRows);
            store.BeginScan("b", Generation(store, "b"), false);
            var other = store.OpenScan("b", Side.Local, ScanTestSettings.BatchRows);
            first.AddAsync(new Listed("a", new Entry("a", true, 0, default, "a", "", "", null)), default).GetAwaiter().GetResult();
            other.AddAsync(new Listed("b", new Entry("b", true, 0, default, "b", "", "", null)), default).GetAwaiter().GetResult();
            Task.WhenAll(first.CompleteAsync(), other.CompleteAsync()).GetAwaiter().GetResult();
            Check.Require(store.InScan("a", Side.Local, "A") && !store.InScan("a", Side.Local, "B") &&
                store.InScan("b", Side.Local, "B") && !store.InScan("b", Side.Local, "A"),
                "interleaved pair writers select their own attached file under the shared lock");
            store.FinishRepair("a", Summary(false));
            using var readOnly = StateStore.OpenReadOnly(path);
            RefusesScan(() => readOnly!.ReadScan("a", ScanFixture.Token(store, "a")));
        }
        using (var store = StateStore.Open(path))
        {
            RefusesScan(() => store.ReadScan("a", Guid.NewGuid()));
            store.BeginScan("a", Generation(store, "a"), false);
            Check.Require(!store.InScan("a", Side.Local, "A") && File.Exists(ScanPath(path, "b")),
                "restart discards the old pair scan without touching another pair");
            var first = store.OpenScan("a", Side.Local, ScanTestSettings.BatchRows);
            first.AddAsync(new Listed("Parent", new Entry("Parent", true, 0, default, "p", "", "", null)), default).GetAwaiter().GetResult();
            first.AddAsync(new Listed("Parent/Child", new Entry("Parent/Child", false, 1, default, "c", "", "", null)), default).GetAwaiter().GetResult();
            first.CompleteAsync().GetAwaiter().GetResult();
            var reopened = store.OpenScan("a", Side.Local, ScanTestSettings.BatchRows);
            reopened.AddAsync(new Listed("Parent", new Entry("Parent", false, 1, default, "file", "", "", null)), default).GetAwaiter().GetResult();
            reopened.CompleteAsync().GetAwaiter().GetResult();
            Check.Require(store.ScanEntry("a", Side.Local, "PARENT/CHILD") is null,
                "only the first Local writer after BeginScan uses the empty-scan fast path");
            store.FinishRepair("a", Summary(false));
        }
        File.WriteAllText(ScanPath(path, "a"), "interrupted disposable scan");
        using (var store = StateStore.Open(path))
        {
            store.BeginScan("a", Generation(store, "a"), false);
            Check.Require(!store.ScanRows("a", _ => Context).Any(),
                "BeginScan discards a damaged disposable file without reading it");
        }
    }

    private static void RefusesScan(Action read)
    {
        try { read(); }
        catch (LiveAdmissionStaleException) { return; }
        throw new InvalidOperationException("Scratch bytes do not confer scan authority after reopen.");
    }

    private static void ScanReadConnections(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair("a"));
        using (var read = ReadScan(store, "a"))
        {
            Check.Require(Number(read, "SELECT count(*) FROM scan") == 0 && !File.Exists(ScanPath(path, "a")),
                "an absent scan reads empty on a native URI-special path without creating a file");
            RefusesWrite(read, "pair");
        }
        store.BeginScan("a", Generation(store, "a"), false);
        var writer = store.OpenScan("a", Side.Local, ScanTestSettings.BatchRows);
        writer.AddAsync(new Listed("entry", new Entry("entry", true, 0, default, "id", "", "", null)), default).GetAwaiter().GetResult();
        writer.CompleteAsync().GetAwaiter().GetResult();
        using (var read = ReadScan(store, "a"))
        {
            Check.Require(Number(read, "SELECT count(*) FROM scan") == 1,
                "a present scan is visible on a native URI-special path");
            RefusesWrite(read, "pair");
            RefusesWrite(read, "scan_state.scan");
        }
        Check.Require(File.Exists(ScanPath(path, "a")) && store.Pair("a") is not null,
            "read connections create no scan file and preserve durable state");
    }

    private static SqliteConnection ReadScan(StateStore store, string pairId) =>
        (SqliteConnection)typeof(StateStore).GetMethod("OpenScanRead",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(store, [pairId])!;

    private static void RefusesWrite(SqliteConnection read, string table)
    {
        try
        {
            Number(read, "DELETE FROM " + table + "; SELECT 0;");
            throw new InvalidOperationException("a scan reader must not write an on-disk database");
        }
        catch (SqliteException error)
        {
            Check.Require(error.SqliteErrorCode == 8, "both on-disk attachments are genuinely read-only");
        }
    }

    private static void DeferredScanDeletion(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(BoundPair("a"));
        var generation = Generation(store, "a");
        var token = store.BeginScan("a", generation, false);
        var writer = store.OpenScan("a", Side.Local, ScanTestSettings.BatchRows);
        writer.AddAsync(new Listed("entry", new Entry("entry", true, 0, default, "id", "", "", null)), default).GetAwaiter().GetResult();
        writer.CompleteAsync().GetAwaiter().GetResult();
        var scanPath = ScanPath(path, "a");
        using (var held = new FileStream(scanPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            CompleteRepair(store, "a", token, generation);
            try
            {
                store.EndScanAsync("a", token, 1).GetAwaiter().GetResult();
                throw new InvalidOperationException("The foreign handle must prevent scratch deletion");
            }
            catch (IOException) { }
            Check.Require(store.LastRepair("a")!.Completed && store.FirstRepairDone("a") && File.Exists(scanPath),
                "a sharing violation during scan cleanup does not fail a committed repair");
            try
            {
                store.BeginScan("a", generation, false);
                throw new InvalidOperationException("BeginScan must not proceed with an undeletable old scan");
            }
            catch (IOException) { }
        }
        store.BeginScan("a", generation, false);
        Check.Require(!store.ScanRows("a", _ => Context).Any(),
            "the next BeginScan discards the leftover file once its foreign handle closes");
    }

    private static void BusyCheckpoint(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(BoundPair("a"));
        using var reader = Inspect(path);
        using var snapshot = reader.BeginTransaction(deferred: true);
        using var hold = reader.CreateCommand();
        hold.Transaction = snapshot;
        hold.CommandText = "SELECT count(*) FROM pair;";
        hold.ExecuteScalar();
        store.SavePair(Pair("b"));
        var generation = Generation(store, "a");
        var token = store.BeginScan("a", generation, false);
        CompleteRepair(store, "a", token, generation);
        Check.Require(store.LastRepair("a")!.Completed && store.FirstRepairDone("a"),
            "a busy checkpoint does not fail an already completed repair");
        snapshot.Commit();
        generation = Generation(store, "a");
        token = store.BeginScan("a", generation, false);
        CompleteRepair(store, "a", token, generation);
        Check.Require(new FileInfo(path + "-wal").Length == 0, "a later checkpoint truncates after the snapshot ends");
    }

    private static void OpaqueHashes(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair("a"));
        using var inspect = Inspect(path);
        var hashes = new[] { "", new string('a', 64), new string('A', 64), new string('g', 64), "opaque", new string('a', 63) };
        for (var index = 0; index < hashes.Length; index++)
        {
            var entry = Saved("hash" + index, hash: hashes[index]);
            store.Commit("a", StateChange.Empty with { Record = [entry] });
            Check.Require(store.SyncedByKey("a", Side.Local, entry.LocalKey)!.DropboxContentHash == hashes[index],
                "canonical and opaque hashes round-trip exactly");
        }
        Check.Require(Number(inspect, "SELECT count(*) FROM synced_entry WHERE typeof(dropbox_hash)='blob'") == 2 &&
            Number(inspect, "SELECT count(*) FROM synced_entry WHERE typeof(dropbox_hash)='text'") == 4,
            "only empty and canonical lowercase full hashes are packed");
    }

    private static void OldVersion(string path)
    {
        foreach (var version in new[] { 0, 1, 2, 3 })
        {
            using (var connection = Inspect(path))
                Number(connection, $"CREATE TABLE IF NOT EXISTS old_engine(value TEXT); PRAGMA user_version={version}; SELECT 0;");
            var before = File.ReadAllBytes(path);
            foreach (var readOnly in new[] { false, true })
            {
                try
                {
                    using var ignored = readOnly ? StateStore.OpenReadOnly(path) : StateStore.Open(path);
                    Check.Require(false, "an older engine database must be refused");
                }
                catch (InvalidDataException error)
                {
                    Check.Require(error.Message == Text.Get(TextKey.StateSchemaTooOld, (TextArg.Path, path)),
                        "the dedicated refusal supplies the exact database path and fresh-pair instruction");
                }
                Check.Require(File.ReadAllBytes(path).SequenceEqual(before), "refusing an older database does not migrate or change it");
            }
        }
    }

    private static void DerivedLocalKeys(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair("a"));
        using var inspect = Inspect(path);
        Check.Require(Number(inspect, "SELECT count(*) FROM pragma_table_xinfo('synced_entry') WHERE name='local_key'") == 0,
            "saved local keys have no stored or virtual column; the expression index owns uniqueness");
        foreach (var name in new[]
        {
            "", "Mixed/Case", "A%_é/雪", "Folder/\U00010428", "Folder/\U00010400",
            "Folder/I", "Folder/i", "Folder/İ", "Folder/ı", "Folder/ß", "Folder/ẞ",
            "Folder/Σ", "Folder/σ", "Folder/ς", "Folder/S", "Folder/ſ",
        })
        {
            var entry = Saved(name);
            store.Commit("a", StateChange.Empty with { Record = [entry] });
            Check.Require(store.SyncedByKey("a", Side.Local, PathNames.Key(name)) == entry &&
                store.SyncedByKey("a", Side.Dropbox, PathNames.DropboxKey(name)) == entry,
                "local and Dropbox lookups use the frozen path identity for empty, Unicode and mixed-case paths");
        }
        var keep = Saved("keep");
        store.Commit("a", StateChange.Empty with { Record = [keep] });
        var invalid = Saved("incoming") with { LocalKey = "WRONG" };
        foreach (var change in new[]
        {
            StateChange.Empty with { Record = [invalid] },
            StateChange.Empty with { Renames = [new RenameApplied(null, invalid, null, null, null)] },
            StateChange.Empty with { Renames = [new RenameApplied(keep, invalid, null, null, null)] },
            StateChange.Empty with { Renames = [new RenameApplied(invalid, Saved("incoming"), null, null, null)] },
        })
        {
            try
            {
                store.Commit("a", change with { ForgetSelf = [(Side.Local, keep.LocalKey)] });
                Check.Require(false, "a mismatched saved local key must be rejected at the commit boundary");
            }
            catch (ArgumentException) { }
            Check.Require(store.SyncedByKey("a", Side.Local, keep.LocalKey) == keep &&
                store.SyncedByKey("a", Side.Local, "INCOMING") is null,
                "invalid Record, Before and After keys cannot alter any stored state");
        }
    }

    private static void Scaling(string folder)
    {
        var samples = new List<(int Count, long Listed, long Finished, long Wal)>();
        foreach (var count in new[] { 200, 800 })
        {
            var path = Path.Combine(folder, "size-" + count + ".db");
            using var store = StateStore.Open(path);
            store.SavePair(BoundPair("a"));
            using var inspect = Inspect(path);
            var entries = Enumerable.Range(0, count).Select(index => Saved(
                "Parent/" + new string('x', 120) + "/File" + index.ToString("D8"),
                (long.MaxValue - index).ToString(CultureInfo.InvariantCulture), new string('a', 64))).ToArray();
            store.Commit("a", StateChange.Empty with { Record = entries });
            var generation = Generation(store, "a");
            var token = store.BeginScan("a", generation, false);
            foreach (var side in new[] { Side.Local, Side.Dropbox })
            {
                var sink = store.OpenScan("a", side, ScanTestSettings.BatchRows);
                foreach (var entry in entries)
                    sink.AddAsync(new Listed(entry.LocalPath, new Entry(entry.LocalPath, false, 12, DateTimeOffset.UnixEpoch,
                        side == Side.Local ? entry.LocalId : entry.DropboxId, "", side == Side.Local ? "" : entry.DropboxContentHash, null)),
                        default).GetAwaiter().GetResult();
                sink.CompleteAsync().GetAwaiter().GetResult();
            }
            Number(inspect, "PRAGMA wal_checkpoint(TRUNCATE)");
            var listed = new FileInfo(path).Length + new FileInfo(ScanPath(path, "a")).Length;
            long peak = 0;
            foreach (var row in store.ScanRows("a", _ => Context))
            {
                store.Commit("a", StateChange.Empty with { Record = [row.Synced! with { LocalModifiedTicks = 35 }] });
                peak = Math.Max(peak, new FileInfo(path + "-wal").Length);
            }
            CompleteRepair(store, "a", token, generation);
            var finished = new FileInfo(path).Length;
            samples.Add((count, listed, finished, peak));
            Console.WriteLine($"storage {count} items: listed={listed / (double)count:F1} B/item finished={finished / (double)count:F1} B/item peak WAL={peak / (double)count:F1} B/item final WAL={new FileInfo(path + "-wal").Length}");
        }
        var small = samples[0];
        var large = samples[1];
        // Permit one SQLite page of rounding; a larger per-item footprint is not hidden by a percentage allowance.
        Check.Require(large.Listed * small.Count <= small.Listed * large.Count + 4096 &&
            large.Finished * small.Count <= small.Finished * large.Count + 4096 &&
            large.Wal * small.Count <= small.Wal * large.Count + 4096,
            "database and write-log bytes per item do not grow with the pair");
    }

    private static void ContractCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair("a"));
        using var inspect = Inspect(path);

        Check.Require(StateStore.DatabasePath("root") ==
            Path.Combine("root", ResourceNames.EngineFolder, ResourceNames.DatabaseFile),
            "the engine database path has one Storage owner");

        // Create lays down the base schema only; fresh creation publishes the version after the additions.
        using (var memory = Inspect(":memory:"))
        {
            using (var create = memory.CreateCommand())
            {
                create.CommandText = StateSchema.Create;
                create.ExecuteNonQuery();
            }
            Check.Require(Number(memory, "PRAGMA user_version") == 0,
                "the base schema alone publishes no version");
            using (var create = memory.CreateCommand())
            {
                create.CommandText = StateSchema.SchemaAdditions + StateSchema.DeletedHistorySchema + StateSchema.RebuildIssues + StateSchema.DeletedHistoryBlocks + StateSchema.DropDeletedHistoryNames + StateSchema.SpentDeletedHistory + StateSchema.LocalPublishedColumns + StateSchema.DiscoveryRequiredColumn + StateSchema.IssuePathKeyIndex + StateSchema.PublishVersion;
                create.ExecuteNonQuery();
            }
            Check.Require(Number(memory, "PRAGMA user_version") == StateSchema.Version,
                "the schema's user_version literal matches StateSchema.Version");
        }

        using (var command = StorageCommand.Create(inspect, null, "SELECT " + StateSchema.ParamSkip + " IS NULL;",
            (StateSchema.ParamSkip, null)))
            Check.Require((long)command.ExecuteScalar()! == 1, "a null argument binds as database NULL");

        var opaque = Saved("opaque") with { LocalId = "007", DropboxId = "007" };
        store.Commit("a", StateChange.Empty with { Record = [opaque] });
        Check.Require(Number(inspect,
                "SELECT count(*) FROM synced_entry WHERE local_id='007' AND typeof(local_id)='text'") == 1 &&
            store.SyncedById("a", Side.Local, "007") == opaque &&
            store.SyncedById("a", Side.Dropbox, "007") == opaque,
            "noncanonical ids stay opaque while canonical integers pack");

        var flagged = Saved("flagged") with { IsFolder = true };
        store.Commit("a", StateChange.Empty with { Record = [flagged] });
        using (var update = inspect.CreateCommand())
        {
            update.CommandText = "UPDATE synced_entry SET is_folder=2 WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) " +
                "AND pathkey(local_path)=$k;";
            update.Parameters.AddWithValue(StateSchema.ParamPair, "a");
            update.Parameters.AddWithValue(StateSchema.ParamTargetKey, PathNames.Key("flagged"));
            update.ExecuteNonQuery();
        }
        Check.Require(store.SyncedByKey("a", Side.Local, PathNames.Key("flagged"))!.IsFolder,
            "a nonzero persisted flag decodes true");

        store.SavePair(Pair("corrupt"));
        using (var update = inspect.CreateCommand())
        {
            update.CommandText = "UPDATE pair SET def_json='null' WHERE pair_id=$p;";
            update.Parameters.AddWithValue(StateSchema.ParamPair, "corrupt");
            update.ExecuteNonQuery();
        }
        var invalid = false;
        try
        {
            store.Pair("corrupt");
        }
        catch (InvalidDataException)
        {
            invalid = true;
        }
        Check.Require(invalid, "a stored null JSON value is rejected as invalid data");
    }

    private static void PartialKeyCollisions(string path)
    {
        using var store = StateStore.Open(path);
        // DESIGN-126-4 §2: replacing a crossed binding registers work against the pair's current roots.
        store.SavePair(BoundPair("a"));
        foreach (var inheritedFirst in new[] { true, false })
        {
            store.ForgetAllSynced("a");
            var first = Saved("A") with { DropboxKey = "B", DropboxPath = "B" };
            var second = Saved("B");
            var existing = inheritedFirst ? second : first;
            var incoming = inheritedFirst ? first : second;
            store.Commit("a", StateChange.Empty with { Record = [existing] });
            try
            {
                // A no-record rename inserts without Record's deliberate replace-either-key semantics.
                store.Commit("a", StateChange.Empty with { Renames = [new RenameApplied(null, incoming, null, null, null)] });
                Check.Require(false, "an inherited and an override key cannot coexist at the same effective key");
            }
            catch (PathOccupiedException error)
            {
                Check.Require(error.Message.Contains("already taken"), "cross-representation uniqueness rejects the collision");
            }
            Check.Require(store.SyncedByKey("a", Side.Dropbox, "B") == existing &&
                store.SyncedByKey("a", Side.Local, existing.LocalKey) == existing &&
                store.SyncedByKey("a", Side.Local, incoming.LocalKey) is null,
                "both collision directions roll back without losing the prior record");
            var watermark = store.ReadWorkWatermark();
            store.Commit("a", StateChange.Empty with { Record = [incoming] });
            Check.Require(store.SyncedByKey("a", Side.Dropbox, "B") == incoming,
                "Record still intentionally replaces an existing effective Dropbox key");
            Check.Require(store.ReadWorkWatermark() == watermark + (inheritedFirst ? 0 : 1),
                "Only replacing the crossed binding releases its former Local endpoint.");
        }
        foreach (var movingOverride in new[] { false, true })
        {
            store.ForgetAllSynced("a");
            var parent = Saved("Old") with { IsFolder = true };
            var child = Saved("Old/Child");
            var occupied = movingOverride ? Saved("New/Child") :
                Saved("Elsewhere") with { DropboxKey = "NEW/CHILD", DropboxPath = "New/Child" };
            var after = parent with { DropboxKey = "NEW", DropboxPath = "New" };
            if (!movingOverride) after = after with { LocalKey = "NEW", LocalPath = "New" };
            store.Commit("a", StateChange.Empty with { Record = [parent, child, occupied] });
            try
            {
                store.Commit("a", StateChange.Empty with { Renames = [new RenameApplied(parent, after, null, null, null)] });
                Check.Require(false, "a descendant rebase cannot create a cross-representation key collision");
            }
            catch (InvalidOperationException error)
            {
                Check.Require(error.Message.Contains("already taken"), "subtree uniqueness probes reject both collision directions");
            }
            Check.Require(store.SyncedByKey("a", Side.Local, parent.LocalKey) == parent &&
                store.SyncedByKey("a", Side.Local, child.LocalKey) == child &&
                store.SyncedByKey("a", Side.Dropbox, occupied.DropboxKey) == occupied,
                "a colliding descendant rolls back the root and the whole rebase");
        }
    }
}
