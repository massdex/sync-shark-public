namespace Massdex.SyncShark.V2;

/// <summary>Pair-scoped eligibility, composed from the existing boundary and protection owners.</summary>
internal sealed class ItemEligibility(
    IReadOnlyList<PairDef> pairs,
    IReadOnlyList<string> productOwned,
    Func<PairDef, Side, ItemState, ItemEligibility.Purpose, string?>? held = null)
{
    internal enum Purpose { Plan, ContentProof, Settle, ReviewedRemoval }

    internal readonly record struct Result(string? Reason)
    {
        internal bool Eligible => Reason is null;
        // Retirement is scheduling-only, never evidence that content was verified.
        internal bool NotPairWork => Reason is "outside-pair" or "excluded" or "ignored" or "product-owned" or "link" or "stub";
    }

    internal Result Evaluate(PairDef pair, Side side, ItemState item, Purpose purpose)
    {
        if (!InPair(item.Path)) return new("outside-pair");
        if (CheckSelection(pair, side, item, carriedRemoval: purpose == Purpose.ReviewedRemoval) is { } selection) return new(selection);
        // Cached classification cannot retire observation or planning debt. Content proof
        // alone may decline an open; effect protection remains owned by the run guard.
        if (purpose is Purpose.ContentProof or Purpose.ReviewedRemoval && CheckClassification(item) is { } classification) return new(classification);
        return new(held?.Invoke(pair, side, item, purpose));
    }

    private static bool InPair(string path)
    {
        if (path.Length == 0) return true; // Existing whole-root scan sentinel.
        if (Path.IsPathRooted(path)) return false;
        try { return PathRules.NormalizeDropboxRelative(path) == path; }
        catch (ArgumentException) { return false; }
    }

    private string? CheckSelection(PairDef pair, Side side, ItemState item, bool descendants = false, bool carriedRemoval = false)
    {
        if (PairInclude.Excludes(pair, item.Path, descendants)) return "excluded";
        if (!carriedRemoval && ((item.Flags & ItemFlags.Ignored) != 0 ||
            PairInclude.NotSynced(pair, item.Path, item.Kind == ItemKind.Folder))) return "ignored";
        if (PairInclude.PermanentBoundary(pair, side, item.Path, descendants, [], productOwned) is not null)
            return "product-owned";
        return PairInclude.PermanentBoundary(pair, side, item.Path, descendants, pairs, []) is not null
            ? "outside-pair" : null;
    }

    // Explanation only: the effect guard remains the authority, including reviewed exceptions.
    internal Result ExplainEffect(PairDef pair, SyncAction action)
    {
        var target = action.ExpectedTarget ?? action.Source ??
            new ItemState(action.Path, action.Kind == ActionKind.MkDir ? ItemKind.Folder : ItemKind.File,
                string.Empty, null, null, 0, DateTimeOffset.MinValue, ItemFlags.None);
        var strict = action.Kind is ActionKind.Move or ActionKind.Delete;
        foreach (var path in new[] { action.Path, action.FromPath }.OfType<string>())
        {
            var item = target with { Path = path };
            if (!InPair(path)) return new("outside-pair");
            if (CheckSelection(pair, action.Target, item, strict) is { } reason) return new(reason);
            var result = Evaluate(pair, action.Target, item, Purpose.Plan);
            if (!result.Eligible) return result;
        }
        if (action.Source is { } source)
        {
            var result = Evaluate(pair, action.Target == Side.A ? Side.B : Side.A, source, Purpose.Plan);
            if (!result.Eligible) return result;
        }
        return new("protection"); // Unexplained direct blocks retain the existing safety notice.
    }

    internal Result EvaluateRoot(string rootId, ItemState item, Purpose purpose)
    {
        Result? rejected = null;
        var boundaries = new ItemEligibility(pairs, productOwned);
        foreach (var pair in pairs.Where(pair => pair.A.RootId == rootId || pair.B.RootId == rootId))
        {
            var side = pair.A.RootId == rootId ? Side.A : Side.B;
            var result = boundaries.Evaluate(pair, side == Side.A ? Side.B : Side.A, item, purpose);
            if (result.Eligible) result = Evaluate(pair, side, item, purpose);
            if (result.Eligible) return result;
            if (rejected is null || !result.NotPairWork) rejected = result;
        }
        // Isolated adapters may have no registered pair; the root still has artifact boundaries.
        return rejected ?? new Result(!InPair(item.Path) ? "outside-pair" :
            PairInclude.ProductBoundary(item.Path, false, productOwned) is not null ? "product-owned" :
            purpose is Purpose.ContentProof or Purpose.ReviewedRemoval ? CheckClassification(item) : null);
    }

    private static string? CheckClassification(ItemState item) =>
        (item.Flags & ItemFlags.Link) != 0 ? "link" :
        (item.Flags & ItemFlags.Placeholder) != 0 ? "stub" : null;
}
