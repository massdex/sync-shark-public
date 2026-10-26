using Microsoft.Data.Sqlite;
using System.Text;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Massdex.SyncShark;

// E3: one real local-to-local slice through the existing authority, planning, journal and recovery
// pipeline. Every case below drives the production runtime entry (SyncEndpointPairRuntime) over real
// disposable NTFS roots; no case substitutes a fake endpoint or a second sync engine.
internal static class PairEndpointRuntimeChecks
{
    private static readonly List<Dictionary<string, object?>> Results = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root, bool hostOnly = false)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        Results.Clear();
        _assertions = 0;

        if (!hostOnly)
        {
        await RealPayloadAndRecoverableOverwrite();
        await OverlapAndCrossPairRefusals();
        await StaleRootIdentityRefusal();
        await DeletionRejectionWithoutPropagation();
        await CancellationWithoutEffect();
        await ConflictWithoutTimestampWinner();
        await AdapterIdentityAndRecovery();
        }
        await PersistedHostFirstSyncAndRestart();
        await PersistedAccountlessIssuedRecovery();

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "PairEndpointRuntimeChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["scope"] = hostOnly ? "persisted-host" : "adapter-and-persisted-host",
            ["cases"] = Results
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "pair-endpoint-runtime-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS pair endpoint runtime: " + _assertions + " assertions; " +
            (hostOnly ? "persisted application host, first-sync and restart." :
                "real local-to-local adapter and persisted application host; see the per-case report."));
    }

    private static async Task PersistedHostFirstSyncAndRestart()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Endpoint binding checks require Windows NTFS identities.");
        Claim("E3-09-host", "The real application host binds, reviews and runs a persisted local pair without cloud credentials");
        var fixture = Fixture("host");
        using var owner = PilotInstanceLock.Acquire(Path.Combine(fixture.Root, "product", "instance.lock"));
        using var http = new HttpClient(new NoNetwork());
        SyncAuthorityRuntimeHost OpenHost() => new(owner.StateAuthority, http,
            new SyncPairStore(owner.StateAuthority), new SyncRuntimeStateStore(owner.StateAuthority),
            new DropboxConnectionStore(owner.StateAuthority), new NoCredentials(),
            new SyncAuthorityDatabase(Path.Combine(fixture.Root, "host.db")));
        var draft = fixture.Pair with
        {
            Id = SyncPair.DeriveId(fixture.Pair.ToPilotSettings(), SyncProviders.Local),
            RecoveryRoot = null, EndpointSchemaVersion = fixture.Document.Version,
            LeftEndpoint = fixture.Document.Left, RightEndpoint = fixture.Document.Right
        };
        var payload = Path.Combine(fixture.Left, "host.txt");
        var target = Path.Combine(fixture.Right, "host.txt");
        await File.WriteAllTextAsync(payload, "host payload");
        await File.WriteAllTextAsync(Path.Combine(fixture.Right, "right-only.txt"), "right-side payload");
        using (var host = OpenHost())
        {
            var endpoints = new SyncPairEndpoints(fixture.LeftEndpoint, fixture.RightEndpoint, SyncMode.TwoWay);
            draft = SyncEndpointPairDraft.Create(endpoints, draft.Name);
            var bound = await host.BindEndpointPairAsync(draft);
            var editor = SyncPairEditorSession.ForExistingPair(bound.Pair, out var reopened);
            Require(reopened.IsValid && editor.KindOf(SyncPairSide.Right) == SyncEndpointKind.Local &&
                editor.RecordOf(SyncPairSide.Right) == fixture.Document.Right,
                "E3-09 editor reopens the explicit right-local identity rather than inventing a cloud account");
            Require(bound.Pair.InitialReviewApproved == false && bound.Pair.RightControlNamespace is not null &&
                bound.Pair.RecoveryRoot is not null && !File.Exists(target),
                "E3-08 binding persists both private namespaces but does not transfer before first-sync approval");
            Require(host.PairConnectionIds(bound.Pair).Count == 0 && !host.IsPairAccountPaused(bound.Pair),
                "E3-08 local endpoints have no cloud account dependency");
            var progress = new List<SyncComparisonProgress>();
            await host.RefreshBootstrapInventoryAsync(bound.Pair.Id, onProgress: update =>
            { if (update.Comparison is { } comparison) progress.Add(comparison); return Task.CompletedTask; });
            var review = await host.LoadBootstrapReviewAsync(bound.Pair.Id);
            Require(review.Any(row => row.RelativePath == "host.txt") && !File.Exists(target),
                "E3-08 actual first-sync inventory exposes the payload without transferring it");
            Require(review.Single(row => row.RelativePath == "host.txt").Effect == "Copy to Right — This computer",
                "R536 local/local public plan names the right local destination, never Dropbox");
            Require(progress.Any(item => item.Phase == "Checking Right — This computer Folder") &&
                progress.Any(item => item.Detail.Contains("Right — This computer metadata records", StringComparison.Ordinal)) &&
                progress.All(item => !item.Phase.Contains("Cloud storage", StringComparison.Ordinal) &&
                    !item.Detail.Contains("dropbox identity map", StringComparison.OrdinalIgnoreCase)),
                "R536 actual local/local observation progress uses endpoint labels through metadata persistence");
            var approved = await host.ApproveBootstrapMergeAsync(bound.Pair.Id);
            Require(approved.InitialReviewApproved == true && approved.PolicyRevision > bound.Pair.PolicyRevision,
                "E3-08 actual first-sync approval survives a policy revision without reallocating private staging");
            var lostAcknowledgement = 0;
            host.Authority.ActivityAppendBoundaryForTest = (stage, _) =>
                stage == "after" && Interlocked.CompareExchange(ref lostAcknowledgement, 1, 0) == 0
                    ? Task.FromException(new IOException("fixture: committed history acknowledgement lost"))
                    : Task.CompletedTask;
            var result = await host.RunPairsAsync([approved], "Endpoint host fixture");
            host.Authority.ActivityAppendBoundaryForTest = null;
            await host.RetryPendingActivityPersistenceAsync();
            Require(lostAcknowledgement == 1 && !host.ActivityLogIncomplete && host.ActivityLogPendingCount == 0,
                "HIST actual Host recovers a lost committed acknowledgement without losing its ordered pending prefix");
            async Task<long> HistoryNumber(string sql)
            {
                await using var connection = new SqliteConnection("Data Source=" + host.Authority.DatabasePath + ";Mode=ReadOnly;Pooling=False");
                await connection.OpenAsync();
                await using var query = connection.CreateCommand(); query.CommandText = sql;
                return Convert.ToInt64(await query.ExecuteScalarAsync());
            }
            Require(await HistoryNumber("SELECT count(*) FROM command_request WHERE activity_opened=1;") > 0 &&
                    await HistoryNumber("SELECT count(*) FROM command_request c WHERE activity_opened=1 AND " +
                        "(activity_closed<>1 OR activity_incomplete<>0 OR next_activity_ordinal<>1+" +
                        "(SELECT count(*) FROM history_event h WHERE h.producer_command_id=c.command_request_id));") == 0,
                "HIST actual Host closes only fully drained writers with one retained row per committed ordinal");
            host.Authority.ActivityAppendBoundaryForTest = (stage, _) => stage == "before"
                ? Task.FromException(new IOException("fixture: hold final history persistence")) : Task.CompletedTask;
            await host.RunPairsAsync([approved], "History final drain retry fixture");
            Require(host.ActivityLogPendingCount > 0 && host.ActivityLogIncomplete &&
                    await HistoryNumber("SELECT count(*) FROM command_request WHERE activity_opened=1 AND activity_closed=0;") == 1,
                "HIST failed final flush keeps the exact terminal command writer open and retains its pending events");
            host.Authority.ActivityAppendBoundaryForTest = null;
            await host.RetryPendingActivityPersistenceAsync();
            Require(host.ActivityLogPendingCount == 0 && !host.ActivityLogIncomplete &&
                    await HistoryNumber("SELECT count(*) FROM command_request WHERE activity_opened=1 AND (activity_closed=0 OR activity_incomplete<>0);") == 0,
                "HIST a recovered final flush closes complete without reissuing file effects");

            if (result.Items.FirstOrDefault(item => item.FailureType is not null) is { } failed)
                throw new InvalidDataException("Host pair failure: " + failed.FailureType + ": " + failed.FailureMessage);
            Require(File.Exists(target) && await File.ReadAllTextAsync(target) == "host payload",
                "E3-08 production coordinated host run transfers the approved bytes");
            Require(result.Items.All(item => item.FailureType is null && item.Result is { Blocked: 0 }),
                "E3-08 the real coordinated run has no hidden pair failure");

            // R538: the installed journey failed only AFTER copying, when automatic monitoring
            // assumed that the second local endpoint must have a cloud cursor. Exercise the actual
            // host configuration, both real watchers, their durable hints and normal run callback.
            var enabled = (await host.SetPairEnabledAsync(approved.Id, true)).Pairs.Single();
            var completion = new TaskCompletionSource<SyncAuthorityBatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbackStarted = 0;
            var monitors = await host.ConfigureMonitorsAsync([enabled], async () =>
            {
                if (Interlocked.CompareExchange(ref callbackStarted, 1, 0) != 0) return true;
                try { completion.TrySetResult(await host.RunPairsAsync([enabled], "Monitor")); }
                catch (Exception error) { completion.TrySetException(error); }
                return true;
            });
            try
            {
                Require(monitors is [{ State: "Monitoring", FailureMessage: null }],
                    "R538 accountless local/local monitoring starts without a cloud feed or cloud credentials");
                Require((await host.LoadMonitorProjectionAsync()).Count(item => item.PairId == enabled.Id &&
                        item.Kind == "directory_changes") == 2,
                    "R538 two distinct retained directory watchers cover both local endpoint bindings");
                await File.WriteAllTextAsync(Path.Combine(fixture.Left, "left-monitor.txt"), "left watcher payload");
                await File.WriteAllTextAsync(Path.Combine(fixture.Right, "right-monitor.txt"), "right watcher payload");
                var monitoredRun = await completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
                Require(monitoredRun.Items.All(item => item.FailureMessage is null) &&
                        await File.ReadAllTextAsync(Path.Combine(fixture.Right, "left-monitor.txt")) == "left watcher payload" &&
                        await File.ReadAllTextAsync(Path.Combine(fixture.Left, "right-monitor.txt")) == "right watcher payload",
                    "R538 real directory notifications drive the normal host to copy new files in BOTH directions");
            }
            finally { await host.StopMonitorsAsync(); }
            var badRight = enabled with { RightEndpoint = enabled.RightEndpoint! with
                { LocalRootFileId = enabled.RightEndpoint.LocalRootFileId!.Value ^ 1UL } };
            var rejectedMonitors = await host.ConfigureMonitorsAsync([badRight], () =>
                throw new InvalidOperationException("A replaced endpoint must not dispatch."));
            Require(rejectedMonitors is [{ State: "Monitor unavailable", FailureMessage: not null }] &&
                    await File.ReadAllTextAsync(Path.Combine(fixture.Right, "left-monitor.txt")) == "left watcher payload",
                "R538 a replaced local root identity is rejected before watcher dispatch with copied files unchanged");
            var retainedFailure = (await new SyncAuthorityDatabase(Path.Combine(fixture.Root, "host.db"))
                .LoadActivityProjectionPageAsync(pairId: enabled.Id,
                    query: new SyncActivityQuery(Severity: "Error", DetailedLog: true)))
                .Single(item => item.Outcome == "Automatic monitoring failed");
            Require(retainedFailure.Reason.Contains(rejectedMonitors[0].FailureMessage!, StringComparison.Ordinal) &&
                    retainedFailure.EndpointContext?.PairId == enabled.Id && retainedFailure.RunId is null,
                "R538 reopened durable GUI/CLI operation log retains the exact monitor error and pair, without inventing a transfer run");
        }
        using (var host = OpenHost())
        {
            var pair = (await host.LoadPairsAsync()).Pairs.Single();
            Require(pair.EndpointDocument == fixture.Document && pair.InitialReviewApproved == true,
                "E3-08 new host instance reads both endpoint identities and approval from the real database");
            await File.WriteAllTextAsync(payload, "host replacement");
            await host.RunPairsAsync([pair], "Endpoint host restart fixture");
            Require(await File.ReadAllTextAsync(target) == "host replacement",
                "E3-08 restarted host uses the same bindings to apply a recoverable replacement");
            Require(Directory.EnumerateFiles(pair.RightControlNamespace!.Local.RootPath)
                .Any(path => File.ReadAllText(path) == "host payload"),
                "E3-08 the old target bytes remain in the exact persisted right-side recovery namespace");
        }
    }

    // AUD03: a normal restart with no pending work is not an issued-effect recovery proof.
    // Stop the real local-create executor after the target rename but before its receipt,
    // reopen the same persisted host with cloud access forbidden, and recover that exact
    // operation. The phase hook supplies only the interruption; no decision is substituted.
    private static async Task PersistedAccountlessIssuedRecovery()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Accountless issued recovery requires Windows NTFS identities.");
        Claim("AUD03-issued-local-host-recovery",
            "Accountless local/local startup recovery verifies a promoted target without a second transfer");
        var fixture = Fixture("host-issued-recovery");
        const string path = "recover-after-promotion.txt";
        const string content = "accountless retained local effect";
        var source = Path.Combine(fixture.Right, path);
        var target = Path.Combine(fixture.Left, path);
        await File.WriteAllTextAsync(source, content);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var owner = PilotInstanceLock.Acquire(Path.Combine(fixture.Root, "product", "instance.lock"));
        using var http = new HttpClient(new NoNetwork());
        var authority = new SyncAuthorityDatabase(Path.Combine(fixture.Root, "host.db"));
        SyncAuthorityRuntimeHost OpenHost() => new(owner.StateAuthority, http,
            new SyncPairStore(owner.StateAuthority), new SyncRuntimeStateStore(owner.StateAuthority),
            new DropboxConnectionStore(owner.StateAuthority), new NoCredentials(), authority);
        SyncPair pair;
        string operationId;
        string attemptId;
        PilotDirectoryIdentity targetIdentity;
        using (var host = OpenHost())
        {
            var draft = SyncEndpointPairDraft.Create(new SyncPairEndpoints(fixture.LeftEndpoint,
                fixture.RightEndpoint, SyncMode.TwoWay), "Accountless issued recovery");
            pair = (await host.BindEndpointPairAsync(draft, cancellationToken: deadline.Token)).Pair;
            await host.RefreshBootstrapInventoryAsync(pair.Id, deadline.Token);
            await host.ApproveBootstrapMergeAsync(pair.Id, deadline.Token);
            pair = (await host.SetPairEnabledAsync(pair.Id, true, deadline.Token)).Pairs.Single();
            var interruptions = 0;
            using var interrupted = new CancellationTokenSource();
            using var firstRunToken = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, interrupted.Token);
            SyncAuthorityCreateLocalExecutor.PhaseForTest = phase =>
            {
                if (phase == "target-promoted-before-receipt")
                {
                    Interlocked.Increment(ref interruptions);
                    interrupted.Cancel();
                    throw new OperationCanceledException(
                        "Fixture cancellation after local target promotion, before receipt.", interrupted.Token);
                }
            };
            try
            {
                var stopped = false;
                try
                {
                    var first = await host.RunPairsAsync([pair], "Accountless issued interruption",
                        cancellationToken: firstRunToken.Token);
                    stopped = first.Stopped;
                }
                catch (OperationCanceledException) when (interrupted.IsCancellationRequested) { stopped = true; }
                Require(interruptions == 1 && stopped,
                    "AUD03 cancellation stops the real host after exactly one promoted local target, before in-run reconciliation; interruptions=" +
                    interruptions + ", stopped=" + stopped);
            }
            finally { SyncAuthorityCreateLocalExecutor.PhaseForTest = null; }
            Require(await File.ReadAllTextAsync(target, deadline.Token) == content &&
                    await File.ReadAllTextAsync(source, deadline.Token) == content,
                "AUD03 the interrupted destination and unchanged source contain the exact real payload");
            using (var file = SyncLocalFileAuthority.OpenExistingReadGuard(target)) targetIdentity = file.VerifySingleLink();
            var pending = (await authority.ListRecoveryOperationsForPairAsync(pair.Id,
                cancellationToken: deadline.Token)).Single();
            operationId = pending.OperationId;
            var retained = await authority.LoadOperationAsync(operationId, deadline.Token);
            Require(retained.Status is SyncAuthorityOperationStatus.EffectIssuing or
                    SyncAuthorityOperationStatus.EffectObserved or SyncAuthorityOperationStatus.EffectUnknown &&
                    retained.Attempts.Count == 1 && retained.Attempts.Single().Status is
                        SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.EffectUnknown,
                "AUD03 the issued operation and single attempt survive in the exact persisted recovery queue");
            attemptId = retained.Attempts.Single().AttemptId;
        }
        using (var host = OpenHost())
        {
            pair = (await host.LoadPairsAsync(deadline.Token)).Pairs.Single();
            Require(host.PairConnectionIds(pair).Count == 0,
                "AUD03 the reopened recovery pair has no cloud connection identity");
            var recoveryProgress = new System.Collections.Concurrent.ConcurrentQueue<SyncComparisonProgress>();
            var recovered = await host.RecoverPairsAsync([pair], onProgress: update =>
            {
                if (update.Comparison is { } progress) recoveryProgress.Enqueue(progress);
                return Task.CompletedTask;
            }, cancellationToken: deadline.Token);
            Require(recoveryProgress.Any(progress => progress.Phase == "Checking endpoint bindings" &&
                    progress.Detail.StartsWith("Starting: Verifying both saved local root identities", StringComparison.Ordinal)) &&
                    recoveryProgress.Any(progress => progress.Phase == "Checking endpoint bindings" &&
                    progress.Detail.StartsWith("Completed: Verifying both saved local root identities", StringComparison.Ordinal)),
                "P05 recovery reports the actual local binding check before awaiting it and its real completion afterward");
            var complete = await authority.LoadOperationAsync(operationId, deadline.Token);
            var receipt = await authority.LoadLatestEffectReceiptAsync(operationId, attemptId, deadline.Token);
            Require(recovered.Items.Single().Result is { Blocked: 0, NeedsVerification: 0 } &&
                    complete.Status == SyncAuthorityOperationStatus.Completed && complete.Attempts.Count == 1 &&
                    receipt is { Certainty: "known_success" },
                "AUD03 the real reopened host reconciles and completes that same issued attempt without cloud access");
            using (var file = SyncLocalFileAuthority.OpenExistingReadGuard(target))
                Require(file.VerifySingleLink() == targetIdentity,
                    "AUD03 recovery preserves the promoted target's exact NTFS identity rather than rewriting it");
            Require(await File.ReadAllTextAsync(target, deadline.Token) == content &&
                    await File.ReadAllTextAsync(source, deadline.Token) == content &&
                    (await authority.ListRecoveryOperationsForPairAsync(pair.Id,
                        cancellationToken: deadline.Token)).Count == 0,
                "AUD03 exact bytes and source survive and the completed recovery leaves no runnable issued effect");
            var repeat = await host.RunPairsAsync([pair], "Accountless recovered unchanged repeat",
                cancellationToken: deadline.Token);
            Require(repeat.Items.Single().Result is { Uploaded: 0, Downloaded: 0, Blocked: 0, NeedsVerification: 0 } &&
                    (await authority.LoadOperationAsync(operationId, deadline.Token)).Attempts.Count == 1,
                "AUD03 an ordinary unchanged run after recovery does not replay the completed transfer");
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("Local-to-local sync attempted a network operation.");
    }

    private sealed class NoCredentials : IDropboxRefreshTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Local-to-local sync attempted to read cloud credentials.");
        public Task WriteAsync(string value, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task DeleteAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    private static async Task RealPayloadAndRecoverableOverwrite()
    {
        Claim("E3-01-create", "A real created file reaches the other bound root with matching bytes");
        var fixture = Fixture("create");
        var bytes = Encoding.UTF8.GetBytes("e3 payload " + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(Path.Combine(fixture.Left, "hello.txt"), bytes);

        var result = await Run(fixture);
        var arrived = Path.Combine(fixture.Right, "hello.txt");
        Require(File.Exists(arrived), "E3-01 the payload reached the bound right root");
        Require(File.ReadAllBytes(arrived).SequenceEqual(bytes),
            "E3-01 the transferred bytes match the source exactly");
        Require(result.Decisions.Any(item => item.Kind == SyncDecisionKind.Uploaded),
            "E3-01 the pipeline reported the create as an upload decision");

        Claim("E3-02-overwrite", "An overwrite preserves the replaced original recoverably");
        var replacement = Encoding.UTF8.GetBytes("e3 replacement " + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(Path.Combine(fixture.Left, "hello.txt"), replacement);
        await Run(fixture);

        Require(File.ReadAllBytes(arrived).SequenceEqual(replacement),
            "E3-02 the replacement reached the right root");
        var recovered = Directory.EnumerateFiles(SyncControlNamespace.LocalRootFor(fixture.Right,
            SyncEndpointPairRuntime.RightControlNamespaceId(fixture.Pair.Id)), "*", SearchOption.TopDirectoryOnly).ToArray();
        Require(recovered.Any(path => File.ReadAllBytes(path).SequenceEqual(bytes)),
            "E3-02 the exact replaced predecessor bytes are retained in the bound recovery container");
    }

    private static Task OverlapAndCrossPairRefusals()
    {
        Claim("E3-03-overlap", "Identical, nested and cross-pair endpoints are refused before any effect");
        var fixture = Fixture("overlap");
        var pair = fixture.Pair;
        var left = fixture.LeftEndpoint;
        var right = fixture.RightEndpoint;

        var identical = SyncEndpointPersistence.Symmetric(new SyncPairEndpoints(left, left, SyncMode.TwoWay));
        var identicalResolution = SyncEndpointPairRuntime.Resolve(pair, identical);
        Require(!identicalResolution.IsResolved &&
                identicalResolution.Validation.Errors.Contains(SyncEndpointError.SameEndpoint),
            "E3-03 an endpoint identical to the left endpoint is refused as the same store");

        var nestedPath = Path.Combine(fixture.Left, "nested");
        Directory.CreateDirectory(nestedPath);
        var nested = SyncEndpointPersistence.Symmetric(new SyncPairEndpoints(left,
            Endpoint(nestedPath), SyncMode.TwoWay));
        Require(!SyncEndpointPairRuntime.Resolve(pair, nested).IsResolved,
            "E3-03 a right endpoint nested inside the left root is refused");

        Directory.CreateDirectory(Path.Combine(fixture.Root, "other-left"));
        var crossPair = new[]
        {
            new SyncPairEndpoints(Endpoint(Path.Combine(fixture.Root, "other-left")), right, SyncMode.TwoWay)
        };
        var crossResolution = SyncEndpointPairRuntime.Resolve(pair, fixture.Document, crossPair);
        Require(!crossResolution.IsResolved &&
                crossResolution.Validation.Errors.Contains(SyncEndpointError.DuplicateEndpointAcrossPairs),
            "E3-03 a right endpoint already retained by another pair is refused as a duplicate");

        var containingPair = new[]
        {
            new SyncPairEndpoints(Endpoint(Path.Combine(fixture.Root, "other-left")),
                Endpoint(fixture.Root), SyncMode.TwoWay)
        };
        Require(!SyncEndpointPairRuntime.Resolve(pair, fixture.Document, containingPair).IsResolved,
            "E3-03 a right endpoint contained by another pair's endpoint is refused as overlapping");
        Require(!Directory.EnumerateFileSystemEntries(fixture.Right).Any(),
            "E3-03 no refused pair produced a filesystem effect");
        return Task.CompletedTask;
    }

    private static async Task StaleRootIdentityRefusal()
    {
        Claim("E3-04-stale-root", "A stale root identity cannot create an unapproved effect");
        var fixture = Fixture("stale");
        await File.WriteAllTextAsync(Path.Combine(fixture.Left, "stale.txt"), "stale payload");
        var savedRight = SyncEndpointRecord.From(fixture.RightEndpoint);
        var staleDocument = new SyncEndpointDocument(SyncEndpointSchema.SymmetricEndpoints,
            SyncEndpointRecord.From(fixture.LeftEndpoint),
            savedRight with { LocalRootFileId = savedRight.LocalRootFileId + 1 });
        var refused = false;
        try
        {
            await Run(fixture, staleDocument);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or
                                          SyncEndpointRuntimeRefusedException)
        {
            refused = true;
        }
        Require(refused, "E3-04 a saved right root identity that does not match live state is refused");
        Require(!Directory.EnumerateFileSystemEntries(fixture.Right).Any(),
            "E3-04 the refused run created nothing in the mismatched root");
    }

    private static async Task DeletionRejectionWithoutPropagation()
    {
        Claim("E3-05-delete-rejection", "Deletion propagation stays opt-in and never erases the other side");
        var fixture = Fixture("delete", propagateDeletions: false);
        await File.WriteAllTextAsync(Path.Combine(fixture.Left, "keep.txt"), "kept");
        await Run(fixture);
        Require(File.Exists(Path.Combine(fixture.Right, "keep.txt")),
            "E3-05 the first run created the counterpart");
        File.Delete(Path.Combine(fixture.Left, "keep.txt"));
        await Run(fixture);
        Require(File.Exists(Path.Combine(fixture.Right, "keep.txt")),
            "E3-05 the unpropagated deletion left the right copy intact");
    }

    private static async Task CancellationWithoutEffect()
    {
        Claim("E3-06-cancellation", "A stop request cannot create an unapproved destructive effect");
        var fixture = Fixture("cancel");
        await File.WriteAllTextAsync(Path.Combine(fixture.Left, "cancel.txt"), "cancel payload");
        var control = new PilotRunControl();
        control.Stop();
        var cancelled = false;
        try
        {
            await Run(fixture, runControl: control);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Require(cancelled, "E3-06 the stopped run ended at a cancellation boundary");
        Require(!Directory.EnumerateFileSystemEntries(fixture.Right).Any(),
            "E3-06 the stopped run issued no effect against the right root");
    }

    private static async Task ConflictWithoutTimestampWinner()
    {
        Claim("E3-07-conflict", "Both changed sides conflict and are preserved without a timestamp winner");
        var fixture = Fixture("conflict");
        await File.WriteAllTextAsync(Path.Combine(fixture.Left, "clash.txt"), "base");
        await Run(fixture);
        var rightCopy = Path.Combine(fixture.Right, "clash.txt");
        Require(File.Exists(rightCopy), "E3-07 the baseline copy exists on both sides");

        await File.WriteAllTextAsync(Path.Combine(fixture.Left, "clash.txt"), "left change");
        await File.WriteAllTextAsync(rightCopy, "right change");
        File.SetLastWriteTimeUtc(rightCopy, DateTime.UtcNow.AddHours(1));
        var result = await Run(fixture);

        Require(result.Decisions.Any(item => item.Kind == SyncDecisionKind.Conflict),
            "E3-07 the pipeline reported a conflict rather than resolving it");
        Require(File.ReadAllText(Path.Combine(fixture.Left, "clash.txt")) == "left change",
            "E3-07 the left version was preserved unchanged");
        Require(File.ReadAllText(rightCopy) == "right change",
            "E3-07 the newer right version was not silently propagated over the left change");
    }

    private static async Task AdapterIdentityAndRecovery()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Claim("E3-08-adapter", "The real local adapter binds identity, create-only effects and recovery ownership");
        var fixture = Fixture("adapter");
        var path = Path.Combine(fixture.Right, "exact.txt");
        var bytes = Encoding.UTF8.GetBytes("same bytes, distinct object");
        await File.WriteAllBytesAsync(path, bytes);
        using var root = PilotDirectoryLease.Open(fixture.Right);
        var namespaceId = SyncEndpointPairRuntime.RightControlNamespaceId(fixture.Pair.Id);
        var local = SyncControlNamespace.BindLocalForTest(root, "adapter-installation-fixture", namespaceId);
        var control = new SyncControlNamespaceBinding("adapter-installation-fixture", namespaceId, local);
        var store = new SyncLocalEndpointStore(new(fixture.Right, root.Identity.VolumeSerialNumber,
            root.Identity.FileId), control, SyncProviders.Local);
        var original = (await store.ListEntriesAsync()).Single();
        File.Move(path, Path.Combine(fixture.Right, "original.saved"));
        await File.WriteAllBytesAsync(path, bytes);
        var replacement = (await store.ListEntriesAsync()).Single(item => item.RelativePath == "exact.txt");
        Require(original.ContentHash == replacement.ContentHash && original.Id != replacement.Id &&
                original.Revision != replacement.Revision,
            "E3-08 equal bytes in a substituted file do not preserve object or revision identity");
        using var destination = new MemoryStream();
        await Refused(() => store.DownloadAsync(original, destination),
            "E3-08 stale download is refused before any destination byte");
        Require(destination.Length == 0, "E3-08 refused download left the destination empty");
        using var upload = new MemoryStream(bytes);
        await Refused(() => store.UploadAsync("exact.txt", upload, original.ContentHash!, original.Revision),
            "E3-08 stale replacement cannot overwrite a new object containing identical bytes");
        upload.Position = 0;
        await Refused(() => store.UploadCreateOnlyAsync("exact.txt", upload, original.ContentHash!),
            "E3-08 create-only upload preserves an occupied destination");
        Require(File.ReadAllBytes(path).SequenceEqual(bytes), "E3-08 refused writes preserved the current bytes");
        var retained = await store.CopyToRecoveryAsync(replacement, store.RecoveryBinding, "retained.bin");
        Require(retained.FileId != replacement.Id && retained.ContentHash == replacement.ContentHash,
            "E3-08 recovery records the retained copy's real identity and verified content");
        await Refused(() => store.GetRecoveryAsync(store.RecoveryBinding with { RootNamespaceId = "foreign" }, "retained.bin"),
            "E3-08 foreign recovery ownership is refused");
        var restored = await store.CopyRecoveryToPathAsync(store.RecoveryBinding, retained, "restored.txt");
        Require(File.ReadAllBytes(Path.Combine(fixture.Right, "restored.txt")).SequenceEqual(bytes) &&
                restored.Id != retained.FileId, "E3-08 recovery restores an independently identified exact copy");
        await Refused(() => store.CopyRecoveryToPathAsync(store.RecoveryBinding, retained, "exact.txt"),
            "E3-08 recovery refuses an occupied destination without replacing it");
        await Refused(() => store.DeleteRecoveryAsync(store.RecoveryBinding, retained with { Revision = "stale" }),
            "E3-08 recovery removal requires the exact retained revision");
        Require(await store.GetRecoveryAsync(store.RecoveryBinding, "retained.bin") == retained,
            "E3-08 refused removal preserved the original recovery copy");
        await store.DeleteRecoveryAsync(store.RecoveryBinding, retained);
        Require(await store.GetRecoveryAsync(store.RecoveryBinding, "retained.bin") is null && File.Exists(path),
            "E3-08 exact removal deletes only the named recovery copy, not the user file");
    }

    private static async Task Refused(Func<Task> operation, string message)
    {
        var refused = false;
        try { await operation(); }
        catch (Exception error) when (error is IOException or DropboxApiException) { refused = true; }
        Require(refused, message);
    }

    private static SyncEndpointPairRuntimeFixture Fixture(string name, bool propagateDeletions = false)
    {
        var root = Path.Combine(_root, name);
        var left = Directory.CreateDirectory(Path.Combine(root, "left")).FullName;
        var right = Directory.CreateDirectory(Path.Combine(root, "right")).FullName;
        var recovery = Directory.CreateDirectory(Path.Combine(root, "recovery")).FullName;
        var leftEndpoint = Endpoint(left);
        var rightEndpoint = Endpoint(right);
        var document = SyncEndpointPersistence.Symmetric(new SyncPairEndpoints(leftEndpoint, rightEndpoint,
            SyncMode.TwoWay));
        var pair = new SyncPair("endpoint-fixture-" + name, "E3 " + name, left,
            leftEndpoint.LocalIdentity!.VolumeSerialNumber, leftEndpoint.LocalIdentity.RootFileId,
            right, rightEndpoint.LocalIdentity!.IdentityToken(),
            PropagateDeletions: propagateDeletions,
            RecoveryRoot: new SyncRemoteRecoveryRootBinding(right,
                rightEndpoint.LocalIdentity!.IdentityToken(), SyncEndpointPairRuntime.LocalProviderLiteral),
            ConnectionId: "", Provider: SyncEndpointPairRuntime.LocalProviderLiteral,
            InitialReviewApproved: true);
        return new SyncEndpointPairRuntimeFixture(root, left, right, recovery, pair, document, leftEndpoint,
            rightEndpoint);
    }

    private static SyncEndpoint Endpoint(string path)
    {
        using var lease = PilotDirectoryLease.Open(path);
        return SyncEndpoint.Local(lease.Path, new SyncLocalEndpointIdentity(lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId));
    }

    private static async Task<SyncRunResult> Run(SyncEndpointPairRuntimeFixture fixture,
        SyncEndpointDocument? document = null, PilotRunControl? runControl = null)
    {
        var authority = new SyncAuthorityDatabase(Path.Combine(fixture.Root, "state", "syncshark.db"));
        var options = new SyncLocalPairRuntimeOptions("install-endpoint-fixture-" + Path.GetFileName(fixture.Root),
            "profile-e3", JsonSerializer.Serialize(new { capability = SyncEndpointPairRuntime.LocalProviderLiteral }));
        await authority.InitializeAsync(options.InstallationId, options.ProfileId,
            BuildIdentity.SourceRevision, options.CapabilityProfileJson);
        await authority.RegisterPairAsync(fixture.Pair.Id, fixture.Pair.PolicyRevision,
            new SyncAuthorityPairConfiguration(fixture.Pair.PropagateDeletions ? "recoverable" : "disabled",
                0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
                AutomaticPruning: false, RecoveryRetentionDays: options.RecoveryRetentionDays));
        await authority.VerifyHealthAsync();
        var admission = await authority.CreateAcceptedCommandAdmissionAsync(null, "run_pair", fixture.Pair.Id,
            null, null, JsonSerializer.Serialize(new { pairIds = new[] { fixture.Pair.Id } }),
            SyncAuthorityCommandPurpose.NormalEffect);
        try
        {
            await authority.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
            var result = await SyncEndpointPairRuntime.RunLocalToLocalAsync(fixture.Pair, document ?? fixture.Document,
                authority, options, admission, retainedPairs: null, runControl: runControl, trigger: "E3 checks",
                onInventory: null, cancellationToken: default);
            await authority.TransitionCommandRequestAsync(admission.CommandRequestId, "completed");
            return result;
        }
        catch
        {
            await authority.TransitionCommandRequestAsync(admission.CommandRequestId, "blocked");
            throw;
        }
    }

    private sealed record SyncEndpointPairRuntimeFixture(string Root, string Left, string Right,
        string Recovery, SyncPair Pair, SyncEndpointDocument Document, SyncEndpoint LeftEndpoint,
        SyncEndpoint RightEndpoint);

    private static void Claim(string id, string requirement) => Results.Add(new Dictionary<string, object?>
    {
        ["id"] = id,
        ["requirement"] = requirement,
        ["observed"] = "pending"
    });

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        _assertions++;
        var current = Results[^1];
        var observed = (string?)current["observed"];
        current["observed"] = observed == "pending" ? message : observed + " | " + message;
        current["state"] = "observed";
        Console.WriteLine("PASS " + message);
    }

    internal static string IdentityToken(this SyncLocalEndpointIdentity identity) =>
        identity.VolumeSerialNumber.ToString("x8") + ":" + identity.RootFileId.ToString("x16");
}
