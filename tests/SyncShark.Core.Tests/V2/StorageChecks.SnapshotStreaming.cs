using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    public static void SnapshotStreamingChecks(string root)
    {
        CompactMatchesSerializeIdentically();
        for (var seed = 0; seed < 12; seed++) SnapshotDifferential(root, seed);
        using var fixture = new SnapshotFixture(Fresh(root, "snapshot-read-only"));
        var items = Enumerable.Range(0, 1100).Select(i => F($"d{i % 7}/f{i:D5}", "id:" + i, "aabb")).ToArray();
        using var staged = SnapshotInput.Create(fixture.Connection, fixture.Dirs, items);
        fixture.Write(staged);
        var plans = staged.Plans().Concat(StoreSchema.QueryPlan(fixture.Connection, "LiveDirectory",
            FileStateReader.SnapshotDirectorySql, "$r", "$d")).Concat(StoreSchema.QueryPlan(fixture.Connection,
            "LivePage", FileStateReader.SnapshotPageSql, "$r", "$d", "$k", "$n")).ToArray();
        foreach (var plan in plans) Console.WriteLine(plan);
        Require(!plans.Any(p => p.Contains("TEMP B-TREE") || (p.Contains(": SCAN ") && !p.EndsWith("SCAN CONSTANT ROW", StringComparison.Ordinal))),
            "snapshot pages and object suppression have no root sort or scan");
        Require(plans.Any(p => p.StartsWith("LivePage:") && p.Contains("USING PRIMARY KEY (root_no=? AND dir_no=?")),
            "live page uses the root/directory primary key range");
        Require(plans.Any(p => p.StartsWith("IncomingObject:") && p.Contains("incoming_object")),
            "suppression probes the non-unique object index");
        // SQLite itself rejects even zero-row DML under query_only: stronger than total_changes.
        StoreSchema.Execute(fixture.Connection, null, "PRAGMA query_only=ON;");
        fixture.Write(staged);
        Require(fixture.Reader.Live(1, null).Count == items.Length, "durable unchanged input is entirely read-only");
        StoreSchema.Execute(fixture.Connection, null, "PRAGMA query_only=OFF;");
    }

    private static void CompactMatchesSerializeIdentically()
    {
        var random = new Random(614);
        for (var i = 0; i < 500; i++)
        {
            var a = F("match/" + i, "a:" + i, "AAbB", random.Next(100), T0.AddTicks(i)) with { Hash = i % 3 == 0 ? null : "AAbB", VersionId = i % 5 == 0 ? "rev-a" : null };
            var b = a with { ObjectId = "b:" + i, VersionId = i % 2 == 0 ? "rev" : null,
                Kind = i % 7 == 0 ? ItemKind.Folder : ItemKind.File };
            // Same final post-hash planning rows: the old writer computed stamps at serialization.
            var old = new FirstSyncChoicesState.StoredMatch(a.Path, Content.SideStamp(a), Content.SideStamp(b));
            var compact = new MatchedPair(a.Path, Content.SideStamp(a), Content.SideStamp(b));
            var current = new FirstSyncChoicesState.StoredMatch(compact.Path, compact.A, compact.B);
            Require(JsonSerializer.Serialize(old) == JsonSerializer.Serialize(current),
                "compact matches preserve serialized prepare bytes");
        }
    }

    private static void SnapshotDifferential(string root, int seed)
    {
        using var current = new SnapshotFixture(Fresh(root, "merge-new-" + seed));
        using var legacy = new SnapshotFixture(Fresh(root, "merge-old-" + seed), legacy: true);
        var random = new Random(seed);
        // Reverse directory discovery order, case clashes, Unicode names, two pages in a directory.
        var initial = Enumerable.Range(0, 900).Select(i => F(
            (i < 650 ? "z/" : "a/") + $"f{i:D5}", "id:" + i, "aabb", i, T0)).ToList();
        initial.AddRange([F("z/A", "case:A", "aa"), F("z/a", "case:a", "bb"),
            F("é/文件", "unicode", "cc"), F("z/hard1", "id:local:hard", "dd"),
            F("a/hard2", "id:local:hard", "dd"), F("blocked/child", "blocked", "ee"), F("link/child", "link-child", "aa"),
            F("z/deleted-alias", "id:local:deleted", "aa"), F("b/deleted-alias", "id:local:deleted", "aa")]);
        current.Write(initial);
        legacy.Write(initial);
        foreach (var fixture in new[] { current, legacy })
        {
            foreach (var item in initial)
                StoreSchema.Execute(fixture.Connection, null, "INSERT INTO baseline_fixture VALUES($p,$o);",
                    ("$p", item.Path), ("$o", item.ObjectId));
            StoreSchema.Execute(fixture.Connection, null,
                "INSERT INTO local_rehash(root_no,object_id,last_attempt,reason) VALUES(1,'id:20',0,'gap');" +
                "UPDATE file_state SET flags=256,hash=NULL WHERE object_id='id:20';");
        }
        var next = new List<ItemState>();
        foreach (var item in initial)
        {
            switch (random.Next(6))
            {
                case 0: break;
                case 1: next.Add(item with { Size = item.Size + 1, Hash = null }); break;
                case 2: next.Add(item with { ObjectId = "replacement:" + item.ObjectId }); break;
                case 3: next.Add(item with { Path = "moved/" + item.Path }); break;
                default: next.Add(item with { Hash = null }); break;
            }
        }
        // The removed old name must see an object in a later directory/page; aliases must not be unique.
        next.RemoveAll(i => i.ObjectId == "id:local:hard" || i.ObjectId == "id:2" ||
            i.ObjectId == "id:local:deleted" || i.ObjectId == "link-child");
        next.AddRange([F("zz/last-hard1", "id:local:hard", "dd"), F("zz/last-hard2", "id:local:hard", "dd"),
            F("zz/last-move", "id:2", "aabb", 2, T0),
            D("link", "link-id") with { Flags = ItemFlags.Link }]);
        var unordered = next.OrderBy(_ => random.Next()).ToArray();
        Func<string, bool> include = p => !p.StartsWith("a/", StringComparison.Ordinal);
        string[] incomplete = ["blocked"];
        current.Write(unordered, include, incomplete);
        legacy.Write(unordered, include, incomplete);
        Require(current.Dump().SequenceEqual(legacy.Dump()), "streaming snapshot matches frozen writer, seed " + seed);
        current.Write(unordered);
        legacy.Write(unordered);
        Require(current.Dump().SequenceEqual(legacy.Dump()), "complete snapshot and debt cleanup match frozen writer, seed " + seed);
    }

    private sealed class SnapshotFixture : IDisposable
    {
        public SqliteConnection Connection { get; }
        public DirTable Dirs { get; }
        public FileStateReader Reader { get; }
        private readonly FileStateTable _writer;
        private readonly LegacySnapshotWriter? _legacy;

        public SnapshotFixture(string folder, bool legacy = false)
        {
            using (var store = SqliteStateStore.Open(folder, Fast, new TestClock())) store.BeginGeneration("root", "snapshot");
            Connection = new SqliteConnection($"Data Source={DbPath(folder)};Pooling=False");
            Connection.Open();
            StoreSchema.Execute(Connection, null, "PRAGMA synchronous=NORMAL; PRAGMA cache_size=-2048;" +
                "CREATE TABLE link_effect(path TEXT PRIMARY KEY);" +
                "CREATE TABLE baseline_fixture(path TEXT PRIMARY KEY,object_id TEXT);" +
                "CREATE INDEX baseline_object ON baseline_fixture(object_id);");
            Dirs = new DirTable(Connection);
            Reader = new FileStateReader(Connection, Dirs);
            // Small batches force transitions inside pages, moves and replacements.
            _writer = new FileStateTable(Connection, Dirs, Reader, Fast with { StoreBatchRows = 31 }, new TestClock(), Baselined);
            _writer.OnLinkWritten = Link;
            if (legacy)
            {
                _legacy = new LegacySnapshotWriter(Connection, Dirs, Reader, Fast with { StoreBatchRows = 31 }, new TestClock(), Baselined);
                _legacy.OnLinkWritten = Link;
            }
        }

        private bool Baselined(long root, string id, SqliteTransaction tx) =>
            StoreSchema.Scalar(Connection, tx, "SELECT 1 FROM baseline_fixture WHERE object_id=$o LIMIT 1;", ("$o", id)) is not null;

        private void Link(SqliteTransaction tx, long root, string path)
        {
            StoreSchema.Execute(Connection, tx, "INSERT OR IGNORE INTO link_effect VALUES($p);", ("$p", path));
            StoreSchema.Execute(Connection, tx,
                "DELETE FROM baseline_fixture WHERE path=$p OR substr(path,1,length($p)+1)=$p||'/';", ("$p", path));
        }

        public void Write(IReadOnlyList<ItemState> items, Func<string, bool>? include = null, IReadOnlyList<string>? incomplete = null)
        {
            var snapshot = new Snapshot("root", Side.A, 1, "c", items, incomplete ?? []) { IncludesPath = include };
            if (_legacy is null) _writer.WriteSnapshot(snapshot, 1);
            else _legacy.WriteSnapshot(snapshot, 1);
        }

        public string[] Dump()
        {
            var rows = Reader.Live(1, null).Select(i => "live:" + JsonSerializer.Serialize(i)).ToList();
            rows.AddRange(Reader.Tombstones(1).Select(t => "tomb:" + t));
            foreach (var sql in new[] { "SELECT object_id,reason,last_attempt FROM local_rehash ORDER BY object_id;", "SELECT path FROM link_effect ORDER BY path;", "SELECT path,object_id FROM baseline_fixture ORDER BY path;" })
            {
                using var command = StoreSchema.Command(Connection, null, sql);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    rows.Add(sql + string.Join("|", values));
                }
            }
            return rows.ToArray();
        }
        public void Dispose() => Connection.Dispose();
    }

    public static async Task SnapshotMillionMemoryAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var fixture = new SnapshotFixture(Fresh(root, "million-snapshot"));
        const int count = 1_000_000;
        using var process = Process.GetCurrentProcess();
        GC.Collect();
        process.Refresh();
        var initialPrivate = process.PrivateMemorySize64;
        long peakPrivate = initialPrivate, peakWorking = process.WorkingSet64, peakManaged = GC.GetTotalMemory(false);
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                peakWorking = Math.Max(peakWorking, process.WorkingSet64);
                peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
                try { await Task.Delay(10, stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
        try
        {
            // 100k directories also exercises the bounded DirTable cache; no million-row input array.
            using var input = SnapshotInput.Create(fixture.Connection, fixture.Dirs,
                Enumerable.Range(0, count).Select(i => F($"d{i / 10:D6}/f{i:D7}", "id:" + i, "aabb")));
            fixture.Write(input);
            StoreSchema.Execute(fixture.Connection, null, "PRAGMA query_only=ON;");
            fixture.Write(input);
            Require((long)StoreSchema.Scalar(fixture.Connection, null, "SELECT COUNT(*) FROM file_state;")! == count,
                "million-item streaming snapshot and unchanged replay preserve all rows");
        }
        finally { stop.Cancel(); await sampler; }
        Console.WriteLine($"snapshot-million: initial-private={initialPrivate}; peak-private={peakPrivate}; peak-working-set={peakWorking}; peak-managed={peakManaged}");
        Require(peakPrivate - initialPrivate < 512_000_000 && peakWorking < 650_000_000 && peakManaged < 256_000_000,
            "million-item snapshot stays within bounded native and managed memory");
    }
}
