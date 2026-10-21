namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    internal const string RecoveryRestorePending = "recovery-restore-pending";
    internal const string RecoveryRestoreReady = "recovery-restore-ready";
    private const int RecoveryRestorePageLimit = 500;
    // Scheduling convenience only: notices and protection remain durable if a process restarts.
    private readonly Dictionary<string, string?> _recoveryRestoreAfter = new(StringComparer.Ordinal);

    // RQ-060/061/065: immutable archive bytes, create-only writes and item-owned durable recovery.
    public Task<IReadOnlyList<RecoveryRestoreItem>> RestoreRecoveryPageAsync(string pairId, RecoveryQuery query,
        bool asCopy, bool dryRun, CancellationToken ct) => RunExclusiveAsync(null, async (_, token) =>
    {
        query.Validate();
        var records = _store.RecoveryPage(pairId, query);
        var results = new RecoveryRestoreItem[records.Count];
        var pair = _store.Pair(pairId);
        string? unavailable = null;
        try
        {
            if (pair is null || !pair.Enabled) unavailable = "Enable this folder pair and Retry; nothing was changed.";
            else
            {
                RefuseLayout(pair);
                if (dryRun) await _endpoints.PrepareRecoveryReadAsync(pair, token).ConfigureAwait(false);
                else await _endpoints.PrepareAsync(pair, token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException) { unavailable = error.Message; }
        await Parallel.ForEachAsync(Enumerable.Range(0, records.Count), new ParallelOptions
        { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (index, itemToken) =>
        {
            var record = records[index];
            try
            {
                if (unavailable is not null || pair is null)
                    throw new IOException(unavailable ?? "The folder pair is unavailable.");
                results[index] = await RestoreRecoveryRecordAsync(pair, record, asCopy, dryRun, itemToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                var detail = "Recovery restore did not finish: " + error.Message + " Retry after fixing the cause, or Ignore to leave it protected.";
                if (!dryRun) SaveRecoveryRestoreFailure(record, detail, asCopy: asCopy);
                results[index] = RecoveryResult(record, "FAILED", detail: detail);
            }
        }).ConfigureAwait(false);
        return (IReadOnlyList<RecoveryRestoreItem>)results;
    }, ct);

    private static string RecoveryRestoreId(RecoveryRecord record) => ShortId.Make("rrs", record.PairId, record.RecoveryId);
    internal static bool IsRecoveryRestoreIntent(SyncAction action) =>
        action.ActionId.StartsWith("rrs", StringComparison.Ordinal) && action.SuppressReconciledBaseline;
    private static RecoveryRestoreItem RecoveryResult(RecoveryRecord record, string classification,
        string? destination = null, string? detail = null) => new(record.RecoveryId, record.Side.ToString(), record.Path,
            record.VersionId, record.Size, record.Created, classification, destination, detail);

    private async Task<RecoveryRestoreItem> RestoreRecoveryRecordAsync(PairDef pair, RecoveryRecord record,
        bool asCopy, bool dryRun, CancellationToken ct)
    {
        if (record.PairId != pair.PairId) throw new IOException("Recovery entry belongs to a different pair.");
        var id = RecoveryRestoreId(record);
        var savedRow = _store.NoticeById(pair.PairId, id);
        var saved = savedRow?.Notice;
        var frozen = saved?.ConflictOperation?.Actions.FirstOrDefault(IsRecoveryRestoreIntent);
        var targetRoot = record.Side == Side.A ? pair.A : pair.B;
        var target = _endpoints.Writer(targetRoot);
        // Reconcile the old destination BEFORE reading expiry or selecting another copy name.
        if (!dryRun && _store.IntentById(pair.PairId, id) is { } pending)
        {
            if (!IsRecoveryRestoreIntent(pending) || pending.Target != record.Side)
                throw new IOException("Recovery operation identity is occupied; no new destination was selected.");
            var reconciled = await _executor.ReconcileAsync([pending], _store.BeginGeneration(targetRoot.RootId, RunKind),
                _endpoints.Writer(pair.A), _endpoints.Writer(pair.B), ct).ConfigureAwait(false);
            var result = reconciled.Single();
            if (result.Outcome == Outcome.Done) return RecoveryResult(record, "RESTORED", pending.Path);
            if (result.Outcome is Outcome.Failed or Outcome.Uncertain)
                throw new IOException(result.Reason ?? "The previous restore is still unconfirmed.");
            frozen ??= pending; // An absent effect retries exactly the saved path, never a new copy.
        }
        if (frozen is not null && await target.ObserveAsync(frozen.Path, ct).ConfigureAwait(false) is { } restored &&
            RecoveryContentMatches(restored, record))
            return RecoveryResult(record, "SKIP-PRESENT", frozen.Path);
        if (savedRow is { Closed: true })
            return RecoveryResult(record, "CONFLICT", frozen?.Path,
                "This Recovery operation already completed, but its destination has since changed. Review the destination; the completed operation was not replayed.");
        if (!dryRun && !asCopy && frozen is null && saved is not null &&
            await target.ObserveAsync(record.Path, ct).ConfigureAwait(false) is { } arrived && RecoveryContentMatches(arrived, record))
        {
            var observedAction = new SyncAction(id, 0, ActionKind.CopyNew, record.Side, record.Path,
                null, arrived, null, false, null) { SuppressReconciledBaseline = true };
            _store.AddNotices([saved with { Path = record.Path, ConflictOperation =
                new ConflictNoticeOperation(record.RecoveryId, record.Path, [observedAction], []) }]);
            return RecoveryResult(record, "SKIP-PRESENT", record.Path);
        }
        if (record.Expires <= _time.GetUtcNow() || target is not IRecoveryRecordReader reader)
            return RecoveryUnavailable(record, dryRun, asCopy,"The retained version has expired or this provider cannot read it.");
        ItemState source;
        try { source = await reader.DescribeRecoveryAsync(record, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is FileNotFoundException or InvalidDataException)
        { return RecoveryUnavailable(record, dryRun, asCopy,"The exact retained version is unavailable: " + error.Message); }
        if (!RecoveryContentMatches(source, record))
            return RecoveryUnavailable(record, dryRun, asCopy,"The retained version metadata did not match its durable Recovery record.");
        var destination = frozen?.Path ?? record.Path;
        if (asCopy && frozen is null)
        {
            var found = false;
            for (var attempt = 1; attempt <= Math.Clamp(_settings.MaxLabelAttempts, 1, 100); attempt++)
            {
                destination = RecoveryCopyPath(record.Path, _time.GetUtcNow(), attempt);
                if (await target.ObserveAsync(destination, ct).ConfigureAwait(false) is not null) continue;
                found = true;
                break;
            }
            if (!found) throw new IOException("No unused restore-copy name was found; rename an existing copy and Retry.");
        }
        var action = frozen ?? new SyncAction(id, 0, ActionKind.CopyNew, record.Side, destination,
            null, source, null, false, null) { SuppressReconciledBaseline = true };
        var current = await target.ObserveAsync(destination, ct).ConfigureAwait(false);
        if (current is not null)
        {
            var identical = RecoveryContentMatches(current, record);
            const string detail = "The existing item was left unchanged. Use --as-copy to retain both versions; Retry or Ignore.";
            if (!dryRun && !identical) SaveRecoveryRestoreFailure(record, detail, destination, asCopy);
            return RecoveryResult(record, identical ? "SKIP-PRESENT" : "CONFLICT", destination, detail);
        }
        // Archived source is not a live object on the other endpoint. Guard only real destination paths.
        var guard = ActionPredicates(pair, LinkExclusions.Of(_store, pair, !_settings.ReplaceOnlineOnlyStubs));
        if (GuardedActions([action with { Source = null }], guard).Any(entry => !Allowed(entry.Guard, entry.Action)))
        {
            const string detail = "This path is protected by exclusions, ignore rules, a link or a nested pair. Include it or choose another location, then Retry; Ignore leaves it protected.";
            if (!dryRun) SaveRecoveryRestoreFailure(record, detail, destination, asCopy);
            return RecoveryResult(record, "PROTECTED", destination, detail);
        }
        // A moved counterpart needs an explicit conflict decision; never erase unrelated identity history.
        var baselines = _store.BaselinesAt(pair.PairId, Side.A, [destination])
            .Concat(_store.BaselinesAt(pair.PairId, Side.B, [destination])).DistinctBy(basis => basis.ObjectKey).ToArray();
        if (baselines.Any(basis => !PairPath.Same(basis.A.Path, destination) || !PairPath.Same(basis.B.Path, destination)))
            throw new IOException("A linked counterpart moved. Restore as a fresh copy rather than changing that identity's history.");
        if (dryRun) return RecoveryResult(record, "RESTORE", destination);
        var notice = new Notice(pair.PairId, 0, NoticeKind.UserDecision, destination,
            "Recovery restore is pending verification. Retry checks the same destination; Ignore leaves it protected.",
            RecoveryRestorePending, id,
            ConflictOperation: new ConflictNoticeOperation(record.RecoveryId, destination, [action], []));
        _store.AddNotices([notice]); // Durable protection precedes both baseline changes and provider effects.
        var generation = _store.BeginGeneration(targetRoot.RootId, RunKind);
        ForgetRecoveryBaselines(pair, destination, generation);
        await using var bytes = await reader.OpenRecoveryRecordAsync(record, ct).ConfigureAwait(false);
        if (!bytes.CanSeek || bytes.Length != record.Size ||
            !string.Equals(await DropboxContentHash.ComputeAsync(bytes, ct).ConfigureAwait(false), record.Hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Retained bytes failed verification; no destination was written.");
        bytes.Position = 0;
        var retained = new RetainedBytesWriter(action.Source!, bytes);
        await foreach (var result in _executor.ApplyAsync(new Plan(pair.PairId, generation, [action], []),
            record.Side == Side.A ? target : retained, record.Side == Side.B ? target : retained, ct))
        {
            if (result.Outcome == Outcome.Done) return RecoveryResult(record, "RESTORED", destination);
            throw new IOException(result.Reason ?? "The restore has not been verified.");
        }
        throw new IOException("Restore stopped before reporting an outcome.");
    }

    internal static string RecoveryCopyPath(string path, DateTimeOffset created, int attempt)
    {
        var slash = path.LastIndexOf('/');
        var directory = slash < 0 ? "" : path[..(slash + 1)];
        var name = slash < 0 ? path : path[(slash + 1)..];
        var extension = Path.GetExtension(name);
        return directory + name[..(name.Length - extension.Length)] + " (restored " +
            created.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ")" +
            (attempt == 1 ? "" : " " + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)) + extension;
    }

    private static bool RecoveryContentMatches(ItemState item, RecoveryRecord record) => item.Kind == ItemKind.File &&
        item.Size == record.Size && !string.IsNullOrEmpty(item.Hash) &&
        string.Equals(item.Hash, record.Hash, StringComparison.OrdinalIgnoreCase);

    private RecoveryRestoreItem RecoveryUnavailable(RecoveryRecord record, bool dryRun, bool asCopy, string detail)
    {
        if (!dryRun) SaveRecoveryRestoreFailure(record, detail + " Retry when available, or Ignore to leave it protected.", asCopy: asCopy);
        return RecoveryResult(record, "NO-VERSION", detail: detail);
    }

    private void SaveRecoveryRestoreFailure(RecoveryRecord record, string detail, string? destination = null, bool asCopy = false)
    {
        var id = RecoveryRestoreId(record);
        var existing = _store.NoticeById(record.PairId, id)?.Notice;
        if (asCopy && existing?.ConflictOperation is { Actions.Count: 0 } operation)
            existing = existing with { ConflictOperation = operation with
                { Completed = operation.Completed.Append("as-copy").Distinct(StringComparer.Ordinal).ToArray() } };
        _store.AddNotices([existing is not null ? existing with { Detail = detail } :
            new Notice(record.PairId, 0, NoticeKind.UserDecision, destination ?? record.Path, detail,
                RecoveryRestorePending, id, ConflictOperation: new ConflictNoticeOperation(record.RecoveryId, destination, [], asCopy ? ["as-copy"] : []))]);
    }

    private void ForgetRecoveryBaselines(PairDef pair, string path, long generation)
    {
        foreach (var basis in _store.BaselinesAt(pair.PairId, Side.A, [path])
            .Concat(_store.BaselinesAt(pair.PairId, Side.B, [path])).DistinctBy(basis => basis.ObjectKey))
        {
            if (!PairPath.Same(basis.A.Path, path) || !PairPath.Same(basis.B.Path, path))
                throw new IOException("The restore's linked baseline moved; it remains protected for review.");
            var forget = new SyncAction(ShortId.Make("rrf", pair.PairId, basis.ObjectKey), 0, ActionKind.Forget,
                Side.A, path, null, null, null, false, basis.ObjectKey);
            _store.CommitResult(pair.PairId, generation, forget, new ActionResult(forget.ActionId, Outcome.Done, null, null, null, null));
        }
    }

    // Called AFTER inventory and BEFORE planning. Explicit observations, not a watcher event, discharge protection.
    private async Task<IReadOnlyList<string>> RefreshRecoveryRestoresAsync(PairDef pair, CancellationToken ct)
    {
        var touched = new System.Collections.Concurrent.ConcurrentBag<string>();
        var page = _store.RecoveryRestoreNoticesPage(pair.PairId,
            _recoveryRestoreAfter.GetValueOrDefault(pair.PairId), RecoveryRestorePageLimit);
        _recoveryRestoreAfter[pair.PairId] = page.Count < RecoveryRestorePageLimit ? null : page[^1].NoticeId;
        await Parallel.ForEachAsync(page, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (originalNotice, token) =>
        {
            var notice = originalNotice;
            token.ThrowIfCancellationRequested();
            if (notice.ConflictOperation?.Completed.Contains("ignored") == true) return;
            try
            {
            if (notice.ReasonKey == RecoveryRestoreReady)
            {
                touched.Add(notice.Path);
                if (SidesEqual(pair, notice.Path) && notice.NoticeId is { } readyId) _store.CloseNotice(readyId, "verified");
                return;
            }
                // Automatic retries use the frozen archive identity, never ordinary sync's deletion planner.
                var recoveryId = notice.ConflictOperation?.Choice;
                var record = recoveryId is null ? null : _store.RecoveryPage(pair.PairId,
                    new RecoveryQuery(Id: recoveryId, Limit: 1)).FirstOrDefault();
                if (record is not null)
                {
                    var copy = notice.ConflictOperation?.Completed.Contains("as-copy") == true ||
                        notice.ConflictOperation?.Destination is { } destination && !PairPath.Same(destination, record.Path);
                    await RestoreRecoveryRecordAsync(pair, record, copy, false, token).ConfigureAwait(false);
                    notice = _store.NoticeById(pair.PairId, notice.NoticeId!)?.Notice ?? notice;
                }
                var action = notice.ConflictOperation?.Actions.FirstOrDefault(IsRecoveryRestoreIntent);
                if (action?.Source is not { } source) return;
                var guard = ActionPredicates(pair, LinkExclusions.Of(_store, pair, !_settings.ReplaceOnlineOnlyStubs));
                if (GuardedActions([action with { Source = null }], guard).Any(entry => !Allowed(entry.Guard, entry.Action))) return;
                var target = _endpoints.Writer(action.Target == Side.A ? pair.A : pair.B);
                var observed = await target.ObserveAsync(action.Path, ct).ConfigureAwait(false);
                if (observed is null || observed.Kind != ItemKind.File || observed.Hash is null ||
                    !string.Equals(observed.Hash, source.Hash, StringComparison.OrdinalIgnoreCase) || observed.Size != source.Size) return;
                if (_store.IntentById(pair.PairId, action.ActionId) is { } pending)
                {
                    var results = await _executor.ReconcileAsync([pending], _store.BeginGeneration(pair.A.RootId, RunKind),
                        _endpoints.Writer(pair.A), _endpoints.Writer(pair.B), ct).ConfigureAwait(false);
                    if (results.Single().Outcome != Outcome.Done) return;
                }
                var generation = _store.BeginGeneration(pair.A.RootId, RunKind);
                ForgetRecoveryBaselines(pair, action.Path, generation);
                foreach (var (root, side) in new[] { (pair.A, Side.A), (pair.B, Side.B) })
                {
                    var live = side == action.Target ? observed :
                        await _endpoints.Writer(root).ObserveAsync(action.Path, ct).ConfigureAwait(false);
                    var changes = new List<Change>();
                    foreach (var stale in _store.Live(root.RootId, [action.Path]).Where(item =>
                        PairPath.Same(item.Path, action.Path) && item.ObjectId != live?.ObjectId))
                        changes.Add(new Change(ChangeKind.Delete, stale.ObjectId, stale.Path, null, null));
                    if (live is not null) changes.Add(new Change(ChangeKind.Upsert, live.ObjectId, live.Path, live, null));
                    var cursor = _store.Cursor(root.RootId) ?? "";
                    _store.WriteDelta(new Delta(root.RootId, side, _store.BeginGeneration(root.RootId, RunKind),
                        cursor, cursor, changes, false, []));
                }
                // Never close before planning: this ready row keeps the path dirty across crashes and empty deltas.
                _store.AddNotices([notice with { ReasonKey = RecoveryRestoreReady, Kind = NoticeKind.Info,
                    // The archive identity is not the restored entity. Persist the positively observed target
                    // identity so old opposite-side deletion evidence cannot undo this explicit restore.
                    ConflictOperation = notice.ConflictOperation! with { Actions = [action with { Source = observed }] },
                    Detail = "Restored bytes verified; waiting for ordinary sync to verify both sides." }]);
                touched.Add(action.Path);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                notice = _store.NoticeById(pair.PairId, notice.NoticeId!)?.Notice ?? notice;
                _store.AddNotices([notice with { Detail = "Restore verification failed: " + error.Message + ". Retry or Ignore; the path remains protected." }]);
            }
        }).ConfigureAwait(false);
        return touched.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // A saved first-sync approval predates any later restore. Its frozen destructive decisions must not
    // reinterpret that restore as deletion. Fresh incremental planning still adopts/copies normally.
    internal Plan GuardReviewedRecoveryRestores(PairDef pair, Plan approved)
    {
        bool Protected(string path) => _store.RecoveryRestoreProtected(pair.PairId, path, descendants: true, includeReady: true);
        var blocked = approved.Actions.Where(action => (action.Kind is ActionKind.Delete or ActionKind.Move or ActionKind.Update or ActionKind.KeepBoth) &&
            (Protected(action.Path) || action.FromPath is { } from && Protected(from) ||
             action.Source is { } source && Protected(source.Path))).ToArray();
        if (blocked.Length == 0) return approved;
        var ids = blocked.Select(action => action.ActionId).ToHashSet(StringComparer.Ordinal);
        return approved with
        {
            Actions = approved.Actions.Where(action => !ids.Contains(action.ActionId)).ToArray(),
            SkippedActions = approved.SkippedActions.Concat(blocked).ToArray(),
            Notices = approved.Notices.Concat(blocked.Select(action => new Notice(pair.PairId, 0, NoticeKind.Info,
                action.Path, "This saved approval touches Recovery restore history. Its destructive step was skipped. Use a fresh incremental sync to compare the current files; no restored file was removed.",
                "recovery-reviewed-plan-stale"))).ToArray()
        };
    }

    private async Task<string> RetryRecoveryRestoreAsync(PairDef pair, Notice notice, string choice, CancellationToken ct)
    {
        if (choice == NoticeChoices.Ignore)
        {
            if (notice.ConflictOperation is { } operation)
                _store.AddNotices([notice with { ReasonKey = RecoveryRestorePending, Kind = NoticeKind.UserDecision,
                    ConflictOperation = operation with
                    { Completed = operation.Completed.Append("ignored").Distinct(StringComparer.Ordinal).ToArray() },
                    Detail = "Recovery verification paused by Ignore. The path remains protected and unsynced; Retry resumes verification. No exclusions changed." }]);
            return "Recovery verification paused. Safety protection remains; Retry resumes it. No files or exclusions changed.";
        }
        if (notice.ConflictOperation is { } resumed)
            _store.AddNotices([notice with { ConflictOperation = resumed with
                { Completed = resumed.Completed.Where(value => value != "ignored").ToArray() } }]);
        var recoveryId = notice.ConflictOperation?.Choice;
        var record = recoveryId is null ? null : _store.RecoveryPage(pair.PairId, new RecoveryQuery(Id: recoveryId, Limit: 1)).FirstOrDefault();
        if (record is null) return "That Recovery version is unavailable; the path remains protected. No files changed.";
        try
        {
            var asCopy = notice.ConflictOperation?.Completed.Contains("as-copy") == true ||
                notice.ConflictOperation?.Destination is { } destination && !PairPath.Same(destination, record.Path);
            var result = await RestoreRecoveryRecordAsync(pair, record, asCopy, false, ct).ConfigureAwait(false);
            return result.Classification + ": " + (result.Detail ?? result.Destination ?? result.Path);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            SaveRecoveryRestoreFailure(record, error.Message + ". Retry or Ignore; the path remains protected.");
            return "Restore did not finish; the path remains protected. " + error.Message;
        }
    }
}
