namespace Massdex.SyncShark.V2;

internal static class IncludePreviewChecks
{
    public static async Task LocalScopeAsync(string fixtureRoot)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Directory.Exists(fixtureRoot)) throw new IOException("Use a fresh include-preview fixture.");
        Directory.CreateDirectory(Path.Combine(fixtureRoot, "wanted"));
        Directory.CreateDirectory(Path.Combine(fixtureRoot, "sibling"));
        for (var i = 0; i < 8; i++)
            await File.WriteAllTextAsync(Path.Combine(fixtureRoot, "wanted", i + ".txt"), "untouched");
        await File.WriteAllTextAsync(Path.Combine(fixtureRoot, "sibling", "outside.txt"), "outside");
        using var lease = PilotDirectoryLease.Open(fixtureRoot);
        var scanner = new PilotLocalScanner { MetadataOnly = true, CloudFilesAware = true };
        var limited = await scanner.ScanAsync(lease, refreshPaths: ["wanted"], maxEntries: 3);
        if (limited.Complete || limited.Entries.Count != 3 || limited.FilesHashed != 0 ||
            limited.Entries.Values.Any(e => e.RelativePath != "wanted" && !e.RelativePath.StartsWith("wanted/")))
            throw new InvalidOperationException("Local preview must cap during collection, never hash or scan siblings.");
        var complete = await scanner.ScanAsync(lease, refreshPaths: ["wanted"], maxEntries: 20);
        if (!complete.Complete || complete.Entries.Count != 9)
            throw new InvalidOperationException("Local preview below the cap must report complete.");
        var missing = await scanner.ScanAsync(lease, refreshPaths: ["missing"], maxEntries: 20);
        if (!missing.Complete || missing.Entries.Count != 0)
            throw new InvalidOperationException("Missing exact scope must not scan the root.");
    }

    public static Task RunAsync()
    {
        static ProductionEndpoints.PreviewEntry File(string path, long size, string? hash = null) =>
            new(path, PilotEntryKind.File, size, hash, false);
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Include preview: " + message); }

        var time = DateTimeOffset.UtcNow.AddDays(-2);
        var a = new ProductionEndpoints.PreviewSide([
            File("scope/local", 1), File("scope/different", 2) with { ModifiedUtc = time.AddSeconds(5) }, File("scope/same", 3, "hash"),
            File("scope/unknown", 4)], true);
        var b = new ProductionEndpoints.PreviewSide([
            File("scope/remote", 1), File("scope/different", 3) with { ModifiedUtc = time }, File("scope/same", 3, "hash"),
            File("scope/unknown", 4)], true);
        var preview = ProductionEndpoints.SummarizeIncludePreview("pair", "scope", a, b);
        Check(preview.Complete && preview.PairId == "pair" && preview.RelativePath == "scope", "scope retained");
        Check(preview.Summary.CopyAToB == 1 && preview.Summary.CopyBToA == 1, "one-sided copies");
        Check(preview.Summary.NewestWins == 1 && preview.Summary.KeepBoth == 0 && preview.Summary.IdenticalFiles == 1,
            "newest-wins reinclude, identical adopt");
        Check(preview.Examples.Any(e => e.RelativePath == "scope/different" && e.Recommendation == "newest-a-to-b"),
            "newer A wins");
        var reverse = ProductionEndpoints.SummarizeIncludePreview("pair", "scope", b, a);
        Check(reverse.Summary.NewestWins == 1 && reverse.Examples.Any(e => e.Recommendation == "newest-b-to-a"),
            "newer B wins");
        foreach (var seconds in new[] { 0, 1, 2 })
        {
            var tied = ProductionEndpoints.SummarizeIncludePreview("pair", "scope",
                new([File("scope/tie", 1, "a") with { ModifiedUtc = time }], true),
                new([File("scope/tie", 1, "b") with { ModifiedUtc = time.AddSeconds(seconds) }], true));
            Check(tied.Summary.HeldTies == 1 && tied.Summary.NewestWins == 0 && tied.Summary.HeldUnknown == 0,
                "known differences at or within the tie window stay held ties");
        }
        foreach (var unknownTime in new DateTimeOffset?[] { null, DateTimeOffset.MinValue,
                     DateTimeOffset.MaxValue, DateTimeOffset.UnixEpoch, DateTimeOffset.FromFileTime(0),
                     DateTimeOffset.UtcNow.AddDays(1) })
        {
            var unknown = ProductionEndpoints.SummarizeIncludePreview("pair", "scope",
                new([File("scope/no-time", 1, "a") with { ModifiedUtc = unknownTime }], true),
                new([File("scope/no-time", 1, "b") with { ModifiedUtc = time }], true));
            Check(unknown.Summary.HeldUnknown == 1 && unknown.Summary.NewestWins == 0 && unknown.Summary.HeldTies == 0,
                "missing, sentinel and future-plus-one-day times are unknown, not fabricated winners or ties");
        }
        var unknownBytes = ProductionEndpoints.SummarizeIncludePreview("pair", "scope",
            new([File("scope/bytes", 1) with { ModifiedUtc = time }], true),
            new([File("scope/bytes", 1) with { ModifiedUtc = time.AddDays(1) }], true));
        Check(unknownBytes.Summary.HeldUnknown == 1 && unknownBytes.Summary.NewestWins == 0 &&
            unknownBytes.Examples.Single().Recommendation == "content-check-needed",
            "different timestamps alone do not prove a content clash");
        Check(preview.Summary.HeldUnknown == 1 && preview.Summary.Deletes == 0, "unknown bytes are not identical or deletion authority");
        var incomplete = ProductionEndpoints.SummarizeIncludePreview("pair", "scope", a, b with { Complete = false });
        Check(!incomplete.Complete && incomplete.Summary.CopyAToB == 0 && incomplete.Summary.CopyBToA == 0,
            "incomplete inventory makes no absence claims on either side");
        Check(incomplete.Summary.NewestWins == 1 && incomplete.Summary.KeepBoth == 0 && incomplete.Summary.HeldUnknown == 3,
            "incomplete scan still reports observed both-sided facts");
        var many = new ProductionEndpoints.PreviewSide(Enumerable.Range(0, 100)
            .Select(i => File("scope/" + i, 1)).ToArray(), true);
        var bounded = ProductionEndpoints.SummarizeIncludePreview("pair", "scope", many, new([], true));
        Check(bounded.Summary.CopyAToB == 100 && bounded.Examples.Count == ProductionEndpoints.IncludePreviewExampleLimit,
            "examples bounded independently of counts");
        var linked = new ProductionEndpoints.PreviewSide([
            new("scope/link", PilotEntryKind.Directory, 0, null, true)], true);
        var remoteChildren = new ProductionEndpoints.PreviewSide([File("scope/link/child", 1)], true);
        var protectedTree = ProductionEndpoints.SummarizeIncludePreview("pair", "scope", linked, remoteChildren);
        Check(protectedTree.Summary.CopyBToA == 0 && protectedTree.Summary.HeldUnknown == 2,
            "held folder protects opposite-side descendants");
        var collision = new ProductionEndpoints.PreviewSide([File("scope/Case", 1), File("scope/case", 2)], true);
        var colliding = ProductionEndpoints.SummarizeIncludePreview("pair", "scope", collision, new([], true));
        Check(colliding.Summary.CopyAToB == 0 && colliding.Summary.HeldUnknown == 1,
            "canonical collisions remain unknown instead of throwing or copying");
        var ignored = ProductionEndpoints.SummarizeIncludePreview("pair", "scope",
            new([File("scope/ignored.tmp", 1)], true), new([], true),
            [new SyncIgnoreRule(SyncIgnoreKind.FileExtension, ".tmp")]);
        Check(ignored.Summary.CopyAToB == 0 && ignored.Summary.HeldUnknown == 1,
            "ignore matcher protects ordinary include preview");
        return Task.CompletedTask;
    }
}
