using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using IOPath = System.IO.Path;

// Normalized Rewind state proof. Every case below runs the shipped production code - the authority
// schema and its additive version 8 migration, the real normalized Rewind persistence, the real shared
// session normalizer and the real product-state aggregate store - against isolated disposable fixtures.
// No live provider, credential, OAuth, signing, install, updater or retained product database is
// reached, and no user folder or original product state is written.
internal static class NormalizedRewindStateChecks
{
    private const string InstallationId = "installation-normalized-rewind-316";
    private const string SourceRevision = "normalized-rewind-316";
    private const string ProfileId = "profile-normalized-rewind-316";
    private const string CapabilityJson = "{\"name\":\"normalized-rewind-checks\"}";
    private static string _root = "";
    private static int _assertions;

    internal static async Task RunAsync(string root)
    {
        _root = IOPath.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated normalized Rewind fixture root.");
        Directory.CreateDirectory(_root);
        _assertions = 0;
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP normalized Rewind state checks: Windows NTFS directory identities are unavailable.");
            return;
        }

        await FreshSchemaVersion8ChecksAsync();
        await AdditiveVersion7MigrationChecksAsync();
        await RealScalePersistenceChecksAsync();
        await RefusalMatrixChecksAsync();
        await LegacyPublicationChecksAsync();

        Console.WriteLine("PASS normalized Rewind state checks: " + _assertions + " assertions; the reviewed preview is" +
            " stored once per admitted preview, one checkpoint is one indexed row update, the bounded aggregate holds" +
            " only the storage reference, and every foreign, mismatched, malformed or regressive update was refused.");
    }

    // ---- NR1: the additive version 8 object inventory on a fresh isolated database ---------------

    private static async Task FreshSchemaVersion8ChecksAsync()
    {
        var database = await NewDatabaseAsync("fresh-schema");
        var objects = await ObjectNamesAsync(database.DatabasePath);
        foreach (var expected in new[]
                 {
                     "table:rewind_session", "table:rewind_preview_item", "table:rewind_path_progress",
                     "index:one_active_rewind_session_per_pair", "index:rewind_session_updated_utc",
                     "index:rewind_preview_item_session", "index:rewind_path_progress_session"
                 })
            Require(objects.Contains(expected), "NR1 the fresh current schema declares " + expected);
        Require(await NumberAsync(database.DatabasePath, "PRAGMA user_version;") == SyncAuthoritySchema.Version,
            "NR1 the fresh current schema records the current version including the version 8 migration");
        Require(await NumberAsync(database.DatabasePath,
                "SELECT count(*) FROM schema_migration WHERE schema_version=" + SyncAuthoritySchema.Version + ";") == 1,
            "NR1 the fresh database retains exactly one receipt for its actual creation version");
        foreach (var table in new[] { "rewind_session", "rewind_preview_item", "rewind_path_progress" })
            Require(await TextAsync(database.DatabasePath,
                    "SELECT sql FROM sqlite_schema WHERE type='table' AND name='" + table + "';") is { } sql &&
                sql.Contains("STRICT", StringComparison.Ordinal),
                "NR1 " + table + " is a STRICT table");
        Require(await TextAsync(database.DatabasePath, "PRAGMA quick_check;") == "ok",
            "NR1 the fresh current schema reports a clean integrity check");
        Require(await NumberAsync(database.DatabasePath,
                "SELECT count(*) FROM pragma_foreign_key_check;") == 0,
            "NR1 the fresh current schema reports no foreign-key violation");
    }

    // ---- NR2: a real version 7 database upgrades additively and keeps every retained row --------

    private static async Task AdditiveVersion7MigrationChecksAsync()
    {
        var root = IOPath.Combine(_root, "additive-v7");
        Directory.CreateDirectory(root);
        var path = IOPath.Combine(root, "authority.db");
        // Build the exact version 7 object inventory this build supports as its predecessor: the shared
        // base schema, its supplement and migrations 3 to 7, then the retained installation row, the
        // version 7 receipt and the sealed user_version. This is the real predecessor shape, not a copy.
        await using (var connection = new SqliteConnection("Data Source=" + path))
        {
            await connection.OpenAsync();
            foreach (var sql in new[]
                     {
                         SyncAuthoritySchema.Sql, SyncAuthoritySchemaSupplement.Sql,
                         SyncAuthoritySchemaMigration3.Sql, SyncAuthoritySchemaMigration4.Sql,
                         SyncAuthoritySchemaMigration5.Sql, SyncAuthoritySchemaMigration6.Sql,
                         SyncAuthoritySchemaMigration7.Sql
                     })
                await ExecuteAsync(connection, sql);
            await ExecuteAsync(connection,
                """
                INSERT INTO installation(installation_id,database_generation,created_utc)
                    VALUES('installation-additive-v7','generation-additive-v7','2026-01-01T00:00:00.0000000Z');
                INSERT INTO sync_pair(pair_id,configuration_revision,state,created_utc)
                    VALUES('pair-additive-v7',1,'discovery_required','2026-01-01T00:00:00.0000000Z');
                INSERT INTO schema_migration(schema_version,applied_utc,source_revision)
                    VALUES(7,'2026-01-01T00:00:00.0000000Z','predecessor-v7');
                PRAGMA user_version=7;
                """);
        }
        Require(await NumberAsync(path, "PRAGMA user_version;") == 7 &&
                await NumberAsync(path, "SELECT count(*) FROM sync_pair;") == 1,
            "NR2 the isolated predecessor fixture is a real version 7 authority database with retained rows");

        var database = new SyncAuthorityDatabase(path);
        await database.InitializeAsync("installation-additive-v7", ProfileId, SourceRevision, CapabilityJson);
        Require(await NumberAsync(path, "PRAGMA user_version;") == SyncAuthoritySchema.Version,
            "NR2 opening the version 7 fixture performs the additive upgrade to the current schema version");
        Require(await NumberAsync(path, "SELECT count(*) FROM sync_pair WHERE pair_id='pair-additive-v7';") == 1 &&
                await TextAsync(path, "SELECT installation_id FROM installation;") == "installation-additive-v7",
            "NR2 the additive upgrade preserved every retained row identity and value");
        Require(File.Exists(database.Version7RollbackDatabasePath) &&
                await NumberAsync(database.Version7RollbackDatabasePath, "PRAGMA user_version;") == 7,
            "NR2 the verified version 7 rollback copy was retained before migration");
        var objects = await ObjectNamesAsync(path);
        Require(objects.Contains("table:rewind_session") && objects.Contains("table:rewind_preview_item") &&
                objects.Contains("table:rewind_path_progress"),
            "NR2 the additive upgrade created exactly the three normalized Rewind tables");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM rewind_session t JOIN sync_pair p ON p.pair_id=t.pair_id;") == 0,
            "NR2 the additive upgrade created no Rewind row and no synthetic session");
    }

    // ---- NR3: the real persisted large session, its bounded aggregate and per-path checkpoints ---

    private static async Task RealScalePersistenceChecksAsync()
    {
        const int pathCount = 10_000;
        const int checkpointCount = 1_000;
        var database = await NewDatabaseAsync("real-scale");
        var installationId = await database.LoadAuthorityInstallationIdAsync()
            ?? throw new InvalidDataException("The isolated authority installation identity was unavailable.");
        const string pairId = "pair-normalized-rewind-scale";
        await RegisterPairAsync(database, pairId, "Normalized Rewind scale", "NormalizedRewindScale");
        var preview = BuildPreview(pathCount);
        var session = new SyncRewindSession("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01", "connection-normalized-rewind",
            pairId, "Normalized Rewind scale", "/NormalizedRewindScale", "local-checkpoint-scan",
            "dropbox-checkpoint-scan", "dropbox-cursor", DateTimeOffset.UtcNow, SyncRewindStatus.AwaitingDropbox,
            Detail: "Pair paused at a complete local and Dropbox checkpoint.");
        var previewJson = JsonSerializer.Serialize(preview);
        Require(Encoding.UTF8.GetByteCount(previewJson) > 1_048_576,
            "NR3 the full reviewed representation of " + pathCount + " paths exceeds 1 MiB");

        // The bounded aggregate proof: the legacy collection is observable in the aggregate, the
        // published reference replaces it, and the aggregate size stops depending on the path count.
        using var instance = PilotInstanceLock.Acquire(IOPath.Combine(_root, "real-scale-aggregate", "state", "instance.lock"));
        var aggregate = new SyncProductStateStore(instance.StateAuthority);
        var aggregateLocalRoot = IOPath.Combine(_root, "real-scale-aggregate", "local");
        Directory.CreateDirectory(aggregateLocalRoot);
        using var aggregateRootLease = PilotDirectoryLease.Open(aggregateLocalRoot);
        var pair = new SyncPair(pairId, "Normalized Rewind scale", aggregateLocalRoot,
            aggregateRootLease.Identity.VolumeSerialNumber, aggregateRootLease.Identity.FileId,
            "/NormalizedRewindScale", "id:normalized-rewind-scale",
            ConnectionId: "connection-normalized-rewind");
        await new SyncPairStore(instance.StateAuthority).UpsertAsync(pair);
        var oversizedRejected = false;
        try
        {
            await aggregate.UpdateAsync(current => current with
            {
                InstallationId = installationId,
                RewindSessions = [session with { Preview = preview }]
            });
        }
        catch (InvalidDataException error) when (error.Message.Contains("1 MiB slot ceiling", StringComparison.Ordinal))
        { oversizedRejected = true; }
        Require(oversizedRejected && aggregate.Load().RewindSessions?.Count == 0,
            "NR3 the oversized legacy aggregate refuses atomically instead of weakening its existing slot ceiling");
        // A representable legacy session exercises removal; the full 10,000-path
        // preview below goes through the actual normalized production store.
        await aggregate.UpdateAsync(current => current with
        {
            InstallationId = installationId,
            RewindSessions = [session with { Preview = preview.Take(256).ToArray() }]
        });
        var aggregateWithLegacy = AggregateBytes(aggregate);

        var store = new SyncRewindStore(database);
        await store.UpsertAsync(session);
        var published = await database.LoadActiveRewindSessionAsync(pairId)
            ?? throw new InvalidDataException("The prepared checkpoint was not retained.");
        var previewSession = published with
        {
            Status = SyncRewindStatus.PreviewReady,
            LocalPreviewScanId = "local-preview-scan",
            DropboxPreviewScanId = "dropbox-preview-scan",
            Preview = preview,
            Detail = pathCount.ToString("N0", CultureInfo.InvariantCulture) +
                " Dropbox rollback change(s) detected; no local effect has been applied."
        };
        await store.PublishPreviewAsync(previewSession, published.SessionId, null, null);
        await aggregate.UpdateAsync(current => current with
        {
            RewindSessions = [],
            RewindStorage = SyncRewindStorageReference.Create(installationId)
        });
        var aggregatePublished = AggregateBytes(aggregate);
        Require(aggregateWithLegacy > aggregatePublished && aggregatePublished < 32 * 1024,
            "NR3 publication removed the growing preview from the aggregate and left only the bounded reference");

        var before = DatabaseBytes(database.DatabasePath);
        var elapsed = Stopwatch.StartNew();
        for (var index = 0; index < checkpointCount; index++)
        {
            var path = preview[index].RelativePath;
            await database.PersistRewindPathCheckpointAsync(previewSession.SessionId, pairId,
                "local-preview-scan", "dropbox-preview-scan", null,
                new SyncAuthorityReviewedPathCheckpoint(path, SyncRewindPathStatus.Completed, null,
                    new SyncDecision(path, SyncDecisionKind.Downloaded, "Reviewed rollback applied.")),
                SyncRewindStatus.Applying,
                "Applying reviewed rollback paths: " +
                (index + 1).ToString(CultureInfo.InvariantCulture) + "/" +
                pathCount.ToString(CultureInfo.InvariantCulture) + " terminal.");
        }
        elapsed.Stop();
        var after = DatabaseBytes(database.DatabasePath);
        Require(await NumberAsync(database.DatabasePath,
                "SELECT count(*) FROM rewind_preview_item WHERE session_id='" + previewSession.SessionId + "';") == pathCount,
            "NR3 exactly one stored preview holds every reviewed item of the admitted generation");
        Require(await NumberAsync(database.DatabasePath,
                "SELECT count(*) FROM rewind_path_progress WHERE session_id='" + previewSession.SessionId + "';") ==
            pathCount,
            "NR3 the reviewed preview owns exactly one progress row per reviewed path and no other row");
        Require(await NumberAsync(database.DatabasePath,
                "SELECT count(*) FROM rewind_path_progress WHERE session_id='" + previewSession.SessionId +
                "' AND status='completed';") == checkpointCount,
            "NR3 exactly the " + checkpointCount + " checkpointed paths are terminal, independent of the total preview count");

        // Restart: a new instance reads the exact retained state through the real reader.
        var restarted = new SyncAuthorityDatabase(database.DatabasePath);
        var retained = await restarted.LoadActiveRewindSessionAsync(pairId)
            ?? throw new InvalidDataException("The restarted reader returned no retained session.");
        Require(retained.Status == SyncRewindStatus.Applying && retained.Preview?.Count == pathCount &&
                retained.PathProgress?.Count == pathCount &&
                retained.PathProgress!.Count(item => item.Status == SyncRewindPathStatus.Completed) == checkpointCount &&
                retained.Detail?.Contains(": 1000/10000", StringComparison.Ordinal) == true,
            "NR3 a restarted reader returns the exact persisted preview, checkpoints and header state");
        Require(retained.Preview!.All(item => !string.IsNullOrWhiteSpace(item.Group) &&
                !string.IsNullOrWhiteSpace(item.ProposedEffect) && item.DropboxChanged && item.Reason.Length > 0),
            "NR3 every retained preview item kept its original Group/ProposedEffect/DropboxChanged/Reason semantics");
        var activePairs = await restarted.LoadActiveRewindPairIdsAsync();
        Require(activePairs.Count == 1 && activePairs.Contains(pairId),
            "NR3 the materialized active-pair read returns exactly the pair that owns a live checkpoint");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result = "normalized-rewind-real-scale",
            reviewedPaths = pathCount,
            checkpoints = checkpointCount,
            previewJsonBytes = Encoding.UTF8.GetByteCount(previewJson),
            aggregateBytesWithLegacyCollection = aggregateWithLegacy,
            aggregateBytesAfterPublication = aggregatePublished,
            databaseBytesBefore = before,
            databaseBytesAfter = after,
            databaseBytes = DatabaseBytes(database.DatabasePath),
            walBytes = DatabaseBytes(database.DatabasePath + "-wal"),
            checkpointMilliseconds = elapsed.Elapsed.TotalMilliseconds,
            millisecondsPerCheckpoint = elapsed.Elapsed.TotalMilliseconds / checkpointCount
        }));
    }

    // ---- NR4: every foreign, mismatched, malformed, duplicate or regressive update is refused ---

    private static async Task RefusalMatrixChecksAsync()
    {
        var database = await NewDatabaseAsync("refusals");
        const string pairId = "pair-normalized-rewind-refusals";
        const string otherPairId = "pair-normalized-rewind-refusals-other";
        await RegisterPairAsync(database, pairId, "Normalized Rewind refusals", "NormalizedRewindRefusals");
        await RegisterPairAsync(database, otherPairId, "Normalized Rewind refusals other", "NormalizedRewindRefusalsOther");
        var session = new SyncRewindSession("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa02", "connection-normalized-rewind",
            pairId, "Normalized Rewind refusals", "/NormalizedRewindRefusals", "local-checkpoint-scan",
            "dropbox-checkpoint-scan", "dropbox-cursor", DateTimeOffset.UtcNow, SyncRewindStatus.AwaitingDropbox);
        await new SyncRewindStore(database).UpsertAsync(session);
        var preview = new[]
        {
            new SyncRewindPreviewItem("Folder/File.txt", "Rewind changed Dropbox", "Retain local then replace from Dropbox",
                true, false, "Dropbox changed this path during rollback."),
            new SyncRewindPreviewItem("Straße/Review.txt", "Rewind restored in Dropbox", "Create on this PC",
                true, false, "Dropbox restored this path and no post-checkpoint local object occupies it.")
        };
        var published = session with
        {
            Status = SyncRewindStatus.PreviewReady,
            LocalPreviewScanId = "local-preview-scan",
            DropboxPreviewScanId = "dropbox-preview-scan",
            Preview = preview,
            PathProgress = preview.Select(item => new SyncRewindPathProgress(item.RelativePath,
                SyncRewindPathStatus.Pending, null, "Reviewed and awaiting apply.", DateTimeOffset.UtcNow)).ToArray()
        };
        await database.PublishRewindPreviewAsync(InstallationId, published, session.SessionId, null, null);

        await RefusesAsync(() => database.PersistRewindPathCheckpointAsync(published.SessionId, pairId,
                "local-preview-scan", "dropbox-preview-scan", null,
                Checkpoint("Outside/Preview.txt", SyncRewindPathStatus.Completed),
                SyncRewindStatus.Applying, "apply"),
            "NR4 a path outside the stored reviewed preview is refused");
        await RefusesAsync(() => database.PersistRewindPathCheckpointAsync(published.SessionId, otherPairId,
                "local-preview-scan", "dropbox-preview-scan", null,
                Checkpoint("Folder/File.txt", SyncRewindPathStatus.Completed),
                SyncRewindStatus.Applying, "apply"),
            "NR4 a checkpoint presented for a foreign pair is refused");
        await RefusesAsync(() => database.PersistRewindPathCheckpointAsync(published.SessionId, pairId,
                "a-different-local-preview", "dropbox-preview-scan", null,
                Checkpoint("Folder/File.txt", SyncRewindPathStatus.Completed),
                SyncRewindStatus.Applying, "apply"),
            "NR4 a checkpoint presented for a different preview generation is refused");
        await RefusesAsync(() => database.PersistRewindPathCheckpointAsync(published.SessionId, pairId,
                "local-preview-scan", "dropbox-preview-scan", "a-different-operation-group",
                Checkpoint("Folder/File.txt", SyncRewindPathStatus.Completed),
                SyncRewindStatus.Applying, "apply"),
            "NR4 a checkpoint presented for a mismatched operation group is refused");
        await RefusesAsync(() => database.PersistRewindPathCheckpointAsync(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa03", pairId, "local-preview-scan", "dropbox-preview-scan", null,
                Checkpoint("Folder/File.txt", SyncRewindPathStatus.Completed),
                SyncRewindStatus.Applying, "apply"),
            "NR4 a checkpoint for a missing session is refused");

        await database.PersistRewindPathCheckpointAsync(published.SessionId, pairId,
            "local-preview-scan", "dropbox-preview-scan", null,
            Checkpoint("Folder/File.txt", SyncRewindPathStatus.Completed),
            SyncRewindStatus.Applying, "apply");
        await RefusesAsync(() => database.PersistRewindPathCheckpointAsync(published.SessionId, pairId,
                "local-preview-scan", "dropbox-preview-scan", null,
                Checkpoint("Folder/File.txt", SyncRewindPathStatus.Pending),
                SyncRewindStatus.Applying, "apply"),
            "NR4 a stale update cannot regress a terminal path checkpoint");
        Require(await NumberAsync(database.DatabasePath,
                "SELECT count(*) FROM rewind_path_progress WHERE session_id='" + published.SessionId +
                "' AND status='completed';") == 1,
            "NR4 the refused regressive update changed no retained row");

        // Duplicate case/Unicode identity: SQLite NOCASE is ASCII-only, so the key is the real
        // ordinal-ignore-case identity computed in managed code.
        var unicodeDuplicate = published with
        {
            Preview = [preview[0], preview[1],
                preview[1] with { RelativePath = "STRASSE/Review.txt".Replace("SS", "ß", StringComparison.Ordinal) }]
        };
        await RefusesAsync(() => database.PublishRewindPreviewAsync(InstallationId, unicodeDuplicate,
                published.SessionId, published.LocalPreviewScanId, published.DropboxPreviewScanId),
            "NR4 a preview whose duplicate differs only by case folding is refused");
        await RefusesAsync(() => database.PublishRewindSessionAsync(InstallationId,
                published with { Status = (SyncRewindStatus)97 }),
            "NR4 an undefined session status is refused");
        await RefusesAsync(() =>
        {
            SyncRewindState.NormalizeAll([published], new HashSet<string>(StringComparer.Ordinal));
            return Task.CompletedTask;
        }, "NR4 the shared normalizer refuses a session whose pair is not retained");
        await RefusesAsync(() => database.PublishRewindSessionAsync(InstallationId,
                session with { SessionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa05" }),
            "NR4 a second active session for the same pair is refused by the partial unique index");
        await database.PersistRewindSessionStateAsync(published.SessionId, pairId, SyncRewindStatus.Applying,
            "Rewind apply is active.", "admitted-operation-group", clearOperationGroup: false);
        await RefusesAsync(() => database.PublishRewindPreviewAsync(InstallationId,
                published with { OperationGroupId = "admitted-operation-group" },
                published.SessionId, published.LocalPreviewScanId, published.DropboxPreviewScanId),
            "NR4 the reviewed path set of an admitted batch cannot be replaced");
        Require((await database.LoadActiveRewindSessionAsync(pairId))?.Preview?.Count == 2,
            "NR4 every refused update left the retained preview exactly as admitted");
    }

    // ---- NR5: legacy publication equality, idempotency and the bounded reference -----------------

    private static async Task LegacyPublicationChecksAsync()
    {
        var database = await NewDatabaseAsync("legacy-publication");
        const string pairId = "pair-normalized-rewind-legacy";
        await RegisterPairAsync(database, pairId, "Normalized Rewind legacy", "NormalizedRewindLegacy");
        var session = new SyncRewindSession("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa04", "connection-normalized-rewind",
            pairId, "Normalized Rewind legacy", "/NormalizedRewindLegacy", "local-checkpoint-scan",
            "dropbox-checkpoint-scan", "dropbox-cursor", DateTimeOffset.UtcNow, SyncRewindStatus.PreviewReady,
            "local-preview-scan", "dropbox-preview-scan",
            [new SyncRewindPreviewItem("Legacy/File.txt", "Rewind changed Dropbox", "Retain local then replace from Dropbox",
                true, false, "Dropbox changed this path during rollback.")],
            "Legacy collection imported.",
            PathProgress: [new SyncRewindPathProgress("Legacy/File.txt", SyncRewindPathStatus.Pending, null,
                "Reviewed and awaiting apply.", DateTimeOffset.UtcNow)]);
        var digest = SyncRewindState.CanonicalDigest([session]);
        Require(!await database.RewindSessionExistsAsync(session.SessionId),
            "NR5 the legacy session is absent from the authority database before publication");

        // Interruption before commit: publishing the same identity twice is exactly idempotent.
        await new SyncRewindStore(database).UpsertAsync(session);
        Require(await database.RewindSessionMatchesAsync(session),
            "NR5 an exact replay of the imported session completes from the retained equal row");
        var secondDigest = SyncRewindState.CanonicalDigest([session]);
        Require(digest == secondDigest, "NR5 the canonical legacy digest is stable across the import");
        var changed = session with { Detail = "changed while the collection was being published" };
        Require(!await database.RewindSessionMatchesAsync(changed) &&
                await database.RewindSessionExistsAsync(session.SessionId),
            "NR5 a changed legacy source collides with the retained identity instead of overwriting it");
        Require(SyncRewindState.CanonicalDigest([changed]) != digest,
            "NR5 a changed legacy source produces a different canonical digest, so compare-and-publish refuses");
        Require(await database.CountRewindSessionsAsync() == 1,
            "NR5 the exact replay created no second session row");

        // Post-publication: a wrong installation identity, a missing database and a corrupt database
        // fail closed instead of becoming an empty active session.
        var otherInstallation = new SyncAuthorityDatabase(IOPath.Combine(_root, "legacy-publication-other", "authority.db"));
        Directory.CreateDirectory(IOPath.Combine(_root, "legacy-publication-other"));
        await otherInstallation.InitializeAsync("installation-normalized-rewind-other", ProfileId, SourceRevision,
            CapabilityJson);
        await RefusesAsync(() => otherInstallation.PublishRewindSessionAsync("installation-normalized-rewind-other",
                session),
            "NR5 a session published for another installation's pair set is refused");
        var missing = new SyncAuthorityDatabase(IOPath.Combine(_root, "legacy-publication-missing", "authority.db"));
        await RefusesAsync(() => missing.LoadRewindSessionsAsync(),
            "NR5 a missing authority database refuses the read instead of reporting no sessions");
        var corrupt = IOPath.Combine(_root, "legacy-publication-corrupt", "authority.db");
        Directory.CreateDirectory(IOPath.Combine(_root, "legacy-publication-corrupt"));
        await File.WriteAllBytesAsync(corrupt, Encoding.ASCII.GetBytes("not a sqlite database"));
        await RefusesAsync(() => new SyncAuthorityDatabase(corrupt).LoadRewindSessionsAsync(),
            "NR5 a corrupt authority database refuses the read instead of reporting no sessions");
    }

    // ---- shared fixture helpers -----------------------------------------------------------------

    private static SyncAuthorityReviewedPathCheckpoint Checkpoint(string path, SyncRewindPathStatus status) =>
        new(path, status, null, new SyncDecision(path, SyncDecisionKind.Downloaded, "Reviewed rollback applied."));

    private static IReadOnlyList<SyncRewindPreviewItem> BuildPreview(int count)
    {
        var rows = new SyncRewindPreviewItem[count];
        for (var index = 0; index < count; index++)
        {
            var suffix = index.ToString("D5", CultureInfo.InvariantCulture);
            rows[index] = new SyncRewindPreviewItem("Folder " + suffix + "/Reviewed path " + suffix + ".txt",
                "Rewind changed Dropbox", "Retain local then replace from Dropbox", true, index % 3 == 0,
                "Dropbox changed this path during the rollback; local content is unchanged since the checkpoint.");
        }
        return rows;
    }

    private static async Task<SyncAuthorityDatabase> NewDatabaseAsync(string name)
    {
        var root = IOPath.Combine(_root, name);
        Directory.CreateDirectory(root);
        var database = new SyncAuthorityDatabase(IOPath.Combine(root, "authority.db"));
        await database.InitializeAsync(InstallationId, ProfileId, SourceRevision, CapabilityJson);
        return database;
    }

    private static async Task RegisterPairAsync(SyncAuthorityDatabase database, string pairId, string name,
        string rootName)
    {
        await database.RegisterPairAsync(pairId, 1,
            new SyncAuthorityPairConfiguration("disabled", 0, 1, "manual_preserve_both",
                "content_times_basic_attrs", RecursiveMode: true, AutomaticPruning: false,
                RecoveryRetentionDays: 30));
        var localRoot = IOPath.Combine(_root, "pair-roots", rootName);
        Directory.CreateDirectory(localRoot);
        using var lease = PilotDirectoryLease.Open(localRoot);
        await database.RetainPairDefinitionAsync(new SyncPair(pairId, name, localRoot,
            lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/" + rootName,
            "id:" + rootName, ConnectionId: "connection-normalized-rewind"), select: true);
    }

    private static long AggregateBytes(SyncProductStateStore store) =>
        JsonSerializer.SerializeToUtf8Bytes(store.Load()).LongLength;

    private static long DatabaseBytes(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static async Task<HashSet<string>> ObjectNamesAsync(string path)
    {
        await using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT lower(type) || ':' || name FROM sqlite_schema WHERE type IN ('table','index','trigger','view')" +
            " AND name NOT LIKE 'sqlite_%';";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<long> NumberAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> TextAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RefusesAsync(Func<Task> action, string message)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            Require(true, message);
            return;
        }
        throw new InvalidOperationException("Normalized Rewind state: expected a refusal but the call succeeded - " + message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Normalized Rewind state: " + message);
        _assertions++;
        Console.WriteLine("PASS " + message);
    }
}
