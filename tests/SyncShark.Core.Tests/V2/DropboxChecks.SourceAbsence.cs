namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task SourceAbsenceBatchAsync()
    {
        using var fixture = new Harness();
        var writer = fixture.Writer();
        fixture.Wire.SeedFile("a.txt", "a");
        fixture.Wire.SeedFile("b.txt", "b");
        var a = (await writer.ObserveAsync("a.txt", CancellationToken.None))!;
        var b = (await writer.ObserveAsync("b.txt", CancellationToken.None))!;
        var missing = a with { ObjectId = "id:absent", Path = "missing.txt" };
        fixture.Wire.LivePageSize = 1;
        fixture.Wire.MetadataPaths.Clear();
        var first = await writer.IsAbsentBatchAsync([(a, a.Path), (b, b.Path), (missing, missing.Path)], CancellationToken.None);
        LocalChecks.Require(first.SequenceEqual(new bool?[] { false, false, true }),
            "paged parent presence and exact identity absence preserve request order");
        LocalChecks.Require(fixture.Wire.ListRequests.Count == 1 && !fixture.Wire.ListRequests[0].Recursive &&
            fixture.Wire.ContinueCursors.Count == 1, "one nonrecursive parent listing follows all its pages");
        LocalChecks.Require(!fixture.Wire.MetadataPaths.Contains(a.ObjectId) && !fixture.Wire.MetadataPaths.Contains(b.ObjectId) &&
            fixture.Wire.MetadataPaths.Contains(missing.ObjectId), "listing is only a fast-present shortcut, absent ID is checked globally");
        LocalChecks.Require(fixture.Wire.MetadataPaths.Count(x => x == DropboxWire.RootId) == 1,
            "batch verifies root once, not once per identity");

        fixture.Wire.MoveOutsideRoot("a.txt", "Elsewhere/a.txt");
        fixture.Wire.SeedFile("missing.txt", "replacement");
        var second = await writer.IsAbsentBatchAsync([(a, a.Path), (missing, missing.Path)], CancellationToken.None);
        LocalChecks.Require(second.All(x => x == false), "relocated original and replacement at path both prevent false absence");
        LocalChecks.Require(fixture.Wire.ListRequests.Count == 1, "later batches never rescan the same parent");

        var clock = new ResilienceChecks.Clock();
        fixture.Client.ReadRetryTime = clock;
        fixture.Client.ReadTransientBudget = TimeSpan.FromSeconds(3);
        fixture.Client.ReadRetryDelay = (delay, token) =>
        { token.ThrowIfCancellationRequested(); clock.Advance(delay); return Task.CompletedTask; };
        fixture.Wire.MetadataFailurePath = "id:absent";
        var failed = false;
        try { await writer.IsAbsentBatchAsync([(missing, missing.Path)], CancellationToken.None); }
        catch (HttpRequestException) { failed = true; }
        LocalChecks.Require(failed, "provider failure propagates for retry, never becomes absence");
        fixture.Wire.MetadataFailurePath = null;
        fixture.Wire.Script("/2/files/get_metadata", System.Net.HttpStatusCode.TooManyRequests, 64);
        var throttled = false;
        try { await writer.IsAbsentBatchAsync([(missing, missing.Path)], CancellationToken.None); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        { throttled = true; }
        LocalChecks.Require(throttled, "exhausted throttling preserves 429 for retry-later, never a false/absent result");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledSafely = false;
        try { await writer.IsAbsentBatchAsync([(missing, missing.Path)], cancelled.Token); }
        catch (OperationCanceledException) { cancelledSafely = true; }
        LocalChecks.Require(cancelledSafely, "cancelled probe never authorizes deletion");
        var oversized = false;
        try { await writer.IsAbsentBatchAsync(Enumerable.Repeat((missing, missing.Path), 129).ToArray(), CancellationToken.None); }
        catch (ArgumentOutOfRangeException) { oversized = true; }
        LocalChecks.Require(oversized, "provider probe bounds retained requests independently of plan size");
    }
}
