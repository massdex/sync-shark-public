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

    // R3-01/R3-02: the writer and date repair both reach the provider's one page producer, so a page size smaller
    // than their path count splits into verified pages instead of being refused.
    internal static async Task MetadataPagesFollowSettingsAsync()
    {
        using var h = new Harness();
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) h.Wire.SeedFile(name, name);
        var settings = V2Settings.Default with { Tunables = V2Tunables.Default with { MetadataPagePaths = 2 } };
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "metadata-pages-spool"),
            isStateDamage: StorageFailureClassifier.IsStateDamage, settings);
        var observed = await writer.ObservePageAsync(["a.txt", "b.txt", "c.txt"], CancellationToken.None);
        Require(observed.Count == 3 && observed.Values.All(item => item is not null), "the writer observes every path");
        Require(h.Wire.Calls.Count(call => call.EndsWith("/get_current_account")) == 2 &&
            h.Wire.Calls.Count(call => call.EndsWith("/get_metadata")) == 5,
            "three paths at page size two are two pages, each with its own account and root check");

        var conversions = 0;
        var pages = 0;
        h.Wire.RequestException = path =>
        {
            if (path.EndsWith("/get_current_account") && Interlocked.Increment(ref pages) == 2)
                Require(Volatile.Read(ref conversions) == 2, "the first page is converted before verifying the next page");
            return null;
        };
        var converted = await h.Client.ReadMetadataPagesAsync(["a.txt", "b.txt", "c.txt"], 2,
            entry => { Interlocked.Increment(ref conversions); return DropboxItems.ToItem(entry!); }, CancellationToken.None);
        h.Wire.RequestException = null;
        Require(conversions == 3 && pages == 2 && converted.Count == 3,
            "conversion retains only the result, not a raw cohort awaiting a later pass");

        // A failure on the second page is reported for that path and keeps the first page.
        h.Wire.MetadataFailurePath = DropboxWire.RootId + "/c.txt";
        var errors = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var rows = await h.Client.ReadMetadataPagesAsync(["a.txt", "b.txt", "c.txt"], settings.Tunables.MetadataPagePaths,
            entry => entry, CancellationToken.None, (path, error) => errors[path] = error.Message);
        Require(rows.ContainsKey("a.txt") && rows.ContainsKey("b.txt") && !rows.ContainsKey("c.txt") &&
            errors.Keys.SequenceEqual(["c.txt"]), "every page completes and only the failed path is reported");
    }
}
