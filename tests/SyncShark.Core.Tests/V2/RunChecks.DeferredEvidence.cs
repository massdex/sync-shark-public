namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task DeferredEvidenceChecksAsync()
    {
        await DeferredEvidence_OrdinaryActionRunsWhileBlockedAsync();
        await DeferredEvidence_ResolvesUploadAndIdenticalDeleteAsync();
        await DeferredEvidence_UsesFreshLocalMtimeAsync();
        await DeferredEvidence_FailureRemainsPendingAsync();
        await DeferredEvidence_OutsideHeldDeleteRefusedAsync();
        await DeferredEvidence_MissingReviewRefusedAsync();
        await DeferredEvidence_ChoicesRetainedAsync();
        await DeferredEvidence_TokenlessCancellationStaysPendingAsync();
        await DeferredEvidence_UnreviewedStaysPendingAsync();
        await DeferredEvidence_NewFolderUsesOrdinaryPlannerAsync();
        await DeferredEvidence_NestedFolderAndCachedChildAsync();
        await DeferredEvidence_FoldersResolveByDepthAsync();
        await DeferredEvidence_PendingChildKeepsFolderAsync();
        await DeferredEvidence_SharedAncestorNotHeldAsync();
        await DeferredEvidence_FolderKeptForUploadedChildAsync();
        await DeferredEvidence_ResolvedWithoutEffectKeepsProgressWholeAsync();
        await RunHeldPerformanceChecksAsync();
    }

    private static async Task DeferredEvidence_OrdinaryActionRunsWhileBlockedAsync()
    {
        var fx = Build();
        using var deadline = new CancellationTokenSource(GateTimeout);
        var ct = deadline.Token;
        fx.A.Seed("held.txt", "local");
        fx.B.Seed("ordinary.txt", "cloud");
        var evidence = new DeferredInventory(fx.B) { Block = true };
        var writer = new SignalingWriter(fx.WriterA);
        fx.Provider.AddInventory(fx.Pair.B, evidence).AddWriter(fx.Pair.A, writer);
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), ct);
        Require(plan.HeldDeletions.Count == 1 && plan.HeldDeletions[0].Path == "held.txt",
            "prepare holds unresolved local-only evidence without probing it");
        var applying = run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), ct, ReviewedHeld(plan));
        try
        {
            await evidence.Started.Task.WaitAsync(ct);
            await writer.PutCompleted.Task.WaitAsync(ct);
            Require(!applying.IsCompleted, "ordinary download completes while evidence remains blocked");
            Require(await fx.A.ObserveAsync("ordinary.txt", ct) is not null,
                "ordinary action reaches its endpoint before evidence is released");
        }
        finally { evidence.Release.TrySetResult(); }
        var report = await applying;
        Require(report.Deferred == 0 && fx.Store.FirstSyncDone(fx.Pair.PairId),
            "released evidence finishes the same first sync");
        Console.WriteLine("PASS DeferredEvidence_OrdinaryActionRunsWhileBlocked");
    }

    private static async Task DeferredEvidence_ResolvesUploadAndIdenticalDeleteAsync()
    {
        foreach (var remove in new[] { false, true })
        {
            var fx = Build();
            using var deadline = new CancellationTokenSource(GateTimeout);
            var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var local = fx.A.Seed("held.txt", "content", mtime: at);
            var evidence = new DeferredInventory(fx.B)
                { Deleted = remove ? at.AddDays(1) : null, HistoryHashes = new Dictionary<string, string?> { [local.Path] = local.Hash } };
            fx.Provider.AddInventory(fx.Pair.B, evidence);
            var run = fx.Run();
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
            var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), deadline.Token,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A:held.txt" });
            Require(report.Deferred == 0 && fx.Store.FirstSyncDone(fx.Pair.PairId), "resolved evidence completes");
            Require(remove ? fx.WriterA.Deletes == 1 && fx.WriterA.Preserves == 0 && report.Deleted == 1
                    : fx.WriterB.Puts == 1 && report.Uploaded == 1,
                "newer deletion of identical content skips Recovery; absent evidence uploads");
            Require((await (remove ? fx.A : fx.B).ObserveAsync("held.txt", deadline.Token) is not null) == !remove,
                "resolved effect reaches the expected endpoint");
        }
        Console.WriteLine("PASS DeferredEvidence_ResolvesUploadAndIdenticalDelete");
    }

    private static async Task DeferredEvidence_UsesFreshLocalMtimeAsync()
    {
        foreach (var editNewer in new[] { false, true })
        {
            var fx = Build();
            using var deadline = new CancellationTokenSource(GateTimeout);
            var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var local = fx.A.Seed("held.txt", "old", mtime: editNewer ? at : at.AddDays(2));
            var evidence = new DeferredInventory(fx.B) { Deleted = at.AddDays(1), Block = true,
                HistoryHashes = new Dictionary<string, string?> { [local.Path] = local.Hash } };
            fx.Provider.AddInventory(fx.Pair.B, evidence);
            var run = fx.Run();
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
            var applying = run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), deadline.Token, ReviewedHeld(plan));
            try
            {
                await evidence.Started.Task.WaitAsync(deadline.Token);
                fx.A.Seed("held.txt", "fresh", mtime: editNewer ? at.AddDays(2) : at);
            }
            finally { evidence.Release.TrySetResult(); }
            var report = await applying;
            Require(report.Deferred == 0, "fresh-state decision executes rather than using stale expected state");
            Require(fx.WriterB.Puts == 1 && fx.WriterA.Deletes == 0,
                "fresh local content wins over deletion even when its mtime is older");
        }
        Console.WriteLine("PASS DeferredEvidence_UsesFreshLocalMtime");
    }

    private static async Task DeferredEvidence_FailureRemainsPendingAsync()
    {
        var fx = Build();
        using var deadline = new CancellationTokenSource(GateTimeout);
        fx.A.Seed("held.txt", "local");
        fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B) { Fail = true });
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), deadline.Token, ReviewedHeld(plan));
        Require(report.Deferred == 0 && fx.Store.FirstSyncDone(fx.Pair.PairId) &&
            fx.Store.SyncRetryAt(fx.Pair.PairId, "held.txt")?.Held is not null,
            "item-level evidence failure completes first sync with durable held retry input");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
            "failed evidence neither guesses upload nor deletes");
        Console.WriteLine("PASS DeferredEvidence_FailureRemainsPending");
    }

    private static async Task DeferredEvidence_OutsideHeldDeleteRefusedAsync()
    {
        var fx = Build();
        using var deadline = new CancellationTokenSource(GateTimeout);
        fx.A.Seed("held.txt", "local");
        var outside = fx.A.Seed("outside.txt", "untouched");
        fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B));
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
        plan = plan with
        {
            HeldDeletions = plan.HeldDeletions.Where(held => held.Path == "held.txt").ToArray(),
            // Decision 64's explicit no-change choice isolates this authorization test: decision 85
            // allows independent work to continue, so an unrelated held upload must not affect its zero-write assertion.
            FirstSyncOptions = plan.FirstSyncOptions! with
            { Winners = new Dictionary<string, Side?> { ["held.txt"] = null } },
            // ordinary deletes need no review; a first-sync choice delete still does.
            Actions = [new SyncAction("outside-delete", 32, ActionKind.Delete, Side.A, "outside.txt", null,
                null, outside, true, null) { ChoiceRemove = true, FirstSyncChoice = true }]
        };
        await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), deadline.Token,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A:held.txt" });
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 && fx.Store.FirstSyncDone(fx.Pair.PairId) &&
            await fx.A.ObserveAsync("outside.txt", default) is not null,
            "unreviewed outside choice stays untouched without refusing first-sync completion");
        Console.WriteLine("PASS DeferredEvidence_OutsideHeldDeleteRefused");
    }

    private static HashSet<string> ReviewedHeld(Plan plan) => plan.HeldDeletions
        .Select(held => FolderDelete.VictimKey(held.Local, held.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task DeferredEvidence_MissingReviewRefusedAsync()
    {
        foreach (var missing in new[] { false, true })
        {
            var fx = Build();
            using var deadline = new CancellationTokenSource(GateTimeout);
            fx.A.Seed("held.txt", "local");
            fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B));
            var run = fx.Run();
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
            await run.ApplyAsync(fx.Pair.PairId, plan,
                new RecordingProgress(), deadline.Token, missing ? new HashSet<string>() : null);
            Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && fx.Store.SyncRetryAt(fx.Pair.PairId, "held.txt") is not null,
                "missing held approval parks durable work while first sync completes");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "null or missing held approval authorizes no effect");
        }
        Console.WriteLine("PASS DeferredEvidence_MissingReviewRefused");
    }

    private static async Task DeferredEvidence_ChoicesRetainedAsync()
    {
        foreach (var choice in new[] { "copy", "remove", "no-change" })
        {
            var fx = Build();
            using var deadline = new CancellationTokenSource(GateTimeout);
            var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            fx.A.Seed("held.txt", "local", mtime: at);
            fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B)
                { Deleted = choice == "copy" ? at.AddDays(1) : null });
            var run = fx.Run();
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
            plan = plan with { FirstSyncOptions = plan.FirstSyncOptions! with
            {
                Winners = choice == "no-change" ? new Dictionary<string, Side?> { ["held.txt"] = null } : null,
                OneSided = choice == "no-change" ? [] : [new OneSidedChoice("", Side.A, choice == "remove")]
            } };
            var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), deadline.Token, ReviewedHeld(plan));
            Require(choice switch
            {
                "copy" => fx.WriterB.Puts == 1 && fx.WriterA.Deletes == 0,
                "remove" => fx.WriterA.Deletes == 1 && fx.WriterA.Preserves == 1 && fx.WriterB.Puts == 0,
                _ => fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0
            }, "held resolution preserves planner choice precedence for " + choice);
            Require(report.Deferred == 0, "resolved choice is no longer pending");
        }
        Console.WriteLine("PASS DeferredEvidence_ChoicesRetained");
    }

    private static async Task DeferredEvidence_TokenlessCancellationStaysPendingAsync()
    {
        var fx = Build();
        using var deadline = new CancellationTokenSource(GateTimeout);
        fx.A.Seed("held.txt", "local");
        fx.B.Seed("ordinary.txt", "cloud");
        fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B) { CancelWithoutCaller = true });
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), deadline.Token, ReviewedHeld(plan));
        Require(report.DeletionEvidencePending.SequenceEqual(["held.txt"]) && fx.Store.FirstSyncDone(fx.Pair.PairId) &&
            fx.Store.SyncRetryAt(fx.Pair.PairId, "held.txt")?.Held is not null,
            "tokenless item evidence cancellation is durable retry work, not caller cancellation or a proven outage");
        Require(fx.WriterA.Puts == 1 && await fx.A.ObserveAsync("ordinary.txt", deadline.Token) is not null,
            "evidence cancellation never cancels the ordinary executor");
        Console.WriteLine("PASS DeferredEvidence_TokenlessCancellationStaysPending");
    }

    private static async Task DeferredEvidence_UnreviewedStaysPendingAsync()
    {
        var fx = Build();
        using var deadline = new CancellationTokenSource(GateTimeout);
        fx.A.Seed("new.txt", "local");
        fx.B.Seed("ordinary.txt", "cloud");
        fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B));
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
        plan = plan with { UnreviewedHeldDeletions = plan.HeldDeletions, HeldDeletions = [] };
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), deadline.Token, ReviewedHeld(plan));
        Require(report.UnreviewedPending == 1 && report.Deferred == 0 &&
            fx.Store.FirstSyncDone(fx.Pair.PairId) && fx.Store.SyncRetryAt(fx.Pair.PairId, "new.txt")?.Held is not null,
            "unreviewed held paths remain durable and protected after first-sync completion");
        Require(fx.WriterA.Puts == 1 && fx.WriterB.Calls == 0, "unreviewed pending paths do not block ordinary download");
        Console.WriteLine("PASS DeferredEvidence_UnreviewedStaysPending");
    }

    private static async Task DeferredEvidence_NewFolderUsesOrdinaryPlannerAsync()
    {
        foreach (var withFile in new[] { false, true })
        {
            var fx = Build();
            using var deadline = new CancellationTokenSource(GateTimeout);
            fx.A.Seed("new-folder", "", ItemKind.Folder);
            if (withFile) fx.A.Seed("new-folder/new.txt", "local");
            var evidence = new DeferredInventory(fx.B)
            {
                AllowedEvidencePaths = new HashSet<string> { "new-folder/new.txt" },
                Cached = withFile ? new Dictionary<string, DateTimeOffset?> { ["new-folder/new.txt"] = null }
                    : new Dictionary<string, DateTimeOffset?>()
            };
            fx.Provider.AddInventory(fx.Pair.B, evidence);
            var run = fx.Run();
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
            Require(plan.HeldDeletions.Count == 0 && plan.Actions.Any(action =>
                action.Kind == ActionKind.MkDir && action.Target == Side.B && action.Path == "new-folder"),
                "new local-only folder without tombstones or held children uses ordinary MkDir");
            var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), deadline.Token);
            Require(report.Deferred == 0 && await fx.B.ObserveAsync("new-folder", deadline.Token) is not null,
                "ordinary new folder needs no held-deletion approval");
            Require(!evidence.Started.Task.IsCompleted && evidence.UnexpectedEvidencePaths.Count == 0,
                "ordinary new folders never enter revision evidence resolution");
        }
        Console.WriteLine("PASS DeferredEvidence_NewFolderUsesOrdinaryPlanner");
    }

    private static async Task DeferredEvidence_NestedFolderAndCachedChildAsync()
    {
        foreach (var allCached in new[] { false, true })
        {
            var fx = Build();
            using var deadline = new CancellationTokenSource(GateTimeout);
            var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            fx.A.Seed("folder", "", ItemKind.Folder, mtime: at);
            fx.A.Seed("folder/nested", "", ItemKind.Folder, mtime: at);
            var heldChild = fx.A.Seed("folder/nested/held.txt", "held", mtime: at);
            var cachedChild = fx.A.Seed("folder/cached.txt", "cached", mtime: at);
            var cached = new Dictionary<string, DateTimeOffset?> { ["folder/cached.txt"] = at.AddDays(1) };
            if (allCached) cached["folder/nested/held.txt"] = at.AddDays(1);
            var evidence = new DeferredInventory(fx.B)
            {
                Deleted = at.AddDays(1), FolderTombstones = new HashSet<string> { "folder" },
                AllowedEvidencePaths = new HashSet<string> { "folder/nested/held.txt", "folder/cached.txt" },
                Cached = cached,
                HistoryHashes = new Dictionary<string, string?> { [heldChild.Path] = heldChild.Hash, [cachedChild.Path] = cachedChild.Hash }
            };
            fx.Provider.AddInventory(fx.Pair.B, evidence);
            var run = fx.Run();
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
            Require(plan.HeldDeletions.Any(held => held.Path == "folder") &&
                plan.HeldDeletions.Any(held => held.Path == "folder/nested"),
                "exact and ancestor tombstones hold folders even when child evidence is entirely cached");
            Require(plan.HeldDeletions.Count == 4, "cached deletion hashes use the same held resolver as fresh evidence");
            var reviewed = ReviewedHeld(plan);
            reviewed.UnionWith(FolderDelete.VictimKeys(fx.Store, fx.Pair, plan));
            var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), deadline.Token, reviewed);
            Require(report.Deferred == 0 && fx.Store.FirstSyncDone(fx.Pair.PairId), "nested and cached deletion outcomes finish");
            foreach (var path in new[] { "folder", "folder/nested", "folder/nested/held.txt", "folder/cached.txt" })
                Require(await fx.A.ObserveAsync(path, deadline.Token) is null, "no folded child delete is lost: " + path);
            Require(fx.WriterA.Preserves == 0, "identical held and cached file deletes skip Recovery");
            Require(evidence.UnexpectedEvidencePaths.Count == 0, "folder classification never probes revision history");
        }
        Console.WriteLine("PASS DeferredEvidence_NestedFolderAndCachedChild");
    }

    private static async Task DeferredEvidence_FoldersResolveByDepthAsync()
    {
        // A serial reference consists of independent parent/child trees run one at a time.
        // The parallel run puts the same trees in one plan, with more siblings than the gate admits.
        const int trees = 20;
        var serialDeleted = 0;
        for (var i = 0; i < trees; i++)
            serialDeleted += await RunFoldersAsync([i], false);
        var parallelDeleted = await RunFoldersAsync(Enumerable.Range(0, trees).ToArray(), true);
        Require(parallelDeleted == serialDeleted && parallelDeleted == trees * 3,
            "parallel held folders have the same complete deletion outcome as serial trees");
        Console.WriteLine("PASS DeferredEvidence_FoldersResolveByDepth");

        static async Task<int> RunFoldersAsync(int[] ids, bool parallel)
        {
            var storagePath = Path.Combine(Path.GetTempPath(), "held-folders-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(storagePath, Settings, TimeProvider.System);
            var fx = Build(record: true, suppliedStore: store);
            using var competingRefresh = new ManualResetEventSlim();
            var refreshes = 0;
            fx.Recorder!.AfterBeginGeneration = kind =>
            {
                if (kind != "held-refresh") return;
                var count = Interlocked.Increment(ref refreshes);
                // Widen the real SQLite generation race. With the run lock, no sibling can
                // enter until this bounded wait ends; without it, the second Begin closes the first.
                if (parallel && count == 1) competingRefresh.Wait(TimeSpan.FromSeconds(1));
                else if (count > 1) competingRefresh.Set();
            };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ct = deadline.Token;
            var parents = ids.Select(i => $"folder-{i:D2}").ToArray();
            var children = parents.Select(path => path + "/child").ToArray();
            var files = parents.Select(path => path + "/cached.txt").ToArray();
            var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            foreach (var path in parents.Concat(children)) fx.A.Seed(path, "", ItemKind.Folder);
            // File results still leave inventory for the parent's refresh. Keep that generation-race
            // coverage alongside the child-folder results, which now retire inventory at commit.
            var historyHashes = files.ToDictionary(path => path, path => fx.A.Seed(path, "cached", mtime: at).Hash);
            fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B)
            {
                FolderTombstones = parents.ToHashSet(), AllowedEvidencePaths = files.ToHashSet(),
                Cached = files.ToDictionary(path => path, _ => (DateTimeOffset?)at.AddDays(1)),
                HistoryHashes = historyHashes
            });
            var completed = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
            var started = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
            var allSlotsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var parentSlotsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var parentStarts = 0;
            var active = 0;
            var peak = 0;
            var orderBroken = 0;
            var gateBroken = 0;
            var applying = false;
            var sync = new object();
            var writer = new SignalingWriter(fx.WriterA)
            {
                BeforeObserve = async (path, token) =>
                {
                    if (!applying || (!parents.Contains(path) && !children.Contains(path)) ||
                        !started.TryAdd(path, 0)) return;
                    if (PairPath.Depth(path) == 1 && children.Any(child => !completed.ContainsKey(child)))
                        Interlocked.Exchange(ref orderBroken, 1);
                    if (PairPath.Depth(path) == 1)
                        Require(fx.Store.LiveUnder(fx.Pair.A.RootId, children).Count == 0,
                            "child folder results retire inventory before any parent resolves");
                    lock (sync)
                    {
                        active++;
                        peak = Math.Max(peak, active);
                        if (active > 16) gateBroken = 1;
                        if (active == 16) allSlotsStarted.TrySetResult();
                    }
                    // No sleeps: the old serial lane cannot reach this barrier.
                    if (parallel && PairPath.Depth(path) == 2)
                        await allSlotsStarted.Task.WaitAsync(token);
                    if (parallel && PairPath.Depth(path) == 1)
                    {
                        if (Interlocked.Increment(ref parentStarts) == 16) parentSlotsStarted.TrySetResult();
                        await parentSlotsStarted.Task.WaitAsync(token);
                    }
                },
                AfterDelete = action =>
                {
                    completed.TryAdd(action.Path, 0);
                    if (!files.Contains(action.Path))
                        lock (sync) active--;
                }
            };
            fx.Provider.AddWriter(fx.Pair.A, writer);
            var run = fx.Run();
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), ct);
            Require(plan.Actions.Count == 0 && plan.HeldDeletions.Count == ids.Length * 3 &&
                    files.All(path => plan.HeldDeletions.Any(held => held.Path == path)),
                "both folder depths and cached files enter the hash-aware held lane");
            var reviewed = ReviewedHeld(plan);
            reviewed.UnionWith(FolderDelete.VictimKeys(fx.Store, fx.Pair, plan));
            applying = true;
            var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), ct, reviewed);
            Require(orderBroken == 0 && gateBroken == 0 && (!parallel || peak == 16),
                "held folders overlap within the 16-way bound and all children finish before parents");
            Require(report.Deferred == 0 && report.Failed == 0 && fx.Store.FirstSyncDone(fx.Pair.PairId),
                "every held folder resolves and first sync completes");
            Require(refreshes == ids.Length,
                "every parent refreshes its completed file delete through a serialized SQLite generation");
            Require(fx.Store.LiveUnder(fx.Pair.A.RootId, parents).Count == 0,
                "all deleted parent, child folder and file rows are absent from SQLite");
            foreach (var path in parents.Concat(children).Concat(files))
                Require(await fx.A.ObserveAsync(path, ct) is null && await fx.B.ObserveAsync(path, ct) is null,
                    "serial and parallel execution leave both endpoints without " + path);
            return report.Deleted;
        }
    }

    private static async Task DeferredEvidence_PendingChildKeepsFolderAsync()
    {
        var fx = Build();
        using var deadline = new CancellationTokenSource(GateTimeout);
        fx.A.Seed("folder", "", ItemKind.Folder);
        fx.A.Seed("folder/pending.txt", "must stay local");
        fx.A.Seed("ordinary-folder", "", ItemKind.Folder);
        fx.B.Seed("ordinary.txt", "cloud");
        var evidence = new DeferredInventory(fx.B)
        {
            Fail = true,
            AllowedEvidencePaths = new HashSet<string> { "folder/pending.txt" }
        };
        fx.Provider.AddInventory(fx.Pair.B, evidence);
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
        Require(plan.HeldDeletions.Select(held => held.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(["folder", "folder/pending.txt"]),
            "only the held file and its local-only ancestor wait, not unrelated new folders");
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), deadline.Token, ReviewedHeld(plan));
        Require(await fx.B.ObserveAsync("ordinary-folder", deadline.Token) is not null,
            "unrelated new folder uses ordinary MkDir even while deletion evidence remains pending");
        Require(report.DeletionEvidencePending.Contains("folder/pending.txt") && fx.Store.FirstSyncDone(fx.Pair.PairId) &&
            fx.Store.SyncRetryAt(fx.Pair.PairId, "folder/pending.txt")?.Held is not null,
            "pending child and containing folder remain protected retry work after first-sync completion");
        Require(fx.WriterB.Puts == 0 && await fx.B.ObserveAsync("folder/pending.txt", deadline.Token) is null,
            "held parent must not accidentally replan and upload a pending child");
        Require(fx.WriterA.Deletes == 0 && await fx.A.ObserveAsync("folder", deadline.Token) is not null,
            "pending child keeps its local folder");
        Require(fx.WriterA.Puts == 1 && evidence.UnexpectedEvidencePaths.Count == 0,
            "ordinary download finishes and folders never enter evidence probes");
        Console.WriteLine("PASS DeferredEvidence_PendingChildKeepsFolder");
    }

    private static async Task DeferredEvidence_SharedAncestorNotHeldAsync()
    {
        var fx = Build();
        using var deadline = new CancellationTokenSource(GateTimeout);
        fx.A.Seed("shared", "", ItemKind.Folder);
        fx.B.Seed("shared", "", ItemKind.Folder);
        fx.A.Seed("shared/held.txt", "local");
        var evidence = new DeferredInventory(fx.B)
        {
            AllowedEvidencePaths = new HashSet<string> { "shared/held.txt" }
        };
        fx.Provider.AddInventory(fx.Pair.B, evidence);
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
        Require(plan.HeldDeletions.All(held => held.Path != "shared"),
            "a shared ancestor is not held merely because it has a held local-only descendant");
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), deadline.Token, ReviewedHeld(plan));
        Require(report.Deferred == 0 && evidence.UnexpectedEvidencePaths.Count == 0,
            "only local-only file descendants reach evidence prepare and resolution");
        Console.WriteLine("PASS DeferredEvidence_SharedAncestorNotHeld");
    }

    private static async Task DeferredEvidence_FolderKeptForUploadedChildAsync()
    {
        var fx = Build();
        using var deadline = new CancellationTokenSource(GateTimeout);
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        fx.A.Seed("folder", "", ItemKind.Folder, mtime: at);
        fx.A.Seed("folder/held.txt", "local", mtime: at.AddDays(2));
        // A shared descendant must never be refreshed by the held-only deferred path.
        fx.A.Seed("folder/unheld.txt", "same");
        fx.B.Seed("folder/unheld.txt", "same");
        var watchedA = new SignalingWriter(fx.WriterA);
        var watchedB = new SignalingWriter(fx.WriterB);
        var evidence = new DeferredInventory(fx.B)
        {
            Deleted = at.AddDays(1), AllowedEvidencePaths = new HashSet<string> { "folder/held.txt" }
        };
        fx.Provider.AddInventory(fx.Pair.B, evidence).AddWriter(fx.Pair.A, watchedA).AddWriter(fx.Pair.B, watchedB);
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
        // The unrelated adoption is not part of this deferred effect observation check.
        plan = plan with { Actions = plan.Actions.Where(action => action.Path != "folder/unheld.txt").ToArray() };
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), deadline.Token, ReviewedHeld(plan));
        Require(report.Deferred == 0 && fx.WriterA.Deletes == 0,
            "an uploaded newer child keeps its containing folder rather than deleting it");
        Require(await fx.A.ObserveAsync("folder", deadline.Token) is not null &&
            await fx.B.ObserveAsync("folder/held.txt", deadline.Token) is not null,
            "kept folder and uploaded child survive on their appropriate sides");
        Require(!watchedA.ObservedPaths.Contains("folder/unheld.txt") && !watchedB.ObservedPaths.Contains("folder/unheld.txt"),
            "deferred folder resolution observes only held scope, never unrelated rows");
        Require(evidence.UnexpectedEvidencePaths.Count == 0, "folder paths are never passed to evidence APIs");
        Console.WriteLine("PASS DeferredEvidence_FolderKeptForUploadedChild");
    }

    // A held path that resolves without an executable action (its local file vanished between prepare and apply)
    // must not pad the planned total, or the apply progress could never reach it.
    private static async Task DeferredEvidence_ResolvedWithoutEffectKeepsProgressWholeAsync()
    {
        var fx = Build();
        using var deadline = new CancellationTokenSource(GateTimeout);
        var held = fx.A.Seed("held.txt", "local");
        fx.B.Seed("ordinary.txt", "cloud");
        fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B));
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
        Require(plan.HeldDeletions.Count == 1 && plan.HeldDeletions[0].Path == "held.txt" &&
                plan.Actions.Count(action => action.Kind != ActionKind.Hold) == 1,
            "the preview holds the unproven path and plans the ordinary download");
        // The held path is gone by execution time, so its resolution produces no action at all.
        await fx.A.DeleteAsync(new SyncAction("seed-gone", 6, ActionKind.Delete, Side.A, "held.txt", null, null,
            held, false, null), deadline.Token);
        var sink = new RecordingProgress();
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, sink, deadline.Token, ReviewedHeld(plan));
        var applying = sink.Reports.Where(entry => entry.Phase == RunPhases.Applying).ToArray();
        Require(applying.Length > 0, "the ordinary action reports applying progress");
        Require(applying.All(entry => entry.Done <= entry.Total), "applying progress stays within its total");
        Require(applying[^1] is { Done: 1, Total: 1 },
            "a held path that resolves without an effect leaves no permanent shortfall");
        Require(report.Deferred == 0 && fx.Store.FirstSyncDone(fx.Pair.PairId) &&
            fx.Store.SyncRetryAt(fx.Pair.PairId, "held.txt") is not null,
            "the vanished path remains durable recheck input without holding first-sync completion open");
        Console.WriteLine("PASS DeferredEvidence_ResolvedWithoutEffectKeepsProgressWhole");
    }

    private sealed class DeferredInventory(FakeEndpoint inner) : IInventorySource, IDeferredDeletionEvidence
    {
        public IReadOnlySet<string> FolderTombstones { get; init; } = new HashSet<string>();
        public bool IsAtOrUnderExactTombstone(string path) => FolderTombstones.Any(tombstone =>
            PairPath.IsSelfOrUnder(path, tombstone));
        public IReadOnlyList<string> FolderDeletionCandidates(IReadOnlyList<string> paths) => paths
            .Where(path => FolderTombstones.Any(tombstone => PairPath.IsSelfOrUnder(path, tombstone))).ToArray();
        public IReadOnlySet<string>? AllowedEvidencePaths { get; init; }
        public List<string> UnexpectedEvidencePaths { get; } = new();
        private void CheckFilePaths(IReadOnlyList<string> paths)
        {
            if (AllowedEvidencePaths is null) return;
            lock (UnexpectedEvidencePaths)
                UnexpectedEvidencePaths.AddRange(paths.Where(path => !AllowedEvidencePaths.Contains(path)));
            Require(UnexpectedEvidencePaths.Count == 0, "evidence APIs accept only candidate file paths, never folders");
        }
        public Func<string, string>? ResolvedPath { get; init; }
        public bool Block { get; init; }
        public bool Fail { get; init; }
        public bool ProviderOutage { get; init; }
        public bool Pause { get; set; }
        public bool LookupFailure { get; init; }
        // Runs after every per-path answer, so a check can pause the pair when Dropbox has already
        // answered (the paused-run budget case) instead of before the lane starts.
        public Action? AfterAnswer { get; init; }
        public bool CancelWithoutCaller { get; init; }
        public IReadOnlyDictionary<string, DateTimeOffset?> Cached { get; init; } = new Dictionary<string, DateTimeOffset?>();
        public DateTimeOffset? Deleted { get; init; }
        public IReadOnlyDictionary<string, string?> HistoryHashes { get; init; } = new Dictionary<string, string?>();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DeletionEvidencePreparation> PrepareDeletionEvidenceAsync(IReadOnlyList<string> paths,
            CancellationToken ct)
        {
            CheckFilePaths(paths);
            var resolved = Cached.ToDictionary(entry => entry.Key,
                entry => new SyncRemoteDeletionEvidence(entry.Value, "cached evidence", ContentHash: HistoryHashes.GetValueOrDefault(entry.Key)),
                StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new DeletionEvidencePreparation(
                resolved, paths.Where(path => !Cached.ContainsKey(path)).ToArray()));
        }
        public async Task ResolveDeletionEvidenceAsync(IReadOnlyList<string> paths,
            Action<string, SyncRemoteDeletionEvidence> resolved, CancellationToken ct)
        {
            CheckFilePaths(paths);
            Started.TrySetResult();
            if (Block) await Release.Task.WaitAsync(ct);
            if (ProviderOutage) throw new HttpRequestException("Dropbox unreachable");
            if (Pause) throw new PairPausedException("p1");
            if (Fail) throw new IOException("Evidence temporarily unavailable");
            if (CancelWithoutCaller) throw new OperationCanceledException("Evidence probe cancelled internally");
            foreach (var path in paths)
                resolved(ResolvedPath?.Invoke(path) ?? path, new SyncRemoteDeletionEvidence(Cached.TryGetValue(path, out var cached) ? cached : Deleted,
                    "probed Dropbox deletion", LookupFailed: LookupFailure, ContentHash: HistoryHashes.GetValueOrDefault(path)));
            AfterAnswer?.Invoke();
        }
        public Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> progress,
            CancellationToken ct) => inner.FullAsync(gen, include, progress, ct);
        public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include,
            CancellationToken ct) => inner.ChangesSinceAsync(cursor, gen, include, ct);
        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths,
            CancellationToken ct) => inner.HashAsync(paths, ct);
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
            CancellationToken ct) => throw new InvalidOperationException("Deferred evidence must not use the eager probe");
    }

    private sealed class SignalingWriter(IEndpointWriter inner) : IEndpointWriter
    {
        public TaskCompletionSource PutCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct)
        {
            var result = await inner.PutAsync(action, content, ct);
            PutCompleted.TrySetResult();
            return result;
        }
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => inner.MoveAsync(action, ct);
        public Func<string, CancellationToken, Task>? BeforeObserve { get; init; }
        public Action<SyncAction>? AfterDelete { get; init; }
        public Func<ItemState?, ItemState?>? AfterObserve { get; init; }
        public async Task DeleteAsync(SyncAction action, CancellationToken ct)
        {
            await inner.DeleteAsync(action, ct);
            AfterDelete?.Invoke(action);
        }
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => inner.MkDirAsync(path, ct);
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) =>
            inner.PreserveAsync(victim, actionId, ct);
        public System.Collections.Concurrent.ConcurrentBag<string> ObservedPaths { get; } = new();
        public async Task<ItemState?> ObserveAsync(string path, CancellationToken ct)
        {
            ObservedPaths.Add(path);
            if (BeforeObserve is not null) await BeforeObserve(path, ct);
            var observed = await inner.ObserveAsync(path, ct);
            return AfterObserve is null ? observed : AfterObserve(observed);
        }
        public Task<Stream> OpenReadAsync(ItemState state, CancellationToken ct) => inner.OpenReadAsync(state, ct);
    }
}
