namespace Massdex.SyncShark.V2;

/// <summary>
/// The UNKNOWN-history review and the standing "Held by your choice" notice (decision 83). Every deferred
/// deletion past its bound is either in an open review or Keep-held under this standing notice; none is held unseen.
/// </summary>
internal static class DefenderReview
{
    internal const string HeldCause = "held";
    internal const string ReviewChanged = "review-changed";
    internal const string SharedReleaseCause = "shared-release";

    internal static string SharedReleaseKey(string rootId, string objectId) => "defender-held-release:" + rootId + ":" + objectId;

    internal static DefenderSharedRelease SharedRelease(IStateStore store, string rootId, string pairId, ItemState item)
    {
        var bindings = store.Pairs().Where(pair => (pair.A.RootId == rootId || pair.B.RootId == rootId) &&
            !pair.Excluded.Any(prefix => PairPath.IsSelfOrUnder(item.Path, prefix)))
            .OrderBy(pair => pair.PairId, StringComparer.Ordinal).ToArray();
        return new(item.ObjectId, System.Text.Json.JsonSerializer.Serialize(bindings.Select(pair =>
            new { pair.PairId, pair.Name, Root = LocalRoot(pair, rootId) })),
            string.Join(", ", bindings.Where(pair => pair.PairId != pairId).Select(pair =>
                string.IsNullOrWhiteSpace(pair.Name) ? pair.PairId : pair.Name)));
    }

    /// <summary>A user's Keep held is rechecked against Defender history at most this often (or when the user acts).</summary>
    internal static readonly TimeSpan KeepRecheck = TimeSpan.FromDays(7);

    internal static string HeldBatch(string rootId) => "held:" + rootId;

    internal static string ReviewKey(string rootId, string batch) => "defender-history-review:" + rootId + ":" + batch;

    internal static string HeldKey(string rootId) => "defender-held:" + rootId;

    internal static RootDef LocalRoot(PairDef pair, string rootId) => pair.A.RootId == rootId ? pair.A : pair.B;

    internal static string HeldDetail(long count) =>
        $"Held by your choice: {count} deleted item(s) are kept and not deleted from Dropbox. " +
        "Release all, release one item, or stop syncing one item (exclude; both copies stay). New deletions get their own review.";

    /// <summary>
    /// Keeps one standing notice per binding while the root has Keep-held rows, and closes it when none remain.
    /// <paramref name="bindings"/> carries each bound pair with its already-loaded open notices.
    /// </summary>
    internal static void RefreshHeld(IStateStore store, string rootId,
        IReadOnlyList<(PairDef Pair, IReadOnlyList<Notice> Open)> bindings)
    {
        foreach (var (pair, open) in bindings)
        {
            var count = store.DefenderHeldCount(rootId, pair.PairId);
            var current = open.FirstOrDefault(n => n.DefenderHistory is { Cause: HeldCause } held && held.RootId == rootId);
            if (count == 0)
            {
                if (current is not null) store.CloseNotice(current.NoticeId!, "released");
                continue;
            }
            var notice = new Notice(pair.PairId, 30, NoticeKind.UserDecision, "", HeldDetail(count), HeldKey(rootId),
                DefenderHistory: new(rootId, HeldBatch(rootId), count, HeldCause, LocalRoot(pair, rootId)));
            if (current is null || current.Detail != notice.Detail || current.DefenderHistory != notice.DefenderHistory)
                store.AddNotices([notice]);
        }
    }

    internal static void RefreshHeld(IStateStore store, string rootId) =>
        RefreshHeld(store, rootId, store.Pairs().Where(p => p.A.RootId == rootId || p.B.RootId == rootId)
            .Select(p => (p, store.DefenderNotices(p.PairId, rootId))).ToArray());
}
