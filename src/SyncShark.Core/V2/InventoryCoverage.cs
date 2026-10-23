using System.Text.Json;

namespace Massdex.SyncShark.V2;

// Coverage is observation metadata, never a reviewed deletion choice or an effect checkpoint.
internal sealed record InventoryCoverage(RootDef Root, string[] Prefixes, string[] Ignores, bool Artifacts)
{
    internal static InventoryCoverage For(PairDef pair, Side side, IReadOnlyList<PairDef> pairs,
        IReadOnlyList<string> owned)
    {
        var root = side == Side.A ? pair.A : pair.B;
        var nested = pairs.Where(p => p.PairId != pair.PairId).SelectMany(p => new[] { p.A, p.B })
            .Select(candidate => PairAdmission.NestedPath(root, candidate)).OfType<string>();
        return new(root, Canonical(pair.Excluded.Concat(owned).Concat(nested)),
            pair.IgnoreRules.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            pair.A.Provider == ProviderIds.Local || pair.B.Provider == ProviderIds.Local);
    }

    private static string[] Canonical(IEnumerable<string> values) => values
        .Select(value => value.Replace('\\', '/').Trim('/')).Where(value => value.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    internal string Json => JsonSerializer.Serialize(this);
    internal bool CanReuse(InventoryCoverage previous) => Root == previous.Root && Artifacts == previous.Artifacts &&
        !previous.Ignores.Except(Ignores, StringComparer.Ordinal).Any();
    internal string[] Widened(InventoryCoverage previous)
    {
        var removed = previous.Prefixes.Where(path => !Prefixes.Any(prefix => PairPath.IsSelfOrUnder(path, prefix))).ToArray();
        return removed.Where(path => !removed.Any(parent => parent != path && PairPath.IsUnder(path, parent))).ToArray();
    }
}
