namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // A bounded proof pass shares the existing protection composition. Only its own file
    // identity retries are exempt; decisions, pending effects and recovery remain protected.
    internal Func<PairDef, Side, ItemState, ItemEligibility.Purpose, string?> ProofEligibilityHolds(
        RootDef root, IReadOnlyList<ItemState> items)
    {
        var paths = items.Where(item => item.Kind == ItemKind.File).Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var holds = _store.Pairs().Where(pair => pair.A.RootId == root.RootId || pair.B.RootId == root.RootId)
            .ToDictionary(pair => pair.PairId, pair => Protection(pair,
                purpose: ItemEligibility.Purpose.ContentProof, proofRoot: root.RootId, proofPaths: paths)
                .ForPaths(paths), StringComparer.Ordinal);
        return (pair, _, item, _) => holds.TryGetValue(pair.PairId, out var protection)
            ? ProtectionHold(protection, item.Path, item.Kind == ItemKind.Folder) : null;
    }

    // One mapping from protection coverage to the eligibility hold reason.
    private static string? ProtectionHold(LinkExclusions protection, string path, bool descendants) =>
        protection.Covers(path) || descendants && protection.Contains(path) ? "held:protection" : null;
}
