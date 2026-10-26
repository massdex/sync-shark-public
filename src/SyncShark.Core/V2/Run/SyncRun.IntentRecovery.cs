namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // One recovery pass owns its protection snapshot, page classifications and bounded failure sample.
    private sealed class IntentRecovery
    {
        private readonly SyncRun _run;
        private readonly PairDef _pair;
        private readonly ProgressThrottle _throttle;
        private readonly CancellationToken _ct;
        private readonly Func<SyncAction, bool>? _filter;
        private readonly LinkExclusions _protections;
        private readonly ActionGuard _guard;
        private readonly ActionGuard _exclusions;
        private readonly IReadOnlyList<PairDef> _pairs;
        private readonly IReadOnlyList<string> _owned;
        private readonly IReadOnlyList<SyncIgnoreRule> _rules;
        private readonly bool _recoveryUnavailable;
        private readonly List<ActionResult> _sleeping = [];
        private readonly List<string> _touched = [];
        private long? _retirementGeneration;
        private long? _generation;
        private long _reconciled;
        private long _total;
        private int _uncertainCount;

        public IntentRecovery(SyncRun run, PairDef pair, ProgressThrottle throttle, CancellationToken ct,
            IReadOnlyList<string>? rules, Func<SyncAction, bool>? filter)
        {
            _run = run; _pair = pair; _throttle = throttle; _ct = ct; _filter = filter;
            _protections = run.LeftAlone(pair);
            _guard = run.ActionPredicates(pair, _protections);
            _exclusions = ExclusionPredicates(pair);
            _pairs = run._store.Pairs();
            _owned = run._productOwned(pair);
            _rules = SyncIgnorePolicy.Normalize((rules ?? []).SelectMany(SyncIgnorePolicy.Parse).ToArray());
            _recoveryUnavailable = RecoverySafety.Unavailable(run._endpoints.Writer(pair.A), run._endpoints.Writer(pair.B)) is not null;
        }

        public async Task<ReconcileResult> RunAsync()
        {
            using var scope = new ReconcileRunScope();
            _total = _run._store.CountUnresolvedIntents(_pair.PairId);
            _throttle.Report(RunPhases.Reconcile, null, 0, _total);
            if (_total > 0)
                await SyncByteProgressStream.ReportCurrentProcessAsync("Unconfirmed effects",
                    $"{_total} effect(s) unconfirmed; re-checking at {_run._time.GetUtcNow():HH:mm:ss}.", ProcessMessageKind.Notice);
            // Release each keyset reader before observing or committing; unresolved rows are visited once.
            foreach (var rows in _run._store.StreamUnresolvedIntentRows(_pair.PairId).Chunk(256))
                await RecoverPageAsync(rows, scope);
            // Group settlement can remove rows ahead of the keyset cursor.
            _throttle.Report(RunPhases.Reconcile, null, _total, _total);
            return new ReconcileResult(_sleeping, _touched) { UncertainCount = _uncertainCount,
                Protected = _run.OutstandingFootprints(_pair.PairId), PendingVerification = _run.RefreshHeldIntentSummary(_pair) };
        }

        private async Task RecoverPageAsync(IntentRow[] rows, ReconcileRunScope scope)
        {
            var page = new RecoveryPage();
            var states = rows.ToDictionary(row => row.Action.ActionId, row => row.State, StringComparer.Ordinal);
            foreach (var entry in GuardedActions(rows.Select(row => row.Action).Where(action => _filter?.Invoke(action) ?? true), _guard))
            {
                _ct.ThrowIfCancellationRequested();
                Classify(entry.Action, entry.Guard, states[entry.Action.ActionId], scope, page);
            }
            _run._store.SetIntentsHeld(_pair.PairId, page.Held, true);
            // Persist classification before parking: a crash can repeat classification, not lose a retry.
            foreach (var retry in page.Parked) _run._store.SaveSyncRetry(_pair.PairId, retry);
            _run._store.SetIntentsHeld(_pair.PairId, page.Released, false);
            foreach (var result in await _run.SettleExcludedIntentsAsync(_pair, page.Excluded, _ct)) RememberUncertain(result);
            if (page.Intents.Count > 0) await ExecutePageAsync(page);
            _reconciled += rows.Length;
            _throttle.Report(RunPhases.Reconcile, null, _reconciled, _total);
        }

        private void Classify(SyncAction action, ActionGuard guard, string state, ReconcileRunScope scope, RecoveryPage page)
        {
            if (IsRecoveryRestoreIntent(action) || GroupAlreadyChecked(action, scope)) return;
            if (_run.PermanentIntentBoundary(_pair, action, _pairs, _owned) is { } boundary)
            {
                _retirementGeneration ??= _run._store.BeginGeneration(_pair.A.RootId, RunKind);
                _run.RetireBoundaryIntent(_pair, action, boundary, _retirementGeneration.Value);
                return;
            }
            var retry = _run._store.SyncRetryAt(_pair.PairId, action.Path);
            var quarantined = _run.IsQuarantinedIntent(_pair, action);
            if (quarantined || !ReconcileAllowed(guard, action) && ReconcileAllowed(_exclusions, action) &&
                !_run.CarriesNewlyIgnored(_pair, action, _rules))
            {
                ClassifyHeld(action, state, retry, quarantined, page);
                return;
            }
            AdmitDue(action, guard, state, retry, page);
        }

        private bool GroupAlreadyChecked(SyncAction action, ReconcileRunScope scope)
        {
            if (action.ProviderEffect is not { Kind: "whole-folder-group" or "whole-folder-member" } group) return false;
            if (_run._store.IntentById(_pair.PairId, action.ActionId) is null) return true;
            var anchorId = group.Kind == "whole-folder-group" ? action.ActionId : group.Id;
            if (!scope.Anchors.Contains(anchorId) && !scope.MissingAnchors.Contains(anchorId)) return false;
            RememberUncertain(new(action.ActionId, Outcome.Uncertain, null, null, null,
                "Whole-folder recovery remains unresolved after this run's anchor check."));
            return true;
        }

        private void ClassifyHeld(SyncAction action, string state, SyncRetry? retry, bool quarantined, RecoveryPage page)
        {
            switch (quarantined ? HoldCause.Pending : HoldCauseOf(_protections, action))
            {
                // Destructive link/stub effects and user decisions remain pending under their standing notice.
                case HoldCause.Link or HoldCause.Stub when RecoverySafety.RequiresRecovery(action) || action.ConflictTransitionKey is not null:
                case HoldCause.Pending:
                    if (state == "held") page.Released.Add(action.ActionId);
                    ParkRetry(page.Parked, retry);
                    break;
                case HoldCause.Link:
                    RetireProtected(action, LinkRetiredReason);
                    break;
                case HoldCause.Stub:
                    RetireProtected(action, StubRetiredReason);
                    break;
                default:
                    if (state != "held") page.Held.Add(action.ActionId);
                    ParkRetry(page.Parked, retry);
                    break;
            }
        }

        private void RetireProtected(SyncAction action, string reason)
        {
            _retirementGeneration ??= _run._store.BeginGeneration(_pair.A.RootId, RunKind);
            _run.RetireProtectedIntent(_pair, action, _retirementGeneration.Value, reason);
        }

        private void AdmitDue(SyncAction action, ActionGuard guard, string state, SyncRetry? retry, RecoveryPage page)
        {
            // Only parking writes this shape; intent state alone cannot identify a parked retry.
            if (retry is { Kind: "sync", Held: null, RetryAfter: var due } && due == DateTimeOffset.MaxValue)
            {
                retry = retry with { RetryAfter = DateTimeOffset.MinValue };
                _run._store.SaveSyncRetry(_pair.PairId, retry);
            }
            if (state == "held") page.Released.Add(action.ActionId);
            if (retry?.RetryAfter > _run._time.GetUtcNow())
            {
                RememberUncertain(new(action.ActionId, Outcome.Uncertain, null, null, null, retry.LastError));
                return;
            }
            var newlyIgnored = _run.CarriesNewlyIgnored(_pair, action, _rules);
            if (!(_recoveryUnavailable && RecoverySafety.RequiresRecovery(action)) &&
                (action.ProviderEffect?.Kind is "whole-folder-group" or "whole-folder-member" ||
                 ReconcileAllowed(guard, action) && !newlyIgnored))
            {
                page.Intents.Add(action);
                page.Retries[action.ActionId] = retry;
            }
            else if (!ReconcileAllowed(_exclusions, action) || newlyIgnored) page.Excluded.Add(action);
            else RememberUncertain(new(action.ActionId, Outcome.Uncertain, null, null, null,
                "Recovery is unavailable; restore access to Recovery and retry."));
        }

        private async Task ExecutePageAsync(RecoveryPage page)
        {
            _generation ??= _run._store.BeginGeneration(_pair.A.RootId, RunKind);
            var results = await _run._executor.ReconcileAsync(page.Intents, _generation.Value,
                _run._endpoints.Writer(_pair.A), _run._endpoints.Writer(_pair.B), _ct,
                new ReconcileProgress(_throttle, _total, _reconciled));
            var byId = page.Intents.ToDictionary(intent => intent.ActionId, StringComparer.Ordinal);
            foreach (var result in results)
                if (byId.TryGetValue(result.ActionId, out var action))
                    RecordResult(action, result, page.Retries.TryGetValue(result.ActionId, out var known)
                        ? known : _run._store.SyncRetryAt(_pair.PairId, action.Path));
            foreach (var result in results.Where(result => result.Outcome == Outcome.Uncertain)) RememberUncertain(result);
        }

        private void RecordResult(SyncAction action, ActionResult result, SyncRetry? retry)
        {
            // Frozen conflict successors retain their dedicated Retry-only notice, never generic Ignore.
            var successor = action.ConflictTransitionKey is not null && !action.SuppressReconciledBaseline ? action : null;
            if (result.Outcome is Outcome.Uncertain or Outcome.Failed)
                _run.SaveFailedRetry(_pair, action.Path, result.Reason, retry, successor, result);
            else if (result.Outcome == Outcome.Done) _run._store.ClearSyncRetry(_pair.PairId, action.Path);
            else if (result.Outcome is Outcome.Skipped or Outcome.Refused)
            {
                if (successor is not null && _run._store.IntentById(_pair.PairId, action.ActionId) is not null)
                    _run.SaveFailedRetry(_pair, action.Path, result.Reason, retry, successor, result);
                else if (successor is not null) _run._store.ClearSyncRetry(_pair.PairId, action.Path);
                else if (retry is not null) _run._store.SaveSyncRetry(_pair.PairId, retry with { RetryAfter = DateTimeOffset.MinValue });
                _touched.Add(action.Path);
                if (action.FromPath is not null) _touched.Add(action.FromPath);
            }
        }

        private void RememberUncertain(ActionResult result)
        {
            _uncertainCount++;
            if (_sleeping.Count < _run._settings.FailureSampleLimit) _sleeping.Add(result);
        }

        private sealed class RecoveryPage
        {
            public List<SyncAction> Intents { get; } = [];
            public Dictionary<string, SyncRetry?> Retries { get; } = new(StringComparer.Ordinal);
            public List<SyncAction> Excluded { get; } = [];
            public List<string> Held { get; } = [];
            public List<string> Released { get; } = [];
            public List<SyncRetry> Parked { get; } = [];
        }
    }
}
