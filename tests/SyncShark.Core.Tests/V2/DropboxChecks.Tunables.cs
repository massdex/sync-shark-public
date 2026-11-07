namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    // The bounded exact-metadata page is the caller's settings value, not a constant: a page size of two splits
    // five paths into three reads.
    internal static async Task ExactMetadataPageFollowsSettingsAsync()
    {
        using var h = new Harness();
        var pages = new List<int>();
        var inventory = new DropboxInventory(Root(), Side.B, h.Client, h.Known, "p1")
        {
            ExactMetadataForTest = (paths, _) =>
            {
                pages.Add(paths.Count);
                return Task.FromResult<IReadOnlyDictionary<string, PilotRemoteEntry?>>(
                    paths.ToDictionary(path => path, _ => (PilotRemoteEntry?)null));
            }
        };
        await inventory.ReadExactAsync(["a.txt", "b.txt", "c.txt", "d.txt", "e.txt"], 2, "cursor", 1, _ => true, default);
        Require(pages.SequenceEqual([2, 2, 1]), "the exact-metadata page size comes from the settings");
    }
}
