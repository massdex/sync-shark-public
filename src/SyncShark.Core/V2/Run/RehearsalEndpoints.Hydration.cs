namespace Massdex.SyncShark.V2;

internal sealed partial class RehearsalEndpoints
{
    internal static async Task<IReadOnlyDictionary<string, string>> HydrateAsync(IInventorySource inventory, DropboxInventory cloud,
        IReadOnlyList<string> prefixes, Func<IReadOnlyList<DropboxChange>, Task> accept, CancellationToken ct,
        Action<string>? listingBasis = null)
    {
        if (inventory is not Overlay overlay) return await cloud.HydratePrefixesAsync(prefixes, accept, ct, listingBasis);
        var failures = await cloud.HydratePrefixesAsync(prefixes,
            page => accept(page.Where(row => !overlay.Try(row.Path, out _)).ToArray()), ct, listingBasis);
        foreach (var prefix in prefixes.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(prefix => !prefixes.Any(parent => !PairPath.Same(prefix, parent) && PairPath.IsSelfOrUnder(prefix, parent))))
            foreach (var page in overlay.StoredItems(prefix).Chunk(256))
                await accept(page.Select(item => new DropboxChange(item.Path, DropboxItems.ToEntry(item))).ToArray());
        return failures;
    }
}
