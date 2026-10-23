namespace Massdex.SyncShark.V2;

// Decision 64: the CLI's view of the open needs-decision paths (ties and needs-decision rows) and the one verb
// that applies the user's chosen side. Both read and resolve observe the endpoints fresh under the run gate, so a
// real change on disk is seen even though the path is left out of the scan while its notice is open.
public sealed partial class V2CliRuntime
{
    /// <summary>The open needs-decision notices of one pair, each with both sides' freshly observed state and a token.</summary>
    public async Task<SyncCliDecisionListData> LoadDecisionsAsync(string pairId, int limit, int offset,
        CancellationToken cancellationToken)
    {
        var pair = RequirePair(pairId);
        var open = _store.OpenNotices(pairId).Where(notice => notice.Kind == NoticeKind.UserDecision)
            .OrderBy(notice => notice.Path, StringComparer.Ordinal)
            .ThenBy(notice => notice.NoticeId, StringComparer.Ordinal).ToArray();
        var page = open.Skip(offset).Take(limit).ToArray();
        IReadOnlyList<DecisionSnapshot> snapshots = page.Length == 0
            ? []
            : await _run.ObserveDecisionsAsync(pairId, page.Select(notice => (notice.NoticeId!, notice.Path)).ToArray(),
                cancellationToken);
        var byId = snapshots.ToDictionary(snapshot => snapshot.NoticeId, StringComparer.Ordinal);
        var items = page.Select(notice => Project(pair, notice, byId[notice.NoticeId!])).ToArray();
        return new SyncCliDecisionListData(pairId, items, limit, offset, items.Length,
            offset + items.Length < open.Length, open.Length);
    }

    /// <summary>
    /// Applies the chosen side on the decision pinned by <paramref name="token"/> and reports the effects done and
    /// whether the notice closed. The token is checked against a fresh observation under the run gate, so a path
    /// that changed since the user looked is refused before any effect.
    /// </summary>
    public async Task<SyncCliDecisionResolveData> ResolveDecisionAsync(string pairId, string relativePath,
        string keep, string token, Action<string> progress, CancellationToken cancellationToken)
    {
        var pair = RequirePair(pairId);
        var winner = ResolveWinner(pair, keep);
        return await _run.WithPairAdmissionAsync(pairId, async () =>
        {
            try
            {
                var report = await _run.ResolveDecisionAsync(pairId, relativePath, token, winner,
                    new RunProgressForwarder(progress), cancellationToken);
                var noticeId = SyncRun.DecisionNoticeId(token);
                var closed = noticeId is null || _store.UnresolvedNotices(pairId).All(open => open.NoticeId != noticeId);
                var reason = report.DecisionFailureReason;
                return new SyncCliDecisionResolveData(pairId, relativePath, FirstSyncChoices.SideName(pair, winner),
                    report.Done, report.Failed, report.Held, closed, closed ? null : reason ?? SyncRun.DecisionNotApplied);
            }
            catch (SyncRunRefusedException error)
            {
                throw new SyncCliReviewException(error.Message);
            }
            catch (EndpointAccessException error)
            {
                throw new SyncCliReviewException(ReconnectText(error));
            }
        }, cancellationToken, progress);
    }

    private static SyncCliDecisionItemData Project(PairDef pair, Notice notice, DecisionSnapshot snapshot) =>
        new(pair.PairId, notice.Path, notice.Rule, notice.ReasonKey, notice.Detail,
            FirstSyncChoices.SideName(pair, Side.A), FirstSyncChoices.SideName(pair, Side.B),
            snapshot.A?.Size, snapshot.B?.Size, snapshot.A?.Mtime, snapshot.B?.Mtime, snapshot.Token,
            IsFolder(snapshot));

    // A folder on either side: shown in the list but refused by decisions resolve (Astra round 3).
    private static bool IsFolder(DecisionSnapshot snapshot) =>
        snapshot.A?.Kind == ItemKind.Folder || snapshot.B?.Kind == ItemKind.Folder;

    // --keep accepts the side letter or the side's user-facing name (the same short label the plan shows).
    private static Side ResolveWinner(PairDef pair, string keep)
    {
        var value = keep.Trim();
        if (value.Equals("a", StringComparison.OrdinalIgnoreCase)) return Side.A;
        if (value.Equals("b", StringComparison.OrdinalIgnoreCase)) return Side.B;
        if (value.Equals(FirstSyncChoices.SideName(pair, Side.A), StringComparison.OrdinalIgnoreCase)) return Side.A;
        if (value.Equals(FirstSyncChoices.SideName(pair, Side.B), StringComparison.OrdinalIgnoreCase)) return Side.B;
        throw new SyncCliUsageException("--keep requires a, b, " + FirstSyncChoices.SideName(pair, Side.A) + " or " +
            FirstSyncChoices.SideName(pair, Side.B) + ".");
    }
}
