namespace Massdex.SyncShark.V2;

internal sealed partial class LocalGapIo
{
    private readonly AsyncLocal<ProofPass?> _proofPass = new();
    internal Func<RootDef, IReadOnlyList<ItemState>,
        Func<PairDef, Side, ItemState, ItemEligibility.Purpose, string?>>? EligibilityPass { get; set; }

    public IDisposable BeginProofPass(RootDef root, IReadOnlyList<ItemState> items,
        PairDef? reviewedRemoval = null, string? reviewedFolder = null)
    {
        var previous = _proofPass.Value;
        if (reviewedRemoval is null && previous?.RootId == root.RootId)
        {
            reviewedRemoval = previous.RemovalPair;
            reviewedFolder = previous.RemovalFolder;
        }
        var pairs = known.Pairs();
        var product = LocalItems.ProductOwnedRelative(root.RootPath, owned).ToArray();
        var pass = new ProofPass(_proofPass, root.RootId, new(pairs, product),
            new(pairs, product, EligibilityPass?.Invoke(root, items) ?? EligibilityHeld), reviewedRemoval, reviewedFolder);
        _proofPass.Value = pass;
        return pass;
    }

    private sealed class ProofPass(AsyncLocal<ProofPass?> slot, string rootId,
        ItemEligibility selection, ItemEligibility proof, PairDef? reviewedRemoval, string? reviewedFolder) : IDisposable
    {
        private readonly ProofPass? _previous = slot.Value;
        internal string RootId => rootId;
        internal ItemEligibility Selection => selection;
        internal ItemEligibility Proof => proof;
        internal PairDef? RemovalPair => reviewedRemoval;
        internal string? RemovalFolder => reviewedFolder;
        internal PairDef? ReviewedRemoval(ItemState item) => reviewedFolder is { Length: > 0 } &&
            PairPath.IsUnder(item.Path, reviewedFolder) ? reviewedRemoval : null;
        internal Dictionary<string, (ItemState? Item, LocalGapHashResult? Failure)> Ancestors { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public void Dispose() => slot.Value = _previous;
    }
}
