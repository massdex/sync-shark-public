using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// DBP02/DBP03/DBP04 and SS-A04/SS-D11 compact observation cut proofs. Every row is written through the real
// PrepareCompactScanAsync and CompleteScanGenerationAsync path on a disposable isolated database, and every
// read goes through the real effective_observation view or a read-only SQLite connection to the same file.
// No provider, credential, signing, install, updater or original product state is touched, and no synthetic
// shortcut replaces a decision under test.
internal static class CompactObservationCutChecks
{
    private const string Installation = "compact-cut-installation";
    private const string Profile = "compact-cut-profile";
    private const string PairId = "compact-cut-pair";
    private const string RootId = "compact-cut-root";
    private const string SerialHex = "c0ffee01";
    private const string RootFileIdHex = "00000000000000a1";
    private static int _assertions;

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var cases = new (string Name, Func<string, Task> Run)[]
        {
            ("cut-projection", RunCutProjectionAsync),
            ("delta-growth", RunDeltaGrowthAsync),
            ("sealed-candidate", RunSealedCandidateAsync),
            ("schema-admission", RunSchemaAdmissionAsync),
            ("v8-to-current-migration", RunMigrationAsync)
        };
        var failures = new List<string>();
        foreach (var item in cases)
        {
            try { await item.Run(Path.Combine(root, item.Name)); }
            catch (Exception error)
            {
                failures.Add(item.Name + ": " + error.GetType().Name + ": " + error.Message);
                Console.WriteLine("FAIL " + item.Name + ": " + error.GetType().Name + ": " + error.Message);
            }
        }
        if (failures.Count > 0)
            throw new InvalidDataException(failures.Count + " of " + cases.Length + " compact observation cut cases failed.");
        Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", check = "compact-observation-cut-checks", assertions = _assertions }));
    }

    // DBP02/SS-A04: a legacy cut and two compact cuts resolve the same complete inventory through the same
    // view. The legacy scan keeps its physical rows, a compact scan writes none, an identical repeat grows no
    // payload row, and the projected identifier is the full composite bound to its exact cut.
    private static async Task RunCutProjectionAsync(string root)
    {
        var (database, path) = await OpenAsync(root, "cut-projection");
        await CreateLocalRootAsync(database, root);
        var inventory = Inventory(40);

        var legacy = await database.BeginScanGenerationAsync(PairId, RootId, "local", "initial", "{}");
        await database.AppendObservationEntriesAsync(legacy, inventory, CancellationToken.None);
        await database.CompleteScanGenerationAsync(legacy);
        Require(Scalar(path, "SELECT count(*) FROM observation WHERE scan_generation_id='" + legacy.ScanGenerationId + "';") == 40,
            "the legacy scan keeps its physical observation rows");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation WHERE scan_generation_id='" + legacy.ScanGenerationId + "';") == 40,
            "the view resolves the legacy scan to exactly its physical rows");
        Require(Scalar(path, "SELECT count(*) FROM observation_cut;") == 0, "a legacy scan creates no compact cut");

        var first = await database.BeginScanGenerationAsync(PairId, RootId, "local", "monitor", "{}");
        await database.PrepareCompactScanAsync(first, inventory, NoEvidence, null, null);
        Require(Scalar(path, "SELECT count(*) FROM observation_cut WHERE scan_generation_id='" + first.ScanGenerationId + "' AND state='sealed';") == 1,
            "preparation seals exactly one candidate and publishes nothing");
        await database.CompleteScanGenerationAsync(first);
        Require(Scalar(path, "SELECT count(*) FROM observation WHERE scan_generation_id='" + first.ScanGenerationId + "';") == 0,
            "a compact scan writes no legacy observation row");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation WHERE scan_generation_id='" + first.ScanGenerationId + "';") == 40,
            "the published compact cut resolves to its sealed complete input count");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation WHERE scan_generation_id='" + first.ScanGenerationId + "' AND complete_generation=1;") == 40,
            "a published compact cut is complete");
        Require(Scalar(path, "SELECT count(*) FROM observation_interval;") == 40,
            "the first compact cut materialises one interval per supplied row");
        Require(Scalar(path, "SELECT count(*) FROM observation_delta;") == 0,
            "publication removes only its own delta rows");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation WHERE scan_generation_id='" + first.ScanGenerationId + "' AND length(observation_id)=141;") == 40,
            "every projected compact identifier is the full one hundred and forty one character composite");
        Require(Scalar(path, "SELECT count(DISTINCT observation_id) FROM effective_observation WHERE scan_generation_id='" + first.ScanGenerationId + "';") == 40,
            "distinct semantic rows keep distinct projected identifiers");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation a JOIN effective_observation b ON b.membership_id=a.membership_id WHERE a.scan_generation_id='"
                + first.ScanGenerationId + "' AND b.scan_generation_id='" + legacy.ScanGenerationId + "';") == 40,
            "the compact cut reuses the legacy membership of every unchanged row");

        var before = Counts(path);
        var second = await database.BeginScanGenerationAsync(PairId, RootId, "local", "monitor", "{}");
        await database.PrepareCompactScanAsync(second, inventory, NoEvidence, null, null);
        await database.CompleteScanGenerationAsync(second);
        var after = Counts(path);
        Require(before.Membership == after.Membership && before.Version == after.Version && before.Interval == after.Interval,
            "an identical complete inventory grows no membership, version or interval row");
        Require(after.Observation == before.Observation, "a no-change compact repeat adds no legacy observation row");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation WHERE scan_generation_id='" + second.ScanGenerationId + "';") == 40,
            "an unchanged compact repeat still resolves the complete inventory");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation a JOIN effective_observation b ON b.observation_id=a.observation_id WHERE a.scan_generation_id='"
                + first.ScanGenerationId + "' AND b.scan_generation_id='" + second.ScanGenerationId + "';") == 0,
            "two cuts bind the same semantic row to different exact cut identities");
    }

    // DBP03: a sparse change set grows changed rows only, and the changed cut resolves exactly while every
    // earlier cut keeps resolving its own original content.
    private static async Task RunDeltaGrowthAsync(string root)
    {
        var (database, path) = await OpenAsync(root, "delta-growth");
        await CreateLocalRootAsync(database, root);
        var inventory = Inventory(100);
        var baseCut = await CompactCutAsync(database, inventory);
        var before = Counts(path);

        var changed = inventory
            .Where(entry => entry.RelativePath != "folder/file-0000.txt")
            .Select(entry => entry.RelativePath switch
            {
                "folder/file-0001.txt" => Entry("folder/renamed-0001.txt", FileId(1), "content-1"),
                "folder/file-0002.txt" => Entry("folder/FILE-0002.TXT", FileId(2), "content-2"),
                var path10 when Index(path10) is >= 3 and <= 12 =>
                    Entry(path10, FileId(Index(path10)), "content-" + Index(path10) + "-changed"),
                _ => entry
            })
            .ToArray();
        var changedCut = await CompactCutAsync(database, changed);
        var after = Counts(path);

        Require(after.Observation == before.Observation, "a compact change set adds no legacy observation row");
        Require(after.Membership == before.Membership + 2, "a rename and a case change each create exactly one new membership");
        Require(after.Version == before.Version + 10, "ten content changes create exactly ten new immutable versions");
        Require(after.Interval == before.Interval + 12, "twelve changed or new keys create exactly twelve new intervals");
        Require(after.Delta == 0, "publication leaves no pending delta row");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation WHERE scan_generation_id='" + changedCut.ScanGenerationId + "';") == 99,
            "the changed cut resolves exactly its complete supplied inventory");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation WHERE scan_generation_id='" + changedCut.ScanGenerationId
                + "' AND json_extract(evidence_json,'$.path')='folder/file-0000.txt';") == 0,
            "a removed path is absent from the new cut");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation WHERE scan_generation_id='" + baseCut.ScanGenerationId
                + "' AND json_extract(evidence_json,'$.path')='folder/file-0000.txt';") == 1,
            "the same removed path is still resolved by the earlier cut");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation WHERE scan_generation_id='" + changedCut.ScanGenerationId
                + "' AND json_extract(evidence_json,'$.path')='folder/renamed-0001.txt';") == 1,
            "the renamed path is present in the new cut");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation WHERE scan_generation_id='" + changedCut.ScanGenerationId
                + "' AND json_extract(evidence_json,'$.path')='folder/file-0001.txt';") == 0,
            "the old rename spelling is absent from the new cut");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation a JOIN object_version v ON v.object_version_id=a.object_version_id WHERE a.scan_generation_id='"
                + baseCut.ScanGenerationId + "' AND json_extract(a.evidence_json,'$.path')='folder/file-0003.txt' AND v.content_hash='"
                + Hash("content-3") + "';") == 1,
            "the earlier cut still resolves the original content of a changed path");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation a JOIN object_version v ON v.object_version_id=a.object_version_id WHERE a.scan_generation_id='"
                + changedCut.ScanGenerationId + "' AND json_extract(a.evidence_json,'$.path')='folder/file-0003.txt' AND v.content_hash='"
                + Hash("content-3-changed") + "';") == 1,
            "the new cut resolves the new version of the same membership");
        Require(Scalar(path, "SELECT count(*) FROM observation_interval WHERE root_binding_id='" + RootId
                + "' AND to_generation IS NULL;") == 99, "exactly the changed cut's own keys stay open");
    }

    // DBP03/DBP04: a sealed candidate resolves its captured parent generation plus its own upserts and
    // removals. A concurrent later publication does not change that captured resolution, and the stale
    // candidate refuses to publish instead of rebasing onto the newer parent.
    private static async Task RunSealedCandidateAsync(string root)
    {
        var (database, path) = await OpenAsync(root, "sealed-candidate");
        await CreateLocalRootAsync(database, root);
        var inventory = Inventory(12);
        var published = await CompactCutAsync(database, inventory);

        var kept = inventory.Where(entry => entry.RelativePath != "folder/file-0000.txt").ToArray();
        var candidate = await database.BeginScanGenerationAsync(PairId, RootId, "local", "monitor", "{}");
        await database.PrepareCompactScanAsync(candidate, kept, NoEvidence, null, null);
        Require(Scalar(path, "SELECT count(*) FROM scan_generation WHERE scan_generation_id='" + candidate.ScanGenerationId
                + "' AND status='complete';") == 0, "a sealed candidate is not a complete scan generation");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation WHERE scan_generation_id='" + candidate.ScanGenerationId
                + "' AND complete_generation=0;") == 11,
            "a sealed candidate resolves its captured parent generation plus its own upserts and removals");
        var sealedIds = Query(path, "SELECT observation_id FROM effective_observation WHERE scan_generation_id='" + candidate.ScanGenerationId + "';");
        Require(sealedIds.All(row => row[0].Length == 141), "a sealed candidate also projects full composite identifiers");

        var sealedBefore = JsonSerializer.Serialize(Query(path,
            "SELECT cut_id,state,sealed_input_count,sealed_input_digest FROM observation_cut WHERE scan_generation_id='" + candidate.ScanGenerationId + "';"));
        var countsBefore = Counts(path);
        var resumed = new SyncAuthorityDatabase(path);
        await resumed.InitializeAsync(Installation, Profile, "compact-cut-fixture", "{}");
        await resumed.PrepareCompactScanAsync(candidate, kept.Reverse().ToArray(), NoEvidence, null, null);
        Require(Counts(path) == countsBefore, "restart with the same sealed inventory reuses all retained rows");
        var changedInventory = kept.Select((entry, index) => index == 0
            ? Entry(entry.RelativePath, FileId(1), "changed-after-sealing") : entry).ToArray();
        var changedRejected = false;
        await RejectAsync(async () => await resumed.PrepareCompactScanAsync(candidate, changedInventory, NoEvidence, null, null),
            () => changedRejected = true);
        Require(changedRejected, "same-count changed inventory cannot reuse an earlier sealed candidate");
        Require(Counts(path) == countsBefore && sealedBefore == JsonSerializer.Serialize(Query(path,
            "SELECT cut_id,state,sealed_input_count,sealed_input_digest FROM observation_cut WHERE scan_generation_id='" + candidate.ScanGenerationId + "';")),
            "changed-input rejection preserves the sealed digest and all retained compact rows");

        var later = await CompactCutAsync(database, kept);
        Require(Scalar(path, "SELECT count(*) FROM effective_observation WHERE scan_generation_id='" + published.ScanGenerationId + "';") == 12,
            "a later publication does not change an earlier cut's historical resolution");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation WHERE scan_generation_id='" + candidate.ScanGenerationId
                + "' AND complete_generation=0;") == 11,
            "a later publication does not change the sealed candidate's captured parent resolution");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation WHERE scan_generation_id='" + later.ScanGenerationId + "';") == 11,
            "the later compact cut resolves its own complete supplied inventory");

        var refusals = 0;
        await RejectAsync(async () => await database.CompleteScanGenerationAsync(candidate), () => refusals++);
        Require(refusals == 1, "a stale captured parent refuses publication instead of rebasing silently");
        Require(Scalar(path, "SELECT count(*) FROM effective_observation WHERE scan_generation_id='" + candidate.ScanGenerationId
                + "' AND complete_generation=1;") == 0, "the refused stale publication published nothing");
        Require(Scalar(path, "SELECT count(*) FROM scan_generation WHERE pair_id='" + PairId + "' AND side='local' AND status='complete';") == 2,
            "only the two genuinely published cuts are complete");
        Require(Scalar(path, "SELECT count(*) FROM observation_cut WHERE state='sealed';") == 1,
            "the refused candidate stays a sealed and unpublished cut");
    }

    // SS-D11: the exact inventory admission still rejects a missing view and an extra object, so the new
    // expected-inventory support does not weaken the existing exact comparison.
    private static async Task RunSchemaAdmissionAsync(string root)
    {
        var (_, missingViewPath) = await OpenAsync(root, "missing-view");
        Execute(missingViewPath, "DROP VIEW effective_observation;");
        var missingRejected = false;
        await RejectAsync(async () =>
        {
            var reopened = new SyncAuthorityDatabase(missingViewPath);
            await reopened.InitializeAsync(Installation, Profile, "compact-cut-fixture", "{}");
        }, () => missingRejected = true);
        Require(missingRejected, "a missing effective_observation view is rejected by exact schema admission");

        var (_, extraObjectPath) = await OpenAsync(root, "extra-object");
        Execute(extraObjectPath, "CREATE INDEX IF NOT EXISTS zz_extra_compact_probe ON observation(observation_id);");
        var extraRejected = false;
        await RejectAsync(async () =>
        {
            var reopened = new SyncAuthorityDatabase(extraObjectPath);
            await reopened.InitializeAsync(Installation, Profile, "compact-cut-fixture", "{}");
        }, () => extraRejected = true);
        Require(extraRejected, "an extra schema object is rejected by exact schema admission");
    }

    // SS-D11: version 8 to version 9 stays additive. The fixture is built from the same compiled DDL a version
    // 8 database was created from, then migrated on disposable isolated data only.
    private static async Task RunMigrationAsync(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "v8-fixture.db");
        var fixture = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        await fixture.OpenAsync();
        await ExecuteAsync(fixture, SyncAuthoritySchema.Sql + "\n" + SyncAuthoritySchemaSupplement.Sql);
        foreach (var migration in new[]
                 {
                     SyncAuthoritySchemaMigration3.Sql, SyncAuthoritySchemaMigration4.Sql,
                     SyncAuthoritySchemaMigration5.Sql, SyncAuthoritySchemaMigration6.Sql,
                     SyncAuthoritySchemaMigration7.Sql, SyncAuthoritySchemaMigration8.Sql
                 })
            await ExecuteAsync(fixture, migration);
        await ExecuteAsync(fixture, "INSERT INTO logical_object(object_id,object_kind,created_utc) VALUES('legacy-retained-object','file','2026-01-01T00:00:00.0000000Z');");
        await ExecuteAsync(fixture, "INSERT INTO schema_migration(schema_version,applied_utc,source_revision) VALUES(8,'2026-01-01T00:00:00.0000000Z','fixture-v8');");
        await ExecuteAsync(fixture, "INSERT INTO installation(installation_id,database_generation,created_utc) "
            + "VALUES('" + Installation + "','compact-cut-v8-generation','2026-01-01T00:00:00.0000000Z');");
        await ExecuteAsync(fixture, "PRAGMA user_version=8;");
        await fixture.DisposeAsync();

        var database = new SyncAuthorityDatabase(path);
        await database.InitializeAsync(Installation, Profile, "compact-cut-fixture", "{}");
        Require(Scalar(path, "PRAGMA user_version;") == SyncAuthoritySchema.Version, "the version 8 fixture migrates to the current schema version");
        Require(Scalar(path, "SELECT count(*) FROM sqlite_schema WHERE type='view' AND name='effective_observation';") == 1,
            "the migrated database carries the effective_observation view");
        Require(Scalar(path, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN ('observation_cut','observation_interval','observation_delta');") == 3,
            "the migrated database carries the three compact tables");
        Require(Scalar(path, "SELECT count(*) FROM logical_object WHERE object_id='legacy-retained-object';") == 1,
            "the migration retains a pre-existing legacy row byte for byte");
        Require(Scalar(path, "SELECT count(*) FROM schema_migration WHERE schema_version IN (8," + SyncAuthoritySchema.Version + ");") == 2,
            "the current-version receipt is added beside the retained version 8 receipt");
        Require(Scalar(path, "SELECT count(*) FROM main.sqlite_schema WHERE type='trigger' AND name LIKE 'observation_interval%';") == 2,
            "the append-only interval triggers exist after migration");
        Require(File.Exists(path + ".v8-rollback"), "the version 8 rollback copy is retained before migration");
    }

    private static readonly (string ObservationId, string EvidenceJson)[] NoEvidence =
        Array.Empty<(string ObservationId, string EvidenceJson)>();

    private static async Task<(SyncAuthorityDatabase Database, string Path)> OpenAsync(string root, string name)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name + ".db");
        var database = new SyncAuthorityDatabase(path);
        await database.InitializeAsync(Installation, Profile, "compact-cut-fixture", "{}");
        await CreateLocalRootAsync(database, root);
        return (database, path);
    }

    private static async Task CreateLocalRootAsync(SyncAuthorityDatabase database, string root)
    {
        var localRoot = Path.Combine(root, "local-root");
        Directory.CreateDirectory(localRoot);
        var volumeId = await database.ResolveVolumeIdAsync(Installation, SerialHex, "NTFS", null,
            supportsFileId128: false, supportsUsn: false, supportsAtomicRename: true);
        await database.RegisterPairAsync(PairId, 1);
        await database.RegisterLocalRootBindingAsync(RootId, PairId, 1, Installation, volumeId, SerialHex,
            RootFileIdHex, localRoot);
    }

    private static async Task<SyncAuthorityScanGeneration> CompactCutAsync(SyncAuthorityDatabase database,
        IReadOnlyList<SyncAuthorityObservationInput> inventory)
    {
        var scan = await database.BeginScanGenerationAsync(PairId, RootId, "local", "monitor", "{}");
        await database.PrepareCompactScanAsync(scan, inventory, NoEvidence, null, null);
        await database.CompleteScanGenerationAsync(scan);
        return scan;
    }

    private static SyncAuthorityObservationInput[] Inventory(int count) =>
        Enumerable.Range(0, count).Select(index =>
            Entry("folder/file-" + index.ToString("D4", CultureInfo.InvariantCulture) + ".txt", FileId(index), "content-" + index))
            .ToArray();

    private static string FileId(int index) => "000000000000" + index.ToString("D4", CultureInfo.InvariantCulture);

    private static int Index(string path) => int.Parse(path.AsSpan(path.Length - 8, 4), CultureInfo.InvariantCulture);

    private static SyncAuthorityObservationInput Entry(string path, string fileId, string content) =>
        new(path, "file", SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File,
                uint.Parse(SerialHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                ulong.Parse(fileId, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                contentHash: Hash(content), size: content.Length),
            Hash(content), content.Length, JsonSerializer.Serialize(new { path, state = "present" }));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static (long Observation, long Membership, long Version, long Interval, long Delta) Counts(string path) =>
        (Scalar(path, "SELECT count(*) FROM observation;"), Scalar(path, "SELECT count(*) FROM membership;"),
            Scalar(path, "SELECT count(*) FROM object_version;"),
            Scalar(path, "SELECT count(*) FROM observation_interval;"),
            Scalar(path, "SELECT count(*) FROM observation_delta;"));

    private static long Scalar(string path, string sql)
    {
        var rows = Query(path, sql);
        if (rows.Count == 0 || rows[0].Length == 0) throw new InvalidDataException("The probe statement returned no value.");
        return long.Parse(rows[0][0], CultureInfo.InvariantCulture);
    }

    private static List<string[]> Query(string path, string sql)
    {
        var rows = new List<string[]>();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
                row[index] = reader.IsDBNull(index)
                    ? "<null>"
                    : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "<null>";
            rows.Add(row);
        }
        return rows;
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RejectAsync(Func<Task> action, Action onRejected)
    {
        try { await action(); }
        catch (InvalidDataException) { onRejected(); return; }
        catch (InvalidOperationException) { onRejected(); return; }
        throw new InvalidDataException("The operation was expected to fail closed and did not.");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidDataException("Compact observation cut check failed: " + label);
        _assertions++;
        Console.WriteLine("PASS " + label);
    }
}
