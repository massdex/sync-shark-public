namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    // A move outside the root keeps the global identity alive and must NOT authorize propagation.
    // These fixtures exercise actual deletion, so remove the object through the stateful provider wire.
    private static async Task DeleteDeltaFixtureSourceAsync(Harness h, string path)
    {
        var writer = h.Writer();
        var original = await writer.ObserveAsync(path, CancellationToken.None);
        Require(original is not null, "delta fixture source exists before its deletion");
        await AuthorizedFixtureDeleteAsync(writer, new SyncAction("fixture-delete-" + path, 6, ActionKind.Delete,
            Side.B, path, null, null, original, false, null), CancellationToken.None);
    }

    private static async Task DeltaFolderRunsAsync()
    {
        using var h = new Harness();
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        local.Seed("empty", "", ItemKind.Folder);
        h.Wire.SeedFolder("empty");
        var pair = new PairDef("p1", "delta folders",
            new RootDef("ra", SyncProviders.Local, null, "C:\\offline-delta-folder", "id:ra"),
            Root(), [], [], true);
        h.Known.SavePair(pair);
        var settings = V2Settings.Default with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };
        var provider = new FakeEndpointProvider().Add(pair.A, local)
            .AddInventory(pair.B, h.Inventory).AddWriter(pair.B, h.Writer());
        var run = new SyncRun(h.Known, new Executor(h.Known, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage),
            provider, settings, TimeProvider.System);
        var runtime = new V2CliRuntime(null, h.Known, run, new EndpointRootProbe(provider),
            Path.Combine(_root, "delta-folder-prepare"));
        await runtime.PrepareFirstSyncAsync("p1", _ => { }, CancellationToken.None);
        var initial = await runtime.ApplyFirstSyncAsync("p1", null, true, null, _ => { }, CancellationToken.None);
        Require(initial.Failed == 0 && h.Known.FirstSyncDone("p1"), "folder fixture completes first sync");
        Require(!string.IsNullOrEmpty(h.Known.Cursor(RootId)),
            "successful prepared apply commits its cursor without an extra full incremental run");
        var before = h.Known.Cursor(RootId)!;
        await DeleteDeltaFixtureSourceAsync(h, "empty");
        h.Wire.SeedDeletedEntry("empty");
        h.Wire.RevisionErrorTag = "not_file";
        h.Wire.SeedChangePage(before, [h.Wire.ChangeDeleted("/Pilot/empty")], false, "folder-after");
        h.Wire.SeedChangePage("folder-after", [], false, "folder-next");
        var revisions = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
        var first = await run.IncrementalAsync("p1", new Progress<RunProgress>(), CancellationToken.None);
        Require(first.Failed == 0 && await local.ObserveAsync("empty", CancellationToken.None) is null &&
            h.Known.Cursor(RootId) == "folder-after", "incremental folder deletion propagates and advances cursor");
        var second = await run.IncrementalAsync("p1", new Progress<RunProgress>(), CancellationToken.None);
        Require(second.Failed == 0 && h.Known.Cursor(RootId) == "folder-next" &&
            h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == revisions,
            "second incremental run succeeds; folder deletion never uses revision history");
    }

    private static async Task DeltaPendingRunsAsync()
    {
        using var h = new Harness();
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        local.Seed("gone.txt", "same");
        local.Seed("finished-folder", "", ItemKind.Folder);
        h.Wire.SeedFile("gone.txt", "same");
        h.Wire.SeedFolder("finished-folder");
        var pair = new PairDef("p1", "delta pending",
            new RootDef("ra", SyncProviders.Local, null, "C:\\offline-delta-pending", "id:ra"),
            Root(), [], [], true);
        h.Known.SavePair(pair);
        var settings = V2Settings.Default with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };
        var provider = new FakeEndpointProvider().Add(pair.A, local)
            .AddInventory(pair.B, h.Inventory).AddWriter(pair.B, h.Writer());
        var run = new SyncRun(h.Known, new Executor(h.Known, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage),
            provider, settings, TimeProvider.System);
        var runtime = new V2CliRuntime(null, h.Known, run, new EndpointRootProbe(provider),
            Path.Combine(_root, "delta-pending-prepare"));
        await runtime.PrepareFirstSyncAsync("p1", _ => { }, CancellationToken.None);
        Require((await runtime.ApplyFirstSyncAsync("p1", null, true, null, _ => { }, CancellationToken.None)).Failed == 0,
            "file fixture completes first sync");
        Require(!string.IsNullOrEmpty(h.Known.Cursor(RootId)),
            "successful prepared apply commits its cursor without an extra full incremental run");
        var before = h.Known.Cursor(RootId)!;
        await DeleteDeltaFixtureSourceAsync(h, "gone.txt");
        h.Wire.SeedDeletedEntry("gone.txt");
        await DeleteDeltaFixtureSourceAsync(h, "finished-folder");
        h.Wire.SeedDeletedEntry("finished-folder");
        h.Wire.SeedChangePage(before,
            [h.Wire.ChangeDeleted("/Pilot/gone.txt"), h.Wire.ChangeDeleted("/Pilot/finished-folder")],
            false, "file-after");
        // A timed-out request (not caller cancellation) is an unresolved per-path probe, not scan failure.
        var delays = UseFastHistoryRetryClock(h);
        var probesBeforeTimeout = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
        h.Wire.OnRevisionAsync = _ => throw new TaskCanceledException("scripted request timeout");
        local.Seed("independent.txt", "keep moving");
        var first = await run.IncrementalAsync("p1", new Progress<RunProgress>(), CancellationToken.None);
        Require(first.Failed == 0 && first.DeletionEvidencePending.Contains("gone.txt") &&
            h.Known.Cursor(RootId) == "file-after" &&
            await local.ObserveAsync("gone.txt", CancellationToken.None) is not null &&
            await local.ObserveAsync("finished-folder", CancellationToken.None) is null &&
            h.Known.Live(RootId, ["gone.txt"]).Count == 0 && h.Known.SyncRetryAt("p1", "gone.txt") is not null &&
            await h.Writer().ObserveAsync("independent.txt", CancellationToken.None) is not null &&
            h.Known.OpenNotices("p1").Any(n => n.Path == "gone.txt" &&
                n.ReasonKey == PlanNotices.DeletionEvidencePending.Key),
            "failed lookup stays pending with a reason, retains both stored state and cursor, and does not fail run");
        Require(delays.Count > 0 &&
            h.Wire.Calls.Count(call => call == "/2/files/list_revisions") > probesBeforeTimeout + 1 &&
            h.Known.DeletionEvidenceRows("p1", RootId).Single(row => row.Path == "gone.txt").Failure is
                { Attempts: 1, Released: true },
            "timeout retries before exhaustion, then releases the cursor with one durable evidence hold");
        h.Wire.SeedChangePage("file-after", [], false, "file-after");
        var localWrites = local.WriteCalls;
        int CloudWrites() => h.Wire.Calls.Count(call => call.Contains("/upload", StringComparison.Ordinal) ||
            call.Contains("/delete", StringComparison.Ordinal) || call.Contains("/restore", StringComparison.Ordinal));
        var cloudWrites = CloudWrites();
        await run.IncrementalAsync("p1", new Progress<RunProgress>(), default);
        Require(local.WriteCalls == localWrites && CloudWrites() == cloudWrites &&
            await local.ObserveAsync("gone.txt", default) is not null && await h.Writer().ObserveAsync("gone.txt", default) is null,
            "ordinary follow-up neither deletes the held local survivor nor resurrects the cloud tombstone");
        // Cursor release after a timeout is not review release: N4/F1 in SyncRun.DeferredEvidence
        // counts only answered unavailable runs. Once reachable, this held item must be re-probed.
        Require(h.Known.SyncRetryAt("p1", "gone.txt") is
            { Kind: "deletion-evidence", ReprobeEvidence: true, EvidenceReleased: false, Attempts: 0 },
            "cursor release after an outage does not spend the answered-failure budget or release review");
        h.Wire.OnRevisionAsync = null;
        h.Known.SaveSyncRetry("p1", h.Known.SyncRetryAt("p1", "gone.txt")! with { RetryAfter = DateTimeOffset.MinValue });
        var probesBeforeRetry = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
        provider.AddInventory(pair.B, new DropboxInventory(Root(), Side.B, h.Client, h.Known, "p1"));
        Console.WriteLine("released evidence retry before: " + System.Text.Json.JsonSerializer.Serialize(h.Known.SyncRetryAt("p1", "gone.txt")));
        var second = await run.IncrementalAsync("p1", new Progress<RunProgress>(), CancellationToken.None);
        Console.WriteLine($"released evidence result: failed={second.Failed}; cursor={h.Known.Cursor(RootId)}; " +
            $"local-exists={await local.ObserveAsync("gone.txt", CancellationToken.None) is not null}; " +
            $"remote-exists={await h.Writer().ObserveAsync("gone.txt", CancellationToken.None) is not null}; " +
            $"probes-before={probesBeforeRetry}; probes-after={h.Wire.Calls.Count(call => call == "/2/files/list_revisions")}; " +
            $"local-writes-before={localWrites}; local-writes-after={local.WriteCalls}; " +
            $"cloud-writes-before={cloudWrites}; cloud-writes-after={CloudWrites()}");
        Console.WriteLine("released evidence retry after: " + System.Text.Json.JsonSerializer.Serialize(h.Known.SyncRetryAt("p1", "gone.txt")));
        Console.WriteLine("released evidence notices: " + System.Text.Json.JsonSerializer.Serialize(h.Known.OpenNotices("p1").Where(n => n.Path == "gone.txt")));
        Require(second.Failed == 0 && h.Known.Cursor(RootId) == "file-after" &&
            await local.ObserveAsync("gone.txt", CancellationToken.None) is not null &&
            h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == probesBeforeRetry + 1 &&
            h.Known.OpenNotices("p1").Any(n => n.Path == "gone.txt" && n.ReasonKey == "deletion-review") &&
            local.WriteCalls == localWrites && CloudWrites() == cloudWrites,
            "cursor-released evidence asks for a decision after exactly one reachable history probe; no delete or resurrection without explicit choice");
        var decisions = h.Known.OpenNotices("p1").Where(n => n.Path == "gone.txt" && n.ReasonKey == "deletion-review").ToArray();
        Console.WriteLine("released evidence decision options: " + System.Text.Json.JsonSerializer.Serialize(
            decisions.Select(notice => new { notice.NoticeId, Options = NoticeChoices.Options(notice) })));
        // NoticeChoices.Options adds explicit exclusion separately from Ignore for item retry notices.
        Require(decisions.Length == 1 && NoticeChoices.Options(decisions[0]).Select(option => option.Choice)
            .ToHashSet(StringComparer.Ordinal).SetEquals(
                [NoticeChoices.DeleteReviewed, NoticeChoices.KeepRestore, NoticeChoices.Ignore, NoticeChoices.Exclude]),
            "one deletion-review notice offers exactly Delete with Recovery, Keep/restore, Ignore, and Stop syncing");
        Require(h.Known.SyncRetryAt("p1", "gone.txt") is { Kind: "decision" } &&
            await h.Writer().ObserveAsync("gone.txt", default) is null,
            "successful re-probe leaves a decision hold without resurrecting the cloud tombstone");
        var probesBeforeDecisionFollowup = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
        await run.IncrementalAsync("p1", new Progress<RunProgress>(), default);
        Require(local.WriteCalls == localWrites && CloudWrites() == cloudWrites &&
            await local.ObserveAsync("gone.txt", default) is not null, "decision hold remains inert across later runs");
        Require(h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == probesBeforeDecisionFollowup &&
            await h.Writer().ObserveAsync("gone.txt", default) is null &&
            h.Known.OpenNotices("p1").Count(n => n.Path == "gone.txt" && n.ReasonKey == "deletion-review") == 1,
            "decision follow-up performs no further history probe or resurrection and does not duplicate the notice");
        var decision = h.Known.OpenNotices("p1").Single(n => n.Path == "gone.txt" && n.ReasonKey == "deletion-review");
        await run.ApplyRetryNoticeChoiceAsync("p1", decision.NoticeId!, NoticeChoices.DeleteReviewed, default);
        await run.IncrementalAsync("p1", new Progress<RunProgress>(), default);
        Require(await local.ObserveAsync("gone.txt", default) is null && CloudWrites() == cloudWrites,
            "only the explicit reviewed choice deletes the unchanged local survivor under OppositeAbsent");
    }
}
