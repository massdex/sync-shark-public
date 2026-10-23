namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task HeldRetryNoticeOverlapAsync()
    {
        foreach (var reason in new[] { PlanNotices.ContentUnconfirmed.Key, FreeSpaceRefusal.InsufficientReasonKey, "name-mapping-collision" })
        foreach (var kind in new[] { "deletion-evidence", "decision" })
        {
            using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "held-notice-overlap-" + Guid.NewGuid().ToString("N")), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("held.txt", "original");
            var remote = fx.B.Seed("held.txt", "original");
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            await fx.B.DeleteAsync(new SyncAction("remote-delete", 0, ActionKind.Delete, Side.B, "held.txt", null, null, remote, false, null), default);
            store.SaveSyncRetry(fx.Pair.PairId, new SyncRetry("held.txt", kind, 1, "history unavailable",
                kind == "decision" ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddHours(1),
                Held: new HeldDeletion("held.txt", Side.A)));
            store.AddNotices([new Notice(fx.Pair.PairId, reason == PlanNotices.ContentUnconfirmed.Key ? 30 : 40,
                NoticeKind.Info, "held.txt", "retryable prior failure", reason)]);
            var writesA = fx.A.WriteCalls;
            var writesB = fx.B.WriteCalls;
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(await fx.A.ObserveAsync("held.txt", default) is not null &&
                fx.A.WriteCalls == writesA && fx.B.WriteCalls == writesB &&
                store.SyncRetryAt(fx.Pair.PairId, "held.txt") is { Held: not null },
                "not-due held/decision retry plus " + reason + " never authorizes propagated local deletion or upload: " + kind);
        }
    }

    private static async Task RemovalCorruptionPropagatesAsync()
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "removal-corrupt-" + Guid.NewGuid().ToString("N")), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("folder", "", ItemKind.Folder);
        fx.A.SubtreeTransform = _ => throw new StateDamagedException("simulated storage corruption");
        store.MarkFirstSyncDone(fx.Pair.PairId);
        store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.UserDecision, "folder", "grown", "reviewed-folder-grew", RemovalSides: [Side.A])]);
        await ExpectAsync<StateDamagedException>(() => fx.Run().ApplyRetryNoticeChoiceAsync(fx.Pair.PairId,
            store.OpenNotices(fx.Pair.PairId).Single().NoticeId!, NoticeChoices.ReviewRemoval, default));
        Require(fx.WriterA.Deletes == 0, "storage corruption remains a stop, not an ordinary listing failure");
    }

    private static async Task RemovalListingFailureAsync()
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "removal-list-failure-" + Guid.NewGuid().ToString("N")), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        IInventorySource unsupported = new ScriptedInventory(fx.A);
        await ExpectAsync<NotSupportedException>(() => unsupported.ReadSubtreeAsync("folder", _ => Task.CompletedTask, default));
        Require(fx.A.FullCalls == 0, "unsupported subtree provider never silently scans the root");
        fx.Provider.AddInventory(fx.Pair.A, unsupported);
        store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.UserDecision, "folder", "grown", "reviewed-folder-grew", RemovalSides: [Side.A])]);
        var run = fx.Run();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var notice = store.OpenNotices(fx.Pair.PairId).Single();
            await ExpectAsync<SyncRunRefusedException>(() => run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.ReviewRemoval, default));
            var remaining = store.OpenNotices(fx.Pair.PairId).Single();
            Require(remaining.Detail.Contains("Retry review, open settings or dismiss") && remaining.Detail.Contains("Subtree listing not implemented"),
                "listing failure keeps one deduplicated actionable notice");
        }
        Require(fx.WriterA.Deletes == 0, "listing failure never authorizes removal");
    }

    private static void RemovalSchemaMigration()
    {
        for (var version = 13; version <= 17; version++)
        {
            var root = Path.Combine(Path.GetTempPath(), "removal-unsupported-schema-" + Guid.NewGuid().ToString("N"));
            using (var store = SqliteStateStore.Open(root, Settings, TimeProvider.System)) { }
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(root)))
            {
                connection.Open();
                StoreSchema.Execute(connection, null, $"DROP TABLE removal_review; PRAGMA user_version={version};");
            }
            using (var migrated = SqliteStateStore.Open(root, Settings, TimeProvider.System)) { }
            using var check = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(root));
            check.Open();
            Require((long)StoreSchema.Scalar(check, null, "PRAGMA user_version;")! == StoreSchema.Version &&
                (long)StoreSchema.Scalar(check, null, "SELECT COUNT(*) FROM sqlite_master WHERE name='removal_review';")! == 1,
                "integrated build upgrades every intermediate slot with presence guards: v" + version);

        }
        foreach (var present in new[] { false, true })
        {
            var root = Path.Combine(Path.GetTempPath(), "removal-migration-" + Guid.NewGuid().ToString("N"));
            using (var store = SqliteStateStore.Open(root, Settings, TimeProvider.System)) { }
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(root)))
            {
                connection.Open();
                StoreSchema.Execute(connection, null, (present ? "" : "DROP TABLE removal_review;") + "PRAGMA user_version=12;");
            }
            using (var migrated = SqliteStateStore.Open(root, Settings, TimeProvider.System)) { }
            using var check = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(root));
            check.Open();
            Require((long)StoreSchema.Scalar(check, null, "PRAGMA user_version;")! == StoreSchema.Version,
                "removal table migration stamps a version even for unversioned preview tables");
            Require((long)StoreSchema.Scalar(check, null, "SELECT COUNT(*) FROM sqlite_master WHERE name='removal_review';")! == 1,
                "presence-guarded migration creates exactly one removal review table");
        }
    }

    private static async Task RemovalProtectedChildrenAsync()
    {
        foreach (var mode in new[] { "notice", "nested-pair", "link", "placeholder", "unverified", "incomplete", "unknown" })
        {
            using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "removal-protected-" + Guid.NewGuid().ToString("N")), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("folder", "", ItemKind.Folder);
            fx.A.Seed("folder/keep", "", ItemKind.Folder);
            fx.A.Seed("folder/keep/item.txt", "keep");
            fx.A.Seed("folder/remove.txt", "remove");
            store.MarkFirstSyncDone(fx.Pair.PairId);
            if (mode == "notice") store.AddNotices([new Notice(fx.Pair.PairId, 5, NoticeKind.UserDecision, "folder/keep", "resolve child", "child-decision")]);
            if (mode == "nested-pair") store.SavePair(Pair("nested", Path.Combine(fx.Pair.A.RootPath, "folder", "keep"), "/nested"));
            fx.A.SubtreeTransform = item => item.Path.StartsWith("folder/keep", StringComparison.Ordinal) ? mode switch
            {
                "link" => item with { Flags = ItemFlags.Link },
                "placeholder" => item with { Flags = ItemFlags.Placeholder },
                "unverified" => item with { Hash = null },
                "incomplete" => item with { Flags = ItemFlags.Unreadable, IncompleteReason = "access denied" },
                "unknown" => item with { Flags = ItemFlags.Unreadable, IncompleteReason = "access denied", ObservationKindUnknown = true },
                _ => item
            } : item;
            store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.UserDecision, "folder", "grown", "reviewed-folder-grew", RemovalSides: [Side.A])]);
            var run = fx.Run();
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, store.OpenNotices(fx.Pair.PairId).Single(NoticeChoices.IsRemovalAttention).NoticeId!, NoticeChoices.ReviewRemoval, default);
            var page = store.OpenNotices(fx.Pair.PairId).Single(NoticeChoices.IsRemovalReview);
            Require(page.Detail.Contains(mode == "unknown" ? "folder/keep/item.txt (type unknown; could not be read): KEEP:" : "folder/keep/item.txt (4 bytes): KEEP:") && page.Detail.Contains("folder/remove.txt (6 bytes): REMOVE"), "protected children keep reasons; independent sibling remains reviewable: " + mode);
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, page.NoticeId!, NoticeChoices.ConfirmRemoval, default);
            var remaining = await MapAsync(fx.A);
            Require(remaining.ContainsKey("folder/keep/item.txt") && remaining.ContainsKey("folder") && !remaining.ContainsKey("folder/remove.txt"), "only protected child's ancestors are kept: " + mode);
            var attention = store.OpenNotices(fx.Pair.PairId).Where(NoticeChoices.IsRemovalAttention).ToArray();
            Require(mode is "incomplete" or "unverified" or "unknown"
                ? attention.Length == 1 && attention[0].Detail.Contains("Retry review")
                : attention.Length == 0, "transient KEEP leaves one retry notice; permanent KEEP closes removal notices: " + mode);
            Require(!store.OpenNotices(fx.Pair.PairId).Any(NoticeChoices.IsRemovalReview), "confirmation retires all page notices");
        }
    }

    private static async Task RemovalListingOutsideGateAsync()
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "removal-gate-" + Guid.NewGuid().ToString("N")), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        fx.A.Seed("folder", "", ItemKind.Folder);
        fx.A.Seed("folder/file.txt", "bytes");
        store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.UserDecision, "folder", "grown", "reviewed-folder-grew", RemovalSides: [Side.A])]);
        var notice = store.OpenNotices(fx.Pair.PairId).Single();
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = new GatedInventory(fx.A) { Block = block };
        fx.Provider.AddInventory(fx.Pair.A, gated);
        fx.A.SubtreeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = fx.Run();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), timeout.Token);
        await gated.Entered.WaitAsync(timeout.Token);
        var review = run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.ReviewRemoval, timeout.Token);
        try
        {
            await fx.A.SubtreeEntered.Task.WaitAsync(timeout.Token);
            Require(!review.IsCompleted, "listing starts while sync holds gate; publication still waits");
        }
        finally { block.TrySetResult(); }
        await running;
        await review;
        Require(store.OpenNotices(fx.Pair.PairId).Any(NoticeChoices.IsRemovalReview), "review publishes after gate release");
    }

    private static async Task RemovalOrphanCleanupAsync()
    {
        foreach (var mode in new[] { "consumed", "roots", "missing-notice", "live" })
        {
            var root = Path.Combine(Path.GetTempPath(), "removal-open-" + Guid.NewGuid().ToString("N"));
            PairDef pair;
            string generation;
            using (var store = SqliteStateStore.Open(root, Settings, TimeProvider.System))
            {
                var fx = Build(suppliedStore: store); pair = fx.Pair;
                fx.A.Seed("folder", "", ItemKind.Folder);
                fx.A.Seed("folder/file.txt", "bytes");
                store.MarkFirstSyncDone(pair.PairId);
                store.AddNotices([new Notice(pair.PairId, 40, NoticeKind.UserDecision, "folder", "grown", "reviewed-folder-grew", RemovalSides: [Side.A])]);
                await fx.Run().ApplyRetryNoticeChoiceAsync(pair.PairId, store.OpenNotices(pair.PairId).Single().NoticeId!, NoticeChoices.ReviewRemoval, default);
                generation = store.ReadRemovalReview(pair, "folder")!.Generation;
                if (mode == "consumed") store.ConsumeRemovalReview(generation);
                if (mode == "roots") store.SavePair(pair with { A = pair.A with { RootPath = @"C:\replacement" } });
                if (mode == "missing-notice") foreach (var n in store.OpenNotices(pair.PairId)) store.CloseNotice(n.NoticeId!, "dismiss");
            }
            using var reopened = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            Require((reopened.ReadRemovalReview(pair, "folder") is not null) == (mode == "live"), "open retires orphaned reviews but preserves live review");
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(root));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM prepare_row WHERE generation=$g";
            command.Parameters.AddWithValue("$g", generation);
            Require(((long)command.ExecuteScalar()! > 0) == (mode == "live"), "orphan staged rows swept on open");
        }
    }

    private static async Task RemovalReviewRegressionsAsync()
    {
        RemovalSchemaMigration();
        await HeldRetryNoticeOverlapAsync();
        await RemovalCorruptionPropagatesAsync();
        await RemovalListingFailureAsync();
        await RemovalProtectedChildrenAsync();
        await RemovalListingOutsideGateAsync();
        await RemovalOrphanCleanupAsync();
        foreach (var mode in new[] { "ignore", "exclude", "content", "excluded", "unrelated", "side-b", "nested", "failed", "interrupt", "consumed", "stale", "stale-dismiss" })
        {
            var root = Path.Combine(Path.GetTempPath(), "removal-regression-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            var target = mode == "side-b" ? fx.B : fx.A;
            var writer = mode == "side-b" ? fx.WriterB : fx.WriterA;
            target.Seed("folder", "", ItemKind.Folder);
            target.Seed("folder/file.txt", "reviewed bytes");
            if (mode == "nested")
            {
                target.Seed("folder/child", "", ItemKind.Folder);
                target.Seed("folder/child/deep", "", ItemKind.Folder);
            }
            if (mode == "excluded") store.SavePair(fx.Pair with { Excluded = ["folder"] });
            store.MarkFirstSyncDone(fx.Pair.PairId);
            store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.UserDecision, "folder", "skipped",
                "reviewed-removal-skipped", RemovalSides: [mode == "side-b" ? Side.B : Side.A])]);
            var run = fx.Run();
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, store.OpenNotices(fx.Pair.PairId).Single().NoticeId!, NoticeChoices.ReviewRemoval, default);
            var review = store.OpenNotices(fx.Pair.PairId).Single(NoticeChoices.IsRemovalReview);
            // Refresh must retain the original reason, rather than turning a skip into growth.
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, review.NoticeId!, NoticeChoices.ReviewRemoval, default);
            review = store.OpenNotices(fx.Pair.PairId).Single(NoticeChoices.IsRemovalReview);
            Require(store.ReadPrepare(fx.Pair.PairId) is null, "removal review leaves first-sync prepare slot free");
            var runtime = new V2CliRuntime(null, store, run, new EndpointRootProbe(fx.Provider), root);
            await runtime.RescanPairAsync(fx.Pair.PairId, null, default);
            if (mode == "ignore") await runtime.SaveIgnoresAsync(fx.Pair.PairId, "FileName:file.txt", default);
            if (mode == "exclude") await runtime.SetSelectionAsync(fx.Pair.PairId, "folder", "exclude", default);
            if (mode == "content") target.Seed("folder/file.txt", "different bytes");
            if (mode == "excluded") Require(review.Detail.StartsWith("This folder is excluded from sync; confirming removes it anyway (Recovery-first)"), "excluded-folder confirmation warns explicitly");
            if (mode == "unrelated")
            {
                store.SavePair(Pair("unrelated", @"C:\unrelated", "/unrelated"));
                fx.A.Seed("elsewhere.txt", "unrelated change");
                await runtime.SetSelectionAsync(fx.Pair.PairId, "elsewhere", "exclude", default);
            }
            if (mode is "stale" or "consumed" or "stale-dismiss")
            {
                var header = store.ReadRemovalReview(fx.Pair, "folder")!;
                if (mode == "consumed") store.ConsumeRemovalReview(header.Generation);
                else store.ClearRemovalReview(header.Generation);
                Require(store.NoticeById(fx.Pair.PairId, review.NoticeId!) is { Closed: false }, "stale/used test reaches real open-notice review validation");
                await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, review.NoticeId!, mode == "stale-dismiss" ? NoticeChoices.Dismiss : NoticeChoices.ConfirmRemoval, default);
                Require(writer.Deletes == 0 && store.OpenNotices(fx.Pair.PairId).Any(NoticeChoices.IsRemovalAttention), "stale and consumed reviews never authorize replay or silently disappear");
                continue;
            }
            if (mode == "interrupt")
            {
                using var canceled = new CancellationTokenSource();
                await ExpectAsync<OperationCanceledException>(() => run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId,
                    review.NoticeId!, NoticeChoices.ConfirmRemoval, canceled.Token, removalResult: (_, _) => canceled.Cancel()));
                Require(store.ReadRemovalReview(fx.Pair, "folder") is null, "interrupted confirmation retires its consumed review");
                Require(store.OpenNotices(fx.Pair.PairId).Count(NoticeChoices.IsRemovalAttention) == 1, "interrupted confirmation leaves exactly one notice");
                await runtime.SaveIgnoresAsync(fx.Pair.PairId, "FileName:keep.txt", default);
                await runtime.SetSelectionAsync(fx.Pair.PairId, "folder", "exclude", default);
                await runtime.RescanPairAsync(fx.Pair.PairId, null, default);
                continue;
            }
            if (mode == "failed")
            {
                await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, review.NoticeId!, NoticeChoices.ConfirmRemoval,
                    default, removalResult: (action, _) => { if (action.ExpectedTarget?.Kind == ItemKind.File) target.Seed("folder/new.txt", "raced"); });
                Require(store.OpenNotices(fx.Pair.PairId).Count == 1 && store.OpenNotices(fx.Pair.PairId).Single().ReasonKey == "reviewed-removal-skipped",
                    "failed confirmation leaves exactly one notice and retains skip reason across refresh");
                Require(await target.ObserveAsync("folder/new.txt", default) is not null, "late child survives failed empty-only removal");
                continue;
            }
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, review.NoticeId!, NoticeChoices.ConfirmRemoval, default);
            if (mode is "ignore" or "exclude" or "content")
                Require(writer.Deletes == 0 && store.OpenNotices(fx.Pair.PairId).Single().ReasonKey == "reviewed-removal-skipped", "changed content or policy holds without losing skip reason");
            else
            {
                Require(await target.ObserveAsync("folder", default) is null && writer.Preserves == 1 && store.Recoveries(fx.Pair.PairId).Count == 1,
                    "confirmed subtree removal is Recovery-first on either side, deepest folders first");
                Require(store.OpenNotices(fx.Pair.PairId).Count == 0, "unrelated pair changes do not hold confirmation");
            }
        }
        await RemovalReviewIndependentFoldersAsync();
    }

    private static async Task RemovalReviewIndependentFoldersAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "removal-independent-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        var run = fx.Run();
        foreach (var path in new[] { "x", "y" })
        {
            fx.A.Seed(path, "", ItemKind.Folder);
            fx.A.Seed(path + "/file.txt", "bytes");
            store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.UserDecision, path, "skipped", "reviewed-removal-skipped", RemovalSides: [Side.A])]);
            var notice = store.OpenNotices(fx.Pair.PairId).Single(n => n.Path == path);
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.ReviewRemoval, default);
        }
        Require(store.ReadRemovalReview(fx.Pair, "x") is not null && store.ReadRemovalReview(fx.Pair, "y") is not null, "two folder entities retain independent reviews");
        var x = store.OpenNotices(fx.Pair.PairId).Single(n => n.Path == "x");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, x.NoticeId!, NoticeChoices.ConfirmRemoval, default);
        Require(await fx.A.ObserveAsync("x", default) is null && await fx.A.ObserveAsync("y/file.txt", default) is not null,
            "confirming one review does not overwrite or remove the other folder");
    }
}
