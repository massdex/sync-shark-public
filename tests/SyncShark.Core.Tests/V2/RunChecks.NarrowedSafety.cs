namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    public static async Task NarrowedSafetyChecksAsync()
    {
        foreach (var scenario in new[] { "move-in", "move-out", "folder-delete", "source-copy" })
        {
            var fx = Build();
            var sourcePath = scenario is "move-out" or "source-copy" ? "private/item.txt" : "open/item.txt";
            var source = fx.A.Seed(sourcePath, "same");
            var target = fx.B.Seed(sourcePath, "same");
            var folder = fx.B.Seed("open", "", ItemKind.Folder);
            fx.B.Seed("open/private/keep.txt", "keep");
            var run = fx.Run();
            await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            fx.Store.SavePair(fx.Pair with { Excluded = ["private", "open/private"] });
            var action = scenario switch
            {
                "folder-delete" => new SyncAction("blocked", 23, ActionKind.Delete, Side.B, "open", null, null, folder, true, "folder"),
                "source-copy" => new SyncAction("blocked", 1, ActionKind.CopyNew, Side.B, "safe.txt", null, source, null, false, "file"),
                _ => new SyncAction("blocked", 9, ActionKind.Move, Side.B,
                    scenario == "move-in" ? "private/item.txt" : "safe.txt", sourcePath, source, target, false, "file")
            };
            var report = await run.ApplyAsync(fx.Pair.PairId, new Plan(fx.Pair.PairId, 1, [action], []),
                new RecordingProgress(), CancellationToken.None);
            Require(report.Skipped == 1 && report.Done == 0 && report.Deleted == 0 && report.Moved == 0,
                scenario + " reports exactly one skipped effect, not success");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, scenario + " performs no effect, including Recovery");
            Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == PlanNotices.OutsidePairArea.Key),
                scenario + " has an explanatory notice");
            Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 0, scenario + " creates no pending intent");
            Require((await MapAsync(fx.B)).ContainsKey("open/private/keep.txt"), scenario + " leaves excluded data intact");
        }

        {
            var fx = Build();
            var source = fx.A.Seed("destination.txt", "incoming");
            var old = fx.B.Seed("private/source.txt", "incoming");
            var occupant = fx.B.Seed("destination.txt", "occupant");
            var run = fx.Run();
            await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            fx.Store.SavePair(fx.Pair with { Excluded = ["private"] });
            // Planner.IncomingWins gives collision cleanup the occupant's key, not the incoming move's key.
            var move = new SyncAction("move", 9, ActionKind.Move, Side.B, "destination.txt", "private/source.txt",
                source, old, false, "incoming-key");
            var cleanup = new SyncAction("cleanup", 4, ActionKind.Delete, Side.B, "destination.txt", null,
                source, occupant, true, "occupant-key");
            var forget = new SyncAction("forget", 4, ActionKind.Forget, Side.B, "other-path.txt", null,
                null, null, false, "occupant-key");
            var report = await run.ApplyAsync(fx.Pair.PairId, new Plan(fx.Pair.PairId, 1, [cleanup, forget, move], []),
                new RecordingProgress(), CancellationToken.None);
            Require(report.Skipped == 3 && report.Done == 0 && fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
                "blocked move suppresses collision delete and transitive baseline cleanup despite different keys/order");
            Require((await MapAsync(fx.B))["destination.txt"] == occupant.Hash,
                "existing collision occupant is never deleted");
        }

        foreach (var kind in new[] { ActionKind.Delete, ActionKind.Move })
        {
            var fx = Build();
            var folder = fx.B.Seed("folder", "", ItemKind.Folder);
            fx.B.Seed("folder/keep.tmp", "keep");
            if (kind == ActionKind.Delete)
            {
                fx.A.Seed("folder", "", ItemKind.Folder);
                fx.A.Seed("folder/local.txt", "local content must survive");
            }
            var run = fx.Run();
            await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            var pair = fx.Pair with { IgnoreRules = ["FileExtension:tmp"] };
            fx.Store.SavePair(pair);
            var action = new SyncAction("carried", 23, kind, Side.B,
                kind == ActionKind.Move ? "renamed" : "folder", kind == ActionKind.Move ? "folder" : null,
                null, folder, false, "folder")
            { RebuildableRemove = kind == ActionKind.Delete, FirstSyncChoice = true };
            var plan = new Plan(pair.PairId, 1, [action], []);
            Require(run.FilterNarrowedPrepare(pair, plan, []).Actions.Count == 1,
                "decision 80 retains carried ignored content outside narrowed-review context");
            var narrowed = run.FilterNarrowedPrepare(pair, plan, ["FileExtension:tmp"]);
            Require(narrowed.Actions.Count == 0 && narrowed.SkippedActions.Single().ActionId == action.ActionId,
                kind + " containing newly ignored content is removed before execution and victim validation");
            var again = run.FilterNarrowedPrepare(pair, narrowed, ["FileExtension:tmp"]);
            Require(again.SkippedActions.Count == 1, "repeated narrowing does not duplicate skipped effects");
            var rebuildables = kind == ActionKind.Delete
                ? new RebuildableResolution([], [new RebuildableFolder("test", "folder", 1, 4)], Side.B)
                : null;
            var report = await run.ApplyAsync(pair.PairId, again, new RecordingProgress(), CancellationToken.None,
                null, rebuildables);
            Require(report.Skipped == 1 && fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
                kind + " narrowed effect is counted and never sent to a writer");
            Require((await MapAsync(fx.B)).ContainsKey("folder/keep.tmp"), "newly ignored child survives");
            Require(report.Deferred == 0 && fx.Store.FirstSyncDone(pair.PairId),
                "explicitly skipped ancestor does not block first-sync completion");
            Require(report.RebuildablesBlocked.Count == 0 &&
                fx.Store.Pair(pair.PairId)!.Excluded.Contains("folder") == (kind == ActionKind.Delete),
                "reviewed rebuildable exclusion persists even though narrowing safely skipped its removal");
            if (kind == ActionKind.Delete)
            {
                var notice = fx.Store.OpenNotices(pair.PairId).Single(item => item.ReasonKey == "reviewed-removal-skipped");
                Console.WriteLine("NOTICE-MEASURE skipped-removal sides=" +
                    (notice.RemovalSides is null ? "null" : "[" + string.Join(",", notice.RemovalSides) + "]") +
                    " choices=" + string.Join(",", NoticeOptions.Options(notice).Select(option => option.Choice)));
                Require(notice.RemovalSides is not null && notice.RemovalSides.SequenceEqual(new[] { Side.B }),
                    "skipped removal retains the reviewed target side B");
                // RQ-032 requires Retry; E9 Dismiss suppresses this cause without granting removal authority.
                Require(NoticeOptions.Options(notice).Select(option => option.Choice).SequenceEqual(
                    new[] { NoticeChoices.RetryNow, NoticeChoices.ReviewRemoval, NoticeChoices.OpenFolderSettings, NoticeChoices.Dismiss }),
                    "skipped removal offers Retry, a fresh review, settings and dismiss, never unsigned deletion");
                foreach (var unsignedChoice in new[] { NoticeChoices.DeleteReviewed, NoticeChoices.ConfirmRemoval })
                {
                    var refused = false;
                    try { await run.ApplyRetryNoticeChoiceAsync(pair.PairId, notice.NoticeId!, unsignedChoice, default); }
                    catch (SyncRunRefusedException) { refused = true; }
                    Require(refused && fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 &&
                        await fx.B.ObserveAsync("folder/keep.tmp", default) is not null &&
                        fx.Store.OpenNotices(pair.PairId).Any(item => item.NoticeId == notice.NoticeId),
                        "an attention notice cannot authorize " + unsignedChoice + " without a frozen review");
                }
                await run.ApplyRetryNoticeChoiceAsync(pair.PairId, notice.NoticeId!, NoticeChoices.RetryNow, default);
                Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 &&
                    fx.Store.NoticeById(pair.PairId, notice.NoticeId!) is { Closed: false } &&
                    fx.Store.Pair(pair.PairId)!.Excluded.Contains("folder"),
                    "attention Retry only requests another observation and never grants removal consent");
                await run.IncrementalAsync(pair.PairId, new RecordingProgress(), default);
                Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 &&
                    await fx.A.ObserveAsync("folder", default) is not null && await fx.A.ObserveAsync("folder/local.txt", default) is not null &&
                    await fx.B.ObserveAsync("folder/keep.tmp", default) is not null,
                    "persisted exclusion never deletes local content or replays an unsigned narrowed removal");
                Require(fx.Store.OpenNotices(pair.PairId).Any(item => item.NoticeId == notice.NoticeId), "attention notice stays open until dismissed");
                await run.ApplyRetryNoticeChoiceAsync(pair.PairId, notice.NoticeId!, NoticeChoices.OpenFolderSettings, default);
                Require(fx.Store.OpenNotices(pair.PairId).Any(item => item.NoticeId == notice.NoticeId), "settings navigation does not acknowledge removal");
                await run.ApplyRetryNoticeChoiceAsync(pair.PairId, notice.NoticeId!, NoticeChoices.Dismiss, default);
                // E9: this Dismiss label uses cause-only Ignore, not terminal resolution or consent.
                Require(fx.Store.OpenNotices(pair.PairId).All(item => item.NoticeId != notice.NoticeId) &&
                    fx.Store.NoticeById(pair.PairId, notice.NoticeId!) is { Closed: false } &&
                    fx.Store.UnresolvedNotices(pair.PairId).Any(item => item.NoticeId == notice.NoticeId) &&
                    fx.Store.Pair(pair.PairId)!.Excluded.Contains("folder") &&
                    fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 &&
                    await fx.B.ObserveAsync("folder/keep.tmp", default) is not null,
                    "dismiss suppresses the unresolved cause and preserves the skipped removal and ignored child");
                fx.Store.AddNotices([notice]);
                Require(fx.Store.OpenNotices(pair.PairId).All(item => item.NoticeId != notice.NoticeId),
                    "repeating the same skipped-removal cause stays suppressed");
                fx.Store.AddNotices([notice with { CauseKey = "new-protected-child" }]);
                Require(fx.Store.OpenNotices(pair.PairId).Single(item => item.NoticeId == notice.NoticeId).CauseKey == "new-protected-child" &&
                    fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 &&
                    await fx.B.ObserveAsync("folder/keep.tmp", default) is not null,
                    "a new skipped-removal cause resurfaces the same notice without deleting content");
            }
        }
        {
            var fx = Build();
            fx.A.Seed("folder", "", ItemKind.Folder);
            var folder = fx.B.Seed("folder", "", ItemKind.Folder);
            fx.A.Seed("folder/keep.tmp", "keep");
            fx.B.Seed("folder/keep.tmp", "keep");
            var run = fx.Run();
            await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            fx.Store.SavePair(fx.Pair with { IgnoreRules = ["FileExtension:tmp"] });
            fx.Store.RecordIntent(fx.Pair.PairId, new SyncAction("pending-carry", 23, ActionKind.Delete,
                Side.B, "folder", null, null, folder, false, "folder"));
            await run.PreviewFirstSyncApplyResolvedAsync(fx.Pair.PairId, null, null, null, null,
                new RecordingProgress(), CancellationToken.None, narrowedIgnoreRules: ["FileExtension:tmp"]);
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
                "apply preview reconciles newly ignored carrying intent without replay or expansion");
            Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 0,
                "unchanged newly protected intent settles using existing excluded-intent semantics");
            Require((await MapAsync(fx.B)).ContainsKey("folder/keep.tmp"), "reconciliation leaves ignored child intact");
        }
        {
            var fx = Build();
            var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            fx.A.Seed("folder", "", ItemKind.Folder, mtime: at);
            fx.A.Seed("folder/desktop.ini", "keep", mtime: at);
            fx.Store.SavePair(fx.Pair with { IgnoreRules = ["FileName:desktop.ini"] });
            fx.Provider.AddInventory(fx.Pair.B, new RecoveryEvidenceInventory(fx.B)
                { Tombstone = true, Deleted = at.AddDays(1) });
            var run = fx.Run();
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(plan.HeldDeletions.Any(item => item.Path == "folder"), "fixture has a held ancestor delete");
            plan = plan with { NarrowedIgnoreRules = ["FileName:desktop.ini"] };
            var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None,
                ReviewedHeld(plan));
            Require(report.Skipped > 0 && report.Deferred == 0 && fx.Store.FirstSyncDone(fx.Pair.PairId),
                "held ancestor safety skip completes rather than remaining evidence-pending");
            Require(fx.WriterA.Deletes == 0 && (await MapAsync(fx.A)).ContainsKey("folder/desktop.ini"),
                "held ancestor skip preserves newly ignored content");
        }
        {
            var fx = Build();
            var run = fx.Run();
            var unrelated = Enumerable.Range(0, 20000).Select(i => new SyncAction("unrelated-" + i,
                0, ActionKind.Forget, Side.B, "path-" + i, null, null, null, false, "key-" + i)).ToArray();
            var skipped = new SyncAction("skipped", 0, ActionKind.Forget, Side.B, "blocked", null, null, null, false, "blocked");
            var plan = new Plan(fx.Pair.PairId, 1, unrelated, []) { SkippedActions = [skipped] };
            var start = GC.GetAllocatedBytesForCurrentThread();
            var filtered = run.FilterNarrowedPrepare(fx.Pair, plan, ["FileExtension:tmp"]);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Require(filtered.Actions.Count == unrelated.Length && filtered.SkippedActions.Count == 1,
                "an isolated skip does not remove unrelated actions");
            Require(allocated < 5_000_000,
                "an isolated skip must not allocate whole-plan path/key dictionaries: " + allocated);
        }
        Console.WriteLine("PASS NarrowedSafetyChecks");
    }
}
