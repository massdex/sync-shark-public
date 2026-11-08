using System.Text.Json;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Checks for PairImport (`--engine-checks store`), written by PairImport's writer.</summary>
public static class PairImportChecks
{
    public static void Run()
    {
        SQLitePCL.Batteries_V2.Init();
        var settings = new EngineSettings("", "", "", "", "", RecycleFallback.Fail, 0,
            FirstRunPolicy.LocalWins, 1, "", 262144, ScanTestSettings.BatchRows);
        BaselineChecks(settings);

        WithRoot(root =>
        {
            var pair = Definition("p1", "Pair One", Local(@"C:\Pair\Local"), Dropbox("/Pair/Remote"),
                [@"C:\Pair\Skip", "/Pair/Other Skip"], ["*.tmp", "Secret"], enabled: false);
            WritePairs(root, pair, Definition("p2", "Pair Two", Local(@"C:\Pair\Second"),
                Dropbox("/Pair/Second Remote"), [], [], enabled: true));
            var path = SqliteStateStore.DatabasePath(root);
            var before = File.ReadAllBytes(path);

            var (imported, paused) = PairImport.Read(root, "p1", settings, new InMemoryStateStore());

            Check.Require(imported.PairId == "p1", "PairId");
            Check.Require(imported.Name == "Pair One", "Name");
            Check.Require(imported.LocalRoot == @"C:\Pair\Local", "LocalRoot");
            Check.Require(imported.DropboxRoot == "/Pair/Remote", "DropboxRoot");
            Check.Require(imported.ConnectionId == "account-1", "ConnectionId");
            Check.Require(imported.IgnoreRules.SequenceEqual(["*.tmp", "Secret"]), "IgnoreRules");
            Check.Require(imported.Exclusions.SequenceEqual(
                [new Exclusion(Side.Local, @"C:\Pair\Skip", true), new Exclusion(Side.Local, "/Pair/Other Skip", true)]),
                "Exclusions");
            Check.Require(imported.FirstRun == FirstRunPolicy.LocalWins, "FirstRun");
            Check.Require(!imported.Enabled && !paused, "an unpaused definition keeps its enabled state");
            Check.Require(imported.Drive is null, "Drive");
            Check.Require(before.SequenceEqual(File.ReadAllBytes(path)), "the v2 database is unchanged");

            var (second, secondPaused) = PairImport.Read(root, "p2", settings, new InMemoryStateStore());
            Check.Require(second.PairId == "p2" && second.Name == "Pair Two" && second.Enabled && !secondPaused,
                "second pair");
        });

        WithRoot(root =>
        {
            WritePairs(root, Definition("p1", "Reversed", Dropbox("/Remote"), Local(@"C:\Local"), [], [], true));
            var (imported, paused) = PairImport.Read(root, "p1", settings, new InMemoryStateStore());
            Check.Require(imported.LocalRoot == @"C:\Local" && !paused, "roots chosen by provider, not by letter");
            Check.Require(imported.DropboxRoot == "/Remote", "Dropbox root chosen by provider");
        });

        WithRoot(root =>
        {
            WritePairs(root, Definition("p1", "Two Local", Local(@"C:\One"), Local(@"C:\Two"), [], [], true));
            Check.Require(Refusal(root, settings) == TextKey.ImportUnsupported, "two local roots");
        });
        WithRoot(root =>
        {
            WritePairs(root, Definition("p1", "Two Dropbox", Dropbox("/One"), Dropbox("/Two"), [], [], true));
            Check.Require(Refusal(root, settings) == TextKey.ImportUnsupported, "two Dropbox roots");
        });
        WithRoot(root =>
        {
            WritePairs(root, Definition("p1", "Other Provider",
                new RootDef("r1", ProviderIds.GoogleDrive, "account-1", "/Drive", "obj-1"),
                Local(@"C:\Local"), [], [], true));
            Check.Require(Refusal(root, settings) == TextKey.ImportUnsupported, "a non-Dropbox remote root");
        });

        foreach (var provider in new string?[] { "unknown-provider", null })
        foreach (var first in new[] { true, false })
        foreach (var valid in new[] { Local("X:/Parent"), Dropbox("/Parent") })
        {
            WithRoot(root =>
            {
                var unknown = new RootDef("unknown", provider!, null, "/Other", "object");
                WritePairs(root, Definition("p1", "Unsupported", first ? unknown : valid,
                    first ? valid : unknown, [], [], true));
                Check.Require(Refusal(root, settings) == TextKey.ImportUnsupported,
                    "unknown and null providers are rejected in either slot beside either valid provider");
            });
        }

        WithRoot(root =>
        {
            WritePairs(root, Definition("p2", "Other", Local(@"C:\Local"), Dropbox("/Remote"), [], [], true));
            Check.Require(Refusal(root, settings) == TextKey.ImportMissing, "a missing pair row");
        });
        WithRoot(root =>
        {
            Check.Require(Refusal(root, settings) == TextKey.ImportMissing, "a missing v2 database");
            Check.Require(!File.Exists(SqliteStateStore.DatabasePath(root)), "a missing v2 database is not created");
        });
        WithRoot(root =>
        {
            var path = SqliteStateStore.DatabasePath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "not a database");
            Check.Require(Refusal(root, settings) == TextKey.ImportReadFailed, "an unreadable v2 database");
        });
        WithRoot(root =>
        {
            WritePairs(root, Definition("p1", "No Account", Local(@"C:\Local"),
                new RootDef("root-dropbox", ProviderIds.Dropbox, null, "/Remote", "object-dropbox"), [], [], true));
            Check.Require(Refusal(root, settings) == TextKey.ImportUnsupported, "a Dropbox root without an account");
        });

        WithRoot(root =>
        {
            WritePairs(root, Definition("p1", "Paused Enabled", Local(@"C:\Local"), Dropbox("/Remote"), [], [], true));
            Pause(root, "p1");
            var path = SqliteStateStore.DatabasePath(root);
            var before = File.ReadAllBytes(path);
            var (first, firstPaused) = PairImport.Read(root, "p1", settings, new InMemoryStateStore());
            var (again, againPaused) = PairImport.Read(root, "p1", settings, new InMemoryStateStore());
            Check.Require(!first.Enabled && firstPaused && !again.Enabled && againPaused,
                "a legacy pause disables the pair, reports it, and repeats identically after a fresh read");
            Check.Require(again.PairId == first.PairId && again.Enabled == first.Enabled &&
                again.IgnoreRules.SequenceEqual(first.IgnoreRules),
                "a repeated read returns the same imported definition");
            Check.Require(before.SequenceEqual(File.ReadAllBytes(path)),
                "reading a paused pair leaves the v2 database unchanged");
        });

        WithRoot(root =>
        {
            WritePairs(root, Definition("p1", "Paused Disabled", Local(@"C:\Local"), Dropbox("/Remote"), [], [], false));
            Pause(root, "p1");
            var path = SqliteStateStore.DatabasePath(root);
            var before = File.ReadAllBytes(path);
            var (imported, paused) = PairImport.Read(root, "p1", settings, new InMemoryStateStore());
            Check.Require(!imported.Enabled && paused, "a legacy pause keeps a disabled pair disabled and reports it");
            Check.Require(before.SequenceEqual(File.ReadAllBytes(path)),
                "reading a paused disabled pair leaves the v2 database unchanged");
        });

        WithRoot(root =>
        {
            WritePairs(root,
                Definition("p1", "Unpaused", Local(@"C:\Local"), Dropbox("/Remote"), [], [], true),
                Definition("p2", "Paused Other", Local(@"C:\Other\Local"), Dropbox("/Other/Remote"), [], [], true));
            Pause(root, "p2");
            var path = SqliteStateStore.DatabasePath(root);
            var before = File.ReadAllBytes(path);
            var (imported, paused) = PairImport.Read(root, "p1", settings, new InMemoryStateStore());
            Check.Require(imported.Enabled && !paused, "another pair's legacy pause does not apply");
            Check.Require(before.SequenceEqual(File.ReadAllBytes(path)),
                "reading an unpaused pair beside a paused one leaves the v2 database unchanged");
        });

        WithRoot(root =>
        {
            WritePairs(root, Definition("p1", "No Pause Table", Local(@"C:\Local"), Dropbox("/Remote"), [], [], true));
            DropPause(root);
            var path = SqliteStateStore.DatabasePath(root);
            var before = File.ReadAllBytes(path);
            Check.Require(Refusal(root, settings) == TextKey.ImportReadFailed,
                "a legacy store without the pause relation fails the read");
            Check.Require(before.SequenceEqual(File.ReadAllBytes(path)),
                "a failed read leaves the legacy v2 database unchanged");
        });

        WithRoot(root =>
        {
            WritePairs(root, Definition("p1", "Prefix Rule", Local(@"C:\Local"), Dropbox("/Remote"), [],
                ["*.tmp", "FileNamePrefix:~$"], true));
            var (imported, paused) = PairImport.Read(root, "p1", settings, new InMemoryStateStore());
            Check.Require(imported.IgnoreRules.SequenceEqual(["*.tmp", "FileNamePrefix:~$"]) && !paused,
                "an imported prefix rule survives unchanged and no engine defaults are injected");
        });

        WithRoot(root =>
        {
            WritePairs(root, Definition("p1", "Already", Local(@"C:\Local"), Dropbox("/Remote"), [], [], true));
            using var store = new InMemoryStateStore();
            store.SavePair(new PairConfig("p1", "Already", @"C:\Local", "/Remote", "account-1", [], [],
                FirstRunPolicy.NewestWins, true, null));
            Check.Require(Refusal(root, settings, store) == TextKey.ImportExists, "an existing engine pair");
        });
    }

    private static void BaselineChecks(EngineSettings settings)
    {
        foreach (var reverse in new[] { false, true })
        WithRoot(root =>
        {
            var local = Local(@"C:\Pair\Local");
            var remote = Dropbox("/Pair/Remote");
            WritePairs(root, Definition("p1", "Pair A", reverse ? remote : local, reverse ? local : remote, [], [], true));
            using (var legacy = Legacy(root))
            using (var setup = legacy.CreateCommand())
            {
                setup.CommandText = """
                    CREATE TABLE dir(dir_no INTEGER PRIMARY KEY,path TEXT NOT NULL);
                    CREATE TABLE root(root_no INTEGER PRIMARY KEY,root_id TEXT NOT NULL);
                    CREATE TABLE file_state(root_no INTEGER,object_id TEXT,dir_no INTEGER,name TEXT);
                    CREATE TABLE sync_point(pair_no INTEGER,object_key TEXT,gen INTEGER,kind INTEGER,hash BLOB,size INTEGER,
                      a_dir INTEGER,a_name TEXT,a_object_id TEXT,a_mtime INTEGER,
                      b_dir INTEGER,b_name TEXT,b_object_id TEXT,b_mtime INTEGER);
                    INSERT INTO dir VALUES(1,'Parent'),(2,'Other');
                    INSERT INTO root VALUES(1,'root-local');
                    INSERT INTO file_state VALUES(1,'id:local:00000001:0000000000000001',1,'Current'),
                      (1,'id:local:00000001:0000000000000003',2,'Moved'),
                      (1,'id:local:00000001:0000000000000005',1,'Both'),
                      (1,'id:local:00000001:0000000000000006',1,'Both');
                    INSERT INTO sync_point VALUES
                      (1,'file',1,0,zeroblob(32),17,1,'File','id:local:00000001:ffffffffffffffff',123,2,'Remote','remote-file',456),
                      (1,'current-old',1,1,NULL,0,1,'Current','id:local:00000001:0000000000000001',111,NULL,NULL,'remote-current',222),
                      (1,'current-new',9,1,NULL,0,1,'Current','id:local:00000001:0000000000000002',333,NULL,NULL,'remote-current',444),
                      (1,'absent-old',1,1,NULL,0,1,'Absent','id:local:00000001:0000000000000003',111,NULL,NULL,'remote-absent',222),
                      (1,'absent-new',9,1,NULL,0,1,'Absent','id:local:00000001:0000000000000004',333,NULL,NULL,'remote-absent',444),
                      (1,'both-old',1,1,NULL,0,1,'Both','id:local:00000001:0000000000000005',111,NULL,NULL,'remote-both',222),
                      (1,'both-new',9,1,NULL,0,1,'Both','id:local:00000001:0000000000000006',333,NULL,NULL,'remote-both',444);
                    """;
                setup.ExecuteNonQuery();
                if (reverse)
                {
                    setup.CommandText = "UPDATE sync_point SET a_object_id=b_object_id,b_object_id=a_object_id," +
                        "a_mtime=b_mtime,b_mtime=a_mtime;";
                    setup.ExecuteNonQuery();
                }
            }
            var before = File.ReadAllBytes(SqliteStateStore.DatabasePath(root));
            using var store = StateStore.Open(StateStore.DatabasePath(root));
            SyncedEntry[] expected;
            using (var source = PairImport.Open(root, "p1", settings, store))
            {
                expected = source.Baselines().ToArray();
                Check.Require(source.Count == new BaselineImportCounts(7, 4, 3), "all source and discarded rows are counted");
                Check.Require(expected.Single(row => row.LocalPath == "Parent/Current").LocalId == "1",
                    "unique current identity wins over a higher generation");
                Check.Require(expected.Single(row => row.LocalPath == "Parent/Absent").LocalId == "4" &&
                    expected.Single(row => row.LocalPath == "Parent/Both").LocalId == "6",
                    "zero or multiple observed identities use the highest generation");
            }
            using (var source = PairImport.Open(root, "p1", settings, store))
            {
                var observed = new RootBinding(source.Pair.LocalRoot, source.Pair.DropboxRoot, source.Pair.ConnectionId,
                    new DirectoryIdentity(0x1234567800000001UL, 10, reverse ? null : 0),
                    new DropboxRootIdentity("account", "namespace", "home", "object-dropbox"));
                foreach (var wrong in new[]
                {
                    observed with { Local = observed.Local with { VolumeSerialNumber = 0x1234567800000002UL } },
                    observed with { Local = observed.Local with { FileId = 11 } },
                    observed with { Local = observed.Local with { FileIdHigh = 1 } },
                    observed with { Dropbox = observed.Dropbox with { FolderId = "different" } }
                })
                {
                    Refuses(TextKey.ImportUnsupported, () => source.ImportAsync(store, _ => Task.FromResult(wrong)).GetAwaiter().GetResult());
                    Check.Require(store.Pair("p1") is null, "root mismatch refuses before writing the pair or baseline");
                }
                Refuses(TextKey.ImportReadFailed, () => source.ImportAsync(store,
                    _ => Task.FromException<RootBinding>(new IOException())).GetAwaiter().GetResult());
                source.ImportAsync(store, _ => Task.FromResult(observed)).GetAwaiter().GetResult();
            }
            foreach (var row in expected)
                Check.Require(store.SyncedByKey("p1", Side.Local, row.LocalKey) == row,
                    "every projected field survives storage encoding");
            var file = expected.Single(row => !row.IsFolder);
            Check.Require(file.LocalId == "-1" && file.LocalModifiedTicks == 123 &&
                file.LocalSize == 17 && file.DropboxId == "remote-file" && file.DropboxContentHash == new string('0', 64),
                "the baseline, not current inventory, supplies all unchanged-content facts");
            Check.Require(file.LocalPath == (reverse ? "Other/Remote" : "Parent/File") &&
                file.DropboxPath == (reverse ? "Parent/File" : "Other/Remote"), "provider reversal preserves correspondence");
            Check.Require(!store.FirstRepairDone("p1"), "import leaves first-run deletion history enabled");
            var remoteEntry = new Entry(file.DropboxPath, false, file.LocalSize, DateTimeOffset.UnixEpoch,
                file.DropboxId, "revision", file.DropboxContentHash, null);
            var decisionRow = new Row(file.LocalKey, null, remoteEntry, file, [], null,
                RowContext.Neutral with { FirstRun = true });
            Check.Require(Massdex.SyncShark.Engine.Planner.Decide.For(decisionRow, FirstRunPolicy.LocalWins, DateTimeOffset.UnixEpoch, false).Kind ==
                ActionKind.DeleteDropbox, "an imported baseline respects a local deletion even on first Repair");
            Check.Require(Massdex.SyncShark.Engine.Planner.Decide.For(decisionRow with { Dropbox = remoteEntry with { ContentHash = new string('1', 64) } },
                FirstRunPolicy.LocalWins, DateTimeOffset.UnixEpoch, false).Kind == ActionKind.Download,
                "changed Dropbox content still beats a local deletion");
            Check.Require(Massdex.SyncShark.Engine.Planner.Decide.For(decisionRow with { Context = decisionRow.Context with { OutOfScope = true } },
                FirstRunPolicy.LocalWins, DateTimeOffset.UnixEpoch, false).Kind == ActionKind.None,
                "ordinary scope checks still precede imported deletion history");
            Check.Require(before.SequenceEqual(File.ReadAllBytes(SqliteStateStore.DatabasePath(root))),
                "streaming import leaves the source unchanged");
            Check.Require(expected.Where(row => row.IsFolder).All(row => row.LocalSize == 0 &&
                row.LocalModifiedTicks == 0 && row.DropboxContentHash == string.Empty), "native folder baseline values");
            var identityColumn = reverse ? "b_object_id" : "a_object_id";
            foreach (var (corrupt, restore) in new[]
            {
                ("UPDATE sync_point SET hash=NULL WHERE object_key='file'", "UPDATE sync_point SET hash=zeroblob(32) WHERE object_key='file'"),
                ($"UPDATE sync_point SET {identityColumn}='bad' WHERE object_key='file'",
                    $"UPDATE sync_point SET {identityColumn}='id:local:00000001:ffffffffffffffff' WHERE object_key='file'"),
                ("UPDATE sync_point SET a_name='current' WHERE object_key='current-new'",
                    "UPDATE sync_point SET a_name='Current' WHERE object_key='current-new'")
            })
            {
                using var legacy = Legacy(root);
                using var update = legacy.CreateCommand();
                update.CommandText = corrupt;
                update.ExecuteNonQuery();
                using (var empty = new InMemoryStateStore())
                using (var source = PairImport.Open(root, "p1", settings, empty))
                    Refuses(TextKey.ImportUnsupported, () => source.Baselines().ToArray());
                update.CommandText = restore;
                update.ExecuteNonQuery();
            }
        });
        WithRoot(root =>
        {
            WritePairs(root, Definition("p1", "Pair A", Local(@"C:\Pair\Local"), Dropbox("/Pair/Remote"), [], [], true));
            using var store = StateStore.Open(StateStore.DatabasePath(root));
            var (pair, paused) = PairImport.Read(root, "p1", settings, store);
            var row = new SyncedEntry(PathNames.Key("File"), "File", PathNames.Key("File"), "File", false,
                "1", 1, 1, "remote", new string('0', 64));
            try { store.SaveImportedPair(pair, paused, [row, row]); throw new InvalidOperationException("collision accepted"); }
            catch (SqliteException) { }
            Check.Require(store.Pair("p1") is null && store.SyncedByKey("p1", Side.Local, row.LocalKey) is null,
                "a duplicate rolls back the pair and every baseline");
            IEnumerable<SyncedEntry> Interrupted()
            {
                yield return row;
                throw new OperationCanceledException();
            }
            try { store.SaveImportedPair(pair, paused, Interrupted()); throw new InvalidOperationException("interruption accepted"); }
            catch (OperationCanceledException) { }
            Check.Require(store.Pair("p1") is null && store.SyncedByKey("p1", Side.Local, row.LocalKey) is null,
                "interrupted source enumeration rolls back the pair and every baseline");
        });
    }

    private static PairDef Definition(string pairId, string name, RootDef a, RootDef b, string[] excluded,
        string[] ignoreRules, bool enabled) => new(pairId, name, a, b, excluded, ignoreRules, enabled);

    private static RootDef Local(string path) => new("root-local", ProviderIds.Local, null, path, "id:local:00000001:000000000000000a");

    private static RootDef Dropbox(string path) =>
        new("root-dropbox", ProviderIds.Dropbox, "account-1", path, "object-dropbox");

    private static void Refuses(TextKey expected, Action action)
    {
        try { action(); }
        catch (EngineRequestException error) { Check.Require(error.Key == expected, "import refusal code"); return; }
        throw new InvalidOperationException("Import did not refuse");
    }

    private static TextKey Refusal(string productRoot, EngineSettings settings, IStateStore? engineStore = null)
    {
        try
        {
            PairImport.Read(productRoot, "p1", settings, engineStore ?? new InMemoryStateStore());
        }
        catch (EngineRequestException exception)
        {
            return exception.Key;
        }
        throw new InvalidOperationException("PairImport.Read did not refuse");
    }

    private static void WithRoot(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "syncshark-pairimport-" + Guid.NewGuid().ToString("N"));
        try
        {
            body(root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void WritePairs(string productRoot, params PairDef[] pairs)
    {
        var path = SqliteStateStore.DatabasePath(productRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = Legacy(productRoot);
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE pair(pair_no INTEGER PRIMARY KEY, pair_id TEXT UNIQUE, def_json TEXT NOT NULL);" +
                "CREATE TABLE pair_pause(pair_id TEXT PRIMARY KEY);";
            create.ExecuteNonQuery();
        }
        foreach (var pair in pairs)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO pair(pair_id, def_json) VALUES($id, $def);";
            insert.Parameters.AddWithValue("$id", pair.PairId);
            insert.Parameters.AddWithValue("$def", JsonSerializer.Serialize(pair));
            insert.ExecuteNonQuery();
        }
    }

    private static void Pause(string productRoot, string pairId)
    {
        using var connection = Legacy(productRoot);
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO pair_pause(pair_id) VALUES($id);";
        insert.Parameters.AddWithValue("$id", pairId);
        insert.ExecuteNonQuery();
    }

    private static void DropPause(string productRoot)
    {
        using var connection = Legacy(productRoot);
        using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TABLE pair_pause;";
        drop.ExecuteNonQuery();
    }

    private static SqliteConnection Legacy(string productRoot)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = SqliteStateStore.DatabasePath(productRoot),
            Pooling = false,
        }.ToString());
        StateStore.OpenConnection(connection);
        return connection;
    }
}
