namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task DeltaEvidenceBoundAsync(string mode)
    {
        using var h = new Harness();
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        // The local copy predates the Dropbox deletion. Rule 3 keeps the unreviewed evidence pending;
        // even a delete proposal is never an automatic upload or delete (a)).
        local.Seed("held.txt", "same", mtime: DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        h.Wire.SeedFile("held.txt", "same", clientModified: DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
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
        // Owner rule 3: unavailable deletion history is never deletion authority. A failed probe releases
        // the cursor to exactly one durable item-owned sync_retry hold; the local survivor stays held
        // until the record is explicitly decided (delete / keep-restore / ignore) or retired by recreation.
        var localWritesBeforeHold = local.WriteCalls;
        int CloudWrites() => h.Wire.Calls.Count(call => call.Contains("/delete", StringComparison.Ordinal) ||
            call.Contains("/upload", StringComparison.Ordinal) || call.Contains("/restore", StringComparison.Ordinal));
        var cloudWritesBeforeHold = CloudWrites();
        provider.AddInventory(pair.B, new DropboxInventory(Root(), Side.B, h.Client, h.Known, "p1"));
        var delaysBefore = delays.Count;
        var probesBefore = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
        var first = await run.IncrementalAsync("p1", new Progress<RunProgress>(), CancellationToken.None);
        if (mode != "permanent")
            Require(delays.Count > delaysBefore &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") > probesBefore + 1,
                "each transient history probe retries within its budget before the single durable hold");
        Require(first.Failed == 0 && await local.ObserveAsync("held.txt", CancellationToken.None) is not null,
            "unreadable history never deletes the local path");
        Require(h.Known.Cursor(RootId) == "bound-after",
            "unreadable history releases the cursor immediately; the evidence hold, not the cursor, protects the path");
        var held = h.Known.SyncRetryAt("p1", "held.txt");
        Require(held is { Kind: "deletion-evidence", Held: not null } &&
            h.Known.ReadSyncRetries("p1").Count() == 1,
            "failed history leaves exactly one durable sync_retry hold, with no duplicate evidence row or notice");
        Require(!h.Known.OpenNotices("p1").Any(n => n.ReasonKey is "deletion-history-unavailable" or "deletion-review"),
            "failed history is a hold, not an automatic deletion or a stale lifecycle notice");
        Require(local.WriteCalls == localWritesBeforeHold && CloudWrites() == cloudWritesBeforeHold &&
            !h.Wire.AttemptedWritePaths.Contains("held.txt"),
            "no delete or upload is authorized before an explicit evidence choice");

        // Make the single hold due. It must surface a choice even if history never becomes readable.
        // Also simulate a successful background probe replacing the failure cache before decision.
        h.Known.SaveSyncRetry("p1", held! with { RetryAfter = DateTimeOffset.MinValue });
        if (mode == "attempts")
            h.Known.StoreDeletionEvidence("p1", RootId,
                [new DeletionEvidenceRecord("held.txt", DateTimeOffset.Parse("2026-10-12T00:00:00Z"))]);
        if (mode == "attempts") h.Wire.OnRevisionAsync = null; // Successful evidence must retire on recreation too.
        else h.Wire.OnRevisionAsync = _ => throw new InvalidDataException("malformed revision history");
        provider.AddInventory(pair.B, new DropboxInventory(Root(), Side.B, h.Client, h.Known, "p1"));
        var decisionRun = await run.IncrementalAsync("p1", new Progress<RunProgress>(), CancellationToken.None);
        Require(decisionRun.Failed == 0 && await local.ObserveAsync("held.txt", CancellationToken.None) is not null &&
            local.WriteCalls == localWritesBeforeHold && CloudWrites() == cloudWritesBeforeHold,
            "released history evidence forbids every delete and upload until the user chooses");
        var decision = h.Known.OpenNotices("p1").Single(n => n.Path == "held.txt" && n.ReasonKey == "deletion-review");
        Require(NoticeChoices.Options(decision).Select(option => option.Choice).SequenceEqual(
            new[] { NoticeChoices.DeleteReviewed, NoticeChoices.KeepRestore, NoticeChoices.Ignore }),
            "unreviewed history evidence offers exactly delete / keep-restore / ignore");
        var proposed = h.Known.SyncRetryAt("p1", "held.txt");
        Require(proposed is { Kind: "decision", ProposedActions: { Count: > 0 }, ReviewedActions: null, Held: not null } &&
            h.Known.ReadSyncRetries("p1").Count() == 1,
            "the proposed action is stored separately from reviewed authority in the same single retry row");
        Require(h.Known.Cursor(RootId) == "bound-next",
            "the safe cursor advances after the decision notice; the path remains protected");

        // A remote recreation retires the obsolete evidence and its decision, then ordinary sync
        // ingests the current remote content. It is not a stale deletion.
        h.Wire.SeedFile("held.txt", "remote restored content", clientModified: DateTimeOffset.Parse("2026-10-13T00:00:00Z"));
        h.Wire.SeedChangePage("bound-next", [h.Wire.ChangeExistingFile("held.txt")], false, "bound-restored");
        provider.AddInventory(pair.B, new DropboxInventory(Root(), Side.B, h.Client, h.Known, "p1"));
        var restored = await run.IncrementalAsync("p1", new Progress<RunProgress>(), CancellationToken.None);
        Require(restored.Failed == 0 && h.Known.Cursor(RootId) == "bound-restored" &&
            h.Known.SyncRetryAt("p1", "held.txt") is null && h.Known.ReadSyncRetries("p1").Count() == 0 &&
            !h.Known.OpenNotices("p1").Any(n => n.Path == "held.txt" &&
                n.ReasonKey is "deletion-review" or "deletion-history-unavailable"),
            "remote recreation retires the evidence hold and its decision without a stale deletion");
        Require((await local.ObserveAsync("held.txt", CancellationToken.None))?.Hash ==
                (await h.Writer().ObserveAsync("held.txt", CancellationToken.None))?.Hash &&
            await local.ObserveAsync("held.txt", CancellationToken.None) is not null &&
            h.Known.Live(RootId, ["held.txt"]).Single().Size ==
                System.Text.Encoding.UTF8.GetByteCount("remote restored content"),
            "ordinary sync ingests the recreated remote file; the local survivor is not deleted");
        Require(CloudWrites() == cloudWritesBeforeHold,
            "recreation and ordinary sync perform no cloud delete");

        // A later deletion starts a fresh hold rather than replaying the retired one.
        h.Wire.OnRevisionAsync = _ => throw new TaskCanceledException("request timeout");
        h.Wire.MoveOutsideRoot("held.txt", "removed-again/held.txt");
        h.Wire.SeedChangePage("bound-restored", [h.Wire.ChangeDeleted("/Pilot/held.txt")], false, "bound-deleted-again");
        provider.AddInventory(pair.B, new DropboxInventory(Root(), Side.B, h.Client, h.Known, "p1"));
        await run.IncrementalAsync("p1", new Progress<RunProgress>(), CancellationToken.None);
        Require(h.Known.Cursor(RootId) == "bound-deleted-again" && h.Known.Live(RootId, ["held.txt"]).Count == 0 &&
            await local.ObserveAsync("held.txt", CancellationToken.None) is not null &&
            h.Known.SyncRetryAt("p1", "held.txt") is { Kind: "deletion-evidence", Held: not null } &&
            h.Known.ReadSyncRetries("p1").Count() == 1,
            "a new remote deletion starts one fresh evidence hold; the local survivor is still never deleted");
    }
}
