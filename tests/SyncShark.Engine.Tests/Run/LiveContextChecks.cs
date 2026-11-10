using System.Runtime.ExceptionServices;
using System.Globalization;
using Massdex.SyncShark.Engine.Executor;
using System.Net.Http;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Checks for LiveContext, LiveCoverage, LiveExecutionBoundary and RecoveryExecutionBoundary: a live row's
/// facts come from the existing PathScope/IssuePolicy/RowContext owners, supplied source coverage is forwarded and
/// never recaptured, and a stale pair, selection or generation refuses before any effect callback
/// (`--engine-checks repair`). It stays one physical file because the row owns exactly these four live-admission
/// types and every case serves that one seam.</summary>
public static class LiveContextChecks
{
    private const string PairId = "pair-a";
    private const string OtherPairId = "pair-b";
    private const string ConnectionId = "connection-a";
    private const string Account = "account-a";
    private const string RootNamespace = "root-a";
    private const string FolderId = "folder-a";
    private const string DropboxRoot = "/ProbePair";
    private const string ChildPath = "Child.txt";
    private const string HeldPath = "Held";
    private const string ExcludedPath = "Excluded";
    private const string Cursor = "cursor-a";
    private const string LocalId = "local-id";
    private const string HeldId = "held-id";
    private const string OperationId = "operation-a";
    private const string PairName = "Pair A";
    private const string RunId = "live-context";
    private const string UploadRoute = "upload";
    private const string SourceIdentity = "source-a";
    private const long Generation = 0;
    private const ulong Volume = 7;
    private const int LogBufferCharacters = 256;

    private static readonly string LocalRoot =
        Path.Combine(Path.GetTempPath(), "syncshark-live-context", "local");
    private static readonly EngineSettings Settings = new("{name} ({side} {time}){ext}", "yyyyMMddHHmmss",
        "Local", "Dropbox", ".case", RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins, 1, "", LogBufferCharacters, ScanTestSettings.BatchRows) { JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes, LogReadBufferBytes = ScanTestSettings.LogReadBufferBytes };
    private static readonly RowContext Neutral = new(false, false, false, null, false, false, false, false);

    private static string VolumeKey => Volume.ToString(CultureInfo.InvariantCulture);

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "syncshark-live-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using (var facts = NewRig(folder, Pair())) await InLive(facts, session => FactsAsync(facts, session));
            using (var live = NewRig(folder, Pair())) await CaptureAsync(live);
            using (var recovery = NewRig(folder, Pair())) await InLive(recovery, RecoveryAsync);
            using (var advanced = NewRig(folder, Pair())) await AdvancedGenerationAsync(advanced);
            using (var obligated = NewRig(folder, Pair()))
                await InLive(obligated, session => ObligatedAsync(obligated, session));
            using (var owed = NewRig(folder, Pair())) await OwedAfterFirstRepairAsync(owed);
            using (var unbound = NewRig(folder, Pair(bound: false)))
                await InLive(unbound, session => UnboundAsync(unbound, session));
            using (var disabled = NewRig(folder, Pair(enabled: false)))
                await InLive(disabled, session => DisabledAsync(disabled, session));
            CoverageCarriers();
            List<Exception> failures = [];
            foreach (var check in new Action[] { ConcurrentReservationBeforeAdmission, ConcurrentReservationUnderLocalDebt, RepairOverlapsLiveWork, RemoteEditThenFolderMove, LiveFolderReplacementSelection, ReplacementIdentityDrain, MovedOutOfSync, CapturedCoverage,
                KeepBothSelection, PersistentFailurePass, StalePrimarySelection, SplitRenameSelection,
                RenameIntoNewFolder, AncestorThenRequestedSelection, HeldRenameSelections, VanishedSelection,
                LockedSelectionContinues, RestatingFolderWithOppositeDelete })
            {
                Console.WriteLine("START " + check.Method.Name);
                try { check(); Console.WriteLine("PASS " + check.Method.Name); }
                catch (Exception error) { failures.Add(new InvalidOperationException(check.Method.Name, error)); }
            }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void ConcurrentReservationBeforeAdmission() => ConcurrentReservation(null);

    // Design 4.3 row 11 through the production LiveExecutionBoundary and EngineHost (SQLite scenario store only): with
    // Local lost-interval debt owed, a conflicting subject is still refused before its callback, the independent new
    // download still publishes while the upload is gated, and the debt stays owed.
    internal static void ConcurrentReservationUnderLocalDebt() => ConcurrentReservation(SourceObligation.NormalRepair);

    private static void ConcurrentReservation(SourceObligation? debt)
    {
        using var scenario = Scenarios.Scenario.Begin("concurrent-reservation" + (debt is null ? "" : "-debt"));
        scenario.LocalFile("Upload.txt", "baseline");
        scenario.RepairAsync();
        if (debt is { } owed)
            Tests.Executor.ChangeRunnerChecks.OweLocal(scenario.Store, scenario.Pair.PairId,
                scenario.Store.ReadPairExecution(scenario.Pair.PairId)!.Value.Generation, owed);
        scenario.LocalFile("Upload.txt", "changed");
        scenario.DropboxFile("Independent.txt", "download");
        scenario.QueueRemoteChanges("Independent.txt");
        scenario.InSession("concurrent-reservation", session =>
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ct = guard.Token;
            var pairId = session.Pair.PairId;
            session.Host.CatchUpLocalAsync(pairId, session.ExpectedGeneration, ct).GetAwaiter().GetResult();
            session.Host.CatchUpDropboxAsync(session.Registration, pairId, session.ExpectedGeneration, ct).GetAwaiter().GetResult();
            Check.Require(PendingAt(session, Side.Dropbox, "Independent.txt").Any(),
                "A1: the independent download is acquired before the upload is gated");
            var entry = session.Local.ReadOneAsync("Upload.txt", ct).GetAwaiter().GetResult()!;
            var saved = session.Store.SyncedByKey(pairId, Side.Local, PathNames.Key(entry.Path))!;
            var remote = session.Dropbox.ReadOneAsync("Upload.txt", ct).GetAwaiter().GetResult()!;
            var selected = session.Store.SelectWork(pairId, session.ExpectedGeneration, session.Scope)!;
            session.Store.EndWorkPass(pairId);
            var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var independent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var context = session.Context with
            {
                Dropbox = new ProxySide(session.Dropbox)
                {
                    BeforeStage = (path, token) =>
                    {
                        if (path != "Upload.txt") return Task.CompletedTask;
                        staged.TrySetResult();
                        return release.Task.WaitAsync(token);
                    }
                },
                Local = new ProxySide(session.Local)
                {
                    AfterPublish = item => { if (PathNames.Key(item.Path) == PathNames.Key("Independent.txt")) independent.TrySetResult(); }
                }
            };
            var syncer = new Syncer(context, new SyncPorts(new RecoveryExecutionBoundary(() => session),
                new LiveExecutionBoundary(_ => session), PairSafety.SelectedBoundaryFor(session), 1));
            var run = syncer.SyncWorkAsync(ct);
            try
            {
                staged.Task.WaitAsync(ct).GetAwaiter().GetResult();
                var callback = false;
                var subject = new IssueSubject([new IssueItem(Side.Local, entry.Path, entry.Id),
                    new IssueItem(Side.Dropbox, remote.Path, remote.Id)], null);
                try
                {
                    session.ItemAttempts.ExecuteAsync(selected, null, subject, (_, _) =>
                    {
                        callback = true;
                        return Task.FromResult(true);
                    }, ct).GetAwaiter().GetResult();
                    throw new InvalidOperationException("A1: an intersecting host reservation must refuse");
                }
                catch (LiveAdmissionStaleException error)
                {
                    Check.Require(error.Reason == LiveAdmissionStaleReason.SelectionChanged && !callback,
                        "A1: real EngineHost refuses an overlapping subject before any admitted callback");
                }
                independent.Task.WaitAsync(ct).GetAwaiter().GetResult();
                Check.Require(!run.IsCompleted, "A1: independent download publishes while upload remains gated");
                release.TrySetResult();
                run.WaitAsync(ct).GetAwaiter().GetResult();
                Check.Require(session.Store.SyncedByKey(pairId, Side.Local, saved.LocalKey)?.LocalSize == entry.Size,
                    "A1: the reserved upload commits after its gate is released");
                Check.Require(debt is null || session.Store.ReadLiveRefusal(pairId) == LiveRefusal.LocalWrites,
                    "B-140B independent and gated work under Local debt leave the debt owed");
            }
            finally
            {
                release.TrySetResult();
                guard.Cancel();
                try { run.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) when (guard.IsCancellationRequested) { }
            }
        });
    }

    // Design 4.3 row 11: a real Repair, run through the production host with Local debt owed, holds its upload of
    // Upload.txt in flight while live work runs through the production LiveExecutionBoundary and the host's real
    // adaptive limiter, started at the shipped level 1 and at 4. At both levels the conflicting live upload never runs
    // beside the Repair, the independent new download publishes while the Repair is held, the Repair's own upload
    // lands once, and the live pass does not spin.
    internal static void RepairOverlapsLiveWork()
    {
        foreach (var level in new[] { 1, 4 }) RepairOverlapsLiveWork(level);
    }

    private static void RepairOverlapsLiveWork(int level)
    {
        using var scenario = Scenarios.Scenario.Begin("repair-overlaps-live-" + level, limiterLevel: level);
        try { RepairOverlapsLiveWork(scenario, level); }
        catch (Exception error)
        {
            scenario.Fail(error);
            throw;
        }
    }

    private static void RepairOverlapsLiveWork(Scenarios.Scenario scenario, int level)
    {
        var label = "; initial limiter level=" + level;
        scenario.LocalFile("Upload.txt", "baseline");
        scenario.RepairAsync();
        var pair = scenario.Pair.PairId;
        Tests.Executor.ChangeRunnerChecks.OweLocal(scenario.Store, pair,
            scenario.Store.ReadPairExecution(pair)!.Value.Generation, SourceObligation.NormalRepair);
        scenario.LocalFile("Upload.txt", "changed");
        var uploads = 0;
        var held = 0;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.RequestException = route =>
        {
            if (!route.EndsWith("/upload", StringComparison.Ordinal)) return null;
            if (Interlocked.Increment(ref uploads) != 1) return null;
            entered.Set();
            // A hold that lapses would let the overlap pass unobserved, so it fails the upload instead.
            try { release.Wait(lifetime.Token); }
            catch (OperationCanceledException) { Interlocked.Exchange(ref held, -1); return new TimeoutException(); }
            Interlocked.Exchange(ref held, 1);
            return null;
        };
        var repair = Task.Run(() => scenario.RepairAsync(lifetime: lifetime.Token));
        Exception? failure = null;
        RepairSummary? summary = null;
        var passes = 0;
        try
        {
            Check.Require(entered.Wait(TimeSpan.FromSeconds(30), lifetime.Token),
                "Row 11: the Repair's upload is in flight" + label);
            scenario.DropboxFile("Independent.txt", "download");
            scenario.QueueRemoteChanges("Independent.txt");
            passes = scenario.EndedWorkPasses;
            var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scenario.InSession("repair-overlaps-live", session =>
            {
                using var guard = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                guard.CancelAfter(TimeSpan.FromSeconds(30));
                var ct = guard.Token;
                session.Host.CatchUpLocalAsync(pair, session.ExpectedGeneration, ct).GetAwaiter().GetResult();
                session.Host.CatchUpDropboxAsync(session.Registration, pair, session.ExpectedGeneration, ct)
                    .GetAwaiter().GetResult();
                // The conflicting live selection reaches production admission while the Repair still holds its upload.
                scenario.BeforeLiveValidation = selection =>
                {
                    if (selection.Key == PathNames.KeyFor(selection.Side, "Upload.txt") && Volatile.Read(ref held) == 0)
                        attempted.TrySetResult();
                };
                var independent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var context = session.Context with
                {
                    Local = new ProxySide(session.Local)
                    {
                        AfterPublish = item =>
                        {
                            if (PathNames.Key(item.Path) == PathNames.Key("Independent.txt")) independent.TrySetResult();
                        }
                    }
                };
                var syncer = new Syncer(context, new SyncPorts(new RecoveryExecutionBoundary(() => session),
                    new LiveExecutionBoundary(_ => session), PairSafety.SelectedBoundaryFor(session), 1));
                var run = syncer.SyncWorkAsync(ct);
                Exception? first = null;
                try
                {
                    // A held Repair must not starve live work, even from the shipped starting level of one permit.
                    independent.Task.WaitAsync(ct).GetAwaiter().GetResult();
                    attempted.Task.WaitAsync(ct).GetAwaiter().GetResult();
                    Check.Require(!repair.IsCompleted && Volatile.Read(ref uploads) == 1 && Volatile.Read(ref held) == 0,
                        "Row 11: the independent live download publishes while the Repair holds the conflicting " +
                        "upload, and no live upload runs beside it: uploads=" + Volatile.Read(ref uploads) + label);
                    // Exact, not a bound: during the hold the conflicting lane waits either for an ordinary permit or
                    // for the Upload.txt path lock the held Repair upload owns (ItemDispatch.ExecuteUploadAsync;
                    // AdmittedExecution.AcquireAsync), and a pass ends only once every lane joins
                    // (Syncer.RunLanesAsync). So no pass should end while the Repair is held; one that does means
                    // that lane finished or was dropped early, which this check reports.
                    Check.Require(scenario.EndedWorkPasses == passes,
                        "Row 11: the live run ends no pass while the Repair holds the footprint: passes=" +
                        (scenario.EndedWorkPasses - passes) + label);
                    release.Set();
                    run.WaitAsync(ct).GetAwaiter().GetResult();
                }
                catch (Exception error)
                {
                    first = error;
                    throw;
                }
                finally
                {
                    release.Set();
                    if (!run.IsCompleted) guard.Cancel();
                    try { run.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) when (guard.IsCancellationRequested) { }
                    catch (Exception) when (first is not null) { }
                }
            });
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            release.Set();
            // A failed overlap cancels the Repair it owns before joining it, so the join waits only for the Repair
            // to observe that cancellation; nothing here bounds how long that takes.
            if (failure is not null) lifetime.Cancel();
            try { summary = repair.GetAwaiter().GetResult(); }
            catch (Exception repairError) { failure ??= repairError; }
            scenario.RequestException = null;
            scenario.BeforeLiveValidation = null;
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        var uploaded = scenario.Account.Snapshot()
            .Single(item => item.Path.EndsWith("Upload.txt", StringComparison.Ordinal));
        Check.Require(summary!.Completed && uploads == 1 && held == 1 &&
            System.Text.Encoding.UTF8.GetString(uploaded.Bytes) == "changed" &&
            System.IO.File.Exists(Path.Combine(scenario.LocalRoot, "Independent.txt")) && !scenario.Store.Obligated(pair),
            "Row 11: the Repair's upload lands once, the independent download stays, and the Repair pays the " +
            "debt: uploads=" + uploads + "; held=" + held + label);
    }

    private static RowContexts Rows(PairSession session) => RowContexts.ForSelection(session.Context);

    // F124: For reads the existing scope, hold and first-run facts; it never invents a full scan, and a run-local
    // dependent hold outranks every ordinary hold without manufacturing an issue.
    private static Task FactsAsync(Rig rig, PairSession session)
    {
        rig.Inner.Commit(rig.Pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Tie, TextKey.TieEqualTimes, HeldPath,
                new IssueSubject([new IssueItem(Side.Local, HeldPath, HeldId)], null))]
        });
        var issues = rig.Inner.Issues(rig.Pair.PairId).Count;
        var context = Rows(session);

        var ordinary = context.For(Make(PathNames.Key("Plain.txt"), File("Plain.txt")));
        Check.Require(ordinary is { FirstRun: true, CompleteInventory: false, DeletionEligible: true },
            "a live row reads the store's first-run fact, never claims a complete inventory and waits for one");
        Check.Require(ReferenceEquals(context.For(Make(PathNames.Key("Both.txt"), File("Both.txt"), File("Both.txt"))),
                context.For(Make(PathNames.Key("Other.txt"), File("Other.txt"), File("Other.txt")))),
            "ordinary rows share the one neutral context the existing owner returns");
        Check.Require(context.For(Make(PathNames.Key("Excluded/a.txt"), File("Excluded/a.txt"))).OutOfScope,
            "a live row uses the pair's PathScope exclusions");
        Check.Require(context.For(Make(PathNames.Key("Skip/a.txt"), File("Skip/a.txt", nameof(TextKey.SkipUnreadable))))
                .Skipped,
            "a live row carries an entry's own skip");
        Check.Require(context.For(Make(PathNames.Key(HeldPath + "/child.txt"), File(HeldPath + "/child.txt"))) is
            { Held: true, HeldKind: IssueKind.Tie }, "a live row carries the store's stored hold kinds");

        var saved = Synced("Saved Local", "Saved Remote");
        var failed = Make(PathNames.Key("JoinKey"), File("Native/Local"), File("Native/Remote"), saved);
        context.HoldDependents(failed);
        Check.Require(rig.Inner.Issues(rig.Pair.PairId).Count == issues,
            "DependentFailure creates no issue of its own");
        Check.Require(context.For(Make(PathNames.Key("Native/Local"), File("Native/Local"))).HeldKind ==
            IssueKind.Failure, "DependentFailure holds a failed prerequisite's current Local alias as Failure");
        Check.Require(context.For(Make(PathNames.Key("JoinKey/child.txt"), File("JoinKey/child.txt"))).HeldKind ==
            IssueKind.Failure, "DependentFailure holds the failed row's descendants as Failure");
        Check.Require(context.For(Make(PathNames.Key("Saved Local"), synced: saved)).HeldKind == IssueKind.Failure,
            "DependentFailure holds a saved alias of the failed row");
        Check.Require(context.For(Make(PathNames.Key(HeldPath + "/other.txt"), File(HeldPath + "/other.txt")))
            .HeldKind == IssueKind.Tie, "DependentFailure leaves an unrelated stored hold unchanged");
        var free = context.For(Make(PathNames.Key("Free.txt"), File("Free.txt")));
        Check.Require(!free.Held && free.HeldKind is null, "DependentFailure leaves an unrelated row unheld");
        return Task.CompletedTask;
    }

    // Malformed requests refuse before source acquisition; genuine capture is exercised by LiveRunChecks.
    private static async Task CaptureAsync(Rig rig)
    {
        var seeded = SeedCompleted(rig);
        await InLive(rig, async session =>
        {
            Check.Require(!Rows(session).For(Make(PathNames.Key("Plain.txt"), File("Plain.txt"))).FirstRun,
                "a completed repair removes the first-run fact while the live inventory stays incomplete");
            var boundary = new LiveExecutionBoundary(_ => session);
            foreach (var selection in new[]
            {
                seeded.Selection with { PairId = OtherPairId },
                seeded.Selection with { PairGeneration = Generation + 1 }
            })
            {
                rig.Counters.Reset();
                await RequireStale(() => boundary.CaptureCoverageAsync(selection, seeded.Footprint,
                    CancellationToken.None), LiveAdmissionStaleReason.SelectionChanged,
                    "a foreign pair or generation refuses before source capture");
                Check.Require(rig.Counters.Commands == 0,
                    "malformed capture performs no store command");
            }
            await Refuses<ArgumentNullException>(() => boundary.CaptureCoverageAsync(seeded.Selection, null!,
                CancellationToken.None), "capture refuses a null footprint");
        });
    }

    private static async Task AdvancedGenerationAsync(Rig rig)
    {
        var seeded = SeedCompleted(rig);
        await InLive(rig, async session =>
        {
            var boundary = new LiveExecutionBoundary(_ => session);
            rig.Inner.SavePairDecision(rig.Pair, Generation, rig.Pair with { Enabled = false }, null, false, null);
            await RequireStale(() => boundary.CaptureCoverageAsync(seeded.Selection, seeded.Footprint,
                CancellationToken.None), LiveAdmissionStaleReason.CoverageChanged,
                "a capture from an advanced store generation refuses as CoverageChanged");
        });
    }

    // F304/F305: supplied coverage is forwarded to the one validation owner, never recaptured, and every stale pair,
    // selection, batch or unowned recovery operation refuses before the effect callback.
    private static async Task BoundaryCasesAsync(PairSession session,
        (WorkSelection Selection, LocalCatchUp Local, DropboxCatchUp Dropbox, LiveEffectScope[] Footprint) seeded)
    {
        var coverage = new LiveCoverage(seeded.Local, seeded.Dropbox);
        var boundary = new LiveExecutionBoundary(_ => session);
        var ran = false;
        Func<IAdmittedExecution, CancellationToken, Task<bool>> execute =
            (_, _) => { ran = true; return Task.FromResult(true); };
        var request = new LiveExecutionRequest(seeded.Selection, coverage, seeded.Footprint);

        await RequireStale(() => boundary.ExecuteAsync(
            [request with { Selection = seeded.Selection with { PairId = OtherPairId } }], execute,
            CancellationToken.None), LiveAdmissionStaleReason.SelectionChanged,
            "a boundary request for another pair refuses before the callback");
        await RequireStale(() => boundary.ExecuteAsync(
            [request with { Selection = seeded.Selection with { PairGeneration = Generation + 1 } }], execute,
            CancellationToken.None), LiveAdmissionStaleReason.SelectionChanged,
            "a boundary request for another generation refuses before the callback");
        await Refuses<ArgumentException>(() => boundary.ExecuteAsync([], execute, CancellationToken.None),
            "an empty live request list is refused");
        await RequireStale(() => boundary.ExecuteAsync(
            [request with { Coverage = coverage with { Local = seeded.Local with { ThroughWorkId = 0 } } }],
            execute, CancellationToken.None), LiveAdmissionStaleReason.CoverageChanged,
            "the supplied coverage reaches the one validation owner and a stale one refuses");
        await RequireStale(() => boundary.ExecuteAsync(
            [request with { Footprint = [] }], execute, CancellationToken.None),
            LiveAdmissionStaleReason.FootprintChanged,
            "the supplied footprint reaches the one validation owner and an empty one refuses");
        await RequireStale(() => boundary.ExecuteAsync(
            [request, request with { Footprint = [] }], execute, CancellationToken.None),
            LiveAdmissionStaleReason.FootprintChanged, "a second stale endpoint refuses the batch");
        Check.Require(!ran, "no refused boundary request reached its effect callback");
    }

    private static async Task RecoveryAsync(PairSession session)
    {
        var boundary = new RecoveryExecutionBoundary(() => session);
        var ran = false;
        Func<IAdmittedExecution, CancellationToken, Task<bool>> execute =
            (_, _) => { ran = true; return Task.FromResult(true); };
        await RequireStale(() => boundary.ExecuteAsync(Operation(), execute, CancellationToken.None),
            LiveAdmissionStaleReason.SelectionChanged,
            "a recovery boundary refuses a durable operation its store does not own");
        Check.Require(!ran, "a refused recovery boundary reached no effect callback");
    }

    // CV-ENTRY/B-140B: an unresolved source obligation is not a pair refusal; coverage that admits no acquired work
    // still refuses before the effect callback.
    private static async Task ObligatedAsync(Rig rig, PairSession session)
    {
        var boundary = new LiveExecutionBoundary(_ => session);
        var ran = false;
        Func<IAdmittedExecution, CancellationToken, Task<bool>> execute =
            (_, _) => { ran = true; return Task.FromResult(true); };
        await RequireStale(() => boundary.ExecuteAsync(
            [new LiveExecutionRequest(Manual(rig), Unminted(), [])], execute, CancellationToken.None),
            LiveAdmissionStaleReason.CoverageChanged,
            "coverage behind the selection refuses the live callback");
        Check.Require(!ran && rig.Inner.Obligated(rig.Pair.PairId) &&
            rig.Inner.ReadLiveRefusal(rig.Pair.PairId) != LiveRefusal.Pair,
            "an obligated pair is not refused for its obligation, and unminted coverage reached no effect callback");
    }

    // R6 (D-1105-5): after the first Repair, owed Repair debt no longer refuses live admission; the request reaches
    // the coverage owner, whose own refusal (an empty footprint) is the observable proof.
    private static async Task OwedAfterFirstRepairAsync(Rig rig)
    {
        var seeded = SeedCompleted(rig);
        var remote = rig.Inner.ReadSource(rig.Pair.PairId, Side.Dropbox)!.Value;
        rig.Inner.AdvanceSource(rig.Pair.PairId, Side.Dropbox, Generation, remote.Epoch, remote.Revision,
            remote.Provenance, remote.Cursor, remote.Epoch, remote.Revision, remote.Provenance, remote.Cursor,
            SourceObligation.NormalRepair);
        Check.Require(rig.Inner.Obligated(rig.Pair.PairId), "R6 fixture owes Repair after its first completion");
        await InLive(rig, async session =>
        {
            var boundary = new LiveExecutionBoundary(_ => session);
            var request = new LiveExecutionRequest(seeded.Selection, new LiveCoverage(seeded.Local, seeded.Dropbox), []);
            await RequireStale(() => boundary.ExecuteAsync([request], (_, _) => Task.FromResult(true),
                CancellationToken.None), LiveAdmissionStaleReason.FootprintChanged,
                "R6 owed Repair after the first completion reaches the coverage owner instead of refusing the pair");
        });
    }

    // CV-ENTRY: neither root may be unbound or disabled when a live effect is admitted.
    private static async Task UnboundAsync(Rig rig, PairSession session)
    {
        var boundary = new LiveExecutionBoundary(_ => session);
        var ran = false;
        Func<IAdmittedExecution, CancellationToken, Task<bool>> execute =
            (_, _) => { ran = true; return Task.FromResult(true); };
        var error = await Refuses<EngineRequestException>(() => boundary.ExecuteAsync(
            [new LiveExecutionRequest(Manual(rig), Unminted(), [])], execute, CancellationToken.None),
            "an unbound pair refuses a live effect before the callback");
        Check.Require(error.Key == TextKey.RootUnbound && !ran,
            "an unbound pair refuses with RootUnbound before the effect callback (observed " + error.Key + ")");
    }

    private static async Task DisabledAsync(Rig rig, PairSession session)
    {
        var boundary = new LiveExecutionBoundary(_ => session);
        var ran = false;
        Func<IAdmittedExecution, CancellationToken, Task<bool>> execute =
            (_, _) => { ran = true; return Task.FromResult(true); };
        var error = await Refuses<EngineRequestException>(() => boundary.ExecuteAsync(
            [new LiveExecutionRequest(Manual(rig), Unminted(), [])], execute, CancellationToken.None),
            "a disabled pair refuses a live effect before the callback");
        Check.Require(error.Key == TextKey.PairDisabled && !ran,
            "a disabled pair refuses with PairDisabled before the effect callback (observed " + error.Key + ")");
    }

    private static void SyncWork(PairSession session)
    {
        // Setup inspects selections; production must own a fresh complete pass, not its leftover traversal.
        session.Store.EndWorkPass(session.Pair.PairId);
        session.Syncer.SyncWorkAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    // PendingAncestors omits held work unless a coverage watermark is supplied; 0 is below every work id, so the
    // in-memory store sees exactly the acquired rows the retired identity-keyed read returned.
    private static IReadOnlyList<WorkSelection> PendingKeys(PairSession session, Side side, params string[] keys) =>
        session.Store.PendingAncestors(session.Pair.PairId, session.ExpectedGeneration, side, keys, 0).ToArray();

    private static IReadOnlyList<WorkSelection> PendingAt(PairSession session, Side side, params string[] paths) =>
        PendingKeys(session, side, paths.Select(path => PathNames.KeyFor(side, path)).ToArray());

    private static void CapturedCoverage()
    {
        using var scenario = Scenarios.Scenario.Begin("captured-coverage");
        scenario.LocalFile(ChildPath, "before");
        scenario.RepairAsync();
        scenario.LocalFile(ChildPath, "after");
        scenario.LocalFile("Sibling.txt", "sibling");
        scenario.InSession("capture", session =>
        {
            var pairId = session.Pair.PairId;
            var generation = session.ExpectedGeneration;
            session.Host.CatchUpLocalAsync(pairId, generation, CancellationToken.None).GetAwaiter().GetResult();
            var selection = session.Store.SelectWork(pairId, generation, session.Scope) ??
                throw new InvalidOperationException("the journal change must be selectable");
            Check.Require(session.Store.BeginSelection(selection), "Selection setup must be admitted.");
            var coverage = new LiveExecutionBoundary(_ => session).CaptureCoverageAsync(selection,
                [new LiveEffectScope(Side.Local, Path.Combine(session.Pair.LocalRoot, ChildPath), false)],
                CancellationToken.None).GetAwaiter().GetResult();
            var local = session.Store.ReadSource(pairId, Side.Local)!.Value;
            var remote = session.Store.ReadSource(pairId, Side.Dropbox)!.Value;
            var domain = session.Pair.Roots!.Local.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture);
            var journal = session.Store.ReadJournal(domain)!;
            Check.Require(coverage.Local.PairId == pairId && coverage.Dropbox.PairId == pairId &&
                coverage.Local.PairGeneration == generation && coverage.Dropbox.PairGeneration == generation &&
                coverage.Local.SourceEpoch == local.Epoch && coverage.Dropbox.SourceEpoch == remote.Epoch &&
                coverage.Local.Checkpoint.JournalId == journal.JournalId &&
                (ulong)coverage.Local.Checkpoint.NextUsn == journal.NextUsn &&
                coverage.Dropbox.Cursor == remote.Cursor,
                "real capture carries the committed pair, generation, source epochs, journal and cursor unchanged");
            BoundaryCasesAsync(session, (selection, coverage.Local, coverage.Dropbox,
                [new LiveEffectScope(Side.Local, Path.Combine(session.Pair.LocalRoot, ChildPath), false)]))
                .GetAwaiter().GetResult();
            var entry = session.Local.ReadOneAsync(ChildPath, CancellationToken.None).GetAwaiter().GetResult()!;
            session.Store.Commit(pairId, StateChange.Empty with
            {
                Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, ChildPath,
                    new IssueSubject([new IssueItem(Side.Local, ChildPath, entry.Id)], null))]
            });
            scenario.BeforeLiveValidation = admitted => Check.Require(admitted.Identity != entry.Id,
                "A13: the held selection never enters production live admission");
            SyncWork(session);
            scenario.BeforeLiveValidation = null;
            Check.Require(PendingAt(session, Side.Local, ChildPath).Any(),
                "A13: the real selection retains its held branch while unrelated work completes");
            Check.Require(scenario.DropboxFiles().ContainsKey("Sibling.txt") &&
                System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()[ChildPath]) == "before",
                "A13: the sibling runs while the held branch has no provider effect");
            session.Store.Commit(pairId, StateChange.Empty with
            {
                Close = [(ChildPath, new[] { IssueKind.NameClash })]
            });
            var released = session.Store.SelectWork(pairId, generation, session.Scope);
            Check.Require(released is not null && released.Key == PathNames.Key(ChildPath),
                "A65: closing the notice makes the attached branch selectable in the real store");
            SyncWork(session);
            Check.Require(!PendingAt(session, Side.Local, ChildPath).Any() &&
                System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()[ChildPath]) == "after",
                "A65: the released residual is re-decided and executed through production admission");
        });
    }

    private static void LockedSelectionContinues()
    {
        using var scenario = Scenarios.Scenario.Begin("locked-selection-continues");
        scenario.LocalFile("Locked.txt", "before");
        scenario.RepairAsync();
        scenario.LocalFile("Locked.txt", "edited");
        scenario.LocalFile("Other.txt", "independent");
        scenario.InSession("locked-selection", session =>
        {
            session.Host.CatchUpLocalAsync(session.Pair.PairId, session.ExpectedGeneration, default).GetAwaiter().GetResult();
            session.Store.EndWorkPass(session.Pair.PairId);
            using (scenario.DenyLocalContent("Locked.txt"))
            {
                session.Syncer.SyncWorkAsync(default).GetAwaiter().GetResult();
                Check.Require(session.Store.Issues(session.Pair.PairId).Any(issue =>
                        issue.Path == "Locked.txt" && IssuePolicy.IsLocalAccessRetryCause(issue.Cause)),
                    "A live locked hash read records its in-use Failure without faulting the host dispatch.");
                var issue = session.Store.Issues(session.Pair.PairId).Single(item => item.Path == "Locked.txt" &&
                    IssuePolicy.IsLocalAccessRetryCause(item.Cause));
                var episode = session.Store.ReadItemRetryState(session.Pair.PairId, [issue.Id])[issue.Id]!;
                var selected = PendingAt(session, Side.Local, "Locked.txt").Single();
                Check.Require(episode.Token.WorkId == selected.AnchorWorkId,
                    "A selected in-use failure retains its acquired anchor, not a competing source-less episode.");
                var remote = scenario.DropboxFiles();
                Check.Require(System.Text.Encoding.UTF8.GetString(remote["Other.txt"]) == "independent" &&
                    System.Text.Encoding.UTF8.GetString(remote["Locked.txt"]) == "before",
                    "Unrelated selected work syncs while the locked row preserves both versions.");
            }
        });
        // Releasing a read-only handle need not emit CLOSE; a real write supplies the next allowed opportunity.
        scenario.LocalFile("Locked.txt", "edited");
        scenario.InSession("released-selection", session =>
        {
            session.Host.CatchUpLocalAsync(session.Pair.PairId, session.ExpectedGeneration, default).GetAwaiter().GetResult();
            session.Store.EndWorkPass(session.Pair.PairId);
            session.Syncer.SyncWorkAsync(default).GetAwaiter().GetResult();
            Check.Require(!session.Store.Issues(session.Pair.PairId).Any(issue =>
                    issue.Path == "Locked.txt" && IssuePolicy.IsLocalAccessRetryCause(issue.Cause)) &&
                System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()["Locked.txt"]) == "edited",
                "The real file CLOSE opportunity recovers the retained row in a later host dispatch.");
        });
    }

    internal static void PersistentFailurePass()
    {
        using var scenario = Scenarios.Scenario.Begin("persistent-failure-pass");
        scenario.LocalFile("File.txt", "original");
        scenario.RepairAsync();
        scenario.RefuseUpload("File.txt");
        scenario.LocalFile("File.txt", "edited");
        scenario.InSession("persistent-failure", session =>
        {
            var pairId = session.Pair.PairId;
            session.Host.CatchUpLocalAsync(pairId, session.ExpectedGeneration, CancellationToken.None).GetAwaiter().GetResult();
            var initial = session.Store.SelectWork(pairId, session.ExpectedGeneration, session.Scope) ??
                throw new InvalidOperationException("LG05: the failing file has acquired work");
            Check.Require(initial.Key == PathNames.Key("File.txt"), "LG05: the failing file is selected");
            var acquired = session.Store.ReadWork(initial!).Count();
            session.Store.EndWorkPass(pairId);
            var passes = scenario.EndedWorkPasses;
            session.Syncer.SyncWorkAsync(CancellationToken.None).GetAwaiter().GetResult();
            // Coalescing several journal rows into one residual is real progress; replacing that residual is not.
            var expectedPasses = acquired > 1 ? 2 : 1;
            Check.Require(scenario.EndedWorkPasses == passes + expectedPasses &&
                session.Store.Issues(pairId).Any(i => i.Kind == IssueKind.Failure) &&
                PendingKeys(session, initial!.Side, initial.Key).Count() == 1,
                "LG05: only initial journal coalescing earns a repeat; acquired=" + acquired + "; passes=" +
                (scenario.EndedWorkPasses - passes) + "; issues=" + string.Join(",", session.Store.Issues(pairId).Select(i => i.Kind + ":" + i.Cause)));
            var residual = PendingKeys(session, initial!.Side, initial.Key).Single().AnchorWorkId;
            var beforeNotice = session.Store.ReadWorkWatermark();
            scenario.LocalFile("File.txt", "later edit");
            session.Host.CatchUpLocalAsync(pairId, session.ExpectedGeneration, CancellationToken.None).GetAwaiter().GetResult();
            Check.Require(session.Store.ReadWorkWatermark() > beforeNotice,
                "LG05: the later edit acquires new rows rather than merely waking the residual");
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stages = 0;
            var context = session.Context with
            {
                Dropbox = new ProxySide(session.Dropbox)
                {
                    BeforeStage = (path, token) => path == "File.txt" && Interlocked.Increment(ref stages) == 1
                        ? refused.Task.WaitAsync(token) : Task.CompletedTask
                },
                Events = item =>
                {
                    session.Context.Events(item);
                    if (item.Error is LiveAdmissionStaleException && item.Path == initial.Key)
                        refused.TrySetResult();
                }
            };
            passes = scenario.EndedWorkPasses;
            var syncer = new Syncer(context, new SyncPorts(new RecoveryExecutionBoundary(() => session),
                new LiveExecutionBoundary(_ => session), PairSafety.SelectedBoundaryFor(session), 1));
            syncer.SyncWorkAsync(guard.Token).GetAwaiter().GetResult();
            // DESIGN-114 §3.6: contention without P0 progress
            // requires a serial round; its fresh-notice retirement then requires the final no-progress round.
            Check.Require(refused.Task.IsCompletedSuccessfully && scenario.EndedWorkPasses == passes + 3 &&
                PendingKeys(session, initial.Side, initial.Key).Single().AnchorWorkId == residual,
                "LG05: contention, serial coalescing and the final no-progress round preserve the same Failure residual");
            passes = scenario.EndedWorkPasses;
            session.Syncer.SyncWorkAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check.Require(scenario.EndedWorkPasses == passes + 1 &&
                PendingKeys(session, initial.Side, initial.Key).Single().AnchorWorkId == residual,
                "LG05: retrying the unchanged Failure residual earns no repeat and creates no successor");
            Check.Require(System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()["File.txt"]) == "original" &&
                System.Text.Encoding.UTF8.GetString(scenario.LocalFiles()["File.txt"]) == "later edit",
                "LG05: failed work preserves both versions while waiting for a real wake");
        });
    }

    private static void ReplacementIdentityDrain()
    {
        using var scenario = Scenarios.Scenario.Begin("replacement-identity-drain");
        scenario.LocalFile("File.txt", "original");
        scenario.RepairAsync();
        var saved = scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local, PathNames.Key("File.txt"))!;
        var replacement = Path.Combine(Path.GetDirectoryName(scenario.LocalRoot)!, "replacement.txt");
        System.IO.File.WriteAllText(replacement, "replacement bytes");
        System.IO.File.Move(replacement, Path.Combine(scenario.LocalRoot, "File.txt"), overwrite: true);
        scenario.InSession("replacement-identity-drain", session =>
        {
            var pairId = session.Pair.PairId;
            session.Host.CatchUpLocalAsync(pairId, session.ExpectedGeneration, CancellationToken.None).GetAwaiter().GetResult();
            var current = session.Local.ReadOneAsync("File.txt", CancellationToken.None).GetAwaiter().GetResult()!;
            Check.Require(current.Id != saved.LocalId &&
                PendingAt(session, Side.Local, "File.txt").Select(work => work.Identity).Distinct().Count() == 2,
                "R05: both deleted old identity and replacement identity have durable work at the same path");
            SyncWork(session);
            Check.Require(System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()["File.txt"]) == "replacement bytes" &&
                !PendingAt(session, Side.Local, "File.txt").Any(),
                "R05: both identity rows drain through ordinary sync and the replacement bytes reach Dropbox");
        });
    }

    private static void LiveFolderReplacementSelection()
    {
        foreach (var interrupted in new[] { false, true })
        {
            using var scenario = Scenarios.Scenario.Begin("live-folder-replacement-" + interrupted);
            scenario.LocalFolder("Item");
            scenario.RepairAsync();
            Directory.Delete(Path.Combine(scenario.LocalRoot, "Item"));
            scenario.LocalFile("Item", "replacement bytes");
            var refused = false;
            scenario.BeforeLiveValidation = _ =>
            {
                if (!interrupted || scenario.Dropbox.ReadOneAsync("Item", CancellationToken.None).GetAwaiter().GetResult() is not null) return;
                refused = true;
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
            };
            scenario.InSession("replace-folder", session =>
            {
                session.Host.CatchUpLocalAsync(session.Pair.PairId, session.ExpectedGeneration, CancellationToken.None).GetAwaiter().GetResult();
                SyncWork(session);
                if (interrupted)
                    Check.Require(refused && !scenario.DropboxFiles().ContainsKey("Item"),
                        "M2: an interrupted upload leaves the removed root settled and the new file unapplied");
            });
            scenario.BeforeLiveValidation = null;
            if (interrupted) Check.Require(refused, "M2: upload admission is reached independently after folder removal");
            scenario.InSession("replacement-next-opportunity", session =>
            {
                SyncWork(session);
                Check.Require(System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()["Item"]) == "replacement bytes" &&
                    session.Store.SelectWork(session.Pair.PairId, session.ExpectedGeneration, session.Scope) is null,
                    "M2: fresh opportunity after interrupted root settlement converges without a stale baseline");
            });
        }
    }

    private static void RemoteEditThenFolderMove()
    {
        using var scenario = Scenarios.Scenario.Begin("remote-edit-then-folder-move");
        scenario.LocalFile("Folder/Child.txt", "original");
        scenario.RepairAsync();
        scenario.InSession("remote-edit-then-folder-move", session =>
        {
            void Capture()
            {
                session.Host.CatchUpLocalAsync(session.Pair.PairId, session.ExpectedGeneration, CancellationToken.None).GetAwaiter().GetResult();
                session.Host.CatchUpDropboxAsync(session.Registration, session.Pair.PairId, session.ExpectedGeneration,
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            scenario.EditRemoteFile("Folder/Child.txt", "remote edited");
            Capture();
            SyncWork(session);
            Check.Require(System.Text.Encoding.UTF8.GetString(scenario.LocalFiles()["Folder/Child.txt"]) == "remote edited",
                "folder admission: remote edit settles before its folder moves");
            scenario.RenameDropbox("Folder", "Moved");
            scenario.QueueRemoteMove("Folder", "Moved", "Moved/Child.txt");
            Capture();
            SyncWork(session);
            Check.Require(System.Text.Encoding.UTF8.GetString(scenario.LocalFiles()["Moved/Child.txt"]) == "remote edited" &&
                System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()["Moved/Child.txt"]) == "remote edited" &&
                !scenario.LocalFiles().ContainsKey("Folder/Child.txt") && !scenario.DropboxFiles().ContainsKey("Folder/Child.txt"),
                "folder admission: tombstone plus remote folder move settles without a nested-lease deadlock");
            Check.Require(session.Store.SelectWork(session.Pair.PairId, session.ExpectedGeneration, session.Scope) is null,
                "folder admission: the completed folder move leaves no acquired work");
        });
    }

    private static void MovedOutOfSync()
    {
        foreach (var excluded in new[] { false, true })
        foreach (var repair in new[] { false, true })
        foreach (var edited in new[] { false, true })
        {
            using var scenario = Scenarios.Scenario.Begin($"move-out-{excluded}-{repair}-{edited}");
            if (excluded) scenario.SetExclusions(new Exclusion(Side.Local, "Excluded", true));
            scenario.LocalFile("File.txt", "original");
            scenario.RepairAsync();
            var destination = excluded ? Path.Combine(scenario.LocalRoot, "Excluded") :
                Path.Combine(Path.GetDirectoryName(scenario.LocalRoot)!, "outside");
            Directory.CreateDirectory(destination);
            System.IO.File.Move(Path.Combine(scenario.LocalRoot, "File.txt"), Path.Combine(destination, "File.txt"));
            if (edited) scenario.DropboxFile("File.txt", "remote edited");
            scenario.InSession("acquire-move-out", session =>
            {
                session.Host.CatchUpLocalAsync(session.Pair.PairId, session.ExpectedGeneration, CancellationToken.None).GetAwaiter().GetResult();
                var selection = session.Store.PendingAncestors(session.Pair.PairId, session.ExpectedGeneration,
                    Side.Local, [PathNames.Key("File.txt")]).FirstOrDefault();
                Check.Require(selection is not null, "move-out: real journal acquires the saved source identity");
                if (!excluded)
                    Check.Require(session.Store.ReadWork(selection!).Any(work => work.Notice.Kind == ChangeKind.Deleted),
                        "move-out: leaving the root is acquired as ordinary deletion, not Leave");
            });
            if (repair) scenario.RepairAsync();
            scenario.InSession("settle-move-out", session =>
            {
                SyncWork(session);
                var remote = scenario.DropboxFiles();
                Check.Require(edited ? remote.TryGetValue("File.txt", out var bytes) &&
                    System.Text.Encoding.UTF8.GetString(bytes) == "remote edited" : !remote.ContainsKey("File.txt"),
                    "move-out: unchanged counterpart deletes; edited counterpart survives ordinary conflict rules");
                Check.Require(System.IO.File.ReadAllText(Path.Combine(destination, "File.txt")) == "original",
                    "move-out: moved Local bytes outside sync remain untouched");
                if (edited)
                    Check.Require(System.IO.File.ReadAllText(Path.Combine(scenario.LocalRoot, "File.txt")) == "remote edited",
                        "move-out: Dropbox edit beats deletion and returns to the old synced path");
                Check.Require(session.Store.SelectWork(session.Pair.PairId, session.ExpectedGeneration, session.Scope) is null &&
                    !session.Store.PendingAncestors(session.Pair.PairId, session.ExpectedGeneration, Side.Local,
                        [PathNames.Key("File.txt"), PathNames.Key("Excluded/File.txt")]).Any(),
                    "move-out: no acquired source or old-path work is stranded");
            });
        }
    }

    private static void KeepBothSelection()
    {
        using var scenario = Scenarios.Scenario.Begin("keep-both-selection");
        scenario.LocalFile("File.txt", "original");
        scenario.RepairAsync();
        scenario.LocalFile("File.txt", "local edited");
        scenario.DropboxFile("File.txt", "remote edited");
        scenario.InSession("keep-both-selection", session =>
        {
            var pairId = session.Pair.PairId;
            var generation = session.ExpectedGeneration;
            session.Host.CatchUpLocalAsync(pairId, generation, CancellationToken.None).GetAwaiter().GetResult();
            var selected = session.Store.SelectWork(pairId, generation, session.Scope)!;
            Check.Require(selected is not null, "LG03: live keep-both has acquired work");
            SyncWork(session);
            Check.Require(session.Store.SelectWork(pairId, generation, session.Scope) is null,
                "LG04: successful Info leaves no residual for the next pass");
            var local = scenario.LocalFiles().Values.Select(System.Text.Encoding.UTF8.GetString).Order().ToArray();
            var remote = scenario.DropboxFiles().Values.Select(System.Text.Encoding.UTF8.GetString).Order().ToArray();
            Check.Require(local.SequenceEqual(new[] { "local edited", "remote edited" }) && remote.SequenceEqual(local),
                "LG03: exactly both versions exist on both sides, without repeated copies");
        });
    }

    private static void StalePrimarySelection()
    {
        foreach (var mode in new[] { "created", "moved-in", "edited" })
        {
            using var scenario = Scenarios.Scenario.Begin("stale-primary-" + mode);
            scenario.LocalFolder("Before");
            if (mode != "created") scenario.LocalFile("File.txt", "original");
            scenario.RepairAsync();
            scenario.InSession("stale-primary", session =>
            {
                var pairId = session.Pair.PairId;
                var generation = session.ExpectedGeneration;
                var path = mode == "edited" ? "File.txt" : "Before/File.txt";
                if (mode == "moved-in")
                    System.IO.File.Move(Path.Combine(scenario.LocalRoot, "File.txt"), Path.Combine(scenario.LocalRoot, path));
                scenario.LocalFile(path, "changed bytes");
                session.Host.CatchUpLocalAsync(pairId, generation, CancellationToken.None).GetAwaiter().GetResult();
                var firstPage = session.Store.ReadWorkWatermark();
                var target = mode == "edited" ? "Renamed.txt" : "After/File.txt";
                if (mode == "edited")
                    System.IO.File.Move(Path.Combine(scenario.LocalRoot, path), Path.Combine(scenario.LocalRoot, target));
                else Directory.Move(Path.Combine(scenario.LocalRoot, "Before"), Path.Combine(scenario.LocalRoot, "After"));
                session.Host.CatchUpLocalAsync(pairId, generation, CancellationToken.None).GetAwaiter().GetResult();
                Check.Require(session.Store.ReadWorkWatermark() > firstPage,
                    "R01: movement is acquired on a second committed page");
                var calls = scenario.Account.Calls.Count;
                SyncWork(session);
                Check.Require(System.Text.Encoding.UTF8.GetString(scenario.LocalFiles()[target]) == "changed bytes" &&
                    System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()[target]) == "changed bytes",
                    "R01: current bytes survive and reach both sides: " + mode);
                Check.Require(!scenario.Account.Calls.Skip(calls).Any(call => call.Contains("/delete", StringComparison.Ordinal)) &&
                    !PendingAt(session, Side.Local, path, target).Any() &&
                    session.Store.SelectWork(pairId, generation, session.Scope) is null,
                    "R01: no Dropbox delete, retained identity work or selectable remainder: " + mode);
            });
        }
    }

    private static void SplitRenameSelection()
    {
        foreach (var mode in new[] { "edited", "deleted", "twice", "excluded" })
        {
            using var scenario = Scenarios.Scenario.Begin("split-live-rename-" + mode);
            scenario.LocalFile("Before.txt", "content");
            if (mode == "excluded") scenario.SetExclusions(new Exclusion(Side.Local, "After.txt", false));
            scenario.RepairAsync();
            System.IO.File.Move(Path.Combine(scenario.LocalRoot, "Before.txt"), Path.Combine(scenario.LocalRoot, "After.txt"));
            if (mode == "edited") scenario.DropboxFile("Before.txt", "remote edited");
            else if (mode == "deleted") scenario.RemoveDropbox("Before.txt");
            else if (mode == "twice") scenario.RenameDropbox("Before.txt", "Other.txt");
            scenario.InSession("split-live-rename", session =>
            {
                var pairId = scenario.Pair.PairId;
                session.Host.CatchUpLocalAsync(pairId, session.ExpectedGeneration, CancellationToken.None).GetAwaiter().GetResult();
                var selection = session.Store.SelectWork(pairId, session.ExpectedGeneration, session.Scope)!;
                Check.Require(selection is not null, "F01: split rename has acquired work: " + mode);
                SyncWork(session);
                Check.Require(session.Store.SelectWork(pairId, session.ExpectedGeneration, session.Scope) is null,
                    "R03: no split work remains on the next pass: " + mode);
                Check.Require(mode == "excluded" ? !scenario.DropboxFiles().ContainsKey("After.txt") :
                    scenario.DropboxFiles().ContainsKey("After.txt"), "F01: split or leave respects the moved content's scope");
                if (mode == "edited")
                    Check.Require(System.Text.Encoding.UTF8.GetString(scenario.LocalFiles()["Before.txt"]) == "remote edited" &&
                        System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()["Before.txt"]) == "remote edited",
                        "R03: split preserves and downloads the edited old-path bytes");
            });
        }
    }

    private static void RenameIntoNewFolder()
    {
        foreach (var edited in new[] { false, true })
        {
            using var scenario = Scenarios.Scenario.Begin("rename-new-folder-" + edited);
            scenario.LocalFile("Before.txt", "content");
            scenario.RepairAsync();
            scenario.LocalFolder("Created");
            System.IO.File.Move(Path.Combine(scenario.LocalRoot, "Before.txt"), Path.Combine(scenario.LocalRoot, "Created/After.txt"));
            if (edited) scenario.LocalFile("Created/After.txt", "edited");
            scenario.InSession("rename-new-folder", session =>
            {
                var pairId = scenario.Pair.PairId;
                session.Host.CatchUpLocalAsync(pairId, session.ExpectedGeneration, CancellationToken.None).GetAwaiter().GetResult();
                var selection = session.Store.SelectWork(pairId, session.ExpectedGeneration, session.Scope)!;
                Check.Require(selection?.Key == PathNames.Key("Created"), "F02: journal orders new parent before moved file");
                var childAdmitted = false;
                scenario.BeforeLiveValidation = admitted =>
                {
                    if (admitted.Key != PathNames.Key("Created/After.txt")) return;
                    childAdmitted = true;
                    Check.Require(session.Store.ReadWork(admitted).All(work => work.Before?.LocalPath != "Created"),
                        "F02: child admission has no parent binding copied into its preimage");
                };
                SyncWork(session);
                scenario.BeforeLiveValidation = null;
                Check.Require(childAdmitted && session.Store.SelectWork(pairId, session.ExpectedGeneration, session.Scope) is null,
                    "F02: child settles under its own admission after the parent completes");
                Check.Require(System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()["Created/After.txt"]) == (edited ? "edited" : "content"),
                    "F02: walked rename preserves edited content");
            });
        }
    }

    private static void AncestorThenRequestedSelection()
    {
        List<Exception> failures = [];
        foreach (var (moveSide, editSide) in new[] { (Side.Local, Side.Local), (Side.Dropbox, Side.Dropbox), (Side.Local, Side.Dropbox) })
        foreach (var parentFirst in new[] { false, true })
        {
            try
            {
            Console.WriteLine($"START move-matrix move={moveSide} edit={editSide} parentFirst={parentFirst}");
            using var scenario = Scenarios.Scenario.Begin("ancestor-requested-" + moveSide + "-" + editSide + "-" + parentFirst);
            scenario.LocalFile("Folder/Child.txt", "content");
            scenario.RepairAsync();
            var saved = scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local, PathNames.Key("Folder/Child.txt"))!;
            void Move()
            {
                if (moveSide == Side.Local)
                    System.IO.Directory.Move(Path.Combine(scenario.LocalRoot, "Folder"), Path.Combine(scenario.LocalRoot, "Moved"));
                else
                {
                    scenario.RenameDropbox("Folder", "Moved");
                    scenario.QueueRemoteMove("Folder", "Moved", "Moved/Child.txt");
                }
            }
            void Edit(string path)
            {
                if (editSide == Side.Local) scenario.LocalFile(path, "edited");
                else scenario.EditRemoteFile(path, "edited");
            }
            scenario.InSession("ancestor-requested", session =>
            {
                var pairId = scenario.Pair.PairId;
                void Capture()
                {
                    session.Host.CatchUpLocalAsync(pairId, session.ExpectedGeneration, CancellationToken.None).GetAwaiter().GetResult();
                    session.Host.CatchUpDropboxAsync(session.Registration, pairId, session.ExpectedGeneration,
                        CancellationToken.None).GetAwaiter().GetResult();
                }
                if (parentFirst) Move(); else Edit("Folder/Child.txt");
                Capture();
                var requested = session.Store.SelectWork(pairId, session.ExpectedGeneration, session.Scope)!;
                Check.Require(requested is not null, "F05: first acquired event is selectable");
                if (!parentFirst) Check.Require(session.Store.BeginSelection(requested!), "Selection setup must be admitted.");
                if (parentFirst) Edit(moveSide == editSide ? "Moved/Child.txt" : "Folder/Child.txt"); else Move();
                Capture();
                SyncWork(session);
                Check.Require(session.Store.SelectWork(pairId, session.ExpectedGeneration, session.Scope) is null,
                    "F05: ancestor and separately acquired child drain through the production opportunity");
                Check.Require(System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()["Moved/Child.txt"]) == "edited" &&
                    System.Text.Encoding.UTF8.GetString(scenario.LocalFiles()["Moved/Child.txt"]) == "edited",
                    "F05: child's edit reaches its new path on both sides; local=" +
                    System.Text.Encoding.UTF8.GetString(scenario.LocalFiles()["Moved/Child.txt"]) + "; remote=" +
                    System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()["Moved/Child.txt"]) + "; saved=" +
                    session.Store.SyncedById(pairId, Side.Local, saved.LocalId));
            });
            }
            catch (Exception error)
            {
                Console.WriteLine($"FAIL move-matrix move={moveSide} edit={editSide} parentFirst={parentFirst}: {error.Message}");
                failures.Add(new InvalidOperationException($"move={moveSide}, edit={editSide}, parentFirst={parentFirst}", error));
                continue;
            }
            Console.WriteLine($"PASS move-matrix move={moveSide} edit={editSide} parentFirst={parentFirst}");
        }
        foreach (var leave in new[] { false, true })
            try
            {
                Console.WriteLine("START folder-move leave=" + leave);
                FolderMoveOutcome(leave);
                Console.WriteLine("PASS folder-move leave=" + leave);
            }
            catch (Exception error)
            {
                Console.WriteLine("FAIL folder-move leave=" + leave + ": " + error.Message);
                failures.Add(new InvalidOperationException("folder move leave=" + leave, error));
            }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static void RestatingFolderWithOppositeDelete()
    {
        using var scenario = Scenarios.Scenario.Begin("restating-folder-opposite-delete");
        scenario.LocalFile("Folder/Child.txt", "original");
        scenario.RepairAsync();
        scenario.LocalFile("Folder/Child.txt", "edited");
        scenario.RemoveDropbox("Folder");
        scenario.QueueRemoteMove("Folder");
        scenario.InSession("restating-folder-opposite-delete", session =>
        {
            var pair = session.Pair;
            var generation = session.ExpectedGeneration;
            session.Host.CatchUpLocalAsync(pair.PairId, generation, CancellationToken.None).GetAwaiter().GetResult();
            session.Host.CatchUpDropboxAsync(session.Registration, pair.PairId, generation,
                CancellationToken.None).GetAwaiter().GetResult();
            Check.Require(session.Store.PendingAncestors(pair.PairId, generation, Side.Dropbox,
                [PathNames.DropboxKey("Folder")], 0).Any(), "the fixture acquired the real feed's path-only folder deletion");
            var folder = session.Local.ReadOneAsync("Folder", CancellationToken.None).GetAwaiter().GetResult()!;
            var source = session.Store.ReadSource(pair.PairId, Side.Local)!.Value;
            var volume = pair.Roots!.LocalVolumeKey();
            var position = session.Store.ReadJournal(volume)!;
            session.Store.AcquireJournalPage(volume, position, position,
                [(pair.PairId, generation, source.Epoch, source.Revision, false)],
                [(pair.PairId, new ChangeNotice(Side.Local, ChangeKind.Changed, folder.Path, null,
                    folder.Id, folder, false, false))]);
            SyncWork(session);
            var remaining = session.Store.SelectWork(pair.PairId, generation, session.Scope);
            var remoteFiles = scenario.DropboxFiles();
            var localFiles = scenario.LocalFiles();
            Check.Require(remoteFiles.TryGetValue("Folder/Child.txt", out var remoteBytes) &&
                System.Text.Encoding.UTF8.GetString(remoteBytes) == "edited" &&
                localFiles.TryGetValue("Folder/Child.txt", out var localBytes) &&
                System.Text.Encoding.UTF8.GetString(localBytes) == "edited",
                "the ordinary live wake preserves and uploads the dirty child despite a restating folder and opposite deletion; " +
                "pending=" + remaining?.Side + ":" + remaining?.Key + "; issues=" +
                string.Join(",", session.Store.Issues(pair.PairId).Select(issue => issue.Path + ":" + issue.Cause)));
            Check.Require(remaining is null, "both parent notices and the dirty child drain without a Repair");
        });
    }

    private static void FolderMoveOutcome(bool leave)
    {
        using var scenario = Scenarios.Scenario.Begin("folder-move-outcome-" + leave);
        if (leave) scenario.SetExclusions(new Exclusion(Side.Local, "Moved", true));
        scenario.LocalFile("Folder/Child.txt", "content");
        scenario.RepairAsync();
        if (!leave) scenario.DropboxFile("Moved", "foreign target");
        scenario.InSession("folder-move-outcome", session =>
        {
            var pairId = scenario.Pair.PairId;
            scenario.LocalFile("Folder/Child.txt", "edited");
            session.Host.CatchUpLocalAsync(pairId, session.ExpectedGeneration, CancellationToken.None).GetAwaiter().GetResult();
            var child = session.Store.SelectWork(pairId, session.ExpectedGeneration, session.Scope) ??
                throw new InvalidOperationException("folder move outcome has pending child work");
            Check.Require(session.Store.BeginSelection(child), "Selection setup must be admitted.");
            System.IO.Directory.Move(Path.Combine(scenario.LocalRoot, "Folder"), Path.Combine(scenario.LocalRoot, "Moved"));
            session.Host.CatchUpLocalAsync(pairId, session.ExpectedGeneration, CancellationToken.None).GetAwaiter().GetResult();
            SyncWork(session);
            if (leave)
            {
                Check.Require(!PendingKeys(session, child.Side, child.Key).Any(),
                    "Leave settles the child's acquired work; remaining=" + string.Join(" | ",
                        PendingKeys(session, child.Side, child.Key).Select(work => work.Side + ":" + work.Key +
                            ":" + string.Join(",", session.Store.ReadWork(work).Select(change => change.Notice)))));
                Check.Require(!scenario.DropboxFiles().ContainsKey("Folder/Child.txt") &&
                    System.Text.Encoding.UTF8.GetString(scenario.LocalFiles()["Moved/Child.txt"]) == "edited",
                    "Leave deletes the unchanged Dropbox subtree and preserves excluded Local bytes");
            }
            else
            {
                Check.Require(System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()["Moved"]) == "foreign target" &&
                    scenario.DropboxFiles().ContainsKey("Folder/Child.txt"),
                    "folder move refuses a foreign target without touching its contents");
                Check.Require(PendingKeys(session, child.Side, child.Key).Any(),
                    "target conflict preserves unfinished child work");
            }
        });
    }

    private static void HeldRenameSelections()
    {
        foreach (var (side, heldPath, edited) in new[]
        {
            (Side.Local, string.Empty, false), (Side.Local, string.Empty, true),
            (Side.Local, "Old/a.txt", false), (Side.Local, "New/a.txt", false), (Side.Dropbox, "Mapped/a.txt", false)
        })
        {
            using var scenario = Scenarios.Scenario.Begin("held-rename-" + side + "-" + Path.GetDirectoryName(heldPath) + "-" + edited);
            scenario.LocalFile("Old/a.txt", "content");
            scenario.LocalFolder("New");
            scenario.RepairAsync();
            var pairId = scenario.Pair.PairId;
            var folder = scenario.Store.SyncedByKey(pairId, Side.Local, PathNames.Key("New"))!;
            var remoteFolder = scenario.Dropbox.ReadOneAsync("New", CancellationToken.None).GetAwaiter().GetResult()!;
            var mapped = scenario.Dropbox.MoveAsync(remoteFolder, "Mapped", CancellationToken.None).GetAwaiter().GetResult();
            scenario.Store.Commit(pairId, StateChange.Empty with
            {
                Record = [folder with { DropboxPath = mapped.Path, DropboxKey = PathNames.DropboxKey(mapped.Path), DropboxId = mapped.Id }]
            });
            var saved = scenario.Store.SyncedByKey(pairId, Side.Local, PathNames.Key("Old/a.txt"))!;
            System.IO.File.Move(Path.Combine(scenario.LocalRoot, "Old/a.txt"), Path.Combine(scenario.LocalRoot, "New/a.txt"));
            if (edited) scenario.LocalFile("New/a.txt", "edited content");
            scenario.LocalFile("Sibling.txt", "sibling");
            scenario.InSession("held-rename", session =>
            {
                session.Host.CatchUpLocalAsync(pairId, session.ExpectedGeneration, CancellationToken.None).GetAwaiter().GetResult();
                var selection = session.Store.SelectWork(pairId, session.ExpectedGeneration, session.Scope)!;
                Check.Require(selection is not null && selection.Identity == saved.LocalId,
                    "the real journal selects the moved identity before its sibling");
                if (heldPath.Length == 0)
                {
                    SyncWork(session);
                    Check.Require(!PendingKeys(session, Side.Local, selection!.Key).Any(),
                        "an ordinary never-held rename completes through the same production selection route");
                    Check.Require(System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()["Mapped/a.txt"]) == (edited ? "edited content" : "content"),
                        "the ordinary post-rename branch syncs edits at the mapped destination");
                    return;
                }
                session.Store.Commit(pairId, StateChange.Empty with
                {
                    Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, heldPath,
                        new IssueSubject([new IssueItem(side, heldPath, side == Side.Local ? saved.LocalId : saved.DropboxId)], null))]
                });
                scenario.BeforeLiveValidation = admitted => Check.Require(admitted.Identity != saved.LocalId,
                    "S16-R2: the held rename never enters production live admission: " + heldPath);
                SyncWork(session);
                scenario.BeforeLiveValidation = null;
                var residuals = PendingKeys(session, Side.Local, selection!.Key).ToArray();
                Check.Require(residuals.Length == 1 && session.Store.ReadWork(residuals[0]).Count() == 1,
                    "both held rename aliases attach the identical single residual");
                var files = scenario.DropboxFiles();
                Check.Require(files.ContainsKey("Old/a.txt") && !files.ContainsKey("Mapped/a.txt") && files.ContainsKey("Sibling.txt"),
                    "S16-R2: only the sibling has an effect while the rename is held");
                session.Store.Commit(pairId, StateChange.Empty with { Close = [(heldPath, new[] { IssueKind.NameClash })] });
                var released = session.Store.SelectWork(pairId, session.ExpectedGeneration, session.Scope);
                Check.Require(released is not null, "S16-R2: closing the exact holder selects the rename");
                SyncWork(session);
                Check.Require(!PendingKeys(session, Side.Local, selection.Key).Any(),
                    "S16-R2: closing the exact holder re-decides and retires the rename");
                Check.Require(scenario.DropboxFiles().ContainsKey("Mapped/a.txt") && !scenario.DropboxFiles().ContainsKey("Old/a.txt"),
                    "S16-R2: the released rename follows the saved parent mapping");
            });
        }
    }

    private static void VanishedSelection()
    {
        using var scenario = Scenarios.Scenario.Begin("vanished-live-selection");
        scenario.RepairAsync();
        scenario.LocalFile("Vanished.txt", "brief content");
        scenario.InSession("vanished-live-selection", session =>
        {
            var pairId = scenario.Pair.PairId;
            session.Host.CatchUpLocalAsync(pairId, session.ExpectedGeneration, CancellationToken.None).GetAwaiter().GetResult();
            var selection = session.Store.SelectWork(pairId, session.ExpectedGeneration, session.Scope);
            Check.Require(selection is not null, "the created file has acquired work before selection execution");
            System.IO.File.Delete(Path.Combine(scenario.LocalRoot, "Vanished.txt"));
            var validations = scenario.LiveValidations;
            SyncWork(session);
            Check.Require(session.Store.SelectWork(pairId, session.ExpectedGeneration, session.Scope) is null,
                "a never-synced removed file completes its empty primary branch");
            Check.Require(scenario.LiveValidations == validations,
                "an empty primary branch needs no effect admission");
        });
    }

    // F298: both carriers preserve exactly what the admission supplied.
    private static void CoverageCarriers()
    {
        var local = new LocalCatchUp(PairId, Generation, 0, new NtfsUsnCheckpoint(Volume, 0, 5, 0), 5);
        var dropbox = new DropboxCatchUp(PairId, Generation, 0, Cursor, 5);
        var coverage = new LiveCoverage(local, dropbox);
        var selection = new WorkSelection(PairId, Generation, 0, Side.Local, VolumeKey, null,
            PathNames.Key(ChildPath), 1, 5);
        var footprint = new[] { new LiveEffectScope(Side.Local, ChildPath, true) };
        var request = new LiveExecutionRequest(selection, coverage, footprint);
        Check.Require(ReferenceEquals(request.Selection, selection) &&
            ReferenceEquals(request.Coverage, coverage) && ReferenceEquals(request.Footprint, footprint),
            "a live request carries the exact supplied selection, coverage and footprint");
        var (carriedLocal, carriedDropbox) = coverage;
        Check.Require(carriedLocal == local && carriedDropbox == dropbox,
            "LiveCoverage carries both sides' committed catch-ups unchanged");
    }

    // Completes the prerequisite repair and adds one Local change so the store mints real live coverage.
    private static (WorkSelection Selection, LocalCatchUp Local, DropboxCatchUp Dropbox, LiveEffectScope[] Footprint)
        SeedCompleted(Rig rig)
    {
        var id = rig.Pair.PairId;
        // A real Repair acquires and publishes deleted history before it completes (#36: owed history obligates).
        var history = rig.Inner.BeginScan(id, Generation, true);
        rig.Inner.OpenScan(id, Side.Dropbox, ScanTestSettings.BatchRows).CompleteAsync().GetAwaiter().GetResult();
        rig.Inner.SaveDeletedHistory(id, history, Cursor);
        rig.Inner.EndScanAsync(id, history, 1).GetAwaiter().GetResult();
        var local = rig.Inner.ReadSource(id, Side.Local)!.Value;
        var remote = rig.Inner.ReadSource(id, Side.Dropbox)!.Value;
        var position = new JournalPosition(Volume, 0);
        Check.Require(rig.Inner.AcquireJournalPage(VolumeKey, null, position,
            [(id, Generation, local.Epoch, local.Revision, false)], []) is not null,
            "the live fixture installs the Local journal position");
        rig.Inner.AdvanceSource(id, Side.Dropbox, Generation, remote.Epoch, remote.Revision, null, null,
            remote.Epoch, remote.Revision, null, Cursor, null);
        var token = rig.Inner.BeginScan(id, Generation, false);
        foreach (var side in Enum.GetValues<Side>())
            rig.Inner.OpenScan(id, side, ScanTestSettings.BatchRows).CompleteAsync().GetAwaiter().GetResult();
        var startLocal = new LocalCatchUp(id, Generation, local.Epoch, new NtfsUsnCheckpoint(Volume, 0, 0, 0), 0);
        var startRemote = new DropboxCatchUp(id, Generation, remote.Epoch, Cursor, 0);
        rig.Inner.FinishRepair(id, CompletedSummary(), Generation, token, startLocal, startRemote, out _);
        var next = position with { NextUsn = 1 };
        var through = rig.Inner.AcquireJournalPage(VolumeKey, position, next,
            [(id, Generation, local.Epoch, local.Revision, false)],
            [(id, new ChangeNotice(Side.Local, ChangeKind.Changed, ChildPath, null, LocalId, null, true, false))]);
        Check.Require(through is not null, "the live fixture commits one Local change");
        var committed = through!.Value;
        var selection = rig.Inner.SelectWork(id, Generation, new PathScope(rig.Inner.Pair(id)!, rig.Inner.Pairs())) ??
            throw new InvalidOperationException("the live fixture's committed change is selectable");
        return (selection,
            new LocalCatchUp(id, Generation, local.Epoch, new NtfsUsnCheckpoint(Volume, 0, 1, 0), committed),
            new DropboxCatchUp(id, Generation, remote.Epoch, Cursor, committed),
            [new LiveEffectScope(Side.Local, Path.Combine(LocalRoot, ChildPath), false)]);
    }

    private static WorkSelection Manual(Rig rig) =>
        new(rig.Pair.PairId, Generation, 0, Side.Local, VolumeKey, null, PathNames.Key(ChildPath), 1, 1);

    private static LiveCoverage Unminted() => new(
        new LocalCatchUp(PairId, Generation, 0, new NtfsUsnCheckpoint(Volume, 0, 0, 0), 0),
        new DropboxCatchUp(PairId, Generation, 0, Cursor, 0));

    private static FolderRemovalState Operation() =>
        new(OperationId, Generation, FolderRemovalPhase.Prepared, Side.Local, Folder(ChildPath), null, null,
            Neutral, null, true, null, null);

    private static RepairSummary CompletedSummary() => new(RunId, DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch, true, false, null, false, new Dictionary<string, int>(),
        new Dictionary<string, int>(), "", false);

    private static PairConfig Pair(bool bound = true, bool enabled = true) =>
        new(PairId, PairName, LocalRoot, DropboxRoot, ConnectionId, [],
            [new Exclusion(Side.Local, ExcludedPath, true)], FirstRunPolicy.NewestWins, enabled, null,
            bound ? Binding() : null);

    private static RootBinding Binding() =>
        new(LocalRoot, DropboxRoot, ConnectionId, new DirectoryIdentity(Volume, 1, null),
            new DropboxRootIdentity(Account, RootNamespace, RootNamespace, FolderId));

    private static Rig NewRig(string folder, PairConfig pair)
    {
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            Account, RootNamespace, RootNamespace, DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        pair = pair with { ConnectionId = binding.ConnectionId,
            Roots = pair.Roots is { } roots ? roots with { ConnectionId = binding.ConnectionId } : null };
        var inner = new InMemoryStateStore();
        inner.SavePair(pair);
        var counters = new StoreCounters(inner);
        var instanceLock = PilotInstanceLock.AcquireForMaintenance(
            Path.Combine(folder, "authority-" + Guid.NewGuid().ToString("N"), "state", "lock"));
        var http = new HttpClient();
        try
        {
            new DropboxConnectionStore(instanceLock.StateAuthority).SaveNewAsync(binding).GetAwaiter().GetResult();
            var host = EngineHost.Compose(instanceLock.StateAuthority.ProductRoot, _ => { },
                instanceLock.StateAuthority, Settings, http, TimeProvider.System, _ => null, _ => counters,
                EngineHost.Mode.Direct, new EngineExternals(new SyntheticCredentials(), _ => new HttpClient()));
            host.RecomposeCurrentAsync(pair.PairId, CancellationToken.None).GetAwaiter().GetResult();
            return new Rig(pair, inner, counters, host, instanceLock, http);
        }
        catch
        {
            http.Dispose();
            instanceLock.Dispose();
            throw;
        }
    }

    private static Task<bool> InLive(Rig rig, Func<PairSession, Task> body) =>
        rig.Host.DispatchAsync(rig.Pair.PairId,
            async (registration, _, generation, _) =>
            {
                await body(Session(rig, registration, generation)).ConfigureAwait(false);
                return true;
            }, CancellationToken.None);

    private static PairSession Session(Rig rig, object registration, long generation) =>
        PairSession.Open(rig.Counters, rig.Pair, Settings, rig.Http, false, null,
            rig.Lock.StateAuthority, rig.Host, registration, generation, TimeProvider.System);

    private static Func<bool, CancellationToken, Task<(RootBinding Roots, DriveEvidence? Drive)>> ReadRoots(
        PairConfig pair) =>
        (_, _) => Task.FromResult((pair.Roots ?? Binding(), (DriveEvidence?)null));

    private static Task<DeletionEvidence> ReadNoDeletionEvidence(string path, bool isFolder,
        CancellationToken ct) => Task.FromResult<DeletionEvidence>(new DeletionEvidence.NoRetainedHistory(path));

    private static ValueTask<IAsyncDisposable> ReserveNothing(string root, string target, long bytes,
        CancellationToken ct) => ValueTask.FromResult<IAsyncDisposable>(new NoReservation());

    private static async Task RequireStale(Func<Task> action, LiveAdmissionStaleReason reason, string what)
    {
        var error = await Refuses<LiveAdmissionStaleException>(action, what);
        Check.Require(error.Reason == reason, what + " (observed reason " + error.Reason + ")");
    }

    private static async Task<T> Refuses<T>(Func<Task> action, string what) where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T error)
        {
            return error;
        }
        throw new InvalidOperationException("expected " + typeof(T).Name + ": " + what);
    }

    private static SyncedEntry Synced(string localPath, string dropboxPath) =>
        new(PathNames.Key(localPath), localPath, PathNames.DropboxKey(dropboxPath), dropboxPath, false, "", 0, 0,
            "", "");

    private static Entry File(string path, string? skip = null) =>
        new(path, false, 0, default, "", "", "", skip);

    private static Entry Folder(string path) => new(path, true, 0, default, "", "", "", null);

    private static Row Make(string key, Entry? local = null, Entry? dropbox = null, SyncedEntry? synced = null) =>
        new(key, local, dropbox, synced, [], null, Neutral);

    private sealed record Rig(PairConfig Pair, InMemoryStateStore Inner, StoreCounters Counters, EngineHost Host,
        PilotInstanceLock Lock, HttpClient Http) : IDisposable
    {
        public void Dispose()
        {
            Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Http.Dispose();
            Lock.Dispose();
        }
    }

    private sealed class NoReservation : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => default;
    }
}
