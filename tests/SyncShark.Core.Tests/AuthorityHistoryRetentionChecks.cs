using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using Rig = CloudPairHostBindingChecks.HostRig;
using Wire = CloudPairHostBindingChecks.HostWire;

// Original M7/G4. Source-only authoring: this file is NOT evidence of an executed PASS.
// Every selection/hold/expiry is the real public Host + production SQLite implementation.
// Timestamp edits below transform only isolated, actually produced input records. They never
// supply an eligibility verdict, delete a row, change status, or manufacture successful effects.
// The input transaction restores every update guard before production maintenance is called.
[SupportedOSPlatform("windows")]
internal static class AuthorityHistoryRetentionChecks
{
    private const int Ceiling = 100_000;
    private const int InspectionRows = 120_000;
    private const long ValueBytes = 32L * 1024 * 1024;
    private static readonly DateTimeOffset Old = DateTimeOffset.UtcNow.AddDays(-120);
    private static int _assertions;
    private static readonly HashSet<string> Covered = new(StringComparer.Ordinal);
    private static readonly string[] Required =
    [
        "whole-graph-soft-target", "explicit-ceiling-refusal", "invalid-ceiling", "finite-graph-load-admission",
        "cold-reopen-repeat-high-water", "origin-baseline-public-planner",
        "rollback-cancellation-exact-guards", "malformed-unclassified-hold",
        "actual-run-maintenance-admission", "actual-recovery-unknown-reopen"
    ];

    internal static async Task RunAsync(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidDataException("Use a fresh isolated authority-history fixture root.");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("No reparse fixture root.");
        _assertions = 0; Covered.Clear();
        CloudPairHostBindingChecks.UseFixtureRoot(root);
        // Finite functional proof, not a performance run or available-memory bypass.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await WholeGraphAsync(Path.Combine(root, "whole-graph"), timeout.Token);
        await ConsumersAsync(Path.Combine(root, "consumers"), timeout.Token);
        await FaultsAsync(Path.Combine(root, "faults"), timeout.Token);
        await UnknownIdentityAsync(Path.Combine(root, "unknown-identity"), timeout.Token);
        await CloudDependenciesAsync(timeout.Token);
        Check(Covered.SetEquals(Required), "missing: " + string.Join(",", Required.Except(Covered)) +
            "; unexpected: " + string.Join(",", Covered.Except(Required)));
        Console.WriteLine("PASS M7/G4 prepared public history retention: " + _assertions +
            " assertions; required cases 10, covered cases 10, missing cases 0. " +
            "Presentation replay identities, permanent authority anchors, unclassified references and " +
            "unresolved historic receipts remain HOLDS, not purported general-history expiry. " +
            "No production activation, migration, VACUUM or live/provider proof.");
    }

    // Focused real-path regression for the changed admission consumer. The separate
    // whole-graph suite remains required evidence; this selector cannot stand in for it.
    internal static async Task RunAdmissionAndRecoveryAsync(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) || File.Exists(root))
            throw new InvalidDataException("Use a fresh isolated admission/recovery fixture root.");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("No reparse fixture root.");
        _assertions = 0;
        Covered.Clear();
        CloudPairHostBindingChecks.UseFixtureRoot(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await CloudDependenciesAsync(timeout.Token);
        Check(Covered.SetEquals(new[] { "actual-run-maintenance-admission", "actual-recovery-unknown-reopen" }),
            "Focused admission/recovery coverage is exact, not whole-graph expiry closure.");
        Console.WriteLine("PASS M7 focused actual admission/recovery: " + _assertions +
            " assertions; no production retention activation or throughput claim.");
    }

    private static async Task WholeGraphAsync(string root, CancellationToken token)
    {
        using var fixture = await Fixture.CreateAsync(root, token);
        // A complete middle inventory has 6,700 observations + memberships + superseded versions
        // (20,101 connected rows). First/origin and latest TWO complete cuts stay protected.
        var scans = await ProduceFourCutsAsync(fixture, 6_700, token);
        var before = await AuthoritySnapshotAsync(fixture.Db, token);
        var guards = await GuardManifestAsync(fixture.Db.DatabasePath, token);
        var materialized = 0;
        fixture.Db.HistoryRetentionBoundaryForTest = phase =>
        { if (phase == "graph-admitted-before-materialization") materialized++; };
        try
        {
            foreach (var readOnly in new[] { false, true })
            foreach (var rowBudget in new[] { false, true })
            {
                try
                {
                    if (readOnly) await fixture.Host.PlanAuthorityHistoryRetentionAsync(Ceiling,
                        rowBudget ? 1 : InspectionRows, rowBudget ? ValueBytes : 1, token);
                    else await fixture.Host.ExpireAuthorityHistoryAsync(Ceiling,
                        rowBudget ? 1 : InspectionRows, rowBudget ? ValueBytes : 1, token);
                    throw new InvalidDataException("Expected actual finite graph admission refusal.");
                }
                catch (SyncAuthorityHistoryRetentionAdmissionException error)
                {
                    Check(error.Reason == (rowBudget ? "graph_row_budget" : "graph_value_byte_budget") &&
                        error.InspectedRows > 0 && (rowBudget ? error.LoadedValueBytes is null : error.LoadedValueBytes > 1),
                        "actual public graph admission reports exact over-budget class before materialization");
                }
            }
        }
        finally { fixture.Db.HistoryRetentionBoundaryForTest = null; }
        Check(materialized == 0 && await AuthoritySnapshotAsync(fixture.Db, token) == before &&
            await GuardManifestAsync(fixture.Db.DatabasePath, token) == guards,
            "row AND value-byte over-budget refusals retain every row/guard, with no graph materialization");
        Done("finite-graph-load-admission");
        foreach (var ceiling in new[] { 0, -1 })
            await ThrowsAsync<ArgumentOutOfRangeException>(() =>
                fixture.Host.ExpireAuthorityHistoryAsync(ceiling, InspectionRows, ValueBytes, token), "invalid finite ceiling");
        await ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Host.ExpireAuthorityHistoryAsync(Ceiling,
            0, ValueBytes, token), "invalid graph row ceiling");
        await ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Host.PlanAuthorityHistoryRetentionAsync(Ceiling,
            InspectionRows, 0, token), "invalid graph value-byte ceiling");
        Check(await AuthoritySnapshotAsync(fixture.Db, token) == before, "invalid ceiling writes nothing");
        Done("invalid-ceiling");
        var refused = await fixture.Host.ExpireAuthorityHistoryAsync(1_000, InspectionRows, ValueBytes, token);
        Check(refused.SelectedRows == 0 &&
            refused.HeldGraphs.Any(hold => hold.Reason == "oversize_complete_graph" && hold.RowCount > 20_000) &&
            await CountAsync(fixture.Db.DatabasePath,
                "SELECT count(*) FROM observation WHERE scan_generation_id=$id;", token, ("$id", scans[1].ScanGenerationId)) == 6_700 &&
            await AuthoritySnapshotAsync(fixture.Db, token) == before,
            "hard ceiling retains the ENTIRE actually produced graph and reports exact oversize");
        Done("explicit-ceiling-refusal");
        var plan = await fixture.Host.PlanAuthorityHistoryRetentionAsync(Ceiling, InspectionRows, ValueBytes, token);
        Check(!plan.Applied && plan.InspectedRows > 0 && plan.InspectedRows <= InspectionRows &&
            plan.LoadedValueBytes > 0 && plan.LoadedValueBytes <= ValueBytes &&
            plan.SelectedRows > 20_000 && plan.SelectedRows <= Ceiling &&
            plan.RowsByTable.GetValueOrDefault("scan_generation") == 1 &&
            plan.RowsByTable.GetValueOrDefault("observation") == 6_700 &&
            plan.RowsByTable.GetValueOrDefault("membership") == 6_700 &&
            plan.RowsByTable.GetValueOrDefault("object_version") == 6_700 &&
            await AuthoritySnapshotAsync(fixture.Db, token) == before &&
            await GuardManifestAsync(fixture.Db.DatabasePath, token) == guards,
            "real read-only plan counts whole graph above soft target, without mutation");
        var applied = await fixture.Host.ExpireAuthorityHistoryAsync(Ceiling, InspectionRows, ValueBytes, token);
        Check(applied.Applied && applied.SelectedRows == plan.SelectedRows &&
            Serialize(applied.RowsByTable) == Serialize(plan.RowsByTable),
            "real apply uses counted complete graph within explicit ceiling");
        Check(await CountAsync(fixture.Db.DatabasePath, "SELECT count(*) FROM scan_generation WHERE scan_generation_id=$id;",
                  token, ("$id", scans[1].ScanGenerationId)) == 0 &&
            await CountAsync(fixture.Db.DatabasePath, "SELECT count(*) FROM observation WHERE scan_generation_id=$id;",
                  token, ("$id", scans[1].ScanGenerationId)) == 0,
            "obsolete complete header AND every child expire together");
        foreach (var keep in new[] { scans[0], scans[2], scans[3] })
            Check(await CountAsync(fixture.Db.DatabasePath, "SELECT count(*) FROM observation WHERE scan_generation_id=$id;",
                token, ("$id", keep.ScanGenerationId)) == 6_700,
                "equally old origins and latest-two complete cuts never become partial");
        Check(await GuardManifestAsync(fixture.Db.DatabasePath, token) == guards &&
            await ForeignKeyErrorsAsync(fixture.Db.DatabasePath, token) == 0,
            "successful expiry restores exact guards and valid foreign keys");
        Done("whole-graph-soft-target");
        var after = await AuthoritySnapshotAsync(fixture.Db, token);
        var sequence = await fixture.Db.LoadActivitySequenceAsync(token);
        var oldEvent = fixture.Event with { EventId = "old-replay", OccurredUtc = Old };
        await fixture.Db.AppendActivityEventAsync(oldEvent, token);
        await fixture.Host.ExpireAuthorityHistoryAsync(Ceiling, InspectionRows, ValueBytes, token);
        var replaySequence = await fixture.Db.LoadActivitySequenceAsync(token);
        await fixture.Db.AppendActivityEventAsync(oldEvent, token);
        await ThrowsAsync<InvalidDataException>(() => fixture.Db.AppendActivityEventAsync(
            oldEvent with { Message = "different payload" }, token), "retained old Activity collision");
        Check(await fixture.Db.LoadActivitySequenceAsync(token) == replaySequence && replaySequence > sequence,
            "old presentation identity replay/collision remains exact, not erased by audit expiry");
        await fixture.ReopenAsync(token);
        var repeat = await fixture.Host.ExpireAuthorityHistoryAsync(Ceiling, InspectionRows, ValueBytes, token);
        Check(repeat.SelectedRows == 0 &&
            await fixture.Db.LoadLatestCompleteScanIdAsync(fixture.Pair.Id, "local", token) == scans[3].ScanGenerationId &&
            await fixture.Db.HasTwoStableCompleteObservationsAsync(fixture.Pair.Id, "local", token),
            "cold SQLite/Host reopen retains same latest cuts; repeat is zero-effect");
        // after excludes the intentionally added old replay event; compare retained inventory separately.
        Check(await CountAsync(fixture.Db.DatabasePath, "SELECT count(*) FROM observation;", token) == 20_100 &&
            after.Length != 0, "reopened inventory count is exact, not just a healthy process");
        await fixture.Db.AppendActivityEventAsync(fixture.Event with { EventId = "after-expiry" }, token);
        Check(await fixture.Db.LoadActivitySequenceAsync(token) > replaySequence,
            "next public Activity sequence is strictly monotonic after expiry/reopen");
        var next = await fixture.Db.BeginScanGenerationAsync(fixture.Pair.Id, fixture.LocalRootId, "local", "manual", "{}", token);
        Check(next.Generation > scans.Max(scan => scan.Generation), "next real scan generation exceeds high water");
        await fixture.Db.AppendObservationEntriesAsync(next, Inputs(1, "now"), token);
        await fixture.Db.CompleteScanGenerationAsync(next, token);
        Check(await CountAsync(fixture.Db.DatabasePath,
            "SELECT max(generation) FROM membership WHERE root_binding_id=$root AND relative_path_key='file-00000.txt';",
            token, ("$root", fixture.LocalRootId)) == 5, "public membership allocator does not recycle a pruned generation");
        Done("cold-reopen-repeat-high-water");
    }

    private static async Task ConsumersAsync(string root, CancellationToken token)
    {
        using var fixture = await Fixture.CreateAsync(root, token);
        await CutAsync(fixture, "local", Inputs(1, "origin"), token);
        await CutAsync(fixture, "dropbox", RemoteInputs(), token);
        await BackdateProducedInputsAsync(fixture.Db.DatabasePath, token);
        // Actual initial adoption binds the two observed sides and produces current synchronization authority.
        var adopted = await new SyncAuthorityInitialPlanner(fixture.Db).AdoptAsync(fixture.Pair.Id, token);
        Check(adopted.Any(row => row.Kind == SyncDecisionKind.Baseline), "real public initial planner adopts matching bytes");
        foreach (var unused in Enumerable.Range(0, 2))
        {
            await CutAsync(fixture, "local", Inputs(1, "origin"), token);
            await CutAsync(fixture, "dropbox", RemoteInputs(), token);
        }
        var options = new SyncAuthorityPlannerOptions(1, "history-profile", "local-control", "cloud-control",
            EnableCreateEffects: false, EnableReplacementEffects: false, EnableDeletionEffects: false, EnableMoveEffects: false);
        var prior = await new SyncAuthorityReconciliationPlanner(fixture.Db, options).PlanAsync(fixture.Pair.Id, token);
        var inventory = await PlanningSnapshotAsync(fixture, token);
        var points = Serialize(await fixture.Db.LoadLatestSynchronizationPointsAsync(fixture.Pair.Id, token));
        var activity = Serialize(await fixture.Db.LoadActivityProjectionAsync(cancellationToken: token));
        var first = await TextAsync(fixture.Db.DatabasePath,
            "SELECT scan_generation_id FROM scan_generation WHERE side='local' ORDER BY generation LIMIT 1;", token);
        await fixture.Host.ExpireAuthorityHistoryAsync(Ceiling, InspectionRows, ValueBytes, token);
        await fixture.ReopenAsync(token);
        var current = await new SyncAuthorityReconciliationPlanner(fixture.Db, options).PlanAsync(fixture.Pair.Id, token);
        Check(Serialize(prior) == Serialize(current) &&
            inventory == await PlanningSnapshotAsync(fixture, token) &&
            points == Serialize(await fixture.Db.LoadLatestSynchronizationPointsAsync(fixture.Pair.Id, token)) &&
            activity == Serialize(await fixture.Db.LoadActivityProjectionAsync(cancellationToken: token)),
            "actual public planner, exact planning fingerprints/baselines and Activity stay identical after cold reopen");
        Check(await CountAsync(fixture.Db.DatabasePath,
            "SELECT count(*) FROM observation WHERE scan_generation_id=$id;", token, ("$id", first)) == 1 &&
            await fixture.Db.HasTwoStableCompleteObservationsAsync(fixture.Pair.Id, "local", token) &&
            await fixture.Db.HasTwoStableCompleteObservationsAsync(fixture.Pair.Id, "dropbox", token),
            "equally old original physical evidence remains with latest-two stable observations");
        Done("origin-baseline-public-planner");
    }

    private static async Task FaultsAsync(string root, CancellationToken token)
    {
        using var fixture = await Fixture.CreateAsync(root, token);
        await ProduceFourCutsAsync(fixture, 3, token);
        var before = await AuthoritySnapshotAsync(fixture.Db, token);
        var guards = await GuardManifestAsync(fixture.Db.DatabasePath, token);
        foreach (var phase in new[] { "delete-guards-suspended", "rows-deleted", "guards-restored-before-commit" })
        {
            var calls = 0;
            fixture.Db.HistoryRetentionBoundaryForTest = reached =>
            {
                if (reached != phase) return;
                calls++; throw new FixtureFault(phase);
            };
            try { await ThrowsAsync<FixtureFault>(() => fixture.Host.ExpireAuthorityHistoryAsync(Ceiling, InspectionRows, ValueBytes, token), phase); }
            finally { fixture.Db.HistoryRetentionBoundaryForTest = null; }
            Check(calls == 1 && await AuthoritySnapshotAsync(fixture.Db, token) == before &&
                await GuardManifestAsync(fixture.Db.DatabasePath, token) == guards &&
                await ForeignKeyErrorsAsync(fixture.Db.DatabasePath, token) == 0,
                "real transaction rolls back data AND exact trigger DDL at " + phase);
            await fixture.ReopenAsync(token);
            Check(await AuthoritySnapshotAsync(fixture.Db, token) == before,
                "failure rollback survives cold reopen at " + phase);
        }
        using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            var calls = 0;
            fixture.Db.HistoryRetentionBoundaryForTest = phase =>
            { if (phase == "delete-guards-suspended") { calls++; cancelled.Cancel(); } };
            try { await ThrowsAsync<OperationCanceledException>(() =>
                fixture.Host.ExpireAuthorityHistoryAsync(Ceiling, InspectionRows, ValueBytes, cancelled.Token), "cancel after guard suspension"); }
            finally { fixture.Db.HistoryRetentionBoundaryForTest = null; }
            Check(calls == 1 && await AuthoritySnapshotAsync(fixture.Db, token) == before &&
                await GuardManifestAsync(fixture.Db.DatabasePath, token) == guards, "cancellation cannot leave guards disabled");
        }
        await fixture.ReopenAsync(token);
        await ThrowsAsync<SqliteException>(() => ExecuteIsolatedAsync(fixture.Db.DatabasePath,
            "DELETE FROM observation WHERE rowid=(SELECT min(rowid) FROM observation);", token),
            "ordinary immutable observation deletion still refuses");
        Check(await AuthoritySnapshotAsync(fixture.Db, token) == before,
            "ordinary delete refusal does not partially mutate rows");
        var done = await fixture.Host.ExpireAuthorityHistoryAsync(Ceiling, InspectionRows, ValueBytes, token);
        Check(done.SelectedRows > 0 && await GuardManifestAsync(fixture.Db.DatabasePath, token) == guards,
            "after fault/cancel, same public gate permits legitimate committed expiry and releases admission");
        Done("rollback-cancellation-exact-guards");
    }

    private static async Task UnknownIdentityAsync(string root, CancellationToken token)
    {
        using var fixture = await Fixture.CreateAsync(root, token);
        var scans = await ProduceFourCutsAsync(fixture, 3, token);
        var version = await TextAsync(fixture.Db.DatabasePath,
            "SELECT object_version_id FROM observation WHERE scan_generation_id=$scan ORDER BY rowid LIMIT 1;",
            token, ("$scan", scans[1].ScanGenerationId));
        // Actual producer's isolated identity input is made unreadable; it never becomes an erase permission.
        await ExecuteIsolatedAsync(fixture.Db.DatabasePath,
            "UPDATE object_version SET identity_json='{' WHERE object_version_id=$id;", token, ("$id", version));
        var before = await AuthoritySnapshotAsync(fixture.Db, token);
        var retained = await fixture.Host.ExpireAuthorityHistoryAsync(Ceiling, InspectionRows, ValueBytes, token);
        Check(retained.SelectedRows == 0 && retained.KeptReasons.GetValueOrDefault("malformed_json") > 0 &&
            await AuthoritySnapshotAsync(fixture.Db, token) == before,
            "one malformed dependency retains entire old complete inventory, not just corrupt row");
        await fixture.ReopenAsync(token);
        Check((await fixture.Host.ExpireAuthorityHistoryAsync(Ceiling, InspectionRows, ValueBytes, token)).SelectedRows == 0 &&
            await AuthoritySnapshotAsync(fixture.Db, token) == before, "malformed graph remains protected after reopen");
        // Distinct valid JSON, unclassified authority reference: actual graph is held, not silently ignored.
        await ExecuteIsolatedAsync(fixture.Db.DatabasePath,
            "UPDATE object_version SET identity_json=$json WHERE object_version_id=$id;", token,
            ("$json", "{\"fileId\":\"middle-0\",\"volumeSerial\":\"fixture-volume\",\"futureAuthorityId\":\"unavailable-reference\"}"),
            ("$id", version));
        var unclassified = await fixture.Host.ExpireAuthorityHistoryAsync(Ceiling, InspectionRows, ValueBytes, token);
        Check(unclassified.SelectedRows == 0 &&
            unclassified.KeptReasons.GetValueOrDefault("unclassified_json_reference") > 0,
            "valid but unclassified JSON ID retains graph with exact reason");
        Done("malformed-unclassified-hold");
    }

    private static async Task CloudDependenciesAsync(CancellationToken token)
    {
        const string name = "authority-history-recovery";
        using var wire = new Wire();
        using var transport = new LostUpdateTransport(wire);
        Rig? rig = null;
        try
        {
            rig = await Rig.CreateAsync(name, wire, transport);
            await rig.Host.SaveTransferSettingsAsync(rig.Host.LoadTransferSettings() with { ResourceMode = "Performance" }, token);
            var left = CloudPairHostBindingChecks.HostDropbox(name + "-left", "/History Left");
            var right = CloudPairHostBindingChecks.HostDropbox(name + "-right", "/History Right");
            await rig.AddEndpointAsync(left); await rig.AddEndpointAsync(right);
            rig.SeedFile(left, "history.txt", "original"); rig.SeedFile(right, "history.txt", "original");
            var draft = SyncEndpointPairDraft.Create(new SyncPairEndpoints(
                SyncEndpoint.Cloud(left.Kind, left.ConnectionId, left.EndpointNamespaceId, left.FolderId, left.FolderPath),
                SyncEndpoint.Cloud(right.Kind, right.ConnectionId, right.EndpointNamespaceId, right.FolderId, right.FolderPath),
                SyncMode.TwoWay), name) with { RecoveryRetentionDays = 30 };
            var bound = await rig.Host.BindEndpointPairAsync(draft, cancellationToken: token);
            await rig.Host.RefreshBootstrapInventoryAsync(bound.Pair.Id, token);
            await rig.Host.ApproveBootstrapMergeAsync(bound.Pair.Id, token);
            var pair = (await rig.Host.SetPairEnabledAsync(bound.Pair.Id, true, token)).Pairs.Single(row => row.Id == bound.Pair.Id);
            await RequireRunSuccessAsync(rig, pair, token);
            await MaintenanceAdmissionAsync(rig, pair, token);
            rig.SeedFile(left, "history.txt", "completed successor");
            await RequireRunSuccessAsync(rig, pair, token);
            var artifacts = await rig.Db.LoadRecoveryArtifactsAsync(token);
            Check(artifacts.Any(row => row.PairId == pair.Id && row.Status == "retained") &&
                artifacts.All(row => row.PairId != pair.Id ||
                    SyncAuthorityCloudRecoveryDescriptor.TryParse(row.IdentityJson, out _)),
                "actual public producer completed an update and created real typed Recovery evidence");
            await BackdateProducedInputsAsync(rig.Db.DatabasePath, token);
            rig.SeedFile(left, "history.txt", "actually applied but reply lost");
            transport.LoseNextUpdate = true;
            using (var finite = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                finite.CancelAfter(TimeSpan.FromSeconds(30));
                var failed = await rig.Host.RunPairsAsync([pair], "M7 actual lost reply input", cancellationToken: finite.Token);
                Console.WriteLine(JsonSerializer.Serialize(new { phase = "actual lost reply", transport.LostReplies, failed }));
                Check(transport.LostReplies == 1 && failed.Items.Count == 1 &&
                    (failed.Items[0].FailureMessage is not null || failed.Items[0].Result is { Blocked: > 0 } or { NeedsVerification: > 0 }),
                    "actual adapter applied one conditional update and surfaced lost reply, not fabricated admission");
            }
            Check(await CountAsync(rig.Db.DatabasePath,
                "SELECT count(*) FROM effect_receipt WHERE certainty='unknown';", token) > 0 &&
                await CountAsync(rig.Db.DatabasePath,
                "SELECT count(*) FROM operation_attempt WHERE status IN ('request_sent','effect_unknown','started','response_received');", token) > 0,
                "real producer persisted unresolved attempt/receipt before pruning");
            await BackdateProducedInputsAsync(rig.Db.DatabasePath, token);
            var retainedCatalog = Serialize(await rig.Host.LoadRecoveryCatalogAsync(pair.Id, cancellationToken: token));
            var retainedAudit = await ProtectedAuditAsync(rig.Db.DatabasePath, token);
            var installation = await rig.Db.LoadInstallationIdAsync(token);
            var requests = wire.TotalRequests;
            var report = await rig.Host.ExpireAuthorityHistoryAsync(Ceiling, InspectionRows, ValueBytes, token);
            Check(wire.TotalRequests == requests &&
                report.KeptReasons.GetValueOrDefault("unresolved_or_live_authority") > 0 &&
                retainedAudit == await ProtectedAuditAsync(rig.Db.DatabasePath, token) &&
                retainedCatalog == Serialize(await rig.Host.LoadRecoveryCatalogAsync(pair.Id, cancellationToken: token)),
                "old retained Recovery AND unknown effects keep exact attempts/receipts/members/history with no provider dispatch");
            rig.Dispose(); rig = null;
            // Clear pooled handles for a genuinely new SQLite/Host object; keep same transport/provider input.
            SqliteConnection.ClearAllPools();
            rig = await Rig.CreateAsync(name, wire, transport);
            Check(await rig.Db.LoadInstallationIdAsync(token) == installation &&
                rig.Host.LoadTransferSettings().ResourceMode == "Performance" &&
                (await rig.Host.LoadPairsAsync(token)).Pairs.Any(row => row.Id == pair.Id),
                "same installation/pair and isolated functional mode survive cold reopen");
            var reopenedAudit = await ProtectedAuditAsync(rig.Db.DatabasePath, token);
            requests = wire.TotalRequests;
            await rig.Host.ExpireAuthorityHistoryAsync(Ceiling, InspectionRows, ValueBytes, token);
            Check(wire.TotalRequests == requests && transport.LostReplies == 1 &&
                reopenedAudit == await ProtectedAuditAsync(rig.Db.DatabasePath, token) &&
                retainedCatalog == Serialize(await rig.Host.LoadRecoveryCatalogAsync(pair.Id, cancellationToken: token)) &&
                await CountAsync(rig.Db.DatabasePath, "SELECT count(*) FROM effect_receipt WHERE certainty='unknown';", token) > 0,
                "reopen/repeat never erases unresolved evidence or reissues lost update/Recovery deletion");
            Done("actual-recovery-unknown-reopen");
        }
        finally { rig?.Dispose(); }
    }

    private static async Task MaintenanceAdmissionAsync(Rig rig, SyncPair pair, CancellationToken token)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var finite = CancellationTokenSource.CreateLinkedTokenSource(token);
        finite.CancelAfter(TimeSpan.FromSeconds(45));
        var run = rig.Host.RunPairsAsync([pair], "M7 maintenance admission", async progress =>
        {
            if (progress.Comparison?.Phase != "Preparing Command" || Interlocked.Exchange(ref calls, 1) != 0) return;
            entered.TrySetResult(true); await release.Task.WaitAsync(finite.Token);
        }, finite.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            var before = await ProtectedAuditAsync(rig.Db.DatabasePath, token);
            var requests = rig.Wire.TotalRequests;
            foreach (var plan in new[] { false, true })
                await ThrowsAsync<InvalidOperationException>(async () =>
                {
                    if (plan) await rig.Host.PlanAuthorityHistoryRetentionAsync(Ceiling, InspectionRows, ValueBytes, token);
                    else await rig.Host.ExpireAuthorityHistoryAsync(Ceiling, InspectionRows, ValueBytes, token);
                }, "Wait for the active sync to reach its checkpoint before changing a folder pair.", requireMessage: true);
            Check(before == await ProtectedAuditAsync(rig.Db.DatabasePath, token) && requests == rig.Wire.TotalRequests,
                "real public RunPairs preparation excludes BOTH maintenance APIs before row/provider changes");
        }
        finally { release.TrySetResult(true); }
        var completed = await run;
        Check(calls == 1 && completed.Items.Single().FailureMessage is null,
            "releasing actual preparation barrier permits legitimate run success");
        // Inverse direction: actual public run is attempted inside the real maintenance transaction.
        var inverse = 0;
        rig.Db.HistoryRetentionBoundaryForTest = phase =>
        {
            if (phase != "selected-before-delete-guards") return;
            inverse++;
            ThrowsAsync<InvalidOperationException>(() => rig.Host.RunPairsAsync([pair], "M7 inverse admission",
                cancellationToken: token), "Another sync command is already active.", requireMessage: true).GetAwaiter().GetResult();
        };
        // The completed baseline has no eligible old graph yet. Make a real independent old scan chain
        // under a second isolated public producer pair so the boundary MUST be reached, not false-covered.
        await rig.Db.RegisterPairAsync("history-admission-extra", 1, cancellationToken: token);
        await rig.Db.RegisterRootBindingAsync("history-admission-root", "history-admission-extra", "local", 1,
            "{\"volumeSerial\":\"fixture-volume\",\"fileId\":\"fixture-root\"}",
            Path.Combine(rig.StateRoot, "history-admission-input"), token);
        for (var index = 0; index < 4; index++)
        {
            var scan = await rig.Db.BeginScanGenerationAsync("history-admission-extra", "history-admission-root", "local", "manual", "{}", token);
            await rig.Db.AppendObservationEntriesAsync(scan, Inputs(1, index switch { 0 => "origin", 1 => "middle", _ => "now" }), token);
            await rig.Db.CompleteScanGenerationAsync(scan, token);
            if (index == 1) await BackdateProducedInputsAsync(rig.Db.DatabasePath, token);
        }
        try { await rig.Host.ExpireAuthorityHistoryAsync(Ceiling, InspectionRows, ValueBytes, token); }
        finally { rig.Db.HistoryRetentionBoundaryForTest = null; }
        Check(inverse == 1, "real maintenance holds same Host admission until transactional completion");
        Done("actual-run-maintenance-admission");
    }

    private static async Task RequireRunSuccessAsync(Rig rig, SyncPair pair, CancellationToken token)
    {
        using var finite = CancellationTokenSource.CreateLinkedTokenSource(token);
        finite.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await rig.Host.RunPairsAsync([pair], "M7 producer", cancellationToken: finite.Token);
        Check(result.Items.Count == 1 && result.Items[0].FailureMessage is null &&
            result.Items[0].Result is { Blocked: 0, Conflicts: 0 }, "actual public producer must reach completed success");
    }

    private sealed class LostUpdateTransport(Wire wire) : HttpMessageHandler
    {
        internal bool LoseNextUpdate;
        internal int LostReplies;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var path = request.RequestUri?.AbsolutePath ?? throw new InvalidDataException("Missing fixture URI.");
            var lose = false;
            if (LoseNextUpdate && request.Method == HttpMethod.Post && path.EndsWith("/files/upload", StringComparison.Ordinal) &&
                request.Headers.TryGetValues("Dropbox-API-Arg", out var values))
            {
                using var document = JsonDocument.Parse(values.Single());
                var body = document.RootElement;
                lose = body.GetProperty("path").GetString()?.EndsWith("/history.txt", StringComparison.Ordinal) == true &&
                    body.GetProperty("mode").ValueKind == JsonValueKind.Object &&
                    body.GetProperty("mode").GetProperty(".tag").GetString() == "update";
            }
            var response = await wire.SendDirectAsync(request);
            if (!lose) return response;
            Check(response.IsSuccessStatusCode, "lost-reply input follows successful actual fixture-provider mutation");
            LoseNextUpdate = false; LostReplies++; response.Dispose();
            throw new HttpRequestException("Isolated response loss after real fixture conditional update.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private PilotInstanceLock? _instance;
        private HttpClient? _http;
        internal SyncAuthorityDatabase Db { get; private set; } = null!;
        internal SyncAuthorityRuntimeHost Host { get; private set; } = null!;
        internal SyncPair Pair { get; private set; } = null!;
        internal string LocalRootId => "history-local";
        internal string CloudRootId => "history-cloud";
        internal SyncActivityEvent Event => new("history-anchor", DateTimeOffset.UtcNow,
            [SyncActivityCategories.Diagnostics], "Information", "Application", "retention fixture", "retained sequence");
        private Fixture(string root) { _root = root; }

        internal static async Task<Fixture> CreateAsync(string root, CancellationToken token)
        {
            Directory.CreateDirectory(root);
            var local = Path.Combine(root, "local"); Directory.CreateDirectory(local);
            using var directory = PilotDirectoryLease.Open(local);
            var fixture = new Fixture(root);
            try
            {
                fixture.Db = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
                await fixture.Db.InitializeAsync("history-install", "history-profile", "owner-fixture", "{}", token);
                fixture.Pair = new SyncPair("history-pair", "History", local, directory.Identity.VolumeSerialNumber,
                    directory.Identity.FileId, "/History", "id:history-cloud");
                await fixture.Db.RegisterPairAsync(fixture.Pair.Id, 1, cancellationToken: token);
                await fixture.Db.RetainPairDefinitionAsync(fixture.Pair, true, token);
                await fixture.Db.RegisterRootBindingAsync(fixture.LocalRootId, fixture.Pair.Id, "local", 1,
                    JsonSerializer.Serialize(directory.Identity), local, token);
                await fixture.Db.RegisterRootBindingAsync(fixture.CloudRootId, fixture.Pair.Id, "dropbox", 1,
                    "{\"folderId\":\"id:history-cloud\"}", fixture.Pair.DropboxRoot, token);
                await fixture.Db.AppendActivityEventAsync(fixture.Event, token);
                fixture.OpenHost();
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        private void OpenHost()
        {
            _instance = PilotInstanceLock.Acquire(Path.Combine(_root, "state", "instance.lock"));
            _http = new HttpClient(new RejectNetwork());
            Host = new SyncAuthorityRuntimeHost(_instance.StateAuthority, _http,
                new SyncPairStore(_instance.StateAuthority), new SyncRuntimeStateStore(_instance.StateAuthority),
                new DropboxConnectionStore(_instance.StateAuthority), new NoTokens(), authorityDatabase: Db);
        }
        internal async Task ReopenAsync(CancellationToken token)
        {
            Host.Dispose(); _http!.Dispose(); _instance!.Dispose();
            _instance = null; _http = null; SqliteConnection.ClearAllPools();
            Db = new SyncAuthorityDatabase(Db.DatabasePath);
            Check(await Db.LoadInstallationIdAsync(token) == "history-install", "cold database identity is unchanged");
            OpenHost();
        }
        public void Dispose()
        {
            Host?.Dispose(); _http?.Dispose(); _instance?.Dispose();
        }
    }

    private static SyncAuthorityObservationInput[] Inputs(int count, string identity) =>
        Enumerable.Range(0, count).Select(index => new SyncAuthorityObservationInput(
            "file-" + index.ToString("D5", CultureInfo.InvariantCulture) + ".txt", "file",
            JsonSerializer.Serialize(new { fileId = identity + "-" + index, volumeSerial = "fixture-volume" }),
            new string('a', 64), 10, "{}")).ToArray();
    private static SyncAuthorityObservationInput[] RemoteInputs() =>
        [new("file-00000.txt", "file", "{\"fileId\":\"cloud-origin\",\"revision\":\"1\"}", new string('a', 64), 10, "{}")];

    private static async Task<SyncAuthorityScanGeneration> CutAsync(Fixture fixture, string side,
        IReadOnlyList<SyncAuthorityObservationInput> entries, CancellationToken token)
    {
        var scan = await fixture.Db.BeginScanGenerationAsync(fixture.Pair.Id,
            side == "local" ? fixture.LocalRootId : fixture.CloudRootId, side, "manual", "{}", token);
        await fixture.Db.AppendObservationEntriesAsync(scan, entries, token);
        await fixture.Db.CompleteScanGenerationAsync(scan, token);
        return scan;
    }
    private static async Task<SyncAuthorityScanGeneration[]> ProduceFourCutsAsync(Fixture fixture, int count, CancellationToken token)
    {
        var first = await CutAsync(fixture, "local", Inputs(count, "origin"), token);
        var middle = await CutAsync(fixture, "local", Inputs(count, "middle"), token);
        await BackdateProducedInputsAsync(fixture.Db.DatabasePath, token);
        var third = await CutAsync(fixture, "local", Inputs(count, "now"), token);
        var fourth = await CutAsync(fixture, "local", Inputs(count, "now"), token);
        return [first, middle, third, fourth];
    }
    private static async Task<string> PlanningSnapshotAsync(Fixture fixture, CancellationToken token)
    {
        var set = await fixture.Db.LoadPlanningObservationSetAsync(fixture.Pair.Id, 1, fixture.LocalRootId,
            fixture.CloudRootId, await fixture.Db.LoadLatestCompleteScanIdAsync(fixture.Pair.Id, "local", token),
            await fixture.Db.LoadLatestCompleteScanIdAsync(fixture.Pair.Id, "dropbox", token), token);
        return Serialize(set);
    }

    private static readonly (string Table, string[] Columns)[] BackdatedColumns =
    [
        ("scan_generation", ["started_utc", "completed_utc"]),
        ("observation", ["observed_utc"]), ("object_version", ["observed_utc"]),
        ("membership", ["observed_utc"]), ("logical_object", ["created_utc"]),
        ("synchronization_point", ["accepted_utc"]), ("reconciliation_decision", ["decided_utc"]),
        ("operation", ["created_utc", "ready_utc", "terminal_utc"]),
        ("operation_attempt", ["started_utc", "ended_utc"]), ("effect_receipt", ["observed_utc"]),
        ("history_event", ["occurred_utc"]), ("resource_allocation", ["created_utc"]),
        ("namespace_allocation", ["created_utc"]), ("recovery_artifact", ["retained_utc", "retain_until_utc", "released_utc"]),
        ("command_request", ["requested_utc", "terminal_utc"])
    ];
    private static async Task BackdateProducedInputsAsync(string database, CancellationToken token)
    {
        await using var connection = OpenIsolated(database);
        await connection.OpenAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var guards = new List<(string Name, string Sql)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name,sql FROM sqlite_schema WHERE type='trigger' ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var sql = reader.GetString(1);
                if (Regex.IsMatch(sql, @"BEFORE\s+UPDATE(?:\s+OF[\s\S]*?)?\s+ON\s+(?:" +
                    string.Join("|", BackdatedColumns.Select(item => Regex.Escape(item.Table))) + @")\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    guards.Add((reader.GetString(0), sql));
            }
        }
        foreach (var guard in guards)
            await SqlAsync(connection, transaction, "DROP TRIGGER \"" + guard.Name + "\";", token);
        foreach (var item in BackdatedColumns)
        {
            var set = string.Join(",", item.Columns.Select(column =>
                column + "=CASE WHEN " + column + " IS NULL THEN NULL ELSE $old END"));
            await SqlAsync(connection, transaction, "UPDATE " + item.Table + " SET " + set + ";", token,
                ("$old", Old.ToString("O", CultureInfo.InvariantCulture)));
        }
        foreach (var guard in guards) await SqlAsync(connection, transaction, guard.Sql, token);
        foreach (var guard in guards)
        {
            await using var verify = connection.CreateCommand(); verify.Transaction = transaction;
            verify.CommandText = "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name=$name;";
            verify.Parameters.AddWithValue("$name", guard.Name);
            Check((string?)await verify.ExecuteScalarAsync(token) == guard.Sql,
                "isolated temporal input restores exact original update guard " + guard.Name);
        }
        await transaction.CommitAsync(token);
        Check(await ForeignKeyErrorsAsync(database, token) == 0,
            "isolated temporal transformation keeps real row identities/status/dependencies and reinstates update guards");
    }

    private static readonly string[] SnapshotTables =
    [
        "logical_object", "object_version", "membership", "observation", "scan_generation", "synchronization_point",
        "operation", "operation_member", "operation_dependency", "operation_attempt", "effect_receipt", "history_event",
        "resource_allocation", "namespace_allocation", "recovery_artifact", "command_request", "lease", "lease_fence",
        "conflict", "tombstone", "projection_watermark"
    ];
    private static Task<string> AuthoritySnapshotAsync(SyncAuthorityDatabase db, CancellationToken token) =>
        RowsDigestAsync(db.DatabasePath, SnapshotTables, token);
    private static async Task<string> ProtectedAuditAsync(string database, CancellationToken token)
    {
        // Do not accidentally demand retention of unrelated disposable command-history rows.
        // Exact public-producer Recovery operations and unresolved effects are the protected set.
        const string operations = "SELECT o.operation_id FROM operation o WHERE o.status NOT IN ('completed','cancelled','superseded') " +
            "OR EXISTS(SELECT 1 FROM recovery_artifact r WHERE r.operation_id=o.operation_id AND r.status<>'released')";
        const string attempts = "SELECT attempt_id FROM operation_attempt WHERE operation_id IN (" + operations + ")";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var connection = OpenIsolated(database); await connection.OpenAsync(token);
        foreach (var item in new (string Table, string Where)[]
        {
            ("operation", "operation_id IN (" + operations + ")"),
            ("operation_member", "operation_id IN (" + operations + ")"),
            ("operation_dependency", "operation_id IN (" + operations + ") OR depends_on_operation_id IN (" + operations + ")"),
            ("resource_allocation", "operation_id IN (" + operations + ")"),
            ("namespace_allocation", "operation_id IN (" + operations + ")"),
            ("operation_attempt", "operation_id IN (" + operations + ")"),
            ("effect_receipt", "attempt_id IN (" + attempts + ")"),
            ("recovery_artifact", "operation_id IN (" + operations + ")"),
            ("command_request", "pair_id IN (SELECT pair_id FROM operation WHERE operation_id IN (" + operations + "))"),
            ("history_event", "operation_id IN (" + operations + ") OR " +
                "CASE WHEN json_valid(detail_json) THEN json_extract(detail_json,'$.OperationId') END IN (" + operations + ")"),
            ("lease", "scope_kind='operation' AND scope_id IN (" + operations + ")"),
            ("lease_fence", "scope_kind='operation' AND scope_id IN (" + operations + ")")
        })
        {
            hash.AppendData(Encoding.UTF8.GetBytes(item.Table + "\n"));
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT rowid,* FROM " + item.Table + " WHERE " + item.Where + " ORDER BY rowid;";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray()) + "\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static async Task<string> RowsDigestAsync(string database, IEnumerable<string> tables, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var connection = OpenIsolated(database); await connection.OpenAsync(token);
        foreach (var table in tables)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(table + "\n"));
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT rowid,* FROM " + table + " ORDER BY rowid;";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var values = Enumerable.Range(0, reader.FieldCount).Select(index =>
                    reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray();
                hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values) + "\n"));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static async Task<string> GuardManifestAsync(string database, CancellationToken token)
    {
        await using var connection = OpenIsolated(database); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type,name,sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name;";
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<string>();
        while (await reader.ReadAsync(token))
            rows.Add(Serialize(new[] { reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2) }));
        return string.Join("\n", rows);
    }
    private static async Task<int> ForeignKeyErrorsAsync(string database, CancellationToken token)
    {
        await using var connection = OpenIsolated(database); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(token);
        var count = 0; while (await reader.ReadAsync(token)) count++; return count;
    }
    private static async Task<long> CountAsync(string database, string sql, CancellationToken token,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = OpenIsolated(database); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }
    private static async Task<string> TextAsync(string database, string sql, CancellationToken token,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = OpenIsolated(database); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return (string?)await command.ExecuteScalarAsync(token) ?? throw new InvalidDataException("Missing actual fixture row.");
    }
    private static async Task ExecuteIsolatedAsync(string database, string sql, CancellationToken token,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = OpenIsolated(database); await connection.OpenAsync(token);
        await SqlAsync(connection, null, sql, token, parameters);
    }
    private static async Task SqlAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(token);
    }
    private static SqliteConnection OpenIsolated(string database) =>
        new(new SqliteConnectionStringBuilder { DataSource = database, ForeignKeys = true, Pooling = false }.ToString());
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
    private static void Check(bool condition, string message)
    { _assertions++; if (!condition) throw new InvalidDataException("M7/G4: " + message); }
    private static void Done(string name)
    { Check(Covered.Add(name), "duplicate coverage " + name); Console.WriteLine("PASS M7/G4 case: " + name); }
    private static async Task ThrowsAsync<T>(Func<Task> action, string message, bool requireMessage = false) where T : Exception
    {
        try { await action(); }
        catch (T error)
        {
            Check(!requireMessage || error.Message.Contains(message, StringComparison.Ordinal), "wrong refusal: " + error.Message);
            return;
        }
        throw new InvalidDataException("Expected " + typeof(T).Name + ": " + message);
    }
    private sealed class FixtureFault(string phase) : Exception(phase);
    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("No external network authority in history fixture.");
    }
    private sealed class NoTokens : IDropboxRefreshTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken token = default) => Task.FromResult<string?>(null);
        public Task WriteAsync(string value, CancellationToken token = default) => throw new InvalidOperationException();
        public Task DeleteAsync(CancellationToken token = default) => throw new InvalidOperationException();
    }
}
