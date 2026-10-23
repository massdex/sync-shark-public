using System.Diagnostics;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class ObservationIdentityProgressChecks
{
    // Profile the generic production persistence path with real retained metadata and history.
    // No provider, credentials, live folders, publication, transfer, or retry is involved.
    internal static async Task ProfileRetainedAsync(string source, string root, string pairId)
    {
        var diagnosticRoot = DiagnosticPaths.RequireRoot();
        var allocation = Path.Combine(diagnosticRoot, "recovery", "root703-atomic-proof");
        var localAllocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TestData", "Projects", "sync-shark", "build");
        var stagedSource = Path.Combine(diagnosticRoot, "build", "root682-cloned-state-live",
            "real-provider-state", "vNext", "state", "syncshark.db");
        if (!string.Equals(source, stagedSource, StringComparison.OrdinalIgnoreCase) ||
            !(root.StartsWith(allocation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
              root.StartsWith(localAllocation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ||
            pairId != "f00d0000000000000028")
            throw new IOException("Use the closed actual staged database, exact parent pair and one fresh owned profiling directory.");
        foreach (var path in new[] { source, root })
            for (var item = path; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
                if ((File.Exists(item) || Directory.Exists(item)) &&
                    (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Profiling must not follow reparse targets.");
        using var originalOwner = PilotInstanceLock.AcquireForMaintenance(SyncSharkRuntimePaths.InstanceLockFile);
        using var stagedOwner = PilotInstanceLock.Acquire(Path.Combine(Path.GetDirectoryName(
            Path.GetDirectoryName(Path.GetDirectoryName(source)))!, "state", SyncSharkRuntimePaths.InstanceLockFileName));
        // Holding a read-only share prevents a writer or replacement while snapshot bytes are copied.
        await using var heldSource = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (File.Exists(source + "-wal") && new FileInfo(source + "-wal").Length != 0)
            throw new IOException("The retained source must be checkpointed and closed.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var token = deadline.Token;
        Directory.CreateDirectory(root);
        var copy = Path.Combine(root, "authority.db");
        if (Directory.EnumerateFileSystemEntries(root).Any(path => path != copy))
            throw new IOException("A profiling directory must contain only its exact unchanged database copy.");
        if (!File.Exists(copy))
        {
            await using var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await heldSource.CopyToAsync(output, token);
        }
        heldSource.Position = 0;
        var sourceHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(heldSource, token));
        await using (var copied = File.OpenRead(copy))
            Require(Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(copied, token)) == sourceHash,
                "Representative database copy is byte-exact; source is held against writes and replacement");
        var db = new SyncAuthorityDatabase(copy);
        var timings = new System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, double Ms)>();
        var previousTiming = SyncAuthorityDatabase.TimingForTest;
        SyncAuthorityDatabase.TimingForTest = (phase, ms) => timings.AddOrUpdate(phase, (1, ms),
            (_, prior) => (prior.Count + 1, prior.Ms + ms));
        try
        {
            var watch = Stopwatch.StartNew();
            await db.VerifyHealthAsync(token, Console.WriteLine);
            Console.WriteLine($"MEASURE health after copy/hash (cache warmth not controlled): {watch.Elapsed.TotalSeconds:F3}s");
            timings.Clear();
            await using var scope = db.BeginConnectionReuseScope();
            await using var read = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = copy, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await read.OpenAsync(token);
            async Task<long> Count(string table)
            {
                await using var command = read.CreateCommand();
                command.CommandText = "SELECT count(*) FROM " + table;
                return Convert.ToInt64(await command.ExecuteScalarAsync(token));
            }
            var attempts = await Count("operation_attempt"); var receipts = await Count("effect_receipt");
            async Task<string?> LatestComplete()
            {
                await using var command = read.CreateCommand();
                command.CommandText = "SELECT scan_generation_id FROM scan_generation WHERE pair_id=$pair AND side='local' AND status='complete' ORDER BY generation DESC LIMIT 1;";
                command.Parameters.AddWithValue("$pair", pairId);
                return await command.ExecuteScalarAsync(token) as string;
            }
            string rootId;
            var entries = new List<SyncAuthorityObservationInput>();
            await using (var command = read.CreateCommand())
            {
                command.CommandText = """
                    SELECT s.root_binding_id,m.relative_path_key,l.object_kind,v.identity_json,
                           v.content_hash,v.size_bytes,o.evidence_json
                    FROM observation o JOIN scan_generation s ON s.scan_generation_id=o.scan_generation_id
                    JOIN membership m ON m.membership_id=o.membership_id
                    JOIN object_version v ON v.object_version_id=o.object_version_id
                    JOIN logical_object l ON l.object_id=v.object_id
                    WHERE s.scan_generation_id=(SELECT scan_generation_id FROM scan_generation
                        WHERE pair_id=$pair AND side='local' ORDER BY generation DESC LIMIT 1)
                    ORDER BY m.relative_path_key LIMIT 1024;
                    """;
                command.Parameters.AddWithValue("$pair", pairId);
                await using var reader = await command.ExecuteReaderAsync(token);
                rootId = "";
                while (await reader.ReadAsync(token))
                {
                    rootId = reader.GetString(0);
                    entries.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetString(6)));
                }
            }
            Require(entries.Count == 1024, "Exactly1024 real parent metadata rows selected from the retained scan");
            var before = await LatestComplete();
            var scan = await db.BeginScanGenerationAsync(pairId, rootId, "local", "manual", "{}", token);
            timings.Clear(); watch.Restart();
            var saved = 0;
            await db.AppendObservationEntriesAsync(scan, entries, token, item =>
            {
                if (item.Detail.StartsWith("Saved ", StringComparison.Ordinal)) saved++;
                return Task.CompletedTask;
            }, "retained local sample", concurrentProgressRetention: true);
            Console.WriteLine(JsonSerializer.Serialize(new { phase = "real_metadata_persistence", rows = entries.Count,
                seconds = watch.Elapsed.TotalSeconds, timings = timings.OrderBy(x => x.Key).ToDictionary(x => x.Key,
                    x => new { count = x.Value.Count, milliseconds = x.Value.Ms }),
                note = "Nested timing totals are not additive. Callback is a counter, not the production log sink." }));
            Require(saved == entries.Count && await LatestComplete() == before,
                "All metadata row notifications retained; partial diagnostic inventory never published");
            Require(await Count("operation_attempt") == attempts && await Count("effect_receipt") == receipts,
                "No operation attempt or effect receipt created");
            Console.WriteLine(JsonSerializer.Serialize(new { result = "profiled", sourceHash, sourceBytes = heldSource.Length,
                copyBytes = new FileInfo(copy).Length, sourceMutation = false, providerCalls = 0, fileEffects = 0 }));
        }
        finally { SyncAuthorityDatabase.TimingForTest = previousTiming; }
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidDataException("Expected rejection: " + typeof(T).Name);
    }

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        foreach (var side in new[] { "local", "dropbox" })
        {
            var db = await DatabaseAsync(Path.Combine(root, side));
            var first = await ScanAsync(db, side, [Entry(side, "original.txt", "old")]);
            var original = (await LatestAsync(db, side)).Single();
            var logAdmission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", "pair",
                null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
            await db.TransitionCommandRequestAsync(logAdmission.CommandRequestId, "executing");
            var logWriter = await db.OpenRunActivityWriterAsync(logAdmission);
            var progress = new List<SyncComparisonProgress>();
            var next = await db.BeginScanGenerationAsync("pair", side + "-root", side, "manual", "{}");
            await db.AppendObservationEntriesAsync(next,
                [Entry(side, "original.txt", "replacement"), Entry(side, "moved.txt", "old")],
                onProgress: async item =>
                {
                    progress.Add(item);
                    // Retaining the log in the same DB must not deadlock or publish a partial scan.
                    await db.AppendRunActivityAsync([logWriter.Enqueue(new("", item.ReportedUtc,
                        [SyncActivityCategories.Sync], "Information", "File", item.Phase,
                        SyncProgressDescription.Describe(item), PairId: "pair", RunId: logAdmission.CommandRequestId))]);
                    Require(await db.LoadLatestCompleteScanIdAsync("pair", side) == first.ScanGenerationId,
                        side + ": committed batches are not published as complete scans");
                });
            await db.CompleteScanGenerationAsync(next);
            await db.CloseRunActivityWriterAsync(logWriter);
            await db.TransitionCommandRequestAsync(logAdmission.CommandRequestId, "completed");
            var rows = await LatestAsync(db, side);
            Require(rows.Count == 2 && rows.Select(item => item.ObjectId).Distinct().Count() == 2,
                side + ": moved file and replacement have separate logical identities");
            Require(rows.Single(item => item.RelativePath == "moved.txt").ObjectId == original.ObjectId,
                side + ": original physical identity survives a move");
            Require(progress.Any(item => item.ProgressUnit == "records saved" && item.CompletedChecks == 2 &&
                                         item.TotalChecks == 2 && item.RelativePath == "original.txt"),
                side + ": exact file and committed count reach the reentrant retained-log callback");
            var replacementId = rows.Single(item => item.RelativePath == "original.txt").ObjectId;
            await ScanAsync(db, side, [Entry(side, "moved.txt", "old"), Entry(side, "original.txt", "replacement")]);
            Require((await LatestAsync(db, side))
                .Single(item => item.RelativePath == "original.txt").ObjectId == replacementId,
                side + ": replacement identity survives another scan and input reordering");
        }
        await LegacyCollisionAsync(Path.Combine(root, "legacy"), false);
        await LegacyCollisionAsync(Path.Combine(root, "legacy-cross-batch"), true);
        await LegacyCollisionAsync(Path.Combine(root, "legacy-issued"), true, issued: true);
        await LegacyCollisionAsync(Path.Combine(root, "legacy-completed"), false, committed: true);
        await LegacyCollisionAsync(Path.Combine(root, "legacy-completed-cross-batch"), true, committed: true);
        await LegacyCollisionAsync(Path.Combine(root, "legacy-completed-old-first"), true, committed: true, originalFirst: true);
        await LegacyCollisionAsync(Path.Combine(root, "legacy-two-authorities"), true, committed: true, bothAuthoritative: true);
        await ExplicitIdentityRecoveryAsync(Path.Combine(root, "explicit-identity-recovery"));
        await ExplicitIdentityRecoveryAsync(Path.Combine(root, "coordinator-cloud-replacement"), true);
        await AtomicSaveAsync(Path.Combine(root, "atomic-save"));
        await SealedRecoveryAdmissionAsync(Path.Combine(root, "sealed-recovery"));
        await LargeInventoryAsync(Path.Combine(root, "batches"));
        await ReservationFilteringAsync(Path.Combine(root, "reservations"));
        await CompleteExclusionCacheAsync(Path.Combine(root, "exclusion-cache"));
        await InsertCollisionRejectionAsync(Path.Combine(root, "insert-collisions"));
        await PathIndexProgressAsync(Path.Combine(root, "path-index-progress"));
        Console.WriteLine("PASS observation identities, legacy observation-only repair, publication guard and concrete committed progress");
    }

    private static async Task PathIndexProgressAsync(string root)
    {
        var db = await DatabaseAsync(root);
        var local = await ScanAsync(db, "local", [Entry("local", "item.txt", "local-item")]);
        var remote = await ScanAsync(db, "dropbox", [Entry("dropbox", "item.txt", "remote-item")]);
        var progress = new List<SyncComparisonProgress>();
        var entries = new[] { new SyncPathIndexEntry("item.txt", PilotEntryKind.File, true, true,
            true, false, "local-item", "remote-item", true) };
        async Task<long> CountIndexAsync()
        {
            await using var read = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = db.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            await read.OpenAsync();
            await using var query = read.CreateCommand();
            query.CommandText = "SELECT count(*) FROM sync_path_index WHERE pair_id='pair';";
            return Convert.ToInt64(await query.ExecuteScalarAsync());
        }
        await db.ReplacePathIndexAsync("pair", local.ScanGenerationId, remote.ScanGenerationId,
            entries, new SyncSelectionIndex([], true), onProgress: async item =>
            {
                progress.Add(item);
                // A real retained-log callback must stay outside the index transaction: no nested writer deadlock.
                await db.AppendActivityEventAsync(new(Guid.NewGuid().ToString("N"), item.ReportedUtc,
                    [SyncActivityCategories.Sync], "Information", "Folder pair", item.Phase,
                    SyncProgressDescription.Describe(item), PairId: "pair", RunId: "index-fixture"));
                var count = await CountIndexAsync();
                Require(count == (item.Phase == "Folder Index Committed" ? 1 : 0),
                    "Index progress names only actual transaction start/committed completion, never synthetic preparation.");
            });
        Require(progress.Count == 2 && progress[0].CompletedChecks == 0 &&
                progress[1].CompletedChecks == 1 && progress.All(item => item.ProgressUnit == "records committed"),
            "Index replacement performs no duplicate per-file logging-only walk and retains truthful atomic progress.");
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => db.ReplacePathIndexAsync("pair",
            local.ScanGenerationId, remote.ScanGenerationId, [], new SyncSelectionIndex([], true), stopped.Token));
        Require(await CountIndexAsync() == 1,
            "A cancelled index replacement preserves its prior committed index.");
    }

    private static async Task SealedRecoveryAdmissionAsync(string root)
    {
        var db = await DatabaseAsync(root);
        var admission = await AdmitFixturePairAsync(db);
        await ScanAsync(db, "dropbox", [Entry("dropbox", "queued.txt", "queued")]);
        var entry = (await LatestAsync(db, "dropbox")).Single();
        await db.RegisterOperationGroupAsync("group", "compound", "{}");
        await db.CreateOperationAsync(new("queued-op", "queued-key", SyncAuthorityOperationKind.CreateLocal,
            "pair", entry.ObjectId, 1, "profile", "{}", 0,
            [new("queued-source", "pair", entry.ObjectId, "source", "dropbox-root", entry.MembershipId,
                entry.ObjectVersionId, entry.MembershipGeneration, "queued.txt", "{}")], [], [], "group"), admission: admission);
        Require((await db.ListRunnableOperationsForPairAsync("pair")).Count == 0 &&
                (await db.ListRecoveryOperationsForPairAsync("pair")).Count == 0,
            "a half-written unsealed plan is absent from execution and startup-recovery queues");
        var refused = false;
        try { await db.AcquireOperationLeaseAsync("queued-op", "worker", TimeSpan.FromMinutes(1)); }
        catch (InvalidOperationException error) { refused = error.Message.Contains("sealed"); }
        Require(refused && (await db.LoadOperationAsync("queued-op"))!.Attempts.Count == 0,
            "direct execution cannot bypass the complete-plan seal or create an attempt");
        await db.MarkOperationGroupActiveAsync("group");
        Require((await db.ListRunnableOperationsForPairAsync("pair")).Count == 1 &&
                (await db.ListRecoveryOperationsForPairAsync("pair")).Count == 0,
            "sealing admits ordinary execution but startup recovery still excludes unissued work");
        var lease = await db.AcquireOperationLeaseAsync("queued-op", "worker", TimeSpan.FromMinutes(1));
        await db.StartDispatchedAttemptAsync("queued-op", lease, new string('c', 64), "download");
        await db.ReleaseLeaseAsync(lease);
        var reopened = new SyncAuthorityDatabase(db.DatabasePath);
        Require((await reopened.ListRecoveryOperationsForPairAsync("pair")).Single().OperationId == "queued-op",
            "the issued uncertain attempt remains discoverable by real startup recovery after reopen");
        refused = false;
        try { await reopened.SuspendOperationGroupForPlanningAsync("group"); }
        catch (InvalidOperationException error) { refused = error.Message.Contains("uncertain"); }
        Require(refused && (await reopened.LoadOperationAsync("queued-op"))!.Attempts.Count == 1,
            "an uncertain issued effect cannot be silently replanned or discarded");
    }

    private static async Task AtomicSaveAsync(string root)
    {
        var db = await DatabaseAsync(root);
        var admission = await AdmitFixturePairAsync(db);
        await ScanAsync(db, "local", [Entry("local", "saved.txt", "local-original")]);
        await ScanAsync(db, "dropbox", [Entry("dropbox", "saved.txt", "remote-original")]);
        var local = (await LatestAsync(db, "local")).Single();
        var remote = (await LatestAsync(db, "dropbox")).Single();
        await db.AcceptSynchronizationPointAsync("atomic-baseline", "pair", local.ObjectId,
            local.ObjectVersionId, remote.ObjectVersionId, local.MembershipId, remote.MembershipId);
        foreach (var side in new[] { "local", "dropbox" })
        {
            await ScanAsync(db, side, [Entry(side, "saved.txt", side + "-atomic-save")]);
            var saved = (await LatestAsync(db, side)).Single();
            Require(saved.ObjectId == local.ObjectId,
                side + ": an atomic save at the same path retains the logical identity and accepted baseline");
        }
        var outcomes = await new SyncAuthorityReconciliationPlanner(db,
            new(1, "profile", "local-control", "remote-control"), admission: admission).PlanAsync("pair");
        Require(outcomes.Count == 1 && outcomes[0].Kind == SyncAuthorityPlannerOutcomeKind.Adopted,
            "the real planner accepts unchanged bytes after atomic saves on both sides without a false identity conflict or transfer");
    }

    private static async Task LegacyCollisionAsync(string root, bool crossBatch, bool issued = false,
        bool committed = false, bool originalFirst = false, bool bothAuthoritative = false)
    {
        var db = await DatabaseAsync(root);
        var admission = await AdmitFixturePairAsync(db);
        await ScanAsync(db, "dropbox", [Entry("dropbox", "original.txt", "old")]);
        var original = (await LatestAsync(db, "dropbox")).Single();
        var replacement = Entry("dropbox", "original.txt", "replacement");
        // Obtain the canonical version identity from a real atomic-save observation,
        // as the old live database did, instead of inventing a version primary key.
        await ScanAsync(db, "dropbox", [replacement]);
        var replacementVersion = (await LatestAsync(db, "dropbox")).Single().ObjectVersionId;
        var legacy = await db.BeginScanGenerationAsync("pair", "dropbox-root", "dropbox", "manual", "{}");
        // Reproduce old persisted input, not the new resolution algorithm.
        await db.RegisterMembershipAsync("legacy-moved", original.ObjectId, "dropbox-root", "moved.txt", 1);
        await db.RegisterMembershipAsync("legacy-replaced", original.ObjectId, "dropbox-root", "original.txt", 3);
        await db.AppendObservationAsync(legacy, "legacy-obs-old", original.ObjectVersionId, "legacy-moved", "{}");
        await db.AppendObservationAsync(legacy, "legacy-obs-new", replacementVersion, "legacy-replaced", "{}");
        var refused = false;
        try { await db.CompleteScanGenerationAsync(legacy); }
        catch (InvalidDataException exception) { refused = exception.Message.Contains("conflicting file identity"); }
        Require(refused, "new publication refuses the exact legacy duplicate-object inventory");
        // The old release published this invalid snapshot. Seed that historical state only
        // in this disposable fixture, then exercise the production repair on real rows.
        await using (var connection = new SqliteConnection("Data Source=" + db.DatabasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE scan_generation SET status='complete',completed_utc=$utc,entry_count=2 WHERE scan_generation_id=$scan; UPDATE observation SET complete_generation=1 WHERE scan_generation_id=$scan;";
            command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$scan", legacy.ScanGenerationId);
            await command.ExecuteNonQueryAsync();
        }
        // An unissued retained operation is not an external effect. Keep its exact
        // original member/version/history; the fresh snapshot receives new identity.
        await db.CreateOperationAsync(new SyncAuthorityOperationPlan("legacy-op", "legacy-op-key",
            SyncAuthorityOperationKind.CreateLocal, "pair", original.ObjectId, 1, "profile", "{}", 0,
            [new("legacy-source", "pair", original.ObjectId, "source", "dropbox-root", "legacy-replaced",
                replacementVersion, 3, "original.txt", "{\"kind\":\"file\"}")], [], []), admission: admission);
        SyncAuthoritySynchronizationPoint? committedPoint = null;
        if (issued || committed)
        {
            var lease = await db.AcquireOperationLeaseAsync("legacy-op", "fixture-worker", TimeSpan.FromMinutes(5));
            var attempt = await db.StartDispatchedAttemptAsync("legacy-op", lease, new string('b', 64), "download");
            if (committed)
            {
                // Reproduce Child's completed copy through the real fenced receipt/commit API.
                // The old physical file was archived; the replacement at the original path
                // has already been copied and owns the accepted synchronization point.
                await ScanAsync(db, "local", [Entry("local", "original.txt", "received")]);
                var local = (await LatestAsync(db, "local")).Single();
                Require(local.ObjectId == original.ObjectId, "the received file retains the completed operation's logical identity");
                if (bothAuthoritative)
                    await db.AcceptSynchronizationPointAsync("older-authority", "pair", original.ObjectId,
                        null, original.ObjectVersionId, null, "legacy-moved");
                await db.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.KnownSuccess,
                    replacement.IdentityJson, Entry("local", "original.txt", "received").IdentityJson,
                    "{}", new string('c', 64));
                await db.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, "completed-copy-point", "pair",
                    original.ObjectId, local.ObjectVersionId, replacementVersion, local.MembershipId,
                    "legacy-replaced", "{}");
                await db.CompleteOperationAsync(lease, "{}");
                committedPoint = (await db.LoadLatestSynchronizationPointsAsync("pair")).Single();
            }
            await db.ReleaseLeaseAsync(lease);
        }
        if (issued || bothAuthoritative)
        {
            var next = await db.BeginScanGenerationAsync("pair", "dropbox-root", "dropbox", "manual", "{}");
            var rejected = false;
            try { await db.AppendObservationEntriesAsync(next, bothAuthoritative
                ? [replacement, Entry("dropbox", "moved.txt", "old")] : [replacement]); }
            catch (InvalidDataException exception) { rejected = exception.Message.Contains("retained effect authority"); }
            Require(rejected && await db.LoadLatestCompleteScanIdAsync("pair", "dropbox") == legacy.ScanGenerationId,
                "an uncertain effect or two effect-bearing identities remain blocked without publishing a misleading repaired inventory");
            Require((await db.LoadOperationAsync("legacy-op"))!.Attempts.Count == 1,
                "the rejected repair preserves the exact uncertain attempt for reconciliation");
            return;
        }
        if (crossBatch)
        {
            var split = await db.BeginScanGenerationAsync("pair", "dropbox-root", "dropbox", "manual", "{}");
            await db.AppendObservationEntriesAsync(split, [originalFirst ? Entry("dropbox", "moved.txt", "old") : replacement]);
            await db.AppendObservationEntriesAsync(split, [originalFirst ? replacement : Entry("dropbox", "moved.txt", "old")]);
            await db.CompleteScanGenerationAsync(split);
        }
        else await ScanAsync(db, "dropbox", [replacement, Entry("dropbox", "moved.txt", "old")]);
        var repaired = await LatestAsync(db, "dropbox");
        Require(repaired.Select(item => item.ObjectId).Distinct().Count() == 2 &&
                repaired.Single(item => item.RelativePath == (committed ? "original.txt" : "moved.txt")).ObjectId == original.ObjectId,
            "legacy collision preserves the accepted effect identity, or historical origin when no baseline exists (committed=" + committed + ", cross-batch=" + crossBatch + ")");
        Require((await db.LoadObjectVersionAsync(replacementVersion)).ObjectId == original.ObjectId,
            "repair preserves the historical version instead of rewriting retained history");
        Require((await db.LoadOperationAsync("legacy-op"))!.ObjectId == original.ObjectId,
            "new observation identity does not rebind the original retained operation");
        if (committed)
        {
            var operation = await db.LoadOperationAsync("legacy-op");
            Require(operation.Status == SyncAuthorityOperationStatus.Completed &&
                    operation.Attempts.Single().Status == SyncAuthorityAttemptStatus.VerifiedSuccess &&
                    (await db.LoadLatestSynchronizationPointsAsync("pair")).Single() == committedPoint,
                "completed effect, exact attempt and accepted synchronization point are unchanged by identity separation");
            var archivedId = repaired.Single(item => item.RelativePath == "moved.txt").ObjectId;
            await ScanAsync(db, "dropbox", [Entry("dropbox", "moved.txt", "old"), replacement]);
            Require((await LatestAsync(db, "dropbox")).Single(item => item.RelativePath == "moved.txt").ObjectId == archivedId,
                "the separated observation-only archive retains its identity after rescan and reordering");
            var outcomes = await new SyncAuthorityReconciliationPlanner(db,
                new(1, "profile", "local-control", "remote-control"), admission: admission).PlanAsync("pair");
            Require(outcomes.Count(item => item.RelativePath == "original.txt") == 1 &&
                    outcomes.Single(item => item.RelativePath == "original.txt") is
                        { OperationId: null, Kind: SyncAuthorityPlannerOutcomeKind.Unchanged } &&
                    (await db.LoadLatestSynchronizationPointsAsync("pair")).Single() == committedPoint,
                "the real planner recognizes the completed file as unchanged, not a download or move to replay");
        }
    }

    private static async Task ExplicitIdentityRecoveryAsync(string root, bool coordinatorReplacement = false)
    {
        var filesRoot = coordinatorReplacement ? Path.Combine(root, "files") : null;
        PilotLocalScan? native = null;
        if (filesRoot is not null)
        {
            Directory.CreateDirectory(filesRoot);
            await File.WriteAllTextAsync(Path.Combine(filesRoot, "original.txt"), "Previous live file bytes\n");
            await File.WriteAllTextAsync(Path.Combine(filesRoot, "moved.txt"), "Verified archive bytes\n");
            using var held = PilotDirectoryLease.Open(filesRoot);
            native = await new PilotLocalScanner().ScanAsync(held, contentProvider: SyncProviders.Dropbox);
            Require(native.Decisions.Count == 0 && native.Entries.Count == 2,
                "coordinator replacement fixture starts with two real freshly hashed NTFS files");
        }
        var db = await DatabaseAsync(root, filesRoot);
        var admission = await AdmitFixturePairAsync(db);
        SyncAuthorityObservationInput FixtureEntry(string side, string path, string fileId)
        {
            if (native is null) return Entry(side, path, fileId);
            var physical = native.Entries[fileId.EndsWith("-old", StringComparison.Ordinal) ? "original.txt" : "moved.txt"];
            var identity = side == "local"
                ? SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File, physical.VolumeSerialNumber!.Value,
                    physical.FileId!.Value, physical.LastWriteUtc, null, null,
                    SyncContentIdentity.IdentityStampFor(SyncProviders.Dropbox), physical.ContentHash, physical.Size)
                : JsonSerializer.Serialize(new { fileId, revision = "1" });
            return new(path, "file", identity, physical.ContentHash, physical.Size,
                JsonSerializer.Serialize(new { path, kind = "file" }));
        }
        foreach (var side in new[] { "local", "dropbox" })
            await ScanAsync(db, side, [FixtureEntry(side, "original.txt", side + "-old")]);
        var oldLocal = (await LatestAsync(db, "local")).Single();
        var oldRemote = (await LatestAsync(db, "dropbox")).Single();
        var objectId = oldLocal.ObjectId;
        var priorPoint = await CompleteCopyAsync("prior-copy", "prior-point", oldLocal, oldRemote, "original.txt");
        foreach (var side in new[] { "local", "dropbox" })
            await ScanAsync(db, side, [FixtureEntry(side, "original.txt", side + "-new")]);
        var newLocal = (await LatestAsync(db, "local")).Single();
        var newRemote = (await LatestAsync(db, "dropbox")).Single();
        var localMoved = await SeedLegacyAsync("local", oldLocal, newLocal);
        var remoteMoved = await SeedLegacyAsync("dropbox", oldRemote, newRemote);
        var currentPoint = await CompleteCopyAsync("current-copy", "current-point", localMoved, remoteMoved, "moved.txt");
        var proof = new SyncCompletedCopyMove(objectId, priorPoint.SynchronizationPointId,
            currentPoint.SynchronizationPointId, "original.txt", "moved.txt", oldLocal.ObjectVersionId,
            newLocal.ObjectVersionId, oldRemote.ObjectVersionId, newRemote.ObjectVersionId,
            oldLocal.MembershipId, oldLocal.MembershipGeneration, localMoved.MembershipId,
            localMoved.MembershipGeneration, "current-copy");
        var pointsBefore = await db.LoadLatestSynchronizationPointsAsync("pair");
        var localEntries = new[] { FixtureEntry("local", "original.txt", "local-old"), FixtureEntry("local", "moved.txt", "local-new") };
        var remoteEntries = new[] { FixtureEntry("dropbox", "original.txt", "dropbox-old") with { ContentHash = new string('d', 64) },
            FixtureEntry("dropbox", "moved.txt", "dropbox-new") };

        // The ordinary scanner must still refuse both accepted incarnations.
        var ordinary = await db.BeginScanGenerationAsync("pair", "dropbox-root", "dropbox", "manual", "{}");
        var refused = false;
        try { await db.AppendObservationEntriesAsync(ordinary, remoteEntries); }
        catch (InvalidDataException error)
        {
            refused = error.Message.Contains("retained effect authority") &&
                error.Data[nameof(SyncAuthorityIdentityCollision)] is SyncAuthorityIdentityCollision { RelativePath: "original.txt" };
        }
        Require(refused, "explicit recovery does not weaken ordinary two-authority rejection");

        var localScan = await db.BeginScanGenerationAsync("pair", "local-root", "local", "manual", "{}");
        var remoteScan = await db.BeginScanGenerationAsync("pair", "dropbox-root", "dropbox", "manual", "{}");
        foreach (var (bad, failedPath, reason) in new[] {
                     (remoteEntries.Take(1).ToArray(), "moved.txt", "absent from the selected inventory"),
                     (new[] { remoteEntries[0] with { IdentityJson = remoteEntries[1].IdentityJson }, remoteEntries[1] }, "original.txt", "physical identity differs"),
                     (new[] { remoteEntries[0] with { ContentHash = null }, remoteEntries[1] }, "original.txt", "content hash is unavailable"),
                     (new[] { remoteEntries[0] with { Size = null }, remoteEntries[1] }, "original.txt", "byte length is unavailable"),
                     (new[] { remoteEntries[0] with { ObjectKind = "directory" }, remoteEntries[1] }, "original.txt", "not a regular file"),
                     (new[] { remoteEntries[0] with { BoundObjectId = "foreign-authority" }, remoteEntries[1] }, "original.txt", "another recovery authority") })
        {
            refused = false;
            try { await db.VerifyIdentityRecoveryInventoriesAsync(localScan, remoteScan, localEntries, bad, [proof], default); }
            catch (IOException error) { refused = error.Message.Contains("dropbox file '" + failedPath + "'") &&
                    error.Message.Contains(reason) && error.Message.Contains("Neither inventory was published"); }
            Require(refused, "each recovery rejection identifies its exact endpoint, file and failed check before either inventory is saved: " + reason);
        }
        await db.VerifyIdentityRecoveryInventoriesAsync(localScan, remoteScan, localEntries, remoteEntries, [proof], default);
        // A new unfinished operation after the read-only verification must also
        // be refused by the real write transaction, not trusted from that read.
        await db.CreateOperationAsync(new("unfinished-copy", "unfinished-key", SyncAuthorityOperationKind.CreateLocal,
            "pair", objectId, 1, "profile", "{}", 0,
            [new("unfinished-source", "pair", objectId, "source", "dropbox-root", oldRemote.MembershipId,
                oldRemote.ObjectVersionId, oldRemote.MembershipGeneration, "original.txt", "{}")], [], []), admission: admission);
        refused = false;
        try { await db.AppendIdentityRecoveryObservationEntriesAsync(localScan, localEntries, [proof], default, null, "local", false); }
        catch (InvalidDataException) { refused = true; }
        Require(refused, "an unfinished retained action invalidates earlier read-only recovery permission");
        var unusedLease = await db.AcquireOperationLeaseAsync("unfinished-copy", "fixture", TimeSpan.FromMinutes(1));
        await db.SupersedeReleasedLocalPreparationAsync(unusedLease, "pair", default);
        await db.ReleaseLeaseAsync(unusedLease);

        if (coordinatorReplacement)
        {
            await CheckCoordinatorReplacementAsync();
            return;
        }

        await db.AppendIdentityRecoveryObservationEntriesAsync(localScan, localEntries, [proof], default, null, "local", false);
        await db.AppendIdentityRecoveryObservationEntriesAsync(remoteScan, remoteEntries, [proof], default, null, "cloud", false);
        const string recoveryCommand = "explicit-identity-recovery-command";
        const string recoveryResult = "{\"pairId\":\"pair\",\"firstSyncScanComplete\":true}";
        await db.CreateCommandRequestAsync(recoveryCommand, "run_pair", "pair", null, null, "{}");
        await db.TransitionCommandRequestAsync(recoveryCommand, "accepted");
        await db.TransitionCommandRequestAsync(recoveryCommand, "executing");
        var oldLocalScan = await db.LoadLatestCompleteScanIdAsync("pair", "local");
        var oldRemoteScan = await db.LoadLatestCompleteScanIdAsync("pair", "dropbox");
        await ThrowsAsync<InvalidDataException>(() => db.PublishIdentityRecoveryAsync(localScan, remoteScan,
            localEntries, remoteEntries, [proof], 999, recoveryCommand, "completed", recoveryResult, default));
        await ThrowsAsync<InvalidDataException>(() => db.PublishIdentityRecoveryAsync(localScan with { RootBindingId = "foreign-root" }, remoteScan,
            localEntries, remoteEntries, [proof], 1, recoveryCommand, "completed", recoveryResult, default));
        // Existing writer diagnostic hook supplies an interruption only. Both
        // scan updates and the command transition execute through production
        // code in the real native transaction; none of those decisions is fake.
        SyncAuthorityDatabase.TimingForTest = (phase, _) =>
        {
            if (phase == "write-body") throw new IOException("Controlled pre-commit interruption");
        };
        try
        {
            await ThrowsAsync<IOException>(() => db.PublishIdentityRecoveryAsync(localScan, remoteScan,
                localEntries, remoteEntries, [proof], 1, recoveryCommand, "completed", recoveryResult, default));
        }
        finally { SyncAuthorityDatabase.TimingForTest = null; }
        Require(await db.LoadLatestCompleteScanIdAsync("pair", "local") == oldLocalScan &&
                await db.LoadLatestCompleteScanIdAsync("pair", "dropbox") == oldRemoteScan &&
                (await db.LoadRunHistoryAsync()).Single(item => item.Id == recoveryCommand).Status == "executing",
            "interruption after both real scan updates rolls back both publications and the recovery command before native commit");
        await db.PublishIdentityRecoveryAsync(localScan, remoteScan, localEntries, remoteEntries, [proof],
            1, recoveryCommand, "completed", recoveryResult, default);
        var retained = (await db.LoadRunHistoryAsync()).Single(item => item.Id == recoveryCommand);
        var reopened = new SyncAuthorityDatabase(db.DatabasePath);
        Require(await reopened.LoadLatestCompleteScanIdAsync("pair", "local") == localScan.ScanGenerationId &&
                await reopened.LoadLatestCompleteScanIdAsync("pair", "dropbox") == remoteScan.ScanGenerationId &&
                retained.Status == "completed" && retained.ResultJson!.Contains("identityRecoveryPublication"),
            "reopening sees both corrected inventories and their exact retained mapping result, never a half-published planning view");
        await reopened.PublishIdentityRecoveryAsync(localScan, remoteScan, localEntries, remoteEntries, [proof],
            1, recoveryCommand, "completed", recoveryResult, default);
        Require((await reopened.LoadRunHistoryAsync()).Single(item => item.Id == recoveryCommand) == retained,
            "exact recovery-publication replay returns the committed mapping without rewriting its result or timestamp");
        var local = await LatestAsync(db, "local");
        var remote = await LatestAsync(db, "dropbox");
        var separated = local.Single(item => item.RelativePath == "original.txt").ObjectId;
        Require(separated != objectId && remote.Single(item => item.RelativePath == "original.txt").ObjectId == separated &&
                local.Single(item => item.RelativePath == "moved.txt").ObjectId == objectId &&
                remote.Single(item => item.RelativePath == "moved.txt").ObjectId == objectId,
            "explicit recovery separates the older distinct incarnation consistently on both endpoints");
        Require((await db.LoadLatestSynchronizationPointsAsync("pair")).SequenceEqual(pointsBefore) &&
                (await db.LoadOperationAsync("prior-copy"))!.Attempts.Count == 1 &&
                (await db.LoadOperationAsync("current-copy"))!.Attempts.Count == 1 &&
                (await db.LoadObjectVersionAsync(oldRemote.ObjectVersionId)).ObjectId == objectId,
            "both accepted authorities, completed attempts and historical object versions remain byte-semantically unchanged");
        var outcomes = await new SyncAuthorityReconciliationPlanner(db,
            new(1, "profile", "local-control", "remote-control"), admission: admission).PlanAsync("pair");
        Require(outcomes.Single(item => item.RelativePath == "moved.txt") is
                { OperationId: null, Kind: SyncAuthorityPlannerOutcomeKind.Unchanged } &&
                outcomes.Single(item => item.RelativePath == "original.txt").OperationId is null,
            "the completed copy is not replayed, and changed current bytes do not receive a silently accepted baseline");
        foreach (var side in new[] { "local", "dropbox" })
            await ScanAsync(db, side, side == "local" ? localEntries.AsEnumerable().Reverse().ToArray() : remoteEntries.AsEnumerable().Reverse().ToArray());
        Require((await LatestAsync(db, "local")).Single(item => item.RelativePath == "original.txt").ObjectId == separated &&
                (await LatestAsync(db, "dropbox")).Single(item => item.RelativePath == "original.txt").ObjectId == separated,
            "ordinary repeat/reordered inventories preserve the repaired identities without another recovery command");
        await db.CreateOperationAsync(new("uncertain-copy", "uncertain-key", SyncAuthorityOperationKind.CreateLocal,
            "pair", objectId, 1, "profile", "{}", 0,
            [new("uncertain-source", "pair", objectId, "source", "dropbox-root", newRemote.MembershipId,
                newRemote.ObjectVersionId, newRemote.MembershipGeneration, "moved.txt", "{}")], [], []), admission: admission);
        var uncertainLease = await db.AcquireOperationLeaseAsync("uncertain-copy", "fixture", TimeSpan.FromMinutes(1));
        await db.StartDispatchedAttemptAsync("uncertain-copy", uncertainLease, new string('e', 64), "download");
        await db.ReleaseLeaseAsync(uncertainLease);
        refused = false;
        try { await db.VerifyIdentityRecoveryInventoriesAsync(localScan, remoteScan, localEntries, remoteEntries, [proof], default); }
        catch (InvalidDataException) { refused = true; }
        Require(refused && (await db.LoadOperationAsync("uncertain-copy"))!.Attempts.Count == 1,
            "a sent request with no verified outcome blocks explicit recovery and retains its exact attempt");
        Console.WriteLine("PASS explicit identity recovery: completed receipts, fresh both-side physical identities, unfinished rejection, history preservation and normal repeat");


        async Task<SyncAuthorityObservedEntry> SeedLegacyAsync(string side, SyncAuthorityObservedEntry older,
            SyncAuthorityObservedEntry newer)
        {
            var scan = await db.BeginScanGenerationAsync("pair", side + "-root", side, "manual", "{}");
            var member = side + "-legacy-new";
            await db.RegisterMembershipAsync(member, objectId, side + "-root", "moved.txt", 1);
            await db.AppendObservationAsync(scan, side + "-legacy-old", older.ObjectVersionId, older.MembershipId, "{}");
            await db.AppendObservationAsync(scan, side + "-legacy-new", newer.ObjectVersionId, member, "{}");
            // Only fixture input reproduces the legacy invalid publication.
            await using var connection = new SqliteConnection("Data Source=" + db.DatabasePath);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE scan_generation SET status='complete',completed_utc=$utc,entry_count=2 WHERE scan_generation_id=$scan; UPDATE observation SET complete_generation=1 WHERE scan_generation_id=$scan;";
            command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$scan", scan.ScanGenerationId);
            await command.ExecuteNonQueryAsync();
            return (await LatestAsync(db, side)).Single(item => item.RelativePath == "moved.txt");
        }

        async Task<SyncAuthoritySynchronizationPoint> CompleteCopyAsync(string operation, string point,
            SyncAuthorityObservedEntry local, SyncAuthorityObservedEntry remote, string path)
        {
            await db.CreateOperationAsync(new(operation, operation + "-key", SyncAuthorityOperationKind.CreateLocal,
                "pair", objectId, 1, "profile", "{}", 0,
                [new(operation + "-source", "pair", objectId, "source", "dropbox-root", remote.MembershipId,
                    remote.ObjectVersionId, remote.MembershipGeneration, path, "{}"),
                 new(operation + "-destination", "pair", objectId, "destination", "local-root",
                    null, null, null, path, "{\"present\":false}")], [], []), admission: admission);
            var lease = await db.AcquireOperationLeaseAsync(operation, "fixture", TimeSpan.FromMinutes(1));
            var attempt = await db.StartDispatchedAttemptAsync(operation, lease, new string('b', 64), "download");
            var version = await db.LoadObjectVersionAsync(local.ObjectVersionId);
            await db.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.KnownSuccess,
                (await db.LoadObjectVersionAsync(remote.ObjectVersionId)).IdentityJson,
                JsonSerializer.Serialize(new { path, identity = version.IdentityJson }), "{}", new string('c', 64));
            await db.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, point, "pair", objectId,
                local.ObjectVersionId, remote.ObjectVersionId, local.MembershipId, remote.MembershipId, "{}");
            await db.CompleteOperationAsync(lease, "{}");
            await db.ReleaseLeaseAsync(lease);
            return (await db.LoadLatestSynchronizationPointsAsync("pair")).Single();
        }

        async Task CheckCoordinatorReplacementAsync()
        {
            using var held = PilotDirectoryLease.Open(filesRoot!);
            var pair = new SyncPair("pair", "Coordinator replacement", filesRoot!,
                held.Identity.VolumeSerialNumber, held.Identity.FileId, "/Fixture", "id:root");
            var remote = new FakeSyncRemoteStore();
            remote.PutWithId("original.txt", "Newer authoritative cloud file bytes\n", "dropbox-replaced");
            remote.PutWithId("moved.txt", "Verified archive bytes\n", "dropbox-new");
            remote.PutWithId("elsewhere.txt", "Previous live file bytes\n", "dropbox-old");
            const string commandId = "coordinator-replacement-command";
            await db.CreateCommandRequestAsync(commandId, "run_pair", "pair", null, null, "{}");
            await db.TransitionCommandRequestAsync(commandId, "accepted");
            await db.TransitionCommandRequestAsync(commandId, "executing");
            SyncAuthorityObservationCoordinator Recovery(string? boundCommand = null) => new(pair, remote, db, "local-root", "dropbox-root")
            {
                CompletedCopyIdentityRecovery = [proof], IdentityRecoveryCommandId = boundCommand ?? commandId,
                IdentityRecoveryResultJson = "{\"firstSyncScanComplete\":true}"
            };
            var priorLocalScan = await db.LoadLatestCompleteScanIdAsync("pair", "local");
            var priorRemoteScan = await db.LoadLatestCompleteScanIdAsync("pair", "dropbox");
            var blocked = false;
            try { await Recovery().ObserveAsync("manual"); }
            catch (IOException error) { blocked = error.Message.Contains("older cloud identity is still present elsewhere"); }
            Require(blocked && await db.LoadLatestCompleteScanIdAsync("pair", "local") == priorLocalScan &&
                    await db.LoadLatestCompleteScanIdAsync("pair", "dropbox") == priorRemoteScan,
                "real coordinator refuses a replacement interpretation while the old cloud object is still present elsewhere; neither endpoint is published");
            // Provider input only: reproduce the observed deleted old cloud ID.
            // Inventory conversion, native local scanner and database admission
            // remain the actual production paths under test.
            remote = new FakeSyncRemoteStore();
            remote.PutWithId("original.txt", "Newer authoritative cloud file bytes\n", "dropbox-replaced");
            remote.PutWithId("moved.txt", "Verified archive bytes\n", "dropbox-new");
            var observed = await Recovery().ObserveAsync("manual");
            var localNow = await LatestAsync(db, "local");
            var remoteNow = await LatestAsync(db, "dropbox");
            var separated = localNow.Single(item => item.RelativePath == "original.txt").ObjectId;
            Require(observed.LocalEntries == 2 && observed.DropboxEntries == 2 && separated != objectId &&
                    remoteNow.Single(item => item.RelativePath == "original.txt").ObjectId == separated &&
                    localNow.Single(item => item.RelativePath == "moved.txt").ObjectId == objectId &&
                    remoteNow.Single(item => item.RelativePath == "moved.txt").ObjectId == objectId,
                "real coordinator maps a genuinely replaced live cloud file and its unchanged local predecessor separately from the exact retained archive");
            Require((await db.LoadLatestSynchronizationPointsAsync("pair")).SequenceEqual(pointsBefore) &&
                    (await db.LoadOperationAsync("prior-copy"))!.Attempts.Count == 1 &&
                    (await db.LoadOperationAsync("current-copy"))!.Attempts.Count == 1 &&
                    localNow.Single(item => item.RelativePath == "original.txt").ContentHash !=
                    remoteNow.Single(item => item.RelativePath == "original.txt").ContentHash &&
                    remote.UploadCount + remote.DownloadCount + remote.DeleteCount + remote.MoveCount == 0,
                "replacement identity recovery retains both historical accepted copies and current differing bytes without accepting a new baseline or issuing any file effect");
            await new SyncAuthorityObservationCoordinator(pair, remote, db, "local-root", "dropbox-root")
                .ObserveAsync("manual");
            Require((await LatestAsync(db, "local")).Single(item => item.RelativePath == "original.txt").ObjectId == separated &&
                    (await LatestAsync(db, "dropbox")).Single(item => item.RelativePath == "original.txt").ObjectId == separated,
                "ordinary real coordinator rescan preserves the replacement mapping without explicit recovery or repeated copy");

            // This is a DIFFERENT logical object from the original conflated
            // component. Its attempt cannot trip that component's earlier guard;
            // the replacement-provider-ID authority query must reject it itself.
            var unrelated = (await LatestAsync(db, "dropbox")).Single(item => item.RelativePath == "original.txt");
            Require(unrelated.ObjectId != objectId, "replacement authority fixture is independent of the original logical object");
            await db.CreateOperationAsync(new("replacement-held-copy", "replacement-held-key",
                SyncAuthorityOperationKind.CreateLocal, "pair", unrelated.ObjectId, 1, "profile", "{}", 0,
                [new("replacement-held-source", "pair", unrelated.ObjectId, "source", "dropbox-root",
                    unrelated.MembershipId, unrelated.ObjectVersionId, unrelated.MembershipGeneration,
                    "original.txt", "{}")], [], []), admission: admission);
            var replacementLease = await db.AcquireOperationLeaseAsync("replacement-held-copy", "fixture", TimeSpan.FromMinutes(1));
            await db.StartDispatchedAttemptAsync("replacement-held-copy", replacementLease, new string('f', 64), "download");
            await db.ReleaseLeaseAsync(replacementLease);
            const string rejectedCommand = "replacement-authority-rejection-command";
            await db.CreateCommandRequestAsync(rejectedCommand, "run_pair", "pair", null, null, "{}");
            await db.TransitionCommandRequestAsync(rejectedCommand, "accepted");
            await db.TransitionCommandRequestAsync(rejectedCommand, "executing");
            var safeLocalScan = await db.LoadLatestCompleteScanIdAsync("pair", "local");
            var safeRemoteScan = await db.LoadLatestCompleteScanIdAsync("pair", "dropbox");
            var heldBefore = await db.LoadOperationAsync("replacement-held-copy");
            blocked = false;
            try { await Recovery(rejectedCommand).ObserveAsync("manual"); }
            catch (IOException error) { blocked = error.Message.Contains("replacement cloud identity already has retained effect authority"); }
            var heldAfter = await db.LoadOperationAsync("replacement-held-copy");
            Require(blocked && await db.LoadLatestCompleteScanIdAsync("pair", "local") == safeLocalScan &&
                    await db.LoadLatestCompleteScanIdAsync("pair", "dropbox") == safeRemoteScan &&
                    (await db.LoadRunHistoryAsync()).Single(item => item.Id == rejectedCommand).Status == "executing",
                "real coordinator independently rejects replacement-ID effect authority on another logical object; neither endpoint nor recovery command is published");
            Require(JsonSerializer.Serialize(heldAfter) == JsonSerializer.Serialize(heldBefore) &&
                    (await db.LoadLatestSynchronizationPointsAsync("pair")).SequenceEqual(pointsBefore) &&
                    (await db.LoadOperationAsync("prior-copy"))!.Attempts.Count == 1 &&
                    (await db.LoadOperationAsync("current-copy"))!.Attempts.Count == 1 &&
                    remote.UploadCount + remote.DownloadCount + remote.DeleteCount + remote.MoveCount == 0,
                "replacement-authority rejection preserves the exact unrelated attempt, original accepted history and zero provider file effects");
        }
    }

    private static async Task ReservationFilteringAsync(string root)
    {
        var db = await DatabaseAsync(root);
        var admission = await AdmitFixturePairAsync(db);
        await db.RegisterObjectAsync("reservation-object", "file");
        SyncAuthorityOperationPlan Plan(string id, string path, string binding = "local-root") =>
            new(id, id + "-key", SyncAuthorityOperationKind.CreateLocal, "pair", "reservation-object", 1,
                "profile", "{}", 0,
                [new(id + "-member", "pair", "reservation-object", "destination", binding,
                    null, null, null, path, "{\"present\":false}")], [], []);
        var timer = Stopwatch.StartNew();
        for (var index = 0; index < 300; index++)
            await db.CreateOperationAsync(Plan("sibling-" + index, $"folder/item-{index:D4}.txt"), admission: admission);
        Console.WriteLine($"MEASURE reservation: 300 independent real operation reservations in {timer.ElapsedMilliseconds} ms");
        foreach (var (first, second) in new[] { ("Ascii.txt", "ASCII.TXT"), ("Änderung.txt", "änderung.txt") })
        {
            var id = "case-" + Guid.NewGuid().ToString("N");
            await db.CreateOperationAsync(Plan(id, first), admission: admission);
            var retainedCount = await db.CountOperationsOfKindForTestAsync(SyncAuthorityOperationKind.CreateLocal);
            var rejected = false;
            try { await db.CreateOperationAsync(Plan(id + "-collision", second), admission: admission); }
            catch (InvalidOperationException error) { rejected = error.Message.Contains("nonterminal operation reservation"); }
            Require(rejected, "reservation prefilter preserves ordinal case-insensitive collision rejection for " + first);
            Require(await db.CountOperationsOfKindForTestAsync(SyncAuthorityOperationKind.CreateLocal) == retainedCount,
                "a colliding reservation rolls back atomically without a phantom operation");
            await db.CreateOperationAsync(Plan(id + "-other-root", second, "dropbox-root"), admission: admission);
            Require((await db.LoadOperationAsync(id + "-other-root"))?.Status == SyncAuthorityOperationStatus.Ready,
                "the same path under a different exact root remains independent");
        }
    }

    private static async Task CompleteExclusionCacheAsync(string root)
    {
        var db = await DatabaseAsync(root);
        var local = await ScanAsync(db, "local", []);
        var remote = await ScanAsync(db, "dropbox", [Entry("dropbox", "excluded.txt", "file")]);
        Task<SyncAuthorityPairObservationSet> Load(string remoteScan) =>
            db.LoadPlanningObservationSetAsync("pair", 1, "local-root", "dropbox-root",
                local.ScanGenerationId, remoteScan);
        var digestBuilds = 0;
        var priorTiming = SyncAuthorityDatabase.TimingForTest;
        SyncAuthorityDatabase.TimingForTest = (phase, elapsed) =>
        {
            if (phase == "planning-state-fingerprint") digestBuilds++;
            priorTiming?.Invoke(phase, elapsed);
        };
        SyncAuthorityPairObservationSet first;
        try
        {
            first = await Load(remote.ScanGenerationId);
            for (var i = 0; i < 100; i++)
                Require(await db.IsPlanningAuthorityCurrentAsync(first.Bind("same-cut")),
                    "unchanged exact inputs remain current on repeated reservation checks");
            Require(digestBuilds == 1,
                "100 current-authority reads do not re-sort or hash the immutable exclusion inventory");
            Require(!await db.IsPlanningAuthorityCurrentAsync(first.Bind("forged-cut") with
                { PlanningStateFingerprint = new string('0', 64) }),
                "a cached legitimate digest never accepts a substituted fingerprint");
        }
        finally { SyncAuthorityDatabase.TimingForTest = priorTiming; }
        var firstAuthority = first.Bind("first-cut");
        Require(first.ExcludedPaths.Count == 0 && await db.IsPlanningAuthorityCurrentAsync(firstAuthority),
            "the first complete inventory's cached exclusion set retains valid planning authority");
        var changed = await db.BeginScanGenerationAsync("pair", "dropbox-root", "dropbox", "manual", "{}");
        await db.AppendObservationEvidenceBatchAsync(changed,
            [("excluded-evidence", "{\"path\":\"excluded.txt\",\"state\":\"selective_sync_excluded\"}")]);
        Require(await db.IsPlanningAuthorityCurrentAsync(firstAuthority),
            "an incomplete successor cannot replace the complete exclusion authority");
        await db.CompleteScanGenerationAsync(changed);
        Require(!await db.IsPlanningAuthorityCurrentAsync(firstAuthority),
            "a newly completed scan invalidates the old cached planning cut");
        var second = await Load(changed.ScanGenerationId);
        Require(second.ExcludedPaths.SetEquals(["excluded.txt"]) &&
                await db.IsPlanningAuthorityCurrentAsync(second.Bind("second-cut")),
            "new selective-sync exclusions are read from the new complete generation");
        await db.SetPairStateAsync("pair", "paused");
        Require(!await db.IsPlanningAuthorityCurrentAsync(second.Bind("second-cut")),
            "cached immutable exclusions never hide a current mutable pair-state change");
    }

    private static async Task LargeInventoryAsync(string root)
    {
        var db = await DatabaseAsync(root);
        var entries = Enumerable.Range(0, 1200).Select(index => Entry("dropbox", $"item-{index:D4}.txt", "id-" + index)).ToArray();
        var checks = new Dictionary<string, int>(StringComparer.Ordinal);
        SyncAuthorityDatabase.TimingForTest = (phase, _) => checks[phase] = checks.GetValueOrDefault(phase) + 1;
        try { await ScanAsync(db, "dropbox", entries); }
        finally { SyncAuthorityDatabase.TimingForTest = null; }
        var original = await LatestAsync(db, "dropbox");
        Require(!checks.ContainsKey("observation-object-collision-check") &&
                !checks.ContainsKey("observation-version-collision-check"),
            "1200 confirmed fresh object/version inserts need no redundant collision lookup");
        checks.Clear();
        var scan = await db.BeginScanGenerationAsync("pair", "dropbox-root", "dropbox", "manual", "{}");
        var committed = new List<int>();
        var timer = Stopwatch.StartNew();
        SyncAuthorityDatabase.TimingForTest = (phase, _) => checks[phase] = checks.GetValueOrDefault(phase) + 1;
        try { await db.AppendObservationEntriesAsync(scan, entries, onProgress: item =>
        {
            if (item.Detail.StartsWith("Saved ", StringComparison.Ordinal)) committed.Add(item.CompletedChecks);
            return Task.CompletedTask;
        }); }
        finally { SyncAuthorityDatabase.TimingForTest = null; }
        await db.CompleteScanGenerationAsync(scan);
        Require(checks.GetValueOrDefault("observation-object-collision-check") == entries.Length &&
                checks.GetValueOrDefault("observation-version-collision-check") == entries.Length,
            "Every ignored object/version insert still executes the exact retained-evidence check");
        var repeated = await LatestAsync(db, "dropbox");
        Require(original.Zip(repeated).All(items => items.First.ObjectId == items.Second.ObjectId &&
                    items.First.ObjectVersionId == items.Second.ObjectVersionId) && repeated.Count == entries.Length,
            "A rescan retains every original logical identity and exact version across metadata batches");
        Require(committed.SequenceEqual(Enumerable.Range(1, entries.Length)),
            "every file produces a committed metadata event with no missing rows");
        Console.WriteLine($"MEASURE inventory: {entries.Length} existing identities and writes in {timer.ElapsedMilliseconds} ms");

        var batched = await db.BeginScanGenerationAsync("pair", "dropbox-root", "dropbox", "manual", "{}");
        var saved = new List<int>();
        var writes = 0; var pending = 0; var maximumPending = 0;
        SyncAuthorityDatabase.TimingForTest = (phase, _) => { if (phase == "write-body") writes++; };
        var watch = Stopwatch.StartNew();
        try
        {
            await db.AppendObservationEntriesAsync(batched, entries, CancellationToken.None,
                async item =>
                {
                    if (!item.Detail.StartsWith("Saved ", StringComparison.Ordinal)) return;
                    saved.Add(item.CompletedChecks);
                    var admitted = Interlocked.Increment(ref pending);
                    int observed;
                    do { observed = Volatile.Read(ref maximumPending); }
                    while (admitted > observed && Interlocked.CompareExchange(ref maximumPending, admitted, observed) != observed);
                    await Task.Yield(); Interlocked.Decrement(ref pending);
                }, "fixture endpoint", concurrentProgressRetention: true);
        }
        finally { SyncAuthorityDatabase.TimingForTest = null; }
        Require(writes == 3,
            "Trusted 1200-row publication uses identity admission plus two bounded metadata transactions, not ten row-chunk commits");
        Require(saved.SequenceEqual(Enumerable.Range(1, entries.Length)) && pending == 0 && maximumPending <= 128,
            "Larger metadata commits preserve every exact saved-row notification and drain at most128 deliveries before advancing");
        Require(await db.LoadLatestCompleteScanIdAsync("pair", "dropbox") == scan.ScanGenerationId,
            "Committed trusted metadata remains incomplete and cannot replace previous complete authority before publication");
        await db.CompleteScanGenerationAsync(batched);
        var batchedRows = await LatestAsync(db, "dropbox");
        Require(repeated.Zip(batchedRows).All(items => items.First.ObjectId == items.Second.ObjectId &&
                    items.First.ObjectVersionId == items.Second.ObjectVersionId) && batchedRows.Count == entries.Length,
            "Trusted metadata batching preserves the exact retained logical objects and versions across the1024-row boundary");
        Console.WriteLine($"MEASURE trusted inventory: {entries.Length} existing identities and writes in {watch.ElapsedMilliseconds} ms; metadata transactions={writes - 1}");

        var interrupted = await db.BeginScanGenerationAsync("pair", "dropbox-root", "dropbox", "manual", "{}");
        using var cancellation = new CancellationTokenSource();
        var interruptedSaved = new List<int>();
        var interruptedPending = 0;
        var cancelled = false;
        try
        {
            await db.AppendObservationEntriesAsync(interrupted, entries, cancellation.Token,
                async item =>
                {
                    if (!item.Detail.StartsWith("Saved ", StringComparison.Ordinal)) return;
                    interruptedSaved.Add(item.CompletedChecks); Interlocked.Increment(ref interruptedPending);
                    if (item.CompletedChecks == 1) cancellation.Cancel();
                    // Callbacks are outside the writer and may reenter the real database.
                    if (item.CompletedChecks == 1)
                        Require(await db.LoadLatestCompleteScanIdAsync("pair", "dropbox") == batched.ScanGenerationId,
                            "Trusted saved-row callback reenters the database without publishing the partial successor");
                    await Task.Yield(); Interlocked.Decrement(ref interruptedPending);
                }, "fixture endpoint", concurrentProgressRetention: true);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && interruptedPending == 0 && interruptedSaved.SequenceEqual(Enumerable.Range(1, 1024)),
            "Cancellation drains every saved-row notification for the committed bounded prefix and refuses the next metadata transaction");
        Require(await db.LoadLatestCompleteScanIdAsync("pair", "dropbox") == batched.ScanGenerationId,
            "Cancelled trusted publication cannot replace the last completed inventory or grant new planning authority");

        var late = await db.BeginScanGenerationAsync("pair", "dropbox-root", "dropbox", "manual", "{}");
        var lateEntries = Enumerable.Range(0, 1024)
            .Select(index => Entry("dropbox", $"a-fresh-{index:D4}.txt", "fresh-" + index))
            .Append(Entry("dropbox", "z-new.txt", "later-fresh"))
            .Append(entries[^1] with { RelativePath = "zz-late-collision.txt", ObjectKind = "directory" })
            .ToArray();
        var lateRejected = false;
        try { await db.AppendObservationEntriesAsync(late, lateEntries, CancellationToken.None,
            _ => Task.CompletedTask, "fixture endpoint", concurrentProgressRetention: true); }
        catch (InvalidOperationException error) { lateRejected = error.Message.Contains("collides", StringComparison.Ordinal); }
        await using var read = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = db.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await read.OpenAsync();
        using var query = read.CreateCommand();
        query.CommandText = "SELECT (SELECT count(*) FROM observation WHERE scan_generation_id=$scan), " +
            "(SELECT count(*) FROM membership WHERE relative_path_key IN ('z-new.txt','zz-late-collision.txt'));";
        query.Parameters.AddWithValue("$scan", late.ScanGenerationId);
        await using var reader = await query.ExecuteReaderAsync();
        Require(await reader.ReadAsync() && lateRejected && reader.GetInt64(0) == 1024 && reader.GetInt64(1) == 0,
            "A late collision retains only the earlier incomplete prefix and atomically rolls back all fresh rows of the rejected metadata transaction");
        Require(await db.LoadLatestCompleteScanIdAsync("pair", "dropbox") == batched.ScanGenerationId,
            "Late trusted-batch collision preserves prior complete authority and cannot publish the retained incomplete prefix");
    }

    private static async Task InsertCollisionRejectionAsync(string root)
    {
        foreach (var concurrent in new[] { false, true })
        foreach (var scenario in new[] { "wrong-kind", "secondary-unique-version", "null-zero-size" })
        {
            var db = await DatabaseAsync(Path.Combine(root, $"trusted-{concurrent}", scenario));
            var baseline = Entry("dropbox", "retained.txt", "retained");
            if (scenario == "null-zero-size") baseline = baseline with { Size = null };
            var complete = await ScanAsync(db, "dropbox", [baseline]);
            var original = (await LatestAsync(db, "dropbox")).Single();
            var changed = scenario switch
            {
                "wrong-kind" => baseline with { ObjectKind = "directory" },
                "secondary-unique-version" => baseline with { ContentHash = new string('b', 64) },
                _ => baseline with { Size = 0 }
            };
            var scan = await db.BeginScanGenerationAsync("pair", "dropbox-root", "dropbox", "manual", "{}");
            var rejected = false;
            try { await db.AppendObservationEntriesAsync(scan, [Entry("dropbox", "a-new.txt", "new"), changed],
                CancellationToken.None, null, "fixture endpoint", concurrent); }
            catch (InvalidOperationException error) { rejected = error.Message.Contains("collides", StringComparison.Ordinal); }
            await using var read = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = db.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            await read.OpenAsync();
            using var query = read.CreateCommand();
            query.CommandText = "SELECT (SELECT count(*) FROM observation WHERE scan_generation_id=$scan) + " +
                "(SELECT count(*) FROM membership WHERE relative_path_key='a-new.txt') + " +
                "(SELECT count(*) FROM logical_object)-1 + (SELECT count(*) FROM object_version)-1;";
            query.Parameters.AddWithValue("$scan", scan.ScanGenerationId);
            Require(rejected && Convert.ToInt64(await query.ExecuteScalarAsync()) == 0,
                $"trusted-{concurrent}/" + scenario + ": exact collision rejection rolls back every earlier fresh logical object, version, membership and observation");
            var retained = (await LatestAsync(db, "dropbox")).Single();
            Require(await db.LoadLatestCompleteScanIdAsync("pair", "dropbox") == complete.ScanGenerationId &&
                    retained.ObjectId == original.ObjectId && retained.ObjectVersionId == original.ObjectVersionId,
                scenario + ": an incomplete rejected scan cannot replace previous complete authority");
        }
    }

    private static async Task<SyncAuthorityCommandAdmission> AdmitFixturePairAsync(SyncAuthorityDatabase db)
    {
        var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", "pair", null, null,
            "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        return admission;
    }

    private static async Task<SyncAuthorityDatabase> DatabaseAsync(string root, string? nativeLocalRoot = null)
    {
        Directory.CreateDirectory(root);
        var db = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        await db.InitializeAsync("installation", "profile", "observation-check", "{}");
        await db.RegisterPairAsync("pair", 1);
        using var nativeRoot = nativeLocalRoot is null ? null : PilotDirectoryLease.Open(nativeLocalRoot);
        var rootIdentity = nativeRoot is null ? "{\"volumeSerial\":\"00000001\",\"fileId\":\"root\"}"
            : JsonSerializer.Serialize(new { volumeSerial = nativeRoot.Identity.VolumeSerialNumber.ToString("x8"),
                fileId = nativeRoot.Identity.FileId.ToString("x16") });
        await db.RegisterRootBindingAsync("local-root", "pair", "local", 1, rootIdentity, nativeLocalRoot ?? root);
        await db.RegisterRootBindingAsync("dropbox-root", "pair", "dropbox", 1,
            "{\"folderId\":\"id:root\"}", "/Fixture");
        return db;
    }

    private static async Task<SyncAuthorityScanGeneration> ScanAsync(SyncAuthorityDatabase db, string side,
        IReadOnlyList<SyncAuthorityObservationInput> entries)
    {
        var scan = await db.BeginScanGenerationAsync("pair", side + "-root", side, "manual", "{}");
        await db.AppendObservationEntriesAsync(scan, entries);
        await db.CompleteScanGenerationAsync(scan);
        return scan;
    }

    private static async Task<IReadOnlyList<SyncAuthorityObservedEntry>> LatestAsync(
        SyncAuthorityDatabase db, string side) => await db.LoadCompleteObservationAsync("pair", side,
        await db.LoadLatestCompleteScanIdAsync("pair", side) ?? throw new InvalidDataException("Missing complete fixture scan."));

    private static SyncAuthorityObservationInput Entry(string side, string path, string fileId) =>
        new(path, "file", side == "local"
                ? JsonSerializer.Serialize(new { volumeSerial = "00000001", fileId, lastWriteUtc = "2026-09-01T00:00:00Z" })
                : JsonSerializer.Serialize(new { fileId, revision = "1" }),
            new string('a', 64), 10, JsonSerializer.Serialize(new { path, kind = "file" }));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        Console.WriteLine("PASS " + message);
    }
}
