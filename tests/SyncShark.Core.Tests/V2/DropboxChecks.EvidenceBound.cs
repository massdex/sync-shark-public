namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task DeltaEvidenceBoundAsync(string mode)
    {
        using var h = new Harness();
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        local.Seed("held.txt", "same");
        h.Wire.SeedFile("held.txt", "same");
        var pair = new PairDef("p1", "evidence bound",
            new RootDef("ra", SyncProviders.Local, null, "C:\\offline-evidence-bound", "id:ra"), Root(), [], [], true);
        h.Known.SavePair(pair);
        var settings = V2Settings.Default with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };
        var provider = new FakeEndpointProvider().Add(pair.A, local)
            .AddInventory(pair.B, h.Inventory).AddWriter(pair.B, h.Writer());
        var run = new SyncRun(h.Known, new Executor(h.Known, settings, ExecutorChecks.Admission()),
            provider, settings, TimeProvider.System);
        var preview = await run.PreviewFirstSyncAsync("p1", new Progress<RunProgress>(), CancellationToken.None);
        Require((await run.ApplyAsync("p1", preview, new Progress<RunProgress>(), CancellationToken.None)).Failed == 0,
            "bound fixture completes first sync");
        // Preview/apply does not commit a cursor; establish the full-run checkpoint before delta replay.
        var checkpoint = await run.IncrementalAsync("p1", new Progress<RunProgress>(), CancellationToken.None);
        Require(checkpoint.Failed == 0 && !string.IsNullOrEmpty(h.Known.Cursor(RootId)),
            "bounded-evidence fixture establishes a committed cursor before delta deletion");
        var before = h.Known.Cursor(RootId)!;
        h.Wire.MoveOutsideRoot("held.txt", "removed/held.txt");
        h.Wire.SeedDeletedEntry("held.txt");
        h.Wire.SeedChangePage(before, [h.Wire.ChangeDeleted("/Pilot/held.txt")], false, "bound-after");
        h.Wire.SeedChangePage("bound-after", [], false, "bound-next");
        var delays = UseFastHistoryRetryClock(h);
        h.Wire.OnRevisionAsync = _ =>
        {
            if (mode == "permanent") throw new InvalidDataException("malformed revision history");
            throw new TaskCanceledException("request timeout");
        };
        if (mode != "permanent")
        {
            var evidence = await h.Client.LookupDeletionEvidenceForV2Async(["held.txt"], CancellationToken.None);
            Require(evidence["held.txt"].LookupFailed && !evidence["held.txt"].PermanentFailure &&
                evidence["held.txt"].DeletedUtc is null,
                "exhausted timeout is transient unknown evidence before the inventory retry ladder");
        }
        if (mode == "age")
            h.Known.StoreDeletionEvidence("p1", RootId, [new DeletionEvidenceRecord("held.txt", null,
                Failure: new(1, DateTimeOffset.UtcNow.AddHours(-25), -1, false))]);
        var runs = mode == "attempts" ? 3 : 1;
        for (var attempt = 1; attempt <= runs; attempt++)
        {
            // Reopen the adapter each run: the failure budget must survive the instance's lifetime.
            provider.AddInventory(pair.B, new DropboxInventory(Root(), Side.B, h.Client, h.Known, "p1"));
            var delaysBefore = delays.Count;
            var probesBefore = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            var report = await run.IncrementalAsync("p1", new Progress<RunProgress>(), CancellationToken.None);
            if (mode != "permanent")
                Require(delays.Count > delaysBefore &&
                    h.Wire.Calls.Count(call => call == "/2/files/list_revisions") > probesBefore + 1,
                    "each transient history probe retries within its budget before the run-level ladder advances");
            Require(report.Failed == 0 && await local.ObserveAsync("held.txt", CancellationToken.None) is not null,
                "unreadable history never deletes the local path");
            Require(h.Known.Cursor(RootId) == (attempt < runs ? before : "bound-after"),
                "transient failure pins cursor only until third run; permanent or aged failure releases immediately");
        }
        Require(h.Known.OpenNotices("p1").Any(n => n.Path == "held.txt" &&
            n.ReasonKey == "deletion-history-unavailable" && n.Detail ==
            "Deletion history could not be read for held.txt; kept locally; options: delete locally / keep / retry"),
            "released history failure raises the durable options notice");
        var probes = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
        local.Seed("held.txt", "new local content must remain held");
        h.Wire.SeedChangePage("bound-after", [h.Wire.ChangeFile("held.txt", "remote restored content")], false, "bound-next");
        provider.AddInventory(pair.B, new DropboxInventory(Root(), Side.B, h.Client, h.Known, "p1"));
        await run.IncrementalAsync("p1", new Progress<RunProgress>(), CancellationToken.None);
        Require(h.Known.Cursor(RootId) == "bound-next" &&
            await local.ObserveAsync("held.txt", CancellationToken.None) is not null &&
            h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == probes &&
            h.Known.OpenNotices("p1").Any(n => n.ReasonKey == "deletion-history-unavailable"),
            "future runs advance without probing or deleting the held path, even after a local edit");
        Require(h.Known.Live(RootId, ["held.txt"]).Single().Size == System.Text.Encoding.UTF8.GetByteCount("remote restored content"),
            "released hold does not discard a later remote upsert while advancing its cursor");
        Require(!h.Known.DeletionEvidenceRows("p1", RootId).Any(row => row.Path == "held.txt" && row.Failure is not null),
            "remote recreation resets the old deletion's failure budget");
        h.Wire.OnRevisionAsync = null;
        h.Wire.SeedChangePage("bound-next", [h.Wire.ChangeDeleted("/Pilot/held.txt")], false, "bound-deleted-again");
        provider.AddInventory(pair.B, new DropboxInventory(Root(), Side.B, h.Client, h.Known, "p1"));
        await run.IncrementalAsync("p1", new Progress<RunProgress>(), CancellationToken.None);
        Require(h.Known.Cursor(RootId) == "bound-deleted-again" && h.Known.Live(RootId, ["held.txt"]).Count == 0 &&
            await local.ObserveAsync("held.txt", CancellationToken.None) is not null,
            "a new remote deletion is ingested after recreation while the local notice hold remains");
        var notice = h.Known.OpenNotices("p1").Single(n => n.ReasonKey == "deletion-history-unavailable");
        h.Known.CloseNotice(notice.NoticeId!, "keep");
        h.Wire.SeedChangePage("bound-next", [], false, "bound-last");
        await h.Inventory.ChangesSinceAsync("bound-next", 999, _ => true, CancellationToken.None);
        Require(!h.Known.OpenNotices("p1").Any(n => n.ReasonKey == "deletion-history-unavailable"),
            "a dismissed released notice is not reopened by an unrelated delta");
    }
}
