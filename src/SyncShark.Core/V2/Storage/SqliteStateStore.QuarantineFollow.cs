namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private int NoticeMaintenancePageRows => Math.Max(V2Settings.MinimumStoreNoticePageRows, _settings.StoreNoticePageRows);

    // Same-identity inventory moves carry the hold; path-bound grants never move.
    private void FollowQuarantineNotices(string rootId)
    {
        foreach (var pair in Pairs().Where(pair => pair.A.RootId == rootId || pair.B.RootId == rootId))
        {
            var side = pair.A.RootId == rootId ? Side.A : Side.B;
            var through = InStore(() => PairRecord(pair.PairId) is null ? string.Empty : _notices.QuarantineHorizon(RequirePair(pair.PairId)));
            var after = string.Empty;
            while (string.CompareOrdinal(after, through) < 0)
            {
                var next = InStore(() => FollowQuarantinePage(rootId, pair.PairId, side, after, through));
                if (next is null) break;
                after = next;
            }
        }
    }

    private string? FollowQuarantinePage(string rootId, string pairId, Side side, string after, string through)
    {
        var pair = PairRecord(pairId);
        var root = RootNo(rootId);
        if (pair is null || root is null || (side == Side.A ? pair.A : pair.B).RootId != rootId) return null;
        var pairNo = RequirePair(pairId);
        var page = _notices.QuarantinePage(pairNo, pairId, after, through, NoticeMaintenancePageRows);
        if (page.Count == 0) return null;
        using var transaction = _connection.BeginTransaction();
        foreach (var notice in page)
        {
            if (notice.Quarantine is not { ConflictingGrantId: null, GrantedExclusionPath: null } hold) continue;
            var expected = hold.Target == side ? hold.ExpectedTarget : hold.Source;
            if (expected is null || _reads.ByObject(root.Value, expected.ObjectId, transaction) is not { } current ||
                !current.Flags.HasFlag(ItemFlags.Quarantined) || current.Path == expected.Path) continue;
            // Preserve reviewed content evidence; a rename is not permission to accept new bytes.
            var moved = expected with { Path = current.Path };
            var updated = hold.Target == side ? hold with { ExpectedTarget = moved } : hold with { Source = moved };
            _notices.Add(transaction, pairNo, notice with { NoticeId = null, Path = current.Path, Quarantine = updated }, NowMs());
            if (!PairPath.Same(notice.Path, current.Path))
                _notices.CloseResolved(transaction, pairNo, notice.NoticeId!, "quarantine-renamed");
        }
        transaction.Commit();
        return page[^1].NoticeId;
    }
}
