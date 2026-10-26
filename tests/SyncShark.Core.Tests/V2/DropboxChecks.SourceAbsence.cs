namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task SourceAbsenceBatchAsync()
    {
        var clock = new ResilienceChecks.Clock();
        using var fixture = new Harness(limiter: ResilienceChecks.Limiter(clock));
        var writer = fixture.Writer();
        fixture.Wire.SeedFile("a.txt", "a");
        fixture.Wire.SeedFile("b.txt", "b");
        var a = (await writer.ObserveAsync("a.txt", CancellationToken.None))!;
        var b = (await writer.ObserveAsync("b.txt", CancellationToken.None))!;
        var missing = a with { ObjectId = "id:absent", Path = "missing.txt" };
        fixture.Wire.LivePageSize = 1;
        fixture.Wire.MetadataPaths.Clear();
        var first = await writer.IsAbsentBatchAsync([(a, a.Path), (b, b.Path), (missing, missing.Path)], CancellationToken.None);
        LocalChecks.Require(first.Select(x => x.Absent).SequenceEqual(new bool?[] { false, false, true }),
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
        // Decision table row 21: confirmed move-out leaves the pair; a replacement still blocks.
        LocalChecks.Require(second[0].Absent == true && second[1].Absent == false,
            "confirmed move-out is removed from sync while replacement at path prevents false absence");
        LocalChecks.Require(fixture.Wire.ListRequests.Count == 1, "later batches never rescan the same parent");

        fixture.Wire.SeedFolder("other-parent");
        await writer.MoveAsync(new SyncAction("move-in-root", 8, ActionKind.Move, Side.B,
            "other-parent/b.txt", b.Path, b, b, false, null), default);
        var movedWithin = await writer.IsAbsentBatchAsync([(b, b.Path)], default);
        LocalChecks.Require(movedWithin[0].Absent == false && fixture.Wire.MetadataPaths.Contains(b.ObjectId),
            "in-root move outside the listed parent remains present by identity metadata");
        foreach (var malformed in new[] { "missing", "relative" })
        {
            fixture.Wire.MutateMetadataResponse = (path, metadata) =>
            {
                if (path != b.ObjectId) return;
                if (malformed == "missing") metadata.Remove("path_lower");
                else metadata["path_lower"] = "relative/path";
            };
            var unknownLocation = await writer.IsAbsentBatchAsync([(b, b.Path)], default);
            LocalChecks.Require(unknownLocation[0].Absent is null,
                "missing or nonabsolute identity location never authorizes removal");
        }
        fixture.Wire.MutateMetadataResponse = null;

        fixture.Client.ReadRetryTime = clock;
        fixture.Client.ReadTransientBudget = TimeSpan.FromSeconds(3);
        fixture.Client.ReadRetryDelay = (delay, token) =>
        { token.ThrowIfCancellationRequested(); clock.Advance(delay); return Task.CompletedTask; };
        fixture.Wire.MetadataFailurePath = "id:absent";
        var absentSibling = missing with { ObjectId = "id:missing-sibling", Path = "absent-sibling.txt" };
        var isolated = await writer.IsAbsentBatchAsync([(missing, missing.Path),
            (absentSibling, absentSibling.Path)], CancellationToken.None);
        LocalChecks.Require(isolated[0].Absent is null && isolated[1].Absent == true &&
            isolated[0].Reason is not null,
            "one item's provider failure stays inconclusive without blocking its proven-absent sibling");
        fixture.Wire.MetadataFailurePath = null;

        // N1: a malformed path or an empty identity is inconclusive for its own item only, never
        // the whole page. The nine healthy members must still be evaluated normally.
        var healthy = Enumerable.Range(0, 9)
            .Select(i => missing with { ObjectId = "id:gone-" + i, Path = "gone-" + i + ".txt" }).ToArray();
        var malformedPath = missing with { ObjectId = "id:malformed-path", Path = "../escape.txt" };
        var isolatedPath = await writer.IsAbsentBatchAsync(
            healthy.Select(x => (x, x.Path)).Append((malformedPath, malformedPath.Path)).ToArray(), CancellationToken.None);
        LocalChecks.Require(isolatedPath.Take(9).All(x => x.Absent == true) && isolatedPath[9].Absent is null &&
            isolatedPath[9].Reason is not null, "a malformed path blocks only its own member of a ten-item batch");
        var emptyIdentity = missing with { ObjectId = "", Path = "empty-identity.txt" };
        var isolatedId = await writer.IsAbsentBatchAsync(
            healthy.Select(x => (x, x.Path)).Append((emptyIdentity, emptyIdentity.Path)).ToArray(), CancellationToken.None);
        LocalChecks.Require(isolatedId.Take(9).All(x => x.Absent == true) && isolatedId[9].Absent is null &&
            isolatedId[9].Reason is not null, "an empty identity blocks only its own member of a ten-item batch");
        fixture.Wire.Script("/2/files/list_folder", System.Net.HttpStatusCode.Forbidden, 1);
        var listingUnavailable = absentSibling with { Path = "fresh-parent/absent.txt" };
        LocalChecks.Require((await writer.IsAbsentBatchAsync([(listingUnavailable, listingUnavailable.Path)],
            CancellationToken.None))[0].Absent == true, "failed optional parent listing falls back to exact absence probes");
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
        using (var revoked = new Harness())
        {
            var revokedWriter = revoked.Writer();
            revoked.Wire.Script("/2/files/list_folder", System.Net.HttpStatusCode.Unauthorized, 1);
            var halted = false;
            try { await revokedWriter.IsAbsentBatchAsync([(missing, missing.Path)], CancellationToken.None); }
            catch (EndpointAccessException) { halted = true; }
            LocalChecks.Require(halted, "lost account authority escapes optional presence shortcut as one endpoint halt");
        }
        LocalChecks.Require(await writer.IsSourceAbsentAsync(null, "no-identity", default) is null,
            "path-only absence never supplies delete authority");
        var oversized = false;
        try { await writer.IsAbsentBatchAsync(Enumerable.Repeat((missing, missing.Path), 129).ToArray(), CancellationToken.None); }
        catch (ArgumentOutOfRangeException) { oversized = true; }
        LocalChecks.Require(oversized, "provider probe bounds retained requests independently of plan size");
    }
}
