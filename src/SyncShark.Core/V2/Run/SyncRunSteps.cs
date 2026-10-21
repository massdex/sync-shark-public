using static Massdex.SyncShark.V2.RunLog;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Massdex.SyncShark.V2;

// Scan, reconcile, hash and execute helpers for SyncRun, split out to keep each file small.
public sealed partial class SyncRun
{
    // The generation kind Run opens for itself, so reconcile commits under the current run's generation.
    private const string RunKind = "run";

    // A detailed trace always goes to the run's log file; on the console it shows only with --verbose (RQ-130).

    // Carries a full inventory read's entries-read count into the run's progress log. The provider reports
    // "<N> items: <detail>"; the count becomes the phase's done, so the log reads "N so far" (the total is unknown).
    // The ProgressThrottle already rate-limits, so there is no second throttle here.
    private sealed class InventoryProgress : IProgress<string>
    {
        private readonly ProgressThrottle _throttle;
        private readonly string _phase;

        public InventoryProgress(ProgressThrottle throttle, RootDef root)
        {
            _throttle = throttle;
            _phase = RunPhases.Reading(root);
        }

        public void Report(string value) => _throttle.Report(_phase, null, LeadingCount(value), 0);

        // The first number in the message ("1,234 items: ..." or "full:3"), or zero when there is none.
        private static long LeadingCount(string value)
        {
            long count = 0;
            var started = false;
            foreach (var ch in value)
            {
                if (char.IsDigit(ch)) { count = count * 10 + (ch - '0'); started = true; }
                else if (ch is ',' or ' ') { /* keep reading a grouped number */ }
                else if (started) break;
            }
            return count;
        }
    }

    // Reports each completed reconcile resolution (observation plus durable commit) to the phase sink. Completions
    // arrive concurrently and a ProgressThrottle's mutable rate-limit state is not thread-safe, so one lock guards it.
    private sealed class ReconcileProgress(ProgressThrottle throttle, long total) : IProgress<int>
    {
        private readonly object _gate = new();

        public void Report(int done)
        {
            lock (_gate) throttle.Report(RunPhases.Reconcile, null, done, total);
        }
    }

    // A full scan carries no delete events; a delta's map holds each deleted path and its deletion time.
    private static readonly IReadOnlyDictionary<string, DateTimeOffset?> NoDeletions =
        new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);

    internal sealed record SideScan(long Gen, string CursorAfter, bool Complete, bool Full,
        IReadOnlyList<string> ChangePaths, IReadOnlyList<string> ChangeObjectIds,
        IReadOnlyDictionary<string, DateTimeOffset?> Deleted, IReadOnlyList<string> PreviousPaths)
    {
        // Full also selects stored scope for hydrated deltas; only a returned full snapshot can establish this.
        public bool ScopedFullCoverage { get; init; }
        public IReadOnlyList<string> IncompleteSubtrees { get; init; } = [];
        public IReadOnlyDictionary<string, string> IncompleteReasons { get; init; } = new Dictionary<string, string>();
        public InventoryCoverage? ObservedCoverage { get; init; }
        public long RehashThrough { get; init; }
        public IReadOnlySet<string> ReleasedDeletionEvidence { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, string> PendingDeletionEvidence { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record BothScan(SideScan A, SideScan B)
    {
        public bool AnyFull => A.Full || B.Full;
        public bool HasChanges => A.ChangePaths.Count > 0 || B.ChangePaths.Count > 0;

        public IReadOnlyList<string> Touched(IEnumerable<string> extra) => A.ChangePaths
            .Concat(B.ChangePaths).Concat(A.PreviousPaths).Concat(B.PreviousPaths).Concat(extra)
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    private sealed record ReconcileResult(IReadOnlyList<ActionResult> Uncertain, IReadOnlyList<string> Touched)
    {
        public static ReconcileResult None { get; } = new([], []);
        public IReadOnlyList<string> Protected { get; init; } = [];
        public int PendingVerification { get; init; }
    }

    internal sealed class Tally(V2Settings? settings = null)
    {
        private readonly int _failureSampleLimit = (settings ?? V2Settings.Default).FailureSampleLimit;
        public int Done;
        public int Failed;
        public int Uncertain;
        public int Skipped;
        public int Uploaded;
        public int Downloaded;
        public int Deleted;
        public int Moved;
        public int KeptBoth;
        public List<ActionResult> Failures { get; } = [];
        // Membership is also used by first-sync completion and notice closure, not just counts.
        // Share one byte of state per id instead of three independently growing hash collections.
        [Flags]
        private enum ActionState : byte { None = 0, Planned = 1, Counted = 2, Done = 4, Refused = 8, Skipped = 16, Quota = 32, DeleteSource = 64 }
        private readonly Dictionary<string, ActionState> _actions = new(StringComparer.Ordinal);
        private readonly object _actionGate = new();

        public bool WasDeleteSourceBlocked(string actionId)
        {
            lock (_actionGate) return (_actions.GetValueOrDefault(actionId) & ActionState.DeleteSource) != 0;
        }

        public bool WasQuotaBlocked(string actionId)
        {
            lock (_actionGate) return (_actions.GetValueOrDefault(actionId) & ActionState.Quota) != 0;
        }

        public bool WasDone(string actionId)
        {
            lock (_actionGate)
                return (_actions.GetValueOrDefault(actionId) & ActionState.Done) != 0;
        }

        public bool WasSettled(string actionId)
        {
            lock (_actionGate)
                return (_actions.GetValueOrDefault(actionId) & (ActionState.Done | ActionState.Refused)) != 0;
        }

        public bool WasSkipped(string actionId)
        {
            lock (_actionGate)
                return (_actions.GetValueOrDefault(actionId) & ActionState.Skipped) != 0;
        }

        public bool WasRefused(string actionId)
        {
            lock (_actionGate)
                return (_actions.GetValueOrDefault(actionId) & ActionState.Refused) != 0;
        }

        // Progress counts only the actions a plan asked for, each one terminal outcome once. The Executor's own
        // child results (the deletes of a folder delete expanded over its live contents) are not in the plan, so
        // they are counted separately for the progress line's suffix and never in the done/total.
        public int PlannedDone { get; private set; }
        public int ExtraDone { get; private set; }
        private int _total;
        private readonly Dictionary<ActionKind, (long Done, long Total)> _kinds = [];

        internal IReadOnlyList<ActionKindProgress> KindProgress()
        {
            lock (_actionGate)
                return _kinds.OrderBy(entry => entry.Key)
                    .Select(entry => new ActionKindProgress(entry.Key, entry.Value.Done, entry.Value.Total)).ToArray();
        }

        /// <summary>The planned actions named so far. It grows as a deferred lane resolves its own plans.</summary>
        public int Total => Volatile.Read(ref _total);

        /// <summary>
        /// Names the actions this run's progress may count, and grows the denominator by the ones not yet named.
        /// Safe to call from the evidence lane's workers; a repeated action id is named and counted once.
        /// </summary>
        public void Plan(IEnumerable<SyncAction> actions)
        {
            lock (_actionGate)
            {
                var added = 0;
                foreach (var action in actions)
                {
                    if (action.Kind == ActionKind.Hold) continue;
                    var state = _actions.GetValueOrDefault(action.ActionId);
                    if ((state & ActionState.Planned) != 0) continue;
                    _actions[action.ActionId] = state | ActionState.Planned;
                    var kind = _kinds.GetValueOrDefault(action.Kind);
                    _kinds[action.Kind] = (kind.Done, kind.Total + 1);
                    added++;
                }
                if (added > 0) Interlocked.Add(ref _total, added);
            }
        }

        /// <summary>The planned actions finished, never above the total the line reports.</summary>
        public int ProgressDone() => Math.Min(PlannedDone, Total);

        // Every result counts, including the Executor's own child deletes of an expanded folder delete, which are
        // not in the plan; the planned action (when found) only picks the transfer bucket.
        public void Add(PairDef pair, SyncAction? action, ActionResult result)
        {
            var refused = result.ProviderNameRefused || result.ParentNameRefused || result.Quarantined || result.DefenderGrantHeld;
            if (refused) Skipped++;
            else if (result.Outcome == Outcome.Done)
            {
                Done++;
                // Each finished effect counts in exactly one bucket: a copy or update by its direction, and
                // a delete, move or keep-both copy in its own, never lumped into the uploaded total.
                switch (action?.Kind)
                {
                    case ActionKind.CopyNew:
                    case ActionKind.Update:
                    {
                        var target = action!.Target == Side.A ? pair.A : pair.B;
                        if (string.Equals(target.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase))
                            Downloaded++;
                        else Uploaded++;
                        break;
                    }
                    case ActionKind.Move: Moved++; break;
                    case ActionKind.Delete: Deleted++; break;
                    case ActionKind.KeepBoth: KeptBoth++; break;
                }
            }
            else if (result.Outcome == Outcome.Failed) Failed++;
            else if (result.Outcome == Outcome.Uncertain) Uncertain++;
            else if (result.Outcome == Outcome.Skipped) Skipped++;
            // One terminal outcome per action: a child result shares the plan count only when it is a planned action.
            if (result.Outcome is Outcome.Done or Outcome.Failed or Outcome.Uncertain or Outcome.Skipped)
            {
                lock (_actionGate)
                {
                    var state = _actions.GetValueOrDefault(result.ActionId);
                    if (result.UploadQuotaBlocked) state |= ActionState.Quota;
                    if (result.DeleteSourceUnconfirmed) state |= ActionState.DeleteSource;
                    if ((state & ActionState.Counted) == 0)
                    {
                        if ((state & ActionState.Planned) != 0)
                        {
                            PlannedDone++;
                            if (action is not null)
                            {
                                var kind = _kinds.GetValueOrDefault(action.Kind);
                                _kinds[action.Kind] = (kind.Done + 1, kind.Total);
                            }
                        }
                        else ExtraDone++;
                    }
                    _actions[result.ActionId] = state | ActionState.Counted |
                        (refused ? ActionState.Refused : result.Outcome == Outcome.Done ? ActionState.Done :
                            result.Outcome == Outcome.Skipped ? ActionState.Skipped : ActionState.None);
                }
            }
            if (result.Outcome is Outcome.Failed or Outcome.Uncertain && Failures.Count < _failureSampleLimit)
                Failures.Add(result);
            // A skipped effect is still unfinished, but it is not a failure: keep its bounded reason in the
            // separate skip histogram so deferred work is explained without inflating the failure counts.
            if (result.Outcome is Outcome.Failed or Outcome.Uncertain)
                RunInstrumentation.Current?.RecordFailure(result.Reason);
            else if (result.Outcome == Outcome.Skipped)
                RunInstrumentation.Current?.RecordSkip(result.Reason);
        }
    }

    private async Task<ReconcileResult> ReconcileAsync(PairDef pair, ProgressThrottle throttle, CancellationToken ct,
        IReadOnlyList<string>? narrowedIgnoreRules = null)
    {
        // The same protection snapshot answers the guard and the hold-cause classification below, so a rehash/held
        // count is read once per run and the classification cannot disagree with the guard.
        var protections = LeftAlone(pair);
        var guard = ActionPredicates(pair, protections);
        var rules = SyncIgnorePolicy.Normalize((narrowedIgnoreRules ?? []).SelectMany(SyncIgnorePolicy.Parse).ToArray());
        var recoveryUnavailable = RecoverySafety.Unavailable(_endpoints.Writer(pair.A), _endpoints.Writer(pair.B)) is not null;
        bool CanReconcile(SyncAction action, ActionGuard prepared) =>
            !(recoveryUnavailable && RecoverySafety.RequiresRecovery(action)) &&
            (action.ProviderEffect?.Kind is "whole-folder-group" or "whole-folder-member" ||
            ReconcileAllowed(prepared, action) && !CarriesNewlyIgnored(pair, action, rules));
        var outstanding = _store.StreamUnresolvedIntentRows(pair.PairId);
        var exclusions = ExclusionPredicates(pair);
        var pairs = _store.Pairs();
        var owned = _productOwned(pair);
        long? retirementGeneration = null;
        var sleeping = new List<ActionResult>();
        var intents = new List<SyncAction>();
        // A resolved commit now clears a parked retry atomically, so keep this run's reconcile intents' retry rows
        // (at most 256) to preserve the existing "Skipped is retried immediately" scheduling afterwards.
        var reconcileRetries = new Dictionary<string, SyncRetry?>(StringComparer.Ordinal);
        var excludedIntents = new List<SyncAction>();
        foreach (var batch in outstanding.Chunk(256))
        {
            var heldIds = new List<string>(batch.Length);
            var releasedIds = new List<string>(batch.Length);
            var parkedRetries = new List<SyncRetry>();
            // The page carries each row's stored state, so classification never point-queries it per intent.
            var rowStates = batch.ToDictionary(row => row.Action.ActionId, row => row.State, StringComparer.Ordinal);
            foreach (var entry in GuardedActions(batch.Select(row => row.Action), guard))
            {
                ct.ThrowIfCancellationRequested();
                var action = entry.Action;
                // Archive restores have no live opposite-side source. Their dedicated verifier owns reconciliation.
                if (IsRecoveryRestoreIntent(action)) continue;
                if (PermanentIntentBoundary(pair, action, pairs, owned) is { } boundary)
                {
                    retirementGeneration ??= _store.BeginGeneration(pair.A.RootId, RunKind);
                    RetireBoundaryIntent(pair, action, boundary, retirementGeneration.Value);
                    continue;
                }
                var retry = _store.SyncRetryAt(pair.PairId, action.Path);
                // Classify protection before retry backoff: a previous failure must not put a now-held
                // item on the fast retry loop. The original intent and its evidence stay intact.
                var quarantined = IsQuarantinedIntent(pair, action);
                if (quarantined ||
                    !ReconcileAllowed(entry.Guard, action) && ReconcileAllowed(exclusions, action) &&
                    !CarriesNewlyIgnored(pair, action, rules))
                {
                    switch (quarantined ? HoldCause.Pending : HoldCauseOf(protections, action))
                    {
                        // Quarantine, Defender grants and pair-wide decisions stay pending and unsynced on their own
                        // notice. An unverified destructive effect under a link or stub (a delete, an overwrite or a
                        // move) stays pending too: retirement drops the intent without observing what it did, so a
                        // crash-interrupted apply must keep waiting for reconciliation. None of these are background
                        // verification or retry debt; release any older hold so the combined count never includes them.
                        case HoldCause.Link or HoldCause.Stub when RecoverySafety.RequiresRecovery(action):
                        case HoldCause.Pending:
                            if (rowStates[action.ActionId] == "held") releasedIds.Add(action.ActionId);
                            ParkRetry(parkedRetries, retry);
                            break;
                        // Links and left-alone stubs never clear by themselves. A non-destructive intent has no effect
                        // to observe, so retire it under the existing standing summary; a new notice would duplicate
                        // that cause (M2).
                        case HoldCause.Link:
                            retirementGeneration ??= _store.BeginGeneration(pair.A.RootId, RunKind);
                            RetireProtectedIntent(pair, action, retirementGeneration.Value, LinkRetiredReason);
                            break;
                        case HoldCause.Stub:
                            retirementGeneration ??= _store.BeginGeneration(pair.A.RootId, RunKind);
                            RetireProtectedIntent(pair, action, retirementGeneration.Value, StubRetiredReason);
                            break;
                        // Rehash/gap debt is the only cause the combined verification summary counts.
                        default:
                            if (rowStates[action.ActionId] != "held") heldIds.Add(action.ActionId);
                            ParkRetry(parkedRetries, retry);
                            break;
                    }
                    continue;
                }
                var wasHeld = rowStates[action.ActionId] == "held";
                // M1: unpark on the retry row alone. Two writers overwrite state='held' outside this path
                // (IntentTable.Commit and Record), so keying the unpark on the intent state stranded a parked row.
                // Only parking writes the shape Kind sync + Held null + MaxValue; decision and restored-held rows
                // always carry a kind or held evidence of their own.
                if (retry is { Kind: "sync", Held: null, RetryAfter: var due } && due == DateTimeOffset.MaxValue)
                {
                    retry = retry with { RetryAfter = DateTimeOffset.MinValue };
                    _store.SaveSyncRetry(pair.PairId, retry);
                }
                // Unpark before changing the classification: a crash can repeat release, never strand
                // an uncertain intent behind the old held deadline.
                if (wasHeld) releasedIds.Add(action.ActionId);
                if (retry?.RetryAfter > _time.GetUtcNow())
                {
                    sleeping.Add(new(action.ActionId, Outcome.Uncertain, null, null, null, retry.LastError));
                    continue;
                }
                if (CanReconcile(action, entry.Guard))
                {
                    if (intents.Count < 256)
                    {
                        intents.Add(action);
                        reconcileRetries[action.ActionId] = retry;
                    }
                    else sleeping.Add(new(action.ActionId, Outcome.Uncertain, null, null, null, retry?.LastError));
                }
                else if (!ReconcileAllowed(exclusions, action) || CarriesNewlyIgnored(pair, action, rules))
                {
                    if (excludedIntents.Count < 256) excludedIntents.Add(action);
                    else sleeping.Add(new(action.ActionId, Outcome.Uncertain, null, null, null, retry?.LastError));
                }
                // Recovery unavailability is a real failure, not background verification debt.
                else sleeping.Add(new(action.ActionId, Outcome.Uncertain, null, null, null,
                    "Recovery is unavailable; restore access to Recovery and retry."));
            }
            _store.SetIntentsHeld(pair.PairId, heldIds, true);
            // Classification must be durable before parking retries. A crash before this loop leaves
            // retry deadlines active, not lost; the next run safely repeats the classification.
            foreach (var retry in parkedRetries) _store.SaveSyncRetry(pair.PairId, retry);
            _store.SetIntentsHeld(pair.PairId, releasedIds, false);
        }
        var pendingVerification = RefreshHeldIntentSummary(pair);
        var excludedUncertain = (await SettleExcludedIntentsAsync(pair, excludedIntents, ct)).Concat(sleeping).ToArray();
        // The phase's real total from its first line, so progress reads n/of-the-actual-count instead of 0/0.
        throttle.Report(RunPhases.Reconcile, null, 0, intents.Count);
        if (intents.Count == 0) return new ReconcileResult(excludedUncertain, [])
        { Protected = OutstandingFootprints(pair.PairId), PendingVerification = pendingVerification };
        // An announced wait: the effects an earlier run left unconfirmed are re-checked now.
        await SyncByteProgressStream.ReportCurrentProcessAsync("Unconfirmed effects",
            $"{intents.Count} effect(s) unconfirmed; re-checking at {_time.GetUtcNow():HH:mm:ss}.", ProcessMessageKind.Notice);
        var results = await _executor.ReconcileAsync(intents, _store.BeginGeneration(pair.A.RootId, RunKind),
            _endpoints.Writer(pair.A), _endpoints.Writer(pair.B), ct,
            new ReconcileProgress(throttle, intents.Count));
        var byId = intents.ToDictionary(intent => intent.ActionId, StringComparer.Ordinal);
        var touched = new List<string>();
        foreach (var result in results)
        {
            if (!byId.TryGetValue(result.ActionId, out var action)) continue;
            var retry = reconcileRetries.TryGetValue(result.ActionId, out var known)
                ? known : _store.SyncRetryAt(pair.PairId, action.Path);
            if (result.Outcome is Outcome.Uncertain or Outcome.Failed)
                SaveFailedRetry(pair, action.Path, result.Reason, retry);
            else if (result.Outcome == Outcome.Done)
                _store.ClearSyncRetry(pair.PairId, action.Path);
            else if (result.Outcome == Outcome.Skipped)
            {
                if (retry is not null) _store.SaveSyncRetry(pair.PairId, retry with { RetryAfter = DateTimeOffset.MinValue });
                touched.Add(action.Path);
                if (action.FromPath is not null) touched.Add(action.FromPath);
            }
        }
        return new ReconcileResult(excludedUncertain.Concat(results.Where(result => result.Outcome == Outcome.Uncertain))
            .ToArray(), touched) { Protected = OutstandingFootprints(pair.PairId), PendingVerification = pendingVerification };
    }

    // Why a guard-rejected intent is held. Only Verification is background verification debt that the combined
    // summary counts; Link/Stub retire under their standing notice; Pending keeps its own notice and stays unsynced.
    private enum HoldCause { Verification, Link, Stub, Pending }

    // Mirrors ActionGuard exactly: a move or delete also rejects a path that CONTAINS a protected path, while a
    // copy rejects only a path at or under one. Source paths are always checked as plain includes.
    private static HoldCause HoldCauseOf(LinkExclusions protections, SyncAction action)
    {
        var strict = action.Kind is ActionKind.Move or ActionKind.Delete;
        bool Touches(string? path, Func<string, bool> covers, Func<string, bool> contains) =>
            path is not null && (covers(path) || strict && contains(path));
        if (Touches(action.Path, protections.CoversLink, protections.ContainsLink) ||
            Touches(action.FromPath, protections.CoversLink, protections.ContainsLink) ||
            action.Source is { } linkSource && protections.CoversLink(linkSource.Path))
            return HoldCause.Link;
        if (Touches(action.Path, protections.CoversStub, protections.ContainsStub) ||
            Touches(action.FromPath, protections.CoversStub, protections.ContainsStub) ||
            action.Source is { } stubSource && protections.CoversStub(stubSource.Path))
            return HoldCause.Stub;
        if (Touches(action.Path, protections.CoversPending, protections.ContainsPending) ||
            Touches(action.FromPath, protections.CoversPending, protections.ContainsPending) ||
            action.Source is { } pendingSource && protections.CoversPending(pendingSource.Path))
            return HoldCause.Pending;
        return HoldCause.Verification;
    }

    // A retry for the same pending effect is not an independent failure while protected. Preserve its
    // evidence/attempts, but park its deadline until protection clears.
    private static void ParkRetry(List<SyncRetry> parked, SyncRetry? retry)
    {
        if (retry is { Held: null, Kind: "sync" } && retry.RetryAfter != DateTimeOffset.MaxValue)
            parked.Add(retry with { RetryAfter = DateTimeOffset.MaxValue });
    }

    private RunReport WithReconciliation(RunReport report, ReconcileResult reconciliation) =>
        report with
        {
            Outcome = reconciliation.Uncertain.Count == 0 || report.Outcome == RunOutcomes.Stopped
                ? report.Outcome : RunOutcomes.Partial,
            PendingVerification = report.PendingVerification + reconciliation.PendingVerification,
            Held = report.Held + reconciliation.PendingVerification,
            Uncertain = report.Uncertain + reconciliation.Uncertain.Count,
            Failures = report.Failures.Concat(reconciliation.Uncertain).Take(_settings.FailureSampleLimit).ToArray()
        };

    // An unresolved effect owns its entire footprint until reconciliation proves its outcome.
    // Do not replay, overwrite, or expand an ancestor deletion across it; siblings remain runnable.
    private IReadOnlyList<string> OutstandingFootprints(string pairId) => _store.StreamUnresolvedIntents(pairId)
        .SelectMany(action => new[] { action.Path, action.FromPath, action.Source?.Path, action.ExpectedTarget?.Path })
        .Where(path => path is not null).Select(path => path!)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // The local disk walk and the remote (Dropbox) listing use different resources, so read the two sides at once.
    // Each side runs on its own task: the local walk enumerates synchronously (PilotLocalScanner commits only on a
    // completed task), so without Task.Run the first side would finish its whole walk before the second started.
    // Each side reports through its own throttle, since a ProgressThrottle's mutable rate-limit state is not safe to
    // share between threads. The store is safe for concurrent callers (SqliteStateStore serializes every operation
    // under one gate), so no store call is serialized here.
    private async Task<BothScan> ScanBothAsync(PairDef pair, ProgressThrottle throttle, CancellationToken ct,
        IReadOnlyDictionary<string, string>? inventoryCursors = null, bool firstSync = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var throttleA = throttle.Sibling();
        var throttleB = throttle.Sibling();
        var a = Task.Run(() => ScanSideAsync(pair, Side.A, throttleA, linked.Token, inventoryCursors, firstSync));
        var b = Task.Run(() => ScanSideAsync(pair, Side.B, throttleB, linked.Token, inventoryCursors, firstSync));
        try
        {
            // Wait for the first side to settle, so a side that fails stops the other at once. Awaiting Task.WhenAll
            // directly would wait for the other side to finish first, delaying (and for a side blocked on I/O,
            // preventing) the cancellation.
            if (!(await Task.WhenAny(a, b).ConfigureAwait(false)).IsCompletedSuccessfully) linked.Cancel();
            var scans = await Task.WhenAll(a, b).ConfigureAwait(false);
            return new BothScan(scans[0], scans[1]);
        }
        catch
        {
            // A side did not finish cleanly; Task.WhenAll has settled both sides, so nothing is still touching the
            // store. Report the failure that started it, never the cancellation it caused, with its original stack.
            var failureA = await SettledAsync(a).ConfigureAwait(false);
            var failureB = await SettledAsync(b).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(Failure(ct, linked.Token, failureA, failureB)).Throw();
            throw; // unreachable: Throw() always rethrows
        }
        finally
        {
            // Close both reading phases (a start line each already went out) with their end line, pass or fail.
            throttleA.Flush(!a.IsCompletedSuccessfully);
            throttleB.Flush(!b.IsCompletedSuccessfully);
        }
    }

    // Awaits one scan side to its outcome; returns the exception it threw, or null when it completed.
    private static async Task<Exception?> SettledAsync(Task<SideScan> side)
    {
        try
        {
            await side.ConfigureAwait(false);
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    // The failure a concurrent scan reports: the caller's own cancellation, else the first side's own error in
    // A-then-B order. A cancellation raised on the other side carries our linked (or the caller's) token and is
    // skipped; an exception a side threw itself — a cancellation of its own included — is kept.
    private static Exception Failure(CancellationToken ct, CancellationToken linked, Exception? a, Exception? b)
    {
        if (ct.IsCancellationRequested) return new OperationCanceledException(ct);
        foreach (var error in new[] { a, b })
            if (error is not null && !IsSiblingCancellation(error, ct, linked)) return error;
        return new OperationCanceledException();
    }

    // Whether an exception is the cancellation this scan raised on the other side, or the caller's own cancellation,
    // not a side's own failure. A default (None) token never counts, so a tokenless cancellation a side raised
    // survives; the caller's token counts only when it can be cancelled.
    private static bool IsSiblingCancellation(Exception error, CancellationToken ct, CancellationToken linked) =>
        error is OperationCanceledException oce &&
        (oce.CancellationToken == linked || (ct.CanBeCanceled && oce.CancellationToken == ct));

    private async Task<SideScan> ScanSideAsync(PairDef pair, Side side, ProgressThrottle throttle, CancellationToken ct,
        IReadOnlyDictionary<string, string>? inventoryCursors = null, bool firstSync = false)
    {
        var watch = Stopwatch.StartNew();
        var root = side == Side.A ? pair.A : pair.B;
        // Links are not narrowed here: the inventory must keep reporting each link item (decision 59).
        var predicates = Predicates(pair, LinkExclusions.None);
        var include = side == Side.A ? predicates.A : predicates.B;
        var inventory = _endpoints.Inventory(root);
        // Publish only the previous worker generation before this run observes local
        // inventory. The worker stages further releases until a later run boundary.
        if (inventory is LocalInventory && _store is ILocalGapStore gapStore)
            LocalGapWorker.PromoteDiscovery(gapStore, root, ct);
        var cursor = inventoryCursors is not null
            ? inventoryCursors.GetValueOrDefault(root.RootId)
            : _store.Cursor(root.RootId);
        var coverage = Coverage(pair, side);
        var observationStore = inventory is DropboxInventory || firstSync && inventory is LocalInventory
            ? _store as SqliteStateStore : null;
        var observation = observationStore?.ReadInventoryObservation(root);
        string[] hydrate = [];
        var observationOnly = false;
        if (observation is not null)
        {
            // Local preview observations may replay the journal, but cannot hydrate newly included
            // prefixes like Dropbox. Widening still takes the conservative full-inventory path.
            if (coverage.CanReuse(observation.Coverage) && (inventory is not LocalInventory ||
                NtfsUsnCheckpoint.TryParseCursor(observation.Cursor, out _, out _) &&
                coverage.Widened(observation.Coverage).Length == 0))
            {
                // The effect/pending cursor owns replay. Observation may be newer after an interrupted
                // prepare, but must never hide unexecuted paths from an ordinary incremental planner.
                observationOnly = cursor is null;
                cursor ??= observation.Cursor;
                hydrate = coverage.Widened(observation.Coverage);
            }
            else
            {
                Trace("inventory bootstrap: observation binding changed or ignore coverage widened");
                cursor = null;
            }
        }
        throttle.Report(RunPhases.Reading(root), null, 0, 0);
        if (cursor is null)
        {
            Trace($"scan {side}: full listing required; no compatible {(inventoryCursors is null ? "committed" : "pending")} inventory cursor");
            var gen = _store.BeginGeneration(root.RootId, "full");
            Trace($"scan {side}: full snapshot read start ({watch.ElapsedMilliseconds} ms)");
            var snapshot = await ReadFullAsync(inventory, gen, include, root, throttle, ct);
            Trace($"scan {side}: full snapshot read done ({watch.ElapsedMilliseconds} ms); storing");
            _store.WriteSnapshot(snapshot with { IncludesPath = path => include(path) && (snapshot.IncludesPath?.Invoke(path) ?? true) });
            if (inventory is LocalInventory && !string.IsNullOrEmpty(snapshot.CursorAfter))
                return await CatchUpLocalWalkAsync(pair, side, inventory, snapshot, include, root, throttle, ct);
            await inventory.VerifyPendingAsync(include, _ => { }, ct);
            if (snapshot.IncompleteSubtrees.Count == 0) CloseHydrationFallback(pair, root);
            if (firstSync && snapshot.IncompleteSubtrees.Count == 0 && snapshot.CursorAfter is { } observedCursor)
                observationStore?.SaveInventoryObservation(coverage, observedCursor);
            Trace($"scan {side}: snapshot stored ({watch.ElapsedMilliseconds} ms)");
            return new SideScan(gen, snapshot.CursorAfter, snapshot.IncompleteSubtrees.Count == 0, true, [], [],
                NoDeletions, []) { ScopedFullCoverage = snapshot.IncompleteSubtrees.Count > 0 && !snapshot.IncompleteSubtrees.Contains(string.Empty), PendingDeletionEvidence = snapshot.PendingDeletionEvidence, IncompleteSubtrees = snapshot.IncompleteSubtrees, IncompleteReasons = snapshot.IncompleteReasons, ObservedCoverage = observationStore is null ? null : coverage, RehashThrough = _store.RehashWatermark(root.RootId) };
        }
        Trace($"scan {side}: resuming {(inventoryCursors is null ? "committed" : "pending")} inventory cursor");
        var deltaGen = _store.BeginGeneration(root.RootId, "delta");
        var hydrationInvalid = false;
        if (hydrate.Length > 0 && inventory is DropboxInventory hydrating && observationStore is not null)
        {
            try
            {
                observationStore.BeginPrefixHydration(root.RootId, ct);
                await hydrating.HydratePrefixesAsync(hydrate, rows =>
                {
                    observationStore.StagePrefixHydration(root.RootId, rows, include);
                    return Task.CompletedTask;
                }, ct);
                observationStore.InstallPrefixHydration(root, side, deltaGen, hydrate, include, cursor, ct);
            }
            catch (Exception error) when (error is InvalidDataException ||
                error is DropboxApiException { StatusCode: System.Net.HttpStatusCode.Conflict })
            {
                // Cancellation/access errors are not bootstrap requests. A malformed/expired subtree
                // is: discard its cursor/coverage hints, and use the existing incomplete-safe full path.
                // Invalidate classification too: a retained pending cursor must fail the Phase1
                // guard if this full bootstrap is incomplete, rather than reuse narrower authority.
                observationStore.AbandonPrefixHydration(root);
                Trace($"inventory hydration unavailable ({error.GetType().Name}); explicit full bootstrap");
                // One notice per root: the stable root id is the notice path; the readable location is in the text.
                WriteNotices([new Notice(pair.PairId, 0, NoticeKind.Info, root.RootId,
                    $"A newly included folder in {root.RootPath} could not be inventoried incrementally. A full inventory will be attempted; if incomplete, retry or exclude the affected folder.",
                    HydrationFallbackNoticeKey)]);
                hydrationInvalid = true;
            }
        }
        var delta = hydrationInvalid
            ? new Delta(root.RootId, side, deltaGen, cursor, cursor, [], true, [])
            : inventory is DropboxInventory dropbox
                ? await dropbox.ChangesSinceAsync(cursor, deltaGen, include, ct, firstSync)
                : await inventory.ChangesSinceAsync(cursor, deltaGen, include, ct);
        // Evidence availability is not cursor invalidity. First-sync Dropbox inventory ingests
        // observed absence and resolves held paths separately with targeted revision checks.
        if (delta.CursorInvalid)
        {
            Trace($"scan {side}: full listing required; provider cursor invalid/reset or malformed feed");
            // BeginGeneration closes the discarded delta generation as incomplete, without advancing its cursor.
            var fullGen = _store.BeginGeneration(root.RootId, "full");
            Trace($"scan {side}: rescan snapshot read start ({watch.ElapsedMilliseconds} ms)");
            var rescan = await ReadFullAsync(inventory, fullGen, include, root, throttle, ct);
            Trace($"scan {side}: rescan snapshot read done ({watch.ElapsedMilliseconds} ms); storing");
            _store.WriteSnapshot(rescan with { IncludesPath = path => include(path) && (rescan.IncludesPath?.Invoke(path) ?? true) });
            if (inventory is LocalInventory && !string.IsNullOrEmpty(rescan.CursorAfter))
                return await CatchUpLocalWalkAsync(pair, side, inventory, rescan, include, root, throttle, ct);
            await inventory.VerifyPendingAsync(include, _ => { }, ct);
            if (rescan.IncompleteSubtrees.Count == 0) CloseHydrationFallback(pair, root);
            if (firstSync && rescan.IncompleteSubtrees.Count == 0 && rescan.CursorAfter is { } rescanCursor)
                observationStore?.SaveInventoryObservation(coverage, rescanCursor);
            Trace($"scan {side}: rescan snapshot stored ({watch.ElapsedMilliseconds} ms)");
            return new SideScan(fullGen, rescan.CursorAfter, rescan.IncompleteSubtrees.Count == 0, true, [], [],
                NoDeletions, []) { ScopedFullCoverage = rescan.IncompleteSubtrees.Count > 0 && !rescan.IncompleteSubtrees.Contains(string.Empty), PendingDeletionEvidence = rescan.PendingDeletionEvidence, IncompleteSubtrees = rescan.IncompleteSubtrees, IncompleteReasons = rescan.IncompleteReasons, ObservedCoverage = observationStore is null ? null : coverage, RehashThrough = _store.RehashWatermark(root.RootId) };
        }
        var paths = new List<string>();
        var ids = new List<string>();
        var deleted = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        var previous = new List<string>();
        foreach (var change in delta.Changes)
        {
            paths.Add(change.Path);
            if (change.Kind == ChangeKind.Delete)
            {
                // D81: hydration is a scoped full inventory, not historical deletion authority.
                // Keep absence in file_state but do not turn excluded-period events into rule-19 deletes.
                if (!hydrate.Any(prefix => PairPath.IsSelfOrUnder(change.Path, prefix)))
                    deleted[change.Path] = change.DeletedUtc;
                continue;
            }
            ids.Add(change.ObjectId);
            var before = _store.ByObject(root.RootId, change.ObjectId)?.Path;
            if (before is not null && !string.Equals(before, change.Path, StringComparison.Ordinal)) previous.Add(before);
        }
        _store.WriteDelta(delta with { IncludesPath = include });
        var retryReads = await RetryScanScopesAsync(pair, root, side, inventory, delta.CursorAfter, deltaGen, include, throttle, ct);
        paths.AddRange(retryReads.Touched);
        delta = delta with { IncompleteSubtrees = delta.IncompleteSubtrees.Concat(retryReads.Incomplete).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        await inventory.VerifyPendingAsync(include, paths.Add, ct);
        if (firstSync && delta.IncompleteSubtrees.Count == 0 && delta.PendingDeletionEvidence.Count == 0)
            observationStore?.SaveInventoryObservation(coverage, delta.CursorAfter);
        // With no effect cursor, load the retained inventory too: a failed prepare may have observed
        // unchanged items that never reached execution, even though its subsequent feed is empty.
        return new SideScan(deltaGen, delta.CursorAfter, delta.IncompleteSubtrees.Count == 0,
            hydrate.Length > 0 || observationOnly, paths, ids, deleted, previous)
            { IncompleteSubtrees = delta.IncompleteSubtrees, IncompleteReasons = delta.IncompleteReasons, ObservedCoverage = observationStore is null ? null : coverage, RehashThrough = _store.RehashWatermark(root.RootId),
                PendingDeletionEvidence = delta.PendingDeletionEvidence, ReleasedDeletionEvidence = delta.ReleasedDeletionEvidence };
    }

    // The full snapshot must be installed first: journal name resolution uses its object/parent rows.
    // Always start from the ORIGINAL pre-walk cursor, including after a multi-day pause.
    private async Task<SideScan> CatchUpLocalWalkAsync(PairDef pair, Side side, IInventorySource inventory,
        Snapshot snapshot, Func<string, bool> include, RootDef root, ProgressThrottle throttle, CancellationToken ct)
    {
        var gen = _store.BeginGeneration(root.RootId, "delta");
        var delta = await inventory.ChangesSinceAsync(snapshot.CursorAfter, gen, include, ct);
        if (delta.CursorInvalid)
        {
            Trace($"scan {side}: local walk catch-up unavailable; retrying one fresh walk");
            _store.AddNotices([new Notice(pair.PairId, 31, NoticeKind.Info, root.RootId,
                "Local change journal catch-up was unavailable. Retrying a fresh inventory; no stale paused snapshot will be planned.",
                "local-walk-catch-up")]);
            var freshGen = _store.BeginGeneration(root.RootId, "full");
            var fresh = await ReadFullAsync(inventory, freshGen, include, root, throttle, ct);
            _store.WriteSnapshot(fresh with { IncludesPath = path => include(path) && (fresh.IncludesPath?.Invoke(path) ?? true) });
            await inventory.VerifyPendingAsync(include, _ => { }, ct);
            return new SideScan(freshGen, fresh.CursorAfter, fresh.IncompleteSubtrees.Count == 0, true, [], [],
                NoDeletions, []) { RehashThrough = _store.RehashWatermark(root.RootId) };
        }
        _store.WriteDelta(delta with { IncludesPath = include });
        await inventory.VerifyPendingAsync(include, _ => { }, ct);
        var complete = snapshot.IncompleteSubtrees.Count == 0 && delta.IncompleteSubtrees.Count == 0;
        if (complete) CloseHydrationFallback(pair, root);
        return new SideScan(gen, delta.CursorAfter, complete, true,
            delta.Changes.Select(c => c.Path).ToArray(), delta.Changes.Select(c => c.ObjectId).ToArray(),
            NoDeletions, []) { RehashThrough = _store.RehashWatermark(root.RootId),
                PendingDeletionEvidence = delta.PendingDeletionEvidence, ReleasedDeletionEvidence = delta.ReleasedDeletionEvidence };
    }

    private const string HydrationFallbackNoticeKey = "inventory-hydration-fallback";

    // A complete stored full inventory of this root resolves its hydration fallback; nothing else does.
    private void CloseHydrationFallback(PairDef pair, RootDef root)
    {
        foreach (var notice in _store.OpenNotices(pair.PairId))
            if (notice.ReasonKey == HydrationFallbackNoticeKey && notice.NoticeId is not null &&
                string.Equals(notice.Path, root.RootId, StringComparison.Ordinal))
                _store.CloseNotice(notice.NoticeId, "resolved");
    }

    // A full inventory read reports its entries read through the run's progress log (see InventoryProgress);
    // provider process messages reach the same log through the run-wide scope RunActivity owns, so "Reading Dropbox"
    // is not silent while a large scan runs.
    private async Task<Snapshot> ReadFullAsync(IInventorySource inventory, long gen, Func<string, bool> include,
        RootDef root, ProgressThrottle throttle, CancellationToken ct)
    {
        var progress = new InventoryProgress(throttle, root);
        return await inventory.FullAsync(gen, include, progress, ct);
    }

    // Inventory absence alone is not deletion evidence. For unmatched one-sided paths each side's deletion
    // times still come from that side's history, even when the preview reused an inventory cursor (decision 4).
    private async Task<BothScan> WithDeletionTimesAsync(PairDef pair, RunScope.Scope scope,
        IReadOnlyList<string> rebuildablePaths, IReadOnlyDictionary<string, PrepareMatch>? matchedAtPrepare, BothScan scan,
        CancellationToken ct) => new(
        scan.A with
        {
            Deleted = await DeletionTimesAsync(pair, pair.A,
                WithoutMatched(OnlyIn(scope.B, scope.A), matchedAtPrepare), rebuildablePaths, ct)
        },
        scan.B with
        {
            Deleted = await DeletionTimesAsync(pair, pair.B,
                WithoutMatched(OnlyIn(scope.A, scope.B), matchedAtPrepare), rebuildablePaths, ct)
        });

    // finding 2: a path the reviewed prepare saw on both sides is planned by the ordinary rules,
    // never judged from this side's deletion history.
    private static IReadOnlyList<string> WithoutMatched(IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, PrepareMatch>? matchedAtPrepare) =>
        matchedAtPrepare is null ? paths : paths.Where(path => !matchedAtPrepare.ContainsKey(path)).ToArray();

    private async Task<IReadOnlyDictionary<string, DateTimeOffset?>> DeletionTimesAsync(PairDef pair, RootDef root,
        IReadOnlyList<string> paths, IReadOnlyList<string> rebuildablePaths, CancellationToken ct)
    {
        var probe = DeletionProbePaths(pair, paths, rebuildablePaths);
        Trace($"deletion evidence for {root.RootId}: {paths.Count} requested, {probe.Length} after " +
            "rebuildable/exclusion filtering");
        if (probe.Length == 0) return NoDeletions;
        var times = await _endpoints.Inventory(root).DeletionTimesAsync(probe, ct);
        return times.ToDictionary(entry => entry.Key, entry => (DateTimeOffset?)entry.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    // A deletion-probe path is dropped when it is at or under a chosen rebuildable folder (never uploaded and
    // never deleted locally, so its deletion evidence is irrelevant) or at or under a pair exclusion (out of
    // scope). The match is the same PairPath.IsSelfOrUnder the planner applies to protected paths.
    internal static string[] DeletionProbePaths(PairDef pair, IReadOnlyList<string> paths,
        IReadOnlyList<string> rebuildablePaths)
    {
        var protectedPaths = pair.Excluded.Concat(rebuildablePaths)
            .Select(path => path.Replace('\\', '/').Trim('/')).Where(path => path.Length > 0).ToArray();
        var include = PairInclude.For(pair, Side.A, [pair], []);
        return paths.Where(path => include(path) && !protectedPaths.Any(protectedPath =>
            PairPath.IsSelfOrUnder(path, protectedPath))).ToArray();
    }

    private static string[] OnlyIn(IReadOnlyList<ItemState> items, IReadOnlyList<ItemState> other)
    {
        var held = new HashSet<string>(other.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        // A mapped cloud name (even an unreadable/colliding one) is presence, never deletion evidence.
        foreach (var item in other)
            if (DropboxNameMapping.LocalPath(item) is { } local) held.Add(local);
        return items.Where(item => (item.Flags & ItemFlags.Ignored) == 0 && !held.Contains(item.Path) && !held.Contains(DropboxNameMapping.MatchPath(item)))
            .Select(item => item.Path).ToArray();
    }

    private async Task<IReadOnlyList<PlanRow>> HashAndJoinAsync(PairDef pair, Func<RunScope.Scope> loadScope,
        BothScan scan, bool firstSync, ProgressThrottle throttle, CancellationToken ct, Action<HashRequest>? refresh = null)
    {
        throttle.Report(RunPhases.Comparing, null, 0, 0);
        var comparison = System.Diagnostics.Stopwatch.StartNew();
        var rows = JoinRows(loadScope(), scan, firstSync);
        var requests = Planner.HashRequests(rows);
        RunLog.Trace($"comparison scope/join: elapsed-ms={comparison.ElapsedMilliseconds}; rows={rows.Count}; hash-a={requests.PathsA.Count}; hash-b={requests.PathsB.Count}");
        if (requests.PathsA.Count == 0 && requests.PathsB.Count == 0) return rows;
        // Requests retain only paths; release the old joined inventory before hashing and reloading.
        rows = [];
        comparison.Restart();
        await PatchHashesAsync(pair, scan, requests, ct);
        RunLog.Trace($"comparison content requests completed: elapsed-ms={comparison.ElapsedMilliseconds}; hash-a={requests.PathsA.Count}; hash-b={requests.PathsB.Count}");
        refresh?.Invoke(requests);
        return JoinRows(loadScope(), scan, firstSync);
    }

    internal static IReadOnlyList<PlanRow> JoinRows(RunScope.Scope scope, BothScan scan, bool firstSync)
    {
        // Full controls scope loading, not absence authority (hydrated deltas also set it).
        // A real full snapshot with explicitly scoped gaps covers only its unaffected paths.
        // Incomplete deltas, root gaps and unexplained incomplete scans remain non-authoritative.
        var completeness = new Completeness(scan.A.Complete || scan.A.ScopedFullCoverage,
            scan.B.Complete || scan.B.ScopedFullCoverage,
            scan.A.IncompleteSubtrees.ToHashSet(StringComparer.OrdinalIgnoreCase),
            scan.B.IncompleteSubtrees.ToHashSet(StringComparer.OrdinalIgnoreCase));
        return Planner.Join(scope.Baselines, scope.A, scope.B, scan.A.Deleted, scan.B.Deleted, completeness, firstSync, scope.ContextA, scope.ContextB, scope.ProtectedAliases);
    }

    private async Task PatchHashesAsync(PairDef pair, BothScan scan, HashRequest requests, CancellationToken ct)
    {
        await Task.WhenAll(PatchSideAsync(pair, pair.A, Side.A, scan.A, requests.PathsA, ct),
            PatchSideAsync(pair, pair.B, Side.B, scan.B, requests.PathsB, ct));
    }

    private async Task PatchSideAsync(PairDef pair, RootDef root, Side side, SideScan scan, IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        if (paths.Count == 0) return;
        // A StoreBatch commits every StoreBatchRows written rows, so a chunk larger than that would
        // split one hash checkpoint across commits and a crash could leave it half-applied. Bound every
        // chunk by both the configured checkpoint size and the store's batch size so it stays atomic.
        var checkpoint = Math.Min(_settings.LocalHashCheckpointFiles, _settings.StoreBatchRows);
        foreach (var chunk in paths.Chunk(checkpoint))
        {
            PairPause.Check();
            ct.ThrowIfCancellationRequested();
            IReadOnlyDictionary<string, string> hashes;
            using (string.Equals(root.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase)
                       ? RunInstrumentation.Current?.Measure(RunTimingStage.LocalHash) : null)
            {
                try { hashes = await _endpoints.Inventory(root).HashAsync(chunk, ct); }
                catch (Exception error) when (ScopeFailure(error, ct))
                {
                    // A bulk provider API need not identify the failed member. Isolate that
                    // bounded chunk once, not the whole inventory and not a retry loop.
                    var recovered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var path in chunk)
                    {
                        try
                        {
                            foreach (var entry in await _endpoints.Inventory(root).HashAsync([path], ct))
                                recovered[entry.Key] = entry.Value;
                        }
                        catch (Exception itemError) when (ScopeFailure(itemError, ct))
                        {
                            SaveFailedRetry(pair, path, "Hash: " + itemError.Message, _store.SyncRetryAt(pair.PairId, path));
                        }
                    }
                    hashes = recovered;
                }
            }
            if (hashes.Count == 0) continue;
            // One batched read of the hashed paths' live rows (a first sync can hash hundreds of thousands of files).
            var live = new Dictionary<string, ItemState>(StringComparer.Ordinal);
            foreach (var item in _store.Live(root.RootId, hashes.Keys)) live.TryAdd(item.Path, item);
            var changes = new List<Change>();
            foreach (var (path, hash) in hashes)
                if (live.TryGetValue(path, out var item))
                    changes.Add(new Change(ChangeKind.Upsert, item.ObjectId, path, item with { Hash = hash }, null));
            if (changes.Count == 0) continue;
            var cursor = _store.Cursor(root.RootId) ?? string.Empty;
            _store.WriteDelta(new Delta(root.RootId, side, scan.Gen, cursor, cursor, changes, false, []));
        }
    }

    /// <summary>
    /// B-165: every file a plan copies (CopyNew, Update, KeepBoth) must carry its source's content hash, and every
    /// file a plan preserves first (RecoveryFirst) must carry its target's hash, before they reach a writer: the real
    /// local and Dropbox writers refuse an unhashed source or Recovery victim. The planner hashes only rows it cannot
    /// compare, so a one-sided file is often unhashed. Hash exactly those files (nothing that will not be written),
    /// store the hashes and decide again from the stored rows. A file that still has no hash (unreadable, or changed
    /// while hashed) is not written: its whole row is held with the content-unconfirmed notice (rule 30) and retried
    /// next run. Preview and incremental runs both plan through here, so a reviewed first-sync plan is the plan apply
    /// executes.
    /// </summary>
    private async Task<(Plan Plan, IReadOnlyList<PlanRow> FinalRows)> DecideWithHashedCopiesAsync(PairDef pair,
        BothScan scan, IReadOnlyList<PlanRow> rows, Func<IReadOnlyList<PlanRow>> reload,
        Func<IReadOnlyList<PlanRow>, Plan> decide, ProgressThrottle throttle, CancellationToken ct, Action<HashRequest>? refresh = null)
    {
        var plan = decide(rows);
        var victims = UnhashedFolderVictims(pair, plan);
        var request = ResolveCurrentPaths(pair, UnhashedFiles(plan, victims));
        if (request.PathsA.Count == 0 && request.PathsB.Count == 0)
            return (HoldUnhashed(pair.PairId, plan, victims), rows);
        // Only path requests survive this pass. Do not retain its rows, plan or folder victims
        // in the async state machine while hashing and allocating the replacement inventory.
        rows = [];
        plan = null!;
        victims = null!;
        throttle.Report(RunPhases.Hashing, null, 0, request.PathsA.Count + request.PathsB.Count);
        await PatchHashesAsync(pair, scan, request, ct);
        refresh?.Invoke(request);
        // finding 4 (review fix): the caller captures the prepare-matched states from these final
        // reloaded rows, so a size-only difference resolved by the hash pass never produces a false copy-back.
        var finalRows = reload();
        plan = decide(finalRows);
        return (HoldUnhashed(pair.PairId, plan, UnhashedFolderVictims(pair, plan)), finalRows);
    }

    /// <summary>The unhashed live files under one collapsed folder delete, on the delete's side.</summary>
    internal sealed record FolderVictims(Side Side, IReadOnlyList<ItemState> Files);

    // A collapsed folder delete (rule 23, no planned descendants) is expanded by the executor into one
    // RecoveryFirst delete per live file under it (FolderDelete; Executor.ExpandFolderAsync, same expansion). Those
    // victims are not plan actions, so read them here with FolderDelete and hash the unhashed ones; the plan keeps
    // its single folder delete.
    private Dictionary<string, FolderVictims> UnhashedFolderVictims(PairDef pair, Plan plan)
    {
        var victims = new Dictionary<string, FolderVictims>(StringComparer.Ordinal);
        var ignoreRules = PairInclude.IgnoreRules(pair);
        foreach (var action in plan.Actions)
        {
            if (!FolderDelete.Expands(action))
                continue;
            var rootId = action.Target == Side.A ? pair.A.RootId : pair.B.RootId;
            var files = FolderDelete.LiveDescendants(_store, rootId, action.Path)
                .Where(item => item.Kind == ItemKind.File && item.Hash is null &&
                    !SyncIgnorePolicy.IsIgnored(item.Path, false, ignoreRules)).ToArray();
            if (files.Length > 0) victims[action.ActionId] = new FolderVictims(action.Target, files);
        }
        return victims;
    }

    /// <summary>One file a plan must hash first: its side and its planned state (path may be post-move).</summary>
    internal readonly record struct UnhashedFile(Side Side, ItemState State);

    /// <summary>The unhashed copy sources and Recovery victims of a plan; linear in the plan size.</summary>
    internal static IReadOnlyList<UnhashedFile> UnhashedFiles(Plan plan,
        IReadOnlyDictionary<string, FolderVictims> folderVictims)
    {
        var files = new List<UnhashedFile>();
        foreach (var victims in folderVictims.Values)
            files.AddRange(victims.Files.Select(file => new UnhashedFile(victims.Side, file)));
        foreach (var action in plan.Actions)
        {
            if (NeedsSourceHash(action))
                files.Add(new UnhashedFile(action.Target == Side.A ? Side.B : Side.A, action.Source!));
            if (NeedsTargetHash(action))
                files.Add(new UnhashedFile(action.Target, action.ExpectedTarget!));
        }
        return files;
    }

    private static bool NeedsSourceHash(SyncAction action) =>
        action.Kind is ActionKind.CopyNew or ActionKind.Update or ActionKind.KeepBoth &&
        action.Source is { Kind: ItemKind.File, Hash: null };

    private static bool NeedsTargetHash(SyncAction action) =>
        action.RecoveryFirst && action.ExpectedTarget is { Kind: ItemKind.File, Hash: null };

    private static bool NeedsHash(SyncAction action) => NeedsSourceHash(action) || NeedsTargetHash(action);

    // A planned state's path can already be the post-folder-move location (the planner rebases children of a moved
    // folder), which does not exist yet. Hash the item where the inventory has it NOW: its stored path when the
    // identity still lives there, else the identity's current path. One batched read per side, then point lookups
    // only for the moved ones.
    private HashRequest ResolveCurrentPaths(PairDef pair, IReadOnlyList<UnhashedFile> files) =>
        new(ResolveSide(pair.A, files.Where(file => file.Side == Side.A)),
            ResolveSide(pair.B, files.Where(file => file.Side == Side.B)));

    private IReadOnlyList<string> ResolveSide(RootDef root, IEnumerable<UnhashedFile> files)
    {
        var states = files.Select(file => file.State).ToArray();
        if (states.Length == 0) return [];
        var live = new Dictionary<string, ItemState>(StringComparer.Ordinal);
        foreach (var item in _store.Live(root.RootId, states.Select(state => state.Path).Distinct(StringComparer.Ordinal)))
            live.TryAdd(item.Path, item);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            if (live.TryGetValue(state.Path, out var here) && string.Equals(here.ObjectId, state.ObjectId, StringComparison.Ordinal))
                paths.Add(state.Path);
            else if (_store.ByObject(root.RootId, state.ObjectId) is { Kind: ItemKind.File } current)
                paths.Add(current.Path);
        }
        return paths.ToArray();
    }

    // Every action of a still-unhashed file's row is dropped together, so no part of a row (the other labelled
    // keep-both copy, a split-baseline Forget, the mirror create) runs alone. Row membership is the transitive
    // closure over the paths and object keys the actions share; each held row's paths get the rule-30 notice.
    private Plan HoldUnhashed(string pairId, Plan plan, IReadOnlyDictionary<string, FolderVictims> folderVictims)
    {
        // A folder delete with a still-unhashed victim is held (not expanded) like any other unhashed action.
        bool Needs(SyncAction action) => NeedsHash(action) || folderVictims.ContainsKey(action.ActionId);
        if (!plan.Actions.Any(Needs)) return plan;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var held = new HashSet<string>(StringComparer.Ordinal);
        var noticed = new List<string>();
        foreach (var action in plan.Actions.Where(Needs))
            Hold(action);
        bool grew;
        do
        {
            grew = false;
            foreach (var action in plan.Actions)
                if (!held.Contains(action.ActionId) && Touches(action))
                {
                    Hold(action);
                    grew = true;
                }
        } while (grew);
        var kept = plan.Actions.Where(action => !held.Contains(action.ActionId)).ToArray();
        // A held row did nothing, so the plan's own notices for it (e.g. "kept both") are withdrawn; the rule-30
        // notice sits on the unhashed file's own path, which the next run re-scopes and retries.
        var notices = plan.Notices.Where(notice => !paths.Contains(notice.Path))
            .Concat(noticed.Distinct(StringComparer.OrdinalIgnoreCase).Select(path =>
            _store.SyncRetryAt(pairId, path) is { LastError: var reason } && reason.StartsWith("Hash:", StringComparison.Ordinal)
                ? new Notice(pairId, 40, NoticeKind.Info, path, reason + ". Retry or Ignore to exclude this item.", "sync-retry")
                : new Notice(pairId, UnconfirmedRule, NoticeKind.Info, path, PlanNotices.ContentUnconfirmed.Detail,
                    PlanNotices.ContentUnconfirmed.Key))).ToArray();
        return plan with { Actions = kept, Notices = notices };

        bool Touches(SyncAction action) =>
            (action.ObjectKey is { } key && keys.Contains(key)) || RowPaths(action).Any(paths.Contains);

        void Hold(SyncAction action)
        {
            held.Add(action.ActionId);
            if (action.ObjectKey is { } key) keys.Add(key);
            foreach (var path in RowPaths(action)) paths.Add(path);
            if (NeedsSourceHash(action)) noticed.Add(action.Source!.Path);
            if (NeedsTargetHash(action)) noticed.Add(action.ExpectedTarget!.Path);
            if (folderVictims.TryGetValue(action.ActionId, out var victims))
                noticed.AddRange(victims.Files.Select(file => file.Path));
        }
    }

    // The item paths an action names on either side (not a folder move's whole subtree: only exact paths).
    private static IEnumerable<string> RowPaths(SyncAction action)
    {
        yield return action.Path;
        if (action.FromPath is { } from) yield return from;
        if (action.Source?.Path is { } source) yield return source;
        if (action.ExpectedTarget?.Path is { } target) yield return target;
    }

    private static ActionResult SafetySkipped(SyncAction action) =>
        new(action.ActionId, Outcome.Skipped, null, null, null, PlanNotices.OutsidePairArea.Detail);

    private async Task<Tally> ExecuteAsync(Plan plan, PairDef pair, ProgressThrottle throttle, CancellationToken ct)
    {
        var tally = new Tally(_settings);
        var byId = plan.Actions.ToDictionary(action => action.ActionId, StringComparer.Ordinal);
        tally.Plan(plan.Actions);
        tally.Plan(plan.SkippedActions);
        foreach (var action in plan.SkippedActions)
            tally.Add(pair, action, SafetySkipped(action));
        var total = tally.Total;
        throttle.Report(RunPhases.Applying, null, 0, total, kinds: tally.KindProgress());
        try
        {
            await foreach (var result in _executor.ApplyAsync(plan, _endpoints.Writer(pair.A), _endpoints.Writer(pair.B), ct)
                               .WithCancellation(ct))
            {
                var action = byId.GetValueOrDefault(result.ActionId);
                tally.Add(pair, action, result);
                throttle.Report(RunPhases.Applying, action?.Path, tally.ProgressDone(), tally.Total, tally.ExtraDone, tally.KindProgress());
            }
        }
        catch (PairPausedException paused)
        {
            paused.Report = BuildReport(pair.PairId, plan, tally, false) with { Outcome = RunOutcomes.Paused };
            throw;
        }
        finally { AddProviderNameRefusalNotice(pair); }
        return tally;
    }

    private void AddProviderNameRefusalNotice(PairDef pair)
    {
        // The deferral problem has one fixed id: the same id the notice is raised with below, so a primary-key
        // read closes it without materialising every open notice of the pair.
        var deferredId = ShortId.Make("n", pair.PairId, "40", RowCodec.Key(string.Empty), "refusal-notices-deferred");
        try
        {
            _store.RefreshProviderNameNotices(pair);
            // A refresh that works retires the earlier run's deferral notice: if the next run succeeds the notice goes away.
            if (_store.NoticeById(pair.PairId, deferredId) is { Closed: false }) _store.CloseNotice(deferredId, "refreshed");
            // Replace the old aggregate only after the item notices were persisted.
            var summaryId = ShortId.Make("n", pair.PairId, "31", RowCodec.Key(string.Empty), ProviderNameRefusedException.NoticeKey);
            if (_store.NoticeById(pair.PairId, summaryId) is { Closed: false }) _store.CloseNotice(summaryId, "replaced-by-item-notices");
        }
        catch (Exception error) when (RunFailure(error, CancellationToken.None))
        {
            RunLog.Trace("Refusal notice refresh deferred: " + error.Message);
            try
            {
                _store.AddNotices([new Notice(pair.PairId, 40, NoticeKind.Info, "",
                    "Item notices could not be refreshed: " + error.Message + ". Sync will refresh them on the next run.",
                    "refusal-notices-deferred", NoticeId: deferredId)]);
            }
            catch (Exception writeError) when (RunFailure(writeError, CancellationToken.None))
            { RunLog.Trace("Unable to persist refusal refresh notice: " + writeError.Message); }
        }
    }

    // a pre-existing notice closes only when its path was re-evaluated in this run, the plan raised
    // no notice for it, and every effect on it (Hold included) ended Done. Untouched paths keep their notices.
    // A notice that records an automatic decision (PlanNotices.AwaitsAcknowledgement: kept both, edit beats delete,
    // moved version kept, decided automatically) waits for the user: our own writes come back through the change feed,
    // so a clean sync of the path would otherwise close it before the user has seen it (notices design §1).
    private void CloseResolvedNotices(IReadOnlyList<Notice> open, Plan plan, Tally tally, Func<string, bool> evaluated)
    {
        var unsettled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var notice in plan.Notices) unsettled.Add(notice.Path);
        foreach (var action in plan.Actions)
        {
            if (tally.WasDone(action.ActionId)) continue;
            unsettled.Add(action.Path);
            if (action.FromPath is not null) unsettled.Add(action.FromPath);
        }
        foreach (var notice in open)
        {
            if (notice.ReasonKey is RecoveryRestorePending or RecoveryRestoreReady) continue;
            if (notice.Repair is not null)
            {
                if (notice.NoticeId is not null && RepairSynced(notice, plan, evaluated, unsettled))
                    _store.CloseNotice(notice.NoticeId, "renamed-and-synced");
                continue;
            }
            if (notice.NoticeId is not null &&
                (notice.ReasonKey == "name-mapping-collision" || !PlanNotices.AwaitsAcknowledgement(notice)) &&
                notice.ReasonKey is not (HeldIntentsKey or "permanent-boundary" or RehashProtection.Reason) &&
                notice.ReasonKey != DropboxQuota.NoticeKey && notice.ReasonKey != QuarantineFailure.NoticeKey && notice.ReasonKey != ExcludedIntentNoticeKey && notice.ReasonKey != ProviderNameRefusedException.NoticeKey &&
                !JournalSizing.IsNotice(notice) && notice.ReasonKey != HydrationFallbackNoticeKey && !NoticeChoices.IsRemovalAttention(notice) &&
                !(notice.ReasonKey is "sync-retry" or "deletion-history-unavailable" &&
                    _store.SyncRetryAt(plan.PairId, notice.Path) is not null) &&
                evaluated(notice.Path) && !unsettled.Contains(notice.Path))
                _store.CloseNotice(notice.NoticeId, "resolved");
        }
    }

    private void AcknowledgeVerifiedRehash(PairDef pair, BothScan scan, Plan plan, Tally tally,
        Func<string, bool> evaluated, Func<string, bool> includeA, Func<string, bool> includeB, CancellationToken ct)
    {
        var unsettled = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var notice in plan.Notices)
            if (notice.Rule != RehashProtection.Rule) unsettled.Add(notice.Path);
        foreach (var action in plan.SkippedActions.Concat(plan.Actions.Where(a => !tally.WasDone(a.ActionId))))
        {
            unsettled.Add(action.Path);
            if (action.FromPath is not null) unsettled.Add(action.FromPath);
        }
        bool Settled(string path)
        {
            if (!evaluated(path) || unsettled.Contains("")) return false;
            for (var ancestor = path; ancestor.Length > 0;)
            {
                if (unsettled.Contains(ancestor)) return false;
                var slash = ancestor.LastIndexOf('/');
                ancestor = slash < 0 ? "" : ancestor[..slash];
            }
            return !unsettled.GetViewBetween(path + "/", path + "0").Any();
        }
        _store.AcknowledgeRehash(pair.A.RootId, scan.A.RehashThrough, path => Settled(path) && includeA(path), ct);
        _store.AcknowledgeRehash(pair.B.RootId, scan.B.RehashThrough, path => Settled(path) && includeB(path), ct);
    }

    // The paths this run joined and planned for: current and baseline paths on both sides.
    private static Func<string, bool> EvaluatedPaths(IReadOnlyList<PlanRow> rows)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.A is not null) paths.Add(row.A.Path);
            if (row.B is not null) paths.Add(row.B.Path);
            if (row.Base is null) continue;
            paths.Add(row.Base.A.Path);
            paths.Add(row.Base.B.Path);
        }
        return paths.Contains;
    }
}
