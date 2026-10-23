namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private const int ConcurrentCollisionPairs = 32;

    // Review finding 1: a full walk lists top-level folders in parallel. Each listing must own its
    // collision state and the serial merge must union it; a shared HashSet would be written by two
    // listings at once. Both child subtrees collide here, at the same moment.
    private static async Task IntakeListingConcurrencyAsync()
    {
        using var h = new Harness();
        h.Wire.SeedFolder("left");
        h.Wire.SeedFolder("right");
        foreach (var folder in new[] { "left", "right" })
            for (var index = 0; index < ConcurrentCollisionPairs; index++)
            {
                h.Wire.SeedListingRaw(RawFileEntry($"{folder}/Clash-{index:D2}.txt", $"id:{folder}-a-{index}"));
                h.Wire.SeedListingRaw(RawFileEntry($"{folder}/clash-{index:D2}.txt", $"id:{folder}-b-{index}"));
            }
        var arrivals = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Wire.BeforeListingAsync = async (_, recursive) =>
        {
            if (!recursive) return;
            if (Interlocked.Increment(ref arrivals) == 2) both.TrySetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };

        var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
        Require(h.Wire.MaxConcurrentListings >= 2,
            "the fixture overlaps both child subtree listings");
        Require(snapshot.Items.Select(item => item.Path).OrderBy(path => path, StringComparer.Ordinal)
                .SequenceEqual(["left", "right"]),
            "no collided entry from either concurrent listing is published");
        Require(snapshot.IncompleteSubtrees.Count == 2 * ConcurrentCollisionPairs,
            "every collision from both concurrent listings is scoped exactly once");
        foreach (var folder in new[] { "left", "right" })
            for (var index = 0; index < ConcurrentCollisionPairs; index++)
                Require(snapshot.IncompleteSubtrees.Any(path =>
                        PairPath.Same(path, $"{folder}/clash-{index:D2}.txt")) &&
                    snapshot.IncompleteReasons.Values.All(reason =>
                        reason.Contains("two different items", StringComparison.Ordinal)),
                    $"{folder}/clash-{index:D2}.txt keeps its exact collision reason");
        Console.WriteLine(
            $"PASS concurrent subtree listing collisions: subtrees=2; pairs={ConcurrentCollisionPairs}");
    }
}
