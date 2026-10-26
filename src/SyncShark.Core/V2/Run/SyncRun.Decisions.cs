using System.Globalization;

namespace Massdex.SyncShark.V2;

// Decision 64: the needs-decision paths' fresh observation, their version token and the one verb that applies a
// decision. The token pins the exact notice id and both sides' current state, so a case-variant notice is never
// confused and a path that changed since the user looked is refused before any effect.
public sealed partial class SyncRun
{
    private const char DecisionTokenSeparator = ':';
    private const string DecisionNoLongerOpen = "That decision is no longer open; nothing was changed.";
    private const string DecisionPathMismatch = "The decision token does not match that path; run decisions list again.";
    private const string DecisionChangedSinceLooked = "The file changed since you looked; run decisions list again.";
    // Folder decisions remain refused without subtree evidence (B-172), except an identity-bound
    // case-only spelling choice: it moves one folder and never authorizes descendant effects.
    private const string DecisionFolderNotResolvable =
        "folder decisions can't be resolved yet â€” decide the files inside, or use a per-path choice";

    /// <summary>The reason a resolved decision could not be applied; the CLI surfaces it with the run result.</summary>
    internal const string DecisionNotApplied = "decision could not be applied";

    /// <summary>
    /// Fresh both-sides observations and version tokens for these open decisions, under the run gate. Each side is
    /// observed at the path directly, so a change on disk is seen even though the path is left out of the scan while
    /// its notice is open.
    /// </summary>
    internal Task<IReadOnlyList<DecisionSnapshot>> ObserveDecisionsAsync(string pairId,
        IReadOnlyList<(string NoticeId, string Path)> decisions, CancellationToken ct) =>
        RunExclusiveAsync(null, async (_, token) =>
        {
            var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("No pair " + pairId);
            var snapshots = new List<DecisionSnapshot>(decisions.Count);
            if (decisions.Count > 0) await _endpoints.PrepareAsync(pair, ct).ConfigureAwait(false);
            var a = _endpoints.Writer(pair.A);
            var b = _endpoints.Writer(pair.B);
            foreach (var (noticeId, path) in decisions)
            {
                var notice = _store.UnresolvedNotices(pairId).FirstOrDefault(n => n.NoticeId == noticeId);
                var frozen = notice is not null && IsCaseSpellingDecision(notice) ? notice.ConflictVersions : null;
                var observedA = await ObserveAsync(a, frozen?.A.Path ?? path, token).ConfigureAwait(false);
                var observedB = await ObserveAsync(b, frozen?.B.Path ?? path, token).ConfigureAwait(false);
                snapshots.Add(new DecisionSnapshot(noticeId, path, observedA, observedB,
                    Token(noticeId, observedA, observedB)));
            }
            return (IReadOnlyList<DecisionSnapshot>)snapshots;
        }, ct, admissionPairId: pairId);

    /// <summary>
    /// Applies the chosen side on the decision pinned by <paramref name="decisionToken"/>: the path is released from
    /// the pending set and planned once with <paramref name="winner"/> as the rule-32 winner. The token is re-derived
    /// from a fresh observation in the same gate hold and must match. The notice closes only on evidence (the winner's
    /// effect completed and no relevant effect is unfinished, or both sides now hold equal content); otherwise it stays
    /// open and this reports "decision could not be applied".
    /// </summary>
    public Task<RunReport> ResolveDecisionAsync(string pairId, string relativePath, string decisionToken, Side winner,
        IProgress<RunProgress> progress, CancellationToken ct) =>
        RunExclusiveAsync(progress, async (sink, token) =>
        {
            var noticeId = DecisionNoticeId(decisionToken) ?? throw new SyncRunRefusedException(DecisionNoLongerOpen);
            var notice = _store.UnresolvedNotices(pairId).FirstOrDefault(open =>
                string.Equals(open.NoticeId, noticeId, StringComparison.Ordinal) && open.Kind == NoticeKind.UserDecision)
                ?? throw new SyncRunRefusedException(DecisionNoLongerOpen);
            var caseChoice = IsCaseSpellingDecision(notice);
            if (notice.ConflictVersions is not null && !caseChoice)
                throw new SyncRunRefusedException("This notice identifies a particular incoming item. Use its rename, replace or dismiss options instead of a generic path winner.");
            if (!PairPath.Same(notice.Path, relativePath))
                throw new SyncRunRefusedException(DecisionPathMismatch);
            var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("No pair " + pairId);
            await _endpoints.PrepareAsync(pair, ct).ConfigureAwait(false);
            var observedA = await ObserveAsync(_endpoints.Writer(pair.A), caseChoice ? notice.ConflictVersions!.A.Path : notice.Path, token).ConfigureAwait(false);
            var observedB = await ObserveAsync(_endpoints.Writer(pair.B), caseChoice ? notice.ConflictVersions!.B.Path : notice.Path, token).ConfigureAwait(false);
            if (caseChoice && notice.ConflictVersions!.A.Kind == ItemKind.Folder &&
                notice.ConflictOperation is { } saved)
                return await ResumeFolderSpellingChoiceAsync(pair, noticeId, saved, decisionToken,
                    observedA, observedB, winner, token).ConfigureAwait(false);
            if (caseChoice && !CaseChoiceStillCurrent(pairId, notice, observedA, observedB))
            {
                RefreshStaleSpellingNotice(notice, observedA, observedB);
            }
            if (!caseChoice && (observedA?.Kind == ItemKind.Folder || observedB?.Kind == ItemKind.Folder))
                throw new SyncRunRefusedException(DecisionFolderNotResolvable);
            if (!string.Equals(decisionToken, Token(noticeId, observedA, observedB), StringComparison.Ordinal))
                throw new SyncRunRefusedException(DecisionChangedSinceLooked);
            if (caseChoice && observedA!.Kind == ItemKind.Folder)
                return await ApplyFolderSpellingChoiceAsync(pair, notice, observedA, observedB!, winner, token).ConfigureAwait(false);
            var report = await IncrementalCoreAsync(pairId,
                new Dictionary<string, Side>(StringComparer.OrdinalIgnoreCase) { [notice.Path] = winner },
                notice.NoticeId, sink, token);
            if (caseChoice)
                await CloseProvedSpellingChoiceAsync(pair, notice, observedA!, observedB!, winner, token).ConfigureAwait(false);
            return DecisionReport(pair, notice, report);
        }, ct, admissionPairId: pairId);

    private RunReport DecisionReport(PairDef pair, Notice notice, RunReport report)
    {
        // The notice closes only on evidence, so a still-open one means the decision did not take effect.
        if (!_store.UnresolvedNotices(pair.PairId).Any(open => open.NoticeId == notice.NoticeId)) return report;
        var (refusedA, refusedB) = ProviderRefusedPaths.Load(_store, pair);
        var refused = _store.Live(pair.A.RootId, [notice.Path]).Any(refusedA.Contains) ||
            _store.Live(pair.B.RootId, [notice.Path]).Any(refusedB.Contains);
        var reason = DecisionNotApplied + ": " + (refused ? ProviderNameRefusedException.Detail : NotAppliedReason(pair.PairId, notice.Path));
        if (refused)
            return report with { DecisionFailureReason = reason };
        return report with
        {
            Failures = report.Failures.Append(new ActionResult(notice.NoticeId!, Outcome.Failed, null, null,
                null, reason)).Take(_settings.FailureSampleLimit).ToArray(),
            DecisionFailureReason = reason,
            Failed = report.Failed + 1
        };
    }

    private async Task<RunReport> ResumeFolderSpellingChoiceAsync(PairDef pair, string noticeId,
        ConflictNoticeOperation saved, string decisionToken, ItemState? a, ItemState? b, Side winner, CancellationToken ct)
    {
        if (!string.Equals(decisionToken, Token(noticeId, a, b), StringComparison.Ordinal))
            throw new SyncRunRefusedException(DecisionChangedSinceLooked);
        if (saved.Choice != (winner == Side.A ? NoticeChoices.KeepA : NoticeChoices.KeepB))
            throw new SyncRunRefusedException("Finish the saved spelling choice before choosing the other side.");
        return FolderSpellingReport(pair.PairId,
            await ApplyConflictOperationAsync(pair, noticeId, saved, ct).ConfigureAwait(false));
    }

    private async Task CloseProvedSpellingChoiceAsync(PairDef pair, Notice notice, ItemState a, ItemState b,
        Side winner, CancellationToken ct)
    {
        var afterA = await ObserveAsync(_endpoints.Writer(pair.A), a.Path, ct).ConfigureAwait(false);
        var afterB = await ObserveAsync(_endpoints.Writer(pair.B), b.Path, ct).ConfigureAwait(false);
        var chosen = DropboxNameMapping.MatchPath(winner == Side.A ? a : b);
        if (afterA is not null && afterB is not null && afterA.ObjectId == a.ObjectId && afterB.ObjectId == b.ObjectId &&
            PairPath.SameLeafLocation(DropboxNameMapping.MatchPath(afterA), chosen) &&
            PairPath.SameLeafLocation(DropboxNameMapping.MatchPath(afterB), chosen) &&
            !_store.UnresolvedIntents(pair.PairId).Any(action => PairPath.IsSelfOrUnder(action.Path, notice.Path)))
            _store.CloseNotice(notice.NoticeId!, winner == Side.A ? "A" : "B");
    }

    private static async Task<bool> ConflictRetryCurrentAsync(SyncAction action,
        IEndpointWriter a, IEndpointWriter b, CancellationToken ct)
    {
        if (action.ExpectedTarget is { Kind: ItemKind.Folder } folder &&
            await (action.Target == Side.A ? a : b).ObserveAsync(folder.Path, ct).ConfigureAwait(false) is { } now &&
            now.ObjectId != folder.ObjectId) return false;
        return await FolderSpellingRetryCurrentAsync(action, a, b, ct).ConfigureAwait(false);
    }

    private static async Task<bool> FolderSpellingRetryCurrentAsync(SyncAction action,
        IEndpointWriter a, IEndpointWriter b, CancellationToken ct)
    {
        if (action is not { Kind: ActionKind.Move, Source.Kind: ItemKind.Folder,
            ExpectedTarget.Kind: ItemKind.Folder, FromPath: not null } move ||
            !PairPath.Same(move.Path, move.FromPath)) return true;
        var source = await (move.Target == Side.A ? b : a).ObserveAsync(move.Source.Path, ct).ConfigureAwait(false);
        var target = await (move.Target == Side.A ? a : b).ObserveAsync(move.FromPath, ct).ConfigureAwait(false);
        return source?.ObjectId == move.Source.ObjectId && source.Path == move.Source.Path &&
            target?.ObjectId == move.ExpectedTarget.ObjectId && target.Path == move.ExpectedTarget.Path;
    }

    private void RefreshStaleSpellingNotice(Notice notice, ItemState? observedA, ItemState? observedB)
    {
        var noticeId = notice.NoticeId!;
        const string stale = "The folder or file identity or exact spelling changed since this notice was raised. Refresh the decision; nothing was changed.";
        var refreshed = notice with { NoticeId = null, Detail = stale, ConflictOperation = null };
        if (observedA is not null && observedB is not null &&
            observedA.ObjectId == notice.ConflictVersions!.Base!.A.ObjectId &&
            observedB.ObjectId == notice.ConflictVersions.Base.B.ObjectId &&
            DropboxNameMapping.SamePlace(observedA, observedB) &&
            !PairPath.SameLeafLocation(DropboxNameMapping.MatchPath(observedA), DropboxNameMapping.MatchPath(observedB)))
            refreshed = refreshed with { ConflictVersions = notice.ConflictVersions with { A = observedA, B = observedB } };
        else
            // Retire obsolete authority and release the row to ordinary planning, never transfer
            // the old identity's choice onto its replacement or keep an impossible choice open.
            refreshed = refreshed with { Kind = NoticeKind.Info, ReasonKey = "stale-spelling", ConflictVersions = null };
        WriteNotices([refreshed]);
        _store.CloseNotice(noticeId, "stale-spelling");
        throw new SyncRunRefusedException(stale);
    }

    private async Task<RunReport> ApplyFolderSpellingChoiceAsync(PairDef pair, Notice notice,
        ItemState a, ItemState b, Side winner, CancellationToken ct)
    {
        // Plan only the frozen correspondence, not an incremental pass: child work and scan cursors
        // remain queued for AutoSync. The existing explicit-action owner retains retry checkpoints.
        var plan = Planner.Decide(pair.PairId, 0,
            [new PlanRow(notice.ConflictVersions!.Base, a, b, new Evidence(false, false, true, true))],
            Options(pair, false, new Dictionary<string, Side?> { [a.Path] = winner }, null));
        if (plan.Actions.Count != 1 || plan.Actions[0] is not { Kind: ActionKind.Move } move ||
            move.Source?.Kind != ItemKind.Folder || move.FromPath is null ||
            !PairPath.Same(move.Path, move.FromPath))
            throw new SyncRunRefusedException("The spelling choice no longer describes one folder Move; nothing was changed.");
        var operation = new ConflictNoticeOperation(winner == Side.A ? NoticeChoices.KeepA : NoticeChoices.KeepB,
            null, [move with { ActionId = ShortId.Make("nch", pair.PairId, notice.NoticeId! + ":case"), CallerOwnsNotice = true }], []);
        _store.SaveConflictOperation(pair.PairId, notice.NoticeId!, operation);
        var result = await ApplyConflictOperationAsync(pair, notice.NoticeId!, operation, ct).ConfigureAwait(false);
        return FolderSpellingReport(pair.PairId, result);
    }

    private static RunReport FolderSpellingReport(string pairId, ActionResult result)
    {
        var done = result.Outcome == Outcome.Done;
        return new RunReport(pairId, done ? RunOutcomes.Complete : RunOutcomes.Partial,
            done ? 1 : 0, done ? 0 : 1, 0, 0, 0, 0, done ? 1 : 0, 0, done ? [] : [result])
        { DecisionFailureReason = done ? null : result.Reason };
    }

    private static bool IsCaseSpellingDecision(Notice notice) => notice.Rule == 20 &&
        notice.ConflictVersions is { Base: not null, Incoming: null } versions &&
        versions.A.Kind == versions.B.Kind && DropboxNameMapping.SamePlace(versions.A, versions.B) &&
        !PairPath.SameLeafLocation(DropboxNameMapping.MatchPath(versions.A), DropboxNameMapping.MatchPath(versions.B));

    private bool CaseChoiceStillCurrent(string pairId, Notice notice, ItemState? a, ItemState? b)
    {
        var frozen = notice.ConflictVersions!;
        if (a is null || b is null || a.Kind != frozen.A.Kind || b.Kind != frozen.B.Kind ||
            a.ObjectId != frozen.A.ObjectId || b.ObjectId != frozen.B.ObjectId ||
            a.Path != frozen.A.Path || b.Path != frozen.B.Path) return false;
        var baseline = _store.Baselines(pairId, [frozen.Base!.ObjectKey]).SingleOrDefault();
        return baseline is not null && baseline.A.ObjectId == a.ObjectId && baseline.B.ObjectId == b.ObjectId;
    }

    /// <summary>The notice id a decision token pins, or null when the token is malformed.</summary>
    internal static string? DecisionNoticeId(string token)
    {
        var cut = token.IndexOf(DecisionTokenSeparator);
        return cut <= 0 ? null : token[..cut];
    }

    // The token pins the notice id and both sides' freshly observed state; any of it changing makes the token differ.
    private static string Token(string noticeId, ItemState? a, ItemState? b) =>
        noticeId + DecisionTokenSeparator + ShortId.Hex16(Part(a), Part(b));

    private static string Part(ItemState? item) => item is null
        ? "absent"
        : item.ObjectId + "|" + item.Path + "|" + item.Size.ToString(CultureInfo.InvariantCulture) + "|" +
          item.Mtime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "|" + (item.Hash ?? "-");

    // A path that cannot be observed fails closed: the decision is refused, never applied on a guess.
    private static async Task<ItemState?> ObserveAsync(IEndpointWriter writer, string path, CancellationToken ct)
    {
        try
        {
            return await writer.ObserveAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new SyncRunRefusedException("This path could not be read for the decision: " + error.Message);
        }
    }

    // Why a decided path could not be closed: the winner's side is gone, one side only, or the sides still differ.
    private string NotAppliedReason(string pairId, string path)
    {
        var pair = _store.Pair(pairId);
        if (pair is null) return "the pair is no longer available";
        var a = _store.Live(pair.A.RootId, [path]).FirstOrDefault();
        var b = _store.Live(pair.B.RootId, [path]).FirstOrDefault();
        if (a is null && b is null) return "the item is no longer present on either side";
        if (a is null || b is null) return "the item is present on only one side; nothing was copied";
        return "the two sides still differ";
    }
}
