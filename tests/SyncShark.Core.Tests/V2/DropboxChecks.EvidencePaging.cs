using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task EvidenceHistoryPagingAsync()
    {
        using var h = new Harness();
        using var sqlite = SqliteStateStore.Open(Path.Combine(_root, "evidence-paging"), StorageChecks.Fast, TimeProvider.System);
        var pair = new PairDef("p1", "Pair A", new RootDef("ra", SyncProviders.Local, null,
            "C:\\offline-evidence", "id:ra"), Root(), [], [], true);
        sqlite.SavePair(pair);
        const int count = 10_241;
        foreach (var batch in Enumerable.Range(0, count).Chunk(256))
        {
            sqlite.StoreDeletionEvidence(pair.PairId, RootId, batch.Select(i =>
                new DeletionEvidenceRecord($"failed-{i:D5}", null, Failure: new(3, DateTimeOffset.UnixEpoch, 1, true))).ToArray());
            sqlite.StoreDeletionEvidence(pair.PairId, RootId, batch.Select(i =>
                new DeletionEvidenceRecord($"cached-{i:D5}", DateTimeOffset.UnixEpoch, "start", new string('a', 64))).ToArray());
        }
        // Complete listing classification includes every exact tombstone in the ancestor set too.
        // Without it SQLite persists no tombstone (measured failure: tombstone-matched=0).
        sqlite.ReplaceDeletionClassification(Root(), "start", new HashSet<string> { "cached-00000" },
            new HashSet<string> { "cached-00000" });
        Require(sqlite.ReadDeletionClassification(Root())!.Exact.Contains("cached-00000"),
            "the cache-reuse fixture persists its exact deletion candidate");
        h.Wire.SeedChangePage("start", [], false, "next");
        var observed = DispatchProxy.Create<IStateStore, EvidenceReadProxy>();
        var proxy = (EvidenceReadProxy)(object)observed;
        proxy.Store = sqlite;
        var inventory = new DropboxInventory(Root(), Side.B, h.Client, observed, pair.PairId);
        var delta = await inventory.ChangesSinceAsync("start", 2, _ => true, CancellationToken.None, firstSync: true);
        Require(!delta.CursorInvalid && delta.CursorAfter == "next", "paged history retains the refreshed cursor");
        Require(proxy.FullReads == 0 && proxy.MaxPage <= 256 && proxy.PageReads == 3 * ((count + 255) / 256 + 1),
            "real SQLite reads bounded pages for cache validation, legacy upgrade and first-sync repair, never full history");
        Require(sqlite.SyncRetryAt(pair.PairId, "failed-00000") is null,
            "released history without a stored local survivor never invents a hold");
        var pageReads = proxy.PageReads;
        var prepared = await inventory.PrepareDeletionEvidenceAsync(["cached-00000"], CancellationToken.None);
        Require(prepared.Resolved["cached-00000"].DeletedUtc == DateTimeOffset.UnixEpoch &&
            proxy.PageReads == pageReads && proxy.MaxSelected <= 1 && proxy.FullReads == 0,
            "a later probe selects its own cache row without rebuilding the history cache");
        var selected = observed.DeletionEvidenceRows(pair.PairId, RootId, ["CACHED-00000", "cached-10240", "missing"]);
        Require(selected.Count == 2 && selected.All(row => row.BasisCursor == "next") && proxy.FullReads == 0,
            "indexed selection is case-insensitive and returns only requested validated evidence");
        // A deleted page must not move the continuation past its next sibling page.
        string? after = null;
        var removed = 0;
        var pages = 0;
        while (true)
        {
            var page = observed.DeletionEvidencePage(pair.PairId, RootId, after, 256, failuresOnly: false);
            pages++;
            if (page.Count == 0) break;
            after = page[^1].Path;
            removed += page.Count;
            sqlite.RemoveDeletionEvidence(pair.PairId, RootId, page.Select(row => row.Path).ToArray());
        }
        Require(removed == count && pages == (count + 255) / 256 + 1 && proxy.FullReads == 0,
            "keyset continuation visits every success exactly once while earlier pages are deleted");
    }

    public class EvidenceReadProxy : DispatchProxy
    {
        public IStateStore Store { get; set; } = null!;
        public int FullReads { get; private set; }
        public int PageReads { get; private set; }
        public int MaxPage { get; private set; }
        public int MaxSelected { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod ?? throw new InvalidOperationException("Missing store method.");
            if (method.Name == nameof(IStateStore.DeletionEvidenceRows) && args?.Length == 2)
            {
                FullReads++;
                throw new InvalidOperationException("Inventory must not materialize deletion evidence history.");
            }
            if (method.Name == nameof(IStateStore.DeletionEvidenceRows) && args?.Length == 3)
                MaxSelected = Math.Max(MaxSelected, ((IReadOnlyList<string>)args[2]!).Count);
            var result = method.Invoke(Store, args);
            if (method.Name == nameof(IStateStore.DeletionEvidencePage))
            {
                PageReads++;
                MaxPage = Math.Max(MaxPage, ((IReadOnlyList<DeletionEvidenceRecord>)result!).Count);
            }
            return result;
        }
    }
}
