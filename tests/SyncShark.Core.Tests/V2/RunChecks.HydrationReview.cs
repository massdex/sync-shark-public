namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task HydrationReviewChecksAsync()
    {
        HydrationIdentityChecks();
        var folder = Path.Combine(Path.GetTempPath(), "hydration-review-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var item = fx.A.Seed("available.txt", "content") with { Flags = ItemFlags.Placeholder };
        MappingSnapshot(store, fx.Pair.A, Side.A, [item]);
        store.AddNotices([new(fx.Pair.PairId, LinkExclusions.StubRule, NoticeKind.Info, item.Path, "online only", "online-only-item", Repair: new(Side.A, item))]);
        var notice = store.OpenNotices(fx.Pair.PairId).Single();
        var downloads = 0;
        var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System)
        {
            HydratePlaceholderForTest = (_, _, _) => { downloads++; return Task.CompletedTask; },
            AfterHydrationForTest = () => throw new IOException("post-download identity verification failed")
        };
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.MakeAvailableOffline, default);
        var debt = store.ByObject(fx.Pair.A.RootId, item.ObjectId)!;
        Require(!debt.Flags.HasFlag(ItemFlags.Placeholder) && debt.Flags.HasFlag(ItemFlags.NeedsRehash) &&
            store.RehashWatermark(fx.Pair.A.RootId) > 0 && store.SyncRetryAt(fx.Pair.PairId, item.Path) is not null,
            "successful download with failed verification persists honest hydration state and durable verification debt");
        Require(store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == "online-only-item").Detail.StartsWith("Hydrated but unverified", StringComparison.Ordinal),
            "post-hydration failure leaves one actionable notice");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.RetryNow, default);
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.MakeAvailableOffline, default);
        Require(downloads == 1, "retry verifies hydration debt rather than downloading again");
        Require(store.CompleteRehash(fx.Pair.A.RootId, debt, item.Hash!), "fresh exact identity and hash clear hydration verification debt");
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default);
        Require(await fx.B.ObserveAsync(item.Path, default) is not null, "verified hydrated file eventually syncs");

        var large = fx.A.Seed("large.txt", "large") with { Flags = ItemFlags.Placeholder, Size = OnlineOnlyHydration.ConfirmationBytes + 1 };
        MappingSnapshot(store, fx.Pair.A, Side.A, [large]);
        store.AddNotices([new(fx.Pair.PairId, LinkExclusions.StubRule, NoticeKind.Info, large.Path, "online only", "online-only-item", Repair: new(Side.A, large))]);
        var largeNotice = store.OpenNotices(fx.Pair.PairId).Single(n => n.Path == large.Path);
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, largeNotice.NoticeId!, NoticeChoices.MakeAvailableOffline, default);
        Require(downloads == 1 && NoticeChoices.Options(store.NoticeById(fx.Pair.PairId, largeNotice.NoticeId!)!.Value.Notice)
            .Any(option => option.Choice == NoticeChoices.ConfirmLargeDownload), "large files offer explicit confirmation without refusing");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, largeNotice.NoticeId!, NoticeChoices.ConfirmLargeDownload, default);
        Require(downloads == 2, "confirmed large file reaches streaming hydration");

        store.AddNotices([new(fx.Pair.PairId, LinkExclusions.StubRule, NoticeKind.Info, "", "online-only summary", "stubs-left-alone")]);
        var summary = store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == "stubs-left-alone");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, summary.NoticeId!, NoticeChoices.RetryNow, default);
        Require(store.NoticeById(fx.Pair.PairId, summary.NoticeId!) is { Closed: true }, "summary auto-closes when no placeholder remains");
        store.AddNotices([summary]);
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, summary.NoticeId!, NoticeChoices.Ignore, default);
        store.AddNotices([summary]);
        Require(store.NoticeById(fx.Pair.PairId, summary.NoticeId!) is { Closed: true } && store.Pair(fx.Pair.PairId)!.Excluded.Count == 0,
            "summary Ignore is sticky and never excludes root");

        var refusedItem = fx.A.Seed("refused.txt", "bytes") with { Flags = ItemFlags.Placeholder };
        MappingSnapshot(store, fx.Pair.A, Side.A, [refusedItem]);
        store.AddNotices([new(fx.Pair.PairId, LinkExclusions.StubRule, NoticeKind.Info, refusedItem.Path, "online only", "online-only-item", Repair: new(Side.A, refusedItem))]);
        var refusedNotice = store.OpenNotices(fx.Pair.PairId).Single(n => n.Path == refusedItem.Path);
        var refused = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System)
        { HydratePlaceholderForTest = (_, _, _) => throw new SyncRunRefusedException("refresh required") };
        await refused.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, refusedNotice.NoticeId!, NoticeChoices.MakeAvailableOffline, default);
        Require(!store.NoticeById(fx.Pair.PairId, refusedNotice.NoticeId!)!.Value.Notice.Detail.StartsWith("Making", StringComparison.Ordinal),
            "refusal never leaves stale in-progress text");
    }

    private static void HydrationIdentityChecks()
    {
        var time = DateTimeOffset.FromFileTime(0);
        var item = new ItemState("empty.txt", ItemKind.File, SyncLocalEndpointStore.ObjectId(1, 2), null, null, 0, time, ItemFlags.Placeholder);
        OnlineOnlyHydration.Validate(item);
        Require(OnlineOnlyHydration.HydrationLength(item) == -1, "empty placeholders use CF_EOF, not a zero-length native request");
        OnlineOnlyHydration.Validate(item with { Size = OnlineOnlyHydration.ConfirmationBytes + 1 });
        var info = new PilotDirectoryLease.ByHandleFileInformation { VolumeSerialNumber = 1, FileIndexLow = 2, NumberOfLinks = 1 };
        OnlineOnlyHydration.RequireIdentity(info, item);
        var wrongId = info; wrongId.FileIndexLow++;
        var wrongVolume = info; wrongVolume.VolumeSerialNumber++;
        var linked = info; linked.NumberOfLinks = 2;
        var sized = info; sized.FileSizeLow = 1;
        var timed = info; timed.LastWriteTime.dwLowDateTime = 1;
        var directory = info; directory.FileAttributes = FileAttributes.Directory;
        foreach (var invalid in new[] { wrongId, wrongVolume, linked, sized, timed, directory })
        {
            try { OnlineOnlyHydration.RequireIdentity(invalid, item); throw new InvalidOperationException("Changed identity accepted"); }
            catch (IOException) { }
        }
        var expected = item with { ObjectId = "id:file", VersionId = "rev", Hash = "hash", Size = 12 };
        var result = new SyncDownloadResult(expected.ObjectId, "rev", 12, "hash");
        DropboxWriter.RequireDownloadIdentity(expected, result);
        foreach (var invalid in new[] { result with { FileId = "id:other" }, result with { Revision = "other" }, result with { Size = 11 }, result with { ContentHash = "other" } })
        {
            try { DropboxWriter.RequireDownloadIdentity(expected, invalid); throw new InvalidOperationException("Changed download accepted"); }
            catch (ExpectedTargetMismatchException) { }
        }
    }

    private static async Task DeletionIgnoreReincludeChecksAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ignore-evidence-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("held.txt", "surviving content");
        fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B) { ProviderOutage = true });
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default, ReviewedHeld(plan));
        // The fixture completes the first sync explicitly so the later re-inclusion can use the ordinary run path;
        // the apply left the item's deletion evidence pending, which is the state under test.
        fx.Store.MarkFirstSyncDone(fx.Pair.PairId);
        var notice = fx.Store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == "deletion-evidence-pending");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.Ignore, default);
        var excluded = fx.Store.Pair(fx.Pair.PairId)!;
        fx.Store.SavePairIncluded(excluded, excluded with { Excluded = [] });
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 && await fx.A.ObserveAsync("held.txt", default) is not null,
            "Ignore then reinclude never grants deletion authority without fresh evidence");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "deletion-evidence-pending" && n.Path == "held.txt"),
            "removing the exclusion lets fresh evidence raise the deletion-evidence notice again");
        Require(fx.WriterB.Puts == 0 && await fx.B.ObserveAsync("held.txt", default) is null &&
            fx.Store.SyncRetryAt(fx.Pair.PairId, "held.txt") is { Held: not null, ReprobeEvidence: true },
            "unresolved re-included evidence remains durable and never uploads");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == "deletion-evidence-pending" && n.Path == "held.txt") == 1,
            "backed-off held evidence keeps one pending notice");
        fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B));
        var retry = fx.Store.SyncRetryAt(fx.Pair.PairId, "held.txt")!;
        fx.Store.SaveSyncRetry(fx.Pair.PairId, retry with { RetryAfter = DateTimeOffset.MinValue });
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.Path == "held.txt" && n.Kind == NoticeKind.UserDecision) &&
            fx.WriterB.Puts == 0 && fx.WriterA.Deletes == 0,
            "resolved survivor requires a decision instead of silently restoring");
        Require(!fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.Path == "held.txt" && n.ReasonKey == "deletion-evidence-pending"),
            "resolved evidence closes the pending notice");
        var current = fx.Store.Pair(fx.Pair.PairId)!;
        fx.Store.SavePair(current with { Excluded = ["folder"] });
        fx.A.Seed("folder/new.txt", "ordinary new content");
        var folderExcluded = fx.Store.Pair(fx.Pair.PairId)!;
        fx.Store.SavePairIncluded(folderExcluded, folderExcluded with { Excluded = [] });
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "folder/new.txt") is null,
            "re-inclusion does not invent evidence holds for ordinary new files");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(await fx.B.ObserveAsync("folder/new.txt", default) is not null,
            "ordinary new file beneath a re-included folder uploads normally");
    }
    internal static async Task ReincludedEvidenceChecksAsync()
    {
        await DeletionIgnoreReincludeChecksAsync();
        await RestoredHoldReviewChecksAsync();
        LegacyRepairNoticeChecks();
    }

    private static async Task RestoredHoldReviewChecksAsync()
    {
        foreach (var scenario in new[] { "folder", "partial-first-sync", "released", "recreated", "cli" })
        {
            using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "restored-hold-" + Guid.NewGuid().ToString("N")), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            var item = fx.A.Seed("held", "content", scenario == "folder" ? ItemKind.Folder : ItemKind.File);
            store.SaveSyncRetry(fx.Pair.PairId, new(item.Path, "post-preview", 0, "original failure", DateTimeOffset.MinValue,
                Held: new HeldDeletion(item.Path, Side.A)));
            store.AddNotices([new(fx.Pair.PairId, 4, NoticeKind.Info, item.Path, "history pending", "deletion-evidence-pending")]);
            var run = fx.Run();
            var notice = store.OpenNotices(fx.Pair.PairId).Single();
            if (scenario == "cli")
            {
                var runtime = new V2CliRuntime(null, store, run, new EndpointRootProbe(fx.Provider), Path.GetTempPath());
                await runtime.SetSelectionAsync(fx.Pair.PairId, item.Path, "exclude", default);
                store.MarkFirstSyncDone(fx.Pair.PairId);
                await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            }
            else await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.Exclude, default);
            Require(store.SyncRetryAt(fx.Pair.PairId, item.Path) is { Held: not null, RetryAfter: var due } && due == DateTimeOffset.MaxValue,
                "Exclude atomically keeps dormant hold provenance");
            var excluded = store.Pair(fx.Pair.PairId)!;
            store.SavePairIncluded(excluded, excluded with { Excluded = [] });
            if (scenario == "recreated") fx.B.Seed(item.Path, "remote content");
            fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B) { LookupFailure = scenario is "released" or "partial-first-sync" });
            if (scenario == "partial-first-sync")
            {
                var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
                Require(!plan.Actions.Any(action => action.Path == item.Path && action.Kind != ActionKind.Hold),
                    "SQLite re-inclusion during partial first sync proposes no upload or deletion");
                await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default, ReviewedHeld(plan));
                Require(fx.WriterB.Puts == 0 && fx.WriterA.Deletes == 0 && store.SyncRetryAt(fx.Pair.PairId, item.Path) is { ReprobeEvidence: true },
                    "partial first-sync apply preserves restored hold and makes no effect");
                continue;
            }
            store.MarkFirstSyncDone(fx.Pair.PairId);
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            if (scenario == "released")
            {
                var retry = store.SyncRetryAt(fx.Pair.PairId, item.Path)!;
                Require(retry.EvidenceReleased && retry.LastError.Contains("probed Dropbox deletion", StringComparison.Ordinal),
                    "failed fresh probe exhausts its budget and preserves its reason");
                var pending = store.OpenNotices(fx.Pair.PairId).Where(n => n.Path == item.Path).ToArray();
                Require(pending.Length == 1 && pending[0].ReasonKey == "deletion-evidence-pending" && pending[0].Detail.Contains(retry.LastError, StringComparison.Ordinal),
                    "restored pending hold has exactly one notice with its failure reason");
                store.SaveSyncRetry(fx.Pair.PairId, retry with { RetryAfter = DateTimeOffset.MinValue });
                await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            }
            if (scenario == "recreated")
                Require(store.SyncRetryAt(fx.Pair.PairId, item.Path)?.Held is null &&
                    !store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "deletion-evidence-pending"),
                    "recreated remote retires old hold instead of granting deletion");
            else
            {
                var decision = store.OpenNotices(fx.Pair.PairId).Single(n => n.Path == item.Path && n.Kind == NoticeKind.UserDecision);
                Require(fx.WriterB.Puts == 0 && fx.WriterA.Deletes == 0,
                    "folder and exhausted evidence both require a decision without effects");
                if (scenario == "folder") Require(NoticeChoices.Options(decision).Select(option => option.Choice).SequenceEqual(
                    new[] { NoticeChoices.KeepRestore, NoticeChoices.Ignore }), "restored folder offers restore or ignore, never delete");
            }
        }
        using var paged = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "restored-pages-" + Guid.NewGuid().ToString("N")), Settings, TimeProvider.System);
        var many = Build(suppliedStore: paged);
        var pair = many.Pair with { B = many.Pair.B with { Provider = SyncProviders.Local }, Excluded = ["folder"] };
        paged.SavePair(pair);
        for (var index = 0; index < 300; index++)
            paged.SuspendHeldRetry(pair.PairId, new("folder/" + index.ToString("D4"), "post-preview", 0, "", DateTimeOffset.MinValue,
                Held: new HeldDeletion("folder/" + index.ToString("D4"), Side.B, "retained-rule")));
        paged.SavePairIncluded(pair, pair with { Excluded = [] });
        Require(paged.ReadSyncRetries(pair.PairId).Count(retry => retry.RetryAfter == DateTimeOffset.MinValue &&
            retry.Held is { Local: Side.B, Rule: "retained-rule" }) == 300,
            "indexed restore crosses 256 rows and retains stored side/rule even on non-Dropbox pairs");
        Console.WriteLine("PASS RestoredHoldReviewChecks");
    }

}
