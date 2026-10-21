namespace Massdex.SyncShark.V2;

internal static class ProviderNameNotices
{
    internal static IEnumerable<Notice> Read(PairDef pair, Func<RootDef, ItemState?, IReadOnlyList<ItemState>> pageOf)
    {
        foreach (var (root, side) in new[] { (pair.A, Side.A), (pair.B, Side.B) })
        {
            ItemState? after = null;
            while (true)
            {
                var page = pageOf(root, after);
                if (page.Count == 0 || page[^1] == after) break;
                foreach (var item in page)
                    yield return new Notice(pair.PairId, 31, NoticeKind.Info, item.Path,
                        ProviderNameRefusedException.Detail + " Suggested name: " + BlockerNames.Suggest(item.Path),
                        ProviderNameRefusedException.NoticeKey, Repair: new NoticeRepair(side, item));
                if (page.Count < 100) break;
                after = page[^1];
            }
        }
    }
}
