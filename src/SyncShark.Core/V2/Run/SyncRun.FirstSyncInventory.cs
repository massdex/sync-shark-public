using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // Bind a pending checkpoint to exactly the coverage used to populate the retained inventory. Include
    // nested roots and product-owned paths: widening either must enumerate unchanged, newly included items.
    // This is metadata about observation coverage, not pair-owned file state (decisions 75/77).
    internal string InventoryScope(PairDef pair)
    {
        var coverage = JsonSerializer.Serialize(new[] { Coverage(pair, Side.A), Coverage(pair, Side.B) });
        return "v2:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(coverage)));
    }

    private InventoryCoverage Coverage(PairDef pair, Side side) =>
        InventoryCoverage.For(pair, side, _store.Pairs(), _productOwned(pair));

    internal string CompatibleInventoryScope(PairDef pair, string? saved) =>
        saved == LegacyInventoryScope(pair) ? saved : InventoryScope(pair);

    internal void RetainInventoryObservation(PairDef pair, IReadOnlyDictionary<string, string>? cursors)
    {
        if (_store is not SqliteStateStore sql || cursors is null) return;
        foreach (var side in new[] { Side.A, Side.B })
        {
            var root = side == Side.A ? pair.A : pair.B;
            if (cursors.TryGetValue(root.RootId, out var cursor) &&
                _store.ReadDeletionClassification(root)?.Cursor == cursor)
                sql.SaveInventoryObservation(Coverage(pair, side), cursor);
        }
    }

    private string LegacyInventoryScope(PairDef pair)
    {
        var coverage = JsonSerializer.Serialize(new
        {
            Format = 1,
            pair.A,
            pair.B,
            pair.Excluded,
            pair.IgnoreRules,
            Roots = _store.Pairs().OrderBy(p => p.PairId, StringComparer.Ordinal)
                .Select(p => new { p.PairId, p.A, p.B }).ToArray(),
            Owned = _productOwned(pair),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(coverage)));
    }

    private static IReadOnlyDictionary<string, string> PreparedInventoryCursors(PairDef pair, BothScan scan,
        IReadOnlyDictionary<string, string>? previous)
    {
        var cursors = new Dictionary<string, string>(StringComparer.Ordinal);
        Add(pair.A, scan.A);
        Add(pair.B, scan.B);
        return cursors;

        void Add(RootDef root, SideScan side)
        {
            if (!HasInventoryCursor(root) || !side.Complete) return;
            // An unresolved deletion was deliberately not installed in file_state. Replay it next time rather
            // than skipping it by advancing past the evidence request. A full scan has no such pending delta.
            var cursor = side.PendingDeletionEvidence.Keys.All(side.ReleasedDeletionEvidence.Contains)
                ? side.CursorAfter : previous?.GetValueOrDefault(root.RootId);
            if (!string.IsNullOrEmpty(cursor)) cursors[root.RootId] = cursor;
        }
    }

    // Local NTFS checkpoints have the same retained-inventory coverage contract as Dropbox cursors.
    // Unsupported local filesystems return no checkpoint and continue to use the full-scan fallback.
    private static bool HasInventoryCursor(RootDef root) =>
        string.Equals(root.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(root.Provider, SyncProviders.Dropbox, StringComparison.OrdinalIgnoreCase);

    private void PromoteFirstSyncInventory(PairDef pair, Plan approved)
    {
        if (!string.Equals(approved.InventoryScope, InventoryScope(pair), StringComparison.Ordinal)) return;
        foreach (var root in new[] { pair.A, pair.B })
        {
            if (!HasInventoryCursor(root) ||
                !approved.InventoryCursors.TryGetValue(root.RootId, out var cursor)) continue;
            // Preview's generations are already closed. This checkpoint records no new inventory rows;
            // promote only after all executable effects succeeded, before exclusion changes invalidate it.
            var gen = _store.BeginGeneration(root.RootId, "first-sync-checkpoint");
            _store.CompleteGeneration(gen, cursor, true);
        }
    }
}
