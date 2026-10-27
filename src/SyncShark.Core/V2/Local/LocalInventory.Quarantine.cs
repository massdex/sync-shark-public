namespace Massdex.SyncShark.V2;

public sealed partial class LocalInventory
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, HistoryPass> HistoryPasses = new();
    private sealed class HistoryPass { internal readonly string Id = Guid.NewGuid().ToString("N"); }

    internal static bool UnderAny(string path, IReadOnlySet<string> prefixes)
    {
        while (true)
        {
            if (prefixes.Contains(path)) return true;
            var slash = path.LastIndexOf('/');
            if (slash < 0) return prefixes.Contains("");
            path = path[..slash];
        }
    }

    internal async Task<PilotLocalScan> ApplyHistoryAsync(PilotLocalScan scan, Func<string, bool> include, CancellationToken ct,
        IReadOnlyList<string>? coveredPaths = null, IReadOnlyList<string>? exactPaths = null)
    {
        if (_history is null) return scan;
        var decisions = new Dictionary<string, PilotDecision>(StringComparer.OrdinalIgnoreCase);
        foreach (var decision in scan.Decisions)
            if (!decisions.TryGetValue(decision.RelativePath, out var old) || !old.IsQuarantine)
                decisions[decision.RelativePath] = decision;
        var incomplete = scan.Decisions.Where(d => !d.IsQuarantine).Select(d => d.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var run = RunOperations.ContextIdentity is { } context ? HistoryPasses.GetValue(context, _ => new()).Id : Guid.NewGuid().ToString("N");
        var now = _clock.GetUtcNow();
        DefenderHistoryResult? history = null;
        string[] detected = [];
        var released = 0;
        var completeFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exact = exactPaths is { Count: > 0 } ? _known.Live(_root.RootId, exactPaths) : [];
        var exactIds = exact.Select(i => i.ObjectId).ToHashSet(StringComparer.Ordinal);
        var queued = _known.DefenderDeferralConsent(exactIds.ToArray());
        // A queued folder is one entity, not an implicit recursive watcher scope.
        var baseline = coveredPaths is { Count: 0 } ? [] : _known.QuarantineBaselineItems(_root.RootId, coveredPaths);
        var candidatesToCheck = baseline.Concat(exact.Where(i => queued.ContainsKey(i.ObjectId)));
        // One open-notice read per bound pair per pass.
        var bindings = _known.Pairs().Where(p => p.A.RootId == _root.RootId || p.B.RootId == _root.RootId)
            .Select(p => (Pair: p, Open: _known.DefenderNotices(p.PairId, _root.RootId))).ToArray();
        var batch = PrepareReview(bindings);
        foreach (var page in candidatesToCheck.Chunk(512))
        {
            ct.ThrowIfCancellationRequested();
            var candidates = page.Where(known => include(known.Path) && !LocalItems.IsExcluded(known.Path, _productOwnedRelative) &&
                !UnderAny(known.Path, incomplete) && !scan.Entries.ContainsKey(known.Path) &&
                !(decisions.TryGetValue(known.Path, out var d) && d.IsQuarantine)).ToArray();
            _known.ClearDefenderDeferrals(page.Where(i => scan.Entries.ContainsKey(i.Path) || decisions.TryGetValue(i.Path, out var positive) && positive.IsQuarantine).Select(i => i.ObjectId).ToArray());
            if (candidates.Length == 0) continue;
            var state = _known.DefenderDeferrals(candidates.Select(i => i.ObjectId).ToArray());
            DefenderConsent Choice(ItemState item) => state.TryGetValue(item.ObjectId, out var row) ? row.Consent : DefenderConsent.Pending;
            // The user's Keep held costs no Defender history query on every run: it is rechecked weekly, or after the
            // user acts (a release becomes Proceed, which is always rechecked for positive evidence first).
            bool Settled(ItemState item) => state.TryGetValue(item.ObjectId, out var row) && row.Consent == DefenderConsent.Keep &&
                row.Updated > now - DefenderReview.KeepRecheck && !exactIds.Contains(item.ObjectId);
            foreach (var kept in candidates.Where(Settled)) decisions[kept.Path] = HeldByChoice(kept);
            candidates = candidates.Where(i => !Settled(i)).ToArray();
            if (candidates.Length == 0) continue;
            if (history is null)
            {
                try { history = await _history.ReadAsync(ct).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
                { history = DefenderHistoryResult.Unknown; }
                // Sorted once, case-insensitively: an exact path or a folder prefix is a binary search, never folders x detections.
                detected = history.Detections.Select(d => DefenderHistory.RelativePath(_root.RootPath, d.Resource))
                    .Where(p => p is not null).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            bool Matched(ItemState item) => DetectedAt(detected, item.Path) || item.Kind == ItemKind.Folder && DetectedUnder(detected, item.Path).Any();
            // A Keep-held row rechecked here is touched, so the next weekly recheck starts from now.
            var uncertain = candidates.Where(i => !Matched(i) && (!history.Complete || Choice(i) == DefenderConsent.Keep))
                .Select(i => i.ObjectId).ToArray();
            var expired = _known.DeferDefenderHistory(uncertain, run, now);
            _known.AssignDefenderBatch(uncertain.Where(id => history.LimitReached || history.Availability != DefenderAvailability.Inactive && expired.Contains(id)).ToArray(), batch);
            var clear = new List<string>();
            var release = new List<string>();
            foreach (var known in candidates)
            {
                var choice = Choice(known);
                if (Matched(known))
                {
                    // Positive evidence always wins over any consent and ends this absence epoch.
                    clear.Add(known.ObjectId);
                    decisions[known.Path] = Positive(known.Path, known.Kind,
                        "Defender history reports this exact missing previously synced path. Held, not deleted; explicit recovery required.");
                    if (known.Kind == ItemKind.Folder)
                        foreach (var child in _known.Live(_root.RootId, DetectedUnder(detected, known.Path).ToArray()))
                        {
                            clear.Add(child.ObjectId);
                            decisions[child.Path] = Positive(child.Path, child.Kind,
                                "Defender history reports this exact missing path. Held with its enclosing folder.");
                        }
                    continue;
                }
                if (choice == DefenderConsent.Keep)
                {
                    decisions[known.Path] = HeldByChoice(known);
                    continue;
                }
                if (history.Complete)
                {
                    // Complete no-match is evidence. A folder stays queued until the ordinary walk settles its children.
                    if (known.Kind == ItemKind.Folder)
                    {
                        completeFolders.Add(known.Path);
                        release.Add(known.ObjectId);
                    }
                    else clear.Add(known.ObjectId);
                    continue;
                }
                if (choice is DefenderConsent.Proceed or DefenderConsent.Released) continue;
                if (history.Availability == DefenderAvailability.Inactive && expired.Contains(known.ObjectId))
                {
                    // Persisted as Released, so later passes and overlapping bindings never restart its counter.
                    release.Add(known.ObjectId);
                    released++;
                    continue;
                }
                decisions[known.Path] = new PilotDecision(known.Path, PilotDecisionKind.Blocked,
                    "Couldn't check complete Defender history. Deletion deferred automatically; the next pass will check again.")
                { DefenderHistoryUnknown = true, QuarantineFile = known.Kind == ItemKind.File };
            }
            _known.ClearDefenderDeferrals(clear);
            _known.ReleaseDefenderDeferrals(release);
        }
        if (_known.DefenderBatchCount(batch) is > 0 and var count)
            foreach (var (pair, _) in bindings)
                _known.AddNotices([new Notice(pair.PairId, 30, NoticeKind.UserDecision, "",
                    $"Couldn't confirm with Windows Defender for {count} deleted item(s). " +
                    (history?.LimitReached == true ? "The history size/entry limit was reached. " : "The 3-run or 24-hour check bound was reached. ") +
                    "Proceed with deletions applies only to this reviewed batch; positive Defender evidence remains held. " +
                    "Keep held moves them to the standing 'Held by your choice' notice. Later deletions, if any, get their own review.",
                    DefenderReview.ReviewKey(_root.RootId, batch),
                    DefenderHistory: new(_root.RootId, batch, count, history?.LimitReached == true ? "history-cap" : "history-bound",
                        DefenderReview.LocalRoot(pair, _root.RootId)))]);
        DefenderReview.RefreshHeld(_known, _root.RootId, bindings);
        if (released > 0)
            foreach (var (pair, _) in bindings)
                _known.AddNotices([new Notice(pair.PairId, 30, NoticeKind.Info, "",
                    $"Couldn't check Defender history for {released} missing items. Defender is confirmed inactive and the automatic deferral expired; ordinary sync rules now apply. Positive detections remain protected.",
                    "defender-history-unavailable")]);
        return scan with { Decisions = decisions.Values.ToArray(), DefenderHistoryClearFolders = completeFolders };
    }

    /// <summary>
    /// Freezes published membership. Newly eligible entities and displaced members join a fresh review,
    /// even while another review is open; they never inherit that review's click.
    /// </summary>
    private string PrepareReview(IReadOnlyList<(PairDef Pair, IReadOnlyList<Notice> Open)> bindings)
    {
        var changed = new List<string>();
        foreach (var (pair, open) in bindings)
            foreach (var notice in open.Where(n => n.DefenderHistory!.Cause is not (DefenderReview.HeldCause or DefenderReview.SharedReleaseCause)))
            {
                var review = notice.DefenderHistory!;
                if (review.ReviewedRoot == DefenderReview.LocalRoot(pair, _root.RootId) && _known.DefenderBatchCount(review.Batch) > 0)
                    continue;
                _known.CloseNotice(notice.NoticeId!, DefenderReview.ReviewChanged);
                changed.Add(review.Batch);
            }
        var batch = Guid.NewGuid().ToString("N");
        foreach (var old in changed.Distinct(StringComparer.Ordinal))
        {
            // Close every binding of the displaced review before moving its members.
            foreach (var (_, open) in bindings)
                foreach (var notice in open.Where(n => n.DefenderHistory?.Batch == old))
                    _known.CloseNotice(notice.NoticeId!, DefenderReview.ReviewChanged);
            _known.ReopenDefenderBatch(old, batch);
        }
        return batch;
    }

    private static PilotDecision HeldByChoice(ItemState item) => new(item.Path, PilotDecisionKind.Blocked,
        "Held by your choice: not deleted from Dropbox. Release it, or stop syncing it, from the 'Held by your choice' notice.")
        { DefenderHistoryUnknown = true, QuarantineFile = item.Kind == ItemKind.File };

    private static PilotDecision Positive(string path, ItemKind kind, string reason) =>
        new(path, PilotDecisionKind.Blocked, reason) { DefenderHistoryMatch = true, QuarantineFile = kind == ItemKind.File };

    private static int LowerBound(string[] sorted, string value)
    {
        var at = Array.BinarySearch(sorted, value, StringComparer.OrdinalIgnoreCase);
        return at >= 0 ? at : ~at;
    }

    private static bool DetectedAt(string[] sorted, string path)
    {
        var at = LowerBound(sorted, path);
        return at < sorted.Length && string.Equals(sorted[at], path, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> DetectedUnder(string[] sorted, string folder)
    {
        var prefix = folder + "/";
        for (var at = LowerBound(sorted, prefix); at < sorted.Length && sorted[at].StartsWith(prefix, StringComparison.OrdinalIgnoreCase); at++)
            yield return sorted[at];
    }
}
