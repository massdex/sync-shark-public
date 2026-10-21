namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    internal const string RecoveryRestorePending = "recovery-restore-pending";
    internal const string RecoveryRestoreReady = "recovery-restore-ready";
    private const int RecoveryRestorePageLimit = 500;
    // Scheduling convenience only: notices and protection remain durable if a process restarts.
    private readonly Dictionary<string, string?> _recoveryRestoreAfter = new(StringComparer.Ordinal);

    // RQ-060/032: immutable archive bytes, create-only writes and item-owned durable recovery.
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
        // Bound memory to this page, but select winners against the entire filtered catalog.
        // A page boundary must not turn an older version into a winner. Same-path copies run serially.
        await Parallel.ForEachAsync(Enumerable.Range(0, records.Count).GroupBy(index => PathKey.Of(records[index].Path)), new ParallelOptions
        { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (indices, itemToken) =>
        {
            foreach (var index in indices)
            {
                var record = records[index];
                try
                {
                    if (query.Id is null && !asCopy &&
                        _store.RecoveryNewestForPath(pairId, query, record.Path)?.RecoveryId != record.RecoveryId)
                    {
                        results[index] = RecoveryResult(record, "SUPERSEDED", detail:
                            "A newer retained version of this path was selected within the filter. Use --id or --as-copy to restore this version.");
                        continue;
                    }
                    if (unavailable is not null || pair is null)
                        throw new IOException(unavailable ?? "The folder pair is unavailable.");
                    results[index] = await RestoreRecoveryRecordAsync(pair, record, asCopy, dryRun, itemToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    var detail = "Recovery restore did not finish: " + error.Message + " Retry after fixing the cause, or dismiss the notice.";
                    if (!dryRun) SaveRecoveryRestoreFailure(record, detail, asCopy: asCopy);
                    results[index] = RecoveryResult(record, "FAILED", detail: detail);
                }
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
        var opposite = _endpoints.Writer(record.Side == Side.A ? pair.B : pair.A);
        // Notice identity belongs to the receipt; executor identity belongs to one durable attempt.
        // Reconcile the old destination BEFORE reading expiry or selecting another copy name.
        if (!dryRun && _store.IntentById(pair.PairId, frozen?.ActionId ?? id) is { } pending)
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
        var priorDestination = frozen?.Path;
        var previous = frozen is null ? null : await target.ObserveAsync(frozen.Path, ct).ConfigureAwait(false);
        if (savedRow is { Closed: true })
        {
            if (!asCopy && previous is not null && !RecoveryContentMatches(previous, record))
                return RecoveryResult(record, "CONFLICT", frozen?.Path,
                    "The completed restore destination has changed. It was left unchanged; use --as-copy to retain both versions.");
            if (!asCopy && previous is not null) return RecoveryResult(record, "SKIP-PRESENT", frozen!.Path);
            // Missing completed destinations and explicit copies are new durable attempts, not executor replay.
            frozen = null;
        }
        else if (previous is not null)
        {
            if (RecoveryContentMatches(previous, record)) return RecoveryResult(record, "SKIP-PRESENT", frozen!.Path);
            // A different live entity is not the restored object. Once no provider effect remains
            // unresolved, retire this path hold rather than requiring unrelated bytes to match forever.
            const string detail = "The destination now contains different current bytes. They were left unchanged; Restore as a copy or Dismiss this notice.";
            if (!dryRun && _store.IntentById(pair.PairId, frozen!.ActionId) is null)
                _store.AddNotices([saved! with { Kind = NoticeKind.Info, ReasonKey = RecoveryRestorePending,
                    Detail = detail, ConflictOperation = new ConflictNoticeOperation(record.RecoveryId, frozen.Path, [], []) }]);
            return RecoveryResult(record, "CONFLICT", frozen!.Path, detail);
        }
        var actionId = frozen?.ActionId ?? ShortId.Make("rrs", record.PairId, record.RecoveryId, Guid.NewGuid().ToString("N"));
        if (record.Expires <= _time.GetUtcNow() || target is not IRecoveryRecordReader reader)
            return RecoveryUnavailable(record, dryRun, asCopy,"The retained version has expired or this provider cannot read it.");
        ItemState source;
        try { source = await reader.DescribeRecoveryAsync(record, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is FileNotFoundException or InvalidDataException)
        { return RecoveryUnavailable(record, dryRun, asCopy,"The exact retained version is unavailable: " + error.Message); }
        if (!RecoveryContentMatches(source, record))
            return RecoveryUnavailable(record, dryRun, asCopy,"The retained version metadata did not match its durable Recovery record.");
        var destination = frozen?.Path ?? (savedRow is { Closed: true } && !asCopy ? priorDestination : null) ?? record.Path;
        if (asCopy && frozen is null)
        {
            var found = false;
            for (var attempt = 1; attempt <= Math.Clamp(_settings.MaxLabelAttempts, 1, 100); attempt++)
            {
                destination = RecoveryCopyPath(record.Path, _time.GetUtcNow(), attempt);
                if (await target.ObserveAsync(destination, ct).ConfigureAwait(false) is not null ||
                    await opposite.ObserveAsync(destination, ct).ConfigureAwait(false) is not null) continue;
                found = true;
                break;
            }
            if (!found) throw new IOException("No unused restore-copy name was found; rename an existing copy and Retry.");
        }
        var action = frozen ?? new SyncAction(actionId, 0, ActionKind.CopyNew, record.Side, destination,
            null, source, null, false, null) { SuppressReconciledBaseline = true };
        var other = await opposite.ObserveAsync(destination, ct).ConfigureAwait(false);
        if (other is not null && !RecoveryContentMatches(other, record))
        {
            const string detail = "The opposite side has a different current item. Both sides were left unchanged; use --as-copy to retain both versions.";
            if (!dryRun) SaveRecoveryRestoreFailure(record, detail, destination, asCopy);
            return RecoveryResult(record, "CONFLICT", destination, detail);
        }
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
            const string detail = "This path is protected by exclusions, ignore rules, a link or a nested pair. Include it or choose another location, then Retry; Dismiss closes this notice.";
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
        await using var bytes = await reader.OpenRecoveryRecordAsync(record, source, ct).ConfigureAwait(false);
        if (!bytes.CanSeek || bytes.Length != record.Size ||
            !string.Equals(await DropboxContentHash.ComputeAsync(bytes, ct).ConfigureAwait(false), record.Hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Retained bytes failed verification; no destination was written.");
        bytes.Position = 0;
        // Reading a remote archive can take time. Recheck the opposite current version before any
        // baseline mutation or write; create-only on the target independently closes its write race.
        other = await opposite.ObserveAsync(destination, ct).ConfigureAwait(false);
        if (other is not null && !RecoveryContentMatches(other, record))
        {
            const string detail = "The opposite destination changed while reading Recovery. Nothing was written; use --as-copy to retain both versions.";
            SaveRecoveryRestoreFailure(record, detail, destination, asCopy);
            return RecoveryResult(record, "CONFLICT", destination, detail);
        }
        // Archive read/hash failure has no effect intent and must never hold a user's live path.
        _store.AddNotices([notice]); // Persist the attempt before baseline changes and provider effects.
        var generation = _store.BeginGeneration(targetRoot.RootId, RunKind);
        ForgetRecoveryBaselines(pair, destination, generation);
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
        if (!dryRun) SaveRecoveryRestoreFailure(record, detail + " Retry when available, or Dismiss. No restore was started.", asCopy: asCopy);
        return RecoveryResult(record, "NO-VERSION", detail: detail);
    }

    private void SaveRecoveryRestoreFailure(RecoveryRecord record, string detail, string? destination = null, bool asCopy = false)
    {
        var id = RecoveryRestoreId(record);
        var row = _store.NoticeById(record.PairId, id);
        var existing = row is { Closed: false } ? row.Value.Notice : null;
        if (asCopy && existing?.ConflictOperation is { Actions.Count: 0 } operation)
            existing = existing with { ConflictOperation = operation with
                { Completed = operation.Completed.Append("as-copy").Distinct(StringComparer.Ordinal).ToArray() } };
        var protects = existing?.ConflictOperation?.Actions.Any(IsRecoveryRestoreIntent) == true;
        _store.AddNotices([existing is not null ? existing with { Detail = detail,
                Kind = protects ? NoticeKind.UserDecision : NoticeKind.Info } :
            new Notice(record.PairId, 0, NoticeKind.Info, destination ?? record.Path, detail,
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
        await Parallel.ForEachAsync(page.GroupBy(notice => PathKey.Of(notice.Path)),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (notices, token) =>
        {
            foreach (var originalNotice in notices)
                await RefreshOneAsync(originalNotice, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return touched.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        async Task RefreshOneAsync(Notice originalNotice, CancellationToken token)
        {
            var notice = originalNotice;
            token.ThrowIfCancellationRequested();
            if (notice.ConflictOperation?.Completed.Contains("ignored") == true) return;
            try
            {
            if (notice.ReasonKey == RecoveryRestoreReady)
            {
                touched.Add(notice.Path);
                // Ready may survive a crash before convergence. Without a baseline, a newly arrived
                // opposite version must not enter newest-wins against the exact restored entity.
                var readyAction = notice.ConflictOperation?.Actions.FirstOrDefault(IsRecoveryRestoreIntent);
                var rehold = false;
                if (readyAction?.Source is { } verified &&
                    _store.BaselinesAt(pair.PairId, Side.A, [notice.Path]).Count == 0 &&
                    _store.BaselinesAt(pair.PairId, Side.B, [notice.Path]).Count == 0)
                {
                    var liveTarget = await _endpoints.Writer(readyAction.Target == Side.A ? pair.A : pair.B)
                        .ObserveAsync(readyAction.Path, token).ConfigureAwait(false);
                    if (liveTarget is not null && liveTarget.ObjectId == verified.ObjectId &&
                        liveTarget.Kind == verified.Kind && liveTarget.Size == verified.Size &&
                        string.Equals(liveTarget.Hash, verified.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        var liveOther = await _endpoints.Writer(readyAction.Target == Side.A ? pair.B : pair.A)
                            .ObserveAsync(readyAction.Path, token).ConfigureAwait(false);
                        rehold = liveOther is not null && (liveOther.Kind != liveTarget.Kind || liveOther.Size != liveTarget.Size ||
                            !string.Equals(liveOther.Hash, liveTarget.Hash, StringComparison.OrdinalIgnoreCase));
                    }
                }
                if (!rehold)
                {
                    if (SidesEqual(pair, notice.Path) && notice.NoticeId is { } readyId) _store.CloseNotice(readyId, "verified");
                    return;
                }
                notice = notice with { ReasonKey = RecoveryRestorePending, Kind = NoticeKind.UserDecision };
                _store.AddNotices([notice]); // Protect before the ordinary planner can see the differing pair.
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
                var targetRoot = action.Target == Side.A ? pair.A : pair.B;
                var target = _endpoints.Writer(targetRoot);
                var observed = await target.ObserveAsync(action.Path, token).ConfigureAwait(false);
                if (observed is null || observed.Kind != ItemKind.File || observed.Hash is null ||
                    !string.Equals(observed.Hash, source.Hash, StringComparison.OrdinalIgnoreCase) || observed.Size != source.Size) return;
                if (_store.IntentById(pair.PairId, action.ActionId) is { } pending)
                {
                    var results = await _executor.ReconcileAsync([pending], _store.BeginGeneration(targetRoot.RootId, RunKind),
                        _endpoints.Writer(pair.A), _endpoints.Writer(pair.B), token).ConfigureAwait(false);
                    if (results.Single().Outcome != Outcome.Done) return;
                }
                var other = await _endpoints.Writer(action.Target == Side.A ? pair.B : pair.A)
                    .ObserveAsync(action.Path, token).ConfigureAwait(false);
                if (other is not null && (other.Kind != ItemKind.File || other.Hash != observed.Hash || other.Size != observed.Size))
                    throw new IOException($"The restored file at side {action.Target}, '{action.Path}', is verified, but side " +
                        $"{(action.Target == Side.A ? Side.B : Side.A)}, '{action.Path}', now contains different bytes. " +
                        $"Keep both files: rename or move the file on side {(action.Target == Side.A ? Side.B : Side.A)} " +
                        $"from '{action.Path}' to an included name that is unused on BOTH sides. If that name exists, choose another. " +
                        "Leave the restored target in place and do not delete or overwrite either file; then choose Retry and run sync. " +
                        "Retry verifies the restored original and syncs both distinct paths. Ignore only pauses verification; it does not resolve this difference.");
                var generation = _store.BeginGeneration(targetRoot.RootId, RunKind);
                ForgetRecoveryBaselines(pair, action.Path, generation);
                foreach (var (root, side) in new[] { (pair.A, Side.A), (pair.B, Side.B) })
                {
                    var live = side == action.Target ? observed :
                        other;
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
                var protects = notice.ConflictOperation?.Actions.Any(IsRecoveryRestoreIntent) == true;
                _store.AddNotices([notice with { Detail = "Restore verification failed: " + error.Message +
                    (protects ? ". Retry or Ignore; the frozen restore remains protected."
                        : ". Retry after fixing the cause, or Dismiss. This actionless notice does not protect the path.") }]);
            }
        }
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
        if (choice is NoticeChoices.Ignore or NoticeChoices.Dismiss)
        {
            if (notice.ConflictOperation?.Actions.Any(IsRecoveryRestoreIntent) != true)
            {
                _store.CloseNotice(notice.NoticeId!, "dismiss");
                return "Recovery notice dismissed. No files, exclusions or protection changed.";
            }
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
        if (record is null) return "That Recovery version is unavailable. No files changed; Dismiss closes an actionless notice.";
        try
        {
            var asCopy = choice == NoticeChoices.RestoreAsCopy || notice.ConflictOperation?.Completed.Contains("as-copy") == true ||
                notice.ConflictOperation?.Destination is { } destination && !PairPath.Same(destination, record.Path);
            var result = await RestoreRecoveryRecordAsync(pair, record, asCopy, false, ct).ConfigureAwait(false);
            return result.Classification + ": " + (result.Detail ?? result.Destination ?? result.Path);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            SaveRecoveryRestoreFailure(record, error.Message + ". Retry after fixing the cause, or dismiss an actionless notice.",
                asCopy: choice == NoticeChoices.RestoreAsCopy);
            return "Restore did not finish. Only an unresolved frozen effect remains protected. " + error.Message;
        }
    }
}
